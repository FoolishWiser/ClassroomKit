namespace ClassroomKit.Core.Board;

/// <summary>板书目录里的一个文件；界面只负责显示，扫描与统计都在 Core。</summary>
public sealed record BoardFile(string Name, string Path, long Size, DateTimeOffset Modified, string Subject = "")
{
    public string SubjectText => string.IsNullOrWhiteSpace(Subject) ? "未分类" : Subject;

    public string SizeText => Size < 1024 * 1024
        ? (Size / 1024.0).ToString("0.#") + " KB"
        : (Size / 1024.0 / 1024).ToString("0.#") + " MB";

    public string ModifiedText => Modified.ToString("MM-dd HH:mm");
}

/// <summary>板书中心的一屏摘要：文件数、占用、磁盘剩余和最近几个文件。</summary>
public sealed record BoardLibrarySummary(
    string Folder,
    int Count,
    long TotalBytes,
    long FreeBytes,
    IReadOnlyList<BoardFile> Recent);

/// <summary>
/// 只读扫描 ClassInX 的默认板书目录（我的文档\ClassIn X Files）。
/// 扫不到目录时返回空摘要，不抛异常：老师没保存过板书是正常状态。
/// </summary>
public static class BoardLibrary
{
    public static BoardLibrarySummary Scan(string folder, int recentCount = 5)
    {
        var recent = new List<BoardFile>();
        var count = 0;
        long total = 0;
        var free = 0L;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (!string.IsNullOrWhiteSpace(root)) free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { /* 网络盘或权限不足时留 0，界面显示「—」 */ }

        try
        {
            if (Directory.Exists(folder))
            {
                // 1.0.4：科目子目录只展开一层；更深层的目录不扫，避免把备份/临时目录算进来。
                AddFiles(folder, "", null);
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    try
                    {
                        var name = Path.GetFileName(dir);
                        if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                        AddFiles(dir, name, null);
                    }
                    catch { /* 单个目录不可读，跳过 */ }
                }
            }
        }
        catch { /* 目录不可读时按空目录处理 */ }

        return new BoardLibrarySummary(
            folder,
            count,
            total,
            free,
            recent.OrderByDescending(x => x.Modified).Take(Math.Max(0, recentCount)).ToArray());

        void AddFiles(string dir, string subject, object? _)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch { return; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || (info.Attributes & FileAttributes.Hidden) != 0) continue;
                    total += info.Length;
                    count++;
                    recent.Add(new BoardFile(info.Name, info.FullName, info.Length, info.LastWriteTime, subject));
                }
                catch { /* 单个文件被占用或刚被删，跳过 */ }
            }
        }
    }
}