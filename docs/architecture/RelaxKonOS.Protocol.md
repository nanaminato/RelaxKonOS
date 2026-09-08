# RelaxKonOS Protocol 通信协议层

> 本文档定义 RelaxKonOS Client↔Server 通信协议契约层 `Shared/RelaxKonOS.Protocol`：模块结构、序列化约定、REST 端点、SignalR Hub 契约、认证集成方式。
>
> * 架构原则见 [`RelaxKonOS.Architecture.md`](./RelaxKonOS.Architecture.md) §4.8
>
> * 当前实现状态见 [`RelaxKonOS.md`](../README.md) §4.8
>
> * 登录与身份见 [`RelaxKonOS.Authentication.md`](../platform/RelaxKonOS.Authentication.md)
>
> * Workspace 模型见 [`RelaxKonOS.Workspace.md`](./RelaxKonOS.Workspace.md)

***

## 1. 定位与边界

`RelaxKonOS.Protocol` 是 Client↔Server **唯一**通信契约层。所有 Client/Server 通信必须经过 Protocol，禁止业务代码直接调用 HTTP / WebSocket / TCP。

**包含**：DTO、Message、API Contract（路由常量）、SignalR Hub 接口、序列化约定。

**不包含**（边界）：

* 客户端代理实现（`HubConnection` 包装、typed HttpClient）→ 位于 `RelaxKonOS.Client`

* Server 端 Hub 实现与端点实现 → 位于 `RelaxKonOS.Server`

* Server 端 OS 抽象（`IIdentityProvider` / `IFileSystem` 等）→ 位于 `RelaxKonOS.Server` 内部

Protocol 程序集**零 PackageReference**，不引用 Core（避免线协议与 Core 版本耦合）。

***

## 2. 通信框架

| 通道                                     | 用途                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| -------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **REST API**（`/api/v1.0/*`）              | 请求-响应：身份（auth）、Workspace/Session/Device、控制权、桌面状态、**文件管理**（files）、**浏览器**（browser 书签/历史/设置）、**Workspace 偏好**（`/workspaces/{id}/preferences` 壁纸/主题/调色板/显示/编码/默认程序）、**系统监控**（system performance 与分页 processes）、**Docker**（docker 引擎安装/容器/镜像/网络/卷/Stack）、**防火墙**（firewall 状态/规则/默认策略，仅 Linux+UFW）、**Git**（git 引擎/仓库/分支/提交/合并/变基/远程）、**隧道**（tunnels FRP profiles/runtime/frps/审计）、**证书**（certificates ACME 预检/签发/续期/部署/吊销/operation）、**Web 服务器**（webservers Nginx 发现/重载/配置测试/集成/operation）、**注册表**（registry schema/keys/values）、**应用私有配置**（app-settings 按用户+作用域+应用+key）、**应用能力**（capabilities 文件/终端/网络等权限声明）、**镜像源**（image-mirrors Docker 拉取镜像前缀）、**进程守护**（guardian 工作负载/安装状态）、健康检查（health） |
| **SignalR Hub**（`/hubs/workspace`）     | 实时双向：桌面状态增量广播、设备上下线通知、控制权变更通知、Session/Workspace 状态变更通知                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| **SignalR Hub**（`/hubs/terminals`）     | 实时双向：远端 PTY 字节流中继（输入/输出/尺寸/退出/会话附加/列表/手动终止）。PTY 由 `TerminalSessionManager` 持有，与 Hub 连接解耦                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| **SignalR Hub**（`/hubs/performance`）   | 实时单向：服务端统一采样器每秒广播 `PerformanceRealtimeSnapshotDto`（CPU/内存/文件系统/磁盘/网络/GPU/网络地址）；客户端显式订阅并以 REST history 回补重连空洞                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| **SignalR Hub**（`/hubs/guardian-logs`） | 实时单向：Process Guardian 守护日志广播；客户端 `Subscribe/Unsubscribe` 按工作负载订阅，服务端推送结构化日志事件（包含 workload id、级别、消息、时间戳）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |

SignalR 内部走 WebSocket（不可用降级 SSE/长轮询），**不裸用 WebSocket**。Workspace 多设备通过 SignalR Group（一个 Workspace 一个 Group）广播。Terminal Hub 不启用 `WithAutomaticReconnect`（自动重连后服务端不会自动重新附加会话），恢复路径是"再次登录打开终端 → 重新 `Start(Attach)` → 回放 1MB 缓冲快照"。所有 Hub 路径常量集中在 `RemoteOsEndpoints`（`WorkspaceHubPath` / `PerformanceHubPath` / `GuardianLogsHubPath`）。

***

## 3. 模块结构

```text
Shared/RelaxKonOS.Protocol/
├── Common/              # PlatformKind、RemoteOsEndpoints（含 Hub 路径）、ProblemDetails、RemoteOsJsonOptions、ServerDescriptorDto
├── Identity/            # UserDto、AuthTokens、LoginRequest/Response、RefreshToken、Logout、AuthApiRoutes
├── Workspace/           # WorkspaceDto、SessionDto、DeviceDto、ControllerLeaseInfo、3 enum
│                        # WorkspacePreferencesDto（含 desktopDisplay + themePreferences + 文本编码）、DefaultAppMappingDto
│                        # ThemePreferencesDto / ThemePaletteContract / ThemePaletteDefaults / ThemePaletteImport
│                        # DesktopDisplaySettingsDto、WorkspaceWindowLayoutDto、TextEncodingPreferences、TerminalSettingsDto
│                        # WorkspaceApiRoutes（含 Preferences）、RegisterDeviceRequest / CreateWorkspaceRequest
│                        # RequestControlRequest、WorkspaceSnapshotDto
├── Desktop/             # DesktopStateDto/Patch、IconPositionDto、WallpaperDto、ThemeKind
├── Files/               # FileSystemEntryType/Dto、FileEntryDto、DirectoryDto、DriveDto、SpecialLocationDto/SpecialFolderKind
│                        # FilePropertiesDto、UpdateUnixPermissionsRequest、Rename/Move/CopyRequest、FileApiRoutes
├── Browser/             # BookmarkDto、HistoryEntryDto、Create*Request、BrowserSettingsDto、BrowserApiRoutes
├── SystemMonitor/       # 兼容 SystemMetricsDto + 新 PerformanceInfo/RealtimeSnapshot/Capabilities
│                        # CpuUsageDto、MemoryUsageDto、DiskUsageDto、NetworkUsageDto、GpuUsageDto、NetworkAddressDto
│                        # ProcessInfoDto、ProcessPageDto、KillProcessResultDto、SystemMonitorApiRoutes
├── Docker/              # DockerResourceDtos（容器/镜像/网络/卷/服务 DTO）、DockerStackDtos（Stack/Service/Validate/Deploy）
│                        # DockerStatusDto、DockerInstallationPlanDto、DockerApiRoutes
├── Git/                 # GitDtos（仓库/分支/提交/变更/差异/合并/远程/状态/登录 DTO）、GitApiRoutes
├── Firewall/            # FirewallDtos（状态/规则/默认策略/变更请求/结果）、FirewallApiRoutes
├── Tunnels/             # TunnelContracts（Profile/Definition/Secret/Runtime/Audit/Frps/登录 DTO）、TunnelApiRoutes
├── Certificates/        # CertificateContracts（证书/挑战/密钥/operation/预检 DTO）、CertificateApiRoutes
├── WebServers/          # WebServerContracts（Nginx 实例/状态/配置测试/集成/operation DTO）
│                        # WebServerSiteContracts（站点/配置/证书绑定 DTO）、WebServerApiRoutes
├── Registry/            # RegistryContracts（Schema/Key/Value/浏览 DTO）、RegistryApiRoutes（注入 AppSettings 路径，共用 app-settings 端点前缀）
├── AppSettings/         # AppSettingsContracts（应用私有配置 DTO、乐观并发 revision）、注入 WorkspaceApiRoutes/RemoteOsEndpoints
├── Capabilities/        # AppCapabilityContracts（应用能力/权限声明/授权 DTO）、注入 AppSettings 端点前缀
├── ImageMirrors/        # ImageMirrorContracts（镜像源 DTO、选择/目标服务）、注入相关端点路径常量
├── ProcessGuardian/     # GuardianStatusDto（工作负载/状态/健康/安装 DTO）、ProcessGuardianApiRoutes
└── Hubs/                # Workspace Hub：IWorkspaceHubClient/Methods/Events、JoinWorkspaceRequest、事件参数
                         # Terminal Hub：ITerminalHubClient、TerminalHubMethods/Events、StartTerminalRequest、AttachTerminalResponse、TerminalSessionInfo
                         # Performance Hub：IPerformanceHubClient、PerformanceHubMethods/Events（广播 RealtimeSnapshot）
                         # GuardianLogs Hub：IGuardianLogsHubClient、GuardianLogsHubMethods（Subscribe/Unsubscribe）、GuardianLogsHubEvents
```

命名空间：`RelaxKonOS.Protocol.{Common,Identity,Workspace,Desktop,Files,Browser,SystemMonitor,Docker,Git,Firewall,Tunnels,Certificates,WebServers,Registry,AppSettings,Capabilities,ImageMirrors,ProcessGuardian,Hubs}`。

DTO 风格：`sealed record` + 主构造（或无参构造 + 公开 setter，供 EF Core JSON 列追踪可变集合）+ `[property: JsonPropertyName]`，对齐 `Framework/RelaxKonOS.Core` 风格。ID 用 `Guid`，时间用 `DateTimeOffset`，状态用 `enum`。所有集合属性（如 `DefaultApps`）使用可变 `List<T>`，EF Core 以合成序号追踪 JSON 子项，禁止以新集合整体替换。

***

## 4. 序列化约定

`RemoteOsJsonOptions.Default` 统一序列化：

* `JsonSerializerDefaults.Web`：camelCase + 大小写不敏感

* `JsonStringEnumConverter`：枚举序列化为 camelCase 字符串（如 `"linux"`、`"running"`、`"controller"`）

* 时间：`DateTimeOffset` → ISO 8601

Server MVC（`AddControllers().AddJsonOptions`）与 SignalR（`AddSignalR().AddJsonProtocol`）共用此配置。Client Http 也用同一份 options 反序列化。

所有 DTO 公开成员显式标注 `[property: JsonPropertyName("camelCaseName")]`，钉死线协议，避免 C# 重命名导致线协议破坏。

***

## 5. REST 端点

路径前缀 `/api/v1.0`，错误统一返回 `ProblemDetails`（RFC 7807 子集）。路由常量集中在 `AuthApiRoutes` / `WorkspaceApiRoutes`。

### 认证

| 方法   | 路径                     | 请求                    | 响应                     | 认证  |
| ---- | ---------------------- | --------------------- | ---------------------- | --- |
| POST | `/api/v1.0/auth/login`   | `LoginRequest`        | `LoginResponse`        | 无   |
| POST | `/api/v1.0/auth/refresh` | `RefreshTokenRequest` | `RefreshTokenResponse` | 无   |
| POST | `/api/v1.0/auth/logout`  | `LogoutRequest`       | 204                    | JWT |
| GET  | `/api/v1.0/auth/me`      | —                     | `UserDto`              | JWT |

### Workspace

| 方法   | 路径                                        | 请求                       | 响应                          | 认证                |
| ---- | ----------------------------------------- | ------------------------ | --------------------------- | ----------------- |
| GET  | `/api/v1.0/workspaces`                      | —                        | `WorkspaceDto[]`            | JWT               |
| GET  | `/api/v1.0/workspaces/{id}`                 | —                        | `WorkspaceDto`              | JWT               |
| POST | `/api/v1.0/workspaces`                      | `CreateWorkspaceRequest` | `WorkspaceDto`              | JWT               |
| GET  | `/api/v1.0/workspaces/{id}/sessions`        | —                        | `SessionDto[]`              | JWT               |
| GET  | `/api/v1.0/workspaces/{id}/devices`         | —                        | `DeviceDto[]`               | JWT               |
| GET  | `/api/v1.0/workspaces/{id}/desktop`         | —                        | `DesktopStateDto`           | JWT               |
| PUT  | `/api/v1.0/workspaces/{id}/desktop`         | `DesktopStatePatch`      | `DesktopStateDto`           | JWT（仅 Controller） |
| POST | `/api/v1.0/workspaces/{id}/control/request` | `RequestControlRequest`  | `ControllerLeaseInfo` / 409 | JWT               |
| POST | `/api/v1.0/workspaces/{id}/control/release` | —                        | 204                         | JWT               |
| POST | `/api/v1.0/devices`                         | `RegisterDeviceRequest`  | `DeviceDto`                 | JWT               |

### Files（文件管理）

路由常量见 `FileApiRoutes`。Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限（不另建 ACL）。详见 [`RelaxKonOS.Explorer.md`](../applications/RelaxKonOS.Explorer.md)。

| 方法     | 路径                          | 请求                                  | 响应                                 | 认证  |
| ------ | --------------------------- | ----------------------------------- | ---------------------------------- | --- |
| GET    | `/api/v1.0/files/drives`      | —                                   | `DriveDto[]`                       | JWT |
| GET    | `/api/v1.0/files/special`     | —                                   | `SpecialLocationDto[]`（仅返回存在的特殊目录） | JWT |
| GET    | `/api/v1.0/files/list`        | query: `path`（空=盘符根）                | `DirectoryDto`                     | JWT |
| GET    | `/api/v1.0/files/info`        | query: `path`                       | `FileSystemEntryDto`               | JWT |
| GET    | `/api/v1.0/files/download`    | query: `path`                       | 字节流                                | JWT |
| GET    | `/api/v1.0/files/content`     | query: `path`                       | 原始文件字节流                            | JWT |
| PUT    | `/api/v1.0/files/content`     | query: `path` + 请求体字节流              | `FileEntryDto`                     | JWT |
| GET    | `/api/v1.0/files/properties`  | query: `path`                       | `FilePropertiesDto`                | JWT |
| PUT    | `/api/v1.0/files/permissions` | `UpdateUnixPermissionsRequest`      | `FilePropertiesDto`                | JWT |
| POST   | `/api/v1.0/files/directory`   | query: `path`                       | `FileSystemEntryDto`（201）          | JWT |
| DELETE | `/api/v1.0/files`             | query: `path`（目录递归）                 | 204                                | JWT |
| POST   | `/api/v1.0/files/rename`      | `RenameRequest`                     | `FileSystemEntryDto`               | JWT |
| POST   | `/api/v1.0/files/move`        | `MoveRequest`                       | `FileSystemEntryDto`               | JWT |
| POST   | `/api/v1.0/files/copy`        | `CopyRequest`                       | `FileSystemEntryDto`               | JWT |
| POST   | `/api/v1.0/files/upload`      | query: `path` + multipart/form-data | `FileEntryDto`                     | JWT |

### Browser（浏览器）

路由常量见 `BrowserApiRoutes`。书签/历史按 JWT `sub` claim 取 userId 隔离；`BrowserSettings` 随 Workspace 持久化。详见 [`RelaxKonOS.Browser.md`](../applications/RelaxKonOS.Browser.md)。

| 方法     | 路径                               | 请求                             | 响应                     | 认证  |
| ------ | -------------------------------- | ------------------------------ | ---------------------- | --- |
| GET    | `/api/v1.0/browser/settings`       | —                              | `BrowserSettingsDto`   | JWT |
| PUT    | `/api/v1.0/browser/settings`       | `BrowserSettingsDto`           | `BrowserSettingsDto`   | JWT |
| GET    | `/api/v1.0/browser/bookmarks`      | —                              | `BookmarkDto[]`        | JWT |
| POST   | `/api/v1.0/browser/bookmarks`      | `CreateBookmarkRequest`        | `BookmarkDto`（201）     | JWT |
| DELETE | `/api/v1.0/browser/bookmarks/{id}` | —                              | 204                    | JWT |
| DELETE | `/api/v1.0/browser/bookmarks`      | —                              | `{ removed }`          | JWT |
| GET    | `/api/v1.0/browser/history?limit=` | query: `limit`（默认 100，上限 1000） | `HistoryEntryDto[]`    | JWT |
| POST   | `/api/v1.0/browser/history`        | `CreateHistoryEntryRequest`    | `HistoryEntryDto`（201） | JWT |
| DELETE | `/api/v1.0/browser/history/{id}`   | —                              | 204                    | JWT |
| DELETE | `/api/v1.0/browser/history`        | —                              | `{ removed }`          | JWT |

### Workspace Preferences（设置中心偏好）

路由常量见 `WorkspaceApiRoutes.Preferences`。复用 `FindAuthorizedWorkspace` 按 JWT `sub` 校验 Workspace 归属。详见 [`RelaxKonOS.Settings.md`](../desktop/RelaxKonOS.Settings.md)。

| 方法  | 路径                                    | 请求                        | 响应                             | 认证       |
| --- | ------------------------------------- | ------------------------- | ------------------------------ | -------- |
| GET | `/api/v1.0/workspaces/{id}/preferences` | —                         | `WorkspacePreferencesDto`      | JWT（按归属） |
| PUT | `/api/v1.0/workspaces/{id}/preferences` | `WorkspacePreferencesDto` | `WorkspacePreferencesDto`（归一化） | JWT（按归属） |

### SystemMonitor（任务管理器）

路由常量见 `SystemMonitorApiRoutes`。服务端 `ISystemMetricsProvider` 以宿主 OS 进程身份实时采集，**不持久化**。详见 [`RelaxKonOS.TaskManager.md`](../applications/RelaxKonOS.TaskManager.md)。

| 方法     | 路径                                              | 请求                                  | 响应                                           | 认证  |
| ------ | ----------------------------------------------- | ----------------------------------- | -------------------------------------------- | --- |
| GET    | `/api/v1.0/system/performance/info`               | —                                   | `PerformanceInfoDto`                         | JWT |
| GET    | `/api/v1.0/system/performance/snapshot`           | —                                   | `PerformanceRealtimeSnapshotDto` / 503（首样本前） | JWT |
| GET    | `/api/v1.0/system/performance/history?seconds=60` | query: `seconds`（1–60）              | `PerformanceRealtimeSnapshotDto[]`           | JWT |
| GET    | `/api/v1.0/system/processes/query`                | page/pageSize/filter/sort/direction | `ProcessPageDto`                             | JWT |
| DELETE | `/api/v1.0/system/processes/{id}?force=`          | query: `force`（可选）                  | `KillProcessResultDto`                       | JWT |

### Docker（Docker 管理器）

路由常量见 `DockerApiRoutes`。服务端 `IDockerEngineService` 调用宿主 `docker` CLI，`IDockerComposeService` 处理 Compose 编排；需要 Docker 引擎或 Compose 已安装（提供安装计划与执行）。详见 [`RelaxKonOS.DockerManager.md`](../applications/RelaxKonOS.DockerManager.md)。

| 方法         | 路径                                           | 请求                                             | 响应                                  | 认证  |
| ---------- | -------------------------------------------- | ---------------------------------------------- | ----------------------------------- | --- |
| GET        | `/api/v1.0/docker/status`                      | —                                              | `DockerStatusDto`（引擎/Compose 状态与版本） | JWT |
| GET        | `/api/v1.0/docker/installation/plan`           | —                                              | `DockerInstallationPlanDto`         | JWT |
| POST       | `/api/v1.0/docker/installation/execute`        | body: 安装选项                                     | Operation 式结果                       | JWT |
| GET        | `/api/v1.0/docker/containers`                  | query: filters/all                             | 容器 DTO\[]                           | JWT |
| POST       | `/api/v1.0/docker/containers`                  | 创建请求                                           | 容器 DTO（201）                         | JWT |
| GET        | `/api/v1.0/docker/containers/{id}`             | —                                              | 容器详情 DTO                            | JWT |
| DELETE     | `/api/v1.0/docker/containers/{id}`             | query: force/v                                 | 204                                 | JWT |
| POST       | `/api/v1.0/docker/containers/{id}/{action}`    | action ∈ start/stop/restart/pause/unpause/kill | 结果 DTO                              | JWT |
| GET        | `/api/v1.0/docker/containers/{id}/logs`        | query: tail/follow/stdout/stderr               | 文本或流式                               | JWT |
| GET        | `/api/v1.0/docker/containers/{id}/stats`       | —                                              | 容器统计 DTO                            | JWT |
| GET        | `/api/v1.0/docker/images`                      | query: filters/all/reference                   | 镜像 DTO\[]                           | JWT |
| POST       | `/api/v1.0/docker/images/pull`                 | body: 拉取请求（仓库+tag）+ 目标镜像源解析                    | Operation                           | JWT |
| DELETE     | `/api/v1.0/docker/images/{id}`                 | query: force/noprune                           | 删除结果                                | JWT |
| POST       | `/api/v1.0/docker/images/build`                | multipart: Dockerfile/tar 上下文 + 标签             | Build 结果                            | JWT |
| GET/POST   | `/api/v1.0/docker/images/{id}/export` / import | —                                              | tar 流 / 导入结果                        | JWT |
| GET        | `/api/v1.0/docker/networks`                    | query: filters                                 | 网络 DTO\[]                           | JWT |
| GET/DELETE | `/api/v1.0/docker/networks/{id}`               | —                                              | 网络详情 / 204                          | JWT |
| GET        | `/api/v1.0/docker/volumes`                     | query: filters                                 | 卷 DTO\[]                            | JWT |
| GET/DELETE | `/api/v1.0/docker/volumes/{name}`              | —                                              | 卷详情 / 204                           | JWT |
| POST       | `/api/v1.0/docker/stacks/validate`             | body: Compose YAML + 名称                        | 验证结果 DTO                            | JWT |
| GET        | `/api/v1.0/docker/stacks`                      | —                                              | Stack DTO\[]                        | JWT |
| POST       | `/api/v1.0/docker/stacks/deploy`               | body: StackDeployDto                           | Stack DTO（200/201）                  | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/services`      | —                                              | 服务 DTO\[]                           | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/definition`    | —                                              | Compose 原文                          | JWT |
| POST       | `/api/v1.0/docker/stacks/{name}/{action}`      | action ∈ start/stop/remove                     | 操作结果                                | JWT |

### Firewall（防火墙，Linux UFW）

路由常量见 `FirewallApiRoutes`。仅在 Linux 宿主 + UFW 可用时生效；Windows 返回 503。变更操作需要当前用户通过 PAM 重新认证（root 会话除外）。详见 [`RelaxKonOS.Firewall.md`](../applications/RelaxKonOS.Firewall.md)。

| 方法     | 路径                                | 请求                                                           | 响应                                               | 认证          |
| ------ | --------------------------------- | ------------------------------------------------------------ | ------------------------------------------------ | ----------- |
| GET    | `/api/v1.0/firewall/status`         | —                                                            | `FirewallStatusDto`（enabled、版本、默认策略、规则计数、活动概要）   | JWT         |
| GET    | `/api/v1.0/firewall/rules`          | —                                                            | `FirewallRuleDto[]`（编号、from/to/port/proto/动作/注释） | JWT         |
| POST   | `/api/v1.0/firewall/rules`          | body: AddFirewallRuleRequest                                 | 新规则 DTO（201）+ operation                          | JWT（PAM 提权） |
| DELETE | `/api/v1.0/firewall/rules/{number}` | —                                                            | 204 + operation                                  | JWT（PAM 提权） |
| PUT    | `/api/v1.0/firewall/enabled`        | body: `{ enabled: bool }`                                    | 状态结果 DTO                                         | JWT（PAM 提权） |
| PUT    | `/api/v1.0/firewall/defaults`       | body: DefaultFirewallPolicyRequest（incoming/outgoing/routed） | 状态结果 DTO                                         | JWT（PAM 提权） |

### Git（Git 客户端）

路由常量见 `GitApiRoutes`。服务端调用宿主 `git` CLI（`IHostGitCli`），仓库元数据持久化到 SQLite（按 Workspace 隔离）。详见 [`RelaxKonOS.GitClient.md`](../applications/RelaxKonOS.GitClient.md)。

| 方法             | 路径                                                         | 请求                                                   | 响应                        | 认证  |
| -------------- | ---------------------------------------------------------- | ---------------------------------------------------- | ------------------------- | --- |
| GET            | `/api/v1.0/git/engine/status`                                | —                                                    | 引擎状态 DTO（版本/是否安装）         | JWT |
| POST           | `/api/v1.0/git/engine/install`                               | —                                                    | 安装结果                      | JWT |
| GET            | `/api/v1.0/git/repositories`                                 | —                                                    | 仓库摘要 DTO\[]               | JWT |
| POST           | `/api/v1.0/git/repositories`                                 | body: CreateGitRepoRequest                           | 仓库 DTO（201）               | JWT |
| GET/DELETE     | `/api/v1.0/git/repositories/{id}`                            | —                                                    | 仓库详情 / 204                | JWT |
| GET            | `/api/v1.0/git/probe`                                        | query: path                                          | 路径是否已有 Git 仓库 + 摘要        | JWT |
| POST           | `/api/v1.0/git/init`                                         | body: path + initialBranch + bare?                   | 仓库 DTO（201）               | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/status`                     | —                                                    | 工作区状态 DTO（变更/暂存/冲突列表）     | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/branches`                   | query: remotes?                                      | 分支 DTO\[]                 | JWT |
| GET/DELETE     | `/api/v1.0/git/repositories/{id}/branches/{name}`            | —                                                    | 分支详情 / 204                | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/branches/{name}/rename`     | body: newName                                        | 分支 DTO                    | JWT |
| PUT            | `/api/v1.0/git/repositories/{id}/branches/{name}/tracking`   | body: remote + remoteBranch                          | 跟踪设置结果                    | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/branches/{name}/comparison` | query: base                                          | A/B 差异 DTO                | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/checkout`                   | body: ref（branch/tag/commit）+ b?（新建）                 | 检出结果 DTO                  | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/stage`                      | body: paths\[] 或 "."                                 | 暂存结果                      | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/unstage`                    | body: paths\[]                                       | 取消暂存结果                    | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/commit`                     | body: message + author + amend?                      | 提交 DTO（201）               | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/fetch`                      | body: remote?                                        | 抓取结果                      | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/pull`                       | body: remote + branch + rebase?                      | 合并/变基结果 DTO               | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/push`                       | body: remote + branch + force? + setUpstream?        | 推送结果（含凭据请求 401）           | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/log`                        | query: limit/skip/branch/author                      | 提交摘要 DTO\[]（分页）           | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/commits/{sha}`              | —                                                    | 完整提交 DTO（含父提交、作者、消息、变更统计） | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/diff`                       | query: from/to/path/cached?                          | 统一差异 DTO\[]               | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/merge`                      | body: source（分支/提交）+ noCommit? + strategy?           | 合并结果 DTO（可能返回冲突列表）        | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/revert`                     | body: sha                                            | 还原结果 DTO                  | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/reset`                      | body: mode（soft/mixed/hard）+ target（commit/branch）   | 重置结果                      | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/restore`                    | body: paths\[] + source（staged/HEAD/commit）+ staged? | 恢复结果                      | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/resolve`                    | body: ResolveRequest（冲突路径 + 策略 theirs/ours/内容）       | 冲突解决结果                    | JWT |
| GET            | `/api/v1.0/git/repositories/{id}/remotes`                    | —                                                    | 远程 DTO\[]                 | JWT |
| POST           | `/api/v1.0/git/repositories/{id}/remotes`                    | body: CreateRemoteRequest（name + url）                | 远程 DTO（201）               | JWT |
| GET/PUT/DELETE | `/api/v1.0/git/repositories/{id}/remotes/{name}`             | —                                                    | 远程详情 / 更新 URL / 删除        | JWT |

### Tunnels（FRP 隧道管理）

路由常量见 `TunnelApiRoutes`。服务端 `ITunnelService` + `FrpTunnelProvider` 管理 FRP profiles、隧道定义、运行时安装/管理、托管 frps 与审计。详见 [FRP 集成文档系列](../applications/RelaxKonOS.FRP_Integration.Goal.md) 与 [`RelaxKonOS.FRP_Integration.Implementation.md`](../applications/RelaxKonOS.FRP_Integration.Implementation.md)。

**Profiles（用户级 FRP 连接配置）**

| 方法     | 路径                                            | 请求                              | 响应                                       | 认证                                    |
| ------ | --------------------------------------------- | ------------------------------- | ---------------------------------------- | ------------------------------------- |
| GET    | `/api/v1.0/tunnels/profiles`                    | —                               | TunnelProfileDto\[]（含摘要状态）               | JWT                                   |
| GET    | `/api/v1.0/tunnels/profiles/{profileId}`        | —                               | TunnelProfileDto 详情                      | JWT                                   |
| POST   | `/api/v1.0/tunnels/profiles`                    | body: CreateProfileRequest      | Profile（201）                             | JWT                                   |
| PUT    | `/api/v1.0/tunnels/profiles/{profileId}`        | body: UpdateProfileRequest      | Profile                                  | JWT                                   |
| DELETE | `/api/v1.0/tunnels/profiles/{profileId}`        | —                               | 204                                      | JWT                                   |
| POST   | `/api/v1.0/tunnels/profiles/{profileId}/secret` | multipart/form-data：token 或凭据文件 | Secret 保存结果（仅返回存储版本/时间）                  | JWT（Secret 经 ISecretStore 加密存储，不回传明文） |
| DELETE | `/api/v1.0/tunnels/profiles/{profileId}/secret` | —                               | 204                                      | JWT                                   |
| POST   | `/api/v1.0/tunnels/profiles/{profileId}/apply`  | —                               | Apply 结果 DTO（启动 frpc、PID、状态快照）或 409（已运行） | JWT                                   |
| POST   | `/api/v1.0/tunnels/profiles/{profileId}/stop`   | —                               | Stop 结果（进程终止确认）                          | JWT                                   |
| GET    | `/api/v1.0/tunnels/profiles/{profileId}/logs`   | query: tail（默认 200）             | 文本日志行 DTO\[]                             | JWT                                   |

**Runtime（FRP 运行时安装 / 外部检测）**

| 方法     | 路径                                                  | 请求                                           | 响应                       | 认证                  |
| ------ | --------------------------------------------------- | -------------------------------------------- | ------------------------ | ------------------- |
| GET    | `/api/v1.0/tunnels/runtime/managed/install/status`    | —                                            | 安装状态 DTO（版本/路径/完整性）      | JWT                 |
| POST   | `/api/v1.0/tunnels/runtime/managed/install`           | query: version?（默认 latest stable）+ platform? | 下载+安装 operation          | JWT（HostGlobal 管理员） |
| POST   | `/api/v1.0/tunnels/runtime/managed/install/from-file` | multipart: tar.gz/zip 安装包                    | 安装 operation             | JWT（HostGlobal 管理员） |
| DELETE | `/api/v1.0/tunnels/runtime/managed`                   | —                                            | 卸载 operation（保留配置）       | JWT（HostGlobal 管理员） |
| POST   | `/api/v1.0/tunnels/runtime/managed/rollback`          | —                                            | 回滚到上一版本 operation        | JWT（HostGlobal 管理员） |
| GET    | `/api/v1.0/tunnels/runtime/external/detect`           | —                                            | 检测系统级 frpc/frps（PATH、版本） | JWT                 |

**Managed Frps（托管 FRP Server 进程，仅本机回环或受控绑定）**

| 方法   | 路径                            | 请求                                             | 响应                                              | 认证                  |
| ---- | ----------------------------- | ---------------------------------------------- | ----------------------------------------------- | ------------------- |
| GET  | `/api/v1.0/tunnels/frps/editor` | —                                              | 当前 frps.toml DTO（结构化配置对象，非原始 TOML）              | JWT（HostGlobal 管理员） |
| PUT  | `/api/v1.0/tunnels/frps/editor` | body: FrpsConfigDto（结构化，经 TunnelValidation 校验） | 写入+校验结果                                         | JWT（HostGlobal 管理员） |
| POST | `/api/v1.0/tunnels/frps/start`  | —                                              | 启动 operation + PID                              | JWT（HostGlobal 管理员） |
| POST | `/api/v1.0/tunnels/frps/stop`   | —                                              | 停止 operation                                    | JWT（HostGlobal 管理员） |
| GET  | `/api/v1.0/tunnels/frps/logs`   | query: tail                                    | frps 日志 DTO\[]                                  | JWT（HostGlobal 管理员） |
| GET  | `/api/v1.0/tunnels/frps/audit`  | query: limit/skip                              | TunnelAuditEntryDto\[]（连接建立/断开/拒绝事件，持久化 SQLite） | JWT（HostGlobal 管理员） |

### Certificates / WebServers（V1 后端）

证书与 Web Server 的 HostGlobal 后端已实现；具体资源模型见 [`RelaxKonOS.CertificateManager.md`](../applications/RelaxKonOS.CertificateManager.md) 与 [`RelaxKonOS.WebServerManager.Design.md`](../applications/RelaxKonOS.WebServerManager.Design.md)。证书 API 提供元数据读取、预检、签发、续期、Kestrel 部署、删除、撤销和 operation 查询/取消；Web Server API 提供 Nginx 发现、状态、配置测试、最小集成、重载和 operation 查询/取消。所有变更请求：

* 所有变更请求携带 `Idempotency-Key`，返回 `OperationDto`（操作 ID、状态、阶段、稳定问题码、时间、可选快照 ID）。

* `CertificateApiRoutes` 与 `WebServerApiRoutes` 只定义 `/api/v1.0` 路径常量；Endpoint、Client 和 UI 不重复字面量。

* 当前单机管理员模式下，资源为 HostGlobal，不引入 User/Workspace 路径参数；需要管理员运行状态才能执行变更。

* Operation 查询、取消和后续进度事件使用 Protocol 契约，不能让 UI 通过日志文本推断状态。

**Certificates（证书 ACME 管理，路由见 CertificateApiRoutes）**

| 方法     | 路径                                              | 请求                                                        | 响应                                         | 认证                                    |
| ------ | ----------------------------------------------- | --------------------------------------------------------- | ------------------------------------------ | ------------------------------------- |
| GET    | `/api/v1.0/certificates/records`                  | —                                                         | CertificateRecordDto\[]（规范化元数据 + 受保护引用）    | JWT（HostGlobal 管理员）                   |
| GET    | `/api/v1.0/certificates/records/{id}`             | —                                                         | 单证书详情 + 部署历史摘要                             | JWT（HostGlobal 管理员）                   |
| POST   | `/api/v1.0/certificates/records/{id}/precheck`    | body: 挑战方式 + 域名列表 + 可选部署目标                                | PrecheckResultDto（可达性、DNS、端口、问题列表）         | JWT（HostGlobal 管理员）                   |
| POST   | `/api/v1.0/certificates/records/issue`            | body: IssueCertificateRequest（ACME account、域名、挑战类型、密钥算法等） | OperationDto（签发异步）                         | JWT（HostGlobal 管理员 + Idempotency-Key） |
| POST   | `/api/v1.0/certificates/records/{id}/renew`       | body: 可选新配置                                               | OperationDto（续期异步）                         | JWT（HostGlobal 管理员 + Idempotency-Key） |
| POST   | `/api/v1.0/certificates/records/{id}/deploy`      | body: DeployTarget（kestrel/nginx/iis/apache + 目标名）        | OperationDto（部署到指定前端）                      | JWT（HostGlobal 管理员 + Idempotency-Key） |
| POST   | `/api/v1.0/certificates/records/{id}/revoke`      | body: RevokeReason                                        | OperationDto（吊销）                           | JWT（HostGlobal 管理员 + Idempotency-Key） |
| DELETE | `/api/v1.0/certificates/records/{id}`             | —                                                         | OperationDto（删除元数据 + 受保护 PEM 引用；数据库绝不保存私钥） | JWT（HostGlobal 管理员 + Idempotency-Key） |
| GET    | `/api/v1.0/certificates/operations`               | query: status/limit                                       | OperationDto\[]（查询操作状态）                    | JWT（HostGlobal 管理员）                   |
| GET    | `/api/v1.0/certificates/operations/{opId}`        | —                                                         | OperationDto 详情                            | JWT（HostGlobal 管理员）                   |
| POST   | `/api/v1.0/certificates/operations/{opId}/cancel` | —                                                         | 取消结果（支持操作中止语义）                             | JWT（HostGlobal 管理员）                   |

**WebServers（Nginx 集成，路由见 WebServerApiRoutes）**

| 方法             | 路径                                            | 请求                                                        | 响应                                                      | 认证                                    |
| -------------- | --------------------------------------------- | --------------------------------------------------------- | ------------------------------------------------------- | ------------------------------------- |
| GET            | `/api/v1.0/webservers/instances`                | —                                                         | NginxInstanceDto\[]（发现/检测结果：版本、二进制、配置路径、状态、systemd/SCM） | JWT（HostGlobal 管理员）                   |
| GET            | `/api/v1.0/webservers/instances/{id}`           | —                                                         | 实例详情 + 当前运行时统计                                          | JWT（HostGlobal 管理员）                   |
| POST           | `/api/v1.0/webservers/instances/{id}/reload`    | —                                                         | OperationDto（重载配置，失败回滚）                                 | JWT（HostGlobal 管理员 + Idempotency-Key） |
| POST           | `/api/v1.0/webservers/instances/{id}/test`      | —                                                         | ConfigTestResultDto（nginx -t 结构化输出）                     | JWT（HostGlobal 管理员）                   |
| POST           | `/api/v1.0/webservers/instances/{id}/integrate` | body: IntegrationRequest（Kestrel 上游、证书关联、最小 server block） | OperationDto（最小侵入集成 + 回滚点）                              | JWT（HostGlobal 管理员 + Idempotency-Key） |
| GET            | `/api/v1.0/webservers/sites`                    | —                                                         | WebServerSiteDto\[]（站点列表：域名、根、上游、证书、监听）                 | JWT（HostGlobal 管理员）                   |
| GET/PUT/DELETE | `/api/v1.0/webservers/sites/{id}`               | body: SiteConfigDto                                       | 站点详情 / 更新 / 删除                                          | JWT（HostGlobal 管理员 + Idempotency-Key） |
| GET            | `/api/v1.0/webservers/operations`               | query: status/limit                                       | OperationDto\[]                                         | JWT（HostGlobal 管理员）                   |
| GET            | `/api/v1.0/webservers/operations/{opId}`        | —                                                         | OperationDto 详情                                         | JWT（HostGlobal 管理员）                   |
| POST           | `/api/v1.0/webservers/operations/{opId}/cancel` | —                                                         | 取消结果                                                    | JWT（HostGlobal 管理员）                   |

### 注册表（Registry）

路由与 app-settings 共用端点前缀（`/api/v1.0/app-settings/*`），通过 `RegistryApiRoutes` 区分子路径；注册表数据按 Workspace 存 SQLite，Schema 受约束。详见 [`RelaxKonOS.Registry.md`](../architecture/RelaxKonOS.Registry.md) 与 [`RelaxKonOS.RegistryApp.md`](../applications/RelaxKonOS.RegistryApp.md)。

| 方法     | 路径                                         | 请求                                            | 响应                                       | 认证       |
| ------ | ------------------------------------------ | --------------------------------------------- | ---------------------------------------- | -------- |
| GET    | `/api/v1.0/app-settings/registry/schema`     | —                                             | RegistrySchemaDto（允许的 key 路径、值类型、默认值、约束） | JWT（按归属） |
| GET    | `/api/v1.0/app-settings/registry/keys`       | query: parentKey（空=根）                         | RegistryKeyBrowseDto\[]（子键列表）            | JWT（按归属） |
| GET    | `/api/v1.0/app-settings/registry/values`     | query: key                                    | RegistryValueDto\[]（值列表：name/type/value） | JWT（按归属） |
| POST   | `/api/v1.0/app-settings/registry/keys`       | body: CreateRegistryKeyRequest（受 schema 校验）   | RegistryKeyDto（201）                      | JWT（按归属） |
| PUT    | `/api/v1.0/app-settings/registry/values`     | body: UpsertRegistryValueRequest（受 schema 校验） | RegistryValueDto                         | JWT（按归属） |
| DELETE | `/api/v1.0/app-settings/registry/keys/{key}` | query: recursive?                             | 204 或删除确认                                | JWT（按归属） |
| DELETE | `/api/v1.0/app-settings/registry/values`     | query: key + name                             | 204                                      | JWT（按归属） |

### 应用私有配置（AppSettings）

详见 [`RelaxKonOS.AppSettings.md`](../development/RelaxKonOS.AppSettings.md)。按 User + Scope（Workspace/User/Document）+ ScopeId + AppId + Key 隔离；revision 乐观并发。

| 方法     | 路径                           | 请求                                              | 响应                                       | 认证       |
| ------ | ---------------------------- | ----------------------------------------------- | ---------------------------------------- | -------- |
| GET    | `/api/v1.0/app-settings/entry` | query: scope/scopeId/appId/key                  | AppSettingEntryDto 或 404                 | JWT（按归属） |
| PUT    | `/api/v1.0/app-settings/entry` | body: UpsertAppSettingRequest（含 revision，新项为 0） | AppSettingEntryDto（并发冲突返回 409 + current） | JWT（按归属） |
| GET    | `/api/v1.0/app-settings/list`  | query: scope/scopeId/appId + prefix?            | AppSettingEntryDto\[]                    | JWT（按归属） |
| DELETE | `/api/v1.0/app-settings/entry` | query: scope/scopeId/appId/key + ifRevision?    | 204 或 409                                | JWT（按归属） |

### 应用能力（App Capabilities）

声明应用所需的文件/终端/网络等能力，由 Settings 的应用权限页管理；按 Workspace 持久化。

| 方法   | 路径                                                | 请求                                                 | 响应                                               | 认证       |
| ---- | ------------------------------------------------- | -------------------------------------------------- | ------------------------------------------------ | -------- |
| GET  | `/api/v1.0/app-settings/capabilities`               | query: appId?（空=全部）                                | AppCapabilityDto\[]（能力声明 + 当前授权状态）               | JWT（按归属） |
| GET  | `/api/v1.0/app-settings/capabilities/declarations`  | query: appId（内置或已安装包）                              | CapabilityDeclarationDto\[]（应用 manifest 中的声明，只读） | JWT（按归属） |
| PUT  | `/api/v1.0/app-settings/capabilities/{appId}`       | body: UpdateAppCapabilitiesRequest（授予/撤销的能力 id 集合） | AppCapabilityDto\[]（更新后的授权快照）                    | JWT（按归属） |
| POST | `/api/v1.0/app-settings/capabilities/{appId}/reset` | —                                                  | 重置为默认值（通常全部拒绝）                                   | JWT（按归属） |

### 镜像源（Image Mirrors）

Docker 拉取镜像时的加速前缀；按 User + Target（如 docker）隔离。详情见 Storage.md §5.5 与 Docker Manager 文档。

| 方法     | 路径                                  | 请求                                                               | 响应                                       | 认证       |
| ------ | ----------------------------------- | ---------------------------------------------------------------- | ---------------------------------------- | -------- |
| GET    | `/api/v1.0/image-mirrors`             | query: target（默认 docker）                                         | ImageMirrorDto\[] + 当前选中项                | JWT（按归属） |
| POST   | `/api/v1.0/image-mirrors`             | body: CreateImageMirrorRequest（name/endpoint/target/isSelected?） | ImageMirrorDto（201）                      | JWT（按归属） |
| PUT    | `/api/v1.0/image-mirrors/{id}`        | body: UpdateImageMirrorRequest                                   | ImageMirrorDto                           | JWT（按归属） |
| DELETE | `/api/v1.0/image-mirrors/{id}`        | —                                                                | 204                                      | JWT（按归属） |
| POST   | `/api/v1.0/image-mirrors/{id}/select` | query: target                                                    | 设置为当前目标服务的选中镜像源；清空选中用 `select` + id=none | JWT（按归属） |

### 进程守护（Process Guardian）

路由见 `ProcessGuardianApiRoutes`。通过命名管道 IPC 与独立 Guardian Agent 通信；日志经 GuardianLogs Hub 广播。详见 [`RelaxKonOS.ProcessGuardian.md`](../applications/RelaxKonOS.ProcessGuardian.md)。

| 方法             | 路径                                        | 请求                                              | 响应                                           | 认证                                    |
| -------------- | ----------------------------------------- | ----------------------------------------------- | -------------------------------------------- | ------------------------------------- |
| GET            | `/api/v1.0/guardian/status`                 | —                                               | GuardianStatusDto（Agent 版本、管道状态、工作负载计数、健康摘要） | JWT（按归属）                              |
| GET            | `/api/v1.0/guardian/workloads`              | —                                               | WorkloadDto\[]（声明持久化到 SQLite）                | JWT（按归属）                              |
| POST           | `/api/v1.0/guardian/workloads`              | body: CreateWorkloadRequest（命令、参数、环境、重启策略、健康检查） | WorkloadDto（201）+ 写入 SQLite 声明 + 通知 Agent    | JWT（按归属）                              |
| GET/PUT/DELETE | `/api/v1.0/guardian/workloads/{id}`         | —                                               | 详情 / 更新声明 / 204（含停止实例）                       | JWT（按归属）                              |
| POST           | `/api/v1.0/guardian/workloads/{id}/start`   | —                                               | 启动操作结果                                       | JWT（按归属）                              |
| POST           | `/api/v1.0/guardian/workloads/{id}/stop`    | query: killAfterSeconds?                        | 停止操作结果                                       | JWT（按归属）                              |
| POST           | `/api/v1.0/guardian/workloads/{id}/restart` | —                                               | 重启操作结果                                       | JWT（按归属）                              |
| GET            | `/api/v1.0/guardian/agent/install-status`   | —                                               | 安装状态 DTO（是否安装、版本、路径、systemd/SCM 服务状态）        | JWT（HostGlobal 管理员）                   |
| POST           | `/api/v1.0/guardian/agent/install`          | —                                               | 安装 operation（部署 Guardian.Agent 并注册原生服务）      | JWT（HostGlobal 管理员 + Idempotency-Key） |
| POST           | `/api/v1.0/guardian/agent/uninstall`        | —                                               | 卸载 operation（保留工作负载声明）                       | JWT（HostGlobal 管理员 + Idempotency-Key） |

### 健康检查（Health）

公共端点，无需鉴权：

| 方法  | 路径              | 说明                                                              |
| --- | --------------- | --------------------------------------------------------------- |
| GET | `/health`       | 200 `{ status: "healthy", version, timestamp }` — 进程存活 + 基本依赖就绪 |
| GET | `/health/ready` | 200 或 503：数据库、可选守护管道、SignalR 背板等关键依赖就绪状态 + `ProblemDetails` 列表  |

***

## 6. SignalR Hub 契约

Hub 路径 `/hubs/workspace`。Server 端实现 `WorkspaceHub : Hub<IWorkspaceHubClient>` 获得编译期校验。

### Client → Server（invoke，方法名见 `WorkspaceHubMethods`）

| 方法                       | 参数                      | 返回                     | 仅 Controller |
| ------------------------ | ----------------------- | ---------------------- | ------------ |
| `JoinWorkspace`          | `JoinWorkspaceRequest`  | `WorkspaceSnapshotDto` | 否            |
| `LeaveWorkspace`         | —                       | void                   | 否            |
| `SendDesktopStateChange` | `DesktopStatePatch`     | void                   | 是            |
| `RequestControl`         | `RequestControlRequest` | `ControllerLeaseInfo`  | 否            |
| `ReleaseControl`         | —                       | void                   | 是            |
| `Heartbeat`              | —                       | void                   | 否            |

### Server → Client（on，事件名见 `WorkspaceHubEvents`，接口 `IWorkspaceHubClient`）

* `OnDesktopStateChanged(DesktopStatePatch)`

* `OnControllerChanged(ControllerChangedEventArgs)`

* `OnDeviceConnected(DevicePresenceEventArgs)`

* `OnDeviceDisconnected(DevicePresenceEventArgs)`

* `OnSessionUpdated(SessionDto)`

* `OnWorkspaceStateChanged(WorkspaceState)`

**未设计** **`SendInput`**：RelaxKonOS 是状态同步模式，Controller 输入通过本地应用状态变更 + 状态同步体现，不在 workspace hub 传原始键鼠。

### Terminal Hub（`/hubs/terminals`）

远端 PTY 字节流中继。Server 端实现 `TerminalHub : Hub<ITerminalHubClient>`，PTY 由 `TerminalSessionManager`（Singleton）持有，与 Hub 连接解耦——连接断开仅 `Detach`，**保留 PTY**。详见 [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md)。

#### Client → Server（invoke，方法名见 `TerminalHubMethods`）

| 方法             | 参数                                                      | 返回                                            | 说明                                                           |
| -------------- | ------------------------------------------------------- | --------------------------------------------- | ------------------------------------------------------------ |
| `Start`        | `StartTerminalRequest req, string? sessionId = null`    | `AttachTerminalResponse {SessionId, Created}` | sessionId 命中且属于当前用户且未退出则**附加**（先回放 1MB 缓冲快照），否则**新建** PTY 会话 |
| `Input`        | `byte[]`                                                | void                                          | 转发到 `session.Pty.Write(data)`                                |
| `Resize`       | `int cols, int rows, int widthPixels, int heightPixels` | void                                          | 转发到 `session.Pty.Resize(...)`                                |
| `Close`        | —                                                       | void                                          | `manager.Remove` —— **手动终止**（杀 PTY），对应关闭终端窗口 / "断开"按钮        |
| `ListSessions` | —                                                       | `TerminalSessionInfo[]`                       | 返回当前用户全部终端会话摘要（多实例）                                          |

#### Server → Client（on，事件名见 `TerminalHubEvents`，接口 `ITerminalHubClient`）

* `OnOutput(byte[] data)`：PTY 输出字节（始终追加进 1MB 环形缓冲；有附加连接时经 `IHubContext` 转发）

* `OnProcessExited(int exitCode)`：子进程退出

> **方法名对齐**：Server Hub 方法名必须与 `TerminalHubMethods` 常量完全一致（`Start` 非 `StartTerminal`），否则 SignalR 运行时找不到方法。`OnDisconnectedAsync` 调 `session.Detach(Context.ConnectionId)` 保留 PTY；仅显式 `Close` 才杀。`TerminalUserIdProvider`（`IUserIdProvider`）以 JWT `sub` claim 作 `Context.UserIdentifier`，按用户过滤会话。

### Performance Hub（`/hubs/performance`）

服务端统一采样器（`PerformanceSampler`，Singleton，`ISystemPerformanceSource` 跨平台采样：Windows/Linux）**每秒**广播 `PerformanceRealtimeSnapshotDto`，并保留最近 60 秒内存历史，供客户端以 REST `performance/history` 回补重连空洞。详见 [`RelaxKonOS.TaskManager.Rewrite.md`](../applications/RelaxKonOS.TaskManager.Rewrite.md)。

**无 Client→Server invoke 方法**（纯推模式，无需订阅确认；客户端仅需建立 Hub 连接即可接收）。但方法名常量仍集中在 `PerformanceHubMethods`（预留）。

#### Server → Client（on，事件名见 `PerformanceHubEvents`，接口 `IPerformanceHubClient`）

* `OnSnapshot(PerformanceRealtimeSnapshotDto dto)`：每秒广播一次

  * 包含：`Timestamp`、采样间隔、`CpuUsageDto`（总 CPU + 核/频率）、`MemoryUsageDto`（总/可用/已用/缓存/Swap）、`FilesystemUsageDto[]`（每个挂载点容量、inode、使用率）、`DiskUsageDto[]`（磁盘读写速率、队列长度、IOPS）、`NetworkUsageDto[]`（网卡收发包/字节/速率/丢包/错包）、`GpuUsageDto[]`（GPU 利用率、显存、温度、功率）、`NetworkAddressDto[]`（每个网卡 IP 地址）

  * 平台不支持的指标子项以 `null` 或空数组返回（绝不伪造 0 值）

  * `MaximumReceiveMessageSize = null` 以容纳包含多网卡多磁盘的大块快照

> **连接语义**：客户端首次建立 Performance Hub 连接后立即订阅广播；重连时应先调 REST `performance/history?seconds=60` 拉取历史，再从 Hub 实时衔接，避免数据空洞。采样器在 Server 启动时即开始采样，不依赖客户端连接（首样本前 `performance/snapshot` 返回 503）。

### GuardianLogs Hub（`/hubs/guardian-logs`）

Process Guardian 守护日志的实时广播。客户端通过 `Subscribe/Unsubscribe` 按工作负载 ID 或全部订阅；服务端 `GuardianLogBroadcastService` + `GuardianLogSubscriptionRegistry`（Singleton）维护订阅列表。详见 [`RelaxKonOS.ProcessGuardian.md`](../applications/RelaxKonOS.ProcessGuardian.md)。

#### Client → Server（invoke，方法名见 `GuardianLogsHubMethods`）

| 方法            | 参数                                                         | 返回   | 说明                                |
| ------------- | ---------------------------------------------------------- | ---- | --------------------------------- |
| `Subscribe`   | `SubscribeGuardianLogsRequest { workloadId: Guid? }`（空=全部） | void | 订阅 Guardian 日志事件；可同时订阅多个 workload |
| `Unsubscribe` | `SubscribeGuardianLogsRequest` 或空（空=取消全部）                  | void | 取消订阅                              |

#### Server → Client（on，事件名见 `GuardianLogsHubEvents`，接口 `IGuardianLogsHubClient`）

* `OnLogEntry(GuardianLogEntryDto dto)`：结构化日志事件

  * 字段：`WorkloadId`（Guid，可空=系统级日志）、`Level`（Trace/Debug/Info/Warning/Error/Critical）、`Message`、`Timestamp`、`Category`（Agent/Pipe/Supervisor/NativeService/Healthcheck）、`Exception`（可空）、`Properties`（可选字典，如 PID、退出码）

> **连接语义**：Hub 使用 JWT `sub` claim 校验工作负载归属；HostGlobal 管理员级日志（如 Agent 安装进度）单独通过系统级事件广播。重连不补发历史日志；需要完整历史的客户端从 REST `/guardian/workloads/{id}` 或专用日志端点读取（由 ProcessGuardian 实现决定）。

***

## 7. 认证集成

* 登录返回 `AuthTokens`（AccessToken + RefreshToken）

* REST：`Authorization: Bearer <accessToken>`

* SignalR：连接时携带 token（query string 或 header），Server 端 `IUserIdProvider` + JWT 中间件解析，连接建立时绑定到 Session/Device/Workspace 并加入对应 Group

* Controller/Observer 协调在 SignalR Hub 层完成（`RequestControl` / `ReleaseControl` + `OnControllerChanged` 广播）

***

## 8. Terminal 传输（已实现）

RemoteTerminal 的 PTY 流传输**已在 Protocol 契约内**，走 SignalR Hub `/hubs/terminals`（见 §6 Terminal Hub）。契约文件位于 `Shared/RelaxKonOS.Protocol/Hubs/`：

| 文件                          | 职责                                                                     |
| --------------------------- | ---------------------------------------------------------------------- |
| `ITerminalHubClient.cs`     | server→client 接口（`OnOutput`/`OnProcessExited`）                         |
| `TerminalHubEvents.cs`      | server→client 事件名常量                                                    |
| `TerminalHubMethods.cs`     | client→server 方法名常量（`Start`/`Input`/`Resize`/`Close`/`ListSessions`）   |
| `StartTerminalRequest.cs`   | 启动请求 DTO（columns/rows/widthPixels/heightPixels/shell/workingDirectory） |
| `AttachTerminalResponse.cs` | `Start` 返回值（`SessionId` + `Created`）                                   |
| `TerminalSessionInfo.cs`    | 会话摘要 DTO（`ListSessions` 用）                                             |

**实现要点**：

* RoyalTerminal（`royalapplications/RoyalTerminal`）是传输无关的终端 UI 栈，通过 `ITerminalTransport` 抽象开放传输方式。RelaxKonOS 用 `RoyalApps.RoyalTerminal.Avalonia` 作为终端控件 + 自实现 `SignalRTerminalTransport`（`ITerminalTransport`）适配器，位于 `Client/RelaxKonOS.Client/Apps/`。

* 传输层未引入裸 WebSocket 端点（选 SignalR：JWT + 强类型 Hub + 一次性连接拉取列表）。

* 不启用 `WithAutomaticReconnect`（自动重连后服务端不会自动重新附加会话）；恢复路径是"再次登录打开终端 → 重新 `Start(Attach)` → 回放 1MB 缓冲快照"。

* `MaximumReceiveMessageSize = null` 解除 SignalR 默认 32KB 上限，允许大块 PTY 输出与 1MB 缓冲快照单帧传输。

完整实现细节（Hub 行为、断开语义、会话生命周期、焦点修复等）见 [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md)。

***

## 9. AI Agent Rules

修改 Protocol 层时：

**必须**：

* 保持 Protocol 零 PackageReference（纯契约）

* 所有 DTO 公开成员加 `[property: JsonPropertyName]`

* 路由字符串集中在 `*ApiRoutes` 静态类，不散落（新增：`DockerApiRoutes` / `GitApiRoutes` / `FirewallApiRoutes` / `TunnelApiRoutes` / `CertificateApiRoutes` / `WebServerApiRoutes` / `ProcessGuardianApiRoutes` / `SystemMonitorApiRoutes` 等）

* Hub 方法名/事件名用 `WorkspaceHubMethods` / `WorkspaceHubEvents` / `TerminalHubMethods` / `TerminalHubEvents` / `PerformanceHubMethods` / `PerformanceHubEvents` / `GuardianLogsHubMethods` / `GuardianLogsHubEvents` 常量，不用字面量

* 枚举值与文档（Authentication.md / Security.md / Workspace.md / 各应用文档）一致

* Workspace 偏好 JSON 列的可变集合（如 `DefaultApps`）**必须**用 `List<T>` 且保留公开 setter，不能以新集合整体替换（EF Core JSON 子项以合成序号追踪）

* 所有 Hub 路径常量集中在 `RemoteOsEndpoints`（`WorkspaceHubPath` / `PerformanceHubPath` / `GuardianLogsHubPath`），Endpoint、Client、UI 三方共享

**禁止**：

* 在 Protocol 引入 `Microsoft.AspNetCore.SignalR.Client` / `HttpClient` 等实现包

* 在 Protocol 引用 `RelaxKonOS.Core`（线协议与 Core 解耦）

* 业务代码直接调用 HTTP / WebSocket（必须经 Protocol 契约）

* 把 Server 端 OS 抽象（`IIdentityProvider`、`ISystemMetricsProvider`、`IDockerEngineService`、`IHostFirewallService`、`IHostGitCli`、`ITunnelService`、`IWebServerManager`、`IFileService`、`IProcessGuardianService` 等）放进 Protocol

* 在 Protocol 中硬编码宿主机路径、OS 专用配置或安全敏感内容（私钥、ACME account key、凭据明文）

**新增模块约束**：

* **证书**：Protocol 只包含规范化元数据和受保护文件引用，绝不包含私钥 PEM、account key、导入密码、DNS-01 token 明文。

* **FRP 隧道**：Secret 相关 DTO 仅描述存储元数据（版本/更新时间/是否有值），不包含 token 明文；明文通过 multipart 单独投递，经服务端 `ISecretStore` 加密后落盘。

* **HostGlobal 资源**（Certificates / WebServers / FRP Runtime 安装 / Guardian Agent 安装）：契约不包含 User 或 Workspace 路径参数；在文档中标明"单机管理员模式"授权边界。

* **防火墙变更**：所有变更端点在契约层的 DTO 注释中声明需要 PAM 二次校验；Windows 平台调用方应处理 503 降级。

* **Git 凭据**：Protocol 不直接传输 Git HTTPS 密码或 SSH 私钥；登录凭据走平台安全存储 `ISecretStore`，推送时服务端如缺失凭据返回 401 + `ProblemDetails.type = git-credentials-required`，客户端另行投递（由 Endpoint 实现验证）。

***

## 10. 相关文档

| 文档                                                                                                                                                                                                                                             | 用途                                                   |
| ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| [`RelaxKonOS.Architecture.md`](./RelaxKonOS.Architecture.md)                                                                                                                                                                                       | 模块定位、依赖约束、架构原则、Server OS 抽象层全景                       |
| [`RelaxKonOS.Authentication.md`](../platform/RelaxKonOS.Authentication.md)                                                                                                                                                                         | 登录、身份模型、User/Session/Device 表                        |
| [`RelaxKonOS.Login.md`](../platform/RelaxKonOS.Login.md)                                                                                                                                                                                           | 登录模块：auth 端点、JWT、IIdentityProvider、登录保护              |
| [`RelaxKonOS.Workspace.md`](./RelaxKonOS.Workspace.md)                                                                                                                                                                                             | Workspace 生命周期、Controller/Observer、偏好字段（主题/显示/编码/布局） |
| [`RelaxKonOS.Security.md`](../platform/RelaxKonOS.Security.md)                                                                                                                                                                                     | Session 安全、权限提升（PAM/UAC）、HostGlobal 管理员模式            |
| [`RelaxKonOS.Storage.md`](../platform/RelaxKonOS.Storage.md)                                                                                                                                                                                       | EF Core + SQLite 持久化、全量表结构、仓储层、HostGlobal 迁移         |
| [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md)                                                                                                                                                                                 | Terminal Hub 实现、持久会话、断开语义                            |
| [`RelaxKonOS.Explorer.md`](../applications/RelaxKonOS.Explorer.md)                                                                                                                                                                                 | 文件管理端点实现、宿主 OS 权限复用、特殊目录/POSIX 权限                    |
| [`RelaxKonOS.Browser.md`](../applications/RelaxKonOS.Browser.md)                                                                                                                                                                                   | 浏览器端点、BrowserSettings 持久化、loopback 端口转发              |
| [`RelaxKonOS.Settings.md`](../desktop/RelaxKonOS.Settings.md)                                                                                                                                                                                      | Workspace 偏好端点、PreferencesSync 多设备同步、主题调色板导入         |
| [`RelaxKonOS.TaskManager.md`](../applications/RelaxKonOS.TaskManager.md)                                                                                                                                                                           | 系统监控端点（兼容 metrics）、跨平台 ISystemMetricsProvider        |
| [`RelaxKonOS.TaskManager.Rewrite.md`](../applications/RelaxKonOS.TaskManager.Rewrite.md)                                                                                                                                                           | Performance Hub 推送设计、跨平台 PerformanceSource、进程分页查询    |
| [`RelaxKonOS.DockerManager.md`](../applications/RelaxKonOS.DockerManager.md)                                                                                                                                                                       | Docker 引擎/容器/镜像/网络/卷/Stack 端点、镜像源解析                  |
| [`RelaxKonOS.Firewall.md`](../applications/RelaxKonOS.Firewall.md)                                                                                                                                                                                 | Linux UFW 防火墙状态、规则、默认策略、PAM 提权变更                     |
| [`RelaxKonOS.GitClient.md`](../applications/RelaxKonOS.GitClient.md)                                                                                                                                                                               | Git 引擎、仓库、分支、提交、合并/变基、远程管理端点                         |
| [`RelaxKonOS.FRP_Integration.Goal.md`](../applications/RelaxKonOS.FRP_Integration.Goal.md) / [`Design.md`](../applications/RelaxKonOS.FRP_Integration.Design.md) / [`Implementation.md`](../applications/RelaxKonOS.FRP_Integration.Implementation.md) | 隧道管理（Profiles/Runtime/ManagedFrps）设计与实现边界            |
| [`RelaxKonOS.CertificateManager.md`](../applications/RelaxKonOS.CertificateManager.md)                                                                                                                                                             | 证书 HostGlobal 管理、ACME 签发、续期、Kestrel 部署契约             |
| [`RelaxKonOS.WebServerManager.Design.md`](../applications/RelaxKonOS.WebServerManager.Design.md)                                                                                                                                                   | Nginx 发现、重载、集成、站点管理契约                                |
| [`RelaxKonOS.ProcessGuardian.md`](../applications/RelaxKonOS.ProcessGuardian.md)                                                                                                                                                                   | 工作负载声明、健康检查、原生服务管理、GuardianLogs Hub                  |
| [`RelaxKonOS.Registry.md`](../architecture/RelaxKonOS.Registry.md) / [`RelaxKonOS.RegistryApp.md`](../applications/RelaxKonOS.RegistryApp.md)                                                                                                          | 注册表 Schema、浏览、读写端点契约                                 |
| [`RelaxKonOS.AppSettings.md`](../development/RelaxKonOS.AppSettings.md)                                                                                                                                                                            | 应用私有配置存储（revision 乐观并发）                              |
| [`RelaxKonOS.Desktop.md`](../desktop/RelaxKonOS.Desktop.md)                                                                                                                                                                                        | 桌面外壳、模态对话框、窗口管理协作                                    |
| [`RelaxKonOS.md`](../README.md)                                                                                                                                                                                                                  | 项目结构、当前进度、代码地图                                       |


### SettingsSystem 升级（2026-09-07，G1 实施中）

Workspace preferences GET 返回 `revision`，PUT 必须携带读取时的 `revision`；缺失为 428、冲突为 409，不接受无版本覆盖。服务端 `Settings/WorkspaceSettingsService` 使用注册表 CompareExchange，客户端统一使用 `Services/WorkspaceSettings/IWorkspaceSettingsService`。偏好仍存 `Workspace\Desktop`，缓存接收不等同 SQLite 落盘。AppSettings 只负责应用私有数据；宿主真实配置与其操作恢复材料不放入 AppSettings 或 Workspace 偏好。完整执行与待验证项见 [SettingsSystem.Goal](../desktop/RelaxKonOS.SettingsSystem.Goal.md)。

注册表 `PutRegistryEntryRequest.expectedRevision` 必传；创建使用 0，更新使用已读 `RegistryEntryDto.revision`。缺失 428、冲突 409。`Workspace\Desktop` 默认值仍可经注册表编辑，但必须通过偏好校验；不能删除受管偏好或其祖先键来重置版本。需恢复默认值时通过携带当前 revision 的偏好更新实现。
