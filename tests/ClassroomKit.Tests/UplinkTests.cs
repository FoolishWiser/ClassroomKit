using ClassroomKit.Core.Hotspot;
using static ClassroomKit.Tests.Harness;

namespace ClassroomKit.Tests;

/// <summary>上行网卡选择：多云卡 / 无外网 / 只允许有线 等场景。</summary>
internal static class UplinkTests
{
    private static UplinkCandidate Wired(string name, string ip, bool internet, int metric = 25, string desc = "Intel(R) Ethernet Connection") =>
        new()
        {
            Id = "{" + name + "-id}",
            Name = name,
            Description = desc,
            IsWired = true,
            HasGateway = true,
            HasInternet = internet,
            Ipv4 = ip,
            Metric = metric,
            SpeedMbps = 1000
        };

    private static UplinkCandidate Wifi(string name, string ip, bool internet, int metric = 20) =>
        new()
        {
            Id = "{" + name + "-id}",
            Name = name,
            Description = "Intel(R) Wi-Fi 6 AX201",
            IsWifi = true,
            HasGateway = true,
            HasInternet = internet,
            Ipv4 = ip,
            Metric = metric,
            SpeedMbps = 866
        };

    /// <summary>有 IP 但没网关（学校只给了地址/路由抖动时会出现）。</summary>
    private static UplinkCandidate NoGateway(string name, string ip) => new()
    {
        Id = "{" + name + "-id}",
        Name = name,
        Description = "Realtek PCIe GbE Family Controller",
        IsWired = true,
        HasGateway = false,
        HasInternet = false,
        Ipv4 = ip,
        Metric = 9999,
        SpeedMbps = 1000
    };

    private static UplinkCandidate Tethering() => new()
    {
        Id = "{tether-id}",
        Name = "本地连接* 12",
        Description = "Microsoft Wi-Fi Direct Virtual Adapter",
        IsWifi = true,
        HasGateway = true,
        HasInternet = false,
        IsTethering = true,
        Ipv4 = "192.168.137.1",
        Metric = 1
    };

    public static Task RunAsync()
    {
        var internetA = Wired("以太网", "192.168.31.154", true, 25);
        var internetB = Wired("以太网 5", "10.0.8.20", true, 5);
        var deadLan = Wired("以太网 2", "10.10.0.5", false, 1);

        return Task.WhenAll(
            Run("两块有线都有外网：选 metric 小的那块", () =>
            {
                var choice = UplinkSelector.Select(new[] { internetA, internetB });
                Eq(UplinkSource.AutoInternet, choice.Source);
                Eq("以太网 5", choice.Chosen?.Name, "选中");
                True(choice.InternetOk, "应标记有外网");
            }),

            // 这是学校最常见的坑：没网的那块 metric 更小（插在上联口 / 先启动），
            // 旧实现按“默认路由”挑会挑到它，学生连上就上不了网。
            Run("有外网的网卡 metric 更大也要选有外网的那块", () =>
            {
                var choice = UplinkSelector.Select(new[] { deadLan, internetA });
                Eq(UplinkSource.AutoInternet, choice.Source);
                Eq("以太网", choice.Chosen?.Name, "选中");
            }),

            Run("有线有外网 + Wi-Fi 有外网：优先有线", () =>
            {
                var choice = UplinkSelector.Select(new[] { Wifi("WLAN", "192.168.1.9", true, 1), internetA });
                Eq("以太网", choice.Chosen?.Name, "选中");
            }),

            Run("只有 Wi-Fi 能上网且不允许：拒绝开热点", () =>
            {
                var choice = UplinkSelector.Select(new[] { Wifi("WLAN", "192.168.1.9", true) }, allowWifi: false);
                Eq(UplinkSource.WifiBlocked, choice.Source);
                True(choice.Chosen is null, "不应选任何网卡");
            }),

            Run("只有 Wi-Fi 能上网且允许：用它", () =>
            {
                var choice = UplinkSelector.Select(new[] { Wifi("WLAN", "192.168.1.9", true) }, allowWifi: true);
                Eq(UplinkSource.AutoInternet, choice.Source);
                Eq("WLAN", choice.Chosen?.Name, "选中");
            }),

            // 学校整片断网时，热点仍要能开，学生至少能访问一体机上的课件
            Run("全都没外网：退回共享局域网", () =>
            {
                var choice = UplinkSelector.Select(new[] { deadLan }, fallbackToLan: true);
                Eq(UplinkSource.AutoLanFallback, choice.Source);
                Eq("以太网 2", choice.Chosen?.Name, "选中");
                False(choice.InternetOk, "应标记无外网");
                Contains(choice.Reason, "没有外网");
            }),

            // 连网关都没有（只配了 IP）：热点仍要能开，且优先级低于有网关的网卡
            Run("没有网关但有 IP：也能兜底开热点", () =>
            {
                var choice = UplinkSelector.Select(new[] { NoGateway("以太网 3", "10.0.0.7") }, fallbackToLan: true);
                Eq(UplinkSource.AutoLanFallback, choice.Source);
                Eq("以太网 3", choice.Chosen?.Name, "选中");
                Contains(choice.Reason, "没有网关");
                Contains(choice.Describe(), "无网关");
            }),

            Run("有网关的网卡优先于只有 IP 的网卡", () =>
            {
                var choice = UplinkSelector.Select(new[] { NoGateway("以太网 3", "10.0.0.7"), deadLan }, fallbackToLan: true);
                Eq("以太网 2", choice.Chosen?.Name, "选有网关的那块");
            }),

            Run("没有网关且禁止回退：不选", () =>
            {
                var choice = UplinkSelector.Select(new[] { NoGateway("以太网 3", "10.0.0.7") }, fallbackToLan: false);
                Eq(UplinkSource.None, choice.Source);
            }),

            Run("169.254 链路本地地址不算可用上行", () =>
            {
                var choice = UplinkSelector.Select(new[] { NoGateway("以太网 9", "169.254.13.7") }, fallbackToLan: true);
                Eq(UplinkSource.None, choice.Source);
            }),

            Run("全都没外网且禁止回退：不选", () =>
            {
                var choice = UplinkSelector.Select(new[] { deadLan }, fallbackToLan: false);
                Eq(UplinkSource.None, choice.Source);
                True(choice.Chosen is null, "不应选任何网卡");
            }),

            Run("指定网卡名：按名字选", () =>
            {
                var choice = UplinkSelector.Select(new[] { internetA, deadLan }, mode: "以太网 2");
                Eq(UplinkSource.Pinned, choice.Source);
                Eq("以太网 2", choice.Chosen?.Name, "选中");
                False(choice.InternetOk, "这块确实没外网");
            }),

            Run("指定驱动描述片段：也能命中", () =>
            {
                var other = Wired("以太网 7", "172.16.0.9", true, 30, "Realtek Gaming 2.5GbE");
                var choice = UplinkSelector.Select(new[] { internetA, other }, mode: "Realtek");
                Eq("以太网 7", choice.Chosen?.Name, "选中");
            }),

            Run("指定的网卡不存在：回退自动选择", () =>
            {
                var choice = UplinkSelector.Select(new[] { internetA, deadLan }, mode: "以太网 99");
                Eq(UplinkSource.AutoInternet, choice.Source);
                Eq("以太网", choice.Chosen?.Name, "选中");
            }),

            Run("本机热点网卡（192.168.137.x）永不被选", () =>
            {
                var choice = UplinkSelector.Select(new[] { Tethering(), deadLan }, fallbackToLan: true);
                Eq("以太网 2", choice.Chosen?.Name, "选中局域网卡而不是本机热点");
            }),

            Run("只有本机热点网卡时：没得选", () =>
            {
                var choice = UplinkSelector.Select(new[] { Tethering() });
                Eq(UplinkSource.None, choice.Source);
                True(choice.Chosen is null, "不应选本机热点");
            }),

            Run("Describe() 能看出有没有外网", () =>
            {
                var ok = UplinkSelector.Select(new[] { internetA }).Describe();
                var lan = UplinkSelector.Select(new[] { deadLan }).Describe();
                Contains(ok, "有外网");
                Contains(lan, "无外网");
            })
        );
    }
}
