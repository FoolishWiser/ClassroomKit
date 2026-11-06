using System.Diagnostics;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Board;

public sealed record BoardSaveStep(string Name, int ElapsedMs);

public sealed record BoardSaveResult(
    bool Ok,
    string Message,
    int ElapsedMs,
    IReadOnlyList<string> Files,
    IReadOnlyList<BoardSaveStep> Steps);

/// <summary>
/// 一键保存 ClassInX 板书：Ctrl+S → 保存至本地 → 三种格式全勾 → 保存（自动确认覆盖）→ 关闭 ClassInX。
/// 全轮询实现（25–40ms 一次），实测整条链路约 1.2 秒；旧固定 sleep 版要 6–7 秒。
/// </summary>
public sealed class ClassInBoardSaver
{
    private const string ProcessName = "ClassIn X";

    private readonly AppLog _log;
    private readonly UiRecipe _recipe;

    public ClassInBoardSaver(AppLog log, UiRecipe recipe)
    {
        _log = log;
        _recipe = recipe;
    }

    public UiRecipe Recipe => _recipe;

    /// <summary>保存目录：我的文档\ClassIn X Files（ClassIn 的默认落盘位置）。</summary>
    public static string SaveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClassIn X Files");

    public BoardSaveResult Save(bool closeAfterSave)
    {
        var total = Stopwatch.StartNew();
        var steps = new List<BoardSaveStep>();
        void Step(string name)
        {
            var at = (int)total.ElapsedMilliseconds;
            steps.Add(new BoardSaveStep(name, at));
            _log.Info($"  · 板书保存步骤「{name}」@ {at} ms");
        }

        var startedAt = DateTime.Now.AddSeconds(-2);
        var (screenW, screenH) = WindowScanner.ScreenSize();

        var board = FindBoardWindow(screenW, screenH);
        if (board is null)
        {
            return Fail("没有找到 ClassInX 板书窗口：请先在 ClassInX 里打开板书再点这个菜单", total, steps);
        }
        Step("找到板书窗口");

        if (!Input.ForceForeground(board.Handle))
        {
            return Fail("无法把 ClassInX 切到前台（可能被别的全屏程序挡住），请先点一下 ClassInX 窗口再试", total, steps);
        }
        Step("聚焦板书窗口");

        Input.SendCtrlS();
        var share = WaitFor(() => FindShareDialog(screenW, screenH), 4000);
        if (share is null)
        {
            return Fail("Ctrl+S 没有弹出「保存和分享板书」窗口（当前可能不在板书界面）", total, steps);
        }
        Step("弹出保存对话框");

        Input.Click(share.X + _recipe.SaveLocal.SaveLocalX, share.Y + _recipe.SaveLocal.SaveLocalY);
        var panel = WaitFor(() => FindSavePanel(), 3000);
        if (panel is null)
        {
            return Fail("没有弹出「保存至本地」面板", total, steps);
        }
        Step("弹出保存面板");

        // 刚出现时像素还没画好，先等采样稳定（连续两次一致）
        WaitStable(panel, 400);

        var boxes = new (string Name, int X, int Y)[]
        {
            (".edb", _recipe.SaveLocal.EdbX, _recipe.SaveLocal.CheckboxY),
            (".png", _recipe.SaveLocal.PngX, _recipe.SaveLocal.CheckboxY),
            (".pdf", _recipe.SaveLocal.PdfX, _recipe.SaveLocal.CheckboxY)
        };

        var clicked = 0;
        foreach (var box in boxes)
        {
            var (x, y) = (panel.X + box.X, panel.Y + box.Y);
            if (Input.IsChecked(x, y)) continue;

            Input.Click(x, y);
            clicked++;

            // 点完等界面重绘：最多 600ms 内读到勾选就算成功
            var ok = false;
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(50);
                if (Input.IsChecked(x, y)) { ok = true; break; }
            }

            if (!ok)
            {
                ClosePanels(panel, share);
                return Fail($"勾选框状态不对（{box.Name} 未能勾上），界面可能变了，请用「重新探测」后再试", total, steps);
            }
        }
        if (clicked > 0) Step($"补勾 {clicked} 个格式");
        Step("三种格式已确认");

        Input.Click(panel.X + _recipe.SaveLocal.SaveButtonX, panel.Y + _recipe.SaveLocal.SaveButtonY);

        // 覆盖确认：每个已存在的文件弹一次「…已经存在，是否替换它？」
        var confirmed = 0;
        var files = new List<string>();
        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
        {
            var prompt = FindOverwriteDialog(screenW, screenH);
            if (prompt is not null)
            {
                Input.Click(prompt.X + _recipe.Overwrite.YesX, prompt.Y + _recipe.Overwrite.YesY);
                confirmed++;
                Thread.Sleep(40);
                continue;
            }

            files = NewFiles(startedAt);
            if (files.Count >= 3) break;
            Thread.Sleep(30);
        }
        if (confirmed > 0) Step($"确认覆盖 {confirmed} 次");

        files = NewFiles(startedAt);
        if (files.Count < 3)
        {
            var found = files.Count == 0 ? "没有发现新文件" : "只发现 " + string.Join("、", files.Select(Path.GetFileName));
            ClosePanels(panel, share);
            return Fail($"保存没有完成：{found}（目标目录 {SaveFolder}）", total, steps);
        }
        Step("文件已落盘");

        ClosePanels(null, share);

        if (closeAfterSave)
        {
            var killed = KillClassIn();
            Step(killed ? "已关闭 ClassInX" : "关闭 ClassInX 失败（请手动关）");
            if (!killed)
            {
                return new BoardSaveResult(true, $"板书已保存（{files.Count} 个文件），但没能关闭 ClassInX", (int)total.ElapsedMilliseconds, files, steps);
            }
        }

        _log.Info($"板书已保存：{string.Join("、", files.Select(Path.GetFileName))}，耗时 {total.ElapsedMilliseconds} ms");
        return new BoardSaveResult(true, $"板书已保存（{files.Count} 个文件，耗时 {total.ElapsedMilliseconds} ms）", (int)total.ElapsedMilliseconds, files, steps);
    }

    private BoardSaveResult Fail(string message, Stopwatch total, List<BoardSaveStep> steps)
    {
        _log.Warn($"保存板书失败：{message}");
        return new BoardSaveResult(false, message, (int)total.ElapsedMilliseconds, Array.Empty<string>(), steps);
    }

    private static WindowInfo? FindBoardWindow(int screenW, int screenH)
    {
        var windows = WindowScanner.Scan();
        return windows
            .Where(w => w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(w => w.CoversScreen(screenW, screenH))
            .OrderByDescending(w => w.ClassName.Contains("Qt", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.Title.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    private WindowInfo? FindShareDialog(int screenW, int screenH)
    {
        var windows = WindowScanner.Scan();
        return windows.FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (w.Title == "Form" || _recipe.MatchesShare(w.Width, w.Height)));
    }

    private WindowInfo? FindSavePanel()
    {
        var windows = WindowScanner.Scan();
        return windows.FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            _recipe.MatchesPanel(w.Width, w.Height));
    }

    private WindowInfo? FindOverwriteDialog(int screenW, int screenH)
    {
        var windows = WindowScanner.Scan();
        return windows.FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            _recipe.MatchesOverwrite(w.Width, w.Height));
    }

    private static void WaitStable(WindowInfo panel, int maxMs)
    {
        var deadline = DateTime.Now.AddMilliseconds(maxMs);
        var (x, y) = (panel.X + 25, panel.Y + 211);
        var previous = Input.Sample(x, y, 11, 11);
        while (DateTime.Now < deadline)
        {
            Thread.Sleep(40);
            var current = Input.Sample(x, y, 11, 11);
            if (Math.Abs(current.Mean - previous.Mean) < 1.5 && current.Green == previous.Green) return;
            previous = current;
        }
    }

    private static T? WaitFor<T>(Func<T?> probe, int timeoutMs) where T : class
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            var value = probe();
            if (value is not null) return value;
            Thread.Sleep(25);
        }
        return null;
    }

    private static List<string> NewFiles(DateTime since)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(SaveFolder)) return result;
            foreach (var file in Directory.EnumerateFiles(SaveFolder))
            {
                try
                {
                    if (File.GetLastWriteTime(file) >= since) result.Add(file);
                }
                catch
                {
                    // 文件正在写，跳过
                }
            }
        }
        catch
        {
            // 目录暂时不可读
        }
        return result;
    }

    private static void ClosePanels(WindowInfo? panel, WindowInfo? share)
    {
        try
        {
            if (panel is not null) Native.PostMessage(new IntPtr(panel.Handle), Native.WmClose, IntPtr.Zero, IntPtr.Zero);
            if (share is not null) Native.PostMessage(new IntPtr(share.Handle), Native.WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 关不掉就算了，下一次 Ctrl+S 会重新弹
        }
    }

    /// <summary>结束整个 ClassInX（含子进程），不问未保存内容。</summary>
    public static bool KillClassIn()
    {
        var any = false;
        foreach (var name in new[] { ProcessName, "ClassInX", "ClassIn" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    any = true;
                }
                catch
                {
                    // 进程可能已退出
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        // 确认真的没了
        for (var i = 0; i < 10; i++)
        {
            if (Process.GetProcessesByName(ProcessName).Length == 0) return any || true;
            Thread.Sleep(120);
        }
        return false;
    }

    public static bool IsClassInRunning() => Process.GetProcessesByName(ProcessName).Length > 0;
}
