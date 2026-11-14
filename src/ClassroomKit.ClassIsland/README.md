# ClassroomKit 助手（ClassIsland 插件）

在 ClassIsland 里查看教室热点与课表联动状态，并把课表推给独立的 ClassroomKit 守护进程。
热点、板书、升级这些逻辑都在守护进程里，ClassIsland 退出或崩溃都不影响上课上网。

## 功能

- **主界面组件**：显示热点开关、SSID、连着几台设备，以及「下一节 数学 · 3 分钟后」。
- **设置页**：热点状态、开关按钮、改热点名、课表联动状态、课前提醒参数、保存板书按钮。
- **自动化行动**：「开启教室热点」「关闭教室热点」「保存板书」，可放进 ClassIsland 的自动化流程。
- **课表桥**：每秒读一次 ClassIsland 课表，变化时推给守护；课前提醒与板书命名都由守护执行。

## 安装

1. 从 Releases 下载 `ClassroomKit-<版本>.cipx`。
2. ClassIsland → 设置 → 插件 → 从本地安装，选择该文件。
3. 重启 ClassIsland，打开 **设置 → ClassroomKit 助手**。
4. 先确认 ClassroomKit 守护进程已安装并在运行（面板 http://127.0.0.1:36037/）。

## 令牌

令牌一般留空即可：插件会自动读取 `%ProgramData%\ClassroomKit\config.json` 里的令牌。
设置页顶部会显示当前令牌来源；显示「空」时课表不会推送 —— 这时在设置页填一次令牌
（守护面板地址里 `?t=` 后面那串），或确认守护装在了默认位置。

## 排错

| 现象 | 处理 |
| --- | --- |
| 设置页显示「ClassroomKit 没在运行」 | 从开始菜单启动守护，或运行 `ClassroomKit.Agent.exe --ensure` |
| 课表一直「未接入」 | 看设置页的令牌来源；再看 `data/Config/Plugins/classroomkit.hotspot/lesson-bridge.log` 里的 401 |
| 热点名没变 | 改完等一轮巡检（约 15 秒），已连接的设备需要重连 |
| 概览空白 | 本文件就是概览内容；若缺失说明安装包不完整，重新安装 .cipx |

## 版本适配

- ClassIsland 2.1.x：用本插件。
- ClassIsland 1.6–1.7：不用插件，按仓库 docs/ClassIsland-接入.md 的零代码接法用 `--ensure` 自动化。
