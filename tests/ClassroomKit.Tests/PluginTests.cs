using System.IO.Compression;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Update;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>ClassIsland 插件包（cipx）解包、选包，以及 0.6.0 新增配置段的默认值。</summary>
internal static class PluginTests
{
    public static Task RunAsync() => RunAll();

    private static async Task RunAll()
    {
        await Run("插件清单：根目录布局与单层目录布局都能读出 id/版本", () =>
        {
            var dir = NewDir();
            try
            {
                var root = Path.Combine(dir, "root.cipx");
                MakeZip(root, ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.0\n"));
                var m1 = PluginPackage.ReadManifest(root);
                NotNull(m1, "清单");
                Eq("classroomkit.hotspot", m1!.Id, "id");
                Eq("0.6.0", m1.Version, "版本");

                var nested = Path.Combine(dir, "nested.cipx");
                MakeZip(nested, ("classroomkit.hotspot/manifest.yml", "id: classroomkit.hotspot\r\nversion: \"0.6.1\"\r\n"));
                Eq("0.6.1", PluginPackage.ReadManifest(nested)!.Version, "带引号与 CRLF 的版本");
            }
            finally { Cleanup(dir); }
        });

        await Run("解包插件：覆盖程序集但保留 data/ 用户数据", () =>
        {
            var dir = NewDir();
            try
            {
                var zip = Path.Combine(dir, "p.cipx");
                MakeZip(zip,
                    ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.0\n"),
                    ("ClassroomKit.ClassIsland.dll", "new-dll"),
                    ("README.md", "readme"));
                var target = Path.Combine(dir, "Plugins", "classroomkit.hotspot");
                Directory.CreateDirectory(Path.Combine(target, "data"));
                File.WriteAllText(Path.Combine(target, "data", "lesson-bridge.log"), "keep");

                var result = PluginPackage.ExtractTo(zip, target);
                True(result.Ok, "解包应成功：" + result.Message);
                Eq("new-dll", File.ReadAllText(Path.Combine(target, "ClassroomKit.ClassIsland.dll")), "程序集已更新");
                Eq("readme", File.ReadAllText(Path.Combine(target, "README.md")), "说明文件");
                Eq("keep", File.ReadAllText(Path.Combine(target, "data", "lesson-bridge.log")), "用户数据保留");
            }
            finally { Cleanup(dir); }
        });

        await Run("解包插件：缺清单/缺程序集/路径穿越/绝对路径一律拒绝且不落文件", () =>
        {
            var dir = NewDir();
            try
            {
                var missing = Path.Combine(dir, "missing.cipx");
                MakeZip(missing, ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.0\n"));
                False(PluginPackage.ExtractTo(missing, Path.Combine(dir, "a")).Ok, "缺 dll 应拒绝");

                var traversal = Path.Combine(dir, "traversal.cipx");
                MakeZip(traversal,
                    ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.0\n"),
                    ("ClassroomKit.ClassIsland.dll", "x"),
                    ("../evil.txt", "evil"));
                var target = Path.Combine(dir, "b", "classroomkit.hotspot");
                var bad = PluginPackage.ExtractTo(traversal, target);
                True(bad.Invalid, "路径穿越应判为无效包：" + bad.Message);
                False(File.Exists(Path.Combine(dir, "b", "evil.txt")), "不能在上级目录落文件");
                False(File.Exists(Path.Combine(dir, "evil.txt")), "不能在更上层落文件");

                var absolute = Path.Combine(dir, "absolute.cipx");
                MakeZip(absolute,
                    ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.0\n"),
                    ("ClassroomKit.ClassIsland.dll", "x"),
                    ("/evil.txt", "evil"));
                True(PluginPackage.ExtractTo(absolute, Path.Combine(dir, "c")).Invalid, "绝对路径应判为无效包");
            }
            finally { Cleanup(dir); }
        });

        await Run("解包插件：临时目录不落在 Plugins 里，解压后不留垃圾", () =>
        {
            var dir = NewDir();
            try
            {
                var zip = Path.Combine(dir, "p.cipx");
                MakeZip(zip,
                    ("manifest.yml", "id: classroomkit.hotspot\nversion: 0.6.5\n"),
                    ("ClassroomKit.ClassIsland.dll", "dll"));
                var plugins = Path.Combine(dir, "Plugins");
                var target = Path.Combine(plugins, "classroomkit.hotspot");
                var staging = Path.Combine(dir, "update", "tmp");
                Directory.CreateDirectory(staging);

                var result = PluginPackage.ExtractTo(zip, target, staging);
                True(result.Ok, "解包应成功：" + result.Message);
                Eq(0, Directory.GetDirectories(plugins, ".ckit-plugin-*").Length, "Plugins 里不能留临时目录");
                Eq(0, Directory.GetDirectories(staging, ".ckit-plugin-*").Length, "解压完要清掉临时目录");
            }
            finally { Cleanup(dir); }
        });

        await Run("待装插件：不比本机新的包不装（避免降级）", () =>
        {
            var pending = new PluginManifest("classroomkit.hotspot", "0.6.5");
            True(PluginPackage.IsNewerThanInstalled(pending, "0.6.4"), "0.6.5 比 0.6.4 新");
            False(PluginPackage.IsNewerThanInstalled(pending, "0.6.5"), "同版本不算新");
            False(PluginPackage.IsNewerThanInstalled(pending, "0.6.6"), "旧包不能覆盖新装");
            True(PluginPackage.IsNewerThanInstalled(pending, ""), "读不到已装版本时允许安装");
        });

        await Run("选插件资产：优先同名版本，其次任意 cipx", () =>
        {
            var older = new ReleaseAsset { Name = "ClassroomKit-0.5.1.cipx", Url = "https://x/older" };
            var exact = new ReleaseAsset { Name = "ClassroomKit-0.6.0.cipx", Url = "https://x/exact" };
            var msi = new ReleaseAsset { Name = "ClassroomKit-0.6.0.msi", Url = "https://x/msi" };
            var release = new GitHubRelease { Tag = "v0.6.0", Assets = new[] { older, exact, msi } };
            Eq("https://x/exact", ReleaseFeed.PickPlugin(release)!.Url, "优先带版本号的 cipx");

            var noExact = new GitHubRelease { Tag = "v0.7.0", Assets = new[] { older, msi } };
            Eq("https://x/older", ReleaseFeed.PickPlugin(noExact)!.Url, "退而取第一个 cipx");
            Eq(null, ReleaseFeed.PickPlugin(new GitHubRelease { Tag = "v0.7.0", Assets = new[] { msi } }), "没有 cipx 返回 null");
        });

        await Run("配置 v6 → v7：托盘保存菜单默认精简、插件同步更新默认开", () =>
        {
            var dir = NewDir();
            try
            {
                var path = Path.Combine(dir, "v6.json");
                File.WriteAllText(path, "{ \"schemaVersion\": 6, \"tray\": { \"enabled\": true }, \"update\": { \"checkOnBoot\": true } }");
                var store = new ConfigStore(path);
                Eq(7, store.Current.SchemaVersion, "升到结构 7");
                False(store.Current.Tray.SaveKeepOpen, "「保存，保留 ClassInX」默认收起");
                False(store.Current.Tray.SaveWithoutRename, "「保存（不按课表改名）」默认收起");
                True(store.Current.Tray.ShowPreviewReminder, "预览课前提醒窗默认保留");
                True(store.Current.Update.UpdatePlugin, "升级时同步插件默认开");

                var custom = Path.Combine(dir, "v6-custom.json");
                File.WriteAllText(custom, "{ \"schemaVersion\": 6, \"tray\": { \"saveKeepOpen\": true, \"saveWithoutRename\": true, \"showPreviewReminder\": false }, \"update\": { \"updatePlugin\": false } }");
                var kept = new ConfigStore(custom).Current;
                True(kept.Tray.SaveKeepOpen, "用户打开的托盘项保留");
                True(kept.Tray.SaveWithoutRename, "用户打开的托盘项保留");
                False(kept.Tray.ShowPreviewReminder, "用户关掉的预览保留");
                False(kept.Update.UpdatePlugin, "用户关掉的插件同步保留");
            }
            finally { Cleanup(dir); }
        });
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void MakeZip(string path, params (string Name, string Text)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
    }
}
