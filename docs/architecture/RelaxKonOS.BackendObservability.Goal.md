# RelaxKonOS 后端可观测性与安全日志（Goal 执行版）

> 状态：设计完成，待实施；本文不代表日志能力已经实现。
>
> 建立日期：2026-09-21。代码基线：当前工作区。
>
> 适用范围：`RelaxKonOS.Server`、`RelaxKonOS.PrivilegedHelper`、`RelaxKonOS.Guardian.Agent`，以及它们发起的宿主级受管操作。

本文定义后端处理的统一日志、诊断与安全审计边界，使运维人员能够回答“何时、由谁、对什么资源、经由哪条处理链发生了什么、为何失败或被拒绝”，同时避免把凭据、令牌、私有配置或用户数据写入日志。

本文是 [架构设计](./RelaxKonOS.Architecture.md)、[安全模型](../platform/RelaxKonOS.Security.md) 与 [特权操作 Goal](../platform/RelaxKonOS.PrivilegedOperations.Goal.md) 的实施补充。发生冲突时，安全模型和特权操作的最小权限约束优先。

## 1. 目标、完成条件与非目标

### 1.1 目标

首次发布前，所有后端请求、后台作业、跨进程调用和高风险状态变更必须有一致、结构化、可关联且经净化的可观测记录。实现后应能：

- 用一个 `correlationId` 关联 HTTP/SignalR 入口、领域服务、数据库失败、特权 Helper 和 Guardian 的同一处理链；
- 以稳定 `eventId`、`eventName`、`outcome` 与 `problemCode` 聚合错误，不依赖自然语言消息；
- 在不读取密码、JWT、cookie、私钥、订阅 URL 或文件内容的前提下，调查认证暴力尝试、授权拒绝、权限提升、配置漂移、异常重启、特权拒绝和失败恢复；
- 将面向排障的运行日志、不可替代的安全审计、以及领域操作状态分开保存，分别定义访问权限、保留期和失败语义；
- 在日志接收端不可用或写入失败时保持业务安全：普通诊断可降级，安全关键操作不得静默地失去审计。

### 1.2 发布完成条件

以下条件全部满足才可将本 Goal 标记为完成：

- Server、Helper 与 Guardian 均使用同一事件命名、字段和净化规则；没有新的直接 `Console.WriteLine`、裸 `ILogger` 自由文本或未审查的异常输出路径。
- 每个 HTTP 请求、SignalR hub 调用、后台任务和 IPC 请求都会建立或延续关联上下文；跨进程边界传递的仅是安全的关联标识，绝不传递 JWT 或密码。
- 认证、授权、特权、配置写入、安装/升级/卸载、服务控制、证书、网络/隧道、防火墙、数据删除与 Guardian 重启均产出规定的审计事件。
- 自动化测试证明常见秘密、原始请求/响应、完整命令行与未净化外部输出不会进入任一日志或审计 sink；同时验证关联、事件分类、采样、轮换和审计失败策略。
- 在隔离的 Ubuntu 与 Windows Server 环境可按 `operationId` 或 `correlationId` 完整还原一次成功、失败和被拒绝的高风险操作。

### 1.3 非目标

- 不把日志当作指标、分布式追踪或用户可浏览的业务历史的替代品；三者可共享关联字段，但有独立存储与保留策略。
- 不在本 Goal 中建设公共日志平台、SIEM、云遥测、会话录制或任意客户端日志上传。
- 不允许 Client 直接读取服务器原始日志；以后如提供诊断包，必须是受能力授权的、已筛选和再次净化的导出。
- 不以“记录更多内容”为由降低凭据保护、最小权限、数据最小化或错误信息的安全边界。

## 2. 当前基线与缺口

当前 `appsettings.json` 只配置了 ASP.NET Core 的默认 `Logging:LogLevel`。Server 在 Nginx、Docker、Proxy、Guardian 等少数领域已经注入 `ILogger<T>`，但事件名称、字段、关联 ID、输出位置与异常处理方式没有统一契约。`RelaxKonOS.Guardian.Agent` 仅覆盖监控探测和重启；`RelaxKonOS.PrivilegedHelper` 没有同等的统一日志入口。

现有的 `HostOperationJournal`、`SettingsOperationJournal`、`TunnelAudit`、`ProxyAuditStore`、文件服务审计及安装/部署 ledger 各自保存操作状态或领域审计。这些记录有业务价值，但字段、保留策略和失败行为不一致，不能替代全局安全审计，也不能取代可检索的应用诊断日志。

当前已存在的正向基础包括：稳定的领域 `problemCode`、若干 `operationId`/幂等键、`ILogger<T>`、`Activity` 兼容的 ASP.NET Core 请求管线，以及测试中对凭据不泄露的检查。本 Goal 在这些基础上收敛接口；不保留旧格式、旧事件名或双写兼容层。

## 3. 冻结的总体模型

### 3.1 三类记录，三种失败语义

| 类型 | 用途 | 最低字段 | 失败策略 | 典型保存位置 |
| --- | --- | --- | --- | --- |
| 运行日志（diagnostic log） | 排障、性能分析、运行状态 | 事件信封、净化后的上下文、异常摘要 | 不阻塞低风险业务；记录一次本地降级计数 | Linux journald/stdout、Windows Event Log，及受 ACL 保护的轮换 JSON 文件 |
| 安全审计（security audit） | 回答高风险操作是否发生、由谁发起、是否被拒绝 | 审计事件、actor reference、目标安全引用、结果、关联/操作 ID | **失败关闭**：若关键事件无法持久化，不执行或不确认该状态变更 | HostGlobal SQLite 的 append-only 审计表；安装后的服务账户只可追加 |
| 操作日记（operation journal） | 幂等、恢复、进度和面向产品的操作查询 | 领域状态机、operation ID、阶段、问题码 | 按领域定义恢复/重试；不是安全审计替代品 | 既有领域 SQLite/file ledger |

一次高风险操作通常同时写入三类记录：入口和异常写运行日志；状态转换写操作日记；请求接受、授权决定和最终结果写安全审计。三者以 `correlationId`、`operationId` 和 `eventName` 关联，但不得通过复制原始请求来关联。

### 3.2 统一事件信封

所有结构化日志与审计事件必须能映射到以下概念模型；各 sink 可省略未适用字段，但不得改变字段含义或重新命名。

```text
timestampUtc          ISO-8601 UTC
severity              Trace | Debug | Information | Warning | Error | Critical
eventId               稳定整数，按事件目录分配
eventName             小写点分命名，例如 security.authorization.denied
component             server | privileged-helper | guardian
environment           Production | Development | ...（不含机器秘密）
instanceId            安装时生成的随机实例引用；不得使用主机名
correlationId         外部可展示的随机 UUID，贯穿一条处理链
traceId/spanId        W3C Activity 标识；仅用于追踪关联
operationId           可选，持久操作的 UUID
actorReference        可选，稳定但不可逆的主体引用，不是用户名/别名/JWT
resourceType          例如 certificate、nginx-site、service、file
resourceReference     资源 UUID、受控 ID 或带盐哈希，绝不写敏感原值
action                封闭的领域动作，例如 certificate.deploy
outcome               started | succeeded | denied | failed | cancelled | recovered
problemCode           稳定、无秘密的领域问题码
durationMs            可选，端到端或本阶段耗时
platform              linux | windows
message               面向人的短摘要；禁止由外部输入直接构成
exceptionType         可选、白名单化的异常类型
```

`correlationId` 由不可信 HTTP 请求提供时必须校验为 UUID；无效、过长或缺失值改为 Server 新生成的 UUID。它可回显给 Client 用于支持工单，但不是身份、授权或幂等依据。内部 `Activity.TraceId` 不能替代该 ID，因为它可能不存在、被采样或来自不可信上游。

### 3.3 关联上下文与处理链

```text
HTTP / SignalR / Background worker
  → RequestObservationMiddleware 建立 LogScope + Activity + correlationId
  → Endpoint / domain service 增加 action、actorReference、operationId
  → IPrivilegedOperationTransport 传递 correlationId、operationId、action（无 JWT）
  → PrivilegedHelper 重新建立本地 scope 并写入自己的审计/运行日志
  → Guardian IPC 同样传递安全关联字段
  → 请求结束写一条净化的 outcome/duration 事件
```

- 认证完成前只可记录 `actorReference = anonymous` 和受限的来源网络分类；认证成功后由受信任的身份服务设置 actor reference。不得把 Client 提交的用户 ID 放进 scope。
- SignalR 每个 hub invocation 建立独立 child activity；连接级 ID 只能作为非敏感诊断字段，不能单独作为审计关联。
- 后台恢复任务必须建立新的 `correlationId`，并在日志和审计中带上原始 `operationId` 与 `recoveryOfOperationId`；不能伪装成原调用仍在执行。
- Helper/Guardian 必须拒绝缺失或格式错误的关联元数据，但该拒绝本身使用本地生成的关联 ID 记录。关联字段不能改变 Helper 的授权、能力或目标验证。

## 4. 事件目录、级别与记录规则

### 4.1 事件目录

事件 ID 和名称集中定义在 `Shared/RelaxKonOS.Protocol/Observability` 的不可变目录中；实现不得在调用点自定义字符串或整数。按如下范围分配：

| 范围 | ID | 事件示例 | 必需结果 |
| --- | ---: | --- | --- |
| 请求与处理链 | 1000–1099 | `request.started`、`request.completed`、`request.unhandled_exception` | completed / failed |
| 身份与会话 | 1100–1199 | `security.login.succeeded`、`security.login.denied`、`security.token.rejected` | succeeded / denied |
| 授权与能力 | 1200–1299 | `security.authorization.denied`、`security.elevation.granted` | granted / denied |
| 特权与 IPC | 1300–1399 | `privileged.request.accepted`、`privileged.transport.rejected` | succeeded / denied / failed |
| 受管状态变更 | 1400–1599 | `configuration.changed`、`service.restart`、`certificate.deployed` | started / succeeded / failed / recovered |
| 外部依赖与输入 | 1600–1699 | `dependency.unavailable`、`input.rejected`、`trust.validation_failed` | warning / denied / failed |
| 数据与恢复 | 1700–1799 | `storage.migration.failed`、`operation.recovery.started` | succeeded / failed / recovered |
| Guardian 与可用性 | 1800–1899 | `guardian.probe.failed`、`guardian.restart.completed` | succeeded / failed |
| 日志系统自身 | 1900–1999 | `observability.sink.degraded`、`observability.audit_write_failed` | warning / critical |

事件目录变更是 wire/运维契约变更：在首次正式发布前直接更新全部调用方、测试、文档和检测规则；不添加旧名别名或双事件写入。

### 4.2 日志级别

- `Trace`：仅本地、短期的开发诊断，默认关闭；不允许包含额外的敏感数据。
- `Debug`：开发诊断和受控测试；生产默认关闭，字段仍需完全净化。
- `Information`：服务启动/停止、成功的高风险动作、有限速的请求完成和重要状态转换。
- `Warning`：预期但需要调查的拒绝、重试、降级、配置漂移、依赖异常或连续失败阈值。
- `Error`：一次操作失败、未处理异常、无法恢复的外部依赖/存储失败；附加净化后的异常。
- `Critical`：服务可能错误运行、审计不可写导致关键操作被拒绝、信任链破坏、重复崩溃或无法维持安全边界。

认证失败、授权拒绝和无效输入不是 `Error`：它们使用 `Information` 或经阈值聚合的 `Warning`，同时始终写安全审计。这样既保留攻击线索，也避免攻击者制造无穷 Error 噪声。

### 4.3 必须记录的安全与风险事件

下列行为无论成功、拒绝或失败都必须生成审计事件；成功/失败的运行日志按级别规则生成：

- 登录、刷新、登出、账号/别名认证方式变更、连续失败阈值、速率限制和会话撤销；
- 授权拒绝、管理员认证申请/取消/失败/授予/过期，以及 capability 与目标范围不匹配；
- 所有 Helper/Guardian IPC 接收、协议/共享密钥/ACL 验证失败、版本不匹配、超时与取消；
- 文件删除、批量移动/复制、受保护路径写入、注册表/环境/主机名/DNS/时区变更；
- 服务、Web Server、Docker、Proxy、Firewall、隧道、证书、SMB、安装器的创建/修改/删除/启停/升级/回滚；
- 外部内容信任失败（签名、哈希、TLS、清单校验）、配置漂移和恢复动作；
- Guardian 对 Server 的重启决定、重启执行结果和重启风暴抑制。

正常只读查询、健康探针与高频性能采样不逐条写 `Information`。它们保留指标/追踪，异常时按窗口聚合为一条事件（次数、窗口、最后问题码），并禁止记录每次请求细节。

## 5. 数据最小化、净化与访问控制

### 5.1 永不记录的数据

任何日志、审计、异常、诊断包、指标标签或 `problemCode` 中都不得出现：

- 密码、验证码、JWT/refresh token、cookie、Authorization/Proxy-Authorization 头、共享密钥、PAM 数据、Windows token 或会话密钥；
- 私钥、PFX、证书私有材料、SSH/Git/Registry/Docker 凭据、订阅 URL 中的 token、完整连接字符串；
- 原始 HTTP/SignalR 请求或响应、全部 header、query string、表单、配置文件、环境变量、文件内容、终端输入输出或未过滤命令行；
- 用户可控的完整路径、文件名、域名、账号名、别名、设备名、IP 地址或客户端 user agent，除非该字段经过本节定义的用途审查与不可逆转换。

禁止为了排障而记录 `Exception.ToString()`、`HttpRequest.ToString()`、`ProcessStartInfo`、外部进程完整 stdout/stderr 或对象序列化结果。异常和外部输出只能经过统一净化器、长度限制及键名匹配后作为受控摘要写入。

### 5.2 安全引用与净化器

`IObservabilitySanitizer` 是唯一允许将外部字符串、异常摘要和资源标识写入记录的边界。它必须：

- 对敏感键名和常见凭据格式做大小写无关替换，未知 query/header/JSON 字段默认丢弃；
- 将路径、URL、账号等高基数/个人数据映射为每安装实例带轮换版本的 HMAC 引用，例如 `hmac:v1:...`；只有确有运维必要的受管资源 ID 可明文；
- 将异常限定为白名单类型、`problemCode`、固定错误类别和最大 1 KiB 的已净化摘要；外部命令输出采用领域专用 sanitizer、尾部截断及明确的 `truncated=true`；
- 限制单事件字段数、单字段长度和序列化总量；超过上限时丢弃附加字段并记录计数，而不是写原始输入；
- 为每项新的明文字段维护“目的、读取角色、保留期、替代安全引用”的审查记录。

同一安装中的哈希可用于事件关联，跨安装不可关联。轮换 HMAC 密钥后保留旧 key ID 仅在其对应日志保留期内；密钥本身不进入应用配置、日志或诊断包。

### 5.3 存储、保留与读取

- 生产环境运行日志默认保留 30 天，受限安全审计默认保留 365 天；具体部署可延长安全审计但不能低于该下限。开发环境可缩短运行日志，但不能关闭高风险审计。
- JSON 文件按日期和大小轮换，使用原子创建、权限为服务身份可写且管理员可读；Linux 同时发送 journald，Windows 同时发送专用 Event Log source。日志目录、父目录、配置与轮换文件必须接受安装器 ACL 检查。
- 审计表使用 append-only schema：普通运行账户无 `UPDATE`/`DELETE` 权限；清理由独立、受控保留作业以批量和自身审计执行。每条审计记录含前一条链式 HMAC 摘要和 `keyId`，用于发现意外篡改；它不是遭到管理员完全控制主机后的不可否认证据。
- 读取原始运行日志限本机管理员和受控支持流程；安全审计的读取需独立的 `observability.audit.read` 权限、访问审计和分页/导出上限。Client API 不暴露文件路径、tail 或任意查询表达式。

## 6. 实现边界与配置

### 6.1 建议模块落点

```text
Shared/RelaxKonOS.Protocol/Observability/
  ObservabilityEventCatalog.cs       # event ID/name/action constants
  CorrelationContext.cs              # 只含可安全跨进程字段
  SecurityAuditContracts.cs

RelaxKonOS.Server/Observability/
  RequestObservationMiddleware.cs
  CorrelationContextAccessor.cs
  ObservabilityLogScope.cs
  IObservabilitySanitizer.cs
  SecurityAuditWriter.cs
  SecurityAuditRetentionWorker.cs
  LoggingOptions.cs

RelaxKonOS.PrivilegedHelper/Observability/
  HelperCorrelationContext.cs
  HelperSecurityAuditWriter.cs

RelaxKonOS.Guardian.Agent/Observability/
  GuardianCorrelationContext.cs
```

所有 Endpoint 仅负责设置已验证的 action/operation ID，不直接决定格式、sink 或秘密净化。领域服务通过小型 `IEventLogger` / `ISecurityAuditWriter` 接口记录封闭的事件对象；不得将 `ILogger` 或任意字典穿透 Protocol 边界。基础设施把事件映射到 `ILogger`、审计存储及需要的计数器。

### 6.2 默认配置合同

新增 `Observability` 配置节，至少包含：`InstanceId`、运行日志目录、每文件上限、保留天数、审计保留天数、请求成功采样率、慢请求阈值、拒绝聚合窗口、外部输出最大字节数和环境允许的 sink。启动时必须拒绝以下不安全组合：生产关闭安全审计、审计保留期小于 365 天、日志目录不为绝对路径、大小/保留/采样值非法、HMAC key 缺失、或 audit sink 不可写。

日志级别可按 component/category 配置，但不能通过配置启用原始请求体、秘密字段或取消 sanitizer。生产中的 `Debug` 临时提升必须有到期时间，且该配置变更本身写安全审计。

### 6.3 异常、HTTP 与外部进程边界

- 全局异常处理中记录一次 `request.unhandled_exception`，返回既有稳定 `problemCode` 和 `correlationId`，不把堆栈、内部路径或底层异常文本返回给 Client。
- 入口仅记录 method、已注册的 route template、最终 status class、耗时和净化后的安全上下文；不记录原始 URL。4xx 默认采样/聚合，5xx 和慢请求始终记录。
- `HttpClient`、数据库、命名管道和外部进程使用活动上下文与固定 dependency 名称；URL/SQL/命令行不能成为日志字段。失败映射为领域 `problemCode` 并附带净化异常类别。
- 外部进程输出只在明确需要的领域操作中捕获，先由专用 sanitizer 处理、限制大小，并绝不跨入公开 API 或通用运行日志；原始受管诊断若确有保存必要，使用独立加密文件并有单独 Goal/访问能力。

## 7. Goal 执行计划

每个 Goal 结束时，`dotnet build RelaxKonOS.sln -c Debug` 和现有自动化测试必须通过。每个阶段均直接迁移本仓库调用方、测试和文档；首次正式发布前不保留旧事件格式或兼容适配层。

### Goal 0：清单、数据分类与事件目录冻结

**工作**：盘点所有 `ILogger`、`Console`、异常边界、外部进程输出、现有 audit/journal、HTTP/SignalR 入口和 IPC contract。为每个记录字段标记来源、敏感等级、用途、sink、读取角色和保留期。冻结事件 ID/名称、action 枚举、审计关键操作清单、默认保留期和生产 sink。

**验收**：没有未分类日志或审计存储；给出 Server/Helper/Guardian 事件映射；明确每个既有领域审计是否保留为操作日记、迁入安全审计或删除；安全负责人确认 `instanceId`、actor reference、HMAC key 轮换和审计失败关闭策略。

### Goal 1：协议、关联上下文与净化基础

**工作**：实现事件目录、`CorrelationContext`、scope/accessor、sanitizer 和受限异常摘要。加入 HTTP、SignalR、后台 worker 的入口中间件；为 privileged/guardian pipe contract 增加仅含安全字段的关联上下文及版本验证。删除散落的原始外部字符串日志调用。

**验收**：一次模拟请求和一项后台恢复可完整关联；无效 client correlation ID 不能污染 scope；Helper/Guardian 可关联但不接收 JWT；测试覆盖 password、JWT、cookie、URL token、路径、命令输出和异常中的秘密净化。

### Goal 2：运行日志 sink、轮换与异常边界

**工作**：配置结构化 JSON 日志、journald/Windows Event Log 和 ACL 受保护的轮换文件；实现 back-pressure、单事件上限、成功请求采样、拒绝聚合与 sink 降级告警。实现全局异常/问题码边界和外部依赖观察。

**验收**：生产默认配置可启动且拒绝不安全目录/配置；高频健康请求不会产生日志风暴；5xx、慢请求、依赖失败可按 correlation ID 查询；磁盘满或单一运行 sink 失败不会泄露数据、阻塞低风险请求或导致无穷递归日志。

### Goal 3：统一安全审计与关键领域迁移

**工作**：实现 append-only 审计表、链式完整性字段、权限/迁移、保留作业和 audit writer。迁移认证/会话、授权/提升、特权 transport、文件受保护写入、Settings、Certificate、WebServer、Proxy、Tunnel、Firewall、Docker、安装部署和 Guardian 高风险操作。

**验收**：每一项清单中的高风险操作都同时可记录 accepted 与 terminal outcome；审计写入失败时关键变更安全地拒绝或不确认；普通诊断 sink 失败不影响已定义的低风险业务；审计表不包含禁录数据且普通服务账户不能修改历史记录。

### Goal 4：领域日志收敛与恢复链路

**工作**：将 Server、Helper、Guardian 的手写自由文本日志替换为事件目录调用；把现有操作日记和全局审计以 operation/correlation ID 关联。补齐安装、证书、WebServer、Proxy、SMB、部署、系统设置和 Guardian 的阶段事件、回滚和恢复事件。

**验收**：从一个 `operationId` 能看到开始、阶段、失败/恢复或完成；所有 Helper 拒绝具备 action、资源安全引用和问题码；日志检查不再发现 `Console.WriteLine`、序列化请求对象、完整 command/output 或未净化异常路径。

### Goal 5：运维读取、验证与发布收尾

**工作**：实现受权限保护的审计查询/受控诊断包（如果 Goal 0 已批准），部署脚本 ACL 验证、key rotation、备份/恢复和保留作业。完成 Ubuntu/Windows Server 隔离环境的故障注入与手册。

**验收**：支持人员能只凭 support correlation ID 定位同一次操作；安全审计查询本身被审计；轮换、磁盘满、SQLite 锁定、Helper 断连、Guardian 重启风暴、非法 IPC、攻击性登录失败和异常中断均按设计工作；发布包与部署文档不依赖开发目录或宽松 ACL。

## 8. 测试与验收矩阵

- **单元测试**：事件目录唯一性、scope 生命周期、ID 格式、事件字段上限、敏感键/模式净化、哈希稳定性与 key version、异常摘要、采样与聚合。
- **集成测试**：HTTP/SignalR/worker/Helper/Guardian 的关联传播，认证/授权/特权/配置修改的审计双记录，稳定 problem code 映射，以及审计失败关闭。
- **存储测试**：SQLite 迁移、append-only 权限、完整性链、保留批处理、并发写入、崩溃恢复、目录 ACL 与轮换原子性。
- **安全回归**：在请求体、header、query、cookie、密码、异常、外部 stdout/stderr、路径、订阅 URL 和配置中注入已知 secret，扫描所有 sink 与诊断导出，确认零泄露。
- **容量与故障测试**：日志洪泛、超长字段、磁盘满、sink 不可用、审计锁冲突、时钟跳变、Helper/Guardian 断开、重复重启和并发高风险操作。
- **人工验证**：在隔离 Ubuntu/Windows Server 用 correlation ID 追踪成功、拒绝、失败、取消、重试和恢复各一次；确认 Windows Event Log/journald、轮换文件与审计数据库的访问权限。

## 9. 实施前决策门

在 Goal 1 前必须确认以下决定：

1. 生产运行日志的初始汇聚位置：仅本机、企业 SIEM，或两者；如有远程 exporter，明确 TLS、认证、断网缓冲与数据驻留要求。
2. HostGlobal 审计库的账户/ACL 模型与安装器拥有的 HMAC key 保护方式；该模型必须适用于 Linux system mode、Linux user mode 和 Windows Service。
3. `actorReference` 的产生和轮换方法，及是否允许支持人员在受控流程中解析其到内部用户记录。
4. 哪些低风险写操作允许在审计短暂不可用时排队，哪些操作必须立即失败关闭；默认所有宿主级、身份、权限和删除操作失败关闭。
5. 是否批准受控诊断包及其读取能力；未批准前只提供本机管理员日志访问，不开放 Server API。

在这些决定冻结前，不应批量添加日志调用或引入新的日志库，以免形成无法清理的格式、泄露面和兼容负担。

## 10. 实施记录

### 2026-09-25：用户执行（effective OS user）通道接入

- 事件目录在「特权与 IPC」区间新增 `user.execution.request.accepted`(1310)、`user.execution.request.completed`(1311)，封闭 action 新增 `user.execution`。
- `UserExecutionRequest` 增加 `CorrelationContext`，协议升级为 `1.2`；Server 的两个用户执行 transport 按提权 transport 同一模式写 accepted/completed 审计并在审计不可用时失败关闭；Helper 的两个用户执行入口拒绝缺失或非法的关联元数据（§3.3 要求的「Helper 必须拒绝缺失或格式错误的关联元数据」）。
- `UserExecutionContextResolver` 的身份解析拒绝写入 `security.authorization.denied`。成功解析不逐请求记录，遵守 §4.3 对正常只读操作不逐条写 `Information` 的约束。
- **已知缺陷（先于本次改动存在，未修复）**：`RelaxKonOS.Server.Tests/ObservabilityChecks.cs` 由提交 `a8caa481` 引入，却调用了 `TestAssert` 中并不存在的 `Equal`/`True`，因此 `RelaxKonOS.Server.Tests` 一直无法编译、该文件的断言从未被执行。补上缺失的断言辅助方法后，`sanitizer must not retain supplied secrets` 断言失败，说明 `ObservabilitySanitizer` 的实现与其测试期望不一致（从实现看，`SensitiveAssignment` 要求敏感键名后紧跟 `:`/`=`，疑似未覆盖带引号的 JSON 赋值形式）。该缺陷阻塞 §1.2 中「自动化测试证明秘密不会进入任一日志或审计 sink」的发布完成条件，需要单独定稿修复后再更新本节。

