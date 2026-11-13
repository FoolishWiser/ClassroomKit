using System.Diagnostics;
using ClassroomKit.Core.Hosts;

namespace ClassroomKit.Agent.Hosts;

/// <summary>真正写 hosts 的那一步。用户态和 SYSTEM 护航任务共用。</summary>
public static class HostsApplier
{
    public static bool CanWrite()
    {
        try
        {
            using var stream = new FileStream(HostsPatcher.DefaultPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static (bool Applied, int Count) Inspect()
    {
        try
        {
            var (text, _) = HostsPatcher.Read(HostsPatcher.DefaultPath);
            var entries = HostsBlock.Extract(text);
            return (entries.Count > 0, entries.Count);
        }
        catch
        {
            return (false, 0);
        }
    }

    public static (bool Ok, string Message) Apply(IReadOnlyList<HostEntry> entries)
    {
        try
        {
            var path = HostsPatcher.DefaultPath;
            var (text, bom) = HostsPatcher.Read(path);
            HostsPatcher.BackupOnce(path);
            var merged = HostsBlock.Merge(text, entries);
            if (string.Equals(merged, text, StringComparison.Ordinal))
                return (true, "hosts 已经是目标内容，无需改动");
            HostsPatcher.Write(path, merged, bom);
            return (true, $"已写入 {entries.Count} 条 GitHub 加速记录");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (false, "没有权限写 hosts（需要管理员）：" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "写 hosts 失败：" + ex.Message);
        }
    }

    public static (bool Ok, string Message) Restore()
    {
        try
        {
            var path = HostsPatcher.DefaultPath;
            var (text, bom) = HostsPatcher.Read(path);
            if (!HostsBlock.HasBlock(text)) return (true, "hosts 里没有 ClassroomKit 写入的内容，无需还原");
            HostsPatcher.Write(path, HostsBlock.Restore(text), bom);
            return (true, "已删除 ClassroomKit 写入的加速记录");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (false, "没有权限改 hosts（需要管理员）：" + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, "还原 hosts 失败：" + ex.Message);
        }
    }

    /// <summary>刷 DNS 缓存，让刚写进去的记录立刻生效。</summary>
    public static void FlushDnsSafe()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(8000);
        }
        catch
        {
            // 刷 DNS 失败不影响写入结果
        }
    }
}
