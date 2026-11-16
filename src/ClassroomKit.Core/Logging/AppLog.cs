using System.Collections.Concurrent;
using System.Text;

namespace ClassroomKit.Core.Logging;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string Format() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

/// <summary>
/// 日志：内存环形缓冲 + 单后台线程写盘。
/// 调用方只入队、不在调用线程碰磁盘：磁盘一卡不会把所有线程一起挂住。
/// 写盘失败只计数；连续失败 3 次后停 30 秒再试，期间日志只留在内存队列里。
/// </summary>
public sealed class AppLog : IDisposable
{
    private const int RingCapacity = 400;
    private const int BatchCapacity = 200;
    private const int FailuresBeforeBackoff = 3;
    /// <summary>后台队列上限：磁盘长时间写不进去时丢掉最旧的，避免内存无限涨。</summary>
    private const int PendingCapacity = 1000;

    /// <summary>有积压时最迟这么久落一次盘。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Lazy<Thread> _writer;
    private readonly object _carryGate = new();
    private readonly List<LogEntry> _carry = new();
    private readonly string? _dir;

    private int _consecutiveFailures;
    private long _writeFailures;
    /// <summary>失败退避截止时间（UTC Ticks）；用 long 让写线程与 Flush 读写原子。</summary>
    private long _backoffUntilUtcTicks;
    private volatile bool _writing;

    public AppLog(string? directory)
    {
        _dir = directory;
        if (!string.IsNullOrEmpty(directory))
        {
            try { Directory.CreateDirectory(directory); } catch { _dir = null; }
        }

        _writer = new Lazy<Thread>(StartWriter, LazyThreadSafetyMode.ExecutionAndPublication);

        if (_dir is not null)
        {
            // 退出兜底：命令行 / 护航任务这类短命进程没人调 Dispose，
            // 这里补一次 Flush，否则最后一批日志会随进程消失（磁盘卡住时最多拖慢退出 2 秒）。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }
    }

    public event Action<LogEntry>? Written;

    public IReadOnlyList<LogEntry> Snapshot(int take = 100)
    {
        var all = _ring.ToArray();
        if (take <= 0 || take >= all.Length) return all;
        return all[^take..];
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        _ring.Enqueue(entry);
        while (_ring.Count > RingCapacity && _ring.TryDequeue(out _)) { }

        if (_dir is not null)
        {
            _pending.Enqueue(entry);
            while (_pending.Count > PendingCapacity && _pending.TryDequeue(out _)) { }

            _ = _writer.Value;                          // 首条日志时懒启动写线程
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }

        // 事件仍在调用方线程触发（面板/诊断依赖同步回调）
        try { Written?.Invoke(entry); } catch { }
    }

    private Thread StartWriter()
    {
        var thread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "classroomkit-log-writer"
        };
        thread.Start();
        return thread;
    }

    private void WriterLoop()
    {
        while (true)
        {
            try { _wake.Wait(FlushInterval); }
            catch (ObjectDisposedException) { break; }

            FlushOnce();
            // 还有积压就继续排空，不必每批都等一个 500ms；进入失败退避后停手
            while (!_pending.IsEmpty && DateTime.UtcNow.Ticks >= Volatile.Read(ref _backoffUntilUtcTicks))
            {
                FlushOnce();
            }
        }
    }

    /// <summary>把队列里最多一批日志写到磁盘（写线程调用）。</summary>
    private void FlushOnce()
    {
        if (_dir is null) return;
        if (DateTime.UtcNow.Ticks < Volatile.Read(ref _backoffUntilUtcTicks)) return;

        var batch = new List<LogEntry>(BatchCapacity);
        lock (_carryGate)
        {
            batch.AddRange(_carry);
            _carry.Clear();
        }

        while (batch.Count < BatchCapacity && _pending.TryDequeue(out var entry)) batch.Add(entry);
        if (batch.Count == 0) return;

        _writing = true;
        try
        {
            AppendToDisk(batch);
            _consecutiveFailures = 0;
            PruneOld(batch[^1].Time);                   // 清理也留在写线程，绝不进调用方
        }
        catch
        {
            // 写盘失败只计数，绝不抛给调用方
            Interlocked.Increment(ref _writeFailures);
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailuresBeforeBackoff)
            {
                _consecutiveFailures = 0;
                Volatile.Write(ref _backoffUntilUtcTicks, DateTime.UtcNow.Add(FailureBackoff).Ticks);
            }

            // 没写进去的这批留到下次重试；最多留两批，磁盘彻底坏掉时不至于越积越多
            var keep = Math.Min(batch.Count, BatchCapacity * 2);
            lock (_carryGate)
            {
                _carry.AddRange(batch.GetRange(0, keep));
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private void AppendToDisk(List<LogEntry> batch)
    {
        var dir = _dir;
        if (dir is null) return;

        // 同一批里跨天就分段写，别把第二天的日志写进前一天的文件
        var index = 0;
        while (index < batch.Count)
        {
            var day = batch[index].Time;
            var sb = new StringBuilder();
            while (index < batch.Count && batch[index].Time.Date == day.Date)
            {
                sb.Append(batch[index].Format()).Append(Environment.NewLine);
                index++;
            }

            File.AppendAllText(Path.Combine(dir, $"agent-{day:yyyyMMdd}.log"), sb.ToString());
        }
    }

    /// <summary>把队列里剩下的日志尽量写盘（退出前调用；最多等 2 秒）。</summary>
    public void Flush()
    {
        if (_dir is null) return;

        // 退出时的最后一批不受失败退避限制
        Volatile.Write(ref _backoffUntilUtcTicks, 0L);

        try { _wake.Release(); } catch (SemaphoreFullException) { }

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && HasBacklog())
        {
            Thread.Sleep(20);
        }
    }

    private bool HasBacklog()
    {
        if (!_pending.IsEmpty || _writing) return true;
        lock (_carryGate)
        {
            return _carry.Count > 0;
        }
    }

    public void Dispose() => Flush();

    private static int _lastPruneDay = -1;

    private void PruneOld(DateTimeOffset now)
    {
        var dir = _dir;
        if (_lastPruneDay == now.DayOfYear || dir is null) return;
        _lastPruneDay = now.DayOfYear;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "agent-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length < "agent-yyyyMMdd".Length) { File.Delete(f); continue; }
                var stamp = name[^8..];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var day)) continue;
                if ((now.Date - day).TotalDays > 14) File.Delete(f);
            }
        }
        catch { }
    }
}
