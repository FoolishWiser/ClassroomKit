using ClassroomKit.Core.Config;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Onboarding;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>空白配置下的教程引导：步骤内容与「什么时候弹」的判定，都不碰 Windows。</summary>
internal static class OnboardingTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-onboarding-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Task RunAsync() => RunAll();

    private static async Task RunAll()
    {
        await Run("教程引导：四步都写全，页面 key 都是真实页面", () =>
        {
            var steps = OnboardingGuide.Steps;
            Eq(4, steps.Count, "步骤数");
            Eq(steps.Count, steps.Select(x => x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "步骤 key 去重后数量");
            foreach (var step in steps)
            {
                True(step.Key.Length > 0, "步骤 key 不能为空");
                True(step.Title.Length > 0, step.Key + " 的标题不能为空");
                True(step.Body.Length > 0, step.Key + " 的正文不能为空");
                True(step.Action.Length > 0, step.Key + " 的按钮文案不能为空");
                True(OnboardingGuide.IsKnownPage(step.Page), step.Key + " 指向了不存在的页面：" + step.Page);
            }
        });

        await Run("教程引导：没看完就弹，点过完成/跳过才不弹", () =>
        {
            True(OnboardingGuide.ShouldShow(done: false), "没看过（或中途关掉）下次继续弹");
            False(OnboardingGuide.ShouldShow(done: true), "看过一次后不再弹");
        });

        await Run("教程引导：新建配置算空白，字段随配置落盘", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            var store = new ConfigStore(path);
            True(store.CreatedNew, "新建配置应标记为空白配置");
            False(store.Current.Ui.OnboardingDone, "新配置默认没看过教程");
            Eq(ConfigStore.CurrentSchemaVersion, store.Current.SchemaVersion, "schema 不变");
            True(File.ReadAllText(path).Contains("\"onboardingDone\"", StringComparison.OrdinalIgnoreCase),
                "新配置应把 onboardingDone 写进文件");
        });

        await Run("教程引导：重启后保持没看过，点完成才置真", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            _ = new ConfigStore(path);
            var again = new ConfigStore(path);
            False(again.CreatedNew, "已有配置文件不算空白配置");
            False(again.Current.Ui.OnboardingDone, "第一次重启不该自动算看过");

            again.Mutate(c => c.Ui.OnboardingDone = true);
            True(new ConfigStore(path).Current.Ui.OnboardingDone, "点过完成后重启仍是完成状态");
        });

        await Run("教程引导：老版本配置缺字段时按「已看过」处理，不挡老师", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            var legacy = "{\n"
                + "  \"schemaVersion\": 7,\n"
                + "  \"token\": \"0123456789abcdef0123456789abcdef\",\n"
                + "  \"hotspot\": { \"ssid\": \"老教室\", \"passphrase\": \"12345678\" }\n"
                + "}\n";
            File.WriteAllText(path, legacy);
            var store = new ConfigStore(path);
            False(store.CreatedNew, "文件存在就不算空白配置");
            True(store.Current.Ui.OnboardingDone, "老配置应被当作看过教程");
            Eq("老教室", store.Current.Hotspot.Ssid, "迁移不该改已有热点名");
            True(File.ReadAllText(path).Contains("\"onboardingDone\"", StringComparison.OrdinalIgnoreCase),
                "老配置应补写 onboardingDone 字段，避免下次再判定");
        });

        await Run("教程引导：配置坏掉按空白配置处理，会重新引导", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            File.WriteAllText(path, "{ 这不是 JSON");
            var store = new ConfigStore(path);
            True(store.CreatedNew, "坏配置应重建为空白配置");
            False(store.Current.Ui.OnboardingDone, "重建后应重新引导");
            Eq(ConfigStore.CurrentSchemaVersion, store.Current.SchemaVersion, "重建后的 schema");
        });
    }
}
