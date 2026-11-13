using System.Text.Json;

namespace ClassroomKit.Core.Hosts;

public sealed record HostsCacheEntry(IReadOnlyList<HostEntry> Entries, string Source, DateTimeOffset At);

/// <summary>上次成功拉到的加速记录（断网时复用，避免什么都写不进去）。</summary>
public sealed class HostsCache(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Path { get; } = path;

    public HostsCacheEntry? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var entry = JsonSerializer.Deserialize<HostsCacheEntry>(File.ReadAllText(Path), Json);
            return entry is { Entries.Count: > 0 } ? entry : null;
        }
        catch
        {
            return null;
        }
    }

    public void Save(IReadOnlyList<HostEntry> entries, string source, DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new HostsCacheEntry(entries, source, at), Json));
            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            // 缓存写不进去不影响主流程
        }
    }
}
