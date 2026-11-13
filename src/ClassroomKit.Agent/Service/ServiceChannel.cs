using System.Text.Json;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Hosts;

namespace ClassroomKit.Agent.Service;

public sealed record HostsRequest(string Id, string Action, IReadOnlyList<HostEntry> Entries, DateTimeOffset At);

public sealed record HostsStatus(string Id, string Action, bool Ok, string Message, bool Applied, int Count, DateTimeOffset At);

public sealed record InstallRequest(string Id, string MsiPath, DateTimeOffset At);

public sealed record InstallStatus(string Id, bool Ok, string Message, DateTimeOffset At);

/// <summary>
/// 普通权限的守护 ↔ SYSTEM 护航任务之间的文件信箱。
/// 为什么不用 socket/管道：护航任务每 5 分钟才醒一次，信箱天然容忍「对方不在」，
/// 也不需要为了提权在用户会话里常驻一个高权限进程。
/// </summary>
public static class ServiceChannel
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Dir => Path.Combine(Paths.DataDir, "service");
    public static string HostsRequestPath => Path.Combine(Dir, "hosts.request.json");
    public static string HostsStatusPath => Path.Combine(Dir, "hosts.status.json");
    public static string InstallRequestPath => Path.Combine(Dir, "install.request.json");
    public static string InstallStatusPath => Path.Combine(Dir, "install.status.json");

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public static void Write<T>(string path, T payload)
    {
        Directory.CreateDirectory(Dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(payload, Json));
        File.Move(temp, path, overwrite: true);
    }

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }

    /// <summary>等护航任务把同 id 的结果写回来；超时返回 null。</summary>
    public static async Task<T?> WaitAsync<T>(string path, Func<T, bool> match, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var value = Read<T>(path);
            if (value is not null && match(value)) return value;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return null;
    }
}
