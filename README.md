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

### 方式一：MSI 安装包（推荐）

```text
双击 ClassroomKit-0.1.0-x64.msi
# 或者命令行静默安装：
msiexec /i ClassroomKit-0.1.0-x64.msi /qn
```

装完自动：落地到 `C:\Program Files\ClassroomKit` → 建 `C:\ProgramData\ClassroomKit` 并授权普通用户可写 → 放宽设备上限（`WifiMaxPeers=16`）→ 注册登录自启任务 → 立刻拉起守护。开始菜单会多一项「ClassroomKit 控制面板」，点了直接带令牌打开面板（等价于 `ClassroomKit.Agent.exe --panel`）。

卸载走「应用和功能」，或 `msiexec /x ClassroomKit-0.1.0-x64.msi`：会关热点 → 删计划任务 → 把 `WifiMaxPeers` 恢复成系统默认 → 清掉 BCU 目录，**保留** `C:\ProgramData\ClassroomKit`（配置与日志；重装后密码和面板令牌不变）。

### 方式二：绿色目录（没有安装包时）

```powershell
# 1. 在一台装了 .NET 8 SDK 的 Windows 上打包（只需一次）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 2. 把 dist 整个目录拷到一体机，右键「以管理员身份运行 PowerShell」
cd D:\ClassroomKit
powershell -ExecutionPolicy Bypass -File install.ps1 -Ssid "CLASSROOM-302" -Passphrase "80258025" -TargetClients 10

# 3. 面板里确认「热点已开」，手机连上试一下
```

装完会自动：复制到 `C:\Program Files\ClassroomKit` → 建 `C:\ProgramData\ClassroomKit` 并授权普通用户可写 → 放宽设备上限 → 注册登录自启任务 → 启动守护。

「强力卸载」按钮要拉起的 BCUninstaller 约 100 MB（自带 .NET 运行时），默认不塞进安装包。需要时在一体机上跑一次：

```powershell
powershell -ExecutionPolicy Bypass -File get-bcu.ps1
```

它会把便携版解到 `C:\Program Files\ClassroomKit\BCU\`（也可手动放；或在 `config.json` 里写 `bcu.path`）。不放的话热点功能完全不受影响，只是点按钮会提示没找到。

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
| 强力卸载 | BCU 便携版 6.3.0 解压后 428 MB / 1348 文件，`7z t` 校验通过；面板按钮定位到 exe 并拉起（API 返回 ok、日志有记录；真机上先弹 UAC——BCU 清单是 requireAdministrator） |

## 目录

```text
src/ClassroomKit.Core     纯逻辑：配置、状态机、HTTP、API（可在 Linux 上跑单测）
src/ClassroomKit.Agent    WinRT 热点、托盘、计划任务、BCU 拉起
web/                      控制面板（无框架、无 CDN，离线可用）
scripts/                  build / build-msi / install / uninstall / rebuild / probe / get-bcu / verify
tests/ClassroomKit.Tests  零依赖测试（dotnet run，16 项）
docs/                     部署手册、ClassIsland 接入、原理与限制
installer/                MSI 定义（product.wxs；文件清单 files.wxs 由 build-msi.ps1 生成）
```

## 测试

```text
dotnet run --project tests/ClassroomKit.Tests -c Release
```

覆盖配置规范化与落盘、守护状态机九条分支（无上行 / 上行是 Wi-Fi / 策略关 / 正常开 / 退避 / 容量告警 / 自动关超时 / SSID 漂移 / 读状态失败）、HTTP 端到端（鉴权、表单与 JSON、静态文件）。

装到目标机之后，再跑一遍**整机验收**（30 项，含注册表、系统上限、策略开关、计划任务、改设置落盘、异常输入、BCU 按钮）：

```powershell
powershell -ExecutionPolicy Bypass -File verify.ps1
# 可选：-InstallDir "D:\ClassroomKit" -SkipRestart -ReportPath C:\ck-verify.txt
```

报告默认写 `%TEMP%\ck-verify.txt`，有失败就以退出码 1 结束（便于塞进部署脚本）。

## 文档

- [部署手册](docs/部署手册.md) —— 装、验、还原卡、升级、卸载、排障
- [ClassIsland 接入](docs/ClassIsland-接入.md) —— 三分钟配完，零代码
- [原理与限制](docs/原理与限制.md) —— 8 台是怎么破的、为什么不写服务、还剩什么限制
