using ClassroomKit.Agent.Tray;
using Forms = System.Windows.Forms;

namespace ClassroomKit.Agent;

/// <summary>托盘常驻的应用上下文。</summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly AgentRuntime _runtime;
    private readonly TrayController _tray;

    public AgentContext()
    {
        _runtime = new AgentRuntime();
        _runtime.Start();
        _tray = new TrayController(_runtime, ExitThread);
        _runtime.ExitRequested = _tray.RequestExit;
    }

    protected override void ExitThreadCore()
    {
        try { _tray.Dispose(); } catch { }
        try { _runtime.Dispose(); } catch { }
        base.ExitThreadCore();
    }
}
