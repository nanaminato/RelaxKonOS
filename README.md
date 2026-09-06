<div align="center">

# RemoteOS

**云原生桌面操作系统环境**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[English](./README.en.md) · [日本語](./README.ja.md)

</div>

---

## ✨ 项目简介

**RemoteOS** 是一个跨平台的云原生桌面操作系统环境，采用 **状态同步（State-Sync）** 模式而非像素流（Pixel Streaming）模式。客户端在本地渲染 UI，服务端提供云端能力（账户、存储、同步、远程运行时），让用户在任何设备上获得一致的桌面体验。

**RemoteOS 不是** 远程桌面工具（RDP/VNC/Screen Streaming）。它传输的是系统状态、应用状态和用户操作意图，而非屏幕像素。

### 核心特性

- 🖥️ **跨平台桌面 Shell** — 基于 Avalonia，模拟 Windows 11 风格界面
- 🌐 **云原生架构** — Client/Server 分离，服务端运行于 Linux 和 Windows Server
- 🔐 **宿主 OS 身份集成** — 复用宿主系统用户与权限体系（Windows LogonUser / Linux PAM）
- 🪟 **窗口管理系统** — 完整的窗口生命周期：创建、移动、缩放、最小化/最大化、Z-Order、模态对话框
- 🧩 **应用 SDK** — 应用通过 `IRemoteApplication` 接口接入，享受统一的窗口管理与生命周期
- 🔌 **SignalR 实时通信** — 终端等应用通过 SignalR Hub 实现实时双向交互
- 🐳 **Docker 管理** — 远端 Docker Engine 检测、容器/镜像/Stack/网络/卷管理
- 🛡️ **进程守护** — 受守护工作负载、健康检查、自动恢复、原生服务管理 + 守护日志 SignalR 广播
- 🔒 **证书管理** — ACME 证书申请、续期、吊销、Kestrel 部署；宿主级资源走版本化迁移持久化
- 🌐 **Web Server 管理** — Nginx 发现、站点、配置快照与最小侵入集成
- 🧾 **Git 客户端** — 远端宿主机 Git 仓库、分支、提交、拉取冲突解决、推送与历史
- 🚇 **FRP 隧道管理** — 内网穿透 Server Profile / 隧道定义 / 密钥与审计
- 🧱 **配置注册表** — 受 schema 约束的 desired/applied 状态机配置中心
- 🪞 **镜像源管理** — APT/Docker/NPM/PyPI 等镜像源随 Workspace 偏好同步
- 🔧 **应用能力与私有 KV** — `/api/v1/capabilities` + App Settings 按用户/应用隔离 KV
- 🌍 **多语言支持** — 内置中文、英文、日文语言包
- 🔧 **开发者扩展** — 支持通过 `DevCli` 工具安装和管理自定义应用包

---

## 🏗️ 架构概览

```
┌─────────────────────────────────────────────────────────┐
│                    RemoteOS.Client                       │
│          (Avalonia Desktop Shell · 本地渲染)             │
│                                                         │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐  │
│  │ Explorer │ │ Terminal │ │ Browser  │ │    ...    │  │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘  │
│       │             │            │              │       │
│  ┌────┴─────────────┴────────────┴──────────────┴────┐  │
│  │              Application Runtime / SDK              │  │
│  └──────────────────────────┬────────────────────────┘  │
│                              │                           │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │              Window Manager (RemoteWindow)          │  │
│  └──────────────────────────┬────────────────────────┘  │
│                             │                            │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │                    Protocol (DTOs)                   │  │
│  └──────────────────────────┬────────────────────────┘  │
└──────────────────────────────┼──────────────────────────┘
                               │ HTTP REST / SignalR
                               ▼
┌─────────────────────────────────────────────────────────┐
│                   RemoteOS.Server                        │
│            (ASP.NET Core · 云端后端 · 跨平台)              │
│                                                         │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌───────┐ ┌──────┐  │
│  │  Auth  │ │Workspace│ │ Storage│ │Files  │ │Browser│  │
│  └────────┘ └────────┘ └────────┘ └───────┘ └──────┘  │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │App-Capab-│ │  AppSettings │ │Registry│ │Image-    │ │
│  │ilities   │ │              │ │        │ │Mirrors   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │   Docker │ │ProcessGuardian│ │Firewall│ │System-   │ │
│  │          │ │ (SignalR Hub) │ │  (UFW) │ │Monitor   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │WebServers│ │ Certificates │ │  Git   │ │ Tunnels  │ │
│  │(Nginx…)  │ │  (ACME/Host) │ │        │ │  (FRP)   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  OS Abstraction Layer (Provider 接口族)             │  │
│  │  IIdentityProvider · ISystemMetricsProvider        │  │
│  │  IFirewallProvider · IWebServerProvider            │  │
│  │  ICertificateProvider · IGitProvider …             │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  Persistence (双域 SQLite)                          │  │
│  │  业务库: EF Core + 增量补齐; HostGlobal: v1~v7 迁移 │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│              RemoteOS.Guardian.Agent                     │
│       (独立进程 · 受守护工作负载 · 原生服务管理)            │
└─────────────────────────────────────────────────────────┘
```

---

## 🛠️ 技术栈

| 组件 | 技术 | 版本 |
|------|------|------|
| UI 框架 | [Avalonia UI](https://avaloniaui.net/) | 12.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| 框架 | .NET | 10.0 |
| 服务端 | ASP.NET Core | 10.0 |
| 实时通信 | SignalR | 10.0 |
| 身份认证 | JWT Bearer | — |
| 持久化 | EF Core + SQLite | 10.0 |
| 终端控件 | RoyalTerminal (Avalonia + PTY) | 0.4.0 |
| 浏览器 | Avalonia.Controls.WebView | 12.0.1 |
| 文件管理 UI | Jaya File Manager (BSD-3 许可) | — |
| 视频播放 | LibVLCSharp.Avalonia | 3.10.0 |

---

## 📁 项目结构

```
RemoteOS/
├── Client/
│   ├── RemoteOS.Client/          # 桌面 Shell + 内置应用（类库）
│   │   ├── Apps/                 # 内置应用
│   │   │   ├── Explorer/         # 文件管理器
│   │   │   ├── Terminal/         # 终端
│   │   │   ├── Browser/          # 浏览器
│   │   │   ├── Settings/         # 设置中心（系统/个性化/时间语言/网络/应用/镜像源/开发者）
│   │   │   ├── TaskManager/      # 任务管理器
│   │   │   ├── Docker/           # Docker 管理器
│   │   │   ├── ProcessGuardian/  # 进程守护
│   │   │   ├── Firewall/         # Linux UFW 防火墙
│   │   │   ├── PortForwarding/   # SSH 端口转发
│   │   │   ├── Certificates/     # ACME 证书管理
│   │   │   ├── WebServers/       # Web Server 管理器（Nginx 等）
│   │   │   ├── Git/              # Git 客户端
│   │   │   ├── Tunnels/          # FRP 隧道管理
│   │   │   ├── Registry/         # 配置注册表
│   │   │   ├── Notepad/          # 记事本
│   │   │   ├── CodeEditor/       # 代码编辑器
│   │   │   ├── TextEditor/       # 文本编码对话框（Notepad/CodeEditor 共用）
│   │   │   ├── ImageViewer/      # 图片查看器
│   │   │   ├── Welcome/          # 欢迎页
│   │   │   └── AppInstaller/     # 应用安装器
│   │   ├── Localization/         # 多语言资源（en-US / zh-CN / ja-JP）
│   │   ├── Services/             # 认证、权限、开发模式等服务
│   │   ├── ViewModels/           # Shell / Login ViewModel
│   │   └── Views/                # Shell / Login / MainWindow 视图
│   └── RemoteOS.Client.Desktop/  # 平台入口（WinExe）
├── Framework/
│   ├── RemoteOS.Core/            # 平台无关原语（几何、窗口、应用模型）
│   ├── RemoteOS.UI/              # Avalonia 共享主题/样式
│   ├── RemoteOS.WindowManager/   # 窗口管理器 + RemoteWindow 控件
│   ├── RemoteOS.App.SDK/         # 应用开发 API（AppContext / IRemoteApplication）
│   └── RemoteOS.Runtime/         # 应用运行时（ApplicationManager）
├── Shared/
│   └── RemoteOS.Protocol/        # 通信协议契约（DTO / 路由 / Hub 接口）
├── RemoteOS.Server/              # 服务端（ASP.NET Core）
├── RemoteOS.Guardian.Agent/      # 进程守护独立进程（原生服务管理）
├── Tools/
│   ├── RemoteOS.DevCli/          # 开发者 CLI 工具
│   └── verify-localization.py    # 多语言验证脚本
├── examples/
│   ├── VideoPlayer/              # 视频播放器示例应用
│   ├── ServerMonitor/            # 服务器监控示例应用
│   └── HelpCenter/               # 帮助中心示例应用
├── deployment/                   # 部署脚本（Linux / Windows）
├── docs/                         # 详细设计文档
├── Directory.Packages.props      # 中央包管理
└── RemoteOS.sln                  # 解决方案文件
```

---

## 🧩 内置应用

| 应用 | 说明 | 状态 |
|------|------|------|
| **Welcome** | 欢迎引导页，验证 Runtime 与 WindowManager | ✅ 已实现 |
| **Notepad** | 文本文件编辑（多编码 UTF-8/GBK/Shift-JIS 打开与保存） | ✅ 已实现 |
| **Code Editor** | 代码文件编辑（语法高亮、多编码支持） | ✅ 已实现 |
| **Image Viewer** | 图片文件浏览（缩放与滚动） | ✅ 已实现 |
| **Settings** | 系统设置中心（5+ 分类页：系统/个性化/时间和语言/网络/应用/镜像源/开发者） | ✅ 已实现 |
| **Terminal** | 远端终端（Remote Mode：SignalR + PTY 持久会话；Local Mode 回退） | ✅ 已实现 |
| **Explorer** | 远端文件管理器（REST API + 宿主 OS 权限复用） | ✅ 已实现 |
| **Browser** | 内置浏览器（书签/历史、主页与链接打开位置持久化） | ✅ 已实现 |
| **Port Forwarding** | 本机 SSH loopback 隧道管理（仅 Client 本地，不参与 Server 同步） | ✅ 已实现 |
| **Task Manager** | 远端任务管理器（性能页 SignalR 1Hz 推送 + 60s 历史；进程页低频采样） | ✅ 已实现 |
| **Docker Manager** | 远端 Docker Engine 管理（容器/镜像/Stack/网络/卷 + Compose 编排） | ✅ 已实现 |
| **Process Guardian** | 守护工作负载、IPC、持久化；SignalR `/hubs/guardian-logs` 日志广播 | 🚧 基本实现 |
| **Firewall** | Linux Server UFW 防火墙状态、默认策略与规则管理 | ✅ 已实现 |
| **App Installer** | 应用包（`.roapp`）安装与管理 | ✅ 已实现 |
| **Registry** | 配置注册表（键/值浏览、desired/applied 状态机、服务端持久化） | ✅ MVP |
| **Certificate Manager** | ACME 证书申请、续期、Kestrel 部署、吊销与删除、自签证书 | ✅ MVP |
| **Web Server Manager** | Nginx 实例/站点/配置快照/操作流水+审计（宿主级 HostGlobal 持久化） | ✅ MVP |
| **Git Client** | 远端 Git 仓库登记、分支、提交、拉取冲突解决、推送、历史 Log | ✅ MVP |
| **Tunnel Manager** | FRP 内网穿透（Server Profile/Definition/Secrets/Audit，Server 端持久化） | ✅ MVP |

---

## 🚀 快速开始

### 前置要求

- **.NET 10.0 SDK** 或更高版本
- **操作系统**：Windows 10/11、Windows Server 2016+、Ubuntu 20.04+
- （可选）Visual Studio 2022+ 或 JetBrains Rider

### 1. 克隆仓库

```bash
git clone <repository-url>
cd RemoteOS
```

### 2. 启动服务端

```bash
cd RemoteOS.Server

# 开发模式运行（默认监听 http://localhost:5000）
dotnet run
```

> ⚠️ **生产环境**：请务必修改 `appsettings.json` 中的 `Jwt:Secret`（至少 32 字符随机字符串）。

### 3. 启动客户端

```bash
cd Client/RemoteOS.Client.Desktop
dotnet run
```

客户端会弹出登录窗口，输入宿主系统的用户名和密码即可登录。

---

## 📖 详细文档

### 架构与核心模型

| 文档 | 说明 |
|------|------|
| [RemoteOS.Architecture.md](./docs/architecture/RemoteOS.Architecture.md) | 架构设计原则、模块依赖、分层架构 |
| [RemoteOS.Protocol.md](./docs/architecture/RemoteOS.Protocol.md) | 通信协议契约、REST/SignalR、序列化约定 |
| [RemoteOS.Workspace.md](./docs/architecture/RemoteOS.Workspace.md) | 用户/工作区/会话/设备、多设备模型 |
| [RemoteOS.ApplicationActivation.md](./docs/architecture/RemoteOS.ApplicationActivation.md) | 应用启动 URI 与窗口实例策略 |

### 平台服务

| 文档 | 说明 |
|------|------|
| [RemoteOS.Authentication.md](./docs/platform/RemoteOS.Authentication.md) | 登录系统、身份模型、OS 用户集成 |
| [RemoteOS.Authentication.Hardening.md](./docs/platform/RemoteOS.Authentication.Hardening.md) | 认证限流、风险控制与登录防护建议 |
| [RemoteOS.Login.md](./docs/platform/RemoteOS.Login.md) | 登录模块实现细节、mstsc 风格登录窗 |
| [RemoteOS.Security.md](./docs/platform/RemoteOS.Security.md) | 安全设计、权限提升、危险操作 |
| [RemoteOS.PrivilegedOperations.Goal.md](./docs/platform/RemoteOS.PrivilegedOperations.Goal.md) | 跨平台受限 Helper、Windows Server 支持与特权操作迁移执行计划 |
| [RemoteOS.Storage.md](./docs/platform/RemoteOS.Storage.md) | 服务端持久化、EF Core + SQLite |

### 桌面体验

| 文档 | 说明 |
|------|------|
| [RemoteOS.Desktop.md](./docs/desktop/RemoteOS.Desktop.md) | 桌面外壳、窗口控制、模态对话框、键盘路由 |
| [RemoteOS.Settings.md](./docs/desktop/RemoteOS.Settings.md) | 设置中心、偏好持久化、多设备同步 |
| [RemoteOS.Localization.md](./docs/desktop/RemoteOS.Localization.md) | 多语言机制、语言包结构 |

### 内置应用

| 文档 | 说明 |
|------|------|
| [RemoteOS.Terminal.md](./docs/applications/RemoteOS.Terminal.md) | 终端应用、SignalR、PTY、持久会话管理 |
| [RemoteOS.Explorer.md](./docs/applications/RemoteOS.Explorer.md) | 文件管理器、REST API、权限复用 |
| [RemoteOS.Browser.md](./docs/applications/RemoteOS.Browser.md) | 浏览器、书签/历史/偏好同步 |
| [RemoteOS.PortForwarding.md](./docs/applications/RemoteOS.PortForwarding.md) | SSH 端口转发、本机 loopback 隧道 |
| [RemoteOS.TaskManager.md](./docs/applications/RemoteOS.TaskManager.md) | 任务管理器、系统指标、进程管理、SignalR 推送重写 |
| [RemoteOS.DockerManager.md](./docs/applications/RemoteOS.DockerManager.md) | Docker 管理器、容器/镜像/Stack/网络/卷 |
| [RemoteOS.Firewall.md](./docs/applications/RemoteOS.Firewall.md) | Linux Server UFW 防火墙应用 |
| [RemoteOS.ProcessGuardian.md](./docs/applications/RemoteOS.ProcessGuardian.md) | 进程守护、健康检查、原生服务管理、日志 Hub |
| [RemoteOS.CertificateManager.md](./docs/applications/RemoteOS.CertificateManager.md) | ACME 证书生命周期、Kestrel 部署、续期、HostGlobal 持久化 |
| [RemoteOS.WebServerManager.Design.md](./docs/applications/RemoteOS.WebServerManager.Design.md) | Web Server 管理、Nginx 集成、站点/快照/审计 |
| [RemoteOS.GitClient.md](./docs/applications/RemoteOS.GitClient.md) | Git 客户端、仓库/分支/提交/冲突/历史 |
| [RemoteOS.FRP_Integration.Design.md](./docs/applications/RemoteOS.FRP_Integration.Design.md) | FRP 内网穿透架构、安全与运维边界 |
| [RemoteOS.RegistryApp.md](./docs/applications/RemoteOS.RegistryApp.md) | 配置注册表浏览、写入与隔离边界 |
| [RemoteOS.CodeEditor.md](./docs/applications/RemoteOS.CodeEditor.md) | 代码编辑器、语法高亮、文件安全边界 |
| [RemoteOS.NetworkInspector.md](./docs/applications/RemoteOS.NetworkInspector.md) | 网络检查器、诊断工具、网络分析 |

### 开发与扩展

| 文档 | 说明 |
|------|------|
| [RemoteOS.Develop.md](./docs/development/RemoteOS.Develop.md) | 开发者快速上手、代码结构、调试指南 |
| [RemoteOS.DeveloperMode.md](./docs/development/RemoteOS.DeveloperMode.md) | 开发模式、DevCli、应用包发布 |
| [RemoteOS.AppSettings.md](./docs/development/RemoteOS.AppSettings.md) | 应用私有配置存储 |
| [RemoteOS.BuiltInApplication.Conventions.md](./docs/development/RemoteOS.BuiltInApplication.Conventions.md) | 内置应用设计约束、国际化、跨平台 |
| [RemoteOS.ApplicationCompatibility.md](./docs/development/RemoteOS.ApplicationCompatibility.md) | 应用兼容性、平台适配、降级策略 |

### 项目文档索引

| 文档 | 说明 |
|------|------|
| [RemoteOS.md](./docs/README.md) | 项目结构、代码地图、当前进度 |

---

## 🔧 开发模式与扩展

RemoteOS 支持开发者构建自定义应用包（`.roapp`），通过 `DevCli` 工具安装到 RemoteOS Shell 中。

### 构建、安装与监视示例应用

```bash
# 设置开发令牌（或通过参数传递）
export REMOTEOS_DEV_TOKEN="<pairing-token>"

# 打包并安装应用；无需为每个应用维护 PowerShell 脚本
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# 监听源码，自动重新打包并更新
dotnet run --project Tools/RemoteOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` 在应用目录的 `artifacts/` 下生成 `.roapp`；纯托管应用可省略 `--runtime`。完整的第三方应用打包命令请参阅 [Developer Mode](./docs/development/RemoteOS.DeveloperMode.md)。

Windows PowerShell 中设置令牌时，使用 `$env:REMOTEOS_DEV_TOKEN = "<pairing-token>"`；其余 `dotnet` 命令保持不变。

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

RemoteOS 内置三种语言支持：

| 语言 | 代码 | 语言包路径 |
|------|------|-----------|
| 🇨🇳 简体中文 | `zh-CN` | `Client/RemoteOS.Client/Localization/zh-CN/` |
| 🇺🇸 English | `en-US` | `Client/RemoteOS.Client/Localization/en-US/` |
| 🇯🇵 日本語 | `ja-JP` | `Client/RemoteOS.Client/Localization/ja-JP/` |

语言包采用 JSON 格式，键值对结构。切换语言后 UI 实时更新。

---

## ⚠️ 第三方声明

本项目使用了以下第三方资源：

- **Jaya File Manager** (BSD 3-Clause License) — 文件管理器 UI 结构移植。详见 [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md)。
- 所有 NuGet 包的许可证信息请参考各自的包页面。

---

## 📄 许可证

本项目采用 **RemoteOS Non-Commercial Source-Available License** 许可。

**允许**：免费使用、修改、开发、学习、非商业目的分发。
**禁止**：商业售卖、转售、SaaS 托管或其他商业用途。

作者保留所有商业化权利。如需商业许可，请直接联系作者。

详见 [`LICENSE`](./LICENSE) 文件。第三方组件许可见 [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md)。

---

## 🤝 贡献

欢迎贡献代码！请：

1. Fork 本仓库
2. 创建特性分支 (`git checkout -b feature/amazing-feature`)
3. 提交更改 (`git commit -m 'Add: amazing feature'`)
4. 推送到分支 (`git push origin feature/amazing-feature`)
5. 创建 Pull Request

---

<div align="center">

**RemoteOS** — 让桌面跨越设备，让状态定义体验。

</div>
