using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ClassroomKit.Agent.Shell;

/// <summary>
/// 自我卸载：不依赖用户在“设置 → 应用”里找，面板点一下 / 命令行一句就卸载。
/// 两种安装态分开处理：
/// - MSI 装的：让 msiexec /x 走标准卸载（会自动停进程、删计划任务、还原 WifiMaxPeers）；
/// - 绿色版（install.ps1 装的）：用临时 cmd 脚本在进程退出后自删目录、任务和注册表值。
/// 之所以要用临时脚本：Windows 不允许删除正在运行的 exe，只能“先退出、后删除”。
/// </summary>
public static class SelfUninstaller
{
    public sealed record Plan(bool ViaMsi, string? ProductCode, string ExeDir, string DataDir, bool KeepData)
    {
        public string Mode => ViaMsi ? "MSI" : "绿色版";
        public string Describe() => ViaMsi
            ? $"通过 Windows 安装包卸载（产品码 {ProductCode}），配置与日志保留在 {DataDir}"
            : $"删除程序目录 {ExeDir}、登录自启任务、注册表放宽项" + (KeepData ? "，配置与日志保留" : "，并删除配置与日志");
    }

    /// <summary>探测当前安装态，决定用哪条路卸载。</summary>
    public static Plan BuildPlan(bool keepData)
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var code = FindMsiProductCode();
        return new Plan(code is not null, code, exeDir, Paths.DataDir, keepData);
    }

    /// <summary>启动卸载，然后调用方应尽快退出进程。返回给面板/命令行的说明文字。</summary>
    public static (bool Ok, string Message) Start(bool keepData, Action<string>? log = null)
    {
        var plan = BuildPlan(keepData);

        if (plan.ViaMsi && plan.ProductCode is { } productCode)
        {
            var script =
                "@echo off\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "taskkill /f /im ClassroomKit.Agent.exe >nul 2>&1\r\n" +
                "timeout /t 1 /nobreak >nul\r\n" +
                $"msiexec /x {productCode} /qn /norestart\r\n" +
                (keepData ? "" : $"rmdir /s /q \"{plan.DataDir}\" >nul 2>&1\r\n") +
                "del \"%~f0\" >nul 2>&1\r\n";

            if (!RunScript(script, out var error))
                return (false, $"卸载脚本没能启动：{error}");

            log?.Invoke($"开始卸载（{plan.Mode}）：msiexec /x {productCode}");
            return (true, "正在卸载（Windows 安装包模式）：程序会退出，几秒后从「应用和功能」里消失");
        }

        var dataLine = keepData ? "" : $"rmdir /s /q \"{plan.DataDir}\" >nul 2>&1\r\n";
        var green =
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "taskkill /f /im ClassroomKit.Agent.exe >nul 2>&1\r\n" +
            "schtasks /delete /tn \"ClassroomKit-Agent\" /f >nul 2>&1\r\n" +
            "reg delete \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\icssvc\\Settings\" /v WifiMaxPeers /f >nul 2>&1\r\n" +
            "sc stop icssvc >nul 2>&1\r\n" +
            "sc start icssvc >nul 2>&1\r\n" +
            "timeout /t 1 /nobreak >nul\r\n" +
            $"rmdir /s /q \"{plan.ExeDir}\" >nul 2>&1\r\n" +
            dataLine +
            "del \"%~f0\" >nul 2>&1\r\n";

        if (!RunScript(green, out var greenError))
            return (false, $"卸载脚本没能启动：{greenError}");

        log?.Invoke($"开始卸载（{plan.Mode}）：{plan.Describe()}");
        return (true, "正在卸载（绿色版模式）：程序会退出，目录与自启项随后被清理");
    }

    /// <summary>找出本机是否由 MSI 安装（ARP 里有 ClassroomKit 且 WindowsInstaller=1）。</summary>
    public static string? FindMsiProductCode()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    if (!name.StartsWith('{')) continue;
                    using var entry = uninstall.OpenSubKey(name);
                    var display = entry?.GetValue("DisplayName") as string;
                    var installer = entry?.GetValue("WindowsInstaller");
                    if (display is not null && display.StartsWith("ClassroomKit", StringComparison.OrdinalIgnoreCase)
                        && installer is int i && i == 1)
                    {
                        return name;
                    }
                }
            }
            catch
            {
                // 读不到就换一个视图；都没有就当绿色版
            }
        }
        return null;
    }

    private static bool RunScript(string content, out string error)
    {
        error = "";
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"ck-uninstall-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(path, content, Encoding.Default);

            // 必须提权：删 Program Files 下的目录、调 msiexec /x 都要管理员。
            // 已经是管理员（比如从计划任务/命令行提权跑）就直接起；否则走 runas 弹一次 UAC。
            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{path}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            if (!AdminElevation.IsElevated()) psi.Verb = "runas";
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权，未卸载";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
