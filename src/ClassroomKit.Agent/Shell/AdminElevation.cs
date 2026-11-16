using System.ComponentModel;
using System.Diagnostics;

namespace ClassroomKit.Agent.Shell;

public static class AdminElevation
{
    /// <summary>凭证/UAC 等待的上限：到点就杀掉子进程，绝不无限期挂在安全桌面上。</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

    /// <summary>最近一次提权失败的原因，供上层打印给用户看。</summary>
    public static string? LastError { get; private set; }

    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 有没有「能弹 UAC」的交互桌面。
    /// UAC 的同意界面跑在安全桌面上，会把整台机器交给 consent.exe：
    /// 老师看不见、点不到时，调用线程会永远卡在 WaitForExit 上。所以后台/无人值守路径必须先问这一句。
    /// </summary>
    public static bool CanPrompt()
    {
        if (!Environment.UserInteractive) return false;
        try { return !string.IsNullOrWhiteSpace(Tasks.ScheduledTaskInstaller.ConsoleUser()); }
        catch { return false; }
    }

    /// <summary>
    /// 以管理员重新启动自己（会弹一次 UAC），并等待结束。
    /// 返回值：子进程退出码；-1 启动失败、-2 没有交互桌面（拒绝弹窗）、-3 等待超时已杀掉、-4 用户取消。
    /// </summary>
    public static int RunElevatedAndWait(string arguments, TimeSpan? timeout = null)
    {
        LastError = null;
        if (!CanPrompt())
        {
            LastError = "当前没有可交互的桌面会话，已跳过提权（请到电脑前手动点一次）";
            return -2;
        }

        var psi = new ProcessStartInfo(Paths.ExePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            LastError = "用户取消了管理员授权";
            return -4;
        }
        catch (Exception ex)
        {
            LastError = "提权启动失败：" + ex.Message;
            return -1;
        }

        if (proc is null)
        {
            LastError = "提权启动失败：进程没有起来";
            return -1;
        }

        using (proc)
        {
            try
            {
                var limit = timeout ?? PromptTimeout;
                if (!proc.WaitForExit((int)limit.TotalMilliseconds))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    LastError = $"提权进程 {limit.TotalSeconds:0} 秒没有结束，已强制终止";
                    return -3;
                }
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                LastError = "提权等待失败：" + ex.Message;
                return -1;
            }
        }
    }
}
