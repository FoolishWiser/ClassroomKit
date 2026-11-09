namespace ClassroomKit.Core.Model;

public sealed class AppConfig
{
    public int Port { get; set; } = 36037;
    public string Token { get; set; } = "";
    public HotspotConfig Hotspot { get; set; } = new();
    public UplinkConfig Uplink { get; set; } = new();
    public BcuConfig Bcu { get; set; } = new();
    public TrayConfig Tray { get; set; } = new();
    public BoardSaveConfig BoardSave { get; set; } = new();
    public ConflictConfig Conflict { get; set; } = new();
    public string? InstalledVersion { get; set; }
}

public sealed class HotspotConfig
{
    public bool Enabled { get; set; } = true;
    public string Ssid { get; set; } = "CLASSROOM-302";
    public string Passphrase { get; set; } = "80258025";
    /// <summary>auto | 2.4GHz | 5GHz</summary>
    public string Band { get; set; } = "auto";
    /// <summary>设计容量：UI 上显示的“在线 x/目标”。</summary>
    public int TargetClients { get; set; } = 10;
    /// <summary>注册表 WifiMaxPeers 期望值，安装时写入。</summary>
    public int RegistryPeers { get; set; } = 16;
    public bool AllowWifiUplink { get; set; }
    public int GuardIntervalSec { get; set; } = 15;
    public bool AutoStartOnLaunch { get; set; } = true;
}

public sealed class UplinkConfig
{
    /// <summary>auto（按外网自动挑）或指定网卡的 Id / 名称 / 描述片段。</summary>
    public string Mode { get; set; } = "auto";
    /// <summary>所有网卡都没有外网时，仍然用局域网网卡开热点（学生能访问本机，只是不能上网）。</summary>
    public bool FallbackToLan { get; set; } = true;
}

public sealed class BcuConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>BCUninstaller 主程序路径；为空表示自动探测。</summary>
    public string Path { get; set; } = "";
}

public sealed class TrayConfig
{
    public bool Enabled { get; set; } = true;
}

/// <summary>托盘「保存 ClassInX 板书并关闭 ClassInX」。</summary>
public sealed class BoardSaveConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>保存成功后是否顺带关掉 ClassInX。</summary>
    public bool CloseAfterSave { get; set; } = true;
    /// <summary>ClassInX 没在运行时，自动把它拉起来再保存（学校一体机上经常没开）。</summary>
    public bool LaunchIfNeeded { get; set; } = true;
    /// <summary>拉起 ClassInX 之后，等板书窗口出现的最长秒数。</summary>
    public int LaunchTimeoutSec { get; set; } = 60;
    /// <summary>手工指定 ClassIn X.exe 的完整路径；留空则自动查找。</summary>
    public string? ClassInPath { get; set; }
}

/// <summary>多板书软件全屏冲突提示。</summary>
public sealed class ConflictConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>两次提示之间的冷却（分钟）。</summary>
    public int CooldownMinutes { get; set; } = 40;
    /// <summary>额外当作"全屏应用"的进程名（留空即可，学校可自行补充）。</summary>
    public List<string> ExtraFullscreenProcesses { get; set; } = new();
}
