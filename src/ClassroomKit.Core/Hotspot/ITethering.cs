using ClassroomKit.Core.Model;

namespace ClassroomKit.Core.Hotspot;

/// <summary>热点后端。真实实现走 WinRT；测试用假实现。</summary>
public interface ITethering
{
    Task<TetheringSnapshot> GetSnapshotAsync(CancellationToken ct);
    Task<bool> StartAsync(CancellationToken ct);
    Task<bool> StopAsync(CancellationToken ct);
    Task<bool> ConfigureAccessPointAsync(string ssid, string passphrase, CancellationToken ct);
    Task<bool> EnsureNoConnectionsTimeoutDisabledAsync(CancellationToken ct);
    Task<IReadOnlyList<TetherClient>> GetClientsAsync(CancellationToken ct);

    /// <summary>Wi-Fi Direct 虚拟适配器的累计接收字节；读不到返回 null。</summary>
    Task<long?> GetAccessPointReceivedBytesAsync(CancellationToken ct);

    /// <summary>复位物理无线网卡（假启动的唯一可靠恢复手段，需要管理员）。</summary>
    Task<bool> RestartWirelessAdapterAsync(CancellationToken ct);
}
