<div align="center">

# RelaxKonOS

**把你的服务器变成一台桌面 · 云原生桌面环境**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.4-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[English](./README.en.md) · [日本語](./README.ja.md)

官网 <https://relaxkon.com> · 文档 <https://relaxkon.com/docs> · 下载 <https://relaxkon.com/downloads> · 仓库 <https://github.com/nanaminato/RelaxKonOS>

</div>

---

## ✨ 项目简介

**RelaxKonOS** 是跨平台的云原生桌面环境——把服务器当成那台机器。桌面客户端（Avalonia）与 Android 客户端（Kotlin + Compose）在本地渲染界面，服务端（ASP.NET Core，运行于 Linux 与 Windows）提供账户、存储、同步和远程运行能力。两端传输系统状态、应用状态和操作意图，不传输屏幕像素。

### 核心用途

- **让工作负载活过连接**：终端会话、被守护的进程与远程服务持续运行在服务端，网络瞬断不再等于会话丢失。
- **让多台设备共享同一个工作区**：工作站、笔记本、服务器控制台与手机看到的是同一份 Workspace，而不是各自独立的本地状态。
- **把服务器运维收进一个客户端**：Docker、防火墙、证书、Web Server、Git、FRP 隧道与代理管理同处一个 Shell，并且直接复用宿主操作系统的账号与权限。
- **让应用可以扩展**：实现一个接口即可获得与内置应用相同的窗口管理、生命周期与能力 API。

### 适用人群

| 你是 | 建议的入手方式 |
| --- | --- |
| Windows 10/11 个人用户 | 在客户端登录页选择**管理本机**，用**个人模式**安装 Server，当前账户成为 RelaxKonOS 管理员；本机授权后可配对其他设备，见[个人电脑使用指南](./deployment/WindowsPersonalComputer.md) |
| Linux 个人用户 / 自建服务器爱好者 | 用**用户模式**在已有的普通 Linux 账号下装一份服务端（**不需要 sudo**），再在自己的电脑上运行客户端 |
| 运维 / 系统管理员 | 用**系统模式**把 Server、Guardian Agent 与权限助手注册为系统服务，面向多用户生产环境 |
| Android 手机 / 平板用户 | 安装 [Android 客户端](https://relaxkon.com/docs/zh-CN/latest/getting-started/android)连上已有的 Server，处理文件、传输、Docker、站点与隧道；功能细节见 [Android 文档](./Client/RelaxKonOS.Client.Android/docs/README.md) |
| 应用开发者 | 用**开发者模式**与 `DevCli` 把自定义应用打包成 `.roapp` 装进同一个桌面 |
| 只想先看看 | 从[官网下载页](https://relaxkon.com/downloads)取已发布的客户端与服务端 ZIP，或[从源码运行](#从源码运行开发者) |

> 不确定该选哪种？先读[「选择运行模式」](#选择运行模式)——四种方式的职责不同，普通用户请勿照搬开发者或管理员的步骤。

### 核心特性

- 🖥️ **桌面与移动双客户端** — 桌面端基于 Avalonia 模拟 Windows 11 界面；移动端为原生 Kotlin + Jetpack Compose，按手机与平板自适应布局
- 🌐 **云原生架构** — Client/Server 分离；服务端运行于 Linux、Windows 10/11 与 Windows Server，可与客户端同机部署
- 🔐 **宿主 OS 身份集成** — 复用宿主系统用户与权限体系（Windows LogonUser / Linux PAM）
- 🪟 **窗口与应用模型** — 完整的窗口生命周期（创建、移动、缩放、最小化/最大化、Z-Order、模态对话框），应用经 `IRemoteApplication` 接入并复用同一套管理
- 🔌 **SignalR 实时通道** — 终端 PTY、守护日志、性能指标按订阅推送，设备拓扑变化即时可见
- 🛠️ **主机运维套件** — Docker 与 Compose、进程守护、UFW 防火墙、ACME 证书、Nginx 站点、Git、FRP 隧道、Mihomo/Squid/sing-box 代理与 SMB 文件服务
- 🧱 **配置与偏好中心** — 受 schema 约束的 desired/applied 注册表、Workspace 偏好同步、镜像源管理、按用户/应用隔离的私有 KV
- 🌍 **多语言与可扩展** — 内置中、英、日语言包；`DevCli` 负责 `.roapp` 打包、安装与监视重载

---

## 🏗️ 架构概览

```
Client（桌面 Avalonia · Android Kotlin/Compose）
  ├─ 内置应用          Explorer · Terminal · Settings · Server Center · Docker · Proxy …
  ├─ 应用运行时 / SDK  IRemoteApplication · AppContext · 能力接口与私有 KV
  ├─ 窗口管理器        RemoteWindow · 生命周期 · Z-Order · 模态对话框
  └─ Protocol（DTO）   路由 · Hub 契约 · 序列化约定
        │  HTTP REST + SignalR（传输状态与操作，不传屏幕像素）
        ▼
Server（ASP.NET Core · Linux / Windows）
  ├─ 身份与工作区      Auth · Workspace · Session · Device · Alias
  ├─ 数据与配置        Storage · Files · AppSettings · Registry · 镜像源
  ├─ 主机运维          Docker · Guardian · Firewall · Metrics · 独立组件服务
  ├─ 站点与网络        WebServers · Certificates · Git · Tunnels · Squid · Proxy
  ├─ OS 抽象层         IIdentityProvider · IFirewallProvider · ICertificateProvider …
  └─ 持久化            业务库 EF Core + SQLite · HostGlobal v1~v7 迁移
        │
        ▼
Guardian Agent / Privileged Helper（独立进程 · 原生服务 · 特权操作）
```

---

## 🛠️ 技术栈

| 组件 | 技术 | 版本 |
|------|------|------|
| 桌面 UI | [Avalonia UI](https://avaloniaui.net/) | 12.1.4 |
| 桌面 MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| 移动客户端 | Kotlin + Jetpack Compose (Material 3) | compileSdk 36 · minSdk 29 |
| 框架 | .NET | 10.0 |
| 服务端 | ASP.NET Core | 10.0 |
| 实时通信 | SignalR | 10.0 |
| 身份认证 | JWT Bearer | — |
| 持久化 | EF Core + SQLite | 10.0 |
| 终端控件 | RoyalTerminal (Avalonia + PTY) | 0.6.3 |
| 浏览器 | Avalonia.Controls.WebView | 12.1.0 |
| 文件管理 UI | Jaya File Manager (BSD-3 许可) | — |
| 视频播放 | LibVLCSharp.Avalonia | 3.10.1 |

---

## 📁 项目结构

```
RelaxKonOS/
├── Client/
│   ├── RelaxKonOS.Client/          # 桌面 Shell + 内置应用（类库）
│   │   ├── Apps/                   # 内置应用（见下方「内置应用」表）
│   │   │   ├── Explorer/           # 文件管理器
│   │   │   ├── Terminal/           # 终端
│   │   │   ├── Browser/            # 浏览器
│   │   │   ├── Settings/           # 设置中心（系统/账户安全/网络/个性化/应用/时间语言/辅助功能/开发者）
│   │   │   ├── ServerCenter/       # 服务器中心：安装、更新、修复、回退、卸载
│   │   │   ├── TaskManager/        # 任务管理器
│   │   │   ├── Docker/             # Docker 管理器
│   │   │   ├── ApplicationDeployments/ # 容器化应用部署
│   │   │   ├── ProcessGuardian/    # 进程守护
│   │   │   ├── Firewall/           # Linux UFW 防火墙
│   │   │   ├── Proxy/              # 代理管理器（Mihomo 运行时、TUN、订阅、系统代理）
│   │   │   ├── ProxyServer/        # 代理服务端（sing-box）
│   │   │   ├── Squid/              # Squid HTTP 代理网关
│   │   │   ├── FileServices/       # 文件服务（SMB）
│   │   │   ├── PortForwarding/     # SSH 端口转发
│   │   │   ├── Certificates/       # ACME 证书管理
│   │   │   ├── WebServers/         # Web Server 管理器（Nginx 等）
│   │   │   ├── Git/                # Git 客户端
│   │   │   ├── Tunnels/            # FRP 隧道管理
│   │   │   ├── Registry/           # 配置注册表
│   │   │   ├── EventAlerts/        # 事件与告警中心
│   │   │   ├── Notepad/            # 记事本
│   │   │   ├── CodeEditor/         # 代码编辑器
│   │   │   ├── TextEditor/         # 文本编码对话框（Notepad/CodeEditor 共用）
│   │   │   ├── ImageViewer/        # 图片查看器
│   │   │   ├── Welcome/            # 欢迎页
│   │   │   └── AppInstaller/       # 应用安装器
│   │   ├── Localization/           # 多语言资源（en-US / zh-CN / ja-JP）
│   │   ├── Services/               # 认证、权限、开发模式等服务
│   │   ├── ViewModels/             # Shell / Login ViewModel
│   │   └── Views/                  # Shell / Login / MainWindow 视图
│   ├── RelaxKonOS.Client.Desktop/  # 桌面平台入口（WinExe）
│   └── RelaxKonOS.Client.Android/  # Android 客户端（Kotlin + Compose，含自有 docs/）
├── Framework/
│   ├── RelaxKonOS.Core/            # 平台无关原语（几何、窗口、应用模型）
│   ├── RelaxKonOS.Core.Tests/      # 原语层单元测试
│   ├── RelaxKonOS.UI/              # Avalonia 共享主题/样式
│   ├── RelaxKonOS.Shell/           # 桌面 Shell 组装
│   ├── RelaxKonOS.WindowManager/   # 窗口管理器 + RemoteWindow 控件
│   ├── RelaxKonOS.App.SDK/         # 应用开发 API（AppContext / IRemoteApplication）
│   └── RelaxKonOS.Runtime/         # 应用运行时（ApplicationManager）
├── Shared/
│   └── RelaxKonOS.Protocol/        # 通信协议契约（DTO / 路由 / Hub 接口）
├── RelaxKonOS.Server/              # 服务端（ASP.NET Core）
├── RelaxKonOS.Guardian.Agent/      # 进程守护独立进程（原生服务管理）
├── RelaxKonOS.PrivilegedHelper/    # 跨平台特权操作 Helper（Windows 服务 / Linux 守护）
├── Tools/                          # DevCli、多语言校验、图标切片等脚本
├── examples/                       # 示例应用（VideoPlayer / ServerMonitor / HelpCenter）
├── deployment/                     # 部署脚本与文档（Linux / Windows / 客户端分发）
├── docs/                           # 详细设计文档（架构/平台/桌面/应用/代理/服务/测试）
├── Directory.Packages.props        # 中央包管理
└── RelaxKonOS.sln                  # 解决方案文件
```

---

## 🧩 内置应用

状态说明：✅ **可用**（日常可依赖）· 🧪 **MVP**（主流程可用，边界与验收仍在补）· 🚧 **进行中**（能力未覆盖完整）。

| 应用 | 说明 | 状态 |
|------|------|------|
| **Welcome** | 欢迎引导页，验证 Runtime 与 WindowManager | ✅ 可用 |
| **Notepad** | 文本文件编辑（UTF-8/GBK/Shift-JIS 等多编码打开与保存） | ✅ 可用 |
| **Code Editor** | 代码文件编辑（语法高亮、多编码支持） | ✅ 可用 |
| **Image Viewer** | 图片文件浏览（缩放与滚动） | ✅ 可用 |
| **Settings** | 设置中心（8 个分类页：系统/账户安全/网络/个性化/应用/时间语言/辅助功能/开发者；含全页搜索） | ✅ 可用 |
| **Server Center** | 服务器中心：SSH 与主机密钥核对、预检、安装、更新、修复、回退与卸载 | ✅ 可用 |
| **Component Center** | 组件中心：统一查看宿主组件依赖、安装状态、版本与来源，管理已接入组件的安装、更新、卸载及操作恢复 | 🚧 分组件接入中 |
| **Tailscale** | 远端宿主 Tailscale 组网管理：设备与连接状态、浏览器登录授权、DNS/路由偏好、连接与断开、操作恢复；安装维护统一进入组件中心 | 🧪 MVP（Linux 已有实机验证，Windows 待实机验收） |
| **Terminal** | 远端终端（SignalR + PTY 持久会话，VT 引擎可选 Auto/Managed/Native） | ✅ 可用 |
| **Explorer** | 远端文件管理器（REST API + 宿主 OS 权限复用） | ✅ 可用 |
| **Browser** | 内置浏览器（书签/历史、主页与链接打开位置持久化） | ✅ 可用 |
| **Port Forwarding** | 本机 SSH loopback 隧道管理（仅 Client 本地，不参与 Server 同步） | ✅ 可用 |
| **Task Manager** | 远端任务管理器（性能页订阅期间 SignalR 1Hz 推送 + 60s 历史；进程页按需低频采样） | ✅ 可用 |
| **Docker Manager** | 远端 Docker Engine 管理（容器/镜像/Stack/网络/卷 + Compose 编排） | ✅ 可用 |
| **App Installer** | 应用包（`.roapp`）安装与管理 | ✅ 可用 |
| **Firewall** | Linux Server UFW 防火墙状态、默认策略与规则管理 | ✅ 可用 |
| **Process Guardian** | 守护工作负载、IPC、持久化；SignalR `/hubs/guardian-logs` 日志广播 | 🚧 进行中 |
| **Application Deployments** | 容器化应用部署：向导、定义编辑、日志、版本与回滚、模板 | 🧪 MVP |
| **Registry** | 配置注册表（键/值浏览、desired/applied 状态机、服务端持久化） | 🧪 MVP |
| **Certificate Manager** | ACME 证书申请、续期、Kestrel 部署、吊销与删除、自签证书 | 🧪 MVP |
| **Web Server Manager** | Nginx 实例/站点/配置快照/操作流水+审计（宿主级 HostGlobal 持久化） | 🧪 MVP |
| **Git Client** | 远端 Git 仓库登记、分支、提交、拉取冲突解决、推送、历史 Log | 🧪 MVP |
| **Tunnel Manager** | FRP 内网穿透（Server Profile/Definition/Secrets/Audit，Server 端持久化） | 🧪 MVP |
| **Proxy Manager** | 代理管理器（Mihomo 运行时安装/启停升级、TUN 模式、订阅与配置档案、系统代理、流量/连接监控、网络安全与紧急恢复） | 🧪 MVP |
| **Squid Gateway** | Squid HTTP 代理网关（APT 安装、监听与 ACL、HTTP parent、校验发布、服务控制） | 🧪 MVP |
| **Proxy Server** | 内置代理服务端（sing-box 运行时、入站与凭据、连接导出与订阅、发布与回滚） | 🧪 MVP |
| **File Services (SMB)** | SMB 文件服务（安装、共享、权限与凭据、结果核验） | 🧪 MVP |
| **Event Alerts** | 事件与告警中心：服务端事件账本与告警投影，桌面提供摘要与告警列表 | 🚧 进行中 |
| **Text Encoding Dialog** | 文本编码对话框（Notepad / Code Editor 共用，非独立应用） | ✅ 可用 |

---

## 🚀 快速开始

### 选择运行模式

服务端有三种运行模式，安装时选定；开发者另有一种模式。选错会带来权限不足或权限过大，先对照下表：

| 模式 | 适用场景 | 权限要求 | 服务形态 |
| --- | --- | --- | --- |
| **个人模式** | Windows 10/11 个人电脑自用 | 安装与维护时按需 UAC | 当前用户登录后启动、注销后停止，不需要 SSH |
| **系统模式** | 多用户主机、Windows Server | root / sudo 或已提升管理员 | Server、Guardian Agent 与权限助手注册为系统服务 |
| **用户模式** | 已有普通 Linux 账号，不想改动系统 | 普通账号，**不需要 sudo** | 用户级进程，默认仅本机监听，可配 SSH 隧道 |
| **开发者模式** | 自己编写 `.roapp` 应用 | 跟随已登录会话 | 不涉及服务端安装，见下方「开发模式与扩展」 |

### 安装服务端

1. 从[官网下载页](https://relaxkon.com/downloads)取得适合自己设备的客户端，打开**服务器中心**；Windows 10/11 本机也可在登录页选择**管理本机**。
2. 添加目标主机的 SSH 地址、端口与账号，核对主机密钥，完成环境检查。
3. 选择官方稳定版、本地 ZIP、服务器上的 ZIP 或自定义 HTTPS 下载；系统模式与用户模式共用对应平台和架构的 `*-server.zip`，安装时选择运行模式。
4. 选择系统模式或用户模式（Windows 10/11 本机还可选个人模式）。系统模式需要 root／sudo 或已提升管理员权限；Linux 用户模式使用普通账号，不需要 sudo。
5. 配置端口、程序与数据目录、网络、TLS 和文件访问范围。Linux 系统模式还可分别设置管理员与 root 范围、Docker 授权和非标准系统选项；用户模式可设置数据、配置、状态和缓存目录。
6. 审阅并确认安装，等待操作回执和健康检查，再返回客户端登录。

### 更新与维护

安装完成后，同一台主机的维护都在**服务器中心**进行；客户端与服务端分别更新，升级客户端不会顺带升级服务端：

| 目标 | 选择的操作 | 需要留意 |
| --- | --- | --- |
| 换用新的服务端包 | 更新 / 升级 | 沿用原安装的目录与配置，普通更新保留 TLS 身份 |
| 修复当前安装或组件 | 修复 | 不等于选择新版本；普通修复保留数据与现有证书 |
| 恢复记录中的前一版本 | 回退 | 只恢复程序，不恢复数据或数据库结构 |
| 移除服务端 | 卸载（默认保留数据） | 永久删除受管数据需要单独确认 |

维护前先备份，并留一条可用的 SSH 或本机管理通道；服务重启期间客户端断开不等于操作失败，重新连接后核对回执、版本与健康状态。完整流程见[安装指南](https://relaxkon.com/docs/zh-CN/latest/getting-started/installation)、[更新、卸载与维护](https://relaxkon.com/docs/zh-CN/latest/apps/server-maintenance)及[用户模式指南](https://relaxkon.com/docs/zh-CN/latest/getting-started/user-mode)。

维护者的部署引擎、参数对应关系、打包和手动诊断见 [`deployment/README.md`](./deployment/README.md)；客户端分发见 [`deployment/ClientDistribution.md`](./deployment/ClientDistribution.md)。

### 从源码运行（开发者）

#### 前置要求

- **.NET 10.0 SDK** 或更高版本
- **操作系统**：Windows 10/11、Windows Server 2016+、Ubuntu 20.04+
- （可选）Visual Studio 2022+ 或 JetBrains Rider

#### 1. 克隆仓库

```bash
git clone https://github.com/nanaminato/RelaxKonOS.git
cd RelaxKonOS
```

#### 2. 启动服务端

```bash
cd RelaxKonOS.Server

# 开发模式运行（默认监听 http://localhost:5000）
dotnet run
```

> ⚠️ **生产环境**：请务必修改 `appsettings.json` 中的 `Jwt:Secret`（至少 32 字符随机字符串）。

#### 3. 启动客户端

```bash
cd Client/RelaxKonOS.Client.Desktop
dotnet run
```

客户端会弹出登录窗口，输入宿主系统的用户名和密码即可登录。

---

## 🔗 官方链接

| 用途 | 地址 |
| --- | --- |
| 产品官网 | <https://relaxkon.com> |
| 文档中心 | <https://relaxkon.com/docs> |
| 下载页（稳定版安装命令与校验和） | <https://relaxkon.com/downloads> |
| 发行说明 | <https://relaxkon.com/releases> |
| 源码仓库 | <https://github.com/nanaminato/RelaxKonOS> |
| 问题反馈 / Issue | <https://github.com/nanaminato/RelaxKonOS/issues> |

---

## 📖 详细文档

### 架构与核心模型

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Architecture.md](./docs/architecture/RelaxKonOS.Architecture.md) | 架构设计原则、模块依赖、分层架构 |
| [RelaxKonOS.Protocol.md](./docs/architecture/RelaxKonOS.Protocol.md) | 通信协议契约、REST/SignalR、序列化约定 |
| [RelaxKonOS.Workspace.md](./docs/architecture/RelaxKonOS.Workspace.md) | 用户/工作区/会话/设备、多设备模型 |
| [RelaxKonOS.Registry.md](./docs/architecture/RelaxKonOS.Registry.md) | 配置注册表架构、desired/applied 状态机 |
| [RelaxKonOS.ApplicationActivation.md](./docs/architecture/RelaxKonOS.ApplicationActivation.md) | 应用启动 URI 与窗口实例策略 |
| [RelaxKonOS.FileUpload.Design.md](./docs/architecture/RelaxKonOS.FileUpload.Design.md) | 文件上传通道、分块与断点续传契约 |

### 平台服务

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Authentication.md](./docs/platform/RelaxKonOS.Authentication.md) | 登录系统、身份模型、OS 用户集成 |
| [RelaxKonOS.Authentication.Hardening.md](./docs/platform/RelaxKonOS.Authentication.Hardening.md) | 认证限流、风险控制与登录防护建议 |
| [RelaxKonOS.Login.md](./docs/platform/RelaxKonOS.Login.md) | 登录模块实现细节、mstsc 风格登录窗 |
| [RelaxKonOS.Security.md](./docs/platform/RelaxKonOS.Security.md) | 安全设计、权限提升、危险操作 |
| [RelaxKonOS.AppPermissions.v2.md](./docs/platform/RelaxKonOS.AppPermissions.v2.md) | 应用权限模型、授权范围与判定规则 |
| [RelaxKonOS.InstallationManagement.Design.md](./docs/platform/RelaxKonOS.InstallationManagement.Design.md) | 安装组件、安装设置、修复与状态区分 |
| [RelaxKonOS.ManagedRuntimes.Design.md](./docs/platform/RelaxKonOS.ManagedRuntimes.Design.md) | 受管软件运行时的安装、版本、更新与回滚规则 |
| [RelaxKonOS.PrivilegedOperations.Goal.md](./docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md) | 跨平台受限 Helper、Windows Server 支持与特权操作迁移 |
| [RelaxKonOS.ServerCenter.Goal.md](./docs/platform/RelaxKonOS.ServerCenter.Goal.md) | 服务器中心、受信任 SSH 部署与生命周期验收 |
| [RelaxKonOS.UserModeServer.Goal.md](./docs/services/RelaxKonOS.UserModeServer.Goal.md) | 无 sudo 的 Linux 用户模式服务端、SSH 隧道与能力边界 |
| [RelaxKonOS.Storage.md](./docs/platform/RelaxKonOS.Storage.md) | 服务端持久化、EF Core + SQLite |

### 桌面体验

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Desktop.md](./docs/desktop/RelaxKonOS.Desktop.md) | 桌面外壳、窗口控制、模态对话框、键盘路由 |
| [RelaxKonOS.Settings.md](./docs/desktop/RelaxKonOS.Settings.md) | 设置中心、偏好持久化、多设备同步 |
| [RelaxKonOS.Theming.md](./docs/desktop/RelaxKonOS.Theming.md) | 颜色契约：模式、调色板与强调色 |
| [RelaxKonOS.SystemStyle.md](./docs/desktop/RelaxKonOS.SystemStyle.md) | 系统风格：形状令牌、recipe 与三套内置 profile |
| [RelaxKonOS.Localization.md](./docs/desktop/RelaxKonOS.Localization.md) | 多语言机制、语言包结构 |

### 内置应用

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Terminal.md](./docs/applications/RelaxKonOS.Terminal.md) | 终端应用、SignalR、PTY、持久会话管理 |
| [RelaxKonOS.Explorer.md](./docs/applications/RelaxKonOS.Explorer.md) | 文件管理器、REST API、权限复用 |
| [RelaxKonOS.Browser.md](./docs/applications/RelaxKonOS.Browser.md) | 浏览器、书签/历史/偏好同步 |
| [RelaxKonOS.PortForwarding.md](./docs/applications/RelaxKonOS.PortForwarding.md) | SSH 端口转发、本机 loopback 隧道 |
| [RelaxKonOS.TaskManager.md](./docs/applications/RelaxKonOS.TaskManager.md) | 任务管理器、系统指标、进程管理、SignalR 推送重写 |
| [RelaxKonOS.ComponentCenter.Design.md](./docs/platform/RelaxKonOS.ComponentCenter.Design.md) | 组件中心、逐组件接入与平台验收边界 |
| [RelaxKonOS.ComponentCenter.Progress.md](./docs/platform/RelaxKonOS.ComponentCenter.Progress.md) | 组件中心、逐组件接入与平台验收边界 |
| [RelaxKonOS.Tailscale.Design.md](./docs/applications/RelaxKonOS.Tailscale.Design.md) | Tailscale 设备、登录、连接控制与生命周期实施状态 |
| [RelaxKonOS.DockerManager.md](./docs/applications/RelaxKonOS.DockerManager.md) | Docker 管理器、容器/镜像/Stack/网络/卷 |
| [RelaxKonOS.ApplicationDeployment.Design.md](./docs/applications/RelaxKonOS.ApplicationDeployment.Design.md) | 容器化应用部署、模板契约、状态机与恢复语义 |
| [RelaxKonOS.Firewall.md](./docs/applications/RelaxKonOS.Firewall.md) | Linux Server UFW 防火墙应用 |
| [RelaxKonOS.ProcessGuardian.md](./docs/applications/RelaxKonOS.ProcessGuardian.md) | 进程守护、健康检查、原生服务管理、日志 Hub |
| [RelaxKonOS.CertificateManager.md](./docs/applications/RelaxKonOS.CertificateManager.md) | ACME 证书生命周期、Kestrel 部署、续期、HostGlobal 持久化 |
| [RelaxKonOS.EventAlertCenter.md](./docs/applications/RelaxKonOS.EventAlertCenter.md) | 事件与告警中心：事件账本、告警投影与验证边界 |
| [RelaxKonOS.WebServerManager.Design.md](./docs/applications/RelaxKonOS.WebServerManager.Design.md) | Web Server 管理、Nginx 集成、站点/快照/审计 |
| [RelaxKonOS.GitClient.md](./docs/applications/RelaxKonOS.GitClient.md) | Git 客户端、仓库/分支/提交/冲突/历史 |
| [RelaxKonOS.FRP_Integration.Design.md](./docs/applications/RelaxKonOS.FRP_Integration.Design.md) | FRP 内网穿透架构、安全与运维边界 |
| [RelaxKonOS.ProxyManager.Design.md](./docs/applications/RelaxKonOS.ProxyManager.Design.md) | 代理管理器、Mihomo 运行时、TUN、订阅与配置档案 |
| [RelaxKonOS.ProxyServer.Design.md](./docs/applications/RelaxKonOS.ProxyServer.Design.md) | 代理服务端设计：sing-box、入站、访问凭据、连接导出与订阅 |
| [RelaxKonOS.RegistryApp.md](./docs/applications/RelaxKonOS.RegistryApp.md) | 配置注册表浏览、写入与隔离边界 |
| [RelaxKonOS.CodeEditor.md](./docs/applications/RelaxKonOS.CodeEditor.md) | 代码编辑器、语法高亮、文件安全边界 |
| [RelaxKonOS.NetworkInspector.md](./docs/applications/RelaxKonOS.NetworkInspector.md) | 网络检查器、诊断工具、网络分析 |

### 代理（Proxy）

| 文档 | 说明 |
|------|------|
| [architecture.md](./docs/proxy/architecture.md) | 代理模块架构、引擎抽象（IProxyEngine）、Mihomo 集成 |
| [squid.md](./docs/proxy/squid.md) | Squid HTTP 代理网关：后端、API、安全边界、恢复和后续计划 |
| [installation.md](./docs/proxy/installation.md) | 代理运行时安装、部署与升级 |
| [mihomo.md](./docs/proxy/mihomo.md) | Mihomo 引擎配置、控制面与运行时管理 |
| [tun.md](./docs/proxy/tun.md) | TUN 模式、网络栈与透明代理 |
| [recovery.md](./docs/proxy/recovery.md) | 代理故障恢复、网络安全与紧急禁用 |
| [security.md](./docs/proxy/security.md) | 代理安全模型、权限边界与审计 |
| [troubleshooting.md](./docs/proxy/troubleshooting.md) | 代理排障指南与常见问题 |

### 移动客户端（Android）

| 文档 | 说明 |
|------|------|
| [docs/mobile](./docs/mobile/README.md) | 移动端入口，指向 Android 自有文档目录 |
| [Android 文档总览](./Client/RelaxKonOS.Client.Android/docs/README.md) | 设计规范、当前功能、构建发布与验证要求 |
| [支持范围](./Client/RelaxKonOS.Client.Android/docs/design/SupportScope.md) | 应用组织、部署范围、平台限制与验证要求 |
| [构建与发布](./Client/RelaxKonOS.Client.Android/docs/development/android-release.md) | Android 构建、签名与发布流程 |

### 开发与扩展

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Develop.md](./docs/development/RelaxKonOS.Develop.md) | 开发者快速上手、代码结构、调试指南 |
| [RelaxKonOS.DeveloperMode.md](./docs/development/RelaxKonOS.DeveloperMode.md) | 开发模式、DevCli、应用包发布 |
| [RelaxKonOS.AppSettings.md](./docs/development/RelaxKonOS.AppSettings.md) | 应用私有配置存储 |
| [RelaxKonOS.BuiltInApplication.Conventions.md](./docs/development/RelaxKonOS.BuiltInApplication.Conventions.md) | 内置应用设计约束、国际化、跨平台 |
| [RelaxKonOS.ApplicationCompatibility.md](./docs/development/RelaxKonOS.ApplicationCompatibility.md) | 应用兼容性、平台适配、降级策略 |
| [RelaxKonOS.LocalDebugging.md](./docs/development/RelaxKonOS.LocalDebugging.md) | 本地调试环境与常见问题 |

### 项目文档索引

| 文档 | 说明 |
|------|------|
| [docs/README.md](./docs/README.md) | 项目结构、代码地图、当前进度 |
| [docs/services](./docs/services) | 用户模式服务端、安装服务与独立组件服务的进度记录 |
| [docs/testing](./docs/testing) | 验收用例与已跳过测试的说明 |

---

## 🔧 开发模式与扩展

RelaxKonOS 支持开发者构建自定义应用包（`.roapp`），通过 `DevCli` 工具安装到 RelaxKonOS Shell 中。

### 构建、安装与监视示例应用

```bash
# 设置开发令牌（或通过参数传递）
export RELAXKONOS_DEV_TOKEN="<pairing-token>"

# 打包并安装应用；无需为每个应用维护 PowerShell 脚本
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# 监听源码，自动重新打包并更新
dotnet run --project Tools/RelaxKonOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` 在应用目录的 `artifacts/` 下生成 `.roapp`；纯托管应用可省略 `--runtime`。完整的第三方应用打包命令请参阅 [Developer Mode](./docs/development/RelaxKonOS.DeveloperMode.md)。

Windows PowerShell 中设置令牌时，使用 `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"`；其余 `dotnet` 命令保持不变。

### 应用开发模型

```csharp
// 实现 IRemoteApplication 接口或继承 RemoteApplicationBase
public class MyApp : RemoteApplicationBase
{
    public override string Id => "com.example.myapp";
    public override string DisplayName => "My Application";

    public override void Activate(AppContext context)
    {
        // 创建窗口
        context.ShowWindow("My Window", contentFactory: () => new MyView());
    }
}
```

---

## 🌍 多语言

RelaxKonOS 内置三种语言支持：

| 语言 | 代码 | 语言包路径 |
|------|------|-----------|
| 🇨🇳 简体中文 | `zh-CN` | `Client/RelaxKonOS.Client/Localization/zh-CN/` |
| 🇺🇸 English | `en-US` | `Client/RelaxKonOS.Client/Localization/en-US/` |
| 🇯🇵 日本語 | `ja-JP` | `Client/RelaxKonOS.Client/Localization/ja-JP/` |

语言包采用 JSON 格式，键值对结构。切换语言后 UI 实时更新。

---

## ⚠️ 第三方声明

本项目使用了以下第三方资源：

- **Jaya File Manager** (BSD 3-Clause License) — 文件管理器 UI 结构移植。详见 [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md)。
- 所有 NuGet 包的许可证信息请参考各自的包页面。

---

## 📄 许可证

本项目采用 **RelaxKonOS Non-Commercial Source-Available License** 许可。

**允许**：免费使用、修改、开发、学习、非商业目的分发。
**禁止**：商业售卖、转售、SaaS 托管或其他商业用途。

作者保留所有商业化权利。如需商业许可，请直接联系作者。

详见 [`LICENSE`](./LICENSE) 文件。第三方组件许可见 [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md)。

---

## 🤝 贡献

源码、Issue 与 Pull Request 都在同一个仓库：<https://github.com/nanaminato/RelaxKonOS>。欢迎贡献代码！请：

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/amazing-feature`)
3. 提交更改 (`git commit -m 'Add: amazing feature'`)
4. 推送到分支 (`git push origin feature/amazing-feature`)
5. 创建 Pull Request

---

<div align="center">

**RelaxKonOS** — 让桌面跨越设备，让状态定义体验。

</div>
