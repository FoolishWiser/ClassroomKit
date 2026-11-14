using ClassroomKit.Core.Config;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>
/// 读守护 config.json 的令牌。0.4.0 的 401 就是因为按 Token 找、而文件里是 token。
/// </summary>
internal static class AgentConfigTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ck-agent-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task RunAsync()
    {
        await Run("守护配置：camelCase 的 token 能读到（大小写不敏感）", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            File.WriteAllText(path, "{ \"port\": 36037, \"token\": \"abc123\" }");
            Eq("abc123", AgentConfigReader.ReadToken(path), "读到的令牌");
        });

        await Run("守护配置：PascalCase 的 Token 也能读到", () =>
        {
            var path = Path.Combine(TempDir(), "config.json");
            File.WriteAllText(path, "{ \"Token\": \"xyz789\" }");
            Eq("xyz789", AgentConfigReader.ReadToken(path), "读到的令牌");
        });

        await Run("守护配置：缺字段 / 空值 / 坏 JSON 一律返回 null", () =>
        {
            var dir = TempDir();
            var missing = Path.Combine(dir, "missing.json");
            File.WriteAllText(missing, "{ \"port\": 1 }");
            Eq<string?>(null, AgentConfigReader.ReadToken(missing), "没有 token 字段");

            var empty = Path.Combine(dir, "empty.json");
            File.WriteAllText(empty, "{ \"token\": \"  \" }");
            Eq<string?>(null, AgentConfigReader.ReadToken(empty), "空值不算令牌");

            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ token: ");
            Eq<string?>(null, AgentConfigReader.ReadToken(broken), "坏 JSON 不抛异常");

            Eq<string?>(null, AgentConfigReader.ReadToken(Path.Combine(dir, "nope.json")), "文件不存在");
        });

        await Run("守护配置：默认路径在 ProgramData\\ClassroomKit 下", () =>
        {
            Contains(AgentConfigReader.DefaultPath, "ClassroomKit", "路径包含程序目录名");
            True(AgentConfigReader.DefaultPath.EndsWith("config.json", StringComparison.OrdinalIgnoreCase), "文件名");
        });
    }
}
