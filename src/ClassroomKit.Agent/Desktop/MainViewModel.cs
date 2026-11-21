using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using ClassroomKit.Agent.Diagnose;
using ClassroomKit.Agent.Update;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.About;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Onboarding;
using ClassroomKit.Core.Ui;
using ClassroomKit.Core.Update;

namespace ClassroomKit.Agent.Desktop;

public sealed record NavItem(string Key, string Title, string Glyph, string Group);

/// <summary>「生效科目」里的一枚可勾选胶囊。</summary>
public sealed class SubjectOption : ObservableObject
{
    private readonly Action<SubjectOption> _toggled;
    private bool _selected;

    public SubjectOption(string name, bool selected, Action<SubjectOption> toggled)
    {
        Name = name;
        _selected = selected;
        _toggled = toggled;
    }

    public string Name { get; }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            _toggled(this);
        }
    }
}

/// <summary>「高级」页里一个科目的习惯设置行：科目固定，习惯软件与理解程度可改。</summary>
public sealed class HabitRow : ObservableObject
{
    private readonly Action<HabitRow> _changed;
    private string _app;
    private string _understand;

    public HabitRow(string subject, string app, string understand, Action<HabitRow> changed)
    {
        Subject = subject;
        _app = app;
        _understand = understand;
        _changed = changed;
    }

    public string Subject { get; }

    /// <summary>classin | seewo | wps；空 = 未设置（保存时这条规则会被丢掉）。</summary>
    public string App
    {
        get => _app;
        set
        {
            if (!Set(ref _app, value)) return;
            Raise(nameof(HasApp));
            _changed(this);
        }
    }

    /// <summary>understand = 理解（冲突时照常弹窗）；silent = 不理解（静默关闭另一款）。</summary>
    public string Understand
    {
        get => _understand;
        set
        {
            if (!Set(ref _understand, value)) return;
            _changed(this);
        }
    }

    public bool HasApp => (App ?? "").Length > 0;
}

public sealed record DiagRow(string Title, string Status, string Detail, string Next)
{
    public string StatusText => Status switch { "ok" => "正常", "warn" => "注意", _ => "有问题" };
}

/// <summary>桌面端唯一的 ViewModel：持有运行时引用，按秒拍快照；所有命令直接调 AgentRuntime，不走 HTTP。</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string PageOverview = "overview";
    public const string PageHotspot = "hotspot";
    public const string PageLesson = "lesson";
    public const string PageUpdate = "update";
    public const string PageDiagnose = "diagnose";
    public const string PageAdvanced = "advanced";
    public const string PageSettings = "settings";
    public const string PageAbout = "about";

    private readonly AgentRuntime _rt;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Queue<(string Text, bool Error)> _toastQueue = new();
    private readonly List<UiLogLine> _allLogs = new();
    private volatile bool _refreshing;
    private bool _loadedOnce;
    private bool _settingsDirty;
    private bool _live = true;
    private bool _onboardingVisible;
    private bool _onboardingPaused;
    private int _onboardingStep;

    private string _pageKey;
    private string _pageTitle = "概览";
    private string _healthStatus = "warn";
    private string _versionText = "";
    private string _stateText = "";
    private string _pauseText = "";
    private string _ssid = "";
    private string _passphrase = "";
    private string _clientsText = "";
    private string _uplinkText = "-";
    private string _maxPeersText = "";
    private string _hotspotStateText = "未知";
    private bool _hotspotOn;
    private string _lessonStateText = "还没有收到课表";
    private string _lessonCurrentText = "-";
    private string _lessonNextText = "-";
    private string _lessonCountdownText = "-";
    private string _lessonNameHint = "-";
    private bool _lessonAvailable;
    private string _updateCurrentText = "-";
    private string _updatePhaseText = "";
    private string _updateLatestText = "-";
    private string _updateNotes = "";
    private string _updateError = "";
    private double _updateProgress;
    private bool _hasUpdate;
    private string _hostsText = "";
    private string _hostsCountText = "";
    private string _hostsServiceText = "";
    private bool _hostsApplied;
    private bool _serviceInstalled;
    private string _boardPreview = "";
    private string _toastText = "";
    private bool _toastVisible;
    private bool _toastError;
    private string _toastGlyph = "\uE73E";
    private bool _diagnosing;
    private string _diagnoseSummary = "还没有自检";
    private string _diagnoseMeta = "自检会把热点、上行、hosts、板书目录逐条过一遍；日志区跟着实时刷新。";
    private bool _logFollow = true;
    private string _logMinLevel = "all";
    private string _updateProgressText = "";
    private bool _updateProgressVisible;
    private string _habitStatusText = "";
    private string _habitLastActionText = "还没有自动处理过";

    private readonly List<HabitRule> _habitEdits = new();
    private bool _editHabitsEnabled;
    private bool _editConflictEnabled = true;
    private string _editIgnoreProcesses = "";
    private string _habitSignature = "";
    private string _newHabitSubjectText = "";
    private bool _habitConfirmSuppress;
    private bool _habitConfirmVisible;
    private string _editSkipBreakNames = "";
    private string _aboutStarsText = "—";
    private int? _aboutStars;
    private DateTimeOffset _starsFetchedAt = DateTimeOffset.MinValue;

    private string _editSsid = "";
    private string _editPassphrase = "";
    private int _editTargetClients = 8;
    private int _editRemindMinutes = 5;
    private int _editDialogSeconds = 30;
    private string _editTimeoutAction = "save";
    private int _editSnoozeMinutes = 2;
    private bool _editLessonEnabled = true;
    private bool _editOnlyWhenClassInBoard = true;
    private bool _editNameWithLesson = true;
    private string _editNameTemplate = BoardFileName.DefaultTemplate;
    private bool _editCloseToTray = true;
    private bool _editStartMinimized;
    private string _editTheme = "system";
    private bool _editAutoInstall;
    private bool _editCheckOnBoot = true;
    private string _editInstallWindow = "";
    private bool _editUpdatePlugin = true;
    private bool _editTraySaveKeepOpen;
    private bool _editTraySaveWithoutRename;
    private bool _editTrayShowPreviewReminder = true;
    private readonly List<string> _editSubjects = new();
    private bool _syncingSubjects;
    private string _newSubjectText = "";
    private string _subjectHint = "未勾选 = 所有科目都提醒";
    private string _updatePluginText = "";
    private string _updatePluginDetail = "";
    private double _updatePluginProgress;
    private string _updatePluginError = "";
    private bool _updatePluginCanUpdate;
    private string _selfHealText = "";
    private string _selfHealDetail = "";
    private bool _selfHealBusy;

    public MainViewModel(AgentRuntime runtime)
    {
        _rt = runtime;
        DiagnoseRows.CollectionChanged += (_, __) => Raise(nameof(DiagnoseEmpty));
        var ui = runtime.Config.Current.Ui;
        Pages = new[]
        {
            new NavItem(PageOverview, "概览", "\uE80F", "监控"),
            new NavItem(PageHotspot, "热点与网络", "\uE701", "监控"),
            new NavItem(PageLesson, "课表与板书", "\uE787", "监控"),
            new NavItem(PageUpdate, "升级", "\uE895", "维护"),
            new NavItem(PageDiagnose, "自检与日志", "\uE9D9", "维护"),
            new NavItem(PageAdvanced, "高级", "\uE7EF", "维护"),
            new NavItem(PageSettings, "设置", "\uE713", "维护"),
            new NavItem(PageAbout, "关于", "\uE946", "维护")
        };
        PagesView = new ListCollectionView(Pages.ToList());
        PagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        _pageKey = ui.LastPage is { Length: > 0 } p && Pages.Any(x => x.Key == p) ? p : PageOverview;
        _pageTitle = Pages.First(x => x.Key == _pageKey).Title;

        // 0.7.0：没看过教程才自动弹；升级上来的老配置在 ConfigStore 里已被盖章成「已看过」，不会挡屏
        ShowOnboarding = OnboardingGuide.ShouldShow(ui.OnboardingDone);
        if (ShowOnboarding) SetOnboardingStep(0);

        ToggleHotspotCommand = new RelayCommand(() => Run(() =>
        {
            var enable = !_rt.Config.Current.Hotspot.Enabled;
            _rt.SetPolicy(enable);
            return enable ? "已开启热点" : "已关闭热点";
        }));
        PauseCommand = new RelayCommand(() =>
        {
            if (_rt.IsPaused)
            {
                _rt.ClearPause();
                Toast("已恢复守护");
            }
            else
            {
                _rt.PauseFor(TimeSpan.FromMinutes(30));
                Toast("已暂停 30 分钟，到点自动恢复");
            }
            _ = RefreshAsync(true);
        });
        RefreshCommand = new RelayCommand(() => { _ = RefreshAsync(true); });
        SaveHotspotCommand = new RelayCommand(SaveHotspot);
        SaveLessonCommand = new RelayCommand(SaveLesson);
        SaveUiCommand = new RelayCommand(SaveUi);
        SaveUpdateCommand = new RelayCommand(SaveUpdate);
        SaveBoardCommand = new RelayCommand(SaveBoardNow);
        SaveBoardNamedCommand = new RelayCommand(() => RunBackground(() => _rt.SaveBoardNamed(false, _rt.Config.Current.BoardSave.NameWithLesson).Message));
        PreviewReminderCommand = new RelayCommand(() => { _rt.Api.PreviewBoardReminder?.Invoke(); Toast("已请求预览课后提醒窗"); });
        ClearLessonCommand = new RelayCommand(() =>
        {
            _rt.Api.ClearLesson?.Invoke();
            Toast("已清除课表数据，等插件下次推送");
            _ = RefreshAsync(true);
        });
        CheckUpdateCommand = new RelayCommand(() => RunUpdateOnce(async () =>
        {
            var status = await _rt.Update.CheckAsync(true);
            if (!status.HasUpdate) return status.PhaseText;
            var after = await _rt.Update.DownloadAsync();
            return after.Phase == UpdatePhase.Ready ? $"发现新版本 v{status.Latest}，安装包已下载" : after.PhaseText;
        }));
        UpgradeCommand = new RelayCommand(() => RunUpdateOnce(async () =>
        {
            var s = await _rt.Update.DownloadAsync();
            if (s.Phase == UpdatePhase.Ready)
            {
                var (ok, msg) = _rt.Update.Install();
                return msg;
            }
            return s.PhaseText;
        }));
        UpdatePluginCommand = new RelayCommand(() => RunUpdateOnce(async () =>
        {
            var s = await _rt.Update.UpdatePluginAsync();
            return string.IsNullOrWhiteSpace(s.PluginText) ? "ClassIsland 插件已处理" : s.PluginText;
        }));
        RepairNetworkCommand = new RelayCommand(() =>
        {
            if (_selfHealBusy)
            {
                Toast("上一次修复还没结束，请稍等");
                return;
            }

            _selfHealBusy = true;
            RunAsync(async () =>
            {
                try
                {
                    var (ok, message) = await _rt.Guard.RepairNowAsync(CancellationToken.None);
                    if (!ok) throw new InvalidOperationException(message);
                    return message;
                }
                finally { _selfHealBusy = false; }
            });
        });
        DiagnoseCommand = new RelayCommand(() => { _ = RunDiagnoseAsync(); });
        InstallServiceCommand = new RelayCommand(() => RunBackground(() => _rt.InstallServiceTask().Message));
        RefreshHostsCommand = new RelayCommand(() => RunAsync(async () =>
        {
            var (ok, msg) = await _rt.Hosts.RefreshAsync(true, CancellationToken.None);
            return msg;
        }));
        RestoreHostsCommand = new RelayCommand(() => RunAsync(async () =>
        {
            var (ok, msg) = await _rt.Hosts.RestoreAsync(CancellationToken.None);
            return msg;
        }));
        OpenLogsCommand = new RelayCommand(() => ShellExec.Open(Paths.LogDir));
        OpenRepoCommand = new RelayCommand(() => ShellExec.Open(AboutInfo.RepoUrl(AboutInfo.DefaultRepo)));
        OpenReleasesCommand = new RelayCommand(() => ShellExec.Open(AboutInfo.ReleasesUrl(AboutInfo.DefaultRepo)));
        OpenStarsCommand = new RelayCommand(() => ShellExec.Open(AboutInfo.StarsUrl(AboutInfo.DefaultRepo)));
        OpenClassIslandSiteCommand = new RelayCommand(() => ShellExec.Open(AboutInfo.ClassIslandSiteUrl));
        RefreshStarsCommand = new RelayCommand(() => { _ = RefreshStarsAsync(true); });
        CopyPasswordCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_rt.Config.Current.Hotspot.Passphrase);
                Toast("密码已复制");
            }
            catch
            {
                Toast("复制失败，剪贴板被别的程序占用", true);
            }
        });
        FixPeersCommand = new RelayCommand(() => RunAsync(async () =>
        {
            var (ok, msg) = await _rt.Api.FixPeersAsync!(CancellationToken.None);
            return msg;
        }));
        RenameToMachineCommand = new RelayCommand(() => Run(() =>
        {
            var name = MachineDefaults.DefaultSsid();
            _rt.Config.Update(c => c.Hotspot.Ssid = name);
            return "热点名已改为：" + name;
        }));
        DismissToastCommand = new RelayCommand(DismissToast);
        AddSubjectCommand = new RelayCommand(AddSubject);
        ClearSubjectSelectionCommand = new RelayCommand(ClearSubjectSelection);
        SaveTrayCommand = new RelayCommand(SaveTray);
        SaveHabitsCommand = new RelayCommand(SaveHabits);
        ConfirmHabitModeCommand = new RelayCommand(() =>
        {
            HabitConfirmVisible = false;
            _habitConfirmSuppress = true;
            try { EditHabitsEnabled = true; }
            finally { _habitConfirmSuppress = false; }
        });
        CancelHabitModeCommand = new RelayCommand(() =>
        {
            HabitConfirmVisible = false;
            Raise(nameof(EditHabitsEnabled));
        });
        AddHabitSubjectCommand = new RelayCommand(AddHabitSubject);
        ClearHabitRuleCommand = new RelayCommand(param =>
        {
            if (param is not HabitRow row) return;
            _habitEdits.RemoveAll(x => string.Equals(x.Subject, row.Subject, StringComparison.OrdinalIgnoreCase));
            _settingsDirty = true;
            RebuildHabitRows(force: true);
            Toast($"已清除「{row.Subject}」的习惯设置，记得保存");
        });
        PreviewConflictCommand = new RelayCommand(() =>
        {
            _rt.Api.PreviewConflict?.Invoke();
            Toast("已请求预览冲突窗（只弹窗，不会关闭任何程序）");
        });
        NextOnboardingCommand = new RelayCommand(() =>
        {
            if (OnboardingAtEnd) FinishOnboarding("教程看完了。以后想看，到「关于」页点「重新显示教程引导」");
            else SetOnboardingStep(_onboardingStep + 1);
        });
        PrevOnboardingCommand = new RelayCommand(() => SetOnboardingStep(_onboardingStep - 1));
        SkipOnboardingCommand = new RelayCommand(() =>
            FinishOnboarding("已跳过教程。以后想看，到「关于」页点「重新显示教程引导」"));
        PauseOnboardingCommand = new RelayCommand(() => PauseOnboarding("教程先收起，左下角「教程引导」能接着看"));
        ResumeOnboardingCommand = new RelayCommand(ResumeOnboarding);
        ContinueOnboardingCommand = new RelayCommand(() =>
        {
            if (OnboardingPaused) ResumeOnboarding();
            else
            {
                ShowOnboarding = true;
                Toast("教程已打开，可以接着看");
            }
        });
        RestartOnboardingCommand = new RelayCommand(() =>
        {
            try { _rt.Config.Mutate(c => c.Ui.OnboardingDone = false); } catch { /* 写不进去也不耽误这次显示 */ }
            OnboardingPaused = false;
            SetOnboardingStep(0);
            ShowOnboarding = true;
            Toast("教程已重新打开");
        });
        GoOnboardingPageCommand = new RelayCommand(() =>
        {
            // 先收起遮罩再切页：遮罩原来盖在内容上，看起来像「点了没反应」（0.8.1 修）
            ShowOnboarding = false;
            OnboardingPaused = true;
            PageKey = OnboardingCurrent.Page;
            Toast("已切到「" + PageTitle + "」，左下角「教程引导」可以接着看");
        });
        OpenUpdatePageCommand = new RelayCommand(() => PageKey = PageUpdate);
        OpenFontLicenseCommand = new RelayCommand(() =>
            ShellExec.Open(Path.Combine(AppContext.BaseDirectory, "licenses", "HarmonyOS-Sans-LICENSE.txt")));
        OpenNoticesCommand = new RelayCommand(() =>
            ShellExec.Open(Path.Combine(AppContext.BaseDirectory, "licenses", "THIRD-PARTY-NOTICES.md")));

        LoadEditFields();
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromSeconds(3.4)
        };
        _toastTimer.Tick += (_, _) => NextToast();
        // 面板 3 秒粒度就够了：每拍一次快照都要碰 WMI / hosts 文件，没有必要每秒重来
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
            (_, _) => _ = RefreshAsync(false), Dispatcher.CurrentDispatcher);
        _timer.Stop();
        _timer.Start();
        _ = RefreshAsync(true);
    }

    /// <summary>界面隐藏/最小化后停表，别在后台白跑 WMI。</summary>
    public void SetLive(bool live)
    {
        if (_live == live) return;
        _live = live;
        if (live)
        {
            _timer.Start();
            _ = RefreshAsync(true);
        }
        else
        {
            _timer.Stop();
        }
    }

    public IReadOnlyList<NavItem> Pages { get; }

    /// <summary>左侧导航（按「监控 / 维护」分组）。</summary>
    public ICollectionView PagesView { get; }

    public string PageKey
    {
        get => _pageKey;
        set
        {
            if (!Set(ref _pageKey, value)) return;
            PageTitle = Pages.FirstOrDefault(x => x.Key == value)?.Title ?? "概览";
            Raise(nameof(PageSubtitle));
            if (ShowOnboarding && value != OnboardingCurrent.Page)
            {
                // 从导航离开教程指向的页面时收起遮罩，避免教程一直挡在内容前面（0.8.1）
                OnboardingPaused = true;
                ShowOnboarding = false;
            }
            if (value == PageAbout) _ = RefreshStarsAsync(false);
            var key = value;
            _ = Task.Run(() => _rt.Config.Update(c => c.Ui.LastPage = key));
        }
    }

    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }

    public string PageSubtitle => _pageKey switch
    {
        PageOverview => "守护状态、下一节课与加速一览",
        PageHotspot => "热点开关、密码与设备容量",
        PageLesson => "课后保存、生效科目与板书命名",
        PageUpdate => "版本、安装包与 ClassIsland 插件",
        PageDiagnose => "一键体检与实时日志",
        PageAdvanced => "按科目记住老师习惯，冲突时自动让路",
        PageSettings => "界面、托盘、升级与护航",
        PageAbout => "版本、开源仓库与更新记录",
        _ => ""
    };
    public string HealthStatus { get => _healthStatus; private set => Set(ref _healthStatus, value); }

    public RelayCommand ToggleHotspotCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveHotspotCommand { get; }
    public RelayCommand SaveLessonCommand { get; }
    public RelayCommand SaveUiCommand { get; }
    public RelayCommand SaveUpdateCommand { get; }
    public RelayCommand SaveBoardCommand { get; }
    public RelayCommand SaveBoardNamedCommand { get; }
    public RelayCommand PreviewReminderCommand { get; }
    public RelayCommand ClearLessonCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand UpdatePluginCommand { get; }
    public RelayCommand DiagnoseCommand { get; }
    public RelayCommand RepairNetworkCommand { get; }
    public RelayCommand InstallServiceCommand { get; }
    public RelayCommand RefreshHostsCommand { get; }
    public RelayCommand RestoreHostsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand OpenRepoCommand { get; }
    public RelayCommand OpenReleasesCommand { get; }
    public RelayCommand OpenStarsCommand { get; }
    public RelayCommand OpenClassIslandSiteCommand { get; }
    public RelayCommand RefreshStarsCommand { get; }
    public RelayCommand CopyPasswordCommand { get; }
    public RelayCommand FixPeersCommand { get; }
    public RelayCommand RenameToMachineCommand { get; }
    public RelayCommand DismissToastCommand { get; }
    public RelayCommand AddSubjectCommand { get; }
    public RelayCommand ClearSubjectSelectionCommand { get; }
    public RelayCommand SaveTrayCommand { get; }
    public RelayCommand SaveHabitsCommand { get; }
    public RelayCommand AddHabitSubjectCommand { get; }
    public RelayCommand ClearHabitRuleCommand { get; }
    public RelayCommand ConfirmHabitModeCommand { get; }
    public RelayCommand CancelHabitModeCommand { get; }
    public RelayCommand PreviewConflictCommand { get; }
    public RelayCommand NextOnboardingCommand { get; }
    public RelayCommand PrevOnboardingCommand { get; }
    public RelayCommand SkipOnboardingCommand { get; }
    public RelayCommand RestartOnboardingCommand { get; }
    public RelayCommand GoOnboardingPageCommand { get; }
    public RelayCommand PauseOnboardingCommand { get; }
    public RelayCommand ResumeOnboardingCommand { get; }
    public RelayCommand ContinueOnboardingCommand { get; }
    public RelayCommand OpenUpdatePageCommand { get; }
    public RelayCommand OpenFontLicenseCommand { get; }
    public RelayCommand OpenNoticesCommand { get; }

    public ObservableCollection<DiagRow> DiagnoseRows { get; } = new();
    public bool DiagnoseEmpty => DiagnoseRows.Count == 0;
    public ObservableCollection<SubjectOption> SubjectOptions { get; } = new();
    public ObservableCollection<UiLogLine> LogEntries { get; } = new();
    public ObservableCollection<HabitRow> HabitRows { get; } = new();

    /// <summary>习惯软件下拉项：空值 = 未设置。</summary>
    public IReadOnlyList<UiOption> HabitAppOptions { get; } = new[]
    {
        new UiOption("", "未设置"),
        new UiOption("classin", "ClassInX"),
        new UiOption("seewo", "希沃白板"),
        new UiOption("wps", "WPS")
    };

    /// <summary>理解程度：理解 = 弹窗交给老师；不理解 = 静默关掉另一款。</summary>
    public IReadOnlyList<UiOption> HabitUnderstandOptions { get; } = new[]
    {
        new UiOption("understand", "理解 · 冲突时弹窗交给我"),
        new UiOption("silent", "不理解 · 静默关闭另一款")
    };

    /// <summary>日志追加后触发（页面据此决定要不要滚到底）。</summary>
    public event Action? LogsChanged;

    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string PauseText { get => _pauseText; private set => Set(ref _pauseText, value); }
    public string Ssid { get => _ssid; private set => Set(ref _ssid, value); }
    public string Passphrase { get => _passphrase; private set => Set(ref _passphrase, value); }
    public string ClientsText { get => _clientsText; private set => Set(ref _clientsText, value); }
    public string UplinkText { get => _uplinkText; private set => Set(ref _uplinkText, value); }
    public string MaxPeersText { get => _maxPeersText; private set => Set(ref _maxPeersText, value); }
    public string HotspotStateText { get => _hotspotStateText; private set => Set(ref _hotspotStateText, value); }
    public bool HotspotOn { get => _hotspotOn; private set => Set(ref _hotspotOn, value); }
    public string LessonStateText { get => _lessonStateText; private set => Set(ref _lessonStateText, value); }
    public string LessonCurrentText { get => _lessonCurrentText; private set => Set(ref _lessonCurrentText, value); }
    public string LessonNextText { get => _lessonNextText; private set => Set(ref _lessonNextText, value); }
    public string LessonCountdownText { get => _lessonCountdownText; private set => Set(ref _lessonCountdownText, value); }
    public string LessonNameHint { get => _lessonNameHint; private set => Set(ref _lessonNameHint, value); }
    public bool LessonAvailable { get => _lessonAvailable; private set => Set(ref _lessonAvailable, value); }
    public string UpdateCurrentText { get => _updateCurrentText; private set => Set(ref _updateCurrentText, value); }
    public string UpdatePhaseText { get => _updatePhaseText; private set => Set(ref _updatePhaseText, value); }
    public string UpdateLatestText { get => _updateLatestText; private set => Set(ref _updateLatestText, value); }
    public string UpdateNotes { get => _updateNotes; private set => Set(ref _updateNotes, value); }
    public bool UpdateNotesEmpty => string.IsNullOrWhiteSpace(UpdateNotes);
    public bool UpdatePluginEmpty => string.IsNullOrWhiteSpace(UpdatePluginText) && string.IsNullOrWhiteSpace(UpdatePluginDetail);
    public bool UpdatePluginProgressVisible => UpdatePluginProgress is > 0 and < 1;
    public string UpdateError { get => _updateError; private set => Set(ref _updateError, value); }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    /// <summary>只有正在下载或已下载好时才显示进度条，空闲时整条藏起来，免得 0% 被当成「卡住」。</summary>
    public bool UpdateProgressVisible { get => _updateProgressVisible; private set => Set(ref _updateProgressVisible, value); }
    public string UpdateProgressText { get => _updateProgressText; private set => Set(ref _updateProgressText, value); }

    /// <summary>首次启动的教程引导遮罩是否显示（0.7.0 起）。</summary>
    public bool ShowOnboarding { get => _onboardingVisible; private set => Set(ref _onboardingVisible, value); }
    /// <summary>教程被收起但仍没看完（左下角「教程引导」能回到原步骤）。</summary>
    public bool OnboardingPaused { get => _onboardingPaused; private set => Set(ref _onboardingPaused, value); }
    public OnboardingStep OnboardingCurrent => OnboardingGuide.Steps[Math.Clamp(_onboardingStep, 0, OnboardingGuide.Steps.Count - 1)];
    public string OnboardingStepText => $"{_onboardingStep + 1} / {OnboardingGuide.Steps.Count}";
    public string OnboardingTitle => OnboardingCurrent.Title;
    public string OnboardingBody => OnboardingCurrent.Body;
    public string OnboardingAction => OnboardingCurrent.Action;
    public string OnboardingNextText => OnboardingAtEnd ? "完成" : "下一步";
    public bool OnboardingAtStart => _onboardingStep <= 0;
    public bool OnboardingAtEnd => _onboardingStep >= OnboardingGuide.Steps.Count - 1;
    public bool OnboardingCanGoBack => !OnboardingAtStart;
    public bool HasUpdate { get => _hasUpdate; private set => Set(ref _hasUpdate, value); }
    public string HostsText { get => _hostsText; private set => Set(ref _hostsText, value); }
    public string HostsCountText { get => _hostsCountText; private set => Set(ref _hostsCountText, value); }
    public string HostsServiceText { get => _hostsServiceText; private set => Set(ref _hostsServiceText, value); }
    public bool HostsApplied { get => _hostsApplied; private set => Set(ref _hostsApplied, value); }
    public bool ServiceInstalled
    {
        get => _serviceInstalled;
        private set
        {
            if (Set(ref _serviceInstalled, value)) Raise(nameof(ServiceMissing));
        }
    }

    /// <summary>护航任务还没装：界面上显示「安装护航任务」。</summary>
    public bool ServiceMissing => !_serviceInstalled;
    public string BoardPreview { get => _boardPreview; private set => Set(ref _boardPreview, value); }
    public string SubjectHint { get => _subjectHint; private set => Set(ref _subjectHint, value); }
    public string NewSubjectText { get => _newSubjectText; set => Set(ref _newSubjectText, value); }
    public string UpdatePluginText { get => _updatePluginText; private set => Set(ref _updatePluginText, value); }
    public string UpdatePluginDetail { get => _updatePluginDetail; private set => Set(ref _updatePluginDetail, value); }
    public double UpdatePluginProgress { get => _updatePluginProgress; private set => Set(ref _updatePluginProgress, value); }
    public string UpdatePluginError { get => _updatePluginError; private set => Set(ref _updatePluginError, value); }
    public bool UpdatePluginCanUpdate { get => _updatePluginCanUpdate; private set => Set(ref _updatePluginCanUpdate, value); }
    public string SelfHealText { get => _selfHealText; private set => Set(ref _selfHealText, value); }
    public string SelfHealDetail { get => _selfHealDetail; private set => Set(ref _selfHealDetail, value); }

    /// <summary>自检页的自动修复总开关：即时写盘，不需要点保存。</summary>
    public bool EditSelfHealEnabled
    {
        get => _rt.Config.Current.Hotspot.SelfHealEnabled;
        set
        {
            if (value == _rt.Config.Current.Hotspot.SelfHealEnabled) return;
            _rt.Config.Mutate(c => c.Hotspot.SelfHealEnabled = value);
            Raise(nameof(EditSelfHealEnabled));
            Toast(value ? "已开启：网络异常时会自动修复" : "已关闭：发现异常只告警，可手动修复");
            _ = RefreshAsync(true);
        }
    }

    /// <summary>零客户端也自动复位网卡的激进选项，默认关。</summary>
    public bool EditRestartAdapterOnIdleNoRx
    {
        get => _rt.Config.Current.Hotspot.RestartAdapterOnIdleNoRx;
        set
        {
            if (value == _rt.Config.Current.Hotspot.RestartAdapterOnIdleNoRx) return;
            _rt.Config.Mutate(c => c.Hotspot.RestartAdapterOnIdleNoRx = value);
            Raise(nameof(EditRestartAdapterOnIdleNoRx));
            Toast(value ? "已开启：零客户端也自动复位（可能打断传屏）" : "已关闭：零客户端时不动网卡");
            _ = RefreshAsync(true);
        }
    }

    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }
    public bool ToastError { get => _toastError; private set => Set(ref _toastError, value); }
    public string ToastGlyph { get => _toastGlyph; private set => Set(ref _toastGlyph, value); }
    public bool Diagnosing { get => _diagnosing; private set => Set(ref _diagnosing, value); }
    public string DiagnoseSummary { get => _diagnoseSummary; private set => Set(ref _diagnoseSummary, value); }
    public string DiagnoseMeta { get => _diagnoseMeta; private set => Set(ref _diagnoseMeta, value); }

    public bool LogFollow
    {
        get => _logFollow;
        set => Set(ref _logFollow, value);
    }

    public string LogMinLevel
    {
        get => _logMinLevel;
        set
        {
            if (!Set(ref _logMinLevel, value)) return;
            RebuildLogs();
        }
    }

    public string EditSsid { get => _editSsid; set => SetDirty(ref _editSsid, value); }
    public string EditPassphrase { get => _editPassphrase; set => SetDirty(ref _editPassphrase, value); }
    public int EditTargetClients { get => _editTargetClients; set => SetDirty(ref _editTargetClients, value); }
    public int EditRemindMinutes { get => _editRemindMinutes; set => SetDirty(ref _editRemindMinutes, value); }
    public int EditDialogSeconds { get => _editDialogSeconds; set => SetDirty(ref _editDialogSeconds, value); }
    public string EditTimeoutAction { get => _editTimeoutAction; set => SetDirty(ref _editTimeoutAction, value); }
    public int EditSnoozeMinutes { get => _editSnoozeMinutes; set => SetDirty(ref _editSnoozeMinutes, value); }
    public bool EditLessonEnabled { get => _editLessonEnabled; set => SetDirty(ref _editLessonEnabled, value); }
    public bool EditOnlyWhenClassInBoard { get => _editOnlyWhenClassInBoard; set => SetDirty(ref _editOnlyWhenClassInBoard, value); }
    public bool EditNameWithLesson { get => _editNameWithLesson; set => SetDirty(ref _editNameWithLesson, value); }
    public string EditNameTemplate { get => _editNameTemplate; set => SetDirty(ref _editNameTemplate, value); }
    public bool EditCloseToTray { get => _editCloseToTray; set => SetDirty(ref _editCloseToTray, value); }
    public bool EditStartMinimized { get => _editStartMinimized; set => SetDirty(ref _editStartMinimized, value); }
    public string EditTheme { get => _editTheme; set => SetDirty(ref _editTheme, value); }
    public bool EditAutoInstall { get => _editAutoInstall; set => SetDirty(ref _editAutoInstall, value); }
    public bool EditCheckOnBoot { get => _editCheckOnBoot; set => SetDirty(ref _editCheckOnBoot, value); }
    public string EditInstallWindow { get => _editInstallWindow; set => SetDirty(ref _editInstallWindow, value); }
    public bool EditUpdatePlugin { get => _editUpdatePlugin; set => SetDirty(ref _editUpdatePlugin, value); }
    public bool EditTraySaveKeepOpen { get => _editTraySaveKeepOpen; set => SetDirty(ref _editTraySaveKeepOpen, value); }
    public bool EditTraySaveWithoutRename { get => _editTraySaveWithoutRename; set => SetDirty(ref _editTraySaveWithoutRename, value); }
    public bool EditTrayShowPreviewReminder { get => _editTrayShowPreviewReminder; set => SetDirty(ref _editTrayShowPreviewReminder, value); }
    /// <summary>
    /// 打开习惯模式前先吓一跳（0.8.0）：没设置习惯软件的科目在冲突时会被静默跳过，
    /// 老师会以为程序坏了，所以先用确认框把后果说清楚；取消就保持关闭。
    /// </summary>
    public bool EditHabitsEnabled
    {
        get => _editHabitsEnabled;
        set
        {
            if (value && !_editHabitsEnabled && !_habitConfirmSuppress)
            {
                HabitConfirmVisible = true;
                Raise(nameof(EditHabitsEnabled));
                return;
            }

            HabitConfirmVisible = false;
            SetDirty(ref _editHabitsEnabled, value);
        }
    }

    /// <summary>高级页的「开启习惯模式」确认层。</summary>
    public bool HabitConfirmVisible
    {
        get => _habitConfirmVisible;
        private set => Set(ref _habitConfirmVisible, value);
    }

    public string EditSkipBreakNames { get => _editSkipBreakNames; set => SetDirty(ref _editSkipBreakNames, value); }
    public string AboutStarsText { get => _aboutStarsText; private set => Set(ref _aboutStarsText, value); }
    public string AboutRepoText => AboutInfo.DefaultRepo;
    public string AboutClassIslandUrl => AboutInfo.ClassIslandSiteUrl;
    public bool EditConflictEnabled { get => _editConflictEnabled; set => SetDirty(ref _editConflictEnabled, value); }
    public string EditIgnoreProcesses { get => _editIgnoreProcesses; set => SetDirty(ref _editIgnoreProcesses, value); }
    public string NewHabitSubjectText { get => _newHabitSubjectText; set => Set(ref _newHabitSubjectText, value); }
    public string HabitStatusText { get => _habitStatusText; private set => Set(ref _habitStatusText, value); }
    public string HabitLastActionText { get => _habitLastActionText; private set => Set(ref _habitLastActionText, value); }

    public IReadOnlyList<UiOption> TimeoutOptions { get; } = UiOptions.TimeoutActions;

    public IReadOnlyList<UiOption> ThemeOptions { get; } = UiOptions.Themes;

    public IReadOnlyList<UiOption> LogLevelOptions { get; } = new[]
    {
        new UiOption("all", "全部日志"),
        new UiOption("info", "信息以上"),
        new UiOption("warn", "警告与错误"),
        new UiOption("error", "只看错误")
    };

    private void SetDirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _settingsDirty = true;
        Raise(name);
    }

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing || !_live) return;
        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(() => SnapshotBuilder.Build(_rt)).ConfigureAwait(true);
            Apply(snapshot);
            if (!_loadedOnce || force) LoadEditFields();
            _loadedOnce = true;
        }
        catch (Exception ex)
        {
            Toast("刷新失败：" + ex.Message, true);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(UiSnapshot s)
    {
        VersionText = $"v{s.Version} · schema {s.Schema}";
        StateText = s.GuardStateText;
        PauseText = s.Paused ? s.PauseText : "守护正常运行";
        HealthStatus = s.GuardStateText switch
        {
            "守护中" => s.Hotspot.On ? "ok" : "warn",
            "已关闭" => "fail",
            _ => "warn"
        };
        Ssid = s.Hotspot.Ssid;
        Passphrase = s.Hotspot.Passphrase;
        HotspotOn = s.Hotspot.On;
        HotspotStateText = s.Hotspot.StateText;
        ClientsText = $"{s.Hotspot.Clients} / {s.Hotspot.TargetClients} 台";
        UplinkText = s.Hotspot.UplinkName;
        MaxPeersText = $"{s.Hotspot.MaxPeers} 台";
        LessonAvailable = s.Lesson.Available;
        LessonStateText = s.Lesson.StateText;
        LessonCurrentText = s.Lesson.Current;
        LessonNextText = s.Lesson.Next;
        LessonCountdownText = s.Lesson.Countdown;
        LessonNameHint = s.Lesson.NameHint;
        SyncSubjects(s.Lesson.Subjects);
        UpdateCurrentText = "v" + s.Update.Current;
        UpdatePhaseText = s.Update.PhaseText;
        UpdateLatestText = string.IsNullOrEmpty(s.Update.Latest) || s.Update.Latest == "-" ? "暂无" : s.Update.Latest;
        UpdateNotes = s.Update.Notes;
        Raise(nameof(UpdateNotesEmpty));
        UpdateError = s.Update.Error;
        UpdateProgress = s.Update.Progress;
        UpdateProgressVisible = s.Update.Phase is "Downloading" or "Ready";
        UpdateProgressText = ClassroomKit.Core.Update.UpdateProgress.Describe(s.Update.Phase, s.Update.Progress);
        HasUpdate = s.Update.HasUpdate;
        UpdatePluginText = s.Update.PluginText;
        UpdatePluginDetail = s.Update.PluginDetail;
        UpdatePluginProgress = s.Update.PluginProgress;
        Raise(nameof(UpdatePluginEmpty));
        Raise(nameof(UpdatePluginProgressVisible));
        UpdatePluginError = s.Update.PluginError;
        UpdatePluginCanUpdate = s.Update.PluginCanUpdate;
        SelfHealText = s.SelfHeal.StatusText;
        SelfHealDetail = s.SelfHeal.Detail;
        Raise(nameof(EditSelfHealEnabled));
        Raise(nameof(EditRestartAdapterOnIdleNoRx));
        HostsText = s.Hosts.Message;
        HostsApplied = s.Hosts.Applied;
        HostsCountText = s.Hosts.Count > 0 ? $"{s.Hosts.Count} 条加速记录" : "还没有写入记录";
        ServiceInstalled = s.Hosts.ServiceInstalled;
        HostsServiceText = !s.Hosts.ServiceInstalled
            ? "护航任务未安装：hosts 写入与静默升级会退化成手动"
            : s.Hosts.ServiceAlive
                ? "护航任务运行中（SYSTEM，每 5 分钟自检）"
                : "护航任务已安装，但最近 20 分钟没有心跳";

        HabitStatusText = !s.Advanced.HabitsEnabled
            ? "习惯模式关闭：冲突照常弹窗交给老师"
            : s.Advanced.RuleCount == 0
                ? "习惯模式已开启，但还没有设置任何科目"
                : $"习惯模式已开启 · {s.Advanced.RuleCount} 条规则 · 当前科目 {s.Advanced.CurrentSubject}";
        HabitLastActionText = string.IsNullOrWhiteSpace(s.Advanced.LastAction)
            ? "还没有自动处理过"
            : s.Advanced.LastAction;
        RebuildHabitRows();

        var hint = _rt.Lesson.NameHint(DateTimeOffset.Now);
        var index = _rt.Lesson.State().Snapshot?.LessonIndex ?? 0;
        BoardPreview = BoardFileName.Render(_rt.Config.Current.BoardSave.NameTemplate, hint, DateTimeOffset.Now, index);

        SyncLogs(s.Logs);
    }

    private void SyncLogs(IReadOnlyList<UiLogLine> incoming)
    {
        if (incoming.Count == 0) return;
        var merged = LogDiff.Merge(_allLogs, incoming, 300);
        if (merged.Count == _allLogs.Count && _allLogs.Count > 0 && merged[^1].Time == _allLogs[^1].Time && merged[^1].Message == _allLogs[^1].Message)
            return;

        // 增量：新列表只是旧列表尾部接上新行（还没到 300 条上限）时只补新行；
        // 日志被清空 / 整段替换 / 超过上限从头部裁掉时，才全量重建 —— 避免每次刷新都 Clear 把滚动位置拉回顶部。
        var added = AddedTailCount(_allLogs, merged);
        if (added > 0 && _allLogs.Count + added == merged.Count)
        {
            for (var i = _allLogs.Count; i < merged.Count; i++)
            {
                _allLogs.Add(merged[i]);
                if (PassesFilter(merged[i])) LogEntries.Add(merged[i]);
            }
            LogsChanged?.Invoke();
            return;
        }

        _allLogs.Clear();
        _allLogs.AddRange(merged);

        LogEntries.Clear();
        foreach (var line in Filter(_allLogs)) LogEntries.Add(line);
        LogsChanged?.Invoke();
    }

    /// <summary>旧列表尾部与新列表开头能对上多少条；对不上（清空、整段替换）返回 0。</summary>
    private static int AddedTailCount(IReadOnlyList<UiLogLine> existing, IReadOnlyList<UiLogLine> merged)
    {
        var limit = Math.Min(existing.Count, merged.Count);
        for (var overlap = limit; overlap > 0; overlap--)
        {
            var matched = true;
            for (var i = 0; i < overlap; i++)
            {
                var a = existing[existing.Count - overlap + i];
                var b = merged[i];
                if (a.Time != b.Time || a.Level != b.Level || !string.Equals(a.Message, b.Message, StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return merged.Count - overlap;
        }
        return 0;
    }

    private List<UiLogLine> Filter(IReadOnlyList<UiLogLine> source) => source.Where(PassesFilter).ToList();

    /// <summary>当前日志级别下这一条要不要显示：增量追加和全量重建共用同一套判断。</summary>
    private bool PassesFilter(UiLogLine line) => _logMinLevel switch
    {
        "error" => line.IsError,
        "warn" => line.IsError || line.IsWarn,
        "info" => !line.IsDebug,
        _ => true
    };

    private void RebuildLogs()
    {
        LogEntries.Clear();
        foreach (var line in Filter(_allLogs)) LogEntries.Add(line);
        LogsChanged?.Invoke();
    }

    private void LoadEditFields()
    {
        if (_settingsDirty) return;
        var c = _rt.Config.Current;
        EditSsid = c.Hotspot.Ssid;
        EditPassphrase = c.Hotspot.Passphrase;
        EditTargetClients = c.Hotspot.TargetClients;
        EditRemindMinutes = c.Lesson.RemindMinutesBefore;
        EditDialogSeconds = c.Lesson.DialogSeconds;
        EditTimeoutAction = c.Lesson.TimeoutAction;
        EditSnoozeMinutes = c.Lesson.SnoozeMinutes;
        EditLessonEnabled = c.Lesson.Enabled;
        EditOnlyWhenClassInBoard = c.Lesson.OnlyWhenClassInBoard;
        EditNameWithLesson = c.BoardSave.NameWithLesson;
        EditNameTemplate = c.BoardSave.NameTemplate;
        EditCloseToTray = c.Ui.CloseToTray;
        EditStartMinimized = c.Ui.StartMinimized;
        EditTheme = c.Ui.Theme;
        EditAutoInstall = c.Update.AutoInstall;
        EditCheckOnBoot = c.Update.CheckOnBoot;
        EditInstallWindow = c.Update.InstallWindow;
        EditUpdatePlugin = c.Update.UpdatePlugin;
        EditTraySaveKeepOpen = c.Tray.SaveKeepOpen;
        EditTraySaveWithoutRename = c.Tray.SaveWithoutRename;
        EditTrayShowPreviewReminder = c.Tray.ShowPreviewReminder;
        EditConflictEnabled = c.Conflict.Enabled;
        EditIgnoreProcesses = string.Join("\n", c.Conflict.IgnoreProcesses ?? new List<string>());
        _habitConfirmSuppress = true;
        HabitConfirmVisible = false;
        EditHabitsEnabled = c.Habits.Enabled;
        _habitConfirmSuppress = false;
        EditSkipBreakNames = string.Join("\n", c.Lesson.SkipBreakNames ?? new List<string>());
        _habitEdits.Clear();
        foreach (var rule in c.Habits.Rules)
            _habitEdits.Add(new HabitRule { Subject = rule.Subject, App = rule.App, Understand = rule.Understand });
        _editSubjects.Clear();
        _editSubjects.AddRange(c.Lesson.Subjects ?? new List<string>());
        RebuildSubjectHint();
        SyncSubjects(Array.Empty<string>());
        RebuildHabitRows(force: true);
        _settingsDirty = false;
    }

    private static readonly HttpClient StarsHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>关于页的 star 数：30 分钟内只查一次，离线就显示「—」。</summary>
    private async Task RefreshStarsAsync(bool force)
    {
        if (!force && _aboutStars is not null && DateTimeOffset.Now - _starsFetchedAt < TimeSpan.FromMinutes(30)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AboutInfo.ApiUrl(AboutInfo.DefaultRepo));
            request.Headers.TryAddWithoutValidation("User-Agent", "ClassroomKit");
            using var response = await StarsHttp.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("stargazers_count", out var element)
                || !element.TryGetInt32(out var stars)) return;
            _aboutStars = stars;
            _starsFetchedAt = DateTimeOffset.Now;
            _ = _dispatcher.BeginInvoke(new Action(() => AboutStarsText = AboutInfo.FormatStars(stars)));
        }
        catch
        {
            // 没网 / GitHub 被挡：保持「—」，不打扰老师
        }
    }

    private void SaveHotspot()
    {
        _rt.Config.Mutate(c =>
        {
            c.Hotspot.Ssid = string.IsNullOrWhiteSpace(EditSsid) ? MachineDefaults.DefaultSsid() : EditSsid.Trim();
            if (!string.IsNullOrWhiteSpace(EditPassphrase)) c.Hotspot.Passphrase = EditPassphrase.Trim();
            c.Hotspot.TargetClients = Math.Clamp(EditTargetClients, 1, 32);
        });
        _settingsDirty = false;
        Toast("热点设置已保存，下一轮巡检生效");
        _ = RefreshAsync(true);
    }

    private void SaveLesson()
    {
        _rt.Config.Mutate(c =>
        {
            c.Lesson.RemindMinutesBefore = Math.Clamp(EditRemindMinutes, 1, 30);
            c.Lesson.DialogSeconds = Math.Clamp(EditDialogSeconds, 5, 600);
            c.Lesson.TimeoutAction = EditTimeoutAction;
            c.Lesson.SnoozeMinutes = Math.Clamp(EditSnoozeMinutes, 1, 60);
            c.Lesson.Enabled = EditLessonEnabled;
            c.Lesson.OnlyWhenClassInBoard = EditOnlyWhenClassInBoard;
            c.BoardSave.NameWithLesson = EditNameWithLesson;
            c.BoardSave.NameTemplate = string.IsNullOrWhiteSpace(EditNameTemplate) ? BoardFileName.DefaultTemplate : EditNameTemplate.Trim();
            c.Lesson.Subjects = LessonSubjects.Normalize(_editSubjects);
            c.Lesson.SkipBreakNames = LessonSubjects.Normalize(SplitProcessList(EditSkipBreakNames));
            c.Lesson.Normalize();
        });
        _settingsDirty = false;
        Toast("课表与板书设置已保存");
        _ = RefreshAsync(true);
    }

    private void SaveUi()
    {
        _rt.Config.Mutate(c =>
        {
            c.Ui.CloseToTray = EditCloseToTray;
            c.Ui.StartMinimized = EditStartMinimized;
            c.Ui.Theme = EditTheme;
            c.Ui.Normalize();
        });
        _settingsDirty = false;
        UiHost.ApplyThemeFromConfig();
        Toast("界面设置已保存");
        _ = RefreshAsync(true);
    }

    private void SaveUpdate()
    {
        _rt.Config.Mutate(c =>
        {
            c.Update.AutoInstall = EditAutoInstall;
            c.Update.CheckOnBoot = EditCheckOnBoot;
            c.Update.InstallWindow = EditInstallWindow ?? "";
            c.Update.UpdatePlugin = EditUpdatePlugin;
        });
        _settingsDirty = false;
        Toast("升级设置已保存");
        _ = RefreshAsync(true);
    }

    private void SaveTray()
    {
        _rt.Config.Mutate(c =>
        {
            c.Tray.SaveKeepOpen = EditTraySaveKeepOpen;
            c.Tray.SaveWithoutRename = EditTraySaveWithoutRename;
            c.Tray.ShowPreviewReminder = EditTrayShowPreviewReminder;
        });
        _settingsDirty = false;
        Toast("托盘菜单已保存，下次打开菜单生效");
        _ = RefreshAsync(true);
    }

    private void SaveHabits()
    {
        _rt.Config.Mutate(c =>
        {
            c.Habits.Enabled = EditHabitsEnabled;
            c.Habits.Rules = _habitEdits
                .Select(x => new HabitRule { Subject = x.Subject, App = x.App, Understand = x.Understand })
                .ToList();
            c.Habits.Normalize();
            c.Conflict.Enabled = EditConflictEnabled;
            c.Conflict.IgnoreProcesses = SplitProcessList(EditIgnoreProcesses);
        });
        _habitEdits.Clear();
        foreach (var rule in _rt.Config.Current.Habits.Rules)
            _habitEdits.Add(new HabitRule { Subject = rule.Subject, App = rule.App, Understand = rule.Understand });
        _settingsDirty = false;
        RebuildHabitRows(force: true);
        Toast(EditHabitsEnabled ? "习惯设置已保存，下一轮巡检生效" : "习惯模式已关闭，冲突恢复弹窗");
        _ = RefreshAsync(true);
    }

    /// <summary>忽略列表：换行、逗号、分号都当分隔符，去掉空白与重复。</summary>
    private static List<string> SplitProcessList(string? text) =>
        (text ?? "")
            .Split(new[] { '\n', '\r', ',', '，', ';', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void AddHabitSubject()
    {
        var name = (NewHabitSubjectText ?? "").Trim();
        if (name.Length == 0) return;
        if (!_habitEdits.Any(x => string.Equals(x.Subject, name, StringComparison.OrdinalIgnoreCase)))
        {
            _habitEdits.Add(new HabitRule { Subject = name, App = "", Understand = true });
            _settingsDirty = true;
            RebuildHabitRows(force: true);
            Toast($"已添加科目「{name}」，选好习惯软件后点保存");
        }
        NewHabitSubjectText = "";
    }

    private void AddSubject()
    {
        var name = (NewSubjectText ?? "").Trim();
        if (name.Length == 0) return;
        if (!_editSubjects.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase)))
        {
            _editSubjects.Add(name);
            _editSubjects.Sort(StringComparer.CurrentCulture);
            _settingsDirty = true;
            SyncSubjects(Array.Empty<string>(), force: true);
            Toast($"已添加科目「{name}」，点保存生效");
        }
        NewSubjectText = "";
    }

    private void ClearSubjectSelection()
    {
        if (_editSubjects.Count == 0) return;
        _editSubjects.Clear();
        _settingsDirty = true;
        SyncSubjects(Array.Empty<string>(), force: true);
        Toast("已清空勾选：所有科目都会提醒时，记得保存");
    }

    private void ToggleSubject(SubjectOption option)
    {
        if (_syncingSubjects) return;
        if (option.IsSelected)
        {
            if (!_editSubjects.Any(x => string.Equals(x, option.Name, StringComparison.OrdinalIgnoreCase)))
                _editSubjects.Add(option.Name);
        }
        else
        {
            _editSubjects.RemoveAll(x => string.Equals(x, option.Name, StringComparison.OrdinalIgnoreCase));
        }
        _settingsDirty = true;
        RebuildSubjectHint();
    }

    private void RebuildSubjectHint() =>
        SubjectHint = _editSubjects.Count == 0 ? "未勾选 = 所有科目都提醒" : $"已勾选 {_editSubjects.Count} 个科目";

    /// <summary>高级页的科目行 = 课表推送 ∪ 生效科目候选 ∪ 配置里的规则 ∪ 手动添加；候选没变就不重排。</summary>
    private void RebuildHabitRows(bool force = false)
    {
        var candidates = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (!candidates.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) candidates.Add(trimmed);
        }

        foreach (var name in _rt.Lesson.AvailableSubjects?.Invoke() ?? Array.Empty<string>()) Add(name);
        foreach (var option in SubjectOptions) Add(option.Name);
        foreach (var name in _rt.Config.Current.Lesson.Subjects ?? new List<string>()) Add(name);
        foreach (var rule in _rt.Config.Current.Habits.Rules) Add(rule.Subject);
        foreach (var rule in _habitEdits) Add(rule.Subject);

        var signature = string.Join('\u0001', candidates);
        if (!force && signature == _habitSignature) return;
        _habitSignature = signature;

        HabitRows.Clear();
        foreach (var subject in candidates)
        {
            var rule = _habitEdits.FirstOrDefault(x => string.Equals(x.Subject, subject, StringComparison.OrdinalIgnoreCase));
            HabitRows.Add(new HabitRow(
                subject,
                rule?.App ?? "",
                rule is { Understand: false } ? "silent" : "understand",
                OnHabitRowChanged));
        }
    }

    /// <summary>行里的下拉一改就回写到编辑列表；保存前只改内存，不写盘。</summary>
    private void OnHabitRowChanged(HabitRow row)
    {
        var rule = _habitEdits.FirstOrDefault(x => string.Equals(x.Subject, row.Subject, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            rule = new HabitRule { Subject = row.Subject };
            _habitEdits.Add(rule);
        }
        rule.App = row.App ?? "";
        rule.Understand = row.Understand != "silent";
        _settingsDirty = true;
    }

    /// <summary>候选科目 = 课表推送 ∪ 配置里已勾选 ∪ 手动添加；只在候选变化时重排，别每秒清空勾选。</summary>
    public void SyncSubjects(IReadOnlyList<string> fromSchedule, bool force = false)
    {
        var candidates = new List<string>();
        void Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var trimmed = name.Trim();
            if (!candidates.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase))) candidates.Add(trimmed);
        }

        foreach (var name in fromSchedule ?? Array.Empty<string>()) Add(name);
        foreach (var name in _editSubjects) Add(name);
        foreach (var name in _rt.Config.Current.Lesson.Subjects ?? new List<string>()) Add(name);

        var current = SubjectOptions.Select(x => x.Name).ToList();
        if (!force && current.Count == candidates.Count && current.SequenceEqual(candidates, StringComparer.OrdinalIgnoreCase))
        {
            RebuildSubjectHint();
            return;
        }

        _syncingSubjects = true;
        try
        {
            SubjectOptions.Clear();
            foreach (var name in candidates)
            {
                var selected = _editSubjects.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                SubjectOptions.Add(new SubjectOption(name, selected, ToggleSubject));
            }
        }
        finally
        {
            _syncingSubjects = false;
        }
        RebuildSubjectHint();
    }

    private void SaveBoardNow()
    {
        RunBackground(() => _rt.SaveBoardNamed(false, _rt.Config.Current.BoardSave.NameWithLesson).Message);
    }

    private void Run(Func<string> action)
    {
        try { Toast(action()); }
        catch (Exception ex) { Toast(ex.Message, true); }
        _ = RefreshAsync(true);
    }

    /// <summary>耗时动作（保存板书、UAC 提权安装护航）放后台，别把定时刷新的界面卡住。</summary>
    private void RunBackground(Func<string> action) => RunAsync(() => Task.Run(action));

    /// <summary>升级类按钮共用一个入口：连点两下不会并发检查/下载/安装。</summary>
    private int _updateBusy;

    private void RunUpdateOnce(Func<Task<string>> action)
    {
        if (Interlocked.CompareExchange(ref _updateBusy, 1, 0) != 0)
        {
            Toast("上一个升级操作还没结束，请稍等");
            return;
        }

        RunAsync(async () =>
        {
            try { return await action().ConfigureAwait(false); }
            finally { Interlocked.Exchange(ref _updateBusy, 0); }
        });
    }

    private void RunAsync(Func<Task<string>> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var message = await action().ConfigureAwait(false);
                _ = _dispatcher.BeginInvoke(new Action(() => { Toast(message); _ = RefreshAsync(true); }));
            }
            catch (Exception ex)
            {
                _ = _dispatcher.BeginInvoke(new Action(() => Toast(ex.Message, true)));
            }
        });
    }

    private async Task RunDiagnoseAsync()
    {
        if (Diagnosing) return;
        Diagnosing = true;
        DiagnoseSummary = "正在自检…";
        DiagnoseMeta = "热点、hosts、计划任务、板书目录会逐条检查，大约几秒。";
        try
        {
            var report = await Task.Run(() => Diagnostics.BuildReport(_rt)).ConfigureAwait(true);
            DiagnoseRows.Clear();
            foreach (var c in report.checks) DiagnoseRows.Add(new DiagRow(c.title, c.status, c.detail, c.next ?? ""));
            DiagnoseSummary = report.summary;
            DiagnoseMeta = $"自检时间 {DateTime.Now:HH:mm:ss}｜v{report.version}｜共 {report.checks.Count} 项";
            var fail = report.checks.Count(x => x.status == "fail");
            var warn = report.checks.Count(x => x.status == "warn");
            HealthStatus = fail > 0 ? "fail" : warn > 0 ? "warn" : "ok";
        }
        catch (Exception ex)
        {
            DiagnoseSummary = "自检失败：" + ex.Message;
        }
        finally
        {
            Diagnosing = false;
        }
    }

    private void SetOnboardingStep(int step)
    {
        _onboardingStep = Math.Clamp(step, 0, OnboardingGuide.Steps.Count - 1);
        Raise(nameof(OnboardingCurrent));
        Raise(nameof(OnboardingStepText));
        Raise(nameof(OnboardingTitle));
        Raise(nameof(OnboardingBody));
        Raise(nameof(OnboardingAction));
        Raise(nameof(OnboardingNextText));
        Raise(nameof(OnboardingAtStart));
        Raise(nameof(OnboardingAtEnd));
        Raise(nameof(OnboardingCanGoBack));
    }

    /// <summary>收起教程但不写 onboardingDone：下次还能从「教程引导」回到原步骤（0.8.1）。</summary>
    private void PauseOnboarding(string? message = null)
    {
        if (!ShowOnboarding) return;
        OnboardingPaused = true;
        ShowOnboarding = false;
        if (!string.IsNullOrEmpty(message)) Toast(message);
    }

    private void ResumeOnboarding()
    {
        ShowOnboarding = true;
        OnboardingPaused = false;
        Toast("教程回来了，接着看");
    }

    private void FinishOnboarding(string message)
    {
        ShowOnboarding = false;
        OnboardingPaused = false;
        try
        {
            _rt.Config.Mutate(c => c.Ui.OnboardingDone = true);
            Toast(message);
        }
        catch (Exception ex)
        {
            Toast("教程状态保存失败：" + ex.Message, true);
        }
    }

    public void Toast(string text, bool error = false)
    {
        // 只排最近 4 条，避免批量失败时刷屏。
        while (_toastQueue.Count >= 4) _toastQueue.Dequeue();
        _toastQueue.Enqueue((text, error));
        if (!ToastVisible) NextToast();
    }

    private void NextToast()
    {
        _toastTimer.Stop();
        if (_toastQueue.Count == 0)
        {
            ToastVisible = false;
            return;
        }
        var (text, error) = _toastQueue.Dequeue();
        ToastText = text;
        ToastError = error;
        ToastGlyph = error ? "\uEA39" : "\uE73E";
        ToastVisible = true;
        // 错误要留够阅读时间，其余轻提示不要挡住视线。
        _toastTimer.Interval = TimeSpan.FromSeconds(error ? 6.0 : 3.4);
        _toastTimer.Start();
    }

    private void DismissToast()
    {
        _toastQueue.Clear();
        _toastTimer.Stop();
        ToastVisible = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _toastTimer.Stop();
    }
}
