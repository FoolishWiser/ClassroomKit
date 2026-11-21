using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Tests;

/// <summary>可控的假热点后端：用来驱动状态机分支。</summary>
internal sealed class FakeTethering : ITethering
{
    public OperationalState State { get; set; } = OperationalState.Off;
    public bool UplinkAvailable { get; set; } = true;
    public bool UplinkHasInternet { get; set; } = true;
    public bool UplinkIsWifi { get; set; }
    public int MaxClients { get; set; } = 16;
    public int Clients { get; set; }
    public bool? NoConnectionsTimeout { get; set; } = false;
    public string? Ssid { get; set; }
    public bool StartSucceeds { get; set; } = true;
    /// <summary>接下来多少次 StartAsync 故意失败（模拟连续启动失败）。</summary>
    public int StartFailuresRemaining { get; set; }
    public bool ConfigureSucceeds { get; set; } = true;
    public bool ThrowOnSnapshot { get; set; }
    /// <summary>启动后停在 Starting（模拟 8821CU 复位网卡后 10–30 秒才真正 On）。</summary>
    public bool StartLeavesTransition { get; set; }
    public bool RestartAdapterSucceeds { get; set; } = true;
    /// <summary>Wi-Fi Direct 适配器的累计接收字节。</summary>
    public long ApReceivedBytes { get; set; }

    public int StartCalls { get; private set; }
    public int RestartAdapterCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int ConfigureCalls { get; private set; }
    public int TimeoutFixCalls { get; private set; }
    public string? LastSsid { get; private set; }

    public Task<TetheringSnapshot> GetSnapshotAsync(CancellationToken ct)
    {
        if (ThrowOnSnapshot) throw new InvalidOperationException("网卡被占用");
        return Task.FromResult(BuildSnapshot());
    }

    private TetheringSnapshot BuildSnapshot() => new()
    {
        OperationalState = State,
        ClientCount = Clients,
        MaxClientCount = MaxClients,
        NoConnectionsTimeoutEnabled = NoConnectionsTimeout,
        UplinkAvailable = UplinkAvailable,
        UplinkHasInternet = UplinkHasInternet,
        UplinkIsWifi = UplinkIsWifi,
        UplinkName = "以太网",
        Capability = Capability.Enabled,
        CurrentSsid = Ssid
    };

    public Task<bool> StartAsync(CancellationToken ct)
    {
        StartCalls++;
        if (StartFailuresRemaining > 0)
        {
            StartFailuresRemaining--;
            return Task.FromResult(false);
        }

        if (StartSucceeds) State = StartLeavesTransition ? OperationalState.Starting : OperationalState.On;
        return Task.FromResult(StartSucceeds);
    }

    public Task<bool> StopAsync(CancellationToken ct)
    {
        StopCalls++;
        State = OperationalState.Off;
        return Task.FromResult(true);
    }

    public Task<bool> ConfigureAccessPointAsync(string ssid, string passphrase, CancellationToken ct)
    {
        ConfigureCalls++;
        LastSsid = ssid;
        if (ConfigureSucceeds) Ssid = ssid;
        return Task.FromResult(ConfigureSucceeds);
    }

    public Task<bool> EnsureNoConnectionsTimeoutDisabledAsync(CancellationToken ct)
    {
        TimeoutFixCalls++;
        NoConnectionsTimeout = false;
        return Task.FromResult(true);
    }

    public Task<long?> GetAccessPointReceivedBytesAsync(CancellationToken ct) =>
        Task.FromResult<long?>(ApReceivedBytes);

    public Task<bool> RestartWirelessAdapterAsync(CancellationToken ct)
    {
        RestartAdapterCalls++;
        if (RestartAdapterSucceeds) State = OperationalState.Off;
        return Task.FromResult(RestartAdapterSucceeds);
    }

    public Task<IReadOnlyList<TetherClient>> GetClientsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TetherClient>>(
            Enumerable.Range(1, Clients)
                .Select(i => new TetherClient($"AA:BB:CC:00:00:{i:D2}", $"设备{i}", $"192.168.137.{i}", DateTimeOffset.Now))
                .ToArray());
}
