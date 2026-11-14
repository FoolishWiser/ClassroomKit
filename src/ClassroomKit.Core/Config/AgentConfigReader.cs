using System.Text.Json;

namespace ClassroomKit.Core.Config;

/// <summary>
/// 只读守护进程 config.json 里的访问令牌。
/// 为什么单独放这里：守护序列化配置用的是 camelCase（token），而 ClassIsland 插件早期按 Token 找，
/// 大小写不匹配就读不到令牌 → 每个请求 401。这里做大小写不敏感匹配，并留出可测试的入口。
/// </summary>
public static class AgentConfigReader
{
    /// <summary>守护的默认配置位置：%ProgramData%\ClassroomKit\config.json。</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ClassroomKit",
        "config.json");

    public static string? ReadTokenFromDefaultPath() => ReadToken(DefaultPath);

    /// <summary>读令牌；文件不存在、字段缺失、为空或 JSON 坏了都返回 null。</summary>
    public static string? ReadToken(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "token", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind != JsonValueKind.String) return null;
                var token = property.Value.GetString();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
