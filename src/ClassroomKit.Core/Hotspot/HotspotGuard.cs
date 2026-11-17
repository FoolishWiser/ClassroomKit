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

    /// <summary>零客户端时采样 Wi-Fi Direct 适配器收包的最小间隔。</summary>
    private static readonly TimeSpan RxSampleInterval = TimeSpan.FromSeconds(60);
    /// <summary>收包一直不涨多久之后才认定「假启动」（有信标但空口收不到东西）。</summary>
    private static readonly TimeSpan RxIdleThreshold = TimeSpan.FromSeconds(300);
    /// <summary>两次自动复位网卡之间的最小间隔，避免空教室整夜反复复位。</summary>
    private static readonly TimeSpan AdapterResetCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DriftWarnWindow = TimeSpan.FromMinutes(10);
    private const int DriftWarnCount = 3;

    private readonly ITethering _tethering;
    private readonly Func<AppConfig> _config;
    private readonly AppLog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _startTimeout;

    private int _failStreak;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _capWarned;
    private bool _timeoutWarned;
    private OperationalState _lastState = OperationalState.Unknown;

    private DateTimeOffset? _onSince;
    private DateTimeOffset _rxSampleAt = DateTimeOffset.MinValue;
    private long? _rxBaseline;
    private DateTimeOffset _rxBaselineAt;
    private bool _rxIdleWarned;
    private DateTimeOffset _lastAdapterReset = DateTimeOffset.MinValue;
    private int _driftFixes;
    private DateTimeOffset _driftWindowStart = DateTimeOffset.MinValue;

    public HotspotGuard(ITethering tethering, Func<AppConfig> config, AppLog log,
        Func<DateTimeOffset>? now = null, TimeSpan? startTimeout = null)
    {
        _tethering = tethering;
        _config = config;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>「热点报已开、但零客户端且长期收不到包」的健康提示，面板/自检用。</summary>
    public string? HealthWarning { get; private set; }

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
                    CountDriftFix();
                    await RefreshSnapshotAsync(ct);
                }
            }

            try { Clients = await _tethering.GetClientsAsync(ct); }
            catch { Clients = Array.Empty<TetherClient>(); }

            _onSince ??= _now();
            await CheckIdleNoRxAsync(snap, cfg, ct);

            SetState(GuardState.On);
            return;
        }

        _lastState = snap.OperationalState;
        _onSince = null;
        _rxBaseline = null;
        _rxBaselineAt = default;
        _rxIdleWarned = false;
        HealthWarning = null;

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
            LastSuccess = _now();
            if (await WaitForOnAsync(ct))
            {
                _failStreak = 0;
                _nextAttempt = DateTimeOffset.MinValue;
                await RefreshSnapshotAsync(ct);
                SetState(GuardState.On);
            }
            else
            {
                // 8821CU 复位网卡后要 10–30 秒才真正 On：别当场判失败，下一轮巡检接着看。
                SetState(GuardState.Starting, "已发出启动，等待系统就绪");
            }
            return;
        }

        _failStreak++;
        var idx = Math.Min(_failStreak - 1, BackoffSeconds.Length - 1);
        var delay = BackoffSeconds[idx];
        _nextAttempt = now.AddSeconds(delay);
        SetState(GuardState.Backoff, $"连续失败 {_failStreak} 次，{delay}s 后重试");
    }

    /// <summary>等系统真的进 On（启动后立刻读常常还是 InTransition）。</summary>
    private async Task<bool> WaitForOnAsync(CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _startTimeout;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_startTimeout.TotalMilliseconds / 10, 100, 1000));

        while (true)
        {
            try
            {
                var snap = await _tethering.GetSnapshotAsync(ct);
                if (snap.OperationalState == OperationalState.On)
                {
                    LastSnapshot = snap;
                    return true;
                }
            }
            catch
            {
                // 读不到就再等一会
            }

            var remain = deadline - DateTimeOffset.UtcNow;
            if (remain <= TimeSpan.Zero) return false;

            try { await Task.Delay(remain < interval ? remain : interval, ct); }
            catch (OperationCanceledException) { return false; }
        }
    }

    /// <summary>
    /// 8821CU 的 soft-AP「假启动」：状态机 On、适配器 Up、IP 正常，但空口不发信标，
    /// 手机能看到 SSID 却连不上，接收计数恒为 0。这里只负责观测与告警；
    /// 是否自动复位网卡由 hotspot.restartAdapterOnIdleNoRx 决定（默认关，免得空教室整夜复位）。
    /// 判断依据只能是「客户端数 + AP 收发计数」，本机 netsh 扫描看不到自己的 GO，不能拿来判。
    /// </summary>
    private async Task CheckIdleNoRxAsync(TetheringSnapshot snap, HotspotConfig cfg, CancellationToken ct)
    {
        if (snap.ClientCount > 0)
        {
            _rxBaseline = null;
            _rxBaselineAt = default;
            _rxIdleWarned = false;
            HealthWarning = null;
            return;
        }

        var now = _now();
        if (now - _rxSampleAt < RxSampleInterval) return;
        _rxSampleAt = now;

        long? rx;
        try { rx = await _tethering.GetAccessPointReceivedBytesAsync(ct); }
        catch { return; }
        if (rx is null) return;

        if (_rxBaseline is null || rx > _rxBaseline)
        {
            _rxBaseline = rx;
            _rxBaselineAt = now;
            return;
        }

        if (now - _rxBaselineAt < RxIdleThreshold) return;

        var minutes = (now - (_onSince ?? now)).TotalMinutes;
        HealthWarning = $"热点已开 {minutes:F0} 分钟、零客户端，AP 累计收包一直是 {rx} 字节：可能是「假启动」" +
                        "（手机能看到热点却连不上）。判断只看客户端数与 AP 收发计数，不要用本机 netsh 扫描。";

        if (!_rxIdleWarned)
        {
            _rxIdleWarned = true;
            _log.Warn(HealthWarning + " 需要时执行 --restart-wifi 复位网卡。");
        }

        if (!cfg.RestartAdapterOnIdleNoRx) return;
        if (now - _lastAdapterReset < AdapterResetCooldown) return;

        _lastAdapterReset = now;
        _log.Warn("按配置自动复位无线网卡，恢复热点广播");
        var ok = await SafeAsync(() => _tethering.RestartWirelessAdapterAsync(ct), "复位无线网卡");
        if (!ok) return;

        _rxBaseline = null;
        _rxBaselineAt = default;
        _rxIdleWarned = false;
        await SafeAsync(() => _tethering.StartAsync(ct), "复位后重开热点");
        await RefreshSnapshotAsync(ct);
    }

    /// <summary>系统反复把热点配置改回去时提醒一次：多半是 Windows「移动热点」里还开着另一份设置。</summary>
    private void CountDriftFix()
    {
        var now = _now();
        if (now - _driftWindowStart > DriftWarnWindow)
        {
            _driftWindowStart = now;
            _driftFixes = 0;
        }

        _driftFixes++;
        if (_driftFixes == DriftWarnCount)
        {
            _log.Warn("热点配置 10 分钟内被系统改了 3 次：Windows「移动热点」里还有一份设置开着，" +
                      "请把两边的密码/名称改成一致，否则手机连接会时好时坏。");
        }
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
