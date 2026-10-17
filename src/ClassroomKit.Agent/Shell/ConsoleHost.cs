using System.Runtime.InteropServices;
using System.Text;

namespace ClassroomKit.Agent.Shell;

/// <summary>WinExe 默认没有控制台；被命令行调用时借用父进程的控制台输出。</summary>
internal static class ConsoleHost
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(-1)) return;

            // 输出走 UTF-8：Git-Bash / Windows Terminal / 管道里都不乱码
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            Console.SetOut(stdout);

            try { Console.OutputEncoding = utf8; } catch { /* 某些重定向场景不允许改 */ }
        }
        catch
        {
            // 没有控制台就静默
        }
    }
}
