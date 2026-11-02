using System.Net;
using System.Net.Sockets;

namespace PCLink;

/// <summary>
/// 房主：等待玩家连接，并把每个玩家转发到本机的游戏端口。
/// </summary>
internal sealed class HostService
{
    private readonly IHostTransport _transport;
    private readonly int _gamePort;
    private readonly List<HostSession> _sessions = new();
    private volatile bool _stopping;

    public HostService(IHostTransport transport, int gamePort)
    {
        _transport = transport;
        _gamePort = gamePort;
    }

    public void Stop() => _stopping = true;

    public int Run()
    {
        _transport.Start();
        Report.Write("SELF", _transport.SelfId);
        Report.Write("LOBBY", _transport.InviteCode);
        Report.Write("JOINED", "1");
        Report.Write("PEERS", "1");
        Report.State("房间已创建，正在等待玩家加入……");

        long lastReport = 0;
        while (!_stopping)
        {
            _transport.Poll();
            while (_transport.TryAcceptPeer(out IPeerChannel peer))
            {
                var session = new HostSession(peer, _gamePort);
                session.Start();
                _sessions.Add(session);
                Report.Log($"玩家已加入，当前人数：{_sessions.Count + 1}");
            }
            _sessions.RemoveAll(session => session.IsFinished);

            if (Environment.TickCount64 - lastReport >= 500)
            {
                lastReport = Environment.TickCount64;
                Report.Write("PEERS", (_transport.PeerCount + 1).ToString());
                Report.Write("PING", _transport.AveragePingMs.ToString("0.0"));
            }
            Thread.Sleep(50);
        }
        return 0;
    }
}

/// <summary>
/// 房主侧：单个玩家的转发会话。玩家第一次发数据时才连接游戏端口。
/// </summary>
internal sealed class HostSession
{
    private readonly IPeerChannel _peer;
    private readonly int _gamePort;
    private TcpClient? _server;
    private NetworkStream? _serverStream;
    private volatile bool _finished;
    private volatile bool _downStarted;

    public HostSession(IPeerChannel peer, int gamePort)
    {
        _peer = peer;
        _gamePort = gamePort;
    }

    public bool IsFinished => _finished;

    public void Start()
    {
        var thread = new Thread(PumpPeerToServer) { IsBackground = true, Name = "联机 玩家→游戏" };
        thread.Start();
    }

    private void PumpPeerToServer()
    {
        var buffer = new byte[32768];
        try
        {
            while (!_finished)
            {
                int read = _peer.Read(buffer, 0, buffer.Length, 500);
                if (read < 0) break;
                if (read == 0) continue;
                if (_serverStream is null && !ConnectServer()) break;
                try
                {
                    _serverStream!.Write(buffer, 0, read);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Report.Log("转发玩家数据出错：" + ex.Message);
        }
        finally
        {
            Close();
        }
    }

    private bool ConnectServer()
    {
        try
        {
            _server = new TcpClient { NoDelay = true };
            _server.Connect(IPAddress.Loopback, _gamePort);
            _serverStream = _server.GetStream();
            if (!_downStarted)
            {
                _downStarted = true;
                var thread = new Thread(PumpServerToPeer) { IsBackground = true, Name = "联机 游戏→玩家" };
                thread.Start();
            }
            return true;
        }
        catch (Exception ex)
        {
            Report.Log($"无法连接本机游戏端口 {_gamePort}：{ex.Message}");
            return false;
        }
    }

    private void PumpServerToPeer()
    {
        var buffer = new byte[32768];
        try
        {
            while (!_finished && _serverStream is not null)
            {
                int read;
                try
                {
                    read = _serverStream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read <= 0) break;
                try
                {
                    _peer.Write(buffer, 0, read);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Report.Log("转发游戏数据出错：" + ex.Message);
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _server?.Dispose();
        }
        catch
        {
            // 忽略
        }
        _peer.Dispose();
    }
}

/// <summary>
/// 加入方：在本机端口上接受游戏连接，转发给房主。
/// </summary>
internal sealed class JoinService
{
    private readonly IJoinTransport _transport;
    private readonly List<JoinSession> _sessions = new();
    private volatile bool _stopping;

    public JoinService(IJoinTransport transport)
    {
        _transport = transport;
    }

    public void Stop() => _stopping = true;

    public int Run()
    {
        _transport.Start();
        Report.Write("SELF", _transport.SelfId);
        Report.Write("JOINED", "0");
        Report.State("正在连接房主……");

        long lastReport = 0;
        while (!_stopping)
        {
            _transport.Poll();
            IPeerChannel? pair = _transport.AcceptLocal();
            if (pair is not null)
            {
                var session = new JoinSession(pair);
                session.Start();
                _sessions.Add(session);
                Report.Log("游戏已连接，正在通过 Steam 转发数据");
            }
            _sessions.RemoveAll(session => session.IsFinished);

            if (Environment.TickCount64 - lastReport >= 500)
            {
                lastReport = Environment.TickCount64;
                bool connected = _transport.IsConnected;
                Report.Write("JOINED", connected ? "1" : "0");
                Report.Write("PEERS", connected ? "2" : "1");
                Report.Write("PING", _transport.PingMs.ToString("0.0"));
            }
            Thread.Sleep(50);
        }
        return 0;
    }
}

/// <summary>
/// 加入方：本机游戏连接与 Steam 通道的转接。
/// </summary>
internal sealed class JoinSession
{
    private readonly LocalPeerPair _pair;
    private volatile bool _finished;

    public JoinSession(IPeerChannel pair)
    {
        _pair = (LocalPeerPair)pair;
    }

    public bool IsFinished => _finished;

    public void Start()
    {
        var up = new Thread(PumpLocalToRemote) { IsBackground = true, Name = "联机 游戏→房主" };
        up.Start();
        var down = new Thread(PumpRemoteToLocal) { IsBackground = true, Name = "联机 房主→游戏" };
        down.Start();
    }

    private void PumpLocalToRemote()
    {
        var buffer = new byte[32768];
        TcpClient client = _pair.Client;
        try
        {
            while (!_finished)
            {
                int read;
                try
                {
                    read = client.GetStream().Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read <= 0) break;
                try
                {
                    _pair.Peer.Write(buffer, 0, read);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Report.Log("转发游戏数据出错：" + ex.Message);
        }
        finally
        {
            Close();
        }
    }

    private void PumpRemoteToLocal()
    {
        var buffer = new byte[32768];
        TcpClient client = _pair.Client;
        try
        {
            while (!_finished)
            {
                int read = _pair.Peer.Read(buffer, 0, buffer.Length, 500);
                if (read < 0) break;
                if (read == 0) continue;
                try
                {
                    client.GetStream().Write(buffer, 0, read);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Report.Log("转发房主数据出错：" + ex.Message);
        }
        finally
        {
            Close();
        }
    }

    private void Close()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _pair.Client.Dispose();
        }
        catch
        {
            // 忽略
        }
        _pair.Peer.Dispose();
    }
}
