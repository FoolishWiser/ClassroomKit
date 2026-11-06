using ClassroomKit.Core.Board;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>板书软件冲突判定、冷却、以及 UI 配方——全部纯逻辑，可在 Linux 上测。</summary>
internal static class BoardTests
{
    private const int ScreenW = 1920;
    private const int ScreenH = 1080;

    private static WindowInfo Win(string proc, string cls, string title, int w, int h, int x = 0, int y = 0, bool topmost = false, string path = "") =>
        new()
        {
            Handle = 1,
            ProcessId = 100,
            ProcessName = proc,
            ProcessPath = path,
            ClassName = cls,
            Title = title,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            ExStyle = topmost ? WindowInfo.WsExTopmost : 0u,
            Visible = true
        };

    private static WindowInfo ClassInFloating() => Win("ClassIn X", "Qt51519QWindowIcon", "", 496, 496, 1655, 325, topmost: true);
    private static WindowInfo SeewoFloating() => Win("EasiNote", "HwndWrapper[EasiNote;x]", "", 550, 200, -175, 880, topmost: true);
    private static WindowInfo ClassInBoard() => Win("ClassIn X", "Qt51519QWindowIcon", "Classroom_0_0", ScreenW, ScreenH);
    private static WindowInfo SeewoBoard() => Win("EasiNote", "HwndWrapper[EasiNote;x]", "希沃白板", ScreenW, ScreenH);
    private static WindowInfo WpsShow() => Win("wpp", "Qt5QWindowIcon", "WPS Presentation Slide Show - [演示文稿2]", ScreenW, ScreenH);
    private static WindowInfo WpsEditor() => Win("wps", "PP12FrameClass", "演示文稿2 - WPS Office", 1400, 1000, 253, 20);

    public static async Task RunAsync()
    {
        {
            await Run("悬浮窗：ClassIn 496x496 置顶小块被认成 ClassInFloating", () =>
            {
                Eq(BoardKind.ClassInFloating, WindowClassifier.Classify(ClassInFloating(), ScreenW, ScreenH));
            });

            await Run("悬浮窗：希沃 550x200 贴边置顶窗被认成 SeewoFloating", () =>
            {
                Eq(BoardKind.SeewoFloating, WindowClassifier.Classify(SeewoFloating(), ScreenW, ScreenH));
            });

            await Run("全屏：ClassIn 板书窗 1920x1080 被认成 ClassInBoard", () =>
            {
                Eq(BoardKind.ClassInBoard, WindowClassifier.Classify(ClassInBoard(), ScreenW, ScreenH));
            });

            await Run("WPS 放映：标题前缀 + 全屏 → WpsShow（不靠置顶，实测 ex=0）", () =>
            {
                Eq(BoardKind.WpsShow, WindowClassifier.Classify(WpsShow(), ScreenW, ScreenH));
            });

            await Run("WPS 放映：标题对但窗口不是全屏 → 不算放映", () =>
            {
                var small = Win("wpp", "Qt5QWindowIcon", "WPS Presentation Slide Show - [x]", 800, 600, 100, 100);
                False(WindowClassifier.Classify(small, ScreenW, ScreenH) == BoardKind.WpsShow, "800x600 的放映窗不应算全屏放映");
            });

            await Run("WPS 编辑窗：PP12FrameClass 被认成 WpsEditor", () =>
            {
                Eq(BoardKind.WpsEditor, WindowClassifier.Classify(WpsEditor(), ScreenW, ScreenH));
            });

            await Run("WPS 编辑窗：老版本 PPFrameClass / PP10FrameClass 同样识别", () =>
            {
                Eq(BoardKind.WpsEditor, WindowClassifier.Classify(Win("wpp", "PPFrameClass", "x - WPS Office", 1200, 800), ScreenW, ScreenH));
                Eq(BoardKind.WpsEditor, WindowClassifier.Classify(Win("wpp", "PP10FrameClass", "x - WPS Office", 1200, 800), ScreenW, ScreenH));
            });

            await Run("WPS 安装路径在 %LOCALAPPDATA% 也能认出是 WPS", () =>
            {
                var w = Win("wpp", "Qt5QWindowIcon", "WPS Presentation Slide Show - [a]", ScreenW, ScreenH,
                    path: @"C:\Users\33570\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wpp.exe");
                True(WindowClassifier.IsWpsInstalled(w), "Kingsoft\\WPS Office 路径应判定为已安装");
                Eq(BoardKind.WpsShow, WindowClassifier.Classify(w, ScreenW, ScreenH));
            });

            await Run("不相干的进程（Chrome）不会被误判", () =>
            {
                Eq(BoardKind.Unknown, WindowClassifier.Classify(Win("chrome", "Chrome_WidgetWin_1", "网页", ScreenW, ScreenH), ScreenW, ScreenH));
            });

            await Run("情境：只有两个悬浮窗、没有全屏 → 不弹窗", () =>
            {
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating() }, ScreenW, ScreenH);
                True(s.BothFloating, "两个悬浮窗应同时被识别");
                False(s.AnyFullscreen, "没有全屏窗口");
                False(s.ShouldPrompt, "不满足全屏条件时不应弹窗");
            });

            await Run("情境：两个悬浮窗 + ClassIn 全屏 → 弹窗", () =>
            {
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), ClassInBoard() }, ScreenW, ScreenH);
                True(s.ShouldPrompt, "悬浮窗共存 + 任一全屏应触发");
                Contains(s.Describe(), "ClassIn全屏");
            });

            await Run("情境：两个悬浮窗 + 希沃全屏 → 弹窗", () =>
            {
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), SeewoBoard() }, ScreenW, ScreenH);
                True(s.ShouldPrompt, "希沃全屏同样应触发");
            });

            await Run("情境：两个悬浮窗 + WPS 放映 → 弹窗", () =>
            {
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), WpsShow() }, ScreenW, ScreenH);
                True(s.ShouldPrompt, "WPS 放映应算作全屏场景");
                True(s.WpsShow, "WpsShow 标志应为真");
            });

            await Run("情境：只有一个悬浮窗 + 全屏 → 不弹窗（缺一半条件）", () =>
            {
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), WpsShow() }, ScreenW, ScreenH);
                False(s.ShouldPrompt, "只有一个悬浮窗时不应弹窗");
            });

            await Run("情境：额外全屏进程（学校自填）也参与判定", () =>
            {
                var custom = Win("SomePlayer", "PlayerWnd", "视频", ScreenW, ScreenH);
                var s = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), custom }, ScreenW, ScreenH,
                    new[] { "SomePlayer" });
                True(s.ShouldPrompt, "配置里的额外全屏进程应触发");
                True(s.OtherFullscreen, "应标记为其它全屏");
            });

            await Run("冷却：同一情境只弹一次，40 分钟内不重复", () =>
            {
                var guard = new ConflictGuard { Enabled = true, Cooldown = TimeSpan.FromMinutes(40) };
                var situation = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), WpsShow() }, ScreenW, ScreenH);
                var t0 = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.FromHours(8));

                True(guard.ShouldPrompt(situation, t0), "第一次应弹");
                guard.MarkPrompted(t0);
                False(guard.ShouldPrompt(situation, t0.AddMinutes(1)), "刚弹过不应再弹");
                False(guard.ShouldPrompt(situation, t0.AddMinutes(39)), "冷却期内不应再弹");
                True(guard.ShouldPrompt(situation, t0.AddMinutes(41)), "超过 40 分钟应允许再弹");
                Eq(1, guard.PromptCount);
            });

            await Run("冷却：直接手动强制触发可以绕过冷却", () =>
            {
                var guard = new ConflictGuard { Enabled = true, Cooldown = TimeSpan.FromMinutes(40) };
                var situation = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), WpsShow() }, ScreenW, ScreenH);
                var t0 = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.FromHours(8));
                guard.MarkPrompted(t0);
                guard.MarkForced(t0.AddMinutes(2));
                Eq(2, guard.PromptCount);
            });

            await Run("冷却：关掉开关就永不弹；情境不成立也不弹", () =>
            {
                var guard = new ConflictGuard { Enabled = false };
                var situation = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), WpsShow() }, ScreenW, ScreenH);
                False(guard.ShouldPrompt(situation, DateTimeOffset.Now), "禁用时不应弹");

                var enabled = new ConflictGuard { Enabled = true };
                False(enabled.ShouldPrompt(new BoardSituation(), DateTimeOffset.Now), "情境不成立时不应弹");
            });

            await Run("冷却：情境消失再回来，冷却期内仍不弹、冷却结束后可弹", () =>
            {
                var guard = new ConflictGuard { Enabled = true, Cooldown = TimeSpan.FromMinutes(40) };
                var situation = WindowClassifier.Situate(new[] { ClassInFloating(), SeewoFloating(), WpsShow() }, ScreenW, ScreenH);
                var t0 = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.FromHours(8));
                guard.MarkPrompted(t0);
                False(guard.ShouldPrompt(situation, t0.AddMinutes(1)), "冷却期内");

                // 情境消失（先不满足条件），再回来：40 分钟冷却仍然是硬约束
                False(guard.ShouldPrompt(new BoardSituation(), t0.AddMinutes(2)), "情境消失");
                False(guard.ShouldPrompt(situation, t0.AddMinutes(3)), "冷却期内重新进入也不应再弹");
                True(guard.ShouldPrompt(situation, t0.AddMinutes(41)), "冷却结束后应允许再弹");
            });

            await Run("配方：默认尺寸能匹配 ClassIn 的分享框与保存面板", () =>
            {
                var recipe = UiRecipe.CreateDefault();
                True(recipe.MatchesShare(820, 448), "820x448 应匹配「保存和分享板书」");
                True(recipe.MatchesPanel(340, 278), "340x278 应匹配「保存至本地」面板");
                True(recipe.MatchesOverwrite(360, 212), "360x212 应匹配覆盖确认框");
                False(recipe.MatchesPanel(600, 400), "尺寸差太多不应匹配");
            });

            await Run("配方：DPI 缩放 ±6% 以内仍能匹配", () =>
            {
                var recipe = UiRecipe.CreateDefault();
                True(recipe.MatchesPanel(340 + 15, 278 + 10), "轻微缩放应仍匹配");
                True(recipe.MatchesShare(820 - 20, 448), "分享框轻微缩放应仍匹配");
            });

            await Run("配方：JSON 往返后自定义坐标不丢", () =>
            {
                var recipe = UiRecipe.CreateDefault();
                recipe.SaveLocal.PdfX = 170;
                recipe.SaveLocal.SaveButtonY = 260;
                recipe.Overwrite.YesX = 200;
                var json = System.Text.Json.JsonSerializer.Serialize(recipe);
                var back = System.Text.Json.JsonSerializer.Deserialize<UiRecipe>(json)!;
                Eq(170, back.SaveLocal.PdfX);
                Eq(260, back.SaveLocal.SaveButtonY);
                Eq(200, back.Overwrite.YesX);
            });
        }
    }
}
