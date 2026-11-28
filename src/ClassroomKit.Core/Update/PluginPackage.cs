using System.IO.Compression;
using System.Text;

namespace ClassroomKit.Core.Update;

/// <summary>ClassIsland 插件清单（manifest.yml）里更新流程需要的字段。</summary>
public sealed record PluginManifest(string Id, string Version);

/// <summary>处理插件包（.cipx）的结果：成功 / 被占用（重试即可）/ 包本身不合法。</summary>
public enum PluginPackageStatus
{
    Ok,
    Locked,
    Invalid,
    /// <summary>目标目录没有写入权限（不是被占用，重试也不会成功）。</summary>
    Denied
}

/// <summary>插件包处理结果。Message 直接给日志和面板用。</summary>
public sealed record PluginPackageResult(PluginPackageStatus Status, string Message)
{
    public bool Ok => Status == PluginPackageStatus.Ok;
    public bool Locked => Status == PluginPackageStatus.Locked;
    public bool Invalid => Status == PluginPackageStatus.Invalid;
    public bool PermissionDenied => Status == PluginPackageStatus.Denied;

    public static PluginPackageResult Success(string message) => new(PluginPackageStatus.Ok, message);
    public static PluginPackageResult Busy(string message) => new(PluginPackageStatus.Locked, message);
    public static PluginPackageResult Bad(string message) => new(PluginPackageStatus.Invalid, message);
    public static PluginPackageResult NoAccess(string message) => new(PluginPackageStatus.Denied, message);
}

/// <summary>
/// 读写 ClassIsland 插件包（.cipx，本质是 zip）。纯文件操作，不联网，便于单测。
/// 包内布局兼容两种打包方式：条目直接在根（manifest.yml、ClassroomKit.ClassIsland.dll…），
/// 或统一套一层目录（classroomkit.hotspot/manifest.yml…）。
/// 解压采用「先解到同级临时目录，再逐文件覆盖目标目录」的两步法：
/// 这样校验失败不会留下半套文件，而且目标目录里的 data/、Config/ 等用户数据不会被清掉。
/// </summary>
public static class PluginPackage
{
    public const string ManifestFile = "manifest.yml";
    public const string AssemblyFile = "ClassroomKit.ClassIsland.dll";

    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>读 zip 里的 manifest.yml，解析 id 与 version；读不到或格式不对返回 null。</summary>
    public static PluginManifest? ReadManifest(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath)) return null;

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            ZipArchiveEntry? found = null;
            var bestDepth = int.MaxValue;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').TrimEnd('/');
                if (!Path.GetFileName(name).Equals(ManifestFile, StringComparison.OrdinalIgnoreCase)) continue;
                var depth = name.Count(c => c == '/');
                if (depth >= bestDepth) continue;
                bestDepth = depth;
                found = entry;
            }
            if (found is null) return null;

            using var stream = found.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return ParseManifest(reader.ReadToEnd());
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读已装插件目录里的 manifest.yml（检测本机插件版本用）。</summary>
    public static PluginManifest? ReadManifestFromDirectory(string pluginDir)
    {
        if (string.IsNullOrWhiteSpace(pluginDir)) return null;

        try
        {
            var path = Path.Combine(pluginDir, ManifestFile);
            return File.Exists(path) ? ParseManifest(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把插件包解压并覆盖到 targetDir（通常是 …\Plugins\classroomkit.hotspot）。
    /// 校验：必须含 manifest.yml 与 ClassroomKit.ClassIsland.dll；拒绝绝对路径、盘符与 .. 穿越。
    /// 失败区分 Locked（文件被占用，重试可能成功）、Denied（没有写入权限，重试没用）与 Invalid（包不对）。
    /// stagingRoot 是临时解压目录的父目录，不要传 …\Plugins（ClassIsland 会把临时目录当成第二个插件）。
    /// </summary>
    public static PluginPackageResult ExtractTo(string zipPath, string targetDir, string? stagingRoot = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return PluginPackageResult.Bad("插件包不存在：" + zipPath);
        if (string.IsNullOrWhiteSpace(targetDir))
            return PluginPackageResult.Bad("没有给出插件目录");

        string fullTarget;
        try
        {
            fullTarget = Path.GetFullPath(targetDir);
        }
        catch (Exception ex)
        {
            return PluginPackageResult.Bad("插件目录不合法：" + ex.Message);
        }

        string? staging = null;
        // 覆盖前先备份旧文件；任何一步失败都整体回滚，别留下「新 dll + 旧清单」的半套插件。
        var backups = new List<(string Dest, string Backup, bool Existed)>();
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);

            var files = new List<(ZipArchiveEntry Entry, string Relative)>();
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal)) continue;

                if (!TryNormalizeEntry(entry.FullName, out var relative))
                    return PluginPackageResult.Bad("插件包里有不安全的路径：" + entry.FullName);
                if (IsJunk(relative)) continue;

                files.Add((entry, relative));
            }

            if (files.Count == 0) return PluginPackageResult.Bad("插件包是空的");

            var prefix = DetectPrefix(files);
            var hasManifest = false;
            var hasAssembly = false;
            foreach (var (_, relative) in files)
            {
                var path = StripPrefix(relative, prefix);
                if (path.Length == 0) continue;
                var name = Path.GetFileName(path);
                if (name.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase)) hasManifest = true;
                if (name.Equals(AssemblyFile, StringComparison.OrdinalIgnoreCase)) hasAssembly = true;
            }

            if (!hasManifest) return PluginPackageResult.Bad("插件包里没有 " + ManifestFile);
            if (!hasAssembly) return PluginPackageResult.Bad("插件包里没有 " + AssemblyFile);

            var parent = Path.GetDirectoryName(fullTarget);
            if (string.IsNullOrEmpty(parent)) return PluginPackageResult.Bad("插件目录不合法：" + targetDir);

            Directory.CreateDirectory(parent);
            // 临时目录绝不能建在 …\Plugins 里面：ClassIsland 会把 .ckit-plugin-* 当成第二个插件加载。
            var stageRoot = string.IsNullOrWhiteSpace(stagingRoot)
                ? Path.Combine(Path.GetTempPath(), "ClassroomKit-plugin-stage")
                : stagingRoot;
            CleanupStaleStaging(stageRoot);
            staging = Path.Combine(stageRoot, ".ckit-plugin-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(staging);

            var staged = new List<(string Source, string Relative)>();
            foreach (var (entry, relative) in files)
            {
                var path = StripPrefix(relative, prefix);
                if (path.Length == 0) continue;

                var dest = Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar));
                var destFull = Path.GetFullPath(dest);
                if (!IsUnder(staging, destFull))
                    return PluginPackageResult.Bad("插件包里有越界路径：" + entry.FullName);

                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                entry.ExtractToFile(destFull, overwrite: true);
                staged.Add((destFull, path));
            }

            Directory.CreateDirectory(fullTarget);
            var copied = 0;
            // manifest.yml 最后写：万一 dll 被占用写失败，目录里仍是旧版本号，下次还会继续重试，
            // 不会变成「新清单 + 旧 dll」让更新器以为已经装好、从此不再修补。
            foreach (var (source, relative) in staged.OrderBy(x => IsManifest(x.Relative) ? 1 : 0))
            {
                // data/ 与 Config/ 是插件的用户数据，升级时一律不动
                if (IsUserData(relative)) continue;

                var dest = Path.Combine(fullTarget, relative.Replace('/', Path.DirectorySeparatorChar));
                var destFull = Path.GetFullPath(dest);
                if (!IsUnder(fullTarget, destFull))
                    return PluginPackageResult.Bad("插件包里有越界路径：" + relative);

                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                var existed = File.Exists(destFull);
                var backup = "";
                if (existed)
                {
                    backup = Path.Combine(staging, ".backup", relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destFull, backup, overwrite: true);
                }
                backups.Add((destFull, backup, existed));
                File.Copy(source, destFull, overwrite: true);
                copied++;
            }

            return PluginPackageResult.Success($"已更新 {copied} 个插件文件");
        }
        catch (InvalidDataException ex)
        {
            Rollback(backups);
            return PluginPackageResult.Bad("插件包已损坏（不是有效的 zip）：" + ex.Message);
        }
        catch (NotSupportedException ex)
        {
            Rollback(backups);
            return PluginPackageResult.Bad("插件包格式不支持：" + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Rollback(backups);
            return PluginPackageResult.NoAccess("没有权限写入插件目录（请用管理员运行，或检查目录权限）：" + ex.Message);
        }
        catch (IOException ex)
        {
            Rollback(backups);
            return PluginPackageResult.Busy("插件文件被占用（ClassIsland 可能正在运行），已恢复原插件：" + ex.Message);
        }
        catch (Exception ex)
        {
            Rollback(backups);
            return PluginPackageResult.Bad("解压插件包失败，已恢复原插件：" + ex.Message);
        }
        finally
        {
            if (staging is not null) TryDeleteDirectory(staging);
        }
    }

    /// <summary>把已经覆盖的文件恢复原样：旧文件拷回，新文件删除。失败尽力而为。</summary>
    private static void Rollback(List<(string Dest, string Backup, bool Existed)> backups)
    {
        for (var i = backups.Count - 1; i >= 0; i--)
        {
            var (dest, backup, existed) = backups[i];
            try
            {
                if (existed) File.Copy(backup, dest, overwrite: true);
                else if (File.Exists(dest)) File.Delete(dest);
            }
            catch
            {
                // 回滚本身失败时保留现场，下一次安装会继续重试
            }
        }
    }

    /// <summary>简单逐行解析 manifest.yml：只认顶层 key: value，去掉引号与行尾注释。</summary>
    private static PluginManifest? ParseManifest(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        string? id = null;
        string? version = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            if (line.Length == 0) continue;
            if (char.IsWhiteSpace(line[0])) continue;   // 只看顶层字段
            if (line[0] == '#') continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            if (key is not ("id" or "version")) continue;

            var value = ReadValue(line[(colon + 1)..]);
            if (value.Length == 0) continue;
            if (key == "id") id = value;
            else version = value;
        }

        return id is null || version is null ? null : new PluginManifest(id, version);
    }

    private static string ReadValue(string raw)
    {
        var text = raw.Trim();
        if (text.Length == 0) return "";

        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var end = text.IndexOf(quote, 1);
            return end > 1 ? text[1..end].Trim() : text.Trim(quote).Trim();
        }

        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment > 0) text = text[..comment].Trim();
        return text;
    }

    /// <summary>条目名归一化：统一 /，拒绝绝对路径、盘符、.. 与 Windows 非法字符。</summary>
    private static bool TryNormalizeEntry(string? raw, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var name = raw.Replace('\\', '/');
        if (name.StartsWith('/')) return false;
        if (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':') return false;

        var parts = new List<string>();
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") return false;
            if (segment.IndexOfAny(InvalidNameChars) >= 0) return false;
            if (segment.Any(char.IsControl)) return false;
            parts.Add(segment);
        }

        if (parts.Count == 0) return false;
        relative = string.Join('/', parts);
        return true;
    }

    private static bool IsJunk(string relative)
    {
        foreach (var segment in relative.Split('/'))
        {
            if (segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)) return true;
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsUserData(string relative)
    {
        var first = relative.Split('/')[0];
        return first.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("Config", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>确定要剥掉的公共前缀：根目录有 manifest.yml 就不剥，否则取 manifest 所在那层目录。</summary>
    private static string DetectPrefix(IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> files)
    {
        if (files.Any(f => f.Relative.Equals(ManifestFile, StringComparison.OrdinalIgnoreCase))) return "";

        var manifestPath = files
            .Select(f => f.Relative)
            .Where(p => Path.GetFileName(p).Equals(ManifestFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Count(c => c == '/'))
            .FirstOrDefault();
        if (manifestPath is null) return "";

        var slash = manifestPath.LastIndexOf('/');
        return slash <= 0 ? "" : manifestPath[..(slash + 1)];
    }

    private static string StripPrefix(string relative, string prefix) =>
        prefix.Length > 0 && relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? relative[prefix.Length..]
            : relative;

    private static bool IsUnder(string root, string path)
    {
        var normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        return path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这个条目是不是清单文件（写盘时排到最后）。</summary>
    private static bool IsManifest(string relative) =>
        Path.GetFileName(relative).Equals(ManifestFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>清掉以前遗留的临时解压目录（崩溃或被占用时可能留下来）。</summary>
    private static void CleanupStaleStaging(string stageRoot)
    {
        try
        {
            if (!Directory.Exists(stageRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(stageRoot, ".ckit-plugin-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromHours(1)) continue;
                    Directory.Delete(dir, recursive: true);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>待安装的插件包是否比本机已装的新（不比新的包直接丢掉，避免把插件降级）。</summary>
    public static bool IsNewerThanInstalled(PluginManifest pending, string? installedVersion) =>
        string.IsNullOrWhiteSpace(installedVersion) || UpdateVersion.IsNewer(pending.Version, installedVersion);

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 临时目录删不掉不影响安装结果，下次启动会再清
        }
    }
}
