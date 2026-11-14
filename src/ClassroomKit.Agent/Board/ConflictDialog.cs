using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClassroomKit.Core.Board;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Board;

/// <summary>弹窗结果：选了哪个 + 用户是否勾了「今天不再提醒」。</summary>
public readonly record struct ConflictDialogResult(ConflictChoice Choice, bool SnoozeToday);

/// <summary>
/// 板书软件冲突提示窗（0.3.0-pre.4 重做）。
/// 无边框圆角卡片、DPI 自适应（缩放按 DeviceDpi）、每个选项带应用图标与数字快捷键、
/// 底部进度条倒计时且**鼠标停在窗口上就暂停**、键盘 Esc/Enter/1-4/S、深浅色跟随系统、可选「今天不再提醒」。
/// 判定与文案全在 <see cref="ConflictDialogModel"/>，这里只负责画和收事件。
/// </summary>
public sealed class ConflictDialog : Forms.Form
{
    private const int BaseWidth = 520;
    private const int BaseHeight = 402;
    private new const int Padding = 20;
    private const int CardHeight = 54;
    private const int CardGap = 8;
    private const int FirstCardY = 104;

    private static readonly ConflictChoice[] CardOrder =
    {
        ConflictChoice.KeepSeewo, ConflictChoice.KeepClassIn, ConflictChoice.KeepBoth, ConflictChoice.KeepNone
    };

    private readonly ConflictDialogModel _model;
    private readonly string _description;
    private readonly Theme _theme;
    private readonly Forms.Timer _timer;
    private readonly List<ChoiceCard> _cards = new();
    private readonly Forms.Label _countdown;
    private readonly Forms.CheckBox _snooze;
    private readonly CountdownBar _bar;
    private readonly CloseButton _close;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _smallFont;
    private readonly Font _cardTitleFont;
    private float _scale = 1f;
    private bool _closing;

    private ConflictDialog(string description, int timeoutSec)
    {
        _description = string.IsNullOrWhiteSpace(description) ? "希沃白板与 ClassInX 的悬浮窗同时存在，并且已经进入全屏场景" : description;
        _model = new ConflictDialogModel(timeoutSec);
        _theme = Theme.Resolve();

        Text = "板书软件冲突";
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        BackColor = _theme.Back;
        DoubleBuffered = true;
        SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint, true);
        ClientSize = new Size(BaseWidth, BaseHeight);

        _titleFont = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold);
        _bodyFont = new Font("Microsoft YaHei UI", 9.5f);
        _smallFont = new Font("Microsoft YaHei UI", 8.5f);
        _cardTitleFont = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);

        var icons = new Dictionary<ConflictChoice, Image?>
        {
            [ConflictChoice.KeepSeewo] = TryAppIcon("希", "EasiNote", "EasiNote5", "SeewoPinco", "HiteVision", "HHBS"),
            [ConflictChoice.KeepClassIn] = TryAppIcon("C", "ClassIn X", "ClassInX", "ClassIn")
        };

        foreach (var choice in CardOrder)
        {
            var card = new ChoiceCard(this, choice, _cardTitleFont, _bodyFont, _smallFont, icons.GetValueOrDefault(choice));
            _cards.Add(card);
            Controls.Add(card);
        }

        _bar = new CountdownBar(_theme);
        _countdown = new Forms.Label { AutoSize = true, ForeColor = _theme.Muted, Font = _smallFont };
        // 弹窗背景是纯色，子控件一律用同色代替透明：视觉等价，也不依赖控件是否支持透明。
        DialogSurface.FillWith(_countdown, _theme.Back);
        _snooze = new Forms.CheckBox
        {
            Text = "今天不再提醒",
            AutoSize = true,
            ForeColor = _theme.Muted,
            Font = _smallFont,
            Cursor = Forms.Cursors.Hand
        };
        DialogSurface.FillWith(_snooze, _theme.Back);
        _snooze.CheckedChanged += (_, _) => _model.SetSnooze(_snooze.Checked);
        _close = new CloseButton(this, _theme);

        Controls.AddRange(new Forms.Control[] { _bar, _countdown, _snooze, _close });

        _timer = new Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) => OnTick();
    }

    public ConflictDialogResult Result => new(_model.Chosen ?? ConflictChoice.KeepBoth, _model.SnoozeToday);

    private float S(float value) => value * _scale;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _scale = Math.Clamp(DeviceDpi / 96f, 0.75f, 3f);
        ClientSize = new Size((int)S(BaseWidth), (int)S(BaseHeight));
        LayoutControls();
        ApplyRoundedCorners();
    }

    private void LayoutControls()
    {
        var width = ClientSize.Width;
        var cardWidth = width - (int)S(Padding * 2);
        for (var i = 0; i < _cards.Count; i++)
        {
            _cards[i].Arrange(_scale, (int)S(Padding), (int)S(FirstCardY + i * (CardHeight + CardGap)), cardWidth, (int)S(CardHeight));
        }

        var bottom = (int)S(FirstCardY + CardOrder.Length * (CardHeight + CardGap));
        _bar.Arrange((int)S(Padding), bottom + (int)S(4), cardWidth, (int)S(5));
        _countdown.Location = new Point((int)S(Padding), bottom + (int)S(16));
        _snooze.Location = new Point(width - (int)S(Padding) - (int)S(120), bottom + (int)S(14));
        _close.Arrange(width - (int)S(Padding) - (int)S(26), (int)S(Padding) - (int)S(6), (int)S(26));
        Region?.Dispose();
        Region = new Region(RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), S(14)));
    }

    /// <summary>Win11 用 DWM 原生圆角（有阴影、和系统一致）；Win10 只靠 Region 裁圆。</summary>
    private void ApplyRoundedCorners()
    {
        try
        {
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var preference = 2; // DWMWCP_ROUND
                _ = DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
            }
        }
        catch
        {
            // 老系统忽略
        }
    }

    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCaptureNative();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageNative(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static GraphicsPath RoundedPath(Rectangle rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(0, radius * 2);
        if (d <= 1)
        {
            path.AddRectangle(rect);
            return path;
        }

        var r = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(r, 180, 90);
        r.X = rect.Right - d;
        path.AddArc(r, 270, 90);
        r.Y = rect.Bottom - d;
        path.AddArc(r, 0, 90);
        r.X = rect.Left;
        path.AddArc(r, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = RoundedPath(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), S(14)))
        {
            using var fill = new SolidBrush(_theme.Back);
            g.FillPath(fill, path);
            using var pen = new Pen(_theme.Border, Math.Max(1f, S(1)));
            g.DrawPath(pen, path);
        }

        // 头部：琥珀色警示图标 + 标题
        var iconSize = S(28);
        var iconRect = new RectangleF(S(Padding), S(Padding), iconSize, iconSize);
        using (var brush = new SolidBrush(_theme.WarnBg))
        {
            g.FillEllipse(brush, iconRect);
        }

        using (var pen = new Pen(_theme.Warn, Math.Max(1.4f, S(1.4f))))
        {
            g.DrawEllipse(pen, iconRect);
        }

        using (var font = new Font("Segoe UI", 13f * _scale, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(_theme.Warn))
        {
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("!", font, brush, iconRect, format);
        }

        var textLeft = (int)(S(Padding) + iconSize + S(10));
        var textWidth = ClientSize.Width - textLeft - (int)S(Padding + 26);
        TextRenderer.DrawText(g, "检测到板书软件冲突", _titleFont, new Rectangle(textLeft, (int)S(14), textWidth, (int)S(26)), _theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        var detail = _description + "\r\n请选择保留哪一个 —— 没选的话 " + _model.TotalSeconds + " 秒后自动「都保留」，不影响上课。";
        TextRenderer.DrawText(g, detail, _bodyFont, new Rectangle(textLeft, (int)S(42), textWidth, (int)S(48)), _theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

        var footer = "Esc / Enter 都保留　·　1-4 选择　·　S 切换今天不再提醒　·　鼠标停在窗口上可暂停倒计时";
        TextRenderer.DrawText(g, footer, _smallFont, new Rectangle((int)S(Padding), ClientSize.Height - (int)S(24), ClientSize.Width - (int)S(Padding * 2), (int)S(16)),
            _theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private void OnTick()
    {
        if (_closing) return;
        try
        {
            var pointer = Forms.Cursor.Position;
            _model.Paused = !IsDisposed && Visible && ClientRectangle.Contains(PointToClient(pointer));

            if (_model.Tick(_timer.Interval))
            {
                Finish(_model.Chosen ?? ConflictChoice.KeepBoth);
                return;
            }

            _bar.Value = _model.Progress;
            _bar.Invalidate();
            _countdown.Text = ConflictDialogModel.CountdownText(_model.SecondsLeft) + (_model.Paused ? "（已暂停）" : "");
        }
        catch (ObjectDisposedException)
        {
            // 窗口已关，忽略
        }
    }

    private void Finish(ConflictChoice choice)
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        _model.Choose(choice);
        _model.SetSnooze(_snooze.Checked);
        Close();
    }

    internal void Pick(ConflictChoice choice) => Finish(choice);

    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var key = e.KeyCode switch
        {
            Forms.Keys.Escape => "escape",
            Forms.Keys.Enter => "enter",
            Forms.Keys.S => "s",
            Forms.Keys.D1 or Forms.Keys.NumPad1 => "1",
            Forms.Keys.D2 or Forms.Keys.NumPad2 => "2",
            Forms.Keys.D3 or Forms.Keys.NumPad3 => "3",
            Forms.Keys.D4 or Forms.Keys.NumPad4 => "4",
            _ => null
        };
        if (key is null) return;

        var press = ConflictDialogModel.MapKey(key);
        if (press.ToggleSnooze)
        {
            _snooze.Checked = !_snooze.Checked;
        }
        else if (press.Choice is { } choice)
        {
            Finish(choice);
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>卡片/关闭按钮拖动窗口用：点在卡片之间也能移动这张卡片。</summary>
    internal void BeginDrag()
    {
        try
        {
            ReleaseCaptureNative();
            SendMessageNative(Handle, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
        }
        catch
        {
            // 拖动失败无所谓
        }
    }

    protected override void OnFormClosed(Forms.FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            _cardTitleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>弹窗并等用户选。默认「都保留」（60 秒不动等于没说）。</summary>
    public static ConflictDialogResult Ask(string description, int timeoutSec = 60)
    {
        using var dialog = new ConflictDialog(description, timeoutSec);
        dialog._timer.Start();
        dialog.ShowDialog();
        return dialog.Result;
    }

    /// <summary>从正在运行的进程里抠应用图标；没在跑就用首字母色块，不阻塞。</summary>
    private static Image? TryAppIcon(string letter, params string[] processNames)
        => IconCache.Get(letter, processNames);

    // ---- 子控件 ----

    /// <summary>一个选项卡片：圆角底、应用图标、标题、说明、右侧数字徽标。整块可点。</summary>
    private sealed class ChoiceCard : Forms.Panel
    {
        private readonly ConflictDialog _owner;
        private readonly ConflictChoice _choice;
        private readonly Font _titleFont;
        private readonly Font _hintFont;
        private readonly Font _badgeFont;
        private readonly Image? _icon;
        private bool _hover;

        public ChoiceCard(ConflictDialog owner, ConflictChoice choice, Font titleFont, Font hintFont, Font smallFont, Image? icon)
        {
            _owner = owner;
            _choice = choice;
            _titleFont = titleFont;
            _hintFont = hintFont;
            _badgeFont = smallFont;
            _icon = icon;
            // 圆角卡片外的四角本来就是弹窗底色；用同色代替透明，视觉等价且不依赖控件是否支持透明。
            DialogSurface.FillWith(this, owner._theme.Back);
            Cursor = Forms.Cursors.Hand;
            SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint, true);
            Click += (_, _) => owner.Pick(choice);
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
            MouseDown += (_, _) => { };
        }

        public void Arrange(float scale, int x, int y, int width, int height)
        {
            SetBounds(x, y, width, height);
        }

        private bool Primary => _choice is ConflictChoice.KeepSeewo or ConflictChoice.KeepClassIn;

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            var theme = _owner._theme;
            var scale = _owner._scale;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var back = Primary
                ? (_hover ? theme.AccentHover : theme.AccentSoft)
                : (_hover ? theme.ChipHover : theme.Chip);
            var border = _choice == ConflictChoice.KeepNone
                ? (_hover ? theme.Danger : theme.DangerSoft)
                : (Primary ? theme.AccentBorder : theme.Border);
            if (_hover) border = _choice == ConflictChoice.KeepNone ? theme.Danger : theme.Accent;

            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), scale * 10);
            using (var fill = new SolidBrush(back)) g.FillPath(fill, path);
            using (var pen = new Pen(border, Math.Max(1f, scale))) g.DrawPath(pen, path);

            var iconSize = scale * 26;
            var iconRect = new RectangleF(scale * 12, (Height - iconSize) / 2, iconSize, iconSize);
            if (_icon is not null)
            {
                g.DrawImage(_icon, iconRect);
            }
            else
            {
                using var chip = new SolidBrush(Primary ? theme.Accent : theme.FaintChip);
                using var chipPath = RoundedPath(Rectangle.Round(iconRect), scale * 7);
                g.FillPath(chip, chipPath);
                TextRenderer.DrawText(g, ConflictDialogModel.InitialOf(_choice), _titleFont,
                    Rectangle.Round(iconRect), Primary ? Color.White : theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            var textLeft = (int)(scale * 48);
            var textWidth = Width - textLeft - (int)(scale * 46);
            TextRenderer.DrawText(g, ConflictDialogModel.Label(_choice), _titleFont,
                new Rectangle(textLeft, (int)(scale * 8), textWidth, (int)(scale * 21)), theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, ConflictDialogModel.Hint(_choice), _hintFont,
                new Rectangle(textLeft, (int)(scale * 28), textWidth, (int)(scale * 18)), theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            var badge = scale * 20;
            var badgeRect = new RectangleF(Width - badge - (float)scale * 12, (Height - badge) / 2, badge, badge);
            using (var badgeBrush = new SolidBrush(theme.BadgeBg))
            using (var badgePath = RoundedPath(Rectangle.Round(badgeRect), scale * 6))
            {
                g.FillPath(badgeBrush, badgePath);
            }

            TextRenderer.DrawText(g, ConflictDialogModel.Shortcut(_choice).ToString(), _badgeFont,
                Rectangle.Round(badgeRect), theme.BadgeText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseDown(Forms.MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == Forms.MouseButtons.Left && !ContainsFocus) _owner.BeginDrag();
        }
    }

    /// <summary>底部倒计时进度条：从满到空，悬停时停住不动。</summary>
    private sealed class CountdownBar : Forms.Control
    {
        private readonly Theme _theme;
        private double _value;

        public CountdownBar(Theme theme)
        {
            _theme = theme;
            SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint, true);
            // 自定义 Control 默认不支持透明背景；父级是纯色，用同色等价替代。
            DialogSurface.FillWith(this, theme.Back);
            TabStop = false;
        }

        public double Value
        {
            get => _value;
            set => _value = Math.Clamp(value, 0, 1);
        }

        public void Arrange(int x, int y, int width, int height) => SetBounds(x, y, width, height);

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var radius = Height / 2f;
            using var trackPath = RoundedPath(new Rectangle(0, 0, Width, Height), radius);
            using (var track = new SolidBrush(_theme.Track)) g.FillPath(track, trackPath);

            var filled = (int)(Width * (1 - _value));
            if (filled < Height) filled = Height;
            if (filled <= 0) return;
            using var barPath = RoundedPath(new Rectangle(0, 0, filled, Height), radius);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, filled), Height), _theme.Accent, _theme.Warn, LinearGradientMode.Horizontal);
            g.FillPath(brush, barPath);
        }
    }

    /// <summary>右上角关闭按钮（等于「都保留」）。</summary>
    private sealed class CloseButton : Forms.Panel
    {
        private readonly ConflictDialog _owner;
        private readonly Theme _theme;
        private bool _hover;

        public CloseButton(ConflictDialog owner, Theme theme)
        {
            _owner = owner;
            _theme = theme;
            // 同上：用弹窗底色代替透明，四角与弹窗一致。
            DialogSurface.FillWith(this, theme.Back);
            Cursor = Forms.Cursors.Hand;
            SetStyle(Forms.ControlStyles.OptimizedDoubleBuffer | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint, true);
            Click += (_, _) => owner.Pick(ConflictChoice.KeepBoth);
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        public void Arrange(int x, int y, int size) => SetBounds(x, y, size, size);

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover)
            {
                using var fill = new SolidBrush(_theme.ChipHover);
                g.FillEllipse(fill, new Rectangle(0, 0, Width - 1, Height - 1));
            }

            using var pen = new Pen(_hover ? _theme.Text : _theme.Muted, Math.Max(1.4f, _owner._scale * 1.4f));
            var pad = Width * 0.33f;
            g.DrawLine(pen, pad, pad, Width - pad, Height - pad);
            g.DrawLine(pen, Width - pad, pad, pad, Height - pad);
        }
    }

    internal Theme CurrentTheme => _theme;

    /// <summary>深浅色跟随系统（注册表 AppsUseLightTheme）；读不到就深色。</summary>
    internal sealed record Theme(
        Color Back, Color Text, Color Muted, Color Faint, Color Border,
        Color Accent, Color AccentHover, Color AccentSoft, Color AccentBorder,
        Color Chip, Color ChipHover, Color ChipText, Color FaintChip,
        Color BadgeBg, Color BadgeText, Color Track, Color Warn, Color WarnBg,
        Color Danger, Color DangerSoft)
    {
        public static Theme Resolve()
        {
            var light = false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SoftwareMicrosoftWindowsCurrentVersionThemesPersonalize");
                light = key?.GetValue("AppsUseLightTheme") is int value && value == 1;
            }
            catch
            {
                // 读不到就用深色
            }

            return light
                ? new Theme(
                    Back: Color.FromArgb(250, 250, 252), Text: Color.FromArgb(24, 26, 32), Muted: Color.FromArgb(96, 102, 112),
                    Faint: Color.FromArgb(140, 146, 156), Border: Color.FromArgb(226, 229, 235),
                    Accent: Color.FromArgb(37, 99, 235), AccentHover: Color.FromArgb(219, 233, 254), AccentSoft: Color.FromArgb(238, 244, 255),
                    AccentBorder: Color.FromArgb(150, 185, 250), Chip: Color.FromArgb(244, 245, 248), ChipHover: Color.FromArgb(233, 236, 242),
                    ChipText: Color.FromArgb(24, 26, 32), FaintChip: Color.FromArgb(203, 208, 217),
                    BadgeBg: Color.FromArgb(232, 235, 241), BadgeText: Color.FromArgb(96, 102, 112), Track: Color.FromArgb(232, 235, 241),
                    Warn: Color.FromArgb(180, 120, 10), WarnBg: Color.FromArgb(253, 240, 214),
                    Danger: Color.FromArgb(220, 38, 38), DangerSoft: Color.FromArgb(250, 214, 214))
                : new Theme(
                    Back: Color.FromArgb(26, 28, 33), Text: Color.FromArgb(233, 236, 240), Muted: Color.FromArgb(160, 166, 176),
                    Faint: Color.FromArgb(120, 126, 136), Border: Color.FromArgb(56, 60, 68),
                    Accent: Color.FromArgb(96, 165, 250), AccentHover: Color.FromArgb(30, 58, 105), AccentSoft: Color.FromArgb(27, 42, 68),
                    AccentBorder: Color.FromArgb(56, 96, 160), Chip: Color.FromArgb(38, 41, 48), ChipHover: Color.FromArgb(48, 52, 60),
                    ChipText: Color.FromArgb(233, 236, 240), FaintChip: Color.FromArgb(82, 87, 96),
                    BadgeBg: Color.FromArgb(52, 56, 64), BadgeText: Color.FromArgb(180, 186, 196), Track: Color.FromArgb(44, 48, 56),
                    Warn: Color.FromArgb(245, 180, 60), WarnBg: Color.FromArgb(64, 52, 26),
                    Danger: Color.FromArgb(248, 113, 113), DangerSoft: Color.FromArgb(74, 36, 36));
        }
    }

    /// <summary>应用图标缓存：进程在跑就抠真图标，否则留空由卡片画首字母色块。</summary>
    private static class IconCache
    {
        private static readonly Dictionary<string, Image?> Cache = new();

        public static Image? Get(string letter, params string[] processNames)
        {
            var key = string.Join("|", processNames);
            if (Cache.TryGetValue(key, out var cached)) return cached;

            Image? image = null;
            foreach (var name in processNames)
            {
                try
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        using (process)
                        {
                            var path = process.MainModule?.FileName;
                            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                            using var icon = Icon.ExtractAssociatedIcon(path);
                            if (icon is null) continue;
                            image = icon.ToBitmap();
                            break;
                        }
                    }
                }
                catch
                {
                    // 权限/位数不匹配时忽略，换下一个进程名
                }

                if (image is not null) break;
            }

            Cache[key] = image;
            return image;
        }
    }
}
