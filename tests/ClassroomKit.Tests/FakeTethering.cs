using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Tests;

/// <summary>可控的假热点后端：用来驱动状态机分支。</summary>
internal sealed class FakeTethering : ITethering
{
    public OperationalState State { get; set; } = OperationalState.Off;
    public bool UplinkAvailable { get; set; } = true;
    public bool UplinkIsWifi { get; set; }
    public int MaxClients { get; set; } = 16;
    public int Clients { get; set; }
    public bool? NoConnectionsTimeout { get; set; } = false;
    public string? Ssid { get; set; }
    public bool StartSucceeds { get; set; } = true;
    public bool ConfigureSucceeds { get; set; } = true;
    public bool ThrowOnSnapshot { get; set; }

    public int StartCalls { get; private set; }
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
        UplinkIsWifi = UplinkIsWifi,
        UplinkName = "以太网",
        Capability = Capability.Enabled,
        CurrentSsid = Ssid
    };

    public Task<bool> StartAsync(CancellationToken ct)
    {
        StartCalls++;
        if (StartSucceeds) State = OperationalState.On;
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

    public Task<IReadOnlyList<TetherClient>> GetClientsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TetherClient>>(
            Enumerable.Range(1, Clients)
                .Select(i => new TetherClient($"AA:BB:CC:00:00:{i:D2}", $"设备{i}", $"192.168.137.{i}", DateTimeOffset.Now))
                .ToArray());
}
