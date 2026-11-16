using System.Diagnostics;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Update;
using Microsoft.Win32;

namespace ClassroomKit.Agent.Update;

/// <summary>
/// ClassIsland 插件在本机的落点。Dir = …\Plugins\classroomkit.hotspot（可安装的插件目录），
/// Root = ClassIsland 安装根目录，Version = 该目录 manifest.yml 里的版本（读不到为空串），
/// Source = 定位依据（"ClassIsland 进程" / "扫描 …" / "注册表"），只用于日志。
/// </summary>
public sealed record PluginLocation(string? Dir, string? Root, string Version, string? Source)
{
    /// <summary>找到了插件目录（或同级 Plugins 目录），可以往里解压 cipx。</summary>
    public bool CanInstall => !string.IsNullOrEmpty(Dir);

    /// <summary>同 CanInstall，语义化别名。</summary>
    public bool Found => CanInstall;
}

/// <summary>
/// 定位并安装 ClassIsland 插件（cipx）。定位顺序：
///   1. 正在运行的 ClassIsland / ClassIsland.Desktop 进程所在目录；
///   2. %LOCALAPPDATA%、%APPDATA%、%PROGRAMDATA%、%PROGRAMFILES%（以及用户目录）下名字含 ClassIsland 的目录（深度 ≤ 3，单次扫描有时间预算）；
///   3. 卸载注册表项里的 InstallLocation。
/// 插件目录 = &lt;根&gt;\data\Plugins\classroomkit.hotspot 或 &lt;根&gt;\Plugins\classroomkit.hotspot；
/// 只有插件目录已存在（或同级 Plugins 目录存在）才算「可以装」，避免往不相干的目录里写文件。
/// </summary>
public sealed class PluginInstaller
{
    public const string PluginId = "classroomkit.hotspot";

    private const int MaxScanDepth = 3;
    private const int MaxScanDirsPerRoot = 1500;

    /// <summary>单次根目录扫描的时间预算：超时就带着已找到的结果收手，避免用户目录巨大时把守护卡住。</summary>
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(8);

    private static readonly string[] ProcessNames = { "ClassIsland", "ClassIsland.Desktop" };

    private static readonly string[] ScanSkipNames =
    {
        "$Recycle.Bin", "$WinREAgent", "Windows", "WindowsApps", "System Volume Information",
        "node_modules", ".git", ".vs", "Temp", "Cache", "Package Cache"
    };

    /// <summary>根目录扫描要遍历用户目录，结果缓存 5 分钟；需要立刻重扫时调 InvalidateRootCache。</summary>
    private static readonly TimeSpan RootCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly object RootCacheGate = new();
    private static IReadOnlyList<string>? _rootCache;
    private static DateTimeOffset _rootCacheAt = DateTimeOffset.MinValue;

    private readonly AppLog? _log;

    public PluginInstaller(AppLog? log = null) => _log = log;

    /// <summary>本机是否装了 ClassIsland，以及它的插件目录在哪。</summary>
    public PluginLocation Detect()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();

        foreach (var (root, source) in CandidateRoots(watch))
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var full = SafeFullPath(root);
            if (full is null || !seen.Add(full)) continue;

            var location = TryLocate(full, source);
            if (location is not null) return location;
        }

        return new PluginLocation(null, null, "", null);
    }

    /// <summary>
    /// 把 cipx 装到探测到的插件目录。ClassIsland 正在运行时仍然尝试（部分文件可能没被锁），
    /// 失败会返回「ClassIsland 正在运行，请先退出」的提示。
    /// </summary>
    public (bool Ok, string Message) Install(string cipxPath)
    {
        var location = Detect();
        return InstallTo(cipxPath, location);
    }

    /// <summary>装到指定位置（Detect 的结果或调用方缓存的目录）。</summary>
    public (bool Ok, string Message) InstallTo(string cipxPath, PluginLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.Dir))
        {
            _log?.Warn("没有找到 ClassIsland 插件目录，跳过插件安装");
            return (false, "没有找到 ClassIsland 插件目录（这台机器可能没装 ClassIsland）");
        }

        var running = IsRunning();
        _log?.Info($"安装 ClassIsland 插件：{Path.GetFileName(cipxPath)} → {location.Dir}" +
                   $"（ClassIsland {(running ? "正在运行" : "未运行")}）");

        var result = PluginPackage.ExtractTo(cipxPath, location.Dir);
        switch (result.Status)
        {
            case PluginPackageStatus.Ok:
            {
                var version = ReadVersion(location.Dir);
                var label = version.Length > 0 ? "v" + version : "新版本";
                _log?.Info($"ClassIsland 插件已更新到 {label}：{location.Dir}");
                return (true, $"ClassIsland 插件已更新到 {label}，重启 ClassIsland 后生效");
            }

            case PluginPackageStatus.Locked:
                _log?.Warn("ClassIsland 插件更新失败（文件被占用）：" + result.Message);
                return (false, "ClassIsland 正在运行或插件文件被占用，请先退出 ClassIsland 再升级：" + result.Message);

            default:
                _log?.Warn("ClassIsland 插件包不合法：" + result.Message);
                return (false, "插件包不合法：" + result.Message);
        }
    }

    /// <summary>ClassIsland 是否正在运行（决定是否需要提示先退出）。</summary>
    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            if (processes.Length == 0) continue;
            foreach (var process in processes) process.Dispose();
            return true;
        }

        return false;
    }

    /// <summary>本机所有可能是 ClassIsland 根目录的路径（已去重，只返回存在的目录；结果缓存 5 分钟）。</summary>
    public IReadOnlyList<string> FindRoots()
    {
        lock (RootCacheGate)
        {
            var cached = _rootCache;
            if (cached is not null && DateTimeOffset.Now - _rootCacheAt <= RootCacheTtl)
                return cached;
        }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        foreach (var (root, _) in CandidateRoots(watch))
        {
            // 时间预算：扫不动了就先返回已经找到的，别让调用方等到底
            if (watch.Elapsed > ScanBudget)
            {
                _log?.Warn($"扫描 ClassIsland 根目录超过 {ScanBudget.TotalSeconds:F0} 秒，已中止并返回找到的 {list.Count} 个目录");
                break;
            }

            var full = SafeFullPath(root);
            if (full is not null && seen.Add(full)) list.Add(full);
        }

        lock (RootCacheGate)
        {
            _rootCache = list;
            _rootCacheAt = DateTimeOffset.Now;
        }

        return list;
    }

    /// <summary>清掉根目录扫描的 5 分钟缓存（装/卸 ClassIsland 或需要立刻重新探测时调用）。</summary>
    public static void InvalidateRootCache()
    {
        lock (RootCacheGate)
        {
            _rootCache = null;
            _rootCacheAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>读已装插件的版本（读不到返回空串）。</summary>
    public static string ReadVersion(string pluginDir) =>
        PluginPackage.ReadManifestFromDirectory(pluginDir)?.Version ?? "";

    /// <summary>ClassIsland 根目录下的插件目录候选（data\Plugins 优先，其次 Plugins）。</summary>
    public static IReadOnlyList<string> PluginDirCandidates(string root)
    {
        var list = new List<string>(2);
        try
        {
            list.Add(Path.Combine(root, "data", "Plugins", PluginId));
            list.Add(Path.Combine(root, "Plugins", PluginId));
        }
        catch
        {
            // 路径不合法就当没有
        }

        return list;
    }

    private static PluginLocation? TryLocate(string root, string source)
    {
        if (!Directory.Exists(root)) return null;

        var dataPlugins = Path.Combine(root, "data", "Plugins");
        var plugins = Path.Combine(root, "Plugins");

        var pluginDir = Path.Combine(dataPlugins, PluginId);
        if (Directory.Exists(pluginDir)) return BuildLocation(root, pluginDir, source);

        pluginDir = Path.Combine(plugins, PluginId);
        if (Directory.Exists(pluginDir)) return BuildLocation(root, pluginDir, source);

        // 插件还没装过：只有当 Plugins 目录已在（说明这是 ClassIsland 的数据目录）才允许装
        if (Directory.Exists(dataPlugins)) return BuildLocation(root, Path.Combine(dataPlugins, PluginId), source);
        if (Directory.Exists(plugins)) return BuildLocation(root, Path.Combine(plugins, PluginId), source);

        return null;
    }

    private static PluginLocation BuildLocation(string root, string dir, string source) =>
        new(dir, root, Directory.Exists(dir) ? ReadVersion(dir) : "", source);

    private IEnumerable<(string Root, string Source)> CandidateRoots(Stopwatch watch)
    {
        foreach (var root in ProcessRoots())
        {
            if (!string.IsNullOrWhiteSpace(root)) yield return (root, "ClassIsland 进程");
        }

        foreach (var baseDir in ScanRoots())
        {
            foreach (var dir in FindClassIslandDirs(baseDir, watch))
                yield return (dir, "扫描 " + baseDir);
        }

        foreach (var install in RegistryRoots())
        {
            if (!string.IsNullOrWhiteSpace(install)) yield return (install, "注册表");
        }
    }

    private static List<string> ProcessRoots()
    {
        var roots = new List<string>();
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(file)) roots.Add(Path.GetDirectoryName(file)!);
                }
                catch
                {
                    // 权限不足 / 32-64 位不匹配 / 进程已退出，跳过
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return roots;
    }

    private static IEnumerable<string> ScanRoots()
    {
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.CommonApplicationData,
                     // 便携版常放在桌面/文档/下载里（ZYT 就是 Desktop\ClassIsland），从用户目录广度扫一层
                     Environment.SpecialFolder.UserProfile
                 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }

        foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var path = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    /// <summary>广度优先找名字含 ClassIsland 的目录（深度 ≤ MaxScanDepth，单次扫描最多 ScanBudget）。</summary>
    private static List<string> FindClassIslandDirs(string root, Stopwatch watch)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return results;
        if (watch.Elapsed > ScanBudget) return results;

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < MaxScanDirsPerRoot)
        {
            // 每处理一个目录看一眼时间预算：用户目录动辄几万个目录，磁盘一慢必须能中途收手
            if (watch.Elapsed > ScanBudget) break;

            var (current, depth) = queue.Dequeue();
            visited++;

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
            {
                var childDepth = depth + 1;
                if (childDepth > MaxScanDepth) continue;

                var name = Path.GetFileName(child);
                if (IsSkipped(name)) continue;

                if (name.Contains("ClassIsland", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(child);
                    continue;   // 命中后不再往里找，插件目录就在它下面
                }

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }

                if (childDepth < MaxScanDepth) queue.Enqueue((child, childDepth));
            }
        }

        return results;
    }

    private static bool IsSkipped(string name)
    {
        foreach (var skip in ScanSkipNames)
        {
            if (name.Equals(skip, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>卸载注册表里的 InstallLocation（HKLM/HKCU × 64/32 位视图）。</summary>
    private static List<string> RegistryRoots()
    {
        var roots = new List<string>();
        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(uninstall);
                    if (key is null) continue;

                    foreach (var name in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(name);
                            if (sub is null) continue;

                            var display = sub.GetValue("DisplayName") as string ?? "";
                            if (!display.Contains("ClassIsland", StringComparison.OrdinalIgnoreCase)) continue;

                            var location = sub.GetValue("InstallLocation") as string ?? "";
                            if (!string.IsNullOrWhiteSpace(location)) roots.Add(location.Trim().Trim('"'));
                        }
                        catch
                        {
                            // 单个子键读失败不影响其它项
                        }
                    }
                }
                catch
                {
                    // 注册表不可用（受限环境）就算了，前面的扫描已经够用
                }
            }
        }

        return roots;
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return Directory.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
