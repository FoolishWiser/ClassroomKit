using ClassroomKit.Agent.Hosts;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent.Service;

/// <summary>
/// SYSTEM 护航任务的一次执行：处理 hosts 写入请求、静默安装请求。
/// 由计划任务 ClassroomKit-Service 每 15 分钟叫一次，命令是 --service-tick。
/// </summary>
public static class ServiceTick
{
    public static int Run()
    {
        var log = new AppLog(Paths.LogDir);

        try
        {
            HandleHosts(log);
        }
        catch (Exception ex)
        {
            log.Warn($"护航任务处理 hosts 请求失败：{ex.Message}");
        }

        try
        {
            HandleInstall(log);
        }
        catch (Exception ex)
        {
            log.Warn($"护航任务处理安装请求失败：{ex.Message}");
        }

        return 0;
    }

    private static void HandleHosts(AppLog log)
    {
        var request = ServiceChannel.Read<HostsRequest>(ServiceChannel.HostsRequestPath);
        if (request is null) return;

        // 太老的请求（护航任务整夜没醒之类）直接丢弃，避免用过期 IP 写 hosts。
        if (DateTimeOffset.Now - request.At > TimeSpan.FromMinutes(30))
        {
            ServiceChannel.Delete(ServiceChannel.HostsRequestPath);
            log.Warn("丢弃过期的 hosts 请求");
            return;
        }

        var (ok, message) = request.Action == "restore"
            ? HostsApplier.Restore()
            : HostsApplier.Apply(request.Entries);

        if (ok) HostsApplier.FlushDnsSafe();
        ServiceChannel.Write(ServiceChannel.HostsStatusPath, new HostsStatus(
            request.Id, request.Action, ok, message, request.Action != "restore", request.Entries.Count, DateTimeOffset.Now));
        ServiceChannel.Delete(ServiceChannel.HostsRequestPath);
        log.Info($"护航任务 hosts {request.Action}：{message}");
    }

    private static void HandleInstall(AppLog log)
    {
        var request = ServiceChannel.Read<InstallRequest>(ServiceChannel.InstallRequestPath);
        if (request is null) return;

        var (ok, message) = InstallRunner.Run(request.MsiPath, log);
        ServiceChannel.Write(ServiceChannel.InstallStatusPath, new InstallStatus(request.Id, ok, message, DateTimeOffset.Now));
        ServiceChannel.Delete(ServiceChannel.InstallRequestPath);
        log.Info($"护航任务静默安装：{message}");
    }
}
