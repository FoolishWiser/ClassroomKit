using ClassroomKit.Core.Config;

namespace ClassroomKit.Core.Model;

public sealed class AppConfig
{
    /// <summary>配置结构版本。旧文件没有这个字段时反序列化为 0，由 ConfigStore 迁移（只盖章，不改已有值）。</summary>
    public int SchemaVersion { get; set; }

    public int Port { get; set; } = 36037;
    public string Token { get; set; } = "";
    public HotspotConfig Hotspot { get; set; } = new();
    public UplinkConfig Uplink { get; set; } = new();
    public BcuConfig Bcu { get; set; } = new();
    public TrayConfig Tray { get; set; } = new();
    public BoardSaveConfig BoardSave { get; set; } = new();
    public ConflictConfig Conflict { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public HostsConfig Hosts { get; set; } = new();
    public string? InstalledVersion { get; set; }
}

public sealed class HotspotConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>默认取计算机名：换教室不用改配置，笔记本漫游也不会跟隔壁班热点撞名。</summary>
    public string Ssid { get; set; } = MachineDefaults.DefaultSsid();
    /// <summary>每台机器随机生成（安装后在面板上能看到）；从旧版升级的机器保留原密码。</summary>
    public string Passphrase { get; set; } = MachineDefaults.DefaultPassphrase();
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

/// <summary>GitHub 访问加速（类似 Watt Toolkit 的 hosts 修改）。</summary>
public sealed class HostsConfig
{
    /// <summary>总开关。开了也只在「直连 GitHub 不通」时才真的改 hosts，通了就不动。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>多久重新取一次加速记录（小时）。</summary>
    public int RefreshHours { get; set; } = 24;
    /// <summary>hosts 源，按顺序尝试。</summary>
    public List<string> Sources { get; set; } = new(ClassroomKit.Core.Hosts.HostsAccelerator.DefaultSources);
}

/// <summary>从 GitHub Releases 检查/下载新版本。</summary>
public sealed class UpdateConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>GitHub 仓库（owner/name）。</summary>
    public string Repo { get; set; } = "FoolishWiser/ClassroomKit";
    /// <summary>是否把预发布版也当成可升级版本。</summary>
    public bool IncludePrerelease { get; set; }

    /// <summary>GitHub 下载被墙/504 时依次尝试的镜像前缀（拼在原始下载地址前面）。</summary>
    public string[] Mirrors { get; set; } = { "https://ghproxy.net/" };
}
