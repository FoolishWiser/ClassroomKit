using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Lesson;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>板书：文件名带节次、科目文件夹归档、板书记录与下一节同科目续用。</summary>
internal static class BoardV104Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    public static Task RunAsync()
    {
        Naming();
        Archiving();
        Reuse();
        ConfigMigration();
        return Task.CompletedTask;
    }

    private static void Naming()
    {
        Run("默认模板带第几节", () =>
        {
            var at = new DateTimeOffset(2026, 11, 14, 10, 5, 0, TimeSpan.FromHours(8));
            Eq("数学_第3节_2026-11-14_10-05", BoardFileName.Render(BoardFileName.DefaultTemplate, "数学", at, 3));
        });

        Run("模板没写节次就不擅自追加，关掉开关时节次渲染为空", () =>
        {
            var at = new DateTimeOffset(2026, 11, 14, 10, 5, 0, TimeSpan.FromHours(8));
            Eq("数学_2026-11-14_10-05", BoardFileName.Render("{科目}_{日期}_{时间}", "数学", at, 2));
            Eq("数学_2026-11-14_10-05", BoardFileName.Render("{科目}_{节次}_{日期}_{时间}", "数学", at, 2, includeLessonInName: false));
        });

        Run("科目为空归到「未分类」，非法字符被清理", () =>
        {
            Eq("未分类", BoardFileName.SubjectFolderName("  "));
            Eq("语文_阅读", BoardFileName.SubjectFolderName("语文/阅读"));
        });

        Run("按科目建文件夹并移入三个文件", () =>
        {
            var dir = TempDir();
            try
            {
                var files = new[] { "a.edb", "a.png", "a.pdf" }
                    .Select(name => Path.Combine(dir, name)).ToArray();
                foreach (var file in files) File.WriteAllText(file, "x");

                var result = BoardFileName.MoveToSubjectFolder(files, dir, "数学");
                Eq(3, result.Moved, "移动成功数");
                Eq(0, result.Failed, "移动失败数");
                foreach (var file in result.Files)
                {
                    Eq("数学", Path.GetFileName(Path.GetDirectoryName(file)), "文件应在科目文件夹里");
                    True(File.Exists(file), "移动后的文件应存在");
                }
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }

    private static void Archiving()
    {
        Run("板书记录：写入、按科目节次查最近、按时间倒序", () =>
        {
            var dir = TempDir();
            try
            {
                var archive = new BoardArchive(Path.Combine(dir, "board-index.json"));
                var older = Record("old", "数学", 3, Now.AddMinutes(-40));
                var newer = Record("new", "数学", 3, Now.AddMinutes(-10));
                archive.Add(older);
                archive.Add(newer);

                Eq(2, archive.Count, "记录数");
                Eq("new", archive.Recent(2)[0].Id, "最近记录排前面");
                Eq("new", archive.LatestFor("数学", 3, Now)!.Id, "同科目同节次取最近一次");
                Eq(null, archive.LatestFor("语文", 3, Now), "科目不同不匹配");
                Eq(null, archive.LatestFor("数学", 2, Now), "节次不同不匹配");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Run("坏索引当空索引，不抛异常", () =>
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "board-index.json");
                File.WriteAllText(path, "{ not json");
                var archive = new BoardArchive(path);
                Eq(0, archive.Count);
                Eq(null, archive.Recent(5).FirstOrDefault());
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }

    private static void Reuse()
    {
        Run("连堂课间不再保存，也不在课间提示续用", () =>
        {
            var dir = TempDir();
            try
            {
                var archive = new BoardArchive(Path.Combine(dir, "board-index.json"));
                archive.Add(Record("p3", "数学", 3, Now.AddMinutes(-50)));
                var snapshot = Snapshot(LessonStates.Breaking, next: "数学", current: null, previous: "数学",
                    lessonIndex: 3, previousIndex: 3, nextIndex: 4,
                    currentStart: Now.AddMinutes(-45), currentEnd: Now.AddMinutes(-5), nextEnd: Now.AddMinutes(48));
                Eq(null, BoardReusePlanner.FromSnapshot(snapshot, archive, Now), "课间一律不提示");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Run("上课中上一节同科目，窗口从本节上课到下课", () =>
        {
            var dir = TempDir();
            try
            {
                var archive = new BoardArchive(Path.Combine(dir, "board-index.json"));
                archive.Add(Record("p3", "数学", 3, Now.AddMinutes(-55)));
                var snapshot = Snapshot(LessonStates.Class, next: "语文", current: "数学", previous: "数学",
                    lessonIndex: 4, previousIndex: 3, nextIndex: 5,
                    currentStart: Now.AddMinutes(-8), currentEnd: Now.AddMinutes(37), nextEnd: Now.AddMinutes(90));

                var target = BoardReusePlanner.FromSnapshot(snapshot, archive, Now);
                NotNull(target, "上课中应生成续用目标");
                Eq(3, target!.PreviousLessonIndex);
                Eq(4, target.LessonIndex);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Run("续用状态：上课中提示一次、稍后重试、这次不用", () =>
        {
            var dir = TempDir();
            try
            {
                var archive = new BoardArchive(Path.Combine(dir, "board-index.json"));
                archive.Add(Record("p3", "数学", 3, Now.AddMinutes(-55)));
                var snapshot = Snapshot(LessonStates.Class, next: "语文", current: "数学", previous: "数学",
                    lessonIndex: 4, previousIndex: 3, nextIndex: 5,
                    currentStart: Now.AddMinutes(-8), currentEnd: Now.AddMinutes(37), nextEnd: Now.AddMinutes(90));
                var target = BoardReusePlanner.FromSnapshot(snapshot, archive, Now)!;

                var state = new BoardReuseState();
                True(state.ShouldPrompt(target, Now), "第一次应提示");
                state = state.MarkPrompted(target);
                False(state.ShouldPrompt(target, Now), "同一目标只提示一次");

                state = state.Snooze(Now.AddMinutes(10));
                False(state.ShouldPrompt(target, Now.AddMinutes(5)), "稍后期间不提示");
                True(state.ShouldPrompt(target, Now.AddMinutes(11)), "稍后到期后重新提示");

                state = state.Dismiss(target);
                False(state.ShouldPrompt(target, Now.AddDays(1)), "这次不用后本目标不再提示");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }

    private static void ConfigMigration()
    {
        Run("结构 9：旧默认模板换成带节次的新默认", () =>
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "old.json");
                File.WriteAllText(path, "{ \"schemaVersion\": 7, \"boardSave\": { \"nameTemplate\": \"{科目}_{日期}_{时间}\" } }");
                var cfg = new ConfigStore(path).Current;
                Eq(9, cfg.SchemaVersion, "升到结构 9");
            Eq("delete", cfg.BoardSave.ClearKey, "默认清屏键是 Delete");
                Eq(BoardFileName.DefaultTemplate, cfg.BoardSave.NameTemplate, "旧默认模板跟着升级");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Run("结构 9：自定义模板不被覆盖", () =>
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "custom.json");
                File.WriteAllText(path, "{ \"schemaVersion\": 7, \"boardSave\": { \"nameTemplate\": \"{科目}_{日期}\" } }");
                Eq("{科目}_{日期}", new ConfigStore(path).Current.BoardSave.NameTemplate, "自定义模板保留");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }

    private static BoardRecord Record(string id, string subject, int index, DateTimeOffset at)
        => new(id, subject, index, at, "/boards", new[] { $"/boards/{subject}_{index}.edb" });

    private static LessonSnapshot Snapshot(string state, string? next, string? current, string? previous,
        int lessonIndex, int previousIndex, int nextIndex, DateTimeOffset currentStart, DateTimeOffset currentEnd, DateTimeOffset nextEnd)
        => LessonSnapshot.FromClassIsland(state, current, next, previous, currentEnd, 0, lessonIndex, true, Now,
            new[] { "数学", "语文" }, null, previousIndex, nextIndex, currentStart, currentEnd, nextEnd);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}