using System.Diagnostics;
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

[SettingsPageInfo("classroomkit.hotspot.page", "ClassroomKit 热点", SettingsPageCategory.External)]
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
        _store.Save(new PluginSettings { BaseUrl = baseUrl, Token = TokenBox.Text?.Trim() ?? "" });
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
}
