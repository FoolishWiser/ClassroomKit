using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using ClassroomKit.Agent.Diagnose;
using ClassroomKit.Agent.Update;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Model;
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
    public const string PageSettings = "settings";

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

    public MainViewModel(AgentRuntime runtime)
    {
        _rt = runtime;
        var ui = runtime.Config.Current.Ui;
        Pages = new[]
        {
            new NavItem(PageOverview, "概览", "\uE80F", "监控"),
            new NavItem(PageHotspot, "热点与网络", "\uE701", "监控"),
            new NavItem(PageLesson, "课表与板书", "\uE787", "监控"),
            new NavItem(PageUpdate, "升级", "\uE895", "维护"),
            new NavItem(PageDiagnose, "自检与日志", "\uE9D9", "维护"),
            new NavItem(PageSettings, "设置", "\uE713", "维护")
        };
        PagesView = new ListCollectionView(Pages.ToList());
        PagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        _pageKey = ui.LastPage is { Length: > 0 } p && Pages.Any(x => x.Key == p) ? p : PageOverview;
        _pageTitle = Pages.First(x => x.Key == _pageKey).Title;

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
        PreviewReminderCommand = new RelayCommand(() => { _rt.Api.PreviewBoardReminder?.Invoke(); Toast("已请求预览课前提醒窗"); });
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
        OpenPanelCommand = new RelayCommand(() => ShellExec.Open(runtime.PanelUrl));
        OpenLogsCommand = new RelayCommand(() => ShellExec.Open(Paths.LogDir));
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
            var key = value;
            _ = Task.Run(() => _rt.Config.Update(c => c.Ui.LastPage = key));
        }
    }

    public string PageTitle { get => _pageTitle; private set => Set(ref _pageTitle, value); }

    public string PageSubtitle => _pageKey switch
    {
        PageOverview => "守护状态、下一节课与加速一览",
        PageHotspot => "热点开关、密码与设备容量",
        PageLesson => "课表提醒、生效科目与板书命名",
        PageUpdate => "版本、安装包与 ClassIsland 插件",
        PageDiagnose => "一键体检与实时日志",
        PageSettings => "界面、托盘、升级与护航",
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
    public RelayCommand InstallServiceCommand { get; }
    public RelayCommand RefreshHostsCommand { get; }
    public RelayCommand RestoreHostsCommand { get; }
    public RelayCommand OpenPanelCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopyPasswordCommand { get; }
    public RelayCommand FixPeersCommand { get; }
    public RelayCommand RenameToMachineCommand { get; }
    public RelayCommand DismissToastCommand { get; }
    public RelayCommand AddSubjectCommand { get; }
    public RelayCommand ClearSubjectSelectionCommand { get; }
    public RelayCommand SaveTrayCommand { get; }

    public ObservableCollection<DiagRow> DiagnoseRows { get; } = new();
    public ObservableCollection<SubjectOption> SubjectOptions { get; } = new();
    public ObservableCollection<UiLogLine> LogEntries { get; } = new();

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
    public string UpdateError { get => _updateError; private set => Set(ref _updateError, value); }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
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
        UpdateError = s.Update.Error;
        UpdateProgress = s.Update.Progress;
        HasUpdate = s.Update.HasUpdate;
        UpdatePluginText = s.Update.PluginText;
        UpdatePluginDetail = s.Update.PluginDetail;
        UpdatePluginProgress = s.Update.PluginProgress;
        UpdatePluginError = s.Update.PluginError;
        UpdatePluginCanUpdate = s.Update.PluginCanUpdate;
        HostsText = s.Hosts.Message;
        HostsApplied = s.Hosts.Applied;
        HostsCountText = s.Hosts.Count > 0 ? $"{s.Hosts.Count} 条加速记录" : "还没有写入记录";
        ServiceInstalled = s.Hosts.ServiceInstalled;
        HostsServiceText = !s.Hosts.ServiceInstalled
            ? "护航任务未安装：hosts 写入与静默升级会退化成手动"
            : s.Hosts.ServiceAlive
                ? "护航任务运行中（SYSTEM，每 5 分钟自检）"
                : "护航任务已安装，但最近 20 分钟没有心跳";

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
        _editSubjects.Clear();
        _editSubjects.AddRange(c.Lesson.Subjects ?? new List<string>());
        RebuildSubjectHint();
        SyncSubjects(Array.Empty<string>());
        _settingsDirty = false;
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
