using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Controls;
using ClassroomKit.ClassIsland.Client;
using ClassroomKit.ClassIsland.Settings;

namespace ClassroomKit.ClassIsland.Actions;

/// <summary>保存 ClassInX 板书：按上一节课改名，具体怎么存交给守护进程。</summary>
[ActionInfo("classroomkit.board.save", "保存板书", "\uE78C", true, "ClassroomKit")]
public class SaveBoardAction : ActionBase
{
    private readonly PluginSettingsStore _store;

    public SaveBoardAction(PluginSettingsStore store) => _store = store;

    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        try
        {
            var settings = _store.Current;
            var reply = await _store.CreateClient().PostActionAsync("board-save", new Dictionary<string, string?>
            {
                ["nameWithLesson"] = "1",
                ["closeAfterSave"] = settings.BoardCloseAfterSave ? "1" : "0",
            });
            if (!reply.Ok)
            {
                await CommonTaskDialogs.ShowDialog("ClassroomKit 板书", reply.Message ?? "保存板书失败", null);
            }
        }
        catch (Exception ex)
        {
            // 守护可能没启动：先按需拉起来再说，拉不起来就把原因说清楚，别静默失败
            var (ok, message, _) = await AgentAccess.EnsureReachableAsync(_store, attempts: 3);
            await CommonTaskDialogs.ShowDialog("ClassroomKit 板书", ok ? "保存板书失败：" + ex.Message : message, null);
        }
    }
}
