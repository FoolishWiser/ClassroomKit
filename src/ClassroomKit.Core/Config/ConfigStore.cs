using System.Security.Cryptography;
using System.Text.Json;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Config;

public sealed class ConfigStore
{
    private readonly object _gate = new();
    private AppConfig _config;

    public string FilePath { get; }

    public ConfigStore(string filePath)
    {
        FilePath = filePath;
        var loaded = Load(filePath);
        _config = loaded ?? CreateDefault();
        if (loaded is null)
        {
            try { SaveLocked(_config); } catch { /* 无写权限时退化为内存配置 */ }
        }
    }

    public AppConfig Current
    {
        get { lock (_gate) { return _config; } }
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static AppConfig CreateDefault() => new()
    {
        Token = NewToken(),
        Hotspot = new HotspotConfig()
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig? Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return null;
            var json = File.ReadAllText(filePath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg is null) return null;
            cfg.Hotspot ??= new HotspotConfig();
            cfg.Bcu ??= new BcuConfig();
            cfg.Tray ??= new TrayConfig();
            if (string.IsNullOrWhiteSpace(cfg.Token)) cfg.Token = NewToken();
            if (cfg.Port is < 1024 or > 65535) cfg.Port = 36037;
            if (cfg.Hotspot.GuardIntervalSec < 3) cfg.Hotspot.GuardIntervalSec = 3;
            if (cfg.Hotspot.TargetClients < 1) cfg.Hotspot.TargetClients = 10;
            return cfg;
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        string json;
        lock (_gate)
        {
            json = JsonSerializer.Serialize(_config, JsonOptions);
        }
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>原子地修改配置并落盘。</summary>
    public T Update<T>(Func<AppConfig, T> mutator)
    {
        AppConfig snapshot;
        T result;
        lock (_gate)
        {
            result = mutator(_config);
            snapshot = _config;
        }
        lock (_gate) { SaveLocked(snapshot); }
        return result;
    }

    private void SaveLocked(AppConfig cfg)
    {
        var json = JsonSerializer.Serialize(cfg, JsonOptions);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, FilePath, overwrite: true);
    }
}
