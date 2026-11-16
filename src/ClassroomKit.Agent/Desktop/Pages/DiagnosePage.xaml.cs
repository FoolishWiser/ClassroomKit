using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace ClassroomKit.Agent.Desktop.Pages;

public partial class DiagnosePage : UserControl
{
    private MainViewModel? _viewModel;

    public DiagnosePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        var next = DataContext as MainViewModel;
        if (ReferenceEquals(next, _viewModel))
        {
            if (next is not null) ScrollToEnd(next);
            return;
        }

        Detach();
        _viewModel = next;
        if (_viewModel is null) return;
        _viewModel.LogsChanged += OnLogsChanged;
        ScrollToEnd(_viewModel);
    }

    private void Detach()
    {
        if (_viewModel is not null) _viewModel.LogsChanged -= OnLogsChanged;
        _viewModel = null;
    }

    private void OnLogsChanged()
    {
        // 页面被切走（后台页）时别再 ScrollIntoView：布局照样要算一遍，纯属白烧
        if (!IsVisible) return;
        if (_viewModel is not null) ScrollToEnd(_viewModel);
    }

    /// <summary>「跟随」打开时把日志滚到最后一行；关掉就停在老师正在看的位置。</summary>
    private void ScrollToEnd(MainViewModel viewModel)
    {
        if (!viewModel.LogFollow) return;
        var count = LogList.Items.Count;
        if (count == 0) return;
        var last = LogList.Items[count - 1];
        if (last is not null) LogList.ScrollIntoView(last);
    }
}
