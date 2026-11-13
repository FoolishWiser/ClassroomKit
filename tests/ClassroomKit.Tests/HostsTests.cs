using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hosts;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>hosts 加速的纯逻辑：区块合并/还原、候选解析、挑最优、缓存、旧配置补默认值。</summary>
internal static class HostsTests
{
    private static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), "ck-tests-" + Guid.NewGuid().ToString("N")[..8], name);

    public static async Task RunAsync()
    {
        await Run("Merge 把区块置顶并保留原有内容（CRLF 也不变）", () =>
        {
            const string original = "127.0.0.1 localhost\r\n\r\n# 我的记录\r\n10.0.0.5 nas.local\r\n";
            var merged = HostsBlock.Merge(original, new[]
            {
                new HostEntry("140.82.113.4", new[] { "github.com", "api.github.com" }),
                new HostEntry("185.199.108.133", new[] { "raw.githubusercontent.com" })
            });

            True(merged.StartsWith(HostsBlock.StartMarker, StringComparison.Ordinal), "区块应在文件最前面");
            Contains(merged, "140.82.113.4 github.com api.github.com", "IP 与域名应同行");
            Contains(merged, "10.0.0.5 nas.local", "原有记录应保留");
            Contains(merged, "\r\n", "应沿用原来的 CRLF");
            Eq(1, CountOccurrences(merged, HostsBlock.StartMarker), "只应有一个受管区块");
        });

        await Run("Merge 会替换旧区块而不是叠加", () =>
        {
            var first = HostsBlock.Merge("127.0.0.1 localhost\n", new[] { new HostEntry("1.1.1.1", new[] { "github.com" }) });
            var second = HostsBlock.Merge(first, new[] { new HostEntry("2.2.2.2", new[] { "github.com" }) });

            Eq(1, CountOccurrences(second, HostsBlock.StartMarker), "仍只应有一个区块");
            Contains(second, "2.2.2.2 github.com", "应是新记录");
            False(second.Contains("1.1.1.1", StringComparison.Ordinal), "旧记录应被替换");
            Contains(second, "127.0.0.1 localhost", "用户原有记录应保留");
        });

        await Run("Restore 删除区块；残缺区块切到文件末尾", () =>
        {
            const string original = "127.0.0.1 localhost\n";
            var merged = HostsBlock.Merge(original, new[] { new HostEntry("1.1.1.1", new[] { "github.com" }) });
            var restored = HostsBlock.Restore(merged);

            False(HostsBlock.HasBlock(restored), "还原后不应再有区块");
            Contains(restored, "127.0.0.1 localhost", "原有记录应保留");

            var broken = original + HostsBlock.StartMarker + "\n1.1.1.1 github.com\n";
            var fixedUp = HostsBlock.Restore(broken);
            False(HostsBlock.HasBlock(fixedUp), "残缺区块也应被清掉");
            Contains(fixedUp, "127.0.0.1 localhost", "残缺块之前的内容应保留");
        });

        await Run("Extract 只读受管区块，忽略注释与 IPv6", () =>
        {
            var text = HostsBlock.Merge("# 用户自己的\n::1 localhost\n", new[]
            {
                new HostEntry("140.82.113.4", new[] { "github.com" })
            });
            var entries = HostsBlock.Extract(text);

            Eq(1, entries.Count, "区块里应只有一条");
            Eq("140.82.113.4", entries[0].Ip, "IP");
            Eq("github.com", entries[0].Hosts[0], "域名");

            var parsed = HostsBlock.ParseEntries("::1 localhost\n# 注释\n1.2.3.4 a.com b.com\n乱码\n");
            Eq(1, parsed.Count, "非 IPv4 与注释应被忽略");
            Eq(2, parsed[0].Hosts.Count, "一行可对应多个域名");
        });

        await Run("SelectEntries 挑时延最低、不可达退回源里第一条、同 IP 合并", () =>
        {
            var candidates = new Dictionary<string, List<string>>
            {
                ["github.com"] = new() { "1.1.1.1", "2.2.2.2" },
                ["api.github.com"] = new() { "2.2.2.2", "3.3.3.3" },
                ["raw.githubusercontent.com"] = new() { "9.9.9.9" }
            };
            var latency = new Dictionary<string, int>
            {
                ["github.com|1.1.1.1"] = 300,
                ["github.com|2.2.2.2"] = 80,
                ["api.github.com|2.2.2.2"] = 70,
                ["api.github.com|3.3.3.3"] = 200,
                ["raw.githubusercontent.com|9.9.9.9"] = -1
            };

            var entries = HostsAccelerator.SelectEntries(
                new[] { "github.com", "api.github.com", "raw.githubusercontent.com" },
                candidates, latency);

            Eq(2, entries.Count, "同 IP 的域名应合并成一条");
            var fast = entries.First(e => e.Ip == "2.2.2.2");
            Eq(2, fast.Hosts.Count, "2.2.2.2 应对应两个域名");
            True(entries.Any(e => e.Ip == "9.9.9.9"), "全部不通时应退回源里第一条");
            False(entries.Any(e => e.Ip == "3.3.3.3"), "不应选更慢的候选");
        });

        await Run("ParseCandidates 只保留关心的域名并去重", () =>
        {
            const string text = "140.82.113.4 github.com api.github.com\n140.82.113.4 github.com\n1.2.3.4 example.com\n";
            var parsed = HostsAccelerator.ParseCandidates(text, new[] { "github.com", "api.github.com" });

            Eq(2, parsed.Count, "应解析出两个域名");
            Eq(1, parsed["github.com"].Count, "同一域名重复 IP 应去重");
            False(parsed.ContainsKey("example.com"), "不关心的域名应被丢弃");
        });

        await Run("HostsCache 能存能读，空缓存返回 null", () =>
        {
            var path = TempFile("hosts-cache.json");
            var cache = new HostsCache(path);
            True(cache.Load() is null, "没有缓存文件时应在 null");

            var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(8));
            cache.Save(new[] { new HostEntry("140.82.113.4", new[] { "github.com" }) }, "https://example/hosts", at);

            var loaded = cache.Load();
            NotNull(loaded, "缓存应能读回");
            Eq("140.82.113.4", loaded!.Entries[0].Ip, "IP");
            Eq("https://example/hosts", loaded.Source, "来源");
            Eq(at, loaded.At, "时间");
        });

        await Run("旧 config.json 没有 hosts 字段时补默认值", () =>
        {
            var path = TempFile("config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"schemaVersion\":1,\"token\":\"abc\",\"hotspot\":{\"ssid\":\"CLASSROOM-302\",\"passphrase\":\"80258025\"}}");

            var cfg = ConfigStore.Load(path);
            NotNull(cfg, "应能加载");
            True(cfg!.Hosts.Enabled, "hosts 加速应默认开启");
            Eq(24, cfg.Hosts.RefreshHours, "默认刷新间隔");
            Eq(HostsAccelerator.DefaultSources.Count, cfg.Hosts.Sources.Count, "应补上默认源");
            Eq("CLASSROOM-302", cfg.Hotspot.Ssid, "旧热点名不应被改动");
        });
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
