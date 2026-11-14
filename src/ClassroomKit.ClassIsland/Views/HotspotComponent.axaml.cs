using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassroomKit.ClassIsland.Settings;

namespace ClassroomKit.ClassIsland.Views;

[ComponentInfo("0f5f4c8e-1b0e-4a5a-9d3c-6c1f6a2b7d21", "ClassroomKit 助手", "\uE701",
    "显示教室热点是否开启、连了几台设备。")]
public partial class HotspotComponent : ComponentBase
{
    private readonly PluginSettingsStore _store;
    private readonly DispatcherTimer _timer;

    public HotspotComponent(PluginSettingsStore store)
    {
        _store = store;
        InitializeComponent();

        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, async (_, _) => await RefreshAsync())
        {
            IsEnabled = false,
        };
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.IsEnabled = true;
        _ = RefreshAsync();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.IsEnabled = false;
    }

    private async Task RefreshAsync()
    {
        // 课表那一行单独跑，不拖慢热点状态的刷新
        _ = UpdateLessonLineAsync();

        var client = _store.CreateClient();
        var (state, _) = await client.TryGetStateAsync();
        if (state is null)
        {
            Headline.Text = "热点";
            Detail.Text = "守护未运行";
            return;
        }

        var hotspot = state.Hotspot;
        var on = hotspot?.State is "on" or "running" or "starting" or "restarting";
        Headline.Text = on ? "热点 ON" : "热点 OFF";
        Detail.Text = hotspot is null
            ? "—"
            : (hotspot.Ssid ?? "—") + " · " + hotspot.Clients + "/" + hotspot.TargetClients;
    }

    /// <summary>课表联动的一行小字：下一节什么课、还有几分钟。没数据就整行藏起来，不占地方。</summary>
    private async Task UpdateLessonLineAsync()
    {
        try
        {
            var lesson = await _store.CreateClient().GetLessonAsync();
            if (!lesson.Available || string.IsNullOrWhiteSpace(lesson.NextSubject))
            {
                LessonLine.IsVisible = false;
                return;
            }

            var when = lesson.SecondsToNext <= 0
                ? "待定"
                : lesson.SecondsToNext < 60
                    ? "不到 1 分钟"
                    : ((lesson.SecondsToNext + 59) / 60) + " 分钟后";
            LessonLine.Text = "下一节 " + lesson.NextSubject + " · " + when;
            LessonLine.IsVisible = true;
        }
        catch
        {
            // 守护没运行或版本没有 /api/lesson：不显示这一行
            LessonLine.IsVisible = false;
        }
    }
}
