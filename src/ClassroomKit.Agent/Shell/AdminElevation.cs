using System.Diagnostics;

namespace ClassroomKit.Agent.Shell;

public static class AdminElevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员重新启动自己（会弹一次 UAC），并同步等待结束。</summary>
    public static int RunElevatedAndWait(string arguments)
    {
        var psi = new ProcessStartInfo(Paths.ExePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var proc = Process.Start(psi);
        if (proc is null) return -1;
        proc.WaitForExit();
        return proc.ExitCode;
    }
}
