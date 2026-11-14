using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClassroomKit.Core.Api;
using ClassroomKit.Core.Client;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

internal static class Program
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("ClassroomKit 测试（纯逻辑，不需要 Windows）");

        Console.WriteLine();
        Console.WriteLine("[ConfigStore]");
        await ConfigTestsAsync();

        Console.WriteLine();
        Console.WriteLine("[上行网卡选择]");
        await UplinkTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[板书冲突与配方]");
        await BoardTests.RunAsync();
        await RecipeTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[GitHub 升级]");
        await UpdateTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[HotspotGuard 状态机]");
        await GuardTestsAsync();

        Console.WriteLine();
        Console.WriteLine("[hosts 加速]");
        await HostsTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[升级时间窗]");
        await InstallWindowTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[板书冲突提示窗]");
        await ConflictDialogTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[课表与课前提醒]");
        await LessonTests.RunAsync();

        Console.WriteLine();
        Console.WriteLine("[HTTP API]");
        await ApiTestsAsync();

        return Report();
    }

    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 8, 0, 0, TimeSpan.FromHours(8));
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed record Fixture(HotspotGuard Guard, FakeTethering Fake, AppConfig Config, Clock Clock);

    private static Fixture NewGuard()
    {
        var fake = new FakeTethering();
        var cfg = ConfigStore.CreateDefault();
        var clock = new Clock();
        var guard = new HotspotGuard(fake, () => cfg, new AppLog(null), () => clock.Now);
        return new Fixture(guard, fake, cfg, clock);
    }

    // ---------------------------------------------------------------- 配置

    private static async Task ConfigTestsAsync()
    {
        await Run("新建配置会落盘并带默认值", () =>
        {
            var path = TempFile("config.json");
            var store = new ConfigStore(path);
            True(File.Exists(path), "config.json 应被创建");
            var cfg = store.Current;
            Eq(32, cfg.Token.Length, "令牌长度");
            True(cfg.Token.All(c => "0123456789abcdef".Contains(c)), "令牌应为小写十六进制");
            Eq(36037, cfg.Port, "默认端口");
            Eq(10, cfg.Hotspot.TargetClients, "默认目标设备数");
            Eq(16, cfg.Hotspot.RegistryPeers, "默认注册表放宽值");
            True(cfg.Hotspot.Enabled, "默认启用热点");
        });

        await Run("令牌在重启后保持不变", () =>
        {
            var path = TempFile("config.json");
            var token = new ConfigStore(path).Current.Token;
            Eq(token, new ConfigStore(path).Current.Token, "重新加载后的令牌");
            Eq(token, ConfigStore.Load(path)!.Token, "静态 Load 的令牌");
        });

        await Run("越界值会被规范化", () =>
        {
            var path = TempFile("config.json");
            File.WriteAllText(path, """
            {
              "port": 80,
              "token": "",
              "hotspot": { "guardIntervalSec": 1, "targetClients": 0 }
            }
            """);
            var cfg = ConfigStore.Load(path)!;
            Eq(36037, cfg.Port, "端口越界应回落 36037");
            Eq(3, cfg.Hotspot.GuardIntervalSec, "巡检间隔下限 3 秒");
            Eq(10, cfg.Hotspot.TargetClients, "目标设备数下限 10");
            Eq(32, cfg.Token.Length, "空令牌应重新生成");
        });

        await Run("板书保存默认会自动拉起 ClassInX", () =>
        {
            var cfg = ConfigStore.CreateDefault();
            True(cfg.BoardSave.Enabled, "默认启用板书保存");
            True(cfg.BoardSave.CloseAfterSave, "默认保存后关闭 ClassInX");
            True(cfg.BoardSave.LaunchIfNeeded, "默认没开就自动拉起");
            Eq(60, cfg.BoardSave.LaunchTimeoutSec, "默认等板书窗口 60 秒");
            Eq<string?>(null, cfg.BoardSave.ClassInPath, "默认自动查找路径");
        });

        await Run("板书等待超时被钳到 5..600 秒", () =>
        {
            var path = TempFile("config.json");

            File.WriteAllText(path, "{ \"boardSave\": { \"launchTimeoutSec\": 1 } }");
            Eq(60, ConfigStore.Load(path)!.BoardSave.LaunchTimeoutSec, "过小回落到 60 秒");

            File.WriteAllText(path, "{ \"boardSave\": { \"launchTimeoutSec\": 9999 } }");
            Eq(60, ConfigStore.Load(path)!.BoardSave.LaunchTimeoutSec, "过大回落到 60 秒");

            File.WriteAllText(path, "{ \"boardSave\": { \"launchTimeoutSec\": 180, \"classInPath\": \"D:\\\\\\\\Apps\\\\\\\\ClassIn X\\\\\\\\ClassIn X.exe\" } }");
            var cfg = ConfigStore.Load(path)!;
            Eq(180, cfg.BoardSave.LaunchTimeoutSec, "合法值保留");
            Contains(cfg.BoardSave.ClassInPath ?? "", "ClassIn X.exe", "手工路径保留");
        });

        await Run("坏 JSON 回退默认且能恢复", () =>
        {
            var path = TempFile("config.json");
            File.WriteAllText(path, "{ 这不是 JSON");
            Eq<AppConfig?>(null, ConfigStore.Load(path), "坏 JSON 应返回 null");
            var store = new ConfigStore(path);
            Eq(36037, store.Current.Port, "坏 JSON 应回退默认配置");
            NotNull(ConfigStore.Load(path), "坏文件应被重写为合法配置");
        });

        await Run("Update 修改并写回磁盘", () =>
        {
            var path = TempFile("config.json");
            var store = new ConfigStore(path);
            store.Update(c =>
            {
                c.Hotspot.Ssid = "LAB-9";
                c.Hotspot.TargetClients = 12;
                return 0;
            });
            Eq("LAB-9", ConfigStore.Load(path)!.Hotspot.Ssid, "磁盘上的 SSID");
            Eq(12, ConfigStore.Load(path)!.Hotspot.TargetClients, "磁盘上的目标设备数");
            Contains(File.ReadAllText(path), "\"ssid\"", "写盘使用 camelCase");
        });

        await Run("新装机器用计算机名当热点名、随机 8 位密码", () =>
        {
            var cfg = ConfigStore.CreateDefault();
            Eq(ConfigStore.CurrentSchemaVersion, cfg.SchemaVersion, "新配置直接是新结构");
            True(cfg.Hotspot.Ssid.Length > 0, "热点名不应为空");
            Eq(MachineDefaults.DefaultSsid(), cfg.Hotspot.Ssid, "热点名默认取计算机名");
            Eq(8, cfg.Hotspot.Passphrase.Length, "密码 8 位");
            True(cfg.Hotspot.Passphrase.All(c => "abcdefghijkmnpqrstuvwxyz23456789".Contains(c)), "密码只用易辨认字符");
        });

        await Run("长中文计算机名按 UTF-8 32 字节截断，不切坏汉字", () =>
        {
            var cut = MachineDefaults.TruncateUtf8(new string('教', 20), MachineDefaults.MaxSsidBytes);
            True(System.Text.Encoding.UTF8.GetByteCount(cut) <= 32, "字节数不超 32");
            Eq(10, cut.Length, "32 字节正好放 10 个汉字");
            Eq("", MachineDefaults.TruncateUtf8("教室", 0), "预算为 0 时返回空");
            Eq("教室", MachineDefaults.TruncateUtf8("教室", 6), "刚好放下时不截断");
        });

        await Run("旧默认值只在成对出现时才判定", () =>
        {
            True(MachineDefaults.IsLegacy("CLASSROOM-302", "80258025"), "识别旧默认值");
            False(MachineDefaults.IsLegacy("CLASSROOM-302", "other-pass"), "只认成对出现");
            False(MachineDefaults.IsLegacy("ROOM-1", "80258025"), "自己改过名字就不算旧默认");
        });

        await Run("旧配置迁移：只盖章，不改热点名/密码", () =>
        {
            var path = TempFile("legacy.json");
            File.WriteAllText(path, "{ \"token\": \"abc\", \"hotspot\": { \"ssid\": \"CLASSROOM-302\", \"passphrase\": \"80258025\" } }");
            var store = new ConfigStore(path);
            Eq(ConfigStore.CurrentSchemaVersion, store.Current.SchemaVersion, "迁移后结构版本");
            Eq(0, store.MigratedFrom, "记录迁移来源");
            Eq("CLASSROOM-302", store.Current.Hotspot.Ssid, "旧 SSID 原样保留");
            Eq("80258025", store.Current.Hotspot.Passphrase, "旧密码原样保留");
            Eq("abc", store.Current.Token, "其它字段不动");
            Eq(ConfigStore.CurrentSchemaVersion, ConfigStore.Load(path)!.SchemaVersion, "迁移结果已落盘");
        });

        await Run("v3 → v4：旧的默认安装窗口改成不限，用户自填的窗口保留", () =>
        {
            var legacy = TempFile("v3-legacy.json");
            File.WriteAllText(legacy, "{ \"schemaVersion\": 3, \"update\": { \"installWindow\": \"02:00-05:00\" } }");
            var store = new ConfigStore(legacy);
            Eq(4, store.Current.SchemaVersion, "升到 4");
            Eq("", store.Current.Update.InstallWindow, "0.3.x 的默认窗口改成不限，开机就能装");

            var custom = TempFile("v3-custom.json");
            File.WriteAllText(custom, "{ \"schemaVersion\": 3, \"update\": { \"installWindow\": \"23:00-06:00\" } }");
            Eq("23:00-06:00", new ConfigStore(custom).Current.Update.InstallWindow, "用户自己填的窗口不动");
        });

        await Run("已是新结构的配置不会被再次迁移", () =>
        {
            var path = TempFile("config.json");
            var store = new ConfigStore(path);
            store.Update(c => { c.Hotspot.Ssid = "ROOM-1"; return 0; });
            var again = new ConfigStore(path);
            Eq(0, again.MigratedFrom, "无需迁移");
            Eq("ROOM-1", again.Current.Hotspot.Ssid, "SSID 保持用户值");
        });
    }

    // ---------------------------------------------------------------- 状态机

    private static async Task GuardTestsAsync()
    {
        await Run("没有上行时等待，不去开热点", async () =>
        {
            var f = NewGuard();
            f.Fake.UplinkAvailable = false;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.WaitUplink, f.Guard.State, "状态");
            Eq(0, f.Fake.StartCalls, "启动次数");
        });

        await Run("上行是 Wi-Fi 时拒绝开热点", async () =>
        {
            var f = NewGuard();
            f.Fake.UplinkIsWifi = true;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.BlockedWifiUplink, f.Guard.State, "状态");
            Eq(0, f.Fake.StartCalls, "启动次数");
        });

        await Run("允许 Wi-Fi 上行后正常开启", async () =>
        {
            var f = NewGuard();
            f.Fake.UplinkIsWifi = true;
            f.Config.Hotspot.AllowWifiUplink = true;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.On, f.Guard.State, "状态");
            Eq(1, f.Fake.StartCalls, "启动次数");
        });

        await Run("策略关时把已开的热点关掉", async () =>
        {
            var f = NewGuard();
            f.Fake.State = OperationalState.On;
            f.Config.Hotspot.Enabled = false;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.Disabled, f.Guard.State, "状态");
            Eq(1, f.Fake.StopCalls, "关闭次数");
            Eq(0, f.Fake.StartCalls, "关闭后不应再启动");
            Eq(OperationalState.Off, f.Guard.LastSnapshot!.OperationalState, "关热点后立刻重读的快照");
        });

        await Run("热点已开时重复巡检不重复启动", async () =>
        {
            var f = NewGuard();
            await f.Guard.TickOnceAsync(Ct);
            await f.Guard.TickOnceAsync(Ct);
            await f.Guard.TickOnceAsync(Ct);
            Eq(1, f.Fake.StartCalls, "启动次数");
            Eq(GuardState.On, f.Guard.State, "状态");
            Eq(1, f.Guard.RestartCount, "重启计数");
            NotNull(f.Guard.LastSuccess, "上次成功时间");
        });

        await Run("启动失败后按 60/300 秒退避", async () =>
        {
            var f = NewGuard();
            f.Fake.StartSucceeds = false;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.Backoff, f.Guard.State, "首次失败状态");
            Eq(1, f.Fake.StartCalls, "启动次数");
            Contains(f.Guard.LastError ?? "", "开启热点失败", "失败原因");

            f.Clock.Advance(30);
            await f.Guard.TickOnceAsync(Ct);
            Eq(1, f.Fake.StartCalls, "退避期内不应再试");

            f.Clock.Advance(31);
            await f.Guard.TickOnceAsync(Ct);
            Eq(2, f.Fake.StartCalls, "60 秒后应重试");

            f.Clock.Advance(100);
            await f.Guard.TickOnceAsync(Ct);
            Eq(2, f.Fake.StartCalls, "第二次退避 300 秒内不应再试");
        });

        await Run("容量不足时告警，恢复后清除", async () =>
        {
            var f = NewGuard();
            f.Config.Hotspot.TargetClients = 10;
            f.Fake.MaxClients = 8;
            await f.Guard.TickOnceAsync(Ct);
            Contains(f.Guard.CapabilityWarning ?? "", "系统上限 8 台", "告警内容");
            Contains(f.Guard.CapabilityWarning ?? "", "目标 10 台", "告警内容");

            f.Fake.MaxClients = 16;
            await f.Guard.TickOnceAsync(Ct);
            Eq<string?>(null, f.Guard.CapabilityWarning, "上限够了应清除告警");
        });

        await Run("发现无设备自动关已开启时立刻关掉", async () =>
        {
            var f = NewGuard();
            f.Fake.NoConnectionsTimeout = true;
            await f.Guard.TickOnceAsync(Ct);
            Eq(1, f.Fake.TimeoutFixCalls, "修复次数");
            await f.Guard.TickOnceAsync(Ct);
            Eq(1, f.Fake.TimeoutFixCalls, "已修复后不应重复调用");
        });

        await Run("SSID 被改掉会自动改回", async () =>
        {
            var f = NewGuard();
            f.Fake.State = OperationalState.On;
            f.Fake.Ssid = "别人改的名字";
            await f.Guard.TickOnceAsync(Ct);
            Eq(1, f.Fake.ConfigureCalls, "覆盖配置次数");
            Eq(f.Config.Hotspot.Ssid, f.Fake.LastSsid, "改回的 SSID");
            Eq(GuardState.On, f.Guard.State, "状态");
        });

        await Run("读取状态抛异常时进入 Failed", async () =>
        {
            var f = NewGuard();
            f.Fake.ThrowOnSnapshot = true;
            await f.Guard.TickOnceAsync(Ct);
            Eq(GuardState.Failed, f.Guard.State, "状态");
            Contains(f.Guard.LastError ?? "", "网卡被占用", "错误信息");
            Eq(0, f.Fake.StartCalls, "不该尝试启动");
        });
    }

    // ---------------------------------------------------------------- HTTP

    private static async Task ApiTestsAsync()
    {
        await Run("HTTP 端到端（MiniHttp + ApiServer）", async () =>
        {
            var dir = TempDir();
            File.WriteAllText(Path.Combine(dir, "index.html"), "<h1>面板</h1>");
            var store = new ConfigStore(Path.Combine(dir, "config.json"));
            var log = new AppLog(null);
            log.Info("测试日志一");
            log.Info("测试日志二");
            var token = store.Current.Token;

            var api = new ApiServer(store, log, dir, "0.1.0-test");
            string? policy = null;
            ApiServer.UserSettings? settings = null;
            api.ApplyPolicy = enabled => policy = enabled ? "on" : "off";
            api.ApplySettings = s => settings = s;

            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            await using var server = new MiniHttpServer(port, api.HandleAsync, log);
            server.Start();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var health = await http.GetAsync($"/api/health?t={token}");
            Eq(200, (int)health.StatusCode, "健康检查状态码");
            Contains(await health.Content.ReadAsStringAsync(), "0.1.0-test", "健康检查版本号");

            Eq(401, (int)(await http.GetAsync("/api/state")).StatusCode, "无令牌应 401");
            Eq(401, (int)(await http.GetAsync("/api/state?t=wrong")).StatusCode, "错令牌应 401");

            var state = await http.GetAsync($"/api/state?t={token}");
            var stateJson = await state.Content.ReadAsStringAsync();
            Eq(200, (int)state.StatusCode, "状态码");
            using (var doc = JsonDocument.Parse(stateJson))
            {
                Eq(MachineDefaults.DefaultSsid(), doc.RootElement.GetProperty("hotspot").GetProperty("ssid").GetString(), "SSID 默认取计算机名");
                Eq("Unknown", doc.RootElement.GetProperty("guardState").GetString(), "守护状态");
            }

            var logs = await http.GetAsync($"/api/logs?take=5&t={token}");
            Contains(await logs.Content.ReadAsStringAsync(), "测试日志二", "日志内容");

            using (var stopReq = new HttpRequestMessage(HttpMethod.Post, $"/api/action?t={token}"))
            {
                stopReq.Content = new StringContent("action=stop", Encoding.UTF8, "application/x-www-form-urlencoded");
                var stop = await http.SendAsync(stopReq);
                Eq(200, (int)stop.StatusCode, "表单动作状态码");
                Contains(await stop.Content.ReadAsStringAsync(), "已关闭", "表单动作返回");
                Eq("off", policy, "表单动作应传到守护");
            }

            using (var startReq = new HttpRequestMessage(HttpMethod.Post, "/api/action"))
            {
                startReq.Headers.Add("X-Token", token);
                startReq.Content = new StringContent("{\"action\":\"start\"}", Encoding.UTF8, "application/json");
                var start = await http.SendAsync(startReq);
                Eq(200, (int)start.StatusCode, "JSON 动作状态码");
                Eq("on", policy, "JSON 动作应传到守护");
            }

            using (var client = new ClassroomKitClient($"http://127.0.0.1:{port}", token))
            {
                var dto = await client.GetStateAsync();
                Eq("0.1.0-test", dto.Version, "客户端读到版本");
                Eq(MachineDefaults.DefaultSsid(), dto.Hotspot.Ssid, "客户端读到 SSID");
                Eq(ConfigStore.CurrentSchemaVersion, dto.ConfigSchema, "客户端读到结构版本");
                False(dto.LegacyDefaults, "新配置不算旧默认值");

                var reply = await client.PostActionAsync("stop");
                True(reply.Ok, "动作返回 ok");
                Eq("off", policy, "客户端动作传到守护");

                True(await client.PingAsync(), "Ping 通过");

                var rename = await client.PostActionAsync("rename-ssid");
                True(rename.Ok, "改名动作返回 ok");
                Eq(MachineDefaults.DefaultSsid(), rename.Ssid, "默认改成计算机名");
                Eq(MachineDefaults.DefaultSsid(), store.Current.Hotspot.Ssid, "改名落到配置");

                using var bad = new ClassroomKitClient($"http://127.0.0.1:{port}", "wrong");
                var (badState, error) = await bad.TryGetStateAsync();
                Eq<AgentState?>(null, badState, "错令牌拿不到状态");
                Contains(error ?? "", "401", "错令牌错误文案");
                False(await bad.PingAsync(), "错令牌 Ping 失败");
            }

            using (var badReq = new HttpRequestMessage(HttpMethod.Post, $"/api/action?t={token}"))
            {
                badReq.Content = new StringContent("action=no-such-thing", Encoding.UTF8, "application/x-www-form-urlencoded");
                var bad = await http.SendAsync(badReq);
                Eq(400, (int)bad.StatusCode, "未知动作状态码");
                Contains(await bad.Content.ReadAsStringAsync(), "unknown action", "未知动作提示");
            }

            using (var setReq = new HttpRequestMessage(HttpMethod.Post, $"/api/settings?t={token}"))
            {
                setReq.Content = new StringContent(
                    "{\"ssid\":\"LAB-2\",\"passphrase\":\"12345678\",\"targetClients\":12,\"allowWifiUplink\":true}",
                    Encoding.UTF8, "application/json");
                var set = await http.SendAsync(setReq);
                Eq(200, (int)set.StatusCode, "保存设置状态码");
                NotNull(settings, "设置应传到守护");
                Eq("LAB-2", settings!.Ssid, "SSID");
                Eq(12, settings.TargetClients, "目标设备数");
            }

            using (var badReq = new HttpRequestMessage(HttpMethod.Post, $"/api/settings?t={token}"))
            {
                badReq.Content = new StringContent(
                    "{\"ssid\":\"x\",\"passphrase\":\"123\"}", Encoding.UTF8, "application/json");
                var bad = await http.SendAsync(badReq);
                Eq(400, (int)bad.StatusCode, "非法 SSID/密码应被拒");
            }

            var index = await http.GetAsync("/");
            Eq(200, (int)index.StatusCode, "首页状态码");
            Contains(await index.Content.ReadAsStringAsync(), "面板", "首页内容");

            var missing = await http.GetAsync("/no-such-file");
            Eq(404, (int)missing.StatusCode, "缺失静态文件应 404");
        });
    }

    // ---------------------------------------------------------------- 工具

    private static string _tempRoot = "";

    private static string TempRoot =>
        _tempRoot.Length > 0
            ? _tempRoot
            : _tempRoot = CreateTempRoot();

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "classroomkit-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempFile(string name) => Path.Combine(TempDir(), name);
}
