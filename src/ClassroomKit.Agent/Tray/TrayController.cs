using System.Diagnostics;
using System.Drawing;
using ClassroomKit.Agent.Board;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Update;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Lesson;
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
    private readonly Forms.ContextMenuStrip _menu;
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
        _menu = menu;
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("打开主界面", null, (_, _) => OpenMainWindow()));
        menu.Items.Add(new Forms.ToolStripMenuItem("打开网页面板", null, (_, _) => OpenPanel()));
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(_resumeItem);
        var saveMenu = new Forms.ToolStripMenuItem("保存板书");
        saveMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("保存并关闭 ClassInX", null, (_, _) => SaveClassInBoard(true, closeAfter: true)));
        saveMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("保存，保留 ClassInX", null, (_, _) => SaveClassInBoard(true, closeAfter: false)));
        saveMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("保存（不按课表改名）", null, (_, _) => SaveClassInBoard(false, closeAfter: false)));
        saveMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        saveMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("预览课前提醒窗", null, (_, _) => PreviewBoardReminder()));
        menu.Items.Add(saveMenu);
        menu.Items.Add(new Forms.ToolStripMenuItem("检查板书软件冲突", null, (_, _) => CheckConflict()));
        menu.Items.Add(new Forms.ToolStripMenuItem("复制热点密码", null, (_, _) => CopyPassword()));
        menu.Items.Add(new Forms.ToolStripMenuItem("查看日志", null, (_, _) => OpenFolder(Paths.LogDir)));
        menu.Items.Add(new Forms.ToolStripMenuItem("检查更新…", null, (_, _) => CheckForUpdates()));
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
        // 左右键都弹菜单；双击不再弹出窗口（菜单里点「打开主界面」）
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                try { _menu.Show(Forms.Cursor.Position); } catch { /* 忽略 */ }
            }
        };

        _runtime.ReminderRequested = ShowBoardReminder;

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

    private void OpenMainWindow()
    {
        try { Desktop.UiHost.Show(); }
        catch (Exception ex)
        {
            _runtime.Log.Warn($"打开主界面失败：{ex.Message}");
        }
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

    /// <summary>托盘菜单：保存 ClassInX 板书（后台线程跑，避免卡住托盘）。</summary>
    private void SaveClassInBoard(bool nameWithLesson, bool closeAfter)
    {
        _runtime.Log.Info($"托盘触发：保存 ClassInX 板书（按课表改名={nameWithLesson}，保存后关闭={closeAfter}）");

        // 注意：保存过程中不能再弹托盘气泡。气泡固定在屏幕右下角，正好压在
        // ClassInX「保存至本地」面板的勾选框和「保存」按钮上（学校一体机 150% 缩放时
        // 面板更低更靠下），会被系统当成最顶层窗口，点击全落在气泡上。
        // 所以只在结束时给一次结果提示；如果超过 8 秒还没完（ClassInX 冷启动要拉起），
        // 再补一条“仍在保存”，那时面板通常已经点完了。
        var finished = 0;
        _ = Task.Run(async () =>
        {
            await Task.Delay(8000).ConfigureAwait(false);
            if (Volatile.Read(ref finished) == 0)
                ShowBalloon("仍在保存板书", "ClassInX 启动或保存较慢，请再等一会儿…", true);
        });

        _ = Task.Run(() =>
        {
            BoardSaveResult result;
            try
            {
                var saved = _runtime.SaveBoardNamed(closeAfter, nameWithLesson);
                result = new BoardSaveResult(saved.Ok, saved.Message, 0, Array.Empty<string>(), Array.Empty<BoardSaveStep>());
            }
            catch (Exception ex)
            {
                _runtime.Log.Error($"保存板书异常：{ex.Message}");
                result = new BoardSaveResult(false, "保存板书异常：" + ex.Message, 0, Array.Empty<string>(), Array.Empty<BoardSaveStep>());
            }

            Interlocked.Exchange(ref finished, 1);
            _runtime.Log.Info(result.Ok ? $"板书保存成功：{result.Message}" : $"板书保存失败：{result.Message}");
            ShowBalloon(result.Ok ? "板书已保存" : "保存板书失败", result.Message, result.Ok);
        });
    }

    /// <summary>到点该提醒时由守护回调：切到 UI 线程弹课前提醒窗，并按选择执行。</summary>
    public void ShowBoardReminder(ReminderDecision decision)
    {
        void Run()
        {
            try
            {
                var cfg = _runtime.Config.Current.Lesson;
                var choice = BoardReminderDialog.Ask(decision.Subject, decision.SecondsToStart, cfg.RemindMinutesBefore, cfg.DialogSeconds, cfg.TimeoutAction);
                _runtime.Log.Info($"课前提醒窗结果：{BoardReminderModel.Label(choice)}");

                switch (choice)
                {
                    case BoardReminderChoice.Save:
                    case BoardReminderChoice.SaveAndClose:
                        var closeAfter = choice == BoardReminderChoice.SaveAndClose;
                        _ = Task.Run(() =>
                        {
                            var (ok, message) = _runtime.SaveBoardNamed(closeAfter, _runtime.Config.Current.BoardSave.NameWithLesson);
                            ShowBalloon(ok ? "板书已保存" : "保存板书失败", message, ok);
                        });
                        break;
                    case BoardReminderChoice.Snooze:
                        _runtime.SnoozeBoardReminder(cfg.SnoozeMinutes);
                        ShowBalloon("稍后提醒", $"再过 {cfg.SnoozeMinutes} 分钟提醒保存板书", true);
                        break;
                    default:
                        _runtime.Log.Info("课前提醒：本次忽略");
                        break;
                }
            }
            catch (Exception ex)
            {
                _runtime.Log.Warn($"课前提醒窗失败：{ex.ToString()}");
            }
        }

        if (_ui is not null) _ui.Post(_ => Run(), null);
        else Run();
    }

    /// <summary>托盘菜单：只预览课前提醒窗，不执行任何保存动作。</summary>
    public void PreviewBoardReminder()
    {
        var cfg = _runtime.Config.Current.Lesson;
        ShowBoardReminder(new ReminderDecision(true, "preview", "preview", DateTimeOffset.Now, "数学", cfg.RemindMinutesBefore * 60));
    }

    /// <summary>托盘菜单：手动检查一次板书冲突（忽略冷却）。</summary>
    private void CheckConflict()
    {
        var (_, message) = _runtime.ConflictWatcher.CheckNow(force: true);
        _runtime.Log.Info($"手动检查板书冲突：{message}");
        if (message != "已触发") ShowBalloon("板书冲突检查", message, false);
    }

    /// <summary>面板上的「预览提示窗」：只显示，不执行任何关闭动作。</summary>
    public void ShowConflictPreview()
    {
        void Run()
        {
            try
            {
                var result = ConflictDialog.Ask("ClassIn悬浮窗 + 希沃悬浮窗 + WPS放映（预览，不会关闭任何软件）", 30);
                var snooze = result.SnoozeToday ? "，勾了今天不再提醒" : "";
                _runtime.Log.Info($"冲突提示窗预览结束，选择：{ConflictDialogModel.Label(result.Choice)}{snooze}（预览不执行动作）");
            }
            catch (Exception ex)
            {
                // 完整堆栈入日志（预览失败时界面本来就没反应，只有日志能定位）；
                // 同时弹气泡，避免用户点了「预览」却什么都看不到。
                _runtime.Log.Warn($"预览冲突窗失败：{ex.ToString()}");
                ShowBalloon("预览冲突窗失败", ex.Message, false);
            }
        }

        if (_ui is not null) _ui.Post(_ => Run(), null);
        else Run();
    }

    /// <summary>由冲突监控回调：切到 UI 线程弹窗，并把选择结果落成动作。</summary>
    public void ShowConflictDialog(BoardSituation situation)
    {
        void Run()
        {
            try
            {
                var result = ConflictDialog.Ask(situation.Describe());
                if (result.SnoozeToday) _runtime.ConflictWatcher.SetSnoozeToday(true);
                _runtime.ConflictWatcher.ApplyChoice(result.Choice);
            }
            catch (Exception ex)
            {
                _runtime.Log.Warn($"板书冲突弹窗失败：{ex.ToString()}");
            }
        }

        if (_ui is not null) _ui.Post(_ => Run(), null);
        else Run();
    }

    private void ShowBalloon(string title, string text, bool ok)
    {
        void Show()
        {
            try
            {
                _icon.ShowBalloonTip(3000, title, text, ok ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
            }
            catch
            {
                // 气泡被系统策略禁用时忽略
            }
        }

        if (_ui is not null) _ui.Post(_ => Show(), null);
        else Show();
    }

    /// <summary>托盘「检查更新」：检查 → 询问 → 下载 → 提权安装。全程用气泡反馈，避免“点了没反应”。</summary>
    private void CheckForUpdates()
    {
        ShowBalloon("检查更新", "正在向 GitHub 查询新版本…", true);

        _ = Task.Run(async () =>
        {
            try
            {
                var status = await _runtime.Update.CheckAsync(force: true).ConfigureAwait(false);

                if (status.Phase == UpdatePhase.Failed)
                {
                    ShowBalloon("检查更新失败", status.Error ?? "未知原因", false);
                    return;
                }

                if (!status.HasUpdate)
                {
                    ShowBalloon("已是最新版本", $"当前 v{status.Current}，不用升级", true);
                    return;
                }

                var notes = (status.Notes ?? "").Trim();
                if (notes.Length > 600) notes = notes[..600] + "…";
                var text = $"发现新版本 v{status.Latest}（当前 v{status.Current}）。\n\n{notes}\n\n" +
                           "现在下载并升级吗？升级时会弹一次管理员授权，程序会短暂退出再自动启动。";
                if (AskYesNo("ClassroomKit 升级", text) != true) return;

                ShowBalloon("正在下载", $"v{status.Latest} 安装包下载中，请稍候…", true);
                var after = await _runtime.Update.DownloadAsync().ConfigureAwait(false);
                if (after.Phase != UpdatePhase.Ready)
                {
                    ShowBalloon("升级失败", after.Error ?? after.PhaseText, false);
                    return;
                }

                var (ok, message) = _runtime.Update.Install();
                ShowBalloon(ok ? "开始安装" : "升级未开始", message, ok);
            }
            catch (Exception ex)
            {
                _runtime.Log.Error($"托盘检查更新异常：{ex.Message}");
                ShowBalloon("检查更新失败", ex.Message, false);
            }
        });
    }

    private bool? AskYesNo(string title, string text)
    {
        bool? answer = null;
        void Ask()
        {
            try
            {
                answer = Forms.MessageBox.Show(text, title, Forms.MessageBoxButtons.YesNo,
                    Forms.MessageBoxIcon.Question) == Forms.DialogResult.Yes;
            }
            catch
            {
                // 没有交互桌面时当作“不升级”
            }
        }

        if (_ui is not null) _ui.Send(_ => Ask(), null);
        else Ask();
        return answer;
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
