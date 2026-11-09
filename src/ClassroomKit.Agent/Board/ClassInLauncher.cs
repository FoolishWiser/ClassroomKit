using System.Diagnostics;
using ClassroomKit.Core.Logging;
using Microsoft.Win32;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 找到并启动 ClassInX。
/// 学校一体机上 ClassInX 经常根本没在运行（开机不自启、老师还没点开），
/// 这时「保存板书」必须先把它拉起来，等板书窗口出现再继续。
/// 查找顺序：配置里写死的路径 → 正在运行的进程 → 卸载注册表 → 常见安装目录。
/// </summary>
public static class ClassInLauncher
{
    /// <summary>ClassInX 的可执行文件名（不同版本叫法不一样）。</summary>
    public static readonly string[] ExeNames = { "ClassIn X.exe", "ClassInX.exe", "ClassIn.exe" };

    /// <summary>进程名（不含 .exe）。</summary>
    public static readonly string[] ProcessNames = { "ClassIn X", "ClassInX", "ClassIn" };

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            if (Process.GetProcessesByName(name).Length > 0) return true;
        }
        return false;
    }

    /// <summary>定位 ClassInX.exe；找不到返回 null。</summary>
    public static string? FindExe(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var path in RunningCandidates()) return path;
        foreach (var path in RegistryCandidates()) return path;
        foreach (var path in CommonCandidates()) return path;
        return null;
    }

    private static IEnumerable<string> RunningCandidates()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                string? file = null;
                try { file = process.MainModule?.FileName; }
                catch { }
                finally { process.Dispose(); }

                if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) yield return file!;
            }
        }
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var found = new List<string>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                RegistryKey? baseKey = null;
                RegistryKey? uninstall = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var sub = uninstall.OpenSubKey(name);
                        var display = sub?.GetValue("DisplayName") as string;
                        if (display is null || !display.Contains("ClassIn", StringComparison.OrdinalIgnoreCase)) continue;

                        if (sub!.GetValue("DisplayIcon") is string icon && icon.Length > 0)
                        {
                            var path = icon.Trim().Trim('"');
                            var comma = path.IndexOf(',');
                            if (comma > 0) path = path[..comma];
                            if (File.Exists(path)) found.Add(path);
                        }

                        if (sub.GetValue("InstallLocation") is string dir && dir.Length > 0)
                        {
                            foreach (var exe in ExeNames)
                            {
                                var candidate = Path.Combine(dir, exe);
                                if (File.Exists(candidate)) found.Add(candidate);
                            }
                        }
                    }
                }
                catch
                {
                    // 注册表个别键无权限，跳过
                }
                finally
                {
                    uninstall?.Dispose();
                    baseKey?.Dispose();
                }
            }
        }
        return found;
    }

    private static IEnumerable<string> CommonCandidates()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var folder in new[] { "ClassIn X", "ClassInX", "ClassIn" })
            {
                foreach (var exe in ExeNames)
                {
                    var candidate = Path.Combine(root, folder, exe);
                    if (File.Exists(candidate)) yield return candidate;
                }
            }
        }
    }

    /// <summary>启动 ClassInX。返回是否成功、给用户看的说明、以及用到的路径。</summary>
    public static (bool Ok, string Message, string? Path) Start(string? configured, AppLog? log = null)
    {
        var exe = FindExe(configured);
        if (exe is null)
        {
            const string message = "没有找到 ClassInX 程序（已查注册表、常见安装目录和运行中的进程）：请先手动启动一次 ClassInX，或在 config.json 里用 boardSave.classInPath 指定 ClassIn X.exe 的完整路径";
            log?.Warn($"启动 ClassInX 失败：{message}");
            return (false, message, null);
        }

        var dir = Path.GetDirectoryName(exe) ?? string.Empty;

        // 先普通启动：绝大多数机器上 ClassInX 不需要管理员，别平白弹 UAC
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = dir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            });
            log?.Info($"已启动 ClassInX：{exe}");
            return (true, $"已启动 ClassInX（{exe}）", exe);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // 清单要求管理员：再来一次带 runas 的启动
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                });
                log?.Info($"已启动 ClassInX（需要管理员，已提权）：{exe}");
                return (true, $"已启动 ClassInX（{exe}）", exe);
            }
            catch (System.ComponentModel.Win32Exception inner) when (inner.NativeErrorCode == 1223)
            {
                const string cancelled = "启动 ClassInX 需要管理员授权，但授权被取消了：请手动启动 ClassInX 后再试";
                log?.Warn(cancelled);
                return (false, cancelled, exe);
            }
            catch (Exception inner)
            {
                log?.Warn($"启动 ClassInX 失败：{inner.Message}");
                return (false, $"启动 ClassInX 失败：{inner.Message}", exe);
            }
        }
        catch (Exception ex)
        {
            log?.Warn($"启动 ClassInX 失败：{ex.Message}");
            return (false, $"启动 ClassInX 失败：{ex.Message}", exe);
        }
    }
}
