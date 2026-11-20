using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassroomKit.Agent.Desktop;

/// <summary>
/// 0.8.1：Win11 的窗口圆角与深色标题栏（DWM 属性）。Win10 上调用直接返回失败，是 no-op；
/// 没有做真 Mica：那需要 AllowsTransparency 分层窗口，会丢 ClearType 和滚动性能，不划算。
/// </summary>
internal static class Backdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window, bool dark)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var corner = DwmwcpRound;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            var mode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref mode, sizeof(int));
        }
        catch
        {
            // DWM 不可用（远程桌面 / 老系统）：保持方角，不影响功能
        }
    }
}
