# ClassroomKit · 教室一体机热点

开机就把热点开起来，老师不用碰、学生连上就能上网。附带一个「强力卸载」入口（BCUninstaller 便携版）。

- 一个 **单文件 exe**（约 70 MB，自包含，目标机不需要装 .NET）
- 托盘常驻 + 本地网页控制面板（`http://127.0.0.1:36037/?t=令牌`）
- 守护式：热点被关、SSID 被改、网卡抽风、系统到点自动关热点，都会自动恢复
- 登录自启（计划任务，延迟 8 秒），老师还在输密码时热点就已经起来了

## 为什么不是「ClassIsland 插件」或「现成热点软件」

| 方案 | 问题 |
| --- | --- |
| ClassIsland 插件 | 热点会跟着课表软件一起崩/一起升级；它要等界面和课表加载完（10–40 秒）才启动，比开机晚；插件 API 跨大版本会变 |
| Connectify 等商业热点 | 闭源、付费、CLI 要高版本授权，学校环境下授权和静默部署都麻烦 |
| 各种开源 hotpot | 底下都是 `netsh wlan hostednetwork`（Win10 已废弃，Intel AX210 直接「不支持承载网络」）或系统 icssvc，一样卡 8 台 |
| 自己开 Windows 服务 | 服务跑在 Session 0，绑定不了「当前登录用户」的连接配置文件，起不来热点 |

所以：**热点是基础设施，必须独立于课表软件活着**。ClassIsland 只在旁边做一件小事——用它的「自动化」在开机/上课时喊一声 `--ensure`（见 `docs/ClassIsland-接入.md`，零代码）。

至于 8 台上限：那是 Windows 移动热点自己的默认值，不是网卡死限。本工具在安装时写 `WifiMaxPeers=16` 并重启 `icssvc`，实测 `MaxClientCount` 从 8 变成 16，**不需要第三方驱动**。

## 快速开始

```powershell
# 1. 在一台装了 .NET 8 SDK 的 Windows 上打包（只需一次）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 2. 把 dist 整个目录拷到一体机，右键「以管理员身份运行 PowerShell」
cd D:\ClassroomKit
powershell -ExecutionPolicy Bypass -File install.ps1 -Ssid "CLASSROOM-302" -Passphrase "80258025" -TargetClients 10

# 3. 面板里确认「热点已开」，手机连上试一下
```

装完会自动：复制到 `C:\Program Files\ClassroomKit` → 建 `C:\ProgramData\ClassroomKit` 并授权普通用户可写 → 放宽设备上限 → 注册登录自启任务 → 启动守护。

## 命令行

```text
ClassroomKit.Agent.exe            常驻守护 + 托盘 + 面板
--ensure                          确保守护在跑且热点开着（在跑则秒退；给 ClassIsland 调用）
--on / --off                      开 / 关热点策略
--status                          打印状态 JSON
--probe                           收集本机能力（JSON），评估能不能用
--fix-peers                       提权写 WifiMaxPeers 并重启 icssvc
--install-task / --uninstall-task 注册 / 删除登录自启任务
--version
```

## HTTP API（全部要令牌：`?t=...` 或请求头 `X-Token`）

| 方法与路径 | 说明 |
| --- | --- |
| `GET /api/state` | 完整状态：守护状态、热点、上行、容量、BCU |
| `GET /api/clients` | 在线设备列表 |
| `GET /api/logs?take=100` | 最近日志 |
| `GET /api/health` | 存活探针 |
| `POST /api/action` | `action=start\|stop\|ensure\|fix-peers\|bcu\|exit`（form 或 JSON 均可） |
| `POST /api/policy` | `enabled=true\|false` |
| `POST /api/settings` | `ssid` / `passphrase` / `targetClients` / `allowWifiUplink` |

## 配置（`C:\ProgramData\ClassroomKit\config.json`）

```json
{
  "port": 36037,
  "token": "32 位十六进制，首次启动自动生成",
  "hotspot": {
    "enabled": true,
    "ssid": "CLASSROOM-302",
    "passphrase": "80258025",
    "band": "auto",
    "targetClients": 10,
    "registryPeers": 16,
    "allowWifiUplink": false,
    "guardIntervalSec": 15,
    "autoStartOnLaunch": true
  },
  "bcu": { "enabled": true, "path": "" },
  "tray": { "enabled": true }
}
```

越界值会被自动纠正（端口 1024–65535、巡检 ≥3 秒、目标 ≥1 台），坏 JSON 会回退默认并重写。

## 实测数据（ZYT 的 Win10 一体机，Intel AX210）

| 项目 | 结果 |
| --- | --- |
| 设备上限 | 注册表前 8 台 → 写 `WifiMaxPeers=16` 后 **16 台** |
| 开机到热点可用 | 计划任务延迟 8 秒 + 约 1 秒拉起 |
| 常驻内存 | 私有约 **56 MB**，工作集约 152 MB |
| 空闲 CPU | 60 秒采样 0.06 秒 ≈ **0.1%**（单核） |
| 无设备自动关 | 已显式关闭（否则 5 分钟热点自己消失） |

## 目录

```text
src/ClassroomKit.Core     纯逻辑：配置、状态机、HTTP、API（可在 Linux 上跑单测）
src/ClassroomKit.Agent    WinRT 热点、托盘、计划任务、BCU 拉起
web/                      控制面板（无框架、无 CDN，离线可用）
scripts/                  build / install / uninstall / rebuild / probe
tests/ClassroomKit.Tests  零依赖测试（dotnet run，16 项）
docs/                     部署手册、ClassIsland 接入、原理与限制
```

## 测试

```text
dotnet run --project tests/ClassroomKit.Tests -c Release
```

覆盖配置规范化与落盘、守护状态机九条分支（无上行 / 上行是 Wi-Fi / 策略关 / 正常开 / 退避 / 容量告警 / 自动关超时 / SSID 漂移 / 读状态失败）、HTTP 端到端（鉴权、表单与 JSON、静态文件）。

## 文档

- [部署手册](docs/部署手册.md) —— 装、验、还原卡、升级、卸载、排障
- [ClassIsland 接入](docs/ClassIsland-接入.md) —— 三分钟配完，零代码
- [原理与限制](docs/原理与限制.md) —— 8 台是怎么破的、为什么不写服务、还剩什么限制
