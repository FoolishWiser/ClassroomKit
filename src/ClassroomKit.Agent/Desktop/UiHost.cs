using System.Windows;
using Application = System.Windows.Application;

namespace ClassroomKit.Agent.Desktop;

/// <summary>桌面主窗口的宿主：懒创建窗口、全局主题、单实例唤醒信号。</summary>
internal static class UiHost
{
    private const string ShowEventName = @"Global\ClassroomKit.Show";

    private static Application? _app;
    private static AgentRuntime? _runtime;
    private static MainWindow? _window;
    private static EventWaitHandle? _showEvent;

    public static void Initialize(Application app, AgentRuntime runtime)
    {
        _app = app;
        _runtime = runtime;
        ApplyTheme(runtime.Config.Current.Ui.Theme);

        // 转换器挂在 Application 级：页面在挂进窗口之前就要解析 StaticResource。
        app.Resources["PageVisibility"] = new PageVisibilityConverter();
        app.Resources["BoolVisibility"] = new System.Windows.Controls.BooleanToVisibilityConverter();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
        var thread = new Thread(WaitForShowSignal) { IsBackground = true, Name = "ClassroomKit.ShowSignal" };
        thread.Start();

        if (!runtime.Config.Current.Ui.StartMinimized)
            app.Dispatcher.BeginInvoke(new Action(Show));
    }

    /// <summary>另一个实例想打开主界面时调用（第二个进程退出前）。</summary>
    public static void SignalShow()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch
        {
            // 主实例还没起来 / 没有权限：忽略
        }
    }

    private static void WaitForShowSignal()
    {
        while (true)
        {
            var handle = _showEvent;
            if (handle is null) return;
            handle.WaitOne();
            _app?.Dispatcher.BeginInvoke(new Action(Show));
        }
    }

    public static void Show()
    {
        if (_app is null || _runtime is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_runtime);
            _window.Closed += (_, _) => _window = null;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    public static void ApplyThemeFromConfig()
    {
        if (_runtime is null) return;
        ApplyTheme(_runtime.Config.Current.Ui.Theme);
    }

    private static void ApplyTheme(string theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var name = theme switch { "light" => "Light", "dark" => "Dark", _ => IsSystemDark() ? "Dark" : "Light" };
        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/ClassroomKit.Agent;component/Desktop/Themes/{name}.xaml", UriKind.Absolute)
        });
        merged.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/ClassroomKit.Agent;component/Desktop/Themes/Styles.xaml", UriKind.Absolute)
        });
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Shutdown()
    {
        try { _app?.Dispatcher.Invoke(() => _app.Shutdown()); }
        catch { }
    }
}
