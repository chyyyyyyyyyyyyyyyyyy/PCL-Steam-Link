namespace PCLink;

internal static class Program
{
    public const string Version = "1.0.0";

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report.Error("内部错误：" + (e.ExceptionObject as Exception)?.Message);

        Options options = Options.Parse(args);
        if (options.Error is not null)
        {
            Report.Error(options.Error);
            return 2;
        }

        switch (options.Mode)
        {
            case LinkMode.Version:
                Report.Write("VERSION", Version);
                return 0;
            case LinkMode.Help:
                PrintHelp();
                return 0;
            case LinkMode.SelfTest:
                return SelfTest.Run();
            case LinkMode.Host:
                return RunHost(options);
            case LinkMode.Join:
                return RunJoin(options);
            default:
                Report.Error("未知运行模式");
                return 2;
        }
    }

    private static int RunHost(Options options)
    {
        IHostTransport transport = options.Loopback
            ? new LoopbackHost(options.LoopbackDir)
            : new SteamHost();
        try
        {
            var service = new HostService(transport, options.Port);
            return service.Run();
        }
        catch (LinkException ex)
        {
            Report.Error(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Report.Error("启动联机失败：" + ex.Message);
            return 4;
        }
        finally
        {
            transport.Dispose();
            if (!options.Loopback) SteamSession.Shutdown();
        }
    }

    private static int RunJoin(Options options)
    {
        IJoinTransport transport = options.Loopback
            ? new LoopbackJoin(options.LoopbackDir, options.Code, options.Port)
            : new SteamJoin(options.Code, options.Port);
        try
        {
            var service = new JoinService(transport);
            return service.Run();
        }
        catch (LinkException ex)
        {
            Report.Error(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            Report.Error("加入联机失败：" + ex.Message);
            return 4;
        }
        finally
        {
            transport.Dispose();
            if (!options.Loopback) SteamSession.Shutdown();
        }
    }

    private static void PrintHelp()
    {
        Report.Write("HELP", "PCL 联机模块 v" + Version);
        Report.Write("USAGE", "PCLink.exe host  --port <游戏端口> [--loopback]");
        Report.Write("USAGE", "PCLink.exe join  --code <房间号> --port <本地端口> [--loopback]");
        Report.Write("USAGE", "PCLink.exe selftest");
        Report.Write("USAGE", "PCLink.exe version");
    }
}
