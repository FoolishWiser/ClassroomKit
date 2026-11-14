using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Model;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>课表快照 → 课前提醒 → 板书改名，全在 Core 里，Linux 上就能跑。</summary>
internal static class LessonTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private static LessonSnapshot Snapshot(
        string state = LessonStates.Breaking,
        string? next = "数学",
        DateTimeOffset? nextStart = null,
        string? previous = "语文",
        string? current = null,
        bool planLoaded = true,
        int staleSeconds = 0)
        => LessonSnapshot.FromClassIsland(
            state, current, next, previous,
            nextStart ?? Now.AddMinutes(4),
            0, 3, planLoaded,
            Now.AddSeconds(-staleSeconds));

    public static Task RunAsync()
    {
        BoardNaming();
        LedgerRules();
        ReminderWindow();
        return Task.CompletedTask;
    }

    private static void BoardNaming()
    {
        Run("默认模板渲染成 科目_日期_时间", () =>
        {
            Eq("数学_2026-11-14_10-05", BoardFileName.Render(BoardFileName.DefaultTemplate, "数学", new DateTimeOffset(2026, 11, 14, 10, 5, 0, TimeSpan.FromHours(8))));
        });

        Run("自定义模板支持 节次/周几 占位符", () =>
        {
            var at = new DateTimeOffset(2026, 11, 14, 10, 5, 0, TimeSpan.FromHours(8));
            Eq("第3节-语文-周六", BoardFileName.Render("{节次}-{科目}-{周几}", "语文", at, 3));
        });

        Run("没有科目名时退回 板书_日期_时间", () =>
        {
            var at = new DateTimeOffset(2026, 11, 14, 10, 5, 0, TimeSpan.FromHours(8));
            Eq("板书_2026-11-14_10-05", BoardFileName.Render("{科目}_{日期}_{时间}", "  ", at));
        });

        Run("文件名里的非法字符被替换", () =>
        {
            Eq("数学_分析_练习", BoardFileName.Sanitize("数学/分析:练习"));
        });

        Run("改名遇到同名文件会补序号", () =>
        {
            var dir = TempDir();
            var kept = Path.Combine(dir, "数学.png");
            File.WriteAllText(kept, "old");
            var src = Path.Combine(dir, "ClassIn-20261114.png");
            File.WriteAllText(src, "new");

            var result = BoardFileName.ApplyTo(new[] { src }, "数学");
            Eq(1, result.Renamed, "改名成功数");
            Eq(Path.Combine(dir, "数学_2.png"), result.Files[0], "同名时补 _2");
            True(File.Exists(result.Files[0]), "新文件应存在");
            True(File.Exists(kept), "原同名文件不能被覆盖");
        });

        Run("空文件列表改名不报错", () =>
        {
            var result = BoardFileName.ApplyTo(Array.Empty<string>(), "数学");
            Eq(0, result.Renamed, "改名数");
            Contains(result.Message, "没有需要改名");
        });
    }

    private static void LedgerRules()
    {
        Run("没有课表数据时不提醒", () =>
        {
            var ledger = LessonLedger.FromState(null);
            var d = ledger.Evaluate(Now, new LessonConfig());
            Eq("no-data", d.Reason);
            False(d.Show, "不该弹窗");
        });

        Run("课表未加载时不提醒", () =>
        {
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(planLoaded: false));
            Eq("no-plan", ledger.Evaluate(Now, new LessonConfig()).Reason);
        });

        Run("上课中、放学后都不提醒", () =>
        {
            var cfg = new LessonConfig();
            var inClass = LessonLedger.FromState(null);
            inClass.Accept(Snapshot(state: LessonStates.Class, current: "语文"));
            Eq("in-class", inClass.Evaluate(Now, cfg).Reason);

            var after = LessonLedger.FromState(null);
            after.Accept(Snapshot(state: LessonStates.AfterSchool, next: null));
            Eq("after-school", after.Evaluate(Now, cfg).Reason);
        });

        Run("离上课还早时不提醒，进入提前量才提醒", () =>
        {
            var cfg = new LessonConfig { RemindMinutesBefore = 5 };

            var early = LessonLedger.FromState(null);
            early.Accept(Snapshot(nextStart: Now.AddMinutes(20)));
            Eq("too-early", early.Evaluate(Now, cfg).Reason);

            var due = LessonLedger.FromState(null);
            due.Accept(Snapshot(nextStart: Now.AddMinutes(4)));
            var d = due.Evaluate(Now, cfg);
            True(d.Show, "4 分钟后上课应该弹窗");
            Eq("ok", d.Reason);
            Eq("数学", d.Subject);
            Eq("2026-11-14 10:04", d.Key, "去重 key 用下一节开始时刻");
        });

        Run("同一节课只提醒一次", () =>
        {
            var cfg = new LessonConfig();
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(nextStart: Now.AddMinutes(3)));
            var first = ledger.Evaluate(Now, cfg);
            True(first.Show, "第一次应弹窗");
            ledger.MarkReminded(first.Key);
            Eq("already", ledger.Evaluate(Now, cfg).Reason);
        });

        Run("稍后提醒在窗口内不弹，过窗后重新弹", () =>
        {
            var cfg = new LessonConfig();
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(nextStart: Now.AddMinutes(4)));
            ledger.Snooze(Now, 2);
            Eq("snoozed", ledger.Evaluate(Now, cfg).Reason);

            var later = Now.AddMinutes(2);
            var snapshot = ledger.Peek()!;
            var refreshed = LessonLedger.FromState(new LessonLedgerState(snapshot with { At = later }, "", ledger.SnoozedUntil));
            True(refreshed.Evaluate(later, cfg).Show, "过窗后应再次弹窗");
        });

        Run("数据过期就不提醒（课表程序可能已退出）", () =>
        {
            var cfg = new LessonConfig { StaleMinutes = 10 };
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(nextStart: Now.AddMinutes(3), staleSeconds: 11 * 60));
            Eq("stale", ledger.Evaluate(Now, cfg).Reason);
        });

        Run("报名用的科目名：上课取当前，课间取上一节，过期不给", () =>
        {
            var inClass = LessonLedger.FromState(null);
            inClass.Accept(Snapshot(state: LessonStates.Class, current: "英语", nextStart: Now.AddMinutes(40)));
            Eq("英语", inClass.NameHint(Now), "上课时用当前科目");

            var breaking = LessonLedger.FromState(null);
            breaking.Accept(Snapshot(state: LessonStates.Breaking, previous: "物理"));
            Eq("物理", breaking.NameHint(Now), "课间用上一节");

            var stale = LessonLedger.FromState(null);
            stale.Accept(Snapshot(staleSeconds: 200 * 60));
            Eq(null, stale.NameHint(Now), "数据太旧宁可回默认名");
        });
    }

    private static void ReminderWindow()
    {
        Run("超时动作 save：倒计时结束选中保存", () =>
        {
            var model = new BoardReminderModel(timeoutSec: 10, timeoutAction: "save");
            False(model.Tick(5000), "半途不该到点");
            True(model.Tick(6000), "累计 11 秒应到点");
            Eq(BoardReminderChoice.Save, model.Chosen, "超时动作");
            True(model.Expired, "应标记过期");
        });

        Run("超时动作 wait：永远不自动关", () =>
        {
            var model = new BoardReminderModel(timeoutSec: 10, timeoutAction: "wait");
            Eq(null, model.TimeoutChoice, "wait 表示不自动处理");
            False(model.Tick(60000), "不该到点");
            Eq(null, model.Chosen, "不该自动选择");
            Contains(model.Body, "等您决定");
        });

        Run("悬停暂停不吃时间", () =>
        {
            var model = new BoardReminderModel(timeoutSec: 10, timeoutAction: "save") { Paused = true };
            False(model.Tick(20000), "暂停时不该到点");
            model.Paused = false;
            False(model.Tick(5000), "恢复后重新计时");
            True(model.Tick(6000), "累计够了才到点");
        });

        Run("键盘与文案", () =>
        {
            Eq(BoardReminderChoice.Save, BoardReminderModel.MapKey('1'));
            Eq(BoardReminderChoice.SaveAndClose, BoardReminderModel.MapKey('2'));
            Eq(BoardReminderChoice.Snooze, BoardReminderModel.MapKey('3'));
            Eq(null, BoardReminderModel.MapKey('9'));
            Eq("保存并关闭", BoardReminderModel.Label(BoardReminderChoice.SaveAndClose));
        });

        Run("标题写清几分钟后上课和科目", () =>
        {
            var model = new BoardReminderModel(subject: "数学", secondsToStart: 300);
            Eq("下节课 5 分钟后开始 · 数学", model.Title);
            var noSubject = new BoardReminderModel(subject: null, secondsToStart: 300);
            Eq("下节课 5 分钟后开始", noSubject.Title);
        });
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-lesson-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
