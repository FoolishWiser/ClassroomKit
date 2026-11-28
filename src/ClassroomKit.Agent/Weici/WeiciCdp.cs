using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClassroomKit.Agent.Weici;

/// <summary>
/// 极简 Chrome DevTools Protocol 客户端：只做 Runtime.evaluate。
/// 维词是 Electron，用 --remote-debugging-port 打开后就可以在这里注入 automation.js。
/// </summary>
internal sealed class WeiciCdp : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _id;

    public static async Task<string?> ListAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<(string WebSocketUrl, string Url)> Targets(string? json)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("webSocketDebuggerUrl", out var ws)) continue;
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var wsUrl = ws.GetString();
                if (!string.IsNullOrWhiteSpace(wsUrl)) list.Add((wsUrl!, url));
            }
        }
        catch
        {
            // 调试端口返回了半截 JSON：当成没有目标
        }

        return list;
    }

    public async Task ConnectAsync(string url, CancellationToken ct)
        => await _ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);

    public async Task<string> EvalAsync(string expression, bool awaitPromise, int timeoutMs, CancellationToken ct)
    {
        var id = ++_id;
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = "Runtime.evaluate",
            ["params"] = new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = awaitPromise,
                ["userGesture"] = true
            }
        };

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var buffer = new byte[1 << 20];
        var builder = new StringBuilder();
        while (true)
        {
            builder.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token).ConfigureAwait(false);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            var text = builder.ToString();
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || idElement.GetInt32() != id) continue;
                if (root.TryGetProperty("error", out var error))
                    throw new InvalidOperationException("CDP 错误：" + error.GetRawText());
                if (!root.TryGetProperty("result", out var resultElement)) return "";
                if (resultElement.TryGetProperty("exceptionDetails", out var ex))
                    throw new InvalidOperationException("页面脚本错误：" + ex.GetRawText());
                var value = resultElement.TryGetProperty("result", out var inner) ? inner : resultElement;
                if (value.TryGetProperty("value", out var v))
                    return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
                return "";
            }
            catch (JsonException)
            {
                // 不是完整 JSON 就继续收下一帧
            }
        }
    }

    public void Dispose()
    {
        try { _ws.Dispose(); } catch { }
    }
}
