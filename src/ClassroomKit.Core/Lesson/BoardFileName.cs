using System.Text;

namespace ClassroomKit.Core.Lesson;

/// <summary>改名结果：成功改名的文件、失败数、给用户看的一句话。</summary>
public sealed record BoardRenameResult(IReadOnlyList<string> Files, int Renamed, int Failed, string Message)
{
    public static BoardRenameResult Unchanged(IReadOnlyList<string> files, string message) =>
        new(files, 0, 0, message);
}

/// <summary>
/// 按课表科目给板书文件改名。模板占位符：{科目} {日期} {时间} {节次} {周几}。
/// 默认模板渲染出「数学_2026-11-14_10-05」这种名字：科目在前好找，时间在后排得开。
/// </summary>
public static class BoardFileName
{
    public const string DefaultTemplate = "{科目}_{日期}_{时间}";

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

    /// <summary>渲染模板；模板为空用默认模板，渲染后为空退回「板书_日期_时间」。</summary>
    public static string Render(string? template, string? subject, DateTimeOffset at, int lessonIndex = 0)
    {
        var tpl = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        // 快照里的时刻本身就带本地偏移（插件用 DateTimeOffset.Now 构造），
        // 这里再 ToLocalTime 会按服务器时区再折一次，测试机是 UTC 就会写成 02:05。
        var local = at;
        var date = local.ToString("yyyy-MM-dd");
        var time = local.ToString("HH-mm");
        var period = lessonIndex > 0 ? $"第{lessonIndex}节" : "";
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
                result.Add(target);
            }
            else
            {
                failed++;
                result.Add(file);
            }
        }

        var message = renamed == 0
            ? "文件名保持不变（文件被占用或无需改名）"
            : failed == 0
                ? $"已改名为 {safe}"
                : $"已改名 {renamed} 个，{failed} 个仍被占用";
        return new BoardRenameResult(result, renamed, failed, message);
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
