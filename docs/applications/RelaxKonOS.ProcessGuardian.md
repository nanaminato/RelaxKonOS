# RelaxKonOS ProcessGuardian 设计

> 内置进程守护管理器。它统一管理由 RelaxKonOS 声明和守护的后台程序，并以只读/受控方式管理现有 `systemd` 单元和 Windows SCM 服务；不是任务管理器的替代品。
>
> 当前状态：**已实现**独立 Guardian Agent 可执行体、本机认证 IPC、工作负载声明持久化、启动/停止/重启/删除、退出退避、健康检查、审计，以及 Windows/Linux 的服务部署脚本。`RunAs` 已实现为工作负载的声明字段、Server 一次性管理员认证和 Linux Agent 的受控 `runuser` 启动；Windows 跨账户令牌启动、内置 Server/Agent 的服务账户变更、正式安装包、可视化安装向导、日志轮转和完整原生服务管理仍待完成；Server 不会替代 Agent 守护任何用户工作负载。
>
> 当前 Agent 启动时必须由宿主配置 `REMOTEOS_GUARDIAN_SHARED_SECRET`。仓库现提供 Windows/Linux 部署脚本，用于一次性注册 Server 和 Agent 系统服务、生成受 ACL 保护的配置及 IPC 密钥；最终安装包应调用这些脚本，最终用户无需手动把 Agent 配置成服务。**当前尚未有接入客户端或 Server 的成品安装向导。**用户可登记任意位置的现有绝对可执行文件，或填写 Agent `PATH` 中的程序名；工作目录仍必须为绝对路径。Agent 仍拒绝 `cmd`、PowerShell、`sh` 和 `bash` 作为隐式 shell 入口。

> **面向的对象是用户后台工作负载，而非 RelaxKonOS.Server。**例如，自包含 .NET 应用可直接登记发布后的可执行文件；依赖运行时的 .NET 应用可登记绝对路径或 Agent `PATH` 中的 `dotnet` 并将 `MyApp.dll` 作为独立参数；Spring Boot 可登记绝对路径或 Agent `PATH` 中的 `java` 并使用 `-jar`、`app.jar` 等独立参数。路径不再有 Guardian 白名单；实际访问权限由目标运行账户和宿主 OS 决定。RelaxKonOS Server 的健康监控是安装程序创建的受保护基础设施规则，不会出现在用户可编辑的 workload 列表中。

### Windows 正式部署布局

Windows 部署脚本默认采用以下布局。它只注册服务和生成机器配置；发布/安装包必须先将两个 self-contained 发布产物放到对应位置。

```text
C:\Program Files\RelaxKonOS\
├── server\RelaxKonOS.Server.exe
└── guardian\RelaxKonOS.Guardian.Agent.exe

C:\ProgramData\RemoteOS\
├── guardian\guardian.json       # 安装程序生成，ACL 保护
└── workloads\                    # 用户的受守护应用发布目录
```

在管理员 PowerShell 中运行 `deployment\windows\Install-RelaxKonOSServices.ps1` 即使用这套默认布局。若 Server 监听端口不是 `5000`，传入 `-ServerPort <端口>`。framework-dependent .NET 或 Java 可直接登记绝对路径的 `dotnet.exe` / `java.exe` 与应用发布目录。
>
> - 即时进程查看与结束任务：[`RelaxKonOS.TaskManager.md`](./RelaxKonOS.TaskManager.md)
> - 安全与权限提升：[`RelaxKonOS.Security.md`](../platform/RelaxKonOS.Security.md)
> - 内置应用通用约束：[`RelaxKonOS.BuiltInApplication.Conventions.md`](../development/RelaxKonOS.BuiltInApplication.Conventions.md)

---

## 1. 定位和边界

`RemoteProcessGuardian`（应用 ID：`remoteos.processguardian`）提供“定义 → 验证 → 部署 → 启动 → 健康检查 → 自动恢复 → 日志/审计 → 停用”的服务化闭环。它借鉴 PM2 的进程清单、启动恢复、生态配置与实时日志，以及 systemd/Windows 服务的启动类型和依赖模型。PM2 将守护进程清单持久化并在主机重启后恢复，且支持实时及落盘日志。[PM2 Process Management](https://pm2.io/docs/runtime/guide/process-management/) [PM2 Log Management](https://pm2.io/docs/runtime/guide/log-management/)

任务管理器面对“主机当前所有可见进程”，可直接结束；守护管理器面对“已登记的工作负载”，关心所需状态、退出原因、重启预算、依赖和启动恢复。两者必须互相链接，但不得共享可变状态或绕开各自权限。

### 1.1 v1 范围

| 项目 | 说明 |
|---|---|
| Guardian workload | 可执行文件、脚本解释器或受控命令；工作目录、参数、环境引用、`RunAs` 运行账户、启动依赖、重启策略、健康检查、日志策略 |
| 生命周期 | 验证、启用/禁用、启动、优雅停止、强制停止、重启、重载（仅声明支持时）、开机恢复 |
| 可观测性 | 当前/期望状态、PID、退出码、重启次数、CPU/内存、健康结果、事件时间线、stdout/stderr 实时与历史日志 |
| 原生服务 | 列表、详情、启动/停止/重启、启动类型读取；通过适配器管理，而不将服务配置伪装成 Guardian 定义 |
| 安装 | 安装或修复 **RelaxKonOS Guardian Agent**（不是任意第三方应用）；用户工作负载由 Agent 部署和守护，Server 与 Agent 自身作为受保护内置项管理 |

不支持在 v1 中把已有任意进程“接管”为守护对象、编辑系统关键服务、储存运行账户密码、在客户端启动宿主进程，或把 Windows `.bat`/shell 文本直接注册为服务。

---

## 2. 运行模型

为确保 Server 重启或用户登出后仍能守护工作负载，守护功能不能依附在 `RelaxKonOS.Server` 进程内。目标架构是一个独立的本机 `RelaxKonOS.Guardian.Agent`：Ubuntu 上为受限 systemd service，Windows 上为 Windows Service。Server 以仅限本机、相互认证的 IPC 向 Agent 下达结构化命令；Agent 才是子进程父级和日志捕获者。

```text
RemoteProcessGuardian (Client UI)
       │ HTTPS + JWT
RelaxKonOS.Server: Guardian endpoints
       │ 授权 / 验证 / 审计 / 定义仓储
       │ local authenticated IPC
RelaxKonOS.Guardian.Agent
       ├─ Supervisor（子进程、重启、日志、健康检查）
       ├─ systemd adapter (Ubuntu)
       └─ SCM adapter (Windows)
              └─ workload / native service
```

Agent 的身份和 IPC 端点不得被普通用户读取或连接。Agent 只接受来自已验证 Server 的请求，拒绝原始 shell 命令；Server 也只能将通过验证的 `ProcessDefinition` 编译为 `LaunchSpec`。

### 2.1 状态机

```text
Draft → Validated → Disabled ⇄ Starting → Running ⇄ Degraded
                           │       │          │
                           │       └→ Stopping → Stopped
                           └──────────────────→ Failed

Failed -- restart budget available --> Backoff → Starting
Failed -- budget exhausted ---------> CrashLoop
```

- **desired state** 由用户动作或“开机启用”决定；**actual state** 由 Agent 事件决定，不能由 UI 乐观伪造。
- 优雅停止先发送平台适当的终止请求，等待 `StopTimeout`，超时后才允许经确认的强制终止整个子进程树。
- 每次启动记录 generation；同 PID 复用或旧事件不得覆盖新 generation。

### 2.2 守护定义

`ProcessDefinition` 是 Protocol 中版本化、可审计的声明，最小字段如下：

| 组 | 字段 |
|---|---|
| 标识 | 系统生成且不可变的 `Id`、用户可读的 `Name`、显示名/描述本地化键、标签、DefinitionVersion；不记录 Owner |
| 启动 | `ExecutablePath`、结构化 `Arguments[]`、`WorkingDirectory`、`Interpreter`、环境变量（值或 SecretReference）、`RunAs` |
| 可用性 | `EnabledOnBoot`、依赖项、`RestartPolicy`、最大重启次数、窗口期、退避上限、启动/停止超时 |
| 健康 | 无/进程存活、HTTP(S)、TCP；间隔、超时、连续失败阈值、初始宽限期 |
| 日志 | 捕获 stdout/stderr、结构化格式、单文件大小、保留数、保留天数、脱敏规则 |
| 安全 | 保存后的可执行文件绝对路径与存在性、目标账户实际访问权限、能力标记、配置修改所需确认级别 |

参数永远是数组而不是命令字符串，禁止 shell expansion、`cmd.exe /c`、`sh -c` 等隐式解释器。脚本必须明确选择已批准解释器和绝对路径。敏感环境变量只存 `SecretReference`，由 Agent 在启动瞬间从 OS 安全存储解析，API、数据库、日志和 UI 一律只显示占位符。

`Id` 是工作负载的机器标识，用于路由、持久化和审计；创建时自动生成，编辑时不可修改。`Name` 是用户填写、可变更的显示名称，可重复但应保持便于运维人员识别。创建与编辑界面只暴露 `Name`，技术 ID 仅在诊断或审计详情中按需展示。

### 2.3 `RunAs`：简化授权模型

`RunAs` 指定子进程实际使用的宿主 OS 账户，不是 RelaxKonOS 内部角色。创建工作负载时默认填入当前登录用户的规范化宿主身份。旧定义缺失该字段时标记为“需要迁移”：在下一次编辑、启用或启动前，界面必须要求显式选择 `RunAs`，不能悄然继续继承 Guardian 的服务账户，也不能自动改成编辑者。Linux 使用登录名（包括 `root`），Windows 使用规范化的本机/域账户标识；Server 必须先通过 `IIdentityProvider` 解析账户是否存在、已启用且属于当前平台，不能把自由文本直接交给启动器。

授权规则刻意保持为两条，不引入账户白名单、工作负载到账户映射或新的 RBAC：

| 请求者 | 目标 `RunAs` | 是否需要再次输入管理员凭据 |
|---|---|---|
| 任意已登录用户（包括 `root`/宿主管理员） | 自己 | 否 |
| 任意已登录用户（包括 `root`/宿主管理员） | 任意其他有效宿主账户（包括 `root`/`Administrator`） | 是 |

管理员认证对话框默认填入 Linux 的 `root`、Windows 的 `Administrator`；任何跨账户指定都必须在该次提交中输入实际管理员密码。Server 立即用 `IIdentityProvider` 校验该账户、确认其当前为宿主管理员，并只把成功/失败结果用于本次定义变更。密码不写入定义、SQLite、审计、日志、浏览器存储或 Agent IPC，也不缓存为令牌；失败时不泄露账户是否存在。

用户工作负载不设 Owner、路径根或逐工作负载 ACL：任意已登录 RelaxKonOS 用户均可创建、修改、启动、停止、删除任意工作负载，也可停止 RelaxKonOS 自身登记的工作负载。`RunAs` 是唯一的额外认证点；管理员密码只授权这一次跨账户保存，不会在之后的启动、重启或开机恢复时再次索要。

Agent 只接受 Server 已批准、已规范化的目标身份，并在启动前再次检查可执行文件、工作目录、日志目录的实际访问权限。Linux 启动器以受控的补充组、UID/GID 切换后 `exec`；Windows 启动器只能使用 OS 可获得的非交互式主令牌。若平台无法为目标账户安全取得该令牌、账户被禁用，或路径 ACL 不允许访问，则拒绝启动并返回稳定问题码；不得为绕过该限制向用户索取、保存或转发目标账户密码。这里的“可任意指定”指授权策略不另设账户白名单，仍以宿主 OS 的账户状态与执行权限为最终边界。

RelaxKonOS 内置程序（包括 RelaxKonOS.Server 和 Guardian Agent）也支持同一套 `RunAs` 决策，但它们属于受保护内置项：其账户配置保存于安装器管理、ACL 保护的主机配置，而不写入用户工作负载定义；变更后必须显示受影响服务并明确确认重启。为它们指定其他账户时同样需要一次管理员认证。任何用户工作负载都不能借此修改 Server/Agent 的服务定义或取得原生服务控制能力。

---

## 3. 平台适配与安装

| 能力 | Ubuntu | Windows |
|---|---|---|
| Guardian Agent | `remoteos-guardian.service`，由 systemd 在开机启动 | `RemoteOSGuardian` Windows Service，SCM 管理 |
| workload 启动 | Agent 直接 fork/exec；不要求用户 shell | Agent 以结构化 `ProcessStartInfo` 启动，使用 Job Object 管理进程树 |
| 原生服务读取/控制 | `systemctl`/D-Bus 适配器，白名单单元 | `ServiceController`/SCM 适配器，白名单服务 |
| 重启语义 | Agent 统一执行；不依赖单元 `Restart=` | Agent 统一执行；不伪造每个 workload 为 SCM 服务 |
| 安装/修复 | 包管理器安装 Agent unit，`systemctl enable --now` | 签名安装包注册 Agent service；SCM 设置自动启动 |

Windows 的 SCM 支持服务的 `auto`、`demand`、`disabled`、`delayed-auto` 等启动类型和依赖；设计只在原生服务适配器中映射这些含义，不把它们错误映射为普通进程。[sc.exe create 参考](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create)

### 3.1 安装流程

1. 探测 Agent 是否存在、版本/签名、IPC 可达性、服务状态、磁盘、可执行路径和所需权限。
2. 显示安装计划、受影响服务、包来源/签名、数据目录与回滚说明。
3. 用户拥有 `server.guardian.install` 后明确确认；宿主 OS 提权机制运行签名安装包或官方系统包，RelaxKonOS 不请求或保存管理员密码。
4. 启动 Agent，建立本地 IPC，执行自检工作负载，记录安装审计。
5. 若失败，保留诊断和已完成步骤；仅在安装器明确提供安全卸载计划时执行回滚。

运行用户和服务账户仍是安全边界。`RunAs` 依 §2.3 的规则授权，并始终以非交互式后台进程运行：Windows 服务位于 Session 0，不能假定可访问交互桌面；Linux 需处理账户不存在、被禁用和补充组初始化。管理员认证只证明“允许本次指定目标账户”，不替代 OS 对启动令牌、文件 ACL、符号链接和实际执行权限的检查。

---

## 4. UI、接口与数据

### 4.1 信息架构

```text
概览（运行数 / 异常 / 最近事件）
├─ 受守护工作负载（列表、筛选、批量安全操作）
├─ 新建 / 编辑向导（命令、生命周期、健康、日志、审查）
├─ 原生服务（只读默认；按许可控制）
├─ 日志与事件
└─ Agent（状态、安装/修复、版本、诊断）
```

编辑向导的最后一步是不可绕过的“审查”：显示规范化后的可执行路径、每个参数、可访问路径、端口健康检查、重启预算、秘密引用数量，以及 `RunAs` 的请求账户、实际账户和权限检查结果。只要把 `RunAs` 改为非当前登录身份，审查页就在提交前显示管理员账户/密码的一次性认证框（默认 `root` 或 `Administrator`）；认证框不支持“记住密码”。启动/停止/重启的单项操作可立即执行；批量操作和强制停止要求展示影响列表。

### 4.2 服务端契约（拟定）

Protocol 放于 `Shared/RelaxKonOS.Protocol/ProcessGuardian/`，仅以 DTO、路由和问题码暴露。拟定端点：

| 方法 | 路由 | 权限 |
|---|---|---|
| GET | `/api/v1.0/guardian/status` | `server.guardian.read` |
| GET/POST | `/api/v1.0/guardian/workloads` | read/manage |
| GET/PATCH/DELETE | `/api/v1.0/guardian/workloads/{id}` | read/manage |
| POST | `/api/v1.0/guardian/workloads/{id}/{start|stop|restart|reload}` | `server.guardian.manage` |
| GET | `/api/v1.0/guardian/workloads/{id}/logs` | read |
| GET | `/api/v1.0/guardian/services` | `server.services.read` |
| POST | `/api/v1.0/guardian/services/{id}/{action}` | `server.services.manage` |
| POST | `/api/v1.0/guardian/agent/installation/{plan|execute}` | `server.guardian.install` |

创建或更新定义时，`RunAs` 是持久化字段；任何用户为其他账户指定 `RunAs` 时，POST/PATCH 都额外携带一次性的管理员认证数据。认证错误统一返回 `guardian.run_as_admin_authentication_failed`，缺少认证返回 `guardian.run_as_admin_authentication_required`；账户无效、平台无法启动或路径无权访问分别使用不同问题码，不能把密码或账户探测细节返回给客户端。

`IProcessGuardianService`（Server）和 `IGuardianAgentClient`（IPC）实现两层边界。当前实现以受共享机密认证的本机 named pipe（Unix 上由 .NET 映射为本机 socket）传递一行 JSON 请求/响应；`GuardianAgent:SharedSecret` 必须由受保护的宿主配置注入，Agent 从 `REMOTEOS_GUARDIAN_SHARED_SECRET` 读取，绝不写入仓储或 HTTP DTO。调用超时、取消、断线和幂等键必须贯穿两层；Agent 事件通过 Server 过滤后使用 SignalR 推送。只有日志尾部/增量事件可流式传输，历史日志按游标分页并应用大小限制。

`ProcessDefinition`/`LaunchSpec` 增加已规范化的 `RunAs` 标识及实际生效身份的只读回显。用于任何跨账户指定的管理员账户名和密码是一次性 HTTP 请求数据：只在 Server 的 HTTPS 边界内校验，绝不进入 `ProcessDefinition`、`LaunchSpec`、SQLite、重放快照或 Agent IPC。Agent 不重新解释 UI 权限规则，只验证 IPC 对端、目标账户与平台启动条件；这样 Server 是唯一的授权决策点，Agent 是唯一的进程创建者。

SQLite 是声明、审计和历史摘要的真源；Agent 在本机持有可重放的最小运行快照，以便 Server 不可用时仍按已批准定义恢复。Agent 恢复 Server 连通后上报 generation、实际状态和未上送事件，由 Server 幂等合并。原始日志留在 Agent 的受限目录，按策略滚动；数据库只保存索引、校验和短摘要。

---

## 5. 权限、可靠性和验收

新增权限：

| 权限 | 允许内容 |
|---|---|
| `server.guardian.read` | Agent、工作负载、日志和事件的只读访问 |
| `server.guardian.manage` | 创建/修改定义、启停/重启受守护工作负载、日志策略 |
| `server.guardian.install` | 安装、修复、升级 Guardian Agent |
| `server.services.read/manage` | 现有系统服务的读取/控制；与 Guardian 权限分离 |

- `RunAs` 不新增权限名：只按 §2.3 判定“自己/一次性管理员认证”，不引入账户白名单、Owner 或逐工作负载 ACL。
- 创建、修改 `RunAs`、每次启动结果和实际生效身份均写入审计；管理员认证仅记录“已验证管理员身份”和管理员账户标识，不记录密码或可复用凭据。
- 除 §2.3 所定义、通过安装器受保护配置修改的内置程序 `RunAs` 外，禁止用户用 Guardian 改写 RelaxKonOS 自身、SSH、登录、网络、防火墙、Docker 等受保护服务；其他受保护服务仍需未来显式白名单和专门的高风险策略。
- 工作目录必须是存在的绝对路径。运行文件可填写存在的绝对路径，也可填写不含路径分隔符的程序名（如 `dotnet`、`java`）：Agent 仅从自身服务启动环境的绝对 `PATH` 条目中解析它，解析成功后立即将结果规范化为绝对路径并持久化。不会加载交互式 shell 或用户 profile；Linux 的 `runuser` 启动会继承该环境。因此后续启动不依赖 `PATH` 顺序，也不会因同名程序替换而改变目标。日志路径和健康检查 URL 仍做规范化与访问权限验证。没有 Guardian 路径白名单，符号链接、网络共享、临时目录是否可用由目标账户的 OS 权限及实际启动结果决定。
- 日志需速率限制、大小上限、轮转和敏感值清洗；健康检查不得携带未掩码的 URL 密钥。
- 因配置无效、权限不足、二进制缺失、退出非零、健康检查失败、重启耗尽而失败时，界面显示稳定问题码与下一步建议，审计留完整安全诊断。

实施顺序：先实现 Agent 安装/IPC/status 与只读工作负载；再实现定义校验、启动/停止/重启、日志和状态机；接着实现 §2.3 的 `RunAs` 身份解析、一次性管理员认证、平台启动器和审计；再加重启/健康/开机恢复；最后接入原生服务适配器和 UI 向导。验收必须在 Ubuntu 与 Windows 各验证 Agent 重启、主机重启、崩溃退避、进程树清理、日志轮转、Server 暂时不可用、权限拒绝、秘密脱敏、审计重放，以及以下 `RunAs` 场景：任意用户指定自己、任意用户指定他人但管理员认证成功/失败、账户或路径不可用、受保护 Server/Agent 账户变更需确认重启。
