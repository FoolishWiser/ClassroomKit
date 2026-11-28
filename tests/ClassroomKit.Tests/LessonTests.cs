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
        int staleSeconds = 0,
        string? breakName = null)
        => LessonSnapshot.FromClassIsland(
            state, current, next, previous,
            nextStart ?? Now.AddMinutes(4),
            0, 3, planLoaded,
            Now.AddSeconds(-staleSeconds), breakName: breakName);

    public static Task RunAsync()
    {
        BoardNaming();
        LedgerRules();
        ReminderWindow();
        SubjectScope();
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

        Run("上课中不提醒，下课后提醒刚上完的那一节", () =>
        {
            var cfg = new LessonConfig();
            var inClass = LessonLedger.FromState(null);
            inClass.Accept(Snapshot(state: LessonStates.Class, current: "语文", nextStart: null));
            Eq("in-class", inClass.Evaluate(Now, cfg).Reason);

            var breaking = LessonLedger.FromState(null);
            breaking.Accept(Snapshot());
            var d = breaking.Evaluate(Now, cfg);
            True(d.Show, "课间应该提醒保存刚上完的板书");
            Eq("ok", d.Reason);
            Eq("语文", d.Subject, "科目取刚上完的那一节");
            Eq("break|2026-11-14 10:04", d.Key, "去重 key 用本课间 + 下一节开始时刻");
            Eq(Now.AddMinutes(4), d.RemindAt);
            Eq(240, d.SecondsToStart, "剩余秒数按下一节开始算");
        });

        Run("放学后保存最后一节", () =>
        {
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(state: LessonStates.AfterSchool, next: null, previous: "数学"));
            var d = ledger.Evaluate(Now, new LessonConfig());
            True(d.Show, "放学后应该提醒保存最后一节的板书");
            Eq("数学", d.Subject);
            Eq("after|2026-11-14|数学", d.Key);
        });

        Run("连堂课间不保存，等这门课最后一段下课再存", () =>
        {
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(next: "语文", previous: "语文"));
            var d = ledger.Evaluate(Now, new LessonConfig());
            Eq("continuous", d.Reason);
            False(d.Show, "连堂中间不打扰老师，避免把同一份板书拆成两段");
        });

        Run("课间名命中跳过清单时不保存", () =>
        {
            var cfg = new LessonConfig { SkipBreakNames = new List<string> { "高一礼让" } };
            var skipped = LessonLedger.FromState(null);
            skipped.Accept(Snapshot(breakName: "高一礼让"));
            Eq("break-skipped", skipped.Evaluate(Now, cfg).Reason);

            var normal = LessonLedger.FromState(null);
            normal.Accept(Snapshot(breakName: "课间"));
            True(normal.Evaluate(Now, cfg).Show, "其它课间照常提醒");

            var another = LessonLedger.FromState(null);
            another.Accept(Snapshot(breakName: "跑操"));
            Eq("break-skipped", another.Evaluate(Now, new LessonConfig { SkipBreakNames = new List<string> { " 跑操 " } }).Reason);
        });

        Run("同一个课间只提醒一次", () =>
        {
            var cfg = new LessonConfig();
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot());
            var first = ledger.Evaluate(Now, cfg);
            True(first.Show, "第一次应弹窗");
            ledger.MarkReminded(first.Key);
            Eq("already", ledger.Evaluate(Now, cfg).Reason);
        });

        Run("数据过期就不提醒（课表程序可能已退出）", () =>
        {
            var cfg = new LessonConfig { StaleMinutes = 10 };
            var ledger = LessonLedger.FromState(null);
            ledger.Accept(Snapshot(staleSeconds: 11 * 60));
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
        Run("下课后 5 分钟进入 15 秒倒计时", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var armed = tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            Eq(BoardAutoSavePhases.Waiting, armed.Phase);
            Eq("语文", armed.Subject);
            Eq(Now.AddMinutes(5), armed.DueAt);
            Eq(Now.AddMinutes(7.5), armed.DeadlineAt);

            var still = tracker.Tick(Now.AddMinutes(4), null, Snapshot(nextStart: Now.AddMinutes(10)));
            Eq(BoardAutoSavePhases.Waiting, still.State.Phase);

            var counting = tracker.Tick(Now.AddMinutes(5), null, Snapshot(nextStart: Now.AddMinutes(10)));
            Eq(BoardAutoSavePhases.Countdown, counting.State.Phase);
            Eq(15, counting.State.RemainingSeconds);
            False(counting.StartSave, "刚进倒计时不该保存");
        });

        Run("15 秒无人操作就自动保存", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            tracker.Tick(Now.AddMinutes(5), null, Snapshot(nextStart: Now.AddMinutes(10)));
            var before = tracker.Tick(Now.AddMinutes(5).AddSeconds(14), null, Snapshot(nextStart: Now.AddMinutes(10)));
            False(before.StartSave, "14 秒还没到");
            var due = tracker.Tick(Now.AddMinutes(5).AddSeconds(15), null, Snapshot(nextStart: Now.AddMinutes(10)));
            True(due.StartSave, "15 秒应触发保存");
            Eq(BoardAutoSavePhases.Saving, due.State.Phase);
        });

        Run("倒计时内点击屏幕会打断，10 秒后重试", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            var start = Now.AddMinutes(5);
            tracker.Tick(start, null, Snapshot(nextStart: Now.AddMinutes(10)));
            var clicked = tracker.Tick(start.AddSeconds(2), start.AddSeconds(1), Snapshot(nextStart: Now.AddMinutes(10)));
            Eq(BoardAutoSavePhases.Cooldown, clicked.State.Phase);
            False(clicked.StartSave, "被打断后不能保存");
            Eq(10, clicked.State.RemainingSeconds);

            var wait = tracker.Tick(start.AddSeconds(12), start.AddSeconds(1), Snapshot(nextStart: Now.AddMinutes(10)));
            False(wait.StartSave, "10 秒冷却结束后重新倒计时，不能立刻保存");
            Eq(BoardAutoSavePhases.Countdown, wait.State.Phase);

            var retry = tracker.Tick(start.AddSeconds(27), start.AddSeconds(1), Snapshot(nextStart: Now.AddMinutes(10)));
            True(retry.StartSave, "重试倒计时 15 秒后保存");
        });

        Run("冷却期间继续操作会重新计 10 秒", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            var start = Now.AddMinutes(5);
            tracker.Tick(start, null, Snapshot(nextStart: Now.AddMinutes(10)));
            tracker.Tick(start.AddSeconds(2), start.AddSeconds(1), Snapshot(nextStart: Now.AddMinutes(10)));
            var again = tracker.Tick(start.AddSeconds(6), start.AddSeconds(5), Snapshot(nextStart: Now.AddMinutes(10)));
            Eq(10, again.State.RemainingSeconds, "继续操作要把冷却重置成 10 秒");
            var still = tracker.Tick(start.AddSeconds(14), start.AddSeconds(5), Snapshot(nextStart: Now.AddMinutes(10)));
            Eq(BoardAutoSavePhases.Cooldown, still.State.Phase);
        });

        Run("持续操作最多顺延 60 秒，之后必须保存", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            var start = Now.AddMinutes(5);
            tracker.Tick(start, null, Snapshot(nextStart: Now.AddMinutes(10)));
            tracker.Tick(start.AddSeconds(2), start.AddSeconds(1), Snapshot(nextStart: Now.AddMinutes(10)));

            // 每 5 秒碰一下屏幕：前 60 秒一直被顺延。
            var t = start.AddSeconds(6);
            for (; t <= start.AddSeconds(56); t = t.AddSeconds(5))
            {
                var held = tracker.Tick(t, t.AddSeconds(-1), Snapshot(nextStart: Now.AddMinutes(10)));
                Eq(BoardAutoSavePhases.Cooldown, held.State.Phase, $"第 {(t - start).TotalSeconds:F0} 秒仍在冷却");
                False(held.StartSave, "冷却中不保存");
            }

            // 超过上限后不能再顺延，回到倒计时；没人再碰，15 秒后保存。
            tracker.Tick(start.AddSeconds(62), start.AddSeconds(61), Snapshot(nextStart: Now.AddMinutes(10)));
            var saved = tracker.Tick(start.AddSeconds(78), start.AddSeconds(61), Snapshot(nextStart: Now.AddMinutes(10)));
            True(saved.StartSave, "顺延上限到了必须保存，否则下课保存永远不触发");
        });

        Run("快到上课前 2 分 30 秒就放弃", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var snapshot = Snapshot(nextStart: Now.AddMinutes(10));
            tracker.Arm(snapshot, Now, true);
            tracker.Tick(Now.AddMinutes(5), null, snapshot);
            // 7:20 点击 → 冷却到 7:30，已越过 7:30 截止线
            tracker.Tick(Now.AddMinutes(7).AddSeconds(20), Now.AddMinutes(7).AddSeconds(19), snapshot);
            var late = tracker.Tick(Now.AddMinutes(7).AddSeconds(31), Now.AddMinutes(7).AddSeconds(19), snapshot);
            Eq(BoardAutoSavePhases.Abandoned, late.State.Phase);
            False(late.StartSave, "放弃后不能再保存");
        });

        Run("课间太短直接放弃", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var state = tracker.Arm(Snapshot(nextStart: Now.AddMinutes(5)), Now, true);
            Eq(BoardAutoSavePhases.Abandoned, state.Phase);
        });

        Run("ClassInX 没运行就不自动保存", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var state = tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, false);
            Eq(BoardAutoSavePhases.Abandoned, state.Phase);
            Contains(state.Message, "ClassInX");
        });

        Run("放学后 30 分钟内仍会尝试保存", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var snapshot = Snapshot(state: LessonStates.AfterSchool, next: null, nextStart: null, previous: "语文");
            var armed = tracker.Arm(snapshot, Now, true);
            Eq(BoardAutoSavePhases.Waiting, armed.Phase);
            Eq(Now.AddMinutes(30), armed.DeadlineAt);
        });

        Run("预览只走倒计时，不真保存，结束后回到空闲", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var state = tracker.Preview(Now);
            Eq(BoardAutoSavePhases.Countdown, state.Phase);
            var done = tracker.Tick(Now.AddSeconds(15), null, null);
            False(done.StartSave, "预览绝不能保存");
            Eq(BoardAutoSavePhases.Idle, done.State.Phase);
        });

        Run("保存完成后 15 秒回到空闲", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            tracker.Arm(Snapshot(nextStart: Now.AddMinutes(10)), Now, true);
            tracker.MarkSaving();
            var saved = tracker.Complete(true, "已保存", Now.AddMinutes(6));
            Eq(BoardAutoSavePhases.Saved, saved.Phase);
            var after = tracker.Tick(Now.AddMinutes(6).AddSeconds(16), null, null);
            Eq(BoardAutoSavePhases.Idle, after.State.Phase);
        });

        Run("真实倒计时进行中，预览不能覆盖", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var snapshot = Snapshot(nextStart: Now.AddMinutes(10));
            tracker.Arm(snapshot, Now, true);
            var due = Now.AddMinutes(5);
            tracker.Tick(due, null, snapshot);
            var preview = tracker.Preview(due.AddSeconds(1));
            Eq(BoardAutoSavePhases.Countdown, preview.Phase);
            False(string.Equals(preview.Key, "preview", StringComparison.Ordinal), "预览不能顶掉真实倒计时");
            var save = tracker.Tick(due.AddSeconds(15), null, snapshot);
            True(save.StartSave, "真实倒计时到点仍应保存");
        });

        Run("迟到的保存结果按代次丢弃", () =>
        {
            var tracker = new BoardAutoSaveTracker(() => new AppConfig());
            var snapshot = Snapshot(nextStart: Now.AddMinutes(10));
            tracker.Arm(snapshot, Now, true);
            var due = Now.AddMinutes(5);
            tracker.Tick(due, null, snapshot);
            var tick = tracker.Tick(due.AddSeconds(15), null, snapshot);
            True(tick.StartSave, "应开始保存");
            tracker.MarkSaving(tick.Generation);
            var stale = tracker.Complete(tick.Generation + 1, true, "上一轮", due.AddSeconds(2));
            Eq(BoardAutoSavePhases.Saving, stale.Phase, "旧代次结果不能改状态");
            var fresh = tracker.Complete(tick.Generation, true, "本轮", due.AddSeconds(3));
            Eq(BoardAutoSavePhases.Saved, fresh.Phase, "当前代次才生效");
        });
    }

    /// <summary>「只在物理课上提醒」这类科目定向：白名单匹配、空列表 = 全部、不匹配时 Evaluate 拒绝。</summary>
    private static void SubjectScope()
    {
        Run("生效科目：空列表 = 所有科目都生效（旧配置行为）", () =>
        {
            True(LessonSubjects.Matches("数学", Array.Empty<string>()), "空列表不限制");
            True(LessonSubjects.Matches("物理", null), "null 也不限制");
            True(LessonSubjects.IsUnrestricted(new[] { "  ", "" }), "只有空白项等于没配");
        });

        Run("生效科目：整理时去空白、去空项、忽略大小写去重", () =>
        {
            var list = LessonSubjects.Normalize(new[] { " 物理 ", "", "物理", " 化学 ", "  " });
            Eq(2, list.Count, "重复项应合并");
            Eq("物理", list[0], "保留第一次出现的写法");
            Eq("化学", list[1], "去掉两端空白");
        });

        Run("生效科目：宽松匹配（去空格、忽略大小写、包含关系）", () =>
        {
            True(LessonSubjects.Matches("物理", new[] { "物理" }), "同名命中");
            True(LessonSubjects.Matches(" 物理 ", new[] { "物 理" }), "两边空白都忽略");
            True(LessonSubjects.Matches("物理实验", new[] { "物理" }), "配置「物理」能匹配「物理实验」");
            True(LessonSubjects.Matches("物理", new[] { "物理/实验" }), "课表写「物理/实验」也能匹配");
            True(LessonSubjects.Matches("Physics", new[] { "physics" }), "英文不区分大小写");
            False(LessonSubjects.Matches("数学", new[] { "物理" }), "不同科目不命中");
            False(LessonSubjects.Matches(null, new[] { "物理" }), "课表没给科目名不算命中");
        });

        Run("生效科目：刚上完的课不在白名单时 Evaluate 返回 subject-skipped", () =>
        {
            var cfg = new LessonConfig { Subjects = new List<string> { "物理" } };

            var math = LessonLedger.FromState(null);
            math.Accept(Snapshot(previous: "数学"));
            var skipped = math.Evaluate(Now, cfg);
            Eq("subject-skipped", skipped.Reason);
            False(skipped.Show, "不匹配的科目不该弹窗");
            Eq("", skipped.Key, "没有进入提醒流程，也就没有去重 key");

            var physics = LessonLedger.FromState(null);
            physics.Accept(Snapshot(previous: "物理实验"));
            var ok = physics.Evaluate(Now, cfg);
            True(ok.Show, "物理实验应算命中「物理」");
            Eq("ok", ok.Reason);
            Eq("物理实验", ok.Subject, "保存的是刚上完的那一节");
        });

        Run("生效科目不影响板书命名", () =>
        {
            var cfg = new LessonConfig { Subjects = new List<string> { "物理" } };

            // 课间：刚上完的是数学，提醒被跳过……
            var breaking = LessonLedger.FromState(null);
            breaking.Accept(Snapshot(previous: "数学"));
            Eq("subject-skipped", breaking.Evaluate(Now, cfg).Reason, "数学课不提醒");

            // ……但命名不看白名单：上课取当前科目，照旧是「数学」
            var inClass = LessonLedger.FromState(null);
            inClass.Accept(Snapshot(state: LessonStates.Class, current: "数学", nextStart: Now.AddMinutes(40)));
            Eq("数学", inClass.NameHint(Now), "板书文件名照旧按当前科目命名");
        });

        Run("课表快照带整份科目清单：归一后去重，缺省为空", () =>
        {
            var withSubjects = LessonSnapshot.FromClassIsland(
                LessonStates.Breaking, null, "数学", "语文", Now.AddMinutes(4), 0, 3, true, Now,
                new[] { " 物理 ", "化学", "物理", "" });
            Eq(2, withSubjects.Subjects.Count, "重复与空白项被丢掉");
            Eq("物理", withSubjects.Subjects[0], "保留第一次出现的写法");

            var plain = Snapshot();
            Eq(0, plain.Subjects.Count, "旧插件不推科目时是空列表");
            True(LessonSubjects.IsUnrestricted(plain.Subjects), "空列表等于所有科目");
        });

        Run("科目清单能随快照 JSON 往返", () =>
        {
            var snapshot = LessonSnapshot.FromClassIsland(
                LessonStates.Breaking, null, "数学", "语文", Now.AddMinutes(4), 0, 3, true, Now,
                new[] { "物理", "化学" });
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            var back = System.Text.Json.JsonSerializer.Deserialize<LessonSnapshot>(json)!;
            Eq(2, back.Subjects.Count, "往返后科目数不变");
            Eq("化学", back.Subjects[1], "科目名不变");

            var empty = System.Text.Json.JsonSerializer.Deserialize<LessonSnapshot>(
                "{\"state\":\"breaking\",\"at\":\"2026-11-14T10:00:00+08:00\"}")!;
            Eq(0, empty.Subjects.Count, "没有 subjects 字段时是空列表");
        });
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-lesson-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
