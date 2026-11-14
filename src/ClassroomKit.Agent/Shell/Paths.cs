namespace ClassroomKit.Agent.Shell;

public static class Paths
{
    public static string ExeDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static string ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            return Path.Combine(ExeDir, "ClassroomKit.Agent.exe");
        }
    }

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClassroomKit");

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string LogDir => Path.Combine(DataDir, "logs");
    /// <summary>ClassInX 板书保存的界面坐标配方。</summary>
    public static string RecipePath => Path.Combine(DataDir, "ui-recipe.json");
    /// <summary>ClassIsland 课表快照与「已提醒过哪一节」的落盘状态。</summary>
    public static string LessonStatePath => Path.Combine(DataDir, "lesson-state.json");
    public static string WebRoot => Path.Combine(ExeDir, "web");
}
