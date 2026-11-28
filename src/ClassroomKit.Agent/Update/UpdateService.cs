using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClassroomKit.Agent.Service;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Update;

namespace ClassroomKit.Agent.Update;

/// <summary>升级流程所处的阶段，直接透给面板显示。</summary>
public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Installing,
    Failed
}

/// <summary>一次升级流程的可序列化快照。</summary>
public sealed record UpdateStatus
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;
    public string PhaseText => Phase switch
    {
        UpdatePhase.Checking => "正在检查新版本…",
        UpdatePhase.UpToDate => "已是最新版本",
        UpdatePhase.Available => "有新版本可升级",
        UpdatePhase.Downloading => "正在下载安装包…",
        UpdatePhase.Ready => "安装包已下载，等待安装",
        UpdatePhase.Installing => Silent ? "正在后台静默安装（无需操作）…" : "正在安装，程序会短暂退出…",
        UpdatePhase.Failed => "升级失败",
        _ => "未检查"
    };

    public string Current { get; init; } = "";
    public string? Latest { get; init; }
    public string? Notes { get; init; }
    public string? HtmlUrl { get; init; }
    public string? AssetName { get; init; }
    public long AssetSize { get; init; }
    /// <summary>0–100；只在下载阶段有意义。</summary>
    public double Progress { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    /// <summary>安装是否走后台护航任务（true = 没有 UAC、没有窗口）。</summary>
    public bool Silent { get; init; }

    public bool HasUpdate => Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing;

    // ── ClassIsland 插件（cipx）随升级一起更新的状态 ──
    /// <summary>本机是否检测到 ClassIsland 插件目录。</summary>
    public bool PluginDetected { get; init; }
    /// <summary>本机已装的插件版本（读不到为空串）。</summary>
    public string PluginCurrent { get; init; } = "";
    /// <summary>远端最新插件版本（读不到为空串）。</summary>
    public string PluginLatest { get; init; } = "";
    /// <summary>远端插件比本机新，可以更新。</summary>
    public bool PluginAvailable { get; init; }
    /// <summary>插件下载进度 0–1（安装包占 0–0.9，插件占 0.9–1.0）。</summary>
    public double PluginProgress { get; init; }
    /// <summary>插件更新失败的原因（成功/未开始时为空串）。</summary>
    public string PluginError { get; init; } = "";
    /// <summary>给面板直接显示的一句话（未检测到 ClassIsland / 已是最新 / 可更新 x → y / 更新失败原因）。</summary>
    public string PluginText { get; init; } = "";
    /// <summary>现在就能更新插件（检测到插件 + 远端有新版本 + 已拿到 cipx 资产）。</summary>
    public bool PluginCanUpdate { get; init; }
}

/// <summary>
/// 从 GitHub Releases 检查 / 下载 / 安装新版本。
/// 设计约束（学校一体机）：
///   · 检查与下载都只走 HTTPS，GitHub 被校园网挡住时给出明确提示，不静默失败；
///   · 安装必须提权：写 Program Files 的 MSI 只能走一次 UAC（runas），被拒绝要如实告知；
///   · 安装脚本在独立 cmd 里等本进程退出后再跑 msiexec，避免自己锁住要替换的 exe；
///   · MSI 升级由安装包自己停进程、重建计划任务并启动新版本，脚本只做兜底拉起。
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly AppLog _log;
    private readonly Func<AppConfig> _config;
    private readonly string _current;
    private readonly Action _requestExit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();

    private UpdateStatus _status;
    private GitHubRelease? _release;
    private string? _installerPath;
    private DateTimeOffset? _checkedAt;

    // ClassIsland 插件（cipx）状态：全部在 _lock 里改，由 Decorate() 投影进 UpdateStatus。
    private PluginLocation? _pluginLocation;
    private DateTimeOffset _pluginDetectedAt;
    private ReleaseAsset? _pluginRelease;
    private string? _pluginPath;
    private bool _pluginDetected;
    private string _pluginCurrent = "";
    private string _pluginLatest = "";
    private bool _pluginAvailable;
    private string _pluginError = "";
    private string _pluginText = "";
    private double _pluginProgress;

    public UpdateService(AppLog log, Func<AppConfig> config, string currentVersion, Action requestExit)
    {
        _log = log;
        _config = config;
        _current = UpdateVersion.Normalize(currentVersion);
        _requestExit = requestExit;
        _status = new UpdateStatus { Current = _current };
        CleanupOldPackages();
        if (File.Exists(PendingPluginPath))
            _log.Info("发现上次没装完的 ClassIsland 插件：退出 ClassIsland 后会自动重试");
    }

    /// <summary>
    /// 清掉升级残留：已经装上的版本（目录名 == 当前版本）安装包没用了；
    /// 另外只保留最新一个待安装目录，避免每次升级都在 ProgramData 里留一份 65 MB。
    /// </summary>
    private void CleanupOldPackages()
    {
        try
        {
            if (!Directory.Exists(UpdateRoot)) return;

            var dirs = Directory.GetDirectories(UpdateRoot)
                .Select(p => (Path: p, Version: UpdateVersion.Normalize(Path.GetFileName(p))))
                .Where(d => d.Version.Length > 0)
                .OrderByDescending(d => d.Version, Comparer<string>.Create(UpdateVersion.Compare))
                .ToList();

            var kept = 0;
            foreach (var dir in dirs)
            {
                if (dir.Version == _current)
                {
                    Directory.Delete(dir.Path, true);
                    _log.Info($"清理升级残留：{Path.GetFileName(dir.Path)}（已安装到当前版本）");
                    continue;
                }

                if (kept == 0)
                {
                    kept++;
                    continue;
                }

                Directory.Delete(dir.Path, true);
                _log.Info($"清理升级残留：{Path.GetFileName(dir.Path)}（只保留最新待安装包）");
            }

            foreach (var part in Directory.EnumerateFiles(UpdateRoot, "*.part", SearchOption.AllDirectories).ToList())
            {
                File.Delete(part);
                _log.Info("清理升级残留：" + Path.GetFileName(part));
            }
        }
        catch (Exception ex)
        {
            _log.Warn("清理升级残留失败：" + ex.Message);
        }
    }

    public string CurrentVersion => _current;

    public UpdateStatus Status
    {
        get { lock (_lock) return _status; }
    }

    public static string UpdateRoot => Path.Combine(Paths.DataDir, "update");

    /// <summary>插件装不上（ClassIsland 正在运行）时先落在这里，等巡检补装。</summary>
    public static string PendingPluginPath => Path.Combine(UpdateRoot, "pending-plugin.cipx");

    /// <summary>有没有等着补装的插件包。</summary>
    public bool HasPendingPlugin => File.Exists(PendingPluginPath);

    private UpdateConfig Config => _config().Update ?? new UpdateConfig();

    /// <summary>检查 GitHub 上是否有更新版本；10 分钟内重复调用直接返回缓存。</summary>
    public async Task<UpdateStatus> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        // 插件检测与远端检查互相独立：没有 ClassIsland 也要能正常升级应用本身
        // 用户点「检查更新」时绕开 5 分钟的目录缓存，否则刚装/刚挪过 ClassIsland 会拿着旧路径。
        DetectPlugin(force);

        var cfg = Config;
        if (!cfg.Enabled)
            return Set(new UpdateStatus { Current = _current, Phase = UpdatePhase.Failed, Error = "自动更新已在配置里关闭（update.enabled=false）" });

        lock (_lock)
        {
            if (!force && _status.Phase is UpdatePhase.UpToDate or UpdatePhase.Available && _checkedAt is { } at &&
                DateTimeOffset.Now - at < CacheTtl)
            {
                return _status;
            }
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 升级请求已经投给护航任务时不要再刷新状态：以前这里无条件 Set(Checking)，
            // 会把 Installing 冲掉，20 分钟看门狗就永远对不上号，面板一直停在「正在安装」。
            if (Status.Phase == UpdatePhase.Installing) return Status;

            Set(new UpdateStatus { Current = _current, Phase = UpdatePhase.Checking, CheckedAt = DateTimeOffset.Now });
            _log.Info($"检查更新：{cfg.Repo}（当前 v{_current}）");

            var url = cfg.IncludePrerelease
                ? $"https://api.github.com/repos/{cfg.Repo}/releases?per_page=10"
                : $"https://api.github.com/repos/{cfg.Repo}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd($"ClassroomKit-Agent/{_current}");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail($"仓库 {cfg.Repo} 还没有发布任何版本（或地址写错了）");
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            {
                return Fail("GitHub 接口限流了，请过几分钟再试");
            }
            if (!response.IsSuccessStatusCode)
            {
                return Fail($"GitHub 返回 {(int)response.StatusCode}，暂时无法检查更新");
            }

            var release = cfg.IncludePrerelease ? ReleaseFeed.ParseList(body, true) : ReleaseFeed.ParseOne(body);
            if (release is null)
                return Fail("GitHub 返回的内容看不懂（可能被网络中间页替换了），稍后再试");

            _release = release;
            _checkedAt = DateTimeOffset.Now;
            // 同一个版本重复检查时别把已经下好的安装包路径丢掉，否则 Install() 会说「还没下载好」。
            if (_installerPath is not null && !_installerPath.Contains(release.Version, StringComparison.OrdinalIgnoreCase))
                _installerPath = null;
            ApplyReleasePlugin(release);

            if (!UpdateVersion.IsNewer(release.Version, _current))
            {
                _log.Info($"已是最新版本 v{_current}（远端 {release.Tag}）");
                return Set(new UpdateStatus
                {
                    Current = _current,
                    Latest = release.Version,
                    Phase = UpdatePhase.UpToDate,
                    HtmlUrl = release.HtmlUrl,
                    CheckedAt = _checkedAt
                });
            }

            var asset = ReleaseFeed.PickInstaller(release);
            _log.Info($"发现新版本 {release.Tag}（当前 v{_current}），安装包：{asset?.Name ?? "无"}");
            return Set(new UpdateStatus
            {
                Current = _current,
                Latest = release.Version,
                Notes = release.Notes,
                HtmlUrl = release.HtmlUrl,
                AssetName = asset?.Name,
                AssetSize = asset?.Size ?? 0,
                Phase = asset is null ? UpdatePhase.Failed : UpdatePhase.Available,
                Error = asset is null ? "这个版本没有附 Windows 安装包" : null,
                CheckedAt = _checkedAt
            });
        }
        catch (OperationCanceledException)
        {
            return Fail("检查更新已取消");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"检查更新失败（网络）：{ex.Message}");
            return Fail("连不上 GitHub（学校网络可能屏蔽了它）：可以手动下载 MSI 安装");
        }
        catch (Exception ex)
        {
            _log.Error($"检查更新异常：{ex.Message}");
            return Fail("检查更新出错：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>下载安装包到 ProgramData\ClassroomKit\update\&lt;版本&gt;\，校验大小与 sha256。</summary>
    public async Task<UpdateStatus> DownloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_release is null)
                return Fail("还没有检查到可用版本，请先点「检查更新」");

            var asset = ReleaseFeed.PickInstaller(_release);
            if (asset is null) return Fail("这个版本没有附 Windows 安装包");

            var dir = Path.Combine(UpdateRoot, _release.Version);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, asset.Name);
            _installerPath = target;

            // 插件包是不是也要一起下：决定进度条 0–0.9 / 0.9–1.0 的切分
            ReleaseAsset? pluginAsset;
            lock (_lock) pluginAsset = _pluginAvailable ? _pluginRelease : null;
            var hasPlugin = pluginAsset is not null;

            if (IsAssetComplete(target, asset))
            {
                _log.Info($"安装包已存在，跳过下载：{target}");
            }
            else
            {
                Set(new UpdateStatus
                {
                    Current = _current,
                    Latest = _release.Version,
                    Notes = _release.Notes,
                    HtmlUrl = _release.HtmlUrl,
                    AssetName = asset.Name,
                    AssetSize = asset.Size,
                    Phase = UpdatePhase.Downloading,
                    CheckedAt = _checkedAt
                });

                _log.Info($"下载安装包：{asset.Name}（{asset.Size / 1024 / 1024.0:F1} MB）");

                try
                {
                    await DownloadAssetAsync(asset, target,
                        p => Set(Status with { Phase = UpdatePhase.Downloading, Progress = UpdateProgress.ForInstaller(p, hasPlugin) }),
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 不是所有下载失败都怪学校网络：磁盘满、没有写权限、杀软拦截也会走到这里，
                    // 一律说“网络被屏蔽”会让老师白折腾换网络。
                    if (ex is UnauthorizedAccessException or IOException)
                    {
                        return Fail("安装包没能写入磁盘（磁盘空间不足或没有写入权限）：" + ex.Message);
                    }

                    var hint = "：" + ex.Message;
                    return Fail("下载安装包失败（直连和镜像都不通，学校网络可能屏蔽了 GitHub）："
                        + "可以用浏览器手动下载安装包再双击安装" + hint);
                }

                _log.Info($"安装包下载完成：{target}（{new FileInfo(target).Length} 字节）");
            }

            if (pluginAsset is not null)
                await DownloadPluginPackageAsync(pluginAsset, dir, ct).ConfigureAwait(false);

            // 下载与安装可能并发：安装已经开始时，结尾的 Ready 会把 Installing 冲掉，
            // 面板会永远显示「正在安装」，看门狗也对不上号。
            if (Status.Phase == UpdatePhase.Installing)
            {
                _log.Info("安装已经开始，下载完成后保持「正在安装」状态");
                return Status;
            }

            return Set(Ready());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail("下载已取消");
        }
        catch (OperationCanceledException)
        {
            _log.Warn("下载超时（连接 2 小时没有完成）");
            return Fail("下载超时：网络太慢或连接被中断，请稍后重试，或用浏览器手动下载安装包");
        }
        catch (Exception ex)
        {
            _log.Error($"下载安装包失败：{ex.Message}");
            return Fail("下载安装包失败：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 安装新版本。优先交给 SYSTEM 护航任务：它在跑 msiexec 前会停掉用户态进程，
    /// 全程无窗口、无 UAC；守护自己不退出，等它来停（停之前热点一直在线）。
    /// 只有没装护航任务（绿色版）时才退回「弹一次 UAC」的老路。
    /// </summary>
    public (bool Ok, string Message) Install()
    {
        // 已经在装的时候再点一次会覆盖安装请求，护航任务与看门狗就对不上同一个 id 了。
        if (Status.Phase == UpdatePhase.Installing)
            return (false, "已经在安装中，请等它结束（最多 20 分钟）");

        // 先把已经下载好的 ClassIsland 插件装上；插件失败只记状态，绝不阻断 MSI 升级
        MaybeApplyPlugin();

        var path = _installerPath;
        if (path is null || !File.Exists(path))
            return (false, "安装包还没下载好，请先下载");

        if (!path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return (false, "当前版本只支持用 MSI 安装包升级，请到 GitHub Release 手动解压 zip");

        if (ScheduledTaskInstaller.IsServiceInstalled())
            return QueueSilentInstall(path);

        return InstallViaUac(path);
    }

    /// <summary>把安装请求投给护航任务，立刻返回；真正的 msiexec 由 SYSTEM 执行。</summary>
    private (bool Ok, string Message) QueueSilentInstall(string path)
    {
        var id = ServiceChannel.NewId();
        ServiceChannel.Write(ServiceChannel.InstallRequestPath, new InstallRequest(id, path, DateTimeOffset.Now));

        // 尝试立刻叫醒护航任务（它是 SYSTEM 任务，普通权限多半叫不动，失败不算错）。
        var (woke, wakeMessage) = ScheduledTaskInstaller.RunServiceNow();

        Set(Status with { Phase = UpdatePhase.Installing, Silent = true, Error = null });
        _log.Info($"已投递静默安装请求 {id}：{Path.GetFileName(path)}（{wakeMessage}）");

        // 护航任务若一直没接手（任务被禁用之类），20 分钟后退回「可安装」，免得面板永远显示升级中。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(20)).ConfigureAwait(false);
            if (Status.Phase != UpdatePhase.Installing) return;
            _log.Warn("静默安装 20 分钟没有完成，认为护航任务没有接手");
            Set(Status with { Phase = UpdatePhase.Ready, Silent = false, Error = "护航任务没有接手安装，可再点一次升级" });
        });

        var hint = woke ? "已交给后台护航任务，正在静默安装" : "已排队，护航任务会在下一次巡检（最多 5 分钟）接手";
        return (true, $"{hint}：没有弹窗也不需要授权，热点会短暂中断，装完自动恢复");
    }

    /// <summary>兜底路径：绿色版没注册护航任务时，仍然弹一次 UAC 让 msiexec 跑起来。</summary>
    private (bool Ok, string Message) InstallViaUac(string path)
    {
        // 自动升级可能发生在凌晨、没人坐在电脑前：这时 Process.Start(runas) 会让调用线程
        // 永远卡在安全桌面上（老师看不见也点不到）。没有交互桌面就如实返回，让用户到机器前手动装。
        if (!AdminElevation.CanPrompt())
        {
            _log.Warn("没有可交互的桌面会话，跳过弹 UAC 的兜底安装");
            Set(Status with { Phase = UpdatePhase.Ready, Error = "当前没有可交互的桌面会话，无法弹管理员授权，请到电脑前手动升级" });
            return (false, "当前没有可交互的桌面会话，无法弹管理员授权：请到这台电脑前手动运行安装包，或先装好护航任务");
        }

        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            // 不再把 .cmd 写到用户可写的 ProgramData：那等于给普通用户一个「管理员会执行的脚本」，
            // 存在 TOCTOU 提权。直接以管理员重启受保护的自身 exe，由 --apply-update 完成安装。
            var psi = new ProcessStartInfo(Paths.ExePath, $"--apply-update \"{path}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!AdminElevation.IsElevated()) psi.Verb = "runas";

            var process = Process.Start(psi);
            _log.Info($"已启动升级进程（PID {process?.Id}）：{Path.GetFileName(path)}");

            Set(Status with { Phase = UpdatePhase.Installing, Silent = false, Error = null });

            // 给 UAC 授权与脚本的 timeout 留出时间，再安静退出，避免 MSI 替换文件时被占用。
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                try { _requestExit(); } catch { /* 退出路径由上层决定 */ }
            });

            return (true, "正在安装新版本：" + (AdminElevation.IsElevated()
                ? "安装过程没有进度窗口，完成后程序自动重启（本地 API 会断开几秒）"
                : "会弹一次管理员授权，安装过程没有进度窗口，完成后程序自动重启（本地 API 会断开几秒）"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _log.Warn("用户取消了升级的管理员授权");
            Set(Status with { Phase = UpdatePhase.Ready, Error = "已取消管理员授权，升级没有开始" });
            return (false, "已取消管理员授权，升级没有开始（可再点一次升级）");
        }
        catch (Exception ex)
        {
            _log.Error($"启动升级脚本失败：{ex.Message}");
            Set(Status with { Phase = UpdatePhase.Ready, Error = "启动升级失败：" + ex.Message });
            return (false, "启动升级失败：" + ex.Message);
        }
    }

    // ── ClassIsland 插件（cipx）────────────────────────────────────────────────
    // 状态都在 _lock 里改，UpdateStatus 由 Decorate() 投影出去；
    // 插件只是「顺带更新」，任何失败都只写 PluginError，绝不阻断 MSI 升级。

    private static readonly TimeSpan PluginDetectTtl = TimeSpan.FromMinutes(5);

    /// <summary>插件解包/覆盖是同步文件操作，面板按钮与巡检可能同时到，串起来避免互相踩。</summary>
    private static readonly object PluginInstallLock = new();

    private bool PluginAvailableNow
    {
        get { lock (_lock) return _pluginAvailable; }
    }

    private string PluginErrorNow
    {
        get { lock (_lock) return _pluginError; }
    }

    /// <summary>探测本机 ClassIsland 插件目录（5 分钟缓存），更新 Detected/Current/Available。</summary>
    private PluginLocation DetectPlugin(bool force = false)
    {
        lock (_lock)
        {
            if (!force && _pluginLocation is not null && DateTimeOffset.Now - _pluginDetectedAt < PluginDetectTtl)
                return _pluginLocation;
        }

        PluginLocation location;
        try
        {
            location = new PluginInstaller(_log).Detect();
        }
        catch (Exception ex)
        {
            _log.Warn("查找 ClassIsland 插件目录失败：" + ex.Message);
            location = new PluginLocation(null, null, "", null);
        }

        lock (_lock)
        {
            _pluginLocation = location;
            _pluginDetectedAt = DateTimeOffset.Now;
            _pluginDetected = location.CanInstall;
            _pluginCurrent = location.Version;
            _pluginAvailable = ComputePluginAvailableLocked();
            RefreshPluginTextLocked();
            TouchLocked();
        }

        if (location.CanInstall)
            _log.Info($"ClassIsland 插件：v{location.Version}（{location.Dir}，来源：{location.Source}）");
        else
            _log.Info("没有找到 ClassIsland 插件目录（这台机器可能没装 ClassIsland）");

        return location;
    }

    /// <summary>从 release 里挑出 cipx（尊重 update.updatePlugin 开关），更新 Available/Latest。</summary>
    private void ApplyReleasePlugin(GitHubRelease release)
    {
        if (!Config.UpdatePlugin)
        {
            lock (_lock)
            {
                _pluginRelease = null;
                _pluginLatest = "";
                _pluginPath = null;
                _pluginProgress = 0;
                _pluginError = "";
                _pluginAvailable = false;
                RefreshPluginTextLocked();
                TouchLocked();
            }

            _log.Info("配置里关闭了插件更新（update.updatePlugin=false）");
            return;
        }

        var asset = ReleaseFeed.PickPlugin(release);
        var latest = asset is null ? "" : PluginVersionOf(asset, release.Version);

        lock (_lock)
        {
            _pluginRelease = asset;
            _pluginLatest = latest;
            _pluginPath = null;
            _pluginProgress = 0;
            _pluginError = "";
            _pluginAvailable = ComputePluginAvailableLocked();
            RefreshPluginTextLocked();
            TouchLocked();
        }

        if (asset is null)
            _log.Info("这个版本没有附 ClassIsland 插件包（.cipx），跳过插件更新");
        else
            _log.Info($"ClassIsland 插件：本机 v{_pluginCurrent}，远端 {latest}（{asset.Name}）");
    }

    /// <summary>远端插件比本机新（本机没装时只要有远端包就算可更新）。</summary>
    private bool ComputePluginAvailableLocked()
    {
        if (!_pluginDetected || _pluginRelease is null) return false;
        if (_pluginLatest.Length == 0) return false;
        if (_pluginCurrent.Length == 0) return true;
        return UpdateVersion.IsNewer(_pluginLatest, _pluginCurrent);
    }

    /// <summary>插件版本：优先用文件名里带的版本号，与 manifest 版本一致时最可靠。</summary>
    private static string PluginVersionOf(ReleaseAsset asset, string releaseVersion)
    {
        var normalized = UpdateVersion.Normalize(releaseVersion);
        if (normalized.Length > 0 && asset.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            return normalized;

        var match = Regex.Match(asset.Name, @"\d+(?:\.\d+){1,3}");
        return match.Success ? match.Value : normalized;
    }

    /// <summary>给面板的一句话（调用方持有 _lock）。</summary>
    private void RefreshPluginTextLocked()
    {
        if (!Config.UpdatePlugin)
        {
            _pluginText = _pluginDetected ? "插件更新已在配置里关闭" : "未检测到 ClassIsland";
            return;
        }

        if (!_pluginDetected)
        {
            _pluginText = "未检测到 ClassIsland";
            return;
        }

        if (_pluginError.Length > 0)
        {
            _pluginText = "更新失败：" + _pluginError;
            return;
        }

        if (_pluginAvailable)
        {
            if (_pluginLatest.Length == 0) _pluginText = "有新插件可以更新";
            else if (_pluginCurrent.Length == 0) _pluginText = "可安装 " + _pluginLatest;
            else _pluginText = $"可更新 {_pluginCurrent} → {_pluginLatest}";
            return;
        }

        _pluginText = "已是最新";
    }

    private void SetPluginError(string message)
    {
        lock (_lock)
        {
            _pluginError = message;
            RefreshPluginTextLocked();
            TouchLocked();
        }
    }

    private void SetPluginProgress(double value)
    {
        lock (_lock)
        {
            _pluginProgress = Math.Clamp(value, 0, 1);
            TouchLocked();
        }
    }

    private void MarkPluginApplied(string installedVersion)
    {
        lock (_lock)
        {
            if (installedVersion.Length > 0) _pluginCurrent = installedVersion;
            _pluginAvailable = ComputePluginAvailableLocked();
            _pluginError = "";
            _pluginProgress = 1;
            RefreshPluginTextLocked();
            TouchLocked();
        }

        _log.Info($"ClassIsland 插件已更新到 v{PluginVersionOrUnknown(installedVersion)}");
    }

    private static string PluginVersionOrUnknown(string version) => version.Length > 0 ? version : "（未知版本）";

    /// <summary>升级前先把已下载的 cipx 装上；失败只记状态，MSI 升级照常进行。</summary>
    private void MaybeApplyPlugin()
    {
        if (!Config.UpdatePlugin) return;

        bool available;
        ReleaseAsset? asset;
        string? path;
        lock (_lock)
        {
            available = _pluginAvailable;
            asset = _pluginRelease;
            path = _pluginPath;
        }

        if (!available || asset is null) return;
        if (path is null || !File.Exists(path))
        {
            SetPluginError("插件包还没下载好");
            return;
        }

        TryInstallPlugin(path, stageOnFailure: true);
    }

    /// <summary>
    /// 把 cipx 装到探测到的插件目录。失败时：被占用且 stageOnFailure 就把包复制到
    /// pending-plugin.cipx 让巡检补装，其它情况只写 PluginError。返回是否安装成功。
    /// </summary>
    private bool TryInstallPlugin(string cipxPath, bool stageOnFailure)
    {
        lock (PluginInstallLock)
        {
            var location = DetectPlugin(force: true);
            if (!location.CanInstall)
            {
                SetPluginError("没有找到 ClassIsland 插件目录");
                return false;
            }

            // ClassIsland 运行时插件 DLL 正在被加载，覆盖到一半才失败会留下混合版本；先拒绝，等它退出再补装。
            if (IsClassIslandRunning())
            {
                var busy = "ClassIsland 正在运行，先不动插件文件；退出后会自动补装";
                if (stageOnFailure && StagePendingPlugin(cipxPath))
                    busy += $"（{Path.GetFileName(PendingPluginPath)}）";
                SetPluginError(busy);
                _log.Warn(busy);
                return false;
            }

            _log.Info($"更新 ClassIsland 插件：{Path.GetFileName(cipxPath)} → {location.Dir}");

            // 临时解压目录放到 update\tmp，绝不能落在 …\Plugins 里（ClassIsland 会把它当第二个插件）。
            var stagingRoot = Path.Combine(UpdateRoot, "tmp");
            var result = PluginPackage.ExtractTo(cipxPath, location.Dir!, stagingRoot);
            if (result.Ok)
            {
                var version = PluginPackage.ReadManifestFromDirectory(location.Dir!)?.Version ?? "";
                MarkPluginApplied(version);
                PluginInstaller.InvalidateRootCache();
                TryDelete(PendingPluginPath);
                _log.Info("ClassIsland 插件文件已更新，重启 ClassIsland 后生效");
                return true;
            }

            string hint;
            if (result.PermissionDenied)
            {
                // 权限问题重试也没用：不排 pending，免得每 5 分钟白撞一次
                hint = "没有权限写入 ClassIsland 插件目录，请用管理员身份运行一次：" + result.Message;
            }
            else if (result.Locked)
            {
                hint = "ClassIsland 正在运行或插件文件被占用，请先退出 ClassIsland 后重试";
            }
            else
            {
                hint = "插件包不合法：" + result.Message;
            }

            if (result.Locked && stageOnFailure && StagePendingPlugin(cipxPath))
                hint += $"；已排入待安装队列（{Path.GetFileName(PendingPluginPath)}），退出 ClassIsland 后下次巡检会自动重试";

            SetPluginError(hint);
            return false;
        }
    }

    private static bool IsClassIslandRunning()
    {
        try
        {
            return Process.GetProcessesByName("ClassIsland").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 cipx 复制到 pending-plugin.cipx，等巡检补装。</summary>
    private bool StagePendingPlugin(string cipxPath)
    {
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            if (PathsEqual(cipxPath, PendingPluginPath)) return true;   // 已经在队列里
            // 先写 .part 再原子替换：巡检可能同时在读 pending，不能让它看到半个包。
            var temp = PendingPluginPath + ".part";
            File.Copy(cipxPath, temp, overwrite: true);
            File.Move(temp, PendingPluginPath, overwrite: true);
            _log.Info("ClassIsland 插件已排入待安装队列：" + PendingPluginPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn("把插件包排入待安装队列失败：" + ex.Message);
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 巡检可调用：上次升级时 ClassIsland 正在运行、插件没能装上的话，这里补装。
    /// pending-plugin.cipx 还在就重试，成功删除；仍被占用就保留，下次再试。
    /// </summary>
    public (bool Ok, string Message) TryApplyPendingPlugin()
    {
        if (!File.Exists(PendingPluginPath)) return (true, "没有待安装的 ClassIsland 插件");
        if (!Config.UpdatePlugin)
            return (false, "插件更新已在配置里关闭（update.updatePlugin=false），保留待安装文件");

        // 队里的包可能比本机已装的还旧（用户手动装过更新的插件）：装上去等于降级，直接清掉。
        var pending = PluginPackage.ReadManifest(PendingPluginPath);
        if (pending is not null)
        {
            var installed = DetectPlugin().Version;
            if (!PluginPackage.IsNewerThanInstalled(pending, installed))
            {
                TryDelete(PendingPluginPath);
                _log.Info($"待安装的插件包 v{pending.Version} 不比本机 v{installed} 新，已清理");
                return (true, $"待安装的插件包 v{pending.Version} 不比本机 v{installed} 新，已清理");
            }
        }

        return TryInstallPlugin(PendingPluginPath, stageOnFailure: false)
            ? (true, "ClassIsland 插件已补装完成，重启 ClassIsland 后生效")
            : (false, "ClassIsland 插件仍没能安装：" + PluginErrorNow);
    }

    /// <summary>
    /// 面板「更新插件」：强制检查 → 需要时下载 cipx → 安装。只碰插件，不碰 MSI。
    /// </summary>
    public async Task<UpdateStatus> UpdatePluginAsync(CancellationToken ct = default)
    {
        if (!Config.UpdatePlugin)
        {
            SetPluginError("插件更新已在配置里关闭（update.updatePlugin=false）");
            return Status;
        }

        await CheckAsync(force: true, ct).ConfigureAwait(false);

        ReleaseAsset? asset;
        string latest;
        lock (_lock)
        {
            asset = _pluginRelease;
            latest = _pluginLatest;
        }

        if (asset is null)
        {
            SetPluginError("这个版本没有附 ClassIsland 插件包（.cipx）");
            return Status;
        }

        if (!PluginAvailableNow)
        {
            // 远端没有比本机新的插件：不重复下载
            lock (_lock)
            {
                _pluginError = "";
                RefreshPluginTextLocked();
                TouchLocked();
            }

            return Status;
        }

        string target;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var versionDir = latest.Length > 0 ? latest : (_release?.Version ?? "plugin");
            var dir = Path.Combine(UpdateRoot, SafeDirectoryName(versionDir));
            Directory.CreateDirectory(dir);
            target = Path.Combine(dir, asset.Name);

            lock (_lock)
            {
                _pluginError = "";
                _pluginProgress = 0;
            }
            Set(Status);

            _log.Info($"手动更新 ClassIsland 插件：{asset.Name}（{asset.Size / 1024.0:F0} KB）");

            if (IsAssetComplete(target, asset))
            {
                _log.Info("插件包已存在，跳过下载：" + target);
            }
            else
            {
                await DownloadAssetAsync(asset, target, p => SetPluginProgress(p / 100.0), ct).ConfigureAwait(false);
            }

            lock (_lock) _pluginPath = target;
            ApplyPluginVersionFromPackage(target);
        }
        catch (OperationCanceledException)
        {
            SetPluginError("插件更新已取消");
            return Status;
        }
        catch (Exception ex)
        {
            _log.Warn("下载 ClassIsland 插件包失败：" + ex.Message);
            SetPluginError("下载插件包失败：" + ex.Message);
            return Status;
        }
        finally
        {
            _gate.Release();
        }

        if (!PluginAvailableNow)
        {
            _log.Info("下载到的插件与已装版本一致，不需要安装");
            SetPluginProgress(1);
            return Status;
        }

        TryInstallPlugin(target, stageOnFailure: true);
        return Status;
    }

    /// <summary>用包里的 manifest 校正插件版本（文件名不一定带版本号）。</summary>
    private void ApplyPluginVersionFromPackage(string cipxPath)
    {
        var manifest = PluginPackage.ReadManifest(cipxPath);
        if (manifest is null) return;

        var latest = UpdateVersion.Normalize(manifest.Version);
        if (latest.Length == 0) return;

        var changed = false;
        lock (_lock)
        {
            if (latest != _pluginLatest)
            {
                _pluginLatest = latest;
                _pluginAvailable = ComputePluginAvailableLocked();
                changed = true;
            }
            RefreshPluginTextLocked();
            TouchLocked();
        }

        if (changed) _log.Info($"插件包里的版本是 {latest}");
    }

    /// <summary>
    /// 下载 cipx 到安装包旁边，进度落在 0.9–1.0。
    /// 插件只是附带升级：失败只写 PluginError，绝不影响 MSI 升级。
    /// </summary>
    private async Task DownloadPluginPackageAsync(ReleaseAsset asset, string dir, CancellationToken ct)
    {
        var target = Path.Combine(dir, asset.Name);
        try
        {
            if (IsAssetComplete(target, asset))
            {
                _log.Info($"插件包已存在，跳过下载：{target}");
            }
            else
            {
                _log.Info($"下载 ClassIsland 插件包：{asset.Name}（{asset.Size / 1024.0:F0} KB）");
                await DownloadAssetAsync(asset, target,
                    p => Set(Status with { Phase = UpdatePhase.Downloading, Progress = UpdateProgress.ForPlugin(p) }),
                    ct).ConfigureAwait(false);
            }

            lock (_lock) _pluginPath = target;
            ApplyPluginVersionFromPackage(target);
        }
        catch (OperationCanceledException)
        {
            // 安装包已经下好了，插件取消不该把整个升级打回失败
            SetPluginError("插件包下载已取消");
        }
        catch (Exception ex)
        {
            _log.Warn("下载 ClassIsland 插件包失败（不影响安装包升级）：" + ex.Message);
            lock (_lock)
            {
                _pluginPath = null;
                _pluginProgress = 0;
            }
            SetPluginError("下载插件包失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 下载一个资产到 target 并校验大小与 sha256：先试分段并发，再回退单连接，
    /// 最后按配置里的镜像依次重试（api.github.com 资产接口打头，比 releases/download 稳）。
    /// onProgress 收到 0–100 的百分比。
    /// </summary>
    private async Task DownloadAssetAsync(ReleaseAsset asset, string target, Action<double>? onProgress, CancellationToken ct)
    {
        var cfg = Config;
        var apiUrl = asset.Id > 0
            ? $"https://api.github.com/repos/{cfg.Repo}/releases/assets/{asset.Id}"
            : null;
        var urls = UpdateUrls.Candidates(asset.Url, cfg.Mirrors, apiUrl);
        var directIndex = apiUrl is null ? 0 : 1;
        Exception? lastError = null;

        for (var i = 0; i < urls.Count; i++)
        {
            var url = urls[i];
            var via = i == directIndex ? "直连" : (apiUrl is not null && i == 0 ? "GitHub API" : "镜像 " + UriHost(url));
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    // 学校网络到 GitHub 的单连接经常只有几十 KB/s，先试分段并发下载；
                    // 服务器不支持 Range（或分片失败）时自动回退单连接，不影响可靠性。
                    if (!await TryParallelDownloadAsync(url, asset, target, onProgress, ct).ConfigureAwait(false))
                        await DownloadToFileAsync(url, asset, target, onProgress, ct).ConfigureAwait(false);

                    var info = new FileInfo(target);
                    if (asset.Size > 0 && info.Length != asset.Size)
                        throw new InvalidDataException($"下载不完整（{info.Length}/{asset.Size} 字节）");
                    if (asset.Digest is not null)
                    {
                        var (digestOk, message) = VerifyDigest(target, asset.Digest);
                        if (!digestOk) throw new InvalidDataException("文件校验不通过：" + message);
                    }
                    else
                    {
                        // 只比大小挡不住被中间人替换的安装包。GitHub 从 2025 年起给资产带 digest，
                        // 自己的发布流程也一定有；缺失就拒绝自动安装，宁可让用户手动装。
                        throw new InvalidDataException("发布资产没有 sha256 摘要，无法确认安装包完整，请手动下载安装");
                    }

                    _log.Info($"下载完成（{via}）：{asset.Name}（{info.Length} 字节）");
                    onProgress?.Invoke(100);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _log.Warn($"下载失败（{via}，第 {attempt} 次）：{ex.Message}");
                    TryDelete(target + ".part");
                    if (attempt < 2) await Task.Delay(800, ct).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            "直连和镜像都不通，学校网络可能屏蔽了 GitHub" + (lastError is null ? "" : "：" + lastError.Message),
            lastError);
    }

    /// <summary>文件已下载且大小、摘要都对得上（没有摘要一律视为不完整）。</summary>
    private static bool IsAssetComplete(string path, ReleaseAsset asset)
    {
        try
        {
            if (!File.Exists(path)) return false;
            if (asset.Size > 0 && new FileInfo(path).Length != asset.Size) return false;
            return !string.IsNullOrEmpty(asset.Digest) && VerifyDigest(path, asset.Digest).Ok;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeDirectoryName(string name)
    {
        var text = (name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        return text.Length == 0 ? "plugin" : text;
    }

    private UpdateStatus Ready() => new()
    {
        Current = _current,
        Latest = _release?.Version,
        Notes = _release?.Notes,
        HtmlUrl = _release?.HtmlUrl,
        AssetName = Path.GetFileName(_installerPath),
        AssetSize = _installerPath is not null && File.Exists(_installerPath) ? new FileInfo(_installerPath).Length : 0,
        Phase = UpdatePhase.Ready,
        // 走完下载才叫 Ready：进度条到这里必须满格，否则面板会显示「已下载」却停在 90%。
        Progress = UpdateProgress.Max,
        CheckedAt = _checkedAt
    };

    private UpdateStatus Fail(string message)
    {
        _log.Warn("更新：" + message);
        return Set(Status with { Phase = UpdatePhase.Failed, Error = message, Progress = 0 });
    }

    private UpdateStatus Set(UpdateStatus status)
    {
        lock (_lock) _status = Decorate(status);
        return _status;
    }

    /// <summary>把插件字段投影到状态上（调用方持有 _lock）。</summary>
    private UpdateStatus Decorate(UpdateStatus status) => status with
    {
        // 进度只允许 0–100：上游回调偶尔会给 NaN/越界值，别让界面画出满格或负条。
        Progress = UpdateProgress.Clamp(status.Progress),
        PluginDetected = _pluginDetected,
        PluginCurrent = _pluginCurrent,
        PluginLatest = _pluginLatest,
        PluginAvailable = _pluginAvailable,
        PluginProgress = Math.Clamp(_pluginProgress, 0, 1),
        PluginError = _pluginError,
        PluginText = _pluginText,
        PluginCanUpdate = _pluginAvailable && _pluginRelease is not null
    };

    /// <summary>插件字段变了但阶段没变时，把当前状态重新投影一遍（调用方持有 _lock）。</summary>
    private void TouchLocked() => _status = Decorate(_status);

    private static string UriHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 下次覆盖 */ }
    }

    private async Task DownloadToFileAsync(string url, ReleaseAsset asset, string target, Action<double>? onProgress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"ClassroomKit-Agent/{_current}");
        // 走 api.github.com 资产接口时，必须带这个头才会返回二进制而不是 JSON。
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var sink = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long received = 0;
            var lastReport = Environment.TickCount64;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                var now = Environment.TickCount64;
                if (now - lastReport >= 400)
                {
                    lastReport = now;
                    var pct = total > 0 ? Math.Min(99, received * 100.0 / total) : 0;
                    onProgress?.Invoke(pct);
                }
            }
        }

        File.Move(target + ".part", target, overwrite: true);
    }

    /// <summary>
    /// 分段并发下载：把文件按 8 MB 切片、最多 6 路同时拉，最后顺序合并。
    /// 每条连接的吞吐被限速时，多连接能成倍提速（用户机器上单连接约 50–70 KB/s）。
    /// 返回 false 表示「这个源不支持分段」，调用方回退单连接；其它异常也回退，不中断升级。
    /// </summary>
    private async Task<bool> TryParallelDownloadAsync(string url, ReleaseAsset asset, string target, Action<double>? onProgress, CancellationToken ct)
    {
        if (asset.Size <= 0) return false;

        const long chunkSize = 8L * 1024 * 1024;
        var parts = (int)Math.Min(8, Math.Max(1, (asset.Size + chunkSize - 1) / chunkSize));
        if (parts < 2) return false;

        var tempPaths = new string[parts];
        var lengths = new long[parts];
        var received = 0L;
        var lastReport = Environment.TickCount64;
        using var limit = new SemaphoreSlim(6);

        _log.Info($"分段下载：{parts} 片 × 8 MB，6 路并发");
        try
        {
            var tasks = new Task[parts];
            for (var i = 0; i < parts; i++)
            {
                var index = i;
                var start = index * chunkSize;
                var end = Math.Min(asset.Size, start + chunkSize) - 1;
                tempPaths[index] = $"{target}.part{index}";
                tasks[index] = Task.Run(async () =>
                {
                    await limit.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, url);
                        request.Headers.UserAgent.ParseAdd($"ClassroomKit-Agent/{_current}");
                        request.Headers.Accept.ParseAdd("application/octet-stream");
                        request.Headers.Range = new RangeHeaderValue(start, end);
                        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (response.StatusCode != HttpStatusCode.PartialContent)
                            throw new NotSupportedException("服务器不支持分段下载");

                        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using var sink = new FileStream(tempPaths[index], FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                        var buffer = new byte[81920];
                        long partBytes = 0;
                        int read;
                        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            partBytes += read;
                            var total = Interlocked.Add(ref received, read);
                            var now = Environment.TickCount64;
                            if (now - lastReport >= 400)
                            {
                                lastReport = now;
                                var pct = Math.Min(99, total * 100.0 / asset.Size);
                                onProgress?.Invoke(pct);
                            }
                        }
                        lengths[index] = partBytes;
                    }
                    finally
                    {
                        limit.Release();
                    }
                }, ct);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            long sum = 0;
            foreach (var len in lengths) sum += len;
            if (sum != asset.Size)
                throw new InvalidDataException($"分段下载不完整（{sum}/{asset.Size} 字节）");

            await using (var merged = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                for (var i = 0; i < parts; i++)
                {
                    await using var src = new FileStream(tempPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    await src.CopyToAsync(merged, ct).ConfigureAwait(false);
                }
            }
            File.Move(target + ".part", target, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"分段下载失败（{ex.Message}），回退单连接");
            return false;
        }
        finally
        {
            foreach (var part in tempPaths)
                if (part is not null) TryDelete(part);
            TryDelete(target + ".part");
        }
    }

    private static (bool Ok, string Message) VerifyDigest(string filePath, string digest)
    {
        // GitHub 的 digest 形如 "sha256:abcdef…"
        var colon = digest.IndexOf(':');
        if (colon <= 0) return (true, "没有可校验的摘要");
        var algorithm = digest[..colon].Trim().ToLowerInvariant();
        var expected = digest[(colon + 1)..].Trim().ToLowerInvariant();
        if (algorithm != "sha256" || expected.Length != 64) return (true, "摘要格式不认识，跳过校验");

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return actual == expected
            ? (true, "sha256 校验通过")
            : (false, $"sha256 不匹配（期望 {expected[..12]}…，实际 {actual[..12]}…）");
    }

    private static HttpClient CreateHttp()
    {
        // 学校网络到 GitHub 可能只有几十 KB/s，78 MB 安装包 5 分钟根本下不完。
        // 用 2 小时兜底，真正的取消由用户操作传入的 CancellationToken 负责。
        var client = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassroomKit-Agent");
        return client;
    }
}
