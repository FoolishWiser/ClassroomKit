namespace ClassroomKit.Core.Update;

/// <summary>升级包下载地址的候选列表：直连在前，镜像前缀在后。</summary>
public static class UpdateUrls
{
    /// <summary>
    /// 生成候选下载地址。镜像只是拼在原始地址前面（ghproxy 风格），
    /// 落地后仍按 GitHub API 提供的 sha256 校验，镜像无法篡改安装包。
    /// </summary>
    public static IReadOnlyList<string> Candidates(string directUrl, IEnumerable<string>? mirrors)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var trimmed = url.Trim();
            if (!urls.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) urls.Add(trimmed);
        }

        Add(directUrl);
        if (mirrors is not null)
        {
            foreach (var mirror in mirrors)
            {
                if (string.IsNullOrWhiteSpace(mirror)) continue;
                var prefix = mirror.Trim().TrimEnd('/');
                if (prefix.Length == 0) continue;
                Add(prefix + "/" + directUrl);
            }
        }
        return urls;
    }
}
