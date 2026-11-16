using System.Diagnostics;
using ClassroomKit.Core.Hosts;

namespace ClassroomKit.Agent.Hosts;

/// <summary>真正写 hosts 的那一步。用户态和 SYSTEM 护航任务共用。</summary>
public static class HostsApplier
{
    // 探测结果缓存：快照每秒调一次 CanWrite，每次都以 ReadWrite 打开 hosts 太浪费，还会和护航任务/杀毒软件抢文件锁。
    private static readonly object CanWriteLock = new();
    private static readonly TimeSpan CanWriteTtl = TimeSpan.FromSeconds(60);
    private static DateTimeOffset _canWriteCheckedAt = DateTimeOffset.MinValue;
    private static bool _canWrite;

    /// <summary>当前进程能不能写 hosts。结果缓存 60 秒。</summary>
    public static bool CanWrite()
    {
        lock (CanWriteLock)
        {
            var now = DateTimeOffset.Now;
            if (now - _canWriteCheckedAt < CanWriteTtl) return _canWrite;

            bool ok;
            try
            {
                using var stream = new FileStream(HostsPatcher.DefaultPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                ok = true;
            }
            catch
            {
                ok = false;
            }

            _canWrite = ok;
            _canWriteCheckedAt = now;
            return ok;
        }
    }

    /// <summary>写/还原 hosts 之后清掉缓存：权限或文件占用可能刚变化，下一次判断重新探测。</summary>
    public static void InvalidateCanWriteCache()
    {
        lock (CanWriteLock) _canWriteCheckedAt = DateTimeOffset.MinValue;
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
        finally
        {
            // 无论成败都清缓存：下一次 CanWrite 重新探测真实权限
            InvalidateCanWriteCache();
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
            if (process is null) return;
            // 重定向了就必须把两个流读掉：输出撑满管道时子进程会一直不退出，WaitForExit 也就形同虚设。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            try { Task.WaitAll(new Task[] { stdout, stderr }, 1000); } catch { }
        }
        catch
        {
            // 刷 DNS 失败不影响写入结果
        }
    }
}
