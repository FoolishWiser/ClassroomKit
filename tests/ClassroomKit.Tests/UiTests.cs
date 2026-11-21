using ClassroomKit.Core.Ui;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>界面相关纯逻辑：窗口几何、日志增量、下拉选项中文标签。</summary>
internal static class UiTests
{
    private static readonly WindowRect Screen1080P150 = new(0, 0, 1280, 680);
    private static readonly WindowRect Screen1080P100 = new(0, 0, 1920, 1040);

    public static Task RunAsync() => RunAll();

    private static async Task RunAll()
    {
        await Run("150% 的 1080p 屏：760 高的窗口被夹到 640", () =>
        {
            var size = WindowLayout.Fit(1120, 760, Screen1080P150);
            Eq(1120d, size.Width, "宽度放得下就不动");
            Eq(640d, size.Height, "高度夹到工作区减留白");
        });

        await Run("尺寸不会小于窗口最小尺寸", () =>
        {
            Eq(WindowLayout.MinWidth, WindowLayout.FitWidth(200, 4000), "宽度下限");
            Eq(WindowLayout.MinHeight, WindowLayout.FitHeight(100, 4000), "高度下限");
        });

        await Run("没记住位置（-1）时居中", () =>
        {
            var rect = WindowLayout.Restore(1120, 760, -1, -1, Screen1080P100, Screen1080P100);
            Eq(400d, rect.Left, "水平居中");
            Eq(140d, rect.Top, "垂直居中");
        });

        await Run("位置跑到屏幕外时拉回中间", () =>
        {
            var rect = WindowLayout.Restore(1120, 760, 5000, 5000, Screen1080P100, Screen1080P100);
            Eq(400d, rect.Left, "离屏后居中");
            Eq(140d, rect.Top, "离屏后居中");
        });

        await Run("副屏的负坐标不会被当成离屏", () =>
        {
            var work = new WindowRect(0, 0, 1920, 1040);
            var virtualScreen = new WindowRect(-1920, 0, 3840, 1080);
            var rect = WindowLayout.Restore(1120, 760, -1000, 100, work, virtualScreen);
            Eq(-1000d, rect.Left, "原位置保留");
            Eq(100d, rect.Top, "原位置保留");
        });

        await Run("日志增量只追加新行", () =>
        {
            var a = Line(0, "第一条");
            var b = Line(1, "第二条");
            var c = Line(2, "第三条");
            var merged = LogDiff.Merge(new[] { a, b }, new[] { b, c });
            Eq(3, merged.Count, "合并后三行");
            Eq("第三条", merged[2].Message, "新行接在末尾");
            Eq("第一条", merged[0].Message, "旧行留在前面");
        });

        await Run("没有新增行时原样返回", () =>
        {
            var a = Line(0, "第一条");
            var b = Line(1, "第二条");
            var merged = LogDiff.Merge(new[] { a, b }, new[] { a, b });
            Eq(2, merged.Count, "长度不变");
            Eq(a.Time, merged[0].Time, "内容不变");
        });

        await Run("完全不相交（进程重启）时整体替换", () =>
        {
            var oldLine = Line(0, "旧进程");
            var newLine = Line(9, "新进程");
            var merged = LogDiff.Merge(new[] { oldLine }, new[] { newLine });
            Eq(1, merged.Count, "只剩新进程的行");
            Eq("新进程", merged[0].Message, "内容来自新列表");
        });

        await Run("日志超过上限时保留最近的行", () =>
        {
            var lines = Enumerable.Range(0, 10).Select(i => Line(i, "第 " + i + " 行")).ToArray();
            var trimmed = LogDiff.Trim(lines, 3);
            Eq(3, trimmed.Count, "只留三行");
            Eq("第 7 行", trimmed[0].Message, "从尾部截取");
            Eq("第 9 行", trimmed[2].Message, "最后一行最新");
        });

        await Run("下拉选项直接显示中文", () =>
        {
            Eq(4, UiOptions.TimeoutActions.Count, "超时动作四项");
            Eq("深色", UiOptions.ThemeLabel("dark"), "主题中文标签");
            var option = UiOptions.TimeoutActions[0];
            Eq("自动保存板书（不关闭 ClassInX）", option.ToString(), "ToString 给中文");
            Eq("save", option.Value, "Value 才是写进配置的原值");
        });

        await Run("未知的主题名回退到跟随系统", () =>
        {
            Eq("system", UiOptions.Normalize("neon", UiOptions.Themes, "system"), "未知值回退");
            Eq("light", UiOptions.Normalize(" LIGHT ", UiOptions.Themes, "system"), "大小写与空格归一");
        });

        await Run("下拉框用模板渲染中文，不再依赖 DisplayMemberPath", () =>
        {
            var root = RepoRoot();
            var styles = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Themes/Styles.xaml"));
            Contains(styles, "SelectionBoxItemTemplate", "选中区必须绑定 SelectionBoxItemTemplate");
            Contains(styles, "ContentStringFormat=\"{TemplateBinding SelectionBoxItemStringFormat}\"", "格式化参数也要传下去");

            foreach (var page in new[] { "SettingsPage.xaml", "LessonPage.xaml", "DiagnosePage.xaml" })
            {
                var xaml = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages", page));
                False(xaml.Contains("DisplayMemberPath", StringComparison.Ordinal), page + " 不应再用 DisplayMemberPath");
                Contains(xaml, "<ComboBox.ItemTemplate>", page + " 要有显式 ItemTemplate");
            }
        });

        await Run("课表页的科目勾选控件存在", () =>
        {
            var root = RepoRoot();
            var styles = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Themes/Styles.xaml"));
            Contains(styles, "x:Key=\"SubjectChip\"", "胶囊勾选样式");
            var lesson = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/LessonPage.xaml"));
            Contains(lesson, "SubjectOptions", "科目候选来自视图模型");
            Contains(lesson, "SubjectChip", "勾选项使用胶囊样式");
        });

        await Run("高级页存在且带「熟知老师习惯再开启」，升级进度条按百分比显示", () =>
        {
            var root = RepoRoot();
            var advanced = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/AdvancedPage.xaml"));
            Contains(advanced, "熟知老师习惯", "开启前要先提示");
            Contains(advanced, "InfoBar", "提示用 0.8.1 的 InfoBar 样式");
            Contains(advanced, "HabitRows", "按科目列表来自视图模型");
            Contains(advanced, "HabitAppOptions", "习惯软件下拉");
            Contains(advanced, "HabitUnderstandOptions", "理解程度下拉");
            False(advanced.Contains("DisplayMemberPath", StringComparison.Ordinal), "高级页也不许用 DisplayMemberPath");

            var window = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainWindow.xaml"));
            Contains(window, "pages:AdvancedPage", "主界面要挂上高级页");

            var vm = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainViewModel.cs"));
            Contains(vm, "PageAdvanced", "导航项");
            Contains(vm, "SaveHabitsCommand", "保存习惯设置");

            var update = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/UpdatePage.xaml"));
            Contains(update, "Maximum=\"100\"", "进度条按 0–100 画，别再写 Maximum=1");
            Contains(update, "UpdateProgressText", "要有百分比文字");
            Contains(update, "UpdateProgressVisible", "空闲时藏起进度条");
        });

        await Run("教程引导只盖内容区，点空白/Esc 能收起，关于页能重开", () =>
        {
            var root = RepoRoot();
            var window = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainWindow.xaml"));
            Contains(window, "ShowOnboarding", "引导遮罩绑定视图模型");
            Contains(window, "OnboardingNextText", "下一步/完成按钮");
            Contains(window, "OnboardingAction", "「去看看」按钮");
            Contains(window, "PauseOnboardingCommand", "能先收起教程");
            Contains(window, "OnOnboardingBackdrop", "点空白处收起");
            Contains(window, "HabitConfirmOverlay", "习惯模式确认层要独立存在");
            var habitOverlay = window.IndexOf("HabitConfirmOverlay", StringComparison.Ordinal);
            False(window[..habitOverlay].Contains("Grid.RowSpan=\"2\"", StringComparison.Ordinal), "教程遮罩不许盖住导航与标题栏");
            Contains(window, "ContinueOnboardingCommand", "左下角能接着看");

            var settings = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/SettingsPage.xaml"));
            False(settings.Contains("重新显示教程引导", StringComparison.Ordinal), "设置页不再重复关于页的内容");
            False(settings.Contains("RestartOnboardingCommand", StringComparison.Ordinal), "设置页不再放教程入口");

            var about = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/AboutPage.xaml"));
            Contains(about, "重新显示教程引导", "关于页能重开");
            Contains(about, "RestartOnboardingCommand", "重开命令");

            var vm = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainViewModel.cs"));
            Contains(vm, "OnboardingGuide.ShouldShow", "没看过教程才弹");
            Contains(vm, "PauseOnboarding", "切页时自动收起");
            Contains(vm, "FinishOnboarding", "完成/跳过要落盘");
        });

        await Run("关于页挂在主界面，欢迎遮罩只给 ClassIsland 与下载入口", () =>
        {
            var root = RepoRoot();
            var window = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainWindow.xaml"));
            Contains(window, "pages:AboutPage", "主界面要挂上关于页");
            Contains(window, "OpenClassIslandSiteCommand", "欢迎页给 ClassIsland 官网入口");
            Contains(window, "OpenReleasesCommand", "欢迎页给下载页入口");

            var about = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/AboutPage.xaml"));
            Contains(about, "AboutStarsText", "显示 GitHub Star");
            Contains(about, "OpenRepoCommand", "能打开仓库");
            Contains(about, "RefreshStarsCommand", "能刷新 Star");

            var vm = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainViewModel.cs"));
            Contains(vm, "PageAbout", "导航项");
            Contains(vm, "HabitConfirmVisible", "开启习惯模式前要确认");
            Contains(vm, "ConfirmHabitModeCommand", "确认后真正打开开关");
        });

        await Run("0.8.2：最大化图标、习惯确认层、默认管理员与去网页面板", () =>
        {
            var root = RepoRoot();
            var window = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainWindow.xaml"));
            Contains(window, "x:Name=\"MaxGlyph\"", "最大化图标要有具名 TextBlock");
            Contains(window, "HabitConfirmOverlay", "习惯确认层在主窗口内");
            Contains(window, "HabitConfirmVisible", "确认层按视图模型显示");
            Contains(window, "ConfirmHabitModeCommand", "确认按钮");
            Contains(window, "CancelHabitModeCommand", "取消按钮");

            var windowCode = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainWindow.xaml.cs"));
            Contains(windowCode, "MaxGlyph.Text", "只改字形文字，不要替换 Content");
            False(windowCode.Contains("MaxButton.Content", StringComparison.Ordinal), "不能再把 MaxButton.Content 换成裸字符串");

            var vm = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainViewModel.cs"));
            Contains(vm, "HabitConfirmVisible", "确认层状态");
            Contains(vm, "ConfirmHabitModeCommand", "确认命令");
            Contains(vm, "CancelHabitModeCommand", "取消命令");
            False(vm.Contains("MessageBox.Show", StringComparison.Ordinal), "确认框不再走系统 MessageBox");

            var manifest = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/app.manifest"));
            Contains(manifest, "level=\"highestAvailable\"", "默认按最高可用权限运行");

            var task = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Tasks/ScheduledTaskInstaller.cs"));
            Contains(task, "<RunLevel>HighestAvailable</RunLevel>", "登录自启任务也要最高可用权限");

            var plugin = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.ClassIsland/Client/AgentAccess.cs"));
            Contains(plugin, "schtasks.exe", "插件先用计划任务拉起守护");
            Contains(plugin, "ClassroomKit-Agent", "计划任务名");
            Contains(plugin, "UseShellExecute = true", "任务不可用时才走 ShellExecute");

            var csproj = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/ClassroomKit.Agent.csproj"));
            False(csproj.Contains("web", StringComparison.OrdinalIgnoreCase), "工程里不应再打包 web 目录");
            False(Directory.Exists(Path.Combine(root, "web")), "仓库里不应再有 web 目录");

            var api = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Core/Api/ApiServer.cs"));
            False(api.Contains("ServeStaticAsync", StringComparison.Ordinal), "不再提供静态文件服务");
            Contains(api, "HttpResponse.Text(\"not found\", 404)", "非 /api 一律 404");

            var about = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/AboutPage.xaml"));
            False(about.Contains("网页面板", StringComparison.Ordinal), "关于页不再提网页面板");
        });

        await Run("0.8.3：自检页能检查并修复网络，自动修复可在界面开关", () =>
        {
            var root = RepoRoot();
            var diag = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/Pages/DiagnosePage.xaml"));
            Contains(diag, "SelfHealText", "显示自动修复状态");
            Contains(diag, "SelfHealDetail", "显示最近一次修复结果");
            Contains(diag, "EditSelfHealEnabled", "自动修复开关");
            Contains(diag, "EditRestartAdapterOnIdleNoRx", "零客户端激进开关");
            Contains(diag, "RepairNetworkCommand", "手动检查并修复");

            var vm = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Desktop/MainViewModel.cs"));
            Contains(vm, "RepairNetworkCommand", "视图模型命令");
            Contains(vm, "RepairNowAsync", "调用守护的手动修复");
            Contains(vm, "SelfHealText", "状态文案");

            var guard = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Core/Hotspot/HotspotGuard.cs"));
            Contains(guard, "SelfHealAsync", "统一修复动作");
            Contains(guard, "RecentClientWindow", "只在最近有设备时自动复位");
            Contains(guard, "SelfHealMaxPerDay", "每天修复上限");
            Contains(guard, "RepairNowAsync", "手动修复入口");

            var diagSource = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Agent/Diagnose/Diagnostics.cs"));
            Contains(diagSource, "selfheal", "自检报告包含自我修复项");

            var api = File.ReadAllText(Path.Combine(root, "src/ClassroomKit.Core/Api/ApiServer.cs"));
            Contains(api, "repair-network", "HTTP 动作");
            Contains(api, "selfHeal", "状态接口暴露修复信息");
        });

    }

    /// <summary>测试输出目录向上找仓库根（src 与 Directory.Build.props 所在处）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根（src + Directory.Build.props）");
    }

    private static UiLogLine Line(int minute, string message) =>
        new(new DateTimeOffset(2026, 11, 14, 8, minute, 0, TimeSpan.FromHours(8)), "info", message);
}
