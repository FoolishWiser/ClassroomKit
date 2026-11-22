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

    private static readonly Regex QueryToken = new(@"(?i)([?&]t=)[^&\s]+", RegexOptions.Compiled);
    private static readonly Regex Bearer = new(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/-]+", RegexOptions.Compiled);

    public static string Redact(string text, IEnumerable<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = SecretPair.Replace(text, "$1=***");
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