# RelaxKonOS 容器化应用部署设计

> 对应 [Goal](./RelaxKonOS.ApplicationDeployment.Goal.md) 与 [实施进度](./RelaxKonOS.ApplicationDeployment.Progress.md)。
>
> 冻结日期：2026-09-19。
>
> 本文冻结第一阶段（M1–M5）的领域模型、协议、状态机、模板契约、持久化与恢复语义、客户端形态和测试策略。协议一经本文冻结即直接生效：按根目录 `AGENTS.md`，首个正式发布前不保留旧路由、别名、双格式解析或兼容适配器，契约变更在同一次改动内更新全部仓库调用方。

## 1. 目标、范围与非目标

### 1.1 本阶段交付

在既有 Docker 能力之上建立**应用部署领域**：把「现成镜像 / Java 可执行 JAR / .NET 发布归档 / Python 项目归档」变成一台受 RelaxKonOS 管理的、可回滚的容器化应用。

交付的形状是**停机替换**（stop-and-replace）的发布事务：新实例发布成功并通过对就绪检查后，才删除旧实例并把候选容器改名为规范名称；任一步失败都尽力恢复上一实例与站点配置，且恢复结果与原始错误分开上报。

### 1.2 明确不在本阶段

- Git / 源码构建、多阶段编译模板。
- 多服务拓扑、服务间依赖、自动切流、自动触发部署。
- 远程 Docker Engine、Windows 容器、集群。
- 任意 Dockerfile 上传。
- 任意宿主目录挂载（只支持受管命名卷）。
- 数据库迁移：镜像回滚不撤销持久化数据变化，第一阶段不自动执行迁移。
- 特权容器、Docker socket 挂载。

### 1.3 与 Goal 的一处偏离（已知缺口）

Goal §2 把「单服务 Compose 项目」列入第一阶段范围，实施清单 I06 亦如此表述。本次实现**未**采用 Compose 作为发布载体：发布事务的停机替换、有界就绪检查、候选容器改名与失败回滚无法用 Compose 表达为事务，故运行时直接组合 `IDockerEngineService` 的容器原语。`IDockerComposeService` 未被本领域调用。

因此「以既有 Compose 项目为输入进行部署」**未实现**，已在进度文档登记为缺口，不计入完成项。`IDockerComposeService` 仍可由 Docker 管理器独立使用。

## 2. 领域模型与持久化

### 2.1 三个聚合

| 聚合 | 语义 | 可变性 |
| --- | --- | --- |
| `ApplicationRecord` | 操作者的**意图**：名称、来源类型、工作负载类型、期望状态、就绪级别、端口/绑定地址、资源上限、数据卷、配置、可选站点关联、当前版本指针、观测到的运行时绑定 | 可变（`Update`） |
| `RevisionRecord` | **不可变**的已发布版本：绑定实际镜像身份（image id）、启动定义、配置快照（含机密版本引用）、平台、模板版本、输入校验值 | 不可变；只有 `Number` 在发布时由存储分配 |
| `DeploymentEntry` + `DeploymentAudit` | 持久后台操作及其审计轨迹：幂等引用、请求指纹、资源锁、状态/阶段/进度、稳定问题码、恢复问题码 | 仅由协调器追加式推进 |

关键约束：**镜像标签只作输入与展示**。`ApplicationRecord` 记录的是意图，`RevisionRecord.ImageId` 才是绑定；本地构建若拿不到 registry digest，记录 image ID 而**不编造 digest**。

`ApplicationRecord` **不包含**来源（archive 引用 / image 引用 / 入口参数）。来源由已发布修订绑定，因此定义可编辑而不会暗示重新构建；`CreateApplicationRequest` / `UpdateApplicationRequest` 因此不携带 `source`。

### 2.2 持久化布局

根目录 `ApplicationDeploymentOptions.RootDirectory`（默认 `data/application-deployments`，相对 `ContentRoot`）：

| 路径 | 内容 | 写入方式 |
| --- | --- | --- |
| `catalog.json` | 应用与修订账本 | 临时文件 + `flush(true)` + 原子 `File.Move(overwrite)` |
| `secrets.json` | 机密密文（DataProtection 保护） | 同上 |
| `operations.json` | 操作条目 + 审计 | 同上 |
| `staging/` | 受限上传暂存（`{id}.archive`） | 流式写入 + 边写边校验上限 |
| `build/` | 每个输入指纹一份构建上下文，最多保留 20 份 | 目录 |
| `mounts/{appId}/{revisionId}/` | 该修订的机密物化文件（posix 0600） | 按修订隔离 |

三个账本全部**失败即关闭**（fail-closed）：文件不可读、JSON 非法或违反不变式时，构造期即置 `unavailable`，此后**所有变更**抛 `application-deployment.store_unavailable`（503），而不是用空账本静默覆盖操作者状态。只读查询同样被拒绝，避免在损坏状态上做决策。

账本不变式（加载时逐条校验）：

- 应用 `Id`/`Name` 唯一；名称符合容器资源命名规则。
- 修订属于已存在的应用，`Id` 唯一，同一应用内 `Number` 唯一。
- 操作 `Id` 唯一；审计条目必须引用存在的操作。
- 一切请求指纹、幂等引用、资源锁、操作者引用均为 64 位大写十六进制 SHA-256。

### 2.3 有界化

不做有界化，账本会随操作数无界增长。策略：

- **修订**：每应用保留 `MaximumRevisionsPerApplication`（默认 20）个最新修订，但**当前版本永不淘汰**，保证回滚目标始终存在。
- **操作**：保留最多 500 条**终态**操作，活动操作永不淘汰——因此裁剪不会丢掉启动核对仍需推理的工作。
- **审计**：最多 2000 条，先按「操作是否仍存在」过滤再按时间裁剪，维持「审计必属于某操作」的不变式。
- **构建上下文**：保留最近 20 份。
- **暂存**：`StagingLifetimeMinutes`（默认 20）过期即清理，且只清理本存储自己的暂存文件。

## 3. 协议冻结

### 3.1 路由

基址 `ApplicationDeploymentApiRoutes.Root = /api/v1.0/application-deployments`。

| 方法 | 路由 | 语义 | 策略 |
| --- | --- | --- | --- |
| GET | `/templates` | 四个模板的能力描述 | Read |
| GET | `/applications` | 应用列表（含观测状态） | Read |
| POST | `/applications` | 创建应用定义 | Manage |
| GET | `/applications/{id}` | 应用快照（应用 + 修订 + 近期操作 + 活动操作） | Read |
| PUT | `/applications/{id}` | 替换应用定义 | Manage |
| DELETE | `/applications/{id}` | 删除应用 → `202` | Manage |
| GET | `/applications/{id}/revisions` | 修订列表 | Read |
| GET | `/applications/{id}/operations` | 该应用的近期操作 | Read |
| GET | `/applications/{id}/logs` | 有界脱敏日志 | Read |
| POST | `/applications/{id}/deploy` | 发布新修订 → `202` | Manage |
| POST | `/applications/{id}/rollback` | 回滚到既有修订 → `202` | Manage |
| POST | `/applications/{id}/start` \| `/stop` \| `/restart` | 生命周期 → `202` | Manage |
| GET | `/operations/{operationId}` | 操作状态 | Read |
| GET | `/operations/active?applicationId=` | 该应用的活动操作 | Read |
| POST | `/operations/{operationId}/cancel` | 请求取消 | Manage |
| POST | `/uploads` | 受限归档上传（multipart） | Manage |
| POST | `/file-references` | 登记服务器上已有文件 → 引用 ID | Manage |

删除用 `ApplicationPattern`：`DELETE` 请求不会把复杂参数推断为请求体，故显式标注 `[FromBody]`。它与其他破坏性动作一样是**长操作**，返回 `202` 与 operationId。

### 3.2 权限

| 权限 ID | 含义 |
| --- | --- |
| `server.application-deployments.read` | 查看应用、修订、操作历史、有界日志 |
| `server.application-deployments.manage` | 创建、部署、启停、回滚、删除 |

服务端授权策略沿用现有角色边界（与 FileServices/Tunnels/Proxy 一致）：

- `ApplicationDeploymentsRead`：已认证 + `controller` 或 `observer`。
- `ApplicationDeploymentsManage`：已认证 + `controller`。

宿主能力：`ServerHostCapabilitiesDto.ApplicationDeployments`；描述符常量 `server.application-deployments`；宿主特性 `ServerHostFeature.ApplicationDeployments`。

**User Mode 不支持本能力。** 本领域组合 Docker Engine，因而继承 Docker 边界；无 sudo 的 User Mode 宿主不可达 Engine，不得被提供该功能。`Supports(ApplicationDeployments) == false` 与 `Describe()` 的 `ApplicationDeployments: !user` 必须一致，两者都已按此实现。

### 3.3 问题码

`ApplicationDeploymentProblemCodes` 全部以 `application-deployment.` 为前缀，客户端本地化，**不把 Docker 原始命令文本作为产品提示**：

- 账本与输入：`store_unavailable`、`invalid_request`、`application_not_found`、`operation_not_found`、`revision_not_found`、`permission_denied`、`idempotency_required`、`idempotency_conflict`、`resource_conflict`、`name_conflict`、`confirmation_required`、`not_cancellable`、`already_active`。
- 预检：`engine_unavailable`、`engine_not_installed`、`platform_unsupported`、`port_unavailable`、`port_conflict`、`disk_full`、`elevation_required`。
- 输入处理：`file_reference_unavailable`、`archive_unavailable`、`archive_too_large`、`archive_unsafe_entry`、`archive_too_many_entries`、`archive_expanded_too_large`、`archive_content_invalid`、`image_reference_invalid`、`image_not_found`、`image_platform_mismatch`、`registry_authentication_failed`、`registry_unreachable`、`entry_point_invalid`、`runtime_mismatch`、`dependency_install_failed`、`build_failed`、`secret_version_missing`。
- 运行时：`container_create_failed`、`container_start_failed`、`container_stop_failed`、`container_remove_failed`、`health_check_failed`、`health_check_timeout`、`activation_failed`、`proxy_validation_failed`、`proxy_unavailable`、`volume_create_failed`、`volume_remove_failed`、`staging_cleanup_failed`。
- 结果：`failed`、`cancelled`、`interrupted`、`not_supported`。
- 恢复（与原始错误分开上报）：`recovery_failed`、`recovery_unknown`、`previous_instance_restored`、`orphaned_resources`。
- 漂移：`drift_container_missing`、`drift_container_externally_modified`、`drift_image_missing`、`drift_volume_missing`、`drift_unowned_resource`。

### 3.4 严格请求契约

四个输入 DTO（`DeploymentSourceInputDto`）、六个请求（`CreateApplicationRequest`、`UpdateApplicationRequest`、`DeployApplicationRequest`、`RollbackApplicationRequest`、`ApplicationLifecycleRequest`、`DeleteApplicationRequest`、`CreateDeploymentFileReferenceRequest`）全部标注 `[JsonUnmappedMemberHandling(Disallow)]`：未知字段一律**拒绝**而非忽略，因此契约不能被静默扩展。

`DeploymentSourceInputDto` 的成员集合本身即安全边界——它**无法表达** shell 片段、Dockerfile、宿主路径或任意构建参数：

```
imageReference, baseImage, archiveReferenceId, runtimeVersion,
programEntry, arguments[], selfContained
```

## 4. 状态机、阶段与进度

### 4.1 操作状态

`Queued → Running → { Succeeded | Failed | Cancelled | Interrupted }`

- `Succeeded`：激活完成，账本已切换到新修订。
- `Failed`：失败，且 `problemCode` 保留原始原因；若恢复也失败，另以 `recoveryProblemCode` 上报，**不覆盖**原始错误。
- `Cancelled`：客户端请求取消，且在安全边界内生效。
- `Interrupted`：进程停止或启动核对发现半途操作；**不重放**。

### 4.2 阶段

`Queued → Preflight → Preparing → (Pulling | Building) → Creating → HealthChecking → Activating → Completed`，另有 `Deactivating`、`CleaningUp`、`RollingBack`、`Failed`、`Cancelled`、`Interrupted`。

`Pulling` 与 `Building` 分离，因此进度不必猜测字节来自 registry 还是本地构建。

### 4.3 进度语义

`DeploymentOperationDto.Progress` 只报告**阶段内已验证的字节数或工作量**。**无可靠分母时为 `null`**，UI 必须呈现「未知进度」而非伪造的累计百分比。当前实现的所有阶段上报均为 `null`；账本加载时校验 `Progress` 与阶段的配对（仅 `Pulling`/`Building`/`Preparing`/`HealthChecking` 允许非空）。

### 4.4 确认语义

`confirmed` 只保留在会**中断或删除正在运行的东西**的请求上：

| 请求 | 条件 | 未满足时 |
| --- | --- | --- |
| `DeployApplicationRequest` | `confirmed = true` | `confirmation_required` 400 |
| `RollbackApplicationRequest` | `confirmed = true` | `confirmation_required` 400 |
| `ApplicationLifecycleRequest` | `force` 为真时必须 `confirmed` | `confirmation_required` 400 |
| `DeleteApplicationRequest` | `deleteVolumes` 为真时必须 `confirmed` | `confirmation_required` 400 |

创建与更新定义不携带 `confirmed`：它们不触达运行实例。

## 5. 模板契约

模板只生成**构建定义**与**启动定义**，绝不各自复制容器管理逻辑。

```csharp
interface IApplicationTemplate
{
    ApplicationSourceKind Kind { get; }
    string TemplateVersion { get; }
    ApplicationDeploymentTemplateDto Describe(ApplicationDeploymentOptions options);
    DeploymentPlan Validate(DeploymentSourceInputDto source, ApplicationRecord definition,
                            ApplicationDeploymentOptions options, string inputReference);
    Task<(string EntryPoint, string[] Arguments)> PrepareBuildContextAsync(
        DeploymentPlan plan, string contextDirectory, ApplicationRecord definition,
        ApplicationDeploymentOptions options, CancellationToken cancellationToken);
}
```

| 模板 | 构建 | 运行入口 |
| --- | --- | --- |
| `Image` | 拉取并解析实际镜像身份 | 镜像默认入口 |
| `JavaJar` | JRE 基础镜像 + 校验过 `Main-Class` 的可执行 JAR，非 root 用户 | `java -jar` + 受控参数 |
| `DotNetPublish` | 读 `*.runtimeconfig.json`（tfm / 自包含 / 框架依赖 / ASP.NET 角色）与 `*.deps.json`（RID）选基础镜像，写 Dockerfile | `dotnet App.dll` 或自包含入口 |
| `PythonProject` | 固定 Python 镜像，构建期安装 `requirements.txt` | 用户指定模块/入口，不假定 Web 框架 |

硬约束：

- 基础镜像必须是**固定版本行标签**；`:latest` 一律 `image_reference_invalid`（`IsPinnedImageReference`）。
- **不调用宿主 Java / Maven / Gradle / dotnet SDK / Python**；一切工具来自镜像。RelaxKonOS Server 自身的运行依赖不属于该限制。
- 本机构建镜像标签由输入指纹派生：`relaxkonos-ad/{appName}:b{inputReference[..12].ToLowerInvariant()}`，同一输入必得同一标签，不同输入不可能静默复用上次构建。
- 模板返回的 `DeploymentPlan` 已是**校验过的类型化输入**，不是原始客户端负载。

模板版本记录在 `RevisionRecord.TemplateVersion`，因此后续模板演进可被回滚目标识别。

## 6. 输入处理

### 6.1 上传与文件引用

- `POST /uploads`：multipart 流式写入暂存区，**边写边校验** `MaximumArchiveBytes`（默认 512 MiB），超限即 `archive_too_large`，并删除半成品。
- `POST /file-references`：登记服务器上已存在的文件（例如远程资源管理器中选中的），要求绝对路径、存在、常规文件、非重解析点、大小在限内。
- 暂存条目绑定**操作者引用**（`Reference(actor)`）：只有登记它的操作者能打开；过期或被替换即 `file_reference_unavailable`，同时清理文件。
- **宿主路径永不进入部署请求或操作记录**：请求只携带引用 ID。

### 6.2 解压安全（`ApplicationArchiveSafety`）

拒绝：外部条目、链接、路径穿越（含 `.`/`..` 段与绝对路径）、符号链接模式、条目数超 `MaximumArchiveEntries`（默认 20000）、路径深度超 `MaximumPathDepth`（默认 32）、**声明大小**与**观测展开总量**超 `MaximumExpandedBytes`（默认 2 GiB）。

观测总量按实际写出字节累计，不信任 ZIP 中央目录的声明，因此压缩炸弹在展开过程中即被截断。

### 6.3 构建上下文

只有声明的文件进入构建上下文；构建上下文与宿主其他目录物理隔离在 `build/{inputReference[..16]}` 下，`PublishRoot` 只识别「单一顶层目录」的常见包装形式。凭据与无关宿主文件不进入镜像层。

## 7. 发布事务

### 7.1 停机替换序列（`ActivateRevisionAsync`）

```
确保数据卷存在
  → 物化该修订的机密文件（secrets.Materialize）
  → 若上一实例在运行：优雅停止
  → 以「候选」角色与名字创建候选容器（stopped，不自动启动）
  → 记录运行时绑定（候选）
  → 启动候选
  → 有界就绪检查（HTTP 或进程级）
  ── 临界区开始（此后不再提供取消）──
  → 可选：应用反向代理路由（先验证配置，失败则回滚站点）
  → 上一容器改名为恢复保留名
  → 候选容器改名为规范名称
  → 删除已保留的上一容器
  → 账本切换当前修订 + 记录运行时绑定（running）
  → 释放退役修订的机密物化文件
```

容器名与卷名：

- 规范容器名 `relaxkonos-ad-{appId[..12]}`，候选用同名 `-cand` 后缀，数据卷 `{容器名}-{卷名}`。
- 名字由应用 ID 派生且**跨修订稳定**，因此「替换」表现为改名而非命名漂移。

### 7.2 失败与恢复

`ActivateRevisionAsync` 的整个副作用区间被一个恢复处理器包围：失败时 `RestorePreviousAsync` 删除候选容器、释放候选修订的机密物化、并在上一实例原本运行时重新启动它。

恢复结果**单独上报**：

- 恢复成功 → `recoveryProblemCode = previous_instance_restored`。
- 恢复失败 → `recoveryProblemCode = recovery_failed`，且原始 `problemCode` 保持不变。
- 从未有过上一实例 → 账本绑定清空并记为已恢复。

因此「部分成功」永远不会被呈现为成功，原始错误也永远不会被恢复结果顶替。

### 7.3 回滚

回滚用旧镜像与旧配置引用**重新部署**（走同一条 `ActivateRevisionAsync`），并在触碰任何资源之前验证：

1. 目标修订存在且不是当前版本（否则 `already_active`）。
2. 旧镜像仍存在（`image_not_found`）。
3. 目标修订引用的每个机密版本仍可解封（`secret_version_missing`）。
4. 宿主端口可用（`port_unavailable`）。

数据卷**默认不随应用或版本删除**；显式删除必须 `deleteVolumes = true` 且 `confirmed = true`。数据库迁移不因回滚而撤销。

### 7.4 取消

- 取消只在安全边界提供（`Cancellable`）。进入激活/恢复临界区后 `Cancellable = false`，请求取消返回 `not_cancellable`。
- 取消请求只做三件事：记录 `cancel-requested`、把 `Cancellable` 置假、取消工作者的 `CancellationTokenSource`。
- **只有工作者**确认取消并释放资源。已请求取消后 `Reporter` 不再写入任何阶段，因此被取消的操作不会在取消记录之后再获得阶段。
- 取消后清理候选资源，**保留旧版本与数据**。

### 7.5 启动核对与恢复

`ApplicationDeploymentCoordinator` 是 `IHostedService`。启动时扫描活动操作：

| 记录的持久状态 | 结论 |
| --- | --- |
| `Queued` | 进程在工作者启动前即终止 → **无副作用**，记为 `Interrupted`，无恢复码。 |
| `Running` | 交给 `RecoverAsync`：删除候选容器、释放中断修订的机密物化文件；规范容器仍在则按期望状态重启并重新绑定，否则清空绑定并报 `orphaned_resources`。 |

**半途的部署永不重放。** 结论不确定即标为 `Interrupted`，禁止盲目重复发布。

确定性拒绝（域/策略不允许）映射为 `Failed` 而非 `Unknown`；只有**结果丢失**才是 `Unknown` 且禁止重放。

## 8. 机密与配置

### 8.1 存储

`ApplicationDeploymentSecretStore` 用 ASP.NET Core DataProtection（purpose `RelaxKonOS.ApplicationDeployments.Secrets.v1`）加密；持久文件中只有密文。每个 `(应用, 名称)` 最多保留 **3 个最新版本**，因此失败的轮换仍可被诊断，且运行中的修订在显式替换前仍可解封。

`ApplicationConfigRecord` 只在账本里保存 `SecretVersion`，**绝不保存值**。

### 8.2 投递（本设计的关键决定）

机密以**服务端物化的只读文件**投递，而不是环境变量值，也不是「每修订一个机密卷」：

- 激活前 `Materialize(appId, revisionId, configuration)` 只把**声明的**机密条目写成文件，目录为 `mounts/{appId}/{revisionId}/`，在类 Unix 上以 owner-only（目录 0700、文件 0600）创建。
- 容器创建时以 `{目录}:/run/relaxkonos/secrets:ro` 只读挂载，且**仅当该修订确实声明了机密条目时才添加挂载**。
- 环境注入 `{NAME}_FILE=/run/relaxkonos/secrets/{NAME}` 指针，而非值。

收益：机密正文不进入容器创建请求、不进入进程列表、不进入 `docker inspect` 结果。每修订一个目录使回滚目标在被淘汰前始终可读；激活成功后 `ReleaseMaterialized(退役修订)` 只删除那一个目录，绝不触碰其他修订。

### 8.3 配置校验

- 名称须匹配环境变量命名规则。
- 非机密值 ≤ 4096 且**不含控制字符**（控制字符可让值突破环境变量列表边界）。
- 更新时允许只提交既有 `secretVersion`（表单不重复回显机密），也允许提交新值以轮换；创建时**必须**提供值。

## 9. 受管资源、所有权与漂移

### 9.1 标签

每个受管容器与受管命名卷都带：

```
relaxkonos.managed=true
relaxkonos.owner=application-deployment
relaxkonos.application-id={appId}
relaxkonos.application={name}
relaxkonos.revision-id={revisionId}
relaxkonos.revision={number}
relaxkonos.operation-id={operationId}
relaxkonos.role={workload|candidate}
```

`IsManaged(labels)` 是所有权判定的唯一依据：`managed == true` **且** `owner == application-deployment`。
在执行删除、启停或卷清理前，还必须验证 `relaxkonos.application-id` 等于目标应用 ID；确定性资源名仅用于发现，不能作为归属证明。

### 9.2 漂移

`ApplicationDto.actualState` 由**真实容器**推导，而不是账本：

- 引擎不可达 → `Unknown`（不可达不是「已停止」，不得伪装）。
- 容器 `running` → `Running`；`restarting` → `Starting`；`created`/`paused` → `Stopped`；`exited` 且期望运行 → `Failed`；`dead`/`removing` → `Failed`。
- 容器不存在且从未部署 → `Unknown`；期望运行却不存在 → `Missing`；否则 `Stopped`。

`driftProblemCode`：

- 引擎不可达 → `null`（观测未知不等于漂移）。
- 期望运行但容器缺失 → `drift_container_missing`。
- 名称匹配但标签不属于本应用 → `drift_unowned_resource`（**不自动接管无标签资源**）。
- 容器状态与记录意图不一致 → `drift_container_externally_modified`。

**因操作者要求而停止的容器不是漂移**，因此期望状态参与比较。

列表页只做**一次**容器列举（避免 N 次调用），按确定性名称匹配，`ownedByUs` 取真；详情页额外 inspect 校验所有权标签——名称冲突正是在那里暴露。该折衷在代码注释与本节中明确记录。

### 9.3 清理边界

清理只针对本应用受管资源：`DeleteAsync` 通过 `FindApplicationContainersAsync(applicationId)`（规范名或 `规范名-*` 前缀）定位容器，通过带前缀的卷名定位数据卷。**绝不清理其他应用或用户的资源。**

删除前必须 Engine 可达，否则记录会被删掉而容器继续以未跟踪的孤儿身份运行。

## 10. 可选反向代理集成

`IApplicationDeploymentProxyIntegration` 只做三件事，且只在**显式关联的站点**上：

- `ApplyAsync`：在关联站点定义里插入 `/` 路由指向 `http://127.0.0.1:{hostPort}`，**先** `TestConfigurationAsync` 验证，验证失败即回滚到变更前的站点定义。
- `RevertAsync`：重新应用变更前捕获的站点定义。
- `RemoveAsync`：应用删除时**只**移除上游恰好等于本应用 `http://127.0.0.1:{hostPort}` 的 `/` 路由；操作者自定义的同路径路由不受影响；站点已不存在不算错误。

站点通过既有 `IWebServerManager` 解析；本领域**从不自己写配置**，也从不接受 shell 文本。站点校验或重载失败时恢复旧配置，并把 `proxy_validation_failed` / `activation_failed` 作为激活失败处理（走 §7.2 的恢复路径）。

**无 Web Server 时应用仍可完整部署**：`SiteId` 为空时 `ApplyAsync` 直接返回成功且不做任何事。

宿主代理通过**回环地址映射端口**访问容器；容器内 localhost 不等于宿主 localhost，因此健康检查与代理上游都指向 `127.0.0.1:{hostPort}`。

## 11. 并发、幂等与取消

### 11.1 幂等

所有变更动作要求 `Idempotency-Key` 头：1–128 个可打印 ASCII 字符，否则 `idempotency_required`（400）。

- 键按 `Reference(actorReference + "\n" + key)` 归一到**操作者范围**，因此两个操作者用同一个字面键不会互相冲突。
- 请求指纹是对**类型化请求**做的 SHA-256 归一（`Fingerprint`），不是对原始客户端 JSON。
- 同键同请求 → 返回既有操作（`created = false`），不重复副作用。
- 同键**不同**请求 → `idempotency_conflict`。

### 11.2 资源锁

每次操作声明资源集 `application-deployment:{appId:D}`，存储侧取 SHA-256 后按序存放。

- 同一应用的活动操作互斥 → `resource_conflict`。
- **不同应用可安全并行**（锁集不相交）。

### 11.3 并发上限

`MaximumConcurrentOperations`（默认 4）在**写入操作记录之前**判定：超限即 `resource_conflict`，避免留下一条永远不会被执行的 `Queued` 记录。

### 11.4 客户端断开

操作由后台工作者执行，与 HTTP 请求生命周期无关：**客户端断开不终止任务**。重连后通过 `GET /operations/{id}` 或 `GET /operations/active` 恢复观察。实时事件（若引入）只作通知，持久记录才是权威来源。

## 12. 客户端形态与信息架构

### 12.1 形态决定

客户端形态为**独立内置应用**：`Client/RelaxKonOS.Client/Apps/ApplicationDeployments/`，与既有 Docker 管理器分离。

理由：Docker 管理器是**底层资源**视图（容器/镜像/网络/卷），应用部署是**意图**视图（定义/修订/操作/日志）。把两者塞进同一个内置应用会让底部导航同时承担两种心智模型，并让「应用」的授权范围与 Docker 的授权范围纠缠。分离后：

- 导航、授权（`server.application-deployments.read/manage`）、本地化命名空间各自独立。
- 两者共享 Server 的 Docker 边界，但不共享 UI 状态。

### 12.2 向导

```
部署来源 → 模板/版本 → 程序入口 → 端口/配置/数据卷/资源
        → 可选域名与证书 → 部署预览 → 操作进度
```

向导只提交**定义**（`POST /applications`），随后单独提交**部署**（`POST /applications/{id}/deploy`，带幂等键与 `confirmed`），两者职责分明，重试语义清晰。

### 12.3 详情页

展示当前版本、期望/实际状态、就绪级别、访问入口（容器端口 / 宿主端口 / 绑定地址 / 可选域名）、资源上限、数据卷、配置（机密只显示名称与版本）、日志、修订历史与操作历史；支持重连观察与明确的失败恢复入口（重试 / 回滚 / 查看恢复码）。

错误使用稳定问题码 + 三语本地化（en-US / zh-CN / ja-JP），本地化键空间 `application-deployment.*`，其中问题码键为 `application-deployment.problem.*`。客户端**不显示 Docker 原始命令文本**。

> 客户端实现状态见进度文档 I11；本地化键见 §12.4。

### 12.4 本地化键

| 文件 | 新增内容 |
| --- | --- |
| `Localization/{culture}/application_deployment.json` | 应用全部界面文本 + `application-deployment.problem.*` 问题码文案 |
| `Localization/{culture}/application.json` | `application.relaxkonos.application-deployments.display_name` / `.description` |
| `Localization/{culture}/permission.json` | `permission.server.application-deployments.read.*` / `.manage.*` |

`Tools/verify-localization.py` 要求三语言**键完全对齐**，因此新增键必须在三种语言同时补齐。

## 13. 安全、权限与审计

- **无特权容器**：不提供特权模式，不挂载 Docker socket。`DockerContainerCreateRequest` 的受管路径不接受这些选项。
- **非 root 模板默认**：生成的 Dockerfile 创建并切换到非 root 用户；数据目录权限在模板内验证。
- **资源上限**：`DockerContainerResourceOptions` 在创建时设置 CPU / 内存 / PID 上限，null 表示**模板默认**，绝不被解释为「无限制」。
- **日志轮转**：默认 `json-file`，`max-size=10m`、`max-file=3`；允许的日志驱动是闭集（`json-file`、`local`、`journald`、`syslog`、`none`）。
- **重启策略**：`unless-stopped`。
- **结构化参数**：一切 Docker 调用传结构化参数，**不拼接 shell 命令**；容器入口与参数用数组表达并按模板校验。
- **日志脱敏与长度限制**：应用日志行经 `ApplicationDeploymentLogSanitizer`（复用 Mihomo 域的 `ProxyLogSanitizer`，上限 512 字符）处理后返回；tail 被限在 1–1000。
- **审计**：记录操作者引用、目标应用、操作类型、结果、operation ID 与资源锁；**不保存凭据与原始构建输出**。操作者以 SHA-256 引用形式存储，不回传明文标识。
- **授权范围**：读日志、取消任务、访问上传引用都检查资源权限；应用管理**不能绕开 Docker 高权限边界**（这正是 User Mode 被排除的原因）。

## 14. 实施顺序与映射

| 阶段 | 交付内容 | 对应实施项 | 本次状态 |
| --- | --- | --- | --- |
| M1 | Protocol、权限、模型、持久化与操作状态机 | I01–I04 | 已实现待验证 |
| M2 | 镜像部署、资源归属与恢复 | I05、I06（Compose 部分未实现） | 已实现待验证 / 部分缺口 |
| M3 | 受限上传、Java/.NET/Python 模板 | I07–I09 | 已实现待验证 |
| M4 | 客户端向导、日志、版本操作、可选站点 | I10、I11 | I10 已实现待验证；I11 见进度文档 |
| M5 | 平台验收、帮助文档、Goal 收口 | I12 | 文档收口进行中；**平台验收未执行** |

### 14.1 服务端文件落点

| 文件 | 职责 |
| --- | --- |
| `ApplicationDeploymentEnums.cs` / `ApplicationDeploymentContracts.cs` / `ApplicationDeploymentOperationContracts.cs` / `ApplicationDeploymentApiRoutes.cs` / `ApplicationDeploymentProblemCodes.cs` / `ApplicationDeploymentRequests.cs` | 协议冻结 |
| `ApplicationDeploymentModels.cs` | 领域记录、账本模型、模板接口、选项 |
| `ApplicationDeploymentValidation.cs` | 命名、校验、标签、容器/卷/镜像名派生 |
| `ApplicationDeploymentCatalogStore.cs` | 应用与修订账本（原子提交、有界修订） |
| `ApplicationDeploymentSecretStore.cs` | 机密密文存储 + 物化/释放 |
| `ApplicationDeploymentOperationStore.cs` | 操作账本 + 审计（幂等、资源锁、有界） |
| `ApplicationArchiveSafety.cs` | 安全解压 |
| `ApplicationDeploymentStagingStore.cs` | 受限暂存与文件引用 |
| `ApplicationTemplates.cs` | 四个模板与模板目录 |
| `ApplicationDeploymentRuntime.cs` | 唯一把计划变成真实 Docker 资源的边界 |
| `ApplicationDeploymentProxyIntegration.cs` | 可选反向代理应用/回退/摘除 |
| `ApplicationDeploymentMapper.cs` | 记录 → DTO 投影（机密值的唯一出口把关） |
| `ApplicationDeploymentManager.cs` | 定义 CRUD、修订/操作/日志读取、观测视图 |
| `ApplicationDeploymentService.cs` | 发布/回滚/生命周期/删除编排 |
| `ApplicationDeploymentCoordinator.cs` | 操作生命周期、幂等、并发、取消、启动核对 |
| `Endpoints/ApplicationDeploymentEndpoints.cs` | HTTP 表面 |

分层方向是单向的：`Endpoints → Coordinator → Service → {Runtime, Stores, Templates}`；`Manager` 只读与定义写入，从不启动工作负载。

## 15. 测试策略（本轮全部跳过）

### 15.1 跳过决定与理由

**本轮 T01–T15 全部跳过，理由是当前工作区没有可用 Docker 环境**（Goal §9 要求的真实 Engine 验收宿主亦未准备）。按进度文档记录规则：跳过必须注明原因，**不计通过**，因此当前「已验收 0/12、执行通过 0/15」的结论不变。

不采用「模拟测试充当验收」的做法：Goal §9 明确「仅有接口、模拟测试或成功的 `compose up` 不能关闭 Goal」。因此本轮不编写以 Mock Engine 为主体的验收测试来制造通过率。

### 15.2 已有实现与测试编号的对应关系（未执行）

| 测试 ID | 场景 | 对应实现 | 状态 |
| --- | --- | --- | --- |
| T01 | DTO 往返、未知字段与非法值拒绝 | `[JsonUnmappedMemberHandling(Disallow)]`、`ApplicationDeploymentValidation` | 跳过 |
| T02 | 版本不可变、原子写入、损坏账本明确失败、机密不落账本 | `CatalogStore`/`OperationStore`/`SecretStore` 的 fail-closed 与不变式 | 跳过 |
| T03 | 幂等重试、同键异请求冲突、同应用互斥、跨应用并行 | `OperationStore.Create` + `Service.Resources` + `Fingerprint` | 跳过 |
| T04 | 镜像部署与失败矩阵、镜像身份固定 | `ApplicationDeploymentRuntime` + `ProduceImageAsync` | 跳过（**需 Docker**） |
| T05 | 越界路径/链接/解压炸弹/超限/过期或跨身份引用 | `ApplicationArchiveSafety`、`StagingStore` | 跳过 |
| T06 | Java/.NET Web/Worker 运行与诊断，不调用宿主工具链 | `ApplicationTemplates` | 跳过（**需 Docker**） |
| T07 | Python Web/Worker 构建运行、锁定依赖、重启不重装 | `PythonProjectTemplate` | 跳过（**需 Docker**） |
| T08 | 就绪成功/超时/崩溃、失败不激活、回滚、数据卷保留 | `ActivateRevisionAsync`、`WaitForReadyAsync`、`RollbackAsync` | 跳过（**需 Docker**） |
| T09 | 各阶段取消、断客户端、重启 Server | `Coordinator` + `RecoverAsync` | 跳过（**需 Docker**） |
| T10 | 越权查询/日志/取消拒绝、脱敏、无特权与 socket、上下文不泄露宿主 | 授权策略、`LogSanitizer`、模板与运行时边界 | 跳过 |
| T11 | 资源限制、日志轮转、宿主重启恢复、漂移识别、清理边界 | `DockerContainerResourceOptions`、`DescribeDrift`、`FindApplicationContainersAsync` | 跳过（**需 Docker**） |
| T12 | 无代理可部署、回环端口、证书绑定、代理失败恢复旧配置 | `ApplicationDeploymentProxyIntegration` | 跳过（**需 Docker + Nginx**） |
| T13 | 四来源向导、错误展示、任务重连、日志与回滚、三语完整 | 客户端内置应用 + 本地化 | 跳过 |
| T14 | 无应用工具链的 Linux 宿主全流程 | 整体 | 跳过（**需隔离 Linux 宿主 E02**） |
| T15 | 磁盘不足、端口冲突、Daemon 不可用、旧镜像/密钥缺失、卸载保留数据 | 预检与恢复路径 | 跳过（**需 Docker**） |

**本轮实际执行的验证**仅是编译级：`RelaxKonOS.Server` 与 `RelaxKonOS.Protocol` 构建通过（0 警告 0 错误）。编译通过**不构成**任何 T 项的通过。

### 15.3 未验证清单（不得当作已验证）

- 任何真实 Docker Engine 行为：拉取、构建、创建、启动、改名、健康检查、日志。
- 无宿主 Java/.NET/Python 工具链的宿主上的三语言模板（T14 的核心断言）。
- DataProtection 跨进程重启后的解封（密钥环持久化）。
- 反向代理站点写入与校验失败回退。
- 客户端向导与三语 UI 的真实呈现。
- 平台矩阵：仅声明支持 `linux/amd64`、`linux/arm64`、`linux/arm`；未验证平台保持未验证。

## 16. 后续项

- Compose 输入部署（§1.3 缺口）。
- 磁盘容量预检（`disk_full` 已定义但当前仅由 Engine 错误映射，未主动探测）。
- `drift_image_missing` / `drift_volume_missing` 的主动核对（问题码已定义，当前由列表/详情观测路径覆盖容器漂移）。
- M5 平台验收与帮助文档、用户文档入口。
