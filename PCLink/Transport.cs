using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Steamworks;

namespace PCLink;

/// <summary>
/// 联机过程中可以直接展示给用户的错误。
/// </summary>
internal sealed class LinkException : Exception
{
    public LinkException(string message) : base(message) { }
}

internal interface IHostTransport : IDisposable
{
    string SelfId { get; }
    string InviteCode { get; }
    int PeerCount { get; }
    double AveragePingMs { get; }
    void Start();
    void Poll();
    bool TryAcceptPeer(out IPeerChannel peer);
}

internal interface IJoinTransport : IDisposable
{
    string SelfId { get; }
    int LocalPort { get; }
    double PingMs { get; }
    bool IsConnected { get; }
    void Start();
    void Poll();
    IPeerChannel? AcceptLocal();
}

/// <summary>
/// Steam 的初始化与回调线程。
/// </summary>
internal static class SteamSession
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;
    private static bool _calledShutdown;
    private static Thread? _pumpThread;
    private static volatile bool _stopping;

    public static bool IsInitialized => _initialized;

    public static ulong SelfSteamId
    {
        get
        {
            if (!_initialized) throw new LinkException("Steam 尚未初始化");
            return SteamUser.GetSteamID().m_SteamID;
        }
    }

    public static void Initialize()
    {
        lock (SyncRoot)
        {
            if (_initialized) return;
            EnsureAppIdFile();
            bool ok;
            try
            {
                ok = SteamAPI.Init();
            }
            catch (Exception ex)
            {
                throw new LinkException(
                    "无法加载 Steam 组件：" + ex.Message + Environment.NewLine +
                    "请确认本机已安装并登录 Steam 客户端，且联机模块的 steam_api64.dll 未被杀毒软件删除。");
            }
            if (!ok)
            {
                throw new LinkException(
                    "无法连接 Steam。请确认：" + Environment.NewLine +
                    "1. 本机已安装 Steam 客户端，并已登录账号；" + Environment.NewLine +
                    "2. 联机模块所在文件夹中的 steam_api64.dll 与 steam_appid.txt 未被删除。");
            }
            _initialized = true;
            _stopping = false;
            _pumpThread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "Steam 回调"
            };
            _pumpThread.Start();
        }
    }

    private static void EnsureAppIdFile()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "steam_appid.txt");
            if (!File.Exists(path)) File.WriteAllText(path, "480");
        }
        catch
        {
            // 忽略：没有写权限时由 Steam 自行处理
        }
    }

    private static void PumpLoop()
    {
        while (!_stopping)
        {
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception ex)
            {
                Report.Log("Steam 回调异常：" + ex.Message);
            }
            Thread.Sleep(5);
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            _stopping = true;
            if (_initialized && !_calledShutdown)
            {
                _calledShutdown = true;
                try
                {
                    SteamAPI.Shutdown();
                }
                catch
                {
                    // 忽略
                }
            }
            _initialized = false;
        }
    }
}

/// <summary>
/// 房主：通过 Steam P2P 监听玩家的连接。
/// </summary>
internal sealed class SteamHost : IHostTransport
{
    private readonly ConcurrentQueue<IPeerChannel> _newPeers = new();
    private readonly List<IPeerChannel> _peers = new();
    private HSteamListenSocket _listenSocket;
    private Callback<SteamNetConnectionStatusChangedCallback_t>? _statusCallback;
    private volatile bool _disposed;

    public string SelfId { get; private set; } = "";
    public string InviteCode => SelfId;
    public int PeerCount => _peers.Count;
    public double AveragePingMs => _peers.Count == 0 ? 0 : _peers.Average(p => p.PingMs);

    public void Start()
    {
        SteamSession.Initialize();
        SelfId = SteamSession.SelfSteamId.ToString();
        try
        {
            _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, 0, Array.Empty<SteamNetworkingConfigValue_t>());
        }
        catch (Exception ex)
        {
            throw new LinkException("无法在 Steam 网络中创建房间：" + ex.Message + Environment.NewLine +
                                    "请确认 Steam 已登录、Steam 好友与聊天服务可正常使用（Steam 处于离线模式时无法联机）。");
        }

        if (_listenSocket.m_HSteamListenSocket == 0)
        {
            throw new LinkException("无法在 Steam 网络中创建房间，请稍后重试。");
        }
        _statusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t param)
    {
        try
        {
            if (param.m_info.m_eState != ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) return;
            EResult result = SteamNetworkingSockets.AcceptConnection(param.m_hConn);
            if (result != EResult.k_EResultOK)
            {
                Report.Log($"接受玩家连接失败：{result}");
                SteamNetworkingSockets.CloseConnection(param.m_hConn, 0, "接受失败", false);
                return;
            }
            ulong remote = param.m_info.m_identityRemote.GetSteamID64();
            var peer = new SteamPeer(param.m_hConn, remote.ToString());
            Report.Log($"玩家已连接：{remote}");
            _newPeers.Enqueue(peer);
        }
        catch (Exception ex)
        {
            Report.Log("处理玩家连接时出错：" + ex.Message);
        }
    }

    public void Poll()
    {
        // 清理已经断开的通道
        _peers.RemoveAll(peer =>
        {
            if (peer.IsOpen) return false;
            peer.Dispose();
            return true;
        });
    }

    public bool TryAcceptPeer(out IPeerChannel peer)
    {
        if (_newPeers.TryDequeue(out IPeerChannel? accepted))
        {
            _peers.Add(accepted);
            peer = accepted;
            return true;
        }
        peer = null!;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            foreach (IPeerChannel peer in _peers) peer.Dispose();
            foreach (IPeerChannel peer in _newPeers) peer.Dispose();

            if (_listenSocket.m_HSteamListenSocket != 0)
            {
                SteamNetworkingSockets.CloseListenSocket(_listenSocket);
                _listenSocket = HSteamListenSocket.Invalid;
            }
        }
        catch
        {
            // 忽略
        }
        _statusCallback?.Dispose();
        _statusCallback = null;
    }
}

/// <summary>
/// 加入方：把本机端口上的连接转发给房主。
/// </summary>
internal sealed class SteamJoin : IJoinTransport
{
    private readonly ulong _hostSteamId;
    private TcpListener? _listener;
    private IPeerChannel? _probePeer;
    private volatile bool _disposed;

    public SteamJoin(ulong hostSteamId, int localPort)
    {
        _hostSteamId = hostSteamId;
        LocalPort = localPort;
    }

    public string SelfId { get; private set; } = "";
    public int LocalPort { get; }
    public double PingMs => _probePeer?.PingMs ?? 0;
    public bool IsConnected => _probePeer is SteamPeer { IsConnected: true };

    public void Start()
    {
        SteamSession.Initialize();
        SelfId = SteamSession.SelfSteamId.ToString();
        if (!string.IsNullOrEmpty(SelfId) && ulong.TryParse(SelfId, out ulong selfId) && selfId == _hostSteamId)
        {
            throw new LinkException("不能加入自己创建的房间，请把房间号发给朋友。");
        }

        StartProbePeer();

        _listener = new TcpListener(IPAddress.Loopback, LocalPort);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            throw new LinkException($"无法监听本地端口 {LocalPort}：{ex.Message}");
        }
        Report.Write("READY", $"127.0.0.1:{LocalPort}");
        Report.State("已连接到房主，正在等待游戏启动……");
    }

    private void StartProbePeer()
    {
        try
        {
            IPeerChannel peer = ConnectToHost();
            _probePeer = peer;
        }
        catch (Exception ex)
        {
            Report.Log("建立探测连接失败（不影响联机）：" + ex.Message);
        }
    }

    private IPeerChannel ConnectToHost()
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(_hostSteamId);
        HSteamNetConnection connection;
        try
        {
            connection = SteamNetworkingSockets.ConnectP2P(
                ref identity, 0, 0, Array.Empty<SteamNetworkingConfigValue_t>());
        }
        catch (Exception ex)
        {
            throw new LinkException("无法连接房主：" + ex.Message);
        }

        if (connection == HSteamNetConnection.Invalid) throw new LinkException("无法连接房主，请确认房间号是否正确、房主是否已创建房间。");
        return new SteamPeer(connection, _hostSteamId.ToString());
    }

    public void Poll()
    {
        // 玩家全部离开后，保持与房主的探测连接即可
    }

    public IPeerChannel? AcceptLocal()
    {
        if (_listener is null) return null;
        if (!_listener.Pending()) return null;
        TcpClient client = _listener.AcceptTcpClient();
        client.NoDelay = true;
        try
        {
            IPeerChannel peer = ConnectToHost();
            return new LocalPeerPair(client, peer);
        }
        catch (Exception ex)
        {
            Report.Log("转发游戏连接失败：" + ex.Message);
            client.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
        _probePeer?.Dispose();
        _probePeer = null;
    }
}

/// <summary>
/// 内网自测用：房主侧监听本地 TCP，代替 Steam 监听。
/// </summary>
internal sealed class LoopbackHost : IHostTransport
{
    private readonly string _dir;
    private readonly ConcurrentQueue<IPeerChannel> _newPeers = new();
    private readonly List<IPeerChannel> _peers = new();
    private TcpListener? _listener;
    private volatile bool _disposed;

    public LoopbackHost(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        InviteCode = (Random.Shared.Next(100000, 999999)).ToString();
    }

    public string SelfId => "loopback";
    public string InviteCode { get; }
    public int PeerCount => _peers.Count;
    public double AveragePingMs => _peers.Count == 0 ? 0 : 1;

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        File.WriteAllText(Path.Combine(_dir, $"lobby-{InviteCode}.txt"), $"127.0.0.1:{port}");
        var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "内网自测接受" };
        thread.Start();
    }

    private void AcceptLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (_listener is null) return;
                if (!_listener.Pending()) { Thread.Sleep(10); continue; }
                Socket socket = _listener.AcceptSocket();
                _newPeers.Enqueue(new LoopbackPeer(socket, "内网自测连接"));
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                Report.Log("自测监听异常：" + ex.Message);
                Thread.Sleep(50);
            }
        }
    }

    public void Poll() => _peers.RemoveAll(p => !p.IsOpen);

    public bool TryAcceptPeer(out IPeerChannel peer)
    {
        if (_newPeers.TryDequeue(out IPeerChannel? accepted))
        {
            _peers.Add(accepted);
            peer = accepted;
            return true;
        }
        peer = null!;
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
        foreach (IPeerChannel peer in _peers) peer.Dispose();
    }
}

/// <summary>
/// 内网自测用：加入方直接连本地 TCP 房间。
/// </summary>
internal sealed class LoopbackJoin : IJoinTransport
{
    private readonly string _dir;
    private readonly ulong _code;
    private TcpListener? _listener;
    private string _endpoint = "";
    private volatile bool _disposed;

    public LoopbackJoin(string dir, ulong code, int localPort)
    {
        _dir = dir;
        _code = code;
        LocalPort = localPort;
    }

    public string SelfId => "loopback";
    public int LocalPort { get; }
    public double PingMs => 1;
    public bool IsConnected => true;

    public void Start()
    {
        string path = Path.Combine(_dir, $"lobby-{_code}.txt");
        if (!File.Exists(path)) throw new LinkException($"找不到自测房间 {_code}");
        _endpoint = File.ReadAllText(path).Trim();
        _listener = new TcpListener(IPAddress.Loopback, LocalPort);
        _listener.Start();
        Report.Write("READY", $"127.0.0.1:{LocalPort}");
    }

    public void Poll()
    {
    }

    public IPeerChannel? AcceptLocal()
    {
        if (_listener is null) return null;
        if (!_listener.Pending()) return null;
        TcpClient client = _listener.AcceptTcpClient();
        client.NoDelay = true;
        try
        {
            string[] parts = _endpoint.Split(':');
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
            return new LocalPeerPair(client, new LoopbackPeer(socket, "内网自测连接"));
        }
        catch (Exception ex)
        {
            Report.Log("连接自测房主失败：" + ex.Message);
            client.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 忽略
        }
    }
}

/// <summary>
/// 加入方：把本机 TCP 客户端与对端通道拼成一对。
/// </summary>
internal sealed class LocalPeerPair : IPeerChannel
{
    private readonly TcpClient _client;
    private readonly IPeerChannel _peer;

    public LocalPeerPair(TcpClient client, IPeerChannel peer)
    {
        _client = client;
        _client.NoDelay = true;
        _peer = peer;
    }

    public string Description => _peer.Description;
    public bool IsOpen => _peer.IsOpen;
    public double PingMs => _peer.PingMs;
    public TcpClient Client => _client;
    public IPeerChannel Peer => _peer;

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        // 这里读取的是“游戏 -> 房主”方向
        try
        {
            _client.Client.ReceiveTimeout = Math.Max(50, timeoutMs);
            int read = _client.GetStream().Read(buffer, offset, count);
            return read;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    public void Write(byte[] buffer, int offset, int count) => _peer.Write(buffer, offset, count);

    public void Dispose()
    {
        _client.Dispose();
        _peer.Dispose();
    }
}
