namespace ClassroomKit.Core.Startup;

/// <summary>启动参数判定（纯函数，方便单测）。</summary>
public static class StartupArgs
{
    public const string TrayOnly = "--tray";
    public const string Autostart = "--autostart";

    /// <summary>
    /// 开机自启带 --tray：固定只到托盘，不弹主界面。
    /// 手动双击（无参数）仍然按设置里的「启动时只驻留托盘」执行。
    /// </summary>
    public static bool IsTrayOnly(IReadOnlyList<string>? args)
    {
        if (args is null) return false;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var value = arg.Trim();
            if (value.Equals(TrayOnly, StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Equals(Autostart, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
