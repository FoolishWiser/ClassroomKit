using System.ComponentModel;
using System.Windows;

namespace ClassroomKit.Agent.Desktop;

public partial class MainWindow : Window
{
    private readonly AgentRuntime _runtime;
    private readonly MainViewModel _viewModel;

    public MainWindow(AgentRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime);
        DataContext = _viewModel;

        var ui = runtime.Config.Current.Ui;
        Width = Math.Max(980, ui.WindowWidth);
        Height = Math.Max(660, ui.WindowHeight);
        if (ui.WindowLeft >= 0 && ui.WindowTop >= 0)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = ui.WindowLeft;
            Top = ui.WindowTop;
        }

        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveGeometry();
        if (_runtime.Config.Current.Ui.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void SaveGeometry()
    {
        try
        {
            var width = (int)(RestoreBounds.Width > 0 && WindowState != WindowState.Normal ? RestoreBounds.Width : ActualWidth);
            var height = (int)(RestoreBounds.Height > 0 && WindowState != WindowState.Normal ? RestoreBounds.Height : ActualHeight);
            var left = (int)(WindowState == WindowState.Normal ? Left : RestoreBounds.Left);
            var top = (int)(WindowState == WindowState.Normal ? Top : RestoreBounds.Top);
            _runtime.Config.Mutate(c =>
            {
                c.Ui.WindowWidth = width > 0 ? width : c.Ui.WindowWidth;
                c.Ui.WindowHeight = height > 0 ? height : c.Ui.WindowHeight;
                c.Ui.WindowLeft = left;
                c.Ui.WindowTop = top;
            });
        }
        catch
        {
            // 几何保存失败不影响使用
        }
    }
}
