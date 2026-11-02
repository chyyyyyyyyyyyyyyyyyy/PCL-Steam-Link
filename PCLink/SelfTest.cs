using System.Net;
using System.Net.Sockets;

namespace PCLink;

/// <summary>
/// 自测：不依赖 Steam，用内网 TCP 走完整条“游戏 → 转发 → 房主 → 游戏端口”链路。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        int failures = 0;
        Report.State("自测开始");
        string dir = Path.Combine(Path.GetTempPath(), "PCLinkSelftest");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 忽略
        }
        Directory.CreateDirectory(dir);

        // 1. 假游戏端口：收到什么就回 "PONG:" + 内容
        var gameServer = new TcpListener(IPAddress.Loopback, 0);
        gameServer.Start();
        int gamePort = ((IPEndPoint)gameServer.LocalEndpoint).Port;
        var gameThread = new Thread(() =>
        {
            while (true)
            {
                TcpClient serverSide;
                try
                {
                    serverSide = gameServer.AcceptTcpClient();
                }
                catch
                {
                    return;
                }
                var handler = new Thread(() =>
                {
                    using (serverSide)
                    {
                        var stream = serverSide.GetStream();
                        var buffer = new byte[4096];
                        try
                        {
                            int read = stream.Read(buffer, 0, buffer.Length);
                            if (read > 0)
                            {
                                byte[] payload = System.Text.Encoding.UTF8.GetBytes("PONG:");
                                byte[] data = new byte[payload.Length + read];
                                payload.CopyTo(data, 0);
                                Buffer.BlockCopy(buffer, 0, data, payload.Length, read);
                                stream.Write(data, 0, data.Length);
                                stream.Flush();
                            }
                        }
                        catch
                        {
                            // 忽略
                        }
                    }
                })
                { IsBackground = true };
                handler.Start();
            }
        })
        { IsBackground = true };
        gameThread.Start();
        Report.Log($"假游戏端口：{gamePort}");

        // 2. 房主
        var host = new LoopbackHost(dir);
        var hostService = new HostService(host, gamePort);
        var hostThread = new Thread(() => hostService.Run()) { IsBackground = true };
        hostThread.Start();

        // 3. 加入方
        int localPort = FindFreePort();
        var join = new LoopbackJoin(dir, ulong.Parse(host.InviteCode), localPort);
        var joinService = new JoinService(join);
        var joinThread = new Thread(() => joinService.Run()) { IsBackground = true };
        joinThread.Start();

        // 4. 等待加入方端口就绪
        if (!WaitPort(localPort, 5000))
        {
            Report.Error("自测失败：加入方没有在本地端口就绪");
            return 1;
        }

        // 5. 假游戏客户端
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, localPort);
                var stream = client.GetStream();
                byte[] message = System.Text.Encoding.UTF8.GetBytes("hello-" + attempt);
                stream.Write(message, 0, message.Length);
                stream.Flush();
                client.ReceiveTimeout = 5000;
                var buffer = new byte[4096];
                int read = stream.Read(buffer, 0, buffer.Length);
                string reply = read > 0 ? System.Text.Encoding.UTF8.GetString(buffer, 0, read) : "";
                string expected = "PONG:hello-" + attempt;
                bool ok = reply == expected;
                Report.Write("CASE", $"转发数据 #{attempt}：{(ok ? "通过" : "失败")}（收到 {reply}）");
                if (!ok) failures++;
            }
            catch (Exception ex)
            {
                Report.Write("CASE", $"转发数据 #{attempt}：失败（{ex.Message}）");
                failures++;
            }
        }

        // 6. Steam 未启动时的错误提示
        try
        {
            SteamSession.Initialize();
            Report.Write("CASE", "Steam 初始化：成功（本机已登录 Steam）");
        }
        catch (LinkException ex)
        {
            string firstLine = ex.Message.Split('\n')[0].Trim();
            bool friendly = firstLine.Contains("Steam");
            Report.Write("CASE", $"Steam 初始化错误提示：{(friendly ? "通过" : "失败")}（{firstLine}）");
            if (!friendly) failures++;
        }
        catch (Exception ex)
        {
            Report.Write("CASE", $"Steam 初始化错误提示：失败（{ex.GetType().Name}）");
            failures++;
        }

        hostService.Stop();
        joinService.Stop();
        try
        {
            gameServer.Stop();
        }
        catch
        {
            // 忽略
        }

        Thread.Sleep(300);
        Report.Write("RESULT", failures == 0 ? "全部通过" : $"{failures} 项失败");
        return failures == 0 ? 0 : 1;
    }

    private static bool WaitPort(int port, int timeoutMs)
    {
        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, port);
                probe.Close();
                return true;
            }
            catch
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
