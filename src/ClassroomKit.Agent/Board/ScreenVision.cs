using System.Drawing;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 截图里找色块的小工具。ClassInX 是 Qt 画的，不同版本/缩放下按钮位置会漂，
/// 所以关键控件优先按颜色和形状定位，配方坐标只当兜底。
/// </summary>
internal static class ScreenVision
{
    internal readonly record struct Blob(int MinX, int MinY, int MaxX, int MaxY, int Area)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public int CenterX => (MinX + MaxX) / 2;
        public int CenterY => (MinY + MaxY) / 2;
    }

    /// <summary>ClassInX 的强调绿（勾选框的勾、保存按钮）：约 #009265。</summary>
    internal static bool IsGreen(Color c) => c.R < 60 && c.G is >= 110 and <= 200 && c.B is >= 80 and <= 145;

    /// <summary>「保存至本地」图标的黄橙色：约 #FFD986。</summary>
    internal static bool IsYellow(Color c) => c.R >= 225 && c.G >= 165 && c.B <= 195 && c.G - c.B >= 45;

    internal static List<Blob> FindBlobs(Bitmap bmp, Func<Color, bool> match, int minArea = 20)
    {
        var w = bmp.Width;
        var h = bmp.Height;
        var result = new List<Blob>();
        if (w <= 0 || h <= 0) return result;

        var seen = new bool[w * h];
        var stack = new Stack<int>(256);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                if (seen[idx]) continue;
                seen[idx] = true;
                if (!match(bmp.GetPixel(x, y))) continue;

                stack.Clear();
                stack.Push(idx);
                int minX = x, maxX = x, minY = y, maxY = y, area = 0;
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    var cy = cur / w;
                    var cx = cur % w;
                    area++;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var nx = cx + dx;
                        var ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var nidx = ny * w + nx;
                        if (seen[nidx]) continue;
                        seen[nidx] = true;
                        if (match(bmp.GetPixel(nx, ny))) stack.Push(nidx);
                    }
                }

                if (area >= minArea) result.Add(new Blob(minX, minY, maxX, maxY, area));
            }
        }

        return result;
    }
}
