using System.Diagnostics;
using System.Security.Principal;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Core.Config;

namespace ClassroomKit.Agent.Shell;

/// <summary>
/// MSI 安装包在「文件落地之后 / 文件删除之前」调用的机器级配置步骤。
/// 这些事 MSI 自己做不了或者不方便做：建 ProgramData 并授权、生成配置、放宽 WifiMaxPeers、注册计划任务。
/// 由 Windows Installer 以 SYSTEM 身份（deferred, no impersonate）调用，因此这里不用弹任何 UI。
/// </summary>
internal static class MsiSetup
{
    private static string LogPath
    {
        get
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                return Path.Combine(Paths.LogDir, "msi-setup.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "ck-msi-setup.log");
            }
        }
    }

    private static void Log(string text)
    {
        Console.WriteLine(text);
        try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}"); } catch { }
    }

    /// <summary>安装后：建数据目录 → 生成配置（已有则保留）→ 放宽设备数上限 → 注册登录自启任务 → 尽量立刻启动。</summary>
    public static int Install()
    {
        Log("== ClassroomKit MSI 安装后配置 ==");

        Directory.CreateDirectory(Paths.DataDir);
        Directory.CreateDirectory(Paths.LogDir);
        GrantUsersModify(Paths.DataDir);
        Log($"数据目录：{Paths.DataDir}");

        var store = new ConfigStore(Paths.ConfigPath);
        Log($"配置：{Paths.ConfigPath}（热点 {store.Current.Hotspot.Ssid}，目标 {store.Current.Hotspot.TargetClients} 台）");

        var peers = Math.Clamp(store.Current.Hotspot.RegistryPeers, 9, 64);
        if (PeerLimit.Write(peers, out var msg))
        {
            Log(msg);
            PeerLimit.RestartIcs();
            Log("icssvc 已重启，新上限生效");
        }
        else
        {
            Log("警告：" + msg);
        }

        var (taskOk, taskMsg) = ScheduledTaskInstaller.Install(Paths.ExePath);
        Log(taskMsg);

        // 护航任务：SYSTEM 身份、开机 1 分钟后与每 15 分钟醒一次，负责写 hosts 和静默升级。
        var (svcOk, svcMsg) = ScheduledTaskInstaller.InstallService(Paths.ExePath);
        Log(svcMsg + (svcOk ? "" : "（hosts 加速与静默升级需要它，请以管理员运行一次 ClassroomKit.Agent.exe --install-service）"));

        // 计划任务挂在「Users 组 + 登录触发」，只会在用户登录后、以用户身份启动（托盘图标才看得见）。
        // MSI 的自定义动作跑在 Session 0，直接 /Run 会把进程拉进 Session 0（没托盘、没有交互桌面），
        // 所以这里用「交互令牌临时任务」把它送进用户会话；没有已登录用户就等下次登录，不算错误。
        var (runOk, runMsg) = ScheduledTaskInstaller.StartInUserSession(Paths.ExePath);
        Log(runOk ? "已请求立即启动：" + runMsg : "暂未启动（下次登录会自动启动）：" + runMsg);

        Log("安装后配置完成");
        return 0;
    }

    /// <summary>卸载前：关热点 → 停掉其它实例（不含自己）→ 删计划任务 → 恢复 WifiMaxPeers → 清 BCU 目录。</summary>
    public static int Cleanup()
    {
        Log("== ClassroomKit MSI 卸载前清理 ==");

        try
        {
            var store = new ConfigStore(Paths.ConfigPath);
            if (store.Current.Hotspot.Enabled) { /* 下面统一停进程即可 */ }
        }
        catch (Exception ex)
        {
            Log("读取配置失败（继续）：" + ex.Message);
        }

        StopOtherInstances();

        var (taskOk, taskMsg) = ScheduledTaskInstaller.Uninstall();
        Log(taskMsg + (taskOk ? "" : "（可能本来就没注册）"));

        var (svcOk, svcMsg) = ScheduledTaskInstaller.UninstallService();
        Log(svcMsg + (svcOk ? "" : "（可能本来就没注册）"));

        if (PeerLimit.Read() is not null)
        {
            if (PeerLimit.Delete(out var msg))
            {
                Log(msg);
                PeerLimit.RestartIcs();
                Log("icssvc 已重启，恢复系统默认上限");
            }
        }
        else
        {
            Log("没有自定义的 WifiMaxPeers，跳过");
        }

        // BCU 是运行时才装进去的便携版（不由 MSI 登记），必须先停掉它自己占着的文件再删，
        // 否则卸载会留下半个目录（实测：BCUninstaller.exe 正开着 → "Access to the path 'Accessibility.dll' is denied"）。
        var bcuDir = Path.Combine(Paths.ExeDir, "BCU");
        if (Directory.Exists(bcuDir))
        {
            StopBcuProcesses();
            if (DeleteWithRetry(bcuDir, attempts: 8, delayMs: 400))
            {
                Log("已删除强力卸载工具目录：" + bcuDir);
            }
            else
            {
                Log("删除 BCU 目录失败（目录仍占用，可手动删除）：" + bcuDir);
            }
        }

        // 配置与日志（C:\ProgramData\ClassroomKit）故意保留：重装后密码/面板令牌不变，
        // 需要彻底清除请手动删除该目录，或用 uninstall.ps1 -RemoveData。
        Log("保留数据目录（配置与日志）：" + Paths.DataDir);

        Log("卸载前清理完成");
        return 0;
    }

    /// <summary>BCU 便携版自己会常驻（主窗口 + 可能的更新器），删目录前先结束它们。</summary>
    private static void StopBcuProcesses()
    {
        var killed = 0;
        foreach (var name in new[] { "BCUninstaller", "BCUninstallerUpdater", "BCUninstaller64" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex)
                {
                    Log($"停止 {name}（{p.Id}）失败：{ex.Message}");
                }
            }
        }
        if (killed > 0) Log($"已结束 {killed} 个强力卸载工具进程");
    }

    /// <summary>删目录带重试：被杀掉的进程释放文件句柄需要一点时间；顺带清掉只读属性。</summary>
    private static bool DeleteWithRetry(string dir, int attempts, int delayMs)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (Exception ex)
            {
                if (i == attempts - 1) Log("最后一次删除尝试失败：" + ex.Message);
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    }
                }
                catch { }
                Thread.Sleep(delayMs);
            }
        }
        return false;
    }

    private static void StopOtherInstances()
    {
        var self = Environment.ProcessId;
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("ClassroomKit.Agent"))
        {
            if (p.Id == self) continue;
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
                killed++;
            }
            catch (Exception ex)
            {
                Log($"停止进程 {p.Id} 失败：{ex.Message}");
            }
        }
        Log(killed == 0 ? "没有其它实例在运行" : $"已停止 {killed} 个实例");
    }

    private static void GrantUsersModify(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls.exe", $"\"{dir}\" /grant *S-1-5-32-545:(OI)(CI)M /T")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(20000);
            Log("已授权 Users 组可写（面板改设置、写日志都要用）");
        }
        catch (Exception ex)
        {
            Log("icacls 失败（面板可能无法保存设置）：" + ex.Message);
        }
    }

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
