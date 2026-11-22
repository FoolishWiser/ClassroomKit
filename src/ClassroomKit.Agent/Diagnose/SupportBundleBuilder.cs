using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Support;

namespace ClassroomKit.Agent.Diagnose;

/// <summary>
/// 一键诊断包：自检报告 + 环境 + 脱敏配置摘要 + 在线设备 + 最近 5 个日志。
/// 老师只需要把生成的 zip 发给维护者，不用自己翻 ProgramData。
/// </summary>
public static class SupportBundleBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const int MaxLogBytes = 512 * 1024;

    public static string Build(AgentRuntime rt, DiagReport report)
    {
        var config = rt.Config.Current;
        var folder = OutputFolder();
        var path = Path.Combine(folder, $"ClassroomKit-诊断-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        var entries = new List<KeyValuePair<string, string>>
        {
            new("说明.txt", Explain()),
            new("自检报告.txt", FormatChecks(report)),
            new("自检报告.json", JsonSerializer.Serialize(report, Json)),
            new("环境.txt", EnvironmentText(rt, config)),
            new("配置摘要.json", JsonSerializer.Serialize(ConfigSummary(config), Json)),
            new("在线设备.txt", ClientsText(rt)),
        };

        foreach (var log in RecentLogs())
            entries.Add(new KeyValuePair<string, string>(log.Name, log.Text));

        var secrets = new[] { config.Token, config.Hotspot.Passphrase };
        return SupportBundle.Create(path, entries, secrets.Where(x => !string.IsNullOrWhiteSpace(x))!);
    }

    private static string OutputFolder()
    {
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
                return Path.Combine(desktop, "ClassroomKit-诊断");
        }
        catch { /* 没有桌面目录时落到 ProgramData */ }

        return Path.Combine(Paths.DataDir, "diagnostics");
    }

    private static string Explain() => string.Join(Environment.NewLine, new[]
    {
        "ClassroomKit 诊断包",
        "包含：自检结果、运行环境、配置摘要（不含令牌与热点密码）、在线设备、最近 5 个日志。",
        "所有文本已做脱敏；如果仍担心，打开 zip 确认后再发送。",
        "生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
    });

    private static string FormatChecks(DiagReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ClassroomKit v{report.version}｜{report.generatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"结论：{report.summary}");
        sb.AppendLine();
        foreach (var check in report.checks)
        {
            sb.AppendLine($"[{check.status.ToUpperInvariant()}] {check.title}：{check.detail}");
            if (!string.IsNullOrWhiteSpace(check.next)) sb.AppendLine("    下一步：" + check.next);
        }

        return sb.ToString();
    }

    private static string EnvironmentText(AgentRuntime rt, AppConfig config) => string.Join(Environment.NewLine, new[]
    {
        "ClassroomKit：" + AgentRuntime.DisplayVersion(),
        "配置 schema：" + config.SchemaVersion,
        "操作系统：" + RuntimeInformation.OSDescription,
        "运行时：" + RuntimeInformation.FrameworkDescription,
        "架构：" + RuntimeInformation.ProcessArchitecture,
        "管理员：" + (AdminElevation.IsElevated() ? "是" : "否"),
        "计算机：" + Environment.MachineName,
        "用户：" + Environment.UserName,
        "程序目录：" + AppContext.BaseDirectory,
        "数据目录：" + Paths.DataDir,
        "端口：" + config.Port,
    });

    private static object ConfigSummary(AppConfig config) => new
    {
        config.SchemaVersion,
        config.Port,
        hotspot = new
        {
            config.Hotspot.Enabled,
            config.Hotspot.Ssid,
            config.Hotspot.Band,
            config.Hotspot.TargetClients,
            config.Hotspot.RegistryPeers,
            config.Hotspot.AllowWifiUplink,
            config.Hotspot.GuardIntervalSec,
            config.Hotspot.SelfHealEnabled,
            config.Hotspot.RestartAdapterOnIdleNoRx,
        },
        uplink = config.Uplink,
        boardSave = new
        {
            config.BoardSave.Enabled,
            config.BoardSave.CloseAfterSave,
            config.BoardSave.NameWithLesson,
            config.BoardSave.NameTemplate,
        },
        lesson = new
        {
            config.Lesson.Enabled,
            config.Lesson.OnlyWhenClassInBoard,
            config.Lesson.StaleMinutes,
            config.Lesson.Subjects,
            config.Lesson.SkipBreakNames,
        },
        ui = new { config.Ui.Theme, config.Ui.OnboardingDone, config.Ui.OnboardingStep, config.Ui.OnboardingPaused },
        update = new { config.Update.AutoInstall, config.Update.CheckOnBoot, config.Update.UpdatePlugin },
        conflict = new { config.Conflict.Enabled, config.Conflict.IgnoreProcesses },
        habits = new { config.Habits.Enabled, count = config.Habits.Rules.Count },
    };

    private static string ClientsText(AgentRuntime rt)
    {
        var clients = rt.Guard.Clients;
        if (clients.Count == 0) return "当前没有设备连上热点。";
        var sb = new StringBuilder();
        foreach (var c in clients)
        {
            var duration = c.ConnectedAt is { } since
                ? (DateTimeOffset.Now - since).ToString(@"hh\:mm\:ss")
                : "-";
            sb.AppendLine($"{c.HostName ?? "未命名"}｜{c.Ip ?? "-"}｜{c.Mac}｜已连接 {duration}");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<(string Name, string Text)> RecentLogs()
    {
        try
        {
            if (!Directory.Exists(Paths.LogDir)) return Array.Empty<(string, string)>();
            return Directory.EnumerateFiles(Paths.LogDir, "agent-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5)
                .Select(path => (Path.GetFileName(path), ReadTail(path, MaxLogBytes)))
                .ToArray();
        }
        catch
        {
            return Array.Empty<(string, string)>();
        }
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = (int)Math.Min(stream.Length, maxBytes);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex)
        {
            return "读取日志失败：" + ex.Message;
        }
    }
}