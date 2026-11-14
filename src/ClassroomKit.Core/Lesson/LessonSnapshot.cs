namespace ClassroomKit.Core.Lesson;

/// <summary>课表状态的粗粒度取值（插件把 ClassIsland 的 TimeState 映射过来）。</summary>
public static class LessonStates
{
    public const string None = "none";
    public const string Prepare = "prepare";
    public const string Class = "class";
    public const string Breaking = "breaking";
    public const string AfterSchool = "after-school";

    /// <summary>这一状态代表「正在上课」。</summary>
    public static bool IsInClass(string? state) => state == Class;

    public static string Describe(string? state) => state switch
    {
        Class => "上课",
        Prepare => "预备",
        Breaking => "课间",
        AfterSchool => "放学",
        _ => "未知"
    };
}

/// <summary>
/// ClassIsland 插件推给 Agent 的一份课表快照。
/// 只放 Agent 真正需要的字段：谁在上课、下一节是谁、还有多久、上节课是谁。
/// </summary>
public sealed record LessonSnapshot
{
    public string State { get; init; } = LessonStates.None;

    /// <summary>当前时间点科目名；课间/放学通常为空。</summary>
    public string? CurrentSubject { get; init; }

    /// <summary>下一节上课类时间点的科目名。</summary>
    public string? NextSubject { get; init; }

    /// <summary>刚结束的那一节课（用于给板书文件命名）。</summary>
    public string? PreviousSubject { get; init; }

    /// <summary>下一节课的开始时刻（本地时区）。</summary>
    public DateTimeOffset? NextStartAt { get; init; }

    /// <summary>距离下一节课还剩多少秒（插件算好一起发，避免 Agent 时钟口径不一致）。</summary>
    public int SecondsToNext { get; init; }

    /// <summary>当前时间点在课表里的序号（从 1 开始，0 = 未知）。</summary>
    public int LessonIndex { get; init; }

    /// <summary>ClassIsland 是否加载了课表。</summary>
    public bool PlanLoaded { get; init; }

    /// <summary>快照生成时刻（用于判断数据是否过期）。</summary>
    public DateTimeOffset At { get; init; }

    public bool HasNext => PlanLoaded && NextStartAt is not null && !string.IsNullOrWhiteSpace(NextSubject);

    public static LessonSnapshot Empty(DateTimeOffset at) => new() { At = at };

    public static LessonSnapshot FromClassIsland(
        string state,
        string? currentSubject,
        string? nextSubject,
        string? previousSubject,
        DateTimeOffset? nextStartAt,
        int secondsToNext,
        int lessonIndex,
        bool planLoaded,
        DateTimeOffset at) => new()
        {
            State = string.IsNullOrWhiteSpace(state) ? LessonStates.None : state,
            CurrentSubject = Normalize(currentSubject),
            NextSubject = Normalize(nextSubject),
            PreviousSubject = Normalize(previousSubject),
            NextStartAt = nextStartAt,
            SecondsToNext = Math.Max(0, secondsToNext),
            LessonIndex = Math.Max(0, lessonIndex),
            PlanLoaded = planLoaded,
            At = at
        };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
