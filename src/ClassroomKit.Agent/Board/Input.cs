using System.Drawing;
using System.Drawing.Imaging;

namespace ClassroomKit.Agent.Board;

/// <summary>鼠标/键盘注入与截屏取样。</summary>
public static class Input
{
    private const byte VkShift = 0x10;
    private const uint MouseRightUp = 0x0010;

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

        // ALT down / SetForegroundWindow / ALT up 每一轮都包在 try/finally 里：
        // 中途抛异常或被中断时 ALT 会留在按下状态，整机键盘就像坏了一样（每次按键都被当成菜单快捷键）。
        // 最多试 3 轮：前台锁偶尔要两次才放行，但每轮都保证把 ALT 抬起来，不会成倍放大风险。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Native.keybd_event(Native.VkMenu, 0, 0, UIntPtr.Zero);
                Thread.Sleep(30);
                Native.SetForegroundWindow(h);
                Thread.Sleep(30);
            }
            catch
            {
                // 无交互桌面 / 窗口已销毁时忽略，下面的前后台判断会返回 false
            }
            finally
            {
                RaiseKey(Native.VkMenu);
            }

            Thread.Sleep(120);
            if (Native.GetForegroundWindow() == h) return true;
        }

        return Native.GetForegroundWindow() == h;
    }

    /// <summary>
    /// 兜底抬键：ALT / CTRL / SHIFT 与鼠标左右键全部抬起。
    /// 注入动作被异常或中断打断后（窗口提前关掉、线程被中止）用它把整机输入状态恢复干净，
    /// 否则表现为「键盘像坏了一直弹菜单」「鼠标像一直按着」。
    /// </summary>
    public static void ReleaseAllInputs()
    {
        RaiseKey(Native.VkMenu);
        RaiseKey(Native.VkControl);
        RaiseKey(VkShift);
        RaiseMouse(Native.MouseLeftUp);
        RaiseMouse(MouseRightUp);
    }

    /// <summary>抬起一个键。没有交互桌面时忽略失败：抬键失败不能再抛。</summary>
    private static void RaiseKey(byte vk)
    {
        try
        {
            Native.keybd_event(vk, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>抬起一个鼠标键，参数是 mouse_event 的 up 标志。</summary>
    private static void RaiseMouse(uint flag)
    {
        try
        {
            Native.mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
        }
        catch
        {
            // 忽略
        }
    }

    public static bool IsForeground(long handle) => Native.GetForegroundWindow() == new IntPtr(handle);

    public static void MoveTo(int x, int y) => Native.SetCursorPos(x, y);

    /// <summary>按住左键从一点拖到另一点（用来把 ClassInX 的对话框往上挪）。</summary>
    public static void DragTo(int fromX, int fromY, int toX, int toY)
    {
        Native.SetCursorPos(fromX, fromY);
        Thread.Sleep(120);
        Native.mouse_event(Native.MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            Thread.Sleep(120);
            const int steps = 12;
            for (var i = 1; i <= steps; i++)
            {
                Native.SetCursorPos(fromX + (toX - fromX) * i / steps, fromY + (toY - fromY) * i / steps);
                Thread.Sleep(15);
            }
            Thread.Sleep(120);
        }
        finally
        {
            // 拖到一半被打断也必须松左键，否则整机鼠标像一直按着
            RaiseMouse(Native.MouseLeftUp);
            Thread.Sleep(220);
        }
    }

    public static void Click(int x, int y)
    {
        Native.SetCursorPos(x, y);
        Thread.Sleep(40);
        Native.mouse_event(Native.MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
        try
        {
            Thread.Sleep(40);
        }
        finally
        {
            // 按下后万一抛异常，左键也要抬起来
            RaiseMouse(Native.MouseLeftUp);
        }
    }

    public static void SendCtrlS()
    {
        try
        {
            Native.keybd_event(Native.VkControl, 0, 0, UIntPtr.Zero);
            Thread.Sleep(25);
            Native.keybd_event(Native.VkS, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Native.keybd_event(Native.VkS, 0, Native.KeyEventKeyUp, UIntPtr.Zero);
            Thread.Sleep(25);
        }
        finally
        {
            // CTRL 一定要抬起：留住的 CTRL 会把老师后续的点击全变成多选
            RaiseKey(Native.VkControl);
        }
    }

    public static void SendEscape()
    {
        try
        {
            Native.keybd_event(Native.VkEscape, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
        }
        finally
        {
            // ESC 抬起放 finally：中途异常不会把 ESC 留在按下状态
            RaiseKey(Native.VkEscape);
        }
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
