using System.Diagnostics;
using System.Drawing.Drawing2D;
using ClassroomKit.Core.Lesson;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 课前提醒窗：无边框圆角卡片，倒计时超时按配置自动行动（默认保存板书、不关 ClassInX）。
/// 鼠标停在窗上时倒计时暂停，离开后继续；键盘 1/2/3/Esc 可直接选择。
/// </summary>
public sealed class BoardReminderDialog : Forms.Form
{
    private const int W = 600;
    private const int H = 372;
    private const int Pad = 26;

    private static readonly Drawing.Color Ink = Drawing.Color.FromArgb(24, 26, 31);
    private static readonly Drawing.Color Muted = Drawing.Color.FromArgb(104, 110, 122);
    private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(214, 138, 34);
    private static readonly Drawing.Color SaveColor = Drawing.Color.FromArgb(34, 138, 90);
    private static readonly Drawing.Color SaveCloseColor = Drawing.Color.FromArgb(38, 104, 178);
    private static readonly Drawing.Color GhostColor = Drawing.Color.FromArgb(236, 238, 242);
    private static readonly Drawing.Color DangerColor = Drawing.Color.FromArgb(188, 72, 66);

    private readonly BoardReminderModel _model;
    private readonly Forms.Timer _timer = new() { Interval = 100 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<ReminderButton> _buttons = new();
    private long _lastTickMs;
    private float _scale = 1f;
    private Drawing.Rectangle _bodyRect;
    private Drawing.Rectangle _barRect;

    private BoardReminderDialog(BoardReminderModel model)
    {
        _model = model;

        Text = "ClassroomKit 课前提醒";
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Drawing.Color.FromArgb(250, 250, 252);
        DoubleBuffered = true;
        KeyPreview = true;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new Drawing.Size(S(W), S(H));

        AddButton("保存板书（1）", BoardReminderChoice.Save, SaveColor, true);
        AddButton("保存并关闭 ClassInX（2）", BoardReminderChoice.SaveAndClose, SaveCloseColor, true);
        AddButton("稍后提醒（3）", BoardReminderChoice.Snooze, GhostColor, false);
        AddButton("忽略本次（Esc）", BoardReminderChoice.Dismiss, Drawing.Color.FromArgb(246, 220, 218), false);

        _timer.Tick += (_, _) => OnTick();
    }

    public BoardReminderChoice Result => _model.Chosen ?? BoardReminderChoice.Dismiss;

    public static BoardReminderChoice Ask(string? subject, int secondsToStart, int minutesBefore, int timeoutSec, string? timeoutAction)
    {
        var model = new BoardReminderModel(timeoutSec, minutesBefore, subject, secondsToStart, timeoutAction);
        using var dialog = new BoardReminderDialog(model);
        dialog._timer.Start();
        dialog._lastTickMs = 0;
        dialog.ShowDialog();
        return dialog.Result;
    }

    private int S(int value) => Math.Max(1, (int)Math.Round(value * _scale));

    private void AddButton(string text, BoardReminderChoice choice, Drawing.Color accent, bool filled)
    {
        var button = new ReminderButton(text, accent, filled) { TabStop = false };
        button.Clicked += (_, _) => Select(choice);
        _buttons.Add(button);
        Controls.Add(button);
    }

    private void Select(BoardReminderChoice choice)
    {
        _model.Choose(choice);
        _timer.Stop();
        DialogResult = Forms.DialogResult.OK;
        Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _scale = Math.Clamp(DeviceDpi / 96f, 0.75f, 3f);
        ClientSize = new Drawing.Size(S(W), S(H));
        ApplyLayout();
        ApplyRoundCorners();
    }

    private void ApplyLayout()
    {
        var buttonH = S(44);
        var gap = S(10);
        var width = (S(W) - S(Pad) * 2 - gap * 3) / 4;
        var y = S(H) - S(Pad) - buttonH;

        for (var i = 0; i < _buttons.Count; i++)
        {
            var b = _buttons[i];
            b.Bounds = new Drawing.Rectangle(S(Pad) + i * (width + gap), y, width, buttonH);
            b.Invalidate();
        }

        _barRect = new Drawing.Rectangle(S(Pad), y - S(46), S(W) - S(Pad) * 2, S(8));
        _bodyRect = new Drawing.Rectangle(S(Pad), S(112), S(W) - S(Pad) * 2, S(150));
    }

    private void ApplyRoundCorners()
    {
        using var path = RoundRect(new Drawing.Rectangle(0, 0, Width, Height), S(16));
        var old = Region;
        Region = new Drawing.Region(path);
        old?.Dispose();
    }

    private void OnTick()
    {
        var now = _clock.ElapsedMilliseconds;
        var delta = _lastTickMs == 0 ? 0 : now - _lastTickMs;
        _lastTickMs = now;

        // 鼠标停在窗上：暂停倒计时，避免老师读的时候被自动保存打断
        _model.Paused = Bounds.Contains(Forms.Cursor.Position);

        if (_model.Tick((int)delta))
        {
            _timer.Stop();
            DialogResult = Forms.DialogResult.OK;
            Close();
            return;
        }

        Invalidate();
    }

    protected override void OnKeyDown(Forms.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Forms.Keys.D1 or Forms.Keys.NumPad1:
                Select(BoardReminderChoice.Save); e.Handled = true; break;
            case Forms.Keys.D2 or Forms.Keys.NumPad2:
                Select(BoardReminderChoice.SaveAndClose); e.Handled = true; break;
            case Forms.Keys.D3 or Forms.Keys.NumPad3:
                Select(BoardReminderChoice.Snooze); e.Handled = true; break;
            case Forms.Keys.Escape:
                Select(BoardReminderChoice.Dismiss); e.Handled = true; break;
            case Forms.Keys.Enter:
                Select(BoardReminderChoice.Save); e.Handled = true; break;
        }
    }

    protected override void OnMouseDown(Forms.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == Forms.MouseButtons.Left && e.Y < S(100))
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0x00A1, (IntPtr)0x0002, IntPtr.Zero);
        }
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new Drawing.SolidBrush(BackColor))
        using (var path = RoundRect(new Drawing.Rectangle(0, 0, Width, Height), S(16)))
            g.FillPath(bg, path);

        // 顶部警示徽标
        var badge = new Drawing.Rectangle(S(Pad), S(Pad), S(46), S(46));
        using (var accent = new Drawing.SolidBrush(Drawing.Color.FromArgb(252, 240, 220)))
            g.FillEllipse(accent, badge);
        using (var pen = new Drawing.Pen(Accent, S(2)))
            g.DrawEllipse(pen, badge);
        using (var font = new Drawing.Font("Segoe UI Symbol", S(18), Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel))
        using (var brush = new Drawing.SolidBrush(Accent))
        using (var fmt = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center })
            g.DrawString("!", font, brush, badge, fmt);

        var textLeft = badge.Right + S(16);
        var textWidth = Width - textLeft - S(Pad);
        TextRenderer.DrawText(g, "课前提醒", new Drawing.Font("Microsoft YaHei UI", S(13), Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel),
            new Drawing.Rectangle(textLeft, badge.Top + S(2), textWidth, S(20)), Muted, Forms.TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _model.Title, new Drawing.Font("Microsoft YaHei UI", S(23), Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel),
            new Drawing.Rectangle(textLeft, badge.Top + S(22), textWidth, S(34)), Ink,
            Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(g, _model.Body, new Drawing.Font("Microsoft YaHei UI", S(14), Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel),
            _bodyRect, Drawing.Color.FromArgb(64, 68, 76),
            Forms.TextFormatFlags.WordBreak | Forms.TextFormatFlags.NoPadding);

        // 倒计时条
        DrawBar(g);

        TextRenderer.DrawText(g, _model.CountdownText + (_model.Paused ? "（已暂停）" : ""),
            new Drawing.Font("Microsoft YaHei UI", S(12), Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel),
            new Drawing.Rectangle(S(Pad), _barRect.Bottom + S(8), Width - S(Pad) * 2, S(18)), Muted,
            Forms.TextFormatFlags.NoPadding);
    }

    private void DrawBar(Drawing.Graphics g)
    {
        using var track = RoundRect(_barRect, _barRect.Height / 2);
        using (var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(230, 232, 238)))
            g.FillPath(brush, track);

        var progress = Math.Clamp(_model.Progress, 0f, 1f);
        var width = (int)Math.Round(_barRect.Width * progress);
        if (width <= 0) return;

        var fill = new Drawing.Rectangle(_barRect.X, _barRect.Y, Math.Max(width, _barRect.Height), _barRect.Height);
        using var fillPath = RoundRect(fill, _barRect.Height / 2);
        using var gradient = new Drawing.Drawing2D.LinearGradientBrush(fill, Accent, SaveColor, LinearGradientMode.Horizontal);
        g.FillPath(gradient, fillPath);
    }

    private static Drawing.Drawing2D.GraphicsPath RoundRect(Drawing.Rectangle rect, int radius)
    {
        var path = new Drawing.Drawing2D.GraphicsPath();
        var d = Math.Max(1, radius * 2);
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class ReminderButton : Forms.Control
    {
        private readonly Drawing.Color _accent;
        private readonly bool _filled;
        private bool _hover;

        public ReminderButton(string text, Drawing.Color accent, bool filled)
        {
            Text = text;
            _accent = accent;
            _filled = filled;
            Cursor = Forms.Cursors.Hand;
            SetStyle(Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.UserPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
        }

        public event EventHandler? Clicked;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(Forms.MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Drawing.Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundRect(rect, Math.Max(6, rect.Height / 4));

            var fill = _filled ? _accent : Drawing.Color.FromArgb(246, 247, 250);
            var text = _filled ? Drawing.Color.White : Ink;
            if (_hover)
            {
                fill = _filled ? Drawing.Color.FromArgb(Math.Min(255, _accent.R + 16), Math.Min(255, _accent.G + 16), Math.Min(255, _accent.B + 16))
                              : Drawing.Color.FromArgb(232, 234, 240);
            }

            using (var brush = new Drawing.SolidBrush(fill)) g.FillPath(brush, path);
            if (!_filled)
            {
                using var pen = new Drawing.Pen(Drawing.Color.FromArgb(214, 217, 224), 1);
                g.DrawPath(pen, path);
            }

            var flags = Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter
                        | Forms.TextFormatFlags.NoPadding | Forms.TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, Text, new Drawing.Font("Microsoft YaHei UI", 13, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Pixel),
                rect, text, flags);
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
