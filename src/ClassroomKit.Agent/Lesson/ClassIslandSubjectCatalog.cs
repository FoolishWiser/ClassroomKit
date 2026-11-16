using System.Diagnostics;
using ClassroomKit.Agent.Update;
using ClassroomKit.Core.Lesson;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Lesson;

/// <summary>
/// 从 ClassIsland 的档案文件里读「所有科目」。插件推送的科目只在插件新版本、ClassIsland 运行中才有；
/// 这里直接读 data\\Profiles 下的档案，插件旧一点、ClassIsland 还没启动也能列出科目。
/// 扫描 ClassIsland 根目录比较重，5 分钟一次；档案本身每分钟重读一次。
/// </summary>
public sealed class ClassIslandSubjectCatalog
{
    private static readonly TimeSpan RootTtl = TimeSpan.FromMinutes(5);

    /// <summary>连续多次扫不到根目录（正常是没装 ClassIsland）后退避到 30 分钟一次。</summary>
    private static readonly TimeSpan EmptyRootTtl = TimeSpan.FromMinutes(30);
    private const int EmptyScansBeforeBackoff = 3;

    /// <summary>单次刷新超过 5 秒记一条 Warn；同类告警最多 10 分钟一条。</summary>
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowWarnTtl = TimeSpan.FromMinutes(10);

    private readonly AppLog _log;
    private readonly PluginInstaller _installer;

    private volatile IReadOnlyList<string> _current = Array.Empty<string>();
    private IReadOnlyList<string> _roots = Array.Empty<string>();
    private DateTimeOffset _rootsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _missingLoggedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _slowWarnedAt = DateTimeOffset.MinValue;
    private bool _foundLogged;
    private int _emptyScans;
    private int _refreshing;

    public ClassIslandSubjectCatalog(AppLog log)
    {
        _log = log;
        _installer = new PluginInstaller(log);
    }

    /// <summary>最近一次读到的全部科目；还没读到过是空列表。</summary>
    public IReadOnlyList<string> Current => _current;

    /// <summary>重读一次档案。读不到就保留上一次的名单，不把已有勾选项弄没。</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var watch = Stopwatch.StartNew();
        try
        {
            var now = DateTimeOffset.Now;
            // 扫根目录很重：一律按 TTL 缓存。以前 _roots 为空就每分钟重扫，没装 ClassIsland 的机器会整天做全盘 BFS。
            // 连续 3 次扫不到就退避到 30 分钟一次。
            var ttl = _emptyScans >= EmptyScansBeforeBackoff ? EmptyRootTtl : RootTtl;
            if (now - _rootsAt > ttl)
            {
                _roots = _installer.FindRoots();
                _rootsAt = now;
                _emptyScans = _roots.Count == 0 ? _emptyScans + 1 : 0;
            }

            var names = new List<string>();
            foreach (var root in _roots)
            {
                names.AddRange(ClassIslandProfileSubjects.ReadDirectory(Path.Combine(root, "data", "Profiles")));
                names.AddRange(ClassIslandProfileSubjects.ReadDirectory(Path.Combine(root, "Profiles")));
            }

            var merged = LessonSubjects.Normalize(names);
            if (merged.Count > 0)
            {
                _current = merged;
                if (!_foundLogged)
                {
                    _foundLogged = true;
                    _log.Info($"从 ClassIsland 档案读到 {merged.Count} 个科目");
                }
            }
            else if (_missingLoggedAt == default || now - _missingLoggedAt > TimeSpan.FromMinutes(30))
            {
                _missingLoggedAt = now;
                _log.Info("暂未从 ClassIsland 档案读到科目（没装 ClassIsland、档案还没生成或目录不可读）");
            }
        }
        catch (Exception ex)
        {
            _log.Debug("读取 ClassIsland 科目失败：" + ex.Message);
        }
        finally
        {
            watch.Stop();
            Interlocked.Exchange(ref _refreshing, 0);

            // 单次刷新超过 5 秒记一条 Warn（节流 10 分钟，避免每分钟刷屏）
            if (watch.Elapsed > SlowRefresh)
            {
                var finishedAt = DateTimeOffset.Now;
                if (_slowWarnedAt == DateTimeOffset.MinValue || finishedAt - _slowWarnedAt > SlowWarnTtl)
                {
                    _slowWarnedAt = finishedAt;
                    _log.Warn($"扫描 ClassIsland 科目用时 {watch.Elapsed.TotalSeconds:F1} 秒，超过 {SlowRefresh.TotalSeconds:F0} 秒");
                }
            }
        }
    }
}
