using System.Windows;
using System.Windows.Threading;
using ClassroomKit.Core.Board;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 下一节同科目时弹出的独立置顶小窗。它不是主窗口的遮罩：主窗口可能最小化在托盘，
/// 这个提示仍然要能出现在 ClassInX 上面。
/// </summary>
public partial class BoardReuseWindow : Window
{
    private readonly BoardReuseTarget _target;
    private readonly Func<BoardReuseTarget, bool> _onReuse;
    private readonly Action<BoardReuseTarget> _onSnooze;
    private readonly Action<BoardReuseTarget> _onDismiss;

    public BoardReuseWindow(BoardReuseTarget target, Func<BoardReuseTarget, bool> onReuse,
        Action<BoardReuseTarget> onSnooze, Action<BoardReuseTarget> onDismiss)
    {
        _target = target;
        _onReuse = onReuse;
        _onSnooze = onSnooze;
        _onDismiss = onDismiss;
        InitializeComponent();
        MessageText.Text = target.Text;

        // 不关就一直置顶会挡着上课；60 秒没动作按「稍后」处理，让老师还能再等到提示
        var autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        autoClose.Tick += (_, _) =>
        {
            autoClose.Stop();
            Close();
        };
        Closed += (_, _) => autoClose.Stop();
        autoClose.Start();
    }

    private void OnReuse(object sender, RoutedEventArgs e)
    {
        if (_onReuse(_target))
        {
            Close();
            return;
        }

        var file = _target.Record.PrimaryFile;
        MessageText.Text = string.IsNullOrWhiteSpace(file)
            ? "找不到这份板书文件，可能已经被移走或删除。请到「板书」页查看保存历史。"
            : $"打开失败：{file}\n请确认 ClassInX 仍能打开 .edb，或到「板书」页手动打开。";
        ReuseButton.IsEnabled = false;
    }

    private void OnSnooze(object sender, RoutedEventArgs e)
    {
        _onSnooze(_target);
        Close();
    }

    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        _onDismiss(_target);
        Close();
    }
}