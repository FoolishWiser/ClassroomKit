using System.Diagnostics;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Service;

/// <summary>
/// 由 SYSTEM 护航任务执行 msiexec /qn —— 全程无窗口、无 UAC。
/// 用户态守护只负责下载 + 投递请求；这里先把它停掉（否则它锁着要替换的 exe），装完再拉回用户会话。
/// </summary>
public static class InstallRunner
{
    public static (bool Ok, string Message) Run(string msiPath, AppLog log)
    {
        if (!File.Exists(msiPath)) return (false, $"升级包不存在：{msiPath}");
        if (!msiPath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return (false, "不是 MSI 安装包：" + Path.GetFileName(msiPath));

        var stopped = StopUserInstances(log);
        var (ok, message) = RunMsiexec(msiPath, log);

        if (ok)
        {
            // MSI 的 deferred 自定义动作通常会自己把新版本拉起来；万一没有（升级时没人登录等），这里兜底。
            Thread.Sleep(2000);
            if (UserInstanceCount() == 0)
            {
                try
                {
                    var (started, startMessage) = ScheduledTaskInstaller.StartInUserSession(Paths.ExePath);
                    log.Info(started ? "已把 ClassroomKit 拉回用户会话：" + startMessage : "暂未拉起（下次登录会自动启动）：" + startMessage);
                }
                catch (Exception ex)
                {
                    log.Warn("拉起新版本失败：" + ex.Message);
                }
            }
        }

        return (ok, message + (stopped > 0 ? $"（已先停掉 {stopped} 个运行实例）" : ""));
    }

    private static (bool Ok, string Message) RunMsiexec(string msiPath, AppLog log)
    {
        var logFile = Path.Combine(Paths.LogDir, "msi-upgrade.log");
        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            var psi = new ProcessStartInfo("msiexec.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments = $"/i \"{msiPath}\" /qn /norestart REBOOT=ReallySuppress /l*v \"{logFile}\""
            };
            using var process = Process.Start(psi);
            if (process is null) return (false, "无法启动 msiexec");
            if (!process.WaitForExit(600_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
                return (false, "msiexec 超时（10 分钟）");
            }

            return process.ExitCode switch
            {
                0 => (true, "静默安装完成"),
                3010 => (true, "静默安装完成（需要重启才能完全生效）"),
                _ => (false, $"msiexec 退出码 {process.ExitCode}，详见 {logFile}")
            };
        }
        catch (Exception ex)
        {
            log.Warn($"静默安装失败：{ex.Message}");
            return (false, "静默安装失败：" + ex.Message);
        }
    }

    /// <summary>停掉用户会话里的守护（否则 MSI 替换不了正在运行的 exe）。返回停掉的个数。</summary>
    private static int StopUserInstances(AppLog log)
    {
        var self = Environment.ProcessId;
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("ClassroomKit.Agent"))
        {
            try
            {
                if (process.Id == self) continue;
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                killed++;
            }
            catch (Exception ex)
            {
                log.Warn($"停止进程 {process.Id} 失败：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        return killed;
    }

    private static int UserInstanceCount()
    {
        var self = Environment.ProcessId;
        var others = 0;
        foreach (var process in Process.GetProcessesByName("ClassroomKit.Agent"))
        {
            using (process)
            {
                if (process.Id != self) others++;
            }
        }

        return others;
    }
}
