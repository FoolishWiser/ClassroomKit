using System.Windows;
using Microsoft.Win32;
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

    /// <summary>主题切换后触发，参数是解析后的 Light / Dark。</summary>
    public static event Action<string>? ThemeChanged;

    public static bool EffectiveThemeIsDark { get; private set; }

    public static void Initialize(Application app, AgentRuntime runtime)
    {
        _app = app;
        _runtime = runtime;
        ApplyTheme(runtime.Config.Current.Ui.Theme);

        // 转换器挂在 Application 级：页面在挂进窗口之前就要解析 StaticResource。
        app.Resources["PageVisibility"] = new PageVisibilityConverter();
        app.Resources["BoolVisibility"] = new System.Windows.Controls.BooleanToVisibilityConverter();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out _);
        var thread = new Thread(WaitForShowSignal) { IsBackground = true, Name = "ClassroomKit.ShowSignal" };
        thread.Start();

        if (!runtime.Config.Current.Ui.StartMinimized)
            app.Dispatcher.BeginInvoke(new Action(Show));
    }

    /// <summary>浅色/深色跟着系统走时，系统切换外观要立刻生效。</summary>
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.VisualStyle) return;
        var app = _app;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_runtime?.Config.Current.Ui.Theme is "light" or "dark") return;
            ApplyTheme("system");
        }));
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

            // 这一拍 Show 抛异常不能把循环带出去：线程一退，托盘再点「打开主界面」就永远没反应了
            try
            {
                _app?.Dispatcher.BeginInvoke(new Action(Show));
            }
            catch
            {
                // 忽略这一拍，等下个唤醒信号
            }
        }
    }

    public static void Show()
    {
        if (_app is null || _runtime is null) return;
        if (_window is null)
        {
            _window = new MainWindow(_runtime);
            ClosedHook(_window);
        }

        // 只有在窗口原本不可见或最小化时才抢前台并闪一下 Topmost：
        // 已经在前台时反复切 Topmost 会把用户正在用的别的程序顶掉（焦点来回跳，看着就像死机）。
        var needForeground = !_window.IsVisible || _window.WindowState == WindowState.Minimized;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        if (!needForeground) return;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private static void ClosedHook(MainWindow window) => window.Closed += (_, _) => _window = null;

    public static void ApplyThemeFromConfig()
    {
        if (_runtime is null) return;
        ApplyTheme(_runtime.Config.Current.Ui.Theme);
    }

    private static void ApplyTheme(string theme)
    {
        var app = Application.Current;
        if (app is null) return;

        var name = theme switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => IsSystemDark() ? "Dark" : "Light"
        };
        EffectiveThemeIsDark = name == "Dark";

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

        ThemeChanged?.Invoke(name);
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
