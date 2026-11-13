using System.Diagnostics;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Service;

/// <summary>
/// 由 SYSTEM 护航任务执行 msiexec /qn —— 全程无窗口、无 UAC。
/// 用户态守护只负责下载 + 投递请求，然后退出，让 MSI 能替换文件。
/// </summary>
public static class InstallRunner
{
    public static (bool Ok, string Message) Run(string msiPath, AppLog log)
    {
        if (!File.Exists(msiPath)) return (false, $"升级包不存在：{msiPath}");

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
}
