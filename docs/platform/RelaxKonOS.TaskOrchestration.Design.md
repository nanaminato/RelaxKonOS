# RelaxKonOS 任务编排、批量执行与组件安装器设计

> 状态：设计提案，尚未实施。
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

## 11. Component Installer / Workloads

组件安装器不是一个新的安装后端，它是 TaskPlan 的图形化生成器。

WorkloadDefinition 可以表达：

```text
workload: proxy-server
  required:
    - sing-box
  optional:
    - nginx
    - certificate-acme
  features:
    - name: public-https
      requires: [nginx, certificate-acme]
    - name: tun
      constraints: [platform capability ...]
```

UI 可以表现为 Windows Features / Visual Studio Workloads 风格：

```text
[✓] Proxy Server
    [✓] sing-box
    [✓] HTTPS certificate support
    [ ] Nginx reverse proxy

[ ] Remote Development
    [ ] OpenSSH
    [ ] Git
    [ ] Docker
```

勾选结果先交给 Planner。Planner 根据当前主机产生 no-op、install、conflict、unsupported 和 approval requirements，再进入统一 Preview。

Workload 不应把具体安装命令写进 UI manifest；它只引用稳定的 runtime/action ID 和约束。

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

### TO-04：Component Installer / Workloads

- workload manifest；
- feature/sub-feature；
- dependencies/conflicts；
- 自动生成 Preview；
- 第一批使用真实服务器初始化场景验收。

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
