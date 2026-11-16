using System.Text.Json;

namespace ClassroomKit.Core.Lesson;

/// <summary>
/// 从 ClassIsland 的档案文件里读出全部科目名。
/// ClassIsland 把档案存在 data\\Profiles\\*.json（便携版）或安装目录的 Profiles\\*.json，
/// 结构是 { "Subjects": { "&lt;guid&gt;": { "Name": "语文", ... } }, ... }。
/// 这里只读不写：插件没装、插件版本旧、ClassIsland 刚启动还没推送时，桌面端也能列出所有科目。
/// </summary>
public static class ClassIslandProfileSubjects
{
    /// <summary>读一个档案目录下的所有 *.json，合并科目名（大小写不敏感去重）。目录不存在返回空。</summary>
    public static IReadOnlyList<string> ReadDirectory(string? profilesDir)
    {
        if (string.IsNullOrWhiteSpace(profilesDir) || !Directory.Exists(profilesDir))
            return Array.Empty<string>();

        var names = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(profilesDir, "*.json")
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                names.AddRange(ReadFile(file));
            }
        }
        catch
        {
            // 目录没权限 / 文件被占用：当作没读到，下一次再试
        }

        return LessonSubjects.Normalize(names);
    }

    /// <summary>读单个档案文件；坏文件返回空，不抛异常。</summary>
    public static IReadOnlyList<string> ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>从档案 JSON 文本里取科目名（给测试和调用方复用）。</summary>
    public static IReadOnlyList<string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var names = new List<string>();
            Collect(doc.RootElement, names, 0);
            return LessonSubjects.Normalize(names);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void Collect(JsonElement element, List<string> names, int depth)
    {
        if (depth > 3 || element.ValueKind != JsonValueKind.Object) return;

        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, "subjects", StringComparison.OrdinalIgnoreCase))
            {
                var before = names.Count;
                CollectSubjects(prop.Value, names);
                if (names.Count > before) return;
                continue;
            }

            // 档案根上就有 Subjects；再往下找两层兜住旧版/集控档案的嵌套写法
            if (depth < 2) Collect(prop.Value, names, depth + 1);
        }
    }

    private static void CollectSubjects(JsonElement value, List<string> names)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in value.EnumerateObject()) AddName(item.Value, names);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AddName(item, names);
        }
    }

    private static void AddName(JsonElement item, List<string> names)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text)) names.Add(text);
            return;
        }

        if (item.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in item.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "name", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.String) continue;
            var text = prop.Value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) names.Add(text);
            return;
        }
    }
}
