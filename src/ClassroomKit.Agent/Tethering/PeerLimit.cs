using System.Diagnostics;
using ClassroomKit.Core.Logging;
using Microsoft.Win32;

namespace ClassroomKit.Agent.Tethering;

/// <summary>
/// 注册表放宽热点设备数上限：HKLM\SYSTEM\CurrentControlSet\Services\icssvc\Settings\WifiMaxPeers
/// 官方未公开，社区验证。写入后需重启 icssvc 才生效；需要管理员。
/// </summary>
public static class PeerLimit
{
    public const string KeyPath = @"SYSTEM\CurrentControlSet\Services\icssvc\Settings";
    public const string ValueName = "WifiMaxPeers";

    public static int? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key?.GetValue(ValueName) is int v) return v;
            if (key?.GetValue(ValueName) is long l) return (int)l;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Write(int peers, out string message)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            if (key is null)
            {
                message = "无法打开注册表项（需要管理员）";
                return false;
            }
            key.SetValue(ValueName, peers, RegistryValueKind.DWord);
            message = $"已写入 {ValueName}={peers}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写注册表失败：{ex.Message}";
            return false;
        }
    }

    public static bool Delete(out string message)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null)
            {
                message = "注册表项不存在";
                return false;
            }
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            message = "已删除 WifiMaxPeers（恢复系统默认）";
            return true;
        }
        catch (Exception ex)
        {
            message = $"删除失败：{ex.Message}";
            return false;
        }
    }

    public static void RestartIcs(AppLog? log = null)
    {
        try
        {
            Run("sc.exe", "stop icssvc", log);

            // 停服务是异步的：不等它真的 STOPPED 就 start，会因「服务正在停止」直接失败。
            // 每 500ms 查一次，最多 30 秒；sc 的 STATE 文案在部分系统上是本地化的，中英文都认。
            var stopped = false;
            for (var i = 0; i < 60; i++)
            {
                var query = Run("sc.exe", "query icssvc", log);
                if (query.Code != 0) break;   // 服务不存在（没开过热点）：没什么可等的
                if (IsStopped(query.Output))
                {
                    stopped = true;
                    break;
                }
                Thread.Sleep(500);
            }

            if (!stopped) log?.Warn("icssvc 30 秒内没有进入 STOPPED，仍尝试启动");

            var start = Run("sc.exe", "start icssvc", log);
            if (start.Code != 0) log?.Warn($"启动 icssvc 失败（{start.Code}）：{start.Output}");
        }
        catch (Exception ex)
        {
            log?.Warn($"重启 icssvc 失败：{ex.Message}");
        }
    }

    /// <summary>sc query 的输出里有没有「已停止」状态（英文 STOPPED / 中文「已停止」）。</summary>
    private static bool IsStopped(string output) =>
        output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("已停止", StringComparison.Ordinal);

    /// <summary>跑一个外部命令并返回退出码；超时会杀掉进程树并返回 -1。</summary>
    private static (int Code, string Output) Run(string file, string args, AppLog? log)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                log?.Warn($"执行 {file} {args} 失败：无法启动进程");
                return (-1, "");
            }

            // 两个流都要读：只重定向不读，子进程写满管道就会一直卡着（以前就是这里挂死的）。
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(15000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 可能已经自己退了 */ }
                p.WaitForExit(2000);
                log?.Warn($"执行 {file} {args} 超时 15 秒，已终止");
                return (-1, "");
            }

            var output = Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            var text = (output[0] + output[1]).Trim();
            // 非 0 不一定是错（例如 sc stop 一个本来就没跑的服务会返回 1062），降级成 DEBUG
            if (p.ExitCode != 0) log?.Debug($"执行 {file} {args} 退出码 {p.ExitCode}：{text}");
            return (p.ExitCode, text);
        }
        catch (Exception ex)
        {
            log?.Warn($"执行 {file} {args} 失败：{ex.Message}");
            return (-1, "");
        }
    }
}
