using System.Net.NetworkInformation;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace ClassroomKit.Agent.Tethering;

/// <summary>WinRT NetworkOperatorTetheringManager 的真实实现。上行网卡由 <see cref="UplinkProbe"/> 挑选。</summary>
public sealed class TetheringService : ITethering
{
    private readonly UplinkProbe _probe;
    private readonly Func<AppConfig> _config;

    public TetheringService(UplinkProbe probe, Func<AppConfig> config)
    {
        _probe = probe;
        _config = config;
    }

    /// <summary>最近一次选择结果（面板与日志用）。</summary>
    public UplinkChoice LastChoice { get; private set; } = new();

    public Task<TetheringSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.Run(Snapshot, ct);

    public Task<bool> StartAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var manager = TryCreateManager(out var error);
        if (manager is null)
        {
            LastError = error;
            return false;
        }
        try
        {
            var result = await manager.StartTetheringAsync();
            if (result.Status == TetheringOperationStatus.Success) return true;
            LastError = $"启动失败：{result.Status} {result.AdditionalErrorMessage}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }, ct);

    public Task<bool> StopAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var manager = TryCreateManager(out var error);
        if (manager is null)
        {
            LastError = error;
            return false;
        }
        try
        {
            var result = await manager.StopTetheringAsync();
            if (result.Status == TetheringOperationStatus.Success) return true;
            LastError = $"关闭失败：{result.Status} {result.AdditionalErrorMessage}";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }, ct);

    public Task<bool> ConfigureAccessPointAsync(string ssid, string passphrase, CancellationToken ct) => Task.Run(async () =>
    {
        var manager = TryCreateManager(out var error);
        if (manager is null)
        {
            LastError = error;
            return false;
        }
        try
        {
            var configuration = manager.GetCurrentAccessPointConfiguration();
            configuration.Ssid = ssid;
            configuration.Passphrase = passphrase;
            await manager.ConfigureAccessPointAsync(configuration);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }, ct);

    public Task<bool> EnsureNoConnectionsTimeoutDisabledAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            // 语义：IsNoConnectionsTimeoutEnabled() == true 表示“无设备 5 分钟自动关热点”这个超时是开着的，
            // 必须调 DisableNoConnectionsTimeout() 才能关掉它（原名极易反向理解）。
            if (!NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled()) return true;
            NetworkOperatorTetheringManager.DisableNoConnectionsTimeout();
            return !NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }, ct);

    public Task<IReadOnlyList<TetherClient>> GetClientsAsync(CancellationToken ct) => Task.Run<IReadOnlyList<TetherClient>>(() =>
    {
        var manager = TryCreateManager(out var error);
        // 不能返回空表：调用方会把「读不到」当成「教室里一台设备都没有」。
        if (manager is null) throw new InvalidOperationException(error ?? "无法访问移动热点");
        try
        {
            var list = new List<TetherClient>();
            var arp = ArpTable.Map();   // 每轮只跑一次 arp.exe，客户端多的时候不再是一台一个进程
            foreach (var client in manager.GetTetheringClients())
            {
                var mac = client.MacAddress?.ToLowerInvariant() ?? "";
                var host = client.HostNames is { Count: > 0 } names ? names[0].DisplayName : "";
                arp.TryGetValue(ArpTable.Normalize(mac), out var ip);
                list.Add(new TetherClient(mac, host, ip, DateTimeOffset.Now));
            }
            return list;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return Array.Empty<TetherClient>();
        }
    }, ct);

    public string? LastError { get; private set; }

    private TetheringSnapshot Snapshot()
    {
        var cfg = _config();
        UplinkChoice choice;
        try
        {
            choice = _probe.Resolve(cfg.Uplink, cfg.Hotspot.AllowWifiUplink);
        }
        catch (Exception ex)
        {
            return TetheringSnapshot.Empty with { Error = ex.Message };
        }

        LastChoice = choice;

        if (choice.Chosen is null)
        {
            // 只有 Wi-Fi 能连通时，让守护状态机报“上行是 Wi-Fi（拒绝）”而不是笼统的“等待上行”
            var blockedWifi = choice.Source == UplinkSource.WifiBlocked;
            return TetheringSnapshot.Empty with
            {
                UplinkAvailable = blockedWifi,
                UplinkIsWifi = blockedWifi,
                UplinkName = null,
                UplinkReason = choice.Reason,
                Error = choice.Reason
            };
        }

        var uplink = choice.Chosen;
        var profile = UplinkProbe.ProfileFor(uplink);
        if (profile is null)
        {
            return TetheringSnapshot.Empty with
            {
                UplinkAvailable = true,
                UplinkIsWifi = uplink.IsWifi,
                UplinkName = uplink.Label,
                UplinkId = uplink.Id,
                UplinkHasInternet = uplink.HasInternet,
                UplinkReason = choice.Reason,
                Error = $"网卡 {uplink.Label} 没有可用的连接配置"
            };
        }

        try
        {
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);

            string? ssid = null;
            string? passphrase = null;
            try
            {
                var ap = manager.GetCurrentAccessPointConfiguration();
                ssid = ap.Ssid;
                passphrase = ap.Passphrase;
            }
            catch
            {
                // 热点未配置时可能抛
            }

            bool? timeout = null;
            try { timeout = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled(); }
            catch { }

            return new TetheringSnapshot
            {
                OperationalState = manager.TetheringOperationalState switch
                {
                    TetheringOperationalState.On => OperationalState.On,
                    TetheringOperationalState.Off => OperationalState.Off,
                    TetheringOperationalState.InTransition => OperationalState.Starting,
                    _ => OperationalState.Unknown
                },
                ClientCount = (int)manager.ClientCount,
                MaxClientCount = (int)manager.MaxClientCount,
                NoConnectionsTimeoutEnabled = timeout,
                UplinkAvailable = true,
                UplinkIsWifi = uplink.IsWifi,
                UplinkName = uplink.Label,
                UplinkId = uplink.Id,
                UplinkHasInternet = uplink.HasInternet,
                UplinkReason = choice.Reason,
                Capability = capability == TetheringCapability.Enabled ? Capability.Enabled : Capability.Disabled,
                Error = capability == TetheringCapability.Enabled ? null : $"热点能力未启用：{capability}",
                CurrentSsid = ssid,
                CurrentPassphrase = passphrase
            };
        }
        catch (Exception ex)
        {
            return TetheringSnapshot.Empty with
            {
                UplinkAvailable = true,
                UplinkIsWifi = uplink.IsWifi,
                UplinkName = uplink.Label,
                UplinkId = uplink.Id,
                UplinkHasInternet = uplink.HasInternet,
                UplinkReason = choice.Reason,
                Error = ex.Message
            };
        }
    }

    public Task<long?> GetAccessPointReceivedBytesAsync(CancellationToken ct) => WlanAdapter.ReadApReceivedBytesAsync(ct);

    public Task<bool> RestartWirelessAdapterAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var ok = await WlanAdapter.RestartAsync(ct).ConfigureAwait(false);
        LastError = ok ? null : "复位无线网卡失败：需要管理员权限（或网卡驱动不支持 Restart-NetAdapter）";
        return ok;
    }, ct);

    private NetworkOperatorTetheringManager? TryCreateManager(out string? error)
    {
        error = null;
        var cfg = _config();
        var choice = _probe.Resolve(cfg.Uplink, cfg.Hotspot.AllowWifiUplink);
        LastChoice = choice;

        if (choice.Chosen is null)
        {
            error = choice.Reason;
            return null;
        }

        var profile = UplinkProbe.ProfileFor(choice.Chosen);
        if (profile is null)
        {
            error = $"网卡 {choice.Chosen.Label} 没有可用的连接配置";
            return null;
        }

        try
        {
            return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>从 ARP 表里找客户端 IP（WinRT 的 Client 只给 MAC）。</summary>
internal static class ArpTable
{
    /// <summary>一次把整张 ARP 表解析成 MAC→IP 映射；调用方每轮调一次，客户端多时不再一台一个进程。</summary>
    public static Dictionary<string, string> Map()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in RunArp())
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!System.Net.IPAddress.TryParse(parts[0], out _)) continue;

                var mac = Normalize(parts[1]);
                if (mac.Length == 0) continue;
                // 同一个 MAC 可能有多条记录（多接口/残留项）：保留先出现的那条
                if (!map.ContainsKey(mac)) map[mac] = parts[0];
            }
        }
        catch
        {
            // ARP 表读不到就不显示 IP
        }
        return map;
    }

    /// <summary>MAC 统一成小写冒号分隔，便于和 WinRT 给的格式比对。</summary>
    public static string Normalize(string mac) => mac.Replace('-', ':').Trim().ToLowerInvariant();

    private static string[] RunArp()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("arp.exe", "-a")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return Array.Empty<string>();

        // 异步读 + 限时等退出：以前同步 ReadToEnd 没有超时，arp.exe 卡住会拖住整轮巡检。
        var textTask = proc.StandardOutput.ReadToEndAsync();
        _ = proc.StandardError.ReadToEndAsync();   // 顺手排空，免得管道写满卡住子进程
        string text;
        if (proc.WaitForExit(3000))
        {
            text = textTask.GetAwaiter().GetResult();
        }
        else
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 可能已经自己退了 */ }
            proc.WaitForExit(1000);
            text = textTask.Wait(1000) && textTask.IsCompletedSuccessfully ? textTask.Result : "";
        }
        return text.Split('\n');
    }
}
