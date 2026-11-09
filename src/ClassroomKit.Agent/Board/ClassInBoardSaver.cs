using System.Diagnostics;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent.Board;

public sealed record BoardSaveStep(string Name, int ElapsedMs);

public sealed record BoardSaveResult(
    bool Ok,
    string Message,
    int ElapsedMs,
    IReadOnlyList<string> Files,
    IReadOnlyList<BoardSaveStep> Steps)
{
    /// <summary>这次是否顺手把 ClassInX 拉起来了。</summary>
    public bool Launched { get; init; }
}

/// <summary>
/// 一键保存 ClassInX 板书：Ctrl+S → 保存至本地 → 三种格式全勾 → 保存（自动确认覆盖）→ 关闭 ClassInX。
/// 全轮询实现（25–40ms 一次），实测整条链路约 1.2 秒；旧固定 sleep 版要 6–7 秒。
/// </summary>
public sealed class ClassInBoardSaver
{
    private const string ProcessName = "ClassIn X";

    private readonly AppLog _log;
    private readonly UiRecipe _recipe;
    private readonly Func<BoardSaveConfig>? _config;

    public ClassInBoardSaver(AppLog log, UiRecipe recipe, Func<BoardSaveConfig>? config = null)
    {
        _log = log;
        _recipe = recipe;
        _config = config;
    }

    public UiRecipe Recipe => _recipe;

    /// <summary>保存目录：我的文档\ClassIn X Files（ClassIn 的默认落盘位置）。</summary>
    public static string SaveFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClassIn X Files");

    public BoardSaveResult Save(bool closeAfterSave) => Save(closeAfterSave, null);

    /// <param name="launchIfNeeded">null = 用配置里的 boardSave.launchIfNeeded。</param>
    public BoardSaveResult Save(bool closeAfterSave, bool? launchIfNeeded)
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
        var launched = false;
        if (board is null)
        {
            // ClassInX 只是被最小化（没关闭）时，GetWindowRect 返回 (-32000,-32000)，
            // CoversScreen 判定永远不成立 —— 这里先把它还原，再重新找板书窗口。
            var restored = RestoreMinimizedClassIn();
            if (restored > 0)
            {
                Step($"还原最小化的 ClassInX（{restored} 个窗口）");
                board = WaitFor(() => FindBoardWindow(screenW, screenH), 4000);
                if (board is not null) Step("板书窗口已还原");
            }
        }

        if (board is null)
        {
            var cfg = _config?.Invoke() ?? new BoardSaveConfig();
            if (!(launchIfNeeded ?? cfg.LaunchIfNeeded))
            {
                return Fail("没有找到 ClassInX 板书窗口：请先在 ClassInX 里打开板书再点这个菜单", total, steps);
            }

            var timeoutSec = cfg.LaunchTimeoutSec <= 0 ? 60 : cfg.LaunchTimeoutSec;

            if (!ClassInLauncher.IsRunning())
            {
                var (ok, message, path) = ClassInLauncher.Start(cfg.ClassInPath, _log);
                if (!ok) return Fail(message, total, steps);
                launched = true;
                Step(path is null ? "启动 ClassInX" : "启动 ClassInX：" + Path.GetFileName(path));
                board = WaitFor(() => FindBoardWindow(screenW, screenH), timeoutSec * 1000);
            }
            else
            {
                Step("ClassInX 已在运行");

                // 有可见窗口却没有板书：多半停在启动台（或者板书窗藏在后面/最小化）——
                // 先给 8 秒缓冲，仍找不到就立刻给出可操作的提示，不再让老师干等一分钟。
                board = WaitFor(() => FindBoardWindow(screenW, screenH), Math.Min(8, timeoutSec) * 1000);
                if (board is null && HasVisibleClassInWindows())
                {
                    BringClassInToFront();
                    return Fail("ClassInX 已经在运行，但没找到板书窗口（可能停在「启动台 / 主界面」，"
                        + "或板书窗口还最小化着）：请先在 ClassInX 里进入课堂、打开板书（窗口标题是 Classroom_…）"
                        + "再点这个菜单。如果板书已经打开，点一下托盘菜单会自动把它还原到前台。",
                        total, steps, launched);
                }

                if (board is null) board = WaitFor(() => FindBoardWindow(screenW, screenH), timeoutSec * 1000);
            }

            if (board is null)
            {
                var head = launched
                    ? $"已启动 ClassInX，但 {timeoutSec} 秒内没等到板书窗口"
                    : $"ClassInX 正在运行，但 {timeoutSec} 秒内没等到板书窗口";
                BringClassInToFront();
                return Fail(head + "：请先在 ClassInX 里进入课堂并打开板书（窗口标题是 Classroom_…），"
                    + "再点这个菜单；学校网络慢时可把 config.json 的 boardSave.launchTimeoutSec 调大",
                    total, steps, launched);
            }
            Step("板书窗口已就绪");
        }
        Step("找到板书窗口");

        // 上一次失败可能留下 ClassInX 的模态小提示框，模态框会让主窗口拒绝被激活 → 先清掉。
        CleanupStrayDialogs(screenW, screenH);

        if (!Input.ForceForeground(board.Handle))
        {
            return Fail("无法把 ClassInX 切到前台（可能被别的全屏程序挡住），请先点一下 ClassInX 窗口再试", total, steps);
        }
        Step("聚焦板书窗口");

        // 冷启动的 ClassInX 有时第一下 Ctrl+S 还在初始化里，按两次并给足等待时间。
        var share = CtrlSAndWait(screenW, screenH, 6000, out var notice);
        if (share is null && notice is null)
        {
            Thread.Sleep(400);
            share = CtrlSAndWait(screenW, screenH, 4000, out notice);
        }

        if (share is null && notice is not null)
        {
            // 空板书（或别的拒存原因）时 ClassInX 只弹一个小「提示」框，Ctrl+S 就不会再有后续对话框。
            var green = Input.FindGreenButton(notice.X, notice.Y, notice.Width, notice.Height);
            if (green is not null) Input.Click(green.Value.X, green.Value.Y);
            Step("关闭 ClassInX 提示框");
            return Fail("ClassInX 弹了提示「" + notice.Title + "」（常见原因是板书内容为空）："
                + "请先在板书上写点内容，再点这个菜单", total, steps, launched);
        }

        if (share is null)
        {
            // 常见情形：ClassInX 刚被拉起来，停在「启动台 / 主界面」，此时 Ctrl+S 什么也不会发生。
            var inClassroom = board.Title.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase);
            var hint = inClassroom
                ? "Ctrl+S 没有弹出「保存和分享板书」窗口：请点一下板书区域让 ClassInX 拿到键盘焦点后再试"
                : "ClassInX 已经打开并切到前台，但当前停在「启动台 / 主界面」，没有课堂板书可保存："
                  + "请先在 ClassInX 里进入课堂、打开板书（窗口标题是 Classroom_…）再点这个菜单";
            return Fail(hint, total, steps, launched);
        }
        Step("弹出保存对话框");

        // 对话框可能只是弹出来但没拿到焦点，先切到前台，免得第一下点击被吞掉。
        Input.ForceForeground(share.Handle);
        Thread.Sleep(150);
        Input.Click(share.X + _recipe.SaveLocal.SaveLocalX, share.Y + _recipe.SaveLocal.SaveLocalY);
        var panel = WaitForSavePanel(share);
        if (panel is null)
        {
            // 把现场留下来：窗口清单 + 对话框截图，下次看日志就能判断是慢、点偏了还是界面变了。
            var diag = DiagnosticName();
            _log.Info("板书保存诊断：ClassInX 可见窗口 " + DescribeWindows());
            if (Input.CaptureRegion(share.X, share.Y, share.Width, share.Height, diag))
                _log.Info("板书保存诊断：保存对话框截图 " + diag);
            Input.SendEscape();
            return Fail("没有弹出「保存至本地」面板（已等 10 秒）：可能是 ClassInX 反应慢，"
                + "或对话框布局变了。稍等再点一次；仍不行请把 logs 里的 board-diag 截图发出来", total, steps);
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
                return new BoardSaveResult(true, $"板书已保存（{files.Count} 个文件），但没能关闭 ClassInX", (int)total.ElapsedMilliseconds, files, steps)
                {
                    Launched = launched
                };
            }
        }

        _log.Info($"板书已保存：{string.Join("、", files.Select(Path.GetFileName))}，耗时 {total.ElapsedMilliseconds} ms");
        return new BoardSaveResult(true, $"板书已保存（{files.Count} 个文件，耗时 {total.ElapsedMilliseconds} ms）", (int)total.ElapsedMilliseconds, files, steps)
        {
            Launched = launched
        };
    }

    private BoardSaveResult Fail(string message, Stopwatch total, List<BoardSaveStep> steps, bool launched = false)
    {
        _log.Warn($"保存板书失败：{message}");
        return new BoardSaveResult(false, message, (int)total.ElapsedMilliseconds, Array.Empty<string>(), steps)
        {
            Launched = launched
        };
    }

    /// <summary>把最小化的 ClassInX 窗口还原（SwRestore），返回还原了几个。</summary>
    private static int RestoreMinimizedClassIn()
    {
        var restored = 0;
        foreach (var window in WindowScanner.Scan().Where(w =>
                     w.Visible && w.Minimized && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            if (Native.ShowWindow(new IntPtr(window.Handle), Native.SwRestore)) restored++;
        }
        return restored;
    }

    /// <summary>ClassInX 是否有可见的顶层窗口（用来区分“还在启动/全隐藏”和“停在启动台”）。</summary>
    private static bool HasVisibleClassInWindows() => WindowScanner.Scan().Any(w =>
        w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase));

    /// <summary>没等到板书时，把 ClassInX 的窗口（通常停在启动台）切到前台，省得老师找窗口。</summary>
    private static void BringClassInToFront()
    {
        // 最小化窗口的尺寸不可信，先还原再挑最大的那个。
        foreach (var minimized in WindowScanner.Scan().Where(w =>
                     w.Visible && w.Minimized && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase)))
        {
            Native.ShowWindow(new IntPtr(minimized.Handle), Native.SwRestore);
            Thread.Sleep(120);
        }

        var biggest = WindowScanner.Scan()
            .Where(w => w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Width * w.Height)
            .FirstOrDefault();
        if (biggest is not null) Input.ForceForeground(biggest.Handle);
    }

    /// <summary>
    /// 板书窗口的标题形如 Classroom_0_0；ClassInX 的启动台 / 全屏壳窗口标题只有「ClassIn X」。
    /// 只按「铺满屏幕」挑会把最小化时冒出来的全屏壳当成板书窗口（然后 Ctrl+S 毫无反应），
    /// 所以标题必须以 Classroom 开头，再用是否铺满屏幕、面积大小排序。
    /// </summary>
    private static WindowInfo? FindBoardWindow(int screenW, int screenH)
    {
        var windows = WindowScanner.Scan();
        return windows
            .Where(w => w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(w => IsBoardTitle(w.Title))
            .OrderByDescending(w => w.CoversScreen(screenW, screenH))
            .ThenByDescending(w => (long)w.Width * w.Height)
            .FirstOrDefault();
    }

    private static bool IsBoardTitle(string title) =>
        title.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase);

    /// <summary>按一次 Ctrl+S，然后等「保存和分享板书」对话框或小提示框出现。</summary>
    private WindowInfo? CtrlSAndWait(int screenW, int screenH, int waitMs, out WindowInfo? notice)
    {
        Input.SendCtrlS();
        notice = null;
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            var share = FindShareDialog(screenW, screenH);
            if (share is not null) return share;
            notice = FindNoticeDialog();
            if (notice is not null) return null;
            Thread.Sleep(30);
        }
        return null;
    }

    /// <summary>清掉 ClassInX 遗留的模态小提示框 / 保存对话框，避免主窗口被拒绝激活。</summary>
    private void CleanupStrayDialogs(int screenW, int screenH)
    {
        for (var i = 0; i < 3; i++)
        {
            var notice = FindNoticeDialog();
            if (notice is not null)
            {
                var green = Input.FindGreenButton(notice.X, notice.Y, notice.Width, notice.Height);
                if (green is not null) Input.Click(green.Value.X, green.Value.Y);
                else Input.SendEscape();
                _log.Info("清掉了 ClassInX 遗留的提示框");
                Thread.Sleep(150);
                continue;
            }

            var share = FindShareDialog(screenW, screenH);
            if (share is not null)
            {
                Input.SendEscape();
                _log.Info("清掉了 ClassInX 遗留的保存对话框");
                Thread.Sleep(200);
                continue;
            }

            break;
        }
    }

    /// <summary>ClassInX 的小提示框（例如「黑板内容为空」）：宽 200–520、高 100–320 的可见小窗。</summary>
    private static WindowInfo? FindNoticeDialog()
    {
        return WindowScanner.Scan().FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            w.Width is >= 200 and <= 520 && w.Height is >= 100 and <= 320);
    }

    private WindowInfo? FindShareDialog(int screenW, int screenH)
    {
        var windows = WindowScanner.Scan();
        return windows.FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            (w.Title == "Form" || _recipe.MatchesShare(w.Width, w.Height)));
    }

    /// <summary>
    /// 点完「保存至本地」后等面板出现。学校一体机比测试机慢，这里最多等 10 秒；
    /// 2.5 秒还没出现就补点一次（第一次点击可能只是把对话框激活）。
    /// 面板还没出现时，用尺寸比例（DPI 缩放）再宽容地兜一次。
    /// </summary>
    private WindowInfo? WaitForSavePanel(WindowInfo share)
    {
        var startedAt = Environment.TickCount64;
        var reclicked = false;
        while (Environment.TickCount64 - startedAt < 10000)
        {
            var panel = FindSavePanel();
            if (panel is not null) return panel;

            if (!reclicked && Environment.TickCount64 - startedAt >= 2500)
            {
                reclicked = true;
                Input.Click(share.X + _recipe.SaveLocal.SaveLocalX, share.Y + _recipe.SaveLocal.SaveLocalY);
            }
            Thread.Sleep(120);
        }

        // 最后兜底：按比例放宽尺寸匹配（例如 125% / 150% 缩放下 340×278 会变成 425×348 / 510×417）。
        var fallback = WindowScanner.Scan().FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            w.Width is >= 240 and <= 700 && w.Height is >= 180 and <= 600 &&
            !w.Title.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase) &&
            !(w.X == share.X && w.Y == share.Y && w.Width == share.Width && w.Height == share.Height));
        if (fallback is not null)
        {
            _log.Warn($"保存面板尺寸和配方不一致（实际 {fallback.Width}×{fallback.Height}，配方 "
                + $"{_recipe.SaveLocal.PanelWidth}×{_recipe.SaveLocal.PanelHeight}），按比例兜底");
        }
        return fallback;
    }

    private static string DescribeWindows() =>
        string.Join("；", WindowScanner.Scan()
            .Where(w => w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Width * w.Height)
            .Take(8)
            .Select(w => $"{(string.IsNullOrEmpty(w.Title) ? "(无标题)" : w.Title)} {w.Width}×{w.Height}"
                + (w.Minimized ? "(最小化)" : "")));

    private static string DiagnosticName() =>
        Path.Combine(Paths.LogDir, $"board-diag-{DateTime.Now:yyyyMMdd-HHmmss}.png");

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
