using System.Text.Json;

namespace ClassroomKit.Core.Update;

/// <summary>GitHub Release 里一个可下载的资产。</summary>
public sealed record ReleaseAsset
{
    public string Name { get; init; } = "";
    public long Size { get; init; }
    public string Url { get; init; } = "";
    /// <summary>GitHub 现在会给 "sha256:…" 形式的摘要；老资产可能没有。</summary>
    public string? Digest { get; init; }
}

/// <summary>从 GitHub API 解析出来的一个 release。纯数据，方便单测。</summary>
public sealed record GitHubRelease
{
    public string Tag { get; init; } = "";
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public bool Prerelease { get; init; }
    public bool Draft { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = Array.Empty<ReleaseAsset>();

    /// <summary>去掉 v 前缀的版本号，例如 v0.2.2 → 0.2.2。</summary>
    public string Version => UpdateVersion.Normalize(Tag);
}

/// <summary>版本号比较 + release JSON 解析 + 选安装包。不联网，便于单测。</summary>
public static class UpdateVersion
{
    /// <summary>去掉前缀 v / V 和两侧空白。</summary>
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var text = version.Trim();
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsDigit(text[1])) text = text[1..];
        return text;
    }

    /// <summary>
    /// 比较两个版本号：返回 -1 / 0 / 1。只比较数字段（1.2.3），后缀（-beta1）按"比正式版旧"处理。
    /// 任何解析不了的地方都按 0 处理，避免因为奇怪的 tag 把用户卡在"永远有新版本"。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        for (var i = 0; i < Math.Max(a.Numbers.Length, b.Numbers.Length); i++)
        {
            var x = i < a.Numbers.Length ? a.Numbers[i] : 0;
            var y = i < b.Numbers.Length ? b.Numbers[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }

        // 数字段相同：带预发布后缀的比正式版旧（0.3.0-beta < 0.3.0）
        if (a.Prerelease == b.Prerelease) return 0;
        return a.Prerelease ? -1 : 1;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static (int[] Numbers, bool Prerelease) Parse(string? version)
    {
        var text = Normalize(version);
        if (text.Length == 0) return (Array.Empty<int>(), false);

        var prerelease = text.Contains('-') || text.Contains('+');
        var core = text;
        var cut = core.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) core = core[..cut];

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            numbers.Add(int.TryParse(digits, out var n) ? n : 0);
        }
        return (numbers.ToArray(), prerelease);
    }
}

/// <summary>解析 GitHub Releases API 的 JSON（单条或数组），并挑出 Windows 安装包。</summary>
public static class ReleaseFeed
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>解析 /releases/latest 返回的单个对象。</summary>
    public static GitHubRelease? ParseOne(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return ReadRelease(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 /releases 返回的数组；跳过 draft，按 includePrerelease 过滤后取版本最高的一个。</summary>
    public static GitHubRelease? ParseList(string json, bool includePrerelease = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            GitHubRelease? best = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var release = ReadRelease(item);
                if (release is null || release.Draft) continue;
                if (release.Prerelease && !includePrerelease) continue;
                if (best is null || UpdateVersion.Compare(release.Version, best.Version) > 0) best = release;
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GitHubRelease? ReadRelease(JsonElement element)
    {
        var tag = Str(element, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var assets = new List<ReleaseAsset>();
        if (element.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in arr.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                var name = Str(asset, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                assets.Add(new ReleaseAsset
                {
                    Name = name,
                    Size = asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                        ? size.GetInt64()
                        : 0,
                    Url = Str(asset, "browser_download_url"),
                    Digest = Str(asset, "digest") is { Length: > 0 } d ? d : null
                });
            }
        }

        return new GitHubRelease
        {
            Tag = tag,
            Name = Str(element, "name"),
            Notes = Str(element, "body"),
            HtmlUrl = Str(element, "html_url"),
            Prerelease = element.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
            Draft = element.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            PublishedAt = DateTimeOffset.TryParse(Str(element, "published_at"), out var at) ? at : null,
            Assets = assets
        };
    }

    /// <summary>
    /// 选 MSI：优先 ClassroomKit-&lt;版本&gt;-x64.msi，其次任意 .msi，最后任意 .zip（绿色版）。
    /// 找不到可用资产返回 null。
    /// </summary>
    public static ReleaseAsset? PickInstaller(GitHubRelease release)
    {
        var msis = release.Assets
            .Where(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && a.Url.Length > 0)
            .ToList();
        if (msis.Count > 0)
        {
            var exact = msis.FirstOrDefault(a =>
                a.Name.Contains(release.Version, StringComparison.OrdinalIgnoreCase));
            return exact ?? msis[0];
        }

        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Url.Length > 0);
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
