# ClassroomKit 0.3.0

0.3 的第一个正式版（开发期的 `0.3.0-pre.1`…`pre.4` 已全部并入本版，不再单独发布）。从这版起只发正式版。

## 改：板书冲突提示窗重做（界面）

- 无边框圆角卡片（Win11 走 DWM 原生圆角，Win10 用 Region 兜底），跟随系统深/浅色，按 `DeviceDpi` 缩放（0.75×–3×），高分屏不再糊成一团。
- 每个选项一整块卡片：应用真实图标（从正在运行的希沃 / ClassInX 进程里抠，抠不到用首字母色块）+ 标题 + 一句后果 + 右侧数字徽标；鼠标悬停高亮、整块可点。
- 底部渐变进度条 + 秒数走倒计时；**鼠标停在窗口上自动暂停**，离开继续（低头找鼠标时不会被自动选掉）。
- 键盘：<kbd>Esc</kbd>/<kbd>Enter</kbd> = 都保留，<kbd>1</kbd>–<kbd>4</kbd> = 四个选项，<kbd>S</kbd> = 切「今天不再提醒」；右上角 ✕ 等同都保留。
- 新增「今天不再提醒」勾选框。

## 新：暂停与恢复

- 面板新增「板书冲突提示」卡片：显示监听中 / 暂停中 / 已关闭与冷却分钟数，可「预览提示窗」「恢复提醒」。
- 勾了「今天不再提醒」写入 `conflict.snoozeDate`（`yyyy-MM-dd`）：当天不再弹，第二天自动恢复；写坏的日期在加载配置时自动清空。
- API：`GET /api/state` 的 `conflict` 增加 `snoozeDate` / `snoozeText`；新 action `conflict-snooze`（`snooze=1` 暂停当天，`clear=1` 提前恢复）。
- `--conflict-preview` 的 JSON 输出增加 `label` 与 `snoozeToday`。

## 测

- 新增 8 项单测：倒计时与超时默认「都保留」、悬停暂停、只认第一次选择、快捷键映射、卡片文案、日期解析、暂停期间不弹、跨天自动恢复；总数 85 → 93。

## 历史：0.3.0-pre.3

## 新：全程静默升级（不弹 UAC）

- 下载完把 MSI 投给 SYSTEM 护航任务 `ClassroomKit-Service`，由它以 `msiexec /qn /norestart` 安装；护航任务先停掉用户态守护（否则替换不了 exe），装完再把新版本拉回用户会话。
- 面板「下载并升级」不再弹 UAC、不再出现安装窗口；没装护航任务时才退回管理员授权安装。
- 排队的代价：护航任务每 5 分钟醒一次（原来是 15 分钟），点升级后最多等 5 分钟；点「立即执行」那一次会马上跑。

## 新：开机自检与自动安装

- 启动 90 秒后自检一次（`update.checkOnBoot`），之后每 `update.checkIntervalHours` 小时查一次（默认 6，0 = 只手动）。
- `update.autoInstall` 默认关闭；打开后只在**安装时间窗**（`update.installWindow`，默认 `02:00-05:00`，可跨午夜、留空不限）内、且**没有设备连着热点**（`update.installWithClients`）时才自己装 —— 上课中不会断学生的网。
- 面板「升级」卡片新增这四个开关/输入框与状态提示；API 新增 action `update-config`。

## 改：升级状态

- `/api/update` 增加 `silent`、`checkOnBoot`、`autoInstall`、`installWithClients`、`installWindow`、`installWindowText`、`checkIntervalHours`、`serviceInstalled`；静默安装时状态文案是「正在后台静默安装（无需操作）…」。

## 历史：0.3.0-pre.2

## 新：GitHub 访问加速（hosts 护航）

- 守护每分钟看一眼 `api.github.com/zen`，**只在直连不通时**才改 hosts；写完立即校验，还是不通就自动还原。
- 从公开 hosts 源取候选，按 443 端口实测时延挑最快的 IP；区块置顶、保留原文件内容与 CRLF/LF，写前备份到 `hosts.classroomkit-original`。
- 写 hosts 需要管理员：普通权限下投递给 **SYSTEM 护航任务** `ClassroomKit-Service`（开机 1 分钟后、每 15 分钟醒一次 —— pre.3 起改为每 5 分钟，动作 `--service-tick`），守护最多等 25 秒。
- 面板新增「GitHub 访问加速」卡片；命令行 `--hosts-status` / `--hosts-refresh` / `--hosts-restore` / `--install-service`；API `GET /api/hosts` 与两个 action。
- 详见 [GitHub-加速.md](GitHub-加速.md)。

## 改：卸载与安装

- MSI 安装时一并注册护航任务，卸载时一并删除；绿色版 `install.ps1` 同样注册。
- 配置结构版本升到 2：旧 `config.json` 自动补 `hosts` 段（只盖章，不改任何已有值）。

## 历史：0.3.0-pre.1

第一版预发布（开发期用 `-pre.N` 标记，现已并入 0.3.0）。内核照旧独立，ClassIsland 这一层从「零代码自动化」升级成**真正的插件**。

## 新：ClassIsland 2.1 插件（`.cipx`）

- 设置页「ClassroomKit 热点」：状态、SSID、密码、已连设备数、上联网卡、守护版本；一键启动/关闭/修复连接上限/打开控制面板。
- 主界面组件：一行显示 `热点 ON · SSID · 3/10`。
- 自动化行动：「开启教室热点」「关闭教室热点」，上下课自动开关。
- 插件只是薄壳，通过 `127.0.0.1:36037` 指挥独立守护进程 —— ClassIsland 崩了、晚启动、升级中，热点都不受影响。1.6/1.7 的零代码接法继续可用。

## 新：默认值按机器生成（防跨教室漫游）

- 全新安装的热点名默认 = **这台电脑的计算机名**；密码 = 每机随机 8 位。
- 老机器升级**不动**已有 SSID/密码；面板会出现提示条「热点名还是旧的默认值」，点一下即可改成电脑名。
- 热点名超过 32 字节（约 10 个汉字）会按 UTF-8 安全截断，不会切坏半个汉字。

## 改：版本与构建

- 版本号收敛到 `Directory.Build.props` 单一来源；插件 manifest 版本由 CI 同步并校验。
- 升级比较改成 SemVer 预发布规则（`pre.2` < `pre.10`、正式版 > 预发布、忽略 `+build`）。
- CI 一次产出 MSI / zip / cipx 三个资产；正式版不带 `-`，Release 都是正式版。

# ClassroomKit 0.2.10

## 修：保存面板位置跟着鼠标走，先挪指针再按 Ctrl+S

ClassInX 的「保存和分享板书」对话框和「保存至本地」面板，位置取决于**按下 Ctrl+S 那一刻鼠标在哪**。老师是从屏幕右下角托盘点的保存，指针还停在底部，于是两条窗口一路贴到任务栏，勾选框和「保存」按钮被挡住/点不到。

- 现在按 Ctrl+S 之前，先把虚拟鼠标指针移到屏幕上方中间（x 居中、y 约 1/6 屏高），停 180ms 再按。
- 对话框底边仍超出屏幕时，会再把对话框往上拖一段，面板跟着上移。
- 点「保存至本地」图标前指针也先回到对话框上方，面板尽量往上弹。
- 保存过程中不弹托盘气泡，结束才给结果提示。

## 升级方式

面板「升级 → 检查更新 → 下载并升级」，或托盘「检查更新…」，或直接安装 `ClassroomKit-0.2.10-x64.msi`。升级走静默安装（`msiexec /qn`）+ 分段并发下载，只剩一次管理员授权。
