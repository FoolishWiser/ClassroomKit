using ClassroomKit.Agent.Bcu;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Core.Api;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Http;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;

namespace ClassroomKit.Agent;

/// <summary>把配置、日志、WinRT 后端、守护状态机、HTTP、托盘串起来。</summary>
public sealed class AgentRuntime : IDisposable
{
    private readonly MiniHttpServer _http;
    private readonly CancellationTokenSource _cts = new();

    private DateTimeOffset? _pauseUntil;
    private bool _enabledBeforePause;

    public AgentRuntime()
    {
        Config = new ConfigStore(Paths.ConfigPath);
        Log = new AppLog(Paths.LogDir);
        Tethering = new TetheringService();
        Guard = new HotspotGuard(Tethering, () => Config.Current, Log);

        var version = typeof(AgentRuntime).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Api = new ApiServer(Config, Log, Paths.WebRoot, version);
        _http = new MiniHttpServer(Config.Current.Port, Api.HandleAsync, Log);

        Api.AttachGuard(Guard);
        Api.ApplyPolicy = SetPolicy;
        Api.ApplySettings = ApplySettings;
        Api.FixPeersAsync = (ct) => Task.FromResult(Commands.FixPeersCore(Log));
        Api.LaunchBcuAsync = (ct) => Task.FromResult(BcuLauncher.Launch(Config.Current.Bcu));
        Api.RequestExit = () => ExitRequested?.Invoke();
    }

    public ConfigStore Config { get; }
    public AppLog Log { get; }
    public TetheringService Tethering { get; }
    public HotspotGuard Guard { get; }
    public ApiServer Api { get; }

    /// <summary>由托盘层注入：退出整个进程。</summary>
    public Action? ExitRequested { get; set; }

    public string BaseUrl => $"http://127.0.0.1:{Config.Current.Port}/";
    public string PanelUrl => $"{BaseUrl}?t={Config.Current.Token}";

    public bool IsPaused => _pauseUntil is not null && DateTimeOffset.Now < _pauseUntil;

    public TimeSpan? PauseRemaining => _pauseUntil is null ? null : _pauseUntil - DateTimeOffset.Now;

    public void Start()
    {
        Log.Info($"ClassroomKit Agent 启动 v{typeof(AgentRuntime).Assembly.GetName().Version?.ToString(3)}");
        Log.Info($"配置：{Paths.ConfigPath}｜面板：{BaseUrl}｜上行策略：{(Config.Current.Hotspot.AllowWifiUplink ? "允许 Wi-Fi" : "仅有线")}");

        try
        {
            _http.Start();
            Log.Info($"Web 面板已监听 127.0.0.1:{Config.Current.Port}");
        }
        catch (Exception ex)
        {
            Log.Error($"Web 面板启动失败（端口 {Config.Current.Port}）：{ex.Message}");
        }

        _ = Task.Run(GuardLoopAsync);

        if (Config.Current.Hotspot.AutoStartOnLaunch && !Config.Current.Hotspot.Enabled)
        {
            SetPolicy(true);
        }
    }

    private readonly SemaphoreSlim _wake = new(0, 1);

    private async Task GuardLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!IsPaused)
                {
                    await Guard.TickOnceAsync(_cts.Token);
                }
                else if (_pauseUntil is not null)
                {
                    Log.Debug($"守护暂停中，剩余 {(_pauseUntil.Value - DateTimeOffset.Now).TotalMinutes:F0} 分钟");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"巡检异常：{ex.Message}");
            }

            try
            {
                // 有唤醒信号（用户在面板点了开/关）就立刻再跑一次，否则按巡检间隔等
                var seconds = Math.Clamp(Config.Current.Hotspot.GuardIntervalSec, 3, 300);
                await _wake.WaitAsync(TimeSpan.FromSeconds(seconds), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }

    public void SetPolicy(bool enabled)
    {
        Guard.SetEnabled(enabled);
        Config.Update(c =>
        {
            c.Hotspot.Enabled = enabled;
            return true;
        });
        if (enabled) ClearPause();
        Wake();
    }

    public void PauseFor(TimeSpan duration)
    {
        _enabledBeforePause = Config.Current.Hotspot.Enabled;
        _pauseUntil = DateTimeOffset.Now + duration;
        Guard.SetEnabled(false);
        Config.Update(c =>
        {
            c.Hotspot.Enabled = false;
            return true;
        });
        Log.Warn($"守护暂停 {duration.TotalMinutes:F0} 分钟（期间不会自动开热点）");
    }

    public void ClearPause()
    {
        if (_pauseUntil is null) return;
        _pauseUntil = null;
        if (_enabledBeforePause)
        {
            Guard.SetEnabled(true);
            Config.Update(c =>
            {
                c.Hotspot.Enabled = true;
                return true;
            });
        }
        Log.Info("守护已恢复");
    }

    private void ApplySettings(ApiServer.UserSettings settings)
    {
        Config.Update(c =>
        {
            c.Hotspot.Ssid = settings.Ssid;
            c.Hotspot.Passphrase = settings.Passphrase;
            c.Hotspot.TargetClients = settings.TargetClients;
            c.Hotspot.AllowWifiUplink = settings.AllowWifiUplink;
            return true;
        });
        Log.Info($"设置已更新：SSID={settings.Ssid}｜目标 {settings.TargetClients} 台");
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await Tethering.ConfigureAccessPointAsync(settings.Ssid, settings.Passphrase, _cts.Token);
                Log.Info(ok ? "热点名称/密码已应用" : "热点名称/密码应用失败，下次巡检会重试");
            }
            catch (Exception ex)
            {
                Log.Warn($"应用热点配置失败：{ex.Message}");
            }
        });
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _http.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }
}
