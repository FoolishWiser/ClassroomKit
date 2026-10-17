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
    public static string WebRoot => Path.Combine(ExeDir, "web");
}
