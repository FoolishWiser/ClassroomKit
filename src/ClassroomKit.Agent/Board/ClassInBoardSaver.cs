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

        // 学校一体机可能是 150% 缩放（DPI 144）：窗口矩形是物理像素，配方是 96 DPI 下量的，
        // 所有配方坐标都要按实际尺寸折算。
        var uiScale = Native.GetDpiForWindow(new IntPtr(board.Handle)) / 96.0;
        if (uiScale < 0.5 || uiScale > 3) uiScale = 1;

        // 上一次失败可能留下 ClassInX 的模态小提示框，模态框会让主窗口拒绝被激活 → 先清掉。
        CleanupStrayDialogs(screenW, screenH, uiScale);

        if (!Input.ForceForeground(board.Handle))
        {
            return Fail("无法把 ClassInX 切到前台（可能被别的全屏程序挡住），请先点一下 ClassInX 窗口再试", total, steps);
        }
        Step("聚焦板书窗口");

        // 冷启动的 ClassInX 有时第一下 Ctrl+S 还在初始化里，按两次并给足等待时间。
        var share = CtrlSAndWait(screenW, screenH, 6000, uiScale, out var notice);
        if (share is null && notice is null)
        {
            Thread.Sleep(400);
            share = CtrlSAndWait(screenW, screenH, 4000, uiScale, out notice);
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

        // 每台机器的 ClassInX 版本/缩放可能不一样，先把对话框拍下来，坐标点偏了能一眼看出来。
        var shareShot = DiagnosticPath("share");
        if (Input.CaptureRegion(share.X, share.Y, share.Width, share.Height, shareShot))
        {
            _log.Info($"板书保存诊断：保存对话框 {share.Width}×{share.Height} @ {share.X},{share.Y}"
                + $" DPI={Native.GetDpiForWindow(new IntPtr(share.Handle))}，截图 {shareShot}");
        }

        // 150% 缩放下对话框是 1230×672，配方是 96 DPI 下的 820×448，差 1.5 倍。
        // 相对对话框的坐标必须按实际尺寸折算，否则会点到预览区/登录卡片上（面板当然不出现）。
        var (shareScaleX, shareScaleY) = _recipe.ScaleFor(share.Width, share.Height);
        int Rx(int v) => (int)Math.Round(v * shareScaleX);
        int Ry(int v) => (int)Math.Round(v * shareScaleY);
        _log.Info($"板书保存诊断：坐标比例 {shareScaleX:F2}×{shareScaleY:F2}"
            + $"（对话框 {share.Width}×{share.Height}，配方 {_recipe.SaveLocal.ShareWidth}×{_recipe.SaveLocal.ShareHeight}）");

        // 对话框可能只是弹出来但没拿到焦点，先切到前台，免得第一下点击被吞掉。
        Input.ForceForeground(share.Handle);
        Thread.Sleep(150);

        // 记下点击前的窗口，点完只认“新冒出来的”那个，避免再把登录卡片/弹层当面板。
        var beforeWindows = WindowScanner.Scan()
            .Where(w => w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Handle)
            .ToHashSet();
        var tile = FindSaveLocalTile(share)
                   ?? (share.X + Rx(_recipe.SaveLocal.SaveLocalX), share.Y + Ry(_recipe.SaveLocal.SaveLocalY));
        Input.Click(tile.X, tile.Y);
        var panel = WaitForSavePanel(share, beforeWindows, tile);
        if (panel is null)
        {
            // 把现场留下来：窗口清单 + 对话框截图，下次看日志就能判断是慢、点偏了还是界面变了。
            var screenShot = DiagnosticPath("nopanel");
            Input.CaptureRegion(0, 0, screenW, screenH, screenShot);
            _log.Info("板书保存诊断：10 秒内没等到保存面板；全屏截图 " + screenShot
                + "；ClassInX 可见窗口 " + DescribeWindows());
            Input.SendEscape();
            return Fail("没有弹出「保存至本地」面板（已等 10 秒）：可能是 ClassInX 反应慢，"
                + "或对话框布局变了。稍等再点一次；仍不行请把 logs 里的 board-diag 截图发出来", total, steps);
        }
        Step("弹出保存面板");

        // 不同机器/缩放下面板尺寸不一样（150% 缩放下配方 340×278 会变成 510×417），
        // 面板内坐标按宽高比例折算，不能直接用配方像素。
        var sx = panel.Width / (double)_recipe.SaveLocal.PanelWidth;
        var sy = panel.Height / (double)_recipe.SaveLocal.PanelHeight;
        int Px(int v) => (int)Math.Round(v * sx);
        int Py(int v) => (int)Math.Round(v * sy);

        _log.Info($"板书保存诊断：保存面板 {panel.Width}×{panel.Height} [{panel.ClassName}] @ {panel.X},{panel.Y}，"
            + $"按 {sx:F2}×{sy:F2} 折算坐标");

        if (Math.Abs(sx - 1) > 0.03 || Math.Abs(sy - 1) > 0.03)
        {
            var panelShot = DiagnosticPath("panel");
            Input.CaptureRegion(panel.X, panel.Y, panel.Width, panel.Height, panelShot);
            _log.Warn($"保存面板和配方不一致：{panel.Width}×{panel.Height} [{panel.ClassName}]，"
                + $"按 {sx:F2}×{sy:F2} 折算坐标，截图 {panelShot}");
        }

        // 刚出现时像素还没画好，先等采样稳定（连续两次一致）
        WaitStable(panel, 400, sx, sy);

        // 面板里优先按像素找勾选框行和绿色保存按钮：ClassInX 的面板高宽比和配方对不上
        // （150% 下 510×519，按比例推出来是 510×417），直接折算式点不准。
        var clicked = 0;
        var located = TryLocatePanelControls(panel, ref clicked, out var saveButton);
        if (located)
        {
            _log.Info($"板书保存诊断：视觉定位成功，保存按钮 @ {saveButton.X},{saveButton.Y}");
        }
        else
        {
            // 兜底：老版本界面/颜色变了时，仍按配方折算点击
            var boxes = new (string Name, int X, int Y)[]
            {
                (".edb", Px(_recipe.SaveLocal.EdbX), Py(_recipe.SaveLocal.CheckboxY)),
                (".png", Px(_recipe.SaveLocal.PngX), Py(_recipe.SaveLocal.CheckboxY)),
                (".pdf", Px(_recipe.SaveLocal.PdfX), Py(_recipe.SaveLocal.CheckboxY))
            };

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
                    CaptureFailure(panel, screenW, screenH, "checkbox");
                    ClosePanels(panel, share);
                    return Fail($"勾选框状态不对（{box.Name} 未能勾上），界面可能变了，请用「重新探测」后再试。"
                        + $"诊断截图已存到 {Paths.LogDir}（board-checkbox-*.png），发给我们能直接看出界面差在哪",
                        total, steps);
                }
            }
        }

        if (clicked > 0) Step($"补勾 {clicked} 个格式");
        Step("三种格式已确认");

        Input.Click(located
            ? saveButton.X
            : panel.X + Px(_recipe.SaveLocal.SaveButtonX),
            located
            ? saveButton.Y
            : panel.Y + Py(_recipe.SaveLocal.SaveButtonY));

        // 覆盖确认：每个已存在的文件弹一次「…已经存在，是否替换它？」
        var confirmed = 0;
        var files = new List<string>();
        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
        {
            var prompt = FindOverwriteDialog(shareScaleX, shareScaleY);
            if (prompt is not null)
            {
                Input.Click(prompt.X + (int)Math.Round(_recipe.Overwrite.YesX * shareScaleX),
                    prompt.Y + (int)Math.Round(_recipe.Overwrite.YesY * shareScaleY));
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
    private WindowInfo? CtrlSAndWait(int screenW, int screenH, int waitMs, double uiScale, out WindowInfo? notice)
    {
        Input.SendCtrlS();
        notice = null;
        var deadline = Environment.TickCount64 + waitMs;
        while (Environment.TickCount64 < deadline)
        {
            var share = FindShareDialog(screenW, screenH);
            if (share is not null) return share;
            notice = FindNoticeDialog(uiScale);
            if (notice is not null) return null;
            Thread.Sleep(30);
        }
        return null;
    }

    /// <summary>清掉 ClassInX 遗留的模态小提示框 / 保存对话框，避免主窗口被拒绝激活。</summary>
    private void CleanupStrayDialogs(int screenW, int screenH, double uiScale)
    {
        for (var i = 0; i < 3; i++)
        {
            var notice = FindNoticeDialog(uiScale);
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
    private static WindowInfo? FindNoticeDialog(double uiScale)
    {
        var minW = (int)Math.Round(200 * uiScale);
        var maxW = (int)Math.Round(520 * uiScale);
        var minH = (int)Math.Round(100 * uiScale);
        var maxH = (int)Math.Round(320 * uiScale);
        return WindowScanner.Scan().FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            w.Width >= minW && w.Width <= maxW && w.Height >= minH && w.Height <= maxH);
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
    /// 面板尺寸按对话框的缩放比例推算；优先认“点完之后新出现”的窗口。
    /// </summary>
    private WindowInfo? WaitForSavePanel(WindowInfo share, HashSet<long> before, (int X, int Y) tile)
    {
        var (expectedW, expectedH) = _recipe.ExpectedPanel(share.Width, share.Height);

        var startedAt = Environment.TickCount64;
        var reclicked = false;
        while (Environment.TickCount64 - startedAt < 10000)
        {
            var panel = FindSavePanel(expectedW, expectedH, before, share);
            if (panel is not null) return panel;

            if (!reclicked && Environment.TickCount64 - startedAt >= 2500)
            {
                reclicked = true;
                var (sw, sh) = WindowScanner.ScreenSize();
                var shot = DiagnosticPath("share-after-click");
                Input.CaptureRegion(0, 0, sw, sh, shot);
                _log.Info($"板书保存诊断：点「保存至本地」后 2.5 秒还没面板（预期 {expectedW}×{expectedH}），"
                    + $"先拍一张 {shot} 再补点一次");
                Input.Click(tile.X, tile.Y);
            }
            Thread.Sleep(120);
        }

        // 最后兜底：挑尺寸最接近预期的一个，不再随便抓第一个（那样会抓成登录卡片/弹层）。
        var fallback = WindowScanner.Scan()
            .Where(w => w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(w => w.Width is >= 200 and <= 1200 && w.Height is >= 150 and <= 1000)
            .Where(w => !w.Title.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase))
            .Where(w => !(w.X == share.X && w.Y == share.Y && w.Width == share.Width && w.Height == share.Height))
            .Where(w => OverlapsShare(w, share))
            .OrderBy(w => Math.Abs(w.Width - expectedW) / (double)expectedW + Math.Abs(w.Height - expectedH) / (double)expectedH)
            .FirstOrDefault();
        if (fallback is not null)
        {
            _log.Warn($"保存面板尺寸和配方不一致（实际 {fallback.Width}×{fallback.Height}，预期 {expectedW}×{expectedH}，"
                + $"配方 {_recipe.SaveLocal.PanelWidth}×{_recipe.SaveLocal.PanelHeight}），按比例兜底");
        }
        return fallback;
    }

    private static string DescribeWindows() =>
        string.Join("；", WindowScanner.Scan()
            .Where(w => w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => (long)w.Width * w.Height)
            .Take(8)
            .Select(w => $"{(string.IsNullOrEmpty(w.Title) ? "(无标题)" : w.Title)} {w.Width}×{w.Height}"
                + $" [{w.ClassName}]" + (w.Minimized ? "(最小化)" : "")));

    private static string DiagnosticPath(string tag) =>
        Path.Combine(Paths.LogDir, $"board-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

    /// <summary>失败时把面板和整屏都拍下来（老师只要把 logs 里最新的 board-*.png 发过来）。</summary>
    private void CaptureFailure(WindowInfo panel, int screenW, int screenH, string tag)
    {
        var screenShot = DiagnosticPath(tag + "-screen");
        var panelShot = DiagnosticPath(tag + "-panel");
        Input.CaptureRegion(0, 0, screenW, screenH, screenShot);
        Input.CaptureRegion(panel.X, panel.Y, panel.Width, panel.Height, panelShot);
        _log.Info($"板书保存诊断：截图 {panelShot}、{screenShot}；窗口 " + DescribeWindows());
    }

    /// <summary>在保存对话框截图里找「保存至本地」的黄橙色圆图标，返回屏幕坐标；找不到返回 null 由调用方用配方坐标。</summary>
    private (int X, int Y)? FindSaveLocalTile(WindowInfo share)
    {
        try
        {
            using var bmp = Input.CaptureBitmap(share.X, share.Y, share.Width, share.Height);
            if (bmp is null) return null;

            var blobs = ScreenVision.FindBlobs(bmp, ScreenVision.IsYellow, 300)
                .Where(b => b.Width is >= 14 and <= 90 && b.Height is >= 14 and <= 90)
                .Where(b => b.CenterX > bmp.Width * 0.55 && b.CenterY < bmp.Height * 0.6)
                .OrderByDescending(b => b.Area)
                .ToList();
            if (blobs.Count == 0) return null;

            var tile = blobs[0];
            _log.Info($"板书保存诊断：视觉定位「保存至本地」图标 @ 对话框内 {tile.CenterX},{tile.CenterY}"
                + $"（{tile.Width}×{tile.Height}，{tile.Area} px），配方值 {_recipe.SaveLocal.SaveLocalX},{_recipe.SaveLocal.SaveLocalY}");
            return (share.X + tile.CenterX, share.Y + tile.CenterY);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在面板截图里找勾选框那一行和绿色「保存」按钮。找到就把没勾的格式（一般是 .pdf）点掉，
    /// 保存按钮坐标通过 saveButton 返回；找不到返回 false，调用方退回配方坐标。
    /// </summary>
    private bool TryLocatePanelControls(WindowInfo panel, ref int clicked, out (int X, int Y) saveButton)
    {
        saveButton = default;
        try
        {
            using var bmp = Input.CaptureBitmap(panel.X, panel.Y, panel.Width, panel.Height);
            if (bmp is null) return false;

            var blobs = ScreenVision.FindBlobs(bmp, ScreenVision.IsGreen, 40);
            var buttons = blobs
                .Where(b => b.Width >= 50 && b.Height >= 18 && b.Area >= 600)
                .OrderByDescending(b => b.Area)
                .ToList();
            if (buttons.Count == 0) return false;

            var save = buttons[0];
            saveButton = (panel.X + save.CenterX, panel.Y + save.CenterY);

            // 勾选框：保存按钮上方、同一水平线上的小绿块
            var marks = blobs
                .Where(b => b.Area is >= 40 and <= 1500 && b.Width <= 45 && b.Height <= 45)
                .Where(b => b.CenterY < save.MinY)
                .ToList();
            if (marks.Count < 2) return true;

            var row = marks
                .GroupBy(m => m.CenterY / 10)
                .OrderByDescending(g => g.Count())
                .First()
                .OrderBy(m => m.CenterX)
                .ToList();
            if (row.Count < 2) return true;

            var step = row.Count >= 3
                ? (row[^1].CenterX - row[0].CenterX) / (row.Count - 1)
                : row[1].CenterX - row[0].CenterX;
            if (step <= 0 || step > bmp.Width / 2) return true;

            // 第三个框（.pdf）默认不勾：按前两个的间距推位置，点一下
            var pdfX = row[^1].CenterX + step;
            var pdfY = row[0].CenterY;
            if (pdfX < bmp.Width - 8 && !Input.IsChecked(panel.X + pdfX, panel.Y + pdfY))
            {
                Input.Click(panel.X + pdfX, panel.Y + pdfY);
                clicked++;
                Thread.Sleep(150);
            }

            _log.Info($"板书保存诊断：视觉定位勾选框 y={pdfY}（x={string.Join("/", row.Select(m => m.CenterX))}，间距 {step}），"
                + $"保存按钮 {save.Width}×{save.Height} @ 面板内 {save.CenterX},{save.CenterY}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private WindowInfo? FindSavePanel(int expectedW, int expectedH, HashSet<long>? before, WindowInfo share)
    {
        var windows = WindowScanner.Scan()
            .Where(w => w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(w => IsLikelyPanel(w, expectedW, expectedH) && OverlapsShare(w, share))
            .ToList();

        // 点完之后新出现的窗口优先（登录卡片/启动台这些老窗口不会被误选）。
        var fresh = before is null ? null : windows.Where(w => !before.Contains(w.Handle)).ToList();
        var match = fresh?.FirstOrDefault();
        if (match is not null) return match;

        return windows.OrderBy(w => Math.Abs(w.Width - expectedW) + Math.Abs(w.Height - expectedH)).FirstOrDefault();
    }

    /// <summary>
    /// 面板尺寸容差放宽：ClassInX 的面板高度会随路径/文字行数变（150% 下实测 510×519，按 1.5 推算只有 417），
    /// 所以宽度卡紧、高度只要求落在合理区间。
    /// </summary>
    private static bool IsLikelyPanel(WindowInfo w, int expectedW, int expectedH) =>
        w.Width >= expectedW * 0.8 && w.Width <= expectedW * 1.3 &&
        w.Height >= expectedH * 0.85 && w.Height <= expectedH * 2.0;

    /// <summary>面板是保存对话框里的子窗口，必须和对话框有足够的重叠（避免把别处的登录卡片当面板）。</summary>
    private static bool OverlapsShare(WindowInfo w, WindowInfo share)
    {
        var x1 = Math.Max(w.X, share.X);
        var y1 = Math.Max(w.Y, share.Y);
        var x2 = Math.Min(w.X + w.Width, share.X + share.Width);
        var y2 = Math.Min(w.Y + w.Height, share.Y + share.Height);
        if (x2 <= x1 || y2 <= y1) return false;
        var overlap = (long)(x2 - x1) * (y2 - y1);
        return overlap * 10 >= (long)w.Width * w.Height * 6;
    }

    private static bool NearSize(int actual, int expected) =>
        Math.Abs(actual - expected) <= Math.Max(12, (int)Math.Round(expected * 0.18));

    private WindowInfo? FindOverwriteDialog(double scaleX, double scaleY)
    {
        var expectedW = (int)Math.Round(_recipe.Overwrite.Width * scaleX);
        var expectedH = (int)Math.Round(_recipe.Overwrite.Height * scaleY);
        return WindowScanner.Scan().FirstOrDefault(w =>
            w.Visible && w.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase) &&
            NearSize(w.Width, expectedW) && NearSize(w.Height, expectedH));
    }

    private static void WaitStable(WindowInfo panel, int maxMs, double sx, double sy)
    {
        var deadline = DateTime.Now.AddMilliseconds(maxMs);
        var (x, y) = (panel.X + (int)Math.Round(25 * sx), panel.Y + (int)Math.Round(211 * sy));
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
