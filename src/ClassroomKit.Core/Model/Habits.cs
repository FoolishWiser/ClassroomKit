namespace ClassroomKit.Core.Model;

/// <summary>老师上课习惯使用的板书 / 演示软件。</summary>
public enum HabitApp
{
    None = 0,
    ClassIn = 1,
    Seewo = 2,
    Wps = 3
}

/// <summary>某个科目的习惯设置（桌面「高级」页配置）。</summary>
public sealed class HabitRule
{
    public string Subject { get; set; } = "";

    /// <summary>classin | seewo | wps；空 = 未设置。</summary>
    public string App { get; set; } = "";

    /// <summary>true = 理解（冲突时照常弹窗）；false = 不理解（静默关闭另一款）。</summary>
    public bool Understand { get; set; } = true;
}

/// <summary>按科目记住老师习惯。默认关闭：不熟知老师习惯就别开。</summary>
public sealed class HabitsConfig
{
    public bool Enabled { get; set; }

    public List<HabitRule> Rules { get; set; } = new();

    /// <summary>修剪空白、按科目去重（后者覆盖前者）、丢弃没选软件的规则。</summary>
    public void Normalize()
    {
        Rules ??= new List<HabitRule>();
        var result = new List<HabitRule>();
        var index = new Dictionary<string, HabitRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in Rules)
        {
            if (rule is null) continue;
            var subject = (rule.Subject ?? "").Trim();
            var app = HabitApps.Parse(rule.App);
            if (subject.Length == 0 || app == HabitApp.None) continue;

            if (index.TryGetValue(subject, out var existing))
            {
                existing.App = HabitApps.ToValue(app);
                existing.Understand = rule.Understand;
                continue;
            }

            var normalized = new HabitRule
            {
                Subject = subject,
                App = HabitApps.ToValue(app),
                Understand = rule.Understand
            };
            index[subject] = normalized;
            result.Add(normalized);
        }

        Rules = result;
    }
}

/// <summary>习惯软件的字符串与枚举互转（配置里存小写英文，界面上显示中文）。</summary>
public static class HabitApps
{
    public static HabitApp Parse(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return HabitApp.None;
        return text switch
        {
            "classin" or "classin x" or "classinx" or "classin_x" => HabitApp.ClassIn,
            "seewo" or "easinote" or "希沃" or "希沃白板" => HabitApp.Seewo,
            "wps" or "wpp" or "wps office" => HabitApp.Wps,
            _ => HabitApp.None
        };
    }

    public static string ToValue(HabitApp app) => app switch
    {
        HabitApp.ClassIn => "classin",
        HabitApp.Seewo => "seewo",
        HabitApp.Wps => "wps",
        _ => ""
    };

    public static string Label(HabitApp app) => app switch
    {
        HabitApp.ClassIn => "ClassInX",
        HabitApp.Seewo => "希沃白板",
        HabitApp.Wps => "WPS",
        _ => "未设置"
    };
}
