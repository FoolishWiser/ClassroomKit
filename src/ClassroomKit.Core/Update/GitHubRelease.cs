using System.Text.Json;

namespace ClassroomKit.Core.Update;

/// <summary>GitHub Release 里一个可下载的资产。</summary>
public sealed record ReleaseAsset
{
    public long Id { get; init; }
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
    /// 比较两个版本号：返回 -1 / 0 / 1。按 SemVer 2.0 比较（预发布阶段 0.3.0-pre.N 必需）：
    /// 先比数字段；数字相同时没有后缀的是正式版，比任何 -pre 都新；
    /// 预发布后缀按 . 分段，纯数字段按数值比、数字段比字母段旧、前缀相同则短者更旧。
    /// 构建元数据（+build）不参与比较。任何解析不了的地方按 0 处理。
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

        if (a.Pre.Length == 0 && b.Pre.Length == 0) return 0;
        if (a.Pre.Length == 0) return 1;
        if (b.Pre.Length == 0) return -1;

        for (var i = 0; i < Math.Max(a.Pre.Length, b.Pre.Length); i++)
        {
            if (i >= a.Pre.Length) return -1;
            if (i >= b.Pre.Length) return 1;

            var x = a.Pre[i];
            var y = b.Pre[i];
            var xNumeric = long.TryParse(x, out var xv);
            var yNumeric = long.TryParse(y, out var yv);

            if (xNumeric && yNumeric)
            {
                if (xv != yv) return xv < yv ? -1 : 1;
                continue;
            }
            if (xNumeric) return -1;
            if (yNumeric) return 1;

            var cmp = string.CompareOrdinal(x, y);
            if (cmp != 0) return cmp < 0 ? -1 : 1;
        }
        return 0;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static (int[] Numbers, string[] Pre) Parse(string? version)
    {
        var text = Normalize(version);
        if (text.Length == 0) return (Array.Empty<int>(), Array.Empty<string>());

        var core = text;
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];

        var pre = "";
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            pre = core[(dash + 1)..];
            core = core[..dash];
        }

        var numbers = new List<int>(3);
        foreach (var part in core.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            numbers.Add(int.TryParse(digits, out var n) ? n : 0);
        }

        var ids = pre.Length == 0 ? Array.Empty<string>() : pre.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return (numbers.ToArray(), ids);
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
                    Id = asset.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number
                        ? aid.GetInt64()
                        : 0,
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
