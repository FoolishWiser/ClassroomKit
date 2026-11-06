using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using ClassroomKit.Core.Board;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 枚举顶层窗口并转换成 <see cref="WindowInfo"/>；同时给出屏幕尺寸。
/// 每 25–40ms 会扫一次，所以进程信息要缓存：取 MainModule 会卡上百毫秒，
/// 而且只有 WPS 判定才需要路径。
/// </summary>
public static class WindowScanner
{
    private static readonly ConcurrentDictionary<int, (string Name, string Path, DateTime At)> ProcCache = new();
    private static readonly TimeSpan ProcCacheTtl = TimeSpan.FromSeconds(15);

    public static (int Width, int Height) ScreenSize()
    {
        var bounds = Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return (bounds.Width, bounds.Height);
    }

    public static List<WindowInfo> Scan()
    {
        var list = new List<WindowInfo>(128);
        var local = new Dictionary<uint, (string Name, string Path)>();
        var now = DateTime.UtcNow;

        Native.EnumWindows((hWnd, _) =>
        {
            try
            {
                Native.GetWindowThreadProcessId(hWnd, out var pid);
                var title = new StringBuilder(512);
                Native.GetWindowTextW(hWnd, title, title.Capacity);
                var cls = new StringBuilder(256);
                Native.GetClassNameW(hWnd, cls, cls.Capacity);
                if (!Native.GetWindowRect(hWnd, out var rect)) return true;
                if (!Native.IsWindowVisible(hWnd)) return true;

                if (!local.TryGetValue(pid, out var proc))
                {
                    proc = Describe(pid, now);
                    local[pid] = proc;
                }

                list.Add(new WindowInfo
                {
                    Handle = hWnd.ToInt64(),
                    ProcessId = (int)pid,
                    ProcessName = proc.Name,
                    ProcessPath = proc.Path,
                    ClassName = cls.ToString(),
                    Title = title.ToString(),
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    ExStyle = (uint)Native.GetWindowLongPtr(hWnd, Native.GwlExStyle).ToInt64(),
                    Visible = true
                });
            }
            catch
            {
                // 单个窗口出错不影响整体扫描
            }

            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static (string Name, string Path) Describe(uint pid, DateTime now)
    {
        var id = (int)pid;
        if (ProcCache.TryGetValue(id, out var cached) && now - cached.At < ProcCacheTtl)
            return (cached.Name, cached.Path);

        string name = "";
        string path = "";
        try
        {
            using var p = Process.GetProcessById(id);
            name = p.ProcessName;

            // 只有 WPS 家族需要安装路径（判断是不是 Kingsoft 装出来的）
            if (NeedsPath(name)) path = SafeMainModulePath(p);
        }
        catch
        {
            // 进程已退出或权限不足：留空，按类名/标题兜底
        }

        ProcCache[id] = (name, path, now);
        return (name, path);
    }

    private static bool NeedsPath(string name) =>
        name.Contains("wps", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("wpp", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("et", StringComparison.OrdinalIgnoreCase);

    private static string SafeMainModulePath(Process p)
    {
        try
        {
            // 高权限进程（如 ClassInXService）取 MainModule 会抛异常
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }
}
