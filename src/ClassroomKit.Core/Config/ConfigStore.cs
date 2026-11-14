using System.Security.Cryptography;
using System.Text.Json;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Hosts;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Update;

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

        var dirty = loaded is null;
        if (loaded is not null && loaded.SchemaVersion < CurrentSchemaVersion)
        {
            MigratedFrom = loaded.SchemaVersion;
            Migrate(loaded);
            dirty = true;
        }

        if (dirty)
        {
            try { SaveLocked(_config); } catch { /* 无写权限时退化为内存配置 */ }
        }
    }

    /// <summary>当前配置结构版本。</summary>
    public const int CurrentSchemaVersion = 5;

    /// <summary>本次加载从哪个结构版本迁移过来；0 表示没有迁移。</summary>
    public int MigratedFrom { get; private set; }

    /// <summary>
    /// 结构迁移：只补字段/盖章，绝不改用户已有值。
    /// v0 → v1 只加 schemaVersion：热点名与密码原样保留，避免升级时把正在上课的机器热点改名。
    /// v2 → v3 只加 lesson（课表联动）与 boardSave 的改名设置，同样不改任何已有值。
    /// v3 → v4 只把「0.3.x 的默认自动安装窗口 02:00-05:00」改成不限（用户自己填过的窗口原样保留）。
    /// v4 → v5 只加 ui 段（桌面窗口尺寸/主题/关窗行为），不改任何已有值。
    /// 改机器名/热点名的动作由用户显式触发（面板按钮 / rename-ssid）。
    /// </summary>
    private static void Migrate(AppConfig cfg)
    {
        if (cfg.SchemaVersion < 0) cfg.SchemaVersion = 0;
        if (cfg.SchemaVersion < 4 && cfg.Update is { } update && update.InstallWindow == "02:00-05:00")
        {
            // 0.3.x 默认把升级锁在凌晨，教室机器一关机就永远等不到；改成不限，用户自填的窗口不动
            update.InstallWindow = "";
        }
        cfg.SchemaVersion = CurrentSchemaVersion;
    }

    public AppConfig Current
    {
        get { lock (_gate) { return _config; } }
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static AppConfig CreateDefault() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
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
            cfg.Uplink ??= new UplinkConfig();
            if (string.IsNullOrWhiteSpace(cfg.Uplink.Mode)) cfg.Uplink.Mode = UplinkSelector.Auto;
            cfg.Bcu ??= new BcuConfig();
            cfg.Update ??= new UpdateConfig();
            if (cfg.Update.Mirrors is null || cfg.Update.Mirrors.Length == 0)
                cfg.Update.Mirrors = new[] { "https://ghproxy.net/" };
            cfg.Update.CheckIntervalHours = Math.Clamp(cfg.Update.CheckIntervalHours, 0, 168);
            cfg.Update.InstallWindow = InstallWindow.Normalize(cfg.Update.InstallWindow);
            cfg.Tray ??= new TrayConfig();
            cfg.BoardSave ??= new BoardSaveConfig();
            if (cfg.BoardSave.LaunchTimeoutSec is < 5 or > 600) cfg.BoardSave.LaunchTimeoutSec = 60;
            if (string.IsNullOrWhiteSpace(cfg.BoardSave.NameTemplate))
                cfg.BoardSave.NameTemplate = BoardFileName.DefaultTemplate;
            cfg.Lesson ??= new LessonConfig();
            cfg.Lesson.Normalize();
            cfg.Ui ??= new UiConfig();
            cfg.Ui.Normalize();
            cfg.Conflict ??= new ConflictConfig();
            if (cfg.Conflict.CooldownMinutes is < 1 or > 1440) cfg.Conflict.CooldownMinutes = 40;
            if (ConflictSnooze.Parse(cfg.Conflict.SnoozeDate) is null) cfg.Conflict.SnoozeDate = "";
            cfg.Conflict.ExtraFullscreenProcesses ??= new List<string>();
            cfg.Hosts ??= new HostsConfig();
            if (cfg.Hosts.Sources is null || cfg.Hosts.Sources.Count == 0)
                cfg.Hosts.Sources = new List<string>(HostsAccelerator.DefaultSources);
            if (cfg.Hosts.RefreshHours is < 1 or > 720) cfg.Hosts.RefreshHours = 24;
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

    /// <summary>原子地修改配置并落盘（不需要返回值时用）。</summary>
    public void Mutate(Action<AppConfig> mutator) => Update(c => { mutator(c); return true; });

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
