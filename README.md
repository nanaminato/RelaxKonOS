<div align="center">

# RelaxKonOS

**云原生桌面操作系统环境**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[English](./README.en.md) · [日本語](./README.ja.md)

官网 <https://relaxkon.com> · 文档 <https://relaxkon.com/docs> · 下载 <https://relaxkon.com/downloads> · 仓库 <https://github.com/nanaminato/RelaxKonOS>

</div>

---

## ✨ 项目简介

**RelaxKonOS** 是一个跨平台的云原生桌面操作系统环境，采用 **状态同步（State-Sync）** 模式而非像素流（Pixel Streaming）模式。客户端在本地渲染 UI，服务端提供云端能力（账户、存储、同步、远程运行时），让用户在任何设备上获得一致的桌面体验。

**RelaxKonOS 不是** 远程桌面工具（RDP/VNC/Screen Streaming）。它传输的是系统状态、应用状态和用户操作意图，而非屏幕像素。

### 核心用途

- **让工作负载活过连接**：终端会话、被守护的进程与远程服务持续运行在服务端，网络瞬断不再等于会话丢失。
- **让多台设备共享同一个工作区**：工作站、笔记本与服务器控制台看到的是同一份 Workspace，而不是各自独立的本地状态。
- **把服务器运维收进一个桌面**：Docker、防火墙、证书、Web Server、Git、FRP 隧道与代理管理同处一个 Shell，并且直接复用宿主操作系统的账号与权限。
- **让应用可以扩展**：实现一个接口即可获得与内置应用相同的窗口管理、生命周期与能力 API。

### 适用人群

| 你是 | 建议的入手方式 |
| --- | --- |
| 个人用户 / 自建服务器爱好者 | 用**用户模式**在已有的普通 Linux 账号下装一份服务端（**不需要 sudo**），再在自己的电脑上运行客户端 |
| 运维 / 系统管理员 | 用**系统模式**把 Server、Guardian Agent 与权限助手注册为系统服务，面向多用户生产环境 |
| 应用开发者 | 用**开发者模式**与 `DevCli` 把自定义应用打包成 `.roapp` 装进同一个桌面 |
| 只想先看看 | 从[官网下载页](https://relaxkon.com/downloads)取已发布的客户端与服务端 ZIP，或[从源码运行](#从源码运行开发者) |

> 不确定该选哪种？先读[「选择安装方式」](#选择安装方式)——三种方式互不重叠，普通用户请勿照搬开发者或管理员的步骤。

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
- 🔀 **代理管理器** — 主机代理运行时（Mihomo 为首款引擎，可扩展 sing-box/Xray）、TUN 模式、订阅与配置档案、系统代理、流量/连接监控、网络安全与恢复
- 🧱 **配置注册表** — 受 schema 约束的 desired/applied 状态机配置中心
- 🪞 **镜像源管理** — APT/Docker/NPM/PyPI 等镜像源随 Workspace 偏好同步
- 🔧 **应用能力与私有 KV** — `/api/v1.0/capabilities` + App Settings 按用户/应用隔离 KV
- 🌍 **多语言支持** — 内置中文、英文、日文语言包
- 🔧 **开发者扩展** — 支持通过 `DevCli` 工具安装和管理自定义应用包

---

## 🏗️ 架构概览

```
┌─────────────────────────────────────────────────────────┐
│                    RelaxKonOS.Client                       │
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
│                   RelaxKonOS.Server                        │
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
│  │  Proxy Management (Mihomo 运行时 · TUN · 订阅/配置)  │  │
│  └───────────────────────────────────────────────────┘  │
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
│              RelaxKonOS.Guardian.Agent                     │
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
RelaxKonOS/
├── Client/
│   ├── RelaxKonOS.Client/          # 桌面 Shell + 内置应用（类库）
│   │   ├── Apps/                 # 内置应用
│   │   │   ├── Explorer/         # 文件管理器
│   │   │   ├── Terminal/         # 终端
│   │   │   ├── Browser/          # 浏览器
│   │   │   ├── Settings/         # 设置中心（系统/个性化/时间语言/网络/应用/镜像源/开发者）
│   │   │   ├── TaskManager/      # 任务管理器
│   │   │   ├── Docker/           # Docker 管理器
│   │   │   ├── ProcessGuardian/  # 进程守护
│   │   │   ├── Firewall/         # Linux UFW 防火墙
│   │   │   ├── Proxy/            # 代理管理器（Mihomo 运行时、TUN、订阅、系统代理）
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
│   └── RelaxKonOS.Client.Desktop/  # 平台入口（WinExe）
├── Framework/
│   ├── RelaxKonOS.Core/            # 平台无关原语（几何、窗口、应用模型）
│   ├── RelaxKonOS.UI/              # Avalonia 共享主题/样式
│   ├── RelaxKonOS.WindowManager/   # 窗口管理器 + RemoteWindow 控件
│   ├── RelaxKonOS.App.SDK/         # 应用开发 API（AppContext / IRemoteApplication）
│   └── RelaxKonOS.Runtime/         # 应用运行时（ApplicationManager）
├── Shared/
│   └── RelaxKonOS.Protocol/        # 通信协议契约（DTO / 路由 / Hub 接口）
├── RelaxKonOS.Server/              # 服务端（ASP.NET Core）
├── RelaxKonOS.Guardian.Agent/      # 进程守护独立进程（原生服务管理）
├── RelaxKonOS.PrivilegedHelper/    # 跨平台特权操作 Helper（Windows 服务 / Linux 守护）
├── Tools/
│   ├── RelaxKonOS.DevCli/          # 开发者 CLI 工具
│   ├── verify-localization.py    # 多语言验证脚本
│   └── slice_app_icons.py        # 应用图标精灵图切片脚本
├── examples/
│   ├── VideoPlayer/              # 视频播放器示例应用
│   ├── ServerMonitor/            # 服务器监控示例应用
│   └── HelpCenter/               # 帮助中心示例应用
├── deployment/                   # 部署脚本（Linux / Windows）
├── docs/                         # 详细设计文档
├── Directory.Packages.props      # 中央包管理
└── RelaxKonOS.sln                  # 解决方案文件
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
| **Task Manager** | 远端任务管理器（性能页订阅期间 SignalR 1Hz 推送 + 60s 历史；进程页按需低频采样） | ✅ 已实现 |
| **Docker Manager** | 远端 Docker Engine 管理（容器/镜像/Stack/网络/卷 + Compose 编排） | ✅ 已实现 |
| **Process Guardian** | 守护工作负载、IPC、持久化；SignalR `/hubs/guardian-logs` 日志广播 | 🚧 基本实现 |
| **Firewall** | Linux Server UFW 防火墙状态、默认策略与规则管理 | ✅ 已实现 |
| **App Installer** | 应用包（`.roapp`）安装与管理 | ✅ 已实现 |
| **Registry** | 配置注册表（键/值浏览、desired/applied 状态机、服务端持久化） | ✅ MVP |
| **Certificate Manager** | ACME 证书申请、续期、Kestrel 部署、吊销与删除、自签证书 | ✅ MVP |
| **Web Server Manager** | Nginx 实例/站点/配置快照/操作流水+审计（宿主级 HostGlobal 持久化） | ✅ MVP |
| **Git Client** | 远端 Git 仓库登记、分支、提交、拉取冲突解决、推送、历史 Log | ✅ MVP |
| **Tunnel Manager** | FRP 内网穿透（Server Profile/Definition/Secrets/Audit，Server 端持久化） | ✅ MVP |
| **Proxy Manager** | 代理管理器（Mihomo 运行时安装/启停升级、TUN 模式、订阅与配置档案、系统代理、流量/连接监控、网络安全与紧急恢复） | ✅ MVP |

---

## 🚀 快速开始

### 选择安装方式

RelaxKonOS 的**服务端**有三种安装方式，用途互不重叠。普通用户请只按「用户模式」操作，不要照搬管理员或开发者的步骤。

| 方式 | 平台 | 权限要求 | 适用场景 | 入口 |
| --- | --- | --- | --- | --- |
| **用户模式（User Mode）** | 仅 Linux | **不需要 sudo**，并且拒绝以 root 运行 | 个人在已有的普通账号下自建一份服务端；仅监听 `127.0.0.1` | [`deployment/user/`](./deployment/user/) |
| 系统模式（System Mode） | Linux（systemd）/ Windows Server | 需要 root 或管理员 | 多用户生产部署：注册系统服务、权限助手与防火墙规则 | [一键服务端安装器](./deployment/README.md) |
| 开发者模式（Developer Mode） | 全平台 | 需要 .NET 10 SDK | 参与 RelaxKonOS 自身开发，构建与调试应用包 | [从源码运行](#从源码运行开发者) |

**客户端**与服务端是相互独立的压缩包：客户端只需下载 ZIP、解压后直接运行，**不需要**安装到服务器上。

> 官网[下载页](https://relaxkon.com/downloads)给出稳定通道的安装命令、包名与 SHA-256 校验和；离线服务器可直接取其中的服务端 ZIP。

### 用户模式安装（Linux，无 sudo）

用户模式面向「我只想在自己的 Linux 账号下跑一份服务端」的场景。它只用你的 XDG 目录，**不创建 systemd 系统服务、不修改 PAM / sudoers / 防火墙 / `/etc`**，也不需要一个常驻的权限助手。

#### 前置条件

- 一个**普通（非 root）Linux 账号**。安装脚本与生命周期命令都会显式拒绝 root 身份，`sudo` 反而会让它失败。
- 系统命令：`bash`、`realpath`、`stat`、`find`、`sha256sum`、`flock`。缺少 `flock`（通常在 `util-linux` 中）会直接报错。
- 若从 HTTPS 发布地址在线安装，还需要 `curl` 与 `unzip`。
- 一份 **`*-user-server.zip`** 发布包（或 `--release-uri` + `--release-sha256`）。包内 `manifest.json` 必须声明 `packageKind: "user-server"`，客户端包与服务端包都会被拒绝。
- 不需要 systemd，不需要 sudo，不需要 root。

#### 安装步骤

```bash
# 1. 以目标账号解压 user-server 发布包（不要用 sudo）
unzip RelaxKonOS-<version>-linux-x64-user-server.zip -d RelaxKonOS-user-server

# 2. 执行用户模式安装器（--mode user 是必需的）
./RelaxKonOS-user-server/deployment/user/install-relaxkonos.sh \
  --mode user \
  --bundle ./RelaxKonOS-user-server
```

安装器会依次校验 bundle 完整性、`manifest.json` 的 `packageKind`、全部文件的 SHA-256 与文件清单，然后把版本落到 `bin/relaxkon` 这个稳定命令路径下。它**不会**修改你的 `PATH`。

也可以从官方发布地址在线安装（必须同时给出 SHA-256）：

```bash
./deployment/user/install-relaxkonos.sh \
  --mode user \
  --release-uri https://<host>/relaxkonos/stable/<version>/linux-x64/server/<archive>.zip \
  --release-sha256 <64-hex-sha256>
```

#### 安装位置

用户模式只写入当前账号的 XDG 目录，全部权限为 `0700` / `0600`：

| 用途 | 默认路径 |
| --- | --- |
| 程序与版本目录 | `${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos/`（`server/versions/<version>/`、`server/current` 软链） |
| 生命周期命令 | `${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos/bin/relaxkon` |
| 配置与密钥 | `${XDG_CONFIG_HOME:-$HOME/.config}/relaxkonos/`（`appsettings.user.json`、`secrets/guardian.secret`） |
| 运行状态 | `${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos/`（PID、控制套接字、`install-state.json`、SQLite 数据库） |
| 日志 | `${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos/logs/{server,guardian}.log` |
| 下载缓存 | `${XDG_CACHE_HOME:-$HOME/.cache}/relaxkonos/` |

#### 启动与验证

```bash
# 把命令路径存成变量，后续命令都基于它
RELAXKON=""${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos/bin/relaxkon""

"$RELAXKON" start      # 后台启动 Server 与同 UID 的 Guardian，并等待就绪
"$RELAXKON" status     # 期望输出：RelaxKonOS User Mode is running (pid <n>, loopback 127.0.0.1:5000).
```

`status` 会通过用户私有的控制套接字（`…/relaxkonos/run/server.sock`，权限 `0600`）探测 `/ready`，因此它比「进程还在不在」更严格：只要套接字没就绪，就会明确报出未就绪而不是假装成功。

其他常用命令：

```bash
"$RELAXKON" start --foreground   # 前台运行，便于直接观察日志
"$RELAXKON" stop                 # 停止 Server 与 Guardian
```

服务端默认监听 `http://127.0.0.1:5000`；端口可用环境变量覆盖：

```bash
RELAXKONOS_PORT=5100 "$RELAXKON" start
```

**远程连接**：用户模式只绑定回环地址，所以请用 SSH 本地转发把端口带到你自己的机器上，再把客户端指向本机地址：

```bash
ssh -L 5000:127.0.0.1:5000 <user>@<server>
```

#### 升级

```bash
"$RELAXKON" upgrade --bundle ./RelaxKonOS-<new-version>-linux-x64-user-server
```

升级会先停止服务、安装新版本、重新启动并等待就绪；如果就绪检查失败，会自动回滚到升级前的版本。

> 同一个版本号不能被重复安装。升级时请使用新的版本号。

#### 卸载

```bash
"$RELAXKON" uninstall
```

它会先停止服务，再删除上文表格中的 data / config / state / cache 四个目录。

> ⚠️ `uninstall` 会一并删除数据库、配置、密钥与日志，**不可恢复**。如需保留数据，请先备份 `${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos/` 与 `${XDG_CONFIG_HOME:-$HOME/.config}/relaxkonos/`。

#### 常见问题

| 现象 | 原因与处理 |
| --- | --- |
| `User Mode must not be installed as root.` | 用了 `sudo` 或已经切到 root。请换回普通账号重新执行。 |
| `--mode user or --mode system is required.` | 漏写 `--mode user`（或用了 `--mode system` 却没加 `sudo`）。 |
| `not a complete user-server bundle` | 解压的不是 `*-user-server` 包，或包不完整（缺少 `manifest.json`、`payload/`、`deployment/user/relaxkon`）。 |
| `bundle is not a user-server manifest` | 包的 `manifest.json` 不是 `packageKind: "user-server"`。请下载用户态服务端包。 |
| `bundle file checksum verification failed` | 传输损坏。重新下载并核对官方公布的 SHA-256 后重试。 |
| `another RelaxKonOS lifecycle operation is already running` | 另一个终端持有生命周期锁（`…/relaxkonos/run/launcher.lock`）。等它结束再执行。 |
| `flock is required for safe User Mode lifecycle operations` | 系统缺少 `flock`（`util-linux`）。先安装再重试。 |
| `status` 提示进程在跑但控制套接字未就绪 | 先看 `logs/server.log`；通常是首次启动仍在初始化，或端口被占用。 |
| `version already installed: <version>` | 该版本已安装。换用新版本号，或先 `uninstall` 再安装。 |

### 系统模式（管理员 / 生产部署）

Linux 需要 root，并用显式模式调用安装器；Windows 需要在管理员 PowerShell 中执行：

```bash
# Linux System Mode：注册 systemd 服务、权限助手与 sudoers 规则
sudo ./deployment/bootstrap/install-relaxkonos.sh --mode system --bundle /mnt/RelaxKonOS-release
```

```powershell
# Windows Server：注册 Windows 服务与权限助手
& .\deployment\bootstrap\Install-RelaxKonOS.ps1 -BundlePath 'D:\RelaxKonOS-release'
```

系统模式会生成并保护 JWT 与组件 IPC 密钥，安装 Server、Guardian Agent 与权限助手，并完成健康检查。完整参数、网络模式（仅本机 / 局域网 / 反向代理）、证书模式与离线安装见[一键服务端安装器](./deployment/README.md)。

> 客户端分发（便携 ZIP 与 Windows MSIX）见 [`deployment/ClientDistribution.md`](./deployment/ClientDistribution.md)。

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

### 平台服务

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Authentication.md](./docs/platform/RelaxKonOS.Authentication.md) | 登录系统、身份模型、OS 用户集成 |
| [RelaxKonOS.Authentication.Hardening.md](./docs/platform/RelaxKonOS.Authentication.Hardening.md) | 认证限流、风险控制与登录防护建议 |
| [RelaxKonOS.Login.md](./docs/platform/RelaxKonOS.Login.md) | 登录模块实现细节、mstsc 风格登录窗 |
| [RelaxKonOS.Security.md](./docs/platform/RelaxKonOS.Security.md) | 安全设计、权限提升、危险操作 |
| [RelaxKonOS.PrivilegedOperations.Goal.md](./docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md) | 跨平台受限 Helper、Windows Server 支持与特权操作迁移执行计划 |
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
| [RelaxKonOS.DockerManager.md](./docs/applications/RelaxKonOS.DockerManager.md) | Docker 管理器、容器/镜像/Stack/网络/卷 |
| [RelaxKonOS.Firewall.md](./docs/applications/RelaxKonOS.Firewall.md) | Linux Server UFW 防火墙应用 |
| [RelaxKonOS.ProcessGuardian.md](./docs/applications/RelaxKonOS.ProcessGuardian.md) | 进程守护、健康检查、原生服务管理、日志 Hub |
| [RelaxKonOS.CertificateManager.md](./docs/applications/RelaxKonOS.CertificateManager.md) | ACME 证书生命周期、Kestrel 部署、续期、HostGlobal 持久化 |
| [RelaxKonOS.EventAlertCenter.Goal.md](./docs/applications/RelaxKonOS.EventAlertCenter.Goal.md) | 事件与告警中心：汇聚、审计、跳转与处理入口实施计划 |
| [RelaxKonOS.WebServerManager.Design.md](./docs/applications/RelaxKonOS.WebServerManager.Design.md) | Web Server 管理、Nginx 集成、站点/快照/审计 |
| [RelaxKonOS.GitClient.md](./docs/applications/RelaxKonOS.GitClient.md) | Git 客户端、仓库/分支/提交/冲突/历史 |
| [RelaxKonOS.FRP_Integration.Design.md](./docs/applications/RelaxKonOS.FRP_Integration.Design.md) | FRP 内网穿透架构、安全与运维边界 |
| [RelaxKonOS.ProxyManager.Design.md](./docs/applications/RelaxKonOS.ProxyManager.Design.md) | 代理管理器、Mihomo 运行时、TUN、订阅与配置档案 |
| [RelaxKonOS.RegistryApp.md](./docs/applications/RelaxKonOS.RegistryApp.md) | 配置注册表浏览、写入与隔离边界 |
| [RelaxKonOS.CodeEditor.md](./docs/applications/RelaxKonOS.CodeEditor.md) | 代码编辑器、语法高亮、文件安全边界 |
| [RelaxKonOS.NetworkInspector.md](./docs/applications/RelaxKonOS.NetworkInspector.md) | 网络检查器、诊断工具、网络分析 |

### 代理（Proxy）

| 文档 | 说明 |
|------|------|
| [architecture.md](./docs/proxy/architecture.md) | 代理模块架构、引擎抽象（IProxyEngine）、Mihomo 集成 |
| [installation.md](./docs/proxy/installation.md) | 代理运行时安装、部署与升级 |
| [mihomo.md](./docs/proxy/mihomo.md) | Mihomo 引擎配置、控制面与运行时管理 |
| [tun.md](./docs/proxy/tun.md) | TUN 模式、网络栈与透明代理 |
| [recovery.md](./docs/proxy/recovery.md) | 代理故障恢复、网络安全与紧急禁用 |
| [security.md](./docs/proxy/security.md) | 代理安全模型、权限边界与审计 |
| [troubleshooting.md](./docs/proxy/troubleshooting.md) | 代理排障指南与常见问题 |

### 开发与扩展

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.Develop.md](./docs/development/RelaxKonOS.Develop.md) | 开发者快速上手、代码结构、调试指南 |
| [RelaxKonOS.DeveloperMode.md](./docs/development/RelaxKonOS.DeveloperMode.md) | 开发模式、DevCli、应用包发布 |
| [RelaxKonOS.AppSettings.md](./docs/development/RelaxKonOS.AppSettings.md) | 应用私有配置存储 |
| [RelaxKonOS.BuiltInApplication.Conventions.md](./docs/development/RelaxKonOS.BuiltInApplication.Conventions.md) | 内置应用设计约束、国际化、跨平台 |
| [RelaxKonOS.ApplicationCompatibility.md](./docs/development/RelaxKonOS.ApplicationCompatibility.md) | 应用兼容性、平台适配、降级策略 |

### 项目文档索引

| 文档 | 说明 |
|------|------|
| [RelaxKonOS.md](./docs/README.md) | 项目结构、代码地图、当前进度 |

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
