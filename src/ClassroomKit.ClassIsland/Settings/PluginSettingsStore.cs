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

    /// <summary>
    /// 插件里所有客户端共用一个 HttpClient：ClassIsland 常驻，每次 new 一个客户端就留一批连不上的连接，
    /// 一小时能攒出几千条。超时保持原来单客户端的 6 秒。
    /// </summary>
    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(6) });

    private readonly string _filePath;

    /// <summary>插件的配置目录（日志等文件也放这里）。</summary>
    public string Folder { get; }
    private readonly object _gate = new();
    private PluginSettings _settings;
    private ClassroomKitClient? _client;
    private string _clientKey = "";

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

    /// <summary>按当前设置取客户端；地址和令牌没变就复用同一个实例，令牌留空时回落到守护进程自己的令牌。</summary>
    public ClassroomKitClient CreateClient()
    {
        var settings = Current;
        var token = AgentAccess.ResolveToken(settings);
        // 令牌可能是稍后才从守护配置里读到的：key 变了才重建，别让旧实例一直带着空令牌
        var key = settings.BaseUrl + "|" + token;
        lock (_gate)
        {
            if (_client is null || !string.Equals(_clientKey, key, StringComparison.Ordinal))
            {
                _client = new ClassroomKitClient(settings.BaseUrl, token, SharedHttp.Value);
                _clientKey = key;
            }
            return _client;
        }
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
