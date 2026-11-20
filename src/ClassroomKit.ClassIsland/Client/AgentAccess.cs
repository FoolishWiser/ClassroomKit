using System.Diagnostics;
using ClassroomKit.ClassIsland.Settings;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Client;

namespace ClassroomKit.ClassIsland.Client;

/// <summary>与 ClassroomKit 守护进程打交道的小工具：找令牌、找程序、必要时把守护拉起来。</summary>
public static class AgentAccess
{
    public const string AgentExeName = "ClassroomKit.Agent.exe";

    /// <summary>优先用插件设置里的令牌；没填就去读守护自己的配置文件。</summary>
    public static string ResolveToken(PluginSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Token)) return settings.Token.Trim();
        return ReadAgentConfigToken() ?? "";
    }

    /// <summary>
    /// 读 %ProgramData%\ClassroomKit\config.json 里的访问令牌（只读，失败就返回 null）。
    /// 大小写不敏感的匹配放在 Core 的 AgentConfigReader 里：守护写的是 camelCase 的 token。
    /// </summary>
    public static string? ReadAgentConfigToken() => AgentConfigReader.ReadTokenFromDefaultPath();

    /// <summary>给设置页看的一句话：令牌从哪儿来的。</summary>
    public static string DescribeTokenSource(PluginSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Token)) return "令牌来源：插件设置";
        return ReadAgentConfigToken() is null
            ? "令牌来源：空 —— 课表不会推送。请填令牌，或确认守护已按默认位置安装"
            : "令牌来源：已自动读取守护配置（%ProgramData%\\ClassroomKit\\config.json）";
    }

    /// <summary>找 ClassroomKit.Agent.exe：环境变量 → 默认安装目录。</summary>
    public static string? FindAgentExe()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CLASSROOMKIT_AGENT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment)) return fromEnvironment;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidate = Path.Combine(programFiles, "ClassroomKit", AgentExeName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 守护没在跑时先让计划任务拉起它（计划任务本身就是最高可用权限，不用再弹 UAC）；
    /// 任务不可用时再直接 --ensure。直接启动可能触发一次管理员授权，但比什么都不做好。
    /// </summary>
    public static bool TryStartAgent(out string message)
    {
        var exePath = FindAgentExe();
        if (exePath is null)
        {
            message = "没有找到 ClassroomKit 守护程序（C:\\Program Files\\ClassroomKit\\ClassroomKit.Agent.exe）。请先安装 ClassroomKit。";
            return false;
        }

        if (TryRunScheduledTask())
        {
            message = "已通过计划任务启动 ClassroomKit 守护进程。";
            return true;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath, "--ensure")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            });
            message = "已尝试启动 ClassroomKit 守护进程（可能需要在弹出的窗口里允许管理员权限）。";
            return true;
        }
        catch (Exception ex)
        {
            message = "启动 ClassroomKit 失败：" + ex.Message;
            return false;
        }
    }

    private static bool TryRunScheduledTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/Run /TN \"ClassroomKit-Agent\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { /* 超时不再等 */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>先直接调接口；连不上就拉起守护，再重试几次。</summary>
    public static async Task<(bool Ok, string Message, AgentState? State)> EnsureReachableAsync(
        PluginSettingsStore store,
        int attempts = 6,
        int delayMs = 500,
        CancellationToken cancellationToken = default)
    {
        var client = store.CreateClient();
        var lastError = "ClassroomKit 守护进程没有响应。";

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var (state, error) = await client.TryGetStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is not null) return (true, "ClassroomKit 守护进程已就绪。", state);
            if (!string.IsNullOrWhiteSpace(error)) lastError = error!;

            if (attempt == 0 && !TryStartAgent(out var startMessage))
                return (false, startMessage, null);

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }

        return (false, lastError, null);
    }
}
