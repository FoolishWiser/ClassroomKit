using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Weici;

namespace ClassroomKit.Agent.Weici;

/// <summary>
/// 维词自动领读：拉起/连接 Electron 的调试端口，注入 automation.js，再调用
/// window.__WAR.start(首词, 尾词, {pron, loop, dryRun})。
/// 只操作维词页面本身，不读取账号、密码，也不碰其它窗口。
/// </summary>
public sealed class WeiciRunner
{
    public const string DefaultAppPath = @"C:\Program Files\weici-teaching\weici-teaching.exe";
    private readonly AppLog _log;

    public WeiciRunner(AppLog log) => _log = log;

    public async Task<WeiciResult> RunAsync(WeiciConfig config, Action<string> log, CancellationToken ct)
    {
        var app = EnsureApp(config, log, ct);
        if (app is not null) return app;

        var list = await WeiciCdp.ListAsync(config.DebugPort, ct).ConfigureAwait(false);
        var targets = WeiciCdp.Targets(list);
        if (targets.Count == 0) return WeiciResult.Fail("NO_TARGET", "找不到维词窗口");

        var js = LoadScript();
        var primary = new List<string>();
        var navigable = new List<string>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            using var cdp = new WeiciCdp();
            try
            {
                await cdp.ConnectAsync(target.WebSocketUrl, ct).ConfigureAwait(false);
                await cdp.EvalAsync(js, false, 8000, ct).ConfigureAwait(false);
                var probe = await cdp.EvalAsync("window.__WAR.probe()", false, 5000, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(probe);
                var root = doc.RootElement;
                var hasParent = root.TryGetProperty("hasParent", out var hp) && hp.ValueKind == JsonValueKind.True;
                var atLogin = root.TryGetProperty("atLogin", out var al) && al.ValueKind == JsonValueKind.True;
                var width = root.TryGetProperty("w", out var w) && w.TryGetInt32(out var wi) ? wi : 0;
                log($"窗口 宽={width} 状态={(hasParent ? "维克多页面" : atLogin ? "登录/主页" : "其他页面，跳过")}");
                if (hasParent) primary.Add(target.WebSocketUrl);
                else if (atLogin) navigable.Add(target.WebSocketUrl);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Info("维词窗口探测失败：" + ex.Message);
            }
        }

        var order = primary.Concat(navigable).ToList();
        if (order.Count == 0) return WeiciResult.Fail("NO_MAIN_WINDOW", "没有找到可操作的维词主界面");

        var expression = "(async function(){ var r = await window.__WAR.start(" +
            JsonSerializer.Serialize(config.FirstWord) + "," +
            JsonSerializer.Serialize(config.LastWord) + ",{" +
            "pron:" + JsonSerializer.Serialize(config.Pron) + "," +
            "loop:" + JsonSerializer.Serialize(config.Loop) + "," +
            "dryRun:" + (config.DryRun ? "true" : "false") + "}); return JSON.stringify(r); })()";

        Exception? last = null;
        foreach (var ws in order)
        {
            ct.ThrowIfCancellationRequested();
            using var cdp = new WeiciCdp();
            try
            {
                await cdp.ConnectAsync(ws, ct).ConfigureAwait(false);
                await cdp.EvalAsync(js, false, 8000, ct).ConfigureAwait(false);
                var raw = await cdp.EvalAsync(expression, true, 240000, ct).ConfigureAwait(false);
                var parsed = Parse(raw);
                if (parsed.Code is "NEED_LOGIN" or "NO_VICTOR_PAGE")
                {
                    log("这个窗口还没登录，换下一个窗口");
                    continue;
                }

                return parsed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                log("窗口执行失败：" + ex.Message);
            }
        }

        return WeiciResult.Fail("FAILED", last?.Message ?? "没有窗口能执行领读");
    }

    private static WeiciResult Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (message.Length == 0) message = Describe(code, root);
            return new WeiciResult(ok, code, message, raw);
        }
        catch
        {
            return new WeiciResult(false, "BAD_RESULT", raw, raw);
        }
    }

    private static string Describe(string code, JsonElement root) => code switch
    {
        "STARTED" => "已开始领读",
        "READY" => "已到领读设置页（试运行没有点开始）",
        "NEED_CREDENTIALS" => "维词登录页是空账号密码；请先在维词里登录一次",
        "LOGIN_FAILED" => "维词登录失败，请先在维词里手动登录",
        "NEED_LOGIN" => "维词还没登录",
        "NO_VICTOR_PAGE" => "当前页面不是维克多词表页",
        "WORD_NOT_FOUND" => "没找到指定的首词或尾词；请检查拼写或换一个单元",
        "ORDER_REVERSED" => "首词在尾词后面，请对调",
        "UNIT_NOT_LOADED" => "单元没有加载完成",
        "SELECT_MISMATCH" => "勾选词数与目标不一致",
        "NO_UNITS" => "词表里没有单元",
        "NO_DIALOG" => "没有弹出「领读/默写设置」对话框",
        "NO_START_BUTTON" => "没有找到「开始」按钮",
        "NEED_DEBUG_PORT" => "维词已经运行但没有调试端口，请先完全退出再重试",
        "NO_APP" => "找不到或启动不了维词主程序",
        _ => ""
    };

    private WeiciResult? EnsureApp(WeiciConfig config, Action<string> log, CancellationToken ct)
    {
        var port = config.DebugPort;
        var list = WeiciCdp.ListAsync(port, ct).GetAwaiter().GetResult();
        if (WeiciCdp.Targets(list).Count > 0)
        {
            log("已连接运行中的维词程序");
            return null;
        }

        if (Process.GetProcessesByName("weici-teaching").Length > 0)
            return WeiciResult.Fail("NEED_DEBUG_PORT", "维词已经在运行，但没有开启调试端口；请先完全退出维词，再用「一键启动」重开");

        var app = string.IsNullOrWhiteSpace(config.AppPath) ? DefaultAppPath : config.AppPath.Trim();
        if (!File.Exists(app)) return WeiciResult.Fail("NO_APP", "找不到维词主程序：" + app);

        log("正在启动维词程序（调试模式）…");
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        var psi = new ProcessStartInfo(app, $"--remote-debugging-port={port}")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(app) ?? ""
        };
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            return WeiciResult.Fail("NO_APP", "启动维词失败：" + ex.Message);
        }

        for (var i = 0; i < 60; i++)
        {
            ct.WaitHandle.WaitOne(800);
            ct.ThrowIfCancellationRequested();
            var probe = WeiciCdp.ListAsync(port, ct).GetAwaiter().GetResult();
            if (WeiciCdp.Targets(probe).Count > 0)
            {
                log($"维词已启动（调试端口 {port}）；首次使用请先登录");
                Thread.Sleep(2500);
                return null;
            }
        }

        return WeiciResult.Fail("NO_APP", "等待维词调试端口超时");
    }

    private static string LoadScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith("automation.js", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        throw new InvalidOperationException("内嵌的 automation.js 不存在");
    }
}
