using System.Diagnostics;

namespace ClassroomKit.Agent.Desktop;

/// <summary>用系统默认程序打开网址/目录。</summary>
internal static class ShellExec
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // 打不开就算了：桌面端还能继续用
        }
    }
}
