namespace ClassroomKit.Core.Onboarding;

/// <summary>教程引导的一步：标题/正文/「去看看」按钮，以及它指向的页面 key。</summary>
public sealed record OnboardingStep(string Key, string Page, string Title, string Body, string Action);

/// <summary>
/// 首次启动的教程引导（0.7.0 起）。纯数据 + 纯函数，方便测试；
/// 桌面端只负责把它画成遮罩卡片，并记录用户是否点过完成/跳过。
/// 只在「空白配置 + 没看过」时自动弹：升级上来的老机器不会突然被挡屏。
/// </summary>
public static class OnboardingGuide
{
    /// <summary>引导内容版本：以后改步骤时可以据此决定要不要给老用户再弹一次。</summary>
    public const int Version = 1;

    /// <summary>四步：开热点 → 关联课表 → 冲突与板书 → 跑一次自检。</summary>
    public static IReadOnlyList<OnboardingStep> Steps { get; } = new[]
    {
        new OnboardingStep(
            "hotspot",
            "hotspot",
            "第 1 步 · 先把热点开起来",
            "在「热点与网络」确认上行接的是那根网线，再把热点名字和密码设成老师记得住的样子。"
            + "默认会跟随本机名、密码随机生成，改完点保存就会写进系统热点。",
            "去设置热点"),
        new OnboardingStep(
            "lesson",
            "lesson",
            "第 2 步 · 关联课表与科目",
            "装好 ClassIsland 和 ClassroomKit 插件后，在「课表与板书」勾选要生效的科目；"
            + "课前会按课表提醒保存板书，没勾选就代表所有科目都提醒。",
            "去看看课表"),
        new OnboardingStep(
            "board",
            "advanced",
            "第 3 步 · 冲突与老师习惯",
            "两款白板软件同时开着时会弹冲突提示。想更省心，就到「高级」里按科目记住老师习惯："
            + "希沃、WPS 还是 ClassIn，冲突时是弹窗交给他，还是静默关掉另一款。",
            "去高级页设习惯"),
        new OnboardingStep(
            "diagnose",
            "diagnose",
            "第 4 步 · 跑一次自检",
            "最后到「自检与日志」点开始自检，确认热点、护航任务、hosts 加速都正常；"
            + "哪一项红了，日志里会写清原因。以后想再看这份教程，到「设置 · 关于」点「重新显示教程引导」。",
            "去自检")
    };

    /// <summary>
    /// 没看过教程就弹：老配置在加载时已经被盖章成「已看过」（见 ConfigStore），
    /// 所以这里只看 done——空白配置中途关掉面板，下次还会接着引导；设置里点「重新显示」也会在下次启动继续出现。
    /// </summary>
    public static bool ShouldShow(bool done) => !done;

    /// <summary>页面 key 是否是桌面端存在的页面（防止引导里写错 key 切到空白页）。</summary>
    public static bool IsKnownPage(string page) => page is "overview" or "hotspot" or "lesson"
        or "update" or "diagnose" or "advanced" or "settings";
}
