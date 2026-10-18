namespace ClassroomKit.Core.Model;

public enum OperationalState
{
    Unknown = 0,
    Off = 1,
    On = 2,
    Starting = 3
}

public enum Capability
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2
}

public sealed record TetherClient(string Mac, string? HostName, string? Ip, DateTimeOffset? ConnectedAt)
{
    public TimeSpan? Uptime => ConnectedAt is null ? null : DateTimeOffset.Now - ConnectedAt.Value;
}

/// <summary>一次热点的完整观测快照。</summary>
public sealed record TetheringSnapshot
{
    public OperationalState OperationalState { get; init; } = OperationalState.Unknown;
    public int ClientCount { get; init; }
    public int MaxClientCount { get; init; }
    public bool? NoConnectionsTimeoutEnabled { get; init; }
    public bool UplinkAvailable { get; init; }
    public bool UplinkIsWifi { get; init; }
    public string? UplinkName { get; init; }
    /// <summary>选中网卡是否真能上外网（NCSI 判定）。</summary>
    public bool UplinkHasInternet { get; init; }
    /// <summary>选中网卡的稳定 Id（面板据此回显下拉框）。</summary>
    public string? UplinkId { get; init; }
    /// <summary>为什么选它 / 为什么没得选。</summary>
    public string? UplinkReason { get; init; }
    public Capability Capability { get; init; } = Capability.Unknown;
    public string? CurrentSsid { get; init; }
    public string? CurrentPassphrase { get; init; }
    public string? Error { get; init; }

    public static TetheringSnapshot Empty { get; } = new();
}
