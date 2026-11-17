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
            Contains(advanced, "熟知老师习惯再开启", "开启前要先提示");
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
