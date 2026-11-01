# 服务端模块提权 Broker 设计

> 状态：分阶段实施，2026-11-01。服务启动/停止/重启的持久计划、单次确认门、私有回调和 SDK/Core/桌面接线已实现；Linux/Windows HTTP/真实 Helper、批准审计、Core/Helper 执行中断及 System 整包安装/更新已有实测。固定服务动作的有期常设许可、后台 SDK 执行、撤销/到期及重启幂等也已两平台实测；DC-05 已把常设范围扩展到受控的 package install/update/repair、file write（绑定内容摘要）、data preserve 和 app query。apt 包动作、Linux 文件/数据动作与 MySQL 有凭据改密已实现；MySQL 配置发布/保留数据卸载闭环和 `app.manage` 改密已有 Linux 容器实测；Windows 包/文件/应用动作、`restore`/`set-mode`/`set-owner`、丢失凭据恢复和 Personal 整包未验收。DC-06 高信任扩展评审已完成，当前版本不实现执行并保持稳定明确拒绝，见[评审](RelaxKonOS.HighTrustModuleExtensions.Review.md)。本文定义 v2 ModuleHost 的第三方模块提权边界；v1 官方模块继续使用现有 `IHostElevationSessionStore` 和 `IPrivilegedOperationTransport`。
>
> 范围：第三方模块申请宿主特权时的计划、展示、确认、执行、审计和撤销。模块包格式与安装由[服务端独立模块分发设计](RelaxKonOS.ServerModuleDistribution.Design.md)拥有；商店来源由[包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)拥有；Helper 的底层封闭操作仍由[统一特权 Helper 架构](privileged-helper.md)拥有。通用能力类别、资源范围和软件安装/卸载的后续扩展方向见[第三方模块声明式特权能力](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md)。
>
> 下文仍包含目标能力。已验证范围及剩余门槛以 [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md) 为准，不能将计划存储单测视为真实提权验收。

## 1. 目标与边界

### 1.1 目标

第三方模块需要安装软件包、重启服务、写受保护配置或调整防火墙时，不能直接拿到 root/LocalSystem，也不能直接调用 Helper。它只能向 Broker 提交一个**声明式请求**；Broker 解析为精确计划，用户在 Core 拥有的界面中确认，Core 再调用 Helper 执行。

### 1.2 必须保持的边界

1. 没有任意命令、shell、可执行文件路径或参数列表接口。
2. 模块不能看到管理员密码、JWT、Helper 共享密钥或其他模块的秘密。
3. 模块不能直接连接 Helper 的 Unix socket / 命名管道；ModuleHost 账户不在 socket ACL/管道 ACL 中。
4. 计划一旦生成，执行时只接受同一个 `planHash`；目标、版本、摘要或参数变化必须重新生成计划并重新确认。
5. 确认由 Core 处理；模块只收到计划 ID 和最终结果。
6. 提权确认不是模块权限的全部：模块还必须已安装、已启用、被用户信任、声明了该提权项，且操作在 Host 策略允许范围内。
7. 失败关闭：读不到信任、版本、计划或管理员验证状态时，不执行。

### 1.3 v1/v2 适用性

| 版本 | 模块位置 | 提权路径 |
| --- | --- | --- |
| v1 官方模块 | Server 进程内 | 现有 `IHostElevationSessionStore`；官方模块可信，可按现有策略自动授权 |
| v2 第三方模块 | 独立 ModuleHost | 只能走本文 Broker；默认每次确认 |

v2 发布后官方模块仍按 v1 的进程内路径运行；上表只描述“官方模块 / 第三方模块”的归属，不表示 v2 要把官方模块迁入 ModuleHost。

v1 不交付进程内第三方模块，因此不存在“进程内第三方 + Broker 确认”的安全承诺。Broker 在 v1 只作为契约预研，不作为第三方安全边界。

## 2. 当前基线

- `IHostElevationSessionStore.IsGranted` 是现有能力/资源授权入口。它对当前主机管理员或 root 会自动返回 true；在 `SessionValid` 开启时，管理员验证会按 access token 记忆。
- `PrivilegedEndpoints` 处理管理员账密验证和短期授权；密码只在本次调用中使用，不落库。
- Helper 只接受封闭 `PrivilegedOperationKind`；没有 `ExecuteCommand`/shell/任意参数接口。
- 当前自动授权逻辑见 [`HostElevationSessionStore.cs`](../../RelaxKonOS.Server/Privileged/HostElevationSessionStore.cs) 和 [`PrivilegedEndpoints.cs`](../../RelaxKonOS.Server/Endpoints/PrivilegedEndpoints.cs)。

Broker 不能直接复用 `IsGranted` 作为第三方模块的准入判断，否则管理员身份会绕过“每次确认”。它需要单独的确认门，并只把管理员验证记忆用于“免密码”，不用于“免确认”。

## 3. 核心模型

### 3.1 模块侧接口

```csharp
public interface IModulePrivilegeBroker
{
    /// <summary>校验模块、声明、策略和用户权限，生成不可变计划。</summary>
    ValueTask<ModulePrivilegePlan> PrepareAsync(
        ModulePrivilegeRequest request,
        CancellationToken cancellationToken);

    /// <summary>读取原计划或原操作结果；不重新规划，不自动重放。</summary>
    ValueTask<ModulePrivilegeStatus> GetAsync(
        Guid planId,
        CancellationToken cancellationToken);
}
```

模块身份由 ModuleHost IPC 身份或 Broker 上下文确定，不从 `ModulePrivilegeRequest` 读取。模块不能声称自己是另一个模块。

### 3.2 请求

```csharp
public sealed record ModulePrivilegeRequest(
    string Kind,
    string Action,
    string Target,
    string Reason,
    string IdempotencyKey,
    ModuleSecretReference[]? Secrets = null);

public sealed record ModuleSecretReference(string Purpose, string Target, string Permission);
```

`Kind` 是冻结的能力类别，`Action` 是该类别允许的动作，`Target` 是声明的资源标识；三者都不是命令行字符串。未知类别、类别外动作、未声明目标或携带秘密值都会在规划前拒绝。`Secrets` 只承载目的限定的引用，不承载秘密值；在其交付适配器实现前，带引用的请求明确返回 unsupported。

### 3.3 操作类型

以下为目标能力类别，逐类实现和验收。首个固定服务动作切片不是永久业务白名单；下一阶段按[声明式能力方向](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md)把软件包安装/卸载等操作提升为通用类别，由模块声明资源范围，Host 解析精确计划，用户确认后交 Helper 复核执行。新增 MySQL 等应用通常不应要求新增应用专用 Core 操作。

| 类型 | 用途 | 关键约束 |
| --- | --- | --- |
| `PackageInstall` | 安装固定包 | 固定包名、版本/版本区间、受信任仓库、摘要；禁止任意 apt 参数 |
| `PackageRemove` | 移除固定包 | 只允许模块声明的包；默认保留配置/数据 |
| `PackageUpdate` | 更新固定包 | 冻结候选、仓库身份、摘要；执行前复查 |
| `ServiceStart/Stop/Restart/Reload` | 控制固定服务 | 固定 unit/service 白名单；显示依赖影响 |
| `FileWrite/Delete/SetMode/SetOwner` | 修改受保护文件 | 固定根、路径规范化、内容摘要/权限差分 |
| `FirewallRule` | 增删改结构化规则 | 结构化五元组，不接受原始命令 |
| `NetworkListener/NetworkTun` | 监听/TUN 配置 | 固定接口/端口范围；显示断网影响 |
| `HostSetting` | 主机设置 | 只允许 Host 定义的设置键 |
| `ProcessExecute` | 运行模块声明的可执行文件 | 最后开放；必须签名清单、可执行文件 hash、参数 schema、固定环境/工作目录/超时 |

现有 Helper 的包操作只覆盖 Nginx、Git、Docker、Samba、Squid、sing-box、Tailscale、Mihomo 等固定组件；这不是第三方通用安装能力已经交付的证据。下一阶段新增通用软件包能力及平台适配器，以 Host 批准来源、模块声明包集合、版本约束、依赖闭包和资源所有权为边界；安装与卸载均可声明。适配器交付前明确拒绝，不把现有 `ComponentPackageInstall` 当成任意 apt 接口，也不要求永久逐应用新增专用枚举。

`ProcessExecute` 不是通用命令接口：

- 不允许把 `sh`、`bash`、`powershell`、`python`、`node`、`apt`、`dpkg`、`systemctl` 等解释器/包管理器作为通用可执行文件。
- 可执行文件必须由模块包声明，带绝对路径、SHA-256、发布者签名；安装时展示给用户。
- 参数必须按声明 schema 校验；不接受自由文本拼接、环境变量注入、stdin 秘密或任意工作目录。
- Helper 侧再次校验可执行文件 hash、参数形状和固定路径；Server 侧校验不作为唯一防线。

### 3.4 计划

```csharp
public sealed record ModulePrivilegePlan(
    Guid PlanId,
    string ModuleId,
    string ModuleVersion,
    string PublisherId,
    string TrustState,
    ModulePrivilegeKind Kind,
    IReadOnlyList<ModulePrivilegeStep> Steps,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> Warnings,
    bool RequiresAdministrator,
    bool Reversible,
    string PlanHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed record ModulePrivilegeStep(
    string Executable,
    IReadOnlyList<string> Arguments,
    string Effect,
    bool Irreversible);
```

`Steps` 用于透明展示，不是模块可执行任意命令的入口。对于 apt，必须额外展示：

- 包名、版本、架构、仓库、冻结摘要；
- 依赖包；
- 将运行的 maintainer scripts 警告；
- 可能重启的服务；
- 是否保留配置/数据；
- 失败后是否可回滚。

对于服务操作，展示 unit、当前状态、依赖服务、预计中断时间。对于文件操作，展示路径、所有者/模式变化和内容 diff 摘要。对于 `ProcessExecute`，展示绝对路径、SHA-256、argv、工作目录、环境差异和超时。

### 3.5 批准与结果

批准由 Core 处理，模块不接触密码：

```csharp
public sealed record ModulePrivilegeApproval(
    Guid PlanId,
    string PlanHash,
    string? AdministratorUsername,
    string? Password,
    string? OwnerDeviceProof);
```

返回给客户端的计划详情包含 `confirmationToken`（一次性、短时、绑定 planHash/用户/会话/模块）。提交批准时：

- 校验 token 未使用、未过期；
- 校验 `PlanHash` 与计划一致；
- 校验模块版本、摘要、信任状态仍与计划生成时一致；
- 校验管理员验证或密码；
- 只执行计划中的步骤；不重新解析、不追加步骤。

结果：

```csharp
public sealed record ModulePrivilegeStatus(
    Guid OperationId,
    Guid PlanId,
    ModulePrivilegeState State,
    string? ProblemCode,
    string? Detail,
    DateTimeOffset UpdatedAt,
    ModulePrivilegeResult? Result);
```

状态至少包含 `AwaitingConfirmation`、`Executing`、`Succeeded`、`Failed`、`Cancelled`、`Expired`、`RecoveryRequired`。

## 4. 确认策略

### 4.1 四个维度

不要把“信任模块”做成一个布尔值。建议拆成：

| 维度 | 可选值 | 默认（第三方） |
| --- | --- | --- |
| 代码信任 | 不信任 / 用户信任 / 发布者信任 | 安装时用户确认 |
| 提权提示 | 每次确认 / 仅危险操作 / 按声明放行 / 拒绝 | 每次确认 |
| 授权范围 | 一次 / 本次会话 / 直到模块更新 / 直到日期 | 一次 |
| 执行位置 | ModuleHost / Server 进程内 | ModuleHost |

### 4.2 提示策略

| 策略 | 行为 |
| --- | --- |
| `EveryTime` | 每个计划都确认；管理员验证记忆只免密码 |
| `DangerousOnly` | 只读/低风险操作可放行；包、服务、防火墙、网络、`ProcessExecute` 仍确认 |
| `AllowDeclared` | 只对安装时已展示并批准的声明操作免确认；新增权限必须重新确认 |
| `Deny` | Broker 直接拒绝，模块不能提权 |

即使选择 `AllowDeclared`，以下仍然强制确认：

- 操作不在 manifest 的 `privileges` 中；
- 目标/版本/摘要超出声明约束；
- 模块更新后摘要变化；
- 提权项扩大；
- 管理员撤销了信任；
- 计划包含不可逆步骤而策略要求确认。

### 4.3 谁能修改

- 安装/卸载/信任模块：服务器管理员，走 `ServerModulesManage` 和主机管理员认证。
- 单次提权确认：任何能通过主机管理员认证的用户；普通用户可以输入管理员账密批准一次操作。
- 常设放行/关闭确认：只能由主机管理员设置。
- 部署托管模式：`ModuleManagement:Authority=deployment` 时，安装、信任和提权策略只读。
- 普通用户不能全局关闭确认，也不能修改其他用户的授权。

### 4.4 管理员验证复用

当前 `HostElevationSessionState` 在 `SessionValid` 开启时会按 access token 记忆成功的管理员验证。Broker 需要一个只读判断：

```csharp
bool IsAdministratorVerified(ClaimsPrincipal principal);
```

它只回答“当前 token 是否已完成管理员验证”，不授予任何能力。

流程：

```text
模块请求 -> 生成计划
  -> token 已验证？ 是：显示计划 + “确认”
                    否：显示计划 + 管理员账号/密码 + “确认”
  -> 确认通过 -> 一次性 approval token -> Core 调 Helper
```

如果 `SessionValid` 关闭，则没有记忆，每次都要密码。owner device key 可以作为管理员验证的替代，但仍需手动确认。

### 4.5 信任绑定与失效

`privilegeConsent` 至少绑定：

- `moduleId`
- `publisherId`
- `version`
- `artifactDigest`
- `allowedKinds`
- `grantedBy`
- `grantedAt`
- `expiresAt`
- `revision`

模块更新后默认回到 `EveryTime`。如果发布者 key 变化、摘要变化或提权项增加，必须重新确认；不能沿用旧授权。

撤销立即生效：Broker 每次请求读取当前 state，不使用进程内永久缓存。读不到 state 按拒绝处理。

## 5. 流程

### 5.1 用户发起的操作

```mermaid
sequenceDiagram
    participant C as 客户端
    participant M as ModuleHost/模块
    participant B as Core Privilege Broker
    participant U as 用户
    participant H as Privileged Helper

    C->>M: 业务请求
    M->>B: PrepareAsync(kind, params, reason)
    B->>B: 校验模块/信任/声明/策略
    B-->>M: planId + planHash + confirmationToken
    M-->>C: 409 module.privilege_confirmation_required + planId
    C->>B: GET plan
    B-->>C: 计划、步骤、影响、警告
    U->>C: 查看并确认（必要时输入管理员密码）
    C->>B: POST approve(planId, planHash, token, password?)
    B->>B: 校验 token/管理员/状态未漂移
    B->>H: 执行精确封闭操作
    H-->>B: 结果
    B-->>C: operationId / 状态
    M->>B: GetAsync(planId)
    B-->>M: 结果
```

### 5.2 后台任务

后台任务不能弹一个没人看的确认框。规则：

- 默认禁止后台提权；
- 管理员可以为模块授予**常设提权许可**，绑定操作类型、目标、范围和过期时间；
- 模块在许可范围内可以直接执行，但仍写审计；
- 超出许可范围返回 `module.privilege_confirmation_required`，不排队等待；
- 无人值守服务器不能靠“稍后有人会点确认”来假装任务仍在进行。

### 5.3 计划过期与重放

- 计划默认 5 分钟过期；过期后必须重新 `Prepare`。
- `confirmationToken` 一次性；重放返回冲突，不重复执行。
- 相同幂等键相同内容返回原操作；相同键不同内容冲突。
- 计划生成后模块版本、摘要、信任或策略变化时，执行失败并要求重新生成。

## 6. 与 Helper 的映射

Broker 不新增“任意命令”Helper 操作。封闭边界应位于软件包、服务、文件等通用能力类别，而不是写死每个第三方应用名称；模块通过声明限定可申请的资源。每个能力类别必须映射到现有或新增的类型化 Helper 操作，Helper 按受保护策略独立校验，不能仅信任模块清单或 Server 参数：

| Broker 操作 | Helper 侧 |
| --- | --- |
| `PackageInstall/Remove/Update` | 既有固定组件操作仅作现状；后续新增通用包事务适配器，冻结来源、包、版本、依赖和移除集合 |
| `ServiceStart/Stop/Restart/Reload` | 既有 `ManagedRuntime`/`NativeServiceAction` 等固定服务操作 |
| `FileWrite/Delete` | `ModuleFileQuery`/`ModuleFileApply`：root 策略限定根、路径规范形、前置摘要、原子发布与回执；`SetMode/SetOwner` 未交付 |
| `DataPreserve/Backup/Delete` | `ModuleDataQuery`/`ModuleDataApply`：精确数据根、结构摘要、受保护备份/保留回执；`Restore` 未交付 |
| `AppQuery/RotateCredential` | `ModuleApplicationQuery`/`ModuleApplicationApply`：root 应用策略、root-only 秘密文件、mysql-admin 固定协议；`Configure/RecoverCredential` 未交付 |
| `FirewallRule` | 既有 `FirewallUfw*`/`FirewallWindows*` |
| `NetworkListener/Tun` | 既有主机网络操作 |
| `HostSetting` | 既有 `HostTimeApply`/`HostIdentityApply`/`HostEnvironmentApply` 等 |
| `ProcessExecute` | 新增受限操作；首版可不实现 |

如果现有 Helper 没有合适的能力类别或平台适配器，先新增通用类型化操作和测试；已有类别覆盖的新模块通过声明资源接入。特殊操作可按独立的高信任扩展机制评审，不能隐式降级为通用命令代理。模块安装时的能力范围授权与执行时的具体计划确认分别校验；详见[两层授权与实施顺序](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md)。

## 7. 审计与可观察性

每次计划/批准/执行记录：

- operationId、planId、planHash；
- moduleId、version、publisher、artifactDigest；
- 操作类型、目标、解析后的步骤摘要；
- 发起用户、批准用户、管理员验证方式（不记录密码）；
- 计划生成、批准、开始、结束、失败阶段；
- 结果、问题码、耗时；
- 关联的 Server 请求 ID、ModuleHost 请求 ID、Helper 请求 ID。

日志和回执不得包含密码、JWT、Helper 共享密钥、私钥、模块秘密或完整的敏感文件内容。文件 diff 只记录摘要和受影响路径。

## 8. 错误码

至少定义：

| 错误码 | 含义 |
| --- | --- |
| `module.privilege_not_declared` | 模块未在 manifest 声明该操作 |
| `module.privilege_denied` | 管理员策略拒绝 |
| `module.privilege_confirmation_required` | 需要用户确认 |
| `module.privilege_plan_expired` | 计划过期 |
| `module.privilege_plan_changed` | 版本/摘要/目标变化 |
| `module.privilege_approval_invalid` | 一次性 token 无效/已用 |
| `module.privilege_admin_required` | 需要管理员验证 |
| `module.privilege_password_invalid` | 管理员密码错误 |
| `module.privilege_target_invalid` | 目标不在声明约束内 |
| `module.privilege_unsupported` | Host/平台不支持该操作 |
| `module.privilege_helper_unavailable` | Helper 不可用 |
| `module.privilege_recovery_required` | 上一次操作待恢复 |

错误必须使用稳定 problemCode 和结构化参数；客户端不解析标题。

## 9. 安全边界

- ModuleHost 不持有 Helper transport、Server 配置、数据库连接或 JWT 密钥。
- Broker 只在 Core 内运行；ModuleHost 通过 IPC 调用，不能直接构造 Core 服务。
- 密码只在 Core 的批准请求中短暂存在；ModuleHost 永远看不到。
- `planHash` 绑定精确目标；执行前重新读取模块版本/摘要/信任状态。
- Helper 再次校验参数和调用者身份；Broker 的校验不是唯一防线。
- 计划、批准、执行、结果都持久化；崩溃后只核对原操作，不自动重放。
- 读不到信任、策略、计划或管理员状态时 fail closed。

## 10. 实施跟踪

| 编号 | 交付 | 完成门槛 |
| --- | --- | --- |
| PB-01 | 冻结 `ModulePrivilegeKind`、请求/计划/批准/结果 DTO、错误码 | 共享 Protocol、Server、ModuleHost SDK、桌面、测试同步 |
| PB-02 | Core Broker、计划存储、一次性 token、管理员验证复用 | 计划过期/重放/漂移/密码错误全部拒绝 |
| PB-03 | 客户端确认 UI（三语言、计划步骤/影响/警告） | 管理员已验证只显示确认；未验证显示账密 |
| PB-04 | ModuleHost IPC 与 Broker 调用 | ModuleHost 无法直连 Helper/Server 数据 |
| PB-05 | 首批通用类别：包安装/移除、服务动作、文件写入/删除、数据保留/备份/删除、数据库查询/有凭据改密 | 模块声明资源；Host 冻结计划；Helper 独立复核和审计；DC-03 的 Linux 配置/数据闭环与 DC-04 的 MySQL 改密已验收；Windows、restore/set-mode/set-owner、configure/recover-credential 未交付 |
| PB-06 | 常设提权许可、撤销、后台任务 | 未授权后台提权 fail closed |
| PB-07 | `ProcessExecute` / 高信任扩展评估（可选，最后） | 签名清单、hash、参数 schema、真实隔离评估；DC-06 评审结论为当前不实现执行，仅保留稳定拒绝与重新评审门槛 |

## 11. 验收

必须覆盖：

- 模块未声明、目标越界、策略拒绝、用户拒绝；
- 管理员已验证（免密码但仍确认）、未验证（密码）、密码错误、owner device key；
- `SessionValid` 开启/关闭；撤销后立即失效；
- 计划过期、重复批准、planHash 漂移、模块更新后旧计划失效；
- Helper 不可用、Helper 重启、执行中断、待恢复；
- ModuleHost 崩溃、重启、IPC 身份伪造、越权读取 Server 数据/Helper；
- 后台任务无许可、常设许可过期/撤销；
- 并发批准、幂等键冲突、旧修订。

每个操作分别记录 Windows/Linux、System/User Mode 的实际结果；不能用单平台或 mock 代替另一平台验收。

## 12. 关联文档

- [第三方模块声明式特权能力](RelaxKonOS.ModuleDeclarativePrivileges.Direction.md)
- [服务端独立模块分发与 v1/v2 宿主设计](RelaxKonOS.ServerModuleDistribution.Design.md)
- [服务端模块管理设计](RelaxKonOS.ServerModuleManagement.Design.md)
- [包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)
- [统一特权 Helper 架构](privileged-helper.md)
- [权限模型与项目重构规范](../platform/RelaxKonOS.PermissionModel.Refactor.md)


## 13. 当前实现契约（2026-11-01）

首个 Broker 操作集合为 `service.manage` 的 `start`/`stop`/`restart`：请求携带类别、动作和目标，且必须落在模块声明的服务与动作范围内，再映射现有 Helper NativeServiceAction。DC-02 起 `package.manage` 的 `install`/`update`/`repair`/`remove` 也进入矩阵：prepare 先向 Helper 发出只读 `ModulePackageQuery(sourceRef, package)`，把返回的 `ModulePackageResolution` 冻结进计划；Helper 不可用、来源未批准、包不在来源策略内、解析失败或版本不在声明区间时，计划创建直接失败（`module.privilege_unsupported` / `module.privilege_not_declared`），不会只凭声明生成计划。执行时 Broker 只发送来源引用与冻结身份；Helper 先按 operationId 查持久回执，重放或中断不会被重新求解，再独立重解：主制品必须完全一致，apt 仍要下载的依赖必须属于冻结闭包，已安装而不再下载的冻结依赖不算漂移。不一致返回 `PlanChanged`，Broker 映射为 `module.privilege_plan_changed` 并把计划记为 `Failed`（已知未执行），响应丢失仍进入 `RecoveryRequired`。能力类别目录已冻结为 `service.manage`、`package.manage`、`file.manage`、`data.manage`、`account.manage`、`network.manage`、`secret.reference`、`setting.manage`、`app.manage`；DC-03 起 `file.manage` 的 `write`/`delete` 与 `data.manage` 的 `preserve`/`backup`/`delete` 进入执行矩阵，prepare 先调用 Helper `ModuleFileQuery`/`ModuleDataQuery` 冻结规范路径、前置状态、摘要与模式；`set-mode`/`set-owner`/`restore` 及未交付类别明确返回 unsupported，绝不转为命令行或逐应用专用操作。`file.manage`/`data.manage` 仅 Linux，Helper 只承认 root 拥有、非链接、group/other 不可写的 `/etc/relaxkonos/module-resources.json`；文件写入使用暂存加原子 rename，数据备份/保留写入 Helper 受保护状态根，删除只清空精确声明根。文件内容只以摘要和长度进入计划，不进入 `privilege-plans.json`、日志或确认页；Core 重启后待确认 write 计划变为 `Expired`。DC-04 起 `app.manage` 的 `query`/`rotate-credential` 进入矩阵：请求必须携带一个匹配清单 `secret.reference` 的 purpose/target/permission 引用，prepare 调用 `ModuleApplicationQuery` 冻结实例、账户、服务器版本和秘密 revision；Helper 使用 root-only 应用策略与两个 0600 凭据文件，通过固定的 mysql 客户端执行 `ALTER USER`，验证新凭据后原子替换账户秘密文件；重放返回待恢复，revision/策略漂移返回 `PlanChanged`。`configure`/`recover-credential`、模块侧秘密值读取与 Windows 应用适配器仍只可声明、不可执行；丢失凭据恢复不宣称支持。

ModuleHost SDK `context.Privileges` 通过 Core 拥有的临时 loopback listener 调用；每进程随机服务令牌确定模块身份，Server 代理为当前已认证业务请求生成短时不透明 context，并在代理请求结束时撤销。模块不能传 userId/JWT/密码来声明身份；没有活动 context 的后台 prepare 被拒绝。模块侧仅返回 planId/planHash/状态。

Core 用户 API：GET `/api/v1.0/server/module-privileges/plans/{id}`、POST 同路径 `/approve` 或 `/cancel`。GET 的确认令牌只交给发起用户的同一会话；approve 提交 planHash、确认令牌及可选管理员账密。桌面组件中心可输入业务返回的 planId 查看并确认/拒绝。计划五分钟过期；服务授权不得跳过确认门。HostElevationSessionState 提供只读 IsAdministratorVerified，既有 sessionValid 控制免密码记忆。owner device 仍通过 Core OwnerDeviceKeyService 校验。

privilege-plans.json 内部存储使用 schemaVersion=7，原子耐久保存计划、执行与终态，确认令牌仅保存哈希；计划绑定类别、动作、目标和模块声明的资源范围，`package.manage` 计划还持久化 Host 已批准来源引用与 Helper 解析出的完整 `ModulePackageResolution`，`file.manage` 持久化根/路径/前置状态/内容摘要/模式，`data.manage` 持久化精确根/结构摘要/备份引用，`app.manage` 持久化实例/账户/服务器版本/秘密 revision，各自带独立摘要并计入 `planHash`；来源、仓库、版本、架构、制品摘要、大小或依赖闭包、文件路径/摘要、数据摘要或应用秘密 revision 被改写即 `module.privilege_recovery_required`。计划同时记录发起用户、同会话批准用户、实际验证管理员身份、验证方式、确认时间与执行尝试结束时间，初始发起者绑定进计划摘要。拒绝旧的审计不完整存储格式，不进行自动迁移；升级前必须显式处理旧状态及其证据。模块安装清单使用 schemaVersion=3，package-plans.json 使用 schemaVersion=3，installed.json 仍使用 schemaVersion=2；它们是不同契约。Executing 在重启后变为 RecoveryRequired，响应丢失也进入 RecoveryRequired，不重放动作。管理员验证记忆保留原始规范身份；owner-device 记录实际设备 ID，不记录密码、私钥或令牌。module-trust.json 绑定精确版本/发布者/归档与清单摘要；Broker 执行前两次复查当前安装、启用、信任、来源目录/撤销、策略与 Host 服务白名单。Helper 再检查自己的 root 管理服务白名单与来源策略。

PB-01/02：核心契约及确认门的定向测试已通过；PB-03：桌面入口已编译；PB-04：Linux/Windows 专用身份与权限隔离已有实机验收；PB-05 服务动作、审计及两平台真实 Helper 中断恢复已实测，DC-02 的 apt 包计划冻结/派发/漂移拒绝已有确定性测试，并在 Ubuntu 26.04 实机验收 mysql-server 与 redis-server 的安装、重放拒绝、摘要漂移拒绝、来源撤销拒绝与移除。PB-06 已实现固定服务动作的有期常设许可与后台 SDK 执行，两平台真实撤销/到期和重启幂等已实测；DC-05 进一步开放受控的 package install/update/repair、file write（内容摘要绑定）、data preserve 与 app query，数据删除等危险动作仍拒绝常设授权；无显式许可仍拒绝后台派发。DC-03 的 Linux 文件 write/delete 与数据 preserve/backup/delete 已通过 44 项确定性检查、Ubuntu 24.04 容器 13 项 Helper 检查和 14 项真实 MySQL 配置发布/保留数据卸载检查。DC-04 的 MySQL `app.manage` query/rotate-credential 已通过 29 项确定性检查与 Ubuntu 24.04 容器 13 项真实改密/旧密码拒绝/秘密 revision 检查；`configure`/`recover-credential`、模块侧秘密值交付、`restore`、Windows 文件/数据/应用动作与 Personal 整包仍未实现或验收。DC-06 未新增任何高信任执行操作；`process.execute`/`high-trust.extension` 等请求返回 `module.privilege_high_trust_extension_unsupported`，`ModuleHighTrustExtensions:Enabled=true` 使启动失败。

### PB-06 当前后台许可契约（2026-11-01）

DC-05 起常设范围扩展为：`service.manage` start/stop/restart、`package.manage` install/update/repair、`file.manage` write（必须绑定精确内容摘要）、`data.manage` preserve、`app.manage` query。`package.manage remove`、`file.manage delete`、`data.manage backup/delete`、`app.manage rotate-credential` 及 `configure`/`recover-credential` 始终要求新的具体确认；数据删除永远不能由常设许可授权。Core 管理 API 为 `/api/v1.0/server/module-privileges/consents` 的创建/列表、`/{id}` 原会话审查及 `/{id}/approve`、`/{id}/revoke`；均要求 `ServerModulesManage`，部署托管只读模式拒绝写入。创建不授予权力、不执行动作；审查令牌仅交原会话，五分钟内经管理员显式确认后生效。最长三十天，到期、撤销和模块/信任/策略漂移拒绝新的派发。

SDK `ExecuteBackgroundAsync` 通过进程令牌认证模块身份，不复用业务请求 context，不接受管理员凭据。许可 ID 不是认证令牌；它不能让其他模块调用。文件写入许可携带 `contentSha256` 而不携带内容；后台请求内容缺失、非法或摘要不符返回 `module.privilege_confirmation_required`，其他类别不得携带该字段。每个操作仍落原耐久计划与 Helper 审计：package 重新解析冻结制品，file/data/app 重新调用 Helper 查询冻结当前状态，`consent:<ID>` 关联授予记录；响应丢失或 Core 重启不会借常设许可自动重放。撤销与派发线性化，已经派发的动作不冒充被取消，撤销完成后拒绝新动作。普通前台 Prepare 仍要求当前业务上下文及单次确认。

`privilege-consents.json` 独立使用 schemaVersion=2，严格读取当前结构，检查冻结身份/请求/组合摘要与审批/撤销证据；DC-05 新增可选 `contentSha256`（仅 file write），旧的服务许可记录保持可读；活进程发现文件丢失或被外部改写时拒绝使用，不用内存缓存掩盖磁盘未知。模块安装清单为 schemaVersion=3，原单次操作审计为内部 schemaVersion=7。

两平台真实 PAM/LogonUser、服务后台 Helper 动作、撤销/实际到期、重启幂等与操作者审计已验证；DC-05 新增的 package/file/data/app 常设范围与内容摘要绑定通过 106 项定向检查，完整 ServerModules 回归 769 项通过，证据统一链接到 [v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md)。后台计划由模块自己调度；本次没有新增主机任务调度器，也没有开放包移除、文件删除、数据备份/删除、凭据轮换或任意进程动作的常设许可。
