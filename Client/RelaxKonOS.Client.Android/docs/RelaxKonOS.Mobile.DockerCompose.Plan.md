# AD04：Android Docker 与 Compose 管理计划

> 状态：实现完成；服务端层已在**真实 Compose 宿主**（Docker 29.8.0 / Compose v5.5.1）端到端验收（详见 §6 与 [Mobile Progress](./RelaxKonOS.Mobile.Progress.md)），带认证的 HTTP 往返与 Android/iOS 真机矩阵仍未执行。Android 资源浏览、受限 Compose 准入、定义预览、持久 Stack 操作（幂等提交、断线/重启恢复、部分失败与卷保护）均已接入。优先级：P1。工作量：大。
> 总入口：[无电脑部署路线图](./RelaxKonOS.Mobile.Deployment.Roadmap.md)。

## 1. 目标与范围

手机可管理容器、镜像、网络和卷，并部署“应用 + 数据库 + 缓存”等 Compose 项目。先交付单机 Docker/Compose；不包含集群、远程 Engine 或完整桌面 YAML IDE。

前置为受支持的 Engine/Compose、对应能力及当前身份权限。[Docker 管理设计](../../../docs/applications/RelaxKonOS.DockerManager.md) 和服务端 `DockerEndpoints` / `DockerComposeService` 是复用基础，不能假设所有现有同步请求已经具有持久任务能力。

## 2. 资源归属

| 资源来源 | Android 允许的管理路径 |
| --- | --- |
| AD02 创建的应用资源 | 显示归属并跳转应用详情；改变部署定义必须回到应用部署领域 |
| 本计划管理的 Compose 项目 | 通过 Stack 定义与操作管理关联服务 |
| 外部或未知归属资源 | 清楚标注来源；显式操作，不自动纳管或清理 |

“应用部署”和“Compose Stack”不是两套可以同时任意修改同一容器的入口。当前应用部署运行时使用容器原语，并未以 Compose 实现发布事务；本计划不宣称给 Stack 自动提供相同的修订回滚能力。

## 3. 手机工作流

- 容器页：列表、详情、状态、端口、日志和资源指标；有权限时可启停、重启和执行明确的删除操作。
- 镜像/卷/网络页：用途与关联资源可查；删除前展示引用和影响。停止应用、移除容器与删除持久数据分别表达。
- 新建 Stack：系统文件选择器导入 Compose 或粘贴文本 → 输入变量和秘密引用 → 服务端解析/校验 → 资源及网络预览 → 提交操作。
- 项目详情：整体阶段、各服务健康状态、日志、受管定义与更新入口。手机逐页展开，平板列表/详情双栏。

首版导入范围先限制为镜像型服务、受支持的命名卷与网络。相对路径、`build`、宿主 bind mount、外部文件、特权模式及 Docker socket 等条目必须明确支持或拒绝；不得静默丢弃后继续部署。构建能力后续另行设计。

## 4. 后端补齐与失败处理

1. 核对现有 Compose 端点是否能脱离 HTTP 请求和手机连接执行；必要时新增共享持久操作契约，更新桌面与 Android 调用方。**已实现**：新增 `DockerStackOperationCoordinator` + 宿主账本 `stack-operations.json`（`DockerComposePaths` 是 Compose 源与账本的唯一路径来源）。`stacks/deploy`、`stacks/{name}/{action}`、`stack-operations/{id}/cancel` 一律返回 `202` + 操作记录；桌面 `RemoteDockerClient` 与 Android 网关/仓库消费同一契约，不再有独立的同步路径。
2. 解析后的预览与最终执行使用相同输入版本。执行前重新检查权限、端口、卷和资源归属，防止预览后条件变化。**已实现**：`stacks/preview` 返回 `definitionVersion`（SHA-256 of 名称 + YAML），提交时必须回传，不一致返回 `docker.stack_definition_changed`；两个客户端都在提交前重新预览，不复用上一次点击的答案。同一版本还包括**变量**：本服务没有变量输入面，而 `docker compose config` 会把未设置的变量静默替换为空串且 exit 0，因此任何变量引用在准入阶段就被拒（`docker.compose_variable_unresolved`），否则「批准一份、执行另一份」正是这一条要防的事。
3. Stack 操作按项目互斥，重复提交可识别；重启服务端后核对实际服务与未完成任务，不把失联当成功。**已实现**：`Idempotency-Key` 与请求指纹绑定（同键不同请求 `docker.stack_idempotency_conflict`）、同项目活动操作互斥（`docker.stack_operation_conflict`）、`DockerCompose:MaximumConcurrentOperations` 为全局上限；`IHostedService.StartAsync` 只观察服务并置为 `interrupted`，不重放，引擎失联不上报为成功。
4. 多服务部分成功时，明确显示每个服务的状态及可采取的重试、停止或恢复动作；不声称整组原子回滚。**已实现**：每次命令后都观察项目容器，`Services` 记录的是观察结果而非 Compose 源的推断；命令成功但服务未达目标状态或命令失败但容器残留均为 `partialFailed`，客户端按观察结果给“停止项目/重新部署”动作。`recoveryProblemCode` 只在需要决策且能补充 `problemCode` 时出现。
5. 更新保留持久卷。恢复旧定义可能仍无法撤销数据库迁移，数据恢复走 [AD08](./RelaxKonOS.Mobile.OperationsRecovery.Plan.md)。**已实现**：项目删除用不带 `--volumes` 的 `down`；`GET /volumes/{name}` 返回 `usedBy`（含已停止容器），`DELETE` 在引用非空时返回 `docker.volume_in_use`。

服务端是否支持特定 Compose 功能以实际校验结果为准，Android 不内置第二个不一致的 YAML 执行器。

## 5. 实施阶段

| ID | 工作 | 完成条件 | 状态 |
| --- | --- | --- | --- |
| AD04-M1 | 容器/镜像/卷/网络只读、归属导航、受控生命周期操作 | 不绕过 AD02 修改受管应用定义 | 已实现待验证 |
| AD04-M2 | 明确导入子集、解析预检、持久 Stack 操作与恢复 | 断开 HTTP 后远端任务仍有可查询结果 | 已实现待验证 |
| AD04-M3 | 导入表单、资源预览、部署、服务详情和日志 | 手机部署带数据库的组合项目 | 已实现待验证 |
| AD04-M4 | 更新、故障恢复、保留数据卸载和卷保护 | 部分失败可处理，关联数据不被误删 | 已实现待验证 |

## 6. 验收清单

| ID | 场景 | 预期 | 状态 |
| --- | --- | --- | --- |
| AD04-T1 | 双服务应用部署与更新 | 服务状态准确，数据库数据保留 | **已验收**（真实宿主）：两服务项目部署后观察结果准确，更新同一项目得到 `succeeded`；命名卷在更新与删除项目后都仍在。数据保留靠的就是这条卷语义，未额外拉数据库镜像 |
| AD04-T2 | 不支持条目、缺变量、路径越界或危险挂载 | 执行前拒绝，不静默改写定义 | **已验收**（真实宿主 + 无 Docker）：`build`/特权/设备/外部资源/bind mount/Docker socket 与**未设置变量**都在准入阶段被拒（400），引擎未被调用 |
| AD04-T3 | 一项服务失败、端口冲突、Engine 失联 | 展示部分结果及可执行的恢复措施 | **部分验收**：一服务立刻退出时 `up` 仍 exit 0，分类为 `partialFailed` 并记录引擎实际观察结果（已验收）；端口冲突与引擎失联未注入 |
| AD04-T4 | 超时重试、App 回收、服务端重启 | 核实原 Stack 操作，不重复创建项目 | **部分验收**：同键同文档在真实宿主回放同一操作（已验收）；重启核对不重放由 `PASS DOCKER STACK` 覆盖；Android 设备矩阵未执行 |
| AD04-T5 | 删除已用卷、停止项目、卸载保留数据 | 引用检查和数据保留语义正确 | **已验收**（真实宿主）：运行中与已停止的容器都算引用并拒绝删除，删除项目保留命名卷，无引用后才释放成功 |
| AD04-T6 | AD02 或外部创建的容器 | 归属明确，不跨管理域覆盖定义 | 未验收 |

使用真实 Compose 环境执行并记录版本，不能用容器列表模拟代替。证据写入 [Mobile Progress](./RelaxKonOS.Mobile.Progress.md)。

已执行环境：**Docker 29.8.0（linux/overlayfs）+ Docker Compose v5.5.1**，校验入口 `RelaxKonOS.Server.Tests --stack-live-only`（`PASS DOCKER STACK LIVE`）。该检查用真实 `docker`/`docker compose` 驱动服务端自身的 `DockerComposeService` 与协调器：解析两服务 + 命名卷定义 → 部署（一服务常驻、一服务立刻退出）→ 分类 `partialFailed` 并记录引擎实际状态 → 更新为 `succeeded` → 同键同文档回放同一操作 → `docker volume rm` 在运行中与已停止时都被拒（`docker.volume_in_use`）→ 删除项目后命名卷仍在 → 无引用后释放成功。无 Engine 时该检查明确 SKIP，绝不假通过。

## 7. 本轮实现要点（供验收对照）

- **端点是提交与读取，不是执行**：`202` + 操作记录；执行、互斥、分类、核对都在 `DockerStackOperationCoordinator` 内，`IDockerComposeService` 只有它一个调用方。
- **批准绑定文档**：`preview` → `definitionVersion` → `deploy` 回传；不一致即拒绝，避免“对另一份 YAML 的批准被套用”。
- **账本是权威**：宿主 `stack-operations.json`，保留策略、幂等与诊断界限见 [DockerManager 设计 §3.3](../../../docs/applications/RelaxKonOS.DockerManager.md)；诊断逐行脱敏、单行 512 字符、末 120 行，丢弃行首即标记截断。
- **Android 侧无 SignalR**：`selectStack` 会读取项目操作历史，发现仍在活动的操作即开始按 1 s 间隔轮询（上限 5 分钟），终态后读诊断并刷新；离开页面不影响服务端继续执行。
- **取消是请求**：`cancel` 只把记录标记为不可再取消并通知工作线程，终态由真正停下的工作线程写入。
- **不声称回滚**：部分失败、中断都只是“结果需要决策”，配置里的“恢复旧定义”不等于回滚数据迁移。
- **真实宿主校验必须存在，且不能假通过**：`--stack-live-only` 是唯一需要 Docker 的检查，因此它不进默认序列（否则套件会因为与仓库无关的原因失败），没有 Engine 或没有 `alpine:3.20` 时打印 SKIP 并返回。
- **它抓到的两个缺陷（都已修）**：① `ListServicesAsync`/`ListAsync` 的 `--format` 模板把标签名写成面向 shell 的 `\"name\"`，而参数是直接进 `ProcessStartInfo.ArgumentList` 的，Docker 报 `failed to parse template: unexpected "\\" in operand` 并退出 1 —— 观察结果恒为空，于是**真实宿主上每一次成功部署都被判为部分失败**，停止的项目也永远不出现在列表里；② 未设置变量被 `docker compose config` 静默替换为空串且 exit 0，现在改为准入即拒。两者都无法由「用容器列表模拟」的校验发现。
