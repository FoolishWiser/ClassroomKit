using System.Collections.Concurrent;

namespace ClassroomKit.Core.Logging;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public string Format() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}

public sealed class AppLog
{
    private const int RingCapacity = 400;
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly object _gate = new();
    private readonly string? _dir;

    public AppLog(string? directory)
    {
        _dir = directory;
        if (!string.IsNullOrEmpty(directory))
        {
            try { Directory.CreateDirectory(directory); } catch { _dir = null; }
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
            try
            {
                lock (_gate)
                {
                    var path = Path.Combine(_dir, $"agent-{entry.Time:yyyyMMdd}.log");
                    File.AppendAllText(path, entry.Format() + Environment.NewLine);
                    PruneOld(entry.Time);
                }
            }
            catch
            {
                // 日志失败绝不影响守护进程
            }
        }

        try { Written?.Invoke(entry); } catch { }
    }

    private static int _lastPruneDay = -1;

    private void PruneOld(DateTimeOffset now)
    {
        if (_lastPruneDay == now.DayOfYear || _dir is null) return;
        _lastPruneDay = now.DayOfYear;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_dir, "agent-*.log"))
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
