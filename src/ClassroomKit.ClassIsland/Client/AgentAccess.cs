using System.Diagnostics;
using System.Text.Json;
using ClassroomKit.ClassIsland.Settings;
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

    /// <summary>读 %ProgramData%\ClassroomKit\config.json 里的访问令牌（只读，失败就返回 null）。</summary>
    public static string? ReadAgentConfigToken()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ClassroomKit",
                "config.json");
            if (!File.Exists(configPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            return document.RootElement.TryGetProperty("Token", out var token) ? token.GetString() : null;
        }
        catch
        {
            return null;
        }
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

    /// <summary>守护没在跑时，用 --ensure 把它拉起来（不弹 UAC，守护会自己装计划任务）。</summary>
    public static bool TryStartAgent(out string message)
    {
        var exePath = FindAgentExe();
        if (exePath is null)
        {
            message = "没有找到 ClassroomKit 守护程序（C:\\Program Files\\ClassroomKit\\ClassroomKit.Agent.exe）。请先安装 ClassroomKit。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath, "--ensure")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
            });
            message = "已尝试启动 ClassroomKit 守护进程。";
            return true;
        }
        catch (Exception ex)
        {
            message = "启动 ClassroomKit 失败：" + ex.Message;
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
