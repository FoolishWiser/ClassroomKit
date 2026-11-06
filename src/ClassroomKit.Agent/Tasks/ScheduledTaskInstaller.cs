using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassroomKit.Agent.Tasks;

/// <summary>用 schtasks 注册/注销「登录后延迟 8 秒、普通权限、无窗口」的自启任务。</summary>
public static class ScheduledTaskInstaller
{
    public const string TaskName = "ClassroomKit-Agent";

    public static (bool Ok, string Message) Install(string exePath)
    {
        try
        {
            var xml = BuildXml(exePath);
            var temp = Path.Combine(Path.GetTempPath(), "classroomkit-task.xml");
            File.WriteAllText(temp, xml, Encoding.Unicode);
            var (code, output) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{temp}\" /F");
            return code == 0
                ? (true, "计划任务已注册（登录后 8 秒启动）")
                : (false, $"注册失败（{code}）：{output}");
        }
        catch (Exception ex)
        {
            return (false, $"注册失败：{ex.Message}");
        }
    }

    public static (bool Ok, string Message) Uninstall()
    {
        try
        {
            var (code, output) = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            return code == 0
                ? (true, "计划任务已删除")
                : (false, $"删除失败（{code}）：{output}");
        }
        catch (Exception ex)
        {
            return (false, $"删除失败：{ex.Message}");
        }
    }

    public static bool IsInstalled()
    {
        var (code, _) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    /// <summary>
    /// 在“已登录的桌面会话”里立即启动一次。
    /// 为什么不用 <see cref="RunNow"/>：安装程序（MSI 的 deferred 动作）跑在 Session 0，
    /// /Run 会把进程拉进 Session 0 —— 没有托盘、也没有交互桌面。改成临时用交互令牌任务拉起，进程就落在用户会话里。
    /// </summary>
    public static (bool Ok, string Message) StartInUserSession(string exePath)
    {
        var user = ConsoleUser();
        if (string.IsNullOrWhiteSpace(user))
            return (false, "当前没有已登录的桌面用户，跳过立即启动（下次登录会自动启动）");

        const string temp = "ClassroomKit-Agent-Start";
        try
        {
            var create = Run("schtasks.exe",
                $"/Create /TN \"{temp}\" /TR \"\\\"{exePath}\\\"\" /SC ONCE /ST 23:59 /RU \"{user}\" /IT /F");
            if (create.Code != 0) return (false, $"临时任务创建失败（{create.Code}）：{create.Output}");

            var run = Run("schtasks.exe", $"/Run /TN \"{temp}\"");
            Run("schtasks.exe", $"/Delete /TN \"{temp}\" /F");
            return run.Code == 0
                ? (true, $"已在 {user} 的桌面会话启动")
                : (false, $"启动失败（{run.Code}）：{run.Output}");
        }
        catch (Exception ex)
        {
            return (false, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>当前 console（交互桌面）会话的用户名；没人登录就返回 null。</summary>
    public static string? ConsoleUser()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == uint.MaxValue || sessionId == 0) return null;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsUserName, out var buffer, out var bytes))
                return null;
            try
            {
                if (bytes <= 2 || buffer == IntPtr.Zero) return null;
                var name = Marshal.PtrToStringUni(buffer)?.Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private const int WtsUserName = 5;

    // 失败时返回 0xFFFFFFFF
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);

    /// <summary>立即启动一次（还原卡擦掉任务后由 ClassIsland 触发 --ensure 也能兜住）。</summary>
    public static (bool Ok, string Message) RunNow()
    {
        var (code, output) = Run("schtasks.exe", $"/Run /TN \"{TaskName}\"");
        return code == 0 ? (true, "已触发") : (false, $"触发失败（{code}）：{output}");
    }

    private static string BuildXml(string exePath)
    {
        var escaped = System.Security.SecurityElement.Escape(exePath) ?? exePath;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>ClassroomKit 教室一体机热点守护（登录后自动打开热点）</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT8S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <GroupId>S-1-5-32-545</GroupId>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>3</Count>
    </RestartOnFailure>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{escaped}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static (int Code, string Output) Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi);
        if (proc is null) return (-1, "无法启动 schtasks");
        var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit(20000);
        return (proc.ExitCode, output.Trim());
    }
}
