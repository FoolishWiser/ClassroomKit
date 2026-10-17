using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Api;

/// <summary>把 HTTP 请求映射到守护状态 / 配置 / 动作。</summary>
public sealed class ApiServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8"
    };

    private readonly ConfigStore _config;
    private readonly AppLog _log;
    private readonly string _webRoot;
    private readonly string _version;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private HotspotGuard? _guard;

    public ApiServer(ConfigStore config, AppLog log, string webRoot, string version)
    {
        _config = config;
        _log = log;
        _webRoot = webRoot;
        _version = version;
    }

    public string? Token => _config.Current.Token;

    public Func<CancellationToken, Task<(bool Ok, string Message)>>? FixPeersAsync { get; set; }
    public Func<CancellationToken, Task<(bool Ok, string Message)>>? LaunchBcuAsync { get; set; }
    public Action? RequestExit { get; set; }
    public Action<UserSettings>? ApplySettings { get; set; }
    public Action<bool>? ApplyPolicy { get; set; }

    public sealed record UserSettings(string Ssid, string Passphrase, int TargetClients, bool AllowWifiUplink);

    public void AttachGuard(HotspotGuard guard) => _guard = guard;

    public async Task<HttpResponse> HandleAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Method == "OPTIONS") return new HttpResponse { Status = 204 };

            if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(request)) return HttpResponse.Text("unauthorized", 401);
                return await HandleApiAsync(request, ct);
            }

            if (request.Method is not ("GET" or "HEAD")) return HttpResponse.Text("method not allowed", 405);
            return await ServeStaticAsync(request.Path);
        }
        catch (Exception ex)
        {
            _log.Error($"API {request.Method} {request.Path} 异常：{ex.Message}");
            return HttpResponse.Text("internal error", 500);
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var expected = Token;
        if (string.IsNullOrEmpty(expected)) return false;
        var supplied = request.Header("X-Token") ?? request.QueryValue("t");
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task<HttpResponse> HandleApiAsync(HttpRequest request, CancellationToken ct)
    {
        var path = request.Path.TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0) path = "/api";

        switch (path)
        {
            case "/api/state":
                return HttpResponse.Json(JsonSerializer.Serialize(StateDto(), Json));

            case "/api/clients":
                return HttpResponse.Json(JsonSerializer.Serialize(ClientsDto(), Json));

            case "/api/logs":
            {
                var take = 100;
                if (int.TryParse(request.QueryValue("take"), out var t)) take = Math.Clamp(t, 1, 400);
                var lines = _log.Snapshot(take).Select(e => e.Format()).ToArray();
                return HttpResponse.Json(JsonSerializer.Serialize(new { lines }, Json));
            }

            case "/api/health":
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, version = _version }, Json));

            case "/api/policy":
                return await PostAsync(request, body =>
                {
                    var enabled = ReadBool(body, "enabled") ?? throw new ApiException("缺少 enabled");
                    ApplyPolicy?.Invoke(enabled);
                    _log.Info($"Web UI 设置策略：{(enabled ? "开" : "关")}");
                    return new { ok = true, enabled };
                });

            case "/api/settings":
                return await PostAsync(request, body =>
                {
                    var cfg = _config.Current.Hotspot;
                    var ssid = ReadString(body, "ssid") ?? cfg.Ssid;
                    var pass = ReadString(body, "passphrase") ?? cfg.Passphrase;
                    var target = ReadInt(body, "targetClients") ?? cfg.TargetClients;
                    var allowWifi = ReadBool(body, "allowWifiUplink") ?? cfg.AllowWifiUplink;

                    if (ssid.Length is < 1 or > 32) throw new ApiException("SSID 需 1–32 字符");
                    if (pass.Length is < 8 or > 63) throw new ApiException("密码需 8–63 字符");
                    if (target is < 1 or > 64) throw new ApiException("目标容量需 1–64");

                    ApplySettings?.Invoke(new UserSettings(ssid, pass, target, allowWifi));
                    return new { ok = true };
                });

            case "/api/action":
                return await HandleActionAsync(request, ct);

            default:
                return HttpResponse.NotFound();
        }
    }

    /// <summary>把请求体解析成字段表：支持 JSON 对象、x-www-form-urlencoded，以及 ?a=b 查询串。</summary>
    private static Dictionary<string, string> ReadFields(HttpRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Query) fields[kv.Key] = kv.Value;

        var body = request.BodyText.Trim();
        if (body.Length == 0) return fields;

        if (body.StartsWith('{'))
        {
            try
            {
                foreach (var prop in JsonDocument.Parse(body).RootElement.EnumerateObject())
                {
                    fields[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => prop.Value.ToString()
                    };
                }
            }
            catch { /* 非法 JSON 就当没有体 */ }
            return fields;
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = i < 0 ? pair : pair[..i];
            var v = i < 0 ? "" : pair[(i + 1)..];
            fields[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return fields;
    }

    private async Task<HttpResponse> HandleActionAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "POST") return HttpResponse.Text("method not allowed", 405);
        var action = ReadFields(request).TryGetValue("action", out var a) ? a : "";

        switch (action)
        {
            case "start":
                ApplyPolicy?.Invoke(true);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已开启" }, Json));

            case "stop":
                ApplyPolicy?.Invoke(false);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已关闭" }, Json));

            case "ensure":
                ApplyPolicy?.Invoke(true);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已确保开启" }, Json));

            case "fix-peers":
            {
                if (FixPeersAsync is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await FixPeersAsync(ct);
                _log.Info($"修复设备上限：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "bcu":
            {
                if (LaunchBcuAsync is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await LaunchBcuAsync(ct);
                _log.Info($"拉起 BCU：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "exit":
                RequestExit?.Invoke();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action }, Json));

            default:
                return HttpResponse.Text("unknown action", 400);
        }
    }

    private Task<HttpResponse> PostAsync(HttpRequest request, Func<JsonElement, object> work)
    {
        if (request.Method != "POST") return Task.FromResult(HttpResponse.Text("method not allowed", 405));
        var body = JsonSerializer.SerializeToElement(ReadFields(request));

        try
        {
            var result = work(body);
            return Task.FromResult(HttpResponse.Json(JsonSerializer.Serialize(result, Json)));
        }
        catch (ApiException ex)
        {
            return Task.FromResult(HttpResponse.Json(
                JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json), 400));
        }
    }

    private object StateDto()
    {
        var cfg = _config.Current;
        var snap = _guard?.LastSnapshot ?? TetheringSnapshot.Empty;
        return new
        {
            version = _version,
            startedAt = _startedAt,
            uptimeSec = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            enabled = cfg.Hotspot.Enabled,
            guardState = _guard?.State.ToString() ?? "Unknown",
            hotspot = new
            {
                state = snap.OperationalState.ToString(),
                ssid = cfg.Hotspot.Ssid,
                passphrase = cfg.Hotspot.Passphrase,
                band = cfg.Hotspot.Band,
                clients = snap.ClientCount,
                maxClients = snap.MaxClientCount,
                targetClients = cfg.Hotspot.TargetClients,
                registryPeers = cfg.Hotspot.RegistryPeers,
                noConnectionsTimeout = snap.NoConnectionsTimeoutEnabled,
                currentSsid = snap.CurrentSsid
            },
            uplink = new
            {
                available = snap.UplinkAvailable,
                isWifi = snap.UplinkIsWifi,
                name = snap.UplinkName
            },
            capability = snap.Capability.ToString(),
            restartCount = _guard?.RestartCount ?? 0,
            lastSuccess = _guard?.LastSuccess,
            lastError = _guard?.LastError,
            capacityWarning = _guard?.CapabilityWarning,
            bcu = new { enabled = cfg.Bcu.Enabled, path = cfg.Bcu.Path }
        };
    }

    private object ClientsDto()
    {
        var cfg = _config.Current;
        var clients = _guard?.Clients ?? Array.Empty<TetherClient>();
        return new
        {
            targetClients = cfg.Hotspot.TargetClients,
            count = clients.Count,
            clients = clients.Select(c => new
            {
                mac = c.Mac,
                hostName = c.HostName,
                ip = c.Ip,
                connectedAt = c.ConnectedAt,
                uptimeSec = c.Uptime is null ? (int?)null : (int)c.Uptime.Value.TotalSeconds
            }).ToArray()
        };
    }

    private async Task<HttpResponse> ServeStaticAsync(string rawPath)
    {
        var path = rawPath == "/" ? "/index.html" : rawPath;
        if (path.Contains("..", StringComparison.Ordinal)) return HttpResponse.Forbidden();

        var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_webRoot, rel));
        var rootFull = Path.GetFullPath(_webRoot);
        if (!full.StartsWith(rootFull, StringComparison.Ordinal)) return HttpResponse.Forbidden();
        if (!File.Exists(full)) return HttpResponse.NotFound();

        var ext = Path.GetExtension(full);
        var type = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        var data = await File.ReadAllBytesAsync(full);
        return HttpResponse.Bytes(data, type);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? ReadInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static bool? ReadBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "on" or "yes" => true,
                "0" or "false" or "off" or "no" => false,
                _ => (bool?)null
            },
            _ => null
        };
    }

    private sealed class ApiException(string message) : Exception(message);
}
