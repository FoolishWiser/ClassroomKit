using System.Globalization;

namespace ClassroomKit.Core.Board;

/// <summary>
/// 「今天不再提醒」的持久化值：配置里存 yyyy-MM-dd，空字符串 = 没暂停。
/// 单独抽出来是为了让窗口、守护和面板说同一句话。
/// </summary>
public static class ConflictSnooze
{
    public const string Format = "yyyy-MM-dd";

    public static DateOnly? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>今天是否还在「不再提醒」里。</summary>
    public static bool IsActive(string? text, DateTimeOffset now)
    {
        var until = Parse(text);
        return until is not null && DateOnly.FromDateTime(now.LocalDateTime) <= until.Value;
    }

    public static string Today(DateTimeOffset now) => DateOnly.FromDateTime(now.LocalDateTime).ToString(Format, CultureInfo.InvariantCulture);

    public static string Describe(string? text, DateTimeOffset now)
    {
        var until = Parse(text);
        if (until is null) return "未暂停";
        return IsActive(text, now) ? $"今天不再提醒（到 {until.Value.ToString(Format, CultureInfo.InvariantCulture)} 为止）" : "未暂停";
    }
}
