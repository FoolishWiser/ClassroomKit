namespace ClassroomKit.Core.Client;

/// <summary>
/// GET /api/lesson 的响应：课表快照 + 课前提醒的判定结果。
/// 插件用它画状态、排错，也能看出「为什么现在没提醒」。
/// </summary>
public sealed record LessonView
{
    /// <summary>守护手上有没有可用的课表数据。</summary>
    public bool Available { get; init; }

    /// <summary>none / prepare / class / breaking / after-school。</summary>
    public string? State { get; init; }

    /// <summary>状态的中文说法（上课 / 课间 …）。</summary>
    public string? StateText { get; init; }

    public string? CurrentSubject { get; init; }

    public string? NextSubject { get; init; }

    public string? PreviousSubject { get; init; }

    public DateTimeOffset? NextStartAt { get; init; }

    public int SecondsToNext { get; init; }

    public int LessonIndex { get; init; }

    public bool PlanLoaded { get; init; }

    /// <summary>快照距今多少秒；超过 180 秒视为过期。</summary>
    public int AgeSec { get; init; }

    /// <summary>现在保存板书会用到的科目名（上一节课优先）。</summary>
    public string? NameHint { get; init; }

    public LessonReminderView Reminder { get; init; } = new();
}

/// <summary>课前提醒的配置与当前判定。</summary>
public sealed record LessonReminderView
{
    public bool Enabled { get; init; }

    public int MinutesBefore { get; init; }

    public int DialogSeconds { get; init; }

    /// <summary>生效科目白名单；空 = 所有科目都提醒。</summary>
    public List<string> Subjects { get; init; } = new();

    /// <summary>save / save-close / dismiss-ignore / wait。</summary>
    public string TimeoutAction { get; init; } = "save";

    /// <summary>已经提醒过的那节课的键（yyyy-MM-dd HH:mm），同一节课不会提醒两次。</summary>
    public string? LastRemindedKey { get; init; }

    public DateTimeOffset? SnoozedUntil { get; init; }

    /// <summary>现在该不该弹：ok / too-early / already / snoozed / in-class …</summary>
    public string? Reason { get; init; }

    /// <summary>该弹的时刻。</summary>
    public DateTimeOffset? RemindAt { get; init; }
}
