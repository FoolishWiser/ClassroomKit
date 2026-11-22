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
    UiAdvanced Advanced,
    UiSelfHeal SelfHeal,
    IReadOnlyList<UiClient> ClientList,
    IReadOnlyList<UiLogLine> Logs,
    DateTimeOffset At);

/// <summary>热点在线设备的一行；Duration 由守护按首次连接时间算好。</summary>
public sealed record UiClient(string Name, string Address, string Mac, string Duration);

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
    string NameHint,
    IReadOnlyList<string> Subjects);

public sealed record UiUpdate(
    string Current,
    string Latest,
    string PhaseText,
    bool HasUpdate,
    double Progress,
    string Notes,
    string Error,
    string PluginText,
    string PluginDetail,
    double PluginProgress,
    string PluginError,
    bool PluginCanUpdate,
    /// <summary>UpdateService 的阶段名（Downloading / Ready / ...），界面据此决定是否显示进度条。</summary>
    string Phase);

public sealed record UiHosts(
    bool Enabled,
    bool Applied,
    int Count,
    string Message,
    bool ServiceInstalled,
    bool ServiceAlive,
    bool Writable);

/// <summary>「高级」页：习惯模式开关、当前科目与最近一次自动处理。</summary>
public sealed record UiAdvanced(
    bool HabitsEnabled,
    bool ConflictEnabled,
    string CurrentSubject,
    string LastAction,
    int RuleCount);

/// <summary>「自检与日志」页的自我修复卡片：开关、状态与最近一次自动/手动修复。</summary>
public sealed record UiSelfHeal(
    bool Enabled,
    bool Aggressive,
    string StatusText,
    string Detail,
    int RepairCount);
