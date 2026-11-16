using ClassroomKit.Core.Lesson;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>从 ClassIsland 档案 JSON 里读科目清单（不依赖 Windows，Linux 上能跑）。</summary>
internal static class ProfileSubjectsTests
{
    public static Task RunAsync()
    {
        ReadFromJson();
        ReadFromDirectory();
        return Task.CompletedTask;
    }

    private static void ReadFromJson()
    {
        Run("档案里 Subjects 是对象：取每项的 Name", () =>
        {
            var dir = NewDir();
            try
            {
                var file = Path.Combine(dir, "Default.json");
                File.WriteAllText(file, "{\"Subjects\":{\"a\":{\"Name\":\"语文\"},\"b\":{\"Name\":\" 数学 \"}}}");
                var list = ClassIslandProfileSubjects.ReadFile(file);
                Eq(2, list.Count, "读到两个科目");
                Eq("语文", list[0], "第一个");
                Eq("数学", list[1], "去空白");
            }
            finally
            {
                Cleanup(dir);
            }
        });

        Run("档案里 subjects 是数组：字符串与 name 对象都认", () =>
        {
            var dir = NewDir();
            try
            {
                var file = Path.Combine(dir, "Default.json");
                File.WriteAllText(file, "{\"subjects\":[\"物理\",{\"name\":\"化学\"},\"\",null]}");
                var list = ClassIslandProfileSubjects.ReadFile(file);
                Eq(2, list.Count, "空项被丢掉");
                Eq("物理", list[0], "字符串项");
                Eq("化学", list[1], "对象项");
            }
            finally
            {
                Cleanup(dir);
            }
        });

        Run("坏 JSON 与没有 Subjects 都返回空", () =>
        {
            var dir = NewDir();
            try
            {
                var bad = Path.Combine(dir, "Bad.json");
                File.WriteAllText(bad, "{ not json");
                Eq(0, ClassIslandProfileSubjects.ReadFile(bad).Count, "坏 JSON 返回空");

                var empty = Path.Combine(dir, "Empty.json");
                File.WriteAllText(empty, "{\"ClassPlans\":{}}");
                Eq(0, ClassIslandProfileSubjects.ReadFile(empty).Count, "没有 subjects 返回空");
                Eq(0, ClassIslandProfileSubjects.ReadFile(Path.Combine(dir, "Missing.json")).Count, "文件不存在返回空");
            }
            finally
            {
                Cleanup(dir);
            }
        });

        Run("几份科目清单合并去重", () =>
        {
            var union = LessonSubjects.Union(new[] { "语文", " 数学 " }, new[] { "数学", "英语" }, null);
            Eq(3, union.Count, "去重后三个");
            Eq("语文", union[0], "保持第一次出现的顺序");
            Eq("英语", union[2], "第三项");
        });
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // 临时目录删不掉不影响结果
        }
    }

    private static void ReadFromDirectory()
    {
        Run("扫描 Profiles 目录：合并所有 json，按文件名排序", () =>
        {
            var dir = NewDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "B.json"), "{\"Subjects\":{\"x\":{\"Name\":\"英语\"}}}");
                File.WriteAllText(Path.Combine(dir, "A.json"), "{\"Subjects\":{\"x\":{\"Name\":\"语文\"}}}");
                var list = ClassIslandProfileSubjects.ReadDirectory(dir);
                Eq(2, list.Count, "两份档案两个科目");
                Eq("语文", list[0], "A.json 在前");
                Eq("英语", list[1], "B.json 在后");
                Eq(0, ClassIslandProfileSubjects.ReadDirectory(null).Count, "null 目录返回空");
            }
            finally
            {
                Cleanup(dir);
            }
        });
    }
}
