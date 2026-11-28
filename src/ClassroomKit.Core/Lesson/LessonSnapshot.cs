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

    /// <summary>当前所在的课间名称（ClassIsland 时间点上的自定义课间名；非课间为空）。</summary>
    public string? BreakName { get; init; }

    /// <summary>
    /// 整份课表的科目清单（ClassIsland 档案里的科目字典，去空白后按名字去重）。
    /// 空列表表示「没提供 / 不限制」，调用方按「全部科目」处理。
    /// </summary>
    public IReadOnlyList<string> Subjects
    {
        get => _subjects;
        // 反射/反序列化赋值也走这里：保证任何构造路径下都不是 null，且已经归一
        init => _subjects = LessonSubjects.Normalize(value);
    }

    private IReadOnlyList<string> _subjects = Array.Empty<string>();

    /// <summary>下一节课的开始时刻（本地时区）。</summary>
    public DateTimeOffset? NextStartAt { get; init; }

    /// <summary>距离下一节课还剩多少秒（插件算好一起发，避免 Agent 时钟口径不一致）。</summary>
    public int SecondsToNext { get; init; }

    /// <summary>当前时间点所在/刚结束的那一节上课序号（从 1 开始，0 = 未知）。</summary>
    public int LessonIndex { get; init; }

    /// <summary>上一节上课序号（课间时等于 LessonIndex；第一节课前为 0）。</summary>
    public int PreviousLessonIndex { get; init; }

    /// <summary>下一节上课序号（课中为 LessonIndex + 1；课间为 LessonIndex + 1）。</summary>
    public int NextLessonIndex { get; init; }

    /// <summary>当前时间点（上课/课间/分割线）的开始时刻。</summary>
    public DateTimeOffset? CurrentStartAt { get; init; }

    /// <summary>当前时间点的结束时刻；课间时也是下一节的上课时刻。</summary>
    public DateTimeOffset? CurrentEndAt { get; init; }

    /// <summary>下一节上课时间点的结束时刻（用于“到下一节下课”的续用窗口）。</summary>
    public DateTimeOffset? NextEndAt { get; init; }

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
        DateTimeOffset at,
        IEnumerable<string>? subjects = null,
        string? breakName = null,
        int previousLessonIndex = 0,
        int nextLessonIndex = 0,
        DateTimeOffset? currentStartAt = null,
        DateTimeOffset? currentEndAt = null,
        DateTimeOffset? nextEndAt = null) => new()
        {
            State = string.IsNullOrWhiteSpace(state) ? LessonStates.None : state,
            CurrentSubject = Normalize(currentSubject),
            NextSubject = Normalize(nextSubject),
            PreviousSubject = Normalize(previousSubject),
            BreakName = Normalize(breakName),
            NextStartAt = nextStartAt,
            SecondsToNext = Math.Max(0, secondsToNext),
            LessonIndex = Math.Max(0, lessonIndex),
            PreviousLessonIndex = Math.Max(0, previousLessonIndex),
            NextLessonIndex = Math.Max(0, nextLessonIndex),
            CurrentStartAt = currentStartAt,
            CurrentEndAt = currentEndAt,
            NextEndAt = nextEndAt,
            PlanLoaded = planLoaded,
            Subjects = LessonSubjects.Normalize(subjects),
            At = at
        };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
