using ClassroomKit.Agent.Service;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Hosts;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Hosts;

/// <summary>
/// GitHub 访问护航：只在「直连不通」时才往 hosts 里写加速记录；写完全程校验，还是不通就自动还原。
/// 写盘要么本进程有权限（管理员运行），要么交给 SYSTEM 护航任务（--service-tick）。
/// </summary>
public sealed class HostsService
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly AppLog _log;
    private readonly Func<AppConfig> _config;
    private readonly HostsAccelerator _accelerator;
    private readonly HostsCache _cache;

    private bool _applied;
    private int _count;
    private string _source = "";
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastCheck;
    private string? _lastError;
    private string? _lastMessage;

    public HostsService(AppLog log, Func<AppConfig> config, HostsAccelerator? accelerator = null, HostsCache? cache = null)
    {
        _log = log;
        _config = config;
        _accelerator = accelerator ?? new HostsAccelerator();
        _cache = cache ?? new HostsCache(Path.Combine(Paths.DataDir, "hosts-cache.json"));
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassroomKit-Agent");
        return client;
    }

    public object StatusDto()
    {
        var (applied, count) = HostsApplier.Inspect();
        return new
        {
            supported = true,
            enabled = _config().Hosts.Enabled,
            applied = applied || _applied,
            count = applied ? count : _count,
            source = _source,
            refreshHours = _config().Hosts.RefreshHours,
            lastCheck = _lastCheck,
            lastSuccess = _lastSuccess,
            lastError = _lastError,
            lastMessage = _lastMessage,
            writable = HostsApplier.CanWrite(),
            serviceInstalled = Tasks.ScheduledTaskInstaller.IsServiceInstalled(),
            backup = HostsPatcher.DefaultPath + HostsPatcher.BackupSuffix
        };
    }

    /// <summary>周期巡检：能直连就不动 hosts；不能直连就刷新并写入。</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var cfg = _config().Hosts;
        _lastCheck = DateTimeOffset.Now;
        if (!cfg.Enabled) return;

        try
        {
            if (await GitHubReachableAsync(ct).ConfigureAwait(false)) return;
            await RefreshAsync(force: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _log.Warn($"hosts 护航巡检失败：{ex.Message}");
        }
    }

    /// <summary>手动/自动刷新：取记录 → 写 hosts → 刷 DNS → 校验。</summary>
    public async Task<(bool Ok, string Message)> RefreshAsync(bool force, CancellationToken ct)
    {
        var cfg = _config().Hosts;
        if (!cfg.Enabled && !force) return (false, "hosts 加速已在配置里关闭");
        if (!force && await GitHubReachableAsync(ct).ConfigureAwait(false))
            return (true, "当前可直连 GitHub，未改动 hosts");

        _lastCheck = DateTimeOffset.Now;
        var result = await _accelerator.RefreshAsync(cfg.Sources, null, ct).ConfigureAwait(false);

        IReadOnlyList<HostEntry> entries;
        if (result.Ok)
        {
            entries = result.Entries;
            _source = result.Source;
            _cache.Save(entries, result.Source, result.At);
        }
        else
        {
            var cached = _cache.Load();
            if (cached is null || DateTimeOffset.Now - cached.At > TimeSpan.FromHours(48))
            {
                _lastError = result.Message;
                return (false, result.Message);
            }
            entries = cached.Entries;
            _source = cached.Source + "（缓存）";
            _log.Warn($"hosts 源不可用，改用缓存：{result.Message}");
        }

        var (applied, message) = await ApplyAsync(entries, ct).ConfigureAwait(false);
        if (!applied)
        {
            _lastError = message;
            return (false, message);
        }

        HostsApplier.FlushDnsSafe();
        var online = await GitHubReachableAsync(ct).ConfigureAwait(false);
        _applied = true;
        _count = entries.Count;
        _lastSuccess = DateTimeOffset.Now;
        _lastMessage = message;

        if (online)
        {
            _lastError = null;
            _log.Info($"hosts 加速已生效（{entries.Count} 条，来源 {_source}）");
            return (true, $"已写入 {entries.Count} 条记录，GitHub 已连通");
        }

        await RestoreAsync(ct).ConfigureAwait(false);
        _lastError = "写入后仍连不上 GitHub，已自动还原 hosts";
        _log.Warn(_lastError);
        return (false, _lastError);
    }

    public Task<(bool Ok, string Message)> RestoreAsync(CancellationToken ct)
    {
        var (ok, message) = HostsApplier.CanWrite() ? HostsApplier.Restore() : RestoreViaService(ct);
        if (ok)
        {
            _applied = false;
            _count = 0;
            _lastMessage = message;
            HostsApplier.FlushDnsSafe();
            _log.Info($"hosts 还原：{message}");
        }
        else
        {
            _lastError = message;
        }
        return Task.FromResult((ok, message));
    }

    private (bool Ok, string Message) RestoreViaService(CancellationToken ct)
    {
        var request = new HostsRequest(ServiceChannel.NewId(), "restore", Array.Empty<HostEntry>(), DateTimeOffset.Now);
        ServiceChannel.Write(ServiceChannel.HostsRequestPath, request);
        var status = ServiceChannel.WaitAsync<HostsStatus>(
            ServiceChannel.HostsStatusPath, s => s.Id == request.Id, TimeSpan.FromSeconds(25), ct).GetAwaiter().GetResult();
        return status is null
            ? (false, "护航服务没有响应：请用管理员运行一次 ClassroomKit.Agent.exe --install-service")
            : (status.Ok, status.Message);
    }

    private async Task<(bool Ok, string Message)> ApplyAsync(IReadOnlyList<HostEntry> entries, CancellationToken ct)
    {
        if (HostsApplier.CanWrite()) return HostsApplier.Apply(entries);

        var request = new HostsRequest(ServiceChannel.NewId(), "apply", entries, DateTimeOffset.Now);
        ServiceChannel.Write(ServiceChannel.HostsRequestPath, request);
        var status = await ServiceChannel.WaitAsync<HostsStatus>(
            ServiceChannel.HostsStatusPath, s => s.Id == request.Id, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);

        return status is null
            ? (false, "护航服务没有响应：请用管理员运行一次 ClassroomKit.Agent.exe --install-service")
            : (status.Ok, status.Message);
    }

    public static async Task<bool> GitHubReachableAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync("https://api.github.com/zen", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
