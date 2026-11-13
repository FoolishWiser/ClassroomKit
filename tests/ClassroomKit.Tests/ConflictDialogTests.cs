using ClassroomKit.Core.Board;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>冲突提示窗的纯逻辑：倒计时节流、「今天不再提醒」、按键映射。窗口本身只能人眼看。</summary>
internal static class ConflictDialogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(8));

    public static async Task RunAsync()
    {
        await Run("提示窗倒计时与超时默认（都保留）", () =>
        {
            var model = new ConflictDialogModel(60);
            Eq(60, model.TotalSeconds, "总时长");
            Eq(60, model.SecondsLeft, "初始剩余秒");
            Eq(0d, model.Progress, "初始进度");

            False(model.Tick(30_000), "30 秒时还没到点");
            Eq(30, model.SecondsLeft, "剩余秒");
            Eq(0.5d, Math.Round(model.Progress, 2), "进度");

            True(model.Tick(31_000), "到点应返回 true");
            True(model.Expired, "到点应标记 Expired");
            Eq(ConflictChoice.KeepBoth, model.Chosen ?? ConflictChoice.KeepBoth, "超时默认都保留");
        });

        await Run("鼠标悬停暂停倒计时", () =>
        {
            var model = new ConflictDialogModel(10);
            model.Paused = true;
            False(model.Tick(9_000), "暂停时不应推进");
            Eq(10, model.SecondsLeft, "暂停期间剩余秒不变");
            model.Paused = false;
            False(model.Tick(5_000), "恢复后继续");
            Eq(5, model.SecondsLeft, "恢复后剩余 5 秒");
        });

        await Run("只认第一次选择", () =>
        {
            var model = new ConflictDialogModel(60);
            model.Choose(ConflictChoice.KeepSeewo);
            model.Choose(ConflictChoice.KeepNone);
            Eq(ConflictChoice.KeepSeewo, model.Chosen!.Value, "第二次点击应被忽略");
            False(model.Tick(100_000), "已经选过就不再触发超时");
        });

        await Run("快捷键映射", () =>
        {
            Eq<ConflictChoice?>(ConflictChoice.KeepBoth, ConflictDialogModel.MapKey("escape").Choice, "Esc");
            Eq<ConflictChoice?>(ConflictChoice.KeepBoth, ConflictDialogModel.MapKey("enter").Choice, "Enter");
            Eq<ConflictChoice?>(ConflictChoice.KeepSeewo, ConflictDialogModel.MapKey("1").Choice, "数字 1");
            Eq<ConflictChoice?>(ConflictChoice.KeepClassIn, ConflictDialogModel.MapKey("2").Choice, "数字 2");
            Eq<ConflictChoice?>(ConflictChoice.KeepBoth, ConflictDialogModel.MapKey("3").Choice, "数字 3");
            Eq<ConflictChoice?>(ConflictChoice.KeepNone, ConflictDialogModel.MapKey("4").Choice, "数字 4");
            Eq<ConflictChoice?>(ConflictChoice.KeepClassIn, ConflictDialogModel.MapKey("numpad2").Choice, "小键盘 2");
            True(ConflictDialogModel.MapKey("s").ToggleSnooze, "S 切换今天不再提醒");
            False(ConflictDialogModel.MapKey("s").Choice.HasValue, "S 不选择");
            Eq<ConflictChoice?>(null, ConflictDialogModel.MapKey("x").Choice, "未知键忽略");
        });

        await Run("卡片文案与首字母", () =>
        {
            foreach (var choice in Enum.GetValues<ConflictChoice>())
            {
                True(ConflictDialogModel.Label(choice).Length > 0, "标题不为空");
                True(ConflictDialogModel.Hint(choice).Length > 0, "说明不为空");
                True(ConflictDialogModel.InitialOf(choice).Length > 0, "首字母不为空");
                True(ConflictDialogModel.Shortcut(choice) is >= 1 and <= 4, "序号在 1-4");
            }

            Contains(ConflictDialogModel.CountdownText(8), "8", "倒计时文案");
            Eq("都保留", ConflictDialogModel.Label(ConflictChoice.KeepBoth), "默认项标题");
        });

        await Run("今天不再提醒的日期解析", () =>
        {
            Eq<DateOnly?>(new DateOnly(2026, 11, 13), ConflictSnooze.Parse("2026-11-13"), "正常日期");
            Eq<DateOnly?>(null, ConflictSnooze.Parse(""), "空字符串");
            Eq<DateOnly?>(null, ConflictSnooze.Parse(null), "null");
            Eq<DateOnly?>(null, ConflictSnooze.Parse("2026-13-01"), "不存在的月份");
            Eq<DateOnly?>(null, ConflictSnooze.Parse("2026-1-1"), "必须补零");

            Eq("2026-11-13", ConflictSnooze.Today(Now), "今天");
            True(ConflictSnooze.IsActive("2026-11-13", Now), "当天有效");
            True(ConflictSnooze.IsActive("2026-11-14", Now), "未到期");
            False(ConflictSnooze.IsActive("2026-11-12", Now), "过期不再生效");
            False(ConflictSnooze.IsActive("", Now), "空值不生效");
            Contains(ConflictSnooze.Describe("2026-11-13", Now), "2026-11-13", "描述里带日期");
            Eq("未暂停", ConflictSnooze.Describe("", Now), "没暂停时的描述");
        });

        await Run("暂停期间不弹窗，清除后恢复", () =>
        {
            var situation = new BoardSituation { ClassInFloating = true, SeewoFloating = true, SeewoFullscreen = true };
            True(situation.ShouldPrompt, "情境应满足弹窗条件");

            var guard = new ConflictGuard { Enabled = true, SnoozeUntil = new DateOnly(2026, 11, 13) };
            False(guard.ShouldPrompt(situation, Now), "暂停期间不弹");
            Contains(guard.LastReason, "今天不再提醒", "给出暂停原因");

            var cleared = new ConflictGuard { Enabled = true, SnoozeUntil = null };
            True(cleared.ShouldPrompt(situation, Now), "清除后可以弹");
        });

        await Run("暂停只影响当天，第二天照常弹", () =>
        {
            var situation = new BoardSituation { ClassInFloating = true, SeewoFloating = true, ClassInFullscreen = true };
            var guard = new ConflictGuard { Enabled = true, SnoozeUntil = new DateOnly(2026, 11, 13) };
            False(guard.ShouldPrompt(situation, Now), "当天不弹");
            True(guard.ShouldPrompt(situation, Now.AddDays(1)), "第二天恢复");
        });
    }
}
