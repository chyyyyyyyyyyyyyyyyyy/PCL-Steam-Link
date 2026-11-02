using System.Text;

namespace PCLink;

/// <summary>
/// 输出给 PCL 的日志。每行都是 KEY=VALUE 格式，PCL 会逐行解析。
/// </summary>
internal static class Report
{
    private static readonly object SyncRoot = new();

    static Report()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 忽略：输出编码设置失败不影响主流程
        }
    }

    public static void Write(string key, string value)
    {
        lock (SyncRoot)
        {
            try
            {
                Console.Out.WriteLine($"{key}={value}");
                Console.Out.Flush();
            }
            catch
            {
                // 输出管道已关闭
            }
        }
    }

    public static void State(string text) => Write("STATE", text);
    public static void Log(string text) => Write("LOG", text);
    public static void Error(string text) => Write("ERROR", text);
}
