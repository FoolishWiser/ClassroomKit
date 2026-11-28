using System.Text.Json;

namespace ClassroomKit.Core.Board;

/// <summary>一次保存产生的板书记录；只存本机路径，不上传。</summary>
public sealed record BoardRecord(
    string Id,
    string Subject,
    int LessonIndex,
    DateTimeOffset SavedAt,
    string Folder,
    IReadOnlyList<string> Files)
{
    public string LessonText => LessonIndex > 0 ? $"第{LessonIndex}节" : "节次未知";

    public string WeekdayText => "周" + "日一二三四五六"[(int)SavedAt.DayOfWeek];

    public string DisplayTime => SavedAt.ToString("MM-dd HH:mm");

    public string FileCountText => Files.Count == 0 ? "没有文件" : $"{Files.Count} 个文件";

    public string? PrimaryFile => Files.FirstOrDefault(f => f.EndsWith(".edb", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        ?? Files.FirstOrDefault();
}

/// <summary>
/// 本机板书索引（%ProgramData%\ClassroomKit\board-index.json）。
/// 续用提示、板书页历史和诊断包都读它；损坏时按空索引处理，绝不阻止启动。
/// </summary>
public sealed class BoardArchive
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly List<BoardRecord> _records;
    private readonly string _path;

    public BoardArchive(string path, int maxRecords = 100)
    {
        _path = path;
        MaxRecords = Math.Clamp(maxRecords, 10, 1000);
        _records = Load(path);
    }

    public int MaxRecords { get; }

    public string Path => _path;

    public int Count
    {
        get { lock (_gate) { return _records.Count; } }
    }

    public void Add(BoardRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Id)) return;
        lock (_gate)
        {
            _records.RemoveAll(x => string.Equals(x.Id, record.Id, StringComparison.Ordinal));
            _records.Insert(0, record);
            if (_records.Count > MaxRecords) _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
            SaveLocked();
        }
    }

    public BoardRecord? LatestFor(string? subject, int lessonIndex, DateTimeOffset localDate)
    {
        if (string.IsNullOrWhiteSpace(subject) || lessonIndex <= 0) return null;
        lock (_gate)
        {
            return _records.FirstOrDefault(x =>
                x.LessonIndex == lessonIndex
                && string.Equals(x.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase)
                && x.SavedAt.ToLocalTime().Date == localDate.ToLocalTime().Date);
        }
    }

    public IReadOnlyList<BoardRecord> Recent(int count)
    {
        lock (_gate)
        {
            return _records.Take(Math.Clamp(count, 0, MaxRecords)).ToArray();
        }
    }

    public BoardRecord? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            return _records.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        }
    }

    private static List<BoardRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<BoardRecord>();
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<BoardRecord>>(json, Json);
            return list?.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Id)).ToList()
                   ?? new List<BoardRecord>();
        }
        catch
        {
            // 坏索引只影响历史/续用，不影响保存本身
            return new List<BoardRecord>();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Json));
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        catch
        {
            // 索引写失败不影响板书文件本身；下次追加时再试
        }
    }
}
