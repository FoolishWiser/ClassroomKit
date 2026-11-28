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

    /// <summary>最近有设备连过的判定窗口：超过这段时间的「零收包」不再自动复位网卡。</summary>
    private static readonly TimeSpan RecentClientWindow = TimeSpan.FromHours(2);
    /// <summary>有设备连着但上行没有外网，持续这么久才尝试自动修复。</summary>
    private static readonly TimeSpan NoInternetGrace = TimeSpan.FromMinutes(2);
    /// <summary>同一次「上行无外网」最多自动修复几次，避免反复打断在线设备。</summary>
    private const int NoInternetRepairLimit = 2;
    /// <summary>自动修复每天最多几次（手动「检查并修复」不受限）。</summary>
    private const int SelfHealMaxPerDay = 3;
    /// <summary>复位网卡后给驱动重新枚举留的时间，太短会读到 Off 或 InTransition。</summary>
    private static readonly TimeSpan ResetSettleDelay = TimeSpan.FromSeconds(10);

    private readonly ITethering _tethering;
    private readonly Func<AppConfig> _config;
    private readonly AppLog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _resetDelay;

    private int _failStreak;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private bool _capWarned;
    private bool _clientsWarned;
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

    private DateTimeOffset _lastSelfHealAt = DateTimeOffset.MinValue;
    private DateOnly _repairDate;
    private int _repairCountToday;
    private DateTimeOffset? _lastClientSeenAt;
    private DateTimeOffset? _noInternetSince;
    private int _noInternetRepairs;
    private bool _noInternetWarned;
    private readonly Dictionary<string, (DateTimeOffset First, DateTimeOffset Last)> _clientSeen =
        new(StringComparer.OrdinalIgnoreCase);

    public HotspotGuard(ITethering tethering, Func<AppConfig> config, AppLog log,
        Func<DateTimeOffset>? now = null, TimeSpan? startTimeout = null, TimeSpan? resetDelay = null)
    {
        _tethering = tethering;
        _config = config;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
        _startTimeout = startTimeout ?? TimeSpan.FromSeconds(30);
        _resetDelay = resetDelay ?? ResetSettleDelay;
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
    /// <summary>客户端列表这次是否读成功；读失败时保留上一次的列表，不能用空表冒充「零客户端」。</summary>
    public bool ClientsKnown { get; private set; } = true;
    /// <summary>最近一次读取客户端失败的原因。</summary>
    public string? LastClientsError { get; private set; }

    /// <summary>最近一次自我修复的时间（自动或手动都算）。</summary>
    public DateTimeOffset? LastRepairAt { get; private set; }
    /// <summary>最近一次自我修复的结果说明，界面与 API 直接显示。</summary>
    public string? LastRepairMessage { get; private set; }
    /// <summary>最近一次自我修复是否成功。</summary>
    public bool LastRepairOk { get; private set; }
    /// <summary>自我修复累计次数（含手动），自检页用于说明「修过几次」。</summary>
    public int RepairCount { get; private set; }

    /// <summary>
    /// 给每个客户端记住第一次出现的时间：系统 API 只给当前连接，arp 每轮都会把 ConnectedAt 刷成现在，
    /// 界面上的「已连接」就会永远显示「刚刚」。这里按 MAC 维护首见时间，设备离开 2 小时后再忘掉。
    /// </summary>
    private IReadOnlyList<TetherClient> StabilizeClients(IReadOnlyList<TetherClient> raw, DateTimeOffset now)
    {
        foreach (var client in raw)
        {
            var key = string.IsNullOrWhiteSpace(client.Mac) ? "-" : client.Mac.Trim();
            _clientSeen[key] = _clientSeen.TryGetValue(key, out var seen) ? (seen.First, now) : (now, now);
        }

        foreach (var stale in _clientSeen.Where(kv => now - kv.Value.Last > TimeSpan.FromHours(2)).ToArray())
            _clientSeen.Remove(stale.Key);

        return raw.Select(client =>
        {
            var key = string.IsNullOrWhiteSpace(client.Mac) ? "-" : client.Mac.Trim();
            var first = _clientSeen.TryGetValue(key, out var seen) ? seen.First : now;
            return client with { ConnectedAt = first };
        }).ToArray();
    }

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

            try
            {
                Clients = StabilizeClients(await _tethering.GetClientsAsync(ct), _now());
                ClientsKnown = true;
                LastClientsError = null;
                _clientsWarned = false;
            }
            catch (Exception ex)
            {
                // 读失败不代表没人：保留上一次的列表，只把「未知」标出来，避免升级逻辑误判教室空了。
                ClientsKnown = false;
                LastClientsError = ex.Message;
                if (!_clientsWarned)
                {
                    _clientsWarned = true;
                    _log.Warn("读取在线设备失败，已保留上一次的列表：" + ex.Message);
                }
            }

            _onSince ??= _now();
            await CheckIdleNoRxAsync(snap, cfg, ct);
            await CheckConnectivityAsync(snap, cfg, ct);

            // 自我修复可能已经把热点复位到 Off/Starting：按修复后的真实快照报状态，别再无条件标 On。
            if (LastSnapshot?.OperationalState == OperationalState.On)
                SetState(GuardState.On);
            else
                SetState(GuardState.Starting, "自我修复后等待系统就绪");
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
        if (cfg.SelfHealEnabled && _failStreak >= 2)
        {
            var healed = await SelfHealAsync($"热点连续启动失败 {_failStreak} 次", resetAdapter: true, force: false, ct);
            if (healed)
            {
                _failStreak = 0;
                _nextAttempt = DateTimeOffset.MinValue;
                return;
            }
        }

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
    /// 自动复位条件见 <see cref="HotspotConfig.SelfHealEnabled"/> 与 <see cref="HotspotConfig.RestartAdapterOnIdleNoRx"/>：
    /// 默认只在最近 2 小时有设备连过时自动复位，空教室整夜零客户端不动网卡。
    /// 判断依据只能是「客户端数 + AP 收发计数」，本机 netsh 扫描看不到自己的 GO，不能拿来判。
    /// </summary>
    private async Task CheckIdleNoRxAsync(TetheringSnapshot snap, HotspotConfig cfg, CancellationToken ct)
    {
        var now = _now();
        if (snap.ClientCount > 0)
        {
            _lastClientSeenAt = now;
            _rxBaseline = null;
            _rxBaselineAt = default;
            _rxIdleWarned = false;
            HealthWarning = null;
            return;
        }

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

        var recentClient = _lastClientSeenAt is { } seen && now - seen <= RecentClientWindow;
        if (!cfg.RestartAdapterOnIdleNoRx && !(cfg.SelfHealEnabled && recentClient)) return;
        if (now - _lastAdapterReset < AdapterResetCooldown) return;

        if (await SelfHealAsync("疑似假启动（零客户端、AP 收包不涨）", resetAdapter: true, force: false, ct))
        {
            _rxBaseline = null;
            _rxBaselineAt = default;
            _rxIdleWarned = false;
        }
    }

    /// <summary>
    /// 有设备连着、上行也连着，但系统报告没有外网：Wi-Fi 上行时多半是无线链路假死，
    /// 复位无线网卡并重开热点；有线上行时复位网卡无济于事，只告警不动作。
    /// 必须等 <see cref="NoInternetGrace"/> 持续成立才动手，避免 DHCP/认证的瞬时抖动。
    /// </summary>
    private async Task CheckConnectivityAsync(TetheringSnapshot snap, HotspotConfig cfg, CancellationToken ct)
    {
        if (!snap.UplinkAvailable || snap.UplinkHasInternet)
        {
            _noInternetSince = null;
            _noInternetRepairs = 0;
            _noInternetWarned = false;
            return;
        }

        if (snap.ClientCount == 0)
        {
            // 没有设备连着就先不计时：等下一台设备连上来，再从头算 2 分钟宽限。
            _noInternetSince = null;
            _noInternetWarned = false;
            return;
        }

        var now = _now();
        _noInternetSince ??= now;
        if (now - _noInternetSince < NoInternetGrace) return;

        var warning = "设备已连接热点，但上行网络没有外网。";
        if (!cfg.SelfHealEnabled)
        {
            if (!_noInternetWarned)
            {
                _noInternetWarned = true;
                _log.Warn(warning + "自动修复已关闭，可在自检页点「检查并修复」。");
            }
            return;
        }

        if (!snap.UplinkIsWifi)
        {
            if (!_noInternetWarned)
            {
                _noInternetWarned = true;
                _log.Warn(warning + "上行是有线网络，复位无线网卡不会恢复外网，请检查网线或学校网络。");
            }
            return;
        }

        if (_noInternetRepairs >= NoInternetRepairLimit)
        {
            if (!_noInternetWarned)
            {
                _noInternetWarned = true;
                _log.Warn(warning + $"已自动修复 {_noInternetRepairs} 次仍无外网，停止自动复位，请检查学校网络。");
            }
            return;
        }

        var before = RepairCount;
        var ok = await SelfHealAsync("有设备连着但上行没有外网", resetAdapter: true, force: false, ct);
        if (RepairCount > before) _noInternetRepairs++;
        if (ok)
        {
            _noInternetSince = null;
            _noInternetWarned = false;
        }
    }

    /// <summary>
    /// 统一的自我修复动作：复位无线网卡 → 等驱动重新枚举 → 覆盖热点配置 → 重开热点 → 等 On。
    /// 自动修复受 15 分钟冷却与每天 3 次上限约束；手动 <see cref="RepairNowAsync"/> 用 force 绕过。
    /// </summary>
    private async Task<bool> SelfHealAsync(string reason, bool resetAdapter, bool force, CancellationToken ct)
    {
        var cfg = _config().Hotspot;
        var now = _now();

        if (!force)
        {
            if (now - _lastSelfHealAt < AdapterResetCooldown)
            {
                LastRepairMessage = "距离上次自动修复不到 15 分钟，先观察";
                return false;
            }

            var today = DateOnly.FromDateTime(now.LocalDateTime);
            if (_repairDate != today)
            {
                _repairDate = today;
                _repairCountToday = 0;
            }

            if (_repairCountToday >= SelfHealMaxPerDay)
            {
                LastRepairMessage = "今天已经自动修复 3 次，暂停到明天";
                return false;
            }

            _repairCountToday++;
        }

        _lastSelfHealAt = now;
        LastRepairAt = now;
        LastRepairOk = false;
        RepairCount++;
        _log.Warn($"自我修复：{reason}（复位网卡：{(resetAdapter ? "是" : "否")}）");

        if (resetAdapter)
        {
            _lastAdapterReset = now;
            if (!await SafeAsync(() => _tethering.RestartWirelessAdapterAsync(ct), "自我修复：复位无线网卡"))
            {
                LastRepairMessage = "复位无线网卡失败";
                return false;
            }

            if (_resetDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(_resetDelay, ct); }
                catch (OperationCanceledException)
                {
                    LastRepairMessage = "自我修复被取消";
                    return false;
                }
            }
        }

        if (!await SafeAsync(() => _tethering.ConfigureAccessPointAsync(cfg.Ssid, cfg.Passphrase, ct),
                "自我修复：覆盖热点配置"))
        {
            LastRepairMessage = "覆盖热点配置失败";
            await RefreshSnapshotAsync(ct);
            return false;
        }

        if (!await SafeAsync(() => _tethering.StartAsync(ct), "自我修复：重开热点"))
        {
            LastRepairMessage = "重开热点失败";
            await RefreshSnapshotAsync(ct);
            return false;
        }

        var on = await WaitForOnAsync(ct);
        await RefreshSnapshotAsync(ct);
        LastRepairOk = on;
        LastRepairMessage = on
            ? $"{reason}：已复位网卡并重开热点"
            : $"{reason}：已发出重开，等待系统就绪";
        _log.Info("自我修复结果：" + LastRepairMessage);

        if (on)
        {
            _failStreak = 0;
            _nextAttempt = DateTimeOffset.MinValue;
            _rxBaseline = null;
            _rxBaselineAt = default;
            _rxIdleWarned = false;
            HealthWarning = null;
            SetState(GuardState.On, "自我修复完成");
        }

        return on;
    }

    /// <summary>
    /// 自检页「检查并修复」：先判当前网络是否真的有问题，再决定复位。
    /// 手动操作不受冷却/每日上限约束，但仍遵守「有线无外网不瞎复位」。
    /// </summary>
    public async Task<(bool Ok, string Message)> RepairNowAsync(CancellationToken ct)
    {
        var cfg = _config().Hotspot;
        if (!cfg.Enabled)
            return (false, "热点策略是关闭状态：先开启热点，再检查网络。");

        TetheringSnapshot snap;
        try { snap = await _tethering.GetSnapshotAsync(ct); }
        catch (Exception ex) { return (false, "读不到热点状态：" + ex.Message); }

        LastSnapshot = snap;
        if (!snap.UplinkAvailable)
            return (false, "上行网络未连接：先接好网线或连上 Wi-Fi。");
        if (!snap.UplinkHasInternet && !snap.UplinkIsWifi)
            return (false, "上行是有线网络且没有外网，复位无线网卡无济于事；请检查网线或学校网络。");
        if (snap.OperationalState == OperationalState.On && snap.UplinkHasInternet && HealthWarning is null)
            return (true, "网络看起来正常，不需要修复。");

        var reason = snap.OperationalState != OperationalState.On
            ? "手动修复：热点没有正常开启"
            : HealthWarning is not null
                ? "手动修复：疑似假启动"
                : "手动修复：有设备但上行没有外网";
        var ok = await SelfHealAsync(reason, resetAdapter: true, force: true, ct);
        return (ok, LastRepairMessage ?? "修复未完成");
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
