namespace ClassroomKit.Core.Ui;

/// <summary>桌面日志行：结构化，便于着色与筛选（守护侧仍用 AppLog 的文本格式落盘）。</summary>
public sealed record UiLogLine(DateTimeOffset Time, string Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        "ERROR" => "错误",
        "WARN" => "警告",
        "DEBUG" => "调试",
        _ => "信息"
    };

    public bool IsError => string.Equals(Level, "ERROR", StringComparison.OrdinalIgnoreCase);
    public bool IsWarn => string.Equals(Level, "WARN", StringComparison.OrdinalIgnoreCase);
    public bool IsDebug => string.Equals(Level, "DEBUG", StringComparison.OrdinalIgnoreCase);

    public string Format() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Level}] {Message}";
}
