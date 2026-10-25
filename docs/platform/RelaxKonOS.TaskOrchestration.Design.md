# RelaxKonOS 任务编排、批量执行与组件安装器设计

> 状态：TO-01 只读规划、持久计划/有效性、HTTP 预览和桌面状态核对已接入；sing-box 变更规划已接入；审批/Queued Run 内部持久状态已加入，生产身份验证、逐步授权和 Executor 尚未接入。
>
> 更新：2026-10-24。
>
> 目标：为服务器初始化、批量管理、组件安装器、专用自动化脚本以及未来 Agent/CLI 提供统一的计划、授权、执行、恢复和审计基础。本文不改变现有 `ManagedRuntimes`、证书、代理、安装管理或 PrivilegedHelper 的领域边界，而是在它们之上增加编排层。

## 1. 结论

RelaxKonOS 不应分别实现“组件安装器的一套批处理逻辑”和“脚本执行器的一套批处理逻辑”。两者应是同一任务编排平台的不同入口：

```text
Component Installer / Workloads ─┐
RelaxKon Task Script / Recipe ───┼─> Planner ─> frozen TaskPlan ─> Preview ─> Approval ─> Task Executor
Agent / CLI / API automation ────┘                                         │
                                                                           ├─ ManagedRuntimes
                                                                           ├─ Certificates
                                                                           ├─ Proxy Server
                                                                           ├─ Services
                                                                           ├─ Firewall
                                                                           ├─ Files / Users / Host settings
                                                                           └─ other bounded domain APIs
```

核心不是“执行脚本”，而是把多个高层意图编译成一个**冻结、可预览、可授权、可恢复的 TaskPlan**。脚本、安装器和 Agent 只能产生意图或计划输入，不能直接绕过领域 API、权限模型、Helper 策略和审计。

第一版不提供任意 PowerShell、Bash、cmd 或“执行任意程序”步骤。未来如果确有需要，原生命令必须作为单独的高风险能力设计，不能成为编排系统的基础逃生口。

## 2. 要解决的问题

### 2.1 服务器初始化的重复操作

RelaxKonOS 的许多功能依赖宿主软件或运行时。用户如果逐项进入应用完成安装、授权、配置和启动，会产生明显的操作成本。例如一个代理服务器环境可能涉及 sing-box、证书、代理实例、端口和防火墙规则；远程开发环境可能涉及 OpenSSH、Git、Docker 或其他运行时。

组件安装器应允许用户选择一个 workload 及其子选项，一次预览并提交整个计划，而不是把每个组件做成互不相关的安装按钮。

### 2.2 自动化和批量管理

高级用户需要可保存、可复用的专用脚本/Recipe，用于：

- 新服务器初始化；
- 多个运行时的安装、更新、修复或卸载；
- 多个服务的启动、停止和重启；
- 证书、代理、防火墙等 RelaxKonOS API 的组合操作；
- 对同类实例执行批量操作；
- 生成可审计、可重放但不会盲目重复副作用的运维流程。

脚本应调用稳定的自动化动作契约，而不是直接绑定内部 HTTP 路由或拼接 shell 命令。

### 2.3 减少重复授权，但不扩大权限

多个需要宿主管理权限的操作可以共享一次明确审批，但“一次授权”不得等同于给脚本一段时间的任意管理员权限。

正确模型是：

1. Planner 解析并冻结完整计划；
2. UI 展示所有副作用、目标和权限要求；
3. 用户对**这个不可变计划**进行一次审批；
4. 执行期间每个特权动作仍经过既有 capability、target、身份和 Helper 策略检查；
5. 计划内容、敏感目标或权限范围发生变化时必须重新计划和审批。

## 3. 分层模型

任务编排建议分为五层，而不是只有“脚本 -> Executor”两层。

### 3.1 Intent / Definition

描述用户“想要什么”，来源可以不同：

- `WorkloadDefinition`：组件安装器的工作负载、功能组、子选项和依赖；
- `TaskRecipe`：用户可保存的专用脚本；
- Agent/CLI：通过结构化 API 提交的自动化意图；
- 内置应用：例如 Proxy Server 提交“准备所需运行时并创建实例”的组合意图。

这一层不直接持有管理员执行权，也不直接调用 Helper。

### 3.2 Planner / Compiler

Planner 负责把意图解析成确定的 TaskPlan，包括：

- 读取当前宿主状态；
- 解析 workload 的选项、依赖和冲突；
- 解析 ManagedRuntime 的明确 releaseId，而不是在执行时重新解释 `latest`；
- 判断已满足的步骤并标记为 no-op；
- 冻结证书、实例、路径、端口、服务等目标引用；
- 推导任务依赖 DAG；
- 推导每一步的 capability、target、资源锁、风险和重启影响；
- 生成预检项、补偿能力和不可恢复提示；
- 计算规范化 plan digest。

Planner 可以做只读发现和校验，但不能在“预览”阶段偷偷执行具有外部副作用的安装、删除、服务重启或防火墙变更。

### 3.3 Approval

审批对象是冻结的 `TaskPlan`，不是脚本文本，也不是一个笼统的 `AdminAccess=true`。

审批至少绑定：

- `planId` / `planDigest`；
- 目标宿主；
- 当前 canonical subject / session；
- 计划使用的 capability + normalized target 集合；
- 关键策略/目录/安装状态修订；
- 审批时间、有效期和撤销状态；
- 是否允许在客户端断开后继续执行；
- 敏感参数的引用和修订，而不是秘密明文。

标准用户可以复用现有管理员认证器完成一次计划级审批，但该审批只覆盖计划中已经列出的封闭操作。HostAdministrator/HostRoot 仍遵循现有宿主等级和动态策略校验，不因为进入 Task Executor 就取得额外能力。

### 3.4 Task Executor

Executor 只接受已冻结计划。它负责：

- 持久化 `TaskRun`；
- 按 DAG 和资源锁调度步骤；
- 给每一步分配稳定 idempotency key；
- 调用领域动作执行器；
- 记录开始、进度、结果、回执和审计；
- 支持显式取消；
- 客户端断开后按计划策略继续或停止；
- Server 重启后通过 journal/reconcile 恢复；
- 失败时执行允许的 compensation，不能宣称跨所有系统动作存在 ACID 原子回滚。

### 3.5 Domain Actions

Executor 不重新实现安装器、证书管理、服务控制或代理配置。各领域注册稳定动作，例如：

```text
runtime.ensureInstalled
runtime.update
runtime.repair
runtime.uninstall

service.ensureRunning
service.stop
service.restart

certificate.request
certificate.install
certificate.renew

proxy.ensureInstance
proxy.updateInstance
proxy.removeInstance

firewall.ensureRule
firewall.removeManagedRule
```

领域动作仍拥有自己的验证、事务、健康检查和 Helper 调用。Task Executor 只负责编排，不把 ManagedRuntimes 已有的事务重新复制一遍。

## 4. TaskPlan 必须是不可变执行契约

建议的核心对象：

```text
TaskPlan
  planId
  planDigest
  hostId
  createdBy
  createdAt
  sourceKind            // workload | recipe | agent | api | app
  sourceIdentity        // workloadId / recipeId / agent request id
  inputRevision
  observedStateRevision
  steps[]
  requiredApprovals[]
  warnings[]
  restartImpact[]
  estimatedEffects[]
```

每个 `TaskStep` 至少包含：

```text
stepId
kind
schemaVersion
displayName
dependsOn[]
inputs                // 规范化后的结构化参数
resourceKeys[]        // 用于锁和并发判断
requiredCapabilities[]
riskClass
preconditions[]
retryPolicy
compensationKind?
nonReversibleReason?
```

执行时不得根据远程 `latest`、新发现的依赖或 Agent 的临时决定增加新的特权步骤。任何会扩大副作用或授权范围的变化都生成新计划并重新审批。

只允许不扩大权限和副作用的运行期数据，例如下载进度、领域动作内部事务 ID、健康检查结果和输出引用。计划内修订传递和新资源输出必须遵守 §19 的绑定规则，不能将任意输出解释为新的目标。

## 5. 计划预览应成为统一用户体验

无论计划来自 Installer、Recipe 还是 Agent，最终使用同一个 Preview 页面。

示例：

```text
Initialize Proxy Server

Will perform 7 changes:
  Install sing-box 1.14.3
  Install Nginx 1.xx
  Request certificate for proxy.example.com
  Create VLESS proxy instance
  Add managed TCP 443 firewall rule
  Enable proxy service
  Start proxy service

Already satisfied:
  RelaxKonOS certificate service available

Privileges:
  5 host-management operations
  Targets: sing-box, nginx, TCP/443, proxy service, certificate reference ...

Restart impact:
  Nginx service will restart

Rollback:
  5 steps compensatable
  Certificate issuance is not guaranteed reversible

[Cancel] [Approve & Run]
```

用户不是在给“脚本”授权，而是在批准一份具体变更清单。

## 6. 单次授权模型

### 6.1 PlanApproval，不是通用 Elevation Session

新增的计划授权应与现有短期单操作 grant 分开建模，例如 `TaskPlanApproval`。它可以复用管理员凭据验证和宿主身份判定，但不能把现有五分钟 grant 简单扩大成“期间所有操作都允许”。

推荐规则：

- 只对当前 `planDigest` 有效；
- 只对当前 host 和 subject 有效；
- 只允许 frozen capability/target 集合；
- 默认只能启动一次 TaskRun；
- 有最大有效期，超时未启动则重新审批；
- 运行开始后可以允许后台继续，但必须持续检查 session/subject 撤销、宿主管理员策略撤销和 Helper 策略；
- Helper 仍检查具体 operation kind、目标、scope 和资源所有权；
- TaskPlanApproval 不能转换成 shell、任意路径、任意服务或任意端口权限。

### 6.2 计划变化必须失效

以下情况至少应触发重新计划，权限或副作用扩大时必须重新审批：

- `latest` 解析到不同 releaseId；
- workload 依赖变化；
- 端口、路径、服务或证书目标变化；
- 当前安装实例/所有权发生变化；
- 管理员策略或受保护信任目录修订变化；
- 原本 no-op 的步骤因宿主漂移变成实际修改；
- 任何新增 privileged step。

仅显示文本、下载进度或不影响执行语义的元数据变化不应让计划无意义地失效。

## 7. 执行语义：DAG + 资源锁，不是简单 for 循环

TaskPlan 采用 DAG 可以表达安装之间的依赖，同时允许安全并发。例如多个独立制品下载可以并行，而共享服务、同一 runtime 或同一配置资源的变更必须串行。

领域动作返回或声明 `resourceKeys`，例如：

```text
runtime:sing-box
runtime:nginx
service:nginx
firewall:tcp:443
proxy-instance:<id>
certificate:<id>
```

Executor 只有在依赖满足且资源锁兼容时才调度。第一版宁可保守串行，也不能为了速度绕过已有 ManagedRuntime/安装锁。

默认失败策略为 `stop-on-failure`。未来可允许显式 best-effort 批处理，但必须由动作类型声明安全，不能让安装/权限/删除步骤默认“失败继续”。

## 8. 幂等、重跑与恢复

服务器初始化脚本必须允许用户在“部分已经配置”的机器上重新执行，因此动作应尽量采用 desired-state / ensure 语义。

例如：

- sing-box 已是目标 releaseId -> no-op；
- 已存在完全一致且属于 RelaxKonOS 的防火墙规则 -> no-op；
- 服务已经运行 -> no-op；
- 已存在外部安装的 Nginx -> 不自动接管，Planner 产生 conflict；
- 同名证书来自不同所有权或密钥 -> 不猜测覆盖。

TaskRun 保存 step journal、idempotency key 和领域回执。Server 或客户端中断后，恢复流程先 reconcile 当前状态，再决定 continue、retry、compensate 或进入 `NeedsRecovery`。不得简单从第一个步骤重新执行。

## 9. 回滚与补偿边界

“批量任务”不能暗示所有操作都能整体事务回滚。每个 Action 必须声明：

- `RetrySafe`：是否可以安全重试；
- `Compensatable`：是否有明确补偿；
- `CompensationScope`：只回滚本次自有资源还是恢复旧状态；
- `Irreversible`：是否存在不可逆外部副作用。

ManagedRuntimes 已有的安装/更新/回滚事务继续由其自己负责。Task Executor 只在动作边界协调补偿，例如前一步新建了自有防火墙规则、后一步失败时可以移除该规则；不能擅自 purge 用户原有包或恢复已撤销凭据。

## 10. RelaxKon Task Script / Recipe

脚本第一版建议采用声明式结构化模型，逻辑名称可使用 `RelaxKon Task Recipe`；具体是 YAML、JSON 还是专用文本语法可以后定，先冻结 AST/Schema。

概念示例：

```yaml
version: 1
name: Initialize Proxy Server

parameters:
  domain:
    type: hostname
  port:
    type: port
    default: 443

tasks:
  - id: singbox
    use: runtime.ensureInstalled
    with:
      runtimeId: sing-box
      channel: stable

  - id: certificate
    use: certificate.request
    with:
      domain: ${domain}

  - id: proxy
    use: proxy.ensureInstance
    needs: [singbox, certificate]
    with:
      protocol: vless
      port: ${port}
      certificateRef: ${certificate.output.certificateRef}
```

### 10.1 V1 明确限制

第一版不支持：

- arbitrary shell / PowerShell / Bash / cmd；
- 任意可执行路径；
- 任意 HTTP 请求作为特权动作；
- 运行时下载并执行未知代码；
- 可无限展开的循环；
- 在审批后根据脚本输出生成新的 privileged task；
- 用字符串拼接绕过路径、端口、服务、用户等类型化参数。

条件判断应优先在 planning 阶段基于只读宿主事实解析。若未来支持运行期分支，必须在审批前冻结所有可能的特权分支及最大权限范围，不能隐式扩大计划。

### 10.2 参数和秘密

Recipe 可声明参数类型，让 UI 自动生成表单。普通参数进入规范化计划；秘密只使用受保护引用：

```text
secretRef + revision + purpose
```

Plan、日志、审计和脚本文本不得展开密码、私钥或 token 明文。Plan digest 应绑定秘密引用的身份/修订和用途，而不是把秘密值本身持久化进计划。

## 11. 组件中心 / Workloads

Component Installer 产品设计已拆分为[组件中心设计](RelaxKonOS.ComponentCenter.Design.md)，统一拥有安装、更新、修复、回退、卸载、来源选择、应用未就绪引导和工作负载交互。各应用仅保留依赖状态及受控跳转，业务配置和服务启停仍归领域应用。

组件中心是 TaskPlan 的图形化输入端，不是新的安装后端。组件/工作负载引用受信任 action/runtime ID 和约束，不在 UI manifest 写安装命令。变更草稿经 Planner 产生 no-op、conflict、unsupported 和审批要求，再使用本文统一 Preview/Approval/Executor。产品实施由 CC 跟踪，编排基础由 TO 跟踪，不重复维护 UI 规范。

## 12. Action Catalog：脚本不直接依赖内部 API

为了隔离内部 API 重构与自动化动作语义，建议增加具有明确 Schema 标识的 `Action Catalog`：

```text
ActionKind
  id
  schemaVersion
  inputSchema
  outputSchema
  planner
  executor
  privilegeMapper
  resourceMapper
  retrySemantics
  compensationSemantics
```

Recipe、Installer、Agent 都只引用 ActionKind。内部 HTTP endpoint、C# service 和 Helper 协议可以演进，调用者不直接依赖其路由。首次正式发布前只维护唯一当前 Action/Recipe Schema；变更时同步更新仓库内调用者、测试、示例和文档，不增加旧 Action 别名、回退路由、双格式解析或迁移适配。schemaVersion 用于精确识别和拒绝不支持的输入，不构成旧版本兼容承诺。

Action Catalog 不是插件任意注册 root 操作的机制。能映射到 Host capability / Helper 的特权 Action 必须由 RelaxKonOS 受信任代码注册，并经过独立安全审查。

## 13. Agent 作为第三种入口

未来 Agent 不应该直接拥有“调用管理员 API”的特殊通道。Agent 的高风险操作也走：

```text
Agent intent -> Planner -> TaskPlan -> human preview/approval -> Executor
```

这样 Agent 可以负责组合和解释，而不是成为权限边界。

低风险只读计划可以允许策略化自动批准；有副作用或特权步骤默认仍要求显式审批。未来如支持“预先批准策略”，也应批准明确的 ActionKind + target scope，而不是批准 Agent 获得管理员角色。

## 14. API 和持久化建议

概念 API：

```text
POST /task-plans/plan
GET  /task-plans/{planId}
POST /task-plans/{planId}/approve
POST /task-plans/{planId}/run
GET  /task-runs/{runId}
POST /task-runs/{runId}/cancel
POST /task-runs/{runId}/recover
```

实际路由可沿用 RelaxKonOS 当前 API 风格；这里冻结的是生命周期，不是 URL。

至少持久化：

- immutable TaskPlan；
- plan digest 与来源；
- approval record；
- TaskRun 状态；
- 每一步 journal；
- 领域 operationId / receipt；
- compensation/recovery 状态；
- 审计主体与宿主策略修订。

以下分别描述计划、审批和执行状态；完整状态与取消语义见 §19.7，不将它们合并成一个可变 TaskPlan：

```text
Plan:     Resolved -> Invalidated
Approval: Granted -> Consumed / Expired / Revoked
Run:      Queued -> Running -> Succeeded / Failed / Cancelled / NeedsRecovery
```

## 15. 与现有系统的关系

### ManagedRuntimes

Task Executor 调用其公开的计划/安装/更新/修复/回滚能力，不复制制品信任、归档切换和健康检查事务。TaskPlan 中冻结明确 releaseId；运行时目录变化不得改变已批准目标。

### HostPrivilegeRouting / PrivilegedHelper

计划级审批复用 canonical host identity、administrator authentication、capability/target 和 Helper 封闭操作。它只是把多个已知目标聚合为一次审批 UX，不降低每一步的 Helper 检查，也不提供 root shell。

### InstallationManagement

现有安装组件、设置、修复、独立 firewall action 等可逐步注册为 ActionKind；不要为了接入 Executor 重写其既有领域事务。

### Certificates / Proxy / other apps

各应用暴露稳定的 planner/executor adapter。Installer 或 Recipe 不能直接写这些应用的数据库、配置文件或服务文件。

## 16. 建议实施顺序

### TO-01：TaskPlan 核心与只读 Planner

当前状态：内部核心实施中。共享不可变契约、严格输入读取、规范化摘要与 Server 规划器，以及 sing-box 安装状态/目录核对动作已加入源码；已增加生产只读观察适配、HTTP 预览及桌面状态核对入口。尚不构成完整 TO-01 交付，详见文末实施记录。

- 定义 `TaskPlan`、`TaskStep`、Action Catalog 和 digest；
- 先接 2~3 个无副作用或低风险 Action；
- 支持 Preview、DAG、no-op 和 conflict；
- 不做脚本解析器。

验收：同一宿主状态、主体、输入和 Action 实现修订生成相同规范化语义载荷；服务端分配的 planId/创建时间不影响确定性，冻结后执行内容不能变化。

### TO-02：TaskRun / journal / Executor

- 持久化 TaskRun；
- 资源锁；
- step state；
- idempotency；
- cancel / reconnect / Server restart reconcile；
- 与 TO-03 的最小审批和执行身份上下文一起交付一个纵向闭环；不能先开放特权 Executor 再补授权；
- 接入一个已有成熟事务领域，例如 ManagedRuntime install/update；关联既有 operationId，不复制领域事务；
- 验证计划内修订传递、动态资源绑定以及领域提交与 journal 写入之间的崩溃恢复。

验收：客户端断开不丢状态；Server 重启不会从头盲目重放。

### TO-03：PlanApproval 与批量特权动作

- 最小能力与 TO-02 同时交付，本项继续补齐批量、撤权和恢复验收；
- 复用 HostAdministratorAuthenticator；
- planDigest + capability/target set 绑定；
- 标准用户一次管理员认证；
- Helper 仍逐动作验证；
- 撤权、策略变化、计划变化全部失败关闭。

验收：一次 UI 审批可完成多个已列出的 privileged steps，但任何未列出的目标均被拒绝。

### TO-04：组件中心 / Workloads 接入

- 对接[组件中心 CC 跟踪项](RelaxKonOS.ComponentCenter.Design.md#10-实施跟踪与验收)，由其拥有 UI、功能树、来源和应用引导；
- 将组件变更和 workload 依赖/冲突转换为冻结 TaskPlan，复用统一 Preview；
- 第一批使用真实服务器初始化及组件生命周期场景验收。

### TO-05：Task Recipe

- 冻结 AST/Schema；
- typed parameters；
- output references；
- planning-time conditions；
- secret references；
- 导入、预览和执行；
- 仍不提供 arbitrary shell。

### TO-06：Agent / CLI

Agent、CLI 只提交结构化 intent/recipe，复用完全相同的 Planner、Approval 和 Executor。

## 17. 第一阶段最小可行范围

为避免再次形成一个过大的横切项目，首个实现建议只覆盖一个真实 workload：

```text
Proxy Server Basic
  -> ensure explicit sing-box release installed
  -> prepare typed inbound / principal / credential references
  -> validate and publish approved proxy configuration
  -> optional managed firewall rule
  -> ensure service running
```

第一阶段先证明：

1. Installer 和手工构造 Recipe 能生成语义相同的 TaskPlan；
2. 同一个 Preview/Approval/Executor 可以执行两种来源；
3. 多个 privileged actions 只要求一次计划级审批；
4. 已满足步骤重新执行为 no-op；
5. 中途失败能准确展示哪些动作已提交、哪些已补偿、哪些需人工恢复；
6. 任意未出现在 approved plan 中的 Helper 目标均无法借此次审批执行。

动作拆分服从现有 Proxy Server 的配置、inbound、principal、credential 和发布事务，不以示意名称 `proxy.ensureInstance` 假定已有通用实例接口。若配置发布已负责服务切换，后续 ensure-running 只验证或补足目标状态，不重复重启。开机启动与当前运行状态独立预览、独立表达。首期不包含创建受信任发布源；来源必须已按 ManagedRuntimes 规则登记。

以上是 TO-02～TO-04 的验收目标，尚未实施。内部结构化 Recipe 输入先验证语义等价，文本解析和导入仍属于 TO-05。每个实际接入动作必须记录 OS、架构、宿主模式、运行时版本范围和验收证据；某个平台未验证就不得展示为可执行。其他运行时不能继承 sing-box 的验收结论。

通过后再扩展 Nginx、OpenSSH、Docker、证书、批量服务管理等动作。

## 18. 非目标

本设计当前不追求：

- 替代 Ansible、Terraform、PowerShell 或完整编程语言；
- 在第一版提供分布式多宿主事务；
- 对第三方任意脚本承诺安全执行；
- 把所有领域操作包装成一个万能 root API；
- 强行让所有动作支持回滚；
- 让 UI manifest 或 Agent 自行声明新的 Helper capability；
- 通过一次授权长期免除后续所有管理员确认。

RelaxKonOS 的优势应是：**将产品已理解、已约束、可审计的领域操作安全地组合起来，而不是重新制造一个通用 shell。**

## 19. 适配现有系统的执行契约

本节为实施约束，全部属于待实现和待验收要求。现有领域设施是接入基础，不代表任务编排已经实现。

### 19.1 代码归属与领域适配

| 位置 | 职责 |
| --- | --- |
| `Shared/RelaxKonOS.Protocol/TaskOrchestration/`（拟新增） | 唯一当前 DTO、枚举、路由、问题码及结构化引用契约 |
| `RelaxKonOS.Server/TaskOrchestration/`（拟新增） | Planner、受信任 Action Catalog、审批、调度、journal 与恢复协调 |
| 各 Server 领域模块 | 适配领域计划、执行、查询、取消、恢复和补偿；继续拥有真实事务 |
| Client Runtime 通信层与内置应用 | 类型化访问、预览、审批、进度与恢复界面；UI 不直接调用 HTTP |
| PrivilegedHelper | 保留封闭操作、受保护信任与所有权验证，不执行 Recipe 或任意 DAG |

可复用的现有入口包括 `Installations/InstallationCoordinator.cs`、`InstallationOperationStore.cs`、ManagedRuntimes 的计划与归档事务、`ProxyServer` 的配置发布及运行时授权设施。适配器调用领域服务，不通过 Server 自调用 HTTP 模拟用户请求，也不绕过 endpoint 原有的准入规则。

Android 只消费共享契约；其交互、实现进度、国际化和主题规范放在 `Client/RelaxKonOS.Client.Android/docs/`，本文只链接该目录，不建立第二份移动端规范。

Guardian 的 `ScriptTask` 是已有的一次性可执行程序任务，具有自己的 RunAs 与 Interrupted 语义。它不是本设计的 TaskRecipe/TaskRun，不自动接入编排恢复，也不注册为特权 Action。界面和日志明确区分两类任务，不通过包装 ScriptTask 绕过第一版禁止任意命令的边界。

### 19.2 准入、审批与逐步授权

每个 Action 描述符除 §12 字段外，还必须提供业务准入、宿主功能要求、所有权判定、执行实现修订及恢复适配器。Planner、审批、执行和恢复分别校验适用规则。现有安装入口的 controller 角色、服务级 `installation:install` 限制、宿主功能检查及 elevation 检查仍有效；普通应用授权不自动变成安装权限。

计划级管理员认证只满足审批中明确的宿主提权要求，不能授予原主体缺少的业务角色。管理员自动宿主授权也不跳过副作用预览和计划确认。

新增受限执行上下文，至少包含：

```text
approvalId / planDigest / runId / stepId
hostId / canonicalSubject / approverIdentity / sessionIdentity
actionKind / actionImplementationRevision / operationKind
normalizedTargets / capabilities / domainOperationId
policyRevision / authorizationExpiry
```

审批分别记录发起主体与审批身份、认证方式及时间，不保存管理员密码。Executor 只能为该 run 的该步骤取得对应领域操作的短期授权。禁止将计划转换成一批通用 `HostElevationSessionStore.Grant()`：现有 grant 可被同会话其他请求使用，不能表达计划及步骤隔离。独立 API 请求不能借用 TaskRun 上下文。

领域动作继续使用当前 Helper 封闭协议和新鲜授权检查。现有运行时回调通道按 operation 创建，通道密钥和短期 grant 不进 plan、journal 或审计；恢复时经校验重建。Helper 若缺少某动作所需的验证能力，该 Action 必须被阻止，不能仅凭 Server 的布尔标记放行。

冻结计划中同时列出正常执行和补偿所需的 capability/target。补偿也要检查当前授权、所有权和信任；撤权后不得以“回滚”为由启动新的未授权特权操作。已进入领域提交或安全收尾边界的操作按其现有事务规则收尾，记录结果；不得在不可中断点强杀 Helper。

### 19.3 会话、后台执行和访问控制

审批记录 `startBefore` 与 `runAuthorizationExpiresAt`，两者用途不同。启动时在同一次持久提交中校验审批未消费、期限与摘要，并绑定唯一 run；并发或网络重试返回同一个 run，禁止重复消费。`run` 请求还需 actor 范围幂等键，同键不同请求返回冲突。

| 事件 | 执行规则 |
| --- | --- |
| 客户端断开 | 允许后台时继续；否则持久请求取消，按领域安全边界收尾 |
| 访问令牌自然过期或刷新 | 已启动任务使用有界后台委托，不持久复用旧 JWT；委托有效且主体/会话未撤销才可继续 |
| 主动注销、会话撤销、主体禁用或业务权限撤销 | 停止调度新变更，当前领域操作安全收尾；不能取得新鲜授权时失败关闭 |
| 执行授权到期 | 停止新变更；保留 journal，恢复前重新审批剩余操作 |
| Server 重启 | 恢复委托身份和撤销状态检查，重建短期通道；不能证明有效则禁止继续变更 |
| 策略、信任或实现修订变化 | 对照冻结约束重新验证；不允许把旧审批自动扩展到新语义 |

后台委托需要持久撤销检查基础；未实现前不得承诺跨 JWT 过期或 Server 重启自动继续特权任务，应进入 `NeedsRecovery`。恢复审批绑定原 run、剩余步骤、已提交回执及补偿范围；不重新执行已成功步骤。若目标或副作用变化，创建关联的新计划并重新审批。

计划和 run 查询默认限创建主体；跨主体查询需要明确的管理权限，并对参数、秘密引用和领域诊断作字段级过滤。取消需要任务控制权限，恢复和补偿还需当前领域权限。仅知道 planId/runId 不构成访问权；订阅和重连遵循相同规则。

### 19.4 计划内修订与动态资源引用

`observedStateRevision` 应表达按领域/资源索引的基线集合，不假设整个宿主有单一全局修订。每个前置条件明确来源：

- `ObservedBaseline`：规划时已存在资源的身份、所有权和修订；
- `PriorStepReceipt`：冻结依赖边所指定前序步骤的可信回执字段；
- `LiveSafetyCheck`：当前权限、撤销、信任、健康及平台约束，执行前仍需检查。

例如运行时修订 10 经安装步骤变为 11，配置发布步骤使用该安装回执里的修订 11。回执必须证明同一 host、run、step、安装身份与预期变更；不得将“读取当前最新修订”当作替代。不能由本次回执解释的修改是外部漂移，停止执行；no-op 变成实际变更也需重新规划。

创建证书、inbound 或凭据等尚无真实 ID 的资源时，审批前冻结逻辑引用，例如 `planned-resource:certificate-01`，以及资源类型、所有者、域名/端口/用途约束和允许消费者。生产步骤通过领域幂等操作创建资源，可信回执一次性绑定逻辑引用和真实 ID；消费者只接受已声明依赖上的类型化绑定。

绑定不修改 TaskPlan 或其摘要，保存为 TaskStepRun 的执行证据。输出不能提供任意路径、服务名或新增目标。秘密只输出受保护引用、修订和用途；创建成功后重试必须找回原引用，不能重新生成秘密。V1 只支持登记过的字段引用，不提供字符串插值执行、动态字段路径或可执行表达式。

### 19.5 幂等、领域台账与崩溃恢复

```text
TaskRun
  TaskStepRun
    domainOperationId + requestDigest + receipt
```

TaskRun 记录编排决策和步骤关系，领域台账记录下载、切换、发布、健康检查及领域补偿。安装步骤复用现有 Installation operationId 和阶段；客户端从领域记录读取真实阶段进度，不把阶段百分比报告成整个计划百分比。

每一步在提交副作用前持久化 dispatch intent，包括稳定幂等键、规范化请求摘要、领域操作身份或查询关联键。幂等键按 host/run/step/执行或补偿区分，重试不换键；同键不同请求必须拒绝。动作注册前必须支持以键查询或幂等重新提交找回原操作，不能只依赖内存 Task。

| 崩溃窗口 | 恢复行为 |
| --- | --- |
| dispatch intent 已保存，领域尚未受理 | 用原键核对后提交 |
| 领域已受理，TaskStep 未保存 operationId | 由原键找回同一操作 |
| 领域已提交，TaskStep 尚未记成功 | 校验原回执及实际状态后记成功，不重复副作用 |
| 领域结果未知或存在待恢复事务 | 先运行领域 reconcile；无法确定则进入 NeedsRecovery |
| 补偿已提交，journal 尚未更新 | 通过独立稳定补偿键找回结果，不重复删除或恢复 |

启动顺序为先让领域完成启动核对，再恢复编排。只有结果确定、当前授权有效且条件满足的步骤可继续；领域 Interrupted 不能自动解释为“未执行”。编排存储采用耐久原子提交、完整性验证和失败关闭；损坏时禁止新变更并保留人工恢复入口，不清空台账假装首次启动。

### 19.6 锁、手工操作与补偿冲突

资源键由领域统一生成并规范化，Executor、安装入口和应用内手工修改共用同一资源身份。服务名、路径大小写、宿主、安装 ID、网络协议与端口必须按领域和平台规则处理；不能让 `service:SingBox` 与另一处自拼的键各锁各的。

Executor 调度预留不替代领域锁。固定顺序为先取得调度预留，再进入领域事务锁；同一步多资源按稳定全序获取。调用领域服务时不得预先持有其内部锁；领域持锁期间不得回调并等待 Executor 锁或重新取得同一领域锁。无共享预留机制的手工操作仍由领域锁串行化，事务锁内必须重新核对修订。

域内重试、发布、安装和恢复继续使用同一领域锁。进程退出后的调度预留必须结合 durable run 和领域 reconcile 清理，不能单凭时间到期认定正在运行的 Helper 已停止。

补偿按依赖逆序处理；并发分支必须先收敛。仅处理本次回执证明的新建资源或允许恢复的旧状态，并核对资源仍为本次提交后的身份/修订。发现后来被用户或其他任务修改时不覆盖、不删除，进入 NeedsRecovery。任一补偿失败保留原失败与补偿失败两个结果。

### 19.7 状态、取消与失败

计划内容冻结后只有独立的有效性记录可变。审批、运行和步骤状态分别持久化。执行请求只引用 planId/digest，由 Server 读取持久冻结计划；客户端不能提交替换步骤，重新计算摘要也不构成授权：

| 对象 | 状态 |
| --- | --- |
| Plan 有效性 | Resolved、Invalidated |
| Approval | Granted、Consumed、Expired、Revoked；消费后撤销仍阻止关联 run 继续取得授权 |
| TaskRun | Queued、Running、Cancelling、Compensating、NeedsRecovery、Succeeded、Failed、Cancelled |
| TaskStepRun | Pending、Dispatching、Running、Reconciling、Succeeded、NoOp、Skipped、Failed、Cancelled、Compensating、Compensated、NeedsRecovery |

WaitingApproval 是界面根据缺少有效审批派生的显示状态。NeedsRecovery 统一用于结果未知、授权无法恢复或补偿不完整；不再混用 `NeedsOperatorRecovery`。人工恢复必须留审计并触发新一轮 reconcile。

取消先持久记录 cancel request，停止调度后续普通步骤，再调用领域取消接口。不可取消的提交阶段继续到安全边界，之后核对实际结果；HTTP 连接关闭不等于取消成功。可选补偿只按已批准的取消/失败策略进行。仅当活动领域操作已收敛且资源结果明确时，TaskRun 才能进入 Cancelled；否则进入 NeedsRecovery。所有副作用均已提交后到达的取消不能把 Succeeded 改写为 Cancelled。

stop-on-failure 停止新步骤，并等待已在运行的独立分支收敛，再决定补偿。Failed/Cancelled 不表示资源恢复原状，界面必须分别展示已提交、已补偿、跳过和保留资源。补偿成功也不能把原任务改为 Succeeded。

### 19.8 摘要、实现变化与运行配额

计划采用独立的规范化语义载荷计算 SHA-256：JSON 属性按 ordinal 排序，集合字段去重后稳定排序，有序参数保留顺序，数字使用 schema 限定的规范表示，禁止重复属性、未知执行字段及含糊类型转换。摘要不包含自身；序列化规则在 Shared 契约中唯一实现，并有固定测试向量。

语义载荷包含 host/主体、source/input revision、步骤 ID、Action schema/执行实现修订、规范化参数、依赖、逻辑资源及输出约束、基线、权限、风险、取消/失败/补偿策略和授权相关约束。planId、创建时间、显示名称、翻译文本、估时和进度不参与语义摘要；审批仍同时绑定 planId 和 digest。两种入口的语义等价验收比较规范化执行图，来源元数据不同不要求整个计划摘要相同。显示副作用和警告必须来自冻结语义，不能通过排除显示字段隐藏变更。

部署升级后缺少精确匹配的 Action 执行实现修订时，不继续执行旧计划。先查询已有领域操作并收敛结果，再由当前实现为剩余意图生成新计划审批；不保留旧实现、旧协议解析或迁移适配来重放旧计划。已有运行记录保留其状态和证据，不因无法执行就自动删除。

每个宿主设置明确的步骤数、依赖深度、计划大小、输出大小、并行数、排队数、重试次数和运行时长上限。超过限制在规划/准入阶段返回稳定问题码；运行超时走安全取消，不强杀提交阶段。限制值由当前配置与契约明确提供，不允许无限默认值。参数、结果、日志和订阅均有大小限制与秘密过滤。

历史保留策略不得清除活动/待恢复任务、尚需查询的领域关联或仍在幂等保证期内的键。审计记录 actor、approver、host、plan/run/step、领域 operationId、策略修订及稳定问题码，不记录密码、私钥、token 或原始敏感诊断。

### 19.9 实施验收补充

TO-01～TO-03 必须覆盖以下行为，测试以真实边界和可注入崩溃点为准：

1. 规范化测试向量、依赖环、类型错误、未知 Action 和实现修订拒绝；no-op 漂移不能静默变为变更。
2. 业务角色/宿主功能不足时，即使管理员认证成功也不能执行；独立请求不能借用计划审批。
3. 同审批并发启动、断线重试只产生一个 run；同幂等键不同请求冲突。
4. 计划内修订正确传递，外部修改被拒绝；伪造或错类型输出不能绑定目标。
5. §19.5 各崩溃窗口均不会重复安装、申请证书、生成秘密或删除资源。
6. 手工操作与编排竞争、嵌套锁、重启遗留预留均不会产生并发覆盖或死锁。
7. JWT 过期、注销、撤权、Server 重启和授权不可用按 §19.3 执行；不保存可复用管理员凭据。
8. 下载取消、提交阶段取消、并发分支失败和补偿冲突准确呈现；取消响应不冒充已撤销副作用。
9. 跨用户查询/订阅/取消/恢复被正确限制；日志、摘要载荷和错误响应不泄露秘密。

这些验收与各领域现有测试共同构成交付门禁。TO 跟踪项与实际接入应用文档在实现时同步更新，未完成项保留“计划中”，不将本文补齐视为功能完成。

### TO-01 内部只读核心（2026-10-24）

`Shared/RelaxKonOS.Protocol/TaskOrchestration/` 现定义 `TaskPlanIntent`、`TaskStepIntent`、`TaskPlan`、`TaskStep`、资源基线、能力/目标与明确配额。集合使用不可变数组，冻结时克隆输入 JSON；原始 JSON 文档释放不影响计划。`TaskPlanJson.ReadIntent` 是待接入边界的唯一严格输入读取入口，限制正文与深度，先拒绝任意层重复属性，再拒绝未知契约字段。当前 Action 参数只允许其明确 schema，不接受 shell、URL、路径或未知参数。

`TaskPlanCanonicalizer` 在 Shared 唯一实现规范化：属性按 ordinal 排序，步骤稳定排序、集合去重/稳定排序，有序输入数组保留顺序，数值仅接受当前 schema 使用的 Int64；语义摘要包含宿主、主体、来源/输入修订、动作 schema/实现修订、参数、依赖、资源/基线、处置结果、风险及失败/取消/配额约束。planId 与 createdAt 不参与摘要。已有基础 JSON/SHA-256 与完整两动作计划的固定向量；此处 schema 是唯一当前格式，不提供旧格式读取。

`RelaxKonOS.Server/TaskOrchestration/TaskPlanner.cs` 先验证整图与所有参数，再执行只读观察；支持依赖去重、环/深度/大小拒绝、no-op/conflict/unsupported，以及同一资源在一次规划中出现矛盾基线时整体拒绝。只读动作返回 Ready（将产生变更）会被拒绝。初批编译期动作 `runtime.verifyInstallation`、`runtime.verifyCatalog` 由受信任 `IRuntimeTaskObservations` 提供事实：前者核对实际 host/runtime/安装 ID/状态修订、所有权和活动/恢复状态；后者核对来源归属、目录/信任修订、签名、新鲜度、有效期及撤销。这里只核对事实，不创建 RuntimeChangePlan，不授予安装许可；目录过期、no-op 漂移或来源撤销都不会转换成变更步骤。

验证：`dotnet run --project Tests/TaskOrchestration/RelaxKonOS.TaskOrchestration.Tests.csproj --no-restore -v:q`，**41 项通过**。专项引用 Server 工程并注入领域观察，覆盖固定向量、规范化、输入所有权、整图/schema 拒绝、配额、取消、矛盾基线、状态漂移与源信任失败。Server 与桌面工程构建通过；既有依赖审计及分析警告仍存在。

尚未实施：修改型计划的统一桌面预览、其他领域 Action、输出引用与修订传递、PlanApproval、TaskRun/journal、Executor、撤权和重启恢复。当前 DTO 不是安装台账可接受的执行请求，内部规划测试不证明这些能力；TO-02/TO-03 与第一阶段真实 workload 验收目标保持计划中。

### TO-01 生产只读预览与桌面状态核对（2026-10-24）

新增 `POST /api/v1.0/task-plans/preview`，由 `RuntimeRead` 与宿主 `PrivilegedOperations` 功能共同准入。主体只取认证后的 canonical subject，宿主身份取 sing-box 领域查询结果，不接受客户端注入。`RuntimeTaskPreviewService`/DI 使用现有 `SingBoxRuntimeService.QueryAsync` 和 `RuntimeCatalogService.CatalogAsync(refresh:false)`，每次请求隔离安装与目录快照缓存；先验证整图/schema 再读取宿主。只读核对不刷新来源、不请求 RuntimeChangePlan、不提权、不提交安装台账。匿名、无读取角色、缺主体、宿主模式不支持及非法输入在观察前拒绝。

HTTP 和客户端均限制正文大小；无 Content-Length 的流式请求也按实际读取长度限额。唯一当前 Task 输入契约使用精确 camelCase，重复属性和未知字段拒绝；Action 参数记录已移入 Shared，并使用同一明确属性名，不保留旧格式解析。完整计划固定摘要向量已同步为 `37a8af03900dd65231a18d6c8a9f8959c87ca35f1f63bb849ae39f12a9760cc2`。响应 `Cache-Control: no-store`，领域错误只返回稳定问题码，不包含原始宿主诊断。

桌面组件中心新增“核对组件状态”面板，通过 `ITaskPlanPreviewClient` 访问固定预览路由。客户端重新验证会话、用户、宿主、来源上下文、精确动作/参数/依赖和摘要，并拒绝修改型步骤或权限扩张；结果使用三语言说明，不把 DTO、摘要或实现修订暴露为用户决策。没有目录时明确显示“目录未检查”。观察者有读权限即可核对，不授予管理权限。生命周期刷新或会话变化使旧结果失效；此面板的结果不作为安装许可，实际变更仍重新请求领域冻结计划和确认。

验证：规划核心 **41 项**与生产适配/真实本地 HTTP 边界 **16 项**通过；后者覆盖 Query/Get 两个 Helper 动词、整图准入、认证/主体/宿主模式、未知字段、重复属性、有界普通/分块正文、摘要/身份、禁止缓存、诊断隔离和无执行路由。Client Installation 专项 **158 项 PASS**，包含预览客户端篡改/错宿主/错主体/会话切换/响应上限与面板过期结果拒绝。WindowPreviews `--component-center-only` 三语言 × 明暗主题通过，新增 6 组状态核对冲突渲染；已查看日文深色实际图。Server 与桌面构建通过，既有审计/分析警告仍存在，NuGet 审计源不可达时另报 NU1900。

当前 preview 是一次只读观察的冻结计划，现已支持持久保存和按 ID 查询/失效（详见下节），不提供审批或执行，也不构成修改型计划的统一 Preview/Approval/Executor 闭环。尚需完成其他领域 Action、最小持久审批/执行身份、TaskRun/journal、领域幂等/恢复与桌面变更入口替换；不将只读核对扩大为批量组件安装已完成。

### TO-01 持久冻结计划与有效性（2026-10-24）

预览现在先将服务端生成的冻结计划耐久保存，再返回成功。新增 `TaskPlanStore`，使用内容根下 `data/task-plans.db` 的 SQLite WAL/FULL 事务；冻结计划、独立有效性/修订与审计一起提交。记录使用现有 Host Data Protection 的专用 purpose 保护，打开与读取时重新核对保护完整性、计划摘要、ID/主体索引、有效性和审计关系。同一 planId 不能替换内容、主体或创建时间，重复保存相同 envelope 返回原记录。此存储不保存密码或授予任何执行权。

`TaskPlanRecord` 分别暴露冻结 `Plan` 与 `Resolved/Invalidated`、有效性修订、到期时间和失效原因。当前预览有效期为创建后 600 秒；读取或重试保存到期计划时持久记录 `task.plan_expired`，重复保存不能使其重新有效。主体默认最多 256 条，存储最多 4096 条；超过限制返回稳定问题码，不自动清除历史。自动保留策略、计划历史列表和桌面按 ID 恢复仍待接入；不以清空存储解决配额或损坏。

新增 `GET /api/v1.0/task-plans/{planId}` 与 `POST .../{planId}/invalidate`，均要求读取角色并按当前认证主体隔离。跨主体查询/失效统一返回未找到；失效提交精确 digest 和有效性 revision，拒绝过时修订及重复/未知输入字段。失效不修改计划内容、不调用领域服务，不授予取消/恢复领域任务的能力。存储查询在宿主变更功能不可用时仍可达，响应禁止缓存。计划保存失败阻止预览成功返回；密钥不可用、保护记录损坏、缺少既有 schema 或 I/O 失败均失败关闭，不重建空台账。已观察到损坏的实例禁止后续写入。

桌面把计划配额和存储不可用映射为三语言处理说明，清空旧核对结果；只接受明确列出的稳定问题码，不显示原始数据库/宿主诊断。此轮未增加桌面历史恢复或审批按钮，现有组件变更继续走领域冻结计划确认。

验证：规划 **41 项**、生产适配/HTTP **23 项**、持久存储 **30 项**通过。存储专项覆盖独立实例并发、重开、ID 内容冲突、跨主体隔离、过期/保存重试、审计/有效性、配额、缺密钥、保护记录损坏与缺 schema；注入计划写入和有效性写入之后、提交之前的故障，重开确认完整回滚到未写入/原修订，未留下部分计划或审计。HTTP 验证本人读取、他人不可见/不可失效、宿主功能不可用仍可读，以及精确摘要/修订门槛。Client Installation **162 项 PASS**，三语言 × 明暗主题组件中心布局回归通过；Server 与桌面构建通过，既有审计/分析警告仍存在。

尚需交付其他领域 Action、计划级审批与受限执行身份、TaskRun/step journal、执行器及领域操作对账/恢复；TaskRun 当前仅有内部持久排队记录（详见下文），尚无生产执行入口；持久计划或审批记录不能作为 RuntimeChangePlan 授权或通用提权凭据使用。

### sing-box 生命周期变更规划（2026-10-24）

新增生产 `POST /api/v1.0/task-plans/plan`，仅接受结构化 Action intent；要求 RuntimeManage、controller 与 sing-box 安装限制，并同时核对 PrivilegedOperations/AgentInstallation 宿主功能。观察者和安装权限受限到其他组件的 controller 在进入规划服务前拒绝。此接口只生成并持久保存提案，不请求管理员认证、不创建 grant、不调用安装台账或执行器；只读 preview 路由仍不登记这些修改动作。

受信任 Action Catalog 现通过 `ITaskPlanningAction` 明确只读属性、写资源与解析后的输入/权限/风险/重试/领域补偿语义，替换原只读接口，不保留旧接口别名。加入 `runtime.install/update/repair/rollback/uninstall`（sing-box-change-1）；当前同一运行时的多个修改步骤即使声明先后依赖也会在观察前返回资源冲突，不能靠顺序隐式覆盖基线。多个有修订传递关系的修改步骤须等待输出绑定实现，尚不承诺可执行。

`RuntimeTaskChangePlanningService` 复用实际 `SingBoxRuntimeService.QueryAsync/PlanAsync`：先验证全部 intent，读取安装事实，由既有领域计划复核来源、信任、明确版本、平台、配置支持和宿主依赖，再重新查询安装/程序/已发布配置身份；规划期间发生变化即拒绝冻结。任务层额外区分未知/外部所有权、活动任务/恢复、安装身份/修订、修复当前 release、可恢复的回退目标和 SemVer 更新/回退方向；缺少完整领域制品或宿主依赖证据的“允许”结果也拒绝。被拒目录不被当作已观察到的目录修订。

Shared 中新增精确生命周期选择和冻结变更记录。后者包含完整不可变 release/artifact/layout、原 release/实测版本/已发布配置身份、数据保留/删除、停服与可能重启、跨步骤补偿限制及宿主依赖证据。所有字段进入任务摘要；输入和冻结 JSON 使用唯一当前严格 camelCase 编解码，未知执行字段拒绝。规划探测只使用固定非执行 key，冻结记录中不包含该 key 或可复用的领域幂等键；未来执行须依据 approved host/run/step 分配稳定 dispatch key，再由冻结选择与制品构造领域请求，不能重新解析版本。此执行绑定本轮未实现。

权限目标与资源复用安装领域的 `SingBoxInstall/singbox`、`service:SingBox` 和 `runtime:sing-box`；安装服务公开唯一只读 ResourceKeys，编排不另拼一套锁身份。数据删除单独标为 destructive；领域内部事务补偿与跨步骤补偿分别表达，不承诺跨步骤整体回滚。当前不提供 PlanApproval/TaskRun/执行路由；桌面变更按钮继续使用已实现的领域冻结计划确认，尚未切换到新接口。

验证：规划核心 **41 项**、生产适配/真实本地 HTTP **31 项**、持久存储 **30 项**、新增生命周期规划 **25 项**通过。覆盖五种意图、精确制品/摘要、键隔离、资源冲突、无权/错误安装目标/缺宿主能力拒绝、只读路由不能夹带变更、提案持久读取、领域拒绝、不完整依赖/撤销制品、错误宿主、版本方向、回退保留版本、数据删除与配置漂移。生产适配使用真实领域服务和记录 Helper 传输，仅出现 Query→Plan→Query，无执行或提权动词；这些模拟检查不替代实际安装或协议验收。Client Installation **162 项**与组件中心三语言 × 明暗主题布局回归通过，Server/桌面构建通过，既有依赖审计/分析警告仍存在。

### TO-02/TO-03 内部审批与 Queued Run 原子分配（2026-10-24）

新增 Shared 审批/运行/步骤状态契约，以及服务端内部 `RecordApproval`、`StartRun`、按主体读取和撤销的持久基础。生产管理员认证验证器、HTTP 审批/运行接口、派发器和 Helper 执行身份桥接尚未接入；内部 `TaskApprovalIdentity/TaskExecutionIdentity` 只表达将由实时验证器提供的证据，不是客户端请求模型或通用提权凭据。本轮测试使用明确的验证器夹具身份，不能据此宣称真实管理员认证、撤权或后台委托已经验收。

审批绑定 planId/digest、主体/会话、实际宿主、策略修订、审批者身份/认证方式和从冻结步骤推导的精确 capability/target。只接受当前已支持的 Action schema/实现修订，冲突计划、额外目标和错上下文均拒绝。`startBefore` 为创建后至多 120 秒并受计划到期约束；`runAuthorizationExpiresAt` 为独立的至多 600 秒授权期限。审批重试不刷新期限，未消费过期审批持久转为 Expired，撤销保留已消费的 run 关联。记录本身不代表当前身份/策略仍有效，后续每一步和 Helper challenge 仍必须实时复核。

同一个 SQLite 事务消费审批、分配唯一 Queued TaskRun、初始化 Pending 步骤及稳定 host/run/step/execute 幂等键，并记录请求映射和审计。不同请求键并发启动同一审批收敛到同一 run；映射同样持久保存，重开后同键不同请求仍冲突。每个 plan 至多一个完整 run，竞争审批不能重复从头开始；后续恢复需专用剩余步骤审批，尚未实现。撤销后的原键重试只可读回原 run 身份，不产生新的执行权。当前步骤没有 requestDigest 或 domainOperationId，不假装已经提交任何领域操作，也不提前将规划时的 no-op 标为执行成功。

计划、审批、run、请求映射与审计全部受 Data Protection 保护，重开时核对索引和跨记录绑定；显式计划失效与相关审批撤销同事务提交。初始化 schema 也使用原子事务。唯一当前数据库格式升为 `user_version=2`，旧格式被保留并失败关闭，不添加旧解析、空库重置或迁移适配。每计划最多 16 个审批、每 run 最多 64 个请求映射；活动和历史记录均未自动清除。无权主体不能借 ID 查询审批/run，损坏的保护记录阻止继续写入。

验证：规划 41 项、HTTP 32 项、计划存储 30 项、生命周期规划 25 项保持通过，新增内部审批/run 存储 **40 项**通过。新增覆盖单次消费、独立实例并发、不同请求键合并、同键不同请求冲突、跨用户/会话/宿主/策略、过期/撤销、竞争审批、稳定步骤键、重开、旧格式与损坏失败关闭；在审批写入、审批消费、run 写入、计划失效联动撤销的提交前注入故障，确认无部分审批、消费或 run 遗留。真实本地 HTTP 继续验证 `/approve`、`/run` 没有开放。Server 构建通过，既有审计/分析警告仍存在。

下一步须完成生产实时身份验证与审批服务、逐步受限上下文、新鲜授权检查、dispatch intent/journal、领域幂等关联与恢复协调，再开放受控 Executor/HTTP 并接桌面变更预览。既有会话撤销设施主要为进程内状态，当前不承诺跨 Server 重启或访问令牌到期继续特权任务；TO-02/TO-03 仍在实施中，未完成纵向执行验收。

### TO-02 实时审批策略证据（2026-10-24）

已实现并注册 `TaskApprovalPolicyReader`，在宿主只读查询前后使用新 DI scope 读取用户、会话、设备、workspace 和宿主权限。先验证 controller 业务角色与 sing-box 安装限制，再检查当前用户安全版本、活动 session、JWT 会话绑定、controller 设备与未过期租约、System Mode 和宿主能力、OS UID/SID 的 canonical binding。查询期间策略发生变化即拒绝；JWT 角色或已有通用提权记录不能代替上述观察。

策略摘要绑定用户/会话、认证方式、controller 授予时间、当前宿主权限与安装限制；令牌刷新和租约续期不改变摘要，但有效期限独立取当前令牌与租约的较早值。内部审批存储新增必填 `ValidUntil`，启动和执行授权期限均不能超过该证据；同键重试不会因刷新凭据而延长期限。宿主管理员认证结果新增 canonical administrator identity：Windows 使用实际登录 token 的 SID，Linux 使用认证得到的 UID，后续审批不得使用用户输入的显示名替代。

验证：规划 41、HTTP 32、计划存储 30、生命周期规划 25、审批/run 存储 43、实时策略 15 项通过。策略测试使用内存仓储及 OS/宿主观察夹具，验证角色/目标先拒绝、安全版本、冲突 claims、失效 session、租约、宿主能力、身份查询不可用与观察期间撤权；不宣称真实管理员认证或后台执行已验收。Server.Tests 全工程仍有 HEAD 中既有的 7 处 `MihomoRuntimeManager.InstallManagedAsync` 缺失编译错误；本轮服务端及专项工程编译通过。

审批服务尚未把实时策略、冻结计划重新校验和管理员认证组合起来，生产 `/approve`、`/run` 仍未开放。注销撤销 session、后台委托持久化、每步新鲜授权、journal/派发和恢复仍须完成；当前进程重启后没有活动 session 时策略读取拒绝，不承诺自动后台续跑。TO-02/TO-03 不标记完成。
### TO-02 审批组合与冻结计划复核（2026-10-25）

已实现并注册内部 `TaskPlanApprovalService` 与 `RuntimeTaskPlanRevalidator`。审批要求明确确认，先校验实时业务权限，再读取主体隔离且未到期的精确 planId/digest；将冻结 selection 重新交给当前领域规划并比较完整语义。重新观察的磁盘可用字节仅在仍满足相同要求时允许变化，制品、源/信任修订、配置身份、状态、权限、影响及其他依赖语义必须一致，冻结计划本身不被改写。

普通用户需新鲜宿主管理员认证，审批记录使用认证返回的 canonical UID/SID，拒绝缺失或跨平台身份；当前宿主管理员/root 仍须明确确认。认证后再次复核领域计划和实时策略，并检查最后一次宿主观察中的状态、配置、版本与 pending/recovery。计划在认证期间失效时，最终审批事务拒绝提交。没有查询或生成通用提权 grant，也没有安装、下载或服务控制派发。

注销端点现将当前主体匹配的 session 标为 Expired，使内部任务策略不能继续使用该会话；不会按另一个主体的 sid 失效会话。此处不声称所有旧访问令牌在所有普通 API 上即时吊销。

验证：规划 41、HTTP 32、计划存储 30、生命周期规划 25、审批/run 存储 43、实时策略 17、审批组合 20，共 208 项通过；服务端与专项工程构建通过，既有包审计及未使用参数警告保留。组合检查使用真实规划器/存储与注入的身份、认证、领域观察夹具，覆盖未确认、业务拒绝先于管理员认证、摘要错误、认证身份缺失/平台错误、认证期间撤权/制品变化/计划失效、配置漂移、磁盘空间与最终 pending 事务。不是实际 OS 管理员认证或真实安装验收。

生产审批/运行 HTTP、逐步授权与 Helper 身份桥、dispatch journal、领域幂等关联和恢复协调仍未开放，桌面修改型 TaskPlan 接入仍待实施。TO-02/TO-03 和 CC-02 不标记完成。
### 桌面修改型计划接入（2026-10-25）

桌面组件中心现在可将本地 sing-box 草稿转为当前 schema 的修改型 intent 并调用生产规划路由，保留冻结提案和摘要，显示步骤/阻止原因及数据影响。严格验证返回的 actor/host、selection、capability/target、语义摘要，草稿或观察变化会丢弃旧结果。Client Installation 170 项及三语言 × 明暗布局通过，见[桌面预览记录](RelaxKonOS.ComponentCenter.Design.md#桌面修改型冻结预览2026-10-25)。未开放审批/运行 HTTP 或 Executor；本次桌面接入仅证明提案，不证明实际执行、逐步授权或恢复。
### TO-02 派发意图与领域身份绑定（2026-10-25）

内部 `PrepareDispatch` 将当前冻结 selection/release/previous release 转为精确 sing-box 安装领域请求，并使用既有 host/run/step 稳定键；规划 probe key 不进入派发。请求、adapter revision、service/kind 和绑定摘要连同 Dispatching 步骤、Running run 与审计在同一事务提交，之后才允许记录领域 operationId。当前仅允许已准备好的修改步骤，依赖未完成或已有活动步骤时拒绝；没有调度器或外部副作用。

`BindDomainOperation` 核对精确 requestDigest、run revision 与步骤状态；同一个 operationId 重试保持原修订，其他 operationId 拒绝。持久绑定不要求新的执行权限，因为它只记录已发生操作的关联；新的派发准备仍要求同一主体/会话/宿主/策略、未撤销且未到期的消费审批。记录不可作为当前 Helper 执行凭据。重开按冻结计划重建请求摘要并重放审计，拒绝索引、状态、请求或事件顺序不一致。

`InstallationCoordinator.FindByKey` 新增只读查询：复用领域 typed Validate 和 Start 相同的 typed request fingerprint，按主体与原 key 找回原安装台账条目，同键不同 service/kind/request 拒绝。查询不创建任务、不运行恢复、不请求管理员认证或 Helper，Interrupted/失败/成功仍保留原领域状态，不能解释成未执行并换键重试。当前尚未将该查询组合成后台协调器，也没有把领域结果写成步骤终态。

派发 journal 改变受保护 run 格式，当前数据库 `user_version=3`；旧 schema 2 和其他不支持格式均失败关闭并保留，依 API 演进政策不添加迁移或双格式读取。

验证：核心 41、HTTP 32、计划存储 30、生命周期规划 25、审批/run 43、实时策略 17、审批组合 20、派发 journal 25，共 233 项通过。新增并发准备只写一次、精确制品和稳定键、跨主体隔离、重开原请求、请求摘要/修订/operationId 错配、撤销后只保留证据、派发与绑定写入后的提交前故障回滚、受保护记录中伪造摘要/审计事件拒绝、schema 2 拒绝，以及真实安装领域校验/台账的只读原键查询；无实际第三方安装或 Helper 执行。Server/专项工程构建通过，既有包审计警告保留。

仍待：逐步实时授权上下文与 Helper challenge 桥接、调度器、领域终态/实际状态对账、NeedsRecovery 和取消持久状态、剩余步骤恢复、生产审批/运行 HTTP 及桌面统一执行入口。TO-02/TO-03 保持未完成；Dispatching 或 Running 记录不证明任何领域副作用已执行。
### TO-02 只读结果对账与终态证据（2026-10-25）

内部 `RecordReconciliation` 新增领域台账终态、同 operationId 的宿主回执、实际安装身份/修订/目标版本的交叉检查。成功必须同时有领域 Succeeded、宿主成功回执和符合冻结目标的已观测结果，卸载核对 Absent/无 active release/无版本；状态修订必须前进。失败和取消仅在同一回执确认且旧程序/配置基线已恢复时记录相应终态。缺回执、错 operationId、状态漂移、仍有 pending/recovery 或领域 Interrupted 均记录 NeedsRecovery，不推断未执行或换键重试。活动领域轮询不写冗余结果。

步骤结果证据、终态、run 状态及审计同一事务保存。失败/取消/待恢复会停止后续 Pending 步骤并标为 Skipped；仅全部步骤成功或 NoOp 才能将 run 记为 Succeeded（只读步骤 NoOp 调度尚未实现）。消费审批后来撤销不妨碍只读记录已发生结果，但不恢复执行权。同一证据重试保持原修订，矛盾终态证据拒绝，重开时重放审计并重新核验历史证据，终态历史不被后续轮询改写。

已注册 `TaskRunReconciliationService` 与生产 `SingBoxTaskReconciliationDomain`：读取原键对应的安装台账、必要时绑定原 operationId，再仅调用 `SingBoxRuntimeQuery` 读取该操作回执。未找到领域受理时保持原 run；领域仍活动时不作终态推断；宿主观察不可用按未知结果记录 NeedsRecovery。没有调用 Start、RuntimeExecute 或 RuntimeRecover，也没有后台调度器或开放新的 HTTP。

结果证据扩展受保护 run 格式，当前 schema 为 `user_version=4`；旧格式失败关闭、保留原文件，不添加兼容解析或迁移。

验证：任务专项原 233 项加结果对账 24 项，共 257 项通过，Server/专项构建通过。新增覆盖领域 ID/service/修订错配、活动状态、成功回执与目标、撤权后只读记录、重开与终态重试、缺失/错回执/目标/修订/pending/recovery/漂移/Interrupted、已恢复基线的失败和取消、结果提交前注入故障回滚、只读服务缺受理/准确回执查询/终态不重读/宿主不可用。服务协调测试使用注入观察夹具；生产适配器仍仅调用既有只读边界，未执行真实安装或宿主恢复。既有依赖审计和编译警告保留。

尚需逐步实时授权、Helper challenge 桥、真实派发/调度、持久取消、NeedsRecovery 人工恢复与剩余步骤审批、生产审批/运行 HTTP 及桌面统一执行。当前只读对账不是自动恢复，TO-02/TO-03 和 CC-02/CC-03 仍未完成。
### TO-02 逐阶段授权与运行时回调桥（2026-10-25）

新增 `TaskStepAuthorizationService` 和不可序列化的进程内 `TaskStepExecutionAuthorization`：保留登录 principal 的克隆，精确绑定 run/step、领域 operationId、requestDigest 和当前 adapter revision。每次检查要求步骤仍 Running、消费审批未撤销/未到期、计划未失效、业务会话/策略仍匹配、能力目标仍为 sing-box，并重新查询审批者 canonical UID/SID 的宿主管理员成员关系。外部身份读取后再次核对本地策略与耐久修订，撤权期间不能发出新授权。没有生成或读取通用 elevation grant，不保留密码。

`TaskApprovalPolicyReader` 新增仅本地身份/策略读取接口，用于运行时已持有领域 gate 的回调；它不调用 RuntimeQuery 或重新进入 proxy domain gate。租约、令牌和 session 有效性仍实时校验，当前不承诺断线/过期或重启后继续执行。每次阶段有效期至多 10 秒，并受 run 授权和当前令牌/controller 租约约束。

`TaskRuntimeAuthorizationBridge` 在读取既有已发布凭据/证书前后各复核任务授权，将返回期限取 task/session/published 的最小值。`SingBoxRuntimeService.ExecuteTaskAsync` 要求相同 operationId 和完整冻结请求，深拷贝计划防止等待锁期间调用方修改嵌套 release/layout；进入运行时前先检查，Helper 每次 callback 再检查。任务路径不能用既有领域授权替代自己的上下文。现有直接领域执行路径保持原边界，TaskPlan 不会由此产生通用提权权力。

验证：实时策略扩至 23 项，新增逐步授权 18 项，任务专项共 281 项通过。覆盖精确 operation/request、会话/宿主/策略变化、审批者失权、保留 claims 克隆、阶段期限、读取发布材料期间撤权、到期、最终耐久撤销和修改冻结请求在 transport 前拒绝；实际本地 `RuntimeAuthorizationServer/Client` 管道证明有效 challenge 取得短期 grant、撤权后下一 challenge 被拒绝。身份/发布材料使用注入夹具；没有实际 OS 管理员密码认证或特权 Helper 安装。Server/专项构建通过，既有包审计与编译警告保留。

尚未把上下文传入安装台账工作线程，`ExecuteTaskAsync` 还没有由 TaskPlan 调度调用。生产审批/运行 HTTP、安装派发组合、持久取消、NeedsRecovery 人工恢复、只读步骤 NoOp 调度及桌面统一执行仍待完成；TO-02/TO-03 不标记完成。
### TO-02 显式步骤派发与安装工作线程（2026-10-25）

已注册内部 `TaskDispatchService`：从当前登录身份读取实时策略，按主体加载 run、冻结计划与步骤；先用原 key 查询已受理操作，存在时只返回/绑定原 operationId，不重启 worker。尚未受理时重新复核冻结计划和实时策略，持久写入 dispatch intent 后，再调用任务专用安装入口。请求由存储计划重建，不接受客户端替换 options、能力或执行 key。

`InstallationCoordinator.StartTask` 使用现有 typed Validate、资源锁和幂等台账。worker 在进入领域代码前持久绑定原 operationId，并取得 `TaskStepExecutionAuthorization`；通过 `ITaskInstallationProgress` 传给 sing-box，后者仅走 `ExecuteTaskAsync` 与逐阶段回调桥。上下文不是 HTTP 请求字段，不保留密码或 callback channel，普通 Start 不能接管 task-owned key。

安装台账新增必填 `taskOwner`（普通操作显式 null，任务操作为 runId/stepId/requestDigest）；幂等冲突检查包含归属，任务对账核对归属后才绑定 operationId。旧文件缺字段失败关闭并保留，不使用旧格式解析。进程重启时 task-owned 活动操作标 Interrupted，保留归属，不调用普通领域 RecoverAsync；仅后续只读对账判定实际回执/pending/recovery，不能视为未执行或获得后台续跑授权。任务专用恢复审批与协调尚待实现。

验证：新增工作线程检查 9 项、任务专项共 290 项通过，Server/专项构建通过。使用真实安装协调器、台账、任务存储、规划/复核与授权服务，领域执行/身份观察为受控夹具，覆盖业务拒绝先于受理、归属/operationId 先绑定、线程上下文、同键只启动一次、普通 Start 接管拒绝、worker 内新鲜策略、领域结果沿用原台账、重启不走普通恢复、缺归属字段失败关闭并保留。没有下载或运行第三方 sing-box，没有真实特权 Helper 安装，既有包审计/编译警告保留。

此服务仅显式派发单个已批准且就绪步骤；生产审批/运行路由、调度/轮询、只读 NoOp、取消及人工恢复与桌面统一执行尚未接通。TO-02/TO-03、CC-02/CC-03 保持未完成。
### TO-02 只读 NoOp 与按依赖推进（2026-10-25）

新增内部 `TaskRunAdvancementService.AdvanceAsync`，每次明确调用至多推进一个步骤：按冻结依赖选择就绪 Pending；只读动作重新走生产只读观察接口，修改动作走既有任务派发，Dispatching 使用原键，Running 仅进行结果对账。查询 run 本身不触发执行，终态不重新观察或派发。此为受当前登录身份约束的显式推进，不是断线后台调度。

`RecordReadStep` 将新观察、NoOp/失败、run 状态及审计一起提交。只接受同用户/宿主/源身份/输入修订、相同步骤/Action/schema/输入且在 run 创建后生成的证据；观察中无写能力。只有完整冻结只读语义仍一致且结果 NoOp 才完成检查。基线冲突或语义变化记 task.read_check_failed，Pending 后续步骤标 Skipped；检查期间策略变化则不记完成。审批消费、有效期与显式失效仍在事务内验证，当前会话策略在观察前后复核。

重开时验证只读证据、审计顺序和依赖；NoOp 不占用领域 operationId 或 dispatch key。多步 run 只在所有步骤 Succeeded/NoOp 后成功，已完成步骤不会因后续轮询重复执行。只读证据扩展当前存储格式至 `user_version=5`，旧 schema 失败关闭、保留文件，不添加兼容读取/迁移。

验证：新增推进 16 项、任务专项共 306 项通过，Server/专项构建通过。覆盖依赖未完成时拒绝修改派发、真实规划器/存储上的核对→NoOp→受控变更派发→结果对账链、重开、终态不重复、旧确认幂等、错输入修订与未认证主体拒绝、基线变化失败并跳过、读结果提交前故障回滚、观察期间撤权、旧 schema 3/4 拒绝；变更派发和结果观察为受控适配夹具，未执行真实第三方软件或特权 Helper。既有包审计及编译警告保留。

生产审批/运行/推进路由、持久取消和 NeedsRecovery 人工恢复/剩余步骤审批、桌面统一确认执行与真实宿主端到端仍待完成。输出引用及写后修订传递尚未实现，不能把当前固定基线检查当作写后动态引用。TO-02/TO-03 和 CC-02/CC-03 保持实施中。
### TO-02/TO-03 持久取消与领域投递（2026-10-25）

新增内部 `RequestRunCancellation` 和 `TaskRunCancellationService`。先在同一耐久事务保存 cancel request、跳过后续 Pending 步骤和审计，再按 taskOwner/原 key 找回唯一领域操作并请求取消。未派发 Pending 可直接收敛 Cancelled；Dispatching/Running 先保持 Cancelling，台账没有受理记录时不推断已取消，也不由推进服务重发原意图。不可取消阶段或投递竞争保持 Cancelling，后续可重试投递而不增加任务取消事件。

推进服务在取消后只进行原操作对账，不再派发普通步骤。已绑定的活动阶段上下文仍可在 run 授权有效且未撤权时供宿主安全收敛；取消后不能新建任务执行上下文或从原 journal 重新准备派发。取消不是撤销全部收敛权限，任务审批撤销/身份失效仍独立阻止下一阶段授权。

只有原操作的取消回执与已恢复状态一致才记 Cancelled；若操作实际已成功提交并通过对账，取消请求不能把 Succeeded 改写为 Cancelled。失败或未知结果仍按既有对账规则收敛 Failed/NeedsRecovery。终态的晚到取消不改写历史。当前共享取消字段/枚举已存在，存储格式仍为 schema 5，读入时重放 cancel_requested 与后续结果事件并核对取消标记/步骤/run 状态。

验证：新增持久取消 12 项，工作线程扩至 15 项，任务专项共 324 项通过，Server/专项构建通过。覆盖跨主体与修订拒绝、Queued 直接取消、重开和幂等、未明受理保持 Cancelling、停止后续派发、既有阶段可收敛而新上下文拒绝、提交成功优先于晚到取消、提交前故障回滚；真实安装协调器/台账加受控领域夹具验证投递前请求已持久保存、精确原任务投递、不可取消阶段等待、重复投递不追加任务事件、领域取消与恢复基线回执共同决定任务终态。没有实际第三方安装或特权 Helper 取消验收，既有依赖审计/编译警告保留。

尚待：没有领域受理记录的 Dispatching 取消需要专用关闭/恢复协调，当前保持 Cancelling；NeedsRecovery 人工恢复及剩余步骤审批、生产审批/运行/推进/取消路由、桌面统一执行和真实主机端到端也未完成。TO-02/TO-03 和 CC-02/CC-03 保持实施中。
### TO-02 未受理取消的关闭屏障（2026-10-25）

补齐“dispatch intent 已保存、领域尚未受理”的取消关闭。安装协调器的新受理与 `TryCloseUnacceptedTask` 共用 admission gate：先核对原 actor/key/typed request/taskOwner，确认仍无领域记录后，在该 gate 内提交 TaskRun 的 dispatch_cancelled 审计与未受理取消终态。TaskRun 保留原请求摘要，domainOperationId 为空，问题码 task.cancelled_before_acceptance；不伪造领域取消回执。

任务绑定新增受理前的耐久检查，在同一 gate 内、创建领域记录之前验证 run/步骤/审批/计划仍允许受理。因取消关闭而迟到的 StartTask 被拒绝，不创建安装台账或 worker；已存在的相同归属记录仍可幂等读回。锁顺序为 coordinator gate→task store，取消标记先单独持久提交并释放 task store 后才进入 gate，关闭回调不调用 Helper 或身份查询。若初次查询误报缺失但 gate 内发现已受理，则不关闭，而绑定原 operationId 并继续等待领域结果。

关闭提交失败仅回滚关闭事务，原 cancel request 保持 Cancelling，可重开后按原键重试。启动后原取消标记和受理前检查仍阻止新受理，不以重开清除屏障。已经绑定领域 operationId 的未知结果不适用此关闭路径，仍按 NeedsRecovery/领域回执处理。存储记录形状未改变，当前 schema 仍为 5。

验证：新增未受理取消 8 项、工作线程扩至 17 项，任务专项共 334 项通过；Server/专项构建通过。覆盖无受理记录的明确关闭/重开/幂等、关闭提交前故障与原请求保留、重开重试、初次缺失但实际已受理的竞态不误关，以及真实安装协调器在 typed Validate 后/受理前被取消关闭时拒绝迟到受理（无新记录/worker）。使用受控领域夹具，没有真实特权 Helper 安装。既有包审计及编译警告保留。

生产审批/运行/推进/取消接口、NeedsRecovery 人工恢复和剩余步骤审批、统一桌面执行及真实宿主端到端仍待完成。TO-02/TO-03 不标记完成。
### TO-02/TO-03 任务执行 HTTP 接入（2026-10-25）

`ServerPipeline` 现注册独立 `TaskExecutionEndpoints`：

- `POST /api/v1.0/task-plans/{planId}/approve`：严格计划摘要/明确确认/可选管理员账户和密码，要求单个有界 Idempotency-Key；复用审批服务与 login 认证限流。凭据 DTO 不生成包含密码的 record ToString，不持久保存密码。
- `POST /api/v1.0/task-plans/{planId}/run`：严格 approvalId/planDigest 及 Idempotency-Key；`TaskRunStartService` 在首次消费前重新复核冻结计划和实时策略，只创建耐久 Queued run，返回 Accepted/Location，不触发派发。原审批已消费时仅按幂等规则读回原 run。
- `GET /api/v1.0/task-runs/{runId}`：主体隔离、RuntimeRead、no-store，仅历史读取，不推进；宿主功能失效后仍可读取。
- `POST /api/v1.0/task-runs/{runId}/advance`：严格 expectedRevision，推进服务在选择步骤前验证修订，拒绝旧页面推进下一步；派发和对账仍使用已实现的工作线程/授权/journal 边界。
- `POST /api/v1.0/task-runs/{runId}/cancel`：严格 expectedRevision，复用持久取消、原操作投递与未受理关闭屏障，返回当前真实状态，不冒充已经取消。

写路由要求 RuntimeManage、controller/明确 sing-box 安装限制及 PrivilegedOperations/AgentInstallation 宿主功能；服务内仍校验 canonical 用户、会话、controller 租约、策略与精确计划审批。相互冲突的 sub/NameIdentifier 拒绝，主体不接受 body 注入。审批正文最多 16 KiB，其他命令 4 KiB，普通/分块正文均逐块有界；大小写严格，重复/未知/缺字段、错误摘要与修订拒绝。异常响应仅输出稳定 task problem，不返回 Helper 或管理员认证诊断。mutating endpoint 的成功/处理错误均 no-store。

验证：新增执行 HTTP 36 项，任务专项共 370 项通过，Server/专项构建通过。真实回环 ASP.NET 服务覆盖匿名、角色/安装限制/缺主体/双主体、宿主功能、显式确认、类型/幂等键/正文边界、分块超限、凭据诊断隔离、owner/跨 actor、排队不派发、启动幂等/换摘要冲突、GET 不推进、旧修订/未知参数、取消仅记请求、成功回执优先于晚到取消、功能失效下历史可读、审批后制品漂移不消费授权，以及真实 login limiter 在认证前拒绝后续请求。规划/存储/审批与启动服务使用实际实现，身份/管理员认证/派发与领域回执为受控适配夹具；未执行第三方软件或真实特权 Helper 安装。既有包审计和编译警告保留。

此前 planning-only fixture 的无执行路由断言仅对应只注册规划分组的场景，不再用它声称生产执行路由未实现。当前桌面尚未调用新执行接口；人工 NeedsRecovery 恢复及剩余步骤审批、真实主机纵向验收、输出/修订传递与其他运行时仍待完成，TO-02/TO-03/CC-02/CC-03 不标记完成。
### 桌面执行传输客户端（2026-10-25）

桌面组件中心新增当前协议执行客户端，严格绑定会话/服务器/计划，校验审批能力及 run/step 不可变身份，支持带 expectedRevision 的推进和取消；首次历史读取与已跟踪刷新的会话校验分别处理。Client Installation 217 项通过，其中新增客户端检查 47 项（受控 HTTP 传输），无 Helper 实际执行。见[桌面客户端记录](RelaxKonOS.ComponentCenter.Design.md#桌面任务执行客户端2026-10-25)。可见执行界面、运行引用持久化/重开与人工恢复尚未接入，服务端 370 项验证边界保持此前记录。

### TO-03 显式恢复结果核对（2026-10-25）

新增 `POST /api/v1.0/task-runs/{runId}/recheck-recovery`，仅 RuntimeRead 所有者可调用，严格接收 expectedRevision。它读取持久原 dispatch 和归属相同的领域 operation，再调用原 operationId 的只读宿主观察；不依赖旧审批继续有效、不调用管理员认证、不派发/恢复领域事务。宿主不支持变更时仍允许读取恢复证据，观察不可用返回稳定错误且不覆盖原记录；GET/普通桌面轮询仍不触发核对。

`RecordRecoveryReconciliation` 在同一事务追加恢复 witness、`recovery_checked` 审计和新状态修订，保留首次结果，不把相互矛盾的新回执覆盖成原证据。Interrupted 台账仅在同 ID 回执与当前安装/版本/配置事实一致且无 pending/recovery/漂移时可确定结果。失败/取消要求恢复旧基线；成功要求状态修订前进且没有已跳过的下游步骤。其余情况仍为 NeedsRecovery；相同 witness 幂等，最多 32 条不同恢复观察。重开重放原结果与后续恢复审计并核对全部状态；提交前故障回滚三者。不会把原失败后的补偿报告为成功，也不会自动复活被跳过的步骤。

受保护 StoredRun 新增强制恢复证据数组，存储 schema 直接升级为 **6**；schema 5 及更早格式失败关闭并保留原文件，不增加旧格式解析、迁移或清空重建。公开 TaskRun 形状不变，允许 NeedsRecovery 在更新修订中转为有依据的 Succeeded/Failed/Cancelled；客户端持续约束原 run/step/operation/key/session，不允许重新 Queued/Running 或同修订改结果。已核实终态仍不可回退。

桌面统一确认/审批/执行、持久运行引用和重开流程已接入，详见[桌面统一执行](RelaxKonOS.ComponentCenter.Design.md#桌面统一执行与重开权限边界2026-10-25)与[恢复核对界面](RelaxKonOS.ComponentCenter.Design.md#桌面显式恢复结果核对2026-10-25)。当前新增的是只读宿主核对与服务端结果记录，任务专用恢复授权/补偿协调和剩余步骤审批仍待实施；TO-02/TO-03 保持未完成。

验证：`dotnet build Tests/TaskOrchestration/RelaxKonOS.TaskOrchestration.Tests.csproj --no-restore -m:1` 通过；运行同工程 `--no-build --no-restore`，任务专项共 **408 项**通过（对账 53、执行 HTTP 44，其他分组一并回归）。新增证据覆盖原操作错配/缺失、不同用户与双主体、修订/未知字段拒绝、已撤审批与宿主变更功能失效下只读可用、原 Interrupted 回执核对、未明结果审计/幂等、失败/取消基线、配置漂移、下游 Skipped 保护、32 条观察上限、重开完整性和提交故障回滚。回环 HTTP 需在允许本机套接字连接的执行环境运行；安装与回执仍为受控领域适配，没有真实 Helper 恢复。Client Installation **264 项**和中英日 × 明暗主题布局通过，桌面构建通过；保留既有包审计与编译警告。
