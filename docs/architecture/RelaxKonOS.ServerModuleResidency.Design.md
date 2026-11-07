# Server 模块闲时卸载与按需加载

> 状态：协调器、三种策略、会话退出触发、驻留契约与运行时依赖租约已落地。Git 与 Firewall 的有限执行程序集已具备真实 ALC 回收，Tailscale 对象图/持久责任跨代验收见 §15；当前模块实况统一见[边界设计 §8/38～41](RelaxKonOS.ServerModuleBoundaries.Design.md#8-实施顺序与跟踪项)。SystemPerformance S1 冻结，Terminal/ModuleHost 阶段验收已交付；完整安装 ABI、Proxy 动态休眠、第三方宿主合作闲时退出和完整多平台故障矩阵仍开放。§7 测量早于当前实现，不能证明生产内存收益；引用回收修复证据见 §13。

已采纳的决策：采用内存休眠与请求唤醒，提供保持加载、闲时延迟释放和退出后立即尝试释放三种策略，以租约保护多用户、长连接、任务及恢复职责；按窄职责拆分 Proxy 共享能力，先以 Git 只读操作验证动态生命周期，再推进 Proxy 拆分。默认采用闲时延迟释放，具体宽限期通过实测确定。模块边界与宿主选择已在[边界调整设计](RelaxKonOS.ServerModuleBoundaries.Design.md)细化，具体契约与项目通过试点冻结；采纳方向不表示已经实施。

## 1. 产品语义

目标是用户不使用管理功能时释放服务端模块资源，需要时自动恢复。用户界面称为“闲时释放内存”或“休眠模块”。这里的卸载指从内存释放管理模块，不删除模块文件、配置、业务数据，也不卸载 Mihomo、Nginx 等受管软件。请求唤醒只加载服务器上已安装、已启用且可信的模块；未安装时返回安装引导，不能因普通请求自动下载软件或提权安装。

退出登录是一次回收检查的触发事件。服务器可能还有其他用户、其他设备、后台任务和对外服务，不能在任意一个用户退出后直接全局卸载。刷新令牌仍有效也不代表模块正在使用；关闭客户端和断网未必发送退出请求，需要独立的闲时计时。

提供以下服务器级策略，并允许管理员逐模块覆盖；普通用户退出不能修改全局策略。

| 策略 | 行为 |
| --- | --- |
| 保持加载 | 首次加载后保持驻留；需要启动预热时另外声明 |
| 闲时延迟释放（默认） | 无活动租约后等待宽限期，例如 5 分钟；具体默认值通过实测确定 |
| 退出后立即尝试释放 | 会话退出后立即检查该会话使用过的模块；有其他使用者则等待最后一个租约释放，再检查其他会话的近期使用和保护条件 |

“立即”表示不加闲时等待，仍需排空请求和检查恢复职责。界面同时显示实际状态、阻止休眠的原因、最近使用时间和最近唤醒耗时。模块目录及状态查询不得为了显示状态唤醒所有模块；真正的业务请求才触发唤醒，管理页轮询状态不刷新业务闲时计时。

## 2. 当前代码依据

- `Startup/ServerModules.cs` 在启动时固定模块集合，依次注册 DI 和映射 Endpoint；`DescribeAsync` 报告依赖与停用阻止项，`ValidateDeactivationAsync` 检查持久工作。
- `Identity/AuthSessionStore.cs` 保存刷新令牌并支持轮换和吊销；不能直接将其中记录数量当成模块使用人数。`SessionValidityService` 与认证端点已有会话过期处理，需要在成功提交退出后接入统一活动通知，不能把刷新轮换当成退出。
- `Startup/Modules/ProxyModule.cs` 合并 Mihomo 管理、订阅、TUN、sing-box 管理和 Squid 网关，并拥有恢复、系统代理守护及到期处理等后台工作。它并非只有 HTTP 表面，不能整体视为随时可释放。
- `Startup/ProxyRuntimeRegistration.cs` 已把运行时发布、共享设置及部分恢复保留在 Core。`DockerProxyResolver` 已通过 `IProxySettingsReader` 读取轻量设置快照（2026-11-03，边界设计 §12）；当前并不是 Docker 通过模块 `Requires` 依赖整个 Proxy。需要处理的是服务所有权和实例引用，而不仅是模块清单。

## 3. 生命周期与并发

安装状态、启用状态和驻留状态分开。已启用但休眠的功能仍可发现，标为“可按需加载”；禁用、不支持、缺失和加载失败各自给出原因。驻留字段已在 `ServerModuleDto.residency` 中升级（`state`、`releasable`、`strategy`、`idleGraceSeconds`、`activeLeases`、`lastUsedAt`、`lastWakeMilliseconds`、`blockedBy`、`failure`、`lastReleasedAt`），未增加旧字段别名或双格式解析；状态查询只读协调器状态，不触发加载。

建议驻留状态为：`Dormant → Loading → Active → Draining → Unloading → Dormant`，失败进入 `Faulted` 并保留失败阶段和实例代次。故障实例有残留引用时禁止创建第二个副作用执行者；确认旧实例隔离或释放后才允许重试。

Core 持有模块协调器、静态路由入口和窄契约。每次业务调用先经过认证、授权、启用状态与宿主限制检查，再原子取得模块租约；不得执行模块代码后才检查权限。并发首次访问合并成一次加载，按依赖拓扑先取得依赖租约，初始化及必要恢复成功后才发布就绪。同一调用的等待取消不取消其他调用共同等待的加载；宿主关闭可取消加载并统一收尾。

租约覆盖 HTTP 请求、流式响应、WebSocket/Hub 连接、后台任务、跨模块调用和恢复操作。HTTP 处理函数返回不等于流已结束；任务离开 HTTP 请求后继续持有自己的租约。依赖实例存活期间保留其依赖租约，禁止长期缓存可卸载模块的裸实例。持久后台工作需在重启后恢复保护状态，不能只依赖内存计数。

回收检查与新租约准入使用同一同步边界。进入排空后拒绝旧代次的新调用，已有调用完成后释放资源；新请求等待排空结束并重新加载，或在尚未开始资源释放时安全撤销排空。不得调用已经 Dispose 的对象。依赖按反向拓扑释放，仅在所有使用者都释放后才可休眠。

唤醒等待有明确时限和有界队列。超时或过载返回稳定的暂不可用错误与重试提示；不能声称“立即”代表零冷启动耗时。业务请求只执行一次，协调器不自动重放可能产生副作用的请求。依赖或初始化失败时释放已取得的资源，限速重试，显示具体原因。

## 4. 真正释放资源的宿主边界

先提供模块自己的可销毁服务容器及任务生命周期，根 DI 仅保存协调器和稳定代理，不将可卸载的实现实例注册成根单例。静态路由调用协调器，避免 Endpoint 委托持续引用模块对象；连接、事件订阅、计时器、日志订阅、缓存和原生句柄均纳入释放清单。动态实例的后台服务由模块宿主显式启停，不能继续依赖根 Host 启动一次的 HostedService 模式。

分两层验收：第一层释放业务对象、缓存和连接，程序集可能仍驻留；第二层才验证代码及其私有依赖的卸载。独立服务容器不证明程序集已卸载，也不保证工作集立即下降。第三方 ModuleHost 的目标是通过退出进程回收资源，但需要 IPC、授权、崩溃恢复和冷启动成本验证；官方模块沿用进程内路径，使用独立程序集和可回收加载上下文验证代码回收。具体边界由[模块边界设计](RelaxKonOS.ServerModuleBoundaries.Design.md)拥有，两条路径均不能仅调用释放方法便宣称完成。

先用低风险、无持久恢复职责的 Git 只读操作验证端到端；Terminal 有长连接与 PTY，需要额外验证断线及会话所有权，Proxy 不适合作为首个热卸载试点。此能力是新的运行时生命周期工程，不通过修改现有启动开关模拟完成，也不要求先实现运行中版本更新。

## 5. 模块边界调整

2026-11-03 已将 Proxy 与共享能力的拆分方向整理为[模块边界与内存回收设计](RelaxKonOS.ServerModuleBoundaries.Design.md)。该文档统一拥有 Core 最小职责、官方独立程序集、第三方 ModuleHost 按需运行、各领域资源所有权、唯一目标 ABI 与 MB 实施项；本文继续拥有驻留状态机、租约和历史实测证据，不维护第二套详细拆分规范。

官方模块保持进程内，第三方继续隔离进程；管理模块休眠不停止外部服务。恢复、安全、授权到期和持久工作须有明确且唯一的责任方，未完成交接前继续保持保护租约。软件安装、更新、卸载仍由[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)负责。

## 6. 实施跟踪与验收

以下条目除 §8 明确标注的已实现部分外均为计划中，不能把采纳方向当作完成记录：

| 编号 | 工作与完成条件 |
| --- | --- |
| RES-01 | 审计所有模块的实例引用、后台任务、活动连接、原生资源及恢复责任；测量相同构建下冷启动、闲时内存与 CPU 基线 |
| RES-02 | 建立租约协调器、单次并发加载、排空和失败隔离；验证跨模块依赖、循环拒绝、取消与宿主关闭 |
| RES-03 | Git 只读试点及静态路由转发，反复加载/释放并证明对象和句柄不会累积；明确仅释放对象还是连同程序集 |
| RES-04 | 会话退出、到期及断线活动策略，多设备/多用户与流式请求保护；策略管理权限和目录查询不唤醒 |
| RES-05 | Proxy 共享服务依赖图及职责转交方案；落实小模块拆分时同步应用文档、任务恢复与受管运行时跟踪项 |
| RES-06 | 驻留契约与各端呈现，记录原因、冷启动耗时、加载失败和重试；Android 细节只在 Android 自有文档维护 |
| RES-07 | Windows/Linux 实机与故障注入：并发唤醒、请求/卸载竞态、连接存续、持续服务、待恢复事务、到期执行与冷启动超时 |

**首轮试点的已落地部分（2026-10-28，§8）**：RES-02 的协调器、单次并发加载、排空与失败隔离；RES-03 的 Git 只读试点、静态路由转发与对象释放；RES-04 的会话退出触发和「查询不唤醒」。当前增加的 Firewall、依赖租约及管理边界分别见 §9/10 与模块管理设计 §12；程序集/句柄累积测量、真实跨模块业务、流式与多设备矩阵仍缺，不能据实现或首轮试点关闭这些条目。

**RES-01 部分进展（2026-10-28）**：同一构建的 Windows 冷启动与闲时基线已实测并记录在 §7；模块实例引用、后台任务、活动连接与原生资源的逐模块审计仍未完成，因此 RES-01 不关闭，Linux 基线也仍然缺失。

测量覆盖首次启动、首次请求、持续访问、最后用户退出、宽限期后、再次唤醒及多轮循环；独立进程方案计算进程树总内存。记录托管堆、进程私有内存/工作集、句柄、线程、CPU 与请求延迟，不以一次 GC 或单个空闲截图证明节省。是否默认开启及适用模块由收益和延迟共同决定。

## 7. 基线测量记录（2026-10-28，Windows）

RES-01 要求「测量相同构建下冷启动、闲时内存与 CPU 基线」。本节记录一次可复现的实测，**只固定当前事实，不证明任何节省**：模块仍按配置+重启生效，没有休眠或按需加载，因此这里的「全关」是拿 8 个模块都停用来给可释放上限量级，不是产品形态。本节测量早于 §8 的运行时切片（当时目录为 8 个模块，随后加入 Tailscale 模块），所以这些数字只能作为「全部管理面都不驻留」的参照上界，不能代表当前默认行为。

### 7.1 环境与方法

| 项 | 值 |
| --- | --- |
| 操作系统 | Windows 11 家庭版 10.0.26200（build 26200） |
| CPU / 内存 | Intel Core Ultra 7 265K，20 核 20 线程 / 31.4 GB |
| 运行时 | .NET 10.0.300 |
| 工作树 | `f2c6b404`，构建时含未提交改动 |
| 发布 | `dotnet publish RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Release -o D:/tmp/rk-publish`（框架依赖、不指定 RID） |
| 运行环境 | `DOTNET_ENVIRONMENT=Production`，显式提供 `Observability:InstanceId/LogDirectory/AuditDatabasePath/AuditHmacKey` 与非默认 `Jwt:Secret`（生产不提供会拒绝启动） |
| 测量点 | 首次 `GET /healthz` 返回 200 的耗时；就绪 5 s 后的工作集/私有字节；再 30–35 s 后的同一组值；期间 CPU 时间差 |
| 重复 | 全开/全关**交错各 3 次**（on-1, off-1, on-2, off-2, on-3, off-3），每次前有 1 次不计入的预热运行；表中给中位数与实测范围 |
| 逐模块 | 6 个单模块停用各 **1 次**，仅作指示值（见 7.4 的警告） |

### 7.2 发布体积

| 组成 | 字节 |
| --- | --- |
| 发布目录合计（49 个顶层条目） | 386,289,465（约 368 MB） |
| `runtimes/`（各 RID 原生资产） | 290,615,296 |
| `Assets/` | 72,302,511 |
| 托管 DLL 合计（49 个文件） | 63,688,688 |
| 其中 `RelaxKonOS.Server.dll` | 4,468,736 |
| 其中 `RelaxKonOS.Protocol.dll` | 2,015,744 |

**模块集合不改变任何字节**：8 个模块都在同一个 `RelaxKonOS.Server.dll` 里，停用只是不注册 DI/路由/后台服务。任何「按模块裁剪发布体积」的说法都要等独立程序集（阶段 3）落地，并且需要配合裁剪（trim）或真正的按需分发；仅改配置永远不会减少文件。

### 7.3 全开与全关

| 指标 | 全开（8 模块） | 全关（8 模块停用） | 差 |
| --- | --- | --- | --- |
| 启动到 `/healthz` 200 | **2993 ms**（2974–2999） | **2622 ms**（2026–2653） | −371 ms（约 −12%） |
| 空闲工作集 | **178.7 MB**（164.5–179.0） | **147.4 MB**（146.8–147.5） | −31.3 MB（约 −17.5%） |
| 空闲私有字节 | **89.5 MB**（77.6–89.7） | **63.0 MB**（62.7–63.0） | −26.5 MB（约 −29.6%） |
| 线程数 | 39–40 | 39–40 | 无可测差异 |
| 进程内已加载模块（DLL） | 221 | 220 | −1 |
| 空闲 35 s 的 CPU 时间 | 0.31 s（0.27–0.33） | 0.33 s（0.28–0.39） | 无差异（低于噪声） |
| 空闲句柄数（单次） | 708 | 699 | −9 |
| `/healthz` 延迟中位数（20 次顺序请求，单次运行） | 26.8 ms（21.7–36.3） | 26.3 ms（20.0–42.8） | 无差异 |

需要注意的点：

- 空闲 CPU 在两种配置下都是**约 0.9% 单核**（0.31–0.33 s / 35 s），差异落在噪声内。模块停用**没有**带来可测的 CPU 下降——这与「后台采样器只在有订阅时工作」一致。
- 空闲 IO 用 `\Process(RelaxKonOS.Server)\IO Data Bytes/sec` 采样三次 5 s，两种配置都是 **0–3 B/s**，即闲时没有持续磁盘活动。
- 工作集与私有字节的差距稳定（全关三次几乎不漂移），但全开有一次掉到 164.5 MB，说明 `WorkingSet` 受 GC/JIT 时机影响，**必须看范围而不是看单点**。

### 7.4 逐模块（指示值，不作为事实）

同一构建、每个配置各 1 次：

| 配置 | 启动 ms | 空闲工作集 MB |
| --- | --- | --- |
| 全开 | 3003 | 178.6 |
| `Terminal` 停用 | 2988 | 178.7 |
| `SystemPerformance` 停用 | 2986 | 162.4 |
| `Docker` 停用 | 2896 | 178.1 |
| `Proxy` 停用 | 2907 | 157.5 |
| `Firewall` 停用 | 2956 | 162.8 |
| `Git` 停用 | 2876 | 161.2 |
| 全关 | 2632 | 145.9 |

**这组数字不能用来给模块排序**，理由是可验证的：四个「明显更低」的模块差值相加约 70 MB，而全关相对全开只有 31 MB，差了一倍多；同时全开自身在重复测量中出现过 164.5 MB 的低点。二者只能说明单次 15 MB 级别的差值落在运行间噪声里，要给每模块定成本必须多种子、多重复（建议 ≥5 次并报中位数与四分位），本节不做这个结论。

### 7.5 结论与边界

已确认（Windows、上述构建）：

1. 8 个模块全开与全关的差距是 **约 31 MB 工作集 / 26 MB 私有字节 / 370 ms 启动**，这是「全部管理面都不驻留」时的上限量级，可作为驻留机制收益的参照上界。
2. 模块开关**不改变**任何发布字节；体积收益只能来自独立程序集 + 裁剪。
3. 闲时 CPU（约 0.9% 单核）与闲时 IO（≈0）在两种配置下相同，因此「停用模块省 CPU」在当前实现里没有依据；收益只在内存。

未验证（不要据本节推断）：

- Linux 实机、System/User Mode、容器化部署的同一组指标；
- 托管堆大小、GC 次数、句柄随时间的增长（只测了单点句柄数）；
- 各模块业务端点的延迟（只测了 `/healthz`，它不经过模块代码）；
- 首次业务请求（JIT 冷路径）的延迟；
- 休眠/唤醒循环后的对象与句柄累积——这属于 RES-03，需要先有动态生命周期才有意义。

## 8. 首个运行时切片（2026-10-28，Windows 开发机）

本节记录已经落地并验证的代码。它与 §7 的基线测量互为参照、不能互相替代：§7 量的是没有驻留机制的构建，本节描述之后新增的机制；两者的测量方法、模块集合与环境都不同。

### 8.1 已实现

- **协调器**（`Residency/ModuleResidencyCoordinator.cs`）：`Dormant → Loading → Active → Draining → Unloading → Dormant`，失败进入 `Faulted` 并保留失败原因、实例代次与退避重试；同一模块的并发首次访问合并为一次加载；最后一个租约结束后按宽限期扫描；回收检查与新租约准入使用同一把锁，排空（`Draining`）期间新请求等待状态转换而不是复活旧实例；宿主关闭取消加载并统一释放。
- **租约与静态路由**：`ModuleLease` 覆盖一次调用（含其 `Task` 完成）；`ModuleServiceProxy<T>` 用 `DispatchProxy` 为模块服务生成租约网关，路由与其它域不再持有可卸载实例。加载失败在模块代码执行前返回稳定错误，HTTP 层映射为 503 + `Retry-After`，`problemCode` 为 `module-load-failed`、`module-wake-timeout` 或 `module-host-stopping`。
- **可销毁服务图**（`Residency/ModuleServiceContainer.cs`）：每次加载新建，释放时反向 Dispose 自己创建的对象；配置、存储工厂、日志等 Core 服务从根容器借用、不参与释放。协调器与网关是根容器里仅有的驻留对象。
- **策略与配置**：服务器级 `ModuleResidency:Strategy`（`KeepLoaded` / `IdleDelayed` / `ReleaseOnLogout`，默认 `IdleDelayed`）、`IdleGraceSeconds`（默认 300，**仍待实测**）、`WakeTimeoutSeconds`（默认 30），可按模块 ID 用 `ModuleResidency:Modules:<id>` 覆盖；未知策略、未知模块、没有任何可释放边界的模块、越界时长都在启动时拒绝，不静默忽略。
- **Git 只读试点**：`relaxkonos.git` 声明 `IServerModuleRuntimeFactory`。根 DI 只注册 `IGitRepositoryService` / `IGitBuildService` 的租约网关与部署端口网关，`HostGitCli`、`LocalGitRepositoryService`、`GitBuildService` 在运行时容器内创建，释放即 Dispose。仍有构建在跑时 `CanUnloadAsync` 返回原因，模块保持 `Active` 并稍后重试，不静默取消工作；部署端口在 Git 不可用时返回 `Unavailable`，不退化成「找不到构建」。
- **会话退出触发**：成功提交登出后按 JWT `sid` 通知协调器；只考虑该会话用过的模块，并且仍要等最后一个租约结束，不根据单个用户退出做全局决定。
- **驻留契约**：`ServerModuleDto.residency` 暴露状态、策略、宽限期、活动租约、最近使用、最近唤醒耗时、阻止原因与失败原因；未声明释放边界的模块显示 `resident` 而不是 `dormant`，禁用模块没有驻留对象。

### 8.2 验证

`Tests/ServerModules` **208 项**通过，本轮新增 33 项：

- 默认策略、默认宽限期与逐模块覆盖；未知策略、未知模块、未知配置项、非可释放模块被拒绝。
- 并发首次访问只加载一次；有租约时不被回收；最后一个租约 + 宽限期后释放且只 Dispose 一次；再次请求以新代次唤醒并记录唤醒耗时。
- 运行时报告「有未完成工作」时保留模块并给出原因；加载失败进入 `Faulted`、同步跨域调用 fail-closed 而不长时间阻塞；唤醒超时返回稳定错误且不取消其它调用共享的加载；`KeepLoaded` 从不释放；登出只释放该会话用过且最后一个租约已结束的模块。
- 真实回环 HTTP（只启用 Git 的宿主）：启动时 `dormant`，`GET /git/repositories` 触发加载并 200，空闲后回到 `dormant`，再次请求以代次 2 唤醒。
- 根容器中不存在 `GitBuildService`、`LocalGitRepositoryService`、`HostGitCli`（对比注册描述符），证明释放不是改配置模拟。

`RelaxKonOS.Server.Tests` 的 `--alias-only`（70 项）、`--session-valid-only`、`--deployment-definition-only` 通过。真实 Server 进程（Development、Git 在内的默认模块集合）启动日志输出 `Server module residency: strategy=IdleDelayed, grace=300s, wakeTimeout=30s, perModule=(none)`；`/healthz` 200，`/server/modules`、`/git/repositories`、`/system/performance/info` 仍为 401，未认证请求不会唤醒模块。

### 8.3 未完成（不得据此宣布 RES-02/03/04 关闭）

- **只释放对象图，不卸载程序集**；工作集、句柄、线程、GC 与多轮唤醒后的累积尚未测量，也没有证明内存收益（RES-03 的测量部分）。
- 运行时依赖租约已补入 §10；当前官方可释放模块尚无相互硬依赖，真实业务组合与跨模块故障矩阵仍未验收，不能以合成图测试关闭此项。
- 通用流及异步序列边界已补入 §11；WebSocket/Hub 与后台任务尚未完整接入。Git 构建已按 §12 接入持续工作租约，并保留 CanUnload 防御检查。连接存续、任务恢复与领域竞态仍属于 RES-04/07 的剩余验收。
- 桌面与 Android 已呈现模块事实并接入管理入口，完整交互与设备验收仍缺；当前事实由模块管理设计 §12 和 Android 自有文档维护。
- Proxy 共享能力拆分、独立进程宿主与完整多平台故障矩阵尚未交付（RES-05/07），已有协调器检查不能代替这些领域验收。
- 唤醒已具备每模块/全局等待及并行初始化上限；当前配置和依赖等待计数规则见 §10 与模块管理设计 §12。

## 管理入口与协议关联

驻留状态的跨端呈现、策略变更、手动唤醒/尝试休眠的管理计划，以及与启停/安装的区别，见[服务端模块管理完整设计](RelaxKonOS.ServerModuleManagement.Design.md)。管理入口与当前写入边界已接入，详见该设计 §12；正常业务请求自动唤醒，不要求先调用管理接口。RES 仍按本节列出的剩余验收逐项关闭。

## Core 管理驻留动作

组件中心的 Wake/TrySleep 通过 Core 冻结计划、显式管理员授权和持久操作回执接入，当前对象释放边界为 Git 与 Firewall。尝试释放调用与闲时扫描相同的租约/排空/CanUnload 边界；活动工作会阻止预览，提交后竞态仍由协调器阻止，不提供强制终止。手动唤醒不执行原业务调用，不安装软件；受理后客户端断开只取消等待。宿主中断的驻留操作重启后保留失败诊断，不自动重放。多模块回执逐项记录结果，不能把部分完成当作集合回滚。

驻留管理专项包含幂等、活动租约保护、对象释放与中断意图恢复；当前验证数量、桌面及 Android 边界见服务端模块管理设计 §12。RES 各项原生平台、长连接及资源循环门槛不因此关闭，除 Git 与下节 Firewall 外，其他模块保持常驻。

## 9. Firewall 请求对象边界

Firewall 根容器仅保存租约代理，当前平台服务在独立 collectible 执行程序集的代次中创建，所有业务 Task 的完整执行期间持有租约。认证、部署能力及变更提权检查先于平台服务调用；管理目录不加载平台服务。原实现没有领域后台任务、长连接、计时器或持久恢复责任，跨域不存在裸服务引用。完整职责审计见 [Firewall 应用文档](../applications/RelaxKonOS.Firewall.md#架构边界)。

释放销毁平台 facade 的对象图并请求本代 ALC 卸载，Linux 静态解析正则随执行程序集进入可回收上下文；Core 特权传输、系统 UFW/Windows Firewall 和所有规则保留。2026-11-05 Firewall-F1 的真实回收、失败异常快照及4代预算证据由边界设计 §41 拥有，不能据此声称工作集或私有内存下降；默认宽限期仍须随资源循环测量评估。

模块运行时的释放失败会传给协调器，并保留失败对象。其余独立资源继续清理，但原代次进入 recovery-blocked 隔离；不能显示 Dormant、重新加载第二个执行者，或再次自动执行已部分完成的 Dispose。此规则同时适用于 Git 和 Firewall，不能只以伪造运行时的 Dispose 失败测试代替真实容器传播检查。

## 10. 运行时依赖保护

宿主从同一已启用模块目录的 `Requires` 冻结运行时依赖图，不从业务请求接收依赖、类型名或程序集路径。依赖必须已启用；启用计划仍要求用户确认整个精确集合。协调器在任何工厂执行前拒绝重复运行时、未知依赖及环，图上限为 128 个运行时。

父运行时初始化前先按依赖拓扑取得直接依赖的租约；这些租约属于运行时实例，覆盖初始化、闲时存续及资源释放，不随最初 HTTP 请求完成而归还。共享依赖只加载一个代次，每个直接使用方各持有一个保护租约，因此目录中的租约数包含实例依赖保护。父实例成功释放后反向归还；取得依赖或工厂初始化失败时回收先前取得的租约。父实例释放或初始化清理失败时保留原执行者及依赖租约，隔离至宿主恢复，禁止在失败对象下面销毁依赖。

父加载等待依赖期间不占工厂初始化并行名额；`MaxParallelLoads=1` 也能完成依赖链。内部依赖等待仍受目标模块 `MaxWaitingPerModule` 限制，但不重复占用当前业务等待者的 `MaxWaitingTotal` 名额，内部等待总量同时受冻结图上限约束。单调用取消只撤销其业务等待，已共享的父/依赖加载继续；成功但无人持有业务租约的实例可以正常进入闲时释放。同步 TryAcquire 的自身超时会撤销该等待，不能在之后悄悄取得遗失租约。

常驻模块所需的可释放模块及其传递依赖会被宿主固定保护；闲时策略不能覆盖该保护，目录返回 `releasable=false` 和 `module-resident-dependent`。正常关闭按反向依赖拓扑释放实例；仍有活动租约、正在排空/释放或失败使用方的依赖不会被强行销毁。宿主会取消共享初始化，对加载与闲时扫描的收尾等待分别设置 30 秒上限，超时保留诊断；这不表示所有领域资源的 Dispose 均已有有界关闭验收。

当前专项使用合成依赖链和菱形图验证初始化次序、共享代次、实例寿命保护、部分失败回收、取消、释放失败隔离、常驻使用方保护和逆序关闭。Git/Firewall 当前没有相互硬依赖，其他真实领域组合、持续任务和长连接仍需各自接入及验收，RES-02/04/07 不因此关闭。

## 11. 流与异步序列的所有权

模块代理支持声明为 `Stream`、`Task<Stream>` 或 `ValueTask<Stream>` 的返回口。方法完成后，租约转交给包装流；必须在底层流清理完成且所有已开始的读写结束后才归还，不能以 Endpoint 委托返回或一次 Read 返回作为释放依据。关闭后拒绝新读写，重复关闭不重放底层 Dispose。调用方仍须遵守 .NET Stream 的显式关闭责任；HTTP `Results.Stream` 的响应清理会承担这一责任。

直接声明的 `IAsyncEnumerable<T>` 延迟到枚举时加载运行时，每次显式枚举拥有独立租约与实现调用。枚举持有的令牌合并方法调用和枚举取消；自然结束、提前退出或取消均先关闭实现枚举器再归还租约。只创建或丢弃未枚举的序列不加载模块。序列元素仍须为物化值，不能借嵌套对象返回裸模块实例或未声明资源。

底层流或枚举器清理失败会保留失败资源、原运行时和其依赖保护，标记 `module-recovery-blocked`，禁止新代次。已开始的流读写在关闭返回后仍未结束时，继续保护原运行时。代理拒绝无法转交所有权的已知契约，包括具体 Stream 子类、Task 包装的异步序列、原始 IResult、延迟同步枚举/查询、裸 object 及 ref/out 返回口；这些路径应改为明确的所有权边界，不能自动回退到原始结果。

当前验证使用合成模块服务，并经过真实 HTTP 响应检查正常传输与客户端中断；不代表 Terminal/PTY、Hub/WebSocket 或所有脱离请求的持久任务已接入；Git 构建的后台边界见 §12。Git 与 Firewall 当前服务口仍返回物化结果。长连接及后台任务必须在各自接入时显式证明完整寿命和收尾责任，RES-04/07 不据此关闭。

## 12. Git 构建后台职责

模块私有的 `IModuleWorkLeaseSource` 绑定具体运行时实例；运行中的方法必须在发布后台任务前取得独立工作租约。取得过程只接受当前 Active 代次，不加载模块，也不能为已释放旧实例借用新代次。工作租约包含独立的宿主停止令牌，不继承 HTTP 的取消或退出。

GitBuildService 在持久化 queued 记录前取得该租约；原请求结束后，构建、终态记录和操作自有镜像清理继续受保护。重复幂等键只返回原记录，不分配第二个工作租约或重跑命令。显式取消持续保护到清理结束；宿主停止则记录 interrupted，与用户取消区分。台账不可读时实际 Git 运行时的 CanUnload 返回恢复阻止；无法持久确认终态或无法收尾命令进程时保留隔离，不以重新加载实例假装清洁。

宿主取消回调在准入锁外执行，关闭期间禁止新的工作租约，并对已有后台工作的收尾等待设置 30 秒上限。仍未结束的工作以及已隔离运行时不会被强行释放；它们的依赖保护继续保留。此等待与初始化/闲时扫描的等待分别计时，不意味着所有领域关闭均可在 30 秒内完成。

Git 命令端口保留结构化文件名、参数、环境与超时规则，取消或输出处理失败后会终止并等待 CLI 子进程退出，无法确认时要求恢复。Windows/Linux 已用真实测试子进程验证取消等待；Linux 另用真实 GitBuildService 和受控 Docker/Git 命令执行器覆盖幂等、HTTP 断连、取消清理、宿主中断和台账写入失败。这不证明远端 BuildKit 的真实取消、镜像补偿、容器或宿主崩溃恢复已经完成；Git 停用已通过独立只读门禁检查持久台账及当前清理租约；中断或进程清理未确认仍保持阻止，未知执行责任的受控关闭流程尚未交付。


## 13. 休眠内存引用审计与首批修复（2026-11-03）

### 实际边界

当前 Git、Firewall、Tailscale 声明了 `IServerModuleResidency`，由模块容器拥有请求服务；休眠释放对象图。内置实现仍编译在 `RelaxKonOS.Server.dll`，并没有可单独卸载的模块 DLL。`InstalledServerModule` 的进程内包另有独立加载上下文，但不是 collectible，并在启动时把服务/Endpoint 注册到根容器；改变 `isCollectible` 或调用 `Unload()` 不能消除这些长寿命引用。隔离包已有 `ModuleHostSupervisor` 和独立进程，但当前按 Server 生命周期启动/停止，尚未接入空闲退出与请求重启。不能把“已有隔离宿主”写成“进程休眠已经完成”。

文件保留在磁盘、程序集代码驻留、托管对象可达、GC 堆已提交空间和操作系统工作集是不同指标。成功释放对象不会保证 RSS 立即下降；生产休眠路径不调用 `GC.Collect`、工作集裁剪或停止被管理的软件。真正的程序集卸载需要可回收上下文、稳定契约以及对所有外部强引用的清理，参见 [.NET 官方卸载文档](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)。

### 已修复的引用

1. 同步完成的 `LoadCoreAsync` 会先把 `entry.Load` 清空，但调用方随后又把完成任务写回；`Task<IServerModuleRuntime>` 的结果继续持有整代运行时。现仅保存尚未完成的加载任务，同步工厂休眠后不再由协调器持有旧代次。
2. `ModuleLease.Dispose` 原来只归还计数，仍持有 Runtime。现在同时清空运行时引用；调用者即使保留完成的请求任务或已释放租约，也不会因此保留模块对象图。重复释放仍无副作用，释放后的 Runtime 访问报已释放。
3. `ModuleServiceContainer` 释放时清空工厂闭包，包括尚未解析的服务工厂所捕获的缓存/对象。资源销毁失败仍进入原隔离路径，不丢弃失败资源的恢复责任。
4. `ModuleLeasedStream` 在成功关闭且所有在途 IO 完成后，清空底层流引用，避免已关闭的包装流继续保留缓冲和模块对象。关闭失败仍保留隔离资源；在途 IO、租约移交及故障阻止重新唤醒语义不变。

### 验证和数值范围

新增 `ModuleMemoryRetentionChecks`；命令 `dotnet run --project Tests/ServerModules/RelaxKonOS.ServerModules.Tests.csproj -c Release -- --module-residency-checks` 同时运行引用回收、Firewall、依赖、流/HTTP 和后台工作测试，并已接入默认模块测试入口。Windows Release 94 项、Ubuntu 26.04 x64 framework-dependent .NET 10 发布程序 101 项全部通过；Linux 测试进程设置 `DOTNET_RUNTIME_ID=linux-x64`，包括真实测试子进程取消及实际 GitBuildService 的持久台账/清理测试。测试在独立目录执行，未替换生产 Server。

修复前新增检查在“休眠运行时不能被完成任务/释放租约保留”处失败。修复后保留调用任务、容器或包装流进行弱引用检查均通过；24 代同步工厂运行时各持有 2 MiB 合成载荷，休眠后存活代次为 0。测试专用 GC 后，Windows 托管存活字节从 248992 到 262472，Linux 从 274008 到 287488。差值包含测试元数据等开销；这是对象可回收验证，不是生产节省 48 MiB 或真实模块常驻内存基准，也没有测得业务吞吐、长期 RSS 或冷启动收益。证据在 `.artifacts/module-memory-20261103/{baseline,fixed-windows,fixed-linux,publish-linux}.log`。

### 审计结果的设计归属

本轮审计确认：Git/Firewall/Tailscale 已有对象边界；Proxy、Docker、Certificate、WebServer 仍有根服务和持续职责；SystemPerformance 的有界历史/静态缓存及 Terminal 的 PTY/可重连会话不能直接按请求结束释放；官方安装包和第三方宿主分别受到根注册和进程常驻限制。目标责任表、ABI、迁移顺序和验收条件已提升为[模块边界与内存回收设计](RelaxKonOS.ServerModuleBoundaries.Design.md)，MB-01～MB-08 均待实施。此前合成回收结果不关闭真实业务内存测量、Proxy 职责迁移或代码/进程卸载验收。

2026-11-03 后续：Git CLI/构建执行器已移入可回收的独立程序集，原模块租约继续拥有业务调用和后台责任；真实 ALC 循环验证、打包边界及未完成项由[边界设计 §10](RelaxKonOS.ServerModuleBoundaries.Design.md#10-git-执行切片实施2026-11-03mb-0203-部分)拥有。§13 的合成对象测试仍是原范围，不替代后续代码回收记录。

2026-11-03 仓库后续：Git 仓库全部 partial 实现已移入同一可回收程序集，以窄存储/身份/文件端口保持 Host 权威；真实仓库/凭据/控制台后的 100 轮代码回收与保留完成请求验证通过，详见[边界设计 §11](RelaxKonOS.ServerModuleBoundaries.Design.md#11-git-仓库实现与-host-端口2026-11-03mb-0203-后续)。这不扩大真实 BuildKit、生产内存或其他模块的验收范围。

2026-11-03 Proxy 后续：共享读取与设置写入/系统代理对账接口已分离，Host 平台原语不再引用管理类；Proxy 尚未取得动态释放边界，安全/恢复 worker 的所有权和执行时机未改变。详见[边界设计 §12](RelaxKonOS.ServerModuleBoundaries.Design.md#12-proxy-共享读取与写入安全边界2026-11-03mb-04-部分)。

2026-11-03 MB-04 续核：到期 worker 的真实异步释放检查已补跑 Linux；系统代理 guard 对账已与设置 writer/控制器客户端分离，仍使用原发布 gate 与身份核验。TUN marker 恢复及发布补偿保持原单一责任方和根引用，Proxy 继续 resident，不声明代码回收或生产内存收益；详见[边界设计 §14](RelaxKonOS.ServerModuleBoundaries.Design.md#14-系统代理-guard-窄执行边界与安全责任审计2026-11-03mb-04-部分)。

2026-11-03 MB-04 TUN 续核：运行控制已脱离 settings writer，后者按调用创建；安全 owner、marker、两级锁和 Core 发布恢复仍保留。验证覆盖生产执行器的启停/补偿/恢复与竞争夹具，不扩大为实机网络事务、代码回收或生产内存收益。当前事实见[边界设计 §15](RelaxKonOS.ServerModuleBoundaries.Design.md#15-tun-运行控制与设置写入执行边界2026-11-03mb-04-部分)。

2026-11-03 MB-04 发布恢复续核：Core worker 改为每轮临时 scope 与按 pending 类型选择执行，原补偿/重试和 publication gate 保留；profile 与订阅后台图及独立根引用审计见[边界设计 §16](RelaxKonOS.ServerModuleBoundaries.Design.md#16-发布恢复临时作用域与-profile-执行图审计2026-11-03mb-04-部分)。这不构成整个 Proxy 回收、后台工作租约或生产内存收益验收，Proxy 仍 resident。

2026-11-03 MB-04 订阅续核：排队订阅已具有独立后台 scope，Proxy 操作台账跟踪完整任务并在停机排空，scope 清理或终态确认失败保留责任；这不是驻留工作租约，Proxy 继续 resident，详见[边界设计 §17](RelaxKonOS.ServerModuleBoundaries.Design.md#17-订阅后台任务独立作用域与完整寿命2026-11-03mb-04-部分)。

2026-11-03 MB-04 任务责任续核：禁用门禁联合只读检查内存执行/清理 owner、持久 operation ledger 与原 publication pending；未知责任拒绝新任务，不据只读快照新增休眠许可。持续安全责任交接与租约仍开放，详见[边界设计 §18](RelaxKonOS.ServerModuleBoundaries.Design.md#18-proxy-任务准入停机与禁用责任门禁2026-11-03mb-04-部分)。

2026-11-03 Proxy 常驻边界收尾：管理排队工作统一独立 async scope；Core 唯一持有 guard、代次绑定 TUN 恢复、sing-box 到期与持续发布扫描，根操作台账只保留活动/故障 owner，历史按需读取。只读禁用检查覆盖上述持久责任。当前实现与验证限制以[边界设计 §19](RelaxKonOS.ServerModuleBoundaries.Design.md#19-proxy-常驻责任与完整执行寿命收尾2026-11-03)为准。Proxy 继续 resident；未新增驻留工作租约、collectible ALC 或生产内存收益证据，RES-05 不关闭。

2026-11-04 MB-04：Proxy 使用同一 coordinator 的 resident work owner，后台租约覆盖 queued 前准入到业务/审计/scope 清理/终态确认，失败隔离同一 owner。目录继续 resident/不可释放。订阅转换已在独立程序集执行并做 ALC 回收检查，这不等于全模块回收；当前事实与验证环境见[边界设计 §20](RelaxKonOS.ServerModuleBoundaries.Design.md#20-linux-modulehost-目标修复proxy-工作租约与订阅独立代码2026-11-04)。

2026-11-04 代码预算续核：订阅转换的活动及待回收 ALC 采用弱引用合计上限，达到上限时拒绝新转换，自然回收后重新准入，生产不调用 GC。具体行为与限制由[边界设计 §21](RelaxKonOS.ServerModuleBoundaries.Design.md#21-订阅转换待回收代码预算2026-11-04mb-04-部分)拥有；Proxy 仍 resident，不能据此宣布 RES-05 或生产内存验收完成。

2026-11-04 测试机续核：Windows/Ubuntu 26.04 最终驻留专项各 28 项通过，补齐预算与 100 轮回收的 Linux 验证，并新增保留 Host 异常时的失败代码回收检查；契约及实现项目依赖守卫已反向验证。环境、证据与验收限制见[边界设计 §22](RelaxKonOS.ServerModuleBoundaries.Design.md#22-proxy-依赖守卫与测试机回收续核2026-11-04mb-0204-部分)。未进行生产内存测量或真实网络故障验收，RES-05/07 继续开放。

2026-11-04 MB-07：进程图标缓存改为 provider 实例所有，Server 与 Helper 各自有界；每个缓存最多 512 条/8 MiB 完成载荷，销毁及淘汰竞争不允许旧加载重新写回。Windows/Linux 各 15 项缓存检查与原进程/性能专项通过，范围见[边界设计 §23](RelaxKonOS.ServerModuleBoundaries.Design.md#23-进程图标缓存实例所有权与载荷预算2026-11-04mb-07-部分)。SystemPerformance 仍 resident，未接入闲时释放、完整 Hub/采样租约或代码回收，不以缓存预算声称进程树内存收益。

2026-11-04 MB-07 输入续核：desktop 图像实际读取限额及 PNG 目标位图分配前的编码/尺寸门禁已落地，Windows/Linux 新增输入检查各 17/18 项通过，原缓存、进程与采样回归保留。当前事实由[边界设计 §24](RelaxKonOS.ServerModuleBoundaries.Design.md#24-desktop-图像输入与目标位图分配预算2026-11-04mb-07-部分)拥有；目录/主题扫描、索引、原生峰值和整个模块休眠仍未验收。

2026-11-04 MB-07 索引续核：desktop 遍历、文件/总读取、映射/文本及整轮主题/路径探测已增加有界预算，Windows/Linux 新增索引专项 **11/12 项**通过，原输入、缓存、进程、性能和 Linux Proxy 驻留专项通过。详细上限与证据由[边界设计 §25](RelaxKonOS.ServerModuleBoundaries.Design.md#25-desktop-流式扫描与索引工作预算2026-11-04mb-07-部分)拥有；逻辑预算不保证慢 I/O 的时间上限，SystemPerformance 仍未接入完整工作租约、排空和闲时回收，不能据此宣称模块休眠或生产内存收益。

2026-11-04 MB-07 图标工作续核：普通/管理员图标共享有界准入，provider 另有独立执行预算；请求取消及 owner Stop/Dispose 不再让已开始的同步工作脱离资源所有者。Windows/Linux 最终各 19 项工作检查通过，完整行为与关闭范围由[边界设计 §26](RelaxKonOS.ServerModuleBoundaries.Design.md#26-图标并发准入取消与-owner-停机排空2026-11-04mb-07-部分)拥有。此队列未接驻留协调器租约，也不涵盖采样/Hub/整个 Helper 进程树；SystemPerformance 仍 resident，完整闲时释放和生产收益验收继续开放。

2026-11-04 MB-07 租约续核：SystemPerformance 已注册同一协调器的常驻工作 owner；完成准入的图标等待项和在途读取取得真实工作租约，协调器停止能撤销等待但不提前释放已开始的读取。Windows/Linux 各 14 项真实协调器检查、原专项及 Windows 默认模块全量回归最终通过，详见[边界设计 §27](RelaxKonOS.ServerModuleBoundaries.Design.md#27-图标工作接入常驻协调器租约2026-11-04mb-07-部分)。缓存仍归 Host owner，采样/Hub/查询/终止的完整租约和模块释放未完成；目录保持 resident/不可释放，RES 不据此关闭。

2026-11-04 MB-07 进程请求续核：统一当前 `ISystemPerformanceWorkLeaseSource`，查询（含缓存命中）和有效终止请求已接入同一协调器工作保护；Host owner 关闭准入并等待请求完成后清理两套快照与 CPU 基线。Windows/Linux 各 19 项新增检查、原专项及 Windows 默认模块全量回归通过，原请求取消与终止回执规则保留。当前范围见[边界设计 §28](RelaxKonOS.ServerModuleBoundaries.Design.md#28-进程查询终止工作保护与快照-owner-排空2026-11-04mb-07-部分)；采样/Hub、实机中断、整个模块释放和生产收益仍未验收，目录仍 resident。

2026-11-04 MB-07 采样续核：实际 source 读取至同步事件分发已取得真实工作租约，需求代次隔离旧读取，多连接注册不再被短路遗漏，循环实际结束后清理状态。Windows 新增 18 项与原性能专项通过；本轮 Linux 上传/执行被自动审批拒绝，尚未运行。当前范围与证据见[边界设计 §29](RelaxKonOS.ServerModuleBoundaries.Design.md#29-实际采样工作租约与需求代次2026-11-04mb-07-部分)。静态信息读取、REST 等待者完整寿命、连续 Hub/网络发送及整个模块释放仍未覆盖，目录保持 resident，不宣称生产内存收益。

2026-11-04 MB-07 静态信息续核：GetInfoAsync 已取得真实工作租约，独立请求 owner 关闭准入并等待实际源 Task；取消及 Stop 超时不提前释放保护。Windows 驻留检查累计 28 项与原性能专项通过，Linux 本轮未执行。完整行为、证据和限制见[边界设计 §30](RelaxKonOS.ServerModuleBoundaries.Design.md#30-性能静态信息读取的工作保护2026-11-04mb-07-部分)。REST 等待者、Hub 网络发送及整个模块回收继续开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 REST 续核：ReadSnapshotAsync 的缓存命中与等待者均取得真实工作租约，owner 关闭能唤醒纯等待并清理需求/订阅；等待者结束不提前释放仍在采集的独立 tick。Windows/Linux 驻留检查累计各 42 项与原性能专项通过，Windows 默认模块回归通过，详见[边界设计 §31](RelaxKonOS.ServerModuleBoundaries.Design.md#31-rest-快照等待者的完整调用保护2026-11-04mb-07-部分)。Hub 订阅/网络发送、同步历史读取与整个模块释放仍开放，不以调用保护声称生产内存收益。

2026-11-04 MB-07 广播续核：实际 typed Hub 发送已取得真实工作租约，Stop/Dispose 不提前释放非合作发送 Task；最多 4 条排队快照在循环实际结束时清空，不持连续工作租约。Windows/Linux 新增广播各 15 项、性能驻留各 42 项及原专项通过，Windows 默认模块回归与 Linux Proxy28 通过，用户授权后直接复用测试机固定工作目录。行为和验收限制见[边界设计 §32](RelaxKonOS.ServerModuleBoundaries.Design.md#32-hub-广播实际发送的工作寿命2026-11-04mb-07-部分)。连续连接/组成员、同步历史读取、模块释放及生产收益继续开放，目录仍 resident。

2026-11-04 MB-07 连续连接续核：PerformanceHubConnections 取得从 OnConnected 到真实组清理结束的连续工作租约，未订阅和已退订连接也受保护；组操作串行，断线/停止不让晚到 Add 复活需求，未知 Remove 失败保留故障与租约且 Stop 可见。Windows/Linux 最终连接各 25 项、原广播15/性能驻留42及专项通过，Windows 默认模块回归、Linux Proxy28 通过。详见[边界设计 §33](RelaxKonOS.ServerModuleBoundaries.Design.md#33-连续-hub-连接与组成员-owner2026-11-04mb-07-部分)；同步历史读取、整体资源清单、连接压力、真实传输/未知结果恢复和生产内存收益仍开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 只读接口续核：GetLatestAsync/GetHistoryAsync 直接替换原同步接口，空缓存与命中均经真实工作准入；HTTP 历史传递请求取消，公开读取在物化前后核验停止，返回结果不持续持租约。Windows/Linux 性能驻留各 53 项、连接25/广播15与原专项通过，Linux Proxy28 通过；行为和证据见[边界设计 §34](RelaxKonOS.ServerModuleBoundaries.Design.md#34-历史与最新快照的异步读取保护2026-11-04mb-07-部分)。后续 HTTP 序列化/传输、整体资源清单、压力与生产收益仍未验收，目录仍 resident。

2026-11-04 MB-07 HTTP 响应续核：已授权性能/进程响应在实际 IResult.ExecuteAsync 取得独立工作租约，linked RequestAborted 通知请求或协调器取消但不脱离真实执行，finally 恢复原令牌；响应失败不重放业务。Windows/Linux 新增 HTTP 各 15 项、原性能驻留53/连接25/广播15及专项通过，Windows 默认模块和 Linux Proxy28 通过，详见[边界设计 §35](RelaxKonOS.ServerModuleBoundaries.Design.md#35-http-性能响应执行保护2026-11-04mb-07-部分)。不等同连续整个 HTTP 管线或物理送达；整体资源清单、压力、未知结果恢复和生产收益仍开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 连接预算续核：连接 owner 使用 128 个合计名额覆盖 pending、live 和故障责任，超限不排队/不取得租约；取消和 Stop 超时不提前归还，真实清理成功才补位，未知故障继续占用。Windows/Linux 新增预算各 13 项、原专项与 Windows 默认模块回归、Linux Proxy28 均通过，详见[边界设计 §36](RelaxKonOS.ServerModuleBoundaries.Design.md#36-连续-hub-连接的准入数量预算2026-11-04mb-07-部分)。这是 owner 数量预算，不是总内存或整个 SignalR 连接上限；真实压力、故障恢复与生产收益仍开放，目录仍 resident。

2026-11-04 MB-07 管理员图标续核：Helper 返回的 Base64 在解码分配前受 349528 原始字符预算约束，保留 256 KiB 解码后门禁；Windows/Linux 各14项和原专项通过，预建4194304字符输入的实际 facade 拒绝路径线程分配小于256 KiB，避免3 MiB解码数组。行为、证据和测量边界由[边界设计 §37](RelaxKonOS.ServerModuleBoundaries.Design.md#37-管理员图标-base64-解码前预算2026-11-04mb-07-部分)拥有；不包含上游字符串/传输缓冲或生产进程树收益，目录仍 resident。


## 14. 当前模块阶段验收入口（2026-11-04）

MB-01～08 的实际状态、剩余阻塞和整项门槛统一由[模块边界设计 §8/38/39](RelaxKonOS.ServerModuleBoundaries.Design.md#8-实施顺序与跟踪项)维护。MB-05A 后台责任、MB-06A 进程代次和 Terminal-T1 会话/PTY 排空已完成该文所列阶段验收；均保持 resident。SystemPerformance S1 已冻结，仅修阻塞问题。上述结果不代表新增模块动态休眠、完整后代树矩阵或生产内存收益已通过。


2026-11-05：MB-06B 与 Terminal-T2 当前进程树阶段验收见[边界设计 §40](RelaxKonOS.ServerModuleBoundaries.Design.md#40-mb-06b-与-terminal-t2原生进程树验收2026-11-05)。Windows 原生 Job/ConPTY、Linux 专用 ModuleHost 身份、本地/永久降权 Helper 树及 EPERM 未知责任已实测；新增身份仍 resident，不启用闲时 kill/重启。Windows 安装 S4U、宿主崩溃和正式内存收益继续开放；本地 Terminal 单会话 .NET worker 的额外成本与点样亦由该节拥有。


## 15. Firewall 独立代码与 Tailscale 对象代次（2026-11-05）

[边界设计 §41](RelaxKonOS.ServerModuleBoundaries.Design.md#41-firewall-f1-独立代码与-tailscale-ts-01-对象代次2026-11-05)拥有本轮验收台账。Firewall 独立 Contracts/执行代码和窄 Core 端口、保留完成/失败 Task 的真实 ALC 回收已验证；Tailscale 当前三代理真实对象图及原持久操作跨代不重放已验证，失败初始化连接即时释放。Tailscale 仍无独立执行程序集。测试不修改外部防火墙或 tailnet，也不代表生产 RSS、真实写入中断、daemon/Helper 联合故障或整体正式包 ABI 已通过。

## 16. MB-01A当前基线入口（2026-11-05）

[当前基线报告](RelaxKonOS.ServerModuleBaseline.20261105.md)与[测量方案](RelaxKonOS.ServerModuleBaseline.Protocol.md)拥有本次固定产品源码、跨平台原始指标/缺失和复现方法；[资源清单](RelaxKonOS.ServerModuleResources.Inventory.md)拥有逐模块根引用及恢复权威。完整宿主启动/闲时与局部Git/Terminal真实生产服务夹具分开验收，不替换§7历史记录，也不以对象/代码回收解释生产内存收益。Linux静态全关因Proxy责任不可验证被拒绝，原门禁保留。

RES-01/03及MB-01整项仍开放：完整宿主真实授权业务、Firewall Helper只读、原300秒自然释放/再唤醒、TerminalHub重连、启动峰值和外部共享树成本未补齐。当前策略、300秒宽限期及SystemPerformance S1冻结不变；下一阶段与验收门槛以边界设计§42及本批报告为准。

2026-11-06 [MB-01B](RelaxKonOS.ServerModuleBaseline.20261106.md) 补齐两平台启动期观察、认证 Git 的 300 秒自然 Dormant 和第二代唤醒，并验证真实 WebSocket 终端断线重连与回放。Windows Personal 模式三轮通过 Defender Helper 自然释放和再次读取。Linux User Mode 的 Firewall 能力受限，隔离 System 模式联合失败与恢复证据见报告。MB-01 与 RES-01/03 继续开放。

## 17. Git 构建清理责任（2026-11-06）

Git 失败或取消后的镜像清理与终态写入均属于原代次工作租约。清理不可确认时保留原持久回执与恢复门禁。真实 Engine 标签删除验证、专用 BuildKit 就绪缺口及 MB-05 实机故障范围见[边界设计 §43](RelaxKonOS.ServerModuleBoundaries.Design.md#43-mb-03-构建清理责任与测试机预检2026-11-06)。生产内存和完整外部故障矩阵继续开放。

2026-11-06 续核见[边界设计 §44](RelaxKonOS.ServerModuleBoundaries.Design.md#44-mb-03-离线-buildkit-与-mb-05-隔离设施续核2026-11-06)。已验证官方 rootless 制品的离线导入和专用 builder 的实际启动阻塞，并实测隔离容器和 Nginx 的中断与恢复。应用操作责任、BuildKit 与 ALC 联合验收、真实 CA 和生产内存测量仍开放。

2026-11-06 Docker 应用层续核见[边界设计 §45](RelaxKonOS.ServerModuleBoundaries.Design.md#45-mb-05-docker-应用层在途中断与回执恢复2026-11-06)。真实 Engine 已接受停止后，工作 owner 排空原 CLI，重建的协调器按原 operationId 观察和恢复 Interrupted 回执，重复请求不会再次停止已显式启动的容器。Docker 仍为常驻管理模块，Engine 已接受的请求可继续执行。

## 18. ModuleHost 与 Terminal 崩溃续核（2026-11-06）

[边界设计 §46](RelaxKonOS.ServerModuleBoundaries.Design.md#46-mb-06-与-terminal-安装崩溃和恢复续核2026-11-06)记录每个平台的本地树证据、正式身份和崩溃矩阵缺口。信任修订、完整树证明和传输任务排空属于代次责任。没有证明的退出保留未知，SDK 后台责任与合作 drain 尚未验收，ModuleHost 闲时退出仍关闭。当前没有生产内存下降证据。


2026-11-06 MB-03/05 的只读测试设施复核见[边界设计 §50](RelaxKonOS.ServerModuleBoundaries.Design.md#50-mb-0305-隔离测试设施复核2026-11-06)。现有工作租约与实机证据范围沿用 §17，应用层跨进程故障及生产内存测量仍开放。

2026-11-07 MB-04 完整 Server 在管理停用后确认原入站发布候选，并由 Core 接续撤销下一条自然到期授权。真实 VLESS TLS 转发、过期拒绝与保留授权重连的证据由[边界设计台账](RelaxKonOS.ServerModuleBoundaries.Design.md)拥有。当前验证使用私有运行环境和测试进程承载的 Helper socket，Proxy 仍为 resident，完整安全交接与动态休眠继续开放。

2026-11-07 MB-04 在私有 systemd 联合验收中确认，独立 Helper 终止不影响已发布 sing-box 进程及认证流量。完整 Server 按原入站台账恢复候选，随后由 Core 处理后续授权到期。验收证据和测试范围见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md)。Proxy 仍为 resident，启用系统代理和 TUN 的同宿主联合故障及生产内存测量继续开放。

Linux 完整非 root Server 已从同一持久状态目录恢复在途 TUN 激活。真实设备创建后、写入完成标记前终止激活进程，原标记在 Helper 中断及首次启动失败后仍由 Core 接续。联合验收和边界见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整-server-接续在途-tun-激活)。

MB-05 证书续期已接入原持久扫描台账。到期执行作用域由原模块租约保护，跨进程实测补回原操作关联，远端订单无重放。证书模块保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-证书续期持久扫描与执行作用域)。

完整 Server 已通过 MB-05 自然续期、停机排空与重启调度验收。宿主收到停止信号后等待原 CA 任务和挑战清理，再持久化原回执并退出。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整-server-自然续期与停机排空)。

自然续期的认证取消也已通过 Linux 实测。真实 CA 响应交付前保留原任务和挑战，排空后持久化原取消回执。重启以原 operationId 读取回执与 CA 订单。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整-server-自然续期的认证取消)。

MB-05 网站发布的原常驻协调器只在新发布受理后创建执行作用域。原工作租约保护发布、补偿、终态和异步清理。Linux 实测原回执恢复不构造执行作用域，Nginx 持续转发。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布准入与执行作用域)。

MB-05 直接 WebServer 的异步执行作用域由原实例锁和工作租约保护。清理失败保留原活跃回执和故障责任。Linux 实测实际重载一次、恢复作用域零。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-直接-webserver-操作执行作用域)。

MB-05 Docker Compose 执行图由原工作租约保护，异步作用域清理结束后才提交终态。Linux 实测私有 Engine 与完整 Server 重启均恢复原回执，恢复只观察，不重新执行停止操作。Docker 保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-docker-compose-准入与执行作用域)。

MB-05 完整认证 Server 的 Linux 重启验收补齐直接 Nginx 重载路径。管理进程退出后，专属 Nginx 保持 TLS 和已重载的配置。重启按原操作 ID 和同键返回原回执，实际 reload 一次。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整认证-server-的-nginx-重载与重启)。

MB-04 原 Core TUN worker 持续扫描原标记。已完成的当前会话保持运行，未完成的关闭保留原标记 ID，直至运行时及管理路由核实。空闲扫描不解析控制器。Linux 实测通过同进程接续和禁用不重放。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-core-接续运行期-tun-恢复)。

MB-04 Linux 专属 Mihomo 实测覆盖实际激活副作用发生后取消。原 TUN 安全任务保留原标记，同一 Core worker 补偿，激活和禁用各一次。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-实际-tun-激活期间取消与同进程补偿)。

MB-05 系统包 Nginx 经生产 Helper 和专属 systemd 的实机中断恢复通过。原 master 在两进程中断期间持续提供 HTTPS，重启恢复原回执，实际重载一次。自然停机和取消排空见下文。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-系统包-nginx-经生产-helper-中断与恢复)。

MB-05 Linux Nginx 固定服务动作提交后，原 WebServer 工作任务等待 Helper 返回，实例锁和执行作用域持有到响应与清理结束。实机验证取消和 SIGTERM 均排空实际 systemd reload，重启未重放。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-已提交-nginx-systemd-作业的取消与停机排空)。

MB-05 Linux 系统包直接重载以原检查点保护未知原生作业。实机覆盖 Helper 超时、Helper 丢失和双进程中断，均保留原 ID 并拒绝同实例新后台操作。终态证据缺失时保持 resident 和恢复责任。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-原生重载结果待核验)。

MB-05 已为系统包 Nginx 直接重载接入生产 Helper 原 ID 成功回执。原工作 owner 只读对账，确认证据后写入原终态。Linux 实机确认重启后原 reload 未重放，证据缺失时继续保留责任。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-原-helper-回执对账)。

MB-05 系统包 Nginx 的成功及可确认失败回执在 Server 与 Helper 均存活时通过响应丢失实测。原任务保持到只读证据核验和原终态写入，之后才开放新后台操作。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-存活进程响应丢失和失败回执)。

MB-05 系统包 Nginx 在 Server 原终态持久化后压缩 Helper 回执详情。原防重放标记和未确认回执保持。Linux 实测已压缩 ID 跨 Helper 重启拒绝重载，清理确认丢失仅重试原清理。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-终态回执保留与防重放)。

MB-05 完整认证网站发布通过 Linux 私有 systemd 的 Configuring 和 Verifying 中断验收。Server 重启后通过原 operationId、应用历史和同键取得原回执，现有 Nginx、TLS 和独立 Docker 上游持续服务。应用关联按实际提交阶段保留，实际重载每次只执行一次。WebServer 保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整认证网站发布中断与恢复)。

MB-05 网站自动签发经既有 Nginx WebRoot 实测。证书 owner 在挑战写入前持久化归属，等待实际签发任务和清理结束后才提交终态。重启以原证书回执清理挑战、只读查询原 CA 订单，网站回执关联同一证书操作。原 Nginx、TLS 和 Docker 上游独立存续。Certificates 和 WebServer 保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布-webroot-签发与中断恢复)。

网站发布在原台账锁内排他准入同一 Nginx 实例和域名。Linux 实测另一真实应用在原 CA 工作尚未结束时被拒绝，原工作与回执继续由原发布操作持有。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布准入与执行作用域)。

首站自动 HTTP-01 由原发布租约执行。临时配置写入前持久化责任，原生重载确认后才进入签发。跨进程恢复保留原发布、原 Helper 证据及临时配置。Nginx 外部进程持续服务既有站点。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布自动建立首站-http-01-路由)。

首站临时路由清理在原 WebServer 工作租约内等待实际重载、备份清理和终态持久化。中断后保留原发布及独立清理 ID，重试按原 Helper 证据接续。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站临时路由的授权清理)。

首站授权续接在原 WebServer 工作租约内复用本地已完成的原证书操作，排空实际绑定、应用关联和回执提交。续接中断时继续保有未核验责任，按原 Helper 成功回执和匹配文件完成对账。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站续接绑定的原生回执对账)。

自动 WebRoot 首次发布的正式 TLS 绑定同样在原发布工作租约内等待原生结果、站点和应用关联及备份清理。中断恢复读取原绑定回执并完成本地记录。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站首次-tls-绑定的原生回执对账)。

MB-05 完整 Linux Server 的网站 WebRoot 签发通过认证取消和 SIGTERM 排空。实际 CA 响应和挑战清理结束前，原证书任务保有责任。已取消的原证书回执保留已知订单供只读对账。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站-webroot-签发的取消与停机排空)。

首个独立 Nginx 站点的显式 WebRoot 启用和证书绑定已通过 Linux 实测。重载中断后原挑战路由与旧 TLS 持续，原启用回执按原 ID 读取。只有原生作业已排空且站点文件与持久定义匹配时，后续授权更新才会接续使用站点备份。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站-webroot-备份核验与授权-tls-绑定)。

MB-04 Core 到期 worker 按原索引创建执行作用域。原候选落盘后，Helper 响应、原终态提交和异步释放都纳入停机排空。Proxy 保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-入站授权到期的停机排空)。

MB-04 入站发布将原 Helper 操作 ID 保存在现有入站台账，恢复只读核对同一回执及配置摘要。在途或证据缺失保留原责任。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-入站发布按原-helper-回执恢复)。

MB-04 已为原在途入站发布接入显式补偿。补偿使用原实例锁、原回执和已验证的旧配置。需要还原时保持服务停止，正常启动重新核验授权。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-在途入站发布的原-id-补偿)。

MB-04 原管理发布队列可在进程中断后按原 ID 接续已验证的领域结果或显式补偿。原队列工作租约覆盖恢复、审计、异步清理和终态持久化。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-原管理发布队列的恢复)。

MB-04 完整认证 Server 已通过原生入站发布提交后、响应交付前中断的联合验收。原发布和恢复键指向同一终态，真实 sing-box 继续服务。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整认证-server-的原入站发布回执)。

MB-04 完整认证 Server 已联合验证真实候选已注册、启动前的中断。Server 与 Helper 重启后按原 ID 补偿，保持服务停止至重新授权启动，原回执与流量验证通过。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整认证-server-的在途入站发布补偿)。

MB-04 Core TUN worker 停机时排空已发出的原禁用和路由核验，未能确认的恢复继续由原标记负责。Proxy 保持 resident。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-core-tun-恢复的停机排空)。

MB-04 Core 系统代理 guard 保留已派发的原对账任务和共享发布锁，直到 Helper 响应和执行作用域结束。系统代理环境与原恢复日志由现有 Helper 事务负责。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-core-系统代理-guard-的停机排空)。

MB-04 Core Mihomo 发布恢复调度器停机时等待原回执、终态持久化与异步释放。原持久索引及领域台账继续负责调度与恢复。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-core-mihomo-发布恢复的停机排空)。

MB-04 缩短了 Mihomo 运行时管理器的控制器和安装台账适配器的发布执行图寿命。原锁、回执和安全 worker 仍由 Core 负责。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-运行时与安装执行作用域)。

MB-04 隔离 Linux 的 Proxy 管理面静态配对基线已完成。三组管理读取中，概览与运行时组的 Server PSS 增量最大，下一阶段优先拆分该请求组的执行依赖。见[配对数据与限制](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-proxy-管理面的配对驻留基线)。

MB-04 概览使用独立请求作用域、直接活动档案查询和连接计数投影。后台启停不解析概览执行图。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-proxy-概览与启停执行边界)。

MB-04 Mihomo 连接快照使用共用流式读取器。连接概览不保留详情，读取期间的片段与投影均有上限。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-连接快照的流式读取)。

MB-04 完整认证 Server 的概览和连接详情使用原 Mihomo 控制器。管理进程重启后的连接与系统代理责任由[联合验收](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整认证-server-的-mihomo-活动连接)追踪。

MB-04 512 条真实连接的前后配对测量验证了流式快照在隔离负载下的 Server PSS 增量。Proxy 保持 resident。逐轮结果见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-512-条真实连接的配对测量)。

MB-04 Mihomo 快照解析代码使用独立可回收代次。并发读取和异步清理结束后才开始闲时计时。Core 网络安全与持久恢复仍由原所有者负责。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-快照解析代码的闲时释放)。

Linux 完整认证 Server 已验证同一进程中的解析代次闲时卸载与再加载，原 Mihomo 连接与系统代理所有权连续。此轮观察的是卸载事件，弱引用回收由独立专项验证。Proxy 整体继续 resident，生产内存验收保持开放。

Mihomo 概览直接使用请求作用域内的控制器。隔离 Linux 实测确认一次概览只探测一次 `/version`、读取一次 Core 恢复状态，独立运行时读取继续在原门禁内即时探测。完整认证 HTTP 联验通过，Proxy 整体继续 resident，生产 PSS 尚未测量。见[模块边界台账](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-概览的单次观测)。

MB-04 真实受管 Mihomo 的 512 连接配对复测覆盖完整认证 Server 及 35 秒闲置窗口。候选 Server PID 的闲置 PSS 在三对中低 0.19–4.99 MiB，延迟未一致改善。结果用于定位隔离负载，生产驻留与 Proxy 整体休眠继续开放。见[配对测量](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-受管-mihomo-概览的配对驻留测量)。

Mihomo 概览和连接详情的原模块工作租约覆盖控制器 Task、HTTP 响应和读取作用域的异步清理。清理失败时租约保留，协调器报告恢复阻塞。Proxy 整体仍 resident，连接解析器的闲时释放继续由自身执行门禁控制。见[HTTP 工作责任](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-概览和连接详情的-http-工作责任)。

Mihomo 连接计数和详情投影已进入现有可回收读取代次。Host 保留共享投影值或协议 DTO 不影响该代次回收。Proxy 整体继续 resident，安全及恢复责任沿用原所有者。见[连接计数与详情投影](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-连接计数与详情投影随代次释放)。

Mihomo 概览按 token 计数，继续校验完整快照。固定快照解析分配下降。隔离 Linux 的三对样本中，候选 35 秒 PSS 低 0.05–1.88 MiB，峰值增量和延迟均无一致改善。Proxy 仍 resident，生产驻留保持开放。见[计数配对测量](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-mihomo-概览按-json-token-计数)。

完整 Server 经 SIGTERM 停止监听后，原 Core 调度器仍持有已受理的 Mihomo 发布恢复，直至真实 Helper 回执和持久清理完成。原 Mihomo 继续转发，重启保留同一终态。见[完整 Server 排空](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整-server-在真实-mihomo-回执等待时排空)。

停用 Proxy 管理面时，原 Core 系统代理 guard 继续承担已派发的 Helper 写入，SIGTERM 后等待原响应。隔离 Linux 重启核对未再次写入原代理环境和归属日志。见[系统代理 guard 排空](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整-server-的系统代理-guard-停机排空)。

隔离 Linux 上，原 Core TUN worker 经完整 Server 的 SIGTERM 等待真实控制器禁用确认，之后核验路由并清理原标记。重启未再次派发 reload，Mihomo 继续转发。Proxy 仍 resident。见[真实 TUN 停机排空](RelaxKonOS.ServerModuleBoundaries.Design.md#mb-04-完整-server-的真实-tun-停机排空)。
