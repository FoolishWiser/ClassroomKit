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
}
