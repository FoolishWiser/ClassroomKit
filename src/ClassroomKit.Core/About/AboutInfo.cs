namespace ClassroomKit.Core.About;

/// <summary>关于页用到的固定地址与展示格式（纯函数，方便单测）。</summary>
public static class AboutInfo
{
    public const string DefaultRepo = "FoolishWiser/ClassroomKit";
    public const string ClassIslandSiteUrl = "https://classisland.tech/";

    public static string RepoUrl(string repo) => "https://github.com/" + NormalizeRepo(repo);

    public static string ReleasesUrl(string repo) => RepoUrl(repo) + "/releases/latest";

    public static string StarsUrl(string repo) => RepoUrl(repo) + "/stargazers";

    /// <summary>GitHub API：取 stargazers_count。</summary>
    public static string ApiUrl(string repo) => "https://api.github.com/repos/" + NormalizeRepo(repo);

    /// <summary>Star 数展示：null → 「—」，1234 → 「1.2k」，12000 → 「12k」。</summary>
    public static string FormatStars(int? stars)
    {
        if (stars is null || stars < 0) return "—";
        var value = stars.Value;
        if (value < 1000) return value.ToString();

        var thousands = value / 1000.0;
        return thousands >= 10
            ? ((int)Math.Round(thousands)).ToString() + "k"
            : thousands.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k";
    }

    private static string NormalizeRepo(string? repo) =>
        string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim().Trim('/');
}
