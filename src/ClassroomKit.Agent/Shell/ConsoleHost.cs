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
            // 关键：输出已被重定向到管道 / 文件时（bash 的 | 、PowerShell 的 $(...) 捕获、
            // cmd 的 > file），.NET 拿到的标准句柄本来就是对的；此时若再去 AttachConsole
            // 并把 Console.Out 改指 CONOUT$，调用方只会捕获到空串。
            var outRedirected = Console.IsOutputRedirected;
            var errRedirected = Console.IsErrorRedirected;

            // 被重定向时默认按系统 ANSI 代码页（简中机器是 GBK）写，脚本按 UTF-8 读就会乱码。
            // 统一改成 UTF-8，JSON 里的中文才能被 PowerShell / ClassIsland 等正确读到。
            var redirectUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (outRedirected || errRedirected)
            {
                try
                {
                    if (outRedirected) Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), redirectUtf8) { AutoFlush = true });
                    if (errRedirected) Console.SetError(new StreamWriter(Console.OpenStandardError(), redirectUtf8) { AutoFlush = true });
                }
                catch { /* 改不了就用默认 */ }
            }

            if (outRedirected && errRedirected) return;

            if (!AttachConsole(-1)) return;

            // 输出走 UTF-8：Git-Bash / Windows Terminal / 管道里都不乱码
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            if (!outRedirected)
            {
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            }
            if (!errRedirected)
            {
                Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
            }

            try { Console.OutputEncoding = utf8; } catch { /* 某些重定向场景不允许改 */ }
        }
        catch
        {
            // 没有控制台就静默
        }
    }
}
