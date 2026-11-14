using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassroomKit.ClassIsland.Client;
using ClassroomKit.ClassIsland.Settings;
using ClassroomKit.Core.Client;
using ClassroomKit.Core.Lesson;

namespace ClassroomKit.ClassIsland.Lesson;

/// <summary>
/// 课表桥：把 ClassIsland 的课表状态算成 LessonSnapshot，推给 ClassroomKit 守护进程。
/// 插件只负责「让守护知道现在上什么课、下一节是什么、上一节是什么」；
/// 课前提醒和板书改名都由守护决定，这里不弹任何窗、也不碰 UI。
/// </summary>
/// <remarks>
/// ILessonsService 由宿主程序注册，插件的容器不一定能直接注入，所以这里从 IAppHost.Host
/// 静态入口取服务（插件初始化时宿主还没构建完，取不到就每秒重试一次，直到宿主就绪）。
/// 推送策略：课表快照有意义地变化才发 HTTP，另外每 60 秒补一次心跳；守护没启动时安静失败并每 5 秒重试。
/// </remarks>
public sealed class LessonBridge : IDisposable
{
    /// <summary>每秒算一次快照；状态没变就不会发 HTTP。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>状态长时间不变时，每 60 秒补一次心跳，让守护知道插件还活着、数据没过期。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>推送失败后多久重试（守护可能刚被拉起来）。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>同一句错误一分钟内只记一次，免得日志被「守护没启动」刷满。</summary>
    private static readonly TimeSpan LogThrottle = TimeSpan.FromMinutes(1);

    private readonly PluginSettingsStore _store;
    private readonly object _gate = new();

    private Timer? _timer;
    private HttpClient? _http;
    private ILessonsService? _lessons;
    private bool _exitHooked;
    private int _busy;

    // 「上一节课」自己记：上课时记下科目，离开上课（课间/预备/放学）后继续带着，别立刻清空。
    private string? _inClassSubject;
    private string? _lastClassSubject;
    private string? _previousSubject;
    private bool _wasInClass;

    private LessonSnapshot? _lastPushed;
    private DateTimeOffset _lastPushAt = DateTimeOffset.MinValue;
    private bool _lastPushOk;
    private string? _lastError;
    private DateTimeOffset _lastErrorAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHostLogAt = DateTimeOffset.MinValue;

    public LessonBridge(PluginSettingsStore store) => _store = store;

    /// <summary>最近一次成功推给守护的快照（设置页排错用）。</summary>
    public LessonSnapshot? LastPushed => _lastPushed;

    /// <summary>最近一次推送失败的原因；推送成功后自动清空。</summary>
    public string? LastError => _lastError;

    /// <summary>是否已经在跑（幂等）。</summary>
    public bool IsRunning => _timer is not null;

    /// <summary>开始每秒算一次课表。宿主还没起来也没关系，取不到 ILessonsService 就一直等。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null) return;
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickInterval);
        }

        Log("课表桥已启动：每秒读一次 ClassIsland 课表，变化时推给 ClassroomKit 守护");
    }

    private async void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            EnsureExitHook();
            var snapshot = TryBuildSnapshot();
            if (snapshot is null) return;

            var now = DateTimeOffset.Now;
            if (!ShouldPush(snapshot, now)) return;
            await PushAsync(snapshot, now).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 课表桥坏了也不能影响 ClassIsland 本身
            LogThrottled("课表桥异常：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>把当前课表算成快照；宿主还没就绪（拿不到 ILessonsService）时返回 null。</summary>
    private LessonSnapshot? TryBuildSnapshot()
    {
        var lessons = ResolveLessons();
        if (lessons is null) return null;

        var now = DateTimeOffset.Now;
        var state = MapState(Safe(() => lessons.CurrentState, TimeState.None));
        var current = Clean(Safe<string?>(() => lessons.CurrentSubject?.Name, null));
        var next = Clean(Safe<string?>(() => lessons.NextClassSubject?.Name, null));
        var planLoaded = Safe(() => lessons.IsClassPlanLoaded, false) && Safe(() => lessons.IsClassPlanEnabled, true);

        var nextStartAt = NextStartAt(lessons, now);
        var left = Safe(() => lessons.OnClassLeftTime, TimeSpan.Zero);
        var seconds = left > TimeSpan.Zero
            ? (int)Math.Round(left.TotalSeconds)
            : nextStartAt is { } start ? (int)Math.Round((start - now).TotalSeconds) : 0;
        var lessonIndex = Math.Max(0, Safe(() => lessons.CurrentSelectedIndex, -1) + 1);

        TrackSubjectHistory(state, current);

        return LessonSnapshot.FromClassIsland(
            state,
            current,
            next,
            _wasInClass ? _previousSubject : _lastClassSubject,
            nextStartAt,
            seconds,
            lessonIndex,
            planLoaded,
            now);
    }

    /// <summary>TimeState → 守护认的 LessonStates 字符串。</summary>
    private static string MapState(TimeState state) => state switch
    {
        TimeState.OnClass => LessonStates.Class,
        TimeState.PrepareOnClass => LessonStates.Prepare,
        TimeState.Breaking => LessonStates.Breaking,
        TimeState.AfterSchool => LessonStates.AfterSchool,
        _ => LessonStates.None,
    };

    /// <summary>记住「这节课」和「上一节课」。课间要沿用刚上完的那一科，不能立刻清空。</summary>
    private void TrackSubjectHistory(string state, string? current)
    {
        if (state == LessonStates.Class && current is not null)
        {
            var switched = _inClassSubject is not null && !string.Equals(_inClassSubject, current, StringComparison.Ordinal);
            if (!_wasInClass || switched)
            {
                // 刚进这节课（或连堂课换了科目）：上一节 = 之前上完的那一节
                _previousSubject = _wasInClass ? _inClassSubject : _lastClassSubject;
                _inClassSubject = current;
                _lastClassSubject = current;
                _wasInClass = true;
            }

            return;
        }

        // 课间/预备/放学：_lastClassSubject 继续带着，它就是「上一节课」
        _wasInClass = false;
        _inClassSubject = null;
    }

    /// <summary>下一节课的开始时刻：今天日期 + 课表里的 StartTime；已经过了就顺延到第二天。</summary>
    private static DateTimeOffset? NextStartAt(ILessonsService lessons, DateTimeOffset now)
    {
        var start = Safe<TimeSpan?>(() => lessons.NextClassTimeLayoutItem?.StartTime, null);
        if (start is null) return null;

        var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        var candidate = day + start.Value;
        if (candidate <= now) candidate = candidate.AddDays(1);
        return candidate;
    }

    /// <summary>只有含义变了才推，避免每秒发一次 HTTP。</summary>
    private bool ShouldPush(LessonSnapshot snapshot, DateTimeOffset now)
    {
        var last = _lastPushed;
        if (last is null) return true;

        if (!string.Equals(last.State, snapshot.State, StringComparison.Ordinal)
            || !string.Equals(last.CurrentSubject, snapshot.CurrentSubject, StringComparison.Ordinal)
            || !string.Equals(last.NextSubject, snapshot.NextSubject, StringComparison.Ordinal)
            || !string.Equals(last.PreviousSubject, snapshot.PreviousSubject, StringComparison.Ordinal)
            || last.PlanLoaded != snapshot.PlanLoaded
            || last.LessonIndex != snapshot.LessonIndex
            || !SameMinute(last.NextStartAt, snapshot.NextStartAt))
            return true;

        if (!_lastPushOk && now - _lastPushAt >= RetryInterval) return true;
        return now - _lastPushAt >= HeartbeatInterval;
    }

    private static bool SameMinute(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b is null : b is not null && (a.Value - b.Value).Duration() < TimeSpan.FromSeconds(30);

    private async Task PushAsync(LessonSnapshot snapshot, DateTimeOffset now)
    {
        _lastPushAt = now;
        try
        {
            var settings = _store.Current;
            _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var client = new ClassroomKitClient(settings.BaseUrl, AgentAccess.ResolveToken(settings), _http);
            var reply = await client.PushLessonAsync(snapshot).ConfigureAwait(false);
            if (!reply.Ok)
            {
                _lastPushOk = false;
                SetError("守护拒绝了课表快照：" + (reply.Message ?? "未知原因"));
                return;
            }

            _lastPushed = snapshot;
            _lastPushOk = true;
            _lastError = null;
        }
        catch (Exception ex)
        {
            // 守护没启动是常态：安静失败，下一拍再试，绝不弹窗。
            _lastPushOk = false;
            SetError("推送课表失败（稍后自动重试）：" + ex.Message);
        }
    }

    /// <summary>从宿主容器拿 ILessonsService。插件容器不一定包含宿主服务，所以走 IAppHost.Host 静态入口。</summary>
    private ILessonsService? ResolveLessons()
    {
        var cached = _lessons;
        if (cached is not null) return cached;

        try
        {
            var host = IAppHost.Host;
            if (host is null)
            {
                // 宿主还没构建完：安静等，一分钟记一次方便排查
                var now = DateTimeOffset.Now;
                if (now - _lastHostLogAt >= TimeSpan.FromMinutes(1))
                {
                    _lastHostLogAt = now;
                    Log("宿主还没就绪（IAppHost.Host 为空），暂不推送课表");
                }

                return null;
            }

            if (host.Services.GetService(typeof(ILessonsService)) is ILessonsService service)
            {
                _lessons = service;
                Log("已从宿主容器取到 ILessonsService，开始推送课表");
                return service;
            }
        }
        catch (Exception ex)
        {
            LogThrottled("取 ILessonsService 失败（宿主可能还没起来）：" + ex.Message);
        }

        return null;
    }

    /// <summary>ClassIsland 退出时告诉守护「课表没了」，别拿旧数据提醒。</summary>
    private void EnsureExitHook()
    {
        if (_exitHooked) return;
        try
        {
            var app = AppBase.Current;
            if (app is null) return;
            app.AppStopping += OnAppStopping;
            _exitHooked = true;
        }
        catch
        {
            // 拿不到 AppBase 就算了：守护那边有 stale 判定兜底
        }
    }

    private void OnAppStopping(object? sender, EventArgs e)
    {
        try
        {
            lock (_gate) { _timer?.Dispose(); }
        }
        catch
        {
            // 退出时任何异常都不许往上抛
        }

        try
        {
            var settings = _store.Current;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var client = new ClassroomKitClient(settings.BaseUrl, AgentAccess.ResolveToken(settings), _http);
            client.ClearLessonAsync(timeout.Token).GetAwaiter().GetResult();
            Log("ClassIsland 正在退出：已通知守护清空课表状态");
        }
        catch (Exception ex)
        {
            Log("ClassIsland 退出时清空课表失败（忽略）：" + ex.Message);
        }
    }

    private void SetError(string message)
    {
        _lastError = message;
        LogThrottled(message);
    }

    private void LogThrottled(string message)
    {
        var now = DateTimeOffset.Now;
        if (string.Equals(message, _lastError, StringComparison.Ordinal) && now - _lastErrorAt < LogThrottle) return;
        _lastErrorAt = now;
        Log(message);
    }

    /// <summary>写到插件配置目录的 lesson-bridge.log；日志失败绝不影响主流程。</summary>
    private void Log(string message)
    {
        try
        {
            var path = Path.Combine(_store.Folder, "lesson-bridge.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
        }
        catch
        {
            // 忽略
        }
    }

    private static T Safe<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        try
        {
            lock (_gate) { _timer?.Dispose(); }
        }
        catch
        {
            // 忽略
        }

        _timer = null;

        try
        {
            _http?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _http = null;
    }
}
