# RelaxKonOS Storage 持久化设计文档

> 本文档定义 RelaxKonOS.Server 的持久化存储方案：技术选型、持久化范围、表结构、仓储层、建库策略、配置项，以及与登录流程的交互。
>
> 本文档针对「配置 + 身份」这一组持久实体落地（User / Workspace / Device / AppSettings / ImageMirrors，含终端外观配置 TerminalSettings），让终端与应用私有配置等服务端状态跨重启保留。
>
> - 用户/Workspace 模型见 [`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md)
> - 登录与身份见 [`RelaxKonOS.Authentication.md`](./RelaxKonOS.Authentication.md) / [`RelaxKonOS.Login.md`](./RelaxKonOS.Login.md)
> - 服务端整体见 [`RelaxKonOS.md`](../README.md) §4.9

---

## 1. 背景

`RelaxKonOS.Server` 此前所有仓储均为内存实现（`InMemory*Repository`，Singleton，`ConcurrentDictionary`），重启即丢。其中**终端外观配置**（`TerminalSettingsDto`：FontFamily / FontSize / ColorScheme / Background/Foreground/CursorColor）作为 [Workspace](../../RelaxKonOS.Server/Domain/Workspace.cs) 的属性存在内存中，经 `GET/PUT /api/v1.0/workspaces/{id}/terminal-settings` 读写——用户改完配置重启服务就丢失。

而 Workspace 在 [`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md) §22/§23 中被明确定义为 **「One Persistent Workspace」**。本次引入 SQLite 持久化层，先把「配置 + 身份」这一组持久实体落地，让终端配置跨重启保留，并为后续 Storage / 同步能力奠基。

---

## 2. 设计目标

- **配置不丢**：终端外观配置（TerminalSettings）随 Workspace 持久化，重启后恢复。
- **身份连续**：User / Device 记录跨重启保留，登录命中既有记录而非每次重建（保留 CreatedAt / LastLoginAt 历史）。
- **零业务侵入**：保留现有 `IUserRepository` / `IWorkspaceRepository` / `IDeviceRepository` 接口，仅新增 EF 实现 + DI 切换；端点与领域模型不改。
- **可回退**：开发期可通过配置切回内存仓储（`Provider=memory`）。
- **可演进**：表结构由 EF Core 模型配置定义，未来可平滑切换到 Migrations。

---

## 3. 技术选型

**EF Core + SQLite Provider**（`Microsoft.EntityFrameworkCore.Sqlite` 10.0.10）。

| 维度 | 选择 | 理由 |
|------|------|------|
| 数据库 | SQLite | 单文件本地库，零运维，契合单服务器/小团队场景；跨平台（Ubuntu / Windows Server） |
| 数据访问 | EF Core | 规范模型配置、Migrations 可演进、强类型 DbSet；与 .NET 生态主流一致 |
| 配置列存储 | `OwnsOne + ToJson` | TerminalSettings（6 字段 record）序列化为单列 JSON 文本，配置可演进——新增外观字段无需改 schema |
| 枚举存储 | `HasConversion<string>()` | 与线协议（camelCase 字符串）对齐，可读性好 |

> 领域模型 `User` / `Workspace` / `Device` 为公开 setter + 无参构造的 plain class，EF 友好，无需改造。`TerminalSettingsDto` 为 sealed record，由 EF 的 JSON 列映射经 System.Text.Json 序列化/反序列化（record 主构造支持）。

### 包（中心化包管理）

[`Directory.Packages.props`](../../Directory.Packages.props) 新增：
- `Microsoft.EntityFrameworkCore.Sqlite` 10.0.10（含 `Microsoft.Data.Sqlite`）
- `Microsoft.EntityFrameworkCore.Design` 10.0.10（Design-time，`PrivateAssets=all`）

---

## 4. 持久化范围（关键决策）

并非所有运行时状态都应持久化。按实体语义划分：

| 实体 | 是否持久化 | 理由 |
|------|-----------|------|
| **User** | ✅ SQLite | 登录 `FindByUsername` 命中后复用；若不持久化，重启后 User.Id 变化 → `FindByUserId` 找不到旧 Workspace → TerminalSettings 成孤儿丢失。**必须与 Workspace 配套**。 |
| **Workspace**（含 TerminalSettings / BrowserSettings / Preferences / WindowLayouts / DesktopDisplay / ThemePreferences） | ✅ SQLite | 系统级、Workspace 语义强的配置均以 JSON 列随 Workspace 持久。 |
| **Device** | ✅ SQLite | 设备登记历史，与 User/Workspace 同属「持久实体」，保持一致。 |
| **AppSettings** | ✅ SQLite | 内置/外置应用的私有版本化 JSON 配置，按 User + scope + AppId + key 隔离；详见 [`RelaxKonOS.AppSettings.md`](../development/RelaxKonOS.AppSettings.md)。 |
| **ImageMirrors** | ✅ SQLite | 按 User + 目标服务隔离的镜像仓库前缀与当前选择；Docker 拉取时由服务端读取，选择默认不使用镜像源。 |
| **Bookmark / HistoryEntry**（浏览器） | ✅ SQLite | 书签与历史记录是用户长期数据；按 UserId 隔离，BrowserRepository（SQLite/EF）提供 CRUD。**不存 Cookie、Extension Config**（需单独加密存储，设计中）。 |
| **RegistryEntry / RegistryKey**（配置注册表） | ✅ SQLite | 受 schema 约束的配置注册表；按 User+Scope+ScopeId 隔离。对应 `CachedSqliteRegistryRepository`。详见 [`RelaxKonOS.Registry.md`](../architecture/RelaxKonOS.Registry.md)。 |
| **GitRepository** | ✅ SQLite | 仅持久化仓库元数据（Id/UserId/Name/Path/CreatedAt）；分支/提交/状态/差异为实时 `git` CLI 结果，**不持久化**；凭据走 `ISecretStore`，不入库。 |
| **TunnelDefinition / TunnelSecret / TunnelServerProfile** | ✅ SQLite | FRP 隧道的用户声明与服务器配置；Secret 值经 `ISecretStore`（DataProtectionSecretStore）加密后以 `ValueCiphertext` 列存储，**绝不存明文**。 |
| **TunnelAuditEntry** | ✅ SQLite | 隧道高风险操作脱敏审计（apply/stop/secret 变更等）；只存动作/目标/结果/问题码，无 payload 或秘密详情。REST `/tunnels/frps/audit` 分页返回。 |
| **AuthenticationProtection**（账号失败状态 + 认证安全审计） | ✅ SQLite | 账号维度失败状态（`AccountFailureState`）跨重启保留递增冷却；IP 和账号+IP 维度短期内存；`AuthenticationSecurityEvent` 记录不含密码/令牌的安全事件审计，合规追溯。 |
| **Certificate* 元数据 / WebServer* 元数据 / Operation 表** | ✅ SQLite（HostGlobal 库） | 证书和 WebServer 的规范化元数据、部署记录、续期尝试、Operation、重试、审计和配置快照；**绝不保存私钥 PEM、ACME account key、DNS-01 token 或导入密码**（这些存受 ACL 保护的文件系统 + `ISecretStore`）。 |
| Session | ❌ 内存 | 「连接关系」是运行时状态（Created→Active→Disconnected→Expired），重启后旧 Session 本就应失效，用户重新登录即可。持久化反而引入状态不一致。 |
| AuthSessionStore（refresh token） | ❌ 内存 | refresh token 仅限当前客户端/服务端进程会话；客户端退出或服务端重启后失效，重新登录可使用用户显式保存于系统凭据库的密码。 |
| TerminalSessionManager（PTY + 环形缓冲） | ❌ 内存 | PTY 是活进程，无法序列化；重启后用户重连新建 PTY + 回放缓冲（缓冲内存丢失为已知行为，见 [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md)）。 |
| PerformanceSampler（60s history） | ❌ 内存 | 系统性能采样是短期运行时数据；历史最多保留 60s 环形内存；任务管理器重连通过 REST history 回补。 |
| FRP 运行时进程 / 托管 frps | ❌ 内存 + 文件系统 | 进程 PID、TOML 配置、原始二进制由 RuntimeManager 写入文件系统；数据库仅保存声明、审计和配置版本号。 |
| Guardian 工作负载实际进程状态 | ❌ 内存（管道 IPC） + 文件（原生服务 unit/SCM） | SQLite 保存声明（重启后重新投递给 Agent）；运行时健康和日志通过 Guardian Agent 管道和 Hub 广播。 |

> 结论：持久化 **User + Workspace + Device + Bookmark + HistoryEntry + AppSettings + ImageMirrors + RegistryEntry/Key + GitRepository + Tunnel*（Definition/Secret/ServerProfile/Audit）+ AuthenticationProtection + Certificate*/WebServer*（HostGlobal 元数据/Operation/审计）**。Session / refresh token / PTY / Performance 采样 / 运行时进程维持内存，符合各自语义。证书与 WebServer 属于宿主机全局资源，使用独立的 `HostGlobalMigrationRunner` 建库/迁移，不污染业务用户数据库。

---

## 5. 表结构

数据库文件默认 `{ContentRoot}/data/remoteos.db`（见 §7）。表名小写复数，由 `RemoteOsDbContext.OnModelCreating` 定义。

### 5.1 users

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Username | TEXT | NOT NULL，≤128 |
| Platform | TEXT | NOT NULL，≤32（枚举字符串） |
| PlatformIdentity | TEXT | ≤256 |
| CreatedAt | TEXT | NOT NULL（ISO 8601） |
| LastLoginAt | TEXT | NULL |

- 唯一索引：`(Username, Platform)`——对应 `InMemoryUserRepository._byName`

### 5.2 workspaces

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL |
| Name | TEXT | NOT NULL，≤256 |
| State | TEXT | NOT NULL，≤32（枚举字符串） |
| CreatedAt | TEXT | NOT NULL |
| terminal_settings | TEXT | NOT NULL（JSON，TerminalSettingsDto 序列化） |
| browser_settings | TEXT | NULL（JSON，BrowserSettingsDto 序列化；既有库增量补齐） |
| preferences | TEXT | NULL（JSON，WorkspacePreferencesDto 序列化；既有库增量补齐） |
| ControllerDeviceId | TEXT | NULL |
| ControllerGrantedAt | TEXT | NULL |
| ControllerLeaseExpiresAt | TEXT | NULL |

- 唯一索引：`(UserId)`——One User One Persistent Workspace，对应 `InMemoryWorkspaceRepository._byUserId`
- `terminal_settings`：EF Core `OwnsOne + ToJson`，把 6 字段 DTO 序列化为单列 JSON。示例值：
  ```json
  {"fontFamily":"Cascadia Mono","fontSize":14,"colorScheme":"Campbell","backgroundColor":"#0C0C0C","foregroundColor":"#CCCCCC","cursorColor":"#FFFFFF"}
  ```
- `browser_settings` / `preferences`：同 `OwnsOne + ToJson` 模式，单列 JSON（BrowserSettingsDto / WorkspacePreferencesDto）。列允许 NULL——读取 NULL 时回退领域模型默认值。既有库（建库时无此列）由 `Program.cs` 启动时 `ALTER TABLE ... ADD COLUMN ... TEXT NULL` 增量补齐（见 [`RelaxKonOS.Settings.md`](../desktop/RelaxKonOS.Settings.md) §4.2 / [`RelaxKonOS.Browser.md`](../applications/RelaxKonOS.Browser.md) §3.3）。

### 5.3 devices

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Name | TEXT | NOT NULL，≤128 |
| Platform | TEXT | NOT NULL，≤32（小写字符串） |
| ClientVersion | TEXT | ≤64 |
| LastLoginAt | TEXT | NULL |

- 唯一索引：`(Name, Platform)`——对应 `InMemoryDeviceRepository._byKey`

> Guid 主键存 TEXT；DateTimeOffset 存 TEXT（ISO 8601，SQLite 原生 datetime 处理）。索引与现有内存仓储的 `_byName` / `_byUserId` / `_byKey` 字典键一一对应，保证切换实现后查询语义不变。

### 5.4 app_settings

| 列 | 类型 | 约束 |
| --- | --- | --- |
| UserId / Scope / ScopeId / AppId / Key | TEXT | 复合主键；隔离配置所属用户、范围、应用和文档 key |
| ValueJson | TEXT | NOT NULL，应用私有 JSON（最大 64 KiB 由 API 限制） |
| SchemaVersion | INTEGER | NOT NULL，应用定义的 JSON 格式版本 |
| Revision | INTEGER | NOT NULL，EF concurrency token |
| UpdatedAt | TEXT | NOT NULL（ISO 8601） |

这是独立表，**不得**把任意应用配置追加到 `workspaces` 的系统偏好 JSON 列。旧数据库在服务端启动时以 `CREATE TABLE IF NOT EXISTS` 增量补齐；长期 schema 演进仍应迁移到 EF Core Migrations。

### 5.5 image_mirrors

| 列 | 类型 | 约束 |
| --- | --- | --- |
| Id | TEXT | PK |
| UserId / Target | TEXT | 当前账户和使用该镜像源的服务（目前为 Docker） |
| Name | TEXT | NOT NULL，≤80 |
| Endpoint | TEXT | NOT NULL，≤255，HTTPS registry host，不含路径或凭据 |
| IsSelected | INTEGER | 当前服务是否使用此镜像源；没有选中项即为默认直连 |
| CreatedAt / UpdatedAt | TEXT | NOT NULL（ISO 8601） |

镜像源不是 Docker 守护进程的全局 `registry-mirrors` 配置。它是用户选定的拉取前缀：服务端仅在 Docker Hub 镜像拉取时读取当前用户的选择，并将如 `mysql:8.4` 解析成 `{Endpoint}/library/mysql:8.4`。显式指定的第三方仓库（例如 `ghcr.io/...`）保持不变。

### 5.6 bookmarks（浏览器书签）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL，FK→users.Id |
| Title | TEXT | NOT NULL，≤512 |
| Url | TEXT | NOT NULL，≤2048 |
| CreatedAt | TEXT | NOT NULL（ISO 8601） |

- 唯一索引：`(UserId, Url)`——同用户下 URL 不重复（应用层保证；冲突时返回已有条目）
- **仓储**：`IBrowserRepository` → `SqliteBrowserRepository`（EF Core）

### 5.7 history_entries（浏览器历史）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL，FK→users.Id |
| Title | TEXT | ≤512 |
| Url | TEXT | NOT NULL，≤2048 |
| VisitCount | INTEGER | NOT NULL，默认 1，同 URL 多次访问累加 |
| FirstVisitedAt | TEXT | NOT NULL |
| LastVisitedAt | TEXT | NOT NULL（索引，倒序查询最近访问） |

- 唯一索引：`(UserId, Url)`
- 索引：`(UserId, LastVisitedAt DESC)` 支持 `limit/offset` 最近 N 条分页

### 5.8 registry_entries（配置注册表）

| 列 | 类型 | 约束 |
|----|------|------|
| UserId | TEXT | NOT NULL，PK 复合组分 1 |
| Scope | TEXT | NOT NULL（RegistryScope 字符串：workspace/user/document），PK 复合组分 2 |
| ScopeId | TEXT | NOT NULL（WorkspaceId/UserId/DocumentId），PK 复合组分 3 |
| Path | TEXT | NOT NULL（注册表键路径，如 `system/terminal/appearance`），PK 复合组分 4 |
| Name | TEXT | NOT NULL（值名称，空为默认值），PK 复合组分 5 |
| ValueType | TEXT | NOT NULL（RegistryValueType 字符串：string/int/bool/number/json/enum） |
| ValueJson | TEXT | NOT NULL，JSON 序列化的值（最大 128 KiB） |
| Revision | INTEGER | NOT NULL，乐观并发令牌（每次 +1） |
| State | TEXT | NOT NULL（RegistryEntryState：synced/applying/applied/error） |
| DesiredUpdatedAt | TEXT | NOT NULL（最后变更时间） |
| DesiredUpdatedBy | TEXT | NOT NULL（变更主体：用户名或系统标识） |
| AppliedRevision | INTEGER | NULL（最后成功应用的 revision） |
| AppliedAt | TEXT | NULL（最后成功应用时间） |
| LastErrorCode | TEXT | NULL（应用失败时的稳定问题码） |
| LastErrorMessage | TEXT | NULL（应用失败时的说明，≤ 512 字符） |

**约束**：复合主键 `(UserId, Scope, ScopeId, Path, Name)`。每个值都必须在 `RegistrySchema` 允许的列表中存在（Endpoint 层校验）；非法路径/值类型拒绝写入。**不直接支持删除注册表键路径**——通过写 null 值或 tombstone 保留审计轨迹，应用层后续 GC。

### 5.9 git_repositories（Git 仓库登记）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL，FK→users.Id |
| Name | TEXT | NOT NULL，≤256（显示名，可重复） |
| Path | TEXT | NOT NULL，≤1024（宿主机绝对路径） |
| CreatedAt | TEXT | NOT NULL |

- 索引：`(UserId, Path)` UNIQUE——同用户下同一宿主机路径只能注册一次。
- **不保存**：分支、提交、变更、远程凭据。凭据走 `ISecretStore`（按仓库 id 加密保存 HTTPS token 或 SSH key 引用）。

### 5.10 tunnel_definitions（FRP 隧道定义 = profile）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL，FK→users.Id |
| Name | TEXT | NOT NULL，≤128 |
| ServerHost | TEXT | NOT NULL，≤255（FRP Server host） |
| ServerPort | INTEGER | NOT NULL（FRP Server 端口） |
| Transport | TEXT | NOT NULL（tcp/tcp+tls/quic/kcp/websocket） |
| AuthType | TEXT | NOT NULL（none/token/oidc，默认 token） |
| SecretReferenceId | TEXT | NULL，FK→tunnel_secrets.Id（经 ISecretStore 加密） |
| TlsServerName | TEXT | NULL，≤255 |
| EnableTls | INTEGER | NOT NULL（0/1） |
| ExtraTomlFragment | TEXT | NULL（用户自定义 TOML 片段，≤ 16 KiB，经 `TunnelValidation` 校验白名单字段） |
| CreatedAt / UpdatedAt | TEXT | NOT NULL |

### 5.11 tunnel_secrets（FRP 凭据引用，不存明文）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| UserId | TEXT | NOT NULL |
| Kind | TEXT | NOT NULL（token/basic_username_password/oidc_client/ssh_key_reference） |
| ValueCiphertext | TEXT | NOT NULL（经 `IDataProtectionProvider` + `DataProtectionSecretStore` 加密后的 base64 或保护 blob 引用；**绝不存明文 token/密码**） |
| SchemaVersion | INTEGER | NOT NULL（加密版本，轮换用） |
| CreatedAt / LastUsedAt | TEXT | NOT NULL / NULL |
| Revision | INTEGER | NOT NULL（每次更新密文 +1） |

### 5.12 tunnel_server_profiles（托管 FRPS 服务器配置声明，非运行时）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Name | TEXT | NOT NULL UNIQUE，≤128 |
| BindAddress | TEXT | NOT NULL，≤64（通常 127.0.0.1 或受控内网；不允许 0.0.0.0 未经授权） |
| BindPort | INTEGER | NOT NULL |
| KcpBindPort | INTEGER | NULL |
| AllowPorts | TEXT | NULL（白名单端口范围，CSV） |
| AuthTokenReferenceId | TEXT | NULL，FK→tunnel_secrets.Id |
| MaxPortsPerClient | INTEGER | NULL |
| TomlOverrideFragment | TEXT | NULL（≤ 16 KiB，白名单字段校验） |
| UpdatedAt | TEXT | NOT NULL |

### 5.13 tunnel_audit_entries（高风险 FRP 操作审计）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| ActorUserId | TEXT | NOT NULL |
| Action | TEXT | NOT NULL，≤64（apply-profile/stop-profile/update-secret/frps-start/frps-stop/runtime-install/runtime-uninstall 等） |
| TargetId | TEXT | NULL（profile id / secret id / frps 配置 id 等） |
| Result | TEXT | NOT NULL，≤32（success/failure/denied/partial） |
| ProblemCode | TEXT | NULL（稳定问题码，≤ 128） |
| CreatedAt | TEXT | NOT NULL（ISO 8601；索引倒序分页） |

- 索引：`(CreatedAt DESC)`、`(ActorUserId, CreatedAt DESC)`
- **无 payload 列**：绝不记录明文 secret、TOML 原始内容或凭据。仅记录谁、做了什么、结果如何。最多 180 天滚动（由后台 `HostOperationJournal` 清理任务）。

### 5.14 account_failure_states（登录保护：账号维度失败计数）

| 列 | 类型 | 约束 |
|----|------|------|
| AccountKey | TEXT | PK（规范化用户名，例如 `username@platform`） |
| FailureCount | INTEGER | NOT NULL（从 1 起） |
| FirstFailureAt | TEXT | NOT NULL |
| LastFailureAt | TEXT | NOT NULL |
| BlockedUntil | TEXT | NULL（递增冷却到期时间；空=当前未被冷却） |

- 仅账号维度持久化，跨重启保留递增冷却逻辑（见 Login.md §4.6）。IP 和账号+IP 维度是短期内存状态（避免把不受控攻击 IP 永久写入数据库）。
- 成功登录后 `LoginProtectionService` 清除该账号的失败记录。

### 5.15 authentication_security_events（认证安全审计）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| EventType | TEXT | NOT NULL，≤64（login-success / login-failure / logout / refresh-success / refresh-failure / rate-limited / account-locked 等） |
| AccountKey | TEXT | NULL（规范化用户名；匿名事件可空） |
| SourceIp | TEXT | NOT NULL，≤64 |
| CreatedAt | TEXT | NOT NULL（ISO 8601；索引倒序） |

- 约束：**不得包含密码、访问令牌、刷新令牌、完整请求体或用户代理指纹中的可识别个人信息**。
- 保留期：默认 180 天；可由后台任务清理。

---

### A. HostGlobal 库（certificates / webservers 元数据与 operation）

由 `HostGlobalMigrationRunner` 管理的独立 SQLite 库（默认 `data/remoteos-hostglobal.db`），因为证书、WebServer 实例、全局操作不属于任何 User 或 Workspace，且必须随 Server 安装生命周期独立迁移（**不使用 `EnsureCreated()`，必须使用 Migrations**）。这组表第一次落地时使用 EF Core Migrations 并建立 `__EFMigrationsHistory` 基线。

**通用原则**：数据库只保存规范化元数据、受保护文件引用（路径 + hash + 权限位）、版本号、状态、稳定问题码、审计引用和保留期。**绝不保存私钥 PEM、ACME account key、DNS-01 token 或导入密码**——这些保存在受 ACL 保护的文件系统（Linux 0600 root:root / Windows NT SERVICE\TrustedInstaller 级 ACL）并通过 `ISecretStore` 记录保护引用。

#### A.1 certificate_records

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| CommonName | TEXT | NOT NULL，≤255 |
| SubjectAlternativeNamesJson | TEXT | NOT NULL（SAN 列表 JSON） |
| NotBefore / NotAfter | TEXT | NOT NULL（证书有效期限） |
| SerialNumber | TEXT | NULL，≤64 |
| ThumbprintSha256 | TEXT | NULL，≤64 |
| KeyAlgorithm | TEXT | NOT NULL（rsa-2048/rsa-4096/ecdsa-p256/ecdsa-p384） |
| ChainPemProtectedRef | TEXT | NOT NULL（指向文件系统受保护证书链的引用；不是 PEM 内容） |
| PrivateKeyProtectedRef | TEXT | NOT NULL（指向文件系统受保护私钥的引用；不直接存私钥） |
| AcmeAccountId | TEXT | NULL，FK→acme_account_records.Id |
| ChallengeType | TEXT | NULL（http-01/dns-01/tls-alpn-01） |
| Source | TEXT | NOT NULL（acme/import/manual） |
| CurrentDeploymentStatus | TEXT | NOT NULL（deployed/not-deployed/error） |
| RenewalThresholdDays | INTEGER | NOT NULL，默认 30 |
| AutoRenewalEnabled | INTEGER | NOT NULL（0/1） |
| CreatedAt / UpdatedAt / LastRenewedAt / LastDeployedAt | TEXT | NOT NULL / NULL |
| RevocationReason | TEXT | NULL（unspecified/key-compromise/ca-compromise/superseded/cessation-of-operation/remove-from-crl/privilege-withdrawn） |
| RevokedAt | TEXT | NULL |
| RetainUntilAt | TEXT | NOT NULL（删除后保留期限；操作软删除标记，保留期后 GC） |

#### A.2 acme_account_records（ACME 账户元数据）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| DirectoryUri | TEXT | NOT NULL，≤512（ACME directory URL，如 Let's Encrypt prod/staging） |
| AcmeAccountKid | TEXT | NULL，≤512（CA 返回的 kid；注册前为空） |
| AccountKeyProtectedRef | TEXT | NOT NULL（文件系统受保护 EC/RSA account key 引用） |
| EmailContactsJson | TEXT | NULL（联系邮箱 JSON 列表） |
| TermsOfServiceAgreedAt | TEXT | NULL |
| CreatedAt / UpdatedAt | TEXT | NOT NULL |

#### A.3 certificate_deployment_records（证书部署到哪些前端）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| CertificateRecordId | TEXT | NOT NULL，FK→certificate_records.Id |
| TargetKind | TEXT | NOT NULL（kestrel/nginx/iis/apache/custom） |
| TargetIdentifier | TEXT | NOT NULL，≤512（如 Kestrel endpoint name、Nginx site id 等） |
| DeployedAt | TEXT | NOT NULL |
| DeployedBy | TEXT | NOT NULL |
| DeploymentSnapshotId | TEXT | NULL，FK→certificate_config_snapshots.Id |
| ResultCode | TEXT | NULL（成功/失败稳定码） |
| ResultMessage | TEXT | NULL，≤1024 |

#### A.4 certificate_renewal_attempts

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| CertificateRecordId | TEXT | NOT NULL，FK→certificate_records.Id |
| ScheduledAt / StartedAt / CompletedAt | TEXT | NOT NULL / NOT NULL / NULL |
| Trigger | TEXT | NOT NULL（scheduled/manual/startup-catchup/pre-deploy） |
| Status | TEXT | NOT NULL（pending/running/succeeded/failed/cancelled/skipped） |
| StableProblemCode | TEXT | NULL（稳定问题码，失败时 UI 映射） |
| ProblemDetail | TEXT | NULL，≤2048（不含秘密/原始响应） |
| NewCertificateRecordId | TEXT | NULL，FK→certificate_records.Id（若生成新记录） |
| OperationId | TEXT | NULL，FK→certificate_operations.Id |

#### A.5 certificate_operations（异步签发/续期/部署/吊销/删除）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| Kind | TEXT | NOT NULL（issue/renew/deploy/revoke/delete/precheck） |
| IdempotencyKey | TEXT | NOT NULL UNIQUE，≤128（客户端传；防重复提交） |
| Status | TEXT | NOT NULL（queued/running/succeeded/failed/cancelled） |
| Phase | TEXT | NULL（当前阶段，如 validate/authorize/finalize/install） |
| CertificateRecordId | TEXT | NULL，FK→certificate_records.Id |
| StableProblemCode | TEXT | NULL |
| ProblemDetail | TEXT | NULL，≤2048 |
| ProgressPercent | INTEGER | NULL（0-100；可选） |
| SnapshotId | TEXT | NULL，FK→certificate_config_snapshots.Id |
| QueuedAt / StartedAt / CompletedAt / CancelledAt | TEXT | NOT NULL / NULL |
| CreatedBy | TEXT | NOT NULL |

#### A.6 certificate_config_snapshots（申请/续期时的完整不可变配置快照）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| CertificateRecordId | TEXT | NULL，FK→certificate_records.Id |
| AcmeAccountId | TEXT | NULL |
| IssueConfigJson | TEXT | NOT NULL（规范化的 IssueCertificateRequest JSON 快照，不可变） |
| SanHashSha256 | TEXT | NOT NULL，≤64（SAN 列表 hash，用于去重/比较） |
| CreatedAt | TEXT | NOT NULL |

#### A.7 certificate_audit_entries（证书敏感操作审计）

| 列 | 类型 | 约束 |
|----|------|------|
| Id | TEXT | PK |
| ActorUserId | TEXT | NULL（登录用户 id；自动化进程可为空） |
| Action | TEXT | NOT NULL，≤64（issue/renew/deploy/revoke/delete/rotate-account-key/import-pfx/export-pfx） |
| TargetId | TEXT | NULL（CertificateId / OperationId 等） |
| Result | TEXT | NOT NULL，≤32 |
| ProblemCode | TEXT | NULL |
| CreatedAt | TEXT | NOT NULL |
- **审计日志永不存私钥 PEM、密码、token**。导出操作需单独审计并标记失败（当前不支持导出私钥）。

**WebServer 库表结构**与 Certificate 系列类似（Nginx 元数据/站点/配置快照/重载操作/审计），具体字段以 [`RelaxKonOS.WebServerManager.Design.md`](../applications/RelaxKonOS.WebServerManager.Design.md) §30.5 为准；它也走 HostGlobal 库 + Migrations，绝不用临时 `CREATE TABLE` 拼接。

---

## 6. 仓储层

### 6.1 接口不变

保留现有接口（[`RelaxKonOS.Server/Storage/`](../../RelaxKonOS.Server/Storage)）：
- `IUserRepository`：FindByUsername / FindById / Add / UpdateLastLogin
- `IWorkspaceRepository`：FindByUserId / FindById / Add / Update
- `IDeviceRepository`：FindByNameAndPlatform / FindById / Add / Update
- `IBrowserRepository`：Bookmark（List/Add/Delete/Clear）+ HistoryEntry（List/Add/Delete/DeleteId/Clear）—— SQL 实现：`SqliteBrowserRepository`；内存回退：`InMemoryBrowserRepository`
- `IAppSettingsRepository`：Find / Upsert（带 revision 乐观并发）
- `IImageMirrorRepository`：List / Create / Update / Delete / Select / GetSelected（按 User + Target 隔离）
- `IRegistryRepository`（注册表）：BrowseKeys / GetValues / UpsertValue / CreateKey / DeleteKey / DeleteValue / GetSchema —— EF 实现：`CachedSqliteRegistryRepository`（带内存缓存层，减少 Schema 查询）
- `SessionRepository`：始终 `InMemorySessionRepository`（Session 不持久化）
- `AuthenticationProtectionStore`：账号维度 `AccountFailureState` 持久化读写 + 安全审计事件追加（EF Core + SQLite，不缓存，每次写入立即落库）

### 6.2 新增 EF 实现

[`RelaxKonOS.Server/Storage/Sqlite/`](../../RelaxKonOS.Server/Storage/Sqlite)：
- [`RemoteOsDbContext`](../../RelaxKonOS.Server/Storage/Sqlite/RemoteOsDbContext.cs)：`DbSet<User/Workspace/Device/Bookmark/HistoryEntry/AppSetting/ImageMirror/GitRepository/RegistryEntry/TunnelDefinition/TunnelSecret/TunnelServerProfile/TunnelAuditEntry/AccountFailureState/AuthenticationSecurityEvent>` + `OnModelCreating`
- 各 `Sqlite*Repository`：注入 DbContext，用 EF 查询实现接口。`RemoteOsDbContext` 还保存 Workspace 拥有的 `TerminalSettings` / `BrowserSettings` / `Preferences`（含 `ThemePreferences` / `DesktopDisplay` / 文本编码）/ `WindowLayouts`，全部用 `OwnsOne + ToJson` JSON 列。
- HostGlobal 库：由 `HostGlobalMigrationRunner` 在 `Program.cs` 启动时独立执行 Migrations（`MigrateAsync()`），使用 `HostGlobalDbContext`（单独 EF Context，不与业务 `RemoteOsDbContext` 混用）。Certificate / WebServer / Operation / 审计 Repository 只操作 HostGlobal 库。

实现要点：
- **查询**用 `AsNoTracking()` 返回 detached 实体（避免跨请求 stale tracking）。
- **Add / Update** 显式 `SaveChanges()` 持久化（与 InMemory 立即生效语义一致）。
- **UpdateLastLogin** 用 tracking 查询（`Find`）加载后修改属性，SaveChanges 检测变更。
- **TerminalSettings / BrowserSettings / Preferences 兜底**：读取时若 JSON 列为 null（防御旧数据），仓储回退各自 `*.Default` 静态值。
- **bookmarks / history_entries**：URL 冲突（同用户同 URL 已存在）时——书签返回已有条目（幂等不报错）；历史记录累加 `VisitCount` 并刷新 `LastVisitedAt`。
- **registry_entries**：Endpoint 层先查 `RegistrySchema` 白名单路径/值类型，未通过时返回 400 `registry-schema-violation`，不执行 DB 写入。

### 6.3 生命周期

业务仓储与 `RemoteOsDbContext` 均为 **Scoped**（每请求一个 DbContext）。Minimal API `[FromServices]` 每请求创建 scope，兼容。Singleton 服务（`AuthSessionStore` / `JwtTokenService` / `TerminalSessionManager` / `PerformanceSampler` / `TunnelService` / `FrpRuntimeManager`）只依赖抽象仓储接口，并不直接持有 DbContext。HostGlobal 库的 Repository 与 `HostGlobalDbContext` 也是 Scoped；后台长期运行的 Worker（`CertificateRenewalWorker` 等）通过 `IServiceScopeFactory` 为每个迭代周期创建独立 scope。

---

## 7. 配置项

[`appsettings.json`](../../RelaxKonOS.Server/appsettings.json) 新增 `Storage` 节：

```json
"Storage": {
  "Provider": "sqlite",
  "DatabasePath": "data/remoteos.db"
}
```

| 项 | 默认 | 说明 |
|----|------|------|
| Provider | `sqlite` | `sqlite`（EF Core + SQLite，默认）或 `memory`（内存仓储，开发回退） |
| DatabasePath | `data/remoteos.db` | SQLite 文件相对路径（相对 ContentRoot）；启动时自动建目录 |

绑定到 [`StorageOptions`](../../RelaxKonOS.Server/Storage/StorageOptions.cs)。`Program.cs` 按 Provider 注册：
- `sqlite`：`AddDbContext<RemoteOsDbContext>(UseSqlite)` + `AddScoped<I*Repository, Sqlite*Repository>` + 启动建库
- `memory`：`AddSingleton<I*Repository, InMemory*Repository>`（开发回退，重启丢失）

---

## 8. 建库策略

启动时（`Program.cs`，`app.Build()` 之后，`storageProvider == "sqlite"` 分支）采用三段式：

```csharp
// 1) 业务库：EnsureCreated 零工具依赖 + 增量 CREATE TABLE IF NOT EXISTS 补齐
// 2) 安全防护：account_failure_states / authentication_security_events
// 3) HostGlobal：证书/WebServer 等宿主级资源走独立版本化迁移
using var scope = app.Services.CreateScope();
var db = scope.ServiceProvider.GetRequiredService<RemoteOsDbContext>();
db.Database.EnsureCreated();
db.Database.ExecuteSqlRaw("""
    CREATE TABLE IF NOT EXISTS "bookmarks" (...);
    CREATE TABLE IF NOT EXISTS "history_entries" (...);
    CREATE TABLE IF NOT EXISTS "app_settings" (...);
    CREATE TABLE IF NOT EXISTS "image_mirrors" (...);
    CREATE TABLE IF NOT EXISTS "git_repositories" (...);
    CREATE TABLE IF NOT EXISTS "tunnel_server_profiles" (...);
    CREATE TABLE IF NOT EXISTS "tunnel_definitions" (...);
    CREATE TABLE IF NOT EXISTS "tunnel_secrets" (...);
    CREATE TABLE IF NOT EXISTS "tunnel_audit_entries" (...);
    CREATE TABLE IF NOT EXISTS "account_failure_states" (...);
    CREATE TABLE IF NOT EXISTS "authentication_security_events" (...);
    CREATE TABLE IF NOT EXISTS "registry_entries" (...);
    CREATE TABLE IF NOT EXISTS "registry_keys" (...);
""");
await HostGlobalMigrationRunner.MigrateAsync(
    db.Database.GetDbConnection().ConnectionString,
    app.Lifetime.ApplicationStopping);
```

### 8.1 业务库（User/Workspace 域）

- **`EnsureCreated()`**：库不存在时按 `RemoteOsDbContext.OnModelCreating` 一次性建表；库已存在则跳过。
- **增量补齐**：`EnsureCreated` 不会为既有库追加新表（例如新增的 `bookmarks` / `history_entries` / `app_settings` / `image_mirrors` / `git_repositories` / `tunnel_*` / `registry_*` / 安全防护表），因此紧接着以 `CREATE TABLE IF NOT EXISTS` 方式补齐。每批次 DDL 与 `OnModelCreating` 的列、类型、索引保持一致，保证「首次部署」和「升级部署」都能落到同一 schema。
- **新增列演进**：若后续需要给旧表加列，遵循相同模式——在 `ExecuteSqlRaw` 中追加 `ALTER TABLE ... ADD COLUMN ... IF NOT EXISTS` 风格的补丁（SQLite 原生支持 IF NOT EXISTS 加列需自行封装 `HasColumnAsync` 判断）。

### 8.2 HostGlobal 域（证书 / WebServer 宿主资源）

证书记录、WebServer 实例、操作流水、审计日志等**不属于**某个 User/Workspace，是宿主机器级资源；且对「可恢复性/幂等性」要求高于普通用户表（例如签发流程必须能跨进程续跑）。因此不放进 `RemoteOsDbContext` 的 `EnsureCreated` 路径，改用 [`HostGlobalMigrationRunner`](../../RelaxKonOS.Server/Storage/Sqlite/HostGlobalMigrationRunner.cs) 提供**独立版本化迁移**：

- 元数据表：`remoteos_host_schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT)`，每个版本仅执行一次。
- 迁移版本（截至当前代码）：
  - **v1**：一次性创建 `certificate_operations` / `certificate_records` / `acme_account_records` / `certificate_deployment_records` / `certificate_renewal_attempts` / `certificate_audit_entries` / `webserver_instances` / `webserver_sites` / `webserver_config_snapshots` / `webserver_operations` 及其索引。
  - **v2**：`certificate_records` 补齐 `contact_email`。
  - **v3**：新增 `webserver_audit_entries`。
  - **v4**：`certificate_records` 补齐 `renewal_window_start` / `renewal_window_end`。
  - **v5**：`certificate_records` 补齐 `key_algorithm`。
  - **v6**：`certificate_records` 补齐 `last_renewal_at` / `last_renewal_problem_code`。
  - **v7**：`certificate_records` 补齐 `kind` / `fingerprint_sha256`。
- 加列安全：对于 `ALTER TABLE ADD COLUMN` 步骤，迁移器内部先 `SELECT EXISTS(SELECT 1 FROM pragma_table_info('table') WHERE name = 'col')` 判断，保证对已手工补列的旧库也不会报错。
- 事务：整个迁移在单个 `BEGIN TRANSACTION` + `COMMIT` 中完成，任何版本失败都不会留下半状态。

### 8.3 未来演进路径

- 当 User/Workspace 域的 schema 变更频繁、或需要数据迁移（列改值/拆表）时，切换为 EF Core Migrations：`dotnet ef migrations add` 生成迁移 + `db.Database.MigrateAsync()` 应用。注意 `EnsureCreated` 与 Migrations 互斥（EnsureCreated 不建 `__EFMigrationsHistory` 表），切换前需删除旧业务库或手工把当前 schema 基线插入 `__EFMigrationsHistory`。
- HostGlobal 域保持「自写迁移器」路线不变——它是宿主运行时能力的一部分，而非业务模型，避免与 EF Migrations 工具链耦合导致运维复杂化。新增版本仅需在 `HostGlobalMigrationRunner.MigrateAsync` 末尾追加 `if (!await IsAppliedAsync(..., N))` 块即可。

---

## 9. 与登录流程的交互

[`AuthEndpoints.Login`](../../RelaxKonOS.Server/Endpoints/AuthEndpoints.cs) 的 FindOrCreate 流程在持久化后变为：

```text
宿主 OS 认证（IIdentityProvider.Verify）
    │
    ├─ FindByUsername(username, platform)
    │     └─ 命中 SQLite 既有 User → 复用（保留 CreatedAt 历史）
    │     └─ 未命中 → Add 新 User → 写入 SQLite
    │
    ├─ FindByUserId(user.Id)
    │     └─ 命中 SQLite 既有 Workspace → 复用（带回已持久化的 TerminalSettings）★ 配置持久化生效
    │     └─ 未命中 → Add 新 Workspace（TerminalSettings=Default）→ 写入 SQLite
    │
    ├─ FindByNameAndPlatform(deviceName, platform)
    │     └─ 命中 → 更新 ClientVersion/LastLoginAt
    │     └─ 未命中 → Add 新 Device → 写入 SQLite
    │
    ├─ 新建 Session（内存，每次登录新建）
    ├─ 设为 Controller（Update Workspace → SQLite）
    └─ Issue JWT
```

**配置持久化闭环**：
1. 用户登录 → Workspace 以 Default TerminalSettings 写入 SQLite
2. 用户改终端配置 → `PUT terminal-settings` → `workspace.TerminalSettings = normalized; Update(workspace)` → SQLite 更新 `terminal_settings` JSON 列
3. **Server 重启** → SQLite 保留
4. 用户重新登录 → `FindByUserId` 命中既有 Workspace → **带回改过的 TerminalSettings** → 配置恢复

---

## 10. 并发与连接

- EF Core SQLite provider 默认使用连接池（`Microsoft.Data.Sqlite` 内置），per-operation 连接。
- 写并发：SQLite 默认 `journal_mode=delete`，单写者。当前单服务器场景写并发极低（登录/改配置），可接受。未来若需提升读写并发，可启用 WAL（`PRAGMA journal_mode=WAL`）。
- 仓储 Scoped + 每请求独立 DbContext，无跨请求共享状态。

---

## 11. 已知警告

- `NU1903`：`SQLitePCLRaw.lib.e_sqlite3` 2.1.11（EF Core Sqlite 传递依赖）有已知高严重性漏洞 [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)。此为 EF Core 10.0.10 捆绑的传递依赖版本，属既有 NU1903 警告类（与 Microsoft.OpenApi 警告同类），当前阶段可接受；后续关注 EF Core 版本更新是否修复。

---

## 12. AI Agent 理解规则

实现 RelaxKonOS.Server 持久化时必须遵守：

- **持久化范围**：持久化 User / Workspace（含 TerminalSettings / BrowserSettings / Preferences / WindowLayouts）/ Device / Bookmark / HistoryEntry / AppSettings。**不要**把 Session、refresh token（AuthSessionStore）、PTY 会话（TerminalSessionManager）写入数据库——它们是运行时/安全/进程状态，持久化会引入不一致或安全风险。
- **接口稳定**：新增 EF 实现时**不要**改动 `IUserRepository` / `IWorkspaceRepository` / `IDeviceRepository` / `IBrowserRepository` 接口；端点与领域模型不动。
- **系统配置与应用配置分离**：TerminalSettings / BrowserSettings / Preferences / WindowLayouts 仍用 `OwnsOne + ToJson`；新应用的私有配置使用 `app_settings`，不得追加 Workspace JSON 字段。详见 [`RelaxKonOS.AppSettings.md`](../development/RelaxKonOS.AppSettings.md)。
- **索引对齐**：SQLite 唯一索引必须与内存仓储的字典键（`_byName`/`_byUserId`/`_byKey`）一一对应，保证切换实现后查询语义不变。
- **Session 始终内存**：`ISessionRepository` 永远是 `InMemorySessionRepository`，不接入 DbContext。
- **建库用 EnsureCreated（当前）**：未来切 Migrations 时需先删旧库或初始化迁移历史（EnsureCreated 与 Migrations 互斥）。
- **配置驱动**：通过 `Storage:Provider` 切换 sqlite/memory，**不要**硬编码。

---

## 13. 相关文档

- [`RelaxKonOS.md`](../README.md) — 项目结构、当前进度（§4.9 Server）
- [`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md) — User/Workspace/Device 模型（§22 Persistent Workspace）
- [`RelaxKonOS.Authentication.md`](./RelaxKonOS.Authentication.md) / [`RelaxKonOS.Login.md`](./RelaxKonOS.Login.md) — 登录流程、身份映射
- [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md) — 终端应用（TerminalSettings 的消费端）
- [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md) — 架构原则

---

## 14. 证书与 Web Server 管理（设计中）

证书和 Web Server 管理属于当前宿主机全局管理能力，不随 User、Workspace、Session 或 AppSettings 保存。其实现将新增独立的 HostGlobal 表：

```text
certificate_records              acme_account_records
certificate_deployment_records   certificate_operations
certificate_renewal_attempts     certificate_audit_entries
webserver_instances              webserver_sites
webserver_config_snapshots       webserver_operations
```

PEM、私钥、ACME account key 和 challenge 文件仍位于受平台 ACL 保护的文件系统；数据库只保存规范化元数据、受保护文件引用、版本、状态、稳定问题码、审计引用和保留期信息，绝不保存私钥、account key、DNS token 或导入密码。

这组表第一次落地时必须从 `EnsureCreated()` 迁移到带 `__EFMigrationsHistory` 的 EF Core Migrations，或提供经过验证的一次性基线迁移。不得在启动时以临时 `CREATE TABLE` / `ALTER TABLE` 拼接生产 schema。每个可变实体使用 revision 并发令牌；Operation、重试、审计和配置快照须保存到服务重启后仍可恢复的存储中。具体字段和保留策略分别以 [`RelaxKonOS.CertificateManager.md`](../applications/RelaxKonOS.CertificateManager.md) §35.5 与 [`RelaxKonOS.WebServerManager.Design.md`](../applications/RelaxKonOS.WebServerManager.Design.md) §30.5 为准。


### SettingsSystem 升级（2026-09-07，G1 实施中）

Workspace preferences GET 返回 `revision`，PUT 必须携带读取时的 `revision`；缺失为 428、冲突为 409，不接受无版本覆盖。服务端 `Settings/WorkspaceSettingsService` 使用注册表 CompareExchange，客户端统一使用 `Services/WorkspaceSettings/IWorkspaceSettingsService`。偏好仍存 `Workspace\Desktop`，缓存接收不等同 SQLite 落盘。AppSettings 只负责应用私有数据；宿主真实配置与其操作恢复材料不放入 AppSettings 或 Workspace 偏好。完整执行与待验证项见 [SettingsSystem.Goal](../desktop/RelaxKonOS.SettingsSystem.Goal.md)。
