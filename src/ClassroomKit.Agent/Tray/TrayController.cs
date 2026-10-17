using System.Diagnostics;
using System.Drawing;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Tray;

/// <summary>托盘图标：状态灯 + 在线数角标 + 右键菜单。</summary>
public sealed class TrayController : IDisposable
{
    private static readonly Color Green = Color.FromArgb(46, 164, 79);
    private static readonly Color Amber = Color.FromArgb(230, 160, 20);
    private static readonly Color Red = Color.FromArgb(200, 60, 60);
    private static readonly Color Grey = Color.FromArgb(140, 140, 140);

    private readonly AgentRuntime _runtime;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.Timer _timer;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _pauseMenu;
    private readonly Forms.ToolStripMenuItem _resumeItem;
    private readonly SynchronizationContext? _ui;

    private Icon? _current;
    private string _lastTooltip = "";

    public TrayController(AgentRuntime runtime, Action onExit)
    {
        _runtime = runtime;
        _ui = SynchronizationContext.Current;
        OnExit = onExit;

        _statusItem = new Forms.ToolStripMenuItem("正在启动…") { Enabled = false };
        _resumeItem = new Forms.ToolStripMenuItem("恢复守护", null, (_, _) => _runtime.ClearPause());
        _pauseMenu = new Forms.ToolStripMenuItem("暂停守护");

        _pauseMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("30 分钟", null, (_, _) => _runtime.PauseFor(TimeSpan.FromMinutes(30))));
        _pauseMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("1 小时", null, (_, _) => _runtime.PauseFor(TimeSpan.FromHours(1))));
        _pauseMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("今天剩余时间", null, (_, _) =>
        {
            var midnight = DateTime.Today.AddDays(1);
            _runtime.PauseFor(midnight - DateTime.Now);
        }));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("打开控制面板", null, (_, _) => OpenPanel()));
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("复制热点密码", null, (_, _) => CopyPassword()));
        menu.Items.Add(new Forms.ToolStripMenuItem("查看日志", null, (_, _) => OpenFolder(Paths.LogDir)));
        menu.Items.Add(new Forms.ToolStripMenuItem("强力卸载（BCU）", null, (_, _) => LaunchBcu()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => RequestExit()));

        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = menu,
            Text = "ClassroomKit 正在启动…",
            Icon = TrayIcons.Build(Grey, null)
        };
        _icon.DoubleClick += (_, _) => OpenPanel();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenPanel();
        };

        _timer = new Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
        Update();
    }

    public Action OnExit { get; }

    private void Update()
    {
        try
        {
            var cfg = _runtime.Config.Current.Hotspot;
            var guard = _runtime.Guard;
            var snap = guard.LastSnapshot ?? TetheringSnapshot.Empty;
            var clients = snap.ClientCount;

            var (color, stateText) = ResolveColor(guard, snap, cfg);

            var tooltip = $"{cfg.Ssid}｜在线 {clients}/{cfg.TargetClients} 台｜{stateText}";
            if (snap.UplinkName is { Length: > 0 }) tooltip += $"｜上行：{snap.UplinkName}";

            var badge = clients > 0 ? clients.ToString() : null;
            if (tooltip != _lastTooltip || _current is null)
            {
                var next = TrayIcons.Build(color, badge);
                _icon.Icon = next;
                _current?.Dispose();
                _current = next;
                _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
                _lastTooltip = tooltip;
            }

            _statusItem.Text = $"{stateText}｜{clients}/{cfg.TargetClients} 台｜{(cfg.Enabled ? "策略：开" : "策略：关")}";
            _resumeItem.Enabled = _runtime.IsPaused;
            _pauseMenu.Enabled = !_runtime.IsPaused;
            _pauseMenu.Text = _runtime.IsPaused
                ? $"暂停中（还剩 {_runtime.PauseRemaining?.TotalMinutes:F0} 分钟）"
                : "暂停守护";
        }
        catch
        {
            // 托盘刷新失败不影响守护
        }
    }

    private static (Color Color, string Text) ResolveColor(HotspotGuard guard, TetheringSnapshot snap, HotspotConfig cfg)
    {
        if (!cfg.Enabled) return (Grey, "已暂停");
        if (snap.Capability == Capability.Disabled) return (Red, "硬件不支持热点");

        return guard.State switch
        {
            GuardState.On => (Green, snap.ClientCount > 0 ? "热点运行中" : "热点已开（无设备）"),
            GuardState.Starting => (Amber, "正在开启"),
            GuardState.WaitUplink => (Amber, "等待网线/上行"),
            GuardState.BlockedWifiUplink => (Red, "上行是 Wi-Fi（不启动）"),
            GuardState.Backoff => (Amber, "开启失败，重试中"),
            GuardState.Disabled => (Grey, "已暂停"),
            GuardState.Failed => (Red, "异常"),
            _ => (Grey, "启动中")
        };
    }

    private void OpenPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_runtime.PanelUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _runtime.Log.Warn($"打开面板失败：{ex.Message}");
        }
    }

    private void CopyPassword()
    {
        try
        {
            var pass = _runtime.Config.Current.Hotspot.Passphrase;
            Forms.Clipboard.SetText(pass);
            _icon.ShowBalloonTip(1500, "已复制", $"热点密码 {pass}", Forms.ToolTipIcon.Info);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    private void LaunchBcu()
    {
        var (ok, message) = Bcu.BcuLauncher.Launch(_runtime.Config.Current.Bcu);
        _runtime.Log.Info($"托盘拉起 BCU：{message}");
        if (!ok)
        {
            Forms.MessageBox.Show(message, "强力卸载", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    public void RequestExit()
    {
        if (_ui is not null) _ui.Post(_ => OnExit(), null);
        else OnExit();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }
}
