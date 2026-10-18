namespace ClassroomKit.Core.Hotspot;

/// <summary>一块可能被当成热点的上行网卡（纯数据，便于在 Linux 上单测）。</summary>
public sealed record UplinkCandidate
{
    /// <summary>稳定标识：网卡 Id（GUID）或连接配置文件名。</summary>
    public string Id { get; init; } = "";
    /// <summary>网卡名，如“以太网 4”。</summary>
    public string Name { get; init; } = "";
    /// <summary>驱动/硬件描述，如“Intel(R) Ethernet Connection I219-LM”。</summary>
    public string Description { get; init; } = "";
    public bool IsWired { get; init; }
    public bool IsWifi { get; init; }
    /// <summary>有没有默认网关（没网关说明只有链路本地地址，共享出去也没意义）。</summary>
    public bool HasGateway { get; init; }
    /// <summary>Windows 网络位置感知（NCSI）判定这块网卡能上外网。</summary>
    public bool HasInternet { get; init; }
    /// <summary>本程序自己开出来的热点网卡（192.168.137.x），必须排除，否则会自己共享自己。</summary>
    public bool IsTethering { get; init; }
    public string? Ipv4 { get; init; }
    public int Metric { get; init; } = int.MaxValue;
    public long SpeedMbps { get; init; }

    public string Label => string.IsNullOrWhiteSpace(Name) ? Description : Name;

    /// <summary>有可用的 IPv4（排除 169.254 链路本地、127 回环）。</summary>
    public bool HasLanIp =>
        !string.IsNullOrWhiteSpace(Ipv4) &&
        !Ipv4.StartsWith("169.254.", StringComparison.Ordinal) &&
        !Ipv4.StartsWith("127.", StringComparison.Ordinal);

    /// <summary>能拿来开热点：不是自己开的热点，且有外网 / 有网关 / 至少有局域网 IP。</summary>
    public bool Usable => !IsTethering && (HasInternet || HasGateway || HasLanIp);

    /// <summary>优选层级：0 有外网，1 有网关（局域网），2 只有 IP（连网关都没配）。</summary>
    public int Tier => HasInternet ? 0 : (HasGateway ? 1 : 2);
}

public enum UplinkSource
{
    /// <summary>自动选中了有外网的网卡。</summary>
    AutoInternet = 0,
    /// <summary>谁都没外网，退一步共享局域网。</summary>
    AutoLanFallback = 1,
    /// <summary>用户在面板/配置里指定了网卡。</summary>
    Pinned = 2,
    /// <summary>只有 Wi-Fi 能上网，但策略不允许用 Wi-Fi 当上行。</summary>
    WifiBlocked = 3,
    /// <summary>一个能用的都没有。</summary>
    None = 4
}

public sealed record UplinkChoice
{
    public UplinkCandidate? Chosen { get; init; }
    public UplinkSource Source { get; init; } = UplinkSource.None;
    public bool InternetOk { get; init; }
    public IReadOnlyList<UplinkCandidate> Candidates { get; init; } = Array.Empty<UplinkCandidate>();

    public string Reason => Source switch
    {
        UplinkSource.AutoInternet => "自动选择：这块网卡有外网",
        UplinkSource.AutoLanFallback => Chosen is { HasGateway: false }
            ? "自动选择：网卡没有网关，只共享局域网"
            : "自动选择：所有网卡都没有外网，共享局域网",
        UplinkSource.Pinned => "按配置指定的网卡",
        UplinkSource.WifiBlocked => "只有 Wi-Fi 能上网，策略不允许拿 Wi-Fi 开热点",
        _ => "没有可用上行（网线没插或网卡未连接）"
    };

    /// <summary>给日志/面板用的一句话，比如“以太网 4（有外网）”。</summary>
    public string Describe()
    {
        if (Chosen is null) return Reason;
        var suffix = Chosen.HasInternet ? "有外网" : (Chosen.HasGateway ? "无外网·局域网" : "无网关");
        return $"{Chosen.Label}（{suffix}）";
    }
}

/// <summary>上行选择策略（纯逻辑：给定候选网卡 + 配置，选出该用哪块开热点）。</summary>
public static class UplinkSelector
{
    public const string Auto = "auto";

    /// <param name="candidates">本机所有候选网卡（含不可用的）。</param>
    /// <param name="mode">"auto" 或指定网卡的 Id / 名称 / 描述片段。</param>
    /// <param name="allowWifi">允许拿 Wi-Fi 当上行。</param>
    /// <param name="fallbackToLan">没有任何网卡能上外网时，仍用局域网网卡开热点（学生能访问本机，只是不能上网）。</param>
    public static UplinkChoice Select(
        IEnumerable<UplinkCandidate> candidates,
        string? mode = Auto,
        bool allowWifi = false,
        bool fallbackToLan = true)
    {
        var all = candidates.ToList();
        var pickable = all.Where(c => c.Usable && (c.IsWired || (c.IsWifi && allowWifi))).ToList();

        // 1) 指定网卡优先（指定了就尊重，找不到再回退自动）
        var pinned = string.IsNullOrWhiteSpace(mode) || string.Equals(mode, Auto, StringComparison.OrdinalIgnoreCase)
            ? null
            : pickable.FirstOrDefault(c => Matches(c, mode!));

        if (pinned is not null)
        {
            return new UplinkChoice
            {
                Chosen = pinned,
                Source = UplinkSource.Pinned,
                InternetOk = pinned.HasInternet,
                Candidates = all
            };
        }

        // 2) 有外网的优先，有线优先于无线，其次跳数（metric）小、速率高
        var withInternet = pickable.Where(c => c.HasInternet).OrderBy(Priority).ThenBy(Rank).ToList();
        if (withInternet.Count > 0)
        {
            return new UplinkChoice
            {
                Chosen = withInternet[0],
                Source = UplinkSource.AutoInternet,
                InternetOk = true,
                Candidates = all
            };
        }

        // 3) 没外网：能用的只剩 Wi-Fi 时按策略拒绝，绝不偷偷用 Wi-Fi 开热点
        var usable = all.Where(c => c.Usable).ToList();
        if (!allowWifi && usable.Count > 0 && usable.All(c => c.IsWifi))
        {
            return new UplinkChoice
            {
                Chosen = null,
                Source = UplinkSource.WifiBlocked,
                InternetOk = false,
                Candidates = all
            };
        }

        // 4) 退一步共享局域网（教室里没外网时，学生仍能连上访问本机资源）
        if (fallbackToLan)
        {
            var lan = pickable.OrderBy(c => c.Tier).ThenBy(Priority).ThenBy(Rank).ToList();
            if (lan.Count > 0)
            {
                return new UplinkChoice
                {
                    Chosen = lan[0],
                    Source = UplinkSource.AutoLanFallback,
                    InternetOk = lan[0].HasInternet,
                    Candidates = all
                };
            }
        }

        return new UplinkChoice
        {
            Chosen = null,
            Source = UplinkSource.None,
            InternetOk = false,
            Candidates = all
        };
    }

    private static int Priority(UplinkCandidate c) => c.IsWired ? 0 : 1;

    /// <summary>同类型网卡之间比：默认路由 metric 小的优先，然后是速率高的。</summary>
    private static (int Metric, long ReverseSpeed) Rank(UplinkCandidate c) =>
        (c.Metric, -c.SpeedMbps);

    private static bool Matches(UplinkCandidate c, string mode)
    {
        if (string.Equals(c.Id, mode, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(c.Name, mode, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(c.Description) &&
            c.Description.Contains(mode, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
