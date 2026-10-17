using System.Drawing;
using System.Drawing.Drawing2D;

namespace ClassroomKit.Agent.Tray;

internal static class TrayIcons
{
    /// <summary>画一个带数字角标的状态圆点，返回 Icon。</summary>
    public static Icon Build(Color fill, string? badge)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(fill);
            using var edge = new Pen(Color.FromArgb(60, 60, 60), 1.6f);
            g.FillEllipse(body, 4, 4, 24, 24);
            g.DrawEllipse(edge, 4, 4, 24, 24);

            if (!string.IsNullOrEmpty(badge))
            {
                using var font = new Font("Segoe UI", badge.Length > 1 ? 9f : 11f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(Color.White);
                var size = g.MeasureString(badge, font);
                g.DrawString(badge, font, text, 16 - size.Width / 2f, 16 - size.Height / 2f + 0.5f);
            }
        }

        // Icon.FromHandle 需要手动销毁原始句柄，这里克隆一份交给调用方
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
