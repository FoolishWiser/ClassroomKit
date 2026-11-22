using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services.NotificationProviders;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Models.Notification;
using ClassIsland.Core.Models.Notification.Templates;
using ClassroomKit.ClassIsland.Client;
using ClassroomKit.ClassIsland.Settings;
using ClassroomKit.Core.Client;
using ClassroomKit.Core.Lesson;

namespace ClassroomKit.ClassIsland.Lesson;

/// <summary>
/// 课后自动保存的 ClassIsland 倒计时提醒。
/// 守护进程负责判定「下课 5 分钟」「15 秒无输入」「点击打断后 10 秒重试」；
/// 插件只把倒计时画在 ClassIsland 的提醒层上，发现状态变化就取消或重画。
/// </summary>
[NotificationProviderInfo(
    "7E4B2F1A-6C3D-4A8E-9B57-2D1C0F8A6E33",
    "课后自动保存",
    "\ued34",
    "下课后安静 15 秒自动保存 ClassInX 板书；中途有人点击会延后重试，不弹窗。")]
public sealed class BoardAutoSaveNotifier : NotificationProviderBase, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestDuration = TimeSpan.FromSeconds(30);

    private readonly PluginSettingsStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Timer _timer;
    private readonly object _gate = new();

    private int _busy;
    private NotificationRequest? _request;
    private SimpleTextTemplateData? _data;
    private string? _shownKey;
    private string? _lastAttemptKey;
    private DateTimeOffset _lastAttemptAt;
    private string? _lastError;
    private DateTimeOffset _lastErrorAt = DateTimeOffset.MinValue;

    public BoardAutoSaveNotifier(PluginSettingsStore store) : base()
    {
        _store = store;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), PollInterval);
    }

    private async void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var settings = _store.Current;
            using var client = new ClassroomKitClient(settings.BaseUrl, AgentAccess.ResolveToken(settings), _http);
            var state = await client.GetBoardAutoSaveAsync().ConfigureAwait(false);
            Apply(state);
        }
        catch (Exception ex)
        {
            Hide();
            LogThrottled("读取自动保存状态失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void Apply(BoardAutoSaveState state)
    {
        var counting = string.Equals(state.Phase, BoardAutoSavePhases.Countdown, StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, BoardAutoSavePhases.Cooldown, StringComparison.OrdinalIgnoreCase);
        if (!counting)
        {
            Hide();
            return;
        }

        var key = CountdownKey(state);
        var text = BuildText(state);

        lock (_gate)
        {
            if (_request is null || !string.Equals(_shownKey, key, StringComparison.Ordinal))
            {
                // 通知被系统禁用或用户立刻关掉时会连续 Cancel；同一轮倒计时不要每秒重建。
                if (_request is null && string.Equals(_lastAttemptKey, key, StringComparison.Ordinal)
                    && DateTimeOffset.Now - _lastAttemptAt < TimeSpan.FromSeconds(5))
                    return;
                Show(state, key, text);
                return;
            }

            var data = _data;
            if (data is null) return;
            Dispatcher.UIThread.Post(() => data.Text = text);
        }
    }

    private void Show(BoardAutoSaveState state, string key, string text)
    {
        NotificationRequest? previous;
        lock (_gate)
        {
            previous = _request;
            _request = null;
            _data = null;
            _shownKey = null;
        }
        if (previous is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { previous.Cancel(); } catch { /* 已经结束 */ }
            });
        }

        var mask = NotificationContent.CreateTwoIconsMask(
            string.IsNullOrWhiteSpace(state.Subject) ? "课后自动保存" : state.Subject + " · 自动保存",
            rightIcon: "lucide(\ue224)");

        var data = new SimpleTextTemplateData { Text = text };
        var overlay = new NotificationContent
        {
            Content = data,
            ContentTemplateResourceKey = SimpleTextTemplateData.TemplateResourceKey,
            Duration = RequestDuration,
            IsSpeechEnabled = false,
        };

        var request = new NotificationRequest { MaskContent = mask, OverlayContent = overlay };
        request.Canceled += (_, _) => Clear(request);
        request.Completed += (_, _) => Clear(request);

        _request = request;
        _data = data;
        _shownKey = key;
        _lastAttemptKey = key;
        _lastAttemptAt = DateTimeOffset.Now;

        Dispatcher.UIThread.Post(() =>
        {
            try { ShowNotification(request); }
            catch (Exception ex)
            {
                Clear(request);
                LogThrottled("显示自动保存倒计时失败：" + ex.Message);
            }
        });
    }

    private void Hide()
    {
        NotificationRequest? request;
        lock (_gate)
        {
            request = _request;
            _request = null;
            _data = null;
            _shownKey = null;
        }

        if (request is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            try { request.Cancel(); }
            catch { /* 已经结束 */ }
        });
    }

    private void Clear(NotificationRequest request)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_request, request)) return;
            _request = null;
            _data = null;
            _shownKey = null;
        }
    }

    private static string CountdownKey(BoardAutoSaveState state) =>
        (state.Key ?? "") + "|" + (state.DueAt?.ToUnixTimeSeconds() ?? 0) + "|" + state.Subject;

    private static string BuildText(BoardAutoSaveState state)
    {
        var left = Math.Clamp(state.RemainingSeconds, 0, 999);
        if (string.Equals(state.Phase, BoardAutoSavePhases.Cooldown, StringComparison.OrdinalIgnoreCase))
            return "已打断，" + left + " 秒后重新倒计时" + Environment.NewLine + "先别碰屏幕就能自动保存";
        if (left <= 0) return "正在保存板书…";
        return "还剩 " + left + " 秒自动保存板书" + Environment.NewLine + "点击屏幕可打断";
    }

    private void LogThrottled(string message)
    {
        var now = DateTimeOffset.Now;
        if (string.Equals(message, _lastError, StringComparison.Ordinal) && now - _lastErrorAt < TimeSpan.FromMinutes(1)) return;
        _lastError = message;
        _lastErrorAt = now;
        try
        {
            File.AppendAllText(Path.Combine(_store.Folder, "board-autosave.log"),
                now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响提醒
        }
    }

    public void Dispose()
    {
        try { _timer.Dispose(); } catch { }
        Hide();
        try { _http.Dispose(); } catch { }
    }
}
