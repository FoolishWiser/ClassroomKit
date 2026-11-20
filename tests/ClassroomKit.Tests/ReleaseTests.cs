using ClassroomKit.Core.About;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Startup;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>0.8.0 新增的纯逻辑：板书文件完整性、启动参数、关于页地址、课间跳过清单。</summary>
internal static class ReleaseTests
{
    public static Task RunAsync()
    {
        BoardFileChecks();
        StartupFlags();
        AboutLinks();
        SkipBreaks();
        return Task.CompletedTask;
    }

    private static void BoardFileChecks()
    {
        var dir = TempDir();

        Run("板书文件：不存在或空文件都算没写完", () =>
        {
            False(BoardFileCheck.LooksComplete(null), "null 不算完成");
            False(BoardFileCheck.LooksComplete(Path.Combine(dir, "没有这个文件.pdf")), "不存在不算完成");

            var empty = Path.Combine(dir, "空.pdf");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            False(BoardFileCheck.LooksComplete(empty), "0 字节不算完成");

            var tiny = Path.Combine(dir, "半截.pdf");
            File.WriteAllBytes(tiny, new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' });
            False(BoardFileCheck.LooksComplete(tiny), "只有 5 字节文件头不算完成");
        });

        Run("板书文件：不是 PDF 的扩展名不校验文件头", () =>
        {
            var png = Path.Combine(dir, "板书.png");
            File.WriteAllBytes(png, Enumerable.Repeat((byte)0x89, 4096).ToArray());
            True(BoardFileCheck.LooksComplete(png), "PNG 大小够就算完成");
        });

        Run("板书文件：PDF 要够大且以 %PDF- 开头", () =>
        {
            var bad = Path.Combine(dir, "假.pdf");
            File.WriteAllBytes(bad, Enumerable.Repeat((byte)'x', 4096).ToArray());
            False(BoardFileCheck.LooksComplete(bad), "文件头不对不算完成");

            var good = Path.Combine(dir, "好.pdf");
            var bytes = new List<byte> { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' };
            bytes.AddRange(Enumerable.Repeat((byte)' ', 4096));
            File.WriteAllBytes(good, bytes.ToArray());
            True(BoardFileCheck.LooksComplete(good), "正常 PDF 头 + 大小足够");
        });
    }

    private static void StartupFlags()
    {
        Run("启动参数：--tray / --autostart 都只到托盘", () =>
        {
            True(StartupArgs.IsTrayOnly(new[] { "--tray" }), "--tray");
            True(StartupArgs.IsTrayOnly(new[] { "--TRAY" }), "大小写不敏感");
            True(StartupArgs.IsTrayOnly(new[] { "run", "--tray" }), "任意位置");
            True(StartupArgs.IsTrayOnly(new[] { "--autostart" }), "开机自启也只到托盘");
            False(StartupArgs.IsTrayOnly(Array.Empty<string>()), "无参数按设置走");
            False(StartupArgs.IsTrayOnly(null), "null 安全");
            False(StartupArgs.IsTrayOnly(new[] { "--run" }), "普通启动不算");
        });
    }

    private static void AboutLinks()
    {
        Run("关于页地址：仓库、下载、Star、API", () =>
        {
            Eq("https://github.com/FoolishWiser/ClassroomKit", AboutInfo.RepoUrl(AboutInfo.DefaultRepo));
            Eq("https://github.com/FoolishWiser/ClassroomKit/releases/latest", AboutInfo.ReleasesUrl(AboutInfo.DefaultRepo));
            Eq("https://github.com/FoolishWiser/ClassroomKit/stargazers", AboutInfo.StarsUrl(AboutInfo.DefaultRepo));
            Eq("https://api.github.com/repos/FoolishWiser/ClassroomKit", AboutInfo.ApiUrl(AboutInfo.DefaultRepo));
            Eq("https://github.com/FoolishWiser/ClassroomKit", AboutInfo.RepoUrl(" /FoolishWiser/ClassroomKit/ "), "两端斜杠与空白被清理");
            Contains(AboutInfo.ClassIslandSiteUrl, "classisland.tech");
        });

        Run("关于页 Star 数展示", () =>
        {
            Eq("—", AboutInfo.FormatStars(null));
            Eq("—", AboutInfo.FormatStars(-3));
            Eq("0", AboutInfo.FormatStars(0));
            Eq("999", AboutInfo.FormatStars(999));
            Eq("1.5k", AboutInfo.FormatStars(1500));
            Eq("15k", AboutInfo.FormatStars(15000));
        });
    }

    private static void SkipBreaks()
    {
        Run("课间跳过清单：去空白、去重、忽略大小写、上限 50", () =>
        {
            var cfg = new LessonConfig
            {
                SkipBreakNames = new List<string> { " 高一礼让 ", "", "高一礼让", "跑操", null!, "  升旗 " }
            };
            cfg.Normalize();
            Eq(3, cfg.SkipBreakNames.Count, "重复与空白项被丢弃");
            Eq("高一礼让", cfg.SkipBreakNames[0], "保留第一次出现的写法");
            Eq("跑操", cfg.SkipBreakNames[1]);
            Eq("升旗", cfg.SkipBreakNames[2], "两端空白去掉");
        });

        Run("课间跳过清单：null 归一为空列表，超长截断 50", () =>
        {
            var cfg = new LessonConfig { SkipBreakNames = null! };
            cfg.Normalize();
            Eq(0, cfg.SkipBreakNames.Count, "null 变空列表");

            var many = new LessonConfig();
            for (var i = 0; i < 60; i++) many.SkipBreakNames.Add("课间" + i);
            many.Normalize();
            Eq(50, many.SkipBreakNames.Count, "最多保留 50 个");
        });
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-release-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
