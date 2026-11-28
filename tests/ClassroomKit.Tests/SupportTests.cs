using System.IO.Compression;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Support;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

internal static class SupportTests
{
    public static Task RunAsync()
    {
        BoardLibraryScan();
        BundleRedaction();
        BundleEntries();
        return Task.CompletedTask;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void BoardLibraryScan()
    {
        Run("板书中心统计顶层文件、按修改时间取最近", () =>
        {
            var dir = TempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "数学_01.png"), "1234");
                File.WriteAllText(Path.Combine(dir, "语文_02.png"), "123456");
                File.SetLastWriteTime(Path.Combine(dir, "数学_01.png"), new DateTime(2026, 11, 14, 9, 0, 0));
                File.SetLastWriteTime(Path.Combine(dir, "语文_02.png"), new DateTime(2026, 11, 14, 10, 0, 0));
                var subjectDir = Path.Combine(dir, "数学");
                Directory.CreateDirectory(subjectDir);
                File.WriteAllText(Path.Combine(subjectDir, "数学_03.png"), "12");
                File.SetLastWriteTime(Path.Combine(subjectDir, "数学_03.png"), new DateTime(2026, 11, 14, 11, 0, 0));

                var summary = BoardLibrary.Scan(dir);
                Eq(3, summary.Count, "顶层与一级科目文件夹都统计");
                Eq(12L, summary.TotalBytes, "总占用");
                Eq("数学_03.png", summary.Recent[0].Name, "最近文件排在前面");
                Eq("数学", summary.Recent[0].Subject, "子目录名当科目");
                Eq(3, summary.Recent.Count, "最近列表数量");
                True(summary.FreeBytes >= 0, "磁盘剩余不应为负");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        Run("板书目录不存在时返回空摘要而不是抛异常", () =>
        {
            var summary = BoardLibrary.Scan(Path.Combine(Path.GetTempPath(), "ck-missing-" + Guid.NewGuid().ToString("N")));
            Eq(0, summary.Count);
            Eq(0L, summary.TotalBytes);
            Eq(0, summary.Recent.Count);
        });
    }

    private static void BundleRedaction()
    {
        Run("诊断包脱敏：token/密码/查询串/Bearer 都变成星号", () =>
        {
            var text = "token=abc123 &password=pass456 ?t=query789 Authorization: Bearer eyJhbGci";
            var clean = SupportBundle.Redact(text);
            False(clean.Contains("abc123"), "token 不应保留");
            False(clean.Contains("pass456"), "密码不应保留");
            False(clean.Contains("query789"), "查询串令牌不应保留");
            False(clean.Contains("eyJhbGci"), "Bearer 令牌不应保留");
            Contains(clean, "token=***");
        });

        Run("诊断包脱敏：显式传入的热点密码也会被替换", () =>
        {
            var clean = SupportBundle.Redact("ssid=教室热点 密码=abc-def-ghi", new[] { "abc-def-ghi" });
            False(clean.Contains("abc-def-ghi"), "明显的密码要替换");
            Contains(clean, "***");
        });
    }

    private static void BundleEntries()
    {
        Run("诊断包 zip：entry 名不能带路径，重名自动加序号", () =>
        {
            var dir = TempDir();
            try
            {
                var path = Path.Combine(dir, "bundle.zip");
                SupportBundle.Create(path, new[]
                {
                    new KeyValuePair<string, string>("../说明.txt", "token=secret-value"),
                    new KeyValuePair<string, string>("../说明.txt", "第二份"),
                    new KeyValuePair<string, string>("logs/agent-1.log", "Bearer abc.def.ghi"),
                }, new[] { "secret-value" });

                using var zip = ZipFile.OpenRead(path);
                var names = zip.Entries.Select(e => e.FullName).ToArray();
                Eq(3, names.Length, "三个条目");
                True(names.All(n => !n.Contains("..") && !n.Contains("/")), "条目名不能带目录穿越");
                True(names.Contains("说明.txt"), "第一份保留原名");
                True(names.Contains("说明-2.txt"), "重名加序号");
                True(names.Contains("agent-1.log"), "日志只留文件名");

                using var reader = new StreamReader(zip.GetEntry("说明.txt")!.Open());
                var body = reader.ReadToEnd();
                False(body.Contains("secret-value"), "压缩内容也要脱敏");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });
    }
}