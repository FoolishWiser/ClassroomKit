using System.Diagnostics;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Bcu;

/// <summary>BCUninstaller（Apache-2.0）便携版：只负责找到并拉起，不做卸载逻辑。</summary>
public static class BcuLauncher
{
    private static readonly string[] RelativeCandidates =
    {
        @"BCU\BCUninstaller.exe",
        @"bcu\BCUninstaller.exe",
        @"BCUninstaller.exe"
    };

    private static readonly string[] AbsoluteCandidates =
    {
        @"C:\Program Files\ClassroomKit\BCU\BCUninstaller.exe",
        @"C:\Program Files\BCUninstaller\BCUninstaller.exe",
        @"C:\Program Files (x86)\BCUninstaller\BCUninstaller.exe"
    };

    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var rel in RelativeCandidates)
        {
            var path = Path.Combine(Paths.ExeDir, rel);
            if (File.Exists(path)) return path;
        }

        foreach (var path in AbsoluteCandidates)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    public static (bool Ok, string Message) Launch(BcuConfig config)
    {
        if (!config.Enabled) return (false, "强力卸载功能已在配置中关闭");

        var path = Locate(config.Path);
        if (path is null)
            return (false, "未找到 BCUninstaller.exe；请把它放到程序目录的 BCU 子目录，或在 config.json 里写 bcu.path");

        try
        {
            var psi = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(path) ?? Paths.ExeDir
            };
            Process.Start(psi);
            return (true, $"已拉起 {Path.GetFileName(path)}（UAC 已由 BCU 处理）");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "用户取消了 UAC 授权");
        }
        catch (Exception ex)
        {
            return (false, $"拉起失败：{ex.Message}");
        }
    }
}
