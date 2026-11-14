using ClassroomKit.Core.Config;
using ClassroomKit.Core.Lesson;

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
    public LessonConfig Lesson { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
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

    /// <summary>保存时按课表科目给文件改名（数学_2026-11-14_10-05）。</summary>
    public bool NameWithLesson { get; set; } = true;

    /// <summary>改名模板，占位符见 <see cref="BoardFileName"/>。</summary>
    public string NameTemplate { get; set; } = BoardFileName.DefaultTemplate;
}

/// <summary>ClassIsland 课表联动：课前提醒保存板书。</summary>
public sealed class LessonConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>上课前多少分钟弹提醒。</summary>
    public int RemindMinutesBefore { get; set; } = 5;

    /// <summary>弹窗停留多少秒。</summary>
    public int DialogSeconds { get; set; } = 30;

    /// <summary>超时动作：save（只保存）/ save-close（保存并关闭）/ dismiss（跳过）/ wait（不自动处理）。</summary>
    public string TimeoutAction { get; set; } = "save";

    /// <summary>点「稍后提醒」后隔多少分钟再弹。</summary>
    public int SnoozeMinutes { get; set; } = 2;

    /// <summary>ClassInX 没在运行时也弹（默认不弹：没板书可存）。</summary>
    public bool OnlyWhenClassInBoard { get; set; } = true;

    /// <summary>课表快照多久没更新就算过期（分钟）。过期不弹窗，面板提示「数据已过期」。</summary>
    public int StaleMinutes { get; set; } = 120;

    /// <summary>
    /// 只在哪些科目上生效（课表里的科目名，如 ["物理"]）。空列表 = 所有科目都生效，也是旧配置没有这个字段时的默认行为。
    /// 匹配规则见 <see cref="LessonSubjects"/>：去空白、忽略大小写，允许「物理」匹配「物理实验」这类包含关系。
    /// 只约束课前提醒（自动保存板书由这个提醒触发，所以也就等于「只听物理课的」）；板书文件名的科目照旧按课表取，不受这里影响。
    /// </summary>
    public List<string> Subjects { get; set; } = new();

    public void Normalize()
    {
        RemindMinutesBefore = Math.Clamp(RemindMinutesBefore, 1, 30);
        DialogSeconds = Math.Clamp(DialogSeconds, 5, 600);
        SnoozeMinutes = Math.Clamp(SnoozeMinutes, 1, 60);
        StaleMinutes = Math.Clamp(StaleMinutes, 1, 1440);
        Subjects = LessonSubjects.Normalize(Subjects);

        var action = (TimeoutAction ?? "").Trim().ToLowerInvariant();
        TimeoutAction = action switch
        {
            "save" => "save",
            "save-close" or "saveclose" => "save-close",
            "dismiss" or "ignore" => "dismiss",
            "wait" => "wait",
            _ => "save"
        };
    }
}

/// <summary>多板书软件全屏冲突提示。</summary>
public sealed class ConflictConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>两次提示之间的冷却（分钟）。</summary>
    public int CooldownMinutes { get; set; } = 40;
    /// <summary>额外当作"全屏应用"的进程名（留空即可，学校可自行补充）。</summary>
    public List<string> ExtraFullscreenProcesses { get; set; } = new();
    /// <summary>「今天不再提醒」的截止日（yyyy-MM-dd）；空 = 不暂停。当天有效，第二天自动恢复。</summary>
    public string SnoozeDate { get; set; } = "";
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

    /// <summary>开机后自动检查一次新版本（开机自检）。</summary>
    public bool CheckOnBoot { get; set; } = true;

    /// <summary>自动检查的间隔（小时）。0 表示只在开机时查一次。</summary>
    public int CheckIntervalHours { get; set; } = 6;

    /// <summary>发现新版本后自动静默安装。默认关：上课中重启热点会断学生网络。</summary>
    public bool AutoInstall { get; set; }

    /// <summary>允许自动安装的时间窗（HH:mm-HH:mm，可跨午夜）。空 = 不限。</summary>
    public string InstallWindow { get; set; } = ClassroomKit.Core.Update.InstallWindow.Default;

    /// <summary>有设备连着热点时也允许自动安装（默认要等没人连着）。</summary>
    public bool InstallWithClients { get; set; }
}

/// <summary>桌面窗口与托盘行为（0.5.0 起；只有桌面端读，守护逻辑不依赖）。</summary>
public sealed class UiConfig
{
    /// <summary>关闭窗口时缩到托盘（false = 关窗即退出）。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>启动时只出托盘，不弹主窗口。</summary>
    public bool StartMinimized { get; set; }

    public int WindowWidth { get; set; } = 1120;
    public int WindowHeight { get; set; } = 760;
    public int WindowLeft { get; set; } = -1;
    public int WindowTop { get; set; } = -1;
    public string LastPage { get; set; } = "overview";

    /// <summary>light | dark | system</summary>
    public string Theme { get; set; } = "system";

    public void Normalize()
    {
        WindowWidth = Math.Clamp(WindowWidth, 960, 3840);
        WindowHeight = Math.Clamp(WindowHeight, 640, 2160);
        if (WindowLeft is < -10000 or > 10000) WindowLeft = -1;
        if (WindowTop is < -10000 or > 10000) WindowTop = -1;
        if (string.IsNullOrWhiteSpace(LastPage)) LastPage = "overview";
        Theme = (Theme ?? "").Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => "system"
        };
    }
}
