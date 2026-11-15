namespace ClassroomKit.Core.Ui;

/// <summary>下拉选项：Value 是写进配置的原值，Label 是给人看的中文。</summary>
public sealed record UiOption(string Value, string Label)
{
    /// <summary>ComboBox 默认用 ToString 渲染条目，这里直接给中文标签。</summary>
    public override string ToString() => Label;
}

public static class UiOptions
{
    public const string TimeoutSave = "save";
    public const string TimeoutSaveClose = "save-close";
    public const string TimeoutWait = "wait";
    public const string TimeoutDismiss = "dismiss";

    public static IReadOnlyList<UiOption> TimeoutActions { get; } = new[]
    {
        new UiOption(TimeoutSave, "自动保存板书（不关闭 ClassInX）"),
        new UiOption(TimeoutSaveClose, "自动保存并关闭 ClassInX"),
        new UiOption(TimeoutWait, "一直等到手动选择"),
        new UiOption(TimeoutDismiss, "自动忽略")
    };

    public static IReadOnlyList<UiOption> Themes { get; } = new[]
    {
        new UiOption("system", "跟随系统"),
        new UiOption("light", "浅色"),
        new UiOption("dark", "深色")
    };

    public static string TimeoutLabel(string? value) => Label(TimeoutActions, value, TimeoutSave);

    public static string ThemeLabel(string? value) => Label(Themes, value, "system");

    /// <summary>把配置里的原值收敛到已知选项；未知值回退 fallback。</summary>
    public static string Normalize(string? value, IReadOnlyList<UiOption> options, string fallback)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        foreach (var option in options)
        {
            if (string.Equals(option.Value, text, StringComparison.Ordinal)) return option.Value;
        }

        return fallback;
    }

    private static string Label(IReadOnlyList<UiOption> options, string? value, string fallback)
    {
        var normalized = Normalize(value, options, fallback);
        foreach (var option in options)
        {
            if (string.Equals(option.Value, normalized, StringComparison.Ordinal)) return option.Label;
        }

        return fallback;
    }
}
