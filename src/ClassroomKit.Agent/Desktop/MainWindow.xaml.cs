using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ClassroomKit.Core.Ui;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomKit.Agent.Desktop;

public partial class MainWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwcpRound = 2;

    private readonly AgentRuntime _runtime;
    private readonly MainViewModel _viewModel;
    private bool _closingForReal;

    public MainWindow(AgentRuntime runtime)
    {
        InitializeComponent();
        _runtime = runtime;
        _viewModel = new MainViewModel(runtime);
        DataContext = _viewModel;

        try
        {
            var icon = new BitmapImage(new Uri("pack://application:,,,/ClassroomKit.Agent;component/Assets/app-64.png"));
            BrandIcon.Source = icon;
            Icon = icon;
        }
        catch
        {
            // 图标缺失不影响启动
        }

        var ui = runtime.Config.Current.Ui;
        var work = new WindowRect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        var screen = new WindowRect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = WindowLayout.Restore(ui.WindowWidth, ui.WindowHeight, ui.WindowLeft, ui.WindowTop, work, screen);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = rect.Width;
        Height = rect.Height;
        Left = rect.Left;
        Top = rect.Top;
        if (ui.Maximized) WindowState = WindowState.Maximized;

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnStateChanged;
        IsVisibleChanged += OnVisibleChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        UiHost.ThemeChanged += OnThemeChanged;
        UpdateMaxButton();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        // Win11 圆角；Win10 返回失败，忽略即可
        var corner = DwmwcpRound;
        try { _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int)); }
        catch { }

        ApplyFrameTheme(handle, UiHost.EffectiveThemeIsDark);
    }

    private void OnThemeChanged(string theme) =>
        ApplyFrameTheme(new WindowInteropHelper(this).Handle, theme == "Dark");

    private static void ApplyFrameTheme(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // 老系统没有这个属性
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        UpdateMaxButton();
        _viewModel.SetLive(IsVisible && WindowState != WindowState.Minimized);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel.SetLive(IsVisible && WindowState != WindowState.Minimized);

    private void UpdateMaxButton()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        SystemCommands.ShowSystemMenu(this, point);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveGeometry();

        if (_closingForReal) return;
        if (!_runtime.Config.Current.Ui.CloseToTray) return;

        e.Cancel = true;
        Hide();
    }

    /// <summary>托盘「退出」/退出对话框走这里：真正关窗，不再缩回托盘。</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    private void SaveGeometry()
    {
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            var bounds = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
            if (bounds.Width < WindowLayout.MinWidth || bounds.Height < WindowLayout.MinHeight) return;

            _runtime.Config.Mutate(c =>
            {
                c.Ui.WindowWidth = (int)Math.Round(bounds.Width);
                c.Ui.WindowHeight = (int)Math.Round(bounds.Height);
                c.Ui.WindowLeft = (int)Math.Round(bounds.Left);
                c.Ui.WindowTop = (int)Math.Round(bounds.Top);
                c.Ui.Maximized = maximized;
            });
        }
        catch
        {
            // 几何保存失败不影响使用
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        UiHost.ThemeChanged -= OnThemeChanged;
        _viewModel.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
