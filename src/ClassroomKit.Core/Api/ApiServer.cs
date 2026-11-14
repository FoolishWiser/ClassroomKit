using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClassroomKit.Core.Board;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using ClassroomKit.Core.Update;

namespace ClassroomKit.Core.Api;

/// <summary>把 HTTP 请求映射到守护状态 / 配置 / 动作。</summary>
public sealed class ApiServer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".json"] = "application/json; charset=utf-8"
    };

    private readonly ConfigStore _config;
    private readonly AppLog _log;
    private readonly string _webRoot;
    private readonly string _version;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private HotspotGuard? _guard;

    public ApiServer(ConfigStore config, AppLog log, string webRoot, string version)
    {
        _config = config;
        _log = log;
        _webRoot = webRoot;
        _version = version;
    }

    public string? Token => _config.Current.Token;

    public Func<CancellationToken, Task<(bool Ok, string Message)>>? FixPeersAsync { get; set; }
    public Func<CancellationToken, Task<(bool Ok, string Message)>>? LaunchBcuAsync { get; set; }
    public Action? RequestExit { get; set; }
    public Func<object>? GetUplinks { get; set; }
    public Func<bool, (bool Ok, string Message)>? Uninstall { get; set; }
    /// <summary>保存 ClassInX 板书（参数：保存后是否关闭 ClassInX）。</summary>
    public Func<bool, bool, (bool Ok, string Message)>? SaveBoard { get; set; }
    /// <summary>冲突检测（参数：是否强制忽略冷却）。</summary>
    public Func<bool, (bool Ok, string Message)>? ConflictCheck { get; set; }
    /// <summary>板书/冲突相关状态。</summary>
    public Func<object>? GetBoard { get; set; }
    /// <summary>按用户选择处理冲突（keep-both/keep-classin/keep-seewo/keep-none）。</summary>
    public Func<string, (bool Ok, string Message)>? ConflictChoice { get; set; }
    /// <summary>预览冲突提示窗（不执行任何关闭动作）。</summary>
    public Func<(bool Ok, string Message)>? PreviewConflict { get; set; }
    public Action<UserSettings>? ApplySettings { get; set; }
    public Action<bool>? ApplyPolicy { get; set; }
    /// <summary>升级状态快照（GET /api/update）。</summary>
    public Func<object>? GetUpdate { get; set; }
    /// <summary>检查 GitHub 更新（参数：是否强制刷新），返回状态快照。</summary>
    public Func<bool, CancellationToken, Task<object>>? CheckUpdateAsync { get; set; }
    /// <summary>后台下载并安装新版本，立即返回当前状态。</summary>
    /// <summary>升级：后台下载 → 静默安装；调用本身立刻返回当前状态。</summary>
    public Func<CancellationToken, Task<object>>? StartUpgrade { get; set; }
    /// <summary>hosts 加速状态（GET /api/hosts）。</summary>
    public Func<object>? GetHosts { get; set; }
    /// <summary>手动刷新加速记录并写入 hosts（force=true 表示忽略「已能直连」的判断）。</summary>
    public Func<bool, CancellationToken, Task<(bool Ok, string Message)>>? RefreshHosts { get; set; }
    /// <summary>还原 hosts（删掉受管区块）。</summary>
    public Func<CancellationToken, Task<(bool Ok, string Message)>>? RestoreHosts { get; set; }
    /// <summary>课表状态（GET /api/lesson）。</summary>
    public Func<object>? GetLesson { get; set; }
    /// <summary>ClassIsland 插件推送的课表快照。</summary>
    public Func<LessonSnapshot, (bool Ok, string Message)>? AcceptLesson { get; set; }
    /// <summary>清空课表状态（插件退出、课表被关掉）。</summary>
    public Action? ClearLesson { get; set; }
    /// <summary>保存板书并可选按上一节课改名（参数：保存后关闭、按课表改名）。</summary>
    public Func<bool, bool, (bool Ok, string Message)>? SaveBoardNamed { get; set; }
    /// <summary>预览课前提醒弹窗（不真的保存）。</summary>
    public Func<(bool Ok, string Message)>? PreviewBoardReminder { get; set; }
    /// <summary>稍后提醒 / 取消稍后（0 = 取消，其余 = 分钟）。</summary>
    public Func<int, (bool Ok, string Message)>? SnoozeBoardReminder { get; set; }
    /// <summary>一键自检报告（GET /api/diagnose）。</summary>
    public Func<CancellationToken, Task<object>>? Diagnose { get; set; }

    public sealed record UserSettings(string Ssid, string Passphrase, int TargetClients, bool AllowWifiUplink, string UplinkMode, bool FallbackToLan);

    public void AttachGuard(HotspotGuard guard) => _guard = guard;

    public async Task<HttpResponse> HandleAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Method == "OPTIONS") return new HttpResponse { Status = 204 };

            if (request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsAuthorized(request)) return HttpResponse.Text("unauthorized", 401);
                return await HandleApiAsync(request, ct);
            }

            if (request.Method is not ("GET" or "HEAD")) return HttpResponse.Text("method not allowed", 405);
            return await ServeStaticAsync(request.Path);
        }
        catch (Exception ex)
        {
            _log.Error($"API {request.Method} {request.Path} 异常：{ex.Message}");
            return HttpResponse.Text("internal error", 500);
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var expected = Token;
        if (string.IsNullOrEmpty(expected)) return false;
        var supplied = request.Header("X-Token") ?? request.QueryValue("t");
        if (string.IsNullOrEmpty(supplied)) return false;
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(supplied);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task<HttpResponse> HandleApiAsync(HttpRequest request, CancellationToken ct)
    {
        var path = request.Path.TrimEnd('/').ToLowerInvariant();
        if (path.Length == 0) path = "/api";

        switch (path)
        {
            case "/api/state":
                return HttpResponse.Json(JsonSerializer.Serialize(StateDto(), Json));

            case "/api/uplinks":
            {
                var dto = GetUplinks?.Invoke() ?? new { mode = "auto", candidates = Array.Empty<object>() };
                return HttpResponse.Json(JsonSerializer.Serialize(dto, Json));
            }

            case "/api/board":
            {
                var dto = GetBoard?.Invoke() ?? new { };
                return HttpResponse.Json(JsonSerializer.Serialize(dto, Json));
            }

            case "/api/clients":
                return HttpResponse.Json(JsonSerializer.Serialize(ClientsDto(), Json));

            case "/api/logs":
            {
                var take = 100;
                if (int.TryParse(request.QueryValue("take"), out var t)) take = Math.Clamp(t, 1, 400);
                var lines = _log.Snapshot(take).Select(e => e.Format()).ToArray();
                return HttpResponse.Json(JsonSerializer.Serialize(new { lines }, Json));
            }

            case "/api/health":
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, version = _version }, Json));

            case "/api/policy":
                return await PostAsync(request, body =>
                {
                    var enabled = ReadBool(body, "enabled") ?? throw new ApiException("缺少 enabled");
                    ApplyPolicy?.Invoke(enabled);
                    _log.Info($"Web UI 设置策略：{(enabled ? "开" : "关")}");
                    return new { ok = true, enabled };
                });

            case "/api/settings":
                return await PostAsync(request, body =>
                {
                    var cfg = _config.Current.Hotspot;
                    var ssid = ReadString(body, "ssid") ?? cfg.Ssid;
                    var pass = ReadString(body, "passphrase") ?? cfg.Passphrase;
                    var target = ReadInt(body, "targetClients") ?? cfg.TargetClients;
                    var allowWifi = ReadBool(body, "allowWifiUplink") ?? cfg.AllowWifiUplink;
                    var uplinkMode = ReadString(body, "uplinkMode") ?? _config.Current.Uplink.Mode;
                    var fallback = ReadBool(body, "fallbackToLan") ?? _config.Current.Uplink.FallbackToLan;

                    if (ssid.Length is < 1 or > 32) throw new ApiException("SSID 需 1–32 字符");
                    if (pass.Length is < 8 or > 63) throw new ApiException("密码需 8–63 字符");
                    if (target is < 1 or > 64) throw new ApiException("目标容量需 1–64");

                    ApplySettings?.Invoke(new UserSettings(ssid, pass, target, allowWifi, uplinkMode, fallback));
                    return new { ok = true };
                });

            case "/api/hosts":
                return HttpResponse.Json(JsonSerializer.Serialize(GetHosts?.Invoke() ?? new { supported = false }, Json));

            case "/api/lesson":
                return await HandleLessonAsync(request);

            case "/api/update":
                return await HandleUpdateAsync(request, ct);

            case "/api/action":
                return await HandleActionAsync(request, ct);

            default:
                return HttpResponse.NotFound();
        }
    }

    /// <summary>GET 取状态；POST action=check 检查更新，action=upgrade 后台下载并安装。</summary>
    private async Task<HttpResponse> HandleUpdateAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Method == "GET")
        {
            return HttpResponse.Json(JsonSerializer.Serialize(GetUpdate?.Invoke() ?? new { phase = "unsupported" }, Json));
        }

        if (request.Method != "POST") return HttpResponse.Text("method not allowed", 405);

        var fields = ReadFields(request);
        var action = fields.TryGetValue("action", out var a) ? a.Trim().ToLowerInvariant() : "check";
        var force = !fields.TryGetValue("force", out var f) || f is "1" or "true" or "on" or "yes";

        switch (action)
        {
            case "check":
            {
                if (CheckUpdateAsync is null) return HttpResponse.Text("not supported", 409);
                var status = await CheckUpdateAsync(force, ct);
                return HttpResponse.Json(JsonSerializer.Serialize(status, Json));
            }

            case "upgrade":
            {
                if (StartUpgrade is null) return HttpResponse.Text("not supported", 409);
                _log.Info("面板请求升级：开始后台下载并安装");
                var upgradeStatus = await StartUpgrade(ct);
                return HttpResponse.Json(JsonSerializer.Serialize(upgradeStatus, Json));
            }

            default:
                return HttpResponse.Text("unknown update action", 400);
        }
    }

    /// <summary>GET 取课表状态；POST 由 ClassIsland 插件推送快照，action=clear 清空。</summary>
    private Task<HttpResponse> HandleLessonAsync(HttpRequest request)
    {
        if (request.Method == "GET")
        {
            var dto = GetLesson?.Invoke() ?? new { available = false };
            return Task.FromResult(HttpResponse.Json(JsonSerializer.Serialize(dto, Json)));
        }

        if (request.Method != "POST") return Task.FromResult(HttpResponse.Text("method not allowed", 405));

        var fields = ReadFields(request);
        if (fields.TryGetValue("action", out var clearAction) &&
            clearAction.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            ClearLesson?.Invoke();
            _log.Info("课表状态已清空（ClassIsland 插件退出或课表关闭）");
            return Task.FromResult(HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action = "clear" }, Json)));
        }

        if (AcceptLesson is null) return Task.FromResult(HttpResponse.Text("not supported", 409));

        var snapshot = ParseLessonSnapshot(fields);
        var (ok, message) = AcceptLesson(snapshot);
        return Task.FromResult(HttpResponse.Json(
            JsonSerializer.Serialize(new { ok, message, state = snapshot.State }, Json), ok ? 200 : 400));
    }

    private static LessonSnapshot ParseLessonSnapshot(Dictionary<string, string> fields)
    {
        static string? Text(Dictionary<string, string> f, string key) =>
            f.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        static int Int(Dictionary<string, string> f, string key, int fallback) =>
            f.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;
        static bool Bool(Dictionary<string, string> f, string key) =>
            f.TryGetValue(key, out var v) && v is "1" or "true" or "on" or "yes";
        static DateTimeOffset? Moment(Dictionary<string, string> f, string key)
        {
            if (Text(f, key) is not { } raw) return null;
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed) ? parsed : null;
        }

        return LessonSnapshot.FromClassIsland(
            state: (Text(fields, "state") ?? LessonStates.None).ToLowerInvariant(),
            currentSubject: Text(fields, "currentSubject"),
            nextSubject: Text(fields, "nextSubject"),
            previousSubject: Text(fields, "previousSubject"),
            nextStartAt: Moment(fields, "nextStartAt"),
            secondsToNext: Int(fields, "secondsToNext", 0),
            lessonIndex: Int(fields, "lessonIndex", 0),
            planLoaded: Bool(fields, "planLoaded"),
            at: Moment(fields, "at") ?? DateTimeOffset.Now);
    }

    private static bool IsOn(string text) => text is "1" or "true" or "on" or "yes";

    /// <summary>把请求体解析成字段表：支持 JSON 对象、x-www-form-urlencoded，以及 ?a=b 查询串。</summary>
    private static Dictionary<string, string> ReadFields(HttpRequest request)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Query) fields[kv.Key] = kv.Value;

        var body = request.BodyText.Trim();
        if (body.Length == 0) return fields;

        if (body.StartsWith('{'))
        {
            try
            {
                foreach (var prop in JsonDocument.Parse(body).RootElement.EnumerateObject())
                {
                    fields[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => "",
                        _ => prop.Value.ToString()
                    };
                }
            }
            catch { /* 非法 JSON 就当没有体 */ }
            return fields;
        }

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            var k = i < 0 ? pair : pair[..i];
            var v = i < 0 ? "" : pair[(i + 1)..];
            fields[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
        }
        return fields;
    }

    private async Task<HttpResponse> HandleActionAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.Method != "POST") return HttpResponse.Text("method not allowed", 405);
        var action = ReadFields(request).TryGetValue("action", out var a) ? a : "";

        switch (action)
        {
            case "start":
                ApplyPolicy?.Invoke(true);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已开启" }, Json));

            case "stop":
                ApplyPolicy?.Invoke(false);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已关闭" }, Json));

            case "ensure":
                ApplyPolicy?.Invoke(true);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已确保开启" }, Json));

            case "fix-peers":
            {
                if (FixPeersAsync is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await FixPeersAsync(ct);
                _log.Info($"修复设备上限：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "bcu":
            {
                if (LaunchBcuAsync is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await LaunchBcuAsync(ct);
                _log.Info($"拉起 BCU：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "uninstall":
            {
                if (Uninstall is null) return HttpResponse.Text("not supported", 409);
                var keepData = ReadFields(request).TryGetValue("keepData", out var kd) &&
                               (kd is "1" or "true" or "on" or "yes");
                var (ok, message) = Uninstall(keepData);
                _log.Warn($"自我卸载：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "save-classin-board":
            {
                if (SaveBoard is null) return HttpResponse.Text("not supported", 409);
                var close = _config.Current.BoardSave.CloseAfterSave;
                var fields = ReadFields(request);
                if (fields.TryGetValue("closeAfterSave", out var c))
                    close = c is "1" or "true" or "on" or "yes";
                var launch = _config.Current.BoardSave.LaunchIfNeeded;
                if (fields.TryGetValue("launchIfNeeded", out var l))
                    launch = l is "1" or "true" or "on" or "yes";
                var (ok, message) = SaveBoard(close, launch);
                _log.Info($"保存 ClassInX 板书：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "conflict-check":
            {
                if (ConflictCheck is null) return HttpResponse.Text("not supported", 409);
                var force = false;
                if (ReadFields(request).TryGetValue("force", out var f)) force = f is "1" or "true" or "on" or "yes";
                var (ok, message) = ConflictCheck(force);
                _log.Info($"板书冲突检测：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 409);
            }

            case "conflict-choice":
            {
                if (ConflictChoice is null) return HttpResponse.Text("not supported", 409);
                if (!ReadFields(request).TryGetValue("choice", out var choice) || string.IsNullOrWhiteSpace(choice))
                    throw new ApiException("缺少 choice 参数");
                var (ok, message) = ConflictChoice(choice.Trim());
                _log.Info($"板书冲突选择：{choice} → {message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, choice, message }, Json), ok ? 200 : 500);
            }

            case "conflict-preview":
            {
                if (PreviewConflict is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = PreviewConflict();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "rename-ssid":
            {
                var fields = ReadFields(request);
                var ssid = fields.TryGetValue("ssid", out var s) ? s.Trim() : "";
                if (ssid.Length == 0) ssid = MachineDefaults.DefaultSsid();
                if (Encoding.UTF8.GetByteCount(ssid) > MachineDefaults.MaxSsidBytes)
                    throw new ApiException($"热点名不能超过 {MachineDefaults.MaxSsidBytes} 字节（约 10 个汉字）");

                var regen = fields.TryGetValue("regeneratePassphrase", out var rp) &&
                            rp is "1" or "true" or "on" or "yes";
                var passphrase = regen ? MachineDefaults.DefaultPassphrase() : null;

                _config.Update(c =>
                {
                    c.Hotspot.Ssid = ssid;
                    if (passphrase is not null) c.Hotspot.Passphrase = passphrase;
                    return 0;
                });

                _log.Info($"热点名改为 {ssid}{(passphrase is null ? "" : "，并重新生成密码")}");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    ssid,
                    passphrase,
                    message = "已保存；下一次巡检会自动重启热点生效，已连接的设备需要重新连接"
                }, Json));
            }

            case "conflict-snooze":
            {
                var fields = ReadFields(request);
                var clear = fields.TryGetValue("clear", out var clearValue) && IsOn(clearValue);
                var snooze = !clear && !(fields.TryGetValue("snooze", out var snoozeValue) && !IsOn(snoozeValue));
                var date = snooze ? ConflictSnooze.Today(DateTimeOffset.Now) : "";
                _config.Update(c =>
                {
                    c.Conflict.SnoozeDate = date;
                    return 0;
                });

                _log.Info(snooze ? $"板书冲突提示：今天（{date}）不再提醒" : "板书冲突提示：已恢复提醒");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    snoozeDate = date,
                    snoozeText = ConflictSnooze.Describe(date, DateTimeOffset.Now),
                    message = snooze ? "今天不再弹板书冲突提示，明天自动恢复" : "已恢复板书冲突提示"
                }, Json));
            }

            case "hosts-refresh":
            {
                if (RefreshHosts is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await RefreshHosts(true, ct);
                _log.Info($"hosts 加速：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "hosts-restore":
            {
                if (RestoreHosts is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = await RestoreHosts(ct);
                _log.Info($"hosts 还原：{message}");
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "update-config":
            {
                var fields = ReadFields(request);
                _config.Update(c =>
                {
                    if (fields.TryGetValue("checkOnBoot", out var boot))
                        c.Update.CheckOnBoot = IsOn(boot);
                    if (fields.TryGetValue("autoInstall", out var auto))
                        c.Update.AutoInstall = IsOn(auto);
                    if (fields.TryGetValue("installWithClients", out var withClients))
                        c.Update.InstallWithClients = IsOn(withClients);
                    if (fields.TryGetValue("installWindow", out var window))
                        c.Update.InstallWindow = InstallWindow.Normalize(window);
                    if (fields.TryGetValue("checkIntervalHours", out var hours) && int.TryParse(hours, out var h))
                        c.Update.CheckIntervalHours = Math.Clamp(h, 0, 168);
                    return 0;
                });

                var cfg = _config.Current.Update;
                _log.Info($"更新设置已保存：开机检查={cfg.CheckOnBoot}｜自动安装={cfg.AutoInstall}｜时间窗={InstallWindow.Describe(cfg.InstallWindow)}｜每 {cfg.CheckIntervalHours} 小时检查");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    checkOnBoot = cfg.CheckOnBoot,
                    autoInstall = cfg.AutoInstall,
                    installWithClients = cfg.InstallWithClients,
                    installWindow = cfg.InstallWindow,
                    checkIntervalHours = cfg.CheckIntervalHours,
                    message = "已保存"
                }, Json));
            }

            case "board-save":
            {
                var fields = ReadFields(request);
                if (SaveBoardNamed is null && SaveBoard is null) return HttpResponse.Text("not supported", 409);

                var close = _config.Current.BoardSave.CloseAfterSave;
                if (fields.TryGetValue("closeAfterSave", out var closeValue)) close = IsOn(closeValue);

                var named = _config.Current.BoardSave.NameWithLesson;
                if (fields.TryGetValue("nameWithLesson", out var namedValue)) named = IsOn(namedValue);

                var (ok, message) = SaveBoardNamed?.Invoke(close, named) ?? SaveBoard!(close, false);
                _log.Info($"保存 ClassInX 板书：{message}");
                return HttpResponse.Json(
                    JsonSerializer.Serialize(new { ok, action, closeAfterSave = close, nameWithLesson = named, message }, Json),
                    ok ? 200 : 500);
            }

            case "board-config":
            {
                var fields = ReadFields(request);
                _config.Update(c =>
                {
                    if (fields.TryGetValue("nameWithLesson", out var named)) c.BoardSave.NameWithLesson = IsOn(named);
                    if (fields.TryGetValue("nameTemplate", out var template))
                        c.BoardSave.NameTemplate = string.IsNullOrWhiteSpace(template)
                            ? BoardFileName.DefaultTemplate
                            : template.Trim();
                    if (fields.TryGetValue("closeAfterSave", out var close)) c.BoardSave.CloseAfterSave = IsOn(close);
                    return 0;
                });

                var board = _config.Current.BoardSave;
                _log.Info($"板书设置已保存：按课表改名={board.NameWithLesson}｜模板={board.NameTemplate}");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    nameWithLesson = board.NameWithLesson,
                    nameTemplate = board.NameTemplate,
                    closeAfterSave = board.CloseAfterSave,
                    message = "已保存"
                }, Json));
            }

            case "board-reminder-preview":
            {
                if (PreviewBoardReminder is null) return HttpResponse.Text("not supported", 409);
                var (ok, message) = PreviewBoardReminder();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, message }, Json), ok ? 200 : 500);
            }

            case "board-reminder-snooze":
            {
                if (SnoozeBoardReminder is null) return HttpResponse.Text("not supported", 409);
                var minutes = 0;
                if (ReadFields(request).TryGetValue("minutes", out var value) && int.TryParse(value, out var parsed))
                    minutes = Math.Clamp(parsed, 0, 60);
                var (ok, message) = SnoozeBoardReminder(minutes);
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok, action, minutes, message }, Json), ok ? 200 : 500);
            }

            case "lesson-config":
            {
                var fields = ReadFields(request);
                _config.Update(c =>
                {
                    if (fields.TryGetValue("enabled", out var enabled)) c.Lesson.Enabled = IsOn(enabled);
                    if (fields.TryGetValue("minutesBefore", out var minutes) && int.TryParse(minutes, out var m))
                        c.Lesson.RemindMinutesBefore = m;
                    if (fields.TryGetValue("dialogSeconds", out var dialog) && int.TryParse(dialog, out var d))
                        c.Lesson.DialogSeconds = d;
                    if (fields.TryGetValue("timeoutAction", out var action0) && !string.IsNullOrWhiteSpace(action0))
                        c.Lesson.TimeoutAction = action0.Trim();
                    if (fields.TryGetValue("snoozeMinutes", out var snooze) && int.TryParse(snooze, out var s))
                        c.Lesson.SnoozeMinutes = s;
                    if (fields.TryGetValue("onlyWhenClassInBoard", out var only))
                        c.Lesson.OnlyWhenClassInBoard = IsOn(only);
                    if (fields.TryGetValue("staleMinutes", out var stale) && int.TryParse(stale, out var st2))
                        c.Lesson.StaleMinutes = st2;
                    c.Lesson.Normalize();
                    return 0;
                });

                var lesson = _config.Current.Lesson;
                _log.Info($"课前提醒设置已保存：{(lesson.Enabled ? "开" : "关")}｜提前 {lesson.RemindMinutesBefore} 分钟｜{lesson.DialogSeconds} 秒后{DescTimeout(lesson.TimeoutAction)}");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    enabled = lesson.Enabled,
                    minutesBefore = lesson.RemindMinutesBefore,
                    dialogSeconds = lesson.DialogSeconds,
                    timeoutAction = lesson.TimeoutAction,
                    snoozeMinutes = lesson.SnoozeMinutes,
                    onlyWhenClassInBoard = lesson.OnlyWhenClassInBoard,
                    staleMinutes = lesson.StaleMinutes,
                    message = "已保存"
                }, Json));
            }

            case "lesson-clear":
                ClearLesson?.Invoke();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action, message = "已清空课表状态" }, Json));

            case "rotate-token":
            {
                var token = ConfigStore.NewToken();
                _config.Update(c => { c.Token = token; return 0; });
                _log.Warn("面板请求重置访问令牌：旧链接与插件里保存的令牌都需要更新");
                return HttpResponse.Json(JsonSerializer.Serialize(new
                {
                    ok = true,
                    action,
                    token,
                    message = "令牌已重置；当前页面需要改用带新令牌的链接重新打开"
                }, Json));
            }

            case "diagnose":
            {
                if (Diagnose is null) return HttpResponse.Text("not supported", 409);
                var report = await Diagnose(ct);
                return HttpResponse.Json(JsonSerializer.Serialize(report, Json));
            }

            case "exit":
                RequestExit?.Invoke();
                return HttpResponse.Json(JsonSerializer.Serialize(new { ok = true, action }, Json));

            default:
                return HttpResponse.Text("unknown action", 400);
        }
    }

    /// <summary>弹窗超时动作的人话说明（写日志用）。</summary>
    private static string DescTimeout(string action) => action switch
    {
        "save" => "自动保存板书（不关闭 ClassInX）",
        "save-close" or "saveclose" => "自动保存并关闭 ClassInX",
        "dismiss" or "ignore" => "自动忽略",
        _ => "一直等到手动选择"
    };

    private Task<HttpResponse> PostAsync(HttpRequest request, Func<JsonElement, object> work)
    {
        if (request.Method != "POST") return Task.FromResult(HttpResponse.Text("method not allowed", 405));
        var body = JsonSerializer.SerializeToElement(ReadFields(request));

        try
        {
            var result = work(body);
            return Task.FromResult(HttpResponse.Json(JsonSerializer.Serialize(result, Json)));
        }
        catch (ApiException ex)
        {
            return Task.FromResult(HttpResponse.Json(
                JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json), 400));
        }
    }

    private object StateDto()
    {
        var cfg = _config.Current;
        var snap = _guard?.LastSnapshot ?? TetheringSnapshot.Empty;
        return new
        {
            version = _version,
            configSchema = cfg.SchemaVersion,
            legacyDefaults = MachineDefaults.IsLegacy(cfg.Hotspot.Ssid, cfg.Hotspot.Passphrase),
            startedAt = _startedAt,
            uptimeSec = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            enabled = cfg.Hotspot.Enabled,
            guardState = _guard?.State.ToString() ?? "Unknown",
            hotspot = new
            {
                state = snap.OperationalState.ToString(),
                ssid = cfg.Hotspot.Ssid,
                passphrase = cfg.Hotspot.Passphrase,
                band = cfg.Hotspot.Band,
                clients = snap.ClientCount,
                maxClients = snap.MaxClientCount,
                targetClients = cfg.Hotspot.TargetClients,
                registryPeers = cfg.Hotspot.RegistryPeers,
                allowWifiUplink = cfg.Hotspot.AllowWifiUplink,
                noConnectionsTimeout = snap.NoConnectionsTimeoutEnabled,
                currentSsid = snap.CurrentSsid
            },
            uplink = new
            {
                available = snap.UplinkAvailable,
                isWifi = snap.UplinkIsWifi,
                name = snap.UplinkName,
                id = snap.UplinkId,
                hasInternet = snap.UplinkHasInternet,
                reason = snap.UplinkReason,
                mode = cfg.Uplink.Mode,
                fallbackToLan = cfg.Uplink.FallbackToLan
            },
            capability = snap.Capability.ToString(),
            restartCount = _guard?.RestartCount ?? 0,
            lastSuccess = _guard?.LastSuccess,
            lastError = _guard?.LastError,
            capacityWarning = _guard?.CapabilityWarning,
            bcu = new { enabled = cfg.Bcu.Enabled, path = cfg.Bcu.Path },
            boardSave = new
            {
                enabled = cfg.BoardSave.Enabled,
                closeAfterSave = cfg.BoardSave.CloseAfterSave,
                nameWithLesson = cfg.BoardSave.NameWithLesson,
                nameTemplate = cfg.BoardSave.NameTemplate
            },
            lesson = GetLesson?.Invoke(),
            conflict = new
            {
                enabled = cfg.Conflict.Enabled,
                cooldownMinutes = cfg.Conflict.CooldownMinutes,
                snoozeDate = cfg.Conflict.SnoozeDate,
                snoozeText = ConflictSnooze.Describe(cfg.Conflict.SnoozeDate, DateTimeOffset.Now)
            },
            hosts = GetHosts?.Invoke()
        };
    }

    private object ClientsDto()
    {
        var cfg = _config.Current;
        var clients = _guard?.Clients ?? Array.Empty<TetherClient>();
        return new
        {
            targetClients = cfg.Hotspot.TargetClients,
            count = clients.Count,
            clients = clients.Select(c => new
            {
                mac = c.Mac,
                hostName = c.HostName,
                ip = c.Ip,
                connectedAt = c.ConnectedAt,
                uptimeSec = c.Uptime is null ? (int?)null : (int)c.Uptime.Value.TotalSeconds
            }).ToArray()
        };
    }

    private async Task<HttpResponse> ServeStaticAsync(string rawPath)
    {
        var path = rawPath == "/" ? "/index.html" : rawPath;
        if (path.Contains("..", StringComparison.Ordinal)) return HttpResponse.Forbidden();

        var rel = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_webRoot, rel));
        var rootFull = Path.GetFullPath(_webRoot);
        if (!full.StartsWith(rootFull, StringComparison.Ordinal)) return HttpResponse.Forbidden();
        if (!File.Exists(full)) return HttpResponse.NotFound();

        var ext = Path.GetExtension(full);
        var type = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        var data = await File.ReadAllBytesAsync(full);
        return HttpResponse.Bytes(data, type);
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? ReadInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
        return null;
    }

    private static bool? ReadBool(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "on" or "yes" => true,
                "0" or "false" or "off" or "no" => false,
                _ => (bool?)null
            },
            _ => null
        };
    }

    private sealed class ApiException(string message) : Exception(message);
}
