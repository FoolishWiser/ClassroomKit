using System.Windows.Automation;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Board;

/// <summary>
/// 保存后清屏：先按 UIAutomation 找 ClassInX 的「橡皮 / 清屏」按钮，找不到再用老师校准过的
/// 相对坐标点一次。两种方式都没有时返回失败，只记日志，不影响板书已经保存成功。
/// </summary>
internal static class BoardScreenCleaner
{
    private static readonly string[] Names =
    {
        "清屏", "一键清屏", "橡皮", "擦除", "清空", "清除", "Clear", "Erase", "Eraser"
    };

    public static (bool Ok, string Message) TryClear(WindowInfo board, UiRecipe recipe, AppLog log)
    {
        try
        {
            var byName = TryClearByAutomation(board, log);
            if (byName is not null) return (true, byName);

            if (recipe.Clear.IsCalibrated)
            {
                var x = board.X + (int)Math.Round(board.Width * recipe.Clear.XPercent / 100.0);
                var y = board.Y + (int)Math.Round(board.Height * recipe.Clear.YPercent / 100.0);
                Input.Click(x, y);
                Thread.Sleep(120);
                log.Info($"保存后清屏：按校准坐标点击 ({x},{y})");
                return (true, "已按校准坐标点击清屏");
            }

            return (false, "没有找到清屏按钮，也没有校准坐标（可在「板书」页校准一次）");
        }
        catch (Exception ex)
        {
            log.Warn("保存后清屏失败：" + ex.Message);
            return (false, "清屏失败：" + ex.Message);
        }
    }

    private static string? TryClearByAutomation(WindowInfo board, AppLog log)
    {
        try
        {
            var root = AutomationElement.FromHandle(new IntPtr(board.Handle));
            if (root is null) return null;
            var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in all)
            {
                string name;
                try { name = element.Current.Name ?? ""; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!Names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) continue;

                try
                {
                    var rect = element.Current.BoundingRectangle;
                    if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) continue;
                    if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                        && pattern is InvokePattern invoke)
                    {
                        invoke.Invoke();
                    }
                    else
                    {
                        Input.Click((int)Math.Round(rect.X + rect.Width / 2), (int)Math.Round(rect.Y + rect.Height / 2));
                    }

                    Thread.Sleep(120);
                    log.Info($"保存后清屏：已点击「{name}」");
                    return $"已点击「{name}」清屏";
                }
                catch
                {
                    // 单个元素失效就继续找下一个
                }
            }
        }
        catch (Exception ex)
        {
            log.Info("清屏按钮自动识别不可用：" + ex.Message);
        }

        return null;
    }
}
