namespace ClassroomKit.Core.Board;

/// <summary>板书冲突提示窗的四个选项。</summary>
public enum ConflictChoice
{
    KeepBoth,
    KeepClassIn,
    KeepSeewo,
    KeepNone
}

/// <summary>一次按键归一化后的意图：要么选了某个选项，要么切换「今天不再提醒」。</summary>
public readonly record struct ConflictKeyPress(ConflictChoice? Choice, bool ToggleSnooze);

/// <summary>
/// 冲突提示窗的全部状态与文案（倒计时、暂停、按键、按钮文案）。
/// 纯逻辑、不碰 UI，方便单测；窗口本身只负责画和传事件。
/// </summary>
public sealed class ConflictDialogModel
{
    private readonly int _totalMs;
    private int _elapsedMs;

    public ConflictDialogModel(int timeoutSec = 60)
    {
        TotalSeconds = Math.Clamp(timeoutSec, 3, 3600);
        _totalMs = TotalSeconds * 1000;
    }

    public int TotalSeconds { get; }

    /// <summary>已经选好的结果；null = 还在等用户。</summary>
    public ConflictChoice? Chosen { get; private set; }

    /// <summary>用户勾了「今天不再提醒」。</summary>
    public bool SnoozeToday { get; private set; }

    /// <summary>鼠标悬停时暂停倒计时（正在读文案就别催他）。</summary>
    public bool Paused { get; set; }

    /// <summary>倒计时走完、已自动按「都保留」处理。</summary>
    public bool Expired { get; private set; }

    public int RemainingMs => Math.Max(0, _totalMs - _elapsedMs);

    public int SecondsLeft => (RemainingMs + 999) / 1000;

    /// <summary>0 = 刚开始，1 = 到点了。</summary>
    public double Progress => _totalMs <= 0 ? 1 : Math.Clamp((double)_elapsedMs / _totalMs, 0, 1);

    /// <summary>推进时钟。返回 true 表示这一拍刚好到点（调用方据此关窗）。</summary>
    public bool Tick(int elapsedMs)
    {
        if (Chosen is not null || Expired) return false;
        if (!Paused) _elapsedMs += Math.Max(0, elapsedMs);
        if (_elapsedMs < _totalMs) return false;

        Expired = true;
        Chosen = ConflictChoice.KeepBoth;
        return true;
    }

    public void Choose(ConflictChoice choice)
    {
        if (Chosen is not null) return;
        Chosen = choice;
    }

    public void SetSnooze(bool value) => SnoozeToday = value;

    /// <summary>倒计时文案：剩 1 秒时不要显示「0 秒后」。</summary>
    public static string CountdownText(int secondsLeft) => secondsLeft switch
    {
        <= 0 => "正在按「都保留」继续…",
        _ => $"{secondsLeft} 秒后自动「都保留」"
    };

    public static string Label(ConflictChoice choice) => choice switch
    {
        ConflictChoice.KeepClassIn => "保留 ClassInX",
        ConflictChoice.KeepSeewo => "保留希沃白板",
        ConflictChoice.KeepNone => "都退出",
        _ => "都保留"
    };

    /// <summary>按钮第二行的小字，说明这个选项会关掉什么。</summary>
    public static string Hint(ConflictChoice choice) => choice switch
    {
        ConflictChoice.KeepClassIn => "关闭希沃白板",
        ConflictChoice.KeepSeewo => "关闭 ClassInX",
        ConflictChoice.KeepNone => "两个都关闭",
        _ => "不关闭任何软件"
    };

    /// <summary>卡片上抠不到真图标时画的首字母（希沃→希，ClassIn→C）。</summary>
    public static string InitialOf(ConflictChoice choice) => choice switch
    {
        ConflictChoice.KeepSeewo => "希",
        ConflictChoice.KeepClassIn => "C",
        ConflictChoice.KeepBoth => "都",
        _ => "×"
    };

    /// <summary>按钮上的数字序号（与键盘 1-4 一致）。</summary>
    public static int Shortcut(ConflictChoice choice) => choice switch
    {
        ConflictChoice.KeepSeewo => 1,
        ConflictChoice.KeepClassIn => 2,
        ConflictChoice.KeepBoth => 3,
        _ => 4
    };

    /// <summary>
    /// 按键归一化：Esc / Enter = 都保留（安全的默认），1-4 = 四个选项，S = 切「今天不再提醒」。
    /// 不认识就返回空，调用方忽略即可。
    /// </summary>
    public static ConflictKeyPress MapKey(string? key)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        if (k.StartsWith("numpad", StringComparison.Ordinal)) k = k[6..];
        return k switch
        {
            "escape" or "esc" => new ConflictKeyPress(ConflictChoice.KeepBoth, false),
            "enter" or "return" => new ConflictKeyPress(ConflictChoice.KeepBoth, false),
            "1" => new ConflictKeyPress(ConflictChoice.KeepSeewo, false),
            "2" => new ConflictKeyPress(ConflictChoice.KeepClassIn, false),
            "3" => new ConflictKeyPress(ConflictChoice.KeepBoth, false),
            "4" => new ConflictKeyPress(ConflictChoice.KeepNone, false),
            "s" => new ConflictKeyPress(null, true),
            _ => new ConflictKeyPress(null, false)
        };
    }
}
