using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Lesson;

/// <summary>课后自动保存的阶段（对 API / 插件 / 界面都稳定）。</summary>
public static class BoardAutoSavePhases
{
    public const string Idle = "idle";
    public const string Waiting = "waiting";
    public const string Countdown = "countdown";
    public const string Cooldown = "cooldown";
    public const string Saving = "saving";
    public const string Saved = "saved";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";

    public static bool IsActive(string? phase) =>
        phase is Countdown or Cooldown or Waiting or Saving;
}

/// <summary>一次自动保存的完整快照；不可变，可安全跨线程传给 API / 插件。</summary>
public sealed record BoardAutoSaveState(
    string Phase,
    bool Active,
    int RemainingSeconds,
    string? Subject,
    DateTimeOffset? DueAt,
    DateTimeOffset? DeadlineAt,
    string Message,
    DateTimeOffset At,
    string? Key)
{
    public static BoardAutoSaveState Idle(DateTimeOffset now) =>
        new(BoardAutoSavePhases.Idle, false, 0, null, null, null, "", now, "");
}

/// <summary>一次 tick 的结果：StartSave=true 表示调用方应立刻在后台保存板书。</summary>
public readonly record struct BoardAutoSaveTick(BoardAutoSaveState State, bool StartSave, long Generation = 0);

/// <summary>
/// 课后自动保存状态机：下课后 5 分钟进入 15 秒倒计时，任何键鼠输入都会打断并在 10 秒后重试，
/// 到下一节上课前 2 分 30 秒仍无法保存就放弃本次。纯逻辑，不碰 IO / UI，方便单测。
/// </summary>
public sealed class BoardAutoSaveTracker
{
    public const int WaitMinutes = 5;
    public const int CountdownSeconds = 15;
    public const int CooldownSeconds = 10;
    public const int DeadlineBeforeNextSeconds = 150;
    public const int AfterSchoolDeadlineMinutes = 30;
    /// <summary>快照时刻与当前时刻相差超过这个分钟数时，以当前时刻作为课间开始。</summary>
    private const int SnapshotToleranceMinutes = 2;

    private readonly object _gate = new();
    private readonly Func<AppConfig> _config;
    private readonly Func<DateTimeOffset> _now;
    private readonly AppLog? _log;

    private string _phase = BoardAutoSavePhases.Idle;
    private string? _key;
    private string? _subject;
    private DateTimeOffset? _dueAt;
    private DateTimeOffset? _deadlineAt;
    private DateTimeOffset _countdownStartedAt;
    private DateTimeOffset _cooldownStartedAt;
    private DateTimeOffset _cooldownUntil;
    private DateTimeOffset _finishedAt;
    private bool _dryRun;
    private string _message = "";
    private long _generation;
    private DateTimeOffset _savingStartedAt;
    private string _armLogKey = "";

    public BoardAutoSaveTracker(Func<AppConfig> config, Func<DateTimeOffset>? now = null, AppLog? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _now = now ?? (() => DateTimeOffset.Now);
        _log = log;
    }

    public BoardAutoSaveState State
    {
        get
        {
            lock (_gate)
            {
                return BuildLocked(_now());
            }
        }
    }

    /// <summary>下课 / 放学：由课表账本判定 Show 后调用。</summary>
    public BoardAutoSaveState Arm(LessonSnapshot snapshot, DateTimeOffset now, bool classInRunning)
    {
        lock (_gate)
        {
            var cfg = _config();
            if (snapshot is null || !cfg.Lesson.Enabled)
                return ResetLocked(now, "");

            var subject = snapshot.PreviousSubject;
            if (string.IsNullOrWhiteSpace(subject))
                return ResetLocked(now, "");

            if (snapshot.State is not (LessonStates.Breaking or LessonStates.AfterSchool))
                return ResetLocked(now, "");

            var breakAt = snapshot.At;
            if (breakAt == default || Math.Abs((now - breakAt).TotalMinutes) > SnapshotToleranceMinutes)
                breakAt = now;

            var dueAt = breakAt.AddMinutes(WaitMinutes);
            var deadlineAt = snapshot.State == LessonStates.AfterSchool
                ? breakAt.AddMinutes(AfterSchoolDeadlineMinutes)
                : snapshot.NextStartAt is { } next
                    ? next.AddSeconds(-DeadlineBeforeNextSeconds)
                    : breakAt.AddMinutes(AfterSchoolDeadlineMinutes);

            var key = LessonLedger.KeyOf(snapshot);
            // 同一个课间每秒都会判定为 Show：已经在等待/倒计时/保存中就不要重置进度。
            if (!_dryRun && string.Equals(_key, key, StringComparison.Ordinal) &&
                _phase is BoardAutoSavePhases.Waiting or BoardAutoSavePhases.Countdown or
                         BoardAutoSavePhases.Cooldown or BoardAutoSavePhases.Saving)
                return BuildLocked(now);

            // 上次因为 ClassInX 没运行而放弃：等它启动后允许重试；课间太短则保持放弃。
            if (!_dryRun && string.Equals(_key, key, StringComparison.Ordinal) &&
                _phase == BoardAutoSavePhases.Abandoned)
            {
                if (_deadlineAt is { } oldDeadline && now > oldDeadline) return BuildLocked(now);
                if (!classInRunning) return BuildLocked(now);
            }

            _key = key;
            _subject = subject.Trim();
            _dueAt = dueAt;
            _deadlineAt = deadlineAt;
            _dryRun = false;
            _finishedAt = default;
            _savingStartedAt = default;
            _generation++;

            if (dueAt.AddSeconds(CountdownSeconds) > deadlineAt)
            {
                LogArmLocked(BoardAutoSavePhases.Abandoned, $"课后自动保存：{_subject} 课间太短，来不及自动保存");
                return SetLocked(BoardAutoSavePhases.Abandoned, now, "课间太短，来不及自动保存");
            }

            if (cfg.Lesson.OnlyWhenClassInBoard && !classInRunning)
            {
                LogArmLocked(BoardAutoSavePhases.Abandoned, $"课后自动保存：{_subject} 下课时 ClassInX 没有运行，跳过本次");
                return SetLocked(BoardAutoSavePhases.Abandoned, now, "ClassInX 没有运行，跳过本次自动保存");
            }

            LogArmLocked(BoardAutoSavePhases.Waiting, $"课后自动保存：{_subject} 下课，{dueAt:HH:mm:ss} 开始倒计时，最晚 {deadlineAt:HH:mm:ss}");
            return SetLocked(BoardAutoSavePhases.Waiting, now,
                $"下课后 {WaitMinutes} 分钟开始倒计时（{dueAt:HH:mm}）");
        }
    }

    /// <summary>每秒调用一次。</summary>
    public BoardAutoSaveTick Tick(DateTimeOffset now, DateTimeOffset? lastInputAt, LessonSnapshot? snapshot)
    {
        lock (_gate)
        {
            if (_phase is BoardAutoSavePhases.Saved or BoardAutoSavePhases.Failed)
            {
                // 正常 15 秒后回 idle；时钟回拨时也不要把结果永久钉在界面上。
                if (now >= _finishedAt.AddSeconds(CountdownSeconds) || now < _finishedAt.AddMinutes(-1))
                    return new BoardAutoSaveTick(ResetLocked(now, ""), false);
                return new BoardAutoSaveTick(BuildLocked(now), false);
            }

            if (!BoardAutoSavePhases.IsActive(_phase))
                return new BoardAutoSaveTick(BuildLocked(now), false);

            var cfg = _config();
            if (!cfg.Lesson.Enabled)
                return new BoardAutoSaveTick(ResetLocked(now, ""), false);

            // 保存任务可能跑几十秒到几分钟：必须有自己的超时，不能一直停在「正在保存」。
            if (_phase == BoardAutoSavePhases.Saving && !_dryRun)
            {
                var limit = TimeSpan.FromSeconds(Math.Clamp(cfg.BoardSave.LaunchTimeoutSec, 5, 600) + 90);
                if (_savingStartedAt != default && now - _savingStartedAt > limit)
                    return new BoardAutoSaveTick(FailSavingLocked(now, "保存超时，请手动保存板书"), false);
                return new BoardAutoSaveTick(BuildLocked(now), false);
            }

            // 预览是独立演示，不依赖课表快照；真实保存才校验快照是否还对得上。
            if (!_dryRun)
            {
                if (snapshot is null || _key is null)
                    return new BoardAutoSaveTick(AbandonLocked(now, "课表数据已清空，放弃本次自动保存"), false);

                if (snapshot.State is not (LessonStates.Breaking or LessonStates.AfterSchool))
                    return new BoardAutoSaveTick(AbandonLocked(now, "已经上课，放弃本次自动保存"), false);

                if (!string.Equals(LessonLedger.KeyOf(snapshot), _key, StringComparison.Ordinal))
                    return new BoardAutoSaveTick(AbandonLocked(now, "课表已经切换，放弃本次自动保存"), false);

                if (_deadlineAt is { } deadline && now > deadline)
                    return new BoardAutoSaveTick(AbandonLocked(now, "快到上课时间，放弃本次自动保存"), false);
            }

            if (_phase == BoardAutoSavePhases.Waiting)
            {
                if (_dueAt is { } due && now >= due)
                {
                    _phase = BoardAutoSavePhases.Countdown;
                    _countdownStartedAt = now;
                    _message = $"{CountdownSeconds} 秒内无人操作就自动保存板书";
                }
            }

            if (_phase == BoardAutoSavePhases.Countdown)
            {
                if (lastInputAt is { } input && input > _countdownStartedAt)
                {
                    _phase = BoardAutoSavePhases.Cooldown;
                    _cooldownStartedAt = now;
                    _cooldownUntil = now.AddSeconds(CooldownSeconds);
                    _message = $"检测到操作，{CooldownSeconds} 秒后重试";
                }
                else if (now >= _countdownStartedAt.AddSeconds(CountdownSeconds))
                {
                    if (_dryRun)
                    {
                        _log?.Info("课后自动保存：预览结束，未执行保存");
                        return new BoardAutoSaveTick(ResetLocked(now, "预览结束，未执行保存"), false);
                    }

                    _phase = BoardAutoSavePhases.Saving;
                    _savingStartedAt = now;
                    _generation++;
                    _message = "正在保存板书…";
                    _log?.Info($"课后自动保存：{_subject} 15 秒无操作，开始保存");
                    return new BoardAutoSaveTick(BuildLocked(now), true, _generation);
                }
            }

            if (_phase == BoardAutoSavePhases.Cooldown)
            {
                if (lastInputAt is { } input && input > _cooldownStartedAt)
                {
                    _cooldownStartedAt = now;
                    _cooldownUntil = now.AddSeconds(CooldownSeconds);
                    _message = $"检测到操作，{CooldownSeconds} 秒后重试";
                }
                else if (now >= _cooldownUntil)
                {
                    if (_deadlineAt is { } limit && now.AddSeconds(CountdownSeconds) <= limit)
                    {
                        _phase = BoardAutoSavePhases.Countdown;
                        _countdownStartedAt = now;
                        _message = $"{CountdownSeconds} 秒内无人操作就自动保存板书";
                    }
                    else
                    {
                        return new BoardAutoSaveTick(AbandonLocked(now, "快到上课时间，放弃本次自动保存"), false);
                    }
                }
            }

            return new BoardAutoSaveTick(BuildLocked(now), false);
        }
    }

    /// <summary>
    /// 只演示倒计时，不真的保存。真实保存正在进行时拒绝预览，避免一个预览把当次保存吞掉。
    /// 返回后的 Key 仍为 "preview" 才表示预览已开始。
    /// </summary>
    public BoardAutoSaveState Preview(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_dryRun && _phase is BoardAutoSavePhases.Waiting or BoardAutoSavePhases.Countdown or
                                 BoardAutoSavePhases.Cooldown or BoardAutoSavePhases.Saving)
                return BuildLocked(now);

            _phase = BoardAutoSavePhases.Countdown;
            _key = "preview";
            _subject = "示例课程";
            _dueAt = now;
            _deadlineAt = now.AddMinutes(10);
            _countdownStartedAt = now;
            _savingStartedAt = default;
            _finishedAt = default;
            _dryRun = true;
            _generation++;
            _message = $"预览：{CountdownSeconds} 秒后自动保存（不会真的保存）";
            return BuildLocked(now);
        }
    }

    /// <summary>取消当前这一次（预览 / 已结束状态）；保存中拒绝取消。返回后 Phase 仍为 saving 表示被拒绝。</summary>
    public BoardAutoSaveState Cancel(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_phase == BoardAutoSavePhases.Saving) return BuildLocked(now);
            _log?.Info("课后自动保存：已取消本次自动保存");
            return ResetLocked(now, "已取消本次自动保存");
        }
    }

    /// <summary>Agent 已经拿到保存任务，进入保存中；代次不匹配说明这次保存已经过期。</summary>
    public void MarkSaving(long generation = 0)
    {
        lock (_gate)
        {
            if (_phase != BoardAutoSavePhases.Countdown && _phase != BoardAutoSavePhases.Saving) return;
            if (generation != 0 && generation != _generation) return;
            _phase = BoardAutoSavePhases.Saving;
            _message = "正在保存板书…";
        }
    }

    /// <summary>兼容旧调用：不校验代次。</summary>
    public BoardAutoSaveState Complete(bool ok, string message, DateTimeOffset? now = null) =>
        Complete(0, ok, message, now);

    /// <summary>Agent 保存完成；代次不匹配（已取消/已开新课间/已超时）时丢弃这次结果。</summary>
    public BoardAutoSaveState Complete(long generation, bool ok, string message, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            var at = now ?? _now();
            if (generation != 0 && generation != _generation) return BuildLocked(at);
            _savingStartedAt = default;
            _finishedAt = at;
            _phase = ok ? BoardAutoSavePhases.Saved : BoardAutoSavePhases.Failed;
            _message = string.IsNullOrWhiteSpace(message) ? (ok ? "板书已保存" : "保存板书失败") : message;
            _log?.Info("课后自动保存：" + _message);
            return BuildLocked(at);
        }
    }

    private BoardAutoSaveState BuildLocked(DateTimeOffset now)
    {
        var remaining = 0;
        if (_phase == BoardAutoSavePhases.Countdown)
            remaining = Math.Max(0, (int)Math.Ceiling((_countdownStartedAt.AddSeconds(CountdownSeconds) - now).TotalSeconds));
        else if (_phase == BoardAutoSavePhases.Cooldown)
            remaining = Math.Max(0, (int)Math.Ceiling((_cooldownUntil - now).TotalSeconds));

        return new BoardAutoSaveState(
            _phase,
            BoardAutoSavePhases.IsActive(_phase),
            remaining,
            _subject,
            _dueAt,
            _deadlineAt,
            _message,
            now,
            _key);
    }

    private BoardAutoSaveState SetLocked(string phase, DateTimeOffset now, string message)
    {
        _phase = phase;
        _message = message;
        return BuildLocked(now);
    }

    private BoardAutoSaveState AbandonLocked(DateTimeOffset now, string message)
    {
        _log?.Info("课后自动保存：" + message);
        _phase = BoardAutoSavePhases.Abandoned;
        _message = message;
        _finishedAt = now;
        _savingStartedAt = default;
        _generation++;
        return BuildLocked(now);
    }

    private BoardAutoSaveState FailSavingLocked(DateTimeOffset now, string message)
    {
        _log?.Info("课后自动保存：" + message);
        _phase = BoardAutoSavePhases.Failed;
        _message = message;
        _finishedAt = now;
        _savingStartedAt = default;
        _generation++;
        return BuildLocked(now);
    }

    private void LogArmLocked(string phase, string message)
    {
        var key = (_key ?? "") + "|" + phase;
        if (string.Equals(key, _armLogKey, StringComparison.Ordinal)) return;
        _armLogKey = key;
        _log?.Info(message);
    }

    private BoardAutoSaveState ResetLocked(DateTimeOffset now, string message)
    {
        _generation++;
        _savingStartedAt = default;
        _phase = BoardAutoSavePhases.Idle;
        _key = null;
        _subject = null;
        _dueAt = null;
        _deadlineAt = null;
        _countdownStartedAt = default;
        _cooldownStartedAt = default;
        _cooldownUntil = default;
        _finishedAt = default;
        _dryRun = false;
        _message = message;
        return BuildLocked(now);
    }
}
