using System.Text.Json;
using ClassroomKit.Core.Lesson;

namespace ClassroomKit.Core.Board;

/// <summary>一次“下一节同科目，是否续用上一节板书”的候选。</summary>
public sealed record BoardReuseTarget(
    string Key,
    string Subject,
    int LessonIndex,
    int PreviousLessonIndex,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    BoardRecord Record,
    string Text);

/// <summary>续用提示的去重/稍后/这次不用状态。</summary>
public sealed record BoardReuseState(
    string TargetKey = "",
    string LastPromptedKey = "",
    string DismissedKey = "",
    DateTimeOffset? SnoozedUntil = null)
{
    public bool ShouldPrompt(BoardReuseTarget target, DateTimeOffset now)
    {
        if (target is null) return false;
        if (string.Equals(DismissedKey, target.Key, StringComparison.Ordinal)) return false;
        if (string.Equals(LastPromptedKey, target.Key, StringComparison.Ordinal)) return false;
        return SnoozedUntil is null || SnoozedUntil <= now;
    }

    public BoardReuseState ForTarget(BoardReuseTarget target)
    {
        if (string.Equals(TargetKey, target.Key, StringComparison.Ordinal)) return this;
        return new BoardReuseState(target.Key);
    }

    public BoardReuseState MarkPrompted(BoardReuseTarget target)
        => this with { TargetKey = target.Key, LastPromptedKey = target.Key, SnoozedUntil = null };

    public BoardReuseState Snooze(DateTimeOffset until)
        => this with { LastPromptedKey = "", SnoozedUntil = until };

    public BoardReuseState Dismiss(BoardReuseTarget target)
        => this with { TargetKey = target.Key, DismissedKey = target.Key, LastPromptedKey = "", SnoozedUntil = null };
}

/// <summary>
/// 纯逻辑：从课表快照里判断“上一节同科目、下一节还是它”的续用窗口。
/// 课间窗口 = 上一节下课 → 下一节下课；上课窗口 = 本节开始 → 本节下课。
/// </summary>
public static class BoardReusePlanner
{
    public static BoardReuseTarget? FromSnapshot(LessonSnapshot snapshot, BoardArchive archive, DateTimeOffset now)
    {
        if (snapshot is null || archive is null) return null;
        if (!snapshot.PlanLoaded) return null;

        string? subject;
        int targetIndex;
        int previousIndex;
        DateTimeOffset start;
        DateTimeOffset end;

        if (snapshot.State == LessonStates.Breaking)
        {
            subject = snapshot.NextSubject;
            targetIndex = snapshot.NextLessonIndex > 0 ? snapshot.NextLessonIndex : snapshot.LessonIndex + 1;
            previousIndex = snapshot.PreviousLessonIndex > 0 ? snapshot.PreviousLessonIndex : snapshot.LessonIndex;
            start = snapshot.CurrentEndAt ?? now;
            end = snapshot.NextEndAt
                  ?? (snapshot.NextStartAt is { } nextStart ? nextStart.AddMinutes(45) : now.AddMinutes(45));

            // 下一节必须和上一节同科目；插件没给上一节时交给档案按科目匹配兜底。
            if (!string.IsNullOrWhiteSpace(snapshot.PreviousSubject)
                && !SameSubject(snapshot.PreviousSubject, subject)) return null;
            if (!string.IsNullOrWhiteSpace(snapshot.NextSubject)
                && !SameSubject(snapshot.NextSubject, subject)) return null;
        }
        else if (snapshot.State == LessonStates.Class)
        {
            subject = snapshot.CurrentSubject;
            targetIndex = snapshot.LessonIndex;
            previousIndex = snapshot.PreviousLessonIndex > 0 ? snapshot.PreviousLessonIndex : snapshot.LessonIndex - 1;
            start = snapshot.CurrentStartAt ?? now.AddMinutes(-45);
            end = snapshot.CurrentEndAt ?? now;

            // 上一节必须有同科目记录；插件没给 PreviousSubject 时允许按档案匹配。
            if (!string.IsNullOrWhiteSpace(snapshot.PreviousSubject)
                && !SameSubject(snapshot.PreviousSubject, subject)) return null;
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(subject) || targetIndex <= 0 || previousIndex <= 0) return null;
        if (now < start.AddMinutes(-2) || now > end.AddMinutes(1)) return null;

        var record = archive.LatestFor(subject, previousIndex, start.ToLocalTime().Date);
        if (record is null) return null;

        var text = $"{subject.Trim()}老师您好，发现您在{record.WeekdayText}的第 {previousIndex} 节{subject.Trim()}课中有板书保存，是否续用？";
        var key = $"{start:yyyyMMdd}|{previousIndex}|{targetIndex}|{subject.Trim()}|{record.Id}";
        return new BoardReuseTarget(key, subject.Trim(), targetIndex, previousIndex, start, end, record, text);
    }

    public static string WeekdayText(DateTimeOffset at) => "周" + "日一二三四五六"[(int)at.DayOfWeek];

    private static bool SameSubject(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>续用状态落盘；坏文件按空状态处理。</summary>
public sealed class BoardReuseStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;

    public BoardReuseStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public BoardReuseState Current { get; private set; }

    public void Save(BoardReuseState state)
    {
        lock (_gate)
        {
            Current = state ?? new BoardReuseState();
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                    File.Move(tmp, _path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch
            {
                // 状态写失败不影响保存/提示；下一次再试
            }
        }
    }

    private static BoardReuseState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new BoardReuseState();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BoardReuseState>(json, Json) ?? new BoardReuseState();
        }
        catch
        {
            return new BoardReuseState();
        }
    }
}
