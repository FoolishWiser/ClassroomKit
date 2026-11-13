using System.Globalization;

namespace ClassroomKit.Core.Update;

/// <summary>
/// 自动安装的时间窗，写法 "HH:mm-HH:mm"，可跨午夜（23:00-06:00）。
/// 空字符串表示不限；写错格式按「不限」处理 —— 配置写歪不该让机器永远不升级。
/// </summary>
public static class InstallWindow
{
    public const string Default = "02:00-05:00";

    public static bool TryParse(string? text, out TimeOnly start, out TimeOnly end)
    {
        start = default;
        end = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim().Replace('—', '-').Replace('–', '-').Replace("~", "-").Replace("至", "-");
        var dash = s.IndexOf('-');
        if (dash <= 0 || dash >= s.Length - 1) return false;

        return TryParseTime(s[..dash], out start) && TryParseTime(s[(dash + 1)..], out end);
    }

    /// <summary>现在是否落在时间窗里。空/格式错 = 不限（true）。</summary>
    public static bool IsOpen(string? text, TimeOnly now)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out var start, out var end)) return true;
        if (start == end) return true;
        return start < end
            ? now >= start && now < end
            : now >= start || now < end;
    }

    /// <summary>规范化成 "HH:mm-HH:mm"；空字符串保留为「不限时间」，写错格式退回默认窗口。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return TryParse(text, out var start, out var end)
            ? $"{start.Hour:00}:{start.Minute:00}-{end.Hour:00}:{end.Minute:00}"
            : Default;
    }

    public static string Describe(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "不限时间";
        if (!TryParse(text, out var start, out var end)) return "不限时间";
        var cross = start > end ? "（跨午夜）" : "";
        return $"每天 {start.Hour:00}:{start.Minute:00}–{end.Hour:00}:{end.Minute:00}{cross}";
    }

    private static bool TryParseTime(string text, out TimeOnly value)
    {
        value = default;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute)) return false;
        var second = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out second)) return false;
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59) return false;
        value = new TimeOnly(hour, minute, second);
        return true;
    }
}
