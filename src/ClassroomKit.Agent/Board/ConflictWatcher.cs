using ClassroomKit.Core.Board;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 每 2 秒扫一次窗口，按「两个悬浮窗共存 + 进入任一全屏」判断是否弹冲突窗。
/// 冷却/去重交给 <see cref="ConflictGuard"/>，UI 交给托盘线程。
/// </summary>
public sealed class ConflictWatcher : IDisposable
{
    private readonly AppLog _log;
    private readonly Func<ConflictConfig> _config;
    private readonly Func<IReadOnlyList<string>> _extraFullscreen;
    private readonly ConflictGuard _guard = new();
    private readonly Action<bool>? _setSnoozeToday;

    private System.Threading.Timer? _timer;
    private BoardSituation _last = new();

    public ConflictWatcher(AppLog log, Func<ConflictConfig> config, Func<IReadOnlyList<string>> extraFullscreen,
        Action<bool>? setSnoozeToday = null)
    {
        _log = log;
        _config = config;
        _extraFullscreen = extraFullscreen;
        _setSnoozeToday = setSnoozeToday;
    }

    /// <summary>需要弹窗时在后台线程触发，托盘层负责 Post 到 UI 线程。</summary>
    public Action<BoardSituation>? PromptRequested { get; set; }

    public BoardSituation LastSituation => _last;

    public ConflictGuard Guard => _guard;

    public void Start(int intervalMs = 2000)
    {
        _timer ??= new System.Threading.Timer(_ => Tick(), null, 1500, Math.Max(500, intervalMs));
    }

    private void Tick()
    {
        try
        {
            var cfg = _config();
            _guard.Enabled = cfg.Enabled;
            _guard.Cooldown = TimeSpan.FromMinutes(Math.Clamp(cfg.CooldownMinutes, 1, 24 * 60));
            _guard.SnoozeUntil = ConflictSnooze.Parse(cfg.SnoozeDate);

            var situation = Evaluate();
            var now = DateTimeOffset.Now;
            var wasActive = _last.ShouldPrompt;
            _last = situation;

            if (situation.ShouldPrompt && !wasActive)
            {
                _log.Info($"检测到板书软件冲突：{situation.Describe()}");
            }

            if (_guard.ShouldPrompt(situation, now))
            {
                _guard.MarkPrompted(now);
                _log.Warn($"弹出板书冲突提示：{situation.Describe()}");
                PromptRequested?.Invoke(situation);
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"冲突检测异常：{ex.Message}");
        }
    }

    public BoardSituation Evaluate()
    {
        var (screenW, screenH) = WindowScanner.ScreenSize();
        var windows = WindowScanner.Scan();
        return WindowClassifier.Situate(windows, screenW, screenH, _extraFullscreen());
    }

    /// <summary>ClassIsland / 面板手动触发：忽略冷却，直接弹（若当前情境成立）。</summary>
    public (bool Ok, string Message) CheckNow(bool force)
    {
        var situation = Evaluate();
        _last = situation;
        if (!situation.ShouldPrompt)
        {
            return (false, $"当前不满足触发条件：{situation.Describe()}");
        }

        if (!force && !_guard.ShouldPrompt(situation, DateTimeOffset.Now))
        {
            return (false, _guard.LastReason);
        }

        if (force)
        {
            _guard.MarkForced(DateTimeOffset.Now);
            _log.Info($"手动触发板书冲突提示：{situation.Describe()}");
            PromptRequested?.Invoke(situation);
            return (true, "已触发");
        }

        _guard.MarkPrompted(DateTimeOffset.Now);
        PromptRequested?.Invoke(situation);
        return (true, "已触发");
    }

    /// <summary>用户在弹窗里勾了「今天不再提醒」（或取消）。由托盘层在关窗后调用。</summary>
    public void SetSnoozeToday(bool snooze)
    {
        _setSnoozeToday?.Invoke(snooze);
        _guard.SnoozeUntil = snooze ? DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime) : null;
        _log.Info(snooze ? "板书冲突提示：今天不再提醒" : "板书冲突提示：恢复提醒");
    }

    public void ApplyChoice(ConflictChoice choice)
    {
        var cfg = _config();
        _log.Info($"板书冲突选择：{choice}");

        switch (choice)
        {
            case ConflictChoice.KeepClassIn:
            {
                var (ok, msg) = AppCloser.CloseSeewo();
                _log.Info($"保留 ClassInX → {msg}");
                if (!ok) _log.Warn(msg);
                break;
            }
            case ConflictChoice.KeepSeewo:
            {
                var (ok, msg) = AppCloser.CloseClassIn();
                _log.Info($"保留希沃白板 → {msg}");
                if (!ok) _log.Warn(msg);
                break;
            }
            case ConflictChoice.KeepNone:
            {
                var (ok1, msg1) = AppCloser.CloseClassIn();
                var (ok2, msg2) = AppCloser.CloseSeewo();
                _log.Info($"都不保留 → {msg1}；{msg2}");
                if (!ok1 || !ok2) _log.Warn($"{msg1}；{msg2}");
                break;
            }
            default:
                _log.Info("都保留，不做处理");
                break;
        }

        _ = cfg; // 配置在 Tick 里已读取，这里只是保持签名一致
    }

    public void Dispose() => _timer?.Dispose();
}
