using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ClassroomKit.Agent.Shell;

/// <summary>
/// 未处理异常落盘：老师那边崩了以后，不用现场复现也能拿到堆栈。
/// 只写文件、不弹自定义窗、不吞异常——进程该退还是退，避免带病继续跑。
/// </summary>
public static class CrashLog
{
    private static int _writing;
    private static readonly object Gate = new();

    public static void Install(System.Windows.Application app)
    {
        app.DispatcherUnhandledException += (_, e) => Write("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("appdomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("task", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>返回写出的文件路径；写不进去时返回 null（崩溃路径本身不能再抛）。</summary>
    public static string? Write(string kind, Exception? ex)
    {
        if (Interlocked.Exchange(ref _writing, 1) == 1) return null;
        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            var path = Path.Combine(Paths.LogDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(kind)}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("ClassroomKit 崩溃报告");
            sb.AppendLine("时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            sb.AppendLine("类型：" + kind);
            sb.AppendLine("版本：" + AgentRuntime.DisplayVersion());
            sb.AppendLine("系统：" + Environment.OSVersion);
            sb.AppendLine("进程：" + Environment.ProcessId);
            sb.AppendLine("管理员：" + AdminElevation.IsElevated());
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "（没有异常对象）");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    private static string Safe(string value)
    {
        var chars = value.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}