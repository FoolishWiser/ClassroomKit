using ClassroomKit.Core.Update;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>GitHub Release 解析、版本比较、安装包选择——纯逻辑，不联网。</summary>
internal static class UpdateTests
{
    private const string SampleLatest = """
{
  "tag_name": "v0.2.2",
  "name": "ClassroomKit 0.2.2",
  "body": "修复最小化时保存板书没反应；新增 GitHub 升级。",
  "html_url": "https://github.com/FoolishWiser/ClassroomKit/releases/tag/v0.2.2",
  "prerelease": false,
  "draft": false,
  "published_at": "2026-11-10T02:30:00Z",
  "assets": [
    {
      "name": "ClassroomKit-0.2.2-x64.msi",
      "size": 65642496,
      "browser_download_url": "https://github.com/FoolishWiser/ClassroomKit/releases/download/v0.2.2/ClassroomKit-0.2.2-x64.msi",
      "digest": "sha256:aaaa"
    },
    {
      "name": "ClassroomKit-0.2.2-win-x64.zip",
      "size": 66613113,
      "browser_download_url": "https://github.com/FoolishWiser/ClassroomKit/releases/download/v0.2.2/ClassroomKit-0.2.2-win-x64.zip"
    }
  ]
}
""";

    private const string SampleList = """
[
  { "tag_name": "v0.2.3-beta1", "name": "beta", "body": "", "html_url": "u3", "prerelease": true, "draft": false, "assets": [] },
  { "tag_name": "v0.2.3", "name": "draft", "body": "", "html_url": "ud", "prerelease": false, "draft": true, "assets": [] },
  { "tag_name": "v0.2.1", "name": "old", "body": "", "html_url": "u1", "prerelease": false, "draft": false, "assets": [] },
  {
    "tag_name": "v0.2.2",
    "name": "new",
    "body": "notes",
    "html_url": "u2",
    "prerelease": false,
    "draft": false,
    "assets": [
      { "name": "ClassroomKit-0.2.2-x64.msi", "size": 10, "browser_download_url": "https://x/0.2.2.msi" }
    ]
  }
]
""";

    public static async Task RunAsync()
    {
        await Run("版本号比较：补 v 前缀、多段、预发布", () =>
        {
            Eq(0, UpdateVersion.Compare("v0.2.2", "0.2.2"), "v 前缀应等价");
            Eq(1, UpdateVersion.Compare("0.2.10", "0.2.9"), "数字段按数值比较");
            Eq(-1, UpdateVersion.Compare("0.2.1", "0.2.2"), "旧版本更小");
            Eq(1, UpdateVersion.Compare("1.0", "0.9.9"), "段数不同按缺省 0 补齐");
            Eq(-1, UpdateVersion.Compare("0.3.0-beta1", "0.3.0"), "预发布版应比正式版旧");
            Eq(0, UpdateVersion.Compare("0.3.0-beta1", "0.3.0-beta2"), "预发布后缀不参与比较");
            True(UpdateVersion.IsNewer("v0.2.2", "0.2.1"), "v0.2.2 应比 0.2.1 新");
            False(UpdateVersion.IsNewer("0.2.1", "0.2.1"), "同版本不算新");
        });

        await Run("解析 /releases/latest：tag、说明、资产、摘要", () =>
        {
            var release = ReleaseFeed.ParseOne(SampleLatest);
            NotNull(release, "应能解析单条 release");
            Eq("0.2.2", release!.Version, "去掉 v 后的版本");
            Eq("ClassroomKit 0.2.2", release.Name, "release 名称");
            Contains(release.Notes, "最小化", "发布说明应保留");
            Eq("https://github.com/FoolishWiser/ClassroomKit/releases/tag/v0.2.2", release.HtmlUrl, "发布页地址");
            False(release.Prerelease, "不应是预发布");
            Eq(2, release.Assets.Count, "资产数量");
            Eq(65642496L, release.Assets[0].Size, "MSI 大小");
            Eq("sha256:aaaa", release.Assets[0].Digest, "摘要应保留");
        });

        await Run("选安装包：优先版本号匹配的 MSI，而不是 zip", () =>
        {
            var release = ReleaseFeed.ParseOne(SampleLatest)!;
            var asset = ReleaseFeed.PickInstaller(release);
            NotNull(asset, "应能选出安装包");
            Eq("ClassroomKit-0.2.2-x64.msi", asset!.Name, "应选 MSI");
        });

        await Run("选安装包：没有 MSI 时退回 zip", () =>
        {
            var release = ReleaseFeed.ParseOne(SampleLatest)! with
            {
                Assets = new[]
                {
                    new ReleaseAsset { Name = "a.txt", Url = "https://x/a.txt" },
                    new ReleaseAsset { Name = "ClassroomKit-0.2.2-win-x64.zip", Url = "https://x/z.zip" }
                }
            };
            Eq("ClassroomKit-0.2.2-win-x64.zip", ReleaseFeed.PickInstaller(release)?.Name, "应退回 zip");
        });

        await Run("解析 /releases 列表：跳过 draft 与预发布，取最高版本", () =>
        {
            var release = ReleaseFeed.ParseList(SampleList);
            NotNull(release, "应能解析列表");
            Eq("0.2.2", release!.Version, "应取最高的正式版（0.2.3 是 draft、beta 是预发布）");

            var withPre = ReleaseFeed.ParseList(SampleList, includePrerelease: true);
            Eq("0.2.3-beta1", withPre?.Version, "允许预发布时应取 beta（比 0.2.2 新）");
        });

        await Run("下载地址：直连优先，镜像按前缀拼接且去重", () =>
        {
            const string direct = "https://github.com/o/r/releases/download/v1/a.msi";
            var noMirror = UpdateUrls.Candidates(direct, null);
            Eq(1, noMirror.Count, "没有镜像时只有直连");
            Eq(direct, noMirror[0], "直连排第一");

            var urls = UpdateUrls.Candidates(direct, new[] { "https://ghproxy.net/", "https://ghproxy.net", "  ", "https://gh.example.com/prefix/" });
            Eq(3, urls.Count, "重复/空白镜像应去掉");
            Eq(direct, urls[0], "直连仍排第一");
            Eq("https://ghproxy.net/" + direct, urls[1], "镜像前缀拼接（去掉多余的斜杠）");
            Eq("https://gh.example.com/prefix/" + direct, urls[2], "保留镜像自带的路径前缀");
        });

        await Run("下载地址：GitHub API 资产接口排第一，其次直连，最后镜像", () =>
        {
            const string direct = "https://github.com/o/r/releases/download/v1/a.msi";
            const string api = "https://api.github.com/repos/o/r/releases/assets/42";
            var urls = UpdateUrls.Candidates(direct, new[] { "https://ghproxy.net/" }, api);
            Eq(3, urls.Count, "三个源");
            Eq(api, urls[0], "API 接口排第一");
            Eq(direct, urls[1], "直连第二");
            Eq("https://ghproxy.net/" + direct, urls[2], "镜像第三");
        });

        await Run("非法 JSON / 缺 tag 时返回 null，不抛异常", () =>
        {
            Eq<GitHubRelease?>(null, ReleaseFeed.ParseOne("<html>blocked</html>"), "非 JSON 应返回 null");
            Eq<GitHubRelease?>(null, ReleaseFeed.ParseOne("""{"name":"no tag"}"""), "缺 tag_name 应返回 null");
            Eq<GitHubRelease?>(null, ReleaseFeed.ParseList("{}"), "对象不是数组应返回 null");
            Eq<ReleaseAsset?>(null, ReleaseFeed.PickInstaller(new GitHubRelease { Tag = "v1.0.0" }), "没有资产应返回 null");
        });
    }
}
