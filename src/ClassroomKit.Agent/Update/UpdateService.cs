using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ClassroomKit.Agent.Shell;
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
        UpdatePhase.Installing => "正在安装，程序会短暂退出…",
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

    public bool HasUpdate => Phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready or UpdatePhase.Installing;
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

    public UpdateService(AppLog log, Func<AppConfig> config, string currentVersion, Action requestExit)
    {
        _log = log;
        _config = config;
        _current = UpdateVersion.Normalize(currentVersion);
        _requestExit = requestExit;
        _status = new UpdateStatus { Current = _current };
    }

    public string CurrentVersion => _current;

    public UpdateStatus Status
    {
        get { lock (_lock) return _status; }
    }

    public static string UpdateRoot => Path.Combine(Paths.DataDir, "update");

    private UpdateConfig Config => _config().Update ?? new UpdateConfig();

    /// <summary>检查 GitHub 上是否有更新版本；10 分钟内重复调用直接返回缓存。</summary>
    public async Task<UpdateStatus> CheckAsync(bool force = false, CancellationToken ct = default)
    {
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
            _installerPath = null;

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

            if (File.Exists(target) && asset.Size > 0 && new FileInfo(target).Length == asset.Size &&
                (asset.Digest is null || VerifyDigest(target, asset.Digest) is { Ok: true }))
            {
                _log.Info($"安装包已存在，跳过下载：{target}");
                return Set(Ready());
            }

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

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.ParseAdd($"ClassroomKit-Agent/{_current}");
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
                        Set(Status with { Progress = pct });
                    }
                }
            }

            File.Move(target + ".part", target, overwrite: true);

            var info = new FileInfo(target);
            if (asset.Size > 0 && info.Length != asset.Size)
            {
                return Fail($"下载不完整（{info.Length}/{asset.Size} 字节），请重试");
            }
            if (asset.Digest is not null)
            {
                var (ok, message) = VerifyDigest(target, asset.Digest);
                if (!ok) return Fail("安装包校验不通过：" + message);
            }

            _log.Info($"安装包下载完成：{target}（{info.Length} 字节）");
            return Set(Ready());
        }
        catch (OperationCanceledException)
        {
            return Fail("下载已取消");
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
    /// 启动安装器：写一个独立 cmd（等本进程退出 → msiexec /qb → 兜底拉起新版本），
    /// 以管理员身份运行它，然后请求本进程退出。返回的 message 直接给面板/托盘显示。
    /// </summary>
    public (bool Ok, string Message) Install()
    {
        var path = _installerPath;
        if (path is null || !File.Exists(path))
            return (false, "安装包还没下载好，请先下载");

        if (!path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return (false, "当前版本只支持用 MSI 安装包升级，请到 GitHub Release 手动解压 zip");

        var logPath = Path.Combine(Paths.LogDir, "update.log");
        var scriptPath = Path.Combine(UpdateRoot, "apply-update.cmd");
        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            var script =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                $"echo [%date% %time%] 开始安装 {Path.GetFileName(path)}>>\"{logPath}\"\r\n" +
                $"msiexec /i \"{path}\" /qb /norestart\r\n" +
                $"echo [%date% %time%] msiexec 退出码=%ERRORLEVEL%>>\"{logPath}\"\r\n" +
                "timeout /t 3 /nobreak >nul\r\n" +
                "set \"EXE=%ProgramFiles%\\ClassroomKit\\ClassroomKit.Agent.exe\"\r\n" +
                "if exist \"%EXE%\" (\r\n" +
                "  tasklist /FI \"IMAGENAME eq ClassroomKit.Agent.exe\" 2>nul | find /I \"ClassroomKit.Agent.exe\" >nul\r\n" +
                "  if errorlevel 1 start \"\" \"%EXE%\"\r\n" +
                ")\r\n" +
                "exit /b 0\r\n";
            File.WriteAllText(scriptPath, script, Encoding.Default);

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath()
            };
            if (!AdminElevation.IsElevated()) psi.Verb = "runas";

            var process = Process.Start(psi);
            _log.Info($"已启动升级脚本（PID {process?.Id}）：{Path.GetFileName(path)}");

            Set(Status with { Phase = UpdatePhase.Installing, Error = null });

            // 给 UAC 授权与脚本的 timeout 留出时间，再安静退出，避免 MSI 替换文件时被占用。
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500).ConfigureAwait(false);
                try { _requestExit(); } catch { /* 退出路径由上层决定 */ }
            });

            return (true, "正在安装新版本：会弹一次管理员授权，程序随后自动重启（面板会断开几秒）");
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

    private UpdateStatus Ready() => new()
    {
        Current = _current,
        Latest = _release?.Version,
        Notes = _release?.Notes,
        HtmlUrl = _release?.HtmlUrl,
        AssetName = Path.GetFileName(_installerPath),
        AssetSize = _installerPath is not null && File.Exists(_installerPath) ? new FileInfo(_installerPath).Length : 0,
        Phase = UpdatePhase.Ready,
        CheckedAt = _checkedAt
    };

    private UpdateStatus Fail(string message)
    {
        _log.Warn("更新：" + message);
        return Set(Status with { Phase = UpdatePhase.Failed, Error = message, Progress = 0 });
    }

    private UpdateStatus Set(UpdateStatus status)
    {
        lock (_lock) _status = status;
        return status;
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
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ClassroomKit-Agent");
        return client;
    }
}
