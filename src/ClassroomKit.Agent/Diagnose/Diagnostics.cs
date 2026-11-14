using ClassroomKit.Agent.Board;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Update;

namespace ClassroomKit.Agent.Diagnose;

/// <summary>
/// 一键自检：把「老师最可能踩的坑」逐条跑一遍，给出 ok/warn/fail 与下一步。
/// 面板和命令行都读这份报告，不写日志、不改配置。
/// </summary>
public static class Diagnostics
{
    public static object Build(AgentRuntime rt)
    {
        var config = rt.Config.Current;
        var snap = rt.Guard.LastSnapshot ?? TetheringSnapshot.Empty;
        var checks = new List<object>();

        void Add(string id, string title, string status, string detail, string? next = null) =>
            checks.Add(new { id, title, status, detail, next });

        var version = AgentRuntime.DisplayVersion();
        Add("version", "程序版本", "ok", $"v{version}｜配置 schema {config.SchemaVersion}");

        // 热点
        if (!config.Hotspot.Enabled)
            Add("hotspot", "热点", "warn", "策略是关闭状态", "在面板点「开启」或执行 --ensure");
        else if (snap.Capability == Capability.Disabled)
            Add("hotspot", "热点", "fail", "这块无线网卡不支持承载网络", "换支持 Wi-Fi Direct 的网卡，或用随身 Wi-Fi");
        else if (snap.OperationalState is OperationalState.Off or OperationalState.Unknown)
            Add("hotspot", "热点", "warn", $"热点状态：{snap.OperationalState}", "等下一轮巡检，或点面板的「立即修复」");
        else
            Add("hotspot", "热点", "ok", $"{snap.OperationalState}｜SSID {config.Hotspot.Ssid}｜在线 {snap.ClientCount}/{config.Hotspot.TargetClients}");

        // 上行
        Add("uplink", "上行网卡", snap.UplinkAvailable ? "ok" : "fail",
            snap.UplinkAvailable
                ? $"{snap.UplinkName ?? "未知"}（{(snap.UplinkIsWifi ? "Wi-Fi" : "有线")}）｜联网：{(snap.UplinkHasInternet ? "是" : "否")}"
                : "没有找到可用的上行网卡：" + (snap.UplinkReason ?? "原因未知"),
            snap.UplinkAvailable ? null : "插好网线或连上学校 Wi-Fi，再点「立即修复」");

        // 设备上限
        var peers = config.Hotspot.RegistryPeers;
        Add("peers", "设备上限", peers >= 16 ? "ok" : "warn",
            $"配置上限 {peers} 台（Windows 默认 8 台）",
            peers >= 16 ? null : "点面板「突破 8 台限制」或执行 --fix-peers");

        // 登录自启
        var loginTask = ScheduledTaskInstaller.IsInstalled();
        Add("autostart", "登录自启", loginTask ? "ok" : "warn",
            loginTask ? "计划任务 ClassroomKit-Agent 已注册（登录后 8 秒启动）" : "没有找到登录自启任务",
            loginTask ? null : "用管理员运行一次 ClassroomKit.Agent.exe --install-task");

        // 护航任务（hosts / 静默升级）
        var serviceTask = ScheduledTaskInstaller.IsServiceInstalled();
        Add("service", "系统护航任务", serviceTask ? "ok" : "warn",
            serviceTask ? "ClassroomKit-Service 已注册（SYSTEM，每 5 分钟自检）" : "没有找到护航任务：hosts 加速与静默升级会退化成手动",
            serviceTask ? null : "用管理员运行一次 ClassroomKit.Agent.exe --install-service");

        // hosts
        var hosts = rt.Hosts.StatusDto();
        var hostsType = hosts.GetType();
        string HostsValue(string name) => hostsType.GetProperty(name)?.GetValue(hosts)?.ToString() ?? "";
        var hostsApplied = HostsValue("applied") == "True";
        var hostsWritable = HostsValue("writable") == "True";
        Add("hosts", "GitHub 访问加速", hostsApplied ? "ok" : (hostsWritable ? "warn" : "warn"),
            $"已生效：{(hostsApplied ? "是" : "否")}｜hosts 可写：{(hostsWritable ? "是" : "否")}｜{HostsValue("lastMessage")}",
            hostsApplied ? null : "网络慢时在面板点「刷新加速记录」");

        // 板书保存
        var saveFolder = ClassInBoardSaver.SaveFolder;
        var folderOk = Directory.Exists(saveFolder);
        Add("board-folder", "板书保存目录", folderOk ? "ok" : "warn",
            folderOk ? saveFolder : $"还没有这个目录：{saveFolder}（第一次保存板书后会自动创建）");
        Add("classin", "ClassInX", ClassInBoardSaver.IsClassInRunning() ? "ok" : "warn",
            ClassInBoardSaver.IsClassInRunning() ? "正在运行" : "没有运行",
            ClassInBoardSaver.IsClassInRunning() ? null : "上课前从桌面或开始菜单打开 ClassInX");

        // 课表联动
        var lessonAvailable = rt.Lesson.State().Snapshot is not null;
        var decision = rt.Lesson.Evaluate(DateTimeOffset.Now);
        Add("lesson", "ClassIsland 课表联动", lessonAvailable ? "ok" : "warn",
            lessonAvailable
                ? $"{LessonStates.Describe(rt.Lesson.State().Snapshot?.State)}｜下一节 {rt.Lesson.State().Snapshot?.NextSubject ?? "-"}｜提醒判定：{decision.Reason}"
                : "守护还没收到课表数据",
            lessonAvailable ? null : "确认 ClassIsland 已启动、装好 ClassroomKit 插件，并在插件设置里填对令牌");

        // 升级
        Add("update", "升级", "ok",
            $"开机检查：{(config.Update.CheckOnBoot ? "开" : "关")}｜自动安装：{(config.Update.AutoInstall ? "开" : "关")}"
            + $"｜时间窗：{InstallWindow.Describe(config.Update.InstallWindow)}");

        // 配置默认值
        if (MachineDefaults.IsLegacy(config.Hotspot.Ssid, config.Hotspot.Passphrase))
            Add("legacy-ssid", "热点名还是旧默认值", "warn",
                $"当前 SSID {config.Hotspot.Ssid} 是所有机器共用的旧默认值，容易和隔壁教室串台",
                "点面板「改用电脑名」，或 POST /api/action action=rename-ssid");

        var fail = checks.Count(c => c.GetType().GetProperty("status")?.GetValue(c)?.ToString() == "fail");
        var warn = checks.Count(c => c.GetType().GetProperty("status")?.GetValue(c)?.ToString() == "warn");

        return new
        {
            ok = fail == 0,
            version,
            generatedAt = DateTimeOffset.Now,
            summary = fail == 0 ? $"没有致命问题（{warn} 项建议）" : $"{fail} 项需要处理，{warn} 项建议",
            checks
        };
    }
}
