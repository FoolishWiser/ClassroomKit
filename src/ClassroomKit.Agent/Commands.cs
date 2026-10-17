using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ClassroomKit.Agent.Shell;
using ClassroomKit.Agent.Tasks;
using ClassroomKit.Agent.Tethering;
using ClassroomKit.Core.Config;
using ClassroomKit.Core.Logging;

namespace ClassroomKit.Agent;

/// <summary>命令行入口（--status / --ensure / --on / --off / --fix-peers / --install-task / --probe）。</summary>
internal static class Commands
{
    public static string Version => typeof(Commands).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static int PrintVersion()
    {
        Console.WriteLine($"ClassroomKit.Agent {Version}");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine($"""
ClassroomKit.Agent {Version}

  (无参数)          常驻守护 + 托盘图标 + Web 面板
  --ensure          确保守护在跑且热点开着（在跑则秒退，供 ClassIsland 调用）
  --on / --off      打开 / 关闭热点策略
  --status          输出当前状态（JSON）
  --probe           收集本机能力信息（JSON），用于 P0 评估
  --fix-peers       提权写入 WifiMaxPeers 并重启 icssvc（放宽设备数上限）
  --install-task    注册登录自启计划任务
  --uninstall-task  删除登录自启计划任务
  --version         版本号
""");
        return 0;
    }

    public static int Status()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var port = store.Current.Port;
        var token = store.Current.Token;
        var json = TryGetState(port, token);
        if (json is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                running = false,
                port,
                configPath = Paths.ConfigPath,
                panel = $"http://127.0.0.1:{port}/?t={token}"
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 3;
        }

        Console.WriteLine(json);
        return 0;
    }

    public static int Ensure()
    {
        var store = new ConfigStore(Paths.ConfigPath);
        if (IsRunning(store, out _))
        {
            if (PostAction(store, "ensure", out var message))
            {
                Console.WriteLine($"已在运行：{message}");
                return 0;
            }
        }

        StartDaemon();
        Console.WriteLine("已启动 ClassroomKit.Agent");
        return 0;
    }

    public static int SetPolicy(bool enabled)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        if (IsRunning(store, out _))
        {
            var ok = PostJson(store, "/api/policy", JsonSerializer.Serialize(new { enabled }), out var message);
            Console.WriteLine(ok ? (enabled ? "热点策略：开" : "热点策略：关") : $"失败：{message}");
            return ok ? 0 : 1;
        }

        store.Update(c =>
        {
            c.Hotspot.Enabled = enabled;
            return true;
        });
        Console.WriteLine(enabled ? "热点策略已写为开启（守护未运行，下次启动生效）" : "热点策略已写为关闭");
        return 0;
    }

    public static int InstallTask()
    {
        var (ok, message) = ScheduledTaskInstaller.Install(Paths.ExePath);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int UninstallTask()
    {
        var (ok, message) = ScheduledTaskInstaller.Uninstall();
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    public static int FixPeersCli()
    {
        var (ok, message) = FixPeersCore(null);
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    /// <summary>写 WifiMaxPeers 并重启 icssvc；没提权就拉起自己一次（会弹 UAC）。</summary>
    public static (bool Ok, string Message) FixPeersCore(AppLog? log)
    {
        var store = new ConfigStore(Paths.ConfigPath);
        var peers = Math.Clamp(store.Current.Hotspot.RegistryPeers, 9, 64);

        if (!AdminElevation.IsElevated())
        {
            try
            {
                File.Delete(ResultPath);
            }
            catch
            {
                // 忽略
            }

            var code = AdminElevation.RunElevatedAndWait("--fix-peers");
            var reported = ReadResult();
            if (reported is not null) return (code == 0, reported);
            return (code == 0, code == 0 ? "已提权执行" : $"提权执行失败（退出码 {code}）");
        }

        var ok = PeerLimit.Write(peers, out var message);
        if (ok)
        {
            PeerLimit.RestartIcs(log);
            message += "；已重启 icssvc，重新开热点后生效";
        }

        WriteResult(message);
        log?.Info($"修复设备上限：{message}");
        return (ok, message);
    }

    public static int ProbeCli()
    {
        var json = Probe.Probe.Collect();
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Path.Combine(Paths.DataDir, "probe.json"), json);
        }
        catch
        {
            // 写不进 ProgramData 也不影响输出
        }

        Console.WriteLine(json);
        return 0;
    }

    private static string ResultPath => Path.Combine(Paths.DataDir, "last-fix.txt");

    private static void WriteResult(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(ResultPath, message);
        }
        catch
        {
            // 忽略
        }
    }

    private static string? ReadResult()
    {
        try
        {
            return File.Exists(ResultPath) ? File.ReadAllText(ResultPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void StartDaemon()
    {
        var psi = new ProcessStartInfo(Paths.ExePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.ExeDir
        };
        Process.Start(psi)?.Dispose();
    }

    private static bool IsRunning(ConfigStore store, out string? stateJson)
    {
        stateJson = TryGetState(store.Current.Port, store.Current.Token);
        return stateJson is not null;
    }

    private static string? TryGetState(int port, string token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/state");
            request.Headers.Add("X-Token", token);
            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode) return null;
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static bool PostJson(ConfigStore store, string path, string json, out string message)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{store.Current.Port}{path}");
            request.Headers.Add("X-Token", store.Current.Token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = client.Send(request);
            message = response.IsSuccessStatusCode ? "ok" : $"HTTP {(int)response.StatusCode}";
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static bool PostAction(ConfigStore store, string action, out string message) =>
        PostJson(store, "/api/action", JsonSerializer.Serialize(new { action }), out message);
}
