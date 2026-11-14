using System.Text;
using System.Text.Json;
using ClassroomKit.Core.Lesson;

namespace ClassroomKit.Core.Client;

/// <summary>调用本机 ClassroomKit.Agent 的 HTTP API。插件与外部工具共用这一个客户端，避免各写一份协议。</summary>
public sealed class ClassroomKitClient : IDisposable
{
    public const string DefaultBaseUrl = "http://127.0.0.1:36037";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ClassroomKitClient(string? baseUrl = null, string? token = null, HttpClient? http = null)
    {
        BaseUrl = Normalize(baseUrl);
        Token = token;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    }

    public string BaseUrl { get; }

    /// <summary>面板会把令牌写进 URL；插件设置页把它存到自己的配置里。</summary>
    public string? Token { get; set; }

    /// <summary>拉一次完整状态。守护没启动时抛 <see cref="ClassroomKitClientException"/>。</summary>
    public async Task<AgentState> GetStateAsync(CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "/api/state", null, ct).ConfigureAwait(false);
        return await ReadAsync<AgentState>(res, ct).ConfigureAwait(false);
    }

    /// <summary>给 UI 用的软版本：失败返回 null 与错误文案，不抛异常。</summary>
    public async Task<(AgentState? State, string? Error)> TryGetStateAsync(CancellationToken ct = default)
    {
        try
        {
            return (await GetStateAsync(ct).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ClassroomKitClientException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>触发一个动作（start / stop / ensure / save-classin-board / rename-ssid …）。</summary>
    public async Task<ActionReply> PostActionAsync(string action, IDictionary<string, string?>? fields = null,
        CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string?>(fields ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
        {
            ["action"] = action
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await SendAsync(HttpMethod.Post, "/api/action", content, ct).ConfigureAwait(false);
        return await ReadAsync<ActionReply>(res, ct).ConfigureAwait(false);
    }

    /// <summary>读守护记着的课表状态（ClassIsland 插件推上来的那一份）。</summary>
    public async Task<LessonView> GetLessonAsync(CancellationToken ct = default)
    {
        using var res = await SendAsync(HttpMethod.Get, "/api/lesson", null, ct).ConfigureAwait(false);
        return await ReadAsync<LessonView>(res, ct).ConfigureAwait(false);
    }

    /// <summary>把 ClassIsland 的课表快照推给守护（每节课状态变化时调，别太频繁）。</summary>
    public async Task<ActionReply> PushLessonAsync(LessonSnapshot snapshot, CancellationToken ct = default)
    {
        using var content = new StringContent(JsonSerializer.Serialize(snapshot, Json), Encoding.UTF8, "application/json");
        using var res = await SendAsync(HttpMethod.Post, "/api/lesson", content, ct).ConfigureAwait(false);
        return await ReadAsync<ActionReply>(res, ct).ConfigureAwait(false);
    }

    /// <summary>插件退出或课表被关掉时告诉守护「课表没了」，别再按旧数据提醒。</summary>
    public async Task<ActionReply> ClearLessonAsync(CancellationToken ct = default)
    {
        using var content = new StringContent("{\"action\":\"clear\"}", Encoding.UTF8, "application/json");
        using var res = await SendAsync(HttpMethod.Post, "/api/lesson", content, ct).ConfigureAwait(false);
        return await ReadAsync<ActionReply>(res, ct).ConfigureAwait(false);
    }

    /// <summary>设置页“测试连接”按钮：能拿到 /api/state 就算通。</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await GetStateAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + path + TokenQuery()) { Content = content };
        if (!string.IsNullOrWhiteSpace(Token))
            request.Headers.Add("X-Token", Token.Trim());

        return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    private string TokenQuery() =>
        string.IsNullOrWhiteSpace(Token) ? "" : "?t=" + Uri.EscapeDataString(Token.Trim());

    private static async Task<T> ReadAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var detail = body.Length > 240 ? body[..240] + "…" : body;
            throw new ClassroomKitClientException((int)res.StatusCode,
                $"HTTP {(int)res.StatusCode}：{(string.IsNullOrWhiteSpace(detail) ? res.ReasonPhrase : detail)}");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            if (value is null) throw new ClassroomKitClientException((int)res.StatusCode, "响应为空");
            return value;
        }
        catch (JsonException ex)
        {
            throw new ClassroomKitClientException((int)res.StatusCode, "响应不是合法 JSON：" + ex.Message);
        }
    }

    private static string Normalize(string? baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) return DefaultBaseUrl;
        if (!url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        return url;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

/// <summary>守护没启动、令牌不对、或者返回体不是预期 JSON 时抛出。</summary>
public sealed class ClassroomKitClientException : Exception
{
    public ClassroomKitClientException(int statusCode, string message) : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
}
