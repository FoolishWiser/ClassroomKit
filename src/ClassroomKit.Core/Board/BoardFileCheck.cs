namespace ClassroomKit.Core.Board;

/// <summary>
/// 判断 ClassInX 保存出来的文件是不是「真的写完了」。
/// 0.8.0 之前只看文件时间，保存面板刚建出 0 字节文件就被当成成功，老师拿到的是空 PDF。
/// </summary>
public static class BoardFileCheck
{
    /// <summary>小于这个字节数一律当成没写完（正常板书 PDF 至少几 KB）。</summary>
    public const int DefaultMinBytes = 1024;

    /// <summary>
    /// 文件存在、不是空的、大小达到下限；.pdf 还要求开头是 %PDF-。
    /// 读文件失败（被占用/没权限）按「还不能算完成」处理，让调用方继续等。
    /// </summary>
    public static bool LooksComplete(string? path, int minBytes = DefaultMinBytes)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;

            var floor = Math.Max(1, minBytes);
            if (info.Length < floor) return false;

            if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Span<byte> header = stackalloc byte[5];
                var read = stream.Read(header);
                if (read < 5) return false;
                return header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D'
                    && header[3] == (byte)'F' && header[4] == (byte)'-';
            }

            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
