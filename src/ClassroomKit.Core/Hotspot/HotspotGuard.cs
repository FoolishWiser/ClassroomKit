using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Hotspot;

public enum GuardState
{
    Unknown = 0,
    Disabled = 1,
    WaitUplink = 2,
    BlockedWifiUplink = 3,
    Starting = 4,
    On = 5,
    Backoff = 6,
    Failed = 7
}

/// <summary>
/// 热点守护状态机。纯逻辑，不依赖 Windows —— 便于在 Linux 上跑单测。
/// 规则：策略关 → 关热点；无上行 → 等待；上行是 Wi-Fi → 拒绝；
/// 已开 → 检查 SSID 漂移；未开 → 按退避拉起（60/300/1800 秒）。
/// </summary>
public sealed class HotspotGuard
{
    private static readonly int[] BackoffSeconds = { 60, 300, 1800 };
    private const int DriftRecoveryFailStreak = 5;

    private readonly ITethering _tethering;
    private readonly Func<AppConfig> _config;
    private readonly AppLog _log;
    private readonly Func<DateTimeOffset> _now;

    private int _failStreak;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _capWarned;
    private bool _timeoutWarned;
    private OperationalState _lastState = OperationalState.Unknown;

    public HotspotGuard(ITethering tethering, Func<AppConfig> config, AppLog log, Func<DateTimeOffset>? now = null)
    {
        _tethering = tethering;
        _config = config;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public GuardState State { get; private set; } = GuardState.Unknown;
    public string? LastError { get; private set; }
    public string? CapabilityWarning { get; private set; }
    public TetheringSnapshot? LastSnapshot { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }
    public DateTimeOffset? LastChange { get; private set; }
    public int RestartCount { get; private set; }
    public IReadOnlyList<TetherClient> Clients { get; private set; } = Array.Empty<TetherClient>();

    private void SetState(GuardState next, string? reason = null)
    {
        if (State == next) return;
        var prev = State;
        State = next;
        LastChange = _now();
        _log.Info($"状态 {prev} → {next}{(string.IsNullOrEmpty(reason) ? "" : $"（{reason}）")}");
    }

    /// <summary>跑一次巡检。返回 true 表示需要把下一次巡检排近一些。</summary>
    public async Task TickOnceAsync(CancellationToken ct)
    {
        var cfg = _config().Hotspot;
        TetheringSnapshot snap;
        try
        {
            snap = await _tethering.GetSnapshotAsync(ct);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetState(GuardState.Failed, "读取状态失败");
            return;
        }

        LastSnapshot = snap;
        LastError = snap.Error;

        if (snap.NoConnectionsTimeoutEnabled == true)
        {
            var ok = await _tethering.EnsureNoConnectionsTimeoutDisabledAsync(ct);
            if (!_timeoutWarned)
            {
                _timeoutWarned = true;
                if (ok) _log.Info("已关闭“无设备 5 分钟自动关热点”（热点不会再自己消失）");
                else _log.Warn("无法关闭“无设备 5 分钟自动关热点”，热点可能自己消失");
            }
        }
        else
        {
            _timeoutWarned = false;
        }

        CheckCapacity(snap, cfg);

        if (!cfg.Enabled)
        {
            if (snap.OperationalState == OperationalState.On)
            {
                await SafeAsync(() => _tethering.StopAsync(ct), "关闭热点");
                await RefreshSnapshotAsync(ct);
            }
            SetState(GuardState.Disabled);
            return;
        }

        if (!snap.UplinkAvailable)
        {
            SetState(GuardState.WaitUplink, "上行未连接");
            return;
        }

        if (snap.UplinkIsWifi && !cfg.AllowWifiUplink)
        {
            SetState(GuardState.BlockedWifiUplink, "上行是 Wi-Fi，拒绝开热点");
            return;
        }

        if (snap.OperationalState == OperationalState.On)
        {
            _failStreak = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            LastSuccess = _now();
            if (_lastState != OperationalState.On) RestartCount++;
            _lastState = OperationalState.On;

            if (Drifted(snap, cfg))
            {
                var ok = await SafeAsync(
                    () => _tethering.ConfigureAccessPointAsync(cfg.Ssid, cfg.Passphrase, ct), "覆盖热点配置");
                if (ok)
                {
                    _log.Warn("热点配置被改动，已自动改回");
                    await RefreshSnapshotAsync(ct);
                }
            }

            try { Clients = await _tethering.GetClientsAsync(ct); }
            catch { Clients = Array.Empty<TetherClient>(); }

            SetState(GuardState.On);
            return;
        }

        _lastState = snap.OperationalState;
        if (snap.OperationalState == OperationalState.Starting)
        {
            SetState(GuardState.Starting, "系统正在启动热点");
            return;
        }

        var now = _now();
        if (now < _nextAttempt)
        {
            SetState(GuardState.Backoff, $"等待重试（{(_nextAttempt - now).TotalSeconds:F0}s）");
            return;
        }

        SetState(GuardState.Starting, "正在开启热点");
        var started = await SafeAsync(() => _tethering.StartAsync(ct), "开启热点");
        if (started)
        {
            _failStreak = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            LastSuccess = _now();
            await RefreshSnapshotAsync(ct);
            SetState(GuardState.On);
            return;
        }

        _failStreak++;
        var idx = Math.Min(_failStreak - 1, BackoffSeconds.Length - 1);
        var delay = BackoffSeconds[idx];
        _nextAttempt = now.AddSeconds(delay);
        SetState(GuardState.Backoff, $"连续失败 {_failStreak} 次，{delay}s 后重试");
    }

    private void CheckCapacity(TetheringSnapshot snap, HotspotConfig cfg)
    {
        if (snap.MaxClientCount <= 0) return;
        if (snap.MaxClientCount >= cfg.TargetClients)
        {
            if (_capWarned)
            {
                _capWarned = false;
                CapabilityWarning = null;
            }
            return;
        }

        CapabilityWarning = $"系统上限 {snap.MaxClientCount} 台，低于目标 {cfg.TargetClients} 台";
        if (!_capWarned)
        {
            _capWarned = true;
            _log.Warn($"{CapabilityWarning}；注册表放宽可能未生效，可执行 --fix-peers 修复");
        }
    }

    private static bool Drifted(TetheringSnapshot snap, HotspotConfig cfg)
    {
        if (string.IsNullOrEmpty(snap.CurrentSsid)) return false;
        if (!string.Equals(snap.CurrentSsid, cfg.Ssid, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(snap.CurrentPassphrase) &&
            !string.Equals(snap.CurrentPassphrase, cfg.Passphrase, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>动作做完立刻重读一次状态，避免面板显示的还是动作前的旧值。</summary>
    private async Task RefreshSnapshotAsync(CancellationToken ct)
    {
        try { LastSnapshot = await _tethering.GetSnapshotAsync(ct); }
        catch { /* 读不到就保留旧快照 */ }
    }

    private async Task<bool> SafeAsync(Func<Task<bool>> action, string what)
    {
        try
        {
            var ok = await action();
            if (!ok) LastError = $"{what}失败";
            return ok;
        }
        catch (Exception ex)
        {
            LastError = $"{what}失败：{ex.Message}";
            _log.Error(LastError);
            return false;
        }
    }

    /// <summary>覆盖策略开关（来自 Web UI / 命令）。</summary>
    public void SetEnabled(bool enabled)
    {
        _config().Hotspot.Enabled = enabled;
        _nextAttempt = DateTimeOffset.MinValue;
        _failStreak = 0;
        _log.Info(enabled ? "策略：开启热点" : "策略：关闭热点");
    }
}
