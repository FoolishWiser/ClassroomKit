namespace ClassroomKit.Core.Ui;

/// <summary>桌面端每个刷新周期的只读快照：ViewModel 只读它，不在 UI 线程碰热点/WMI。</summary>
public sealed record UiSnapshot(
    string Version,
    int Schema,
    bool Enabled,
    bool Paused,
    string PauseText,
    string GuardStateText,
    UiHotspot Hotspot,
    UiLesson Lesson,
    UiUpdate Update,
    UiHosts Hosts,
    IReadOnlyList<string> LogLines,
    DateTimeOffset At);

public sealed record UiHotspot(
    string StateText,
    string Ssid,
    string Passphrase,
    int Clients,
    int TargetClients,
    int MaxPeers,
    string UplinkName,
    bool On);

public sealed record UiLesson(
    bool Available,
    string StateText,
    string Current,
    string Next,
    string Countdown,
    string NameHint);

public sealed record UiUpdate(
    string Current,
    string Latest,
    string PhaseText,
    bool HasUpdate,
    double Progress,
    string Notes,
    string Error);

public sealed record UiHosts(
    bool Enabled,
    bool Applied,
    int Count,
    string Message,
    bool ServiceInstalled,
    bool ServiceAlive,
    bool Writable);
