using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassroomKit.Core.Support;

/// <summary>
/// 诊断包：把自检、环境、配置摘要和最近日志打成一个 zip 交给老师转发。
/// 所有文本都先脱敏；entry 名只保留安全字符，避免 ../ 之类写出包外。
/// </summary>
public static class SupportBundle
{
    private static readonly Regex SecretPair = new(
        @"(?i)(token|password|passphrase|secret)\s*=\s*[^&\s,;]+",
        RegexOptions.Compiled);

    private static readonly Regex QueryToken = new(@"(?i)([?&](?:t|token|key|sig|signature)=)[^&\s]+", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/-]+", RegexOptions.Compiled);
    private static readonly Regex EscapedWindowsPath = new(@"(?i)\b[A-Z]:\\\\[^""\r\n]*", RegexOptions.Compiled);
    private static readonly Regex WindowsPath = new(@"(?i)\b[A-Z]:\\(?:[^\\\r\n""']+\\)*[^\\\r\n""']*", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"(?i)\\\\[^\\\r\n""']+\\[^\\\r\n""']+(?:\\[^\\\r\n""']*)*", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
    private static readonly Regex Mac = new(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.Compiled);

    public static string Redact(string text, IEnumerable<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // 先遮路径、IP、MAC，再遮显式密钥；否则密钥替换会把路径切碎，反而露出用户名。
        var result = EscapedWindowsPath.Replace(text, "<路径>");
        result = WindowsPath.Replace(result, "<路径>");
        result = UncPath.Replace(result, "<路径>");
        result = Ipv4.Replace(result, "<IP>");
        result = Mac.Replace(result, "<MAC>");
        result = SecretPair.Replace(result, "$1=***");
        result = QueryToken.Replace(result, "$1***");
        result = Bearer.Replace(result, "$1***");
        if (secrets is not null)
        {
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 3)
                    result = result.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary>计算机名/用户名/SSID 之类只留前几个字符，够认出「是不是这台」又不泄露全名。</summary>
    public static string MaskName(string? value, int keep = 2)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";
        if (text.Length <= keep) return new string('*', Math.Max(2, text.Length));
        return text[..Math.Max(1, keep)] + "***";
    }

    /// <summary>保留网段，隐去最后一节：能看出「还是这个热点」，看不出是哪台设备。</summary>
    public static string MaskIp(string? ip)
    {
        var text = (ip ?? "").Trim();
        var parts = text.Split('.');
        if (parts.Length != 4) return text.Length == 0 ? "" : "<IP>";
        return parts[0] + "." + parts[1] + "." + parts[2] + ".***";
    }

    public static string MaskMac(string? mac) => string.IsNullOrWhiteSpace(mac) ? "" : "**:**:**:**:**:**";

    public static string Create(string path, IEnumerable<KeyValuePair<string, string>> entries,
        IEnumerable<string>? secrets = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            var entryName = Unique(SafeEntryName(name), used);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(Redact(value ?? "", secrets));
        }

        return path;
    }

    private static string SafeEntryName(string? name)
    {
        var leaf = Path.GetFileName((name ?? "").Replace('\\', '/'));
        var clean = new string((leaf.Length == 0 ? "entry.txt" : leaf)
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
            .ToArray()).Trim();
        if (clean.Length == 0 || clean is "." or "..") clean = "entry.txt";
        return clean;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = stem + "-" + i + ext;
            if (used.Add(candidate)) return candidate;
        }

        return Guid.NewGuid().ToString("N") + ext;
    }
}