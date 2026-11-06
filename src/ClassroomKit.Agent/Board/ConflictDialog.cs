using System.Drawing;
using ClassroomKit.Core.Board;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Board;

/// <summary>多板书软件全屏冲突的提示窗：四个选项，60 秒不选＝都保留。</summary>
public sealed class ConflictDialog : Forms.Form
{
    private readonly Forms.Label _countdown;
    private int _secondsLeft;

    private ConflictDialog(string description, int timeoutSec)
    {
        Text = "板书软件冲突";
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        ClientSize = new Size(520, 300);
        BackColor = Color.FromArgb(24, 26, 30);
        ForeColor = Color.FromArgb(230, 232, 236);
        Font = new Font("Microsoft YaHei UI", 9.5f);

        var title = new Forms.Label
        {
            Text = "检测到板书软件冲突",
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            Location = new Point(24, 20),
            AutoSize = true
        };

        var detail = new Forms.Label
        {
            Text = "当前状态：" + description + "\r\n\r\n同时开着希沃白板和 ClassInX 的悬浮窗，又进入了全屏场景，" +
                   "两个软件的悬浮窗/画笔可能互相抢屏。请选择保留哪一个：",
            Location = new Point(24, 58),
            Size = new Size(470, 90)
        };

        _secondsLeft = timeoutSec;
        _countdown = new Forms.Label
        {
            Text = $"{_secondsLeft} 秒后自动选择「都保留」",
            ForeColor = Color.FromArgb(150, 155, 165),
            Location = new Point(24, 258),
            AutoSize = true
        };

        var y = 152;
        Add(new[] { "都保留", "保留 ClassInX（退出希沃白板）", "保留希沃白板（退出 ClassInX）", "都不保留（两个都退出）" },
            ConflictChoice.KeepBoth, ConflictChoice.KeepClassIn, ConflictChoice.KeepSeewo, ConflictChoice.KeepNone);

        Controls.AddRange(new Forms.Control[] { title, detail, _countdown });
        for (var i = 0; i < _buttons.Count; i++)
        {
            _buttons[i].Location = new Point(24 + (i % 2) * 246, y + (i / 2) * 48);
            _buttons[i].Size = new Size(232, 38);
            Controls.Add(_buttons[i]);
        }

        var timer = new Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            _countdown.Text = $"{_secondsLeft} 秒后自动选择「都保留」";
            if (_secondsLeft <= 0)
            {
                Choice = ConflictChoice.KeepBoth;
                timer.Stop();
                Close();
            }
        };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();

        // 点右上角关闭 = 都保留
        Choice = ConflictChoice.KeepBoth;
    }

    private readonly List<Forms.Button> _buttons = new();

    public ConflictChoice Choice { get; private set; } = ConflictChoice.KeepBoth;

    private void Add(string[] texts, params ConflictChoice[] choices)
    {
        for (var i = 0; i < texts.Length && i < choices.Length; i++)
        {
            var choice = choices[i];
            var button = new Forms.Button
            {
                Text = texts[i],
                FlatStyle = Forms.FlatStyle.Flat,
                BackColor = i == 0 ? Color.FromArgb(58, 120, 220) : Color.FromArgb(45, 48, 55),
                ForeColor = Color.White,
                Cursor = Forms.Cursors.Hand
            };
            button.Click += (_, _) =>
            {
                Choice = choice;
                Close();
            };
            _buttons.Add(button);
        }
    }

    public static ConflictChoice Ask(string description, int timeoutSec = 60)
    {
        using var dialog = new ConflictDialog(description, timeoutSec);
        dialog.ShowDialog();
        return dialog.Choice;
    }
}
