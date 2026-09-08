# RelaxKonOS Terminal 模块设计

> **当前 UI 行为（优先于下文早期工具条描述）**：一个服务端 PTY 对应一个桌面终端窗口；不提供会话切换、新建、断开、Restart 或 Clear 按钮。进入已认证桌面时会自动恢复所有存活会话；再次手动打开 Terminal 时也会恢复存活会话，若不存在会话则新建一个。只有关闭某个终端窗口才会显式终止对应的服务端进程。字体、字号和配色为 Workspace 级设置，经 `GET`/`PUT /api/v1.0/workspaces/{id}/terminal-settings` 保存在服务器端。

> 内置终端应用：基于 [RoyalTerminal](https://github.com/royalapplications/RoyalTerminal) NuGet 包引入终端能力，支持 **Remote Mode**（SignalR 远端 PTY）与 **Local Mode**（本地 PTY 回退）。
>
> - 架构原则见 [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)（§6 Application Execution Model / §6.2 Remote Service Application）
> - 项目当前状态见 [`RelaxKonOS.md`](../README.md)（§7 RemoteTerminal）
> - 桌面外壳与窗口管理见 [`RelaxKonOS.Desktop.md`](../desktop/RelaxKonOS.Desktop.md)
> - 登录与身份认证见 [`RelaxKonOS.Login.md`](../platform/RelaxKonOS.Login.md)（终端 Remote Mode 复用 `IAuthSession` JWT）

---

## 1. 定位

RemoteTerminal 是 RelaxKonOS 的内置应用之一，遵循架构 §6 的两类应用模型：

- **Remote Mode**（本次实现）：PTY 运行于 `RelaxKonOS.Server`，经 **SignalR Hub** 流式传输到 Client。属 §6.2 Remote Service Application。客户端认证后（`IAuthSession.State == Authenticated`）自动走 Remote Mode，JWT 通过 SignalR `AccessTokenProvider` 传递。
- **Local Mode**（回退）：运行于 `RelaxKonOS.Client`，本地 PTY 启动平台默认 shell。属 §6.1 Local Application。当未登录（dev 调试）时自动回退。

**传输选型：SignalR**。RoyalTerminal 的终端栈是传输方式无关的（`ITerminalTransport` 抽象），SignalR 与裸 WebSocket 均可行。本项目选择 SignalR 的理由：
1. 已有 `Microsoft.AspNetCore.SignalR.Client` 基础设施（与后续 Workspace Hub 同栈）。
2. 自动重连、JWT 鉴权、强类型 Hub 契约（`Hub<ITerminalHubClient>`）。
3. 无需额外引入裸 WebSocket 端点与手写握手机制。

### 1.1 从其他内置应用打开终端

`RelaxKonOS.AppSDK.IOpenTerminalApplication` 定义了“在指定远程目录中新建终端”的应用契约。内置应用通过
`ApplicationManager.OpenTerminal(workingDirectory)` 调用该契约，无需依赖 `TerminalApp`；运行时会检查应用兼容性后，将目录传给终端的 `WorkingDirectory`。RemoteExplorer 的“在此处打开终端”菜单会优先使用所选文件夹，否则使用当前地址栏目录。

---

## 2. 包与集成方式

### 2.1 NuGet 包

| 包 | 版本 | 用途 |
|----|------|------|
| `RoyalApps.RoyalTerminal.Avalonia` | 0.4.0 | Avalonia 终端控件 `TerminalControl` + 默认会话组合（托管 VT 处理器 + 平台 PTY 工厂），目标 net10.0 |
| `RoyalApps.RoyalTerminal.Terminal.Pty.Platform` | 0.4.0 | Server 端 PTY 工厂（Windows ConPTY / Unix forkpty），Terminal Hub 哑中继用 |
| `Microsoft.AspNetCore.SignalR.Client` | 10.0.0 | Client 端 SignalR 连接（`HubConnection`） |

- 中心化包管理：版本声明在 [`Directory.Packages.props`](../../Directory.Packages.props)，csproj 仅 `PackageReference`（不带 Version）。
- `RoyalApps.RoyalTerminal.Avalonia` 传递依赖 `Terminal.Pty.Platform`、`Terminal.Transport.Ssh.Abstractions`、`Terminal.Transport.Ssh.SshNet` 等，Client 端无需单独引用 PTY 平台包（仅 Server 需要）。
- `AddSignalR` 由 `Microsoft.NET.Sdk.Web` 隐式 FrameworkReference 提供，Server 端无需额外 NuGet。
- **不引入** `RoyalApps.RoyalTerminal.Avalonia.App`（自带 MainWindow / 标签 / 标题栏，会与 RelaxKonOS 的 WindowManager 冲突）。
- **不引入** native Ghostty VT 包（`Terminal.Vt.Ghostty` + RID 相关原生资产），当前使用托管 VT 处理器。

### 2.2 嵌入而非替换 Shell

`TerminalControl` 作为普通 `UserControl` 内容塞进 `RemoteWindow`，与 Notepad / Settings 同构。但与 Notepad 不同的是，`TerminalControl` **在代码后台创建**（非 XAML），因为需要通过 9 参数构造函数注入自定义传输工厂：

```text
TerminalApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("Terminal", view)
    |
    WindowManager.Create → RemoteWindow
    |
    TerminalView (UserControl)
    ├── 工具条（Restart / Clear / 状态栏）
    └── TerminalHost (Grid)
        └── TerminalControl (code-behind 创建, 9-param ctor)
            └── TerminalTransportFactory = SignalRTransportFactory
```

### 2.3 TerminalControl 构造（9 参数构造函数）

`TerminalControl.TerminalTransportFactory` 是**只读属性**，只能在构造时注入。XAML 声明使用的是无参构造函数（创建默认 `CompositeTerminalTransportFactory`），无法在运行时替换。因此 `TerminalView.axaml.cs` 使用 9 参数构造函数：

```csharp
var control = new TerminalControl(
    new TerminalSessionService(),          // ITerminalSessionService
    new DefaultTerminalInputAdapter(),     // ITerminalInputAdapter
    new DefaultTerminalSelectionService(), // ITerminalSelectionService
    new DefaultTerminalScrollService(),    // ITerminalScrollService
    new DefaultVtProcessorFactory(),       // IVtProcessorFactory
    new DefaultPtyFactory(),               // IPtyFactory (Local Mode 回退用)
    new NullSshCredentialProvider(),       // ISshCredentialProvider (包内置, no-op)
    new KnownHostsSshHostKeyValidator(),   // ISshHostKeyValidator (包内置)
    transportFactory);                     // ITerminalTransportFactory — 我们的 SignalRTransportFactory
```

- 前两行服务与无参构造函数的默认值完全一致（镜像 `TerminalControl()` 源码）。
- `NullSshCredentialProvider` / `KnownHostsSshHostKeyValidator` 来自包传递依赖 `Terminal.Transport.Ssh.SshNet` / `Terminal.Transport.Ssh.Abstractions`，namespace 为 `RoyalTerminal.Terminal.Transport.Ssh` / `.SshNet`。SSH 传输在 RelaxKonOS 不使用（Remote Mode 走 SignalR），这两个 provider 仅满足构造函数签名。
- 构造后设置 `Focusable = true`、`Columns`、`Rows`、`ScrollbackLimit`、`TerminalFontSize`（原先在 XAML 中设置的属性）。

---

## 3. 架构：Remote Mode（SignalR）

### 3.1 数据流

```text
┌───────────── Client (RelaxKonOS.Client) ─────────────┐    ┌──────── Server (RelaxKonOS.Server) ────────┐
│                                                    │    │                                          │
│  TerminalControl                                   │    │  TerminalHub (Hub<ITerminalHubClient>)   │
│    ├── VT 处理器 (托管, client-side)               │    │    └── TerminalSessionManager (Singleton)│
│    ├── Skia 渲染                                   │    │         └── TerminalSession              │
│    └── TerminalTransportFactory                    │    │              └── IPty (ConPTY/forkpty)   │
│         └── SignalRTransportFactory                │    │  Start(req,sid)→ GetOrCreate+Attach      │
│              └── SignalRTerminalTransport          │    │                  （回放 1MB 缓冲快照）      │
│                   ├── HubConnection                │◄───┤  Input(data)  → session.Pty.Write(data) │
│                   │   OnOutput(byte[])  ◄──────────┤────┤  Resize(c,r)  → session.Pty.Resize(c,r) │
│                   │   OnProcessExited(int) ◄───────┤────┤  pty.DataReceived→缓冲+Clients.Caller   │
│                   │   Start(Input/Resize) ────────►│────┤  Close()      → manager.Remove (杀 PTY) │
│                   │   KillAsync→Close (手动终止) ──►│────┤  ListSessions()→ ListForUser           │
│                   │   StopAsync (不杀, 仅关连接)    │    │  OnDisconnected→ Detach (保留 PTY)      │
│                   │   [JWT via AccessTokenProvider]│    │                                          │
│                   └── DataReceived/ProcessExited   │    │  IPtyFactory (PlatformPtyFactory)       │
│                        (事件, 传给 TerminalControl)  │    │  IUserIdProvider (sub claim)            │
└────────────────────────────────────────────────────┘    └──────────────────────────────────────────┘
```

**核心设计：服务端是 PTY 哑中继 + 持久会话**。Server 端持有 PTY（与 Hub 连接解耦），只做：附加/创建会话、转发输入字节、回传输出字节（+1MB 环形缓冲供恢复）、手动终止、列表。VT 解析（标题/响铃/光标/颜色/滚动）全部在客户端的 `TerminalControl` 内完成。连接断开**不**杀 PTY，仅 detach；再次登录 `Start(Attach)` 回放缓冲快照重现历史。

### 3.2 Protocol 契约层

`Shared/RelaxKonOS.Protocol/Hubs/` 下定义了 Terminal Hub 的完整契约（零 PackageReference，DTO 用 sealed record + `[property: JsonPropertyName]`）：

| 文件 | 职责 |
|------|------|
| `ITerminalHubClient.cs` | server→client 接口：`OnOutput(byte[])`、`OnProcessExited(int)`。Server 端 `Hub<ITerminalHubClient>` 获编译期校验；Client 端 `HubConnection.On<T>` 注册回调 |
| `TerminalHubEvents.cs` | server→client 事件名常量（`nameof(ITerminalHubClient.OnOutput)` 等），Client 端 `HubConnection.On` 用 |
| `TerminalHubMethods.cs` | client→server invoke 方法名常量（`Start`/`Input`/`Resize`/`Close`） |
| `StartTerminalRequest.cs` | 启动终端请求 DTO（columns/rows/widthPixels/heightPixels/shell/workingDirectory） |

> **方法名对齐**：Server Hub 方法名必须与 `TerminalHubMethods` 常量完全一致。`Start` 方法在 Hub 上命名为 `Start`（非 `StartTerminal`），因为客户端 `InvokeAsync(TerminalHubMethods.Start)` 发送的是 `"Start"`。

### 3.3 传输层实现（Client 端）

| 文件 | 职责 |
|------|------|
| `SignalRTransportOptions.cs` | `ITerminalTransportOptions` 实现：HubUrl、Dimensions、TokenProvider/AccessToken、Shell/WorkingDirectory |
| `SignalRTerminalTransport.cs` | `ITerminalTransport` 实现：`HubConnection` 生命周期管理，桥接 `OnOutput`→`DataReceived`、`OnProcessExited`→`ProcessExited` |
| `SignalRTransportFactory.cs` | `ITerminalTransportFactory` 实现：对 `SignalRTransportOptions` 返回 SignalR 传输，其余（如 `PtyTransportOptions`）委托内部 `CompositeTerminalTransportFactory`（含 `PtyTerminalTransportProvider`，Local Mode 回退用） |

**`SignalRTransportFactory` 是自包含的**：在构造时创建内部 `CompositeTerminalTransportFactory([PtyTerminalTransportProvider()])`，无需从 `TerminalControl` 读取默认工厂。持有最近创建的 `SignalRTerminalTransport` 引用，供 `StopActiveAsync()` 主动关闭连接。

### 3.4 认证

SignalR 连接通过 `AccessTokenProvider` 携带 JWT：

```csharp
http.AccessTokenProvider = () =>
    Task.FromResult<string?>(opts.TokenProvider?.Invoke() ?? opts.AccessToken);
```

- `TokenProvider` 是 `Func<string?>`，每次连接/重连时调用以获取最新 token（从 `IAuthSession.Tokens.AccessToken` 读取）。
- Server 端 `TerminalHub` 标注 `[Authorize]`，JWT 验证由 `JwtBearer` 中间件处理（与 auth 端点同密钥/签发者）。
- 未认证时（`IAuthSession.State != Authenticated`），VM 回退到 `PtyTransportOptions`（Local Mode），不发起 SignalR 连接。

---

## 4. Server 端：TerminalHub

### 4.1 Hub 实现（持久会话哑中继）

`RelaxKonOS.Server/Hubs/TerminalHub.cs`：PTY 由 `TerminalSessionManager` 持有，与 Hub 连接解耦。

- **`Start(StartTerminalRequest req, string? sessionId = null)` → `AttachTerminalResponse`**：`manager.GetOrCreate(UserIdentifier, sessionId, req)` —— sessionId 命中且属于当前用户且未退出则**附加**（先回放缓冲快照），否则**新建** PTY 会话；`Context.Items["sid"]=sessionId`。返回 `{SessionId, Created}`。
- **`Input(byte[])`**：取 `Context.Items["sid"]` 对应会话 → `pty.Write(data)`。
- **`Resize(int, int, int, int)`**：对应会话 → `pty.Resize(cols, rows, ...)`。
- **`Close()`**：`manager.Remove(sid)` —— **手动终止**（杀 PTY 并从注册表移除）。对应客户端"断开"按钮 / 关闭终端窗口。
- **`ListSessions()`**：`manager.ListForUser(UserIdentifier)` —— 返回当前用户的全部终端会话摘要（多实例）。
- **`OnDisconnectedAsync(Exception?)`**：`session.Detach(Context.ConnectionId)` —— **仅 detach 当前连接，不终止 PTY**。网络掉线 / 桌面关闭 / 进程退出均走此路径，PTY 存活供再次登录恢复。

### 4.2 持久会话与环形缓冲

- `RelaxKonOS.Server.Terminal.TerminalSession`：持有 `IPty` + 1MB 环形缓冲（移植自参考项目 `visual-windows-server-master`）。`IPty.DataReceived` 始终把输出追加进缓冲（哪怕无人连接，ConPTY 读线程持续排空管道，shell 不阻塞），并在有附加连接时经 `IHubContext<TerminalHub, ITerminalHubClient>` 转发原始字节。`Attach` 时先把 `GetBufferSnapshot()` 经 `OnOutput` 回放 → 客户端重现历史输出。
- `RelaxKonOS.Server.Terminal.TerminalSessionManager`（Singleton）：`ConcurrentDictionary<string, TerminalSession>` 按 sessionId 索引、按 UserId 归属。`GetOrCreate` / `ListForUser` / `Remove`。子进程退出经 `ProcessExited` 回调自动出字典。
- `RelaxKonOS.Server.Terminal.TerminalUserIdProvider`（`IUserIdProvider`）：以 JWT `sub` claim 作为 `Context.UserIdentifier`，供按用户过滤。
- `IPtyFactory`（`PlatformPtyFactory`）：Windows 用 ConPTY，Linux 用 forkpty。
- 默认 shell：Windows `powershell`，Linux `bash`；环境继承宿主进程 + `TERM=xterm-256color`；工作目录未指定时用 `UserProfile`。

### 4.2.1 断开语义

| 客户端动作 | 客户端调用 | 服务端结果 | PTY |
|------------|-----------|-----------|-----|
| 关闭终端窗口（`Detach`，`IAuthSession.State` 仍 Authenticated） | `KillActiveAsync` → hub `Close` | `manager.Remove` | **终止** |
| "断开"按钮 | `KillActiveAsync` → hub `Close` | `manager.Remove` | **终止** |
| 切换会话 / 新建（`StopActiveAsync`，不调 Close） | `conn.StopAsync` | `OnDisconnected` → `Detach` | **存活** |
| 桌面关闭/登出（`LogoutAsync` 先把 State 置 Unauthenticated，再关窗 → `Detach` 走非 kill 分支） | `conn.StopAsync` | `OnDisconnected` → `Detach` | **存活** |
| 网络掉线 / 进程退出 / 崩溃 | （无 Close 调用） | `OnDisconnected` → `Detach` | **存活** |

> 关窗与桌面关闭的判据：`MainWindow.DisconnectAsync` 在 `Close()` 前先 `IAuthSession.LogoutAsync`（State→Unauthenticated）。`TerminalViewModel.Detach` 据此判断：`State == Authenticated` 表示仅关了终端窗口 → 杀；否则保留。

### 4.3 Server 注册

`RelaxKonOS.Server/Program.cs`：

```csharp
builder.Services.AddSingleton<IPtyFactory, RelaxKonOS.Server.Terminal.PlatformPtyFactory>();
builder.Services.AddSingleton<RelaxKonOS.Server.Terminal.TerminalSessionManager>();
builder.Services.AddSingleton<IUserIdProvider, RelaxKonOS.Server.Terminal.TerminalUserIdProvider>();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = null); // 大输出流不截断
// ...
app.MapHub<TerminalHub>("/hubs/terminals");
```

`MaximumReceiveMessageSize = null` 解除 SignalR 默认 32KB 消息上限，允许大块 PTY 输出（如 `cat` 大文件）与 1MB 缓冲快照单帧传输。

**JWT over SignalR**：`AddJwtBearer` 的 `Events.OnMessageReceived` 对 `/hubs/terminals` 路径从查询串 `access_token` 读取 token 注入 `context.Token`（.NET 客户端走 Authorization 头，query 兜底；修复 WebSocket 升级 401）。

### 4.4 恢复与多实例

- **恢复**：桌面 Window host 就绪后，`DesktopRestoreOrchestrator` 依次运行恢复参与者。`TerminalDesktopRestoreParticipant` 仅拉取该用户的存活会话；有会话才为各会话创建窗口并通过 `Start(Attach)` 回放 1MB 缓冲快照，无会话或服务不可达时不创建新终端。客户端随后由 `TerminalControl` 渲染历史输出，可继续输入。
- **多实例**：每用户可有多个终端会话（sessionId 索引）。进程内 `TerminalViewModel._openSessions`（静态 `ConcurrentDictionary`）记录本进程已开 sessionId，避免两个窗口附加同一会话。工具条 ComboBox 可切换、新建、断开。

---

## 5. 会话生命周期

| 时机 | 触发 | 动作 |
|------|------|------|
| 控件 Loaded | `TerminalView.OnLoaded` | `vm.AttachAsync` → 订阅事件 → `StartSessionAsync(initial:true)`：拉取会话列表 → 自动恢复或新建 |
| 关闭终端窗口 | `TerminalView.OnUnloaded` | `vm.Detach()`：若 `State==Authenticated` → `KillActiveAsync`（杀 PTY）；否则 `StopActiveAsync`（保留 PTY）→ `terminal.StopPty()` |
| 桌面关闭/登出 | `MainWindow.DisconnectAsync` | 先 `LogoutAsync`（State→Unauthenticated）→ `Close()` → 各终端 `Detach` 走非 kill 分支（PTY 存活） |
| 网络掉线/进程退出 | — | 客户端无 Close 调用；服务端 `OnDisconnected` → `Detach`（PTY 存活） |
| 进程退出 | `TerminalControl.ProcessExited` | 更新状态栏、置 `HasExited`；服务端会话自动出字典 |
| 切换会话 | `SwitchSessionCommand` | `StopActiveAsync`（不杀）→ `StartSessionAsync(sessionId)` |
| 新建 | `NewTerminalCommand` | `StopActiveAsync`（不杀）→ `StartSessionAsync(null)` |
| 断开按钮 | `DisconnectCommand` | `KillActiveAsync`（杀）→ 从本地列表移除 |
| Restart 按钮 | `RestartCommand` | `KillActiveAsync`（杀旧）→ `StartSessionAsync(null)` |
| Clear 按钮 | `ClearCommand` | `terminal.ClearScrollback()` |
| 窗口 resize | `TerminalControl` 内部自动同步 | transport `Resize()` → SignalR `Resize` → `pty.Resize()` |

### 5.1 模式选择（TerminalViewModel.StartSessionAsync）

```csharp
if (_session is { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } })
{
    // Remote Mode: 先拉取会话列表，自动恢复或新建
    var listOpts = BuildRemoteOptions(url, dimensions, sessionId: null);
    Sessions = (await TerminalHubConnection.ListSessionsAsync(listOpts)).ToArray();
    var resumeId = initial ? Sessions.Where(s => !s.HasExited && !_openSessions.ContainsKey(s.SessionId))
                                     .OrderByDescending(s => s.CreatedAt).FirstOrDefault()?.SessionId : sessionId;
    options = BuildRemoteOptions(url, dimensions, resumeId);  // SignalRTransportOptions，含 SessionId
}
else
{
    // Local Mode fallback: local PTY via inner composite factory
    options = new PtyTransportOptions(
        Command: null,
        WorkingDirectory: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment: null,
        Dimensions: dimensions);
}
```

### 5.2 关键 RoyalTerminal API

| 成员 | 说明 |
|------|------|
| `TerminalControl` 9-param ctor | 注入自定义 `ITerminalTransportFactory`（`TerminalTransportFactory` 只读） |
| `TerminalControl.StartSessionAsync(ITerminalTransportOptions, CancellationToken)` | 会话入口，路由到传输工厂 |
| `TerminalControl.StopPty()` | 停止 PTY / 关闭传输（清理用） |
| `TerminalControl.ClearScrollback()` | 清滚动历史 |
| `TerminalControl.ProcessExited` | `EventHandler<int>`，int = 退出码 |
| `TerminalControl.TitleChanged` | `EventHandler<string>`，shell OSC 0/2 标题 |
| `ITerminalTransport` | 传输抽象：`StartAsync`/`SendInput`/`Resize`/`StopAsync` + `DataReceived`/`ProcessExited` 事件 |
| `ITerminalTransportFactory.Create(ITerminalTransportOptions)` | 按 options 类型选择传输 |
| `PtyTransportOptions(Command, WorkingDirectory, Environment, Dimensions)` | Local Mode 选项 |
| `TerminalSessionDimensions(Columns, Rows, WidthPixels, HeightPixels)` | 初始网格尺寸 |

---

## 6. 输入焦点修复

`TerminalControl.Focusable` 默认为 `false`，导致控件无法获取键盘焦点、无法输入。修复方案：

1. **XAML → code-behind**：构造 `TerminalControl` 后设置 `control.Focusable = true`。
2. **初始聚焦**：`TerminalView.OnLoaded` 中 `FocusTerminal()`。
3. **点击重聚焦**：`TerminalControl.PointerPressed` → `Dispatcher.UIThread.Post(FocusTerminal)`（延迟到冒泡事件之后，避免 `RemoteWindow` 的 `WindowManager.Focus` 抢回焦点）。

> `RemoteWindow` 在每次 pointer press 时通过 `WindowManager.Focus` → `window.View.Focus()` 将自身置于最前。若不延迟聚焦，键盘焦点会落在 `RemoteWindow` 而非 `TerminalControl`。

---

## 7. 文件清单

### Client 端

| 文件 | 职责 |
|------|------|
| `Client/RelaxKonOS.Client/Apps/TerminalApp.cs` | `RemoteApplicationBase` 实现，Manifest + `Activate` 开窗，注入 `IAuthSession` |
| `Client/RelaxKonOS.Client/Apps/TerminalView.axaml` | UserControl，工具条（会话 ComboBox/切换/新建/断开/Restart/Clear）+ `TerminalHost` |
| `Client/RelaxKonOS.Client/Apps/TerminalView.axaml.cs` | code-behind 创建 `TerminalControl`（9-param ctor）+ 焦点管理 |
| `Client/RelaxKonOS.Client/Apps/TerminalViewModel.cs` | 会话状态、列表+恢复、断开语义、切换/新建/断开/Restart/Clear 命令 |
| `Client/RelaxKonOS.Client/Apps/SignalRTransportOptions.cs` | SignalR 传输选项 DTO（含 `SessionId`） |
| `Client/RelaxKonOS.Client/Apps/SignalRTerminalTransport.cs` | `ITerminalTransport` 的 SignalR 实现（`StartAsync` 附加、`StopAsync` 不杀、`KillAsync` 杀、`ListSessionsAsync`） |
| `Client/RelaxKonOS.Client/Apps/SignalRTransportFactory.cs` | 传输工厂：SignalR + 本地 PTY 回退；`StopActiveAsync`/`KillActiveAsync`/`CurrentSessionId` |
| `Client/RelaxKonOS.Client/Apps/TerminalHubConnection.cs` | 构建 `HubConnection` + 一次性连接拉取 `ListSessions` |

### Server 端

| 文件 | 职责 |
|------|------|
| `RelaxKonOS.Server/Hubs/TerminalHub.cs` | 持久会话 Hub（Start=attach/create, Input, Resize, Close=kill, ListSessions, OnDisconnected=detach） |
| `RelaxKonOS.Server/Terminal/TerminalSession.cs` | PTY + 1MB 环形缓冲 + attach/detach/kill |
| `RelaxKonOS.Server/Terminal/TerminalSessionManager.cs` | Singleton 注册表（sessionId 索引、userId 过滤） |
| `RelaxKonOS.Server/Terminal/TerminalUserIdProvider.cs` | `IUserIdProvider`（JWT sub claim） |
| `RelaxKonOS.Server/Terminal/ConPty.cs` / `PlatformPtyFactory.cs` | Windows ConPTY / Unix forkpty 工厂 |
| `RelaxKonOS.Server/Program.cs` | 注册 `IPtyFactory`/Manager/`IUserIdProvider` + `AddSignalR` + JwtBearer `OnMessageReceived` + `MapHub<TerminalHub>` |

### Protocol 契约层

| 文件 | 职责 |
|------|------|
| `Shared/RelaxKonOS.Protocol/Hubs/ITerminalHubClient.cs` | server→client 接口 |
| `Shared/RelaxKonOS.Protocol/Hubs/TerminalHubEvents.cs` | server→client 事件名常量 |
| `Shared/RelaxKonOS.Protocol/Hubs/TerminalHubMethods.cs` | client→server 方法名常量（Start/Input/Resize/Close/ListSessions） |
| `Shared/RelaxKonOS.Protocol/Hubs/StartTerminalRequest.cs` | 启动请求 DTO |
| `Shared/RelaxKonOS.Protocol/Hubs/AttachTerminalResponse.cs` | `Start` 返回值（SessionId + Created） |
| `Shared/RelaxKonOS.Protocol/Hubs/TerminalSessionInfo.cs` | 会话摘要 DTO（ListSessions 用） |

### 包管理

| 文件 | 职责 |
|------|------|
| `Directory.Packages.props` | `RoyalApps.RoyalTerminal.Avalonia` / `.Terminal.Pty.Platform` / `Microsoft.AspNetCore.SignalR.Client` 版本声明 |
| `Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj` | Client 引用 `RoyalApps.RoyalTerminal.Avalonia` + `Microsoft.AspNetCore.SignalR.Client` |
| `RelaxKonOS.Server/RelaxKonOS.Server.csproj` | Server 引用 `RoyalApps.RoyalTerminal.Terminal.Pty.Platform` |

---

## 8. AI Agent 规则

实现与维护终端模块时必须遵守：

- **Remote Mode 是默认模式**：认证后（`IAuthSession.State == Authenticated`）自动走 SignalR 远端 PTY。Local Mode 仅作未登录时的 dev 回退，不得作为正式运行模式。
- **传输用 SignalR，禁止裸 WebSocket**：RoyalTerminal 传输抽象（`ITerminalTransport`）是传输方式无关的，本项目选择 SignalR（JWT + 强类型 Hub + 一次性连接拉取列表）。禁止为终端单独引入裸 WebSocket 端点。**不启用 `WithAutomaticReconnect`**（自动重连后服务端不会自动重新附加会话，进入半附加状态）；恢复路径是"再次登录打开终端"→重新 `Start(Attach)`→服务端回放缓冲快照。
- **`TerminalControl` 在 code-behind 创建（9-param ctor）**：`TerminalTransportFactory` 是只读属性，只能通过构造函数注入 `SignalRTransportFactory`。禁止在 XAML 中声明 `TerminalControl` 后尝试运行时替换传输工厂。
- **服务端是 PTY 哑中继 + 持久会话**：`TerminalHub` 只做附加/输入/输出/退出/尺寸/手动终止/列表，**不做 VT 解析**。PTY 由 `TerminalSessionManager` 持有，与 Hub 连接解耦。VT 渲染（标题/响铃/光标/颜色）全部在客户端 `TerminalControl` 完成。服务端不得引入 VT 处理器。
- **Hub 方法名必须与 `TerminalHubMethods` 常量一致**：Server Hub 方法 `Start`/`Input`/`Resize`/`Close`/`ListSessions` 必须与 `TerminalHubMethods` 中的常量值（`nameof`）完全匹配，否则 SignalR 运行时找不到方法。
- **连接断开仅 detach，保留 PTY**：`TerminalHub.OnDisconnectedAsync` 必须调用 `session.Detach(Context.ConnectionId)`，**禁止**在断开时杀 PTY。只有显式 `Close`（客户端"断开"按钮 / 关闭终端窗口）才 `manager.Remove` 杀 PTY。这是"再次登录恢复原桌面"的前提。
- **断开语义判据**：客户端 `TerminalViewModel.Detach` 按 `IAuthSession.State` 判断——`Authenticated` 表示仅关了终端窗口 → `KillActiveAsync`（杀）；`Unauthenticated`（桌面登出/关闭中）→ `StopActiveAsync`（保留）。网络掉线/崩溃不触发 `Detach`，服务端 `OnDisconnected` 保留 PTY。
- **JWT 鉴权 + query 兜底**：`TerminalHub` 标注 `[Authorize]`；Client 端通过 `AccessTokenProvider` 从 `IAuthSession.Tokens.AccessToken` 取 token；Server `AddJwtBearer` 的 `OnMessageReceived` 对 `/hubs/terminals` 从查询串 `access_token` 读 token（修复 WebSocket 升级 401）。禁止未认证连接。
- **按用户索引会话**：`TerminalUserIdProvider`（`IUserIdProvider`）以 JWT `sub` claim 作 `Context.UserIdentifier`；会话按 sessionId 索引、按 userId 归属过滤。多实例。
- **嵌入 `TerminalControl`，禁止引入 `RoyalApps.RoyalTerminal.Avalonia.App`**：RelaxKonOS 自有 WindowManager / RemoteWindow / Taskbar，不能被 RoyalTerminal 的外壳接管。
- **焦点修复不可移除**：`TerminalControl.Focusable = true`（code-behind 设置）+ `PointerPressed` 延迟聚焦是输入正常的前提。`RemoteWindow` 的 `WindowManager.Focus` 会抢回焦点，必须用 `Dispatcher.UIThread.Post` 延迟。
- **会话清理走 View 的 `OnUnloaded`**：SDK 无 `Deactivate` 钩子；`RemoteWindow` 关闭 → 控件离开视觉树 → `Detach()` → 按断开语义 `KillActiveAsync`/`StopActiveAsync` + `StopPty()`。
- **不引入 native Ghostty VT**（除非显式需求）：当前用托管 VT；引入 native 需按 RID 处理原生资产包。
- **程序集名是 `RoyalTerminal.Avalonia`**（非 NuGet 包 id `RoyalApps.RoyalTerminal.Avalonia`）；`NullSshCredentialProvider` 在 `RoyalTerminal.Terminal.Transport.Ssh.SshNet` namespace，`KnownHostsSshHostKeyValidator` 在 `RoyalTerminal.Terminal.Transport.Ssh` namespace（abstractions 程序集，传递依赖可用）。
