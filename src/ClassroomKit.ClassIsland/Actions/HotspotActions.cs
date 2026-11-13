using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassroomKit.ClassIsland.Client;
using ClassroomKit.ClassIsland.Settings;

namespace ClassroomKit.ClassIsland.Actions;

/// <summary>上课时：确保热点开着（守护没跑就先拉起来）。</summary>
[ActionInfo("classroomkit.hotspot.start", "开启教室热点", "\uE701", true, "ClassroomKit")]
public class StartHotspotAction : ActionBase
{
    private readonly PluginSettingsStore _store;

    public StartHotspotAction(PluginSettingsStore store) => _store = store;

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        var (ok, message, _) = await AgentAccess.EnsureReachableAsync(_store);
        if (!ok)
        {
            await CommonTaskDialogs.ShowDialog("ClassroomKit 热点", message, null);
            return;
        }

        try
        {
            await _store.CreateClient().PostActionAsync("ensure");
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("ClassroomKit 热点", "开启热点失败：" + ex.Message, null);
        }
    }
}

/// <summary>放学时：关掉热点。</summary>
[ActionInfo("classroomkit.hotspot.stop", "关闭教室热点", "\uE71A", true, "ClassroomKit")]
public class StopHotspotAction : ActionBase
{
    private readonly PluginSettingsStore _store;

    public StopHotspotAction(PluginSettingsStore store) => _store = store;

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        try
        {
            await _store.CreateClient().PostActionAsync("stop");
        }
        catch (Exception ex)
        {
            await CommonTaskDialogs.ShowDialog("ClassroomKit 热点", "关闭热点失败：" + ex.Message, null);
        }
    }
}
