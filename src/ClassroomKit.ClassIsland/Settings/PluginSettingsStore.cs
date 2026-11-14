using System.Text.Json;
using ClassroomKit.Core.Client;
using ClassroomKit.ClassIsland.Client;

namespace ClassroomKit.ClassIsland.Settings;

/// <summary>把插件设置落在 ClassIsland 的插件配置目录里。</summary>
public sealed class PluginSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;

    /// <summary>插件的配置目录（日志等文件也放这里）。</summary>
    public string Folder { get; }
    private readonly object _gate = new();
    private PluginSettings _settings;

    public PluginSettingsStore(string pluginConfigFolder)
    {
        Directory.CreateDirectory(pluginConfigFolder);
        Folder = pluginConfigFolder;
        _filePath = Path.Combine(pluginConfigFolder, "settings.json");
        _settings = Load();
    }

    public PluginSettings Current
    {
        get { lock (_gate) return _settings.Clone(); }
    }

    public void Save(PluginSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Clone();
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(tmp, _filePath, overwrite: true);
        }
    }

    /// <summary>按当前设置建一个客户端，令牌留空时回落到守护进程自己的令牌。</summary>
    public ClassroomKitClient CreateClient()
    {
        var settings = Current;
        return new ClassroomKitClient(settings.BaseUrl, AgentAccess.ResolveToken(settings));
    }

    private PluginSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new PluginSettings();
            var parsed = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(_filePath), JsonOptions);
            if (parsed is null) return new PluginSettings();
            if (string.IsNullOrWhiteSpace(parsed.BaseUrl)) parsed.BaseUrl = new PluginSettings().BaseUrl;
            parsed.Normalize();
            return parsed;
        }
        catch
        {
            return new PluginSettings();
        }
    }
}
