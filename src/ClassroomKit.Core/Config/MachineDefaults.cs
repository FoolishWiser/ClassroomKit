using System.Security.Cryptography;
using System.Text;

namespace ClassroomKit.Core.Config;

/// <summary>
/// 按"这台机器"生成默认值，替代旧版写死的教室常量（CLASSROOM-302 / 80258025）。
/// 目的：同一个安装包发到任意教室都能直接用，笔记本漫游到别的班级也不会与隔壁热点撞名。
/// </summary>
public static class MachineDefaults
{
    /// <summary>旧的写死热点名，只用于判断"这台机器还是旧默认值"。</summary>
    public const string LegacySsid = "CLASSROOM-302";

    /// <summary>旧的写死密码，同上。</summary>
    public const string LegacyPassphrase = "80258025";

    /// <summary>SSID 的字节上限（802.11 规定 32 字节，不是 32 个字符）。</summary>
    public const int MaxSsidBytes = 32;

    /// <summary>计算机名 → 热点名：去掉控制字符与首尾空白，按 UTF-8 字节截断且不切坏汉字。</summary>
    public static string DefaultSsid()
    {
        var raw = Environment.MachineName ?? "";
        var cleaned = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return "ClassroomKit";
        return TruncateUtf8(cleaned, MaxSsidBytes);
    }

    /// <summary>随机 8 位密码：满足 WPA2 PSK 的 8 位下限，去掉 0/o/1/l/I 这类投影仪上看不清的字符。</summary>
    public static string DefaultPassphrase()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }

    /// <summary>配置里的热点名/密码是否还是旧版写死的值（只影响提示，不会自动改）。</summary>
    public static bool IsLegacy(string? ssid, string? passphrase) =>
        string.Equals(ssid, LegacySsid, StringComparison.Ordinal) &&
        string.Equals(passphrase, LegacyPassphrase, StringComparison.Ordinal);

    /// <summary>按 UTF-8 字节截断，绝不从多字节字符中间切断。</summary>
    public static string TruncateUtf8(string text, int maxBytes)
    {
        if (maxBytes <= 0 || text.Length == 0) return "";
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;

        var sb = new StringBuilder(text.Length);
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > maxBytes) break;
            sb.Append(rune.ToString());
            used += size;
        }
        return sb.ToString();
    }
}
