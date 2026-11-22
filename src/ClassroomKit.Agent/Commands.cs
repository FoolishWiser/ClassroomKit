using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClassroomKit.Agent.Board;
using ClassroomKit.Agent.Diagnose;
using ClassroomKit.Agent.Lesson;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Agent.Update;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent;

/// <summary>命令行入口（--status / --ensure / --on / --off / --fix-peers / --install-task / --probe）。</summary>
internal static class Commands
{
    public static string Version => AgentRuntime.DisplayVersion();

    public static int PrintVersion()
    {
        Console.WriteLine($"ClassroomKit.Agent {Version}");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine($"""
ClassroomKit.Agent {Version}

  (无参数)          常驻守护 + 托盘图标 + 桌面窗口
  --ensure          确保守护在跑且热点开着（在跑则秒退，供 ClassIsland 调用）
  --on / --off      打开 / 关闭热点策略
  --status          输出当前状态（JSON）
  --probe           收集本机能力信息（JSON），用于 P0 评估
  --fix-peers       提权写入 WifiMaxPeers 并重启 icssvc（放宽设备数上限）
  --restart-wifi    复位无线网卡再重开热点（修「手机能看到热点却连不上」的假启动，需要管理员）
  --install-task    注册登录自启计划任务
  --uninstall-task  删除登录自启计划任务
  --install-service 注册 SYSTEM 护航任务（hosts 写入 + 静默升级；需要管理员）
  --uninstall-service 删除 SYSTEM 护航任务
  --hosts-status    GitHub 加速 hosts 的当前状态（JSON）
  --hosts-refresh   刷新 hosts 加速记录（默认强制；--check 只在直连不通时才改）
  --hosts-restore   删除 ClassroomKit 写入的 hosts 记录
  --service-tick    （护航任务内部使用）处理一次 hosts / 静默安装请求
  --check-update    检查 GitHub 上有没有新版本（JSON；--pre 连预发布版一起看）
  --update          检查 → 下载 → 静默安装（装了护航任务就不弹 UAC；--dry-run 只检查）
  --uplinks         列出本机网卡并输出上行选择结果（JSON）
  --save-board      保存 ClassInX 板书（三种格式）并按上一节课改名；默认不关闭 ClassInX
                    --close 保存后关闭 ClassInX；--no-name 不改名；--no-launch 不自动启动 ClassInX
  --save-classin-board  同 --save-board，但默认保存后关闭 ClassInX（旧行为）
  --lesson-status   课表联动状态（JSON：当前/下一节/自动保存判定）
  --lesson-reminder-test 打印「现在会不会提醒、还差多久」（JSON；加 --show 直接弹窗）
  --diagnose        一键自检（JSON：热点/上行/自启/护航/hosts/课表/升级）
  --rotate-token    换一个本地 API 令牌（ClassIsland 插件要重新填）
  --probe-classin   重新探测 ClassInX 保存界面（更新 ui-recipe.json）
  --conflict-check  检查当前是否存在板书软件全屏冲突（JSON）
  --conflict-preview 预览冲突提示窗（无人值守时可加 --timeout 8）
  --uninstall       卸载本程序（MSI 装走 msiexec /x；绿色版自删目录）。加 --purge-data 连配置日志一起删
  --msi-setup       （MSI 安装包内部使用）安装后配置
  --msi-cleanup     （MSI 安装包内部使用）卸载前清理
  --version         版本号
""");
        return 0;
    }

    /// <summary>CLI：只检查更新，输出 JSON。</summary>
    public static int CheckUpdateCli(string[] args)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var includePre = args.Any(a => a.Equals("--pre", StringComparison.OrdinalIgnoreCase));
        // 只影响本次检查，不写回 config.json（避免一次 --pre 之后一直收预发布版）。
        var originalPre = store.Current.Update.IncludePrerelease;
        if (includePre) store.Current.Update.IncludePrerelease = true;
        try
        {

            var service = new UpdateService(log, () => store.Current, Version, () => { });
            var status = service.CheckAsync(force: true).GetAwaiter().GetResult();
            Console.WriteLine(StatusJson(status));
            return status.Phase == UpdatePhase.Failed ? 2 : 0;
        }
        finally
        {
            store.Current.Update.IncludePrerelease = originalPre;
        }
    }

    /// <summary>CLI：检查 → 下载 → 安装（供管理员远程升级；--dry-run 只检查）。</summary>
    public static int UpdateCli(string[] args)
    {
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var service = new UpdateService(log, () => store.Current, Version, () => { });

        var status = service.CheckAsync(force: true).GetAwaiter().GetResult();
        if (status.Phase == UpdatePhase.Failed)
        {
            Console.WriteLine(StatusJson(status));
            return 2;
        }

        if (!status.HasUpdate || dryRun)
        {
            Console.WriteLine(StatusJson(status));
            return 0;
        }

        Console.WriteLine($"发现新版本 v{status.Latest}（当前 v{status.Current}），开始下载…");
        var downloaded = service.DownloadAsync().GetAwaiter().GetResult();
        if (downloaded.Phase != UpdatePhase.Ready)
        {
            Console.WriteLine(StatusJson(downloaded));
            return 1;
        }

        var (ok, message) = service.Install();
        Console.WriteLine(StatusJson(service.Status));
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static string StatusJson(UpdateStatus status) => JsonSerializer.Serialize(new
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
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static int Status()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var port = store.Current.Port;
        var token = store.Current.Token;
        var json = TryGetState(port, token);
        if (json is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                running = false,
                port,
                configPath = Paths.ConfigPath,
                api = $"http://127.0.0.1:{port}/api/"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 3;
        }

        Console.WriteLine(json);
        return 0;
    }

    /// <summary>CLI：保存 ClassInX 板书。</summary>
    public static int SaveClassInBoardCli(string[] args)
    {
        var oldStyle = args.Any(a => a.Equals("--save-classin-board", StringComparison.OrdinalIgnoreCase));
        var close = oldStyle
            ? !args.Any(a => a.Equals("--no-close", StringComparison.OrdinalIgnoreCase))
            : args.Any(a => a.Equals("--close", StringComparison.OrdinalIgnoreCase));
        var launch = !args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase));
        var nameWithLesson = !args.Any(a => a.Equals("--no-name", StringComparison.OrdinalIgnoreCase));
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var recipe = UiRecipe.Load(Paths.RecipePath);
        var saver = new ClassInBoardSaver(log, recipe, () => store.Current.BoardSave);
        var result = saver.Save(close, launch);

        var files = result.Files.ToList();
        BoardRenameResult? rename = null;
        if (result.Ok && nameWithLesson && files.Count > 0)
        {
            var lesson = new LessonRuntime(log, () => store.Current.Lesson, Paths.LessonStatePath);
            var now = DateTimeOffset.Now;
            var baseName = BoardFileName.Render(store.Current.BoardSave.NameTemplate, lesson.NameHint(now), now,
                lesson.State().Snapshot?.LessonIndex ?? 0);
            rename = BoardFileName.ApplyTo(files, baseName);
            files = rename.Files.ToList();
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = result.Ok && (rename?.Failed ?? 0) == 0,
            launched = result.Launched,
            message = result.Message + (rename is null ? "" : "｜" + rename.Message),
            elapsedMs = result.ElapsedMs,
            files,
            renamed = rename?.Renamed ?? 0,
            renameFailed = rename?.Failed ?? 0,
            steps = result.Steps.Select(x => new { step = x.Name, atMs = x.ElapsedMs })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        _ = store;
        return result.Ok ? 0 : 1;
    }

    /// <summary>CLI：课表联动状态（读守护落盘的 lesson-state.json）。</summary>
    public static int LessonStatusCli()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var lesson = new LessonRuntime(log, () => store.Current.Lesson, Paths.LessonStatePath);
        Console.WriteLine(JsonSerializer.Serialize(lesson.Status(), new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return 0;
    }

    /// <summary>CLI：现在会不会提醒？加 --show 直接弹窗（不保存，只演示）。</summary>
    public static int LessonReminderTestCli(string[] args)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var lesson = new LessonRuntime(log, () => store.Current.Lesson, Paths.LessonStatePath);
        var decision = lesson.Evaluate(DateTimeOffset.Now);
        var cfg = store.Current.Lesson;

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            show = decision.Show,
            reason = decision.Reason,
            key = decision.Key,
            remindAt = decision.RemindAt,
            subject = decision.Subject,
            secondsToStart = decision.SecondsToStart,
            nameHint = lesson.NameHint(DateTimeOffset.Now),
            config = new
            {
                enabled = cfg.Enabled,
                onlyWhenClassInBoard = cfg.OnlyWhenClassInBoard,
                staleMinutes = cfg.StaleMinutes
            }
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        if (args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine("1.0 起不再弹课后提醒窗；请用 POST /api/board-autosave action=preview 预览自动保存倒计时。");
        return 0;
    }

    /// <summary>CLI：一键自检。</summary>
    public static int DiagnoseCli()
    {
        using var runtime = new AgentRuntime();
        Console.WriteLine(JsonSerializer.Serialize(Diagnostics.Build(runtime), new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return 0;
    }

    /// <summary>CLI：换一个面板令牌。</summary>
    public static int RotateTokenCli()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var token = ConfigStore.NewToken();
        store.Update(c =>
        {
            c.Token = token;
            return 0;
        });

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            token,
            api = $"http://127.0.0.1:{store.Current.Port}/api/",
            message = "已写入 config.json；旧令牌立即失效，ClassIsland 插件需在设置页重新读取令牌"
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }

    /// <summary>CLI：重新探测 ClassInX 保存界面。当前若正好开着对话框，会把尺寸写进配方。</summary>
    public static int ProbeClassInCli()
    {
        var recipe = UiRecipe.Load(Paths.RecipePath);
        var (screenW, screenH) = WindowScanner.ScreenSize();
        var windows = WindowScanner.Scan();

        var classInWindows = windows
            .Where(w => w.Visible && w.ProcessName.Equals("ClassIn X", StringComparison.OrdinalIgnoreCase))
            .Select(w => new { handle = w.Handle, title = w.Title, className = w.ClassName, width = w.Width, height = w.Height, x = w.X, y = w.Y })
            .ToArray();

        var share = windows.FirstOrDefault(w => w.Visible && w.ProcessName.Equals("ClassIn X", StringComparison.OrdinalIgnoreCase) &&
                                                (w.Title == "Form" || recipe.MatchesShare(w.Width, w.Height)));
        var panel = windows.FirstOrDefault(w => w.Visible && w.ProcessName.Equals("ClassIn X", StringComparison.OrdinalIgnoreCase) &&
                                                 recipe.MatchesPanel(w.Width, w.Height));

        if (share is not null)
        {
            recipe.SaveLocal.ShareWidth = share.Width;
            recipe.SaveLocal.ShareHeight = share.Height;
        }
        if (panel is not null)
        {
            recipe.SaveLocal.PanelWidth = panel.Width;
            recipe.SaveLocal.PanelHeight = panel.Height;
        }
        recipe.ProbedAt = DateTimeOffset.Now;
        recipe.Source = "probe";
        recipe.Save(Paths.RecipePath);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            screen = new { width = screenW, height = screenH },
            classInRunning = ClassInBoardSaver.IsClassInRunning(),
            saveFolder = ClassInBoardSaver.SaveFolder,
            recipePath = Paths.RecipePath,
            recipe = new
            {
                share = new { recipe.SaveLocal.ShareWidth, recipe.SaveLocal.ShareHeight },
                panel = new { recipe.SaveLocal.PanelWidth, recipe.SaveLocal.PanelHeight },
                probedAt = recipe.ProbedAt
            },
            classInWindows,
            matchedShareDialog = share is not null,
            matchedSavePanel = panel is not null
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        return 0;
    }

    /// <summary>CLI：只读地报告当前板书冲突情境（不弹窗，弹窗由托盘/接口负责）。</summary>
    public static int ConflictCheckCli(string[] args)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var watcher = new ConflictWatcher(log, () => store.Current.Conflict, () => store.Current.Habits,
            () => store.Current.Conflict.ExtraFullscreenProcesses, () => null);
        var situation = watcher.Evaluate();

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            situation = situation.Describe(),
            shouldPrompt = situation.ShouldPrompt,
            classInFloating = situation.ClassInFloating,
            seewoFloating = situation.SeewoFloating,
            classInFullscreen = situation.ClassInFullscreen,
            seewoFullscreen = situation.SeewoFullscreen,
            wpsShow = situation.WpsShow,
            otherFullscreen = situation.OtherFullscreen,
            cooldownMinutes = store.Current.Conflict.CooldownMinutes,
            enabled = store.Current.Conflict.Enabled
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        _ = args;
        return situation.ShouldPrompt ? 0 : 8;
    }

    /// <summary>CLI：只弹一次冲突提示窗，用于现场预览/自检。</summary>
    public static int ConflictPreviewCli(string[] args)
    {
        var timeout = 60;
        var idx = Array.FindIndex(args, a => a.Equals("--timeout", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var t)) timeout = Math.Clamp(t, 3, 300);

        var result = ConflictDialog.Ask("ClassIn悬浮窗 + 希沃悬浮窗 + WPS放映（预览）", timeout);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            choice = result.Choice.ToString(),
            label = ConflictDialogModel.Label(result.Choice),
            snoozeToday = result.SnoozeToday,
            timeout
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static int Ensure()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        if (IsRunning(store, out _))
        {
            if (PostAction(store, "ensure", out var message))
            {
                Console.WriteLine($"已在运行：{message}");
                return 0;
            }
        }

        // 主实例在但 /api/state 没响应：60 秒内只允许派生一次，避免 --ensure 自我派生出一串新进程
        if (EnsureCooldownHit())
        {
            Console.WriteLine("60 秒内已尝试启动过守护，跳过本次派生");
            return 0;
        }

        MarkEnsureAttempt();
        StartDaemon();
        Console.WriteLine("已启动 ClassroomKit.Agent");
        return 0;
    }

    /// <summary>派生尝试的时间戳文件（60 秒冷却）。</summary>
    private static string EnsureStampPath => Path.Combine(Paths.DataDir, "ensure.last");

    private static readonly TimeSpan EnsureCooldown = TimeSpan.FromSeconds(60);

    /// <summary>本进程刚写过时间戳（Program 先写再调 Ensure），本次调用不该被自己写的记录拦下。</summary>
    private static bool _attemptMarkedHere;

    /// <summary>60 秒冷却是否命中：上次派生尝试还在冷却期内。</summary>
    internal static bool EnsureCooldownHit()
    {
        if (_attemptMarkedHere) return false;

        try
        {
            if (!File.Exists(EnsureStampPath)) return false;
            if (!long.TryParse(File.ReadAllText(EnsureStampPath).Trim(), out var ticks)) return false;

            var age = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            // 时钟往回跳（age 为负）就当没命中，否则会一直卡在冷却里
            return age >= TimeSpan.Zero && age < EnsureCooldown;
        }
        catch
        {
            return false;   // 读不到冷却文件就当没命中：宁可多试一次，也不能永远不启动
        }
    }

    /// <summary>原子写一次派生尝试时间戳（临时文件 + Move 覆盖），失败忽略。</summary>
    internal static void MarkEnsureAttempt()
    {
        _attemptMarkedHere = true;
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var tmp = EnsureStampPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, DateTime.UtcNow.Ticks.ToString());
            File.Move(tmp, EnsureStampPath, true);
        }
        catch
        {
            // 冷却文件写不进（权限/磁盘）不阻塞启动，最多是冷却失效
        }
    }

    public static int SetPolicy(bool enabled)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        if (IsRunning(store, out _))
        {
            var ok = PostJson(store, "/api/policy", JsonSerializer.Serialize(new { enabled }), out var message);
            Console.WriteLine(ok ? (enabled ? "热点策略：开" : "热点策略：关") : $"失败：{message}");
            return ok ? 0 : 1;
        }

        store.Update(c =>
        {
            c.Hotspot.Enabled = enabled;
            return true;
        });
        Console.WriteLine(enabled ? "热点策略已写为开启（守护未运行，下次启动生效）" : "热点策略已写为关闭");
        return 0;
    }

    public static int InstallTask()
    {
        var (ok, message) = ScheduledTaskInstaller.Install(Paths.ExePath);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int UninstallTask()
    {
        var (ok, message) = ScheduledTaskInstaller.Uninstall();
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 应急入口：复位的物理无线网卡后再开热点。8821CU 这类老驱动在 soft-AP 下会「假启动」——
    /// 状态机说 On、适配器 Up、IP 也有，但空口不发信标，手机连不上，只有复位网卡能救回来。
    /// </summary>
    public static int RestartWifiCli()
    {
        Console.WriteLine("正在复位无线网卡（约 10–30 秒）…");
        var ok = WlanAdapter.RestartAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!ok)
        {
            Console.WriteLine("复位失败：需要以管理员身份运行，或这块网卡的驱动不支持 Restart-NetAdapter");
            return 1;
        }

        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(null);
        var tethering = new TetheringService(new UplinkProbe(log), () => store.Current);
        var started = tethering.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(started
            ? "网卡已复位，热点已重新开启（等 10–30 秒再让手机连）"
            : "网卡已复位，但重开热点失败：" + (tethering.LastError ?? "未知原因"));
        return started ? 0 : 1;
    }

    public static int FixPeersCli()
    {
        var (ok, message) = FixPeersCore(null);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 写 WifiMaxPeers 并重启 icssvc；没提权就拉起自己一次（会弹 UAC）。
    /// allowPrompt=false 用于 HTTP API 这类「用户可能不在电脑前」的入口：
    /// UAC 的同意界面会接管整台机器的键鼠，没人点就会一直挂着，绝不能在后台线程上等。
    /// </summary>
    public static (bool Ok, string Message) FixPeersCore(AppLog? log, bool allowPrompt = true)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var peers = Math.Clamp(store.Current.Hotspot.RegistryPeers, 9, 64);

        if (!AdminElevation.IsElevated())
        {
            try
            {
                File.Delete(ResultPath);
            }
            catch
            {
                // 忽略
            }

            if (!allowPrompt)
            {
                return (false, "需要管理员权限：请到这台电脑上点一次「突破设备数限制」并确认授权");
            }

            var code = AdminElevation.RunElevatedAndWait("--fix-peers");
            var reported = ReadResult();
            if (reported is not null) return (code == 0, reported);
            return (code == 0, code == 0 ? "已提权执行" : $"提权执行失败（退出码 {code}）");
        }

        var ok = PeerLimit.Write(peers, out var message);
        if (ok)
        {
            PeerLimit.RestartIcs(log);
            message += "；已重启 icssvc，重新开热点后生效";
        }

        WriteResult(message);
        log?.Info($"修复设备上限：{message}");
        return (ok, message);
    }

    /// <summary>CLI：SYSTEM 护航任务的一次执行（--service-tick），处理 hosts 写入与静默安装请求。</summary>
    public static int ServiceTickCli() => Service.ServiceTick.Run();

    public static int HostsStatusCli()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var hosts = new Hosts.HostsService(log, () => store.Current);
        Console.WriteLine(JsonSerializer.Serialize(hosts.StatusDto(), new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>CLI：刷新 hosts 加速记录（默认强制刷新；--check 只在直连 GitHub 不通时才动）。</summary>
    public static int HostsRefreshCli(string[] args)
    {
        var force = !args.Any(a => a.Equals("--check", StringComparison.OrdinalIgnoreCase));
        var (ok, message) = RunHosts((h, ct) => h.RefreshAsync(force, ct));
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int HostsRestoreCli()
    {
        var (ok, message) = RunHosts((h, ct) => h.RestoreAsync(ct));
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    private static (bool Ok, string Message) RunHosts(
        Func<Hosts.HostsService, CancellationToken, Task<(bool Ok, string Message)>> action)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var hosts = new Hosts.HostsService(log, () => store.Current);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        return action(hosts, cts.Token).GetAwaiter().GetResult();
    }

    /// <summary>CLI：注册 SYSTEM 护航计划任务（每 5 分钟醒一次；hosts 写入与静默升级都由它执行）。</summary>
    public static int InstallServiceTask()
    {
        var (ok, message) = ScheduledTaskInstaller.InstallService(Paths.ExePath);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int UninstallServiceTask()
    {
        var (ok, message) = ScheduledTaskInstaller.UninstallService();
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int Panel()
    {
        Console.WriteLine("网页面板已在 0.8.2 移除；ClassroomKit 现在只有桌面端，以及给 ClassIsland 插件用的本地 /api。");
        return 2;
    }

    /// <summary>诊断用：列出所有网卡与最终选中的上行（JSON）。</summary>
    public static int UplinksCli()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new AppLog(Paths.LogDir);
        var probe = new UplinkProbe(log);
        var choice = probe.Resolve(store.Current.Uplink, store.Current.Hotspot.AllowWifiUplink);

        var json = JsonSerializer.Serialize(new
        {
            mode = store.Current.Uplink.Mode,
            allowWifiUplink = store.Current.Hotspot.AllowWifiUplink,
            fallbackToLan = store.Current.Uplink.FallbackToLan,
            source = choice.Source.ToString(),
            reason = choice.Reason,
            describe = choice.Describe(),
            chosen = choice.Chosen is null ? null : new
            {
                choice.Chosen.Id,
                choice.Chosen.Name,
                choice.Chosen.Description,
                choice.Chosen.Ipv4,
                choice.Chosen.HasInternet,
                choice.Chosen.HasGateway,
                choice.Chosen.IsWifi
            },
            candidates = choice.Candidates.Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Ipv4,
                c.IsWired,
                c.IsWifi,
                c.HasGateway,
                c.HasInternet,
                c.IsTethering,
                c.Metric,
                c.SpeedMbps,
                c.Usable
            })
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        Console.WriteLine(json);
        return choice.Chosen is null ? 3 : 0;
    }

    /// <summary>命令行卸载：默认保留配置日志，加 --purge-data 一起删。</summary>
    public static int Uninstall(string[] args)
    {
        var keepData = !args.Any(a => a.Equals("--purge-data", StringComparison.OrdinalIgnoreCase));
        var plan = SelfUninstaller.BuildPlan(keepData);
        Console.WriteLine($"安装方式：{plan.Mode}");
        Console.WriteLine(plan.Describe());

        var (ok, message) = SelfUninstaller.Start(keepData);
        Console.WriteLine(message);
        if (ok) Console.WriteLine("本进程即将退出，卸载在后台继续（会弹一次管理员授权）。");
        return ok ? 0 : 1;
    }

    public static int ProbeCli()
    {
        var json = Probe.Probe.Collect();
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Path.Combine(Paths.DataDir, "probe.json"), json);
        }
        catch
        {
            // 写不进 ProgramData 也不影响输出
        }

        Console.WriteLine(json);
        return 0;
    }

    private static string ResultPath => Path.Combine(Paths.DataDir, "last-fix.txt");

    private static void WriteResult(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(ResultPath, message);
        }
        catch
        {
            // 忽略
        }
    }

    private static string? ReadResult()
    {
        try
        {
            return File.Exists(ResultPath) ? File.ReadAllText(ResultPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void StartDaemon()
    {
        try
        {
            var psi = new ProcessStartInfo(Paths.ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Paths.ExeDir
            };
            Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // 0.8.2 清单改成 highestAvailable：标准账户直接 CreateProcess 会被 740 拒绝，改走 ShellExecute。
            Process.Start(new ProcessStartInfo(Paths.ExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Paths.ExeDir
            })?.Dispose();
        }
    }

    private static bool IsRunning(ConfigStore store, out string? stateJson)
    {
        stateJson = TryGetState(store.Current.Port, store.Current.Token);
        return stateJson is not null;
    }

    private static string? TryGetState(int port, string token)
    {
        try
        {
            // 4 秒：/api/state 在机器卡顿时可能 2 秒才回，超时太短会被当成没启动而反复派生
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/state");
            request.Headers.Add("X-Token", token);
            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode) return null;
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static bool PostJson(ConfigStore store, string path, string json, out string message)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{store.Current.Port}{path}");
            request.Headers.Add("X-Token", store.Current.Token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = client.Send(request);
            message = response.IsSuccessStatusCode ? "ok" : $"HTTP {(int)response.StatusCode}";
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static bool PostAction(ConfigStore store, string action, out string message) =>
        PostJson(store, "/api/action", JsonSerializer.Serialize(new { action }), out message);
}
