namespace ClassroomKit.Core.Lesson;

/// <summary>课前提醒窗的四个出口。</summary>
public enum BoardReminderChoice
{
    /// <summary>保存板书（是否关闭 ClassInX 由 boardSave.closeAfterSave 决定）。</summary>
    Save,
    /// <summary>保存并关闭 ClassInX。</summary>
    SaveAndClose,
    /// <summary>过几分钟再问。</summary>
    Snooze,
    /// <summary>这节课不问。课不保存。</summary>
    Dismiss
}

/// <summary>
/// 课前提醒窗的状态与文案（倒计时、暂停、按键、超时动作）。
/// 与 ConflictDialogModel 同一套路：窗口只负责画，逻辑全在这里，方便单测。
/// </summary>
public sealed class BoardReminderModel
{
    private readonly int _totalMs;
    private readonly BoardReminderChoice? _timeoutChoice;
    private int _elapsedMs;

    public BoardReminderModel(
        int timeoutSec = 30,
        int minutesBefore = 5,
        string? subject = null,
        int secondsToStart = 300,
        string? timeoutAction = "save")
    {
        TotalSeconds = Math.Clamp(timeoutSec, 5, 600);
        _totalMs = TotalSeconds * 1000;
        MinutesBefore = Math.Clamp(minutesBefore, 1, 30);
        Subject = string.IsNullOrWhiteSpace(subject) ? "" : subject.Trim();
        SecondsToStart = Math.Max(0, secondsToStart);
        _timeoutChoice = ResolveTimeout(timeoutAction);
    }

    public int TotalSeconds { get; }
    public int MinutesBefore { get; }
    public string Subject { get; }
    public int SecondsToStart { get; }

    /// <summary>null = 超时也不自动处理（一直挂着等老师）。</summary>
    public BoardReminderChoice? TimeoutChoice => _timeoutChoice;

    public BoardReminderChoice? Chosen { get; private set; }
    public bool Expired { get; private set; }
    public bool Paused { get; set; }

    public int RemainingMs => Math.Max(0, _totalMs - _elapsedMs);
    public int SecondsLeft => (RemainingMs + 999) / 1000;
    public double Progress => _totalMs <= 0 ? 1 : Math.Clamp((double)_elapsedMs / _totalMs, 0, 1);

    /// <summary>标题：下节课 3 分钟后开始 · 数学。</summary>
    public string Title
    {
        get
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(SecondsToStart / 60.0));
            return Subject.Length == 0
                ? $"下节课 {minutes} 分钟后开始"
                : $"下节课 {minutes} 分钟后开始 · {Subject}";
        }
    }

    /// <summary>正文明说超时会发生什么，老师才敢不点。</summary>
    public string Body => _timeoutChoice switch
    {
        BoardReminderChoice.Save => $"ClassInX 里还有板书。{TotalSeconds} 秒后自动保存，不会关闭 ClassInX。",
        BoardReminderChoice.SaveAndClose => $"ClassInX 里还有板书。{TotalSeconds} 秒后自动保存并关闭 ClassInX。",
        BoardReminderChoice.Dismiss => $"ClassInX 里还有板书。{TotalSeconds} 秒后跳过本次，不会保存。",
        _ => "ClassInX 里还有板书，等您决定后再关掉这个窗口。"
    };

    public string CountdownText => _timeoutChoice is null
        ? "等待处理"
        : $"{SecondsLeft} 秒后自动{Verb(_timeoutChoice.Value)}";

    public static string Label(BoardReminderChoice choice) => choice switch
    {
        BoardReminderChoice.Save => "保存",
        BoardReminderChoice.SaveAndClose => "保存并关闭",
        BoardReminderChoice.Snooze => "稍后提醒",
        _ => "忽略本次"
    };

    private static string Verb(BoardReminderChoice choice) => choice switch
    {
        BoardReminderChoice.Save => "保存",
        BoardReminderChoice.SaveAndClose => "保存并关闭",
        _ => "跳过"
    };

    /// <summary>配置里的超时动作 → 选项；"wait" 或写错都表示不自动处理。</summary>
    public static BoardReminderChoice? ResolveTimeout(string? action) => (action ?? "").Trim().ToLowerInvariant() switch
    {
        "save" => BoardReminderChoice.Save,
        "save-close" or "saveclose" => BoardReminderChoice.SaveAndClose,
        "dismiss" or "ignore" => BoardReminderChoice.Dismiss,
        _ => null
    };

    /// <summary>推进时钟；返回 true 表示这一拍刚好到点（调用方据此关窗并执行超时动作）。</summary>
    public bool Tick(int elapsedMs)
    {
        if (Chosen is not null || Expired) return false;
        if (_timeoutChoice is null) return false;
        if (!Paused) _elapsedMs += Math.Max(0, elapsedMs);
        if (_elapsedMs < _totalMs) return false;

        Expired = true;
        Chosen = _timeoutChoice;
        return true;
    }

    public void Choose(BoardReminderChoice choice)
    {
        if (Chosen is not null) return;
        Chosen = choice;
    }

    /// <summary>键盘：1 保存、2 保存并关闭、3 稍后提醒、Esc 稍后提醒。</summary>
    public static BoardReminderChoice? MapKey(char key) => key switch
    {
        '1' => BoardReminderChoice.Save,
        '2' => BoardReminderChoice.SaveAndClose,
        '3' => BoardReminderChoice.Snooze,
        _ => null
    };
}
