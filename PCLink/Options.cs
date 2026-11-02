namespace PCLink;

internal enum LinkMode
{
    Host,
    Join,
    SelfTest,
    Version,
    Help
}

internal sealed class Options
{
    public LinkMode Mode { get; private set; } = LinkMode.Help;
    /// <summary>房主：要转发到的本地 MC 端口；加入方：本地监听端口。</summary>
    public int Port { get; private set; } = 25565;
    /// <summary>加入方：房主 SteamID（或内网测试用的房间号）。</summary>
    public ulong Code { get; private set; }
    public bool Loopback { get; private set; }
    public string LoopbackDir { get; private set; } = "";
    public bool Probe { get; private set; } = true;
    public string? Error { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        if (args.Length == 0)
        {
            options.Error = "缺少参数";
            return options;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "host":
            case "create":
                options.Mode = LinkMode.Host;
                break;
            case "join":
                options.Mode = LinkMode.Join;
                break;
            case "selftest":
                options.Mode = LinkMode.SelfTest;
                return options;
            case "version":
                options.Mode = LinkMode.Version;
                return options;
            case "help":
            case "-h":
            case "--help":
                options.Mode = LinkMode.Help;
                return options;
            default:
                options.Error = $"未知的命令：{args[0]}";
                return options;
        }

        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            string? value = i + 1 < args.Length ? args[i + 1] : null;
            switch (key.ToLowerInvariant())
            {
                case "--port":
                case "--game-port":
                case "--local-port":
                    if (!int.TryParse(value, out int port) || port is < 1 or > 65535)
                    {
                        options.Error = $"端口无效：{value}";
                        return options;
                    }
                    options.Port = port;
                    i++;
                    break;
                case "--code":
                case "--lobby":
                case "--steamid":
                case "--host":
                    if (!ulong.TryParse(value, out ulong code) || code == 0)
                    {
                        options.Error = $"房间号无效：{value}";
                        return options;
                    }
                    options.Code = code;
                    i++;
                    break;
                case "--loopback":
                    options.Loopback = true;
                    break;
                case "--loopback-dir":
                    options.Loopback = true;
                    options.LoopbackDir = value ?? "";
                    i++;
                    break;
                case "--no-probe":
                    options.Probe = false;
                    break;
                default:
                    if (key.StartsWith("--", StringComparison.Ordinal))
                    {
                        options.Error = $"未知的参数：{key}";
                        return options;
                    }
                    // 允许直接写房间号
                    if (ulong.TryParse(key, out ulong standaloneCode))
                    {
                        options.Code = standaloneCode;
                        break;
                    }
                    options.Error = $"无法识别的参数：{key}";
                    return options;
            }
        }

        if (options.Loopback && string.IsNullOrEmpty(options.LoopbackDir))
        {
            options.LoopbackDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCLink");
        }

        if (options.Mode == LinkMode.Join && options.Code == 0)
        {
            options.Error = "加入房间需要提供房间号（--code）";
        }

        return options;
    }
}
