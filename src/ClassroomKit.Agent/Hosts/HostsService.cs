using ClassroomKit.Agent.Service;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Hosts;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Hosts;

/// <summary>
/// GitHub 访问护航：默认全天生效 —— 有可用记录就写进 hosts（直连也走加速）；写完全程校验，还是不通就自动还原。
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
            serviceAlive = IsServiceAlive(),
            heartbeatAt = ServiceChannel.ReadHeartbeat()?.At,
            backup = HostsPatcher.DefaultPath + HostsPatcher.BackupSuffix
        };
    }

    /// <summary>周期巡检：不论直连是否可用都刷新并写入（全天生效，直连也走加速记录）。</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var cfg = _config().Hosts;
        _lastCheck = DateTimeOffset.Now;
        if (!cfg.Enabled) return;

        try
        {
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

        var (applied, message, pending) = await ApplyAsync(entries, ct).ConfigureAwait(false);
        if (!applied)
        {
            _lastError = message;
            return (false, message);
        }
        if (pending)
        {
            // 请求已进信箱，护航 5 分钟内会写；此处不刷 DNS、不校验，免得误判成「写了也没通」
            _lastMessage = message;
            _log.Info($"hosts 刷新请求已交给护航任务：{message}");
            return (true, message);
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
            return (true, $"已写入 {entries.Count} 条记录（全天生效），GitHub 已连通");
        }

        await RestoreAsync(ct).ConfigureAwait(false);
        _lastError = "写入后仍连不上 GitHub，已自动还原 hosts";
        _log.Warn(_lastError);
        return (false, _lastError);
    }

    public Task<(bool Ok, string Message)> RestoreAsync(CancellationToken ct)
    {
        bool ok;
        string message;
        if (HostsApplier.CanWrite())
        {
            (ok, message) = HostsApplier.Restore();
        }
        else
        {
            var viaService = RestoreViaService(ct);
            if (viaService.Pending)
            {
                // 护航任务每 5 分钟才醒一次，请求已经躺在信箱里了：别把它当成失败
                _lastMessage = viaService.Message;
                _log.Info($"hosts 还原请求已交给护航任务：{viaService.Message}");
                return Task.FromResult((true, viaService.Message));
            }
            (ok, message) = (viaService.Ok, viaService.Message);
        }
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

    private (bool Ok, string Message, bool Pending) RestoreViaService(CancellationToken ct)
    {
        var request = new HostsRequest(ServiceChannel.NewId(), "restore", Array.Empty<HostEntry>(), DateTimeOffset.Now);
        ServiceChannel.Write(ServiceChannel.HostsRequestPath, request);
        var installed = Tasks.ScheduledTaskInstaller.IsServiceInstalled();
        if (installed) Tasks.ScheduledTaskInstaller.RunServiceNow();   // 护航平时每 5 分钟才醒，顺手叫它一次
        var status = ServiceChannel.WaitAsync<HostsStatus>(
            ServiceChannel.HostsStatusPath, s => s.Id == request.Id,
            TimeSpan.FromSeconds(installed ? 25 : 5), ct).GetAwaiter().GetResult();
        if (status is not null) return (status.Ok, status.Message, false);
        return installed
            ? (true, PendingMessage, true)
            : (false, "护航任务未安装：在面板点「安装护航任务」，只需一次管理员授权", false);
    }

    private async Task<(bool Ok, string Message, bool Pending)> ApplyAsync(IReadOnlyList<HostEntry> entries, CancellationToken ct)
    {
        if (HostsApplier.CanWrite())
        {
            var direct = HostsApplier.Apply(entries);
            return (direct.Ok, direct.Message, false);
        }

        var request = new HostsRequest(ServiceChannel.NewId(), "apply", entries, DateTimeOffset.Now);
        ServiceChannel.Write(ServiceChannel.HostsRequestPath, request);
        var installed = Tasks.ScheduledTaskInstaller.IsServiceInstalled();
        if (installed) Tasks.ScheduledTaskInstaller.RunServiceNow();   // 护航平时每 5 分钟才醒，顺手叫它一次
        var status = await ServiceChannel.WaitAsync<HostsStatus>(
            ServiceChannel.HostsStatusPath, s => s.Id == request.Id,
            TimeSpan.FromSeconds(installed ? 25 : 5), ct).ConfigureAwait(false);

        if (status is not null) return (status.Ok, status.Message, false);
        return installed
            ? (true, PendingMessage, true)
            : (false, "护航任务未安装：在面板点「安装护航任务」，只需一次管理员授权", false);
    }

    private const string PendingMessage = "已提交给护航任务，最多 5 分钟内写入（面板稍后刷新可见）";

    /// <summary>护航任务最近 20 分钟是否写过心跳。</summary>
    private static bool IsServiceAlive()
    {
        var heartbeat = ServiceChannel.ReadHeartbeat();
        return heartbeat is not null && DateTimeOffset.Now - heartbeat.At < TimeSpan.FromMinutes(20);
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
