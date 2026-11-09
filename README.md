# ClassroomKit · 教室一体机热点

开机就把热点开起来，老师不用碰、学生连上就能上网。附带一个「强力卸载」入口（BCUninstaller 便携版）。

- 一个 **单文件 exe**（约 70 MB，自包含，目标机不需要装 .NET）
- 托盘常驻 + 本地网页控制面板（`http://127.0.0.1:36037/?t=令牌`）
- 守护式：热点被关、SSID 被改、网卡抽风、系统到点自动关热点，都会自动恢复
- 登录自启（计划任务，延迟 8 秒），老师还在输密码时热点就已经起来了

![控制面板](docs/img/panel.png)

## 为什么不是「ClassIsland 插件」或「现成热点软件」

| 方案 | 问题 |
| --- | --- |
| ClassIsland 插件 | 热点会跟着课表软件一起崩/一起升级；它要等界面和课表加载完（10–40 秒）才启动，比开机晚；插件 API 跨大版本会变 |
| Connectify 等商业热点 | 闭源、付费、CLI 要高版本授权，学校环境下授权和静默部署都麻烦 |
| 各种开源 hotpot | 底下都是 `netsh wlan hostednetwork`（Win10 已废弃，Intel AX210 直接「不支持承载网络」）或系统 icssvc，一样卡 8 台 |
| 自己开 Windows 服务 | 服务跑在 Session 0，绑定不了「当前登录用户」的连接配置文件，起不来热点 |

所以：**热点是基础设施，必须独立于课表软件活着**。ClassIsland 只在旁边做一件小事——用它的「自动化」在开机/上课时喊一声 `--ensure`（见 `docs/ClassIsland-接入.md`，零代码）。

至于 8 台上限：那是 Windows 移动热点自己的默认值，不是网卡死限。本工具在安装时写 `WifiMaxPeers=16` 并重启 `icssvc`，实测 `MaxClientCount` 从 8 变成 16，**不需要第三方驱动**。

## 下载

预编译安装包在 [Releases](https://github.com/FoolishWiser/ClassroomKit/releases/latest)：

- `ClassroomKit-0.2.2-x64.msi`（约 62 MB，推荐）
- `ClassroomKit-0.2.2-win-x64.zip`（绿色版）

两者都是自包含单文件，目标机不需要装 .NET。

## 快速开始

### 方式一：MSI 安装包（推荐）

```text
双击 ClassroomKit-0.2.2-x64.msi
# 或者命令行静默安装：
msiexec /i ClassroomKit-0.2.2-x64.msi /qn
```

装完自动：落地到 `C:\Program Files\ClassroomKit` → 建 `C:\ProgramData\ClassroomKit` 并授权普通用户可写 → 放宽设备上限（`WifiMaxPeers=16`）→ 注册登录自启任务 → 立刻拉起守护。开始菜单会多一项「ClassroomKit 控制面板」，点了直接带令牌打开面板（等价于 `ClassroomKit.Agent.exe --panel`）。

卸载走「应用和功能」，或 `msiexec /x ClassroomKit-0.2.2-x64.msi`：会关热点 → 删计划任务 → 把 `WifiMaxPeers` 恢复成系统默认 → 清掉 BCU 目录，**保留** `C:\ProgramData\ClassroomKit`（配置与日志；重装后密码和面板令牌不变）。

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

## 升级

不用重装、不用换包：软件自己从 GitHub Releases 查新版并安装（只在你点的时候查，开机时不联网）。

| 入口 | 操作 |
| --- | --- |
| 面板「升级」卡片 | 点「检查更新」→ 有新版本时点「下载并升级」 |
| 托盘右键 | 「检查更新…」→ 一路确认 |
| 命令行 | `ClassroomKit.Agent.exe --check-update`（只看，不装）；`--update` 下载并安装 |

安装是标准 MSI 升级：会弹一次 UAC（点「是」），热点会中断几秒，其余配置、令牌、日志都保留。
学校网络屏蔽 GitHub 时，检查更新会直接告诉你「连不上 GitHub」，这时手动下载 MSI 双击装即可。下载本身会先直连、失败再自动走 `update.mirrors` 里的镜像（默认 `https://ghproxy.net/`），落地后仍按 GitHub 给的 sha256 校验，镜像改不了包。

升级失败会写 `C:\ProgramData\ClassroomKit\logs\update.log`（msiexec 退出码也在里面）。

## 命令行

```text
ClassroomKit.Agent.exe            常驻守护 + 托盘 + 面板
--ensure                          确保守护在跑且热点开着（在跑则秒退；给 ClassIsland 调用）
--on / --off                      开 / 关热点策略
--status                          打印状态 JSON
--probe                           收集本机能力（JSON），评估能不能用
--fix-peers                       提权写 WifiMaxPeers 并重启 icssvc
--install-task / --uninstall-task 注册 / 删除登录自启任务
--uplinks                        列出本机所有网卡，说明会挑哪块开热点
--uninstall                      卸载自己（MSI 装的走 msiexec /x；绿色版自己删目录）
--uninstall --purge-data         连 C:\ProgramData\ClassroomKit（配置/日志）一起删
--panel                          用默认浏览器打开面板
--save-classin-board            保存 ClassInX 板书（三种格式）并关闭 ClassInX；ClassInX 没开时先自动拉起
--save-classin-board --no-close 只保存，不关 ClassInX
--save-classin-board --no-launch 不自动启动 ClassInX（没开就直接报错）
--conflict-check                 检查板书软件是否全屏冲突（JSON；无冲突退出码 8）
--conflict-preview               在屏幕上预览一次冲突提示窗
--probe-classin                  重新探测 ClassInX 保存对话框的坐标并写入 ui-recipe.json
--check-update                   检查 GitHub 上有没有新版本（只查不装；--pre 连预发布一起看）
--update                         下载并安装新版（--dry-run 只检查不安装）
--version
```

## HTTP API（全部要令牌：`?t=...` 或请求头 `X-Token`）

| 方法与路径 | 说明 |
| --- | --- |
| `GET /api/state` | 完整状态：守护状态、热点、上行、容量、BCU |
| `GET /api/clients` | 在线设备列表 |
| `GET /api/logs?take=100` | 最近日志 |
| `GET /api/health` | 存活探针 |
| `GET /api/uplinks` | 本机所有网卡 + 自动选中的那块 + 选择理由 |
| `GET /api/board` | 板书相关：ClassInX 是否在跑、保存目录、冲突可见性判定与冷却状态 |
| `POST /api/action` | `action=start\|stop\|ensure\|fix-peers\|bcu\|uninstall\|exit\|save-classin-board\|conflict-check\|conflict-choice\|conflict-preview`（form 或 JSON 均可） |

`action` 细节：`uninstall` 可带 `keepData=true`；`save-classin-board` 可带 `closeAfterSave=false` 只存不关；
`conflict-check` 可带 `force=true` 忽略冷却；`conflict-choice` 接受 `choice=keep-both|keep-classin|keep-seewo|keep-none`（给 ClassIsland 或外部脚本回应用弹窗用）；`conflict-preview` 只在屏幕上弹一次提示窗，不关闭任何软件。
| `POST /api/policy` | `enabled=true\|false` |
| `POST /api/settings` | `ssid` / `passphrase` / `targetClients` / `allowWifiUplink` / `uplinkMode` / `fallbackToLan` |

## 多块网卡：自动挑「真有外网」的那块

一体机上常有好几块有线网卡（校内网 / 监控 / 预留），学校还经常整片断外网。程序不靠“Windows 默认连接”，而是自己枚举所有网卡再挑：

1. **有外网**（NCSI 判定能上网）的优先；同是有外网时，有线优先于无线，再看默认路由 metric（小者优先）和速率；
2. 谁都没外网 → **退回局域网兜底**（`fallbackToLan`）：只要有网关就用，学生连上能访问一体机上的课件，只是上不了外网；
3. 连网关都没配、只有 IP（学校只给地址、或网络抖动刚把默认路由删掉）→ 也**允许兜底**开热点，优先级低于有网关的网卡；
4. 本机自己开出来的热点网卡（`192.168.137.x` / Wi-Fi Direct）永不被选，避免自己共享自己；
5. 想固定用某块：面板「上行网卡」下拉选它，或配置里写 `uplink.mode`（网卡名 / Id / 驱动描述片段都认）。指定的网卡拔线了会自动回退自动选择。

面板每 15 秒刷新网卡列表，显示每块是「有外网 / 无外网 / 无网关 / 本机热点」。命令行查：`ClassroomKit.Agent.exe --uplinks`。

## 板书：一键保存并关闭 ClassInX

托盘右键 → **保存 ClassInX 板书并关闭 ClassInX**：自动完成「Ctrl+S → 保存至本地 → 三种格式全勾 → 保存 → 关闭 ClassInX」，实测 **1.8 秒**（人工大约 15 秒，还要记得勾 pdf）。

- 文件落在 ClassInX 的默认保存位置：`文档\ClassIn X Files\黑板_<日期>_<时间>.{edb,png,pdf}`，保存完成后气泡会告诉你存了哪几个；
- 同名文件已存在时 ClassInX 会逐个弹「是否替换」，程序自动点「是」，不需要人盯着；
- 想只存不关（比如接着讲）：命令行加 `--no-close`，配置文件里把 `boardSave.closeAfterSave` 设成 `false`；
- **ClassInX 没开也能用**：程序会自己找到 `ClassIn X.exe` 并启动它，等板书窗口出现（默认最多 60 秒，`boardSave.launchTimeoutSec` 可调 5–600），然后接着走保存流程；装到别的盘也没关系（配置路径 → 运行中进程 → 卸载注册表 → 常见目录，依次找）；不想要这个行为就设 `boardSave.launchIfNeeded = false` 或命令行加 `--no-launch`；
- 板书是空的（或 ClassInX 刚打开停在启动台）时，它只会弹「黑板内容为空」——程序会把提示框关掉并告诉你「请先在板书上写点内容」，不会假装成功；
- 换了 ClassInX 版本、按钮位置变了怎么办：托盘菜单或 `ClassroomKit.Agent.exe --probe-classin` 重新探测一次，坐标写进 `C:\ProgramData\ClassroomKit\ui-recipe.json`。探测失败时改这个 JSON 也能手工修。

## 板书软件冲突提示

教室里常同时开着**希沃白板**和 **ClassInX**，两个悬浮窗叠在全屏内容上，学生看不清。程序每 2 秒看一眼窗口，命中「两个悬浮窗同时存在 + 有东西占满全屏」时弹一个置顶提示：

![冲突提示窗](docs/img/conflict-dialog.png)

四个选项就是四种处理，选完立刻执行：

| 选项 | 动作 |
| --- | --- |
| 都保留 | 什么都不做（60 秒不选也是这个） |
| 保留 ClassInX | 关闭希沃白板（先关窗口，5 秒不退再结束进程） |
| 保留希沃白板 | 关闭 ClassInX |
| 都不保留 | 两个都关 |

「全屏」认三种：**WPS 放映**（标题 `WPS Presentation Slide Show` + 进程 `wpp`，实测 WPS 12 通用宿主下放映窗是 `Qt5QWindowIcon` 且**不置顶**）、**ClassInX 板书全屏**、**希沃白板全屏**；还可以在 `conflict.extraFullscreenProcesses` 里加自己学校的放映程序（按进程名）。

不打扰的细节：同一个冲突只弹一次，之后 **40 分钟**内不再弹（`conflict.cooldownMinutes` 可调 1–1440）；想立刻再检查一次，托盘菜单 →「检查板书软件冲突」，或 `POST /api/action {"action":"conflict-check","force":true}`；ClassIsland 也能接：收到提示后由班牌/自动化调 `conflict-choice` 直接给出选择。

## 卸载

- **MSI 装的**：控制面板「应用」里卸载，或 `msiexec /x ClassroomKit-0.2.2-x64.msi`；面板「维护 → 卸载本软件」也会调起同样的流程。
- **绿色版**：面板「维护 → 卸载本软件」，或 `ClassroomKit.Agent.exe --uninstall`。
- 卸载会停进程、删自启任务、把 `WifiMaxPeers` 改回原值并重启共享服务、删安装目录（含 BCU）；**默认保留** `C:\ProgramData\ClassroomKit`（配置和日志，方便重装后沿用 SSID/密码）。想连配置日志一起删：面板勾「同时删除配置与日志」再点卸载，或 `ClassroomKit.Agent.exe --uninstall --purge-data`（这两种方式都会先调 `msiexec /x` 再删目录；直接 `msiexec /x` 卸载则一定保留配置）。
- 需要管理员权限：没提权会弹一次 UAC，点「是」即可。

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
  "uplink": {
    "mode": "auto",
    "fallbackToLan": true
  },
  "boardSave": {
    "enabled": true,
    "closeAfterSave": true,
    "launchIfNeeded": true,
    "launchTimeoutSec": 60,
    "classInPath": null
  },
  "conflict": {
    "enabled": true,
    "cooldownMinutes": 40,
    "extraFullscreenProcesses": []
  },
  "update": {
    "enabled": true,
    "repo": "FoolishWiser/ClassroomKit",
    "includePrerelease": false,
    "mirrors": ["https://ghproxy.net/"]
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
| ClassInX 板书保存 | 全轮询实现 **1.76 秒**（Ctrl+S 160ms / 对话框 340ms / 面板 490ms / 补勾 pdf 470ms / 三个覆盖确认 800ms） |
| 冲突提示判定 | 每 2 秒扫一次可见窗口；40 分钟冷却；弹窗 60 秒倒计时默认「都保留」 |
| 强力卸载 | BCU 便携版 6.3.0 解压后 428 MB / 1348 文件，`7z t` 校验通过；面板按钮定位到 exe 并拉起（API 返回 ok、日志有记录；真机上先弹 UAC——BCU 清单是 requireAdministrator） |

## 目录

```text
src/ClassroomKit.Core     纯逻辑：配置、状态机、HTTP、API（可在 Linux 上跑单测）
src/ClassroomKit.Agent    WinRT 热点、托盘、计划任务、BCU 拉起
web/                      控制面板（无框架、无 CDN，离线可用）
scripts/                  build / build-msi / install / uninstall / rebuild / probe / get-bcu / verify
tests/ClassroomKit.Tests  零依赖测试（dotnet run，55 项）
docs/                     部署手册、ClassIsland 接入、原理与限制
installer/                MSI 定义（product.wxs；文件清单 files.wxs 由 build-msi.ps1 生成）
```

## 测试

```text
dotnet run --project tests/ClassroomKit.Tests -c Release
```

63 项，覆盖配置规范化与落盘、上行网卡选择、守护状态机九条分支（无上行 / 上行是 Wi-Fi / 策略关 / 正常开 / 退避 / 容量告警 / 自动关超时 / SSID 漂移 / 读状态失败）、窗口分类与冲突冷却、UI 配方匹配、GitHub Release 解析与版本比较、HTTP 端到端（鉴权、表单与 JSON、静态文件）。

装到目标机之后，再跑一遍**整机验收**（45 项，含注册表、系统上限、策略开关、计划任务、改设置落盘、异常输入、网卡判别、板书/冲突接口、BCU 按钮）：

```powershell
powershell -ExecutionPolicy Bypass -File verify.ps1
# 可选：-InstallDir "D:\ClassroomKit" -SkipRestart -ReportPath C:\ck-verify.txt
```

报告默认写 `%TEMP%\ck-verify.txt`，有失败就以退出码 1 结束（便于塞进部署脚本）。

## 文档

- [部署手册](docs/部署手册.md) —— 装、验、还原卡、升级、卸载、排障
- [ClassIsland 接入](docs/ClassIsland-接入.md) —— 三分钟配完，零代码
- [原理与限制](docs/原理与限制.md) —— 8 台是怎么破的、为什么不写服务、还剩什么限制

## 许可证

[MIT](LICENSE) 许可。可以自由用于学校、公司或二创，保留版权声明即可。
