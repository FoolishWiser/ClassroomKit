using System.Collections.ObjectModel;
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

public sealed record NavItem(string Key, string Title, string Glyph);

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
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private volatile bool _refreshing;
    private bool _loadedOnce;
    private bool _settingsDirty;

    private string _pageKey;
    private string _versionText = "";
    private string _stateText = "";
    private string _pauseText = "";
    private string _ssid = "";
    private string _passphrase = "";
    private string _clientsText = "";
    private string _uplinkText = "-";
    private string _maxPeersText = "";
    private bool _hotspotOn;
    private string _lessonStateText = "还没有收到课表";
    private string _lessonCurrentText = "-";
    private string _lessonNextText = "-";
    private string _lessonCountdownText = "-";
    private string _lessonNameHint = "-";
    private bool _lessonAvailable;
    private string _updatePhaseText = "";
    private string _updateLatestText = "-";
    private string _updateNotes = "";
    private string _updateError = "";
    private double _updateProgress;
    private bool _hasUpdate;
    private string _hostsText = "";
    private bool _hostsApplied;
    private bool _serviceInstalled;
    private string _boardPreview = "";
    private string _toastText = "";
    private bool _toastVisible;
    private bool _toastError;
    private bool _diagnosing;
    private string _diagnoseSummary = "还没有自检";

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
    private bool _editStartMinimized = true;
    private string _editTheme = "system";
    private bool _editAutoInstall;
    private bool _editCheckOnBoot = true;
    private string _editInstallWindow = "";

    public MainViewModel(AgentRuntime runtime)
    {
        _rt = runtime;
        var ui = runtime.Config.Current.Ui;
        Pages = new[]
        {
            new NavItem(PageOverview, "概览", "\uE80F"),
            new NavItem(PageHotspot, "热点", "\uE701"),
            new NavItem(PageLesson, "课表与板书", "\uE787"),
            new NavItem(PageUpdate, "升级", "\uE895"),
            new NavItem(PageDiagnose, "自检与日志", "\uE9D9"),
            new NavItem(PageSettings, "设置", "\uE713")
        };
        _pageKey = ui.LastPage is { Length: > 0 } p && Pages.Any(x => x.Key == p) ? p : PageOverview;

        ToggleHotspotCommand = new RelayCommand(() => Run(() =>
        {
            var enable = !_rt.Config.Current.Hotspot.Enabled;
            _rt.SetPolicy(enable);
            return enable ? "已开启热点" : "已关闭热点";
        }));
        RefreshCommand = new RelayCommand(() => { _ = RefreshAsync(true); });
        SaveHotspotCommand = new RelayCommand(SaveHotspot);
        SaveLessonCommand = new RelayCommand(SaveLesson);
        SaveUiCommand = new RelayCommand(SaveUi);
        SaveBoardCommand = new RelayCommand(SaveBoardNow);
        PreviewReminderCommand = new RelayCommand(() => { _rt.Api.PreviewBoardReminder?.Invoke(); Toast("已请求预览课前提醒窗"); });
        SaveBoardNamedCommand = new RelayCommand(() => RunBackground(() => _rt.SaveBoardNamed(false, _rt.Config.Current.BoardSave.NameWithLesson).Message));
        CheckUpdateCommand = new RelayCommand(() => RunAsync(() => { _ = _rt.Update.DownloadAsync(); return Task.FromResult("已开始检查/下载"); }));
        UpgradeCommand = new RelayCommand(() => RunAsync(async () => { var s = await _rt.Update.DownloadAsync(); if (s.Phase == UpdatePhase.Ready) { var (ok, msg) = _rt.Update.Install(); return msg; } return s.PhaseText; }));
        DiagnoseCommand = new RelayCommand(() => { _ = RunDiagnoseAsync(); });
        InstallServiceCommand = new RelayCommand(() => RunBackground(() => _rt.InstallServiceTask().Message));
        RefreshHostsCommand = new RelayCommand(() => RunAsync(async () => { var (ok, msg) = await _rt.Hosts.RefreshAsync(true, CancellationToken.None); return msg; }));
        RestoreHostsCommand = new RelayCommand(() => RunAsync(async () => { var (ok, msg) = await _rt.Hosts.RestoreAsync(CancellationToken.None); return msg; }));
        OpenPanelCommand = new RelayCommand(() => ShellExec.Open(runtime.PanelUrl));
        OpenLogsCommand = new RelayCommand(() => ShellExec.Open(Paths.LogDir));
        CopyPasswordCommand = new RelayCommand(() => { try { System.Windows.Clipboard.SetText(_rt.Config.Current.Hotspot.Passphrase); Toast("密码已复制"); } catch { Toast("复制失败", true); } });
        FixPeersCommand = new RelayCommand(() => RunAsync(async () => { var (ok, msg) = await _rt.Api.FixPeersAsync!(CancellationToken.None); return msg; }));
        RenameToMachineCommand = new RelayCommand(() => Run(() =>
        {
            var name = MachineDefaults.DefaultSsid();
            _rt.Config.Update(c => c.Hotspot.Ssid = name);
            return "热点名已改为：" + name;
        }));

        LoadEditFields();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => _ = RefreshAsync(false), Dispatcher.CurrentDispatcher);
        _timer.Start();
        _ = RefreshAsync(true);
    }

    public IReadOnlyList<NavItem> Pages { get; }

    public string PageKey
    {
        get => _pageKey;
        set
        {
            if (!Set(ref _pageKey, value)) return;
            _rt.Config.Update(c => c.Ui.LastPage = value);
        }
    }

    public RelayCommand ToggleHotspotCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveHotspotCommand { get; }
    public RelayCommand SaveLessonCommand { get; }
    public RelayCommand SaveUiCommand { get; }
    public RelayCommand SaveBoardCommand { get; }
    public RelayCommand SaveBoardNamedCommand { get; }
    public RelayCommand PreviewReminderCommand { get; }
    public RelayCommand CheckUpdateCommand { get; }
    public RelayCommand UpgradeCommand { get; }
    public RelayCommand DiagnoseCommand { get; }
    public RelayCommand InstallServiceCommand { get; }
    public RelayCommand RefreshHostsCommand { get; }
    public RelayCommand RestoreHostsCommand { get; }
    public RelayCommand OpenPanelCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopyPasswordCommand { get; }
    public RelayCommand FixPeersCommand { get; }
    public RelayCommand RenameToMachineCommand { get; }

    public ObservableCollection<DiagRow> DiagnoseRows { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public string PauseText { get => _pauseText; private set => Set(ref _pauseText, value); }
    public string Ssid { get => _ssid; private set => Set(ref _ssid, value); }
    public string Passphrase { get => _passphrase; private set => Set(ref _passphrase, value); }
    public string ClientsText { get => _clientsText; private set => Set(ref _clientsText, value); }
    public string UplinkText { get => _uplinkText; private set => Set(ref _uplinkText, value); }
    public string MaxPeersText { get => _maxPeersText; private set => Set(ref _maxPeersText, value); }
    public bool HotspotOn { get => _hotspotOn; private set => Set(ref _hotspotOn, value); }
    public string LessonStateText { get => _lessonStateText; private set => Set(ref _lessonStateText, value); }
    public string LessonCurrentText { get => _lessonCurrentText; private set => Set(ref _lessonCurrentText, value); }
    public string LessonNextText { get => _lessonNextText; private set => Set(ref _lessonNextText, value); }
    public string LessonCountdownText { get => _lessonCountdownText; private set => Set(ref _lessonCountdownText, value); }
    public string LessonNameHint { get => _lessonNameHint; private set => Set(ref _lessonNameHint, value); }
    public bool LessonAvailable { get => _lessonAvailable; private set => Set(ref _lessonAvailable, value); }
    public string UpdatePhaseText { get => _updatePhaseText; private set => Set(ref _updatePhaseText, value); }
    public string UpdateLatestText { get => _updateLatestText; private set => Set(ref _updateLatestText, value); }
    public string UpdateNotes { get => _updateNotes; private set => Set(ref _updateNotes, value); }
    public string UpdateError { get => _updateError; private set => Set(ref _updateError, value); }
    public double UpdateProgress { get => _updateProgress; private set => Set(ref _updateProgress, value); }
    public bool HasUpdate { get => _hasUpdate; private set => Set(ref _hasUpdate, value); }
    public string HostsText { get => _hostsText; private set => Set(ref _hostsText, value); }
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
    public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
    public bool ToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }
    public bool ToastError { get => _toastError; private set => Set(ref _toastError, value); }
    public bool Diagnosing { get => _diagnosing; private set => Set(ref _diagnosing, value); }
    public string DiagnoseSummary { get => _diagnoseSummary; private set => Set(ref _diagnoseSummary, value); }

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

    public IReadOnlyList<string> TimeoutActions { get; } = new[] { "save", "save-close", "wait", "dismiss" };
    public IReadOnlyList<string> Themes { get; } = new[] { "system", "light", "dark" };

    private void SetDirty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _settingsDirty = true;
        Raise(name);
    }

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing) return;
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
        VersionText = $"v{s.Version} · 配置 schema {s.Schema}";
        StateText = s.GuardStateText;
        PauseText = s.PauseText;
        Ssid = s.Hotspot.Ssid;
        Passphrase = s.Hotspot.Passphrase;
        HotspotOn = s.Hotspot.On;
        ClientsText = $"{s.Hotspot.Clients} / {s.Hotspot.TargetClients} 台";
        UplinkText = s.Hotspot.UplinkName;
        MaxPeersText = $"{s.Hotspot.MaxPeers} 台";
        LessonAvailable = s.Lesson.Available;
        LessonStateText = s.Lesson.StateText;
        LessonCurrentText = s.Lesson.Current;
        LessonNextText = s.Lesson.Next;
        LessonCountdownText = s.Lesson.Countdown;
        LessonNameHint = s.Lesson.NameHint;
        UpdatePhaseText = s.Update.PhaseText;
        UpdateLatestText = string.IsNullOrEmpty(s.Update.Latest) || s.Update.Latest == "-" ? "暂无" : s.Update.Latest;
        UpdateNotes = s.Update.Notes;
        UpdateError = s.Update.Error;
        UpdateProgress = s.Update.Progress;
        HasUpdate = s.Update.HasUpdate;
        HostsText = s.Hosts.Message;
        HostsApplied = s.Hosts.Applied;
        ServiceInstalled = s.Hosts.ServiceInstalled;

        var hint = _rt.Lesson.NameHint(DateTimeOffset.Now);
        var index = _rt.Lesson.State().Snapshot?.LessonIndex ?? 0;
        BoardPreview = BoardFileName.Render(_rt.Config.Current.BoardSave.NameTemplate, hint, DateTimeOffset.Now, index);

        SyncLogs(s.LogLines);
    }

    private void SyncLogs(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        if (LogLines.Count == lines.Count && lines.Count > 0 && LogLines[^1] == lines[^1]) return;
        LogLines.Clear();
        foreach (var line in lines) LogLines.Add(line);
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
            c.Lesson.Normalize();
        });
        _settingsDirty = false;
        Toast("课表与板书设置已保存");
    }

    private void SaveUi()
    {
        _rt.Config.Mutate(c =>
        {
            c.Ui.CloseToTray = EditCloseToTray;
            c.Ui.StartMinimized = EditStartMinimized;
            c.Ui.Theme = EditTheme;
            c.Update.AutoInstall = EditAutoInstall;
            c.Update.CheckOnBoot = EditCheckOnBoot;
            c.Update.InstallWindow = EditInstallWindow ?? "";
            c.Ui.Normalize();
        });
        _settingsDirty = false;
        UiHost.ApplyThemeFromConfig();
        Toast("设置已保存");
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

    /// <summary>耗时动作（保存板书、UAC 提权安装护航）放后台，别把 1 秒刷新的界面卡住。</summary>
    private void RunBackground(Func<string> action) => RunAsync(() => Task.Run(action));

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
        try
        {
            var report = await Task.Run(() => Diagnostics.BuildReport(_rt)).ConfigureAwait(true);
            DiagnoseRows.Clear();
            foreach (var c in report.checks) DiagnoseRows.Add(new DiagRow(c.title, c.status, c.detail, c.next ?? ""));
            DiagnoseSummary = report.summary;
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
        ToastText = text;
        ToastError = error;
        ToastVisible = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { timer.Stop(); ToastVisible = false; };
        timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
