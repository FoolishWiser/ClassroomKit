namespace ClassroomKit.Core.Client;

/// <summary>GET /api/state 的强类型视图：只取插件需要的字段，多出来的字段忽略。</summary>
public sealed class AgentState
{
    public string Version { get; set; } = "";
    public int ConfigSchema { get; set; }
    public bool LegacyDefaults { get; set; }
    public bool Enabled { get; set; }
    public string GuardState { get; set; } = "";
    public HotspotState Hotspot { get; set; } = new();
    public UplinkState Uplink { get; set; } = new();
    public string Capability { get; set; } = "";
    public string? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public string? CapacityWarning { get; set; }
}

public sealed class HotspotState
{
    public string State { get; set; } = "";
    public string Ssid { get; set; } = "";
    public string Passphrase { get; set; } = "";
    public string Band { get; set; } = "auto";
    public int Clients { get; set; }
    public int MaxClients { get; set; }
    public int TargetClients { get; set; }
    public bool AllowWifiUplink { get; set; }
    public string? CurrentSsid { get; set; }
}

public sealed class UplinkState
{
    public bool Available { get; set; }
    public bool IsWifi { get; set; }
    public string Name { get; set; } = "";
    public bool HasInternet { get; set; }
    public string Reason { get; set; } = "";
    public string Mode { get; set; } = "auto";
}

/// <summary>POST /api/action 的返回。</summary>
public sealed class ActionReply
{
    public bool Ok { get; set; }
    public string? Action { get; set; }
    public string? Message { get; set; }
    public string? Ssid { get; set; }
    public string? Passphrase { get; set; }
}
