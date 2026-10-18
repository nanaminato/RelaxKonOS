# 受管归档运行时的 Helper 事务组件

> 2026-10-18，MR-01/MR-02 实现中。公共事务、固定平台宿主、Helper 生命周期组合和 Server 新鲜授权服务已有代码；安装操作台账/API 已接通；受管安装的已发布配置发布、启停、开机启动与配置回滚已接通；卸载/数据保留与客户端版本计划页已接通。公共目录仍不允许安装。本文不表示 sing-box 或其他应用已经完成跨版本更新。

## 与安装操作台账的关系

`RuntimeArchiveSwitch` 接收既有 Server 安装操作的 operationId，复用 `InstallationStage` 阶段，不新增一套用户可见操作记录或安装端点。Server 的 `InstallationCoordinator` 继续负责后台任务、用户权限、操作查询和取消；Helper 事务状态保存特权侧必须独立持有的切换检查点、活动指针和去重回执。

当前组件支持归档运行时的 Install / Update / Repair / Rollback 事务框架、受管安装的配置发布与服务控制，以及只针对自有受管安装的 Uninstall（保留或删除数据）；不支持包管理器、业务数据以外的删除或第一方宿主组件更新。这些操作仍需按各自生命周期实现，不能将归档切换包装成通用卸载/软件包事务。sing-box 已通过既有安装台账调用该框架并可在受管活动修订上发布配置，公共目录仍将新来源版本标为配置不支持。

## 受保护的实例状态

宿主为每个受管实例提供固定的保护目录和权限核对函数。`RuntimeArchiveStateStore` 在其中原子写入 `archive-state.json`，以 `instance.lock` 串行化全部变更。当前固定版配置发布、启停与归档生命周期已共用 `state/instance.lock`；归档安装后的配置发布/启停仍待适配，存在活动归档修订时拒绝进入原平铺程序路径。读取快照允许与工作进程并行，读取者看到完整旧状态或新状态。Linux 状态锁和来源账本锁创建时显式使用 0600，不依赖调用者 umask，也不把已有可写保护文件静默修正为可信。

文件保存：安装/宿主/运行时身份和状态修订、活动程序修订、当前配置身份、保留版本、一个未结束事务、以及既有 operationId 对应的 Helper 回执。程序修订引用不可变制品、实测版本及安装时配置证据；当前发布配置身份单独维护，配置发布不必更改旧程序版本的证据。配置内容、凭据、证书私钥不进入此元数据文件；备份使用适配器保护的文件和不透明 GUID 引用。

首次初始化必须提供实测所有权观察。未知/外部实例不能安装覆盖，初始化也不能把未登记的已有受管程序自动接管。状态损坏会拒绝读取和新操作，不创建空状态替代；状态修订每次持久化变更递增。状态上限 32 MiB、256 个程序修订、4096 个回执，达到限额时拒绝新事务，不静默丢弃历史。

## 切换检查点

| 检查点 | 特权侧持久证据与行为 |
| --- | --- |
| Preflight | 绑定安装/宿主、明确 releaseId、状态/目录/信任修订和请求指纹；记录未结束 operationId 后才调用适配器 |
| Preparing | 保存原配置/资源备份引用、原运行与开机启动状态；准备目录必须可由 operationId 找回，捕获/准备中断也可清理 |
| Verifying | 保存已准备的新不可变程序修订；适配器完成目标二进制版本/特性、现行发布配置及当前授权检查 |
| Activating | 同一原子写入更新活动程序/配置身份并记录 SwitchStarted；这是普通取消的截止点 |
| HealthChecking | 服务切换后验证实测版本、配置身份、原生配置有效性、健康、运行状态及开机启动状态 |
| Completed | 验证成功才清除 pending 并保存成功回执；备份引用保留在回执中，后续只能按所有权与保留策略清理 |
| RollingBack / Interrupted | 停止失败候选，重新验证旧状态当前仍允许恢复，再恢复活动指针、配置、服务、启动状态和自有资源；不完整则保留待恢复状态 |

调用适配器前记录 pending，可在准备方法尚未返回时重启恢复。新程序修订 ID 必须等于 operationId，避免恢复时依赖随机临时路径或重新生成秘密。准备阶段可取消；Activating 检查点持久化后，激活、健康检查和补偿使用不被客户端/普通取消令牌中断的事务边界。进程被终止仍可能中断，因此不能用取消处理替代持久恢复。

安装默认不启动或启用开机启动；其他切换保持原运行/启动状态。Repair 只能选择当前 releaseId，并建立新的程序修订，不报告成跨版本升级。Update / Rollback 必须是不同 releaseId；Rollback 还必须具有保留的相同制品摘要，并由运行时适配器确认数据格式、凭据和实际文件仍可恢复。目录和版本名称不能单独授予这些许可。

同一幂等键和完整冻结请求重试返回原 operationId 的持久回执，不再次执行；改变目标、修订或其他字段会冲突。存在 pending 时拒绝新变更，重复请求也不能绕过恢复重新准备。回执记录实际活动程序修订，后续升级不会改写历史结果。

## 补偿及启动恢复

适配器失败后，框架记录 RollingBack。已经进入切换时先停止候选，然后要求 `ValidateRestoreAsync` 核对当前授权、证书、凭据和数据格式。恢复成功必须返回与旧实测版本、捕获的配置身份、原运行/启动状态匹配的证据，以及自有资源已恢复和健康结论。仅停止/启动成功或进程存活不能通过这些检查。

不能恢复时设置 RecoveryRequired，保留原失败码和独立 RecoveryProblemCode；例如旧凭据已撤销时记录 `runtime.credentials_revoked`，不重启旧服务。此时后续生命周期变更仍被阻止，只能走[人工恢复入口](#人工恢复入口)：重试补偿、紧急停止，或由操作员确认后关账。

`RecoverAsync` 从磁盘 pending 核对和补偿，不重新运行捕获/准备，不重新生成凭据，不因进程活着就提交新版本。切换前中断只清理本次准备；切换后中断重新验证旧状态后恢复，补偿失败继续待恢复。当前框架没有自动推断不可用的授权；运行时适配器无法取得恢复所需当前证据时必须拒绝恢复并保持停止。已拒绝候选从可回退修订记录中移除，旧程序及其历史证据保留。

`IRuntimeArchiveSwitchAdapter` 是宿主内的固定运行时实现，不是客户端可提交的插件。适配器负责受保护备份、原生生成/校验、服务和资源所有权、版本/特性检查、当前凭据授权、真实健康探测、清理和保留策略。公共组件没有任意 shell、可执行路径或原生参数的远端入口，也不能独立证明平台服务已经符合规范。

## 受管安装的配置发布与服务控制

`SingBoxManagedRuntime` 在同一个实例锁内处理受管活动修订上的 `ProxyServerStatus/Validate/Apply/GenerateKeys/Action`。程序只能来自 `versions/{revisionId}/` 中已记录且逐文件摘要仍匹配的不可变制品；本类不下载程序、不改活动修订、不建立信任。

| 操作 | 规则 |
| --- | --- |
| 状态 | 版本取实测记录，`ConfigurationHash` 取持久化的已发布配置身份；配置缺失/内容不符或运行进程的应用身份不符即为 `proxy_server.configuration_drift`；存在未结束事务或待恢复状态时为 `proxy_server.recovery_required` |
| 校验（Validate） | 用已记录程序执行原生 version/check 和构建特性核对，不触碰服务与状态；失败不留下候选配置 |
| 发布（Apply） | 拒绝在漂移或未结束事务上覆盖；先把被替换配置保存为 `config.json.previous`，仅在原服务运行且登记一致时停止，原子替换配置后用同一身份重新登记并恢复原运行/开机启动状态；核对进程应用身份与监听后才提交新身份并递增状态修订 |
| 失败补偿 | 重新核对 `.previous` 与已记录身份一致，恢复配置、登记和原运行状态；核对不通过或恢复失败进入待恢复，不谎报成功 |
| 启动/重启 | 只启动已发布的身份；未登记但配置存在时按该身份登记后再启动，运行后必须读到相同应用身份 |
| 开机启动 | 仅改登记的开机启动状态，不启停服务，事后核对实际状态 |
| 配置回滚 | 只恢复 `.previous` 并重新执行原生检查，不涉及程序版本回滚 |
| 程序版本 | Install/Upgrade 在已有受管修订上是空操作；真正的版本切换只走生命周期事务，Uninstall 明确返回 `proxy_server.managed_runtime_required` |

未取得新鲜授权的配置发布不会进行；配置发布的身份与运行时事务使用同一 `ConfigurationIdentity`，因此事务进行中不能发布配置，发布配置后旧冻结计划的状态修订即冲突。原生检查通过 `ISingBoxNativeChecks` 固定宿主实现执行，测试可替换为记录型实现而不执行第三方程序。

## 卸载与数据保留

`RuntimeArchiveRemoval` 是独立的归档移除事务，复用同一 operationId、阶段词汇和幂等回执，不改写 `RuntimeArchiveSwitch` 的切换语义。它只接受自有受管安装：`Ownership` 必须是 `Managed` 且存在活动修订，`ReleaseId` 必须为空，`PreviousReleaseId` 必须等于当前活动发布，请求不得携带预发布或保留数据以外的安装选项。

| 检查点 | 特权侧持久证据与行为 |
| --- | --- |
| Preflight | 记录 `Removal`（操作身份、请求与幂等摘要、冻结计划）后才调用适配器；适配器核对修订逐文件摘要、服务登记归属与配置身份 |
| Activating | 保存捕获的服务/配置快照；`RemovalStarted` 写入后普通取消不再生效，只能成功或补偿 |
| Verifying | 适配器确认登记与活动修订已移除；未移除报 `runtime.removal_incomplete` 并走补偿 |
| CleanupStarted | 不可逆边界：之后删除 `versions/`、`staging/`、`operations/`（`versions` 空时删除目录），`KeepData=false` 时再删 `config.json`、`config.json.previous`、`candidate.json`；此阶段失败只上报 `runtime.removal_cleanup_failed`，不回滚也不假装原状返回 |
| Completed | `Ownership=Absent`、`Active=null`、`Revisions=[]`；`KeepData=true` 保留 `ConfigurationIdentity`，删除数据时清空 |

`RuntimeArchiveStateStore` 校验 `Pending` 与 `Removal` 互斥，并核对检查点一致性：未记录 `RemovalStarted` 不得进入 Verifying，`ExpectedTrust/CatalogRevision` 只拒绝负值。启动恢复按 `CleanupStarted` 分流——之前重新核对当前程序与配置后恢复原登记，之后只关账并返回 `runtime.removal_cleanup_interrupted`，因为程序或数据可能已部分删除。

`SingBoxRemovalAdapter` 是卸载的固定宿主实现：只删除受保护实例根内由本安装记录的文件，拒绝外部安装、配置漂移和程序文件被改写；不调用包管理器、不删除证书私钥、接口与防火墙规则（本运行时没有自有规则），也不移除代理服务端应用入口。

Server 侧 `SingBoxRuntimeService.PlanAsync` 允许卸载不带来源（内部用 `"removal"` 占位），`RuntimeCatalogEndpoints` 只在卸载时可省略 `SourceId`，`SingBoxInstallationService` 以 `InstallationOperationKind.Uninstall` 受理并校验“无发布、无 releaseId”；其余变更仍必须有 release 且 runtimeId 与 releaseId 一致。

客户端版本计划页在这条链路上只做三件事：显示四维状态与阻止原因、先取计划再入台账、卸载必须经二次确认并在确认宿主缺失时什么都不做。它不提交 URL、可执行路径或原生参数；刷新失败保留上次目录并明确显示失败，待恢复状态禁用安装/更新/卸载。

## 人工恢复入口

补偿失败（`RecoveryRequired=true`）后，`RuntimeArchiveRecovery` 提供三条互不替代的路径，全部复用既有安装 operationId、阶段词汇和幂等回执，不新增用户可见台账。

| 入口 | 路由 | 规则 |
| --- | --- | --- |
| 查询 | `GET …/managed-runtimes/sing-box/recovery` | 只读快照：是否待恢复、事务与变更、阶段、切换是否已开始、原始失败、补偿失败、目标/原发布与实测服务状态；观测失败返回 `ServiceProblem`，不伪造"已停止" |
| 重试 | `POST …/sing-box/recovery/retry` | 复用 `RecoverAsync`：重新核对授权、凭据、配置与数据格式后补偿；不重新捕获/准备，不重新生成凭据；仍失败则保持待恢复并返回新的补偿失败码 |
| 紧急停止 | `POST …/sing-box/recovery/stop` | 只停服务并关闭开机启动，不读发布配置与凭据（凭据已撤销时仍可用）；只对本次观测存在的服务生效，复观测仍在运行报 `runtime.recovery_stop_failed`；不改台账，不宣称恢复 |
| 关账 | `POST …/sing-box/recovery/close` | 只承认"已停止且不再自动恢复"，不假装恢复 |

关账要求三个条件同时成立，缺一即拒：`RecoveryRequired=true`（补偿确实已失败）、请求体 `Confirmed=true`（操作员明确确认）、当前实测服务未运行（`Service.Running == false`）。对应问题码 `runtime.recovery_not_required` / `runtime.recovery_confirmation_required` / `runtime.recovery_stop_required`；操作 ID 为空、不存在、不匹配分别报 `runtime.operation_id_invalid` / `runtime.recovery_absent` / `runtime.recovery_target_conflict`。关账按不可逆边界分流，且只清理失败事务自身留下的候选，已提交指针不动：

| 关账检查点 | 结果 |
| --- | --- |
| 切换已开始 | 保留 `Active` 与 `ConfigurationIdentity`，剔除未提交的候选修订，回执 `RuntimeSwitchOutcome.Failed` |
| 切换尚未开始 | 保留旧修订与活动发布，候选不入回退记录 |
| 卸载未开始 | 只清 `Removal`，保持受管与活动修订 |
| 卸载已开始 | 转移为 `Ownership=Absent`、`Active=null`、`Revisions=[]`；`KeepData=false` 时连配置身份一并清空 |

关账回执的 `ClosedProblemCode` 固定为 `runtime.recovery_closed_by_operator`，因此后续任何读取都能区分"恢复成功"与"由人放弃恢复"。三条写路由都要求 `CanInstall` 与该操作员的新鲜提权（缺失返回 `elevation-required`），查询只要读权限；Helper 侧对应 `SingBoxRuntimeRecoveryQuery/Stop/Close` 三种特权操作，Stop 要求 `Confirmed=false`、Close 要求 `Confirmed=true`，形状不符在下载与执行前即被拒。

客户端 `ProxyServerRuntimeViewModel` 把这三条路径做成显式决策而不是静默失败：待恢复面板列出事务、变更、阶段、原始失败、补偿失败与实测服务；运行中只提供"重试"与"紧急停止"，只有实测停止后才出现"关账"并在关账前要求确认；`elevation-required` 弹出密码框并重试一次，操作员取消即失败而不重试；缺少确认宿主时关账什么都不做。

## 下载取消

取消是**请求**，不是中断连接。Server 从不因为客户端断开或请求取消就杀掉 Helper：一次安装是独立的一次特权进程，`Execute` 调用固定用 `CancellationToken.None`，所以连接断了后台事务照常走完。要真正中止准备阶段，请求必须落到那个已经在跑的事务上。

| 环节 | 机制与规则 |
| --- | --- |
| 请求通道 | 事务全程持有 `state/instance.lock`，因此请求不能走同一把锁。它是 `state/cancel.request` 单文件：内容为**该事务自己的 operationId**，先写临时文件再原子覆盖 |
| 送达 | 特权操作 `SingBoxRuntimeCancel` 只写这个标记，不读台账、不取实例锁，所以在事务运行中也能立即返回；它回答的是“请求已记录”，不是“已取消” |
| 生效 | 运行中的事务启动一个 200 ms 轮询监视器，标记内容与自己匹配才取消内部令牌；该令牌贯穿 Preflight、捕获、准备与 Verifying，`PrepareAsync` 原样把它传进下载与解包 |
| 边界 | 与切换检查点同一条线：写 `SwitchStarted` 之后激活与健康检查用不可取消令牌，取消不再生效。到达太晚的请求照样被记录，但结果由事务回执决定，界面按“已完成”呈现 |
| 收尾 | 事务结束（成功、失败、补偿、取消）时删除标记；标记只匹配自己的 operationId，因此残留文件永远不会误伤下一条事务，内容不合法时按“未请求”处理 |

台账一侧用同一套边界说话：`RuntimeArchiveSwitch` 与 `RuntimeArchiveRemoval` 每个阶段都向上报“此刻还能不能取消”（切换：Preflight/Preparing/Verifying 可以，Activating/HealthChecking/RollingBack 不可以；卸载：Preflight/Activating 可以，Verifying 之后不可以），特权帧携带该标志，`POST /api/v1.0/installations/{id}/cancel` 记进 `Cancellable`。所以界面不会出现“点了却没反应”的取消按钮。

`InstallationCoordinator` 因此区分两种取消语义：`CooperativeCancel = false` 的安装器（apt、git、文件引用等）沿用原有做法，取消本地工作令牌即可；sing-box 是 `true`，协调器只记录请求并把它转给主机，**不**取消自己的等待——主事务还在跑的时候就把台账写成 `Cancelled` 是谎报。主事务真正取消后回执是 `RuntimeSwitchOutcome.Cancelled`（`runtime.operation_cancelled`），服务端据此把台账落成 `Cancelled` 而不是 `Failed`。请求送不到主机时，协调器把 `Cancellable` 恢复成可用并上报错误，不留下“看似取消过”的状态。

客户端 `ProxyServerRuntimeViewModel` 在轮询操作时读同一个 `Cancellable` 决定是否显示「取消下载」，点击只发请求并立刻撤下按钮；循环继续等待回执，收到 `Cancelled` 就按“已取消、未改变任何内容”收尾，而不是报成失败。

## 验证证据与剩余工作

`Tests/ManagedRuntimes/SwitchChecks.cs` 有 96 项检查，覆盖真实磁盘状态、共享互斥锁、幂等回执、跨版本目标、同版本修复、保留版本回退、配置漂移、当前发布配置恢复、切换取消边界、独立版本/配置/运行/启动/原生健康失败、撤销恢复及待恢复阻断。

其中四组测试启动独立子进程，分别在捕获、准备、激活、健康检查中 `Environment.Exit`，由父进程重新读取磁盘并恢复。服务和二进制使用文件适配器模拟，没有执行第三方运行时、操作真实服务或更改系统启动设置；不能据此宣称 Windows/Linux 生命周期验收通过。

仍需将组件接入各声明平台的原生配置与监听/互通验收、依赖预检，并完成真实服务的故障与重启验收。总体进度继续以 [任务表](RelaxKonOS.ManagedRuntimes.Design.md) 为准。

`ManagedServiceChecks` 另有 13 项检查，覆盖受管安装的状态上报、只校验不发布、首次发布与身份递增、运行中再发布保留运行状态、仅改开机启动不重启、配置回滚、磁盘漂移拒绝覆盖、登记失败后的补偿与运行状态恢复、程序文件漂移拒绝执行、以及 Install/Uninstall 的边界；其原生检查使用记录型实现，不执行第三方程序。

`RemovalChecks` 有 15 项检查，覆盖保留数据的卸载、程序/暂存/备份目录清理、实例根外文件不受影响、单回执与幂等重放、重复卸载被拒、删除发布配置与保留副本、移除登记失败后的补偿、清理开始前恢复、清理开始后中断关账、外部安装拒绝与程序漂移拒绝。测试使用文件适配器模拟服务，不操作真实 SCM/systemd。

`CancellationChecks` 有 16 项检查，覆盖请求只被自己的事务认领、残留与畸形标记不会中止无关事务、单标记下新请求覆盖旧请求、Helper 在运行中登记请求而完全不动台账、取消字段不被其他生命周期动词接受、空操作 ID 被拒、准备中登记的请求确实取消运行令牌、结束即清标记、以及调用方令牌仍能结束监视。事务侧的取消边界由事务夹具体另行证明：切换点之前取消得到 `Cancelled` 且不激活，切换点之后取消不打断已提交的切换。

`RecoveryChecks` 有 16 项检查，覆盖无待恢复时快照为空、事实描述与双问题码、目标事务不符被拒、未确认/未停/目标错三种拒绝对状态无影响、未进入待恢复不可关账、跨过切换点关账保留活动修订、切换前关账保留旧修订且候选不入回退、重复关账被拒、卸载未开始关账保持受管、卸载已开始关账转移所有权、删除数据关账不留配置身份，以及卸载词汇与发布字段。关账一律返回 `runtime.recovery_closed_by_operator`，检查不执行第三方服务。

## sing-box 真实文件与原生命令适配器

`RelaxKonOS.PrivilegedHelper/SingBoxArchiveAdapter.cs` 已实现 `IRuntimeArchiveSwitchAdapter` 的归档/原生部分：

- 只接受当前已审查的 1.14.3 配置能力，核对运行时、宿主 OS/架构、服务宿主和固定入口文件名；保留目录发现与安装许可的区分。
- 从 Helper 保护的来源账本解析明确目标，通过制品准备组件验证整个归档及所有声明文件，保留依赖 DLL/库和许可证，移动到 `versions/{operationId}/`；暂存根也按 operationId 分隔，中断后可定位清理。
- 捕获当前配置与服务注册快照，配置及含秘密的备份只写私有操作目录。准备和切换前读取当前已发布授权并生成配置，用实际目标二进制执行 version / check，比较配置身份；不使用业务草稿。
- 激活时先停止服务、原子替换配置，再通过固定宿主配置已验证程序和启动状态；成功证据包含原生检查、程序/配置身份和宿主监听结论。未知开机启动状态不能作为验证成功。
- 补偿时重新验证当前授权与旧制品许可，逐文件核对保留程序，再验证并恢复备份配置、程序和注册状态。缺少新鲜授权时保持停止和待恢复，不恢复旧凭据。

`ISingBoxArchiveHost` 必须由 Helper 内置的 Windows/systemd 实现提供，不能从客户端注入；它负责固定服务所有权、实际进程路径、实际加载配置身份和监听检查。`ISingBoxArchiveAuthorization` 必须由 Server 当前已发布状态及凭据/证书授权提供，不能将输入草稿或长时间缓存当作授权；无法取得新鲜证据时拒绝。授权租期最多五分钟，切换/恢复多次重新读取，不在元数据回执中保存秘密。

当前已实现 Windows 与 Linux 固定服务宿主代码、Helper 本机生命周期组合、Server 当前授权提供者和既有配置路径共锁；Server 安装操作台账/API 及受管修订上的已发布配置发布与服务控制已接通；卸载与数据保留走独立归档移除事务。公共目录仍全部标为配置不支持，不能当作已开放的安装功能。保留的操作备份也仍需接入最终卸载和保留策略。

可选真实二进制检查：

```text
dotnet run --project Tests/ManagedRuntimes/RelaxKonOS.ManagedRuntimes.Tests.csproj -- native-sing-box-archive <sing-box-1.14.3-windows-amd64.zip的绝对路径>
```

2026-10-17 在 Windows x64 通过 12 项，使用工作树既有缓存，开始执行前先核对归档 SHA-256 为 `d8b86ef58936dba9230ec6a57ffa4243933f3c14ef4809828b6c553440ed1518`（与当前固定清单一致）。测试在临时目录创建隔离信任账本和签名清单，实际解包并执行官方程序的 version、REALITY 密钥生成和 check；覆盖空配置及带真实生成凭据的 REALITY 已发布配置、同版本修复、模拟服务配置失败后的原生补偿、授权中断保持待恢复和授权恢复后的再次检查。

这些测试没有运行 sing-box 入站服务、修改 SCM/systemd 或系统信任目录，服务仍为测试宿主，不证明真实监听/互通或平台服务验收。第二版本在线获取的 TLS 认证失败，未取得可验证的跨版本证据；不能因此扩大支持范围、跳过证书验证或把同版本修复当作升级。385 项常规检查与这 12 项可选检查分别记录，普通测试不依赖忽略目录中的缓存归档。

## 新鲜授权与固定生命周期组合

`ProxyServerRuntimeAuthorizationProvider` 每次读取受保护的已发布业务状态，不夹带草稿配置、新凭据或未发布证书。存在未确认发布时拒绝授权；逐个检查已发布凭据是否仍存在、启用、未轮换/过期且仍授权原入站，并核对当前证书来源与已发布证书的版本、内容、私钥和有效期。授权最长 30 秒，且不超过最早凭据/证书期限。

`RuntimeAuthorizationServer/Client` 为单个 operationId 建立随机本机管道和 256 位临时密钥。请求与响应分别使用带角色、通道、操作和随机 nonce 的 HMAC；认证前不读取业务机密，同一 nonce 不重复接受。长度、连接时限和租期有界，失败只返回固定问题码；管道和密钥不进入冻结计划、状态、回执或普通 DTO。操作结束关闭通道，Helper 每次切换/补偿检查都重新挑战，而不是复用首次下载时的授权。注册可能耗尽前一次租期，因此候选和旧版本均在服务注册后、实际启动前再次核对新鲜授权。

`SingBoxRuntimeService` 持有与业务配置/凭据变更相同的 Server gate，并在持有期间提供回调；读取者不反向取得该 gate 或调用 Helper，避免锁递归。缺少 Server 回调、错误操作身份、已结束通道或撤销授权都不能恢复旧凭据。

Helper 的 `SingBoxRuntimeQuery/Plan/Execute/Recover` 是唯一当前本机操作形状，所有混用字段被拒绝。`SingBoxRuntimeLifecycle.Create` 仅组合固定根、保护状态/信任账本、固定 OS 宿主和内置下载器；不接受客户端选择路径、服务或程序。首次状态初始化观察外部/未知所有权，宿主身份来自本机身份的摘要。计划在实例锁内从保护目录生成，执行使用冻结计划以保留幂等请求身份；适配器将冻结的完整版本记录与保护账本返回的记录逐字节比较，包含制品布局和主机赋予的配置/验证元数据。回执重放先返回原结果，不能因后来的状态或信任修订变化再次执行。查询可按既有 operationId 返回特权侧准确回执，不能用当前进程存活推断某次操作成功。

本机新操作已接入 Server 安装操作台账，客户端版本计划页已实现：只显示目录与阻止原因，先取冻结计划再写入台账，卸载需二次确认且不带发布。全部公共目录仍不允许安装，不能通过提交自行构造的冻结计划开放版本。受管修订上的配置发布与服务控制、卸载/数据保留已接通，后续仍须实现下载取消和人工恢复入口。

## Linux 固定宿主与进程证明

2026-10-18 新增 `LinuxSingBoxArchiveHost`，仅管理 `/var/lib/relaxkonos/proxy-server` 与 `relaxkonos-sing-box.service`。程序仅能来自 `versions/{非空GUID}/.../sing-box`；配置固定为根目录 `config.json`。拒绝链接、单位语法注入、外部服务、额外 drop-in、未重载单元和无法核实的状态；服务、登记文件、程序和配置及其祖先必须属于 root 且不可被组或其他用户写入。已有固定平铺版本不自动转为受管归档安装。

配置登记保存程序和配置摘要。systemd 使用 DynamicUser、只读系统与私有临时目录，通过 LoadCredential 为每次启动取得私有配置副本；只授予绑定低端口的能力。启动前核对当前程序与配置摘要；运行观察核对 MainPID、进程启动时间、`/proc/{pid}/exe`、实际参数，以及该进程命名空间中本次 systemd credential 的摘要。替换磁盘配置不会给旧进程附上新配置身份。

`LinuxOwnedListeners` 有界解析 IPv4/IPv6 TCP/UDP 表，显式处理原生 32 位字序，只接受 TCP LISTEN 和未连接 UDP 绑定。网络命名空间的表本身不代表进程所有权；读取前后将 inode 与目标进程描述符关联，并核对启动时间。健康检查还在监听读取后重新核对完整进程与配置身份。

Debian 13 x64 测试机上通过 533 项常规检查：33 项准入、68 项签名目录、19 项集成、68 项归档、24 项制品准备、96 项事务、68 项版本/原生输出、4 项子进程、18 项 Linux 监听、31 项新鲜授权、30 项生命周期边界、46 项安装台账、13 项受管服务及 15 项归档卸载检查。包含独立模拟 Helper 进程的 NDJSON 进度及宿主停止后结果保留；模拟 Helper 与服务不构成真实 sing-box 验收。Android 当前契约的 23 项 JVM 检查也在该测试机通过，含安装枚举及发布请求调用验证；没有执行 Android 设备界面验收。

Linux 监听检查包括双字节序与双地址族解析、畸形表拒绝、真实 TCP/UDP inode 归属及错误进程启动时间拒绝。显式命令 `RelaxKonOS.ManagedRuntimes.Tests linux-systemd-host` 另通过 12 项真实 systemd 检查：安装时不启动、不启用开机启动、开机启动变更只改登记而启停不变、启停、实际配置身份、真实监听健康与错误端口拒绝、磁盘配置变化不改写运行身份、启动前配置漂移拒绝、可写程序拒绝、自有注册移除及固定配置路径使用同一实例锁。测试机 root 的 umask 为 0002，锁文件仍明确创建为 0600；没有通过放宽保护检查取得通过。此专项要求固定服务及根目录均不存在，仅在授权测试机执行；退出后清理本次服务及测试文件。

systemd 专项运行的是重命名测试 apphost，并非 sing-box，未验证协议、跨版本、重启恢复或生产生命周期接线。不得将其与既有 Windows 真实 sing-box 归档检查混称为完整 Linux 生命周期验收。

2026-10-18 授权测试机以保留业务数据的卸载/重装方式部署当前工作树 `0.1.0-dev20261018-02`（先在原版本上执行保留数据卸载，再以新包安装；安装标识保持不变），Server 与 Guardian 在 Linux 系统模式运行并启用开机启动。三类文件访问策略设为 full，未修改宿主文件的 Unix 权限。自签名 HTTPS 证书包含测试机 IP 与 loopback 身份，使用该证书作为明确 CA 验证 loopback 健康返回 200；开发机以同一证书验证 LAN TLS 成功、受保护 API 返回 401。测试机未安装 ufw/iptables，nftables 规则集为空，没有主机防火墙端口限制。此宿主部署不等于第三方运行时已完成生产多版本安装；其代理运行时根目录仍不存在，因此受管配置发布尚未在该机执行端到端验收。

## Windows 固定宿主与进程证明

`WindowsSingBoxArchiveHost` 只管理固定根目录对应的 SingBox 组件服务。配置只允许根目录 `config.json`，程序必须位于 `versions/{非空GUID}/.../sing-box.exe`；拒绝邻接目录、旧平铺程序路径和任意程序名。复用独立组件服务的 ImagePath/所有权检查、受保护宿主包和 Job Object，不创建通用命令接口。开机启动参数在 SCM 注册时明确写为 auto 或 demand，不先自动启用再补一次修改。

状态读取核对 SCM、真实 PID、启动时间、程序映像，并使用进程启动时捕获的 AppliedIdentity。组件工作进程在实际启动前核对程序摘要；sing-box 还核对实际配置摘要。程序、sing-box 配置和目录需要受保护所有权/写权限。工作进程及 sc.exe 使用固定可信环境，不继承下载代理或其他环境注入。

监听核对使用 [Windows IP Helper 的 TCP PID 表](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable) 及 UDP PID 表，分别读取 IPv4/IPv6、本地地址/范围、网络序端口和归属 PID；TCP 只接受 LISTEN。绑定必须属于已核实的组件 PID，之后再次核对启动时间，避免仅凭全局端口存在或复用 PID 判定健康。

`WindowsHostChecks` 在 Windows 上通过 27 项：四类原生表解析及错误边界、真实临时回环 TCP/UDP 的 PID 归属、固定路径拒绝、当前状态契约及启动前配置/程序摘要漂移。没有创建、启动、停止或删除真实 SCM 服务，因此平台服务端到端仍待验收。既有 `--independent-component-services-only` 专项因当前账户不能创建临时符号链接而中断；本轮只通过该工程编译，不能复用历史隔离主机结论声称当前格式已复验。

独立组件服务状态是唯一当前格式，AppliedIdentity 必须存在（其他无应用证明组件可以显式 null），不解析缺字段旧格式。第一方更新需要重新发布并验证当前独立服务宿主及状态，不能继续运行旧宿主却为其补造新证明；此更新编排和隔离服务复验仍由 MR-09 追踪。相关共享服务改动见 [独立组件进度](../services/RelaxKonOS.IndependentComponentServices.Progress.md)。

## 既有安装台账接线

`SingBoxInstallationService` 使用现有 `InstallationCoordinator` 的 operationId、持久幂等键和阶段记录；Rollback 是独立操作，其他服务不因此开放回滚。严格请求包含明确确认及冻结计划，计划幂等键必须等于台账请求键。安装权限使用 SingBoxInstall，目标为 `singbox`，不接受客户端原生参数。

启动恢复只接受同一 operationId 的特权回执；无回执时按指定原事务执行恢复，拒绝恢复其他 pending 事务。活跃实例锁返回 busy 时继续等待，不重启工作进程，也不根据版本或进程存在推断成功。当前执行提交后不提供取消；下载取消仍待实现。

进度来自实际准备与持久检查点，并经过 Helper 帧协议验证。进度读端断开不打断特权切换，宿主停止不能丢弃最终特权结果。同版本（含仅构建元数据变化）不可作为 Update，必须选择 Repair；较旧版本须明确走 Rollback。
