using System.Diagnostics;
using System.Runtime.InteropServices;
using Steamworks;

namespace PCLink;

/// <summary>
/// 一条从本机 TCP 到对端（房主/玩家）的可靠字节通道。
/// </summary>
internal interface IPeerChannel : IDisposable
{
    /// <summary>读取数据。返回 &gt;0 为读到的字节数，0 为超时，-1 为已断开。</summary>
    int Read(byte[] buffer, int offset, int count, int timeoutMs);
    void Write(byte[] buffer, int offset, int count);
    bool IsOpen { get; }
    double PingMs { get; }
    string Description { get; }
}

/// <summary>
/// 基于 Steam 网络套接字的通道。
/// </summary>
internal sealed class SteamPeer : IPeerChannel
{
    private readonly HSteamNetConnection _connection;
    private readonly Queue<byte[]> _pending = new();
    private int _pendingOffset;
    private volatile bool _closed;
    private volatile bool _disposed;

    public SteamPeer(HSteamNetConnection connection, string description)
    {
        _connection = connection;
        Description = description;
    }

    public string Description { get; }
    public bool IsOpen => !_closed && !_disposed && IsConnectionAlive();
    /// <summary>是否已经真正建立连接（而不仅仅是正在连接）。</summary>
    public bool IsConnected
    {
        get
        {
            if (_closed || _disposed) return false;
            return SteamNetworkingSockets.GetConnectionInfo(_connection, out SteamNetConnectionInfo_t info) &&
                   info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
        }
    }
    public double PingMs
    {
        get
        {
            var status = default(SteamNetConnectionRealTimeStatus_t);
            var lanes = default(SteamNetConnectionRealTimeLaneStatus_t);
            if (SteamNetworkingSockets.GetConnectionRealTimeStatus(_connection, ref status, 0, ref lanes) != EResult.k_EResultOK) return 0;
            return status.m_nPing;
        }
    }

    private bool IsConnectionAlive()
    {
        if (!SteamNetworkingSockets.GetConnectionInfo(_connection, out SteamNetConnectionInfo_t info)) return false;
        return info.m_eState is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
            or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute
            or ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting;
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (_pending.Count > 0)
            {
                byte[] chunk = _pending.Peek();
                int available = chunk.Length - _pendingOffset;
                int take = Math.Min(available, count);
                Buffer.BlockCopy(chunk, _pendingOffset, buffer, offset, take);
                _pendingOffset += take;
                if (_pendingOffset >= chunk.Length)
                {
                    _pending.Dequeue();
                    _pendingOffset = 0;
                }
                return take;
            }
            if (watch.ElapsedMilliseconds > timeoutMs) return 0;
            if (_closed || _disposed) return -1;

            ReceiveAvailable();
            if (_pending.Count == 0) Thread.Sleep(2);
        }
    }

    private void ReceiveAvailable()
    {
        IntPtr[] pointers = new IntPtr[16];
        int count;
        try
        {
            count = SteamNetworkingSockets.ReceiveMessagesOnConnection(_connection, pointers, pointers.Length);
        }
        catch (Exception ex)
        {
            Report.Log($"接收 Steam 消息异常：{ex.Message}");
            _closed = true;
            return;
        }
        if (count < 0)
        {
            _closed = true;
            return;
        }
        for (int i = 0; i < count; i++)
        {
            try
            {
                SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(pointers[i]);
                if (message.m_cbSize > 0)
                {
                    byte[] data = new byte[message.m_cbSize];
                    Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
                    _pending.Enqueue(data);
                }
            }
            finally
            {
                SteamNetworkingMessage_t.Release(pointers[i]);
            }
        }
    }

    public unsafe void Write(byte[] buffer, int offset, int count)
    {
        if (_closed || _disposed) throw new IOException("通道已关闭");
        fixed (byte* pointer = &buffer[offset])
        {
            EResult result = SteamNetworkingSockets.SendMessageToConnection(
                _connection, (IntPtr)pointer, (uint)count,
                Constants.k_nSteamNetworkingSend_Reliable, out long _);
            if (result != EResult.k_EResultOK) throw new IOException($"发送失败：{result}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            SteamNetworkingSockets.CloseConnection(_connection, 0, "关闭连接", false);
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>
/// 内网自测用的通道，直接包一层 TCP。
/// </summary>
internal sealed class LoopbackPeer : IPeerChannel
{
    private readonly System.Net.Sockets.Socket _socket;
    private volatile bool _closed;

    public LoopbackPeer(System.Net.Sockets.Socket socket, string description)
    {
        _socket = socket;
        _socket.ReceiveTimeout = 500;
        _socket.SendTimeout = 5000;
        Description = description;
    }

    public string Description { get; }
    public bool IsOpen => !_closed && _socket.Connected;
    public double PingMs => 1;

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        if (_closed) return -1;
        try
        {
            _socket.ReceiveTimeout = Math.Max(50, timeoutMs);
            int read = _socket.Receive(buffer, offset, count, System.Net.Sockets.SocketFlags.None);
            if (read == 0)
            {
                _closed = true;
                return -1;
            }
            return read;
        }
        catch (System.Net.Sockets.SocketException ex) when (ex.SocketErrorCode is
            System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.WouldBlock)
        {
            return 0;
        }
        catch
        {
            _closed = true;
            return -1;
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (_closed) throw new IOException("通道已关闭");
        int sent = 0;
        while (sent < count)
        {
            sent += _socket.Send(buffer, offset + sent, count - sent, System.Net.Sockets.SocketFlags.None);
        }
    }

    public void Dispose()
    {
        _closed = true;
        try
        {
            _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both);
        }
        catch
        {
            // 忽略
        }
        _socket.Dispose();
    }
}
