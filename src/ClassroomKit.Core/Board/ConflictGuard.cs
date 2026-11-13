namespace ClassroomKit.Core.Board;

/// <summary>
/// 冲突弹窗的节流器：同一次"进入全屏"只弹一次；两次弹窗之间至少隔 Cooldown（默认 40 分钟）。
/// 纯逻辑，不含 UI，便于单测。
/// </summary>
public sealed class ConflictGuard
{
    private bool _promptedForCurrentSituation;

    /// <summary>冷却时长。</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(40);

    /// <summary>总开关（面板/配置里可关）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>「今天不再提醒」的截止日（含当天）；null = 没暂停。</summary>
    public DateOnly? SnoozeUntil { get; set; }

    public DateTimeOffset? LastPromptAt { get; private set; }

    public int PromptCount { get; private set; }

    /// <summary>最近一次判定给人的描述，便于日志/接口展示。</summary>
    public string LastReason { get; private set; } = "";

    /// <summary>是否允许现在弹窗。</summary>
    public bool ShouldPrompt(BoardSituation situation, DateTimeOffset now)
    {
        if (!Enabled || situation is null) return false;

        if (SnoozeUntil is not null && DateOnly.FromDateTime(now.LocalDateTime) <= SnoozeUntil.Value)
        {
            LastReason = $"今天不再提醒（到 {SnoozeUntil.Value:yyyy-MM-dd} 为止，可在面板清除）";
            return false;
        }

        if (!situation.ShouldPrompt)
        {
            // 情境解除：下一次再进入全屏就重新有资格弹
            _promptedForCurrentSituation = false;
            LastReason = $"不满足触发条件（{situation.Describe()}）";
            return false;
        }

        // 冷却优先：超过冷却时长后，即使还停在同一情境里也允许再提醒一次
        if (LastPromptAt is not null && now - LastPromptAt.Value < Cooldown)
        {
            var left = Cooldown - (now - LastPromptAt.Value);
            LastReason = $"冷却中，还剩 {(int)left.TotalMinutes} 分钟";
            return false;
        }

        if (_promptedForCurrentSituation && LastPromptAt is null)
        {
            LastReason = $"同一次冲突已提示过（{situation.Describe()}）";
            return false;
        }

        LastReason = $"触发：{situation.Describe()}";
        return true;
    }

    public void MarkPrompted(DateTimeOffset now)
    {
        _promptedForCurrentSituation = true;
        LastPromptAt = now;
        PromptCount++;
    }

    /// <summary>ClassIsland 强制触发：绕过冷却与"同一次"限制。</summary>
    public void MarkForced(DateTimeOffset now)
    {
        _promptedForCurrentSituation = true;
        LastPromptAt = now;
        PromptCount++;
        LastReason = "手动/ClassIsland 触发";
    }

    public void Reset()
    {
        _promptedForCurrentSituation = false;
        LastPromptAt = null;
        PromptCount = 0;
        LastReason = "";
    }
}
