using System.Globalization;
using ClassroomKit.Agent.Diagnose;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Ui;

namespace ClassroomKit.Agent.Desktop;

/// <summary>把守护的运行时状态拍成一份只读快照，视图层只认这个。</summary>
internal static class SnapshotBuilder
{
    public static UiSnapshot Build(AgentRuntime rt)
    {
        var config = rt.Config.Current;
        var snap = rt.Guard.LastSnapshot ?? TetheringSnapshot.Empty;
        var lessonState = rt.Lesson.State();
        var lesson = lessonState.Snapshot;
        var update = rt.Update.Status;
        var hosts = rt.Hosts;

        var stateText = rt.Guard.State switch
        {
            GuardState.On => "守护中",
            GuardState.Starting => "正在启动",
            GuardState.Disabled => "已关闭",
            _ => "未知"
        };
        if (rt.IsPaused) stateText = "已暂停";

        var pauseText = rt.IsPaused && rt.PauseRemaining is { } remaining
            ? $"还有 {Math.Max(0, (int)remaining.TotalMinutes)} 分钟自动恢复"
            : "没有暂停";

        return new UiSnapshot(
            AgentRuntime.DisplayVersion(),
            config.SchemaVersion,
            config.Hotspot.Enabled,
            rt.IsPaused,
            pauseText,
            stateText,
            new UiHotspot(
                DescribeOperational(snap.OperationalState),
                config.Hotspot.Ssid,
                config.Hotspot.Passphrase,
                snap.ClientCount,
                config.Hotspot.TargetClients,
                config.Hotspot.RegistryPeers,
                snap.UplinkName ?? "-",
                snap.OperationalState == OperationalState.On),
            new UiLesson(
                lesson is not null,
                LessonStates.Describe(lesson?.State ?? LessonStates.None),
                lesson?.CurrentSubject ?? "-",
                lesson?.NextSubject ?? "-",
                Countdown(lesson),
                lesson?.PreviousSubject ?? rt.Lesson.NameHint(DateTimeOffset.Now) ?? "-"),
            new UiUpdate(
                update.Current,
                update.Latest ?? "-",
                update.PhaseText,
                update.HasUpdate,
                update.Progress,
                update.Notes ?? "",
                update.Error ?? ""),
            new UiHosts(
                config.Hosts.Enabled,
                hosts.Applied,
                hosts.Count,
                hosts.LastError ?? hosts.LastMessage ?? "",
                hosts.ServiceInstalled,
                hosts.ServiceAlive,
                hosts.Writable),
            rt.Log.Snapshot(200).Select(e => e.Format()).ToArray(),
            DateTimeOffset.Now);
    }

    private static string DescribeOperational(OperationalState state) => state switch
    {
        OperationalState.On => "已开启",
        OperationalState.Starting => "正在开启",
        OperationalState.Off => "已关闭",
        _ => "未知"
    };

    private static string Countdown(LessonSnapshot? lesson)
    {
        if (lesson is null || lesson.SecondsToNext <= 0) return "-";
        var span = TimeSpan.FromSeconds(lesson.SecondsToNext);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟后"
            : $"{Math.Max(1, span.Minutes)} 分钟后";
    }
}
