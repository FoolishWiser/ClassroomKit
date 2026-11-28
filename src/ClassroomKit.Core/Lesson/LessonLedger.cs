using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Lesson;

/// <summary>一次「这次课间要不要自动保存」的判定结果。Reason 便于日志与单测定位。</summary>
public readonly record struct ReminderDecision(
    bool Show,
    string Reason,
    string Key = "",
    DateTimeOffset? RemindAt = null,
    string? Subject = null,
    int SecondsToStart = 0)
{
    public static ReminderDecision No(string reason, string key = "") => new(false, reason, key);
}

/// <summary>可落盘的信箱状态：Agent 重启后仍记得「这节已经处理过」。</summary>
public sealed record LessonLedgerState(
    LessonSnapshot? Snapshot,
    string LastRemindedKey);

/// <summary>
/// 课表快照 + 自动保存去重状态。纯逻辑，不碰 IO/UI，方便单测。
/// 规则：同一节课（按开始时刻作 key）只触发一次自动保存。
/// </summary>
public sealed class LessonLedger
{
    private readonly object _gate = new();
    private LessonSnapshot? _snapshot;
    private string _lastRemindedKey = "";

    public static LessonLedger FromState(LessonLedgerState? state)
    {
        var ledger = new LessonLedger();
        if (state is null) return ledger;
        ledger._snapshot = state.Snapshot;
        ledger._lastRemindedKey = state.LastRemindedKey ?? "";
        return ledger;
    }

    public LessonLedgerState Export()
    {
        lock (_gate)
        {
            return new LessonLedgerState(_snapshot, _lastRemindedKey);
        }
    }

    public void Accept(LessonSnapshot snapshot)
    {
        lock (_gate) { _snapshot = snapshot; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _snapshot = null;
            _lastRemindedKey = "";
        }
    }

    public LessonSnapshot? Peek()
    {
        lock (_gate) { return _snapshot; }
    }

    public string LastRemindedKey
    {
        get { lock (_gate) { return _lastRemindedKey; } }
    }

    /// <summary>快照已存在多少秒；没有快照返回 null。</summary>
    public double? AgeSeconds(DateTimeOffset now)
    {
        var snapshot = Peek();
        if (snapshot is null || snapshot.At == default) return null;
        return Math.Max(0, (now - snapshot.At).TotalSeconds);
    }

    public bool IsStale(DateTimeOffset now, int staleMinutes)
    {
        var age = AgeSeconds(now);
        if (age is null) return true;
        var limit = Math.Clamp(staleMinutes, 1, 1440) * 60.0;
        return age.Value > limit;
    }

    /// <summary>
    /// 去重 key：课间用「本课间 + 下一节开始时刻」，放学后用「日期 + 刚上完的科目」。
    /// 这样连堂课间、同一个课间反复推送都不会重复弹。
    /// </summary>
    public static string KeyOf(LessonSnapshot snapshot)
    {
        if (snapshot.State == LessonStates.Breaking)
        {
            return snapshot.NextStartAt is { } start
                ? "break|" + start.ToString("yyyy-MM-dd HH:mm")
                : "break|" + snapshot.At.ToString("yyyy-MM-dd") + "|" + snapshot.LessonIndex;
        }

        if (snapshot.State == LessonStates.AfterSchool)
        {
            return "after|" + snapshot.At.ToString("yyyy-MM-dd") + "|" + (snapshot.PreviousSubject ?? "");
        }

        if (snapshot.NextStartAt is { } next) return next.ToString("yyyy-MM-dd HH:mm");
        return "";
    }

    /// <summary>
    /// 0.8.0 起改为「下课后保存」：课间判断刚上完的那一节，放学后保存最后一节。
    /// 1.0.4 起连堂课间也默认保存（下一节同科目要续用上一节板书）；
    /// 只有 lesson.saveConnectedClasses=false 时才保持旧行为。课间名命中 SkipBreakNames 仍跳过。
    /// </summary>
    public ReminderDecision Evaluate(DateTimeOffset now, LessonConfig config)
    {
        if (config is null || !config.Enabled) return ReminderDecision.No("disabled");

        var snapshot = Peek();
        if (snapshot is null) return ReminderDecision.No("no-data");
        if (!snapshot.PlanLoaded) return ReminderDecision.No("no-plan");
        if (IsStale(now, config.StaleMinutes)) return ReminderDecision.No("stale");

        var state = snapshot.State;
        if (state != LessonStates.Breaking && state != LessonStates.AfterSchool)
            return ReminderDecision.No(state == LessonStates.Class || state == LessonStates.Prepare ? "in-class" : "not-breaking");

        var subject = snapshot.PreviousSubject;
        if (string.IsNullOrWhiteSpace(subject)) return ReminderDecision.No("no-previous");

        // 科目定向：课间看刚上完的那一节（板书是它的），空列表不限制
        if (!LessonSubjects.Matches(subject, config.Subjects))
            return ReminderDecision.No("subject-skipped");

        if (state == LessonStates.Breaking)
        {
            // 连堂：下一节还是同一门课。1.0.4 默认保存，给下一节续用；只有用户明确关掉才跳过。
            // 连堂（下一节同科目）不保存：等这门课最后一段下课再存。
            if (snapshot.HasNext && SameSubject(snapshot.NextSubject, subject))
                return ReminderDecision.No("continuous");

            if (MatchesBreak(snapshot.BreakName, config.SkipBreakNames))
                return ReminderDecision.No("break-skipped");
        }

        var key = KeyOf(snapshot);
        var remindAt = state == LessonStates.Breaking ? snapshot.NextStartAt : now;
        var seconds = snapshot.NextStartAt is { } start ? (int)Math.Round((start - now).TotalSeconds) : 0;

        if (_lastRemindedKey.Length > 0 && _lastRemindedKey == key)
            return new ReminderDecision(false, "already", key, remindAt, subject, seconds);

        return new ReminderDecision(true, "ok", key, remindAt, subject, seconds);
    }

    private static bool SameSubject(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesBreak(string? name, IReadOnlyList<string>? skip)
    {
        if (string.IsNullOrWhiteSpace(name) || skip is null || skip.Count == 0) return false;
        var trimmed = name.Trim();
        foreach (var item in skip)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            if (string.Equals(trimmed, item.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>已经弹过（或用户明确忽略）：这节课不再打扰。</summary>
    public void MarkReminded(string key)
    {
        lock (_gate)
        {
            _lastRemindedKey = key ?? "";
        }
    }

    /// <summary>
    /// 给板书文件用的科目名：正在上课就用当前科目，课间就用刚结束的那一节。
    /// 数据太旧（超过 maxAgeMinutes）一律不给，宁可让文件名退回默认格式，也别张冠李戴。
    /// </summary>
    public string? NameHint(DateTimeOffset now, int maxAgeMinutes = 180)
    {
        var snapshot = Peek();
        if (snapshot is null || snapshot.At == default) return null;
        if ((now - snapshot.At).TotalMinutes > Math.Clamp(maxAgeMinutes, 1, 1440)) return null;

        if (LessonStates.IsInClass(snapshot.State) && !string.IsNullOrWhiteSpace(snapshot.CurrentSubject))
            return snapshot.CurrentSubject;

        return string.IsNullOrWhiteSpace(snapshot.PreviousSubject) ? null : snapshot.PreviousSubject;
    }
}
