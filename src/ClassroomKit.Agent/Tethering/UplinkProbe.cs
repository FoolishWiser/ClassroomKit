using System.Net;
using System.Net.NetworkInformation;
using ClassroomKit.Core.Hotspot;
using ClassroomKit.Core.Logging;
using ClassroomKit.Core.Model;
using Windows.Networking.Connectivity;

namespace ClassroomKit.Agent.Tethering;

/// <summary>
/// 枚举本机所有候选上行网卡，并按“哪块真有外网”挑一块来开热点。
/// 要点：
/// 1) 学校一体机常有 2–3 块有线网卡（校内网 / 监控 / 预留），只有一块真能上外网；
/// 2) 有时候整个学校都没外网，这时仍要用局域网网卡开热点，让学生能连上访问本机资源；
/// 3) 自己开出来的热点网卡（192.168.137.x）必须排除，否则会自己共享自己。
/// </summary>
public sealed class UplinkProbe
{
    private readonly AppLog _log;

    public UplinkProbe(AppLog log) => _log = log;

    public UplinkChoice Resolve(UplinkConfig uplink, bool allowWifi) =>
        UplinkSelector.Select(Enumerate(), uplink.Mode, allowWifi, uplink.FallbackToLan);

    /// <summary>本机所有网卡（含 Wi-Fi、含不可用的），按“有线优先 + metric”排序。</summary>
    public IReadOnlyList<UplinkCandidate> Enumerate()
    {
        var profiles = ReadProfiles();
        var routes = DefaultRouteMetrics();
        var list = new List<UplinkCandidate>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;

                var props = nic.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var ip = ipv4?.Address.ToString();
                if (ip is null) continue;

                var gateway = props.GatewayAddresses
                    .Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                              !g.Address.Equals(IPAddress.Any));

                var type = nic.NetworkInterfaceType;
                var isWired = type is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx
                    or NetworkInterfaceType.Ethernet3Megabit;
                var isWifi = type == NetworkInterfaceType.Wireless80211;

                var guid = nic.Id.Trim('{', '}');
                profiles.TryGetValue(guid, out var profile);

                var isTethering = ip.StartsWith("192.168.137.", StringComparison.Ordinal) ||
                                  nic.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                                  nic.Name.Contains("本地连接*", StringComparison.Ordinal);

                var hasInternet = profile?.Internet == true;

                list.Add(new UplinkCandidate
                {
                    Id = nic.Id,
                    Name = nic.Name,
                    Description = nic.Description,
                    IsWired = isWired,
                    IsWifi = isWifi,
                    HasGateway = gateway,
                    HasInternet = hasInternet,
                    IsTethering = isTethering,
                    Ipv4 = ip,
                    Metric = routes.TryGetValue(ip, out var m) ? m : int.MaxValue / 2,
                    SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0
                });
            }
            catch (Exception ex)
            {
                _log.Debug($"跳过网卡 {SafeName(nic)}：{ex.Message}");
            }
        }

        return list
            .OrderBy(c => c.IsTethering ? 2 : c.IsWired ? 0 : 1)
            .ThenBy(c => c.Metric)
            .ThenByDescending(c => c.SpeedMbps)
            .ToList();
    }

    /// <summary>把选中的网卡映射回 WinRT 连接配置（开热点要用它）。</summary>
    public static ConnectionProfile? ProfileFor(UplinkCandidate candidate)
    {
        var id = candidate.Id.Trim('{', '}');
        foreach (var profile in SafeProfiles())
        {
            try
            {
                var adapterId = profile.NetworkAdapter?.NetworkAdapterId.ToString("D") ?? "";
                if (string.Equals(adapterId, id, StringComparison.OrdinalIgnoreCase)) return profile;
            }
            catch
            {
                // 某些配置文件读不到 adapter，跳过
            }
        }
        return null;
    }

    private sealed record ProfileInfo(bool Internet);

    private static Dictionary<string, ProfileInfo> ReadProfiles()
    {
        var map = new Dictionary<string, ProfileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in SafeProfiles())
        {
            try
            {
                var id = profile.NetworkAdapter?.NetworkAdapterId.ToString("D");
                if (string.IsNullOrEmpty(id)) continue;
                var level = profile.GetNetworkConnectivityLevel();
                map[id] = new ProfileInfo(level == NetworkConnectivityLevel.InternetAccess);
            }
            catch
            {
                // 忽略读不到连接级别的配置
            }
        }
        return map;
    }

    private static IReadOnlyList<ConnectionProfile> SafeProfiles()
    {
        try
        {
            return NetworkInformation.GetConnectionProfiles()?.ToList() ?? new List<ConnectionProfile>();
        }
        catch
        {
            return Array.Empty<ConnectionProfile>();
        }
    }

    /// <summary>读路由表里 0.0.0.0 默认路由的 metric：真正的“哪块网卡优先上网”。</summary>
    private static Dictionary<string, int> DefaultRouteMetrics()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("route.exe", "print -4")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return map;
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!System.Net.IPAddress.TryParse(parts[3], out _)) continue;
                if (!int.TryParse(parts[^1], out var metric)) continue;
                // 同一块网卡有多条默认路由时取最小 metric
                map[parts[3]] = map.TryGetValue(parts[3], out var old) ? Math.Min(old, metric) : metric;
            }
        }
        catch
        {
            // 没有 route.exe 或权限问题：退回按名称排序
        }
        return map;
    }

    private static string SafeName(NetworkInterface nic)
    {
        try { return nic.Name; } catch { return "?"; }
    }
}
