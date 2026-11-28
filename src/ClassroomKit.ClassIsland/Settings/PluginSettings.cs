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

    /// <summary>只在哪些科目上提醒（逗号分隔，如「物理,化学」；留空 = 所有科目）。匹配规则与守护一致。</summary>
    public string LessonSubjects { get; set; } = "";

    /// <summary>板书改名模板，占位符 {科目} {日期} {时间} {节次} {周几}。</summary>
    public string BoardNameTemplate { get; set; } = "{科目}_{节次}_{日期}_{时间}";

    /// <summary>保存板书后是否关闭 ClassInX。</summary>
    public bool BoardCloseAfterSave { get; set; } = true;

    /// <summary>把范围收进守护认可的区间，坏值回落到默认。</summary>
    public void Normalize()
    {
        LessonSubjects = (LessonSubjects ?? "").Trim();
        if (string.IsNullOrWhiteSpace(BoardNameTemplate)) BoardNameTemplate = "{科目}_{节次}_{日期}_{时间}";
        BoardNameTemplate = BoardNameTemplate.Trim();
    }

    public PluginSettings Clone() => new()
    {
        BaseUrl = BaseUrl,
        Token = Token,
        LessonSubjects = LessonSubjects,
        BoardNameTemplate = BoardNameTemplate,
        BoardCloseAfterSave = BoardCloseAfterSave,
    };
}
