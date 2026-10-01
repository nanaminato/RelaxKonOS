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
| **SignalR Hub**（`/hubs/performance`）   | 实时单向：至少一个客户端显式订阅期间，服务端统一采样器每秒广播 `PerformanceRealtimeSnapshotDto`（CPU/内存/文件系统/磁盘/网络/GPU/网络地址）；客户端以 REST history 回补重连空洞                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| **SignalR Hub**（`/hubs/guardian-logs`） | 实时单向：Process Guardian 守护日志广播；客户端 `Subscribe/Unsubscribe` 按工作负载订阅，服务端推送结构化日志事件（包含 workload id、级别、消息、时间戳）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |

SignalR 内部走 WebSocket（不可用降级 SSE/长轮询），**不裸用 WebSocket**。Workspace 多设备通过 SignalR Group（一个 Workspace 一个 Group）广播。Terminal Hub 不启用 `WithAutomaticReconnect`（自动重连后服务端不会自动重新附加会话），恢复路径是"再次登录打开终端 → 重新 `Start(Attach)` → 回放 1MB 缓冲快照"。所有 Hub 路径常量集中在 `RelaxKonOSEndpoints`（`WorkspaceHubPath` / `PerformanceHubPath` / `GuardianLogsHubPath`）。

***

## 3. 模块结构

```text
Shared/RelaxKonOS.Protocol/
├── Common/              # HostPlatformKind、ClientPlatformKind、RelaxKonOSEndpoints（含 Hub 路径）、ProblemDetails、RelaxKonOSJsonOptions、ServerDescriptorDto
├── Identity/            # UserDto、AuthTokens、LoginRequest/Response、RefreshToken、Logout、AuthApiRoutes
├── Workspace/           # WorkspaceDto、SessionDto、DeviceDto、ControllerLeaseInfo、3 enum
│                        # WorkspacePreferencesDto（含 desktopExperience + desktopDisplay + 文本编码）、DefaultAppMappingDto
│                        # DesktopExperiencePreferencesDto（appearance + systemStyleId + shell）
│                        # AppearancePreferencesDto / ThemePaletteContract / ThemePaletteDefaults / ThemePaletteImport
│                        # SystemStyles/（SystemStyleRecipes / SystemStyleTokenContract / SystemStyleManifestDto
│                        #               / SystemStyleManifestValidator / BuiltInSystemStyles）
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
├── AppSettings/         # AppSettingsContracts（应用私有配置 DTO、乐观并发 revision）、注入 WorkspaceApiRoutes/RelaxKonOSEndpoints
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

`RelaxKonOSJsonOptions.Default` 统一序列化：

* `JsonSerializerDefaults.Web`：camelCase + 大小写不敏感

* `JsonStringEnumConverter`：枚举序列化为 camelCase 字符串（如 `"linux"`、`"running"`、`"controller"`）

* 时间：`DateTimeOffset` → ISO 8601

Server MVC（`AddControllers().AddJsonOptions`）与 SignalR（`AddSignalR().AddJsonProtocol`）共用此配置。Client Http 也用同一份 options 反序列化。

所有 DTO 公开成员显式标注 `[property: JsonPropertyName("camelCaseName")]`，钉死线协议，避免 C# 重命名导致线协议破坏。

***

## 5. REST 端点

路径前缀 `/api/v1.0`，错误统一返回 `ProblemDetails`（RFC 7807 子集）。路由常量集中在 `AuthApiRoutes` / `WorkspaceApiRoutes`。

**路由常量分两类，混用会静默改变实际路径**：

* **绝对常量**（含 `/api/v1.0` 前缀，如 `ApplicationDeploymentApiRoutes.Applications`、`AuthApiRoutes.Login`）：供客户端拼接 URL。
* **相对常量**（`*Pattern`，如 `ApplicationsPattern = "/applications"`）：供服务端在 `MapGroup` 内注册。

`MapGroup` 会把组前缀与传入的模式**直接拼接**（`RoutePatternFactory.Combine`），不会识别"绝对路径"。因此在非空前缀的组内传入绝对常量，实际路径会变成前缀重复的 `/api/v1.0/x/api/v1.0/x/...`：服务端照常启动、编译无警告，客户端只会收到 `404`——表现像"服务器未提供该接口"（`application-deployment.http_404`），实为路径写错。绝对常量只允许用于空前缀的组（如 `MapGroup("")`），或直接 `app.Map*`。

> 2026-09-20 实例：`ApplicationDeploymentEndpoints` 的 `GET/POST /applications` 曾误用绝对常量 `Applications`，实际注册为 `/api/v1.0/application-deployments/api/v1.0/application-deployments/applications`，导致客户端所有"应用部署"读取 404；改用 `ApplicationsPattern` 后恢复。

### 认证

| 方法   | 路径                     | 请求                    | 响应                     | 认证  |
| ---- | ---------------------- | --------------------- | ---------------------- | --- |
| POST | `/api/v1.0/auth/login`   | `LoginRequest`        | `LoginResponse`        | 无   |
| POST | `/api/v1.0/auth/refresh` | `RefreshTokenRequest` | `RefreshTokenResponse` | 无   |
| POST | `/api/v1.0/auth/logout`  | `LogoutRequest`       | 204                    | JWT |
| GET  | `/api/v1.0/auth/me`      | —                     | `UserDto`              | JWT |

### Server

| 方法  | 路径                                     | 请求 | 响应                       | 认证  |
| --- | -------------------------------------- | -- | ------------------------ | --- |
| GET | `/api/v1.0/server/capabilities`          | —  | `ServerCapabilitiesDto`  | JWT |
| GET | `/api/v1.0/server/host-operating-system` | —  | `HostOperatingSystemDto` | 无   |

`/server/host-operating-system` 是**唯一一个不要求凭据的 Server 信息面**，存在理由只有一条：客户端要在**尚未登录、手上也没有可用密码**时，为一条保存的连接选出正确的平台标记。因此它只回答 `HostOperatingSystemKind`（`unknown` / `ubuntu` / `windows10` / `windows11` / `windowsServer`），不含账号、版本、配置、路径或主机身份；任何想往响应里增加的东西都要先过这条标准。

`unknown` 同时表示「宿主不是这几类」与「问不出来」（`RtlGetVersion` 失败、`/etc/os-release` 读不到、平台既非 Windows 也非 Linux）；客户端对两种情况都必须回落通用标记，不得猜成其中任何一个。类别只描述产品标记，能力仍由 `ServerCapabilitiesDto` 回答，两者不可互相替代。路由常量见 `ServerApiRoutes`。

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
| GET    | `/api/v1.0/files/thumbnail`   | query: `path`、`maxEdge`（16–1024，默认 256） | `image/jpeg` 或 `image/png` 字节流       | JWT |
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
| POST   | `/api/v1.0/files/uploads`     | `CreateUploadRequest` + 头 `Idempotency-Key` | `UploadSessionDto`（201）            | JWT |
| GET    | `/api/v1.0/files/uploads/{uploadId}` | —                             | `UploadSessionDto`                 | JWT |
| PATCH  | `/api/v1.0/files/uploads/{uploadId}` | 头 `Upload-Offset`、`Content-Length`；体为原始字节 | 204 + 头 `Upload-Offset`            | JWT |
| DELETE | `/api/v1.0/files/uploads/{uploadId}` | —                             | 204                                | JWT |
| POST   | `/api/v1.0/files/uploads/{uploadId}/commit` | `CommitUploadRequest`      | `FileEntryDto`（201）                | JWT |

`files/upload` 是**小文件快路径**（服务端显式声明请求体上限 16 MiB，超限 `413` + `upload-too-large-for-single-shot`，
客户端只在声明长度 ≤ 4 MiB 时使用）；大文件走可续传的分块会话 `files/uploads*`，`Upload-Offset` 是"服务端到底收到多少"
的唯一权威（任何疑问都用 `GET` 询问，绝不用本地估计代替）。完整的偏移规则、问题码、幂等语义、暂存与提交方式、
提权如何固定在会话级，以及反向代理必须放宽的项，见
[`RelaxKonOS.FileUpload.Design.md`](./RelaxKonOS.FileUpload.Design.md)。

缩略图（`files/thumbnail`）不是原图的替代品，而是「原图还在路上时先给一张认得出的图」：渲染在
`ImageThumbnailRenderer`，响应体为 `image/jpeg`（含 alpha 通道时改用 `image/png`），`maxEdge` 越界返回
400 `invalid-size`。内容不是本服务能解码的图像时返回 415 `thumbnail-unsupported` —— 这是**正常答复而非错误**，
客户端应读作「没有缩略图」并照旧拉取原图。渲染顺序是先 `Identify` 读头（声明像素数超过 64M 直接拒绝，防解压炸弹），
再单帧解码（`DecoderOptions.MaxFrames = 1`，防动画帧内存放大），并在缩放前应用 EXIF 方向。

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
| GET    | `/api/v1.0/system/performance/snapshot`           | —                                   | `PerformanceRealtimeSnapshotDto` / 503（等待窗口内仍无有效样本） | JWT |
| GET    | `/api/v1.0/system/performance/history?seconds=60` | query: `seconds`（1–60）              | `PerformanceRealtimeSnapshotDto[]`           | JWT |
| GET    | `/api/v1.0/system/processes/query`                | page/pageSize/filter/sort/direction | `ProcessPageDto`                             | JWT |
| DELETE | `/api/v1.0/system/processes/{id}`          | JSON: `TerminateProcessRequest`（必需 expectedStartTime）                  | `KillProcessResultDto`                       | JWT |

`GET /system/performance/snapshot` 是首进、重连与没有实时订阅的客户端的降级路径，因此**读取本身构成 demand**：当前无人需要采集时，服务端为该请求申请一段有界采样窗口（窗口需覆盖建立差分基线所需的两个采样周期），窗口内取到有效样本即返回，仍取不到才返回 `503` + `type: .../performance-not-ready`。客户端必须按问题码渲染该状态，不得当作连接失败。demand 一释放采样立即回到空闲，`订阅期间才采集`的成本规则不变。

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
| GET/DELETE | `/api/v1.0/docker/volumes/{name}`              | DELETE query: `confirmed`                       | 卷详情（含 `usedBy`）/ 操作结果              | JWT |
| POST       | `/api/v1.0/docker/stacks/preview`              | body: `DockerStackDefinitionDto`（名称 + Compose YAML） | `DockerStackPreviewDto`（服务/命名卷/网络 + `definitionVersion`）。只解析，不落地任何改动 | JWT |
| GET        | `/api/v1.0/docker/stacks`                      | —                                              | Stack DTO\[]                        | JWT |
| POST       | `/api/v1.0/docker/stacks/deploy`               | body: `DockerStackDeployRequest`（定义 + 预览回报的 `definitionVersion`）+ `Idempotency-Key` | `202` + `DockerStackOperationDto`    | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/operations`    | query: limit（1–100，默认 20）                     | `DockerStackOperationDto[]`（最新在前）  | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/operations/active` | —                                           | 活动操作 DTO / `404`                   | JWT |
| GET        | `/api/v1.0/docker/stack-operations/{operationId}` | —                                            | `DockerStackOperationDto` / `404`     | JWT |
| GET        | `/api/v1.0/docker/stack-operations/{operationId}/diagnostics` | —                                 | `DockerStackOperationDiagnosticsDto`（已脱敏、限长、可标记截断） | JWT |
| POST       | `/api/v1.0/docker/stack-operations/{operationId}/cancel` | `Idempotency-Key`                    | `DockerStackOperationDto`             | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/services`      | —                                              | 服务 DTO\[]                           | JWT |
| GET        | `/api/v1.0/docker/stacks/{name}/definition`    | —                                              | Compose 原文                          | JWT |
| POST       | `/api/v1.0/docker/stacks/{name}/{action}`      | action ∈ start/stop/restart/delete + `Idempotency-Key` | `202` + `DockerStackOperationDto` | JWT |

Compose 编排的**每个变更都是持久操作，不是同步结果**（`stacks/deploy`、`stacks/{name}/{action}`、`stack-operations/{id}/cancel` 均返回 `202` 与操作记录）：

- 操作由 `DockerStackOperationCoordinator` 在 HTTP 请求之外执行，记录落在宿主 `stack-operations.json`（`DockerCompose:DataDirectory`，与 Compose 源同目录，原子写）。手机断开、App 被回收或服务端重启后，结果仍可从 `stack-operations/{id}` 或项目历史读到。
- 变更请求必须携带 `Idempotency-Key`（可见 ASCII、≤128 字符）；缺失或畸形返回 `docker.stack_idempotency_required`。同一键配同一请求指纹回放原操作；同一键配**不同**请求返回 `docker.stack_idempotency_conflict`，不会静默合并。
- 同一项目同时只允许一个活动操作（`docker.stack_operation_conflict`）；不同项目互相独立，另有 `DockerCompose:MaximumConcurrentOperations`（默认 2）的全局上限。
- 部署必须回报 `preview` 给出的 `definitionVersion`（名称 + YAML 的内容标识）；不一致返回 `docker.stack_definition_changed`，即“对另一份文档的批准不能用于这一份”。
- `preview` 与 `deploy` 共用同一份准入检查，都在调用引擎之前以 `400` 拒绝：不接受的 Compose 条目（`build`、特权与设备权限、外部资源、bind mount、Docker socket）返回 `docker.compose_feature_unsupported`，**变量引用**返回 `docker.compose_variable_unresolved`。后者是必须的，因为 `docker compose config` 会把未设置的变量静默替换为空串并 exit 0，而本服务不提供变量输入面——放行等于让操作者批准一份、执行另一份。`$$` 是 Compose 的字面美元转义、注释不参与插值，两者都不算引用。
- 结果是**观察到的服务状态**，不是命令退出码：命令成功但有服务未达目标状态为 `partialFailed`，命令失败但有容器残留同样为 `partialFailed`，二者都不声称原子回滚。`recoveryProblemCode` 只在需要操作者决策**且**能补充 `problemCode` 未表达的信息时才出现（因此不会与 `problemCode` 取同一个值）。
- 服务端重启时不重放活动操作：核对项目实际服务后置为 `interrupted`（`docker.stack_interrupted`），失联不上报为成功。

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

路由和类型以 `Shared/RelaxKonOS.Protocol/Tunnels/TunnelApiRoutes.cs` 与 `TunnelContracts.cs` 为准。所有路由要求登录及宿主 Tunnels feature；`TunnelsRead` 允许 Controller/Observer，`TunnelsManage` 仅允许 Controller，profile/definition 按 JWT 主体隔离。当前执行边界见 [FRP 实现](../applications/RelaxKonOS.FRP_Integration.Implementation.md)。

**Profiles 与隧道定义**

| 方法 | 路径 | 请求 / 响应 | 策略 |
| --- | --- | --- | --- |
| GET | `/api/v1.0/tunnels/profiles` | `TunnelServerProfileDto[]` 安全元数据，只有 `tokenConfigured` | TunnelsRead |
| GET | `/api/v1.0/tunnels/profiles/{profileId}` | `TunnelServerProfileDto`，不返回 Token | TunnelsManage |
| POST / PUT | `/api/v1.0/tunnels/profiles` / `/api/v1.0/tunnels/profiles/{profileId}` | `UpsertTunnelServerProfileRequest` → profile；创建 201，更新 200，`expectedRevision` 冲突 409 | TunnelsManage |
| DELETE | `/api/v1.0/tunnels/profiles/{profileId}` | 204；存在关联隧道时 409，不提供 revision CAS 或隐式停止 | TunnelsManage |
| PUT | `/api/v1.0/tunnels/profiles/{profileId}/secret` | JSON `SetTunnelProfileTokenRequest` → 204；独立写入式入口，不反射正文 | TunnelsManage |
| POST | `/api/v1.0/tunnels/profiles/{profileId}/apply`、`/stop` | 无正文 → `TunnelOperationResultDto { succeeded, state, problemCode }` | TunnelsManage |
| GET | `/api/v1.0/tunnels/profiles/{profileId}/logs` | `TunnelLogEntryDto[]`，最多 200 行脱敏日志，无 tail 参数 | TunnelsRead |
| GET | `/api/v1.0/tunnels` | `TunnelDefinitionDto[]`，provider 增补当前运行/应用状态 | TunnelsRead |
| GET | `/api/v1.0/tunnels/{tunnelId}` | 单项期望状态 `TunnelDefinitionDto` | TunnelsRead |
| POST / PUT | `/api/v1.0/tunnels` / `/api/v1.0/tunnels/{tunnelId}` | `UpsertTunnelDefinitionRequest` → definition；创建 201，更新 200，revision 冲突 409 | TunnelsManage |
| DELETE | `/api/v1.0/tunnels/{tunnelId}` | 204，不隐式应用运行配置，不提供 revision CAS | TunnelsManage |

Profile Token 不通过任何 GET 返回，没有 Token DELETE、multipart 秘密或配置下载接口。应用、停止与配置变更是同步 API，没有领域 operation ID、PID 响应、`Idempotency-Key` 或取消接口。Windows 受管 profile 生命周期另需 `FrpLifecycle + profileId` 授权。客户端未知结果需读取当前事实，不能把请求重发或当前资源存在当作原动作已成功。

Provider 以应用时 profile revision、隧道 ID/revision 集合和受保护 Token 的内存指纹区分运行配置与当前期望状态：运行中发生修改投影 `savedNotApplied`；缺少应用身份为 `unknown`；禁用项不显示连接。指纹不进入 DTO，重启后没有凭据证明便不推断当前配置已应用。`connected` 只证明 frpc 的服务器登录，不证明每个 proxy 注册或公网访问。

**Runtime**

| 方法 | 路径 | 请求 / 响应 | 策略 |
| --- | --- | --- | --- |
| GET | `/api/v1.0/tunnels/runtime` | `TunnelRuntimeDto`（当前/上一版本、路径、完整性与状态） | TunnelsRead |
| GET | `/api/v1.0/tunnels/runtime/download?version={version}` | 固定版本 → `TunnelRuntimeDownloadDto`；未受信版本 404 | TunnelsRead |
| POST | `/api/v1.0/tunnels/runtime/external/detect` | `DetectExternalTunnelRuntimeRequest { executablePath }` → `TunnelRuntimeDto`；只探测指定绝对文件 | TunnelsManage |

运行时安装统一走 `POST /api/v1.0/installations/Frp/{Install|Upgrade|Repair|Uninstall}`，请求 `FrpInstallationRequest`，沿用公共安装的确认、稳定键、提权、操作 ID、活动查询与取消语义；回滚为 Repair + rollback。Install 的包来源通过 `/installations/Frp/file-reference` 或 `/package` 产生限时 FileReferenceId，其余动作不借用包引用。安装/升级/非回滚修复要求受信固定版本；没有 latest、旧 runtime/managed 路由或客户端任意 URL 安装。

**Managed Frps（宿主级）**

| 方法 | 路径 | 请求 / 响应 | 策略 |
| --- | --- | --- | --- |
| GET | `/api/v1.0/tunnels/frps` | 安全 `ManagedFrpsConfigurationDto`，不返回 Token | TunnelsRead |
| GET | `/api/v1.0/tunnels/frps/editor` | 编辑 DTO，含当前 Token；成功读取秘密有审计，不返回 dashboard 密码 | TunnelsManage |
| PUT | `/api/v1.0/tunnels/frps` | `UpdateManagedFrpsConfigurationRequest` → 安全 DTO；要求 confirmed 与必需 expectedRevision（首次 0，冲突 409） | TunnelsManage |
| POST | `/api/v1.0/tunnels/frps/start`、`/stop` | 同步 `TunnelOperationResultDto`，无 PID/operation ID | TunnelsManage |
| GET | `/api/v1.0/tunnels/frps/logs` | 有界脱敏 `TunnelLogEntryDto[]` | TunnelsRead |
| GET | `/api/v1.0/tunnels/frps/audit` | 有界配置/生命周期/秘密读取 `TunnelAuditEntryDto[]`，不接收 limit/skip | TunnelsRead |

frps DTO 必需 `revision/appliedRevision`：首次未配置 revision=0；保存版本为正数，应用版本仅在活跃进程已核实时返回。保存推进 revision，不重启或重新应用运行配置；活跃进程应用版本不同的 Start 返回 `tunnel.frps_restart_required`。成功 Stop 返回 disconnected，不能返回 connected。Linux 重新打开配置但缺少原进程归属时返回 Unknown，并拒绝宣称停止未知进程；不按名称杀进程。配置记录直接采用必需 revision 的当前格式，不解析旧格式。PUT 不回传 Token，空白替换保留已存 Token/dashboard 密码。

Windows frps 生命周期另需 `FrpLifecycle + frps` 授权。frps Token 编辑读取与 profile Token 写入式接口具有不同边界；不得将两者写成共享秘密读取 API。日志与审计不返回 TOML、受保护密钥载荷或 dashboard 密码。

### Certificates / WebServers（V1 后端）

证书与 Web Server 的 HostGlobal 后端已实现；具体资源模型见 [`RelaxKonOS.CertificateManager.md`](../applications/RelaxKonOS.CertificateManager.md) 与 [`RelaxKonOS.WebServerManager.Design.md`](../applications/RelaxKonOS.WebServerManager.Design.md)。证书 API 提供元数据读取、预检、签发、续期、Kestrel 部署、删除、撤销和 operation 查询/取消；Web Server API 提供 Nginx 发现、状态、配置测试、最小集成、重载和 operation 查询/取消。所有变更请求：

* 所有变更请求携带 `Idempotency-Key`，返回 `OperationDto`（操作 ID、状态、阶段、稳定问题码、时间、可选快照 ID）。

* `CertificateApiRoutes` 与 `WebServerApiRoutes` 只定义 `/api/v1.0` 路径常量；Endpoint、Client 和 UI 不重复字面量。

* 当前单机管理员模式下，资源为 HostGlobal，不引入 User/Workspace 路径参数；需要管理员运行状态才能执行变更。

* Operation 查询、取消和后续进度事件使用 Protocol 契约，不能让 UI 通过日志文本推断状态。

**Certificates（当前路由见 CertificateApiRoutes）**

| 方法 | 路径 | 请求 | 响应 | 认证 |
| --- | --- | --- | --- | --- |
| GET | `/api/v1.0/certificates` | — | CertificateDto[]，只含元数据 | JWT + Certificates 宿主能力 |
| GET | `/api/v1.0/certificates/{id}` | — | CertificateDto | JWT + Certificates 宿主能力 |
| POST | `/api/v1.0/certificates/preflight` | CertificatePreflightRequest：domains、challengeType | CertificatePreflightResultDto | JWT + Certificates 宿主能力；无幂等键 |
| POST | `/api/v1.0/certificates` | RequestCertificateRequest：域名、挑战、邮箱、条款、密钥与公网确认 | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| POST | `/api/v1.0/certificates/self-signed` | CreateSelfSignedCertificateRequest：SAN、密钥、有效天数 | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| POST | `/api/v1.0/certificates/{id}/renew` | — | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| GET | `/api/v1.0/certificates/{id}/deployments/kestrel` | — | KestrelCertificateDeploymentDto | JWT；实际选择器/监听/指纹与观察时间，无私钥 |
| POST | `/api/v1.0/certificates/{id}/deployments/kestrel` | — | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| POST | `/api/v1.0/certificates/{id}/revoke` | RevokeCertificateRequest：confirmed | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| DELETE | `/api/v1.0/certificates/{id}` | DeleteCertificateRequest：confirmed | CertificateOperationDto | JWT + Idempotency-Key；宿主管理员 |
| GET | `/api/v1.0/certificates/operations/{operationId}` | — | CertificateOperationDto | JWT + Certificates 宿主能力 |
| POST | `/api/v1.0/certificates/operations/{operationId}/cancel` | — | CertificateOperationDto | JWT + Certificates 宿主能力；无幂等键 |

当前没有操作集合或按请求键查询接口。DNS-01 返回不可用；Direct/Webroot HTTP-01 预检不证明公网可达。私钥/PEM 不进入 DTO；自签名支持私有 IP/DNS SAN，但不提供 ACME 续期或撤销。幂等身份、恢复限制和宿主权限见 [证书管理](../applications/RelaxKonOS.CertificateManager.md#354-protocol-与操作模型)。

**WebServers（当前 Nginx 端点，路由见 WebServerApiRoutes）**

| 方法 | 路径 | 请求 | 响应 | 认证 |
| --- | --- | --- | --- | --- |
| GET | `/api/v1.0/webservers`、`/api/v1.0/webservers/{id}` | — | WebServerDto 列表 / 详情，包含真实能力 | JWT + WebServer 宿主能力 |
| POST | `/api/v1.0/webservers/discover` | — | WebServerDto[] | JWT + WebServer 宿主能力 |
| GET | `/api/v1.0/webservers/{id}/status` | — | WebServerStatusDto | JWT + WebServer 宿主能力 |
| POST | `/api/v1.0/webservers/{id}/config/test` | — | WebServerConfigTestDto | JWT + WebServer 宿主能力 |
| GET | `/api/v1.0/webservers/managed/catalog`、`/api/v1.0/webservers/managed/download` | download query: version | Nginx 安装目录 / 下载引用 | JWT + WebServer 宿主能力 |
| GET | `/api/v1.0/webservers/integration-candidates` | — | WebServerIntegrationCandidateDto[] | JWT + WebServer 宿主能力 |
| POST | `/api/v1.0/webservers/integration-candidates/{candidateId}/integrate` | IntegrateWebServerRequest | WebServerOperationDto | JWT + Idempotency-Key + NginxConfigurationWrite |
| POST | `/api/v1.0/webservers/{id}/lifecycle/{action}`、`/api/v1.0/webservers/{id}/reload` | — | WebServerOperationDto | JWT + Idempotency-Key + NginxLifecycle；ACME include 使用 NginxConfigurationWrite |
| GET | `/api/v1.0/webservers/{id}/sites` | — | WebServerSiteDto[] | JWT + WebServer 宿主能力 |
| POST | `/api/v1.0/webservers/{id}/sites` | UpsertWebServerSiteRequest：创建的 expectedUpdatedAt 为 null；更新为已读完整 updatedAt | WebServerSiteDto（同步） | JWT + NginxConfigurationWrite；无 Idempotency-Key |
| DELETE | `/api/v1.0/webservers/{id}/sites/{siteId}` | DeleteWebServerSiteRequest：必需 expectedUpdatedAt | 204（同步） | JWT + NginxConfigurationWrite；无 Idempotency-Key |
| GET | `/api/v1.0/webservers/operations/{operationId}` | — | WebServerOperationDto | JWT + WebServer 宿主能力 |
| POST | `/api/v1.0/webservers/operations/{operationId}/cancel` | — | WebServerOperationDto | JWT + WebServer 宿主能力 |

站点变更在写入锁内比较原版本，冲突返回 409，不能静默覆盖；完整时间精度必须保留。站点提交没有操作 ID，失联后读取事实并显式处理未知结果。Web 长任务目前只有按 ID 查询接口，没有集合或按请求键查找接口。共享执行与补偿细节见 [Web Server 设计](../applications/RelaxKonOS.WebServerManager.Design.md#23-api-建议)。

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
| GET            | `/api/v1.0/guardian/status`                 | —                                               | `GuardianStatusDto` | JWT |
| GET            | `/api/v1.0/guardian/workloads`              | —                                               | `GuardianWorkloadDto[]`，由 Agent 管理声明 | JWT |
| POST           | `/api/v1.0/guardian/workloads`              | `UpsertGuardianWorkloadRequest`；跨账户 `RunAs` 需逐次管理员证明 | `GuardianAgentResponse` | JWT |
| GET/DELETE     | `/api/v1.0/guardian/workloads/{id}`         | —                                               | 定义 / 删除结果 | JWT |
| POST           | `/api/v1.0/guardian/workloads/{id}/start`   | —                                               | 启动操作结果                                       | JWT（按归属）                              |
| POST           | `/api/v1.0/guardian/workloads/{id}/stop`    | — | 停止操作结果 | JWT |
| POST           | `/api/v1.0/guardian/workloads/{id}/restart` | —                                               | 重启操作结果                                       | JWT（按归属）                              |
| GET/POST       | `/api/v1.0/guardian/scripts`               | POST: `SubmitScriptTaskRequest` + UUID `Idempotency-Key` | 当前账号任务列表 / 提交结果，均由 Agent 持久保存 | JWT |
| GET            | `/api/v1.0/guardian/scripts/{id}`          | —                                               | 当前账号的任务结果与有界输出 | JWT |
| POST           | `/api/v1.0/guardian/scripts/{id}/cancel`   | —                                               | 取消请求结果 | JWT |
| GET            | `/api/v1.0/guardian/workloads/{id}/logs` | — | 有界日志数组 | JWT |
| GET            | `/api/v1.0/guardian/audit` | — | 守护审计数组 | JWT |
| GET            | `/api/v1.0/guardian/services` | — | 原生服务数组 | JWT，System Mode |
| POST           | `/api/v1.0/guardian/services/{id}/{action}` | `NativeServiceActionRequest` | 原生服务操作结果 | JWT，System Mode + 宿主授权 |
| POST           | `/api/v1.0/guardian/agent/installation/plan` | — | 安装计划 | JWT，System Mode |

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
| `AttachExisting` | `string sessionId` | `AttachTerminalResponse {SessionId, Created=false}` | 只附加当前用户仍存活的会话；找不到则失败，绝不新建 PTY |
| `Input`        | `byte[]`                                                | void                                          | 转发到 `session.Pty.Write(data)`                                |
| `Resize`       | `int cols, int rows, int widthPixels, int heightPixels` | void                                          | 转发到 `session.Pty.Resize(...)`                                |
| `CloseSession` | `string sessionId`                                      | void                                          | `manager.Remove` —— **手动终止**（杀 PTY），对应关闭终端窗口 / "断开"按钮；先校验会话归属当前用户 |
| `ListSessions` | —                                                       | `TerminalSessionInfo[]`                       | 返回当前用户全部终端会话摘要（多实例）                                          |

#### Server → Client（on，事件名见 `TerminalHubEvents`，接口 `ITerminalHubClient`）

* `OnOutput(byte[] data)`：PTY 输出字节（始终追加进 1MB 环形缓冲；有附加连接时经 `IHubContext` 转发）

* `OnProcessExited(int exitCode)`：子进程退出

> **方法名对齐**：Server Hub 方法名必须与 `TerminalHubMethods` 常量完全一致（`Start` 非 `StartTerminal`），否则 SignalR 运行时找不到方法。`OnDisconnectedAsync` 调 `session.Detach(Context.ConnectionId)` 保留 PTY；仅显式 `CloseSession` 才杀。`TerminalUserIdProvider`（`IUserIdProvider`）以 JWT `sub` claim 作 `Context.UserIdentifier`，按用户过滤会话。

### Performance Hub（`/hubs/performance`）

服务端统一采样器（`PerformanceSampler`，Singleton，`ISystemPerformanceSource` 跨平台采样：Windows/Linux）在**至少一个客户端订阅期间每秒**广播 `PerformanceRealtimeSnapshotDto`，并保留最近 60 秒内存历史，供客户端以 REST `performance/history` 回补重连空洞。无人订阅时不读取性能计数器，并清空内存历史。详见 [`RelaxKonOS.TaskManager.Rewrite.md`](../applications/RelaxKonOS.TaskManager.Rewrite.md)。

客户端通过 `Subscribe` / `Unsubscribe` 显式控制订阅；连接断开也会自动移除订阅。方法名集中在 `PerformanceHubMethods`。

#### Server → Client（on，事件名见 `PerformanceHubEvents`，接口 `IPerformanceHubClient`）

* `OnSnapshot(PerformanceRealtimeSnapshotDto dto)`：每秒广播一次

  * 包含：`Timestamp`、采样间隔、`CpuUsageDto`（总 CPU + 核/频率）、`MemoryUsageDto`（总/可用/已用/缓存/Swap）、`FilesystemUsageDto[]`（每个挂载点容量、inode、使用率）、`DiskUsageDto[]`（磁盘读写速率、队列长度、IOPS）、`NetworkUsageDto[]`（网卡收发包/字节/速率/丢包/错包）、`GpuUsageDto[]`（GPU 利用率、显存、温度、功率）、`NetworkAddressDto[]`（每个网卡 IP 地址）

  * 平台不支持的指标子项以 `null` 或空数组返回（绝不伪造 0 值）

  * `MaximumReceiveMessageSize = null` 以容纳包含多网卡多磁盘的大块快照

> **连接语义**：客户端首次建立 Performance Hub 连接后立即订阅广播；订阅会启动采样，首个有效样本约在一个采样间隔后可用。重连时先调 REST `performance/history?seconds=60` 拉取仍在同一订阅期内的历史，再从 Hub 实时衔接；无人订阅期间 snapshot 返回 503。

### ApplicationDeploymentLogs Hub（`/hubs/application-deployment-logs`）

应用部署实时日志，路径常量 `RelaxKonOSEndpoints.ApplicationDeploymentLogsHubPath`。要求 JWT、`ApplicationDeploymentsRead` 策略及应用部署宿主能力，与 REST 操作诊断的读取范围一致。

- Client → Server：`ApplicationDeploymentLogsHubMethods.Subscribe(Guid operationId)`，每连接观察一个操作，返回 `DeploymentLiveLogSnapshot`；不存在的操作拒绝订阅。
- Server → Client：`IApplicationDeploymentLogsClient.OnDeploymentLogs(DeploymentLiveLogSnapshot)`，快照包含 `OperationId`、递增 `Version`、`Lines` 和 `Truncated`。每行含 UTC 时间、脱敏消息、可选 `DeploymentStage`。
- 最近 300 行有界快照，最多每秒推送两次有变化的尾部；客户端以版本去重，重连时重新 Subscribe 补回内存尾部。尾部不跨服务端重启保存；REST 持久操作记录仍是状态真相源，终态诊断通过原有操作日志端点读取。
- 断开连接移除订阅，不取消部署；token 到期关闭连接，客户端刷新 token 后重连。此 Hub 不承载归档数据，归档仍走 HTTP 流式 multipart 上传。

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
| `TerminalHubMethods.cs`     | client→server 方法名常量（`Start`/`AttachExisting`/`Input`/`Resize`/`CloseSession`/`ListSessions`）   |
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

* 所有 Hub 路径常量集中在 `RelaxKonOSEndpoints`（`WorkspaceHubPath` / `PerformanceHubPath` / `GuardianLogsHubPath`），Endpoint、Client、UI 三方共享

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

## 设置平台契约（实施中，2026-09-08）

`Protocol/Settings/SettingsContracts.cs` 定义 ClientDevice/Workspace/AppPrivate/HostUser/HostMachine 范围、能力原因、生效时间、目录与时区预览/应用/操作查询契约。路由集中在 `SettingsApiRoutes`：`/settings/catalog`、`/host-settings/time` 与 `/host-settings/identity` 的 GET/preview/apply，以及 `/settings/operations/{id}` 与 rollback，均位于 `/api/v1.0` 下。

预览接收 expectedRevision、idempotencyKey、强类型 TimeZoneChange；应用仅接收 planId，不能更换已预览载荷。需要 `HostTimeChange` 的 `host/time` 授权。428 表示 revision/授权/计划期限前置条件不满足；409 表示外部修改或幂等冲突。操作状态未知不代表失败可重试；可查询持久记录，不能自动重放。

`Protocol/Settings/HostIdentityContracts.cs` 定义 `HostIdentityState`（生效名称、待生效名称、平台上报的最大长度、内容 revision、观测时间、provider）、`HostIdentitySnapshot`、`HostnameChange` 与 `HostnamePreviewRequest`。`HostIdentityValidation` 校验单一 RFC 952/1123 标签，并按调用方给出的最大长度判定，客户端因此使用远程快照上报的上限而不是本机平台的猜测。宿主主机名路由为 `/host-settings/identity` 的 GET/preview/apply，需要 `HostIdentityChange` 对 `host/identity` 的授权；目标固定为 `hostMachine`，不接受调用方指定目标。操作状态与恢复记录写入 Server 独立加密日志的 `identity_operations` 表。

设置通知只包含 settingId、scope、Workspace 资源标识和版本，授权订阅后通过 GET 重读；不广播偏好/环境值。当前仅 Workspace 通知已接通，宿主设置通知仍在实施。DNS DTO/领域接入尚未完成，不能视为已有可用路由。


### 应用部署定义更新（2026-10-01）

`PUT /api/v1.0/application-deployments/applications/{id}` 的 `UpdateApplicationRequest` 必须携带完整 `expectedUpdatedAt`，来自原 `ApplicationDto.updatedAt`。缺失/默认值 400，原子写入时过期 409（`application-deployment.definition_conflict`），活动操作阻断。原幂等键/载荷返回原回执，不重复轮换秘密；更新定义不创建修订或替换容器。Shared、Server、桌面和 Android 同步使用当前必需字段，不保留无版本覆盖。领域边界见 [应用部署设计](../applications/RelaxKonOS.ApplicationDeployment.Design.md)。
