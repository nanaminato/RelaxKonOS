# RemoteOS 架构设计文档

> 本文档定义 RemoteOS 的架构设计原则：模块定位、职责边界、依赖约束、应用运行模型、Client / Server 架构原则。
>
> 本文档描述**设计原则**，不描述当前完整代码结构。
>
> - 当前项目文件清单与代码地图见 [`RemoteOS.md`](../README.md)
> - 当两者存在差异：**架构原则以本文档为准**，**当前实现以 `RemoteOS.md` 为准**。

---

## 1. RemoteOS 核心定位

RemoteOS 是一个跨平台云原生桌面操作系统环境。

RemoteOS 不是：Remote Desktop、RDP Server、VNC、Screen Streaming Tool。

**RemoteOS 禁止采用**（像素流模式）：

```text
Server
  Application
  Desktop Rendering
  Screen Capture
      |
  Pixel Stream
      |
Client
```

**RemoteOS 采用**（状态同步模式）：

```text
RemoteOS.Client                    RemoteOS.Server
  Window Manager                       Workspace
  Application Runtime       |          State
  Local Rendering          Protocol    Storage
                          ←──────→     Remote Runtime
                                       Compute
```

核心原则：

> RemoteOS 传输的是系统状态、应用状态和用户操作意图，而不是屏幕像素。

---

## 2. Client / Server 职责划分

### 2.1 RemoteOS.Client

RemoteOS.Client 是用户交互端。

- **负责**：Desktop Shell、Window Manager、UI Rendering、Application UI、用户输入处理、本地 Runtime。
- **Client 不负责**：用户身份管理、云端数据存储、Workspace 生命周期、Remote Service 执行。

### 2.2 RemoteOS.Server

RemoteOS.Server 是 RemoteOS Cloud Backend，**跨平台运行于 Ubuntu（Linux）与 Windows Server**。

- **负责**：User Account、Workspace、Application State、Storage、Synchronization、Remote Runtime、Compute Service。
- **架构**：单一代码库 + OS 抽象层。平台差异（认证、权限、文件、服务管理）封装在抽象接口之后，Linux/Windows 各有实现。
- **Server 不负责**：UI Rendering、Window Management、Screen Capture、Pixel Streaming。
- **Server 永远不生成桌面图像。**

---

## 3. Solution Architecture

RemoteOS 采用分层架构：

```text
Application Layer      （Client / 内置应用）
    ▲
Runtime Layer          （Runtime / App.SDK）
    ▲
Framework Layer        （UI / WindowManager）
    ▲
Platform Layer         （Core）
```

项目依赖方向：

```text
Core → UI → WindowManager → App.SDK → Runtime → Client → Client.Desktop
```

通信方向：

```text
Client ←── Protocol ──→ Server
```

禁止：

- Core 引用 UI
- Framework 引用 Server
- Application 直接访问网络
- UI 直接调用 HTTP / WebSocket

---

## 4. 模块定位与边界

### 4.1 RemoteOS.Client.Desktop

- **定位**：RemoteOS 平台入口，类似 Windows Boot Loader / Linux Desktop Entry。
- **职责**：配置 Avalonia AppBuilder、平台初始化、字体配置、日志初始化、启动 `RemoteOS.Client`。
- **不包含**：Shell 逻辑、Window Logic、Application Logic。

### 4.2 RemoteOS.Client

- **定位**：RemoteOS Shell，类似 Windows Explorer。
- **职责**：Desktop、Taskbar、StartMenu、Shell Window、System UI、内置应用装配、DI 初始化。
- **装配关系**：

  ```text
  Application → Runtime → WindowManager
  ```

- **不包含**：WindowManager 算法、Application 生命周期、网络通信。

### 4.3 RemoteOS.Core

- **定位**：平台无关基础层。所有模块均可以依赖 Core。
- **包含**：
  - **Geometry**：`Point`、`Size`、`Rect`
  - **Window Model**：`WindowId`、`WindowInfo`、`WindowState`、`WindowChangedEventArgs`
  - **Application Model**：`AppId`、`ApplicationManifest`、`ApplicationInfo`
- **严格禁止**：Core 不允许引用 Avalonia、Network、Database、Server。Core 必须保持纯净。

### 4.4 RemoteOS.UI

- **定位**：RemoteOS 统一视觉系统，类似 WinUI / Material Design。
- **职责**：Theme、Style、Control Template、System Components。
- **例如**：Button、TextBox、ListBox、RemoteWindow Style。
- **所有系统 UI 必须复用该模块。**

### 4.5 RemoteOS.WindowManager

- **定位**：RemoteOS 核心窗口管理系统，负责模拟操作系统窗口。
- **架构**：

  ```text
  WindowManager
      |
  RemoteWindow
      |
  Avalonia Control
  ```

  `WindowManager` 是窗口状态唯一管理者。

- **职责**：Create、Close、Move、Resize、Focus、Minimize、Maximize、Z Order、Taskbar State、**模态对话框（`ShowDialogAsync<TResult>` + `ModalDialog<TResult>` + owner 局部遮罩 `ModalBlocker`，可嵌套）**。详见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md) §3。
- **应用启动流程**：

  ```text
  User Click Icon
      |
  ApplicationManager.Launch
      |
  AppContext.ShowWindow
      |
  WindowManager.Create
      |
  RemoteWindow
  ```

- **禁止**：WindowManager 包含 Application Logic、Application Registry。

### 4.6 RemoteOS.App.SDK

- **定位**：RemoteOS 应用开发接口，类似 Windows SDK / Android SDK。
- **提供**：
  - **Window API**（已实现）：`AppContext.ShowWindow()`
  - **Modal Dialog API**（已实现）：`AppContext.ShowDialogAsync<TResult>(owner, title, contentFactory)`，可复用、可嵌套、任意结果类型，详见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md) §3
  - **Storage API**（规划）：`Storage.Save()` / `Storage.Load()`
  - **Sync API**（规划）：`Sync.Push()` / `Sync.Pull()`
  - **Remote API**（规划）：`RemoteClient.Execute()`
- **应用接入方式**：实现 `IRemoteApplication` 或继承 `RemoteApplicationBase`。

### 4.7 RemoteOS.Runtime

- **定位**：RemoteOS 应用运行时。RemoteOS 应用不是普通 exe。
- **职责**：Application Registry、Application Loading、Lifecycle Management。
- **流程**：

  ```text
  Desktop Icon
      |
  ApplicationManager.Launch
      |
  Create AppContext
      |
  IRemoteApplication.Activate
      |
  ShowWindow
      |
  WindowManager
  ```

- **Runtime 不负责**：Window Rendering、Window Algorithm、UI Control。

### 4.8 RemoteOS.Protocol

- **定位**：Client 与 Server 通信契约。所有通信必须经过 Protocol。
- **禁止**：业务代码直接调用 HTTP / WebSocket / TCP。
- **Protocol 包含**：DTO、Message、API Contract、Client Proxy。

### 4.9 RemoteOS.Server

- **定位**：RemoteOS Cloud Backend，跨平台运行于 Ubuntu / Windows Server。
- **提供**：Authentication、Workspace、Storage、Sync、Remote Runtime、Compute API、Security Integration。
- **架构**：单一代码库 + OS 抽象层。平台差异封装在 Provider 抽象接口族之后，Linux/Windows 各自提供实现；所有原生 OS API 先在 `Windows Server Test` 测试床验证，再迁移进抽象层实现。
  - 身份：`IIdentityProvider`（`WindowsLogonProvider` / `LinuxPamProvider`，占位）
  - 系统指标：`ISystemMetricsProvider`（`WindowsMetricsProvider` / `LinuxMetricsProvider`）
  - 防火墙：`IFirewallProvider`（`LinuxUfwFirewallProvider` / Windows 暂禁用）
  - Web Server：`IWebServerProvider`（Nginx 优先，IIS/Apache 设计中）
  - 证书：`ICertificateProvider`（ACME + 宿主机证书存储，Kestrel 部署已实现）
  - Git：`IGitProvider`（跨平台 `git` CLI 封装，凭据委托宿主 OS）
  - 隧道运行时：`ITunnelRuntimeProvider`（FRP managed/external 两种模式）
  - Docker：`IDockerEngineService` / `IDockerComposeService`（基于 `docker` CLI）
  - 守护：`IProcessGuardianService`（命名管道 IPC ↔ Guardian Agent）
  - 文件：`IFileService`（`LocalFileService`，以宿主 OS 进程身份执行 IO）
- **端点族（Program.cs 已注册 17 组 + 健康检查）**：
  - `MapAuthEndpoints`（login/refresh/logout/me）、`MapWorkspaceEndpoints`（preferences / window-layout）
  - `MapFileEndpoints`、`MapBrowserEndpoints`（书签/历史/设置）、`MapSystemMonitorEndpoints` + SignalR `/hubs/performance`
  - `MapAppCapabilityEndpoints`（`/api/v1.0/capabilities`，宣告当前 Server 身份/平台支持）
  - `MapAppSettingsEndpoints`（应用私有 KV，按 UserId/Scope/ScopeId/AppId/Key 隔离）
  - `MapRegistryEndpoints`（配置注册表 keys/values 浏览+写入 desired/applied 状态机）
  - `MapImageMirrorEndpoints`（APT/Docker/NPM/PyPI 等镜像源配置随 Workspace 同步）
  - `MapDockerEndpoints`、`MapProcessGuardianEndpoints` + SignalR `/hubs/guardian-logs`
  - `MapWebServerEndpoints`（实例/站点/快照/操作）、`MapCertificateEndpoints`（ACME 生命周期+部署）
  - `MapGitEndpoints`、`MapTunnelEndpoints`、`MapFirewallEndpoints`、`MapHealthEndpoints`（`/healthz` / `/ready`）
- **持久化（双域 SQLite）**：
  - 业务库：EF Core `EnsureCreated` + 启动时 `CREATE TABLE IF NOT EXISTS` 增量补齐；覆盖 User/Workspace(含 TerminalSettings/BrowserSettings/Preferences/WindowLayout)/Device/Bookmark/HistoryEntry/AppSettings/ImageMirrors/GitRepository/TunnelServerProfile/TunnelDefinition/TunnelSecret/TunnelAuditEntry/RegistryKey/RegistryEntry/AccountFailureState/AuthenticationSecurityEvent
  - HostGlobal 域：证书/WebServer 等宿主级资源不挂 DbContext，走 `HostGlobalMigrationRunner` 自写 v1~v7 版本化迁移（事务保证 + 列存在性判断）；含 certificate_* 与 webserver_* 全套操作流水/记录/审计
- **进程内内存态（不落库，各有语义理由）**：Session / 刷新令牌 / PTY 由 `TerminalSessionManager` 持有的进程 + Guardian Agent 守护运行态
- **禁止**：UI Rendering、Desktop Rendering、Screen Streaming。
- **详见**：[`RemoteOS.Authentication.md`](../platform/RemoteOS.Authentication.md)、[`RemoteOS.Security.md`](../platform/RemoteOS.Security.md)、[`RemoteOS.Storage.md`](../platform/RemoteOS.Storage.md)、[`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)

---

## 5. Application Runtime Model

RemoteOS Application 不是简单本地程序。Application 是：

> RemoteOS Runtime Managed Component

应用由以下部分组成：

```text
Application Package
  ├── Manifest
  ├── UI
  ├── Logic
  ├── State Manager
  └── Remote Connector
```

---

## 6. Application Execution Model

RemoteOS 应用分为两类。

### 6.1 Local Application

- **运行位置**：`RemoteOS.Client`。
- **特点**：UI 本地渲染、Runtime 本地执行、使用 Client 硬件能力。
- **例如 RemoteBrowser**：

  ```text
  RemoteBrowser
      |
  Avalonia Window
      |
  WebView2
      |
  Chromium
  ```

  Server 保存：History、Bookmark、Cookie、Extension Config。

### 6.2 Remote Service Application

- **运行位置**：`RemoteOS.Server`。
- **特点**：Server 执行 Runtime、Client 提供交互 UI、状态持久保存。
- **例如 RemoteTerminal**（**已实现 MVP**，含持久会话）：
  - Client：Terminal Window、Input、Output Rendering（VT 解析在客户端 `TerminalControl` 完成）
  - Server：PTY（ConPTY/forkpty）、Shell、Process（`TerminalHub` 哑中继，只转发字节）；PTY 由 `TerminalSessionManager`（Singleton）持有，与 Hub 连接解耦
  - 传输：SignalR Hub（`/hubs/terminals`），JWT 鉴权，详见 [`RemoteOS.Terminal.md`](../applications/RemoteOS.Terminal.md)
- **断开**：Client Offline 不会导致 Runtime Destroy。`TerminalHub.OnDisconnectedAsync` 仅 `session.Detach`，**保留 PTY**；只有显式 `Close`（关闭终端窗口 / "断开"按钮）才 `manager.Remove` 杀 PTY。PTY 输出始终追加进 1MB 环形缓冲（ConPTY 读线程持续排空管道，shell 不阻塞）。
- **重新连接**：Restore Terminal Session —— 再次登录打开终端，`Start(Attach)` 命中存活会话则回放 1MB 缓冲快照重现历史输出，可继续输入。这是"再次登录恢复原桌面"的前提。

---

## 7. RemoteOS 架构原则

- **原则 1 — UI 本地渲染**。禁止 Screen Capture、Pixel Streaming。
- **原则 2 — 状态优先**。RemoteOS 同步 State / Data / Command，而不是 Image / Frame。
- **原则 3 — 模块单向依赖**。禁止反向引用；所有 Client/Server 通信必须经 `RemoteOS.Protocol`。
- **原则 4 — 应用隔离**。应用只能通过 App.SDK / Runtime API 访问系统能力；私有 KV 经 `MapAppSettingsEndpoints` 隔离，禁止直接读写 SQLite 文件。
- **原则 5 — 用户/权限委托宿主 OS**。RemoteOS 不建独立 ACL/密码体系；身份走 `IIdentityProvider`，文件权限走宿主 IO 身份，提权（PAM/UAC/SCM/sudo）交给宿主。
- **原则 6 — 持久化双域隔离**。
  - User/Workspace 域：业务库（EF Core + 增量补齐），每用户隔离。
  - HostGlobal 域：证书/WebServer 等宿主级资源走 `HostGlobalMigrationRunner` 独立版本化迁移，不纳入业务 DbContext，确保证书签发/WebServer 操作的可恢复性独立于业务 schema 演进。
- **原则 7 — OS 抽象先探测后集成**。任何原生 OS API（Win32、/proc、PAM、UFW、Nginx 控制、systemd/SCM）先在「Windows Server Test」测试床验证，再进入 `*Provider` 实现，确保 Ubuntu/Windows Server 两种路径都有设计位。

---

## 8. AI Agent 开发规则

修改 RemoteOS 代码时必须遵守：

- **禁止实现**：RDP、VNC、Screen Capture、Desktop Streaming、Pixel Transfer。
- **正确方向**：Local UI Rendering、Application Runtime、Window Management、Workspace State、Remote Service。
- **架构纪律**：Core 纯净、Protocol 作为唯一通信入口、WindowManager 管理窗口、Runtime 管理应用生命周期、Shell 负责装配。

---

## 9. 开发原则

本地 Shell 已就绪。后续按优先级逐步丰富系统能力：

1. `RemoteOS.Client`
2. `RemoteOS.WindowManager`
3. `RemoteOS.Core`
4. `RemoteOS.Runtime`
5. `RemoteOS.App.SDK`

服务端能力按「**先 Protocol，再 Endpoints + Provider，最后 Client App UI**」节奏实现：

- 身份与登录 → 已完成（`IIdentityProvider` + auth 端点 + JWT + 安全防护表）
- Workspace / Preferences / Window Layout → 已完成（业务库 JSON 列持久化，多设备同步）
- Storage / Files / Browser → 已完成（REST + 书签/历史/AppSettings 持久化）
- SystemMonitor + TaskManager → 已完成（`ISystemMetricsProvider` + SignalR `/hubs/performance` 推送）
- Docker / ProcessGuardian / Firewall → 已基本完成（CLI 封装 + Guardian Agent IPC + UFW，健康/服务适配设计中）
- Registry / AppSettings / Capabilities / ImageMirrors → 已实现 MVP（配置状态机+应用私有 KV+能力宣告+镜像源同步）
- Certificates / WebServers → 已实现 MVP（ACME 证书闭环 + Nginx 最小侵入集成，HostGlobal 自写迁移器）
- Git / Tunnels → 已实现 MVP（Git CLI 封装 / FRP 配置持久化与审计）

每个能力落地必须：先有 Protocol 契约（`Shared/RemoteOS.Protocol/*`），再有 Server 端点与仓储，然后是 Client 应用（`Apps/*/`）。详见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md) 与各个应用设计文档。

---

## AI Agent 理解总结

```text
RemoteOS         = Operating System Shell
RemoteOS.Client  = UI / Window / Interaction
RemoteOS.Server  = Workspace / State / Storage / Remote Runtime
Application      = RemoteOS Runtime Managed Component（不是普通 exe）
```

任何设计必须优先考虑：状态驱动、本地 UI 渲染、模块隔离、Server 提供云能力。

不要将 RemoteOS 演变为：Remote Desktop Tool 或 Server Management Dashboard。
