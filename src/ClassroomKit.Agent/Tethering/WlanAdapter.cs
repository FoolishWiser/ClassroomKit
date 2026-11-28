using System.Diagnostics;
using System.Text;

namespace ClassroomKit.Agent.Tethering;

/// <summary>
/// 无线网卡的应急操作。8821CU 这类老驱动在 soft-AP 下会出现「假启动」：
/// WinRT 报热点已开、虚拟适配器 Up，但空口不发信标，手机能看到 SSID 却连不上，
/// 只有 Restart-NetAdapter 才能真正救回来（2026-11-17 现场报告）。
/// </summary>
internal static class WlanAdapter
{
    /// <summary>读 Wi-Fi Direct（承载网络）虚拟适配器的累计接收字节；读不到返回 null。</summary>
    public static async Task<long?> ReadApReceivedBytesAsync(CancellationToken ct)
    {
        const string script = """
            $ad = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
                  Where-Object { $_.InterfaceDescription -like '*Wi-Fi Direct*' }
            if (-not $ad) { exit 3 }
            $rx = 0
            foreach ($a in $ad) {
              try { $s = Get-NetAdapterStatistics -Name $a.Name -ErrorAction Stop; $rx += $s.ReceivedBytes } catch { }
            }
            $rx
            """;

        var (code, stdout, _) = await RunAsync(script, 10000, ct).ConfigureAwait(false);
        if (code != 0) return null;
        return long.TryParse(stdout.Trim(), out var value) ? value : null;
    }

    /// <summary>复位物理无线网卡（需要管理员）。失败通常是没提权，或网卡驱动不支持。</summary>
    public static async Task<bool> RestartAsync(CancellationToken ct)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            # 机器上可能有两块无线网卡（内置 + USB）。以前取第一块，可能复位了没在用的那块，
            # 真正承载热点的那块纹丝不动。按默认路由 metric 排序，优先复位正在上网的那块。
            $ad = Get-NetAdapter -Physical -ErrorAction Stop |
                  Where-Object { $_.InterfaceDescription -match 'Wireless|Wi-Fi|WLAN|802\.11' -and
                                 $_.InterfaceDescription -notmatch 'Wi-Fi Direct|Virtual' } |
                  Sort-Object @{ Expression = {
                      $r = Get-NetRoute -InterfaceIndex $_.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
                           Sort-Object RouteMetric | Select-Object -First 1
                      if ($r) { [int]$r.RouteMetric } else { 9999 }
                  } } |
                  Select-Object -First 1
            if (-not $ad) { exit 3 }
            Restart-NetAdapter -Name $ad.Name -Confirm:$false -ErrorAction Stop
            'ok'
            """;

        var (code, stdout, _) = await RunAsync(script, 60000, ct).ConfigureAwait(false);
        return code == 0 && stdout.Trim() == "ok";
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string script, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
            {
                psi.ArgumentList.Add(arg);
            }

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "", "无法启动 powershell");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (-1, "", "超时");
            }

            return (proc.ExitCode,
                await ReadOrEmptyAsync(outTask).ConfigureAwait(false),
                await ReadOrEmptyAsync(errTask).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }

    /// <summary>子进程死后管道可能还没关：最多等 2 秒，读不到就当空。</summary>
    private static async Task<string> ReadOrEmptyAsync(Task<string> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false);
        if (done != task) return "";
        try { return await task.ConfigureAwait(false); } catch { return ""; }
    }
}
