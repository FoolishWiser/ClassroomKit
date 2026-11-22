using System.Text.Json;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Lesson;

/// <summary>
/// 守护侧的课表账本：接收 ClassIsland 插件推来的快照、判断该不该提醒、记住提醒过哪一节。
/// 状态落盘到 %ProgramData%\ClassroomKit\lesson-state.json，Agent 重启后不会重复提醒同一节课。
/// </summary>
public sealed class LessonRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly AppLog _log;
    private readonly Func<LessonConfig> _config;
    private readonly string _statePath;
    private LessonLedger _ledger;

    public LessonRuntime(AppLog log, Func<LessonConfig> config, string statePath)
    {
        _log = log;
        _config = config;
        _statePath = statePath;
        _ledger = LessonLedger.FromState(LoadState());
    }

    public LessonLedgerState State() => _ledger.Export();

    /// <summary>插件推来一份课表快照。</summary>
    public (bool Ok, string Message) Accept(LessonSnapshot? snapshot)
    {
        if (snapshot is null) return (false, "缺少课表快照");

        _ledger.Accept(snapshot);
        SaveState();
        _log.Debug($"课表快照：{LessonStates.Describe(snapshot.State)}｜当前={snapshot.CurrentSubject ?? "-"}"
            + $"｜下一节={snapshot.NextSubject ?? "-"}（{snapshot.SecondsToNext} 秒后）｜上一节={snapshot.PreviousSubject ?? "-"}");
        return (true, $"已接收课表快照：{LessonStates.Describe(snapshot.State)}");
    }

    /// <summary>插件退出或课表被关掉：别再拿旧数据提醒。</summary>
    public void Clear()
    {
        _ledger.Clear();
        SaveState();
        _log.Info("课表数据已清空（ClassIsland 插件退出或课表关闭）");
    }

    public ReminderDecision Evaluate(DateTimeOffset now) => _ledger.Evaluate(now, _config());

    public void MarkReminded(string key)
    {
        _ledger.MarkReminded(key);
        SaveState();
    }

    public string? NameHint(DateTimeOffset now) => _ledger.NameHint(now);

    /// <summary>ClassIsland 档案里的全部科目（由 AgentRuntime 注入；读不到时给空列表）。</summary>
    public Func<IReadOnlyList<string>>? AvailableSubjects { get; set; }

    /// <summary>GET /api/lesson 的响应。</summary>
    public object Status()
    {
        var cfg = _config();
        var now = DateTimeOffset.Now;
        var snapshot = _ledger.Peek();
        var decision = _ledger.Evaluate(now, cfg);
        return new
        {
            available = snapshot is not null,
            state = snapshot?.State ?? LessonStates.None,
            stateText = LessonStates.Describe(snapshot?.State),
            currentSubject = snapshot?.CurrentSubject,
            nextSubject = snapshot?.NextSubject,
            previousSubject = snapshot?.PreviousSubject,
            breakName = snapshot?.BreakName,
            nextStartAt = snapshot?.NextStartAt,
            secondsToNext = snapshot?.SecondsToNext ?? 0,
            lessonIndex = snapshot?.LessonIndex ?? 0,
            planLoaded = snapshot?.PlanLoaded ?? false,
            ageSec = (int)(_ledger.AgeSeconds(now) ?? 0),
            at = snapshot?.At,
            nameHint = _ledger.NameHint(now),
            availableSubjects = LessonSubjects.Union(snapshot?.Subjects, AvailableSubjects?.Invoke(), cfg.Subjects),
            reminder = new
            {
                enabled = cfg.Enabled,
                staleMinutes = cfg.StaleMinutes,
                subjects = LessonSubjects.Normalize(cfg.Subjects),
                skipBreakNames = LessonSubjects.Normalize(cfg.SkipBreakNames),
                lastRemindedKey = _ledger.LastRemindedKey,
                reason = decision.Reason,
                remindAt = decision.RemindAt,
                secondsToStart = decision.SecondsToStart
            }
        };
    }

    private LessonLedgerState? LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            var json = File.ReadAllText(_statePath);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<LessonLedgerState>(json, Json);
        }
        catch (Exception ex)
        {
            _log.Warn($"读取课表状态失败（按空状态处理）：{ex.Message}");
            return null;
        }
    }

    private void SaveState()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_ledger.Export(), Json);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _statePath, true);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存课表状态失败：{ex.Message}");
        }
    }
}
