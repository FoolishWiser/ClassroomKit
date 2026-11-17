namespace ClassroomKit.Core.Update;

/// <summary>
/// 升级进度的唯一换算入口：内部统一用 0–100 的百分比，
/// 安装包占 0–90（还要留出插件那段），插件占 90–100。
/// 界面上进度条要写 Maximum=100；以前 WPF 里写成 Maximum=1，第一个回调就显示满格。
/// </summary>
public static class UpdateProgress
{
    public const double Max = 100;
    public const double PluginStart = 90;

    /// <summary>去掉 NaN/Infinity 并夹到 0–100。</summary>
    public static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, Max) : 0;

    /// <summary>安装包下载进度：有插件包时只走到 90，把最后 10 留给插件。</summary>
    public static double ForInstaller(double percent, bool hasPlugin) =>
        Clamp(percent) * (hasPlugin ? 0.9 : 1.0);

    /// <summary>插件包下载进度：接在安装包后面，90–100。</summary>
    public static double ForPlugin(double percent) =>
        PluginStart + Clamp(percent) * 0.1;

    /// <summary>面板上显示的一句话，例如「47%」「已下载」。</summary>
    public static string Describe(string? phase, double value) => phase switch
    {
        "Downloading" => $"{Clamp(value):F0}%",
        "Ready" => "已下载",
        _ => ""
    };
}
