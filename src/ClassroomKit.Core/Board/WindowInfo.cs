namespace ClassroomKit.Core.Board;

/// <summary>枚举到的一个顶层窗口。纯数据，便于单测里直接构造。</summary>
public sealed record WindowInfo
{
    public long Handle { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string ProcessPath { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string Title { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint ExStyle { get; init; }
    public bool Visible { get; init; } = true;
    /// <summary>是否处于最小化状态（最小化窗口的矩形是 (-32000,-32000)，不能按尺寸判断）。</summary>
    public bool Minimized { get; init; }

    public const uint WsExTopmost = 0x00000008;
    public const uint WsExTransparent = 0x00000020;

    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
    public bool IsTopmost => (ExStyle & WsExTopmost) != 0;

    /// <summary>窗口矩形是否基本覆盖整个屏幕（容差 8px，任务栏也允许存在）。</summary>
    public bool CoversScreen(int screenWidth, int screenHeight, int tolerance = 8)
    {
        if (!Visible) return false;
        if (Width < screenWidth - tolerance) return false;
        if (Height < screenHeight - tolerance) return false;
        return X <= tolerance && Y <= tolerance;
    }

    /// <summary>占比很小的置顶窗——悬浮窗（ClassIn 496x496、希沃 550x200 都落在这里）。</summary>
    public bool LooksFloating(int screenWidth, int screenHeight, double maxRatio = 0.30)
    {
        if (!Visible) return false;
        var screen = (double)screenWidth * screenHeight;
        if (screen <= 0) return false;
        return Area > 0 && Area < screen * maxRatio;
    }

    public override string ToString() =>
        $"0x{Handle:X} {ProcessName}({ProcessId}) [{ClassName}] \"{Title}\" {Width}x{Height}@{X},{Y} ex=0x{ExStyle:X}";
}
