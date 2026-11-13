using System.Diagnostics;
using System.Net.Sockets;

namespace ClassroomKit.Core.Hosts;

public sealed record HostsRefreshResult(
    bool Ok,
    string Message,
    IReadOnlyList<HostEntry> Entries,
    string Source,
    DateTimeOffset At);

/// <summary>
/// 从公开 hosts 源取 GitHub 域名 → IP，按 443 端口实测时延挑最快的一个。
/// 只做「拿候选 + 挑最优」，写盘与校验在 Agent 侧（需要管理员）。
/// </summary>
public sealed class HostsAccelerator
{
    public static readonly IReadOnlyList<string> DefaultSources = new[]
    {
        "https://raw.hellogithub.com/hosts",
        "https://cdn.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts",
        "https://ghproxy.net/https://raw.githubusercontent.com/521xueweihan/GitHub520/main/hosts"
    };

    public static readonly IReadOnlyList<string> DefaultDomains = new[]
    {
        "github.com",
        "api.github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "github.githubassets.com",
        "avatars.githubusercontent.com",
        "camo.githubusercontent.com",
        "gist.github.com",
        "gist.githubusercontent.com",
        "user-images.githubusercontent.com",
        "release-assets.githubusercontent.com"
    };

    private const int ProbeTimeoutMs = 1200;
    private const int MaxCandidatesPerDomain = 4;

    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient _http;
    private readonly Func<string, int, CancellationToken, Task<int>> _probe;

    public HostsAccelerator(HttpClient? http = null, Func<string, int, CancellationToken, Task<int>>? probe = null)
    {
        _http = http ?? SharedHttp;
        _probe = probe ?? TcpProbeAsync;
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassroomKit-Agent (hosts accelerator)");
        return client;
    }

    public async Task<HostsRefreshResult> RefreshAsync(
        IReadOnlyList<string>? sources = null,
        IReadOnlyList<string>? domains = null,
        CancellationToken ct = default)
    {
        var want = domains is { Count: > 0 } ? domains : DefaultDomains;
        var domainSet = new HashSet<string>(want, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        Dictionary<string, List<string>>? candidates = null;
        string? source = null;

        foreach (var url in sources is { Count: > 0 } ? sources : DefaultSources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{url} → HTTP {(int)response.StatusCode}");
                    continue;
                }
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = ParseCandidates(text, domainSet);
                if (parsed.Count == 0)
                {
                    errors.Add($"{url} → 没有可用记录");
                    continue;
                }
                candidates = parsed;
                source = url;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{url} → {ex.Message}");
            }
        }

        if (candidates is null || source is null)
            return new HostsRefreshResult(false, "拉取 hosts 源失败：" + string.Join("；", errors), Array.Empty<HostEntry>(), "", DateTimeOffset.Now);

        var best = await MeasureAsync(candidates, want, ct).ConfigureAwait(false);
        var entries = SelectEntries(want, candidates, best);
        var reachable = best.Count(x => x.Value >= 0);
        var message = $"从 {HostOf(source)} 取到 {entries.Count}/{want.Count} 个域名" +
                      (reachable > 0 ? $"，{reachable} 个候选实测可连" : "，没有候选连得上（按源顺序写入）");
        return new HostsRefreshResult(entries.Count > 0, message, entries, source, DateTimeOffset.Now);
    }

    /// <summary>解析 hosts 源文本，只保留指定域名。域名 → 去重后的 IP 列表。</summary>
    public static Dictionary<string, List<string>> ParseCandidates(string text, IReadOnlyCollection<string> domains)
    {
        var set = new HashSet<string>(domains, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!System.Net.IPAddress.TryParse(tokens[0], out var ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork) continue;

            foreach (var host in tokens.Skip(1))
            {
                if (!set.Contains(host)) continue;
                if (!map.TryGetValue(host, out var list)) map[host] = list = new List<string>();
                if (!list.Contains(tokens[0], StringComparer.OrdinalIgnoreCase)) list.Add(tokens[0]);
            }
        }
        return map;
    }

    /// <summary>每个域名挑时延最低的 IP；一个都不通就退回源里第一条。</summary>
    public static IReadOnlyList<HostEntry> SelectEntries(
        IReadOnlyList<string> domains,
        IReadOnlyDictionary<string, List<string>> candidates,
        IReadOnlyDictionary<string, int> latencyMs)
    {
        var chosen = new List<(string Domain, string Ip)>();
        foreach (var domain in domains)
        {
            if (!candidates.TryGetValue(domain, out var ips) || ips.Count == 0) continue;
            string? pick = null;
            var best = int.MaxValue;
            foreach (var ip in ips)
            {
                if (!latencyMs.TryGetValue(domain + "|" + ip, out var ms)) continue;
                if (ms < 0 || ms >= best) continue;
                best = ms;
                pick = ip;
            }
            chosen.Add((domain, pick ?? ips[0]));
        }

        return chosen
            .GroupBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
            .Select(g => new HostEntry(g.Key, g.Select(x => x.Domain).ToList()))
            .ToList();
    }

    private async Task<Dictionary<string, int>> MeasureAsync(
        Dictionary<string, List<string>> candidates,
        IReadOnlyList<string> domains,
        CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(16);
        var tasks = new List<Task>();

        foreach (var domain in domains)
        {
            if (!candidates.TryGetValue(domain, out var ips)) continue;
            foreach (var ip in ips.Take(MaxCandidatesPerDomain))
            {
                var key = domain + "|" + ip;
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var ms = await _probe(ip, 443, ct).ConfigureAwait(false);
                        lock (result) result[key] = ms;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    private static async Task<int> TcpProbeAsync(string ip, int port, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeoutMs);
            await client.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
            return (int)watch.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
}
