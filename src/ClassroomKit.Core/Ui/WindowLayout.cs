namespace ClassroomKit.Core.Ui;

/// <summary>一块屏幕的工作区（DIP；WPF 的 SystemParameters.WorkArea 就是这个单位）。</summary>
public readonly record struct WindowRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>
/// 窗口几何的纯计算：把记住的尺寸夹进当前屏幕、把跑到屏幕外的位置拉回来。
/// 之所以按 DIP 算：150% 缩放的 1080p 屏只有约 1280×680 DIP，1120×760 会超出屏幕底部。
/// </summary>
public static class WindowLayout
{
    public const double MinWidth = 960;
    public const double MinHeight = 620;
    public const double DefaultWidth = 1120;
    public const double DefaultHeight = 760;

    /// <summary>四周留白：窗口不贴着屏幕边缘，看起来才不挤。</summary>
    public const double Edge = 40;

    /// <summary>至少要有这么多像素落在屏幕里，才认为记住的位置还能用。</summary>
    public const double MinVisible = 96;

    public static double FitWidth(double width, double workWidth) =>
        Math.Round(Math.Clamp(Safe(width, DefaultWidth), MinWidth, Math.Max(MinWidth, workWidth - Edge)));

    public static double FitHeight(double height, double workHeight) =>
        Math.Round(Math.Clamp(Safe(height, DefaultHeight), MinHeight, Math.Max(MinHeight, workHeight - Edge)));

    public static WindowRect Fit(double width, double height, WindowRect work) =>
        new(0, 0, FitWidth(width, work.Width), FitHeight(height, work.Height));

    public static (double Left, double Top) Center(WindowRect window, WindowRect work) =>
        (Math.Round(work.Left + (work.Width - window.Width) / 2),
         Math.Round(work.Top + (work.Height - window.Height) / 2));

    public static bool IsMostlyVisible(WindowRect window, WindowRect screen, double minVisible = MinVisible)
    {
        var x = Math.Min(window.Right, screen.Right) - Math.Max(window.Left, screen.Left);
        var y = Math.Min(window.Bottom, screen.Bottom) - Math.Max(window.Top, screen.Top);
        return x >= minVisible && y >= minVisible;
    }

    /// <summary>
    /// 还原窗口：尺寸先夹进工作区；位置是 -1 哨兵、或整个窗口已经离开屏幕（拔掉副屏、改分辨率）就居中。
    /// virtualScreen 用 SystemParameters.VirtualScreen*，这样放在副屏上的位置不会被误判成离屏。
    /// </summary>
    public static WindowRect Restore(
        double width,
        double height,
        double left,
        double top,
        WindowRect workArea,
        WindowRect virtualScreen)
    {
        var size = Fit(width, height, workArea);
        var saved = new WindowRect(left, top, size.Width, size.Height);

        var sentinel = Math.Abs(left + 1) < 0.001 && Math.Abs(top + 1) < 0.001;
        if (!sentinel && IsMostlyVisible(saved, virtualScreen)) return saved;

        var (cx, cy) = Center(size, workArea);
        return saved with { Left = cx, Top = cy };
    }

    private static double Safe(double value, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;
}
