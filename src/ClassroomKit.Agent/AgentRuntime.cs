using System.Reflection;
using ClassroomKit.Agent.Board;
using ClassroomKit.Agent.Diagnose;
using ClassroomKit.Agent.Hosts;
using ClassroomKit.Agent.Input;
using ClassroomKit.Agent.Lesson;
using ClassroomKit.Agent.Bcu;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Agent.Update;
using ClassroomKit.Core.Api;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Update;

namespace ClassroomKit.Agent;

/// <summary>把配置、日志、WinRT 后端、守护状态机、HTTP、托盘串起来。</summary>
public sealed class AgentRuntime : IDisposable
{
    private readonly MiniHttpServer _http;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset? _pauseUntil;
    private bool _enabledBeforePause;
    private string? _autoSaveSkipKey;

    public AgentRuntime()
    {
        Config = new ConfigStore(Paths.ConfigPath);
        Log = new AppLog(Paths.LogDir);
        Uplink = new UplinkProbe(Log);
        Tethering = new TetheringService(Uplink, () => Config.Current);
        Guard = new HotspotGuard(Tethering, () => Config.Current, Log);

        var version = DisplayVersion();
        Api = new ApiServer(Config, Log, version);
        _http = new MiniHttpServer(Config.Current.Port, Api.HandleAsync, Log);

        Api.AttachGuard(Guard);
        Api.RepairNetwork = ct => Guard.RepairNowAsync(ct);
        Api.ApplyPolicy = SetPolicy;
        Api.ApplySettings = ApplySettings;
        // HTTP 入口不许弹 UAC：用户可能不在电脑前，安全桌面会把整台机器卡住
        Api.FixPeersAsync = (ct) => Task.FromResult(Commands.FixPeersCore(Log, allowPrompt: false));
        Api.LaunchBcuAsync = (ct) => Task.FromResult(BcuLauncher.Launch(Config.Current.Bcu));
        Api.GetUplinks = UplinksDto;

        Update = new UpdateService(Log, () => Config.Current, version, () => ExitRequested?.Invoke());
        Api.GetUpdate = UpdateDto;
        Api.CheckUpdateAsync = async (force, ct) =>
        {
            var status = await Update.CheckAsync(force, ct).ConfigureAwait(false);
            return UpdateDto(status);
        };
        Api.StartUpgrade = _ => StartUpgradeAsync();
        Api.Uninstall = Uninstall;
        Api.RequestExit = () => ExitRequested?.Invoke();

        Hosts = new HostsService(Log, () => Config.Current);
        Api.GetHosts = () => Hosts.StatusDto();
        Api.RefreshHosts = Hosts.RefreshAsync;
        Api.RestoreHosts = Hosts.RestoreAsync;
        Api.InstallService = InstallServiceTask;

        Recipe = UiRecipe.Load(Paths.RecipePath);
        BoardSaver = new ClassInBoardSaver(Log, Recipe, () => Config.Current.BoardSave);

        // 先起课表与科目，冲突巡检才能按「当前科目」查老师习惯。
        Subjects = new ClassIslandSubjectCatalog(Log);
        Lesson = new LessonRuntime(Log, () => Config.Current.Lesson, Paths.LessonStatePath);
        Lesson.AvailableSubjects = () => Subjects.Current;
        AutoSave = new BoardAutoSaveTracker(() => Config.Current, log: Log);

        ConflictWatcher = new ConflictWatcher(Log, () => Config.Current.Conflict,
            () => Config.Current.Habits,
            () => Config.Current.Conflict.ExtraFullscreenProcesses,
            () => Lesson.State().Snapshot?.CurrentSubject,
            SaveConflictSnooze);

        Api.SaveBoard = SaveBoard;
        Api.SaveBoardNamed = SaveBoardNamed;
        Api.GetLesson = Lesson.Status;
        Api.AcceptLesson = Lesson.Accept;
        Api.ClearLesson = Lesson.Clear;
        Api.GetBoardAutoSave = () => AutoSave.State;
        Api.BoardAutoSaveAction = AutoSaveAction;
        Api.Diagnose = _ => Task.FromResult(Diagnostics.Build(this));
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
    public ClassIslandSubjectCatalog Subjects { get; }
    public LessonRuntime Lesson { get; }

    /// <summary>课后自动保存状态机（1.0.0 起）。</summary>
    public BoardAutoSaveTracker AutoSave { get; }

    /// <summary>面板/接口请求预览冲突窗时触发（由托盘层弹到 UI 线程）。</summary>
    public Action? PreviewConflictRequested { get; set; }

    /// <summary>由托盘层注入：退出整个进程。</summary>
    public Action? ExitRequested { get; set; }

    public string BaseUrl => $"http://127.0.0.1:{Config.Current.Port}/";

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
        Log.Info(AdminElevation.IsElevated()
            ? "权限：管理员（最高可用）"
            : "权限：标准用户（热点/hosts 的部分操作可能失败，建议用管理员账户）");
        Log.Info($"配置：{Paths.ConfigPath}｜本地 API：{BaseUrl}｜上行策略：{(Config.Current.Hotspot.AllowWifiUplink ? "允许 Wi-Fi" : "仅有线")}");
        if (!string.IsNullOrWhiteSpace(Config.LoadError)) Log.Error(Config.LoadError);
        // 上一次如果是被强杀/断电退出的，可能把 ALT 或鼠标左键留在按下状态；开机先清一遍。
        try { Board.Input.ReleaseAllInputs(); } catch { }

        using (var self = System.Diagnostics.Process.GetCurrentProcess())
        {
            if (self.SessionId == 0)
                Log.Warn("当前运行在 Session 0（没有交互桌面）：热点照常工作，但托盘图标不会出现；从开始菜单里的 ClassroomKit 重新启动，或重新登录即可看到托盘");
        }

        try
        {
            _http.Start();
            Log.Info($"本地 API 已监听 127.0.0.1:{Config.Current.Port}");
        }
        catch (Exception ex)
        {
            Log.Error($"本地 API 启动失败（端口 {Config.Current.Port}）：{ex.Message}");
        }

        ConflictWatcher.Start();
        _ = Task.Run(GuardLoopAsync);
        _ = Task.Run(LessonLoopAsync);
        _ = Task.Run(SubjectLoopAsync);
        _ = Task.Run(HostsLoopAsync);
        _ = Task.Run(UpdateLoopAsync);

        if (Config.Current.Hotspot.AutoStartOnLaunch && !Config.Current.Hotspot.Enabled)
        {
            SetPolicy(true);
        }
    }

    /// <summary>每分钟重读一次 ClassIsland 档案里的科目；扫描根目录的结果缓存 5 分钟。</summary>
    private async Task SubjectLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                Subjects.Refresh();
            }
            catch (Exception ex)
            {
                Log.Debug("读取 ClassIsland 科目失败：" + ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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

    /// <summary>
    /// 更新巡检：启动 90 秒后先查一次（开机自检），之后每 checkIntervalHours 小时查一次；
    /// 开了 autoInstall 就只在时间窗内、且（默认）没有学生连着热点时才静默装 —— 上课中绝不重启热点。
    /// </summary>
    private async Task UpdateLoopAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(90), _cts.Token); }
        catch (OperationCanceledException) { return; }

        var first = true;
        var nextCheckAt = DateTimeOffset.UtcNow;
        string? lastPendingNote = null;
        while (!_cts.IsCancellationRequested)
        {
            var cfg = Config.Current.Update ?? new UpdateConfig();
            var bootCheck = false;
            var dueCheck = false;
            try
            {
                // 上次升级时 ClassIsland 正开着、插件没写进去：每 5 分钟补装一次，消息没变就不重复刷日志
                if (Update.HasPendingPlugin)
                {
                    var (pluginOk, pluginMessage) = Update.TryApplyPendingPlugin();
                    if (pluginOk || pluginMessage != lastPendingNote)
                        Log.Info(pluginOk ? "补装 ClassIsland 插件：" + pluginMessage : pluginMessage);
                    lastPendingNote = pluginOk ? null : pluginMessage;
                }

                var now = DateTimeOffset.UtcNow;
                bootCheck = first && cfg.Enabled && cfg.CheckOnBoot;
                dueCheck = !first && cfg.Enabled && now >= nextCheckAt;
                if (first && !bootCheck)
                    nextCheckAt = now.AddHours(cfg.CheckIntervalHours <= 0 ? 6 : Math.Clamp(cfg.CheckIntervalHours, 1, 168));
                if (bootCheck || dueCheck)
                {
                    var status = await Update.CheckAsync(false, _cts.Token).ConfigureAwait(false);
                    if (first) Log.Info($"开机自检更新：{status.PhaseText}{(status.Latest is null ? "" : $"（远端 v{status.Latest}）")}");

                    if (cfg.AutoInstall && status.HasUpdate && CanInstallNow(cfg))
                    {
                        Log.Info($"自动安装新版本：v{status.Current} → v{status.Latest}（{InstallWindow.Describe(cfg.InstallWindow)}）");
                        var after = await Update.DownloadAsync(_cts.Token).ConfigureAwait(false);
                        if (after.Phase == UpdatePhase.Ready)
                        {
                            var (ok, message) = Update.Install();
                            Log.Info(ok ? "自动安装已启动：" + message : "自动安装未启动：" + message);
                        }
                        else
                        {
                            Log.Warn("自动安装中止：" + (after.Error ?? after.PhaseText));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn("更新巡检异常：" + ex.Message);
            }

            var finishedAt = DateTimeOffset.UtcNow;
            if (bootCheck || dueCheck)
                nextCheckAt = finishedAt.AddHours(cfg.CheckIntervalHours <= 0 ? 6 : Math.Clamp(cfg.CheckIntervalHours, 1, 168));
            first = false;

            // 最多睡 5 分钟：待装插件随时可能出现（比如手动更新插件时 ClassIsland 正开着），不能等下一个 6 小时巡检
            TimeSpan delay;
            if (Update.HasPendingPlugin)
                delay = TimeSpan.FromMinutes(5);
            else if (!cfg.Enabled)
                delay = TimeSpan.FromMinutes(5);
            else
            {
                var remain = nextCheckAt - finishedAt;
                delay = remain < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30)
                    : remain > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5)
                    : remain;
            }
            try { await Task.Delay(delay, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>自动安装的前置条件：时间窗打开（默认不限），且（默认）没有设备连着热点。</summary>
    private bool CanInstallNow(UpdateConfig cfg)
    {
        if (!InstallWindow.IsOpen(cfg.InstallWindow, TimeOnly.FromDateTime(DateTime.Now))) return false;
        if (cfg.InstallWithClients) return true;

        var clients = Guard.Clients.Count;
        if (clients > 0)
        {
            Log.Info($"有 {clients} 台设备连着热点，自动升级推迟到设备断开后（可设 update.installWithClients=true 强制）");
            return false;
        }

        return true;
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

    /// <summary>弹窗里勾了「今天不再提醒」/面板上清除后调这里：只改配置里的日期，改完下一次巡检就生效。</summary>
    private void SaveConflictSnooze(bool snooze)
    {
        var date = snooze ? ConflictSnooze.Today(DateTimeOffset.Now) : "";
        Config.Update(c =>
        {
            c.Conflict.SnoozeDate = date;
            return 0;
        });
        Log.Info(snooze ? $"板书冲突提示：今天（{date}）不再提醒" : "板书冲突提示：已恢复提醒");
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

    /// <summary>
    /// 课后自动保存巡检：每 1 秒看一次课表与空闲状态。
    /// 下课（或放学）后只 Arming 一次；15 秒无键鼠输入才真正保存，点击会打断并 10 秒后重试。
    /// </summary>
    private async Task LessonLoopAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token); }
        catch (OperationCanceledException) { return; }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.Now;
                var cfg = Config.Current.Lesson;
                var snapshot = Lesson.State().Snapshot;

                if (cfg.Enabled && snapshot is not null)
                {
                    var decision = Lesson.Evaluate(now);
                    if (decision.Show)
                    {
                        // 一次巡检只枚举一次进程，避免「判定时在运行、Arm 时已退出」的错位。
                        var running = ClassInBoardSaver.IsClassInRunning();
                        if (cfg.OnlyWhenClassInBoard && !running)
                        {
                            if (!string.Equals(_autoSaveSkipKey, decision.Key, StringComparison.Ordinal))
                            {
                                _autoSaveSkipKey = decision.Key;
                                Log.Info("课后自动保存：ClassInX 没有运行，这次跳过；启动后会自动重试");
                            }
                        }
                        else
                        {
                            _autoSaveSkipKey = null;
                            var armed = AutoSave.Arm(snapshot, now, running);
                            // 只有真的进入等待才算「已提醒」，否则本次课间还有机会重试。
                            if (armed.Phase is BoardAutoSavePhases.Waiting or BoardAutoSavePhases.Countdown or
                                BoardAutoSavePhases.Cooldown)
                            {
                                Lesson.MarkReminded(decision.Key);
                                var when = snapshot.State == LessonStates.AfterSchool ? "已经放学" : "已经下课";
                                Log.Info($"课后自动保存：{decision.Subject ?? "上一节"}{when}，开始等待空闲");
                            }
                        }
                    }
                }

                var tick = AutoSave.Tick(now, LastInputMonitor.LastInputAt(), snapshot);
                if (tick.StartSave)
                {
                    var generation = tick.Generation;
                    AutoSave.MarkSaving(generation);
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            var (ok, message) = SaveBoardNamed(
                                Config.Current.BoardSave.CloseAfterSave,
                                Config.Current.BoardSave.NameWithLesson);
                            AutoSave.Complete(generation, ok, message);
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"课后自动保存异常：{ex.Message}");
                            AutoSave.Complete(generation, false, "保存板书异常：" + ex.Message);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"课后自动保存巡检异常：{ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal (bool Ok, string Message) AutoSaveAction(string action)
    {
        var now = DateTimeOffset.Now;
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "preview":
            {
                var state = AutoSave.Preview(now);
                if (!string.Equals(state.Key, "preview", StringComparison.Ordinal))
                    return (false, "当前有自动保存正在进行，稍后再预览");
                return (true, "已开始预览自动保存倒计时（15 秒后不会真的保存）");
            }
            case "cancel":
            {
                var state = AutoSave.Cancel(now);
                if (state.Phase == BoardAutoSavePhases.Saving)
                    return (false, "板书正在保存，无法取消");
                return (true, "已取消本次自动保存");
            }
            default:
                return (false, "未知动作，可用 preview / cancel");
        }
    }

    /// <summary>保存板书并按「上一节课」改名（nameWithLesson=false 时保持原样）。</summary>
    /// <summary>安装/修复 SYSTEM 护航任务：只弹一次 UAC，装完立刻跑一次。</summary>
    internal (bool Ok, string Message) InstallServiceTask()
    {
        if (Tasks.ScheduledTaskInstaller.IsServiceTaskPresent())
        {
            var (woke, wakeMessage) = Tasks.ScheduledTaskInstaller.RunServiceNow();
            return woke
                ? (true, "护航任务已存在，已让它立即运行一次")
                : (false, "护航任务已存在，但触发失败：" + wakeMessage);
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Paths.ExePath, "--install-service")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            if (!Shell.AdminElevation.CanPrompt())
                return (false, "当前没有可交互的桌面会话，护航任务需要到电脑前手动装一次");

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return (false, "无法启动安装进程");
            if (!process.WaitForExit(120000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "安装护航任务超时（2 分钟），已终止：请稍后重试");
            }

            // 装任务的是另一个进程，它清不了本进程的 30 秒缓存；这里显式失效一次再查。
            Tasks.ScheduledTaskInstaller.InvalidateServiceCache();
            return Tasks.ScheduledTaskInstaller.IsServiceInstalled()
                ? (true, "护航任务已安装并运行")
                : (false, "安装未完成：可能取消了管理员授权，可再点一次");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "已取消管理员授权，护航任务没有安装");
        }
        catch (Exception ex)
        {
            return (false, "安装护航任务失败：" + ex.Message);
        }
    }

    internal (bool Ok, string Message) SaveBoardNamed(bool closeAfterSave, bool nameWithLesson)
    {
        if (!Config.Current.BoardSave.Enabled)
            return (false, "「保存 ClassInX 板书」已在配置里关闭");

        var result = BoardSaver.Save(closeAfterSave, null);
        if (!result.Ok) return (false, result.Message);
        if (!nameWithLesson || result.Files.Count == 0) return (true, result.Message);

        var now = DateTimeOffset.Now;
        var subject = Lesson.NameHint(now);
        var baseName = BoardFileName.Render(
            Config.Current.BoardSave.NameTemplate,
            subject,
            now,
            Lesson.State().Snapshot?.LessonIndex ?? 0);
        var rename = BoardFileName.ApplyTo(result.Files, baseName);
        Log.Info($"板书改名：{baseName}｜成功 {rename.Renamed} 个｜失败 {rename.Failed} 个");

        var message = result.Message + "｜" + rename.Message;
        return (rename.Failed == 0, message);
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
        progress = Math.Round(UpdateProgress.Clamp(status.Progress), 1),
        hasUpdate = status.HasUpdate,
        silent = status.Silent,
        error = status.Error,
        checkedAt = status.CheckedAt,
        checkOnBoot = Config.Current.Update.CheckOnBoot,
        autoInstall = Config.Current.Update.AutoInstall,
        installWithClients = Config.Current.Update.InstallWithClients,
        installWindow = Config.Current.Update.InstallWindow,
        installWindowText = InstallWindow.Describe(Config.Current.Update.InstallWindow),
        checkIntervalHours = Config.Current.Update.CheckIntervalHours,
        updatePlugin = Config.Current.Update.UpdatePlugin,
        pluginDetected = status.PluginDetected,
        pluginCurrent = status.PluginCurrent,
        pluginLatest = status.PluginLatest,
        pluginAvailable = status.PluginAvailable,
        pluginProgress = Math.Round(Math.Clamp(status.PluginProgress, 0, 1), 1),
        pluginError = status.PluginError,
        pluginText = status.PluginText,
        pluginCanUpdate = status.PluginCanUpdate,
        serviceInstalled = Tasks.ScheduledTaskInstaller.IsServiceInstalled()
    };

    /// <summary>面板点「升级」：后台下载 → 校验 → 静默安装；立刻把当前状态返回给页面。</summary>
    private Task<object> StartUpgradeAsync()
    {
        var status = Update.Status;
        if (status.Phase is UpdatePhase.Installing or UpdatePhase.Downloading) return Task.FromResult<object>(UpdateDto(status));
        if (!status.HasUpdate)
        {
            Log.Warn("升级请求被忽略：当前没有可安装的新版本");
            return Task.FromResult<object>(UpdateDto(status));
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

        return Task.FromResult<object>(UpdateDto());
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
        Log.Flush();   // 队列里的日志在退出前尽量落盘（最多等 2 秒）
    }
}
