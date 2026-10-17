using System.Net.NetworkInformation;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Model;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace ClassroomKit.Agent.Tethering;

/// <summary>WinRT NetworkOperatorTetheringManager 的真实实现。</summary>
public sealed class TetheringService : ITethering
{
    private const uint InterfaceWifi = 71;

    public Task<TetheringSnapshot> GetSnapshotAsync(CancellationToken ct) =>
        Task.Run(Snapshot, ct);

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
        var manager = TryCreateManager(out _);
        if (manager is null) return Array.Empty<TetherClient>();
        try
        {
            var list = new List<TetherClient>();
            foreach (var client in manager.GetTetheringClients())
            {
                var mac = client.MacAddress?.ToLowerInvariant() ?? "";
                var host = client.HostNames is { Count: > 0 } names ? names[0].DisplayName : "";
                list.Add(new TetherClient(mac, host, ArpTable.IpFor(mac), DateTimeOffset.Now));
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

    private static TetheringSnapshot Snapshot()
    {
        ConnectionProfile? profile;
        var isWifi = false;
        var uplinkName = "";
        var uplinkUp = false;

        try
        {
            profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null)
            {
                var adapter = profile.NetworkAdapter;
                isWifi = adapter is not null && adapter.IanaInterfaceType == InterfaceWifi;
                uplinkName = profile.ProfileName ?? "";
                uplinkUp = profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
            }
        }
        catch (Exception ex)
        {
            return TetheringSnapshot.Empty with { Error = ex.Message };
        }

        if (profile is null || !uplinkUp)
        {
            return TetheringSnapshot.Empty with
            {
                UplinkAvailable = false,
                UplinkIsWifi = isWifi,
                UplinkName = uplinkName
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
                UplinkIsWifi = isWifi,
                UplinkName = uplinkName,
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
                UplinkIsWifi = isWifi,
                UplinkName = uplinkName,
                Error = ex.Message
            };
        }
    }

    private static NetworkOperatorTetheringManager? TryCreateManager(out string? error)
    {
        error = null;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                error = "没有可用的上行连接（网线未插？）";
                return null;
            }
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
    public static string? IpFor(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        try
        {
            var normalized = mac.Replace('-', ':').ToLowerInvariant();
            foreach (var line in RunArp())
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!System.Net.IPAddress.TryParse(parts[0], out _)) continue;
                var candidate = parts[1].Replace('-', ':').ToLowerInvariant();
                if (candidate == normalized) return parts[0];
            }
        }
        catch
        {
            // ARP 表读不到就不显示 IP
        }
        return null;
    }

    private static string[] RunArp()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("arp.exe", "-a")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) return Array.Empty<string>();
        var text = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(3000);
        return text.Split('\n');
    }
}
