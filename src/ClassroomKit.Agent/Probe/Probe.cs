using System.Text.Json;
using ClassroomKit.Agent.Bcu;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Support;
using Microsoft.Win32;

namespace ClassroomKit.Agent.Probe;

/// <summary>P0 探针：一次性看清这台机器能不能承载 10 台热点。</summary>
public static class Probe
{
    public static string Collect()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var log = new ClassroomKit.Core.Logging.AppLog(Paths.LogDir);
        var probe = new UplinkProbe(log);
        var tethering = new TetheringService(probe, () => store.Current);
        var snapshot = tethering.GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();

        var data = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["version"] = AgentRuntime.DisplayVersion(),
            ["machine"] = SupportBundle.MaskName(Environment.MachineName),
            ["user"] = SupportBundle.MaskName(Environment.UserName, 1),
            ["os"] = Environment.OSVersion.VersionString,
            ["elevated"] = AdminElevation.IsElevated(),
            ["configPath"] = "<数据目录>\\config.json",
            ["registryPeers"] = PeerLimit.Read(),
            ["registryTarget"] = store.Current.Hotspot.RegistryPeers,
            ["maxClientCount"] = snapshot.MaxClientCount,
            ["capability"] = snapshot.Capability.ToString(),
            ["operationalState"] = snapshot.OperationalState.ToString(),
            ["clientCount"] = snapshot.ClientCount,
            ["noConnectionsTimeout"] = snapshot.NoConnectionsTimeoutEnabled,
            ["currentSsid"] = SupportBundle.MaskName(snapshot.CurrentSsid),
            ["uplinkAvailable"] = snapshot.UplinkAvailable,
            ["uplinkIsWifi"] = snapshot.UplinkIsWifi,
            ["uplinkName"] = snapshot.UplinkName,
            ["uplinkId"] = SupportBundle.MaskName(snapshot.UplinkId, 4),
            ["uplinkHasInternet"] = snapshot.UplinkHasInternet,
            ["uplinkReason"] = snapshot.UplinkReason,
            ["uplinkMode"] = store.Current.Uplink.Mode,
            ["uplinks"] = probe.Enumerate().Select(c => new
            {
                Id = SupportBundle.MaskName(c.Id, 4),
                Name = SupportBundle.MaskName(c.Name, 2),
                Description = SupportBundle.MaskName(c.Description, 2),
                Ipv4 = SupportBundle.MaskIp(c.Ipv4),
                c.IsWired,
                c.IsWifi,
                c.HasGateway,
                c.HasInternet,
                c.IsTethering,
                c.Metric,
                c.Usable
            }).ToArray(),
            ["hotspotError"] = snapshot.Error,
            ["taskInstalled"] = ScheduledTaskInstaller.IsInstalled(),
            ["bcuPath"] = string.IsNullOrWhiteSpace(store.Current.Bcu.Path) ? "" : "<路径>",
            ["geekOnDesktop"] = File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "geek.lnk")),
            ["classIsland"] = DetectClassIsland(),
            ["restoreSystem"] = DetectRestoreSystem(),
            ["wirelessAdapters"] = WirelessAdapters()
        };

        return JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static object? DetectClassIsland()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassIsland"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassIsland"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClassIsland")
        };

        var found = candidates.Where(Directory.Exists).ToArray();
        if (found.Length > 0) return new { installed = true, locations = found.Length };

        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var item = key.OpenSubKey(sub);
                    var name = item?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("ClassIsland", StringComparison.OrdinalIgnoreCase)) continue;
                    return new { installed = true, uninstallEntry = name };
                }
            }
            catch
            {
                // 忽略
            }
        }

        return new { installed = false };
    }

    private static object DetectRestoreSystem()
    {
        var services = new[] { "DFServ", "RebootRestoreRx", "ShadowDefender", "HDTool", "FrzState" };
        var hits = new List<string>();
        foreach (var name in services)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
                if (key is not null) hits.Add(name);
            }
            catch
            {
                // 忽略
            }
        }

        return new { detected = hits.Count > 0, services = hits };
    }

    private static string[] WirelessAdapters()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
                .Select(n => $"{n.Name}｜{n.Description}｜{n.OperationalStatus}")
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
