# RemoteOS Login 模块设计文档

> 本文档定义 RemoteOS 登录模块：客户端登录窗口（mstsc 风格）、传输层、认证会话、服务端 auth 端点、JWT、IIdentityProvider 抽象及登录防护。
>
> 本文档描述**已实现的登录模块**：客户端可真实登录到本机 Server 并进入桌面。不含 SignalR Hub（桌面状态同步是独立模块）。
>
> - 通信契约见 [`RemoteOS.Protocol.md`](../architecture/RemoteOS.Protocol.md)
> - 身份模型与认证原则见 [`RemoteOS.Authentication.md`](./RemoteOS.Authentication.md)
> - Workspace / Session / Device 模型见 [`RemoteOS.Workspace.md`](../architecture/RemoteOS.Workspace.md)
> - 登录成功后的桌面外壳（连接栏、断开 = 登出）见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md)
> - 整体架构见 [`RemoteOS.Architecture.md`](../architecture/RemoteOS.Architecture.md)

---

## 1. 模块定位

### 1.1 参考 Windows mstsc

RemoteOS 登录模块参考 Windows Server 远程桌面连接工具 **mstsc** 的交互范式：启动先弹独立登录窗口，输入"地址 + 用户名 + 密码"→ 连接成功后进入桌面。

|  | mstsc | RemoteOS Login |
|---|---|---|
| 登录窗口 | 独立"远程桌面连接"窗口 | 独立 `LoginWindow`（顶层 Avalonia Window） |
| 输入项 | 计算机 + 用户名 + 密码 | Server URL + 用户名 + 密码 |
| 传输协议 | RDP（像素流） | HTTP REST（状态同步，非像素流） |
| 认证后 | 全屏远程桌面 | MainWindow 桌面 Shell |
| 凭据存储 | 默认不保存（可选 .rdp） | 连接列表保存在当前 OS 用户范围；密码可选加密保存：Windows DPAPI / macOS Keychain / Linux Secret Service |

### 登录前语言

登录窗口提供显示语言选择器。未认证时，选择写入本机的 `LocalApplicationData/RemoteOS/client-language.json`，因此下次启动仍使用该语言；该文件只保存 BCP-47 语言标识，不含凭据或令牌。认证成功后，客户端加载当前 user-workspace 的 `WorkspacePreferencesDto.Language`，并以服务器偏好作为桌面和内置应用的语言来源。注销后恢复本机登录语言。

### 1.2 实现范围

**已实现**：

- 客户端：`LoginWindow` + `LoginView` + `LoginViewModel` + `IRemoteOsClient`（typed HttpClient）+ `IAuthSession`（可选记住设备）+ 统一 bearer 自动刷新/401 单次重试 + 启动分叉
- 服务端：`/api/v1.0/auth/login|refresh|logout|me` 端点 + JWT 签发 + `IIdentityProvider` 抽象 + `WindowsLogonProvider`（LogonUser）+ `LinuxPamProvider`（PAM 认证与账户检查、NSS 用户信息）+ 登录端点限流、账号/IP/账号+IP 递增冷却，以及 SQLite 持久化仓储
- 协议：零改动（复用 Protocol 已有的 `LoginRequest`/`LoginResponse`/`AuthTokens`/`AuthApiRoutes`/`ProblemDetails`）

**非范围（未来扩展）**：

- SignalR Hub `/hubs/workspace`（桌面状态增量同步，独立模块）
- 登录后的进程级用户切换（当前服务端能力仍以服务进程身份运行）

---

## 2. 登录流程时序

```text
Client (LoginViewModel)         Server (AuthEndpoints)         Host OS (LogonUser/PAM)
    |  POST /api/v1.0/auth/login       |                               |
    |  { LoginRequest }              |                               |
    |------------------------------>>|                               |
    |                                | IIdentityProvider.Verify(u,p) |
    |                                |------------------------------>>|
    |                                |<<------------------------------|
    |                                |   CredentialVerifyResult      |
    |                                |                               |
    |                                | 查/建 User, Workspace         |
    |                                | 查/建 Device, 新建 Session    |
    |                                | JwtTokenService.Issue         |
    |                                |   claims: sub/name/           |
    |                                |    workspace_id/device_id/    |
    |                                |    role/jti                   |
    |<<------------------------------|                               |
    |  200 LoginResponse             |                               |
    |   { User, Workspace,           |                               |
    |     Session, Device,           |                               |
    |     Tokens, AssignedRole }     |                               |
    |                                |                               |
    IAuthSession.State = Authenticated
    App 关闭 LoginWindow，打开 MainWindow（桌面）
```

失败路径：Server 返回 RFC 7807 `ProblemDetails`（错误码在 `type` URI），Client `RemoteOsClient` 抛 `RemoteOsAuthException`，`LoginViewModel` 按 `type` 映射本地化文案显示在登录窗口。

---

## 3. 客户端架构

### 3.1 启动分叉

```text
App.OnFrameworkInitializationCompleted
    |
    Bootstrapper.Build(this)  → IServiceProvider（含 IAuthSession / IRemoteOsClient / LoginViewModel / DesktopShellViewModel）
    |
    desktop.ShutdownMode = OnExplicitShutdown
    |
    new LoginWindow { DataContext = LoginViewModel }
    desktop.MainWindow = loginWindow; loginWindow.Show()
    |
    session.StateChanged += handler
        |
        State == Authenticated?
            |
            Dispatcher.UIThread.Post:
                new MainWindow { DataContext = DesktopShellViewModel }
                desktop.MainWindow = mainWindow; mainWindow.Show()
                loginWindow.Close()
                mainWindow.Closed → desktop.Shutdown()
```

`OnExplicitShutdown` 防止登录窗→桌面切换时进程提前退出。`MainWindow` 与 `DesktopShellViewModel` 在登录成功后才实例化，登录前不占用桌面资源。

### 3.2 组件关系

```text
LoginWindow (顶层 Window)
  └── LoginView (UserControl, x:DataType=LoginViewModel)
        └── LoginViewModel (CommunityToolkit.Mvvm)
              ├── [ObservableProperty] ServerUrl / Username / Password / IsConnecting / StatusMessage / ErrorMessage / HasError
              ├── [RelayCommand] ConnectCommand → ConnectAsync
              └── IAuthSession (DI 注入)

IAuthSession (AuthSession, 单例；可选记住设备)
  ├── State (Unauthenticated / Connecting / Authenticated)
  ├── ServerUrl / Tokens / CurrentUser / CurrentWorkspace / CurrentSession / CurrentDevice / AssignedRole
  ├── event StateChanged
  ├── LoginAsync(serverUrl, LoginRequest)
  ├── LogoutAsync()
  └── RefreshAsync()

IRemoteOsClient (RemoteOsClient, typed HttpClient)
  ├── LoginAsync(serverUrl, request)   → POST /api/v1.0/auth/login
  ├── RefreshAsync(serverUrl, refresh) → POST /api/v1.0/auth/refresh
  ├── LogoutAsync(serverUrl, access, refresh?) → POST /api/v1.0/auth/logout
  └── GetMeAsync(serverUrl, access)    → GET  /api/v1.0/auth/me
```

### 3.3 启动状态机

```text
Unauthenticated ──Connect──>> Connecting ──成功──>> Authenticated
                                │                       │
                                失败                     Logout
                                ↓                       ↓
                            Unauthenticated <<────── Unauthenticated
```

### 3.4 关键约束

- **不 mutate `HttpClient.BaseAddress`**：`RemoteOsClient` 每个方法接收 `serverUrl` 构造绝对 URI（`new Uri(new Uri(serverUrl), route.TrimStart('/'))`），避免 typed HttpClient 共享实例并发竞态。
- **登录窗用顶层 `Window`**，不用 `RemoteWindow`（`RemoteWindow` 必须挂在 `DesktopShellView` 的 `PART_WindowHost` Canvas，登录前桌面尚未建立）。
- **已保存连接**：客户端可保存多组 `Server URL + 用户名`，并可选择通过平台安全存储加密保存密码；登录窗保持可见，用户从下拉列表选择任意已保存项以回填凭据，再明确点击“连接”登录。不会保存 `RefreshToken` 或任何可替代密码的令牌；登出不会删除已保存连接。Linux 即使 Secret Service 暂不可用，也会保留服务器和用户名，只禁用该记录的免密码登录。
- **HTTP 调用经 `IRemoteOsClient` 抽象**，业务代码不直接 `new HttpClient`（Architecture.md §4.8）。

---

## 4. 服务端架构

### 4.1 端点总览

| 方法 | 路由 | 认证 | 说明 |
|---|---|---|---|
| POST | `/api/v1.0/auth/login` | 无 | 先限流与风险检查，再验证凭据并签发 JWT；受限时返回 429 + `Retry-After` |
| POST | `/api/v1.0/auth/refresh` | 无 | RefreshToken 换新令牌对（旧 refresh 作废） |
| POST | `/api/v1.0/auth/logout` | JWT | 吊销 RefreshToken |
| GET | `/api/v1.0/auth/me` | JWT | 返回当前 `UserDto` |

### 4.2 IIdentityProvider 抽象

```text
IIdentityProvider
    |
    ├── WindowsLogonProvider  (advapi32!LogonUser, [SupportedOSPlatform("windows")])
    └── LinuxPamProvider      (libpam 认证/账户检查 + NSS 用户信息)
```

- `Verify(username, password) → CredentialVerifyResult`：委托宿主 OS 验证，9 种错误码映射（BadCredentials/NoSuchUser/AccountDisabled/AccountLockedOut/PasswordExpired/AccountExpired/AccountRestriction/InvalidInput/Unknown）。
- `GetUserInfo(username) → PlatformUserInfo`：返回 Uid（Windows: `domain\user`）/ DisplayName / HomeDirectory?。
- **平台选择**：`Program.cs` 按 `OperatingSystem.IsWindows/IsLinux` 注册对应 Provider，其他服务端宿主平台在启动时明确拒绝。
- **服务器不存储密码**：认证仍完全委托宿主 OS（Authentication.md §17）；客户端仅在用户勾选自动登录后，才将密码交给操作系统安全存储。

`WindowsLogonProvider` 从 `Windows Server Test/Categories/Authentication/WindowsCredentialVerifier.cs` 迁移而来（已验证 LogonUser 可行）；`Windows Server Test` 项目改为引用 Server 调 `IIdentityProvider`，单一真源。

### 4.3 JWT 签发

```text
JwtTokenService.Issue(user, workspace, device, role)
    |
    AccessToken (15min, HMACSHA256)
      claims: sub=userId, name=username, workspace_id, device_id, role, jti=tokenId
    |
    RefreshToken (7d idle, 30d absolute, 随机 32 字节)
      仅保存在 Client/Server 当前进程内，登记到 AuthSessionStore
```

- AccessToken：REST/Hub 鉴权，`Authorization: Bearer <token>`。
- RefreshToken：换新用，一次性（原子消费后签发后继 token）；**不会**持久化到客户端或服务端数据库。
- `AuthSessionStore`：单例 `ConcurrentDictionary`，支持原子消费/吊销，并定期清理过期项；服务端重启会令当前 refresh token 失效。
- 密钥：`appsettings.json` `Jwt:Secret`（≥32 字符），Production 启动校验非默认值。

### 4.4 内存仓储与领域模型

```text
Server.Domain.{User, Workspace, Session, Device}   (领域模型, ToDto() 映射)
    |
Server.Storage.{IUserRepository, IWorkspaceRepository, ISessionRepository, IDeviceRepository}
    |
InMemory*Repository (Singleton, ConcurrentDictionary, 重启丢失)
```

- **领域模型 vs Protocol DTO 分离**：Server 内部用领域模型，端点处手动 `ToDto()` 映射（Protocol 不污染领域逻辑）。
- **User**：按 (username, platform) 索引，One User。
- **Workspace**：按 UserId 索引，One User One Persistent（复用，不存在则建）。
- **Device**：按 (name, platform) 索引，复用，更新 LastLoginAt/ClientVersion。
- **Session**：每次登录新建（Session ≠ Workspace）。

### 4.5 login 端点流程

```text
1. HTTP 入口按源 IP Token Bucket 限流（默认每 IP 10 次/分钟）→ 429 + Retry-After
2. 检查账号、IP、账号+IP 三个风险计数器与冷却时间 → 429 + Retry-After
3. 参数校验（空字段）→ 400 invalid-input
4. IIdentityProvider.Verify；失败统一返回 401 invalid-credential，并记录风险事件
5. 成功后清除该账号的冷却状态、记录成功事件
6. GetUserInfo → 查/建 User、Workspace、Device、Session
7. 设 Workspace Controller（Grace Period 5min，见 Workspace.md §19）并签发 JWT
8. 返回 LoginResponse(user, workspace, session, device, tokens, role)
```

### 4.6 登录防暴力破解与反向代理

`LoginProtectionService` 将同一次失败同时计入账号、IP 和账号+IP：账号维度防代理池攻击，IP 维度防 password spraying，账号+IP 维度提供递增冷却。账号失败状态及不含秘密的 `authentication_security_events` 记录写入 SQLite，服务重启后不会清空；IP 和账号+IP 是短期内存状态，避免把不受控攻击 IP 永久写入数据库。

默认值位于 `appsettings.json` 的 `AuthenticationSecurity`：入口 10 次/分钟；5 分钟内单 IP 30 次失败后阻止 5 分钟；账号连续第 5/6/7/8/9/10 次失败的冷却为约 2 秒/5 秒/15 秒/30 秒/1 分钟/5 分钟，之后最高 1 小时。管理员可调整这些数值，但不应把它们替换为“多次失败永久锁定”，以免攻击者借此锁死管理员。

服务端默认忽略 `X-Forwarded-For`。若部署在 Nginx、Caddy 或 IIS 后，必须仅在 `AuthenticationSecurity:TrustedProxies` 中列出反向代理的直连 IP，或在 `TrustedNetworks` 中列出其受控 CIDR；只有这些代理的请求才会解析一个转发地址。不要将公网客户端网段或不受控代理放入此列表。

---

## 5. 错误处理矩阵

`ProblemDetails`（Protocol.Common）只有 `type/title/status/detail/traceId` 五字段，**无 Errors 字典**。错误码通过 RFC 7807 的 `type` URI 传递，客户端按 `type` 字符串映射本地化文案。`type` 前缀统一 `https://remoteos.app/problems/`。

| 场景 | HTTP | ProblemDetails.type | UI 文案 |
|---|---|---|---|
| 网络不可达/拒绝连接 | — | — | "无法连接到服务器：{host}" |
| 凭据错误/用户不存在 | 401 | `.../invalid-credential` | "用户名或密码错误" |
| 入口或风险冷却限制 | 429 | `.../login-rate-limited` 或限流中间件响应 | "登录尝试过于频繁，请稍后重试"；读取 `Retry-After` 后倒计时禁用按钮 |
| 账户锁定 | 423 | `.../account-locked` | "账户已锁定，请联系管理员" |
| 账户禁用 | 403 | `.../account-disabled` | "账户已禁用" |
| 密码过期 | 403 | `.../password-expired` | "密码已过期，请先在服务器上修改" |
| 账户过期 | 403 | `.../account-expired` | "账户已过期" |
| 账户受限 | 403 | `.../account-restriction` | "账户登录受限" |
| 输入为空 | 400 | `.../invalid-input` | "请填写完整信息" |
| 服务器内部错误 | 500 | `.../auth-failed` | "登录失败，请稍后重试" |

`CredentialError → ProblemDetails` 映射在 `AuthEndpoints.MapCredentialErrorToProblem`；`type → UI 文案` 映射在 `LoginViewModel.MapProblemToMessage`。

---

## 6. 安全考量

- **服务器不存储宿主 OS 密码**：认证委托宿主 OS（LogonUser/PAM），服务端密码仅在校验瞬间传入，不落库、不日志。由于宿主 Provider 可能区分用户不存在、密码错误或账户状态，`login` 对外一律对验证失败返回相同的 `401 invalid-credential`，避免用户名枚举。
- **自动登录的安全性**：未勾选“记住”时 `AuthSession` 不写盘。密码仅通过平台凭据库保存：Windows 为当前用户 DPAPI 加密文件，macOS 为 Keychain，Linux 为 Secret Service。Linux 的非敏感连接元数据另存于当前用户数据目录，目录权限 `0700`、文件权限 `0600`，其中密码始终为 `null`。密码不会写入普通配置、数据库或日志。
- **JWT 对称密钥**：Production 启动校验 `Jwt:Secret` 非默认占位值；Development 用固定开发密钥。
- **HTTPS**：Production 强制 HTTPS 重定向；Development 允许 http 方便本地测试。
- **密码字段**：`LoginView` 默认用 `TextBox PasswordChar="●"` 掩码显示，并提供“查看 / 隐藏”切换；`LoginViewModel` 不记录密码到日志。
- **RefreshToken 一次性**：刷新后旧 token 立即吊销，登出吊销 refresh。
- **最小权限**：`WindowsLogonProvider` 用 `LOGON32_LOGON_NETWORK`（轻量登录，不加载用户配置），不默认 root/Administrator。

---

## 7. Token 生命周期

```text
登录 → AccessToken(15min) + RefreshToken(7d idle / 30d absolute)
          |
          每个受保护 API 请求 → 到期前自动 RefreshAsync；收到 401 时刷新并单次重试
          |
          AccessToken 过期 → 新 AccessToken + 新 RefreshToken（旧 refresh 原子消费）
          |
          RefreshToken 过期/失效 → 回 LoginWindow；若用户已保存密码则自动回填，用户确认后重新登录
          系统凭据未保存、被删除或密码已变更 → 在 LoginWindow 手动重新登录
          |
          登出 → LogoutAsync → 吊销 RefreshToken（AccessToken 自然过期）
```

- AccessToken TTL：15 分钟（`Jwt:AccessTokenTtl`）。
- RefreshToken idle TTL：7 天（`Jwt:RefreshTokenTtl`）；绝对会话上限：30 天（`Jwt:RefreshTokenMaximumLifetime`）。每次刷新延长 idle TTL，但不会超过绝对上限。
- refresh token 不写入本地凭据库，故它只服务于持续运行的客户端会话；客户端退出或服务端重启后，用户可用已选择保存的系统密码再次登录。
- 只有 refresh 明确返回 401（过期/吊销/服务端重启）才会 `AuthSession.Reset()` 并回到登录窗；网络故障不会错误清空会话。
- **桌面外壳衔接**：登录后 `MainWindow` 的 mstsc 连接栏"关闭连接"与标题栏"关闭"均触发 `IAuthSession.LogoutAsync()` 后 `MainWindow.Close()`（当前阶段断开即退出进程，不回登录窗），详见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md) §2.4。

---

## 8. 当前边界与未来扩展

| 能力 | 当前 | 未来 |
|---|---|---|
| SignalR Hub `/hubs/workspace` | 不含 | 独立模块，登录后建立 Hub 连接（携带 JWT） |
| Linux PAM | 已实现 | P/Invoke `libpam.so.0`：`pam_authenticate` + `pam_acct_mgmt`；NSS `getpwnam_r` 获取 UID/显示名/Home |
| 显示选项折叠面板 | 预留空 Expander | 显示大小/颜色深度/本地资源（对标 mstsc） |
| 已保存连接列表 | 已实现：按 Server URL + 用户名保存多条记录；有已保存密码时可在简洁选择模式下免密码连接 | 仅扩展 mstsc 风格显示选项（显示大小/颜色深度/本地资源） |
| 自动登录凭据 | Windows DPAPI / macOS Keychain / Linux Secret Service（勾选后启用） | Linux 桌面密钥环不可用时仅密码不保存；服务器/用户名列表仍持久化，可正常手动登录 |
| 持久化仓储 | SQLite + EF Core（User/Workspace/Device/Bookmark/HistoryEntry），Session/刷新令牌内存 | 全量持久化按需扩展 |
| 多设备控制权竞争 | 单设备 = Controller | Observer/Request Control 弹窗（Workspace.md §21） |
| Token 自动刷新拦截 | 统一 `AuthenticatedHttpHandler` | 到期前刷新 + 401 后单次刷新并重试；并发请求共用一次 refresh |

Linux 服务端部署要求系统提供 PAM 运行库（Ubuntu 的 `libpam0g`，通常由基础系统预装）以及可用的 `login` PAM service。RemoteOS 不读取 `/etc/shadow`，也不保存或记录登录请求中的密码；PAM conversation 仅在同步认证调用期间持有凭据。账户信息通过 NSS 查询，因此系统配置的 SSSD/LDAP 用户同样可被解析。

---

## 9. AI Agent 理解规则

实现 RemoteOS 登录模块时必须遵守：

**必须**：

- 启动顺序：先 `LoginWindow`，登录成功后才创建 `MainWindow`。
- 登录窗口用顶层 `Window`，不用 `RemoteWindow`（RemoteWindow 必须挂在 DesktopShellView 内）。
- HTTP 调用经 `IRemoteOsClient` 抽象，业务代码不直接 `new HttpClient`。
- 凭据验证经 `IIdentityProvider` 抽象，平台差异封装在 Provider 实现。
- 错误响应解析 `ProblemDetails`，用 `type` 字段做错误码映射（无 Errors 字典）。
- 未勾选“记住此计算机和用户名”时，不新增本地记录；勾选后保存 `Server URL + 用户名`。只有额外勾选“加密保存密码”时才将密码写入操作系统安全存储；不保存 RefreshToken，且启动后由用户选择目标服务器，避免在多服务器环境中错误地自动连到上一台机器。
- Server 领域模型与 Protocol DTO 分离，端点处手动 `ToDto()` 映射。
- 序列化统一用 `RemoteOsJsonOptions.Default`（camelCase + 枚举字符串）。

**禁止**：

- 在 Protocol 引入 `Microsoft.AspNetCore.*` 或 `HttpClient` 包。
- 把 `IIdentityProvider` / `CredentialVerifyResult` 放进 Protocol。
- 把密码写入日志、配置、数据库，或操作系统安全存储以外的任意文件。
- 在登录窗阶段引入 SignalR 连接（Hub 是独立模块）。
- mutate 共享 `HttpClient.BaseAddress`。
- 用 `RemoteWindow` 承载登录界面。
- 破坏现有桌面功能（MainWindow / Bootstrapper 桌面装配 / DesktopShellViewModel / 内置 App / WindowManager 仅在 App.axaml.cs 启动顺序分叉，逻辑不动）。

---

## 10. 已保存连接（mstsc 风格，多服务器）

登录窗口的“计算机”输入框同时是已保存连接的下拉列表。RemoteOS 按 **Server URL + 用户名** 保存多条独立记录；因此同一台服务器可使用不同账户，多台服务器也不会互相覆盖。

启动时如果存在至少一条已保存密码的记录，窗口进入简洁选择模式：显示服务器下拉框和用户名，隐藏密码标签与输入框。窗口默认选中最近使用的服务器，并回填服务器、用户名及（不可见的）已保存密码；用户仍需点击“连接”才会登录。选择“显示选项”后展开服务器、用户名和密码字段，并按当前选择项预填，便于核对或修改；“显示选项 / 隐藏选项”始终可切换。没有任何已保存密码时，窗口默认展开完整表单。

服务器下拉项和可编辑选择文本均只显示 Server URL。客户端在发送请求前验证 URL 必须为带 `http` 或 `https` 协议的完整地址；无效地址会显示错误提示，不会导致客户端异常退出。展开的密码框提供“查看 / 隐藏”按钮，用户可确认输入内容。

| 用户选择 | 本地保存内容 | 下次使用方式 |
|---|---|---|
| 勾选“记住此计算机和用户名”，不勾选保存密码 | Server URL、用户名 | 选择该项后会回填地址和用户名，仍需输入密码。 |
| 同时勾选“加密保存密码” | Server URL、用户名、密码 | 选择该项会回填凭据；用户点击“连接”后免密码登录。 |
| 不勾选“记住此计算机和用户名” | 不新增或更新本地记录 | 本次登录结束后不保留新输入。 |

密码不是普通配置：只有用户显式勾选后才会写入当前操作系统的安全存储（Windows DPAPI、macOS Keychain、Linux Secret Service），不会写入服务端、连接元数据文件、日志或数据库。未保存密码的记录也不保存 RefreshToken，不能绕过密码输入。

Linux 客户端将连接元数据写入 `${XDG_DATA_HOME:-~/.local/share}/RemoteOS/remembered-connections.json`（实际根目录由 .NET `LocalApplicationData` 解析）。文件仅含服务器地址、用户名、最后使用时间及空密码字段，父目录强制为 `0700`，文件强制为 `0600`。密码单独保存在桌面 Secret Service（如 GNOME Keyring/KWallet 提供的 Freedesktop Secret Service）中。启动时两层数据按服务器地址和用户名合并；Secret Service 不存在、未启动或未解锁时，连接列表仍可读取，但不会回填密码。旧版本只存在 Secret Service 中的聚合记录仍可读取，并会在下次成功保存时生成连接元数据文件。

旧版本的单条加密会话在首次读取时会迁移为一条新的连接记录。若保存的密码已失效，客户端会保留该服务器和用户名，但移除失效密码并提示用户重新输入；网络暂时不可达不会删除任何已保存连接。退出远程桌面只会注销当前会话，不会清除本地已保存连接，行为与 mstsc 的已保存凭据一致。
