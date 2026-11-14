using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using ClassroomKit.ClassIsland.Client;
using ClassroomKit.ClassIsland.Settings;
using ClassroomKit.Core.Client;

namespace ClassroomKit.ClassIsland.Views;

[SettingsPageInfo("classroomkit.hotspot.page", "ClassroomKit 助手", SettingsPageCategory.External)]
public partial class HotspotSettingsPage : SettingsPageBase
{
    private readonly PluginSettingsStore _store;
    private readonly DispatcherTimer _timer;
    private AgentState? _state;

    public HotspotSettingsPage(PluginSettingsStore store)
    {
        _store = store;
        InitializeComponent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await RefreshAsync())
        {
            IsEnabled = false,
        };

        var settings = _store.Current;
        BaseUrlBox.Text = settings.BaseUrl;
        TokenBox.Text = settings.Token;
        TokenSourceText.Text = AgentAccess.DescribeTokenSource(settings);

        // 课表联动：先填本地存的值，刷新时再用守护的实际生效值覆盖显示
        LessonMinutesBox.Text = settings.LessonRemindMinutes.ToString(CultureInfo.InvariantCulture);
        LessonSecondsBox.Text = settings.LessonDialogSeconds.ToString(CultureInfo.InvariantCulture);
        LessonTemplateBox.Text = settings.BoardNameTemplate;
        LessonCloseAfterSaveBox.IsChecked = settings.BoardCloseAfterSave;
        SelectTimeoutAction(settings.LessonTimeoutAction);
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.IsEnabled = true;
        _ = RefreshAsync();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.IsEnabled = false;
    }

    private async Task RefreshAsync()
    {
        await RefreshStateAsync();
        await RefreshLessonAsync();
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            var client = _store.CreateClient();
            var (state, error) = await client.TryGetStateAsync();
            if (state is null)
            {
                _state = null;
                StatusTitle.Text = "ClassroomKit 没在运行";
                StatusBadge.Text = "";
                StatusDetail.Text = string.IsNullOrWhiteSpace(error)
                    ? "连不上本机的守护进程。"
                    : error;
                SsidText.Text = PassphraseText.Text = ClientsText.Text = UplinkText.Text = "—";
                SetButtons(enabled: true);
                return;
            }

            _state = state;
            var hotspot = state.Hotspot;
            StatusTitle.Text = hotspot?.State switch
            {
                "on" or "running" => "热点已开启",
                "starting" or "restarting" => "热点正在启动",
                _ => hotspot?.State == "off" ? "热点已关闭" : "热点状态：" + (hotspot?.State ?? "未知"),
            };
            StatusBadge.Text = state.Enabled ? "" : "（守护已暂停）";
            StatusDetail.Text = string.IsNullOrWhiteSpace(state.LastError)
                ? state.CapacityWarning ?? ""
                : "最近一次错误：" + state.LastError;

            SsidText.Text = hotspot?.Ssid ?? "—";
            PassphraseText.Text = hotspot?.Passphrase ?? "—";
            ClientsText.Text = hotspot is null ? "—" : hotspot.Clients + " / " + hotspot.TargetClients;
            UplinkText.Text = state.Uplink is null
                ? "—"
                : (state.Uplink.Name ?? "未知") + (state.Uplink.HasInternet ? "（有网）" : "（无网）");
            VersionText.Text = "守护版本 " + state.Version + " · 配置结构 v" + state.ConfigSchema
                + (state.LegacyDefaults ? " · 仍是旧默认值，建议点上面「恢复为电脑名」" : "");
            SetButtons(enabled: true);
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "刷新失败：" + ex.Message;
        }
    }

    private void SetButtons(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        FixPeersButton.IsEnabled = enabled;
        RenameButtonSafe();
    }

    private void RenameButtonSafe()
    {
        // 「恢复为电脑名」在没有状态时也能点（走守护的默认值）。
    }

    private async Task RunActionAsync(string action, IDictionary<string, string?>? fields = null)
    {
        StatusMessage.Text = "";
        try
        {
            var client = _store.CreateClient();
            var reply = await client.PostActionAsync(action, fields);
            StatusMessage.Text = reply.Message ?? ("已执行：" + action);
        }
        catch (ClassroomKitClientException ex)
        {
            StatusMessage.Text = "执行失败：" + ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "执行失败：" + ex.Message;
        }

        await RefreshAsync();
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e) => await RunActionAsync("ensure");

    private async void OnStopClick(object? sender, RoutedEventArgs e) => await RunActionAsync("stop");

    private async void OnFixPeersClick(object? sender, RoutedEventArgs e) => await RunActionAsync("fix-peers");

    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        var ssid = SsidBox.Text?.Trim();
        if (string.IsNullOrEmpty(ssid))
        {
            StatusMessage.Text = "热点名称不能为空。";
            return;
        }

        await RunActionAsync("rename-ssid", new Dictionary<string, string?> { ["ssid"] = ssid });
        SsidBox.Text = "";
    }

    private async void OnRenameToMachineNameClick(object? sender, RoutedEventArgs e)
        => await RunActionAsync("rename-ssid", new Dictionary<string, string?> { ["ssid"] = "" });

    private void OnSaveSettingsClick(object? sender, RoutedEventArgs e)
    {
        var baseUrl = BaseUrlBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = new PluginSettings().BaseUrl;

        // 基于当前设置改，别把课表联动那块存的东西冲掉
        var settings = _store.Current;
        settings.BaseUrl = baseUrl;
        settings.Token = TokenBox.Text?.Trim() ?? "";
        _store.Save(settings);

        StatusMessage.Text = "连接设置已保存。";
        _ = RefreshAsync();
    }

    private void OnPanelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _store.Current;
            var baseUrl = settings.BaseUrl.TrimEnd('/');
            var token = AgentAccess.ResolveToken(settings);
            var url = string.IsNullOrEmpty(token) ? baseUrl + "/" : baseUrl + "/?t=" + Uri.EscapeDataString(token);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage.Text = "打不开控制面板：" + ex.Message;
        }
    }

    // ---------------- 课表联动 ----------------

    /// <summary>读守护手里的课表视图（现在上什么课、下一节、上一节、该不该提醒）。读不到就安静地留空。</summary>
    private async Task RefreshLessonAsync()
    {
        try
        {
            var lesson = await _store.CreateClient().GetLessonAsync();
            ApplyLesson(lesson);
        }
        catch
        {
            // 守护没运行、或守护版本还没有 /api/lesson：不刷错误，只把数字清了
            LessonStateText.Text = "还没拿到课表（守护没运行或版本较旧）";
            LessonCurrentText.Text = "—";
            LessonNextText.Text = "—";
            LessonPreviousText.Text = "—";
            LessonNameHintText.Text = "—";
            LessonReminderText.Text = "—";
            LessonLastKeyText.Text = "—";
            LessonEffectiveText.Text = "—";
        }
    }

    private void ApplyLesson(LessonView? lesson)
    {
        if (lesson is null || !lesson.Available)
        {
            LessonStateText.Text = "守护还没收到课表（先确认 ClassIsland 的课表已启用）";
            LessonCurrentText.Text = "—";
            LessonNextText.Text = "—";
            LessonPreviousText.Text = "—";
            LessonNameHintText.Text = "—";
            LessonReminderText.Text = "—";
            LessonLastKeyText.Text = "—";
            LessonEffectiveText.Text = "—";
            return;
        }

        LessonStateText.Text = string.IsNullOrWhiteSpace(lesson.StateText)
            ? lesson.State
            : lesson.StateText + "（" + lesson.State + "）";
        LessonCurrentText.Text = lesson.CurrentSubject ?? "—";
        LessonPreviousText.Text = lesson.PreviousSubject ?? "还没有上一节课的记录";
        LessonNextText.Text = DescribeNext(lesson);
        LessonNameHintText.Text = string.IsNullOrWhiteSpace(lesson.NameHint) ? "—" : lesson.NameHint;

        var reminder = lesson.Reminder;
        if (reminder is null)
        {
            LessonReminderText.Text = "—";
            LessonLastKeyText.Text = "—";
            LessonEffectiveText.Text = "—";
            return;
        }

        var headline = reminder.Enabled ? "会提醒" : "不提醒";
        var when = reminder.RemindAt is { } at ? "，计划 " + at.ToString("HH:mm:ss") : "";
        LessonReminderText.Text = headline + when + "：" + (string.IsNullOrWhiteSpace(reminder.Reason) ? "（守护没给原因）" : reminder.Reason);
        LessonLastKeyText.Text = string.IsNullOrWhiteSpace(reminder.LastRemindedKey) ? "还没提醒过" : reminder.LastRemindedKey;
        LessonEffectiveText.Text = "守护实际生效：提前 " + reminder.MinutesBefore + " 分钟 · 弹窗 " + reminder.DialogSeconds
            + " 秒 · 超时" + DescribeTimeout(reminder.TimeoutAction)
            + (reminder.SnoozedUntil is { } snooze ? " · 已推迟到 " + snooze.ToString("HH:mm") : "");
    }

    private static string DescribeNext(LessonView lesson)
    {
        if (string.IsNullOrWhiteSpace(lesson.NextSubject)) return "今天没有下一节课了";

        var when = lesson.SecondsToNext > 0
            ? " · " + (lesson.SecondsToNext < 60 ? "不到 1 分钟" : ((lesson.SecondsToNext + 59) / 60) + " 分钟后")
            : "";
        var clock = lesson.NextStartAt is { } at ? "（" + at.ToString("HH:mm") + "）" : "";
        return lesson.NextSubject + when + clock;
    }

    private static string DescribeTimeout(string? action) => (action ?? "").Trim().ToLowerInvariant() switch
    {
        "save" => "自动保存",
        "save-close" => "保存并关闭 ClassInX",
        "dismiss" => "忽略本次",
        "wait" => "不自动关闭（一直等）",
        _ => action ?? "未知",
    };

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        var value = int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
        return Math.Clamp(value, min, max);
    }

    private void SelectTimeoutAction(string? action)
    {
        var wanted = (action ?? "").Trim().ToLowerInvariant();
        foreach (var item in LessonTimeoutBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && string.Equals(tag, wanted, StringComparison.OrdinalIgnoreCase))
            {
                LessonTimeoutBox.SelectedItem = item;
                return;
            }
        }

        LessonTimeoutBox.SelectedIndex = 0;
    }

    private string CurrentTimeoutAction()
        => LessonTimeoutBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "save";

    /// <summary>保存课表联动设置：课本地的那份，同时写给守护（板书配置 + 课前提醒配置）。</summary>
    private async void OnLessonSaveClick(object? sender, RoutedEventArgs e)
    {
        LessonMessage.Text = "";

        var settings = _store.Current.Clone();
        settings.LessonRemindMinutes = ParseInt(LessonMinutesBox.Text, settings.LessonRemindMinutes, 1, 30);
        settings.LessonDialogSeconds = ParseInt(LessonSecondsBox.Text, settings.LessonDialogSeconds, 5, 600);
        settings.LessonTimeoutAction = CurrentTimeoutAction();
        settings.BoardNameTemplate = LessonTemplateBox.Text?.Trim() ?? "";
        settings.BoardCloseAfterSave = LessonCloseAfterSaveBox.IsChecked == true;
        settings.Normalize();
        _store.Save(settings);

        // 回填被收进区间的值，让界面显示的就是实际生效的
        LessonMinutesBox.Text = settings.LessonRemindMinutes.ToString(CultureInfo.InvariantCulture);
        LessonSecondsBox.Text = settings.LessonDialogSeconds.ToString(CultureInfo.InvariantCulture);
        LessonTemplateBox.Text = settings.BoardNameTemplate;
        SelectTimeoutAction(settings.LessonTimeoutAction);

        var lines = new List<string> { "插件设置已保存" };

        var board = await TryPostAsync("board-config", new Dictionary<string, string?>
        {
            ["nameWithLesson"] = "1",
            ["nameTemplate"] = settings.BoardNameTemplate,
            ["closeAfterSave"] = settings.BoardCloseAfterSave ? "1" : "0",
        });
        lines.Add("板书：" + board);

        var lesson = await TryPostAsync("lesson-config", new Dictionary<string, string?>
        {
            ["minutesBefore"] = settings.LessonRemindMinutes.ToString(CultureInfo.InvariantCulture),
            ["dialogSeconds"] = settings.LessonDialogSeconds.ToString(CultureInfo.InvariantCulture),
            ["timeoutAction"] = settings.LessonTimeoutAction,
        });
        lines.Add("课前提醒：" + lesson);

        LessonMessage.Text = string.Join("；", lines);
        await RefreshAsync();
    }

    private async Task<string> TryPostAsync(string action, IDictionary<string, string?> fields)
    {
        try
        {
            var reply = await _store.CreateClient().PostActionAsync(action, fields);
            return reply.Ok ? (reply.Message ?? "已保存") : "守护返回失败：" + (reply.Message ?? "没有说明");
        }
        catch (Exception ex)
        {
            return "没写进去（" + ex.Message + "）";
        }
    }

    private async Task RunLessonActionAsync(string action, IDictionary<string, string?>? fields, string fallback)
    {
        LessonMessage.Text = "";
        try
        {
            var reply = await _store.CreateClient().PostActionAsync(action, fields);
            LessonMessage.Text = reply.Ok ? (reply.Message ?? fallback) : "失败：" + (reply.Message ?? "守护返回失败");
        }
        catch (Exception ex)
        {
            LessonMessage.Text = "执行失败：" + ex.Message;
        }

        await RefreshLessonAsync();
    }

    private async void OnLessonPreviewClick(object? sender, RoutedEventArgs e)
        => await RunLessonActionAsync("board-reminder-preview", null, "已弹出预览");

    private async void OnLessonSnoozeClick(object? sender, RoutedEventArgs e)
    {
        var minutes = Math.Clamp(_store.Current.LessonSnoozeMinutes, 1, 60);
        await RunLessonActionAsync("board-reminder-snooze",
            new Dictionary<string, string?> { ["minutes"] = minutes.ToString(CultureInfo.InvariantCulture) },
            "已推迟 " + minutes + " 分钟提醒");
    }

    private async void OnLessonUnsnoozeClick(object? sender, RoutedEventArgs e)
        => await RunLessonActionAsync("board-reminder-snooze", new Dictionary<string, string?> { ["minutes"] = "0" }, "已取消稍后提醒");
}
