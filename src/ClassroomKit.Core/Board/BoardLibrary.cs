namespace ClassroomKit.Core.Board;

/// <summary>板书目录里的一个文件；界面只负责显示，扫描与统计都在 Core。</summary>
public sealed record BoardFile(string Name, string Path, long Size, DateTimeOffset Modified)
{
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
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).ToArray();
                count = files.Length;
                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        total += info.Length;
                        recent.Add(new BoardFile(info.Name, info.FullName, info.Length, info.LastWriteTime));
                    }
                    catch { /* 单个文件被占用或刚被删，跳过 */ }
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
    }
}