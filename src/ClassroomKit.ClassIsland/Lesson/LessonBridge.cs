using System.Globalization;
using System.Reflection;
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
/// 课后自动保存和板书改名都由守护决定，这里不弹任何窗、也不碰 UI。
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

    /// <summary>科目清单 30 秒才读一次：课表快照每秒算一次，没必要每次都反射档案服务。</summary>
    private static readonly TimeSpan SubjectsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>宿主档案服务的类型名：优先 IPC 的公开档案服务，退到核心服务接口。</summary>
    private static readonly string[] ProfileServiceTypeNames =
    {
        "ClassIsland.Shared.IPC.Abstractions.Services.IPublicProfileService",
        "ClassIsland.Core.Abstractions.Services.IProfileService"
    };

    /// <summary>反射找到的档案服务类型（进程内不会变；找不到时保持 null，下次再找）。</summary>
    private static Type[]? _profileServiceTypes;

    private readonly PluginSettingsStore _store;
    private readonly object _gate = new();

    private Timer? _timer;
    private HttpClient? _http;
    private ILessonsService? _lessons;
    private bool _exitHooked;
    private int _busy;

    // 整份课表的科目清单：30 秒缓存一次；读失败就沿用上次结果，不打断课表推送。
    private object? _profileService;
    private IReadOnlyList<string> _cachedSubjects = Array.Empty<string>();
    private DateTimeOffset _subjectsCachedAt = DateTimeOffset.MinValue;

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
        var times = ReadLessonTimes(lessons, now, state, planLoaded);

        TrackSubjectHistory(state, current);

        return LessonSnapshot.FromClassIsland(
            state,
            current,
            next,
            _wasInClass ? _previousSubject : _lastClassSubject,
            nextStartAt,
            seconds,
            times.LessonIndex,
            planLoaded,
            now,
            ReadSubjects(),
            state == LessonStates.Breaking ? ReadBreakName(lessons, now) : null,
            times.PreviousLessonIndex,
            times.NextLessonIndex,
            times.CurrentStartAt,
            times.CurrentEndAt,
            times.NextEndAt);
    }

    private sealed record LessonTimes(
        int LessonIndex,
        int PreviousLessonIndex,
        int NextLessonIndex,
        DateTimeOffset? CurrentStartAt,
        DateTimeOffset? CurrentEndAt,
        DateTimeOffset? NextEndAt);

    /// <summary>
    /// 按课表里的「上课类」时间点算真实节次：课中 = 当前这一节，课间 = 刚结束那一节，
    /// 同时给出上一节/下一节序号与三个边界时刻。反射失败时退回 CurrentSelectedIndex + 1。
    /// </summary>
    private static LessonTimes ReadLessonTimes(ILessonsService lessons, DateTimeOffset now, string state, bool planLoaded)
    {
        var fallback = Math.Max(0, Safe(() => lessons.CurrentSelectedIndex, -1) + 1);
        var fallbackResult = new LessonTimes(fallback, state == LessonStates.Breaking ? fallback : 0, fallback > 0 ? fallback + 1 : 0, null, null, null);
        if (!planLoaded) return fallbackResult;

        try
        {
            var plan = Safe<object?>(() => lessons.CurrentClassPlan, null);
            // 必须按 TimeLayout.Layouts 里的全部「上课」时间点编号：ClassIsland 的 GetClassIndex()
            // 也是这么数的，包含被禁用的节次。只看 ValidTimeLayoutItems 会在禁用某一节后把
            // 后面的节次整体往前挪，文件名里的「第几节」就和学校作息对不上。
            var items = ReadLayoutItems(plan);
            if (items is null) return fallbackResult;

            var classes = new List<(int Ordinal, TimeSpan Start, TimeSpan End)>();
            foreach (var item in items)
            {
                if (item is null || !IsClassItem(item)) continue;
                var start = ReadTimeSpan(item, "StartTime");
                var end = ReadTimeSpan(item, "EndTime");
                if (start is null || end is null) continue;
                classes.Add((classes.Count + 1, start.Value, end.Value));
            }

            if (classes.Count == 0) return fallbackResult;

            var time = now.TimeOfDay;
            var day = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var current = classes.FirstOrDefault(c => time >= c.Start && time < c.End);
            var previous = classes.LastOrDefault(c => c.End <= time);
            var next = classes.FirstOrDefault(c => c.Start >= time);

            DateTimeOffset? At(TimeSpan value) => day + value;

            if (current.Ordinal > 0 && (state == LessonStates.Class || state == LessonStates.Prepare))
            {
                return new LessonTimes(current.Ordinal, current.Ordinal - 1, current.Ordinal + 1,
                    At(current.Start), At(current.End), next.Ordinal > 0 ? At(next.End) : null);
            }

            if (state == LessonStates.Breaking || state == LessonStates.Prepare || state == LessonStates.AfterSchool)
            {
                var lesson = previous.Ordinal > 0 ? previous.Ordinal : fallback;
                return new LessonTimes(lesson, lesson, next.Ordinal > 0 ? next.Ordinal : 0,
                    previous.Ordinal > 0 ? At(previous.Start) : null,
                    previous.Ordinal > 0 ? At(previous.End) : null,
                    next.Ordinal > 0 ? At(next.End) : null);
            }

            if (current.Ordinal > 0)
            {
                return new LessonTimes(current.Ordinal, current.Ordinal - 1, current.Ordinal + 1,
                    At(current.Start), At(current.End), next.Ordinal > 0 ? At(next.End) : null);
            }

            return fallbackResult;
        }
        catch (Exception)
        {
            return fallbackResult;
        }
    }

    /// <summary>优先读 TimeLayout.Layouts（全部时间点），读不到再退回 ValidTimeLayoutItems。</summary>
    private static System.Collections.IEnumerable? ReadLayoutItems(object? plan)
    {
        try
        {
            var layout = plan?.GetType().GetProperty("TimeLayout", BindingFlags.Public | BindingFlags.Instance)?.GetValue(plan);
            var layouts = layout?.GetType().GetProperty("Layouts", BindingFlags.Public | BindingFlags.Instance)?.GetValue(layout) as System.Collections.IEnumerable;
            if (layouts is not null)
            {
                foreach (var _ in layouts) return layouts;
            }
        }
        catch { /* 反射失败就用有效时间点 */ }

        return plan?.GetType().GetProperty("ValidTimeLayoutItems", BindingFlags.Public | BindingFlags.Instance)?.GetValue(plan) as System.Collections.IEnumerable;
    }

    private static bool IsClassItem(object item)
    {
        try
        {
            var value = item.GetType().GetProperty("TimeType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            if (value is null) return true;
            var text = value.ToString();
            if (string.Equals(text, "Class", StringComparison.OrdinalIgnoreCase)) return true;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var number) && number == 0;
        }
        catch (Exception)
        {
            return true;
        }
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
            || !string.Equals(last.BreakName, snapshot.BreakName, StringComparison.Ordinal)
            || last.PlanLoaded != snapshot.PlanLoaded
            || last.LessonIndex != snapshot.LessonIndex
            || last.PreviousLessonIndex != snapshot.PreviousLessonIndex
            || last.NextLessonIndex != snapshot.NextLessonIndex
            || !SameMinute(last.CurrentStartAt, snapshot.CurrentStartAt)
            || !SameMinute(last.CurrentEndAt, snapshot.CurrentEndAt)
            || !SameMinute(last.NextEndAt, snapshot.NextEndAt)
            || !SameSubjects(last.Subjects, snapshot.Subjects)
            || !SameMinute(last.NextStartAt, snapshot.NextStartAt))
            return true;

        if (!_lastPushOk && now - _lastPushAt >= RetryInterval) return true;
        return now - _lastPushAt >= HeartbeatInterval;
    }

    private static bool SameMinute(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b is null : b is not null && (a.Value - b.Value).Duration() < TimeSpan.FromSeconds(30);

    /// <summary>
    /// 当前课间的名字（ClassIsland 时间点上的自定义课间名，如「高一礼让」）。
    /// SDK 里这个名字挂在时间点对象上，不同版本暴露的位置不一样，所以用反射兜底：
    /// 先看 ILessonsService.CurrentTimeLayoutItem，再看 CurrentClassPlan.ValidTimeLayoutItems 按时间匹配。
    /// 读不到就返回 null（调用方不设置跳过规则）。
    /// </summary>
    private static string? ReadBreakName(ILessonsService lessons, DateTimeOffset now)
    {
        try
        {
            object? item = null;
            var property = lessons.GetType().GetProperty("CurrentTimeLayoutItem", BindingFlags.Public | BindingFlags.Instance);
            if (property is not null) item = property.GetValue(lessons);

            if (item is null)
            {
                var planProperty = lessons.GetType().GetProperty("CurrentClassPlan", BindingFlags.Public | BindingFlags.Instance);
                var plan = planProperty?.GetValue(lessons);
                var itemsProperty = plan?.GetType().GetProperty("ValidTimeLayoutItems", BindingFlags.Public | BindingFlags.Instance);
                if (itemsProperty?.GetValue(plan) is System.Collections.IEnumerable items)
                {
                    var time = now.TimeOfDay;
                    foreach (var candidate in items)
                    {
                        if (candidate is null) continue;
                        var start = ReadTimeSpan(candidate, "StartTime");
                        var end = ReadTimeSpan(candidate, "EndTime");
                        if (start is null) continue;
                        if (time < start) continue;
                        if (end is not null && time >= end) continue;
                        item = candidate;
                        break;
                    }
                }
            }

            return Clean(ReadString(item, "BreakNameText") ?? ReadString(item, "BreakName"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static TimeSpan? ReadTimeSpan(object? target, string name)
    {
        try
        {
            var value = target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
            return value is TimeSpan span ? span : null;
        }
        catch (Exception) { return null; }
    }

    private static string? ReadString(object? target, string name)
    {
        try
        {
            return target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) as string;
        }
        catch (Exception) { return null; }
    }

    /// <summary>科目清单按集合比较（顺序不敏感）：科目改名、增删都要重新推一次。</summary>
    private static bool SameSubjects(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        var a = LessonSubjects.Normalize(left).Select(LessonSubjects.Compact).ToHashSet(StringComparer.Ordinal);
        var b = LessonSubjects.Normalize(right).Select(LessonSubjects.Compact).ToHashSet(StringComparer.Ordinal);
        return a.SetEquals(b);
    }

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
            SetError(ex.Message.Contains("401", StringComparison.Ordinal)
                ? "推送课表失败（稍后自动重试）：" + ex.Message + " —— 令牌为空或有误，先看设置页的「令牌来源」"
                : "推送课表失败（稍后自动重试）：" + ex.Message);
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

    /// <summary>
    /// 读整份课表的科目清单（ClassIsland 档案里的科目字典，取每项的 Name）。
    /// 30 秒缓存一次；宿主没就绪、反射失败都只记日志并沿用上次结果，绝不打断课表推送。
    /// </summary>
    private IReadOnlyList<string> ReadSubjects()
    {
        var now = DateTimeOffset.Now;
        if (now - _subjectsCachedAt < SubjectsCacheTtl) return _cachedSubjects;

        _subjectsCachedAt = now;
        try
        {
            var profile = ReadProperty(ResolveProfileService(), "Profile");
            if (ExtractSubjectNames(ReadProperty(profile, "Subjects")) is { } names)
                _cachedSubjects = LessonSubjects.Normalize(names);
        }
        catch (Exception ex)
        {
            LogThrottled("读取课表科目清单失败（沿用上次结果）：" + ex.Message);
        }

        return _cachedSubjects;
    }

    /// <summary>从宿主容器拿档案服务（IPublicProfileService，备选 IProfileService）；拿不到返回 null。</summary>
    private object? ResolveProfileService()
    {
        if (_profileService is { } cached) return cached;

        var host = IAppHost.Host;
        var services = host?.Services;
        if (services is null) return null;

        foreach (var type in ProfileServiceTypes())
        {
            if (services.GetService(type) is not { } service) continue;
            _profileService = service;
            Log("已从宿主容器取到档案服务，开始同步整份课表的科目清单");
            return service;
        }

        return null;
    }

    /// <summary>档案服务候选类型：先按预期的全名找，再按简单名在已加载程序集里兜底。</summary>
    private static Type[] ProfileServiceTypes()
    {
        if (_profileServiceTypes is { Length: > 0 } cached) return cached;

        var types = new List<Type>();
        foreach (var name in ProfileServiceTypeNames)
        {
            var type = FindLoadedType(name);
            if (type is not null && !types.Contains(type)) types.Add(type);
        }

        if (types.Count == 0)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var type in SafeTypes(assembly))
                {
                    if (type.Name is not ("IPublicProfileService" or "IProfileService")) continue;
                    if (!types.Contains(type)) types.Add(type);
                }
            }
        }

        if (types.Count == 0) return types.ToArray();

        _profileServiceTypes = types.ToArray();
        return _profileServiceTypes;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(fullName, throwOnError: false) is { } type) return type;
            }
            catch
            {
                // 动态程序集可能不允许查询：跳过
            }
        }

        return null;
    }

    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    /// <summary>反射读属性；显式接口实现也能读到（先在实现类上找，再去接口上找）。</summary>
    private static object? ReadProperty(object? target, string name)
    {
        if (target is null) return null;
        var type = target.GetType();

        var property = type.GetProperty(name);
        if (property is not null) return property.GetValue(target);

        foreach (var contract in type.GetInterfaces())
        {
            var declared = contract.GetProperty(name);
            if (declared is not null) return declared.GetValue(target);
        }

        return null;
    }

    /// <summary>
    /// 从档案的科目字典里取出全部科目名：字典项取 Value（或本身）的 Name，去空白后去重。
    /// 返回 null 表示「这次没读到」，调用方沿用缓存。
    /// </summary>
    private static IEnumerable<string>? ExtractSubjectNames(object? subjects)
    {
        if (subjects is null) return null;

        var names = new List<string>();
        if (subjects is System.Collections.IDictionary dictionary)
        {
            foreach (var value in dictionary.Values) AddSubjectName(names, value);
            return names;
        }

        if (subjects is not System.Collections.IEnumerable items) return null;

        foreach (var item in items)
        {
            if (item is null) continue;
            // 字典被当成 IEnumerable 枚举时给的是 KeyValuePair：先取它的 Value
            AddSubjectName(names, ReadProperty(item, "Value") ?? item);
        }

        return names;
    }

    private static void AddSubjectName(List<string> names, object? subject)
    {
        if (subject is null) return;
        if (ReadProperty(subject, "Name") is string { Length: > 0 } name) names.Add(name);
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
