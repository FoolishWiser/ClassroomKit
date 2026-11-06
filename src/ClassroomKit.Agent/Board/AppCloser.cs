using System.Diagnostics;
using ClassroomKit.Core.Board;

namespace ClassroomKit.Agent.Board;

/// <summary>把某个软件"退掉"：先发 WM_CLOSE 让它自己收尾，5 秒还在就整棵进程树结束。</summary>
public static class AppCloser
{
    public static (bool Ok, string Message) Close(string[] processNames, string displayName)
    {
        var windows = WindowScanner.Scan()
            .Where(w => w.Visible && processNames.Any(n => w.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var w in windows)
        {
            Native.PostMessage(new IntPtr(w.Handle), Native.WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        for (var i = 0; i < 25; i++)
        {
            if (!AnyRunning(processNames)) return (true, $"已关闭{displayName}");
            Thread.Sleep(200);
        }

        var killed = 0;
        foreach (var name in processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    killed++;
                }
                catch
                {
                    // 已退出/无权限
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        Thread.Sleep(300);
        if (AnyRunning(processNames)) return (false, $"没能关闭{displayName}（可能被保护）");
        return (true, $"已强制关闭{displayName}（{killed} 个进程）");
    }

    public static bool AnyRunning(string[] processNames)
    {
        foreach (var name in processNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch
            {
                // 忽略
            }
        }
        return false;
    }

    public static (bool Ok, string Message) CloseClassIn() =>
        Close(new[] { "ClassIn X", "ClassInX", "ClassIn" }, " ClassInX");

    public static (bool Ok, string Message) CloseSeewo() =>
        Close(new[] { "EasiNote", "EasiNote5", "SeewoPinco", "HiteVision", "HHBS" }, "希沃白板");
}
