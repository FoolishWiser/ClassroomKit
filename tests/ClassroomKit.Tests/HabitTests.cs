using ClassroomKit.Core.Board;
using ClassroomKit.Core.Model;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>「高级」页按科目记住老师习惯：判定只看纯逻辑，不碰 Windows。</summary>
internal static class HabitTests
{
    private static HabitsConfig Enabled(params HabitRule[] rules)
    {
        var cfg = new HabitsConfig { Enabled = true };
        cfg.Rules.AddRange(rules);
        return cfg;
    }

    private static HabitRule Rule(string subject, string app, bool understand) =>
        new() { Subject = subject, App = app, Understand = understand };

    /// <summary>两个悬浮窗都在 + 希沃全屏：原来的冲突判定会弹窗。</summary>
    private static BoardSituation ConflictWithSeewo() => new()
    {
        ClassInFloating = true,
        SeewoFloating = true,
        SeewoFullscreen = true
    };

    private static BoardSituation SeewoOnly() => new()
    {
        SeewoFloating = true,
        SeewoFullscreen = true
    };

    public static Task RunAsync() => RunAll();

    private static async Task RunAll()
    {
        await Run("习惯软件写法：classin/希沃/wps 的中英文都能认", () =>
        {
            Eq(HabitApp.ClassIn, HabitApps.Parse("ClassIn X"), "ClassIn X");
            Eq(HabitApp.ClassIn, HabitApps.Parse(" classinx "), "classinx 带空格");
            Eq(HabitApp.Seewo, HabitApps.Parse("希沃白板"), "中文希沃");
            Eq(HabitApp.Seewo, HabitApps.Parse("EasiNote"), "进程名 easinote");
            Eq(HabitApp.Wps, HabitApps.Parse("WPS Office"), "WPS Office");
            Eq(HabitApp.None, HabitApps.Parse("别的软件"), "认不出的软件");
            Eq("classin", HabitApps.ToValue(HabitApp.ClassIn), "写回配置用小写英文");
        });

        await Run("配置归一：同一科目后者覆盖、空软件丢弃、首尾空白去掉", () =>
        {
            var cfg = new HabitsConfig();
            cfg.Rules.Add(Rule(" 语文 ", "", false));
            cfg.Rules.Add(Rule("数学", "wps", true));
            cfg.Rules.Add(Rule("数学", "希沃", false));
            cfg.Rules.Add(Rule("", "wps", true));
            cfg.Normalize();

            Eq(1, cfg.Rules.Count, "只剩数学一条");
            Eq("数学", cfg.Rules[0].Subject, "科目名");
            Eq("seewo", cfg.Rules[0].App, "后写的覆盖先写的");
            False(cfg.Rules[0].Understand, "理解程度跟着覆盖");
        });

        await Run("没开启习惯模式时照常弹窗", () =>
        {
            var cfg = new HabitsConfig();
            cfg.Rules.Add(Rule("语文", "wps", false));
            var d = SubjectHabitPolicy.Decide(cfg, "语文", ConflictWithSeewo(), null, false);
            Eq(HabitAction.Prompt, d.Action, "动作");
        });

        await Run("开了习惯模式但当前科目没设置：不做任何处理（不弹窗、不关软件）", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("数学", "wps", false)), "语文",
                ConflictWithSeewo(), null, false);
            Eq(HabitAction.SuppressPrompt, d.Action, "动作");
            Eq(0, d.Close.Count, "不关任何软件");
            Contains(d.Reason, "没有设置", "原因");
        });

        await Run("「理解」的科目：冲突照常交给老师", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("数学", "classin", true)), "数学",
                ConflictWithSeewo(), null, false);
            Eq(HabitAction.Prompt, d.Action, "动作");
            Eq(HabitApp.ClassIn, d.Preferred, "习惯软件");
            Contains(d.Reason, "理解", "原因");
        });

        await Run("「不理解」：老师打开习惯软件时静默关掉竞争软件", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("语文", "classin", false)), "语文",
                ConflictWithSeewo(), SeewoOnly(), false);
            Eq(HabitAction.CloseCompetitors, d.Action, "动作");
            Eq(1, d.Close.Count, "只关一个");
            Eq(HabitApp.Seewo, d.Close[0], "关掉希沃");
            Contains(d.Reason, "静默关闭", "原因");
        });

        await Run("同一拍里一起出现：先不处理，避免误杀手动打开的软件", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("语文", "classin", false)), "语文",
                ConflictWithSeewo(), new BoardSituation(), false);
            Eq(HabitAction.SuppressPrompt, d.Action, "动作");
            Eq(0, d.Close.Count, "不关任何软件");
        });

        await Run("老师先开习惯软件、再自己打开另一款：不关也不弹（反向不处理）", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("语文", "classin", false)), "语文",
                ConflictWithSeewo(), ConflictWithSeewo(), false);
            Eq(HabitAction.SuppressPrompt, d.Action, "动作");
            Eq(0, d.Close.Count, "不能关老师自己开的软件");
        });

        await Run("老师没在用习惯软件：有冲突也不弹窗", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("语文", "classin", false)), "语文",
                ConflictWithSeewo(), ConflictWithSeewo(), true);
            Eq(HabitAction.SuppressPrompt, d.Action, "动作");
            Eq(0, d.Close.Count, "不关软件");
        });

        await Run("刚启动第一拍只观察，不替老师动手", () =>
        {
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("语文", "classin", false)), "语文",
                ConflictWithSeewo(), null, true);
            Eq(HabitAction.SuppressPrompt, d.Action, "动作");
            Eq(0, d.Close.Count, "不关软件");
        });

        await Run("WPS 当作习惯软件：ClassInX 正在全屏时被静默关闭", () =>
        {
            var current = new BoardSituation { WpsShow = true, ClassInFloating = true, ClassInFullscreen = true };
            var previous = new BoardSituation { ClassInFloating = true, ClassInFullscreen = true };
            var d = SubjectHabitPolicy.Decide(Enabled(Rule("物理", "wps", false)), "物理",
                current, previous, false);
            Eq(HabitAction.CloseCompetitors, d.Action, "动作");
            Eq(HabitApp.ClassIn, d.Close[0], "关掉 ClassInX");
        });

        await Run("规则查找忽略大小写与空白", () =>
        {
            var cfg = Enabled(Rule("Math", "wps", false));
            Eq("Math", SubjectHabitPolicy.Resolve(cfg, " math ")?.Subject, "忽略大小写与空白");
            Eq<HabitRule?>(null, SubjectHabitPolicy.Resolve(cfg, ""), "空科目不匹配");
        });

        await Run("WPS 编辑窗也算在用 WPS（不必等到放映）", () =>
        {
            var s = new BoardSituation { WpsEditor = true };
            True(s.WpsActive, "WpsActive 应包含编辑窗");
            True(SubjectHabitPolicy.IsActive(HabitApp.Wps, s), "WPS 处于活动状态");
            False(s.ShouldPrompt, "只有编辑窗时不构成原冲突条件");
        });
    }
}
