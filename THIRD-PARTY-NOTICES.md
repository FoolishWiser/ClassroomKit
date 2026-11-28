# 第三方组件与许可

ClassroomKit 本体以 MIT 许可开源（见 [LICENSE](LICENSE)）。下面列出随安装包一起分发的第三方资源，以及它们的许可与来源。

## HarmonyOS Sans 字体

- 文件：`HarmonyOS_SansSC_Regular.ttf`、`HarmonyOS_SansSC_Semibold.ttf`
- 用途：桌面界面字体（以 WPF 资源内嵌进程序，只在本程序内使用，不安装到系统）
- 版权：Copyright © 2021 Huawei Device Co., Ltd.
- 许可：HarmonyOS Sans Fonts License Agreement（随包提供全文：安装目录 `licenses/HarmonyOS-Sans-LICENSE.txt`）
- 说明：未对字体文件做任何修改；字体也未单独对外分发，只作为本软件的一部分提供。

许可要点（以许可原文为准）：

1. 允许在软件中嵌入、捆绑并随软件一起再分发字体；
2. 再分发时须保留版权声明与许可文本，并在文档中注明使用了该字体；
3. 不得单独分发字体文件，不得修改字体、不得使用其保留字体名称（Reserved Font Name）。

## 图标字体

- Segoe MDL2 Assets / Segoe Fluent Icons：Windows 系统自带字体，仅通过字体族名引用，不随包分发。
- 应用图标 `Assets/app.ico`、`Assets/app-64.png`：本项目自制。

## 维词自动领读脚本

- 文件：`src/ClassroomKit.Agent/Weici/automation.js`（以资源内嵌进程序，运行时由 CDP 注入维词页面）
- 来源：Teachers' own weici-autoread experimental project, bundled with permission; no third-party open-source code is included.
- 说明：脚本只在本机已登录的维词窗口里操作页面，不收集账号、密码或页面内容；不使用时不注入。

## .NET 运行时与第三方库

单文件安装包内含 .NET 8 运行时与下列 NuGet 依赖，均以各自许可分发（MIT / Apache-2.0）：

- Microsoft.Extensions.*、System.Text.Json（MIT，.NET Foundation）
- Microsoft.Win32.* / Windows API 互操作代码为本项目自有实现

完整依赖清单见仓库中的各个 `*.csproj` 文件；如需某一依赖的许可全文，请在仓库提 issue。
