using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Core.Http;

public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string? Remote { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    public string? QueryValue(string name) => Query.TryGetValue(name, out var v) ? v : null;
    public string BodyText => Encoding.UTF8.GetString(Body);
}

public sealed class HttpResponse
{
    public int Status { get; set; } = 200;
    public string ContentType { get; set; } = "text/plain; charset=utf-8";
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public bool NoCache { get; set; } = true;
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static HttpResponse Text(string text, int status = 200) => new()
    {
        Status = status,
        ContentType = "text/plain; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(text)
    };

    public static HttpResponse Json(string json, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(json)
    };

    public static HttpResponse Html(string html, int status = 200) => new()
    {
        Status = status,
        ContentType = "text/html; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(html)
    };

    public static HttpResponse Bytes(byte[] data, string contentType, int status = 200) => new()
    {
        Status = status,
        ContentType = contentType,
        Body = data
    };

    public static HttpResponse NotFound(string what = "not found") => Text(what, 404);
    public static HttpResponse Forbidden() => Text("forbidden", 403);
}

/// <summary>
/// 极简 HTTP/1.1 服务：只绑回环、只处理一问一答（Connection: close）。
/// 不用 HttpListener（非管理员要 URL ACL）、不用 Kestrel（体积）。
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1 * 1024 * 1024;

    private readonly int _port;
    private readonly Func<HttpRequest, CancellationToken, Task<HttpResponse>> _handler;
    private readonly AppLog _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public MiniHttpServer(int port, Func<HttpRequest, CancellationToken, Task<HttpResponse>> handler, AppLog log)
    {
        _port = port;
        _handler = handler;
        _log = log;
    }

    public int Port => _port;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _loop = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log.Warn($"接受连接失败：{ex.Message}");
                continue;
            }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            using var stream = client.GetStream();
            // 读请求要快（15 秒），但处理请求可能很久：保存板书要先拉起 ClassInX 再等板书窗口，
            // 最长可见 boardSave.launchTimeoutSec（默认 60 秒），所以处理与回写单独给 5 分钟预算。
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));

            var request = await ReadRequestAsync(stream, readTimeout.Token);
            if (request is null) return;

            using var workTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            workTimeout.CancelAfter(TimeSpan.FromMinutes(5));

            HttpResponse response;
            try
            {
                response = await _handler(request, workTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                response = HttpResponse.Text("处理超时", 504);
            }
            catch (Exception ex)
            {
                _log.Error($"处理 {request.Method} {request.Path} 出错：{ex.Message}");
                response = HttpResponse.Text("internal error", 500);
            }

            await WriteResponseAsync(stream, response, request.Method == "HEAD", workTimeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            _log.Debug($"连接异常：{ex.Message}");
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var raw = new MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) return null;
            raw.Write(buffer, 0, n);
            if (raw.Length > MaxHeaderBytes) return null;
            var arr = raw.GetBuffer();
            headerEnd = IndexOfDoubleCrlf(arr, (int)raw.Length);
        }

        var all = raw.GetBuffer();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0) return null;

        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;
        var method = parts[0].ToUpperInvariant();
        var target = parts[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        string path = target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qIdx = target.IndexOf('?');
        if (qIdx >= 0)
        {
            path = target[..qIdx];
            foreach (var pair in target[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) query[Uri.UnescapeDataString(pair)] = "";
                else query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        var body = Array.Empty<byte>();
        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var parsed))
            contentLength = Math.Clamp(parsed, 0, MaxBodyBytes);

        var already = (int)raw.Length - (headerEnd + 4);
        if (contentLength > 0)
        {
            body = new byte[contentLength];
            var copied = Math.Min(already, contentLength);
            if (copied > 0) Buffer.BlockCopy(all, headerEnd + 4, body, 0, copied);
            var read = copied;
            while (read < contentLength)
            {
                var n = await stream.ReadAsync(body.AsMemory(read, contentLength - read), ct);
                if (n <= 0) break;
                read += n;
            }
            if (read < contentLength) body = body[..read];
        }

        return new HttpRequest
        {
            Method = method,
            Path = path,
            Query = query,
            Headers = headers,
            Body = body
        };
    }

    private static int IndexOfDoubleCrlf(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == 13 && buffer[i + 1] == 10 && buffer[i + 2] == 13 && buffer[i + 3] == 10)
                return i;
        }
        return -1;
    }

    private static string StatusText(int code) => code switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        500 => "Internal Server Error",
        _ => "OK"
    };

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, bool headOnly, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(StatusText(response.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        if (response.NoCache) sb.Append("Cache-Control: no-store\r\n");
        foreach (var (k, v) in response.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(head, ct);
        if (!headOnly && response.Body.Length > 0)
            await stream.WriteAsync(response.Body, ct);
        await stream.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _cts?.Dispose();
    }
}
