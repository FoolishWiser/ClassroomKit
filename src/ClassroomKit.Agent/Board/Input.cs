using System.Drawing;
using System.Drawing.Imaging;

namespace ClassroomKit.Agent.Board;

/// <summary>鼠标/键盘注入与截屏取样。</summary>
public static class Input
{
    /// <summary>
    /// 把窗口强制切到前台。
    /// 实测：单纯 SetForegroundWindow / AttachThreadInput / SwitchToThisWindow / 最小化再恢复 在后台进程里全都无效，
    /// 只有「先按一下 ALT 再 SetForegroundWindow」才成功（Windows 的前台锁放行规则）。
    /// </summary>
    public static bool ForceForeground(long handle)
    {
        var h = new IntPtr(handle);

        // 最小化的窗口 SetForegroundWindow 不会还原它，必须先 SW_RESTORE，
        // 否则老师点了「保存板书」只会看到“没反应”。
        if (Native.IsIconic(h))
        {
            Native.ShowWindow(h, Native.SwRestore);
            Thread.Sleep(220);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Native.keybd_event(Native.VkMenu, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            Native.SetForegroundWindow(h);
            Thread.Sleep(30);
            Native.keybd_event(Native.VkMenu, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
            Thread.Sleep(120);

            if (Native.GetForegroundWindow() == h) return true;
        }
        return Native.GetForegroundWindow() == h;
    }

    public static bool IsForeground(long handle) => Native.GetForegroundWindow() == new IntPtr(handle);

    public static void Click(int x, int y)
    {
        Native.SetCursorPos(x, y);
        Thread.Sleep(40);
        Native.mouse_event(Native.MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.mouse_event(Native.MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public static void SendCtrlS()
    {
        Native.keybd_event(Native.VkControl, 0, 0, UIntPtr.Zero);
        Thread.Sleep(25);
        Native.keybd_event(Native.VkS, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(Native.VkS, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
        Thread.Sleep(25);
        Native.keybd_event(Native.VkControl, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
    }

    public static void SendEscape()
    {
        Native.keybd_event(Native.VkEscape, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        Native.keybd_event(Native.VkEscape, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
    }

    /// <summary>把屏幕上一块区域存成 PNG（排查「保存至本地」面板没弹出来时用）。</summary>
    public static bool CaptureRegion(int x, int y, int width, int height, string path)
    {
        if (width <= 0 || height <= 0) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
            }
            bmp.Save(path, ImageFormat.Png);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>截取屏幕上一块区域，取平均亮度与"绿色像素"数量（勾选框判定用）。</summary>
    public static (double Mean, double Peak, int Green, int Bright) Sample(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0, 0, 0);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        }

        double sum = 0, peak = 0;
        int green = 0, bright = 0;
        for (var py = 0; py < height; py++)
        {
            for (var px = 0; px < width; px++)
            {
                var c = bmp.GetPixel(px, py);
                var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                sum += lum;
                if (lum > peak) peak = lum;
                if (lum > 200) bright++;
                // ClassIn 的勾选是绿色 (#34A884 附近)
                if (c.G > 110 && c.G - c.R > 40 && c.G - c.B > 25) green++;
            }
        }
        return (sum / (width * height), peak, green, bright);
    }

    /// <summary>
    /// 在给定矩形里找绿色按钮的质心（ClassInX 的「确定 / 保存」都是绿色胶囊按钮）。
    /// 返回 null 表示这块区域没有明显的绿色按钮。
    /// </summary>
    public static (int X, int Y)? FindGreenButton(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        }

        long sx = 0, sy = 0;
        var count = 0;
        for (var py = 0; py < height; py += 2)
        {
            for (var px = 0; px < width; px += 2)
            {
                var c = bmp.GetPixel(px, py);
                if (c.G > 110 && c.G - c.R > 40 && c.G - c.B > 25)
                {
                    sx += px;
                    sy += py;
                    count++;
                }
            }
        }

        if (count < 20) return null;
        return (x + (int)(sx / count), y + (int)(sy / count));
    }

    /// <summary>勾选框判定：绿色填充或高亮像素足够多即视为已勾选。</summary>
    public static Bitmap? CaptureBitmap(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsChecked(int x, int y, int size = 11)
    {
        var (_, _, green, bright) = Sample(x, y, size, size);
        return green >= 3 || bright >= 3;
    }
}
