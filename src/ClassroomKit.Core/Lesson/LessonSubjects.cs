namespace ClassroomKit.Core.Lesson;

/// <summary>
/// 「生效科目」白名单（lesson.subjects）的匹配规则。
/// 课表里的科目名各校写法不一样（物理 / 物理实验 / 物理/实验），配置一次「物理」就都算命中，
/// 省得老师为了多一个字反复改配置。规则：
/// 1. 空列表（或者只有空白项）表示不限制，所有科目都生效 —— 旧配置没有这个字段时就是这种情况；
/// 2. 比较前两边都去掉空白（含全角空格）、忽略大小写；
/// 3. 相等算命中；一方包含另一方也算命中（「物理」能匹配「物理实验」「物理/实验」）；
/// 4. 课表没给科目名（null / 空白）时不算命中：宁可少弹一次，也不要在科目不确定时自动保存。
/// </summary>
public static class LessonSubjects
{
    /// <summary>整理用户填的科目名：去空白、去空项、忽略大小写去重（保留第一次出现的写法）。</summary>
    public static List<string> Normalize(IEnumerable<string>? subjects)
    {
        var list = new List<string>();
        if (subjects is null) return list;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in subjects)
        {
            if (string.IsNullOrWhiteSpace(subject)) continue;
            var trimmed = subject.Trim();
            if (!seen.Add(Compact(trimmed))) continue;
            list.Add(trimmed);
        }

        return list;
    }

    /// <summary>空列表 = 所有科目都生效。</summary>
    public static bool IsUnrestricted(IEnumerable<string>? subjects) => Normalize(subjects).Count == 0;

    /// <summary>课表给的科目名是否落在白名单里。</summary>
    public static bool Matches(string? subject, IEnumerable<string>? subjects)
    {
        var rules = Normalize(subjects);
        if (rules.Count == 0) return true;

        var name = Compact(subject);
        if (name.Length == 0) return false;

        foreach (var rule in rules)
        {
            var key = Compact(rule);
            if (key.Length == 0) continue;
            if (name == key || name.Contains(key, StringComparison.Ordinal) || key.Contains(name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>去空白并转小写，用作比较键。</summary>
    public static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000') continue;
            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
