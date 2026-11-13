using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassroomKit.ClassIsland.Actions;
using ClassroomKit.ClassIsland.Settings;
using ClassroomKit.ClassIsland.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClassroomKit.ClassIsland;

/// <summary>
/// ClassroomKit 的 ClassIsland 插件：薄壳。
/// 只负责读状态、发动作；热点、网卡、升级这些都在独立的 ClassroomKit.Agent 里，ClassIsland 崩了也不影响上课上网。
/// </summary>
[PluginEntrance]
public class ClassroomKitPlugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton(new PluginSettingsStore(PluginConfigFolder));

        services.AddSettingsPage<HotspotSettingsPage>();
        services.AddComponent<HotspotComponent>();
        services.AddAction<StartHotspotAction>();
        services.AddAction<StopHotspotAction>();
    }
}
