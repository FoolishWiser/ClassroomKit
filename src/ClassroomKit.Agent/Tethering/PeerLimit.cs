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
        Run("sc.exe", "stop icssvc", log);
        Thread.Sleep(800);
        Run("sc.exe", "start icssvc", log);
    }

    private static void Run(string file, string args, AppLog? log)
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
            p?.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            log?.Warn($"执行 {file} {args} 失败：{ex.Message}");
        }
    }
}
