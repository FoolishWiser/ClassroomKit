using ClassroomKit.Agent.Tray;

namespace ClassroomKit.Agent.Desktop;

/// <summary>进程级宿主：守护运行时 + 托盘；WPF Application 只负责消息循环。</summary>
internal sealed class AgentHost : IDisposable
{
    private readonly TrayController _tray;

    public AgentHost()
    {
        Runtime = new AgentRuntime();
        Runtime.Start();
        _tray = new TrayController(Runtime, UiHost.Shutdown);
        Runtime.ExitRequested = _tray.RequestExit;
        Runtime.ConflictWatcher.PromptRequested = _tray.ShowConflictDialog;
        Runtime.PreviewConflictRequested = _tray.ShowConflictPreview;
    }

    public AgentRuntime Runtime { get; }

    public void Dispose()
    {
        try { _tray.Dispose(); } catch { }
        try { Runtime.Dispose(); } catch { }
    }
}
