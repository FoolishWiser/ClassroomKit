using System.Text;

namespace ClassroomKit.Core.Hosts;

/// <summary>一条 hosts 记录：一个 IP 对应若干域名。</summary>
public sealed record HostEntry(string Ip, IReadOnlyList<string> Hosts);

/// <summary>
/// hosts 文件里「受管区块」的合并 / 提取 / 还原。纯字符串逻辑，便于单测。
/// 标记行保持 ASCII：hosts 在中文 Windows 上是 ANSI(936)，写中文注释会变乱码。
/// </summary>
public static class HostsBlock
{
    public const string StartMarker = "# >>> ClassroomKit hosts accelerator (auto-generated) >>>";
    public const string EndMarker = "# <<< ClassroomKit hosts accelerator <<<";

    public static bool HasBlock(string? text) => (text ?? "").Contains(StartMarker, StringComparison.Ordinal);

    /// <summary>把受管区块放到文件最前面（Windows 按顺序匹配，置顶才不会被旧记录抢走），原有内容原样保留。</summary>
    public static string Merge(string? original, IReadOnlyList<HostEntry> entries)
    {
        var text = original ?? "";
        var nl = Newline(text);
        var rest = Restore(text).Trim('\r', '\n', ' ', '\t');
        if (entries.Count == 0) return rest.Length == 0 ? "" : rest + nl;

        var sb = new StringBuilder();
        sb.Append(StartMarker).Append(nl);
        sb.Append("# Managed by ClassroomKit. Remove with: restore hosts.").Append(nl);
        foreach (var entry in entries)
            sb.Append(entry.Ip).Append(' ').Append(string.Join(' ', entry.Hosts)).Append(nl);
        sb.Append(EndMarker);
        if (rest.Length == 0) return sb.Append(nl).ToString();
        return sb.Append(nl).Append(nl).Append(rest).Append(nl).ToString();
    }

    /// <summary>删除受管区块，其余内容原样返回。区块不完整（被手改坏了）时切到文件末尾。</summary>
    public static string Restore(string? original)
    {
        var text = original ?? "";
        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0) return text;

        var nl = Newline(text);
        var lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r') lineStart--;

        var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
        int cutEnd;
        if (end >= 0)
        {
            cutEnd = end + EndMarker.Length;
            while (cutEnd < text.Length && text[cutEnd] != '\n' && text[cutEnd] != '\r') cutEnd++;
            if (cutEnd < text.Length && text[cutEnd] == '\r') cutEnd++;
            if (cutEnd < text.Length && text[cutEnd] == '\n') cutEnd++;
        }
        else cutEnd = text.Length;

        var body = (text[..lineStart] + text[cutEnd..]).Trim('\r', '\n');
        return body.Length == 0 ? "" : body + nl;
    }

    /// <summary>读出受管区块里的记录（区块不存在或残缺返回空）。</summary>
    public static IReadOnlyList<HostEntry> Extract(string? text)
    {
        var raw = text ?? "";
        var start = raw.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start < 0) return Array.Empty<HostEntry>();
        var bodyStart = start + StartMarker.Length;
        var end = raw.IndexOf(EndMarker, bodyStart, StringComparison.Ordinal);
        return ParseEntries(end >= 0 ? raw[bodyStart..end] : raw[bodyStart..]);
    }

    /// <summary>解析一段 hosts 文本（忽略注释、空行、非 IPv4、非法行）。</summary>
    public static IReadOnlyList<HostEntry> ParseEntries(string text)
    {
        var list = new List<HostEntry>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            if (!System.Net.IPAddress.TryParse(tokens[0], out var ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
            list.Add(new HostEntry(tokens[0], tokens.Skip(1).ToArray()));
        }
        return list;
    }

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
