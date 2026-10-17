namespace ClassroomKit.Tests;

/// <summary>不依赖任何测试框架的极简断言器：dotnet run 即可，退出码非 0 表示有失败。</summary>
internal static class Harness
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    public static async Task Run(string name, Func<Task> body)
    {
        try
        {
            await body();
            _passed++;
            Console.WriteLine($"  [OK]   {name}");
        }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"         {ex.Message}");
        }
    }

    public static Task Run(string name, Action body) => Run(name, () =>
    {
        body();
        return Task.CompletedTask;
    });

    public static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{(what.Length > 0 ? what + " " : "")}期望 {Show(expected)}，实际 {Show(actual)}");
    }

    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception($"{what}（期望为真）");
    }

    public static void False(bool condition, string what)
    {
        if (condition) throw new Exception($"{what}（期望为假）");
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null) throw new Exception($"{what} 不应为空");
    }

    public static void Contains(string haystack, string needle, string what = "")
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
            throw new Exception($"{(what.Length > 0 ? what + " " : "")}应包含 {needle}，实际 {haystack}");
    }

    private static string Show<T>(T value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        _ => value.ToString() ?? "null"
    };

    public static int Report()
    {
        Console.WriteLine();
        if (Failures.Count == 0)
        {
            Console.WriteLine($"全部通过：{_passed} 项");
            return 0;
        }

        Console.WriteLine($"通过 {_passed} 项，失败 {Failures.Count} 项：");
        foreach (var f in Failures) Console.WriteLine("  - " + f);
        return 1;
    }
}
