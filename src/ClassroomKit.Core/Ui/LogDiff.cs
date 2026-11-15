namespace ClassroomKit.Core.Ui;

/// <summary>
/// 日志增量合并：守护每秒给的是「最近 N 条」，直接 Clear+Add 会让正在翻看历史的人被拽回顶部。
/// 这里找出旧列表尾部与新列表头部的重叠段，只把新增的行接上去；找不到重叠（进程重启等）才整体替换。
/// </summary>
public static class LogDiff
{
    public static IReadOnlyList<UiLogLine> Merge(
        IReadOnlyList<UiLogLine> existing,
        IReadOnlyList<UiLogLine> incoming,
        int max = 200)
    {
        if (incoming.Count == 0) return Trim(existing, max);
        if (existing.Count == 0) return Trim(incoming, max);

        var limit = Math.Min(existing.Count, incoming.Count);
        for (var k = limit; k > 0; k--)
        {
            var matched = true;
            for (var i = 0; i < k; i++)
            {
                if (!SameLine(existing[existing.Count - k + i], incoming[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched) continue;
            if (k == incoming.Count) return Trim(existing, max); // 没有新增行

            var merged = new List<UiLogLine>(existing.Count + incoming.Count - k);
            merged.AddRange(existing);
            for (var i = k; i < incoming.Count; i++) merged.Add(incoming[i]);
            return Trim(merged, max);
        }

        return Trim(incoming, max);
    }

    public static IReadOnlyList<UiLogLine> Trim(IReadOnlyList<UiLogLine> lines, int max)
    {
        if (max <= 0 || lines.Count <= max) return lines;
        var result = new UiLogLine[max];
        for (var i = 0; i < max; i++) result[i] = lines[lines.Count - max + i];
        return result;
    }

    private static bool SameLine(UiLogLine a, UiLogLine b) =>
        a.Time == b.Time && a.Level == b.Level && string.Equals(a.Message, b.Message, StringComparison.Ordinal);
}
