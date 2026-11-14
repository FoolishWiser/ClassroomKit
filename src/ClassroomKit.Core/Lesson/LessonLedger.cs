using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Lesson;

/// <summary>一次「要不要弹课前提醒」的判定结果。Reason 便于日志与单测定位。</summary>
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

/// <summary>可落盘的信箱状态：Agent 重启后仍记得「这节已经提醒过」。</summary>
public sealed record LessonLedgerState(
    LessonSnapshot? Snapshot,
    string LastRemindedKey,
    DateTimeOffset? SnoozedUntil);

/// <summary>
/// 课表快照 + 提醒去重状态。纯逻辑，不碰 IO/UI，方便单测。
/// 规则：同一节课（按开始时刻作 key）只提醒一次；点「稍后提醒」用时间窗挡住，过窗后再问。
/// </summary>
public sealed class LessonLedger
{
    private readonly object _gate = new();
    private LessonSnapshot? _snapshot;
    private string _lastRemindedKey = "";
    private DateTimeOffset? _snoozedUntil;

    public static LessonLedger FromState(LessonLedgerState? state)
    {
        var ledger = new LessonLedger();
        if (state is null) return ledger;
        ledger._snapshot = state.Snapshot;
        ledger._lastRemindedKey = state.LastRemindedKey ?? "";
        ledger._snoozedUntil = state.SnoozedUntil;
        return ledger;
    }

    public LessonLedgerState Export()
    {
        lock (_gate)
        {
            return new LessonLedgerState(_snapshot, _lastRemindedKey, _snoozedUntil);
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
            _snoozedUntil = null;
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

    public DateTimeOffset? SnoozedUntil
    {
        get { lock (_gate) { return _snoozedUntil; } }
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

    /// <summary>同一节课的去重 key：下一节课的开始时刻（本地时间，精确到分钟）。</summary>
    public static string KeyOf(LessonSnapshot snapshot) =>
        snapshot.NextStartAt is { } start
            ? start.ToString("yyyy-MM-dd HH:mm")
            : "";

    public ReminderDecision Evaluate(DateTimeOffset now, LessonConfig config)
    {
        if (config is null || !config.Enabled) return ReminderDecision.No("disabled");

        var snapshot = Peek();
        if (snapshot is null) return ReminderDecision.No("no-data");
        if (!snapshot.PlanLoaded) return ReminderDecision.No("no-plan");
        if (IsStale(now, config.StaleMinutes)) return ReminderDecision.No("stale");
        if (LessonStates.IsInClass(snapshot.State)) return ReminderDecision.No("in-class");
        if (snapshot.State == LessonStates.AfterSchool) return ReminderDecision.No("after-school");
        if (!snapshot.HasNext) return ReminderDecision.No("no-next");

        var key = KeyOf(snapshot);
        var start = snapshot.NextStartAt!.Value;
        var seconds = (int)Math.Round((start - now).TotalSeconds);
        if (seconds <= 0) return ReminderDecision.No("started", key);

        var lead = Math.Clamp(config.RemindMinutesBefore, 1, 30) * 60;
        if (seconds > lead)
            return new ReminderDecision(false, "too-early", key, start, snapshot.NextSubject, seconds);

        if (_snoozedUntil is { } snoozed && snoozed > now)
            return new ReminderDecision(false, "snoozed", key, start, snapshot.NextSubject, seconds);

        if (_lastRemindedKey.Length > 0 && _lastRemindedKey == key)
            return new ReminderDecision(false, "already", key, start, snapshot.NextSubject, seconds);

        return new ReminderDecision(true, "ok", key, start, snapshot.NextSubject, seconds);
    }

    /// <summary>已经弹过（或用户明确忽略）：这节课不再打扰。</summary>
    public void MarkReminded(string key)
    {
        lock (_gate)
        {
            _lastRemindedKey = key ?? "";
            _snoozedUntil = null;
        }
    }

    /// <summary>稍后提醒：在 now + minutes 之前不再弹，窗口过后若还没上课会再问一次。</summary>
    public void Snooze(DateTimeOffset now, int minutes)
    {
        lock (_gate)
        {
            _snoozedUntil = now.AddMinutes(Math.Clamp(minutes, 1, 60));
        }
    }

    public void ClearSnooze()
    {
        lock (_gate) { _snoozedUntil = null; }
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
