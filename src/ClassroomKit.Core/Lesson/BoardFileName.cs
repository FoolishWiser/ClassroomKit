using System.Text;

namespace ClassroomKit.Core.Lesson;

/// <summary>改名结果：成功改名的文件、失败数、给用户看的一句话。</summary>
public sealed record BoardRenameResult(IReadOnlyList<string> Files, int Renamed, int Failed, string Message)
{
    public static BoardRenameResult Unchanged(IReadOnlyList<string> files, string message) =>
        new(files, 0, 0, message);
}

/// <summary>归档结果：最终文件路径、移动成功数、失败数，以及给日志看的一句话。</summary>
public sealed record BoardMoveResult(IReadOnlyList<string> Files, int Moved, int Failed, string Message)
{
    public static BoardMoveResult Unchanged(IReadOnlyList<string> files, string message) =>
        new(files, 0, 0, message);
}

/// <summary>
/// 按课表科目给板书文件改名。模板占位符：{科目} {日期} {时间} {节次} {周几}。
/// 默认模板渲染出「数学_2026-11-14_10-05」这种名字：科目在前好找，时间在后排得开。
/// </summary>
public static class BoardFileName
{
    public const string DefaultTemplate = "{科目}_{节次}_{日期}_{时间}";

    private static readonly char[] Invalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Windows 文件名里不能出现的字符一律换成下划线，并截断到安全长度。</summary>
    public static string Sanitize(string? text, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (char.IsControl(ch)) continue;
            builder.Append(Array.IndexOf(Invalid, ch) >= 0 ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim();
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);

        cleaned = cleaned.Trim(' ', '.', '_', '-');
        if (cleaned.Length <= maxLength) return cleaned;

        var cut = cleaned[..maxLength];
        // 不要把一个代理对/汉字组合从中间劈开
        if (cut.Length > 0 && char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.Trim(' ', '.', '_', '-');
    }

    /// <summary>
    /// 渲染模板；模板为空用默认模板，渲染后为空退回「板书_日期_时间」。
    /// includeLessonInName=false 时 {节次} 渲染为空，不再自作主张补「_第N节」。
    /// </summary>
    public static string Render(string? template, string? subject, DateTimeOffset at, int lessonIndex = 0,
        bool includeLessonInName = true)
    {
        var tpl = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        // 快照里的时刻本身就带本地偏移（插件用 DateTimeOffset.Now 构造），
        // 这里再 ToLocalTime 会按服务器时区再折一次，测试机是 UTC 就会写成 02:05。
        var local = at;
        var date = local.ToString("yyyy-MM-dd");
        var time = local.ToString("HH-mm");
        var period = includeLessonInName && lessonIndex > 0 ? $"第{lessonIndex}节" : "";
        var weekday = "周" + "日一二三四五六"[(int)local.DayOfWeek];
        // 没有科目名时用「板书」占位，否则默认模板会渲染成 _2026-11-14_10-05 这种不好认的名字
        var name = Sanitize(subject) is { Length: > 0 } s ? s : "板书";

        var rendered = tpl
            .Replace("{科目}", name, StringComparison.Ordinal)
            .Replace("{日期}", date, StringComparison.Ordinal)
            .Replace("{时间}", time, StringComparison.Ordinal)
            .Replace("{节次}", period, StringComparison.Ordinal)
            .Replace("{周几}", weekday, StringComparison.Ordinal);

        rendered = Sanitize(rendered);
        if (rendered.Length == 0) rendered = $"板书_{date}_{time}";
        return rendered;
    }

    /// <summary>
    /// 把一批文件改成 baseName + 原扩展名；同名已存在时补 _2/_3。
    /// ClassInX 可能还占着文件，所以每个文件重试 3 次；仍失败就保留原名（不抛异常）。
    /// </summary>
    public static BoardRenameResult ApplyTo(IEnumerable<string> files, string? baseName)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
        var safe = Sanitize(baseName);
        if (list.Count == 0) return BoardRenameResult.Unchanged(list, "没有需要改名的文件");
        if (safe.Length == 0) return BoardRenameResult.Unchanged(list, "文件名保持不变（没有可用的科目名）");

        var result = new List<string>(list.Count);
        var renamed = 0;
        var failed = 0;
        var movedPairs = new List<(string Source, string Target)>();

        foreach (var file in list)
        {
            var dir = Path.GetDirectoryName(file);
            var ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(dir)) { result.Add(file); continue; }

            var target = UniqueTarget(dir, safe, ext);
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                result.Add(file);
                continue;
            }

            var moved = false;
            for (var attempt = 0; attempt < 3 && !moved; attempt++)
            {
                try
                {
                    File.Move(file, target);
                    moved = true;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }

            if (moved)
            {
                renamed++;
                movedPairs.Add((file, target));
                result.Add(target);
            }
            else
            {
                failed++;
                result.Add(file);
            }
        }

        if (failed > 0)
        {
            // 一半新名、一半原名会让同一份板书分裂成两处，宁可全部退回原名。
            foreach (var pair in movedPairs.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(pair.Target) && !File.Exists(pair.Source))
                        File.Move(pair.Target, pair.Source);
                }
                catch { /* 回滚失败保留现状，至少不丢文件 */ }
            }

            return new BoardRenameResult(list, 0, failed,
                $"有 {failed} 个文件改名失败，已全部恢复原名（文件可能被 ClassInX 占用）");
        }

        var message = renamed == 0 ? "文件名保持不变（文件被占用或无需改名）" : $"已改名为 {safe}";
        return new BoardRenameResult(result, renamed, 0, message);
    }

    /// <summary>科目文件夹名：清理非法字符，空科目归到「未分类」。</summary>
    public static string SubjectFolderName(string? subject)
    {
        var safe = Sanitize(subject, 40);
        return safe.Length == 0 ? "未分类" : safe;
    }

    /// <summary>
    /// 把一批文件移进 &lt;rootFolder&gt;\&lt;科目&gt;\。失败时已移动的文件会尽量回滚，绝不删除原件。
    /// </summary>
    public static BoardMoveResult MoveToSubjectFolder(IEnumerable<string> files, string rootFolder, string? subject,
        bool enabled = true)
    {
        var list = files?.Where(f => !string.IsNullOrWhiteSpace(f)).ToList() ?? new List<string>();
        if (list.Count == 0) return BoardMoveResult.Unchanged(list, "没有需要归档的文件");
        if (!enabled) return BoardMoveResult.Unchanged(list, "科目文件夹已关闭");
        if (string.IsNullOrWhiteSpace(rootFolder)) return BoardMoveResult.Unchanged(list, "没有板书目录");

        var folder = SubjectFolderName(subject);
        var targetDir = Path.Combine(rootFolder, folder);
        try { Directory.CreateDirectory(targetDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BoardMoveResult.Unchanged(list, $"无法创建科目文件夹「{folder}」：{ex.Message}");
        }

        var result = new List<string>(list.Count);
        var movedPairs = new List<(string Source, string Target)>();
        var failed = 0;
        string? firstError = null;

        foreach (var file in list)
        {
            try
            {
                var ext = Path.GetExtension(file);
                var target = UniqueTarget(targetDir, Path.GetFileNameWithoutExtension(file), ext);
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(file);
                    continue;
                }

                var moved = false;
                for (var attempt = 0; attempt < 5 && !moved; attempt++)
                {
                    try
                    {
                        File.Move(file, target);
                        moved = true;
                    }
                    catch (IOException) { Thread.Sleep(250); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(250); }
                }

                if (moved)
                {
                    movedPairs.Add((file, target));
                    result.Add(target);
                }
                else
                {
                    failed++;
                    firstError ??= "文件被占用";
                    result.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failed++;
                firstError ??= ex.Message;
                result.Add(file);
            }
        }

        if (failed > 0)
        {
            // 有一个失败就整体回滚，避免同一份板书一半在科目文件夹、一半在根目录。
            foreach (var pair in movedPairs.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(pair.Target) && !File.Exists(pair.Source))
                        File.Move(pair.Target, pair.Source);
                }
                catch { /* 回滚失败就保留现状，至少不丢文件 */ }
            }

            return new BoardMoveResult(list, 0, failed,
                $"有 {failed} 个文件无法移入「{folder}」，已保留在原目录" + (firstError is null ? "" : $"（{firstError}）"));
        }

        return new BoardMoveResult(result, movedPairs.Count, 0,
            movedPairs.Count == 0 ? "文件已在科目文件夹" : $"已归档到「{folder}」");
    }

    private static string UniqueTarget(string dir, string baseName, string ext)
    {
        var candidate = Path.Combine(dir, baseName + ext);
        var index = 2;
        while (File.Exists(candidate) && index < 1000)
        {
            candidate = Path.Combine(dir, $"{baseName}_{index}{ext}");
            index++;
        }
        return candidate;
    }
}
