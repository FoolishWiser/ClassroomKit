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
                "--msi-setup" => MsiSetup.Install(),
                "--msi-cleanup" => MsiSetup.Cleanup(),
                "--uninstall-task" => Commands.UninstallTask(),
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
            return 0;
        }

        Forms.Application.EnableVisualStyles();
        Forms.Application.SetCompatibleTextRenderingDefault(false);

        using var context = new AgentContext();
        Forms.Application.Run(context);
        return 0;
    }
}
