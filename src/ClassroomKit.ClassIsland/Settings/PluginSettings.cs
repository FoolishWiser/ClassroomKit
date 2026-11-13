namespace ClassroomKit.ClassIsland.Settings;

/// <summary>插件自己的设置：怎么连上本机的 ClassroomKit 守护进程。</summary>
public sealed class PluginSettings
{
    /// <summary>守护进程的地址；默认端口与守护一致。</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:36037";

    /// <summary>访问令牌；留空时自动读取 %ProgramData%\ClassroomKit\config.json 里的令牌。</summary>
    public string Token { get; set; } = "";

    public PluginSettings Clone() => new() { BaseUrl = BaseUrl, Token = Token };
}
