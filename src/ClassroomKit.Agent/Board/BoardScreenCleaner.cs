using ClassroomKit.Core.Board;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 保存后清屏：确认 ClassInX 在前台，Ctrl+A 全选，再按配置的 Delete 或 Backspace。
/// 用清屏前后的 32x32 亮度指纹判断画面是否真的变了；失败只记日志，不影响板书已保存。
/// </summary>
internal static class BoardScreenCleaner
{
    private const double ChangedThreshold = 0.02;

    public static (bool Ok, string Message) TryClear(WindowInfo board, string? clearKey, AppLog log)
    {
        try
        {
            if (!Input.IsForeground(board.Handle) && !Input.ForceForeground(board.Handle))
                return (false, "清屏失败：无法把 ClassInX 切到前台");
            if (!Input.IsForeground(board.Handle))
                return (false, "清屏失败：ClassInX 不在前台，未发送按键");

            var width = Math.Max(1, board.Width);
            var height = Math.Max(1, board.Height);

            // 点一下画布中心，确保键盘焦点在板书上，而不是标题栏或工具栏。
            Input.Click(board.X + width / 2, board.Y + height / 2);
            Thread.Sleep(80);

            var before = Input.Fingerprint(board.X, board.Y, width, height);
            if (before is null) return (false, "清屏失败：无法截取板书窗口");

            var key = NormalizeKey(clearKey);
            var first = SendAndMeasure(board, before, key);
            if (first >= ChangedThreshold)
                return (true, $"已按 Ctrl+A + {KeyText(key)} 清屏");

            var alternate = key == "backspace" ? "delete" : "backspace";
            var second = SendAndMeasure(board, before, alternate);
            if (second >= ChangedThreshold)
                return (true, $"已按 Ctrl+A + {KeyText(alternate)} 清屏");

            log.Info($"保存后清屏：已发送 Ctrl+A + {KeyText(key)}，但画面没有变化");
            return (false, "已发送清屏按键，但画面没有变化（可能本来就没有板书）");
        }
        catch (Exception ex)
        {
            log.Warn("保存后清屏失败：" + ex.Message);
            return (false, "清屏失败：" + ex.Message);
        }
    }

    private static double SendAndMeasure(WindowInfo board, double[] before, string key)
    {
        Input.SendCtrlA();
        Thread.Sleep(120);
        if (key == "backspace") Input.SendBackspace();
        else Input.SendDelete();
        Thread.Sleep(350);

        var after = Input.Fingerprint(board.X, board.Y, Math.Max(1, board.Width), Math.Max(1, board.Height));
        return after is null ? 0 : Input.Difference(before, after);
    }

    private static string NormalizeKey(string? value) =>
        string.Equals(value, "backspace", StringComparison.OrdinalIgnoreCase) ? "backspace" : "delete";

    private static string KeyText(string key) => key == "backspace" ? "Backspace" : "Delete";
}
