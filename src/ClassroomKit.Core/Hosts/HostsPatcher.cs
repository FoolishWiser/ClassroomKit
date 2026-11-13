using System.Text;

namespace ClassroomKit.Core.Hosts;

/// <summary>
/// hosts 文件的读写：按字节保真（有 UTF-8 BOM 就按 UTF-8，没有就按 Latin1 原样往返），
/// 写入用「同目录临时文件 + 覆盖移动」，避免写一半把网络搞坏；改之前留一份 hosts.classroomkit-original。
/// </summary>
public static class HostsPatcher
{
    public const string BackupSuffix = ".classroomkit-original";

    public static string DefaultPath
    {
        get
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.IsNullOrWhiteSpace(win)) win = @"C:\Windows\System32";
            return Path.Combine(win, "drivers", "etc", "hosts");
        }
    }

    public static (string Text, bool Utf8Bom) Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), true);
        return (Encoding.Latin1.GetString(bytes), false);
    }

    public static void Write(string path, string text, bool utf8Bom)
    {
        var encoding = utf8Bom ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : Encoding.Latin1;
        var temp = path + ".classroomkit-tmp";
        File.WriteAllBytes(temp, encoding.GetBytes(text));
        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { /* 忽略 */ }
        }
    }

    /// <summary>第一次改 hosts 前留一份原样备份；已经备份过就不再覆盖。</summary>
    public static string? BackupOnce(string path)
    {
        var dest = path + BackupSuffix;
        if (File.Exists(dest)) return dest;
        File.Copy(path, dest, overwrite: false);
        return dest;
    }
}
