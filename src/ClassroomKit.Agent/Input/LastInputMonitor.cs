using System.Runtime.InteropServices;

namespace ClassroomKit.Agent.Input;

/// <summary>
/// 最近一次键盘 / 鼠标输入时间。用 GetLastInputInfo 采样，不装全局钩子；
/// 任何键鼠动作都算「有人操作」，自动保存倒计时据此打断。
/// </summary>
public static class LastInputMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    /// <summary>
    /// 返回最后输入时刻；没有交互桌面（Session 0）或调用失败时返回 null。
    /// GetTickCount 是 32 位、约 49.7 天回绕，用无符号差值计算空闲时长。
    /// </summary>
    public static DateTimeOffset? LastInputAt()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return null;
            if (info.Time == 0) return null;

            var idleMs = unchecked(GetTickCount() - info.Time);
            // 刚开机 / 回绕边界给出的超大值不可信，按“没有输入”处理
            if (idleMs > 0x7FFFFFFF) return null;
            return DateTimeOffset.Now.AddMilliseconds(-(double)idleMs);
        }
        catch
        {
            return null;
        }
    }
}
