# 驻留原型、早期基线与实施记录

按原顺序保留早期基线、运行时原型、引用审计与驻留实施记录。各段中的状态和限制对应当时制品。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [7. 基线测量记录（2026-10-28，Windows）](#7-基线测量记录2026-10-28windows)
- [8. 首个运行时切片（2026-10-28，Windows 开发机）](#8-首个运行时切片2026-10-28windows-开发机)
- [13. 休眠内存引用审计与首批修复（2026-11-03）](#13-休眠内存引用审计与首批修复2026-11-03)
- [14. 当前模块阶段验收入口（2026-11-04）](#14-当前模块阶段验收入口2026-11-04)
- [15. Firewall 独立代码与 Tailscale 对象代次（2026-11-05）](#15-firewall-独立代码与-tailscale-对象代次2026-11-05)
- [16. MB-01A当前基线入口（2026-11-05）](#16-mb-01a当前基线入口2026-11-05)
- [17. Git 构建清理责任（2026-11-06）](#17-git-构建清理责任2026-11-06)
- [18. ModuleHost 与 Terminal 崩溃续核（2026-11-06）](#18-modulehost-与-terminal-崩溃续核2026-11-06)

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

本轮审计确认：Git/Firewall/Tailscale 已有对象边界；Proxy、Docker、Certificate、WebServer 仍有根服务和持续职责；SystemPerformance 的有界历史/静态缓存及 Terminal 的 PTY/可重连会话不能直接按请求结束释放；官方安装包和第三方宿主分别受到根注册和进程常驻限制。目标责任表、ABI、迁移顺序和验收条件已提升为[模块边界与内存回收设计](../RelaxKonOS.ServerModuleBoundaries.Design.md)，MB-01～MB-08 均待实施。此前合成回收结果不关闭真实业务内存测量、Proxy 职责迁移或代码/进程卸载验收。

2026-11-03 后续：Git CLI/构建执行器已移入可回收的独立程序集，原模块租约继续拥有业务调用和后台责任；真实 ALC 循环验证、打包边界及未完成项由[边界设计 §10](Git.md#10-git-执行切片实施2026-11-03mb-0203-部分)拥有。§13 的合成对象测试仍是原范围，不替代后续代码回收记录。

2026-11-03 仓库后续：Git 仓库全部 partial 实现已移入同一可回收程序集，以窄存储/身份/文件端口保持 Host 权威；真实仓库/凭据/控制台后的 100 轮代码回收与保留完成请求验证通过，详见[边界设计 §11](Git.md#11-git-仓库实现与-host-端口2026-11-03mb-0203-后续)。这不扩大真实 BuildKit、生产内存或其他模块的验收范围。

2026-11-03 Proxy 后续：共享读取与设置写入/系统代理对账接口已分离，Host 平台原语不再引用管理类；Proxy 尚未取得动态释放边界，安全/恢复 worker 的所有权和执行时机未改变。详见[边界设计 §12](Proxy-Publication.md#12-proxy-共享读取与写入安全边界2026-11-03mb-04-部分)。

2026-11-03 MB-04 续核：到期 worker 的真实异步释放检查已补跑 Linux；系统代理 guard 对账已与设置 writer/控制器客户端分离，仍使用原发布 gate 与身份核验。TUN marker 恢复及发布补偿保持原单一责任方和根引用，Proxy 继续 resident，不声明代码回收或生产内存收益；详见[边界设计 §14](Proxy-Safety.md#14-系统代理-guard-窄执行边界与安全责任审计2026-11-03mb-04-部分)。

2026-11-03 MB-04 TUN 续核：运行控制已脱离 settings writer，后者按调用创建；安全 owner、marker、两级锁和 Core 发布恢复仍保留。验证覆盖生产执行器的启停/补偿/恢复与竞争夹具，不扩大为实机网络事务、代码回收或生产内存收益。当前事实见[边界设计 §15](Proxy-Safety.md#15-tun-运行控制与设置写入执行边界2026-11-03mb-04-部分)。

2026-11-03 MB-04 发布恢复续核：Core worker 改为每轮临时 scope 与按 pending 类型选择执行，原补偿/重试和 publication gate 保留；profile 与订阅后台图及独立根引用审计见[边界设计 §16](Proxy-Publication.md#16-发布恢复临时作用域与-profile-执行图审计2026-11-03mb-04-部分)。这不构成整个 Proxy 回收、后台工作租约或生产内存收益验收，Proxy 仍 resident。

2026-11-03 MB-04 订阅续核：排队订阅已具有独立后台 scope，Proxy 操作台账跟踪完整任务并在停机排空，scope 清理或终态确认失败保留责任；这不是驻留工作租约，Proxy 继续 resident，详见[边界设计 §17](Proxy-Subscriptions.md#17-订阅后台任务独立作用域与完整寿命2026-11-03mb-04-部分)。

2026-11-03 MB-04 任务责任续核：禁用门禁联合只读检查内存执行/清理 owner、持久 operation ledger 与原 publication pending；未知责任拒绝新任务，不据只读快照新增休眠许可。持续安全责任交接与租约仍开放，详见[边界设计 §18](Proxy-Publication.md#18-proxy-任务准入停机与禁用责任门禁2026-11-03mb-04-部分)。

2026-11-03 Proxy 常驻边界收尾：管理排队工作统一独立 async scope；Core 唯一持有 guard、代次绑定 TUN 恢复、sing-box 到期与持续发布扫描，根操作台账只保留活动/故障 owner，历史按需读取。只读禁用检查覆盖上述持久责任。当前实现与验证限制以[边界设计 §19](Proxy-Publication.md#19-proxy-常驻责任与完整执行寿命收尾2026-11-03)为准。Proxy 继续 resident；未新增驻留工作租约、collectible ALC 或生产内存收益证据，RES-05 不关闭。

2026-11-04 MB-04：Proxy 使用同一 coordinator 的 resident work owner，后台租约覆盖 queued 前准入到业务/审计/scope 清理/终态确认，失败隔离同一 owner。目录继续 resident/不可释放。订阅转换已在独立程序集执行并做 ALC 回收检查，这不等于全模块回收；当前事实与验证环境见[边界设计 §20](Milestones.md#20-linux-modulehost-目标修复proxy-工作租约与订阅独立代码2026-11-04)。

2026-11-04 代码预算续核：订阅转换的活动及待回收 ALC 采用弱引用合计上限，达到上限时拒绝新转换，自然回收后重新准入，生产不调用 GC。具体行为与限制由[边界设计 §21](Proxy-Subscriptions.md#21-订阅转换待回收代码预算2026-11-04mb-04-部分)拥有；Proxy 仍 resident，不能据此宣布 RES-05 或生产内存验收完成。

2026-11-04 测试机续核：Windows/Ubuntu 26.04 最终驻留专项各 28 项通过，补齐预算与 100 轮回收的 Linux 验证，并新增保留 Host 异常时的失败代码回收检查；契约及实现项目依赖守卫已反向验证。环境、证据与验收限制见[边界设计 §22](Proxy-Publication.md#22-proxy-依赖守卫与测试机回收续核2026-11-04mb-0204-部分)。未进行生产内存测量或真实网络故障验收，RES-05/07 继续开放。

2026-11-04 MB-07：进程图标缓存改为 provider 实例所有，Server 与 Helper 各自有界；每个缓存最多 512 条/8 MiB 完成载荷，销毁及淘汰竞争不允许旧加载重新写回。Windows/Linux 各 15 项缓存检查与原进程/性能专项通过，范围见[边界设计 §23](SystemPerformance.md#23-进程图标缓存实例所有权与载荷预算2026-11-04mb-07-部分)。SystemPerformance 仍 resident，未接入闲时释放、完整 Hub/采样租约或代码回收，不以缓存预算声称进程树内存收益。

2026-11-04 MB-07 输入续核：desktop 图像实际读取限额及 PNG 目标位图分配前的编码/尺寸门禁已落地，Windows/Linux 新增输入检查各 17/18 项通过，原缓存、进程与采样回归保留。当前事实由[边界设计 §24](SystemPerformance.md#24-desktop-图像输入与目标位图分配预算2026-11-04mb-07-部分)拥有；目录/主题扫描、索引、原生峰值和整个模块休眠仍未验收。

2026-11-04 MB-07 索引续核：desktop 遍历、文件/总读取、映射/文本及整轮主题/路径探测已增加有界预算，Windows/Linux 新增索引专项 **11/12 项**通过，原输入、缓存、进程、性能和 Linux Proxy 驻留专项通过。详细上限与证据由[边界设计 §25](SystemPerformance.md#25-desktop-流式扫描与索引工作预算2026-11-04mb-07-部分)拥有；逻辑预算不保证慢 I/O 的时间上限，SystemPerformance 仍未接入完整工作租约、排空和闲时回收，不能据此宣称模块休眠或生产内存收益。

2026-11-04 MB-07 图标工作续核：普通/管理员图标共享有界准入，provider 另有独立执行预算；请求取消及 owner Stop/Dispose 不再让已开始的同步工作脱离资源所有者。Windows/Linux 最终各 19 项工作检查通过，完整行为与关闭范围由[边界设计 §26](SystemPerformance.md#26-图标并发准入取消与-owner-停机排空2026-11-04mb-07-部分)拥有。此队列未接驻留协调器租约，也不涵盖采样/Hub/整个 Helper 进程树；SystemPerformance 仍 resident，完整闲时释放和生产收益验收继续开放。

2026-11-04 MB-07 租约续核：SystemPerformance 已注册同一协调器的常驻工作 owner；完成准入的图标等待项和在途读取取得真实工作租约，协调器停止能撤销等待但不提前释放已开始的读取。Windows/Linux 各 14 项真实协调器检查、原专项及 Windows 默认模块全量回归最终通过，详见[边界设计 §27](SystemPerformance.md#27-图标工作接入常驻协调器租约2026-11-04mb-07-部分)。缓存仍归 Host owner，采样/Hub/查询/终止的完整租约和模块释放未完成；目录保持 resident/不可释放，RES 不据此关闭。

2026-11-04 MB-07 进程请求续核：统一当前 `ISystemPerformanceWorkLeaseSource`，查询（含缓存命中）和有效终止请求已接入同一协调器工作保护；Host owner 关闭准入并等待请求完成后清理两套快照与 CPU 基线。Windows/Linux 各 19 项新增检查、原专项及 Windows 默认模块全量回归通过，原请求取消与终止回执规则保留。当前范围见[边界设计 §28](SystemPerformance.md#28-进程查询终止工作保护与快照-owner-排空2026-11-04mb-07-部分)；采样/Hub、实机中断、整个模块释放和生产收益仍未验收，目录仍 resident。

2026-11-04 MB-07 采样续核：实际 source 读取至同步事件分发已取得真实工作租约，需求代次隔离旧读取，多连接注册不再被短路遗漏，循环实际结束后清理状态。Windows 新增 18 项与原性能专项通过；本轮 Linux 上传/执行被自动审批拒绝，尚未运行。当前范围与证据见[边界设计 §29](SystemPerformance.md#29-实际采样工作租约与需求代次2026-11-04mb-07-部分)。静态信息读取、REST 等待者完整寿命、连续 Hub/网络发送及整个模块释放仍未覆盖，目录保持 resident，不宣称生产内存收益。

2026-11-04 MB-07 静态信息续核：GetInfoAsync 已取得真实工作租约，独立请求 owner 关闭准入并等待实际源 Task；取消及 Stop 超时不提前释放保护。Windows 驻留检查累计 28 项与原性能专项通过，Linux 本轮未执行。完整行为、证据和限制见[边界设计 §30](SystemPerformance.md#30-性能静态信息读取的工作保护2026-11-04mb-07-部分)。REST 等待者、Hub 网络发送及整个模块回收继续开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 REST 续核：ReadSnapshotAsync 的缓存命中与等待者均取得真实工作租约，owner 关闭能唤醒纯等待并清理需求/订阅；等待者结束不提前释放仍在采集的独立 tick。Windows/Linux 驻留检查累计各 42 项与原性能专项通过，Windows 默认模块回归通过，详见[边界设计 §31](SystemPerformance.md#31-rest-快照等待者的完整调用保护2026-11-04mb-07-部分)。Hub 订阅/网络发送、同步历史读取与整个模块释放仍开放，不以调用保护声称生产内存收益。

2026-11-04 MB-07 广播续核：实际 typed Hub 发送已取得真实工作租约，Stop/Dispose 不提前释放非合作发送 Task；最多 4 条排队快照在循环实际结束时清空，不持连续工作租约。Windows/Linux 新增广播各 15 项、性能驻留各 42 项及原专项通过，Windows 默认模块回归与 Linux Proxy28 通过，用户授权后直接复用测试机固定工作目录。行为和验收限制见[边界设计 §32](SystemPerformance.md#32-hub-广播实际发送的工作寿命2026-11-04mb-07-部分)。连续连接/组成员、同步历史读取、模块释放及生产收益继续开放，目录仍 resident。

2026-11-04 MB-07 连续连接续核：PerformanceHubConnections 取得从 OnConnected 到真实组清理结束的连续工作租约，未订阅和已退订连接也受保护；组操作串行，断线/停止不让晚到 Add 复活需求，未知 Remove 失败保留故障与租约且 Stop 可见。Windows/Linux 最终连接各 25 项、原广播15/性能驻留42及专项通过，Windows 默认模块回归、Linux Proxy28 通过。详见[边界设计 §33](SystemPerformance.md#33-连续-hub-连接与组成员-owner2026-11-04mb-07-部分)；同步历史读取、整体资源清单、连接压力、真实传输/未知结果恢复和生产内存收益仍开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 只读接口续核：GetLatestAsync/GetHistoryAsync 直接替换原同步接口，空缓存与命中均经真实工作准入；HTTP 历史传递请求取消，公开读取在物化前后核验停止，返回结果不持续持租约。Windows/Linux 性能驻留各 53 项、连接25/广播15与原专项通过，Linux Proxy28 通过；行为和证据见[边界设计 §34](SystemPerformance.md#34-历史与最新快照的异步读取保护2026-11-04mb-07-部分)。后续 HTTP 序列化/传输、整体资源清单、压力与生产收益仍未验收，目录仍 resident。

2026-11-04 MB-07 HTTP 响应续核：已授权性能/进程响应在实际 IResult.ExecuteAsync 取得独立工作租约，linked RequestAborted 通知请求或协调器取消但不脱离真实执行，finally 恢复原令牌；响应失败不重放业务。Windows/Linux 新增 HTTP 各 15 项、原性能驻留53/连接25/广播15及专项通过，Windows 默认模块和 Linux Proxy28 通过，详见[边界设计 §35](SystemPerformance.md#35-http-性能响应执行保护2026-11-04mb-07-部分)。不等同连续整个 HTTP 管线或物理送达；整体资源清单、压力、未知结果恢复和生产收益仍开放，SystemPerformance 仍 resident。

2026-11-04 MB-07 连接预算续核：连接 owner 使用 128 个合计名额覆盖 pending、live 和故障责任，超限不排队/不取得租约；取消和 Stop 超时不提前归还，真实清理成功才补位，未知故障继续占用。Windows/Linux 新增预算各 13 项、原专项与 Windows 默认模块回归、Linux Proxy28 均通过，详见[边界设计 §36](SystemPerformance.md#36-连续-hub-连接的准入数量预算2026-11-04mb-07-部分)。这是 owner 数量预算，不是总内存或整个 SignalR 连接上限；真实压力、故障恢复与生产收益仍开放，目录仍 resident。

2026-11-04 MB-07 管理员图标续核：Helper 返回的 Base64 在解码分配前受 349528 原始字符预算约束，保留 256 KiB 解码后门禁；Windows/Linux 各14项和原专项通过，预建4194304字符输入的实际 facade 拒绝路径线程分配小于256 KiB，避免3 MiB解码数组。行为、证据和测量边界由[边界设计 §37](SystemPerformance.md#37-管理员图标-base64-解码前预算2026-11-04mb-07-部分)拥有；不包含上游字符串/传输缓冲或生产进程树收益，目录仍 resident。

## 14. 当前模块阶段验收入口（2026-11-04）

MB-01～08 的实际状态、剩余阻塞和整项门槛统一由[模块边界设计 §8/38/39](Milestones.md#8-实施顺序与跟踪项)维护。MB-05A 后台责任、MB-06A 进程代次和 Terminal-T1 会话/PTY 排空已完成该文所列阶段验收；均保持 resident。SystemPerformance S1 已冻结，仅修阻塞问题。上述结果不代表新增模块动态休眠、完整后代树矩阵或生产内存收益已通过。


2026-11-05：MB-06B 与 Terminal-T2 当前进程树阶段验收见[边界设计 §40](ModuleHost-Terminal.md#40-mb-06b-与-terminal-t2原生进程树验收2026-11-05)。Windows 原生 Job/ConPTY、Linux 专用 ModuleHost 身份、本地/永久降权 Helper 树及 EPERM 未知责任已实测；新增身份仍 resident，不启用闲时 kill/重启。Windows 安装 S4U、宿主崩溃和正式内存收益继续开放；本地 Terminal 单会话 .NET worker 的额外成本与点样亦由该节拥有。

## 15. Firewall 独立代码与 Tailscale 对象代次（2026-11-05）

[边界设计 §41](Firewall-Tailscale.md#41-firewall-f1-独立代码与-tailscale-ts-01-对象代次2026-11-05)拥有本轮验收台账。Firewall 独立 Contracts/执行代码和窄 Core 端口、保留完成/失败 Task 的真实 ALC 回收已验证；Tailscale 当前三代理真实对象图及原持久操作跨代不重放已验证，失败初始化连接即时释放。Tailscale 仍无独立执行程序集。测试不修改外部防火墙或 tailnet，也不代表生产 RSS、真实写入中断、daemon/Helper 联合故障或整体正式包 ABI 已通过。

## 16. MB-01A当前基线入口（2026-11-05）

[当前基线报告](../RelaxKonOS.ServerModuleBaseline.20261105.md)与[测量方案](../RelaxKonOS.ServerModuleBaseline.Protocol.md)拥有本次固定产品源码、跨平台原始指标/缺失和复现方法；[资源清单](../RelaxKonOS.ServerModuleResources.Inventory.md)拥有逐模块根引用及恢复权威。完整宿主启动/闲时与局部Git/Terminal真实生产服务夹具分开验收，不替换§7历史记录，也不以对象/代码回收解释生产内存收益。Linux静态全关因Proxy责任不可验证被拒绝，原门禁保留。

RES-01/03及MB-01整项仍开放：完整宿主真实授权业务、Firewall Helper只读、原300秒自然释放/再唤醒、TerminalHub重连、启动峰值和外部共享树成本未补齐。当前策略、300秒宽限期及SystemPerformance S1冻结不变；下一阶段与验收门槛以边界设计§42及本批报告为准。

2026-11-06 [MB-01B](../RelaxKonOS.ServerModuleBaseline.20261106.md) 补齐两平台启动期观察、认证 Git 的 300 秒自然 Dormant 和第二代唤醒，并验证真实 WebSocket 终端断线重连与回放。Windows Personal 模式三轮通过 Defender Helper 自然释放和再次读取。Linux User Mode 的 Firewall 能力受限，隔离 System 模式联合失败与恢复证据见报告。MB-01 与 RES-01/03 继续开放。

## 17. Git 构建清理责任（2026-11-06）

Git 失败或取消后的镜像清理与终态写入均属于原代次工作租约。清理不可确认时保留原持久回执与恢复门禁。真实 Engine 标签删除验证、专用 BuildKit 就绪缺口及 MB-05 实机故障范围见[边界设计 §43](Git.md#43-mb-03-构建清理责任与测试机预检2026-11-06)。生产内存和完整外部故障矩阵继续开放。

2026-11-06 续核见[边界设计 §44](Milestones.md#44-mb-03-离线-buildkit-与-mb-05-隔离设施续核2026-11-06)。已验证官方 rootless 制品的离线导入和专用 builder 的实际启动阻塞，并实测隔离容器和 Nginx 的中断与恢复。应用操作责任、BuildKit 与 ALC 联合验收、真实 CA 和生产内存测量仍开放。

2026-11-06 Docker 应用层续核见[边界设计 §45](Docker.md#45-mb-05-docker-应用层在途中断与回执恢复2026-11-06)。真实 Engine 已接受停止后，工作 owner 排空原 CLI，重建的协调器按原 operationId 观察和恢复 Interrupted 回执，重复请求不会再次停止已显式启动的容器。Docker 仍为常驻管理模块，Engine 已接受的请求可继续执行。

## 18. ModuleHost 与 Terminal 崩溃续核（2026-11-06）

[边界设计 §46](ModuleHost-Terminal.md#46-mb-06-与-terminal-安装崩溃和恢复续核2026-11-06)记录每个平台的本地树证据、正式身份和崩溃矩阵缺口。信任修订、完整树证明和传输任务排空属于代次责任。没有证明的退出保留未知，SDK 后台责任与合作 drain 尚未验收，ModuleHost 闲时退出仍关闭。当前没有生产内存下降证据。


2026-11-06 MB-03/05 的只读测试设施复核见[边界设计 §50](Milestones.md#50-mb-0305-隔离测试设施复核2026-11-06)。现有工作租约与实机证据范围沿用 §17，应用层跨进程故障及生产内存测量仍开放。

2026-11-07 MB-04 完整 Server 在管理停用后确认原入站发布候选，并由 Core 接续撤销下一条自然到期授权。真实 VLESS TLS 转发、过期拒绝与保留授权重连的证据由[边界设计台账](../RelaxKonOS.ServerModuleBoundaries.Design.md)拥有。当前验证使用私有运行环境和测试进程承载的 Helper socket，Proxy 仍为 resident，完整安全交接与动态休眠继续开放。

2026-11-07 MB-04 在私有 systemd 联合验收中确认，独立 Helper 终止不影响已发布 sing-box 进程及认证流量。完整 Server 按原入站台账恢复候选，随后由 Core 处理后续授权到期。验收证据和测试范围见[模块边界台账](../RelaxKonOS.ServerModuleBoundaries.Design.md)。Proxy 仍为 resident，启用系统代理和 TUN 的同宿主联合故障及生产内存测量继续开放。

Linux 完整非 root Server 已从同一持久状态目录恢复在途 TUN 激活。真实设备创建后、写入完成标记前终止激活进程，原标记在 Helper 中断及首次启动失败后仍由 Core 接续。联合验收和边界见[模块边界台账](Proxy-Safety.md#mb-04-完整-server-接续在途-tun-激活)。

MB-05 证书续期已接入原持久扫描台账。到期执行作用域由原模块租约保护，跨进程实测补回原操作关联，远端订单无重放。证书模块保持 resident。见[模块边界台账](Certificates.md#mb-05-证书续期持久扫描与执行作用域)。

完整 Server 已通过 MB-05 自然续期、停机排空与重启调度验收。宿主收到停止信号后等待原 CA 任务和挑战清理，再持久化原回执并退出。见[模块边界台账](Milestones.md#mb-05-完整-server-自然续期与停机排空)。

自然续期的认证取消也已通过 Linux 实测。真实 CA 响应交付前保留原任务和挑战，排空后持久化原取消回执。重启以原 operationId 读取回执与 CA 订单。见[模块边界台账](Milestones.md#mb-05-完整-server-自然续期的认证取消)。

MB-05 网站发布的原常驻协调器只在新发布受理后创建执行作用域。原工作租约保护发布、补偿、终态和异步清理。Linux 实测原回执恢复不构造执行作用域，Nginx 持续转发。见[模块边界台账](WebServer.md#mb-05-网站发布准入与执行作用域)。

MB-05 直接 WebServer 的异步执行作用域由原实例锁和工作租约保护。清理失败保留原活跃回执和故障责任。Linux 实测实际重载一次、恢复作用域零。见[模块边界台账](WebServer.md#mb-05-直接-webserver-操作执行作用域)。

MB-05 Docker Compose 执行图由原工作租约保护，异步作用域清理结束后才提交终态。Linux 实测私有 Engine 与完整 Server 重启均恢复原回执，恢复只观察，不重新执行停止操作。Docker 保持 resident。见[模块边界台账](Docker.md#mb-05-docker-compose-准入与执行作用域)。

MB-05 完整认证 Server 的 Linux 重启验收补齐直接 Nginx 重载路径。管理进程退出后，专属 Nginx 保持 TLS 和已重载的配置。重启按原操作 ID 和同键返回原回执，实际 reload 一次。见[模块边界台账](WebServer.md#mb-05-完整认证-server-的-nginx-重载与重启)。

MB-04 原 Core TUN worker 持续扫描原标记。已完成的当前会话保持运行，未完成的关闭保留原标记 ID，直至运行时及管理路由核实。空闲扫描不解析控制器。Linux 实测通过同进程接续和禁用不重放。见[模块边界台账](Proxy-Safety.md#mb-04-core-接续运行期-tun-恢复)。

MB-04 Linux 专属 Mihomo 实测覆盖实际激活副作用发生后取消。原 TUN 安全任务保留原标记，同一 Core worker 补偿，激活和禁用各一次。见[模块边界台账](Proxy-Safety.md#mb-04-实际-tun-激活期间取消与同进程补偿)。

MB-05 系统包 Nginx 经生产 Helper 和专属 systemd 的实机中断恢复通过。原 master 在两进程中断期间持续提供 HTTPS，重启恢复原回执，实际重载一次。自然停机和取消排空见下文。见[模块边界台账](WebServer.md#mb-05-系统包-nginx-经生产-helper-中断与恢复)。

MB-05 Linux Nginx 固定服务动作提交后，原 WebServer 工作任务等待 Helper 返回，实例锁和执行作用域持有到响应与清理结束。实机验证取消和 SIGTERM 均排空实际 systemd reload，重启未重放。见[模块边界台账](WebServer.md#mb-05-已提交-nginx-systemd-作业的取消与停机排空)。

MB-05 Linux 系统包直接重载以原检查点保护未知原生作业。实机覆盖 Helper 超时、Helper 丢失和双进程中断，均保留原 ID 并拒绝同实例新后台操作。终态证据缺失时保持 resident 和恢复责任。见[模块边界台账](WebServer.md#mb-05-nginx-原生重载结果待核验)。

MB-05 已为系统包 Nginx 直接重载接入生产 Helper 原 ID 成功回执。原工作 owner 只读对账，确认证据后写入原终态。Linux 实机确认重启后原 reload 未重放，证据缺失时继续保留责任。见[模块边界台账](WebServer.md#mb-05-nginx-原-helper-回执对账)。

MB-05 系统包 Nginx 的成功及可确认失败回执在 Server 与 Helper 均存活时通过响应丢失实测。原任务保持到只读证据核验和原终态写入，之后才开放新后台操作。见[模块边界台账](WebServer.md#mb-05-nginx-存活进程响应丢失和失败回执)。

MB-05 系统包 Nginx 在 Server 原终态持久化后压缩 Helper 回执详情。原防重放标记和未确认回执保持。Linux 实测已压缩 ID 跨 Helper 重启拒绝重载，清理确认丢失仅重试原清理。见[模块边界台账](WebServer.md#mb-05-nginx-终态回执保留与防重放)。

MB-05 完整认证网站发布通过 Linux 私有 systemd 的 Configuring 和 Verifying 中断验收。Server 重启后通过原 operationId、应用历史和同键取得原回执，现有 Nginx、TLS 和独立 Docker 上游持续服务。应用关联按实际提交阶段保留，实际重载每次只执行一次。WebServer 保持 resident。见[模块边界台账](WebServer.md#mb-05-完整认证网站发布中断与恢复)。

MB-05 网站自动签发经既有 Nginx WebRoot 实测。证书 owner 在挑战写入前持久化归属，等待实际签发任务和清理结束后才提交终态。重启以原证书回执清理挑战、只读查询原 CA 订单，网站回执关联同一证书操作。原 Nginx、TLS 和 Docker 上游独立存续。Certificates 和 WebServer 保持 resident。见[模块边界台账](WebServer-TLS.md#mb-05-网站发布-webroot-签发与中断恢复)。

网站发布在原台账锁内排他准入同一 Nginx 实例和域名。Linux 实测另一真实应用在原 CA 工作尚未结束时被拒绝，原工作与回执继续由原发布操作持有。见[模块边界台账](WebServer.md#mb-05-网站发布准入与执行作用域)。

首站自动 HTTP-01 由原发布租约执行。临时配置写入前持久化责任，原生重载确认后才进入签发。跨进程恢复保留原发布、原 Helper 证据及临时配置。Nginx 外部进程持续服务既有站点。见[模块边界台账](WebServer-TLS.md#mb-05-网站发布自动建立首站-http-01-路由)。

首站临时路由清理在原 WebServer 工作租约内等待实际重载、备份清理和终态持久化。中断后保留原发布及独立清理 ID，重试按原 Helper 证据接续。见[模块边界台账](WebServer-TLS.md#mb-05-首站临时路由的授权清理)。

首站授权续接在原 WebServer 工作租约内复用本地已完成的原证书操作，排空实际绑定、应用关联和回执提交。续接中断时继续保有未核验责任，按原 Helper 成功回执和匹配文件完成对账。见[模块边界台账](WebServer-TLS.md#mb-05-首站续接绑定的原生回执对账)。

自动 WebRoot 首次发布的正式 TLS 绑定同样在原发布工作租约内等待原生结果、站点和应用关联及备份清理。中断恢复读取原绑定回执并完成本地记录。见[模块边界台账](WebServer-TLS.md#mb-05-首站首次-tls-绑定的原生回执对账)。

MB-05 完整 Linux Server 的网站 WebRoot 签发通过认证取消和 SIGTERM 排空。实际 CA 响应和挑战清理结束前，原证书任务保有责任。已取消的原证书回执保留已知订单供只读对账。见[模块边界台账](WebServer-TLS.md#mb-05-网站-webroot-签发的取消与停机排空)。

首个独立 Nginx 站点的显式 WebRoot 启用和证书绑定已通过 Linux 实测。重载中断后原挑战路由与旧 TLS 持续，原启用回执按原 ID 读取。只有原生作业已排空且站点文件与持久定义匹配时，后续授权更新才会接续使用站点备份。见[模块边界台账](WebServer-TLS.md#mb-05-首站-webroot-备份核验与授权-tls-绑定)。

MB-04 Core 到期 worker 按原索引创建执行作用域。原候选落盘后，Helper 响应、原终态提交和异步释放都纳入停机排空。Proxy 保持 resident。见[模块边界台账](Proxy-Safety.md#mb-04-入站授权到期的停机排空)。

MB-04 入站发布将原 Helper 操作 ID 保存在现有入站台账，恢复只读核对同一回执及配置摘要。在途或证据缺失保留原责任。见[模块边界台账](Proxy-Publication.md#mb-04-入站发布按原-helper-回执恢复)。

MB-04 已为原在途入站发布接入显式补偿。补偿使用原实例锁、原回执和已验证的旧配置。需要还原时保持服务停止，正常启动重新核验授权。见[模块边界台账](Proxy-Publication.md#mb-04-在途入站发布的原-id-补偿)。

MB-04 原管理发布队列可在进程中断后按原 ID 接续已验证的领域结果或显式补偿。原队列工作租约覆盖恢复、审计、异步清理和终态持久化。见[模块边界台账](Proxy-Publication.md#mb-04-原管理发布队列的恢复)。

MB-04 完整认证 Server 已通过原生入站发布提交后、响应交付前中断的联合验收。原发布和恢复键指向同一终态，真实 sing-box 继续服务。见[模块边界台账](Proxy-Publication.md#mb-04-完整认证-server-的原入站发布回执)。

MB-04 完整认证 Server 已联合验证真实候选已注册、启动前的中断。Server 与 Helper 重启后按原 ID 补偿，保持服务停止至重新授权启动，原回执与流量验证通过。见[模块边界台账](Proxy-Publication.md#mb-04-完整认证-server-的在途入站发布补偿)。

MB-04 Core TUN worker 停机时排空已发出的原禁用和路由核验，未能确认的恢复继续由原标记负责。Proxy 保持 resident。见[模块边界台账](Proxy-Safety.md#mb-04-core-tun-恢复的停机排空)。

MB-04 Core 系统代理 guard 保留已派发的原对账任务和共享发布锁，直到 Helper 响应和执行作用域结束。系统代理环境与原恢复日志由现有 Helper 事务负责。见[模块边界台账](Proxy-Safety.md#mb-04-core-系统代理-guard-的停机排空)。

MB-04 Core Mihomo 发布恢复调度器停机时等待原回执、终态持久化与异步释放。原持久索引及领域台账继续负责调度与恢复。见[模块边界台账](Proxy-Publication.md#mb-04-core-mihomo-发布恢复的停机排空)。

MB-04 缩短了 Mihomo 运行时管理器的控制器和安装台账适配器的发布执行图寿命。原锁、回执和安全 worker 仍由 Core 负责。见[模块边界台账](Proxy-Publication.md#mb-04-mihomo-运行时与安装执行作用域)。

MB-04 隔离 Linux 的 Proxy 管理面静态配对基线已完成。三组管理读取中，概览与运行时组的 Server PSS 增量最大，下一阶段优先拆分该请求组的执行依赖。见[配对数据与限制](Proxy-Observation.md#mb-04-proxy-管理面的配对驻留基线)。

MB-04 概览使用独立请求作用域、直接活动档案查询和连接计数投影。后台启停不解析概览执行图。见[模块边界台账](Proxy-Observation.md#mb-04-proxy-概览与启停执行边界)。

MB-04 Mihomo 连接快照使用共用流式读取器。连接概览不保留详情，读取期间的片段与投影均有上限。见[模块边界台账](Proxy-Observation.md#mb-04-mihomo-连接快照的流式读取)。

MB-04 完整认证 Server 的概览和连接详情使用原 Mihomo 控制器。管理进程重启后的连接与系统代理责任由[联合验收](Proxy-Observation.md#mb-04-完整认证-server-的-mihomo-活动连接)追踪。

MB-04 512 条真实连接的前后配对测量验证了流式快照在隔离负载下的 Server PSS 增量。Proxy 保持 resident。逐轮结果见[模块边界台账](Proxy-Observation.md#mb-04-mihomo-512-条真实连接的配对测量)。

MB-04 Mihomo 快照解析代码使用独立可回收代次。并发读取和异步清理结束后才开始闲时计时。Core 网络安全与持久恢复仍由原所有者负责。见[模块边界台账](Proxy-Observation.md#mb-04-mihomo-快照解析代码的闲时释放)。

Linux 完整认证 Server 已验证同一进程中的解析代次闲时卸载与再加载，原 Mihomo 连接与系统代理所有权连续。此轮观察的是卸载事件，弱引用回收由独立专项验证。Proxy 整体继续 resident，生产内存验收保持开放。

Mihomo 概览直接使用请求作用域内的控制器。隔离 Linux 实测确认一次概览只探测一次 `/version`、读取一次 Core 恢复状态，独立运行时读取继续在原门禁内即时探测。完整认证 HTTP 联验通过，Proxy 整体继续 resident，生产 PSS 尚未测量。见[模块边界台账](Proxy-Observation.md#mb-04-mihomo-概览的单次观测)。

MB-04 真实受管 Mihomo 的 512 连接配对复测覆盖完整认证 Server 及 35 秒闲置窗口。候选 Server PID 的闲置 PSS 在三对中低 0.19–4.99 MiB，延迟未一致改善。结果用于定位隔离负载，生产驻留与 Proxy 整体休眠继续开放。见[配对测量](Proxy-Observation.md#mb-04-受管-mihomo-概览的配对驻留测量)。

Mihomo 概览和连接详情的原模块工作租约覆盖控制器 Task、HTTP 响应和读取作用域的异步清理。清理失败时租约保留，协调器报告恢复阻塞。Proxy 整体仍 resident，连接解析器的闲时释放继续由自身执行门禁控制。见[HTTP 工作责任](Proxy-Observation.md#mb-04-mihomo-概览和连接详情的-http-工作责任)。

Mihomo 连接计数和详情投影已进入现有可回收读取代次。Host 保留共享投影值或协议 DTO 不影响该代次回收。Proxy 整体继续 resident，安全及恢复责任沿用原所有者。见[连接计数与详情投影](Proxy-Observation.md#mb-04-mihomo-连接计数与详情投影随代次释放)。

Mihomo 概览按 token 计数，继续校验完整快照。固定快照解析分配下降。隔离 Linux 的三对样本中，候选 35 秒 PSS 低 0.05–1.88 MiB，峰值增量和延迟均无一致改善。Proxy 仍 resident，生产驻留保持开放。见[计数配对测量](Proxy-Observation.md#mb-04-mihomo-概览按-json-token-计数)。

完整 Server 经 SIGTERM 停止监听后，原 Core 调度器仍持有已受理的 Mihomo 发布恢复，直至真实 Helper 回执和持久清理完成。原 Mihomo 继续转发，重启保留同一终态。见[完整 Server 排空](Proxy-Publication.md#mb-04-完整-server-在真实-mihomo-回执等待时排空)。

停用 Proxy 管理面时，原 Core 系统代理 guard 继续承担已派发的 Helper 写入，SIGTERM 后等待原响应。隔离 Linux 重启核对未再次写入原代理环境和归属日志。见[系统代理 guard 排空](Proxy-Safety.md#mb-04-完整-server-的系统代理-guard-停机排空)。

隔离 Linux 上，原 Core TUN worker 经完整 Server 的 SIGTERM 等待真实控制器禁用确认，之后核验路由并清理原标记。重启未再次派发 reload，Mihomo 继续转发。Proxy 仍 resident。见[真实 TUN 停机排空](Proxy-Safety.md#mb-04-完整-server-的真实-tun-停机排空)。

停用 Proxy 管理面时，Core 的入站授权到期经真实 Helper 撤销并生成原回执。Linux 完整 Server 在 SIGTERM 后等到原响应并持久化完成，重启按原 ID 读到同一回执，未重放撤销。见[授权到期停机排空](Proxy-Safety.md#mb-04-完整-server-的入站授权到期停机排空)。

Proxy 管理队列保留原工作租约至实际清理和终态落盘。调用方可取消等待，Host 停机仍排空已受理任务。见[管理队列停机所有权](Proxy-Publication.md#mb-04-proxy-管理队列的-host-停机所有权)。

已授权的订阅响应体由独立可回收代次读入、解码和转换，流清理完成后卸载。真实分段响应与 Mihomo 转发在隔离 Linux 联验，弱引用回收由本地 Release 验证。Proxy 整体仍 resident。见[订阅响应体执行边界](Proxy-Subscriptions.md#mb-04-订阅响应体的可回收执行边界)。

排队订阅刷新、激活和批次共享原持久调度与发布恢复。新操作持有一个可回收执行代次至终态或待核验责任落盘，Host 继续独占发布权威和安全 owner。见[订阅后台执行图](Proxy-Subscriptions.md#mb-04-订阅后台执行图的可回收交接)。

完整认证 Server 的未激活订阅刷新已在真实受管 Mihomo 环境联验。下载在途持有工作租约，转换代次卸载后原档案与成功回执落盘。重启同键读取原结果，不重复下载。见[完整认证订阅队列](Proxy-Subscriptions.md#mb-04-真实受管-mihomo-的完整认证订阅队列)。

单条活动订阅刷新用同一 operationId 贯穿队列、Core 发布和原生回执。原发布权威确认后，队列用原证据补写终态并持有工作租约至落盘。Linux 验证强制终止后重启与存活 Server 的原生回执断线均保持原刷新 ID 和真实转发。缺少证据仍保留责任。Proxy 整体继续 resident。见[活动订阅终态恢复](Proxy-Subscriptions.md#mb-04-活动订阅刷新恢复原队列终态)。

实际 Mihomo 拒绝活动订阅配置时，隔离 Linux 验证补偿后的原转发与订阅时间保持。Server 在 Helper 失败回执在途时终止，重启补写原 Failed，期间未重复下载或派发配置。见[活动订阅补偿回执](Proxy-Subscriptions.md#mb-04-活动订阅原生失败与补偿回执)。

隔离 Linux 上，解析代次在 Core 等待原 Helper 回执时闲时卸载。认证读取唤醒新代次并看到同一真实连接，Server 排空原发布后，原连接完成转发。Proxy 整体继续 resident。见[读取代次与原发布恢复](Proxy-Observation.md#mb-04-读取代次闲时卸载与原发布恢复)。

Git 原构建台账持久区分明确未派发、预检责任未知和可能已派发。重新打开台账后，仅明确未派发的原操作在持久化终态后允许执行代次释放。原回执可按同一 ID 和幂等键查询。见[构建派发恢复台账](Git.md#mb-03-git-构建派发检查点与重启对账)。

MB-05 的 Nginx 清理确认可在存活 Server 内按原 ID 重试。原工作租约持有到确认落盘，停机后的待清理责任仍由原标记接续，停用检查读取同一标记。WebServer 保持 resident。见[清理确认与租约](WebServer.md#mb-05-nginx-原回执清理确认与租约)。

MB-05 WebRoot 挑战内容不匹配时，原证书台账保持活动清理责任并拒绝新证书操作。跨进程仍可认证查询原回执和原 CA 订单。Certificates 与 WebServer 保持 resident，见[挑战文件归属验收](WebServer-TLS.md#mb-05-webroot-挑战内容变化时保留原清理责任)。

MB-05 已在隔离 Linux 验证生产续期 worker 的自然扫描遇到挑战归属阻塞时保持原扫描未完成。恢复后按同一扫描 ID 和原续期回执接续。见[续期扫描跨阻塞验收](WebServer-TLS.md#mb-05-续期扫描跨挑战归属阻塞恢复)。

Mihomo 连接详情作用域直接构造控制器，工作租约继续覆盖真实响应及异步清理。原解析代次和 Core 恢复责任分别维持，Proxy 整体仍 resident。见[连接详情读取执行图](Proxy-Observation.md#mb-04-mihomo-连接详情读取执行图)。

Mihomo 流量与日志读取现在共享原工作租约、独立作用域和 HTTP 响应排空逻辑。Core 发布恢复继续由原 owner 承担。见[流量与日志读取租约](Proxy-Observation.md#mb-04-mihomo-流量与日志读取租约)。

Mihomo 流量的累计量及内存读取使用现有有界连接快照的统计分支。该分支与详情共享可回收代次，实际控制器流和 HTTP 响应仍由原工作租约保护。见[流量累计量读取](Proxy-Observation.md#mb-04-mihomo-流量累计量使用有界连接快照)。

Mihomo 连接详情保留既有可回收投影。按需 token 解码候选降低了受控解析分配，Linux 配对实测未达到峰值 PSS 目标。见[连接详情按需解码评估](Proxy-Observation.md#mb-04-mihomo-连接详情按需解码评估)。

Mihomo 流量轮询在 512 条真实连接的隔离负载下有可测的 Server PSS 和 CPU 成本。原发布回执及转发连续，Proxy 整体保持 resident。见[高连接数流量轮询基线](Proxy-Observation.md#mb-04-mihomo-高连接数流量轮询基线)。

Docker Engine 离线时，启动对账继续持有原 resident 工作租约和项目互斥。Engine 恢复后的只读观察、作用域清理及原终态持久化完成后才归还工作责任。见[Engine 离线启动对账](Docker.md#mb-05-docker-engine-离线时的原启动对账)。

存活 Server 中的 Compose 变更返回后若首次 Engine 观察失败，原工作租约继续覆盖独立只读观察作用域及原终态提交。Docker 保持 resident。见[变更后的观察中断](Docker.md#mb-05-docker-变更返回后的-engine-观察中断)。

已派发的 Compose 变更遇到用户取消和 Engine 观察断线时，原工作租约覆盖 CLI 清理、后续只读观察及原终态持久化。见[取消与观察断线](Docker.md#mb-05-docker-用户取消与观察断线交叠)。

桌面 Proxy 概览限制每个工作区的在途流量请求数，并在离开页面时取消。Server 的工作租约继续由真实任务和清理的寿命决定。见[流量轮询排空](Proxy-Observation.md#mb-04-桌面-mihomo-流量轮询排空)。

Proxy 的运行时、代理组、路由及 DNS 读取使用现有 resident 工作租约。原 Core 管理器仍负责运行时观察，实际响应和异步清理结束才释放请求责任。见[控制面读取责任](Proxy-Observation.md#mb-04-mihomo-运行时与控制面读取责任)。

四条控制面读取的隔离配对实测未显示稳定 PSS 改善。Proxy 继续保持 resident，原读任务和响应由现有租约保护。见[控制面读取配对测量](Proxy-Observation.md#mb-04-mihomo-控制面读取配对测量)。

桌面路由与 DNS 共用一次 Mihomo 配置读取，其控制器任务、响应和异步清理由同一个原工作租约保护。见[路由和 DNS 单次采样](Proxy-Observation.md#mb-04-mihomo-路由和-dns-单次采样)。

512 条真实连接的隔离配对复测显示，配置读取合并后 35 秒 PSS 较低，读取期间峰值方向不一，Server CPU 增加。Proxy 保持 resident，生产内存未测。见[配置合并读取配对测量](Proxy-Observation.md#mb-04-mihomo-配置合并读取配对测量)。

MB-03 在私有 Linux Engine 网络上验证了宿主和 builder 镜像内 Git 的专属 HTTPS 传输与信任拒绝路径。真实 BuildKit 执行与 ALC 联合验收仍等待可运行专属 RootlessKit 的环境。见[HTTPS 源仓库夹具](Git.md#mb-03-专属-https-源仓库夹具)。

MB-04 的重叠概览请求共用一次受管运行时、活动档案、恢复状态及连接计数。原采样的租约覆盖实际读取和清理，已认证请求各自持有响应租约。见[重叠概览采样](Proxy-Observation.md#mb-04-mihomo-重叠概览采样)。

概览与独立运行时读取可以共享在途运行时判定和版本探测。共享采样持有执行与清理责任，两个入口各自排空响应。见[跨路由运行时采样](Proxy-Observation.md#mb-04-mihomo-跨路由运行时采样)。

MB-04 的重叠流量请求共用一个控制器采样和连接快照读取。采样租约负责实际控制器任务与作用域清理，各请求的租约负责自身 HTTP 响应。最后一个客户端取消后，新请求等原采样清理再读取。Proxy 继续 resident。见[重叠流量采样](Proxy-Observation.md#mb-04-mihomo-重叠流量采样)。

MB-04 的重叠连接详情请求共用一次在途读取与投影。采样工作和各 HTTP 响应分别持有原租约，清理失败保留恢复责任。概览计数和流量统计保留各自投影。见[重叠连接详情采样](Proxy-Observation.md#mb-04-mihomo-重叠连接详情采样)。

MB-05 的四条 Compose 只读 HTTP 路由使用工作租约保护 CLI、响应及异步作用域清理。未核实的 CLI 清理保留恢复责任，Docker 继续 resident。见[Compose 只读 HTTP 工作责任](Docker.md#mb-05-docker-compose-只读-http-工作责任)。

MB-05 的 Compose 执行代码在所有作用域结束后可闲时卸载。Core 保有共享 Engine、原栈操作台账和终态恢复，Docker 管理模块继续 resident。见[Compose 独立执行程序集](Docker.md#mb-05-docker-compose-独立执行程序集)。

MB-05 的 ACME 原订单观察由原证书 owner 持有一个工作租约，覆盖真实 CA Task、HTTP 响应和异步清理。完整 Linux Server 的断线与 SIGTERM 验收已通过。Certificates 继续 resident。见[原订单观察](Certificates.md#mb-05-acme-原订单观察的-http-工作责任)。

MB-05 的 ACME SDK 与执行代码支持闲时卸载和按需唤醒。原证书 owner 继续负责原订单、挑战清理、续期及部署，Certificates 保持 resident。见[ACME SDK 独立执行程序集](Certificates.md#mb-05-acme-sdk-独立执行程序集)。

撤销工作在 CA 响应和终态清理期间保留 Core 租约。确认后清理受阻或 CA 结果待核验时，停机按已持久检查点交接，启动重新接管未决责任。SDK 执行代次在实际调用结束后可独立卸载。见[撤销确认与本地恢复](Certificates.md#mb-05-acme-撤销确认与本地恢复)。

ARI 查询由原续期扫描持有工作租约。进程中断后，原扫描可以重读 ARI，已经关联的续期继续由原操作台账负责。真实断流后的到期时间兜底见[ARI 断流与原扫描恢复](Certificates.md#mb-05-ari-断流与原扫描恢复)。

实际 HTTPS 验收覆盖续期时的旧连接、替代证书的新握手、确认撤销后的选择移除和另一 SNI 的持续服务。TLS 选择仍属于 Core，注册表只持有活动证书的完整链上下文，已选择的连接继续持有其所需上下文。Linux URL 和命名 HTTPS 端点通过仅根信任验证。见[Kestrel 动态证书链](Certificates.md#mb-05-kestrel-动态证书链)。

MB-05 的网站发布和具名首站恢复使用独立可回收执行代码。原 Nginx、TLS、发布台账及应用关联权威由 Host 保持，WebServer 管理模块继续 resident。见[网站发布独立执行程序集](WebServer.md#mb-05-网站发布独立执行程序集)。

MB-05 直接重载与网站发布共享 WebServer 执行代次。新重载的代次随原工作保持到命令、终态和 Helper 回执清理结束。原回执恢复由 Host 负责，外部 Nginx 和 TLS 持续运行，WebServer 管理模块保持 resident。见[直接重载独立执行程序集](WebServer.md#mb-05-直接-nginx-重载独立执行程序集)。

Nginx 显式证书轮换沿用原重载 owner、原生回执和待核验门禁。专属 Nginx 可以在 Server 中断时持续提供替代证书，恢复仍按 Helper 的原生证据决定终态。见[Nginx 显式证书轮换](WebServer-TLS.md#mb-05-nginx-显式证书轮换与原重载回执)。

自动部署协调器空闲时等待证书台账信号，持久续期版本和目标用于重启接续。每次对账取得 WebServer 工作租约，实际重载和未核实原生结果继续由原 WebServer 操作 owner 持有。见[Nginx 续期自动部署](WebServer-TLS.md#mb-05-nginx-续期自动部署)。

管理模块停用且续期成功时，Core 证书台账保留待交接版本，外部 Nginx 持续服务。Linux 验证了准入前进程中断及重新启用后一次接管，见[准入前进程中断](WebServer-TLS.md#mb-05-nginx-自动部署准入前的进程中断)。

管理模块启用时，续期目标已经持久化但重载台账写入失败，WebServer 原 owner 持有恢复责任。Linux 已验证进程终止后按原续期检查点只准入一次重载。见[目标检查点恢复](WebServer-TLS.md#mb-05-nginx-自动部署的目标检查点恢复)。

自动重载的原排队回执已落盘而 SQLite 投影失败时，原工作租约持有到进程中断。启动从同一回执恢复中断终态，随后完成部署对账和租约排空。见[派发前的原回执恢复](WebServer-TLS.md#mb-05-nginx-自动重载在派发前的原回执恢复)。

Proxy 同次停机联合验收中，原入站到期回执落盘后，Server 仍持有独立 TUN 清理责任。系统代理 guard 与 TUN 复用共享发布锁及原归属记录，已有网络转发继续。见[Proxy 联合安全责任](Proxy-Safety.md#mb-04-同次停机的-proxy-安全责任与恢复)。

Proxy 设置写入的 resident 租约覆盖执行、审计、响应与异步作用域清理。受管发布的重启恢复继续读取原发布权威和 Helper 回执。见[设置写入工作租约](Proxy-Publication.md#mb-04-proxy-设置写入的工作租约)。

档案激活和配置应用也由原 Proxy 工作租约覆盖执行、审计、响应与清理。受管恢复使用原发布和档案选择标记。见[档案发布工作租约](Proxy-Publication.md#mb-04-proxy-档案发布的工作租约)。

直连配置 reload 的未知结果保留原磁盘配置与元数据。TUN 从原标记接续实际状态观察。见[直连 reload 回执丢失](Proxy-Publication.md#mb-04-mihomo-直连-reload-回执丢失)。

直连 reload 检查点使用原加密发布台账和调度索引。恢复只处理匹配的磁盘候选，TUN 紧急禁用和系统代理 guard 继续由 Core 原执行者接续。见[直连 reload 持久检查点](Proxy-Publication.md#mb-04-mihomo-直连-reload-持久检查点)。

直连具名重试与 Core 启动恢复共用原发布锁和磁盘还原逻辑。原控制器结果仍未知时，原 ID、调度索引和写入门禁继续保留。见[直连具名恢复](Proxy-Publication.md#mb-04-mihomo-直连具名恢复)。

直连档案收到原 reload 确认后，Core 可接续原 SQLite 提交并持久化回执。候选配置漂移时继续保留调度与写入责任。见[直连档案提交恢复](Proxy-Publication.md#mb-04-mihomo-直连档案提交恢复)。

宿主代理策略一致且原生应答齐备时，Core 可接续直连设置的本地提交，保留原系统代理 guard 和 TUN 所有者。见[直连设置提交恢复](Proxy-Publication.md#mb-04-mihomo-直连设置提交恢复)。

Linux Helper 可按原 ID 只读核对系统代理效果和原归属。持续 guard 仍使用原代理归属事务。见[Linux 系统代理原生回执](Proxy-Safety.md#mb-04-linux-系统代理原生回执)。

Linux 直连设置按原 ID 核对宿主策略变更。guard 可完成已证实的本地提交，独立接管前在同一加密台账保留待核对责任。见[Linux 直连宿主策略变更恢复](Proxy-Safety.md#mb-04-linux-直连宿主策略变更恢复)。

真实端口漂移回退已复现旧监听关闭后的新连接缺口。该路径保留原待恢复责任和 resident 门槛。见[Linux 宿主代理切换验收矩阵](Proxy-Safety.md#mb-04-linux-宿主代理切换验收矩阵)。

有在途端口变更时，原 guard 可在其工作作用域内只读核对控制器与 Helper。候选已确认且真实监听可用时，guard 继续维护已保存代理策略，并在旧监听就绪前保留可用端口。见[guard 监听切换](Proxy-Safety.md#mb-04-系统代理-guard-监听切换)。

旧监听与宿主策略的显式补偿通过同一发布锁和独立 ID 接续。原终态保留补偿 ID，结果未知时继续占用原恢复责任。 丢失控制器应答后，可凭仅涉及端口和日志级别的原配置证据与真实监听状态接续，宿主效果仍按原补偿 ID 核对。见[补偿效果对账](Proxy-Publication.md#mb-04-直连监听补偿应答丢失的效果对账)。见[Linux 直连监听显式补偿](Proxy-Publication.md#mb-04-linux-直连监听显式补偿)。

完整 Linux Server 的直连活动订阅刷新在原确认落盘、SQLite 提交受阻期间持有工作租约。实际 Server 被终止后，由 Core 恢复原档案提交，原队列以同一 ID 和幂等键确认终态，Mihomo 继续转发。见[直连活动订阅队列的进程中断恢复](Proxy-Subscriptions.md#mb-04-直连活动订阅队列的进程中断恢复)。

完整 Linux Server 在原直连订阅重载成功但应答未送达时被终止。重启后原队列持久保留无完成时间的待恢复回执，发布检查点继续阻止新写入。原 Mihomo 保持转发。见[直连活动订阅重载应答未知](Proxy-Subscriptions.md#mb-04-直连活动订阅重载应答未知)。

批量订阅刷新在原工作租约内保存分项计划及派发状态，原队列只以发布或 SQLite 草稿回执核对已派发分项。未知分项继续占用持久责任，未开始的分项跳过。见[批量订阅刷新分项回执](Proxy-Subscriptions.md#mb-04-批量订阅刷新分项回执)。

未激活订阅的草稿替换先持久化原意图，进程中断后可在原工作租约内凭文件标记和摘要补交 SQLite 回执。未知证据继续承担写入和停用门禁。见[草稿文件替换后的原回执恢复](Proxy-Subscriptions.md#mb-04-草稿文件替换后的原回执恢复)。

草稿未应用路径在原工作租约内先持久化清理决定，再处理原暂存文件并确认未应用回执。只有原草稿和暂存证据核对后才进入清理。见[草稿替换未应用的原回执](Proxy-Subscriptions.md#mb-04-草稿替换未应用的原回执)。

排队订阅激活在同一工作租约内持有原发布 ID，重启由原发布权威接续档案事务，原队列可补齐已确认终态。见[排队订阅激活的原发布回执](Proxy-Subscriptions.md#mb-04-排队订阅激活的原发布回执)。

订阅激活的明确失败按原 ID 保留终态，应答未知继续占用原发布责任。见[订阅激活未知与失败的实机回执](Proxy-Subscriptions.md#mb-04-订阅激活未知与失败的实机回执)。

受管 Mihomo 候选已实际服务后停止时，生产 Helper 负责恢复旧配置。Server 工作租约覆盖原 Helper 应答，进程中断后按原回执核对终态和补偿结果。见[切换后的订阅激活补偿](Proxy-Subscriptions.md#mb-04-受管-mihomo-切换后的订阅激活补偿)。

直连 Mihomo 档案与设置发布在原发布锁内按需加载执行代次。实际 reload、结果核验、持久确认及临时文件清理结束后释放，HTTP 工作责任继续保护响应。Core 继续负责原检查点、系统代理和 TUN。见[直连档案与设置发布执行器](Proxy-Publication.md#mb-04-直连档案与设置发布执行器)。

显式 WebRoot 启用复用 WebServer 可回收执行代次。原 Nginx、站点配置、TLS 和未知回执继续由 Host 与原 Helper 边界负责。见[直接 WebRoot 启用执行器](WebServer-TLS.md#mb-05-直接-webroot-启用执行器)。

Linux Nginx 集成复用 WebServer 执行代次。原操作先保存暂存与派发边界，Host 在重启后清理原暂存或核对原 Helper 回执和快照。外部 Nginx 与 TLS 继续运行。见[Nginx 集成检查点](WebServer.md#mb-05-nginx-集成检查点与执行代次)。

Linux 系统包受管 Nginx 的启动、停止与重启使用同一可回收执行代次。实际作业、终态提交与清理结束前保留原工作责任。恢复只读原 Helper 记录，Nginx 服务继续由原 systemd 单元持有。见[启停与重启验收](WebServer.md#mb-05-linux-nginx-启动停止与重启的原回执)。

已知失败的 Nginx 集成允许原实例授权下的具名补偿。原台账持有唯一补偿 ID，实际文件、reload 与回执清理排空后释放执行责任。重启只核对补偿证据。见[集成具名补偿](WebServer.md#mb-05-已知-nginx-集成失败的具名补偿)。

Linux 系统包 WebRoot 的原工作 owner 持有站点计划、当前 Helper 子回执和清理责任。恢复只核对原子回执与文件摘要，外部 Nginx 和 TLS 保持运行。见[WebRoot 逐站点回执](WebServer-TLS.md#mb-05-webroot-逐站点回执与清理恢复)。

WebRoot 已知拒绝可经原实例授权发起一次具名补偿。原 owner 持有唯一补偿 ID，排空实际配置恢复、reload、终态和回执清理。重启只核对同一补偿证据，原 Nginx master 与 HTTPS 持续服务。见[WebRoot 具名补偿](WebServer-TLS.md#mb-05-webroot-已知拒绝的具名补偿)。
