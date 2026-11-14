namespace ClassroomKit.ClassIsland.Settings;

/// <summary>插件自己的设置：怎么连上本机的 ClassroomKit 守护进程，以及课表联动的本地副本。</summary>
public sealed class PluginSettings
{
    /// <summary>守护进程的地址；默认端口与守护一致。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:36037";

    /// <summary>访问令牌；留空时自动读取 %ProgramData%\ClassroomKit\config.json 里的令牌。</summary>
    public string Token { get; set; } = "";

    // ---- 课表联动 ----
    // 真正生效的值存在守护的 config.json 的 lesson 段里（设置页保存时经 lesson-config 动作写过去）。
    // 这里留一份副本，守护没运行时设置页还能显示上次填过的内容。

    /// <summary>上课前多少分钟提醒（1–30）。</summary>
    public int LessonRemindMinutes { get; set; } = 5;

    /// <summary>提醒弹窗停留多少秒（5–600）。</summary>
    public int LessonDialogSeconds { get; set; } = 30;

    /// <summary>弹窗超时动作：save（自动保存）/ save-close（保存并关闭）/ dismiss（忽略本次）/ wait（不自动关闭）。</summary>
    public string LessonTimeoutAction { get; set; } = "save";

    /// <summary>点「稍后提醒」后隔多少分钟再弹（1–60）。</summary>
    public int LessonSnoozeMinutes { get; set; } = 2;

    /// <summary>板书改名模板，占位符 {科目} {日期} {时间} {节次} {周几}。</summary>
    public string BoardNameTemplate { get; set; } = "{科目}_{日期}_{时间}";

    /// <summary>保存板书后是否关闭 ClassInX。</summary>
    public bool BoardCloseAfterSave { get; set; } = true;

    /// <summary>把范围收进守护认可的区间，坏值回落到默认。</summary>
    public void Normalize()
    {
        LessonRemindMinutes = Math.Clamp(LessonRemindMinutes <= 0 ? 5 : LessonRemindMinutes, 1, 30);
        LessonDialogSeconds = Math.Clamp(LessonDialogSeconds <= 0 ? 30 : LessonDialogSeconds, 5, 600);
        LessonSnoozeMinutes = Math.Clamp(LessonSnoozeMinutes <= 0 ? 2 : LessonSnoozeMinutes, 1, 60);
        if (string.IsNullOrWhiteSpace(BoardNameTemplate)) BoardNameTemplate = "{科目}_{日期}_{时间}";
        BoardNameTemplate = BoardNameTemplate.Trim();
        LessonTimeoutAction = (LessonTimeoutAction ?? "").Trim().ToLowerInvariant() switch
        {
            "save" => "save",
            "save-close" or "saveclose" => "save-close",
            "dismiss" or "ignore" => "dismiss",
            "wait" => "wait",
            _ => "save",
        };
    }

    public PluginSettings Clone() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        LessonRemindMinutes = LessonRemindMinutes,
        LessonDialogSeconds = LessonDialogSeconds,
        LessonTimeoutAction = LessonTimeoutAction,
        LessonSnoozeMinutes = LessonSnoozeMinutes,
        BoardNameTemplate = BoardNameTemplate,
        BoardCloseAfterSave = BoardCloseAfterSave,
    };
}
