# GitHub 访问加速（hosts 护航）

学校里 GitHub 经常时通时不通（DNS 污染、出口抖动）。这个功能在**直连不通时**往系统 hosts 里写一小段 GitHub 域名的 IP，写完立刻校验；还是不通就自己把这段删掉。

它解决的问题很具体：检查更新、下载安装包、GitHub API 都走这几个域名，一旦解析到被污染的地址，升级就会失败。

## 它做什么

1. 每 60 秒（开机那次延后 1 分钟）先试一次 `https://api.github.com/zen`；
2. **能通就什么都不做** —— 不写 hosts、不改 DNS，正常网络下这个功能等于不存在；
3. 不通才去拉公开 hosts 源（依次尝试，第一个成功的就用）：
   - `https://raw.hellogithub.com/hosts`
   - `https://cdn.jsdelivr.net/gh/521xueweihan/GitHub520@main/hosts`
   - `https://ghproxy.net/https://raw.githubusercontent.com/521xueweihan/GitHub520/main/hosts`
4. 对每个域名的候选 IP 做 443 端口实测（1.2 秒超时、每域名最多 4 个候选、16 路并发），挑**时延最低**的；一个都不通就按源的顺序。
5. 把选中的记录写进 hosts 的受管区块（**置顶**，Windows 按顺序匹配，置顶才不会被旧记录抢走），保留文件原有内容和换行风格（CRLF/LF），写前备份一次。
6. `ipconfig /flushdns` → 再校验一次 `api.github.com/zen`；**仍然不通就自动还原 hosts**，并在状态里写明原因。

覆盖的域名：`github.com`、`api.github.com`、`codeload.github.com`、`raw.githubusercontent.com`、`objects.githubusercontent.com`、`github.githubassets.com`、`avatars.githubusercontent.com`、`camo.githubusercontent.com`、`gist.github.com`、`gist.githubusercontent.com`、`user-images.githubusercontent.com`、`release-assets.githubusercontent.com`。

## 受管区块长什么样

```text
# >>> ClassroomKit hosts accelerator (auto-generated) >>>
# Managed by ClassroomKit. Remove with: restore hosts.
140.82.113.4 github.com api.github.com
185.199.108.133 raw.githubusercontent.com
# <<< ClassroomKit hosts accelerator <<<
```

标记行故意全是 ASCII：hosts 在中文 Windows 上是 ANSI(936)，写中文注释会变乱码。原文件已被备份到
`C:\Windows\System32\drivers\etc\hosts.classroomkit-original`（只备份一次，不会覆盖第一次的原件）。

## 谁来写 hosts

写 `hosts` 需要管理员。程序有两条路：

| 场景 | 谁写 |
| --- | --- |
| 守护本身以管理员运行 | 自己写，立即生效 |
| 守护是普通权限（正常情况） | 投递请求给 **SYSTEM 护航任务** `ClassroomKit-Service`，由它写；守护最多等 25 秒 |

护航任务是安装时注册的计划任务：`SYSTEM` 身份、开机 1 分钟后启动、之后**每 15 分钟**醒一次、每次最长跑 10 分钟，动作是 `ClassroomKit.Agent.exe --service-tick`。
守护和它之间用文件信箱通信（`C:\ProgramData\ClassroomKit\service\hosts.request.json` / `hosts.status.json`），不需要常驻高权限进程，也不需要开端口。

绿色版没经过 MSI 时，用**管理员**跑一次：

```powershell
ClassroomKit.Agent.exe --install-service
```

## 面板与命令行

面板「GitHub 访问加速」卡片：状态（已生效 / 未修改 / 已关闭）、记录条数、来源、最近成功时间，两个按钮「刷新 hosts」「还原 hosts」。

```text
ClassroomKit.Agent.exe --hosts-status    打印状态 JSON
ClassroomKit.Agent.exe --hosts-refresh   立即刷新（默认强制；加 --check 只在直连不通时才动）
ClassroomKit.Agent.exe --hosts-restore   删除受管区块
```

HTTP：`GET /api/hosts`；`POST /api/action {"action":"hosts-refresh"}` / `{"action":"hosts-restore"}`。

## 配置

```json
"hosts": {
  "enabled": true,
  "refreshHours": 24,
  "sources": ["https://raw.hellogithub.com/hosts"]
}
```

- `enabled`：关掉后守护完全不碰 hosts（手动 `--hosts-refresh` 仍可用）；
- `sources`：留空会用内置三条源；
- 源全部失败时，会用 48 小时内成功过的缓存（避免断网时反复失败）。

## 排障

| 现象 | 原因 / 处理 |
| --- | --- |
| 状态一直是「未修改」 | 说明直连是通的，不需要加速；或护航任务没注册 |
| 「护航任务未注册」 | 管理员运行 `--install-service`（或重装 MSI） |
| 「护航服务没有响应」 | 任务没跑起来：`schtasks /Query /TN ClassroomKit-Service`，或 `schtasks /Run /TN ClassroomKit-Service` |
| 写入后仍连不上，日志里有「已自动还原」 | 该网络对 GitHub 是整体阻断（不只是 DNS），hosts 帮不上；用镜像或代理 |
| 安全软件把 hosts 改回去了 | 加白名单；程序下次巡检会再写，但每次都触发告警的话建议关掉本功能 |
| 装了代理（Clash 等） | 代理通常自己接管 DNS，**不要**开 hosts 加速，避免把流量指到错误 IP |
| 想彻底清干净 | `--hosts-restore`，或用备份 `hosts.classroomkit-original` 覆盖回 hosts |
