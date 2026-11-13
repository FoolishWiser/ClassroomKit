using System.Reflection;
using ClassroomKit.Agent.Board;
using ClassroomKit.Agent.Hosts;
using ClassroomKit.Agent.Bcu;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Agent.Update;
using ClassroomKit.Core.Api;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent;

/// <summary>把配置、日志、WinRT 后端、守护状态机、HTTP、托盘串起来。</summary>
public sealed class AgentRuntime : IDisposable
{
    private readonly MiniHttpServer _http;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset? _pauseUntil;
    private bool _enabledBeforePause;

    public AgentRuntime()
    {
        Config = new ConfigStore(Paths.ConfigPath);
        Log = new AppLog(Paths.LogDir);
        Uplink = new UplinkProbe(Log);
        Tethering = new TetheringService(Uplink, () => Config.Current);
        Guard = new HotspotGuard(Tethering, () => Config.Current, Log);

        var version = DisplayVersion();
        Api = new ApiServer(Config, Log, Paths.WebRoot, version);
        _http = new MiniHttpServer(Config.Current.Port, Api.HandleAsync, Log);

        Api.AttachGuard(Guard);
        Api.ApplyPolicy = SetPolicy;
        Api.ApplySettings = ApplySettings;
        Api.FixPeersAsync = (ct) => Task.FromResult(Commands.FixPeersCore(Log));
        Api.LaunchBcuAsync = (ct) => Task.FromResult(BcuLauncher.Launch(Config.Current.Bcu));
        Api.GetUplinks = UplinksDto;

        Update = new UpdateService(Log, () => Config.Current, version, () => ExitRequested?.Invoke());
        Api.GetUpdate = UpdateDto;
        Api.CheckUpdateAsync = async (force, ct) =>
        {
            var status = await Update.CheckAsync(force, ct).ConfigureAwait(false);
            return UpdateDto(status);
        };
        Api.StartUpgrade = StartUpgrade;
        Api.Uninstall = Uninstall;
        Api.RequestExit = () => ExitRequested?.Invoke();

        Hosts = new HostsService(Log, () => Config.Current);
        Api.GetHosts = () => Hosts.StatusDto();
        Api.RefreshHosts = Hosts.RefreshAsync;
        Api.RestoreHosts = Hosts.RestoreAsync;

        Recipe = UiRecipe.Load(Paths.RecipePath);
        BoardSaver = new ClassInBoardSaver(Log, Recipe, () => Config.Current.BoardSave);
        ConflictWatcher = new ConflictWatcher(Log, () => Config.Current.Conflict, () => Config.Current.Conflict.ExtraFullscreenProcesses);

        Api.SaveBoard = SaveBoard;
        Api.ConflictCheck = ConflictWatcher.CheckNow;
        Api.GetBoard = BoardDto;
        Api.ConflictChoice = ApplyConflictChoice;
        Api.PreviewConflict = () =>
        {
            PreviewConflictRequested?.Invoke();
            return (true, "已请求弹出预览窗（只在屏幕上显示，不会关闭任何软件）");
        };
    }

    public ConfigStore Config { get; }
    public AppLog Log { get; }
    public UplinkProbe Uplink { get; }
    public TetheringService Tethering { get; }
    public HotspotGuard Guard { get; }
    public ApiServer Api { get; }
    public UiRecipe Recipe { get; }
    public ClassInBoardSaver BoardSaver { get; }
    public ConflictWatcher ConflictWatcher { get; }
    public UpdateService Update { get; }
    public HostsService Hosts { get; }

    /// <summary>面板/接口请求预览冲突窗时触发（由托盘层弹到 UI 线程）。</summary>
    public Action? PreviewConflictRequested { get; set; }

    /// <summary>由托盘层注入：退出整个进程。</summary>
    public Action? ExitRequested { get; set; }

    public string BaseUrl => $"http://127.0.0.1:{Config.Current.Port}/";
    public string PanelUrl => $"{BaseUrl}?t={Config.Current.Token}";

    public bool IsPaused => _pauseUntil is not null && DateTimeOffset.Now < _pauseUntil;

    public TimeSpan? PauseRemaining => _pauseUntil is null ? null : _pauseUntil - DateTimeOffset.Now;

    /// <summary>
    /// 面板/API 显示的版本号：保留 -pre.N 预发布后缀，去掉 SourceLink 追加的 +commit 构建元数据。
    /// </summary>
    public static string DisplayVersion()
    {
        var asm = typeof(AgentRuntime).Assembly;
        var info = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public void Start()
    {
        Log.Info($"ClassroomKit Agent 启动 v{DisplayVersion()}");
        Log.Info($"配置：{Paths.ConfigPath}｜面板：{BaseUrl}｜上行策略：{(Config.Current.Hotspot.AllowWifiUplink ? "允许 Wi-Fi" : "仅有线")}");
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            if (self.SessionId == 0)
                Log.Warn("当前运行在 Session 0（没有交互桌面）：热点照常工作，但托盘图标不会出现；从开始菜单「ClassroomKit 控制面板」重新启动，或重新登录即可看到托盘");
        }

        try
        {
            _http.Start();
            Log.Info($"Web 面板已监听 127.0.0.1:{Config.Current.Port}");
        }
        catch (Exception ex)
        {
            Log.Error($"Web 面板启动失败（端口 {Config.Current.Port}）：{ex.Message}");
        }

        ConflictWatcher.Start();
        _ = Task.Run(GuardLoopAsync);
        _ = Task.Run(HostsLoopAsync);

        if (Config.Current.Hotspot.AutoStartOnLaunch && !Config.Current.Hotspot.Enabled)
        {
            SetPolicy(true);
        }
    }

    private readonly SemaphoreSlim _wake = new(0, 1);

    private async Task GuardLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused)
                {
                    await Guard.TickOnceAsync(_cts.Token);
                }
                else if (_pauseUntil is not null)
                {
                    Log.Debug($"守护暂停中，剩余 {(_pauseUntil.Value - DateTimeOffset.Now).TotalMinutes:F0} 分钟");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"巡检异常：{ex.Message}");
            }

            try
            {
                // 有唤醒信号（用户在面板点了开/关）就立刻再跑一次，否则按巡检间隔等
                var seconds = Math.Clamp(Config.Current.Hotspot.GuardIntervalSec, 3, 300);
                await _wake.WaitAsync(TimeSpan.FromSeconds(seconds), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>
    /// GitHub 护航巡检：启动 1 分钟后先看一眼，之后每小时醒一次。
    /// 真正要不要改 hosts 由 HostsService 决定（能直连就什么都不动）。
    /// </summary>
    private async Task HostsLoopAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token); }
        catch (OperationCanceledException) { return; }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Hosts.TickAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"hosts 护航异常：{ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromHours(1), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void SetPolicy(bool enabled)
    {
        Guard.SetEnabled(enabled);
        Config.Update(c =>
        {
            c.Hotspot.Enabled = enabled;
            return true;
        });
        if (enabled) ClearPause();
        Wake();
    }

    public void PauseFor(TimeSpan duration)
    {
        _enabledBeforePause = Config.Current.Hotspot.Enabled;
        _pauseUntil = DateTimeOffset.Now + duration;
        Guard.SetEnabled(false);
        Config.Update(c =>
        {
            c.Hotspot.Enabled = false;
            return true;
        });
        Log.Warn($"守护暂停 {duration.TotalMinutes:F0} 分钟（期间不会自动开热点）");
    }

    public void ClearPause()
    {
        if (_pauseUntil is null) return;
        _pauseUntil = null;
        if (_enabledBeforePause)
        {
            Guard.SetEnabled(true);
            Config.Update(c =>
            {
                c.Hotspot.Enabled = true;
                return true;
            });
        }
        Log.Info("守护已恢复");
    }

    private void ApplySettings(ApiServer.UserSettings settings)
    {
        var uplinkChanged = !string.Equals(Config.Current.Uplink.Mode, settings.UplinkMode, StringComparison.OrdinalIgnoreCase);

        Config.Update(c =>
        {
            c.Hotspot.Ssid = settings.Ssid;
            c.Hotspot.Passphrase = settings.Passphrase;
            c.Hotspot.TargetClients = settings.TargetClients;
            c.Hotspot.AllowWifiUplink = settings.AllowWifiUplink;
            c.Uplink.Mode = string.IsNullOrWhiteSpace(settings.UplinkMode) ? UplinkSelector.Auto : settings.UplinkMode.Trim();
            c.Uplink.FallbackToLan = settings.FallbackToLan;
            return true;
        });
        Log.Info($"设置已更新：SSID={settings.Ssid}｜目标 {settings.TargetClients} 台｜上行 {(uplinkChanged ? settings.UplinkMode : Config.Current.Uplink.Mode)}");

        if (uplinkChanged)
        {
            // 换了上行网卡就得重开一次热点，否则系统还按旧的网卡共享
            _ = Task.Run(async () =>
            {
                try
                {
                    await Tethering.StopAsync(_cts.Token);
                    Log.Info("上行网卡已切换，热点已关闭，等待守护用新网卡重开");
                    Wake();
                }
                catch (Exception ex)
                {
                    Log.Warn($"切换上行时关闭热点失败：{ex.Message}");
                }
            });
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await Tethering.ConfigureAccessPointAsync(settings.Ssid, settings.Passphrase, _cts.Token);
                Log.Info(ok ? "热点名称/密码已应用" : "热点名称/密码应用失败，下次巡检会重试");
            }
            catch (Exception ex)
            {
                Log.Warn($"应用热点配置失败：{ex.Message}");
            }
        });
    }

    /// <summary>面板 / 接口用的板书与冲突状态。</summary>
    private object BoardDto()
    {
        var cfg = Config.Current;
        var situation = ConflictWatcher.LastSituation;
        var guard = ConflictWatcher.Guard;
        return new
        {
            classIn = new
            {
                running = ClassInBoardSaver.IsClassInRunning(),
                saveFolder = ClassInBoardSaver.SaveFolder
            },
            boardSave = new
            {
                enabled = cfg.BoardSave.Enabled,
                closeAfterSave = cfg.BoardSave.CloseAfterSave,
                launchIfNeeded = cfg.BoardSave.LaunchIfNeeded,
                launchTimeoutSec = cfg.BoardSave.LaunchTimeoutSec,
                classInPath = cfg.BoardSave.ClassInPath,
                recipeSource = Recipe.Source,
                recipeProbedAt = Recipe.ProbedAt
            },
            conflict = new
            {
                enabled = cfg.Conflict.Enabled,
                cooldownMinutes = cfg.Conflict.CooldownMinutes,
                situation = situation.Describe(),
                shouldPrompt = situation.ShouldPrompt,
                classInFloating = situation.ClassInFloating,
                seewoFloating = situation.SeewoFloating,
                classInFullscreen = situation.ClassInFullscreen,
                seewoFullscreen = situation.SeewoFullscreen,
                wpsShow = situation.WpsShow,
                otherFullscreen = situation.OtherFullscreen,
                lastPromptAt = guard.LastPromptAt,
                promptCount = guard.PromptCount,
                lastReason = guard.LastReason
            }
        };
    }

    private static bool TryParseChoice(string text, out ConflictChoice choice)
    {
        switch (text.Replace("-", "").Replace("_", "").Trim().ToLowerInvariant())
        {
            case "keepboth":
            case "both":
            case "都保留": choice = ConflictChoice.KeepBoth; return true;
            case "keepclassin":
            case "classin":
            case "保留classin": choice = ConflictChoice.KeepClassIn; return true;
            case "keepseewo":
            case "seewo":
            case "保留希沃": choice = ConflictChoice.KeepSeewo; return true;
            case "keepnone":
            case "none":
            case "都不保留": choice = ConflictChoice.KeepNone; return true;
            default: choice = ConflictChoice.KeepBoth; return false;
        }
    }

    private (bool Ok, string Message) ApplyConflictChoice(string text)
    {
        if (!TryParseChoice(text, out var choice))
            return (false, $"无法识别的选择：{text}（可用 keep-both / keep-classin / keep-seewo / keep-none）");

        ConflictWatcher.ApplyChoice(choice);
        return (true, $"已按「{choice}」处理");
    }

    private (bool Ok, string Message) SaveBoard(bool closeAfterSave, bool launchIfNeeded)
    {
        if (!Config.Current.BoardSave.Enabled)
            return (false, "「保存 ClassInX 板书」已在配置里关闭");

        var result = BoardSaver.Save(closeAfterSave, launchIfNeeded);
        return (result.Ok, result.Message);
    }

    /// <summary>面板“上行网卡”下拉框的数据：所有候选 + 当前选中 + 选择理由。</summary>
    private object UplinksDto()
    {
        var cfg = Config.Current;
        var choice = Uplink.Resolve(cfg.Uplink, cfg.Hotspot.AllowWifiUplink);
        var chosenId = choice.Chosen?.Id;

        return new
        {
            mode = cfg.Uplink.Mode,
            allowWifiUplink = cfg.Hotspot.AllowWifiUplink,
            fallbackToLan = cfg.Uplink.FallbackToLan,
            source = choice.Source.ToString(),
            reason = choice.Reason,
            describe = choice.Describe(),
            chosen = choice.Chosen is null ? null : new
            {
                id = choice.Chosen.Id,
                label = choice.Chosen.Label,
                isWifi = choice.Chosen.IsWifi,
                hasInternet = choice.Chosen.HasInternet,
                ipv4 = choice.Chosen.Ipv4
            },
            candidates = choice.Candidates.Select(c => new
            {
                id = c.Id,
                name = c.Name,
                label = c.Label,
                description = c.Description,
                isWired = c.IsWired,
                isWifi = c.IsWifi,
                hasGateway = c.HasGateway,
                hasInternet = c.HasInternet,
                isTethering = c.IsTethering,
                ipv4 = c.Ipv4,
                metric = c.Metric,
                usable = c.Usable,
                chosen = string.Equals(c.Id, chosenId, StringComparison.OrdinalIgnoreCase)
            }).ToArray()
        };
    }

    /// <summary>升级状态 DTO（字段名给面板直接用）。</summary>
    private object UpdateDto() => UpdateDto(Update.Status);

    private object UpdateDto(UpdateStatus status) => new
    {
        phase = status.Phase.ToString().ToLowerInvariant(),
        phaseText = status.PhaseText,
        current = status.Current,
        latest = status.Latest,
        notes = status.Notes,
        htmlUrl = status.HtmlUrl,
        assetName = status.AssetName,
        assetSize = status.AssetSize,
        progress = Math.Round(status.Progress, 1),
        hasUpdate = status.HasUpdate,
        error = status.Error,
        checkedAt = status.CheckedAt
    };

    /// <summary>面板点「升级」：后台下载 → 校验 → 调起安装器；立刻把当前状态返回给页面。</summary>
    private object StartUpgrade()
    {
        var status = Update.Status;
        if (status.Phase is UpdatePhase.Installing or UpdatePhase.Downloading) return UpdateDto(status);
        if (!status.HasUpdate)
        {
            Log.Warn("升级请求被忽略：当前没有可安装的新版本");
            return UpdateDto(status);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var after = await Update.DownloadAsync().ConfigureAwait(false);
                if (after.Phase != UpdatePhase.Ready)
                {
                    Log.Warn($"升级中止：{after.Error ?? after.PhaseText}");
                    return;
                }

                var (ok, message) = Update.Install();
                Log.Info(ok ? $"升级已启动：{message}" : $"升级未启动：{message}");
            }
            catch (Exception ex)
            {
                Log.Error($"后台升级异常：{ex.Message}");
            }
        });

        return UpdateDto();
    }

    private (bool Ok, string Message) Uninstall(bool keepData)
    {
        var (ok, message) = SelfUninstaller.Start(keepData, Log.Warn);
        if (ok)
        {
            // 先让 HTTP 响应发出去，再退出自己（脚本会等 2 秒后 taskkill）
            _ = Task.Run(async () =>
            {
                await Task.Delay(800);
                ExitRequested?.Invoke();
            });
        }
        return (ok, message);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _http.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
