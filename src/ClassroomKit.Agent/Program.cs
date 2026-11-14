using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tray;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent;

internal static class Program
{
    // 机器级：同一台机器只允许一份守护（计划任务、手动启动、Session 0/1 都算同一份）
    private const string MutexName = @"Global\ClassroomKit.Agent";

    [STAThread]
    private static int Main(string[] args)
    {
        ConsoleHost.Attach();
        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "";

        try
        {
            return command switch
            {
                "" or "run" or "--run" => RunTray(),
                "--ensure" => Commands.Ensure(),
                "--on" or "--start" => Commands.SetPolicy(true),
                "--off" or "--stop" => Commands.SetPolicy(false),
                "--status" => Commands.Status(),
                "--probe" => Commands.ProbeCli(),
                "--fix-peers" => Commands.FixPeersCli(),
                "--install-task" => Commands.InstallTask(),
                "--panel" => Commands.Panel(),
                "--uplinks" => Commands.UplinksCli(),
                "--save-classin-board" or "--save-board" => Commands.SaveClassInBoardCli(args),
                "--lesson-status" => Commands.LessonStatusCli(),
                "--lesson-reminder-test" => Commands.LessonReminderTestCli(args),
                "--diagnose" => Commands.DiagnoseCli(),
                "--rotate-token" => Commands.RotateTokenCli(),
                "--probe-classin" => Commands.ProbeClassInCli(),
                "--conflict-check" => Commands.ConflictCheckCli(args),
                "--conflict-preview" => Commands.ConflictPreviewCli(args),
                "--uninstall" => Commands.Uninstall(args),
                "--msi-setup" => MsiSetup.Install(),
                "--msi-cleanup" => MsiSetup.Cleanup(),
                "--uninstall-task" => Commands.UninstallTask(),
                "--install-service" => Commands.InstallServiceTask(),
                "--uninstall-service" => Commands.UninstallServiceTask(),
                "--hosts-status" => Commands.HostsStatusCli(),
                "--hosts-refresh" => Commands.HostsRefreshCli(args),
                "--hosts-restore" => Commands.HostsRestoreCli(),
                "--service-tick" => Commands.ServiceTickCli(),
                "--check-update" => Commands.CheckUpdateCli(args),
                "--update" => Commands.UpdateCli(args),
                "--version" or "-v" => Commands.PrintVersion(),
                "--help" or "-h" or "/?" => Commands.Help(),
                _ => Unknown(command)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ClassroomKit 出错：{ex}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"未知参数：{command}");
        return Commands.Help();
    }

    private static int RunTray()
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // 已有实例在跑：不重复开热点、不抢端口，直接把策略确保为开就退出
            try { Commands.Ensure(); } catch { /* 已有实例可能刚起来，忽略 */ }
            Desktop.UiHost.SignalShow();
            return 0;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);

        // WPF 负责消息循环与主窗口，WinForms 托盘挂同一个线程
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        // 先装好 Dispatcher 同步上下文：托盘控制器在构造时就捕获它，否则弹窗会投递不出去
        SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
        using var host = new Desktop.AgentHost();
        Desktop.UiHost.Initialize(app, host.Runtime);
        try
        {
            app.Run();
        }
        finally
        {
            Desktop.UiHost.Shutdown();
        }

        return 0;
    }
}
