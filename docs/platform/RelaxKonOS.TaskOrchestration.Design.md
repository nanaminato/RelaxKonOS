# RelaxKonOS 任务编排、批量执行与组件安装器设计

> 状态：设计提案，尚未实施。
>
> 更新：2026-10-18。
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

只允许不扩大权限和副作用的运行期数据，例如下载进度、领域动作内部事务 ID、健康检查结果和输出引用。

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

TaskRun 保存 step journal、idempotency key 和领域回执。Server 或客户端中断后，恢复流程先 reconcile 当前状态，再决定 continue、retry、compensate 或进入 `NeedsOperatorRecovery`。不得简单从第一个步骤重新执行。

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

为了避免未来 API 重构导致所有 Recipe 失效，建议增加版本化 `Action Catalog`：

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

Recipe、Installer、Agent 都只引用 ActionKind。内部 HTTP endpoint、C# service 和 Helper 协议可以演进，只要对应 Action schema 的兼容规则明确。

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

大体状态：

```text
Draft/Resolved -> WaitingApproval -> Approved -> Running
                                      |            |
                                      |            +-> Succeeded
                                      |            +-> Failed
                                      |            +-> Cancelling -> Cancelled
                                      |            +-> NeedsRecovery
                                      +-> Expired / Revoked
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

验收：同一宿主状态 + 同一输入生成确定计划；计划执行前不能变化。

### TO-02：TaskRun / journal / Executor

- 持久化 TaskRun；
- 资源锁；
- step state；
- idempotency；
- cancel / reconnect / Server restart reconcile；
- 接入一个已有成熟事务领域，例如 ManagedRuntime install/update。

验收：客户端断开不丢状态；Server 重启不会从头盲目重放。

### TO-03：PlanApproval 与批量特权动作

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
  -> ensure sing-box installed
  -> ensure one proxy instance configured
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
