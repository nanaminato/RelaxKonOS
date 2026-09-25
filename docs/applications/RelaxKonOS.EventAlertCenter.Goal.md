# RelaxKonOS 事件与告警中心 Goal

> 建立日期：2026-09-21  
> 状态：设计完成，待实施；本文不是已交付功能的声明。  
> 适用范围：`RelaxKonOS.Server`、`RelaxKonOS.Guardian.Agent`、`Shared/RelaxKonOS.Protocol` 与 `Client/RelaxKonOS.Client`。

## 1. 背景

部署失败、证书自动续期失败、Guardian 不可用或其工作负载反复崩溃、Docker Engine 不可用、FRP 隧道断开，分别已经有操作状态、日志或领域审计。但管理员必须逐个打开应用、理解不同的状态模型，才能发现和处理同一个宿主的风险。

本 Goal 建立一个内置的“事件与告警中心”（下称“中心”）：以一个可审计、可去重、可恢复的操作视图汇总这些信号，给出安全的证据摘要，并将用户带回负责修复的原应用或受控操作入口。它为之后 Android 运维伴侣的推送读取模型预留稳定服务端契约，但不在本 Goal 中实现移动端或外部推送。

中心是**汇聚层，不是新的运维执行引擎**。Docker、证书、应用部署、Guardian、隧道仍是各自资源的事实来源和唯一修改者；中心不通过日志文本猜测状态，也不复制它们的状态机。

## 2. 当前基线

### 2.1 已有可复用基础

- `Shared/RelaxKonOS.Protocol/Observability/` 已定义 `ObservabilityEventCatalog`、安全 `CorrelationContext`、`SecurityAuditEvent`、稳定 outcome/severity。
- `RelaxKonOS.Server/Observability/` 已有请求关联、净化器、结构化运行日志与带 HMAC 链的 append-only `SecurityAuditWriter`；其审计库尚没有供产品读取的 API。
- 应用部署的 `ApplicationDeploymentOperationStore` 保存持久操作、阶段、稳定问题码和限长诊断；`ApplicationDeploymentCoordinator` 已承担恢复与执行。
- 证书的 `CertificateOperationStore`、`CertificateRenewalWorker` 与 `CertificateRenewalAttemptRepository` 已记录续期操作、重试和连续失败。
- Guardian 通过 `NamedPipeProcessGuardianService` 访问独立的 `RelaxKonOS.Guardian.Agent`；Agent 现有工作负载状态、日志和 `audit.jsonl`，而 Server 能返回 `guardian.agent_unavailable`、`guardian.agent_timeout` 等稳定问题码。
- Docker API 具有 `GetStatusAsync` 和明确的 Engine 操作结果；FRP 隧道协议已经公开连接状态（包括 `Disconnected`、`RuntimeUnavailable`）和各领域审计。
- 桌面已通过 `BuiltInApplicationRegistry` 注册 Docker、Process Guardian、Certificates、Application Deployments、Tunnel Manager；SDK 提供受 Shell 验证的 `IAppActivationService` / `IAppActivationHandler`。

### 2.2 当前缺口

- 这些记录没有统一的、面向用户的事件/告警数据模型、查询 API、未处理计数或桌面应用。
- `SecurityAuditWriter` 用于安全取证，不能被当成通用告警队列；它也不应直接向无关权限的用户开放。
- `ObservabilityEventCatalog` 的 Guardian、证书、Docker、隧道事件尚不足以表达中心需要的全部状态转换。
- Guardian 的日志是内存窗口，`audit.jsonl` 没有序列化拉取/确认协议；Guardian 自身崩溃不可能自行上报，必须由 Server 侧心跳/可用性检测发现。
- 目前客户端的内置应用深链接仅在少数应用中实现；不能将 Server 提供的任意 URI 当作跳转目标。

## 3. 目标与完成条件

第一阶段完成后，具备 `EventsRead` 权限的用户可以在单独应用中：

- 浏览、筛选、分页查询部署失败、证书续期失败/即将到期、Guardian/工作负载异常、Docker 异常、隧道断开及中心自身采集异常；
- 将重复的同一故障聚合为一个当前告警，了解首次/最后发生时间、次数、严重性、稳定问题码、已净化证据与关联操作；
- 确认（acknowledge）、附加限长处理说明，并在**事实恢复**后看到已解决状态；这些用户动作和任何从中心发起的修复均保留安全审计；
- 点击固定、受验证的跳转目标，进入资源详情、操作记录、日志或原应用的既有受控操作入口；跳转失败时仍显示可复制的安全引用和问题码；
- 在桌面壳中看到当前未确认高严重性告警数及非打扰式本地提示；刷新、断线重连或 Server 重启均不会丢失历史或把旧事件再次通知为新事件；
- 对关闭的领域，清楚呈现“功能不可用/未安装”，而不是把不存在的服务误报为运行故障。

只有当本文件 §17 的验收条件全部满足，才可关闭 Goal。

## 4. 非目标

- 不替代 Security Audit、领域 operation journal、原始运行日志、指标、分布式追踪或各应用历史页面。
- 不在第一阶段引入短信、邮件、Webhook、移动端推送、SIEM 导出、值班排班、任意规则脚本或跨主机集中控制。
- 不自动执行重启、回滚、重签证书、重新部署或修改隧道；“处理”只能跳转到现有授权/确认流程。自动修复需要独立 Goal 与明确的风险策略。
- 不展示原始 Docker/FRP/Guardian 输出、完整路径、域名、账号、IP、token、私钥、请求体或异常堆栈。
- 不将所有 `Warning` 日志都制成告警；高频健康采样、正常重试和预期停机必须去噪。

## 5. 术语与不变量

| 名称 | 含义 |
| --- | --- |
| 信号（signal） | 某领域发出的、已净化且有稳定类型的事实；可表示失败、恢复或风险阈值跨越。 |
| 事件（event） | 中心持久化的一条不可变信号记录。它是时间线和取证索引，不会被编辑或删除。 |
| 告警（alert） | 由一个或多个同类事件投影出的当前问题；有 open / acknowledged / resolved / suppressed 状态。 |
| 事件类型 | 封闭的、版本化字符串，例如 `deployment.operation_failed`；不是自由文本日志分类。 |
| 问题码 | 既有领域或本中心定义的稳定、无秘密的代码；本地化文案不能作为判断条件。 |
| 资源引用 | 中心内部使用的受控 UUID 或经 `IObservabilitySanitizer` 生成的引用，绝非原始资源名。 |
| 跳转目标（remediation target） | 枚举化的应用、页面和资源 ID 组合；不是可由 Server、日志或外部输入提供的 URI。 |

不变量：事件只追加；同一个事件只入库一次；中心不写入任何领域资源；告警的“已解决”必须有恢复信号、已验证的当前状态或明确的人工关闭理由，不能因用户点击确认而自动解决。

## 6. 领域模型与状态机

### 6.1 事件与告警分离

`OperationalEvent` 是内部不可变持久模型；它至少包含：`EventId`、`OccurredAt`、`Type`、`Severity`、`Source`、`Outcome`、`ProblemCode`、`CorrelationId`、可选 `OperationId`、`ResourceType`、`ResourceReference`、`DedupeKey`、受限 `Evidence`、`RemediationTarget` 与 `PayloadVersion`。

`OperationalAlert` 是对事件的投影，至少包含：`AlertId`、`DedupeKey`、`Type`、当前严重性、状态、首次/最后发生时间、发生次数、最后事件 ID、确认者安全引用/确认时间、处理说明引用、解决者/时间与解决原因。相同 `DedupeKey` 的活跃信号更新同一告警的次数和最后发生时间；恢复信号只解决其对应 key 的打开/已确认告警。

`DedupeKey` 由中心按照封闭类型与安全资源引用生成，例如：

```text
deployment.operation_failed : deployment-application:<applicationId>
certificate.renewal_failed  : certificate:<certificateId>
guardian.agent_unavailable  : guardian-agent:<instanceId>
guardian.workload_unhealthy : guardian-workload:<workloadId>
docker.engine_unavailable   : docker-engine:<instanceId>
tunnel.disconnected         : tunnel:<tunnelId>
```

相同资源的不同根因以 `problemCode` 作为事件证据，而非盲目拆成无限告警。若实施盘点证明某类问题必须独立跟踪，变更其 dedupe 规则、测试和文档；不要在调用点私自拼接 key。

### 6.2 告警状态

```text
signal failure/risk ──> Open ── user confirms ──> Acknowledged
       │                    │                         │
       └── recovery ────────┴─────────────────────────┴──> Resolved

Open/Acknowledged ── authorized suppression rule ──> Suppressed
Suppressed ── rule expires or is removed + signal remains ──> Open
```

- `Open`：需要注意，尚无人确认。
- `Acknowledged`：有人正在处理；新同 key 事件仍递增次数，只有严重性升级时才重新产生本地提示。
- `Resolved`：领域报告恢复、中心检查证明恢复，或有权限的用户以结构化原因人工关闭。人工关闭不伪造领域已恢复。
- `Suppressed`：临时静默显示/提示，仍保留事件和安全审计；必须有原因、创建人和到期时间，不能无限期抑制 `Critical`。

第一阶段只支持一次性、单告警的确认和有时限的抑制；批量确认、任意 DSL 和持久化全局静默留给后续设计。

### 6.3 首批事件目录

| 类型 | 来源与触发 | 默认级别 | 恢复或关闭依据 | 跳转 |
| --- | --- | --- | --- | --- |
| `deployment.operation_failed` | `ApplicationDeploymentCoordinator` 写入失败或恢复失败终态 | Error / Critical | 新部署成功、明确人工关闭 | 应用部署的 operation/detail |
| `certificate.renewal_failed` | `CertificateRenewalWorker`/证书操作失败 | Error | 同一证书续期成功 | 证书详情和操作 |
| `certificate.renewal_exhausted` | 连续失败达到现有重试上限 | Critical | 重试计数重置且续期成功 | 证书详情 |
| `certificate.expiring_soon` | 每日证书扫描跨过配置阈值 | Warning / Critical | 新 `NotAfter` 超出阈值、吊销或删除 | 证书详情 |
| `guardian.agent_unavailable` | Server 心跳连续失败、IPC 超时或服务非运行 | Critical | 连续成功心跳 | Guardian 概览/安装指引 |
| `guardian.workload_failed` | Agent 记录启动失败、退出或重启预算耗尽 | Error / Critical | Agent 报告健康运行并达到稳定窗口 | 工作负载与日志 |
| `guardian.server_restart_failed` | Guardian 对受保护 Server 重启未成功 | Critical | Server 重新就绪 | Guardian 与 Server 诊断 |
| `docker.engine_unavailable` | Engine 状态从可用转为不可用，或异常持续阈值 | Error | 连续可用探测 | Docker 概览 |
| `docker.operation_failed` | 用户请求或部署调用的 Docker 操作失败 | Error | 关联操作成功/人工关闭 | Docker 资源或部署操作 |
| `tunnel.disconnected` | 受管隧道 `Connected` 变为 `Disconnected`/`RuntimeUnavailable` 且超过宽限期 | Warning / Error | 同隧道恢复 `Connected` | 隧道定义/运行时 |
| `event-center.source_degraded` | 某采集器、存储或投影循环不能安全工作 | Critical | 自检成功 | 中心诊断页 |

运行时不存在的 Docker、FRP 或 Guardian，由 capability/安装状态决定是否启用相应采集器；未安装不创建 `*_unavailable` 告警。计划维护造成的短暂状态只能在显式维护窗口内被抑制，不能通过检查“当前用户是否发起操作”来猜测。

## 7. 建议架构

```text
领域操作、轮询检查、Guardian 可靠事件流
                 │  （封闭类型 + 安全引用 + 关联字段）
                 ▼
       IOperationalEventPublisher
                 ▼
  EventAlertStore ──> AlertProjector ──> 查询 API / 未确认计数
       │                    │                    │
       │                    └──> 本地 SignalR 提示（提示，不作事实源）
       ▼
 Security Audit（仅敏感用户操作、读审计、抑制与处理动作）
                 ▼
  Event & Alert Center 桌面应用 ──> Shell 验证的固定跳转 ──> 原领域应用
```

建议新增以下目录；准确类名可随实现调整，但职责边界不可混淆：

```text
Shared/RelaxKonOS.Protocol/EventAlerts/
  EventAlertApiRoutes.cs
  EventAlertContracts.cs
  EventAlertEnums.cs
  EventAlertProblemCodes.cs

RelaxKonOS.Server/EventAlerts/
  IOperationalEventPublisher.cs
  OperationalEventPublisher.cs
  EventAlertStore.cs
  AlertProjector.cs
  EventAlertQueryService.cs
  EventAlertNotificationHub.cs
  Sources/{Deployment,Certificate,Guardian,Docker,Tunnel}EventSource.cs

Client/RelaxKonOS.Client/Apps/EventAlerts/
  EventAlertCenterApp.cs
  EventAlertCenterViewModel.cs
  RemoteEventAlertClient.cs
  Views/
```

发布器在入库前执行：验证已注册类型和 payload version、强制资源/操作引用格式、长度和字段上限、通过既有 sanitizer 将外部片段转换为安全摘要，并在一个事务内追加事件及更新告警投影。不得以 `ILogger` 文本、`SecurityAuditEvent`、异常 `Message` 或 `ToString()` 作为输入协议。

对于对外部副作用已经提交的领域，发布失败不能回滚该副作用。此时领域必须继续写自身 journal 和 Security Audit，并把中心失败记录为可检测的 `event-center.source_degraded`；后台重放器从领域的持久 operation journal 进行幂等补偿。对仅作展示的事件中心，不能把不可用伪装成领域操作失败。

## 8. 信号接入与恢复策略

### 8.1 应用部署

`ApplicationDeploymentCoordinator` 在持久操作已转为 `Failed`、或 `RecoveryProblemCode` 非空时发布事件；在同一应用的后续成功或已验证恢复时发布恢复信号。告警按应用 ID 聚合，单个事件仍保留 `operationId` 以供时间线和操作详情跳转。`ApplicationDeploymentOperationStore` 仍是操作权威，中心仅保存 `operationId`、应用 ID 引用、阶段和问题码，不复制诊断数组或应用名称。

因写入顺序需要恢复，事件包含操作 ID，启动重放器扫描保留的 terminal operation，并以 `(source, operationId, terminal state)` 作为幂等来源键。重放器不重新执行部署。

### 8.2 证书

在 `CertificateRenewalWorker`、`CertificateManager` 和 `CertificateOperationStore` 的成功/失败终态之间加一个适配器：手工与后台续期走同一事件路径。每日扫描检查到距离 `NotAfter` 的阈值时产生/更新 `certificate.expiring_soon`；成功续期、删除或吊销给出对应恢复/关闭依据。证书主题、域名、邮箱、ACME 原始响应、私钥路径不进入事件证据。

### 8.3 Guardian

Guardian 工作负载信号不能只靠 Server 临时轮询日志。扩展私有 `GuardianAgentRequest/Response` 为**版本化、带递增 sequence 的事件拉取协议**：Agent 先将受限事件追加到其本地账本；Server 成功提交中心事件后才推进 checkpoint。拉取可重放且按 `(agentInstanceId, sequence)` 去重。事件只含工作负载 ID、安全问题码、状态、次数和时间，不转送 stdout/stderr。

Guardian 自身崩溃/管道断开由 Server 的 `GuardianAvailabilityMonitor` 检测：启动后记录成功心跳；连续失败跨阈值才开一个 agent 告警，连续成功才恢复。它应同时检查实际安装/capability，避免对未安装 Agent 报警。受保护 Server 的重启成败也要通过 Guardian 事件账本传递，不能依赖未持久化的 `ILogger`。

### 8.4 Docker

在 Docker capability 启用且运行时已安装时，`DockerAvailabilityMonitor` 以有限间隔调用现有状态服务。只对可用→不可用、不可用→可用和持续超过配置窗口的异常发布事件；必须有指数退避，不能每次轮询都新增告警。用户或部署路径的失败由 Docker 适配器直接发布结构化操作信号。Docker CLI 输出、镜像名、容器名、挂载路径、命令行和 registry 凭据不得进入中心。

### 8.5 隧道

`TunnelService` 的受管状态转换是首选来源；状态仅靠刷新得到时，`TunnelAvailabilityMonitor` 对已启用的受管定义轮询。`Starting`、短暂重连和用户明确停止处于可配置宽限期，不产生断开告警。`Connected` 连续稳定一段时间后才发布恢复，避免网络抖动造成开关风暴。关联隧道 ID 留在中心，服务器地址、token、TOML 及日志正文不留存。

## 9. API、权限与实时更新

所有公开路由在 `Shared/RelaxKonOS.Protocol/EventAlerts/EventAlertApiRoutes.cs` 中定义，基址固定为 `/api/v1.0/event-alerts`。首个正式发布前如接口改变，直接更新所有调用方、测试和文档；不保留旧路由、字段别名或双格式解析。

| 路由 | 语义 | 所需权限 |
| --- | --- | --- |
| `GET /events` | 游标分页的事件时间线；按时间、type、severity、state、source 过滤 | `EventsRead` |
| `GET /alerts` | 当前/历史告警的游标分页和聚合计数 | `EventsRead` |
| `GET /alerts/{id}` | 告警、限长事件摘要、固定跳转和处理记录 | `EventsRead` + 资源访问二次检查 |
| `GET /summary` | 未确认/打开计数、最高级别和最近更新时间 | `EventsRead` |
| `POST /alerts/{id}/acknowledgement` | 确认并附加 1–512 字符的净化处理说明 | `EventsManage` |
| `POST /alerts/{id}/resolve` | 仅对允许人工关闭的类型，需结构化原因 | `EventsManage` |
| `POST /alerts/{id}/suppression` | 有效期和理由的临时抑制 | `EventsManage`；Critical 需额外授权 |
| `DELETE /alerts/{id}/suppression` | 解除抑制 | `EventsManage` |

事件 ID 使用 Server 生成 UUID，排序使用不可变 `(occurredAt, eventId)` 游标，不接受客户端 offset。最大 page size、过滤长度、时间范围、处理说明和导出量均受限。请求不得接受 `DedupeKey`、资源引用、severity、审计字段或跳转 URI 的任意覆盖。

新增 `ServerEventAlertsRead`、`ServerEventAlertsManage` 和（如需要）`ServerEventAlertsCriticalSuppress` 应用权限，并映射到 Server policy。中心看到一个资源并不自动授予其操作权限：详情/API 查询按目标领域再做授权；没有 Docker/证书/隧道读取权时展示“受限事件”及最少的时间、严重性和问题码，不能借中心越权泄露资源信息。

实时更新使用独立 SignalR hub，只发送 `alertId`、版本、状态、严重性和计数变化；客户端重新请求摘要/详情作为权威。每用户/会话订阅必须绑定授权，断线重连不可丢失持久事件，也不能借 hub 广播完整证据。

## 10. 存储、保留与审计

中心使用 Server 本地 SQLite 中独立的 `operational_events`、`operational_alerts`、`alert_actions`、`alert_suppressions` 和 `event_source_checkpoints` 表。迁移由既有 Server storage 机制管理，所有时间 UTC；事件/告警行不得保存 JSON 自由文本、原始异常或领域密钥。

建议默认策略：事件 90 天、已解决告警及其处理动作 365 天、打开/确认/抑制状态永不因普通保留作业删除。到期清理由受控 hosted service 分批执行，并为每批次写 Security Audit；引用仍被安全审计引用时，不删除审计本身。实际保留值做成受验证的 `EventAlerts` 配置，生产环境不得低于上述下限，除非有合规方案和后续独立设计。

中心的应用记录不是 Security Audit 的替代品。以下行为通过 `ISecurityAuditWriter` 写 append-only 审计，并将 `alertId`/安全资源引用作为目标：读取受限详情、确认、人工解决、创建/删除抑制、处理跳转请求、任一从中心触发的领域操作及保留清理。审计写入失败时，确认、人工关闭和抑制**失败关闭**；只读列表可降级但必须显示审计/服务不可用状态。

## 11. 桌面体验与跳转

新内置应用 ID 为 `relaxkonos.event-alerts`，单窗口、最小权限声明为读权限；只有用户请求处理动作时再走现有应用权限流程。应用包含：

- 顶部概览：打开、已确认、Critical 计数与最后刷新时间；不是“系统健康百分比”。
- 告警列表：严重性、来源、稳定摘要、持续时间、次数、状态和确认人安全显示名（无权限时不显示）。默认把 Open Critical/Error 放在前面；筛选与排序全部由 Server 支持。
- 详情抽屉：事件时间线、问题码、本地化说明、限长安全证据、关联 operation/correlation ID、处理记录和固定的“打开处理位置”按钮。
- 处理操作：确认、受权限保护的手动关闭和临时抑制；执行前显示影响、期限和审计提示，失败不乐观更新。
- 壳级入口：状态栏/通知区域显示**未确认**的最高级别计数。首次打开或严重性升级可显示一个本地 toast；同一告警在冷却时间内不重复弹出，且 `Acknowledged` 不因普通重复事件再 toast。

跳转使用 `RemediationTargetKind` 枚举和严格 DTO（如 `ApplicationDeployment`、`ApplicationDeploymentOperation`、`Certificate`、`GuardianWorkload`、`DockerOverview`、`TunnelDefinition`、`EventAlertDetail`），由客户端映射到本地 `relaxkonos://` URI 并调用 Shell 的 `IAppActivationService`。目标 ID 必须是 GUID 或经验证的本地 ID；客户端不解析来自 Server 的 URI、路径、命令或 display text。相关领域应用实现最小 `IAppActivationHandler`：导航到详情/日志/操作，而不绕过原有登录、能力检查、凭据确认或危险操作对话框。

若目标应用不存在、Server capability 关闭或用户没有权限，中心显示原因和可复制 correlation/operation ID；不降级为启动终端或展示原始日志。

## 12. 安全与隐私要求

- 沿用 `IObservabilitySanitizer` 的默认拒绝和 HMAC 引用策略。事件正文只允许每类型白名单字段，单值与总负载有硬上限；未知字段拒绝而非透传。
- 事件类型、问题码、severity、source、跳转种类、资源种类、状态转换全部是封闭枚举/目录；领域调用点不得新造字符串。
- 不以客户端传入 actor/resource/correlation 作为可信记录。actor 从已认证服务端主体取得，correlation 仅以既有规则验证/生成。
- 资源详情、事件证据、关联操作和跳转均重新做领域授权；不能通过猜测 UUID 获得其他用户的资源信息。
- 处理说明视为不可信文本：长度限制、纯文本/安全 Markdown 子集、净化敏感模式，UI HTML 编码；不得写入诊断日志或作为命令参数。
- 抑制必须有最短/最长有效期、理由、创建者和不可删除审计；Critical 的长期抑制需独立权限并醒目显示。
- 源事件可重放且签名/访问由本机 IPC 保护；Guardian 私有协议继续使用 shared secret 与本地 ACL，绝不经 HTTP 暴露。

### 简短威胁模型

| 威胁 | 控制 |
| --- | --- |
| 恶意工作负载或外部程序把秘密写进告警 | 不采集自由文本；仅接受白名单字段、稳定问题码和 sanitizer 输出。 |
| 低权限用户借中心读高权限资源 | `EventsRead` 外加领域资源二次授权；受限事件最小化显示。 |
| 伪造跳转导致任意 URI/路径/命令执行 | Server 只发送枚举 target；客户端本地构造并由 Shell 验证 URI。 |
| 攻击者制造海量失败耗尽库或通知轰炸 | 去重、限流、每源配额、事件保留上限、通知冷却和聚合。 |
| 管理员静默隐藏严重事故 | 强制期限、Critical 限制、醒目 Suppressed 状态和不可变安全审计。 |
| Server/Guardian 重启造成漏报或重复 | 源 checkpoint、幂等来源键、持久事件先写后投影、启动重放与心跳状态机。 |
| 中心数据库不可用掩盖领域事故 | 领域 journal/audit 独立保存；自检开 `source_degraded`，补偿重放，不把副作用当作已回滚。 |

## 13. 实施阶段

每阶段都同步更新全部仓库调用方、测试、三语本地化与文档；首次正式发布前不保留兼容别名、旧路由或双写格式。每阶段结束至少执行受影响项目的 build 与相关自动化测试，最终执行 `dotnet build RelaxKonOS.sln -c Debug`。

| 阶段 | 工作 | 可独立验收 |
| --- | --- | --- |
| M0：盘点与契约冻结 | 枚举所有现有领域状态、问题码、审计和客户端入口；冻结事件类型、dedupe、severity、保留、权限、目标枚举及隐私字段表。 | 每个首批类型有唯一来源、恢复条件、资源权限和跳转目标；无“解析日志文本”设计。 |
| M1：Protocol 与持久化 | 建立 EventAlerts Protocol、SQLite schema/migration、store、事件验证、投影、游标查询、保留作业和 Server policies。 | 重启后 event/alert/action 不丢失；幂等重放不重复；非法字段/状态转换/游标被拒绝。 |
| M2：首批领域接入 | 为部署、证书、Docker、隧道添加结构化发布；实现 Docker/隧道状态转换、宽限与恢复；保留领域 journal 为权威。 | 每个来源有 failure、duplicate、recovery、未安装与恢复测试。 |
| M3：Guardian 可靠接入 | 实现 Agent 事件账本、sequence/checkpoint、Server availability monitor、工作负载/受保护 Server 信号和启动补偿。 | Agent/Server 任意一侧重启、管道中断、重复拉取、重启预算耗尽均无漏报/重复告警。 |
| M4：受控操作与审计 | 实现确认、人工关闭、临时抑制、二次资源授权及 Security Audit 失败关闭。 | 处理动作可追溯；无权限/过期/冲突/审计不可写不会改变告警状态。 |
| M5：桌面应用与跳转 | 注册应用、列表/详情/状态徽章、本地化、SignalR 提示、固定深链和各领域 activation handler。 | 链接到正确资源且不绕过领域确认；离线、权限不足、应用缺失与实时重连体验明确。 |
| M6：恢复与发布验证 | 真实 Linux（含 Docker/FRP）和 Windows Server（Guardian）演练、性能/容量、文档和 Android 消费契约评审。 | §17 所列情景有可复核证据；保留与安全检查通过。 |

## 14. 测试策略

### 单元与契约测试

- 每个注册事件类型验证 schema、默认 severity、资源类型、payload 白名单、dedupe 与允许状态转换。
- 事件追加/投影在重复投递、并发、时钟相同、恢复先到、存储重启和保留清理下确定性工作。
- 所有 API 验证分页、过滤、上限、授权、Capability 关闭、未知 JSON 字段和版本不兼容；Protocol route/DTO 有编译时调用覆盖。
- sanitizer 测试覆盖 password/JWT/token/私钥/URL query/路径/域名/命令行/异常文本，确保它们不会进入中心库、hub、客户端模型或审计。
- 审计测试覆盖确认、关闭、抑制、受限详情读取和 audit sink 失败关闭。

### 集成与故障演练

- 创建失败的应用部署、恢复失败、后续成功与 Server 重启重放。
- 模拟 ACME 续期连续失败、重试耗尽、即将过期、手动成功续期与证书删除。
- Docker 启停/不可用、操作失败、轮询抖动和恢复；未安装 Docker 不产生 false positive。
- FRP 短暂重连、超过宽限期的断开、RuntimeUnavailable、恢复和用户计划停用。
- Guardian 工作负载启动失败、异常退出、健康检查失败、重启预算耗尽、Agent 管道失联、Agent 崩溃/恢复、受保护 Server 重启失败。
- 多用户、无领域读取权、过期会话、没有 EventAlerts 权限和 SignalR 订阅重连。

### UI 与可用性验证

- 三语下的严重性、状态、问题码说明、空状态、受限状态、离线和降级状态可理解且不溢出。
- 键盘可达、屏幕阅读器标签、列表虚拟化及高对比度主题；1000+ 历史事件分页不卡顿。
- 从每个首批告警跳转到正确应用/资源，确认原应用仍要求其既有权限与危险操作确认。

## 15. 迁移与部署

这是新功能，不迁移既有领域审计为中心事件，也不反向把中心告警写回旧 ledger。M0 盘点后可为仍在保留窗口内、且拥有稳定 ID/问题码的 terminal operation 做一次受限回填；回填事件必须标记 `historical-import`，不触发 toast/通知，也不把未知旧失败伪装成打开告警。

配置示例应在实现时加入安装器和用户模式文档，至少包括启用开关、事件/告警保留、Docker/Guardian/隧道轮询间隔、连续失败阈值、恢复稳定窗口、通知冷却及证书临期阈值。生产启动应拒绝非法保留、负数阈值、超大 payload、缺失中心存储路径或将中心的敏感动作审计关闭的组合。

User Mode 下只启用该模式实际具备的 capability 和同一 UID 的 Guardian；不尝试启动系统服务或提升权限。Windows Server 的 Guardian 服务检查走 SCM，Linux 走 systemd/私有 IPC；这两种检测的差异留在 source adapter，不能泄漏进通用 Protocol 或 UI。

## 16. 依赖、取舍与开放问题

- 本 Goal 依赖后端可观测性目标中 `IObservabilitySanitizer`、关联上下文和安全审计的实际可用性。若其尚未完成，M1 必须先补齐中心需要的最小共享能力，不能复制一份弱化的 sanitizer/audit writer。
- 若 `SecurityAuditWriter` 的“读取审计”本身还没有权限与 API 设计，中心只显示自己的操作时间线；不得借此开放原始安全审计库。
- 需要 M0 产品确认：`certificate.expiring_soon` 的 Warning/Critical 天数、各级通知冷却、人工关闭是否允许于所有类型、抑制最长时长，以及是否把“Docker 未安装但已启用部署能力”视为配置错误事件。
- 需要 M0/Android 评审：未来移动推送是按告警状态变化、按事件还是按摘要订阅；第一阶段仅冻结不含个人数据的服务器读取/通知投影，不提前引入设备 token。
- 如真实运行环境证明 Domain journal 无法可靠重放，必须先为该领域补足持久 outbox/来源序列；不能用内存 Channel 或 SignalR 成功来声称“可靠告警”。

## 17. Goal 关闭规则

本 Goal 仅在以下全部成立时关闭：

- M0–M6 完成，首批五个领域均能产生、聚合、查询、确认、恢复与跳转的可复核证据。
- 事件、告警、处理记录和 source checkpoint 在 Server 重启与重复投递后保持一致；Guardian 的重启/断链演练证明不漏报、不重复通知。
- 对所有首批来源验证未安装、短暂抖动、持续异常、恢复、权限不足和中心存储降级路径。
- 所有敏感处理动作写入可验证 Security Audit，且审计不可写时失败关闭；测试证明秘密和原始外部文本不泄露。
- Linux + Docker/FRP 与 Windows Server + Guardian 的真实环境验证完成，未验证平台明确记录为未验证。
- `dotnet build RelaxKonOS.sln -c Debug`、相关自动化测试、三语本地化、帮助文档和 README 入口均通过；测试环境、版本、命令和结果被记录。

仅有 UI 原型、日志聚合、模拟状态或一个“重启”按钮均不能关闭本 Goal。
