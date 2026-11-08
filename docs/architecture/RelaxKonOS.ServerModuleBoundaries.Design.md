# Server 模块边界与内存回收设计

> 状态：分阶段实施，2026-11-05。已落地 MB-02/03 的 Git 仓库、CLI 与构建实现独立程序集及真实 ALC 回收，初始执行切片见 §10，仓库后续见 §11；MB-04 的 Proxy 共享读取、到期实例、guard/TUN 与发布恢复作用域边界已拆出，见 §12～22（含订阅代码预算、依赖守卫与 Linux 续核）。SystemPerformance S1 已冻结；MB-05A 三域后台工作、MB-06A 代次 owner 与 Terminal-T1 会话/PTY 排空阶段验收见 §38/39，后续 MB-06B/Terminal-T2 原生树实机验收见 §40，Firewall-F1 独立代码与 Tailscale TS-01 对象/持久责任验收见 §41；MB-01～08 实际状态见 §8。完整安装包 ABI、动态休眠、其余领域代码拆分与第三方 ModuleHost 闲时退出仍未实现。当前对象回收修复与测试证据由[驻留设计 §13](RelaxKonOS.ServerModuleResidency.Design.md#13-休眠内存引用审计与首批修复2026-11-03)拥有，不能作为本文整体完成的证明。
>
> 本文拥有为内存回收调整模块边界的目标、程序集依赖、资源所有权和迁移验收。[模块总设计](RelaxKonOS.ModulesAndExtensions.Design.md)拥有总体架构；[驻留设计](RelaxKonOS.ServerModuleResidency.Design.md)拥有状态机、租约与测量证据；[分发设计](RelaxKonOS.ServerModuleDistribution.Design.md)拥有安装和版本事务；[模块管理设计](RelaxKonOS.ServerModuleManagement.Design.md)拥有跨端产品和操作协议。Android 细节只在 Android 工程文档维护。

## 1. 目标与确定的决策

目标是使不使用的管理功能真正失去对业务对象、缓存、连接以及可选实现程序集的强引用，需要时按当前已安装的有效版本恢复。衡量对象是整个 Server 及其模块进程树，不能通过把内存移入另一个常驻进程声称节省。

1. 按持久职责、调用关系和资源所有权拆分，不按页面或现有文件夹机械拆分。Core 只保留必要的宿主基础设施和轻量调度；多个调用方使用同一实现不自动构成迁入 Core 的理由。
2. 官方模块继续遵循分发设计的进程内模型。实现移入独立程序集，经过对象生命周期迁移后再使用可回收加载上下文；不因本设计自动迁到第三方 ModuleHost。
3. 第三方代码继续只在 ModuleHost 执行，复用当前专用身份、IPC、代码信任和 Broker。为其增加闲时退出与按需启动，不开放第三方进程内执行。
4. 稳定路由、鉴权、模块目录、状态、调度和代理由 Host 持有；可回收实现不能直接注册进根 DI、Endpoint 或根 HostedService。
5. 安装、启停集合、更新、回退和移除继续遵循现有计划、重启和核实规则。内存唤醒固定使用当前生效版本；休眠不是版本切换或热更新入口。
6. 休眠不卸载磁盘文件、不丢失配置或操作台账，也不停止 Mihomo、sing-box、Squid、Docker、Nginx、Tailscale 等外部服务。外部资源变化仍由原领域事务与 Helper 决定。

不承诺固定节省量；不以强制 GC、裁剪工作集、缩短宽限期或中止用户工作代替边界调整。

## 2. 当前事实与结构性限制

| 当前入口 | 实际边界 | 调整要求 |
| --- | --- | --- |
| `Startup/Modules/`、`ServerModules.Catalog.cs` | Git 仓库/CLI/构建实现已拆出；Firewall、Tailscale 仍是 Host 内可销毁对象，其余领域仍有根服务 | 先消除根容器对重实现的持有，再独立项目 |
| `Residency/` | 协调器、依赖租约、后台工作、流所有权已存在 | 继续作为唯一准入和排空协调者，补充代码/进程释放观察 |
| `InstalledModuleStore.cs` 中的 `InstalledServerModule` | 官方包使用非 collectible ALC，启动阶段直接注册服务和路由 | 升级当前 ABI；仅打开 collectible 标志不足以回收 |
| `ModuleHostSupervisor.cs` | 已有第三方进程、信任和健康监督，生命周期随 Server | 把启动/退出接入驻留准入，保留原权限和故障隔离 |
| `ProxyModule.cs`、`ProxyRuntimeRegistration.cs` | 管理、引擎、恢复、安全、共享代理设置和运行时发布存在跨职责引用 | 为每种职责指定唯一所有者，不把整个 Proxy 搬入 Core |

对象回收、程序集卸载、磁盘占用及进程内存下降分别验收。对象释放成功只证明对象边界；调用 `AssemblyLoadContext.Unload()` 只表示请求卸载，仍需确认上下文与私有实现可回收。运行时依据见 [.NET 卸载文档](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)。

## 3. 目标层次与依赖方向

```mermaid
flowchart TD
    API[Host 稳定路由与认证授权] --> Gate[Core 驻留协调器与类型化代理]
    Schedule[Core 持久调度与恢复入口] --> Gate
    Gate --> Official[官方模块：进程内独立程序集和模块容器]
    Gate --> IPC[第三方模块：受限 IPC 与监督器]
    IPC --> Process[ModuleHost 专用身份进程]
    Official --> Ports[Host 窄服务端口]
    Process --> Broker[Core 类型化 Broker]
    Ports --> Domain[既有领域事务与授权]
    Broker --> Domain
    Domain --> Helper[既有 Helper 封闭操作]
```

图中的 Core 是职责边界，不要求立即把整个宿主基础设施移成一个新项目。项目拆分遵守以下方向：

| 层 | 放置内容 | 不得引用或持有 |
| --- | --- | --- |
| 共享 Protocol / `ServerModule.Abstractions` 及按领域的契约项目 | 稳定 DTO、租约/生命周期接口、类型化调用端口；直接演进现有抽象项目 | Server 实现、领域仓储实现、私有第三方依赖、模块实例或静态缓存 |
| Host 目录和入口 | 已安装身份、有效版本、静态路由、鉴权和合同校验、模块工厂描述 | 实现 Type、MethodInfo、已加载 Assembly、模块委托和私有 DTO 的永久缓存 |
| Core 基础设施 | 身份/会话、宿主限制、配置与秘密保护、基础存储工厂、审计、调度、Helper/Broker 通道、共享锁与通用恢复索引 | 完整订阅解析器、引擎注册表、应用级配置生成器或业务缓存 |
| 官方模块实现项目 | 每代独立容器、领域服务、配置生成器、业务缓存、任务与事件订阅 | 对其他模块实现项目或 Server 主程序集的引用；借用根容器注册自身 |
| 第三方 ModuleHost | 本模块执行器、私有依赖和工作数据 | Server 数据库/配置/Helper 秘密、其他模块数据或直接 Helper 连接 |

实现项目按 `ServerModules/RelaxKonOS.ServerModule.<Domain>/` 组织，首个 Git 执行项目及 `Shared/RelaxKonOS.ServerModule.Git.Contracts/` 已建立，其余名称在对应实施项中确定。Host 不通过对实现项目的静态引用、`typeof(Implementation)` 或启动期类型扫描加载它。发布和安装读取当前受信清单定位程序集，读取元数据不执行模块代码。共享契约只加载一次；模块包不能自带另一份契约造成类型身份分裂。

现有根服务按实际职责分配窄端口，而非把 `IServiceProvider`、`WebApplicationBuilder`、整个配置或任意 Helper 动作交给新模块。官方代码仍是受信代码；这些端口用于资源所有权和依赖控制，不被宣称为进程内安全沙箱。

## 4. Core、调度和可回收执行器

Core 保留稳定能力入口、存储和锁的基础设施；具体领域的配置格式、业务决策与执行仍属于相应模块。已发布状态、来源身份、业务配置、操作回执和恢复证明保存在原领域的持久存储，不能只存在于可休眠实例中。

Core 的轻量调度索引只存模块/实例/操作身份、工作类别、到期时间、修订、状态和恢复引用，不缓存完整订阅、证书挑战、引擎对象或领域实现委托。业务提交与调度登记须在可证明的一致性边界内完成：可共用事务时原子提交，否则使用持久 outbox 并在启动时对账；不能先返回成功再仅靠内存登记到期任务。

调度以至少一次唤醒设计，执行器用原领域 operationId、幂等键、修订和共享锁避免重复副作用，不宣称跨进程“恰好一次执行”。Core 和模块不能同时拥有同一恢复执行循环；迁移时先停止旧责任方，再以持久状态、代次和操作修订隔离确认新责任方接管。

有 TUN/系统代理承诺、证书挑战、到期授权或未完成安装/发布事务时，允许专用小型系统执行器保持保护租约。轻量化调度尚未完成前，原实现继续受保护，不能为了休眠先关闭安全职责。缺失、禁用、撤销或加载失败不能悄悄丢弃任务：保留故障和人工恢复入口，拒绝与未解决责任冲突的新操作。Core 只执行已经明确实现并授权的紧急恢复动作，不以模块不可用为理由新增通用命令或自行扩大 Helper 权限。

## 5. 各领域的目标边界

以下名称描述目标职责，不自动成为新的模块 ID、公开路由或可安装组件。ID、能力和包依赖只有随具体实施项同步更新全部调用方后才生效。

| 领域 | 常驻的最小部分 | 可休眠/可回收部分 | 阻止休眠的条件 |
| --- | --- | --- | --- |
| Git | 稳定仓库/构建端口、持久操作索引、共享文件/部署端口 | CLI 封装、仓库服务与写锁、构建执行器和内存日志 | 活动构建、CLI/镜像清理、未提交终态或待恢复操作 |
| Firewall | 固定路由、授权和 Helper 传输 | 平台查询/编辑 facade 与私有解析状态 | 请求或传输未结束；系统规则不随休眠删除 |
| Tailscale | 固定路由、身份/计划/持久回执端口 | 查询、登录与控制协调器、请求缓存 | 登录流、控制任务、未核实回执；外部 tailscaled 不受休眠控制 |
| Mihomo 管理 | 共享代理设置端口、任务/安全责任索引 | profile/订阅解析和编辑、诊断、管理缓存 | 发布/下载/订阅任务、流式诊断、安全租约 |
| Mihomo 运行与安全 | 按需的控制端口；有系统代理/TUN 承诺时受保护的安全执行器 | 引擎控制客户端、平台适配器及非必要缓存 | 运行时事务、TUN/系统代理恢复、仍依赖实例的消费者 |
| sing-box 入站管理 | 持久授权到期索引、现有安装/配置发布与恢复端口 | 入站配置生成、管理查询和编辑服务 | 发布/恢复事务、活动连接、到期执行尚未交接 |
| Squid 网关管理 | 安装/配置发布与操作索引端口 | 管理与配置生成、查询缓存 | 领域事务或待恢复操作；网关外部服务持续运行 |
| Docker 管理 | 被安装/部署使用的 Engine/出站代理窄端口、栈操作索引 | Compose 管理、代理编辑、镜像/容器等管理缓存和操作执行器 | 活动栈/构建/部署或清理、Engine 会话、未完成对账 |
| Certificate | 续期计划、挑战与操作索引、明确存续的挑战服务 | ACME 客户端、证书管理和续期执行器 | 有效挑战、签发/续期/发布或恢复尚未完成 |
| WebServer | 发布索引、稳定挑战/服务端口、恢复调度 | 网站管理、配置生成和发布执行器 | 进行中的发布、挑战引用、待恢复事务 |
| SystemPerformance | 订阅/权限入口和有界轻量状态 | 采样器、进程快照、图标解码/缓存、原生性能句柄 | 活动订阅、采样或输出未完成 |
| Terminal | 会话身份/目录、连接所有权和断线规则 | 无会话时的管理实现、平台 PTY 工厂与私有依赖 | 任一 PTY、可重连会话、Hub/输出流或未确认的子进程清理 |

### 5.1 Proxy 首先按四种职责拆开

将当前聚合 Proxy 边界拆为共享出站设置、Mihomo 管理与控制、sing-box 入站管理、Squid 网关管理；安全/恢复作为明确的专用职责，不挂在任一管理页面的生命周期上。管理与控制可在第一个切片中共用模块，前提是内部生命周期和依赖已经分离；不能把尚未拆开的代码宣称为四个可独立回收模块。

Docker 与运行时下载只依赖结构化的出站路由结果和必要的运行能力租约，不依赖订阅服务或整个 Proxy 模块。读取已保存的地址、旁路和认证引用不应唤醒管理模块；真正需要其控制客户端或持续安全承诺时才持有相应租约。不得把“代理流量正在经过外部 Mihomo”直接当作整个管理模块永久常驻的理由，也不得在未知所有权时撤下保护。

`ProxyRuntimeRegistration` 已在 Core 注册的运行时安装/发布骨干继续遵循原领域权威；后续分离时先提供契约端口，保留冻结计划、台账、锁、Helper 复核及原操作成功证明，不在驻留协调器重写安装器。遵循[受管运行时设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)，本设计不关闭 MR/CC 的宿主生命周期验收。

### 5.2 缓存、会话与存储

缓存需要有所有者、字节/条目上限、闲置释放时点和失效规则。现有性能历史与图标已有条目限制，但静态缓存不会随模块容器销毁；迁移时将可选重缓存变为每代所有，必要的小型共享缓存单独计量。权限隔离的进程快照不能为了省内存合并；授权信息不通过缓存继续授予旧权限。

Terminal 断连不等于会话结束，不能将客户端退出直接变成 PTY 关闭。若重型原生资源无法可靠从进程内回收，该模块暂时报告代码不可回收并保留原因；是否采用新的官方执行进程另立决策，不擅自改成第三方 ModuleHost 模型。

DB 工厂和存储模式可由 Host 提供，每次工作创建并及时释放上下文；模块不得长期持有 scoped DbContext、HttpContext、借用连接或别的模块裸服务。业务历史、构建日志和配置应持久化/分页读取，不能在每次唤醒时把全部历史加载到内存。

## 6. 官方模块运行 ABI 与程序集回收

### 6.1 从启动注册转为按代次激活

当前 `ConfigureServices(IServiceCollection, ...)` / `MapEndpoints(...)` 把包代码挂到根容器，不适合作为可回收实现的运行 ABI。目标是由 Host 映射其支持的固定路由，并通过共享领域契约调用每代模块运行时；模块激活只接收窄 Host 端口和本模块上下文，创建自己的服务容器。模块可以注册服务到自己的容器，但不能修改根 DI、Host 中间件或永久路由。

生命周期在现有运行时抽象上演进，覆盖创建/初始化、类型化服务解析、释放预检、后台工作排空和异步销毁。初始化恢复完成后才发布 Active；加载中不暴露半成品服务。激活后新增后台任务必须先取得代次租约；运行时主动报告待恢复职责，不能只以请求数为 0 判断安全释放。

ABI 变更时直接更新唯一当前契约、Host API 范围、官方模块实现、安装清单、签名样例、SDK、打包工具、测试与文档，重新构建受影响包；不保留旧入口桥接、双 ABI 解析或失败后退回启动注册。执行模式由明确的当前契约确定，不根据卸载失败静默切到另一种宿主。

### 6.2 强引用清理规则

Host 保存工厂元数据和共享契约代理，不保存模块实现的 Type/委托/MethodInfo。模块私有 DTO、异常对象、流、异步枚举器和反射元数据不得作为根缓存内容；错误转成有界的共享问题快照，详细日志的提供程序也不得持久保留原模块对象。序列化元数据、DI 激活缓存、事件订阅和诊断钩子必须在真实卸载测试中审计。

每代拥有的 HttpClient/handler、计时器、CancellationTokenSource、注册回调、事件处理器、文件监视、DbContext、原生句柄及连接均有销毁清单。借用的 Host 资源只释放本代引用/租约，不被模块销毁。跨模块只保存稳定端口，调用/实例依赖通过协调器取租约；环依赖拒绝，不能通过全局 service locator 绕过。

程序集回收按“停止准入 → 排空现有租约和后台工作 → 检查/交接持久责任 → 销毁资源 → 断开所有引用 → 请求 ALC 卸载 → 观察弱引用回收”执行。请求在卸载阶段等待受限的新代次；未确认旧副作用执行者已结束时禁止创建第二代。原生库、终结器或框架缓存阻止回收时记录阻止原因，不吞掉异常后伪报成功。

对象运行时 Dormant 与代码释放结果分开：目标诊断记录释放层级（对象/程序集/进程）、请求时刻、观察时刻、代次和未完成原因；具体 DTO 由管理协议实施项统一定义，不在本文新增公共字段。ALC GC 尚未发生时可报告“对象已释放、代码回收待观察”；不能报告“DLL 已卸载”。设定有限观察预算，超时保留弱引用诊断并阻止代次无限积累，按管理流程安排受控重启，不在每次业务请求上强制 GC。

## 7. 第三方 ModuleHost 按需运行

监督器保持身份/信任/有效版本等轻量元数据，Dormant 时不保留 ModuleHost 进程和其 IPC 连接。业务调用仍先做 Host 认证、模块访问授权和能力检查，再以同一准入锁取得代次租约，并发调用共享一次启动。每次启动重新核对当前代码信任、撤销状态、有效版本、制品摘要和专用身份，建立新的 IPC 会话和服务令牌；旧代次回调不能作用于新代次。

IPC 请求、响应流、模块回调、批准中的 Broker 操作和持久后台任务全部纳入工作保护。普通业务流量不自动重放；IPC 断开时，Broker 继续按原持久 operationId 记录真实结果，模块重开查询，不以连接死亡推导安装或配置成功。常设许可仍重新核对范围、期限与撤销，不因自动唤醒而重新授信。

闲时先请求合作排空，确认无子任务、回调和待提交回执后关闭连接并等待进程/属于该模块的子进程树结束，再进入 Dormant。不能只证明父进程退出。排空超时不能默认杀进程并报告成功；进入故障/待恢复，管理员的明确终止操作遵循已有授权与后果展示。若故障需要强制隔离，只处理可证明属于本代的进程资源，领域事务继续按持久回执核实，不以强杀充当成功补偿。

未知后台工作责任、还不能提供完整租约的模块保持加载并说明原因。模块缺失、禁用、过期或未确认信任时不能被调度器自动下载、安装、启用或提权。

## 8. 实施顺序与跟踪项

以下为 2026-11-05 的实际状态，区分阶段性交付和整个 MB 完成。历史实施证据见后续章节；通过小项测试不自动关闭整个 MB。当前推进以模块里程碑为单位，执行台账见 §38。

| 编号 | 实际完成与证据 | 剩余阻塞 | 整项验收条件 |
| --- | --- | --- | --- |
| MB-01 | **部分完成，整项开放**。MB-01A 资源清单与分层基线见 §42。[MB-01B](RelaxKonOS.ServerModuleBaseline.20261106.md) 验证双平台启动期采树、交错认证 Git、自然释放和 Terminal Hub，Windows 另完成真实 Firewall 三轮 | Linux 同制品的真实 Helper 联合矩阵仍缺，System 模式探索中终端退出证明缺失且共享 Helper 触发宿主代理恢复。外部共享服务归属、长时间与非空仓库负载继续开放 | 隔离 Helper 和宿主持久恢复责任后补 Linux 全量联合矩阵，保留缺失与原始证据，关联 RES-01/03 |
| MB-02 | **部分交付**。Git Contracts/独立实现和窄 Host 端口，Proxy 订阅转换契约/私有实现，Firewall-F1 独立契约/执行代码及构建门禁；回收验证见 §10/11/20～22/41 | 全官方包仍非唯一 collectible ABI；其他模块尚有根服务和实现依赖 | 全契约不引用 Server/实现；Host/模块互引构建门禁；发布包与私有依赖按唯一当前 ABI 验证 |
| MB-03 | **Git 有限切片交付**。真实仓库/CLI/凭据/控制台与构建执行项目，100轮 ALC 和保留完成请求验证（§10/11）；镜像清理责任与真实 Engine 标签检查见 §43；派发检查点和跨进程恢复见[构建派发对账](#mb-03-git-构建派发检查点与重启对账)，专属仓库见[HTTPS 源仓库夹具](#mb-03-专属-https-源仓库夹具) | 真实 Docker/BuildKit 执行中取消、清理、恢复与 ALC 联合矩阵仍受 RootlessKit 启动限制；生产内存/冷启动缺失 | 真实任务/流/持久故障责任结束才回收；两平台循环不累积句柄/代码代次；实际引擎恢复与资源目标达到门槛 |
| MB-04 | **常驻边界阶段交付**。共享读取、guard/TUN、到期与发布恢复单一 owner，后台工作租约、订阅转换预算及独立代码（§12～22）。完整认证入站发布恢复、[Core 同次停机排空](#mb-04-同次停机的-proxy-安全责任与恢复)、[宿主代理切换](#mb-04-linux-宿主代理切换验收矩阵)和[直连监听显式补偿](#mb-04-linux-直连监听显式补偿)已有 Linux 实机证据。[订阅后台执行图](#mb-04-订阅后台执行图的可回收交接)使用原持久批次计划和原操作 ID，[直连档案与设置执行器](#mb-04-直连档案与设置发布执行器)共用原发布检查点 | Proxy 整体仍 resident。补偿 reload 应答丢失继续待核对，完整网络故障矩阵、管理执行图交接和动态休眠未完成 | 系统代理/TUN/到期责任在重启及休眠后连续可靠；未知结果不重放；正式包/代码释放与生产收益分别验证 |
| MB-05 | **MB-05A 常驻后台责任阶段交付**（§39）。三域真实工作租约、准入/Stop 同锁、实际任务及清理排空。Docker 在途停止与原回执恢复见 §45，独立 Pebble、Nginx、Engine 实测见 §52～54。完整认证网站 WebRoot 签发、崩溃恢复、取消与 SIGTERM 联验通过。[Docker Compose](#mb-05-docker-compose-独立执行程序集)、[ACME SDK](#mb-05-acme-sdk-独立执行程序集)、[网站发布](#mb-05-网站发布独立执行程序集)和[直接 Nginx 重载](#mb-05-直接-nginx-重载独立执行程序集)已接入独立执行程序集 | 三域仍 resident。Nginx 集成、WebRoot 启用与启停执行图、完整故障矩阵、缺失身份的 CA 订单核实与整机掉电开放 | 后台 owner、终态与禁用门槛通过。按真实工作与安全责任边界推进独立执行器和动态休眠 |
| MB-06 | **MB-06A/06B 阶段交付**（§39/40），Windows LocalSystem/S4U 安装夹具及 Core／Helper 崩溃恢复实测通过（§48） | 正式发行服务全链、Linux 固定启动器跨代故障、SDK 合作 drain/后台任务申报仍开放 | 整项仍须合作 drain/后台申报、正式安装多代信任/撤销/崩溃矩阵与旧回调隔离，验收后才启用闲时退出/重启 |
| MB-07 | **SystemPerformance S1 已交付并冻结范围**（§23～37）；Firewall-F1 代码回收与 Tailscale TS-01 持久责任跨对象代次验收交付（§41）；Terminal-T1/T2 阶段交付（§39/40） | SystemPerformance 与 Terminal 仍 resident，生产收益/完整原生压力未测；Terminal 当前账号 Windows 树与 Linux 本地/永久降权 Helper 树已实测，Windows 安装 Helper/S4U、宿主崩溃/孤儿恢复矩阵仍开放；其余模块代码边界未验证 | Terminal-T1/T2 会话/当前原生树阶段已验收，断线不杀会话；整项还须安装/崩溃/压力矩阵及收益证据，再决定独立程序集，不能将 S1/T2 当作整项关闭 |
| MB-08 | **未完成**。已有当前诊断 DTO/跨端管理契约及局部打包/ModuleHost 验证 | 安装包非完整目标 ABI；真实业务同构建内存/冷启动门槛、完整平台故障矩阵未有 | 发布包/私有依赖/各端调用一致；Windows/Linux 整个进程树与冷启动 p50/p95、错误率达到预先记录门槛，关联 RES-06/07 |

首个官方代码卸载试点采用 Git，不直接从 Proxy 全量拆分开始。MB-04 的依赖审计可与 MB-01 期间推进，但安全执行器迁移必须依赖已验证的持久调度与租约；不能先删旧 worker 再补恢复。MB-06 可在官方 ALC 试点之外独立推进，其结果不改变官方宿主选择。

每个实施项提交必须同步模块、应用和追踪文档；涉及受管软件安装/更新/恢复时，先遵循受管运行时设计并更新相应 MR/CC 状态。包维护继续固定当前有效制品，睡眠和唤醒期间不得换成 latest。模块 ID 或配置键拆分直接更新所有内仓调用、测试和示例，不添加旧 `proxy` 别名或并存配置解析。

## 9. 验收与决策门槛

### 9.1 正确性与回收

| 场景 | 必须证明 |
| --- | --- |
| 未认证/无权限、禁用、缺包或未信任请求 | 不加载代码、不启动进程、不授予新权限；状态查询不唤醒 |
| 并发首次访问、取消、唤醒超时、过载 | 单次加载、有界等待；单个调用取消不破坏其他调用；副作用不自动重放 |
| HTTP 流、枚举、Hub/PTY、后台任务和依赖 | 完成清理前租约不结束，旧代次不能取得新代次保护，未完成责任阻止释放 |
| 释放异常、未提交终态、进程/Server 中断 | 原执行者和操作身份保持；不产生第二个执行者；重开观察真实回执 |
| 真程序集卸载 | 真实独立实现与私有依赖的 ALC/对象弱引用消失；根 DI、路由、反射/序列化缓存不保留实现；不能只测合成 buffer |
| 真进程释放 | 父进程、所有自有子进程及 IPC 消失；外部受管软件继续提供原业务；新会话拒绝旧 token/回调 |
| 安全/到期/续期职责 | 管理模块休眠后，既有承诺仍执行；模拟错过唤醒、撤销、过期、恢复失败并展示可操作故障 |
| 多轮循环 | 建议至少 100 轮并包含保留完成 Task/包装流等引用的调用方；存活代次和资源不会持续累积 |

测试允许在专门的可回收验证步骤触发 GC 来排除观察时机；生产运行不得依赖强制 GC。实测报告必须区分这两种环境。

### 9.2 资源与用户体验

同一 Release、同一 .NET/RID、相同有效模块、真实业务数据及连接集合，交错重复测量冷启动、首次请求、持续访问、无租约后的宽限期、再次唤醒及长循环。先预热，再至少 3 次独立运行，保留原始时间序列与范围。记录 Server 和 ModuleHost 整个进程树的存活托管堆、GC 提交空间、private bytes、RSS/可取得的 PSS、代码/私有依赖映射、线程、句柄、CPU/IO 和唤醒请求 p50/p95；不把共享映射简单相加当作系统物理内存节省。

每个试点在实施前从 MB-01 基线写明可接受的冷启动 p95、闲时总内存目标和错误率，结果必须超过重复测量噪声；未达到目标则保留已验证的对象释放边界，重新评估程序集拆分或该模块的默认驻留策略，不宣传未测收益。本文不凭合成测试指定统一 MB 或百分比，也不修改当前 300 秒宽限期、30 秒唤醒时限。

交付报告分别写明“实例释放”“代码回收”“进程退出”“生产资源下降”的实测范围。只有相应事实已验证才显示成功；休眠故障仍保留入口与原因，客户端请求不需要先手动唤醒。


## 10. Git 执行切片实施（2026-11-03，MB-02/03 部分）

本节保留首次 CLI/构建切片的证据；仓库实现迁移及当前扩大后的边界以 §11 为准。

### 已落地边界

- `Shared/RelaxKonOS.ServerModule.Git.Contracts/` 持有 Git 构建/CLI 接口、选项、共享问题类型及稳定台账模型。只读 `GitBuildLedger.InspectLedger` 不加载执行程序集，停用门禁仍可检查持久责任。Host 与模块共享 JSON 类型，私有实现类型不会进入持久台账的序列化元数据缓存。
- `ServerModules/RelaxKonOS.ServerModule.Git/` 持有 `HostGitCli`、`GitBuildCommandRunner`、`GitBuildService` 和按代次的 `GitExecutionModule`。该切片无 Server 实现引用；项目构建守卫只允许共享契约依赖，另有测试检查 Host 实际程序集引用。
- Host 的 `GitModuleRuntime` 继续作为原协调器唯一生命周期入口。`CollectibleGitExecution` 从部署目录 `Modules/Git/RelaxKonOS.ServerModule.Git.dll` 加载执行代码，使用 collectible ALC；缺失/链接载荷拒绝，不回退到旧内置实现。当前没有私有托管或原生依赖，不提供任意动态依赖扫描。
- `IGitExecutionModule` 是当前随产品发布的执行切片 ABI；上下文只有内容根路径、构建选项、数据保护、日志和工作租约端口，不传根 DI、完整配置或 Host 环境对象。该入口没有注册路由/根服务的能力。官方进程内代码仍是完整受信代码，这不是安全沙箱。
- 构建服务按线程安全 Lazy 每代创建一次；业务仍通过原类型化代理和后台租约执行。销毁失败保留隔离责任；成功销毁先清空实现引用，再请求 ALC 卸载。工厂仅保存弱引用，最多容纳 4 个尚未回收上下文，超过时拒绝再次加载并由原故障路径报告，避免无限代次累积；生产不强制 GC。
- Server 项目对实现项目仅有构建顺序引用（`ReferenceOutputAssembly=false`），编译时只依赖共享契约；构建/发布将执行 DLL 放入 `Modules/Git/` 并传播到引用 Server 的产物。测试工程可直接引用实现用于受控命令故障注入，不能据测试依赖误判 Host 静态依赖。

### 验证与限制

验证命令仍为 `--module-residency-checks`，已加入默认模块测试入口。真实 `GitBuildService` 在 collectible ALC 执行、凭据写入后休眠、重开原台账、100 轮上下文弱引用回收，以及刻意保留旧实现元数据时第 5 代拒绝均通过；测试 GC 仅用于观察。原有请求/流、依赖、构建取消、清理、宿主关闭及持久回执故障专项继续运行。凭据使用隔离的测试保护器与临时数据，不接触现有仓库或生产凭据。

Windows 最终驻留专项 **203 项**（`residency-windows-final.log`）、Linux 最终驻留专项 **210 项**及模块包 **141 项**（合计 351 条 PASS，`linux-final.log`）通过；原始构建/发布及基线复现证据均位于 `.artifacts/module-boundaries-20261103/`。Windows 的 Git 工作区 64 项及账户隔离/注册身份检查通过；Server.Tests 构建通过；framework-dependent .NET 10 Linux 发布已核对包含模块 DLL。完整 Linux 默认回归在第三方 ModuleHost 进程夹具报 `module-host-package-invalid`，变更前保留产物以同一命令也能复现；此前跨 Windows 打包的 apphost 还需补执行位。未修改生产 RID/包校验或安全环境清理来绕过失败，不宣称完整回归已通过。

这是随 Server 发布的官方执行程序集，**不是新增独立包安装器**。现有官方安装包的 `IInstalledServerModule` 启动注册 ABI 尚未迁移，未增加新旧 ABI 双解析。`IProblemException` 与后台租约接口已直接移到共享 Abstractions，Git 构建/CLI 接口和选项已移到 Git.Contracts；命名空间保持当前源码调用一致，不保留旧程序集类型转发。引用这些类型的内仓调用者和测试已同步构建，旧外部二进制应按当前依赖重新构建，不承诺二进制兼容。首次执行切片尚未迁移 `LocalGitRepositoryService`；这一限制已由 §11 的后续实施解除。静态路由、登记持久化和部署端口仍保持 Host 所有。

MB-01 的当前真实业务内存基线、MB-02 的全模块/安装包 ABI 和 MB-03 的真实构建引擎生命周期仍开放；仓库代码边界的后续进展见 §11。受控构建执行器测试不是实际 Docker/BuildKit 执行中的 collectible ALC 验收；弱引用收集也不证明生产 RSS 已下降。MB-04～MB-08 本轮未实施，未部署这批改动到生产 Server。


## 11. Git 仓库实现与 Host 端口（2026-11-03，MB-02/03 后续）

### 当前边界

`LocalGitRepositoryService` 的全部 partial 实现已移入 `ServerModules/RelaxKonOS.ServerModule.Git/`，覆盖仓库登记业务、分支/提交/历史/图、工作区/冲突/同步、凭据选择与控制台记录。`IGitRepositoryService` 直接移入共享 Git.Contracts，没有旧构造入口或 Host 内实现回退。Host `GitModuleRuntime` 只按共享接口转发；模块内仓库服务与构建服务分别用线程安全 Lazy 激活，共享同一可回收执行程序集。

Host 新增两种窄端口：

| 端口 | Host 拥有 | 模块接收 |
| --- | --- | --- |
| `IGitRepositoryStorage` / `HostGitRepositoryStorage` | EF DbContext 工厂、登记 CRUD、固定应用/用户/作用域下的受保护凭据 JSON 存储及短写入互斥 | 实体化 `GitRepositoryRecord` / `GitCredentialSetting`；无 DbContext、实体跟踪或 IQueryable |
| `IGitRepositoryHost` / `HostGitRepositoryAccess` | 每次调用解析当前身份、固定 Git Helper 操作、路径包含规则、三种冲突文件操作 | 当前模式/用户/家目录、结构化命令结果与共享文件 DTO；无 HttpContext、根 DI、任意 Helper 动作或任意可执行路径 |

上下文增加延迟激活的类型化端口，不把整个根服务容器传入模块。构建与模块状态查询不激活仓库存储；只有真实仓库调用才解析这些依赖。正常元数据查询返回前销毁 DbContext，Git 网络/进程操作不再持有数据库上下文。Host 依然负责普通用户/Helper 身份及底层文件访问，Git 参数策略、权限和租约不因模块迁出而放宽；现有数据库表、凭据保护 purpose 与 JSON 字段保持当前结构。

凭据更新只在短存储编辑期持有 Host 的固定写锁，不执行网络或 Git；替代原来跨服务实例保留的静态逐用户锁字典，避免持续积累用户 ID。读取/编辑单个 host 时按其精确 key 查询，列举/删除才读取该用户的相关集合。编辑口只能写 `accounts-` 命名空间内的固定应用设置，取消和异常通过异步销毁释放上下文与写锁。受保护凭据的持久模型移到共享契约，模块私有类型不会进入 Host 持有的默认序列化元数据缓存；模型本身不改变信任或授权。

仓库写锁与最多 500 条控制台记录仍按代次归模块；销毁时释放信号量、清空缓存，再由 Host 清除整个模块引用并请求卸载。静态路由、无状态编辑器桥、用户权限、元数据存储与外部 Git 工作树仍归原所有者；移除仓库登记不删除实际工作树。

### 验证

新增 `GitRepositoryAssemblyChecks`，接入 `--module-residency-checks` 和默认模块测试入口。真实 Git 初始化/登记、add/status、SQLite 元数据、并发账户保存、用户隔离与控制台记录后能回收真实 ALC；100 轮重开保持同一仓库与受保护账户，并执行真实 `git status`。调用方故意保留完成的凭据请求 Task，旧上下文仍全部可回收。

测试还在实际 Git 执行检查模式时断言最近使用的元数据 DbContext 已销毁，并验证凭据写锁的取消、越界 key 拒绝及后续正常获取。回收观察先退出模块 async continuation 的调用栈，再进行有界的测试 GC；不能在模块方法尚在栈上时把暂时未收集误判为永久泄漏。生产流程不增加等待 GC、反射清空框架缓存或强制回收。

- Windows 最终驻留专项 **217 项**通过；Linux 最终驻留专项 **224 项**及模块包 **141 项**通过（`linux-final.log` 合计 365 条 PASS）。
- Windows/Linux Git 工作区各 **64 项**通过，均包含账户持久化/凭据隔离及 Helper 注册身份检查。
- Windows 冲突处理 **89 项**、推送预览 **13 项**通过；Server.Tests 构建通过。
- framework-dependent .NET 10 Linux Server 发布成功，仍把当前执行 DLL 放在 `Modules/Git/`。本轮独立测试目录未覆盖生产 Server，未操作已有仓库、组件数据或生产凭据。

证据目录为 `.artifacts/git-repository-boundary-20261103/`：`residency-windows-final.log`、`linux-final.log`、`workspace-{windows-final,linux}.log`、`conflicts-windows.log`、`server-tests-build.log` 及 publish 日志。Linux 独立目录为 `/home/nanami/component-catalog-20261102/phase23/git-{repository,workspace}-tests/`。§10 中完整默认模块回归的既有 ModuleHost 夹具失败及基线复现仍是已知限制，本轮不宣称该整套回归通过。

### 仍然开放

本轮实现和实测范围是 Git 业务实现的程序集边界；未改变独立官方安装包的启动 ABI、公开模块诊断 DTO、模块集合启停/版本变更的重启规则。真实 Docker/BuildKit 的执行中取消/清理/恢复与 ALC 联合验收、业务压力下生产内存/延迟基线、其他领域拆分及第三方宿主闲时退出仍待实施。未根据测试 GC 的结果宣称生产 RSS 或整个进程树下降，MB-01/02/03 仍按各自完整完成条件追踪。


## 12. Proxy 共享读取与写入/安全边界（2026-11-03，MB-04 部分）

### 已实现

原 `IProxySettingsService` 将读取、修改设置和系统代理对账组合在同一接口中；`DockerProxyResolver`、订阅下载策略、安装材料选择等只读调用因而依赖 `MihomoSettingsService`，构造时连带控制器与发布执行图。现已直接拆成 `IProxySettingsReader`、`IProxySettingsWriter`、`IProxySystemProxyReconciler`，同步所有内仓调用、测试和验收工具，不保留旧接口、旧 Get 入口或类型转发。公开 HTTP 路由/DTO 和权限保持当前契约；GET 使用 reader，PUT 使用 writer。

Core 的 `ProxySettingsReader` 只依赖 `IProxyPlatformPaths`，读取 `mihomo-settings.json`，不构造引擎、控制器、安装/发布执行器，也不改设置、系统代理或 TUN。Docker 与宿主下载路由、订阅下载策略、配置事务的设置观察以及安装引导均改用该端口。该切片中配置写入、系统代理对账和 TUN 运行控制映射到同一个 `MihomoSettingsService`；§14 随后拆出对账实现，仍保持同一发布锁和副作用执行权威。

读取采用 64 KiB 上限和有界、固定文件共享语义；拒绝路径链接、空文件、目录替代、无效 JSON、重复属性、未知字段和无效端口/日志级别/宿主地址。只有文件或父目录确实缺失才返回产品默认值，损坏/不可读不能回落为默认监听端口。默认值、可选设置规范化及严格解析由 reader 统一拥有，冻结设置意图也复用该解析；不改变设置文件的有效字段、凭据保护或冻结身份。

受管代理被选中但设置不可读时，Docker/运行时出站解析返回原 `ManagedProxyUnavailable`，不继续默认端口或直连；独立的自定义代理选择仍按原作用域/旁路规则解析。保存地址必须为字面 IP；出站可用性还核对该地址属于本机，显式监听器匹配保存地址，不能凭其他本地监听器把远端地址标成受管可用。修复元数据并按原规则刷新解析后可以恢复。首次安装的只读材料选择遇到损坏设置映射为 `runtime.settings_drift`，在创建控制器凭据、原生工作或发布意图之前拒绝；已受理安装重放继续只使用原冻结材料，不额外读取当前设置。

Windows 用户态系统代理的 HKCU 原语及 bypass 拼接从管理类移至 `HostSystemProxyService`，消除 Host 平台适配器对 `MihomoSettingsService` 的静态引用。执行条件和行为沿用原实现：服务账户/非交互会话不得借此改用户代理，Linux 仍走原 Helper 的封闭操作。本轮没有实改 Windows 注册表或宿主系统代理的验收，不扩展原平台支持范围。

### 当前所有权与尚未交接的职责

以下记录为共享读取切片时的所有权；guard 对账的后续独立实现及当前引用图见 §14。

| 职责 | 当前所有者 | 本轮结果与限制 |
| --- | --- | --- |
| 已保存代理设置观察 | Core `ProxySettingsReader` | 已与写入/控制器构造图分离；读不唤醒管理对象 |
| 设置写入、系统代理对账、TUN 运行控制 | Core 同一 `MihomoSettingsService` 的三个类型化接口 | 保留单例/发布锁；尚未拆成可回收执行器 |
| 持久配置/安装/移除发布恢复 | Core `MihomoPublicationRecoveryService` 与原领域台账 | 未迁移执行权威；冻结、撤销、补偿和原操作回执仍由原实现复核 |
| 系统代理 guard / TUN 标记恢复 | `ProxyModule` 注册的 `SystemProxyGuardHostedService` / `ProxyRecoveryHostedService` | 原注册时机/循环保持；guard 分别持有 reader 和 reconciler，没有提前停止或新增第二个执行者 |
| sing-box 授权到期执行 | `ProxyModule` 的 `ProxyServerExpiryService` | 每轮临时解析执行实例，见 §13；未交接到 Core 调度，仍不能随模块停机丢弃责任 |
| OS 系统代理写入 | Core `HostSystemProxyService` / 既有 Helper | 平台原语归属已纠正，权限未扩大 |
| profile/订阅、Squid、入站业务管理 | 现有聚合 `ProxyModule` 服务图 | 仍有根服务/Scoped 服务和背景责任，未实施新模块 ID 或代码回收 |

Proxy **仍为 resident**，未新增可释放标记、休眠工厂或“已释放整个 Proxy”的状态。默认启用 Proxy 时，guard/恢复职责仍可能构造并保留管理图；本轮证明的是只读消费者不再引入该图，不证明默认 Server 常驻内存已下降。MB-04 的持久调度索引、职责交接、动态执行器及真实故障矩阵继续开放，不能用读端拆分跳过这些条件。

### 验证证据

- Windows 设置边界 **31 项**、Linux **32 项**（增加真实 symlink 拒绝）通过。测试把管理 writer 的构造器替换成会失败的计数器，验证未注册 Proxy 模块时的真实监听器/出站查询不会构造它；也覆盖损坏、重复/未知字段、超限、取消、目录替代与修复后恢复。
- 两平台系统代理能力/PAC 准入、apply/persist 失败补偿、guard 与禁用竞争、待发布拒绝检查通过；受控真实 HTTP 代理请求、旁路、各作用域和停止监听器拒绝直连通过。
- 配置发布专项 Windows **84** / Linux **85** 项；默认注册 **8** 项验证三类写入/安全接口为同一单例、guard 仅注册一次；安装发布 **88**、卸载发布 **92** 项通过（Linux 全部，Windows 安装/卸载亦通过）。未调用真实宿主安装或服务变化来冒充这些台账/传输夹具的范围。
- Server、Server.Tests、ManagedRuntimes.Tests 和 ServerModules.Tests 已构建；framework-dependent .NET 10 Linux 发布通过。未重新部署生产 Server，没有改 appsettings 或组件数据。

证据位于 `.artifacts/proxy-boundaries-20261103/`，Linux 最终合并记录为 `linux-final.log`；独立远端目录为 `/home/nanami/component-catalog-20261102/phase23/proxy-boundary-{server,runtimes}/`。Windows 首次受限沙箱运行安装夹具遇到文件替换权限错误，允许沙箱外文件操作后最终 88 项通过；出站测试夹具的 Accept/Dispose 停机竞态亦已修正为仅在取消后接纳 ObjectDisposedException。不把这些失败运行作为通过证据。物理内存/长期 CPU 与整个 Proxy 生命周期仍须由 MB-01/04/08 后续实测。

## 13. sing-box 到期调度与执行实例分离（2026-11-03，MB-04 部分）

`ProxyServerExpiryService` 不再在构造时持有 `ProxyServerService`，只持有作用域工厂与日志。启动立即检查、此后每 30 秒检查；每轮在独立异步作用域解析 `IProxyServerExpiryExecutor`，等待完成并释放作用域后才等待下一轮。作用域局部变量位于独立异步方法，不留在长期计时器状态机上。领域失败记录后继续下一轮；停机同时覆盖正在执行和等待计时器两种取消路径。

`ProxyServerService` 与到期执行接口改为 transient：HTTP 请求或已排队的领域操作持有自己的实例直至完成，后台计时器不再给管理实例增加单例根引用。`ProxyServerStore` 仍是 Core 单例，所有实例共用原状态锁；到期继续读取受保护材料、仅剔除已发布授权中的失效凭据，不发布未应用草稿，保留原发布/对账/补偿实现。没有新增第二个到期循环，也没有转移 Helper 权限或改变运行时安装生命周期。

这只是对象持有边界：引擎注册表等依赖仍有独立常驻引用，Proxy 仍为 resident；调度器仍由 `ProxyModule` 注册，尚无跨禁用的持久调度索引、租约交接或 collectible Proxy 实现。不能据此宣称程序集卸载或生产 RSS 降低，MB-04 继续开放。

Windows / Linux 验证使用生产 30 秒周期，覆盖懒构造、失败后释放、下一轮新实例、成功后释放、等待期间弱引用回收、执行中取消并等待异步释放；同时运行原 Proxy Server 配置、加密、凭据、订阅、发布、回滚、到期、导出与幂等回归。Windows 默认注册专项由 8 增至 **10** 项，新增非单例执行器与单一到期调度器检查。证据保存在 `.artifacts/proxy-expiry-boundary-20261103/` 的 `windows-final.log`、`linux-final.log` 和 `registration-final.log`，本轮未部署生产。

一次使用错误参数的 Windows 注册运行实际进入 ManagedRuntimes 全套，并在 `LifecycleChecks` 的伪造制品布局授权断言失败（`registration-windows.log`）；未诊断或修复此全套失败，不将它作为通过证据。改用当前无前导横线参数后，独立注册专项 10 项通过；本节只宣称上述专项及 Proxy Server 两平台回归通过。

续核（2026-11-03）：确认上述实现、测试与日志均已落地，保留全部未提交实现。补强执行中取消测试：执行器的 `DisposeAsync` 在测试闸门上真正异步等待，断言 `StopAsync` 在闸门释放前不能完成，释放后才确认作用域销毁和停机完成。当前 Windows Release Proxy Server 全回归及注册专项 10 项再次通过，记录为 `windows-disposal-recheck-final.log`、`registration-recheck.log`；新增闸门检查尚未重跑 Linux，前述 Linux 记录对应补强前测试。默认并行构建无诊断失败，改用 `--no-restore -m:1` 构建成功；沙箱内回归在临时状态文件原子替换时被拒绝，批准沙箱外运行后通过，失败记录 `windows-disposal-recheck.log` 不作成功证据。

下一切片先审计系统代理 guard、TUN 恢复与发布补偿的根引用、共享锁和持久责任，再确定窄执行端口；在持久调度交接及故障验收完成前继续保留现有 worker。此次不扩大为 Proxy 休眠、不移交安全责任，也不关闭 MB-04。

## 14. 系统代理 guard 窄执行边界与安全责任审计（2026-11-03，MB-04 部分）

### 根引用、锁与持久责任

本表记录 guard 切片完成时的引用图；TUN 与 writer 的后续分离见 §15。

| 职责 | 根引用及当前锁 | 持久责任与本轮处理 |
| --- | --- | --- |
| 系统代理 guard | 根 HostedService → reader + `IProxySystemProxyReconciler`；原接口映射到设置单例，从而带入控制器客户端和配置发布图 | 拆为 Core 单例 `MihomoSystemProxyReconciler`，仍取得唯一 `MihomoPublicationStore.Gate` 后读取设置及执行。启动失败继续保留 startup 标志，原间隔与重试规则不变 |
| TUN 启停及启动恢复 | `ProxyRecoveryHostedService`、生命周期 recovery 接口及管理调用 → 同一 `ProxyTunSafetyService` → `IProxyTunRuntimeController` → 设置单例；安全服务私有 `_gate`，随后取得发布 gate | `proxy-tun-recovery.json` 先于网络变更落盘，区分未完成转换与已激活会话；先停引擎 TUN 再恢复/核实管理路径，失败保留 marker。未移动、改锁或停止此责任，仍会构造管理图 |
| 配置、安装、移除发布补偿 | Core `MihomoPublicationRecoveryService` 长期持有三个发布协调器；各协调器共用 publication gate，授权回调在持锁期间不重入 | `mihomo-publication.protected` 保存已确认身份、Pending 冻结计划、设置/档案意图与完成回执；按原 operationId 观察 Helper、确认或补偿。busy/helper 不可用继续原操作重试，不能重新准备或以当前健康推导成功。此次不改注册时机、执行器或恢复循环 |
| OS 系统代理 | Core `HostSystemProxyService` → 既有特权传输；Linux Helper 使用环境 provider 的同一命名 mutex | Linux `/var/lib/relaxkonos-system-proxy/recovery.json` 与所有权快照仍由 Helper 事务拥有；Windows 仍为交互用户 HKCU 原语，不宣称具备同等持久 journal。无新增 OS 权限或真实系统代理写入验收 |

TUN 的安全锁不能简单改成每请求对象锁；发布 gate 也不能按新执行实例复制。恢复 worker 根引用不是全部可删除的缓存：在持久责任、执行租约和禁用交接得到验证前必须保留。当前锁顺序和责任单一性保持原实现，本次不宣称全部跨域锁竞态已验收。

### 已实现的窄边界

系统代理对账从 `MihomoSettingsService` 直接迁至 `MihomoSystemProxyReconciler`，旧类不再实现 reconciler 或保留转发方法。writer 与 TUN 控制仍是同一设置单例；guard 不再引用该单例、直接控制器客户端或配置发布执行器。所有调用方与注册测试同步使用当前接口，没有兼容别名。

已发布状态的检查提取为无缓存的 `MihomoManagedPublicationVerification.VerifyAsync`，供 writer 与 guard 在原发布 gate 内复用：只读取现有控制器秘密，核对 active YAML 身份，查询真实 Helper 的受管归属、活动修订、pending/recovery 和配置身份。撤销不生成凭据；不明或漂移时返回原 recovery-required，不能写 OS。guard 仍依赖运行时只读观察、授权和传输图，并未使整个 Proxy 脱离根 DI。

### 验证与范围

新增注册构造陷阱：替换设置管理单例和控制器客户端为抛异常工厂后，生产 guard 与 reconciler 仍能构造。系统代理夹具用独立 reconciler 与 writer 验证共锁、禁用后不复活、startup 清理、pending 拒绝，并增加等待锁取消及后续可用检查。发布夹具增加凭据撤销、YAML 漂移、设置损坏、pending、Helper recovery 与外部所有权六种 guard 拒绝场景，各自核对无宿主写入、无原生配置执行且锁可再获取。

Linux 在用户指定的 `192.168.1.9` 测试机独立目录 `/home/nanami/component-catalog-20261102/phase23/proxy-guard-boundary-20261103/expiry/` 已补跑 §13 新增的真实异步释放闸门检查与完整 Proxy Server 回归，通过；首次旧地址超时和 SSH 历史密钥不匹配均未作为成功。用户确认密钥变更后固定核对观察到的测试机密钥，未修改本机 known_hosts。产物和日志仅用于测试，没有替换运行中的 Server/Helper。

Windows/Linux 默认注册各 **11 项**、配置发布事务分别 **96/97 项**、系统代理共锁/取消与 TUN 专项均通过。Release Server.Tests 构建及 Linux framework-dependent 发布通过；runtimes Linux 发布首次缺少 RID 还原目标，完成正常 restore 后成功。证据统一保存于 `.artifacts/proxy-guard-boundary-20261103/`：`expiry-linux-final.log`、`system-proxy-{windows-final,linux}.log`、`registration-{windows-final,linux}.log`、`publication-{windows-final,linux}.log`、`tun-{windows,linux}.log` 以及构建/发布日志；复跑命令见同目录 `verification.txt`。测试使用隔离状态、伪造 Helper/系统代理后端和受控网络，不证明真实 OS 代理、TUN 或安装故障的端到端验收。

Proxy 继续 **resident**，MB-04/RES-05 保持开放。下一边界需先为 TUN 运行控制与配置写入明确当前意图核验端口，再验证完整启停/恢复/补偿和锁竞争；不能先把安全服务改为 transient 或移除恢复 worker。Core 持久调度、跨禁用安全责任交接、collectible Proxy 与生产内存测量仍未实现。

## 15. TUN 运行控制与设置写入执行边界（2026-11-03，MB-04 部分）

### 本轮实现

`MihomoSettingsService` 只实现 `IProxySettingsWriter`，不再实现或转发 `IProxyTunRuntimeController`。TUN 的设置读取、实时配置改写、路由排除、controller 可达性/重载、回滚及 live flag 观察直接迁到 `MihomoTunRuntimeController`。没有保留旧方法或双入口，公开 HTTP/配置契约不变。

Core 保留单例 TUN 控制器，依赖 paths、reader、controller、publication store 与设备观察端口；它不依赖 settings writer、配置发布执行器、系统代理 writer 或安装器。原 Windows 适配器确认移到 `IProxyTunDeviceObserver` / `HostProxyTunDeviceObserver`：同名活动网卡、10 秒等待与取消规则保持原实现，Linux 仍无该 Windows 专项等待；测试注入受控观察器，不创建真实 TUN。TUN 重载成功不等于 Windows 适配器已激活的门禁仍然存在。

设置 writer 的具体类与接口改为 transient。当前内仓产品调用者只有设置 PUT 入口；每次调用持有自己的 writer，完成后根 DI 不再保留该管理实例。writer 本身没有计时器、私有锁或需迁移的实例缓存，controller/运行时/发布依赖仍按原所有者驻留，不宣称这一步释放这些依赖或已测得堆/RSS 收益。

### 锁、恢复与补偿

`ProxyTunSafetyService`、`IProxyRecoveryService` 映射及启动恢复 worker 保持原单例/注册，不把安全锁改为每调用私有锁。顺序仍为 safety gate → publication gate；writer、guard、TUN 控制与发布事务共用同一个 `MihomoPublicationStore.Gate`。writer 不在持有 publication gate 时反向获取 safety gate。新 DI 构造陷阱验证 TUN startup recovery 在 settings writer 构造即抛异常时仍可解析。

TUN marker 仍在接触引擎前落盘。reload、设备确认或管理路由核验失败继续由原安全服务驱动停 TUN 与管理路径恢复；恢复失败保留同一 marker。reload 中取消可能已替换 YAML，不推断已经回滚；marker 保留，随后启动恢复重新取得两把锁完成 teardown。controller 原先不可达时不发 reload，恢复未核实不清 marker。设置写入继续保留 live TUN flag 与精确 exclusions，不把普通设置更新当成 TUN 转换。

**没有新增受管发布下的 TUN 支持**：存在 confirmed publication 或 pending 时，直接 TUN 启用和停用仍走原 `UncoordinatedWriteProblemAsync` 拒绝，不能绕过授权、发布或网络事务门禁。若因此不能完成安全恢复，仍保留恢复阻止与 marker，不能伪报已清理。Core 三个发布协调器及恢复 worker、冻结计划、Helper 回执、settings/profile 补偿意图均未迁移或削减。

### 验证与未完成边界

新增 `ProxyTunBoundaryChecks`，接入 `--proxy-tun-boundary-only`、`--proxy-tun-only` 与默认 Server.Tests 入口。24 项覆盖真实 TUN 执行器与安全服务组合：启停、marker-before-engine、路由排除、writer 保留 live TUN、reload/设备/管理路径失败补偿、不可达恢复、未知观察、pending/confirmed 拒绝、平台补偿失败重试、reload 中取消及恢复、writer/TUN/安全停机竞争和等待锁取消。这里的“真实执行器”指生产 C# 实现；controller、设备与网络平台是受控夹具，不代表实机 TUN 或 Windows Wintun 验收。

Windows/Linux Release 新边界各 **24 项**、完整 TUN 与系统代理专项、默认注册各 **13 项**、配置发布事务分别 **96/97 项**通过。Server.Tests/ManagedRuntimes.Tests 构建和 framework-dependent Linux 发布成功；存在既有 nullable/未使用参数/平台分析警告，Helper 构建另有无法取得 NuGet 漏洞数据的 NU1900 警告，不宣称完成漏洞检查。Linux 在已确认测试机的 `/home/nanami/component-catalog-20261102/phase23/proxy-tun-boundary-20261103/` 独立目录执行。证据目录 `.artifacts/proxy-tun-boundary-20261103/`：`tun-windows-final.log`、`tun-linux.log`、`system-proxy-{windows,linux}.log`、`registration-{windows,linux}.log`、`publication-{windows,linux}.log` 以及构建/发布日志；复跑命令见 `verification.txt`，源码与 Linux 产物摘要见 `provenance.json`。本轮保留全部未提交实现，未提交、推送或部署，也未更改生产配置/组件数据。

最终结果：完整 solution Release 构建通过（0 错误、2 个既有 Server 告警）。Windows 模块默认全量及最终 ModuleHost 定向检查通过；Linux 模块默认全量在已补齐 Git 的隔离镜像通过，包含真实子进程和异架构包拒绝。最小 ASP.NET 镜像首轮缺少 Git 的失败保留，不作为产品回归失败或通过证据。新增工作租约/独立代码专项 Windows/Linux 各 **23** 项；原 Core 安全所有权各 **17**、lifecycle/TUN scope 各 **31**、责任 **45/46**、订阅各 **37**、TUN 执行各 **30**、settings **31/32**、Mihomo 注册各 **15**、配置发布 **107/108**、安装发布各 **88**、移除发布各 **92**、profile 各 **19**、runtime HTTP 各 **46** 项通过，Squid、出站代理、入站原回归与到期/HTTP 队列专项均通过。本轮不重复原生归档/固定系统服务验收；保留 §19 已有证据的原范围。

Proxy 仍为 **resident**，MB-04/RES-05 保持开放。配置档案事务、引擎注册表、controller、TUN 安全 owner 与 Core 发布恢复仍有独立根引用；还没有按需执行租约、持久调度和跨禁用安全责任交接，也没有 collectible Proxy 或生产内存基线。下个切片须先审计发布协调器与 profile 执行图的持久意图和请求/后台所有权，不能因为 writer 已 transient 就移除补偿 worker 或给整个 Proxy 添加可释放标记。

## 16. 发布恢复临时作用域与 profile 执行图审计（2026-11-03，MB-04 部分）

本轮开始按用户要求将 §12～15 当前实现提交并推送到 `origin/master`，提交 `5bae1c72`。以下新增实现与文档保留未提交，没有继续推送或部署。

### 持久意图与执行所有权

| 入口/对象 | 持久意图和根引用 | 请求/后台所有权及本轮边界 |
| --- | --- | --- |
| 配置发布 | publication store 保存 candidate、冻结 configuration plan、settings/profile selection 与 completion；配置协调器引用授权、runtime、settings files 和可选 profile repository | 在原 gate 内验证，再持久 Begin；受理前可取消，受理后使用原不随请求取消的执行路径，最终按 Helper 同操作回执/身份确认或补偿。本轮不改事务实现 |
| profile 管理/激活 | Core SQLite profile repository 为单例，仅保留连接字符串，每次访问自行创建/关闭连接；模块 profile facade 为单例，配置事务/订阅服务为 scoped | 直接 profile 激活/apply 随 HTTP Task 等待；激活时读取持久 YAML，冻结 profile ID/revision、旧 active ID/revision 与 createdAt，不把请求服务或 YAML 委托写进恢复索引 |
| profile selection 提交 | `MihomoProfilePublication` 与 publication pending 对应，SQLite `proxy_profile_publication` 保存同 operationId 的选择 hash、配置身份及提交修订 | 选择切换与 SQLite 操作标记在同一事务内；恢复校验精确选择，成功重放不再增加修订。发布确认前复核 Helper 事实；冲突、撤销或未知状态保持 pending，不能从当前 active 推导原任务成功 |
| 订阅后台操作 | 单例 `ProxyOperationStore` 保存受理/阶段/终态台账；内存执行委托捕获 scoped subscription → configuration → publication 图 | 请求取消不取消已排队委托，Task 完成前仍由委托持有实例；尚未迁为独立后台作用域/模块工作租约，不能随请求结束释放或宣称可休眠。重启后的操作 interrupted 记录不是发布成功证明，已有 publication pending 另由 Core 恢复 |
| Core 安装/人工恢复 | 单例 `MihomoInstallationBootstrapService`、安装台账适配器和 `MihomoRuntimeRecoveryService` 仍分别持有部分发布协调器 | 这些是独立根引用，本轮未全部消除；安装、移除、手动重试的冻结计划、共享锁和授权 callback 生命周期不改 |
| Core startup recovery | 原 worker 构造持有三个协调器，配置分支连带 profile repository；现只保留 scope factory 与 logger | 每轮独立异步 scope，先读 pending，仅解析对应类型的协调器；无 pending 或无可恢复计划不构造任何协调器。选型读取只是提示，协调器仍在 gate 内重读/核对原 operationId，竞争变化不能执行旧意图 |

### 已实现的窄边界

`MihomoPublicationRecoveryService` 不再构造注入 store 和三个协调器；每轮使用私有 async 方法拥有 scope、pending 快照和选中执行器，完成后异步释放，再按原规则等待 5 秒重试。仅 busy/helper 不可用自动重试同一持久操作；operator-required、未知或不完整的恢复状态继续保留 pending 与日志，未新增 prepare/install/configure 重放或第二个恢复循环。旧构造入口直接删除并更新所有内仓测试及验收工具，没有兼容重载。

三个发布协调器改为 transient。它们不拥有独立实例锁、后台循环或未落盘的事务权威；所有实例仍共用 Core 单例 `MihomoPublicationStore.Gate` 和原持久状态。临时恢复实例不再被 worker 在重试等待或完成后持有，但安装/人工恢复等单例消费者仍可长期持有自己的实例，因此不能据此宣称整个发布/profile 图已释放。

scope 覆盖 `RecoverAsync` 完整 Task 和异步清理，作用域销毁完成前不启动下一轮。取消等锁仍只撤销当前等待，不能释放别人持有的 gate；已经进入原领域不可取消受理段的工作继续按原规则收尾。宿主全流程关闭上限、真实 Helper 中断及独立后台任务租约仍不在本轮证明范围。

### 验证与边界

新增检查覆盖无 pending 不构造任何协调器、创建 worker 懒构造、busy 失败后的真实异步 scope disposal、重试计时器等待中旧配置协调器弱引用回收、下一轮新实例、停机取消等待锁时保持 pending 与原锁所有权；配置、安装、移除三类选择均通过将未选类型工厂替换为抛异常来验证。测试 GC 只用于弱引用观察，生产路径不增加强制 GC。原配置/安装/移除发布回归继续验证同操作确认、补偿、消息丢失与重开；SQLite profile 专项继续覆盖原子选择与幂等。

Windows/Linux 最终注册各 **14**、配置发布事务 **102/103**、安装发布各 **88**、移除发布各 **92**、SQLite profile 各 **19** 项通过。ManagedRuntimes.Tests/Server 构建与 framework-dependent Linux 发布成功；Linux 测试目录为 `/home/nanami/component-catalog-20261102/phase23/proxy-publication-boundary-20261103/`。构建、发布与日志统一保存在 `.artifacts/proxy-publication-boundary-20261103/`，最终各专项为 `mihomo-*-only-windows-final.log`、`mihomo-*-only-linux.log`；复跑命令及产物摘要分别为 `verification.txt`、`provenance.json`。夹具使用隔离状态与伪造 Helper，未执行真实安装或改动生产服务。首轮沙箱内构建缓慢未完成并已中止，批准沙箱外构建后成功；既有编译/平台与 NuGet 漏洞元数据网络警告不作为已解决。

Proxy 仍为 **resident**，MB-04/RES-05 继续开放：独立安装/人工恢复根引用、订阅任务作用域与工作租约、持久调度/跨禁用责任交接、受管 TUN 网络事务、collectible Proxy 和生产内存测量仍缺。下一切片优先处理排队订阅工作的独立作用域及完整寿命，保留原受理、审计、发布补偿和中断记录，不先把模块标成可释放。

## 17. 订阅后台任务独立作用域与完整寿命（2026-11-03，MB-04 部分）

### 实现与所有权

三个排队订阅路由（refresh_all、refresh、activate）不再注入并捕获请求 scoped 的 `IProxySubscriptionService`。它们在原权限和幂等键检查下，只向单例 `ProxySubscriptionOperations` 传入固定操作枚举、订阅 ID、操作者字符串；HTTP 路由、202 与 operationId 契约不变，不把 HttpContext、请求令牌、用户 principal、服务实例或任意执行命令带入后台。

受理仍通过 `ProxyOperationStore` 原台账先持久保存 queued，幂等重放返回原 operationId，不创建第二个执行 scope。成功受理后执行使用独立 async scope，在其中解析 subscription → configuration → publication 图与原审计端口；scope 覆盖业务 Task、结果/异常失败审计和异步清理。清理完成后才由台账持久化最终状态，不把一个已完成业务审计事件等同于 scope 清理或整个操作成功。请求结束/取消不撤销已受理工作；原发布的冻结、受理后执行、恢复与补偿规则不变。

`ProxyOperationStore` 以同一个实例注册为 HostedService，按原准入 gate 跟踪所有已受理 Proxy 委托到业务、审计、scope 清理和终态写入完成；停机先关闭新键准入再等待完整任务。已知幂等键仍可重开查询；停机等待令牌只取消等待，不取消业务或抢先销毁资源。此跟踪也覆盖原 lifecycle/TUN 委托，不给它们新增取消语义。Host 关闭预算耗尽不能被解释为任务完成，进程中断后仍按原台账规则显示 Interrupted，不自动重放订阅或从当前发布状态推导原任务成功。

台账首次受理落盘失败会撤掉尚未执行的内存条目，不留下可幂等返回的假受理。终态无法持久确认时保留失败跟踪、阻止新操作并使排空报告失败。scope DisposeAsync 失败可能只完成部分释放：保留失败 scope 与异常，不自动重试 Dispose，不把它当成普通任务失败后清除恢复责任；即使随后失败终态落盘也失败，独立保留的 cleanup scope 引用仍在，后续操作被阻止。业务问题/抛异常、审计故障而清理成功时，仍按原失败终态落盘，不伪报成功；业务异常额外记录固定 OperationInterrupted 失败审计，不泄露异常正文。

### 验证

新增 `ProxySubscriptionBoundaryChecks`，接入 `--proxy-subscription-boundary-only` 和默认 Server.Tests 入口。Windows/Linux 各 **36 项**通过，包含生产三类分派、独立 scope、请求 scope 关闭/取消、幂等重放、真实异步释放、等待期间不写成功、正常停机排空/拒绝新键、scope 成功后弱引用回收、领域/异常/审计/释放失败、停机等待取消、终态落盘故障与重开 Interrupted。释放失败及释放/终态落盘双重失败均确认会保留 owner 并阻止新任务。

真实回环 HTTP 使用生产路由注册，验证未认证 401、缺幂等键 400 均不创建订阅实例，202/幂等重放仅创建一个 scope，HTTP 完成后操作者审计仍正确，实际 Host.StopAsync 等待异步释放。测试替换 subscription 实现和网络工作，不访问真实订阅源或安装软件。默认注册夹具已收窄为仅检查 Proxy 注册的 HostedService 工厂，避免构造缺失 SignalR/组件依赖的无关部署图；初次失败不作为通过证据。

Windows/Linux 注册各 **15**、配置发布 **102/103**、profile 各 **19**、安装发布各 **88**、移除发布各 **92** 项通过。两个测试工程 Release 构建与 framework-dependent Linux 发布成功；Linux 在已确认测试机的 `/home/nanami/component-catalog-20261102/phase23/proxy-subscription-boundary-20261103/` 独立目录执行。证据目录为 `.artifacts/proxy-subscription-boundary-20261103/`，构建、发布、复跑命令与产物摘要随日志保存。本轮保留 §16 及全部其他未提交实现，没有提交、推送或部署。

### 剩余边界

Proxy 仍为 **resident**，MB-04/RES-05 不关闭。独立后台 scope 和任务排空不是模块工作租约：尚未接入驻留协调器、持久调度与跨禁用责任交接；安装/人工恢复、引擎与平台服务仍有根引用。既有 operation 台账损坏处理/保留预算、故障诊断的独立管理入口、真实进程中断/发布补偿联合验收仍须后续处理。未证明 collectible Proxy、生产内存下降或真实网络事务。下一切片需把后台工作准入/停止与模块禁用检查的持久、内存责任统一审计，再决定租约端口；不能在任务未确认或清理失败时宣称休眠成功。

## 18. Proxy 任务准入、停机与禁用责任门禁（2026-11-03，MB-04 部分）

本轮开始按用户要求将 §16/17 已验证实现提交并推送到 `origin/master`，提交 `4dbb89d1`；以下新增改动保持未提交，不再推送或部署。

### 当前门禁

`ProxyModule.InspectOrphanedWorkAsync` 现在汇总两类事实：原 Mihomo publication authority，以及 Proxy operation 的内存/磁盘责任。启用宿主可从同一 `ProxyOperationStore` 的 admission gate 下观察活动执行、异步清理、终态确认与隔离资源；禁用启动不构造订阅/配置服务，而通过 Core paths 只读检查持久 ledger。publication pending 和不可读的原阻止规则继续保留，任何一个领域不确定都不能报可禁用。

Queued、Running、Interrupted，以及 Failed + OperationInterrupted 代表仍有未知执行责任，均阻止禁用；已确认 Succeeded、Cancelled 和已知领域问题 Failed 不因历史存在就永久阻止。live scope 清理失败即使已有 Failed 终态也有独立内存恢复 owner，继续阻止。成功清理、终态持久化并归还跟踪后解除活动阻止；stopping 本身不是持久未完成任务。故障不会通过重写历史、自动重放或从当前引擎状态推断成功来消除。

禁用检查只读，不执行 `LoadAsync` 的显示用 Interrupted 规范化，也不写台账、启动工作或创建状态目录。它同时重读磁盘，不让已加载缓存掩盖损坏；读取使用最多 32 MiB 的固定 ledger 文件，拒绝链接、目录替代、空/null/损坏 JSON、重复字段、未知字段、无效 state、重复 operationId/幂等键和缺少有效条目身份。只有实际文件/父目录缺失才当作无历史。该大小是拒绝未知历史的读取上限，不是已实现的历史裁剪/保留预算。

新键受理复用同一严格读取，在原 admission gate 内核对磁盘与内存已知条目；不一致或重启后仍有未知执行责任时拒绝新工作，避免覆盖旧责任。原幂等重开与只读查询保留，不以新键绕过中断。首次读取失败不再把 `_loaded` 提前设为 true 或忽略损坏 JSON，因此修复未知文件前不能悄悄以空历史受理。本轮没有新增强制关账/删除历史入口；需要未来可证明的操作恢复流程解除未知责任。

### 验证与范围

新增 `ProxyOperationResponsibilityChecks`，接入 `--proxy-subscription-boundary-only` 和默认 Server.Tests，Windows/Linux **37/38** 项通过；原订阅全寿命各 **36** 项继续通过。覆盖所有持久状态的禁用规则、字节不变、无文件不建目录、损坏/重复/未知字段、目录替代、已加载后磁盘损坏、重启未知责任拒绝新键、大小上限和取消传播；同一门禁生产方法用于禁用启动/组件目录事实。live 活动任务与成功排空后解除也已验证，原清理失败专项新增检查 live 责任仍被保留。Linux 另包含真实 symlink 拒绝。

Windows/Linux 默认注册各 **15**、配置发布 **102/103**、SQLite profile 各 **19** 项通过，原发布恢复和补偿代码未修改。两个测试工程 Release 构建及 Linux framework-dependent 发布成功，既有编译/平台及 NuGet 元数据网络警告未解决。Linux 独立目录为 `/home/nanami/component-catalog-20261102/phase23/proxy-task-responsibility-20261103/`。证据统一位于 `.artifacts/proxy-task-responsibility-20261103/`；复跑命令/源码产物摘要随日志保存。所有数据为隔离临时文件和伪造订阅/Helper，未改变生产配置或服务。

### 未完成

Proxy 仍 **resident**，MB-04/RES-05 不关闭。本切片只是任务责任观察和准入拒绝，不是休眠租约、持久任务调度、强制关闭或跨进程原子禁用。检查返回后的新工作仍由原管理重核、停机准入关闭/排空及重启门禁处理；不能将只读快照当作永久授权。TUN/系统代理/授权到期的跨禁用交接、安装/人工恢复的独立根引用、故障关账产品流程、真实宿主中断与补偿联合验收仍开放。下一步须统一审计这些持续安全责任的禁用门槛与任务租约所有权，不能仅凭任务 ledger 干净就宣布整个 Proxy 可禁用或释放。未验证代码卸载或生产内存收益。

## 19. Proxy 常驻责任与完整执行寿命收尾（2026-11-03）

本节为当前实现事实；§12～18 保留各阶段审计记录，其中“下一切片”和尚未交接的责任描述以本节为准。开始时已按用户要求提交并推送 `36e8ba25`；本节新增实现保留工作区，未提交、推送或部署。

### 所有权与根引用

| 责任 | 当前 owner、寿命与共享权威 |
| --- | --- |
| 系统代理 guard | Core 唯一 `SystemProxyGuardHostedService`；每轮 async scope 解析 transient reconciler，不持有 settings writer/引擎注册表；沿用 publication gate、原保存意图和原系统代理身份核验 |
| TUN 安全与启动恢复 | Core 单例 `ProxyTunSafetyService` 及唯一启动恢复 worker；安全平台、原安全 gate 与 publication gate 保持共享。启动只捕获一次 operationId/createdAt，失败每 30 秒重试同一代 marker；旧引用不能撤销随后建立的新会话。未知/损坏 marker 保留阻止与人工恢复，不尝试网络修改 |
| sing-box 到期与发布对账 | Core 唯一 expiry worker，每轮独立 scope 解析 `ProxyServerPublicationService`；执行器仅依赖 protected store、Helper 与模式，不依赖引擎注册表、证书加载或管理 facade。与管理共用 store gate、pending、原候选/前态摘要证明及补偿 |
| Mihomo 持久发布恢复 | Core 唯一 `MihomoPublicationRecoveryScheduler` 即时及每 30 秒扫描原 pending；每次选择原配置/安装/移除协调器，busy/helper unavailable 每 5 秒独立 scope 重试。等待仅保留操作身份，人工阻塞身份不自动重放；新身份重新处理，旧 scope 清理期间新建的 pending 不会被标成已尝试 |
| 管理请求与排队工作 | 引擎/registry/lifecycle/subscription 为 scoped，profile、配置 writer、入站与 Squid facade 按需创建。订阅、lifecycle、TUN、入站、Squid 排队工作全部在独立 async scope 内完成执行、审计及清理；受理不捕获请求 facade、HttpContext 或请求令牌 |
| 安装、原生运行时与组件 | 人工发布恢复端点按调用解析 transient recovery/coordinator；既有 Core installation/runtime authority 保持单例；其共享 gate、原生执行取消与恢复所有权仍必要，不以 transient 注册制造第二个 authority。组件和 Docker 共享读取端口不引入管理解析图 |

持久调度直接使用原领域 marker、protected published state 和 publication pending 作为意图来源，不另建可能与业务提交脱节的内存索引。跨管理禁用时 Core 仍执行原有授权的安全、到期和补偿责任，没有第二个 owner，也不扩展 Helper 权限。Squid 不新增自动发布重放；原未确认/恢复故障继续保留并阻止不安全禁用。

### 完整寿命与内存边界

所有排队工作沿用先持久 queued 再执行、幂等回执、稳定审计及非请求取消的受理规则。共享 `ProxyOperationScope` 覆盖业务、审计和 `DisposeAsync`，之后才能写最终状态；清理失败保留失败 scope，即使终态落盘再次失败也不能丢失 owner。停机在等待 admission gate 前关闭新键准入，即使等待令牌已经取消；取消只终止等待，不撤销已受理任务。未知重启责任仍显示 Interrupted 并阻止新任务，不从当前运行状态推断成功。

根 `ProxyOperationStore` 不再缓存全部历史 DTO/索引；历史查询及更新严格按需读取原 ledger，仅活动任务、失败清理和未确认终态保留根引用。更新重读并核对原条目，禁止陈旧快照覆盖持久责任；32 MiB 上限同时约束读取与替换，没有自动裁剪或强制关账。测试以根 store 存活时历史 DTO 的弱引用回收验证引用边界，不能据此给出生产 RSS/GC 堆节省数字；全量 JSON 读取的成本仍存在。

禁用门禁合并任务内存/磁盘责任、Mihomo pending、未知 TUN marker、不可读系统代理意图、入站 pending 和 Squid 未确认补偿。只读检查不启动管理图、网络动作或清理历史；共用已有 store gate。健康且已交接的 Core 安全责任继续运行，不因关闭管理功能被撤销；未知责任保守阻止。此快照不是跨进程原子禁用授权。

### 验证与交付范围

证据保存在 `.artifacts/proxy-completion-20261103/`，复跑命令、平台结果和摘要由 `verification.txt` 与 `provenance.json` 记录。Windows/Linux 使用隔离状态、伪造 Helper 和生产注册/路由，覆盖管理启用/禁用时 Core 唯一 owner、guard 故障重试、TUN 恢复失败保留、旧代恢复隔离、到期响应丢失对账不重发、真实异步 scope 清理、HTTP 幂等回执及 Host 排空、发布选择/补偿/锁竞争和新 pending 竞争。原安装/移除/profile、系统代理、Squid、共享 settings/出站代理回归同步执行。归档验收使用两版经 SHA256 核验的官方 Mihomo 二进制与模拟运行服务宿主，原生配置检查由 root 在既有隔离、无网络临时 systemd 进程中执行，不冒充固定服务的真实 systemd 生命周期事务。普通用户首跑缺少该检查权限失败，root 复跑通过。

Windows/Linux 专项结果：Core 安全所有权各 **17**、lifecycle/TUN scope 各 **31**、任务责任 **45/46**、订阅寿命各 **37**、TUN 执行各 **30**、settings 边界 **31/32**、注册各 **15**、配置发布 **107/108**、安装发布各 **88**、移除发布各 **92**、profile 各 **19**、runtime HTTP 各 **46**。入站原回归及新增生产 HTTP 队列、guard、Squid 和出站代理均通过。原生归档 Linux **65** 项通过。构建沿用既有编译/平台告警，未将其算作已解决；Windows 模块默认全量回归通过。额外 Linux 模块默认全量回归未通过：上传 apphost 执行位修正后，父测试进程的 `DOTNET_RUNTIME_ID=linux-x64` 与清理环境后的子进程实际 `ubuntu.26.04-x64` 不一致，ModuleHost 夹具报 package-invalid；不将该额外全量套件计为通过，不修改生产启动器来绕过包 RID 核验。Proxy 各 Linux 专项独立执行并通过，完整日志见证据清单。

当前常驻 Proxy 的管理执行作用域、安全责任交接、持久发布扫描及根历史缓存边界已实现。Proxy 仍为 **resident**；MB-04/RES-05 的完整目标不因此关闭：独立可回收程序集、驻留协调器工作租约、collectible ALC、原生系统代理/TUN/SCM/systemd 真实进程中断联合验收、生产内存/延迟测量仍未交付。Linux 测试机已有固定 Mihomo unit，真实 systemd 验收的空白机前提不满足；本轮未停止或覆盖该服务。未知任务的人工核实/关账产品流程与历史保留维护仍需独立设计，不通过删除记录或自动重放实现“完成”。后续验收分别跟踪上述独立事项，不能将本节表述为整个 Proxy 已可休眠或 DLL 已卸载。

## 20. Linux ModuleHost 目标修复、Proxy 工作租约与订阅独立代码（2026-11-04）

基于已提交并推送的 `c7c02ea5` 继续，§19 为上一阶段记录；当前事实以本节为准。保留原安全恢复、到期与发布补偿 owner，未新增原生权限或自动重放。

### ModuleHost 的严格目标身份

模块包目标不再使用发行版相关、可受父进程环境影响的 `RuntimeInformation.RuntimeIdentifier`。新增共享 `ModulePackagePlatform.CurrentRid`，Host 候选上下文/目录、ModuleHost、Windows Helper 和所有内仓夹具统一从实际 OS、进程架构及 libc 计算目标。Linux glibc 返回 `linux-x64/arm64`；musl 保持独立目标，当前六种候选列表仍不支持 musl，不能当成 glibc 接受。Windows/macOS 仍按原可移植目标；不支持的 OS/架构明确拒绝。

原候选清单、签名、逐文件证明、入口、版本和 RID 精确相等规则保留；没有发行版别名、双格式解析或向子进程传递父进程 RID 覆盖。测试核对环境覆盖不影响目标、完整异架构包仍被拒绝、真实清理环境子进程的启动/认证/健康/退出。此修复只统一目标判定，不扩大平台验收声明。

### 常驻工作租约

`ProxyModule` 通过 `IServerModuleWorkResidency` 加入原唯一 `ModuleResidencyCoordinator`，与可释放图的 `IServerModuleResidency` 分开。该 owner 不创建第二个管理容器，不解析业务服务或持有解析器；目录仍报告 **resident**、KeepLoaded、不可释放，休眠入口拒绝，配置不能据此打开 Proxy idle release。原其他模块的依赖与排空行为保持原规则。

`ProxyOperationStore` 在新键持久 queued 之前取得真实 coordinator work lease；取得失败不写假受理，首次落盘失败释放未转交租约，幂等重放不创建第二份租约。所有排队订阅、运行启停、TUN、入站与 Squid 共用该台账，因此租约覆盖独立 scope 的业务、审计、异步释放及终态持久确认。受理后仍不继承请求取消或改成宿主取消撤销；停机等待取消不终止已受理领域事务。ledger 和 coordinator 都等待完整工作，原宿主等待预算不扩大为无限完成承诺。

scope 清理或终态落盘失败时，原失败 scope/未确认记录继续由台账保留，同时将同一 coordinator owner 隔离为 recovery-blocked，再结束计数租约；不把“计数归零”解释为资源已确认或可重新加载。后续新键拒绝，原键仍可查询。持久未知责任继续由原只读禁用门禁核实，不自动关账。Core 的 guard、同代 TUN 启动恢复、sing-box 到期和 Mihomo 持久发布恢复继续独立运行，不因为管理 owner 停止准入而失去安全责任。

### 独立程序集边界

新增 `Shared/RelaxKonOS.ServerModule.Proxy.Contracts` 与 `ServerModules/RelaxKonOS.ServerModule.Proxy`；先将完整订阅文本转换实现从 Server 移出。唯一 ABI 是 `IProxySubscriptionNormalizer.Normalize(string)`，仅文本进出，无 Server 项目引用、服务容器、数据库、Helper、网络或锁权限。原 YAML、通用 Base64 和 vmess/vless/trojan/ss/hysteria2/tuic 转换逻辑原样迁移，没有新协议别名或旧实现回退。

Server 仅静态引用共享契约，使用固定 bundled `Modules/Proxy/RelaxKonOS.ServerModule.Proxy.dll`，拒绝链接/缺失文件；项目已加入 solution 与 build/publish 内容，不从任意路径或类型配置加载。每次转换在独立 collectible ALC 中同步执行，完成后卸载请求并丢弃私有对象；私有异常不通过根日志/操作台账保留实现栈，Host 返回固定中性失败。这里是订阅转换代码边界，并非整个 Proxy 管理图、独立安装包 ABI 或整个模块的 collectible 验收。

### 验证与范围

新增 `ProxyResidencyChecks` 使用真实模块注册、coordinator 与原台账，验证 resident 目录、休眠拒绝、请求取消后的独立租约、回执重放、ledger/coordinator 双排空、scope 清理/终态落盘故障隔离与租约受理失败。独立程序集检查验证 Server 无实现静态引用、各订阅格式、原 YAML/无效输入行为及根 Host 存活时 ALC 弱引用回收；强制 GC 仅用于测试观察，生产不增加 GC 调用。

构建、Windows/Linux 日志、镜像身份、复跑命令与源文件/产物摘要统一保存 `.artifacts/proxy-residency-20261104/`，最终平台结果以 `verification.txt` 和 `provenance.json` 为准。远端 `192.168.1.9:22` 经重试仍超时，Linux 改用本机 Docker 隔离容器，产物只读挂载并复制到临时目录，运行结束删除容器。此环境不代替原 Ubuntu 26.04 测试机或真实 systemd/Windows OS 故障验收。

最终结果：完整 solution Release 构建通过（0 错误、2 个既有 Server 告警）。Windows 模块默认全量及最终 ModuleHost 定向检查通过；Linux 模块默认全量在已补齐 Git 的隔离镜像通过，包含真实子进程和异架构包拒绝。最小 ASP.NET 镜像首轮缺少 Git 的失败保留，不作为产品回归失败或通过证据。新增工作租约/独立代码专项 Windows/Linux 各 **23** 项；原 Core 安全所有权各 **17**、lifecycle/TUN scope 各 **31**、责任 **45/46**、订阅各 **37**、TUN 执行各 **30**、settings **31/32**、Mihomo 注册各 **15**、配置发布 **107/108**、安装发布各 **88**、移除发布各 **92**、profile 各 **19**、runtime HTTP 各 **46** 项通过，Squid、出站代理、入站原回归与到期/HTTP 队列专项均通过。本轮不重复原生归档/固定系统服务验收；保留 §19 已有证据的原范围。

Proxy 仍为 **resident**，MB-04/RES-05 保持开放。已实现真实管理后台工作租约及订阅转换代码回收；其余管理、引擎、profile/发布和原生 authority 尚未作为整体独立可回收程序集。未宣称生产内存收益、全模块休眠或真实 TUN/系统代理/SCM/systemd 联合故障矩阵完成。未知历史的人工核实/关账与保留维护仍没有自动绕过入口。

## 21. 订阅转换待回收代码预算（2026-11-04，MB-04 部分）

§20 的每次转换独立 ALC 原来只请求 Unload，没有限制自然 GC 尚未回收的上下文数量。Host 现在通过同一同步准入边界记录弱引用，合计最多允许 16 个活动或待回收上下文；扫描时移除已死亡引用，达到上限则在加载实现代码前返回固定 Host 异常，不创建更多代次、不回退内置实现、不强制 GC。转换完成仍立即请求卸载，根预算不持有实现对象、类型、异常或强上下文引用。

自然 GC 延迟也可能暂时耗尽预算，之后的新转换会失败，待回收后才可重新准入；长期无法回收时需安排宿主恢复。16 是代码代次的防积累上限，不是字节预算、吞吐承诺或已验证的最优值。它不改变 queued 工作租约、原领域失败回执或 Core 安全责任，也不使整个 Proxy 可休眠。MB-04/RES-05 继续开放，生产内存与延迟测量仍待完成。

Windows Release Server.Tests 构建通过（0 错误、3 个既有告警），驻留/独立代码专项 **26 项**通过。新增检查主动保留真实转换上下文，验证预算饱和时 32 个并发调用全部拒绝；释放测试强引用后上下文可回收，同一个准入 gate 再执行 100 轮真实转换并逐轮确认旧 ALC 消失。GC 只用于测试观察。原订阅完整寿命及任务责任专项同时通过。证据位于 `.artifacts/proxy-code-budget-20261104/`；首次沙箱内测试在临时 ledger 文件原子替换处被拒绝，最终同一隔离测试在沙箱外通过，首次失败不计作成功。此续核尚未执行 Linux、新测试机、生产资源或真实网络故障验收，未提交、推送或部署。

## 22. Proxy 依赖守卫与测试机回收续核（2026-11-04，MB-02/04 部分）

保留 §21 及其余全部未提交实现。Proxy 转换实现项目现在在构建准备阶段只允许引用仓库内精确路径的共享 Proxy.Contracts 项目；契约项目只允许 SDK 隐式 .NET 框架依赖。两个项目均拒绝额外项目、显式二进制、包、框架和 COM 引用。当前转换器没有私有依赖；未来增加依赖须先设计其共享类型身份、加载与释放规则，再更新守卫，不能由默认上下文隐式托管新的私有实现。此约束是开发期防回归规则，不是受信官方代码的安全沙箱，也不是所有模块的完整 ABI 验收。

新增 `Tests/ServerModules/Check-ProxyModuleBoundary.ps1`：两个当前项目正向通过，分别注入 Server、Git 实现、其他目录同名契约项目、二进制、包、ASP.NET 框架和 COM 引用，全部在预期守卫失败，共 **16 项**。夹具通过临时导入注入依赖，不修改实际工程或还原不存在的包；可用 `pwsh -NoProfile -File Tests/ServerModules/Check-ProxyModuleBoundary.ps1` 复跑。首次守卫未排除 SDK 隐式框架、随后路径条件引用错误的失败已修正，最终正常构建和 Linux 发布通过，不把调试失败计作通过。

驻留专项增加真实转换失败路径：主动使私有实现抛异常，保留返回的 Host 中性异常和堆栈后观察代码回收，确认无 InnerException 泄漏或异常强引用残留。Windows 与 Ubuntu 26.04 x64 测试机最终各 **28 项**通过，包含 §21 的预算饱和、32 个并发拒绝、回收后重新准入及 100 轮真实 ALC 回收。测试机 SSH 公钥与此前已验证机器一致，产物上传到新建独立目录并核对 SHA256；使用 framework-dependent linux-x64 产物、远端 .NET/ASP.NET 10.0.14。Linux 任务责任 **46**、订阅全寿命 **37**、Core 安全所有权 **17**、lifecycle/TUN scope **31**、settings **32** 与原系统代理回归同时通过。

证据和复跑命令在 `.artifacts/proxy-boundary-verification-20261104/`，远端目录及上传摘要见 `remote-provenance.json`，环境见 `remote-environment.log`，最终驻留日志为 `proxy-residency-{windows,linux}-final.log`。连接凭据只在 Git 忽略的本地文件保存，不进入实现或共享文档。测试使用隔离状态与受控后端，未改动生产 Server/Helper、系统代理、TUN 或固定服务；未提交、推送或部署。MB-02/04、RES-05/07 的整体范围继续开放，Proxy 仍 resident，生产进程树内存与延迟收益尚无验收结论。

## 23. 进程图标缓存实例所有权与载荷预算（2026-11-04，MB-07 部分）

### 边界与预算

原 `ProcessIconProvider` 的缓存、淘汰队列和 Linux desktop 索引均为静态根引用；载荷仅按 512 条限制，单个 SVG 可达 256 KiB。并发 `ConcurrentDictionary.GetOrAdd` 工厂还可能多次入淘汰队列，命中期间不触发条目淘汰，重复队列项没有独立上限。现将图标缓存及索引移为 provider 实例所有，使用单锁维护唯一 FIFO 节点、条目与已完成载荷字节；昂贵加载在锁外执行，同键由 Lazy 合并。

| 所有者 | 当前寿命及限制 |
| --- | --- |
| Server 普通进程图标 | `ProcessSampler` 私有持有 provider，DI 销毁 sampler 时清空 provider 缓存并断开 desktop 索引引用 |
| Helper 管理员图标 | Helper 显式持有自己的进程寿命 provider，复用同一有界实现；Linux 一次性 Helper 的缓存随进程退出消失，不与 Server 普通缓存合并 |
| 每个 provider 的图标缓存 | 最多 512 条（含缺失图标的负缓存），且完成载荷合计最多 8 MiB；队列节点与条目一一对应，FIFO 淘汰；超过单缓存字节预算的单个结果只返回当前调用，不保留 |

身份与授权仍先于图标缓存：每次确认 PID/完整启动时间，Windows 使用同一进程句柄读取身份与可执行路径，管理员请求仍走原 Helper 固定操作。缓存键仍是可执行路径、文件长度和修改时间；文件元数据变化创建新键，旧键按预算淘汰。普通/管理员快照与 CPU 基线不合并，公开路由、DTO 与权限不变。

销毁清空字典、节点和计数，拒绝新缓存加载；已经开始的加载可为原调用完成，但不能重新写回已销毁的缓存。被淘汰旧条目的延迟完成也不能覆盖同键新条目或重复计量。加载异常移除失败 Lazy 与节点，允许后续重试。成功清理只释放本 owner 的引用，不清除其他 provider 的缓存。

### 验证与未完成

新增 `ProcessIconCacheChecks` 接入默认 Server.Tests 及 `--process-termination-only`、`--performance-only`，Windows/Ubuntu 26.04 各 **15 项**通过：并发同键单次加载、负缓存条目限制、40 份 256 KiB 合成 SVG 载荷只保留 32 份/8 MiB、FIFO 重载、超大结果不保留、异常重试、并发淘汰、独立 owner、销毁/淘汰与在途完成竞争、保留已销毁 cache 的弱引用回收。测试 GC 仅用于最后的引用观察。

Windows 原进程回归及新增真实 PNG provider 销毁检查 **37 项**、Linux 原进程身份与 pidfd 子进程回归 **12 项**通过；两平台性能采样专项通过，Linux 既有 Proxy 驻留 **28 项**通过。Server.Tests 与 Helper Release 构建、两者 framework-dependent linux-x64 发布通过，既有编译/平台告警保留。测试机使用 Ubuntu 26.04 x64/.NET 10.0.14，上传产物摘要已核对；日志、源码/产物摘要及复跑命令保存在 `.artifacts/process-icon-boundary-20261104/`，远端目录由 `remote-provenance.json` 记录，未替换生产 Server/Helper 或修改固定服务。

这是**缓存保留载荷**预算与对象所有权，不是整个模块的内存上限：字典/路径字符串、desktop 索引、Lazy、在途读取、调用者保留的图像及原生解码峰值另计；Linux desktop 扫描/图像解码输入预算仍需后续处理。未增加闲时计时器或清理扫描，Helper 常驻 owner 也未新增闲时释放。SystemPerformance 仍 resident，Hub/采样/原生句柄完整租约与 collectible ALC 尚未实施；8 MiB 不是整个进程树的上限，也不证明生产 RSS 降低。MB-07、RES-01/03/04/07 继续开放，全部改动保留未提交，未推送或部署。

## 24. desktop 图像输入与目标位图分配预算（2026-11-04，MB-07 部分）

在 §23 的实例缓存上继续，Linux desktop 图标原来对 SVG 先读文件长度再 `ReadAllBytes`，文件增长可绕过实际读取预算；PNG 使用 `SKBitmap.Decode(path)`，会在缩小到 32px 前按源图像尺寸分配完整位图。现在先读取有界文件快照，SVG 最多 **256 KiB**、PNG 最多 **1 MiB**，按实际字节计数，超限最多消费预算加一个字节即拒绝。文件长度只是提前拒绝与缓冲起步的提示，长度变化不会绕过限制；缓冲按需增长，普通小图标不分配整个最大输入预算。

PNG 使用快照创建 `SKCodec`，先核对真实 PNG 编码及两个维度均在 **1～1024** 内，再以固定 BGRA8888/Premul 格式分配目标位图，目标像素缓冲最多 **4 MiB**。解码必须完整成功，截断输入不作为部分成功图标。原 32×32 输出、保留宽高比和透明边距规则不变；SVG 仍返回原始字节，不在 Server 栅格化。其他编码伪装 `.png`、空文件、格式损坏或超限均按现有可选图标失败回退，不增加客户端协议或授权入口。Linux 图标主题符号链接仍可用，同样经过实际字节和解码尺寸限制。

新增 `ProcessIconInputChecks` 接入默认 Server.Tests 与原两个进程/性能专项。Windows **17 项**、Ubuntu 26.04 **18 项**通过（Linux 增加真实主题符号链接），涵盖实际 Skia PNG 生成/解码、1024px 边界、超宽/超高、编码与尺寸限制、错误编码/截断/空文件、SVG 原文、短读、旧长度提示下增长与超限、消费字节上限，以及 128 字节输入的读取分配低于 64 KiB。该分配检查只计同步托管读取路径，不是原生或整个 Server 的内存基准。

原缓存各 **15 项**、Windows/Linux 原进程回归分别 **37/12 项**、两平台性能采样及 Linux Proxy 驻留 **28 项**通过；Server.Tests/Helper Release 构建及 linux-x64 发布通过。测试机环境及产物摘要沿用核对过的 SSH 身份与新建隔离目录，证据在 `.artifacts/process-icon-input-20261104/`；保留既有告警，未修改生产配置、服务或数据，未提交、推送或部署。

这只是图像编码输入和目标像素分配的单调用预算，不能视为所有原生编解码内部内存、并发调用、路径字符串或进程树峰值的完整上限。desktop 目录遍历、索引、主题扫描及其工作预算仍开放；Windows 原生 shell 图标提取沿用既有实现，SystemPerformance 闲时释放、完整 Hub/采样租约和代码回收尚未接入。MB-07、RES-01/03/04/07 继续开放，不宣称生产内存或冷启动收益。

## 25. desktop 流式扫描与索引工作预算（2026-11-04，MB-07 部分）

在 §23/24 之上，desktop 发现从 `SearchOption.AllDirectories` 加 `ToArray()` 改为有限深度的流式遍历；预算耗尽后不再推进枚举器。目录、普通文件和链接均消耗遍历条目预算，目录链接不递归，避免循环；普通 `.desktop` 文件链接仍可使用，但目标内容必须经过实际读取限额。扫描实现移到同一 provider 的 `ProcessIconProvider.Desktop.cs` partial，Server/Helper 共用源码，没有新增公开协议或绕过权限的路径。

| 单次索引构建的预算 | 当前上限 |
| --- | --- |
| 用于扫描的根目录候选 | 64；XDG 与 home 发现也采用有限枚举，不先收集所有用户目录 |
| 遍历条目与子目录深度 | 8192 条、根目录下最多 8 层；非 desktop 条目也计入 |
| desktop 输入 | 每文件 64 KiB、合计 8 MiB；文件增长的额外超限检测字节计入实际读取量，最多多消费一个字节 |
| 持有的映射与文本 | 2048 项；键与图标路径 UTF-16 文本合计最多 512 KiB；单路径/字段最多 4096 字符 |
| 图标路径探测 | 整次构建最多 32768 次 `File.Exists` 候选探测，不是逐 desktop 文件重新分配预算 |
| 主题目录发现 | 每个主题根最多检查 128 个条目、最多保留 64 个主题（优先 hicolor）；两个主题根各取一次快照，所有记录复用 |

达到预算只保留已经找到的图标映射，其余按原可选图标语义回退；不写配置或持久数据，不把部分图标索引当成进程列表失败。根目录与文件系统返回顺序参与选择，不承诺超限后的全量图标覆盖。BOM、quoted/env command、Hidden/NoDisplay 和首次有效同名项的既有解析行为保留；本轮不是完整 desktop 规范解析升级。索引仍每 provider 惰性构建一次，随 owner 释放引用，不新增闲时扫描、TTL 或热刷新循环。

新增 `ProcessDesktopIndexChecks`，接入默认 Server.Tests 及原进程/性能专项。Windows **11 项**、Ubuntu 26.04 **12 项**通过，覆盖真实文件和根/深度/非 desktop 遍历上限、每文件/总读取预算、2048 项和字符串预算、共享路径探测、主题快照、BOM/env/隐藏项，Linux 另验证真实目录符号链接环不递归而 desktop 文件链接可有界读取。原输入检查 **17/18**、缓存各 **15**、进程 **37/12**、性能采样以及 Linux Proxy 驻留 **28** 项通过。

Server.Tests/Helper Release 构建与 linux-x64 发布通过，既有告警保留。测试机使用已核对 SSH 公钥的新建隔离目录，上传 SHA256 核验后执行；证据、环境、远端路径和复跑命令位于 `.artifacts/process-desktop-index-20261104/`。未替换生产 Server/Helper、改动服务或生产数据，全部未提交实现保留，未提交、推送或部署。

这些是逻辑工作量、输入和保留文本预算，不是慢文件系统 I/O 的严格时间界限，也不包含字典/节点等全部托管开销、原生解码内部内存或所有并发请求的峰值。SystemPerformance 仍 resident；完整 Hub/采样/图标工作租约、并发准入与在途原生工作的停机排空、闲时对象/代码回收以及生产进程树内存与延迟测量仍待实施。MB-07、RES-01/03/04/07 不因此关闭。

## 26. 图标并发准入、取消与 owner 停机排空（2026-11-04，MB-07 部分）

### 当前执行边界

原 provider 对每次请求直接 `Task.Run`，没有全 owner 并发/等待上限，销毁也不等待已开始的同步文件/原生工作。新增同源 `ProcessIconWorkQueue`，Server 的 `ProcessSampler` 对普通及管理员图标请求共享 **4 个执行名额、32 个额外等待名额**；底层 provider（包括 Helper 自己的 provider）也使用同样的独立预算。Server facade 排队时不调用普通 provider 或 Helper，provider 排队时不创建新的 `Task.Run`。名额是每个 owner 的限制，不是整机所有 Server/Helper 的全局总上限。

超出等待上限或已关闭准入时返回原可选图标空结果，HTTP 继续走既有默认图标回退，不新增公开问题码、路由或强制重试。请求在排队期间取消会移除等待并传播取消，不启动实际工作；名额取得后才启动已受理工作。已经启动的同步读取/扫描/解码或管理员传输不能因调用者取消而脱离 owner，仍等待真实 Task 完成，之后才传播请求取消、归还名额；不强杀原生工作、不重放 Helper 请求。

管理员图标受理后使用独立于请求的传输等待，沿用传输自身的故障/超时规则；原权限与 PID/启动时间核验仍在实际读取前执行。进程查询与终止没有改动取消语义，图标队列也不获得终止权限。跨等待的普通/管理员请求没有合并快照、缓存或授权身份。

### 资源与停机责任

`ProcessSampler` 以原同一单例增加 `IHostedService`，其 Stop 阶段先关闭图标准入并唤醒排队请求，再按 Host 传入的关闭令牌等待在途普通/管理员工作完成。等待取消只结束停机等待，不能显示实际工作完成或清空仍在使用的资源。本轮不增加或延长宿主关闭预算，也不宣称覆盖 Kestrel/Hub 与整个 Server 的全部关闭顺序。

同步 Dispose 只发起关闭；如果仍有在途工作，由原 owner 在最后工作完成后清理 provider 图标缓存与 desktop 索引，没有丢弃在途 Task 或另建不受跟踪的清理任务。清理成功只执行一次；故障保留 owner/故障并使后续 Stop 继续失败，不重复部分清理或重新开放准入。成功清理后清空持有的释放委托；保留完成请求 Task 的调用者不会因此保留已完成工作闭包的合成载荷。

Helper 的独立 provider 取得执行预算，但本轮没有新增 Helper 进程树的空闲退出协议或证明所有真实 Helper IPC 关闭事务。队列的等待/计数及 drain Task 属于当前 owner；它们**尚不是 `ModuleResidencyCoordinator` 的工作租约**，不改变目录 `resident`、不可释放或现有策略校验。采样器/Hub/进程查询/终止的完整驻留租约仍需后续接入。

### 验证与范围

新增 `ProcessIconWorkChecks` 接入默认 Server.Tests 与原进程/性能专项，Windows/Ubuntu 26.04 最终各 **19 项**通过：4+32 饱和及过载不执行、排队取消、停机取消等待者、已开始的受控同步 worker 不被取消脱离、停机等待取消保留执行与资源、失败返回名额、清理故障不重放、保留完成 Task/owner 的闭包弱引用回收，以及真实 Host.StopAsync 等待管理员图标传输。实际 `ProcessSampler` 同时发起 4 个受控管理员请求、1 个普通与 31 个管理员等待，验证共享预算、第五个 Helper 调用拒绝、停机后排队请求不执行与接受请求不重放。

原 Windows/Linux desktop **11/12**、输入 **17/18**、缓存各 **15**、进程 **37/12**、性能采样与 Linux Proxy 驻留 **28** 项通过。Server.Tests/Helper Release 构建和 linux-x64 发布通过，既有告警保留；证据、测试机环境、上传摘要和最终补强测试程序集摘要位于 `.artifacts/process-icon-work-20261104/`。Linux 最终工作/进程日志为 `process-linux-final.log`，其余原专项及首轮 17 项记录分别保留。

受控 worker/Helper 传输夹具不等于实机原生阻塞或真实 Helper 进程树关闭验收。此处约束图标工作的数量与所有权，未证明所有原生内部内存、慢 I/O 时间或生产进程树内存/延迟上限；SystemPerformance 整体仍 resident，MB-07、RES-01/03/04/07 继续开放。全部既有未提交实现保留，未修改生产配置或服务，未提交、推送或部署。

## 27. 图标工作接入常驻协调器租约（2026-11-04，MB-07 部分）

### 唯一 owner 与准入

`SystemPerformanceModule` 现在通过现有 `IServerModuleWorkResidency` 注册到同一个 `ModuleResidencyCoordinator`，复用 Proxy 已验证的常驻工作 owner 模型。新增 factory 仅创建工作身份/代次元数据，不解析管理服务、不创建第二个采样器、缓存或 HostedService；其 Resolve 明确拒绝管理解析。目录仍报告 **resident、KeepLoaded、不可释放**，TrySleep 和针对该模块的 idle-release 配置仍拒绝，不增加模块 ID、公共 DTO、旧构造入口或无租约回退。

Server `ProcessSampler` 在同一 4+32 图标队列准入后、等待执行名额前通过固定模块 ID 的 `ISystemPerformanceWorkLeaseSource` 取得真实工作租约。等待协调器本身仍受原容量、取消和关闭规则约束；完成协调器准入的排队项才持有工作租约，失败时不会调用 provider 或 Helper。超出图标本地准入上限的请求不取得租约；正常权限、PID/启动时间核验、读取/解码和管理员传输仍按 §26 的业务路径执行。

### 取消与结束责任

队列等待同时观察请求、owner 停止及工作租约的协调器停止令牌，取消仍在等待的项并归还其保护；即使 ProcessSampler.Stop 尚未调用，协调器关闭也能撤销这些等待。执行名额取得并通过最后停止核验后的读取不因请求或宿主取消而脱离 owner，继续等待真实业务/原生 worker/传输 Task 完成，之后归还工作租约并结束本请求的队列跟踪。接受的 Helper 读取不重放，停机等待取消不把在途租约清零。

目录的 `activeLeases` 因此包含图标排队保护，不是正在进行 OS 读取的并发数量。工作租约保护请求执行寿命；缓存和 desktop 索引仍归常驻 Host owner，按 §26 在其排空/销毁阶段清理，不能把空元数据 runtime 的释放当成整个采样器或缓存已回收。Helper 内部 provider 保持自己的有界执行 owner，不建立第二套 Server 协调器，也不直接访问 Server 的租约或 DI。

`ProcessSampler` 构造契约已直接增加必需的租约源，所有内仓直接调用和测试同步更新；只在隔离测试中使用计数租约夹具，生产没有空实现、缺源直读或失败后绕过协调器。读取失败但 Task 已完成不自动作为持久事务恢复；未知原生资源/真实传输关闭故障仍需后续专门验收，不能据本轮关闭完整故障矩阵。

### 验证与限制

新增 `ProcessIconResidencyChecks` 使用生产模块注册、真实 coordinator 与生产租约源，受控 Helper 传输只替换实际外部动作。Windows/Ubuntu 26.04 各 **14 项**通过：resident 目录、休眠与 idle 配置拒绝、预取消不取租约、4 个执行及 32 个等待项的真实保护、过载不取第 37 份租约、排队取消归还、在途请求取消保留、协调器停止撤销排队项、两条关闭路径共同等待实际读取、等待超时保留保护、完成后零租约及准入失败不执行。原工作队列各 **19**、desktop **11/12**、输入 **17/18**、缓存各 **15**、进程 **37/12**、两平台性能采样及 Linux Proxy 驻留 **28** 项通过。

Windows 默认模块全量回归最终通过。首跑在合成路由宿主缺失 `IServerModeResolver` 时失败：§26 增加 HostedService 后该宿主会构造 ProcessSampler。夹具现补齐 Host mode 和明确拒绝实际操作的 Helper 端口，并断言模块启停、未认证路由不执行 Helper；未改动生产注册来绕过失败。首轮失败与最终通过日志均保留，不把首轮计作通过。

Server.Tests、ServerModules.Tests、Helper Release 构建和 Server.Tests/Helper linux-x64 发布通过，既有告警保留。证据位于 `.artifacts/process-icon-residency-20261104/`，包括 `process-windows.log`、`process-termination-only-linux.log`、`modules-windows{,-final}.log`、环境和上传 SHA256 核验记录；Linux 在已核对 SSH 身份的新建隔离目录运行，未替换生产 Server/Helper 或操作真实受管服务。

本轮只接入图标工作；采样、Hub 长连接、进程查询/终止及其完整资源生命周期尚未全部获得协调器保护。SystemPerformance 仍常驻，MB-07、RES-01/03/04/07 继续开放；未证明 collectible ALC、整个 Helper 进程树退出或生产内存/延迟收益。全部既有未提交实现保留，未提交、推送或部署。

## 28. 进程查询/终止工作保护与快照 owner 排空（2026-11-04，MB-07 部分）

### 当前接口与保护范围

租约源直接统一为 `ISystemPerformanceWorkLeaseSource` / `SystemPerformanceWorkLeaseSource`，覆盖图标、进程查询和有效终止请求，仍使用同一协调器、固定模块 ID 及 §27 的常驻元数据 runtime。生产注册、构造参数、全部内仓直接调用与测试同步采用当前接口，不保留旧端口、类型别名、兼容构造或准入失败后的直读/直控路径。Helper 图标队列和公开 HTTP/DTO 没有改变。

`QueryAsync` 在读取或命中缓存前取得工作租约，覆盖快照 gate 等待、原平台/Helper 采集、权限可见性过滤、CPU 差分和分页物化；命中 5 秒缓存不会再次访问 Helper，但也不能绕过协调器准入。`KillAsync` 先按原规则拒绝无效 PID/启动时间，再取得租约，覆盖普通控制/Helper Task、原回执处理及两个快照的失效标记。未添加业务重放、自动 Helper 提升、通用命令或新的终止权限。

查询与终止继续传递原请求取消令牌，不套用图标“受理后独立传输等待”的取消规则；原实现何时完成/取消 Task、如何核验 PID/完整启动时间和返回 `requiresElevation` 或未确认回执均保留。租约在既有业务 Task 结束及后续处理完成后归还，不从取消请求推导终止成功。真实 Helper 进程树与传输取消后的物理收尾仍由原端口负责，不能仅凭 Task 夹具据此关闭实机故障验收。

### Host owner 与缓存引用

新增 `ProcessRequestLifetime` 跟踪已准入查询/终止调用，不创建第二个执行队列或自动重试。ProcessSampler.Stop/Dispose 先关闭查询/终止与图标两类准入，再共同等待各自排空；Stop 令牌只取消等待，不清空在途工作或租约。原快照 gate 等待项也在请求寿命内，按原请求取消/完成规则收尾。本轮不为查询/终止新增并发或等待数量上限，不将跟踪计数误报成总内存预算。

最后一个查询/终止调用结束后，owner 在关闭路径清空普通和管理员两套最新快照及 Previous CPU 差分字典，并拒绝新调用。清理只执行一次，故障在 Stop 可见且不自动重放部分清理。释放的是 owner 对物化 DTO 的引用；已返回给客户端或仍被调用方 Task 持有的结果继续有效，不承诺这些外部引用立即消失或 RSS 立即降低。图标缓存仍按 §26 的独立寿命排空，不与进程快照合并。

### 验证与未完成

新增 `ProcessRequestResidencyChecks`，使用真实模块/协调器/生产租约源和只观察取得次数的测试包装；普通控制与 Helper 终止均由受控端口替换，新增终止夹具不操作用户进程。Windows/Ubuntu 26.04 各 **19 项**通过：实际查询与缓存命中的保护、缓存不能绕过关闭准入、cache-gate 等待/取消、保留原传输令牌、owner 与协调器同时排空、取消 Stop 保留在途保护、取消快照不写缓存、清理后旧物化结果有效、精确 PID/时间、终止未确认回执保留、一次执行不重放、普通权限失败不暗中提升、无效/准入失败不执行。

原图标协调器各 **14**、图标工作各 **19**、desktop **11/12**、图像输入 **17/18**、缓存各 **15**、进程 **37/12**、两平台性能采样及 Linux Proxy 驻留 **28** 项通过；Windows 默认模块全量回归通过。Server.Tests/ServerModules.Tests Release 构建及 Server.Tests linux-x64 发布通过，既有告警保留。初次新增夹具的类型/成员重名编译错误已修正，失败日志不计作通过。证据保存在 `.artifacts/process-request-residency-20261104/`，包含最终构建、平台专项、默认模块回归、SSH 身份核对后的隔离目录、环境与上传 SHA256 记录。

SystemPerformance 仍 resident/不可释放。采样和 Hub 长连接的完整租约、整个组件资源清单、并发压力、真实终止/传输中断与未知结果恢复、程序集回收及生产进程树内存/延迟仍待验收；MB-07、RES-01/03/04/07 保持开放。全部既有未提交实现保留，未提交、推送或部署，也未改动生产配置或服务。

## 29. 实际采样工作租约与需求代次（2026-11-04，MB-07 部分）

`PerformanceSampler` 直接要求当前 `ISystemPerformanceWorkLeaseSource`，每次实际原始读取前取得同一协调器的工作租约，覆盖 source Task、差分/历史提交与同步事件分发。没有消费者时不取得租约、不读取 OS；协调器关闭拒绝读取并结束循环，没有失败后绕过保护的路径。源读取同时观察宿主和协调器停止令牌；不合作的 source Task 未结束时继续保留租约，Stop 等待预算耗尽不能提前清理资源。实际循环结束才清空原始基线、历史与健康状态。

修复订阅集合的短路错误：此前已有消费者时，后续连接没有执行 Add；现在每个连接都加入集合，最后一个连接或 REST demand 离开才进入空闲。无效 demand 窗口在增加计数前拒绝，创建 demand 失败会归还计数。注册表为需求从无到有/从有到无维护内部代次，不新增公开协议。旧代次读取晚到时不提交为新代次基线；首次新读取仅建立基线，随后才发布差分。状态事件按当前注册表状态协调，迟到事件不能回退已观察的代次；同步事件调用前再次核对代次与停止状态，外部回调在采样状态锁外执行。

新增 `PerformanceResidencyChecks`，使用真实模块注册、协调器与生产租约源，受控 source 只替换外部采集。Windows 新增 **18 项**通过，覆盖多连接、重复订阅、demand 交接、并发成员、失败 demand 计数归还、空闲零读取/租约、旧读取晚到、新基线、同步发布期间保护、非合作读取的关闭及等待超时、关闭拒绝、读取失败归还与后续新租约。原 Windows 性能专项通过。Server.Tests Release 构建与 linux-x64 发布通过，既有告警保留；证据位于 `.artifacts/performance-sampling-residency-20261104/`。本轮远程上传与执行被自动审批拒绝，Linux 测试尚未运行，不能沿用上一轮结果声称本轮跨平台通过。

Windows 默认模块全量回归通过，ServerModules.Tests Release 构建通过；`git diff --check` 通过，本地测试机配置仍在 Git 追踪之外。

本轮保护实际采样 tick，不包含 `GetInfoAsync` 的独立读取、REST 等待者整个寿命、连续 Hub 订阅或广播服务后续网络发送；已进入的同步回调也不能通过代次核验撤销。持续订阅的两次 tick 之间不连续持有工作租约。SystemPerformance 继续 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 保持开放，未证明完整模块回收或生产进程树内存/延迟收益。此前成果已提交为 `49faa84a`，本节实现随后提交并推送为 `f469c08a`，未部署。

## 30. 性能静态信息读取的工作保护（2026-11-04，MB-07 部分）

`PerformanceSampler.GetInfoAsync` 现在在实际 source 调用前取得同一协调器工作租约，覆盖完整源 Task 和取消后的结果核验。预取消、owner 关闭或协调器拒绝时不访问 source；请求与协调器停止令牌传入源读取。源忽略取消时仍等待实际 Task，完成后取消不能返回成功 DTO，异常按原调用路径传播，不重放、不缓存静态信息、不创建 realtime demand。

原请求寿命跟踪器直接更名为 `SystemPerformanceRequestLifetime`，进程查询/终止和静态信息分别持有自己的实例；没有旧类型别名或第二套协调器。PerformanceSampler.Stop/Dispose 关闭静态信息准入，Stop 同时等待原采样循环与已准入静态读取。owner Stop 不额外改变静态源的请求取消规则，协调器关闭仍会通知源；等待预算只结束等待，实际调用未完成时工作租约和 owner 跟踪继续保留。同步 Dispose 发起关闭，不将它等同于异步排空。静态信息没有新增执行/等待数量上限或总内存预算。

Windows `PerformanceResidencyChecks` 从 18 项扩至 **28 项**，新增静态信息预取消、真实租约且无采样 demand、返回原 DTO、异常归还、非合作读取取消、停机等待超时、关闭准入、实际完成后排空、协调器停止与拒绝回退检查；原性能专项通过。Server.Tests Release 构建通过，既有告警保留；证据位于 `.artifacts/performance-info-residency-20261104/`。本轮未执行 Linux 测试，先前的远程上传/执行审批拒绝仍未解除。

ServerModules.Tests Release 构建及 Windows 默认模块全量回归通过，`git diff --check` 通过，本地测试机配置继续由 Git 忽略。

本轮没有完成 REST demand 等待者、连续 Hub 订阅和广播网络发送的完整租约；整个组件资源清单、真实 I/O 中断、程序集释放与生产进程树内存/延迟验收继续开放。SystemPerformance 仍 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 不关闭。本节实现随后提交并推送为 `13e74655`，未部署。

## 31. REST 快照等待者的完整调用保护（2026-11-04，MB-07 部分）

`ReadSnapshotAsync` 现在先验证有界窗口，再进入与静态信息共享的请求 owner 并取得同一协调器工作租约，随后才读取缓存。缓存命中也不能绕过关闭准入；需要等待时，保护覆盖 demand、事件订阅、等待、取消核验及 finally 中的释放。每个等待者独立持有 demand 和工作租约，取消一个不会结束其他等待者的需求。订阅事件后再次读取缓存，补上首次缓存检查与订阅之间的发布窗口；返回缓存或已发布结果前核验请求、协调器和 owner 停止状态。

Stop/Dispose 在关闭请求准入后发出独立的一次性等待停止信号，唤醒没有客户端取消的 REST 等待者，不额外改变静态信息源的取消规则。协调器停止和请求取消通过每次等待自己的 linked token 唤醒等待。等待结束清理事件订阅、demand 和延时计时器后归还工作保护；关闭等待者不代表其触发的非合作采样 source Task 已结束，后者继续持有 §29 的独立 tick 租约。本轮没有新增 REST 并发数量或总内存预算，也不把调用方法结束等同 HTTP 响应传输结束。

Windows `PerformanceResidencyChecks` 从 28 项扩至 **42 项**，覆盖缓存取得租约、关闭后保留缓存仍拒绝、预取消/无效窗口不准入、多个独立等待者、单独取消、owner 停止唤醒/拒绝、超时、协调器停止/拒绝、REST 与实际 tick 各自保护，以及晚到读取不恢复已消失需求的历史。原性能专项通过；Server.Tests Release 最终构建通过，保留 3 个既有告警。ServerModules.Tests Release 构建与 Windows 默认模块全量回归通过；缓存返回核验的最终小幅补强另由最终性能专项复验。子 agent 只读审查未发现资源释放或排空问题，其建议的返回前停止核验已采用。证据位于 `.artifacts/performance-rest-residency-20261104/`，最终专项为 `performance-windows-final.log`；`git diff --check` 通过，测试机配置继续在 Git 追踪之外。

用户随后明确授权上传与远程执行，并要求不使用隔离环境；已在测试机固定目录 `/home/nanami/relaxkonos-tests` 直接运行。Ubuntu 26.04 性能驻留 **42 项**、原进程/性能/图标专项与 Proxy 驻留 **28 项**均通过，上传 SHA256 核验、环境与三项零退出码记录见同目录的 `remote-provenance.json`、`remote-environment.log` 和 Linux 日志。连续 Hub 订阅、广播网络发送和同步历史读取的完整调用保护、整体资源清单、实机故障、代码释放与生产进程树内存/延迟仍待后续验收。SystemPerformance 保持 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 不关闭。本节后续改动尚未提交、推送或部署，既有成果全部保留。

## 32. Hub 广播实际发送的工作寿命（2026-11-04，MB-07 部分）

`PerformanceBroadcastService` 使用当前必需的 `ISystemPerformanceWorkLeaseSource`，从队列取出快照后、实际 typed SignalR 发送前取得同一协调器工作租约。队列保持 **4 条、DropOldest、单消费者**，事件处理仅尝试入队，不在同步采样回调等待网络或取得租约。排队快照本身不持有工作租约，activeLeases 不是队列长度；队列最多另有一条正在发送的快照，载荷没有新增字节预算。

现有 typed 客户端发送方法不接受取消令牌，本轮不修改 wire 契约、不通过 WaitAsync 将发送 Task 与 owner 分离。Stop/Dispose 先停止接受新事件，实际发送未结束时保持工作租约；Stop 等待预算耗尽只结束等待。协调器关闭在发送前阻止新发送，正在发送的 Task 仍由当前循环等待；完成后归还保护并结束循环。原发送异常不自动重放，也不改成成功；业务异常仍按 BackgroundService 的既有故障路径呈现。循环实际退出时退订采样事件、完成 writer 并清空剩余队列引用。

新增 `PerformanceBroadcastResidencyChecks` 使用真实模块注册、协调器及生产租约源，受控 typed Hub 客户端仅替换外部发送。Windows/Ubuntu 26.04 各 **15 项**通过：空闲零发送/租约、发送期间真实保护、排队不取租约且不并行发送、4 条 DropOldest、Stop 后退订和拒绝事件、非合作发送的协调器排空与 Stop 超时、完成后清空队列、关闭准入无回退、同步 Dispose 保留在途保护，以及发送失败归还、不重放、退订并清空。原性能驻留各 **42 项**、进程/图标/性能专项与 Linux Proxy 驻留 **28 项**通过；Windows 默认模块全量回归通过。Server.Tests、ServerModules.Tests Release 构建与 Server.Tests linux-x64 发布通过，既有告警保留。

证据位于 `.artifacts/performance-broadcast-residency-20261104/`，包含 Windows、Linux、默认模块日志及环境/上传 SHA256/远端路径。用户明确授权后直接复用 `/home/nanami/relaxkonos-tests`，不使用隔离环境、不部署服务；凭据仅由本地脚本读取用于认证，没有输出或加入 Git。子 agent 实现测试，另一子 agent 只读复核生产寿命边界，无实质问题；`git diff --check` 通过，全部既有实现保留。

协调器单独关闭且广播空闲时，没有连续工作租约或独立停止通知；广播在下一次发送准入拒绝或 Host Stop 时退出。连续 Hub 连接/组成员、同步历史读取及 HTTP/SignalR 传输更底层的物理收尾并不因此完成保护；不将受控发送 Task 夹具当成真实慢网络故障验收。SystemPerformance 仍 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 继续开放，未声明代码回收或生产内存/延迟收益。§31/32 实现随后提交并推送为 `15630840`，未部署。

## 33. 连续 Hub 连接与组成员 owner（2026-11-04，MB-07 部分）

新增单例 `PerformanceHubConnections`，使用现有模块注册和同一协调器租约源，以同一实例注册 HostedService。`PerformanceHub.OnConnectedAsync` 在接受业务调用前取得连续工作租约；连接尚未订阅或已经退订但仍连接时继续持有，不因此产生采样 demand。Subscribe/Unsubscribe/OnDisconnected 均委托该 owner，Hub 方法名、认证和 wire 契约保持当前接口，不保留旧构造或无租约回退。组管理器与 Abort 委托随连接由 owner 持有，正常清理后移除条目，不另建采样器。

每个连接串行执行组操作：成功 Add 且仍开放后才登记采样需求，重复订阅不重复 Add；成功 Remove 才结束正常退订，退订后连接租约仍在。Add 开始前记录可能已入组，失败或晚到结束不能复活已关闭连接，最后清理使用独立令牌 Remove。已开始的 Remove 不传递关闭令牌，必须等待实际 Task；真实 Remove 失败不自动重放，保留该连接条目、工作租约与 Stop 可见故障。

断线、连接取消、Host Stop/Dispose 和协调器停止共用每个连接唯一的关闭任务。先关闭连接并 Abort，再等待实际组操作，撤销采样需求并清理可能的组成员，成功后才归还连续工作租约、请求跟踪和条目引用。Abort 异常仍尝试撤销需求及组清理，然后保留故障责任。Stop 先关闭新连接准入，等待所有关闭任务与已准入连接构建；等待预算耗尽不能把在途 Add/Remove 或未知清理故障视为完成。没有新增连接/组操作并发数量或字节预算。

新增 `PerformanceHubConnectionChecks` 接入默认和性能专项。Windows/Ubuntu 26.04 最终各 **25 项**通过：实际 Hub 生命周期回调、连接未订阅持租约、重复订阅/退订、多连接独立需求、清理后同 ID 重连、重复/预取消准入、晚到 Add、Stop 超时与拒绝准入、协调器关闭、进行中 Remove 使用独立令牌并等待、连接取消、Add 失败补偿、普通退订和最终断线 Remove 失败不重放且保留责任。正常路径使用真实模块注册、协调器和生产租约源，仅替换组传输与连接上下文；未知 Remove 故障夹具使用计数租约，避免将真实协调器的故障等待伪造为已恢复，不据此宣称未知结果恢复已实现。

原 Windows/Linux 广播各 **15 项**、性能驻留各 **42 项**、进程/图标/性能专项及 Linux Proxy 驻留 **28 项**通过；Windows 默认模块全量回归通过。Server/Server.Tests/ServerModules.Tests Release 构建与 Server.Tests linux-x64 发布通过，既有告警保留。首轮连接 21 项和最终补强 25 项均保留证据，最终测试仅补强夹具，生产代码未再变化。目录为 `.artifacts/performance-connections-residency-20261104/`，最终平台日志是 `performance-windows-final.log`、`performance-linux-final.log`；Linux 用户授权后直接复用 `/home/nanami/relaxkonos-tests`，环境、整包及最终测试程序集上传 SHA256 核验均记录。子 agent 实现测试，另一子 agent 只读审查并促成在途 Remove 与 Abort 异常两项修复；最终未发现进一步实质问题。`git diff --check` 通过，测试机配置仍在 Git 追踪之外。

目录 activeLeases 现在包含所有连续 Hub 连接（不只订阅者）、实际采样/广播/REST/进程工作，不等于 OS 并发读取数。此处不证明真实 SignalR 传输故障、远端组存储或 HTTP/网络底层物理清理；组任务失败的保留责任需要后续显式恢复设计，不能以 Abort 当作清理已确认。同步历史读取的完整保护、整体资源清单、连接压力及数量预算、程序集释放及生产进程树内存/延迟仍开放。SystemPerformance 保持 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 不关闭；本节实现随后提交并推送为 `b48f2f4b`，未部署，全部既有成果保留。

## 34. 历史与最新快照的异步读取保护（2026-11-04，MB-07 部分）

`IPerformanceSampler` 直接采用 `GetLatestAsync` / `GetHistoryAsync` 当前接口，均可传入取消令牌；不保留原同步方法或阻塞异步适配。生产实现、HTTP 历史端点、内仓调用与测试夹具同步更新；路由、JSON 与 60 点历史容量不变。历史 HTTP 端点传递请求取消令牌，不创建额外采样 demand 或 OS 读取。

公开读取在访问空缓存或非空缓存前进入现有请求 owner 并取得同一协调器工作租约，链接请求与协调器停止令牌，在状态协调/历史物化前后核验取消和 owner 关闭。已准入但尚未返回的读取由 owner Stop 跟踪；关闭后拒绝公开读缓存，不能以空结果或旧快照绕过准入。返回的 DTO/数组归调用者引用所有，不在返回后继续持有工作租约，owner 释放自身历史也不修改已物化结果。`ReadSnapshotAsync` 使用私有读取核心，复用它已持有的租约，避免额外嵌套准入。

`PerformanceResidencyChecks` 从 42 项扩至 **53 项**，Windows/Ubuntu 26.04 均通过。新增覆盖预取消不准入、历史/最新缓存命中均取得真实保护、读取不产生 demand/source 调用、原 1～60 秒窗口界限、已物化历史不持租约且不受 owner 缓存变化影响、关闭 owner/协调器拒绝旧缓存，以及取得真实租约后暂停准入返回的读取在 Stop 超时/请求取消时仍保留保护。暂停仅在测试包装端口，生产没有额外等待队列。旧停止后空缓存检查改为观察 owner 存储，公开方法则明确验证关闭拒绝。

原 Windows/Linux 连接各 **25 项**、广播各 **15 项**、进程/图标/性能专项及 Linux Proxy 驻留 **28 项**通过。Server/Server.Tests Release 构建与 Server.Tests linux-x64 发布通过，既有告警保留。证据位于 `.artifacts/performance-history-residency-20261104/`，包含平台日志、环境、上传摘要核验及远端固定目录；用户授权后直接复用 `/home/nanami/relaxkonos-tests`。子 agent 更新测试，另一子 agent 只读审查生产实现未发现实质问题；全仓旧同步调用已清除，`git diff --check` 通过，凭据继续由本地忽略文件持有。

ServerModules.Tests Release 构建及 Windows 默认模块全量回归通过。

此处保护方法执行至物化结束，不覆盖随后 HTTP JSON 序列化、响应发送或调用方持有结果的整个寿命；没有新增生产 GC、历史 TTL、采样频率或并发数量预算。整体资源清单、连接压力、真实传输和未知结果恢复、程序集释放及生产进程树内存/延迟仍待验收。SystemPerformance 保持 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 不关闭，本节实现随后提交并推送为 `6ff45a08`，未部署。

## 35. HTTP 性能响应执行保护（2026-11-04，MB-07 部分）

Host 内部新增 `SystemPerformanceResult`，在 `IResult.ExecuteAsync` 真正执行前通过当前必需租约源取得同一协调器工作租约，覆盖 JSON 序列化、文件字节写入以及错误结果执行的真实 Task。性能 info/snapshot/history、进程查询/图标/终止的已授权结果均使用包装；未认证仍由认证中间件提前拒绝，管理员能力不足的 `ElevationRequired` 继续在包装和业务调用前返回。info/history 显式声明原成功响应类型，避免返回类型变为 `IResult` 后丢失原推断的响应元数据；路由、JSON、状态码与终止协议不变。

响应执行暂时将 `HttpContext.RequestAborted` 扩展为原请求断连与协调器停止的 linked token，在执行前后核验停止，并在 finally 恢复原令牌。通知取消不代表底层传输已被强制中止；忽略取消的结果必须等待其真实 Task 结束才归还租约，没有 `WaitAsync` 分离、后台重放或将租约挂到可能不执行的 OnCompleted 回调。序列化/写入异常按原管线传播并归还已完成的工作保护，协调器拒绝时不执行结果，也不重放已经完成的查询/终止；终止可能已有副作用但响应不可用仍是原未知响应语义。

新增 `PerformanceHttpResidencyChecks` 接入默认与性能专项，Windows/Ubuntu 26.04 各 **15 项**通过：真实 Results.Ok JSON 与 Results.File 字节写入期间真实租约、成功/异常/取消恢复原请求令牌、请求和协调器取消不脱离非合作结果 Task、关闭及预取消不执行结果、实际 DELETE 端点能力不足时不执行业务/不取得响应租约、终止业务已执行而响应准入失败不重放，以及 info/history 原成功响应类型元数据保留。只有响应传输/进程业务的外部动作被受控夹具替换，终止测试不操作用户进程；非合作 IResult 不是实机网络阻塞证明。

原 Windows/Linux 连接各 **25 项**、广播各 **15 项**、性能驻留各 **53 项**、进程/图标/性能专项及 Linux Proxy 驻留 **28 项**通过；Windows 默认模块全量回归通过。Server/Server.Tests/ServerModules.Tests Release 构建与 Server.Tests linux-x64 发布通过，既有告警保留。证据位于 `.artifacts/performance-http-residency-20261104/`，包括平台日志、默认模块回归、环境、远端路径与上传 SHA256 核验；用户授权后直接复用 `/home/nanami/relaxkonos-tests`。子 agent 实现测试，另一子 agent 只读审查 linked token/恢复和真实任务所有权后未发现实质问题；`git diff --check` 通过，测试机凭据保持本地忽略文件。

服务方法的业务租约与响应执行租约各有独立寿命，两者之间返回的是共享 DTO 或 byte[]，不执行模块代码；本轮不声称连续保护整个 HTTP 管线、下游中间件收尾或物理网络送达。返回方法后的调用方引用不转移到模块 runtime。SystemPerformance 仍 resident/KeepLoaded/不可释放，整体资源清单、压力与总内存预算、真实网络/未知结果恢复、代码释放与生产进程树内存/延迟继续开放；本节实现随后提交并推送为 `c7b03794`，未部署。

## 36. 连续 Hub 连接的准入数量预算（2026-11-04，MB-07 部分）

§33 的连接 owner 原先没有数量上限。现增加每个 `PerformanceHubConnections` owner **128 个名额**，合计覆盖尚在协调器准入的连接、已连接/退订但未断开的连接，以及清理故障条目。名额和连接 ID 在同一 owner 锁内预留，重复 ID 或预算已满时立即通过现有 Hub 异常路径拒绝，不建立等待队列、不取得新工作租约、不创建连接条目或组操作。SignalR 按其现有 OnConnected 失败规则结束拒绝连接，原认证仍先于该入口。

取得真实工作租约后再次核验停止状态，取消或准入失败在实际 Task 收尾后归还名额；取消通知和 Stop 等待预算耗尽不能提前归还仍在准入的名额。连接发布后所有权一并转移给唯一连接关闭任务，只有组清理和工作租约释放成功才同锁移除连接及名额。未知退组/Abort 故障继续占用预算并使 Stop 可见，预算耗尽不通过丢弃旧故障或重放清理恢复准入。正常断线成功后可以立即补位。

新增 `PerformanceHubBudgetChecks` 接入默认和性能专项，Windows/Ubuntu 26.04 各 **13 项**通过：128 个存活或并发 pending 名额饱和、超限及 pending 重复 ID 不取得新租约、成功断线补位、非合作准入取消不提前归还、晚到取消不发布连接、预取消/准入异常归还、Stop 等待超时保留 pending、未知 Remove 故障占名额及健康名额独立回收、Stop 故障不重放。正常路径使用真实模块/协调器/生产租约源，暂停返回只由测试包装端口提供；未知清理夹具使用计数租约，不伪造真实协调器故障恢复。

原 Windows/Linux HTTP 各 **15 项**、连续连接各 **25 项**、广播各 **15 项**、性能驻留各 **53 项**、进程/图标/性能专项与 Linux Proxy 驻留 **28 项**通过；Windows 默认模块全量回归通过。Server/Server.Tests/ServerModules.Tests Release 构建及 Server.Tests linux-x64 发布通过，既有告警保留。证据位于 `.artifacts/performance-connection-budget-20261104/`，含平台日志、默认模块、环境与上传 SHA256 核验；用户授权后直接复用测试机 `/home/nanami/relaxkonos-tests`。子 agent 实现测试，另一子 agent 只读复核预留转移、取消和 Stop 竞态后未发现实质问题；`git diff --check` 通过，测试机凭据仍在本地忽略文件中。

128 是 owner 持有的连接数量上限，不是整个 SignalR/Kestrel 连接上限、每个连接的组操作等待数量、载荷字节预算或整个 Server 的内存峰值；超限拒绝本身也不代表握手传输已完成物理关闭。故障恢复、整体资源清单、真实压力/网络矩阵、程序集释放与生产进程树内存/延迟继续开放。SystemPerformance 仍 resident/KeepLoaded/不可释放，MB-07、RES-01/03/04/07 不关闭；本节实现随后本地提交为 `c3a38243`，尚未推送或部署。

## 37. 管理员图标 Base64 解码前预算（2026-11-04，MB-07 部分）

资源核对发现 `ProcessSampler.GetIconCoreAsync` 的管理员路径原先在 `Convert.FromBase64String` 分配完成后才检查 256 KiB 输出上限，超大 Helper 响应可以先分配不需要的解码数组。现在先检查原始编码字符串最多 **349528 个字符**（256 KiB 数据的 Base64 长度上界），再进入原解码器；空白也计入输入预算，不先扫描/复制以规避预算。保留原解码后 **256 KiB** 检查，因为同一最大编码长度也能表示多 1～2 字节的载荷。

失败响应、非 PNG/SVG 类型、无效/空编码和超预算仍返回原可选图标空结果，不重放 Helper，不放宽权限或 PID/时间核验。现有 4+32 队列、协调器工作租约、请求取消及实际 Task 排空不变。预算约束此 facade 的解码输入和解码数组，最多还会为同长度的超限编码分配两字节额外空间后拒绝；它不约束已经在传输层构造的字符串/JSON、Helper 进程树峰值、图像原生解码或全请求内存。

新增 `ProcessPrivilegedIconBudgetChecks` 接入默认、进程和性能专项，Windows/Ubuntu 26.04 各 **14 项**通过：实际 facade 与真实协调器下 PNG/SVG 精确输出边界、同编码长度的多 1～2 字节输出拒绝、超长/空白膨胀输入拒绝、预算内既有空白处理、格式错误/空输出/不支持类型/失败响应回退、每次只执行一次并归还租约、owner/协调器关闭拒绝。测试仅替换 Helper 外部传输，不访问真实管理员进程图标。

新增分配回归预先构造 **4194304 字符**有效 Base64 并热身真实准入；实际同步完成的 facade 调用拒绝输入，调用线程新增分配 **小于 256 KiB**，不创建其 3 MiB 解码数组。输入构造在测量窗口之外，不把 UTF-16 输入字符串或其他线程内存当成零成本，不使用全局 GC/工作集裁剪。这是受控解码分配回归，不是生产 RSS 或整个进程树收益测量。

原 Windows/Linux HTTP15、连接预算13、连续连接25、广播15、性能驻留53、请求19、图标租约14/工作19、desktop11/12、图像输入17/18、缓存各15及性能采样通过；Linux 原进程与 Proxy 驻留28通过。Server/Server.Tests Release 构建及 Server.Tests linux-x64 发布通过，既有告警保留。本轮没有修改默认模块注册或公开端点，未重复默认模块全量回归，不沿用上一轮结果冒充本轮执行。证据位于 `.artifacts/process-privileged-icon-budget-20261104/`，包括平台日志、环境、远端路径及上传 SHA256 核验；用户授权后直接复用 `/home/nanami/relaxkonos-tests`。子 agent 实现测试，另一子 agent 只读核对 padding、空白与分配边界后未发现实质问题；`git diff --check` 通过，凭据仍为本地忽略文件。

SystemPerformance 仍 resident/KeepLoaded/不可释放，整体资源清单、真实压力/网络故障、程序集释放与生产进程树内存/延迟继续开放，MB-07、RES-01/03/04/07 不关闭。本节实现已提交并推送为 `f16521fd`，包含此前本地提交 `c3a38243`，未部署，全部既有成果保留。

## 38. 模块里程碑与阶段性交付（2026-11-04）

### 当前执行约定

MB-01～08 的实际状态、阻塞和整项验收以 §8 为准。本节拥有下一阶段模块里程碑，旧章节保留当时证据，不再将后续小项优化当成新的默认推进方向。里程碑必须同时交付所有权边界、故障/停止行为、仓内调用、应用文档与相应验证；达到阶段门槛后继续下一模块，不等待逐个小问题确认。共享契约/根注册的变更由主执行者统一集成，独立领域实施可并行。

### SystemPerformance S1：交付并冻结

当前可交付范围是 **resident 模块的有界缓存/输入和完整实际工作 owner**：进程查询/控制、图标队列、采样、REST 等待/缓存、连续 Hub 连接/组成员及广播和 HTTP 结果执行均已取得相应真实工作保护，关闭不提前释放真实 Task；已验证的预算与完整限制见 §23～37。最后代码交付基线为 `f16521fd`。不将方法租约、队列预算或受控分配回归称为整个模块可休眠、程序集已回收或生产内存收益。

S1 之后仅接受阻塞性交付问题：权限/身份错误、丢失副作用或未知责任、在途任务脱离 owner、关闭提前释放、稳定复现的功能回归。以下进入后续待办，不抢占 MB-05/06/Terminal：整体字节峰值与压力预算、进程快照闲时清理、完整 Helper IPC 输入预算、未知组清理的显式恢复、真实网络/原生/进程树故障矩阵、独立代码与空闲释放、生产内存/冷启动测量。除非其中出现上述阻塞事实，不继续默认扩展 S1。

### 下一批模块验收门槛

| 里程碑 | 本阶段交付 | 必须通过的验收 | 保留的后续阻塞 |
| --- | --- | --- | --- |
| MB-05A：三域后台工作责任 | Docker 栈、Certificate 操作/续期与 WebServer 发布共用可跟踪准入/执行 owner，接唯一协调器 resident 工作身份；Core Engine/TLS/站点权威不搬移 | queued 前准入、幂等不新建任务；HTTP 断开不丢任务；注册与 Stop 同锁；真实执行/终态提交/清理结束才归还；快完成无残留；取消不提前终态；未知持久化/清理失败保留责任并阻止禁用；重启按原规则观察或标记 interrupted、不重放副作用 | 真实 Engine/CA/Nginx 中断联合验收、持久轻调度、独立执行程序集、动态休眠与生产收益 |
| MB-06A：ModuleHost 代次 owner | 正式 installed 身份及明确开发项使用唯一 resident 工作身份，进程与 IPC/proxy/Broker scope 归同代 owner | 权限/信任先于使用；每代 token/客户端不裸借用；代理覆盖实际流，Broker覆盖真实操作；关闭撤回准入/token后排空；超时不提前释放；旧代回调拒绝；实际自有进程退出核验、失败保留，不重放业务 | SDK 后台任务/drain 申报、合作闲时退出/重启、完整进程树/崩溃/信任撤销双平台矩阵 |
| Terminal-T1：会话与 PTY 排空 | 会话连续工作租约、异步创建/清理、输出/回放任务所有权、后端可确认完成端口及必要的窄退出确认协议 | 断线只 Detach且可重连；用户/管理员身份复核不变；并发创建/关闭不双执行；输出顺序与有界慢客户端行为；真实 PTY/输出完成前保留保护；自然退出/启动失败/Stop超时/未知清理可见；正常已确认路径零租约，不将正常关闭一律变成未知 | Helper/原生句柄与完整子进程树实机矩阵、独立代码/释放收益、长期压力与会话预算 |

共享 `ResidentModuleWorkRuntimeFactory` 仅持元数据与工作代次，不解析第二套领域服务；`IResidentModuleWorkLeaseSource` 使用同一协调器。新增此基础本身不等于领域图可回收；本批阶段验收结果见 §39。验收报告必须分别列“实现且已验证”“实现但实机未验证”“未实现/阻塞”，不能把根 resident 元数据回收当作业务图或进程回收。

### 本轮审计发现与实施状态

MB-05A 已确认的阻塞是证书操作丢弃后台 Task且取消提前终态、网站发布快完成注册残留与 Stop 竞态、三域无真实模块工作保护。MB-06A 已确认代理/Broker返回裸连接、健康故障与在途使用相撞、Kill 后未等待退出及正式 installed ID 未接协调器。Terminal-T1 已确认未跟踪的输出/退出任务、同步 Stop 无确认、先移除会话再清理及 Helper 输出无退出回执。上述阻塞已在本批阶段实现修复并完成 §39 所列验收，不扩大受管软件安装/版本事务。


## 39. MB-05A、MB-06A 与 Terminal-T1 阶段验收（2026-11-04）

本批在既有未提交成果上集成，SystemPerformance S1 保持冻结，仅运行回归。MB-01～08 的整项状态仍由 §8 拥有，以下阶段通过不关闭整项，也不宣称生产内存收益或新模块程序集回收。

### 实现且已验证

- **MB-05A**：Docker 栈、Certificate 操作/续期和 WebServer 发布采用同一协调器的 resident 工作身份。后台 owner 在 queued 前准入、注册与 Stop 同锁，覆盖实际副作用、终态提交和清理；请求断开及等待预算耗尽不丢任务。失败持久化/清理保留责任并拒绝新工作，依赖图也拒绝穿过已故障依赖的新准入。证书取消先记录 `cancel-requested`，不提前写 Cancelled；桌面与 Android 持续观察原 operationId。损坏证书台账隔离该域，不终止 Core Host。不可取消的 ACME API 等待实际 Task，HTTP-01 最后删除/Dispose 等待真实响应及 listener 代次结束。
- **MB-06A**：唯一进程代次持有连续工作租约，proxy 与 Broker 回调借用该代的范围，覆盖真实上游响应复制和结果执行。停止先撤回 token/新准入，再等实际范围与进程退出；超时、缺树退出回执及非零退出保留未知责任，关闭不重放业务。开发身份仅在明确 Development 配置且校验通过时加入元数据，不能覆盖内置/安装身份。根工厂不创建第二套业务服务图。
- **Terminal-T1**：会话连续租约覆盖 Detach/重连、串行输出/回放及实际 PTY 清理。输出待发有界为 1 MiB/128 数据项及最多一个退出控制项；慢连接溢出中断该连接，保留会话。用户执行协议直接升级至 **1.10**，Windows/Linux 统一 Output/Exited 帧，Server/Helper 必须同时更新。Linux 原生验收发现 RoyalTerminal Dispose 抑制退出事件/未确认 reap，已用绑定进程身份的 pidfd 实际退出观察和 waitid 补齐；Stop 等待读取线程及真实退出，不把等待超时当作成功。

Windows 与 Ubuntu 26.04/.NET 10.0.14：resident 元数据/依赖故障 **8**、后台 owner **32**、真实 localhost HTTP-01 **16**、终端帧 **8**、Terminal 生命周期 **15** 项均通过。Terminal Linux 项包含真实非 root PTY、阻塞实际读取、停止后退出/reap；Windows 包含真实原生后端相应检查。ModuleHost 生命周期 Windows **19** / Linux **21** 项及 **6 项生产端点范围**验收通过，使用真实 ModuleHost 进程及实际 proxy/Broker 响应写入阻塞；默认模块全量两平台通过。原 Docker 栈、证书 TLS/SNI/重放/绑定和终端身份回归随聚合执行通过；SystemPerformance 与 Proxy 原专项通过。

真实 Docker Engine/Compose 使用测试机已缓存 Alpine 3.22 的固定 image ID 验收通过：即时操作结果与保存的 Engine 快照一致，独立后续观察确认 worker 退出 7，随后修复、原键幂等、停止、占用卷拒删、移除后保留并最终释放卷，以及真实工作租约排空均通过。本次即时快照的 worker 仍 running，故 Succeeded 仅代表当次观察，不能算稳定运行或该次 partial-failure 的实机证明；partial-failure 分类由受控回归覆盖。首轮错误地要求即时已退出而失败，修正的是测试观察时序，未改生产判定。未下载镜像，随机项目的容器/网络/卷全部清理。此项不覆盖 daemon 重启/真实中断或 BuildKit/ALC 联合矩阵。

桌面证书取消视图模型及 UI review 通过；Android 生产/仪器测试 Kotlin 编译通过，完整单元 **1872 项、0 失败**。Android 仪器测试未在设备执行。首次完整单测发现两个现有 Proxy/SingBox JSON 夹具缺当前 `commitWitness`，仅补测试字段，未增加双格式解析。首次 Linux 原生退出及首次受限构建失败日志保留，不作为通过证据。

### 已实现但实机矩阵未完成

ModuleHost Linux 专用 UID 清场/树退出证明、Windows Job 树退出回执及 Helper 停止语义已实现并编译；尚未部署当前 Helper 到正式安装身份完成双平台树/信任撤销/崩溃矩阵。Broker 此次实际端点覆盖计划读取和响应范围，不冒称新增真实提权副作用中断矩阵。Terminal 验证根进程、原生读取及退出帧，不代表全部后代树、安装 Helper/S4U、长时压力或任意孤儿恢复均已验收。

### 未实现及下一阶段门槛

三域持久轻调度、CA/Nginx 联合中断恢复、跨文件证书台账事务、SDK 后台责任/drain 申报与合作闲时退出仍开放。所有新增身份保持 resident，未启用自动闲时 kill 或新可释放模块。Terminal 会话总数/Helper 字节预算、完整树压力、独立实现程序集，以及 MB-01/08 同构建业务基线、生产进程树内存和冷启动收益必须另行验收。

证据统一保留在本地忽略目录 `.artifacts/module-milestones-20261104/`：`milestones-windows-delivery.log`、`milestones-linux.log`、`modules-windows-final.log`、`modules-linux.log`、`modulehost-linux.log`、`remote-provenance.json`、`docker-live-linux.log`、`docker-live-provenance.json`、客户端日志与 Android 最终结果。Linux 直接复用 `/home/nanami/relaxkonos-tests`，上传校验 SHA256；未替换生产 Server/Helper。凭据仅在 Git 追踪之外的本地文件中。


## 40. MB-06B 与 Terminal-T2：原生进程树验收（2026-11-05）

本轮继续 §39 的进程树缺口，不扩大 SystemPerformance S1 范围。MB-06B 交付专用 ModuleHost 树退出和同源原生证明；Terminal-T2 交付会话独占树 owner，普通关闭与根先退出均必须结束后代、原生输出和工作租约后才成功。此处记录树生命周期门槛，不等于 SDK 合作闲时退出、完整安装矩阵或内存收益通过。

### ModuleHost 原生树

Linux 专用 launcher 在加载模块前启用 subreaper。原来的专用 UID 清理会把已退出僵尸仍算责任，却未负责领养/reap，因而依赖宿主 PID 1 的行为；现在在独占 launcher 内按既有专用 UID 终止，领养该 UID 的直接僵尸并真实 reap，要求该 UID 在 `/proc` 中彻底为空后才发送 `tree-stopped`。不把此 UID 清理机制应用于共享 Server 或 Terminal 用户身份。根退出码与树清理分别核验，成功清树不能把根 exit 7 改写成成功。

Windows ModuleHost 与 Terminal 使用同源 `WindowsProcessTreeJob`。根进程挂起创建，加入不允许 breakaway 的 Job 后才 resume。真实验收发现 `ActiveProcesses=0` 可能早于已知成员进程对象 signaled；新证明在终止前捕获有界成员列表、打开原生句柄并以 IsProcessInJob 再绑定，随后等待 Job 归零及已绑定句柄终态。无效句柄、权限不足和超时不伪报成功，未按裸 PID 终止复用进程。

已通过的真实验收：Windows 当前账号 Job **11 项**；Linux 独立数字 UID 原生树 **4 场景**（正常、根先被杀、根 exit 7、控制输入断开），每场景有真实 root/child/setsid/嵌套后代，忽略 TERM 的子孙结束并 reap，无关进程存活。数字 UID 仅用于该次子进程，未新增系统账号。

Linux 同时复用既有固定 `/etc/relaxkonos/module-host.json`、Server UID 999 与已有 `fixture-host` 专用账号，使用本轮 root-owned 测试产物执行真实 Prepare/包校验/永久降权，再验收 shutdown/disconnect × plain/detached **4 路径**：受保护宿主文件、不可变包、真实 Helper socket/环境隔离、私有存储、健康检查及显式树退出/reap 均通过。此夹具由 root 显式设置配置所要求的 SUDO_UID，**没有**更换生产 launcher 或 sudoers；它验证当前 Prepare 与树实现，不声称现有 sudo 固定路径已部署当前二进制。原 fixture 数据恢复，临时包和其隔离副本移除，固定配置未修改。

### Terminal 独占树与失败责任

Linux 本地终端改用同 UID 的单会话 Server `terminal-worker` 入口；Helper 仍先永久切换到既有目标 UID/GID，再进入同源 worker。每个 worker 独占 subreaper，按直接子进程前沿取得绑定实例的 pidfd、终止并 reap，重复直到内核 ECHILD；覆盖脱离 session/process group 的后代，不扫描或清场共享用户 UID，也不在共享 Server 安装全局 subreaper。Windows ConPTY 与 ModuleHost 共用上述 Job 证明。

协议直接更新至 **1.11**，新增 `TreeFaulted` 控制帧；清理未知只通知一次，父端保留同一 worker/会话故障和工作租约，worker 保持 subreaper/残存子孙所有权，不自动退出、重试或提权。正常确认仍需 Exited、worker 成功退出和实际读写排空。Server/Helper 必须同步构建更新，不提供旧版本双解析。

Windows 当前账号 ConPTY、Linux 同 UID 本地 worker、Linux 当前 Helper 永久降权路径各 **17 项**通过：显式关闭、根先退出 0/7、detach 不结束树、真实 root/child/setsid grandchild、无关同身份进程存活、完整树结束/reap 后真实协调器租约归零及重复关闭不重执行。Windows 使用真实已绑定进程句柄；Linux 在启动时绑定真实 pidfd，结束时核验退出信号与原实例已从 `/proc` 回收。首轮 `.NET Process.HasExited` 缓存与 `/proc` 消失不一致是夹具观察缺陷，替换为内核证明，未用 Refresh 或增加等待掩盖。

Linux 自然退出场景另发现生产阻塞：内核快照已显示根为 Z、pidfd signaled，但 worker 线程仍停在 `anon_pipe_read`，标准输入 ReadAsync 的取消没有排空实际同步读取。现使用独占 worker 的 O_NONBLOCK stdin + native poll/read，循环检查取消；调用方等待原生读取实际结束后才归还清零的池缓冲并恢复标志，不用 WhenAny 遗弃读 Task。真实打开管道的空头部与部分载荷取消 **2 项**通过，均保持管道未 EOF，核验实际 ReadTask 完成和 ActiveNativeReads=0。运行期独立 pidfd 只读观察根状态，WNOWAIT 保留真实退出 0/7，最终清理阶段才 reap。

真实权限故障专项通过：独占 root 夹具创建 root 后代后永久降权，实际 pidfd 信号收到 EPERM；同源生产失败保留方法发出 `TreeFaulted`，原 worker 仍活且保持树责任。随后由独立特权测试 owner 显式通过绑定句柄结束该 worker，领养并精确终止/reap 自有 root 后代，`/proc` 消失。此人工测试收尾不是生产自动恢复，也不表示普通 UID 可以清理 sudo 升权后的所有后代。

原里程碑聚合两平台通过：resident 目录8、HTTP-01 真实 listener16、三域后台 owner32、终端帧9、终端驻留15及 Docker/证书/身份回归；Linux 加上述输入2项。默认模块全量两平台、Linux ModuleHost 实际端点/生命周期通过。Helper 与 Server 同源构建、linux-x64 两套发布通过；未新增 SystemPerformance 实现，未借本轮关闭 MB-01/08。

### 保留的后续边界

当前 Windows 机器为普通 Medium token，未置备 LocalSystem/S4U 的专用账号验收服务；当前账号 Job/ConPTY 实测不代替该安装矩阵。完整信任撤销/宿主崩溃/多代重新核验、SDK 后台责任申报与合作 drain、闲时退出/唤醒仍开放。Linux 遇到由 sudo 等改变身份、当前 owner 无权信号的后代时必须保留未知责任，不能为成功清场扩大权限。

本地同 UID Terminal 新增独占 .NET worker 有额外进程/内存成本；本轮优先确立可确认树边界，不据此宣称内存节省。最终 Linux 三个本地点样中，该 worker RSS 54356～54508 KiB、PSS 30245～30407 KiB（约53 MiB RSS/29.5～29.7 MiB PSS），不含父 Server 和整棵树；它不是峰值、稳定态、前后差值或净新增成本测量。Helper 运输进程与其子进程另行逐个记录，不能把 sudo 的较小 RSS 当作 Helper 成本或重复相加成树峰值。Terminal 会话数量/进程预算、长期压力、轻量 worker 打包及真实同构建内存/冷启动收益继续由 MB-01/07/08 跟踪。

### 证据及部署边界

证据位于本地 Git 忽略目录 `.artifacts/process-tree-20261105/`：最终 `remote-run-complete.log`、`remote-provenance.json` 的7条远端命令均0；三条 Terminal 树/权限故障、`milestones-linux.log`、模块全量、`modulehost-root-provenance.json` 与4条实际隔离路径日志保留。Windows `windows-job-tree-final.log`、`terminal-tree-windows-frozen.log`、`milestones-windows-frozen.log`、`modules-windows.log` 及构建日志拥有相应最终结果。首次失败、内核现场 `terminal-live-kernel-diagnostic.json` 和修复前 Linux 发布归档保留，不把它们算作通过。

SSH 固定核验主机密钥，归档上传校验 SHA256。用户授权测试机继续复用 `/home/nanami/relaxkonos-tests`；root-owned 当前测试产物仅放在 `/opt/relaxkonos/acceptance/process-tree-20261105`，未替换运行中的 Server/Helper、sudoers 或固定配置，未新增系统账号/服务。应用候选等既有成果保留；凭据仍在 Git 追踪之外。


## 41. Firewall-F1 独立代码与 Tailscale TS-01 对象代次（2026-11-05）

本批继续更多模块，不重做已交付 ModuleHost/Terminal，也不扩展 SystemPerformance S1。两个阶段分别验证代码所有权和对象/持久责任边界，不能相互替代；MB-02/07 的剩余整项条件见 §8。

### Firewall-F1：独立契约、执行代码与窄 Host 端口

`IHostFirewallService`、共享错误及 `IFirewallHostOperations` 当前 ABI 位于独立 `RelaxKonOS.ServerModule.Firewall.Contracts`。Linux UFW、Windows facade 和不可用后端的唯一源码迁到 `RelaxKonOS.ServerModule.Firewall`；Server 不再编译这些实现，也不静态引用实现程序集。正式构建/发布把共享契约放在根目录，实现仅放 `Modules/Firewall/RelaxKonOS.ServerModule.Firewall.dll`，无旧根 DI 实现或兼容入口。项目与所有仓内直接构造测试调用同批更新。

Core 保留原认证/HostFeature/FirewallChange grant、特权传输和 Helper 业务权威。新端口只能表达 **13 类 Firewall 操作**及 Firewall 专有字段；不能接受任意 PrivilegedOperationRequest、路径、服务/安装请求或 shell。Core 映射前拒绝未知 enum 和属于其他操作的字段。模块日志只传共享 enum，不向 Root ILogger<T> 缓存交付私有 Type/Exception。Helper 的未确认结果仍按原结果返回，不因代码释放推成功或自动重放；其外部责任没有转移到管理代码。

Host 每代创建 collectible ALC。现有唯一协调器/方法租约继续覆盖实际业务 Task，包括不合作的外部等待；取消等待不脱离原任务，也不重复增加后台 owner。实际任务结束才可对象释放/请求 ALC 卸载；Root 边界重新创建已知共享错误、取消或有界未知错误，不保留原私有堆栈/Inner。激活失败也在创建 ALC 时登记弱引用并重建 Root 错误。最多 **4 个**尚未自然回收的上下文，超过时拒绝第五代，不强制生产 GC，也不回退常驻实现。

Windows 与 Ubuntu 26.04/.NET 10.0.14 各 **31 项**通过：每个进程分别运行 Linux/Windows 两套 facade 各 **100 个真实 ALC**循环，调用方保留成功/失败/取消 Task、规则 DTO 和异常后仍观察回收；覆盖真正异步 Helper 失败、构造失败、真实协调器非合作 mutation 取消保护、未知结果不重放、借用 Core transport 不销毁及4代预算。两平台 facade 的外部 Helper 结果为受控端口，未写宿主规则。测试 GC 仅为弱引用 oracle，不属于产品内存控制策略。

独立构建门禁 **18 项**通过：规范项目依赖正例及 Host/其他执行项目、伪同名路径、二进制 Reference、包、额外框架和 COM 注入拒绝。两套 linux-x64 publish 实测实现只在 Modules/Firewall、根目录无实现 DLL、Host/test deps 无静态实现依赖，契约在共享上下文。正式打包脚本原本递归收录 publish，未增加第二套发布路径。旧 Firewall HTTP/释放与读/配对规则/结构化校验回归、Windows 原生只读回归和默认模块全量通过。

### Tailscale TS-01：本地实际工作结束与外部持久责任分开

当前三代理共享同一生产 runtime/coordinator，已具备方法租约及原持久控制/登录流程，无需新设 resident actor。新增验收使用真实注册、runtime、journal、授权作用域及生产 Helper 桥，仅替换最外层 IPC transport，验证唤醒/执行/对象释放的完整服务图。Prepared/Unknown/Issued/Closure 留在原加密账本；Applying 后等待原实际任务，HTTP 取消不能提前归还租约。浏览器后续、daemon 和 Helper 原始 intent/receipt 仍由原权威负责，管理对象休眠不清账、不执行退出登录、不以后来 Running 状态推导成功。

修复两 journal 的实际资源缺口：在 `SqliteConnection.Open` 或初始化 PRAGMA 失败时，尚未发布给调用方的连接现在立即 Dispose。正常路径仍 Pooling=false、按调用作用域关闭；损坏文件报错且保留原字节，不自动重建或重放操作。

Windows/Linux 各 **27 项**通过：16轮真实 runtime/协调器/journal/Helper 桥对象图回收，保留已完成调用 Task/挑战也不根住旧图；非合作 IPC 与已受理修改取消期间拒绝释放；控制和首次登录的 Unknown 跨代只凭原回执恢复，原 session 单次挑战隔离、关闭意图跨代不重提交、授权拒绝不达 Helper。两 journal 各16次损坏数据库 Open 后，未强制 GC 即核验连接/原生文件句柄释放并保留原损坏页；Linux 另检查 `/proc/self/fd`，不以 .NET FileShare 锁替代原生 SQLite 证据。

Tailscale 仍在 Server 程序集中，本阶段只证明对象边界和持久责任连续性，不声称其 ALC 回收或真实 daemon/Helper 崩溃验收。原 Tailscale 控制、登录、回执恢复、权限及 HTTP 全专项在 Windows 通过；未运行安装或改变测试机 tailnet/防火墙配置。

### 交付证据与仍开放条件

证据统一为本地 Git 忽略目录 `.artifacts/firewall-tailscale-20261105/`：`domain-windows.log` / `domain-linux.log`、两平台 `modules-*.log`、`tailscale-windows.log`、`firewall-build-guards.log`、`publish-layout.json`、构建/发布日志与 `remote-provenance.json`。首次编译中测试 nullable operationId 的10个错误已修正并保留原日志，不作为通过证据。SSH 固定主机密钥与归档 SHA256 核验，普通 UID 1000 直接复用 `/home/nanami/relaxkonos-tests/firewall-tailscale-20261105`；未替换运行中的 Server/Helper、未修改系统网络或账号。

Firewall 真实 UFW/Windows 写入中断、并发外部规则漂移/Helper 恢复矩阵，以及当前正式官方包整体 ABI/多平台业务负载下内存与冷启动收益仍开放。Tailscale 独立代码是否值得拆分须由基线决定，本批不为改而改；其真实 daemon/Helper/浏览器断连联合恢复也未验收。默认模块对象/代码回收测试不是生产 RSS 下降证据，MB-01/02/07/08 不因此整体关闭。

## 42. MB-01A：当前资源清单与分层测量（2026-11-05）

本阶段以 `443b0b7e1143fca826a51239afd19ea17022ca6a` 为产品源码基线，开始时master工作区干净；本次不修改产品模块行为、默认宽限期或SystemPerformance S1。[资源清单](RelaxKonOS.ServerModuleResources.Inventory.md)覆盖9个内置模块、官方安装包和第三方ModuleHost的根引用、后台责任、缓存/私有依赖、外部进程、持久恢复权威与释放层级。[测量方案](RelaxKonOS.ServerModuleBaseline.Protocol.md)固定完整矩阵及工具限制，[本批报告](RelaxKonOS.ServerModuleBaseline.20261105.md)拥有逐轮数值、环境、证据、阻塞与下一阶段门槛。

当前验收严格区分完整宿主启动/闲时控制与Git/Terminal局部生产服务业务夹具。后者核验真实CLI、Dormant/新代唤醒和真实PTY输出/关闭，但没有完整Host/认证/Hub/Helper，手动TrySleep与5秒窗口不等于原300秒自然闲时与长期稳态。Linux静态全关因无法核实固定主机路径Proxy恢复责任被原门禁拒绝；未清账、改权限、改防火墙/Tailscale或移除必要责任。

MB-01A不关闭MB-01/RES-01：真实Firewall只读及回收前后指标、完整宿主认证业务、自然释放/再唤醒、Hub重连、启动峰值、外部共享服务归属和退出后全树核验仍缺。对象释放、ALC仍待自然回收、会话树关闭或静态配置差额均不解释为生产内存下降。下一步先补MB-01B正式宿主矩阵，再按证据评估Terminal容量/worker、Git/Firewall收益与常驻领域责任迁移；SystemPerformance S1继续冻结。

## 43. MB-03 构建清理责任与测试机预检（2026-11-06）

Git 构建在失败或取消后继续持有原代次工作租约，并在镜像清理结果确定后才提交终态。删除仅针对当前 operationId 的镜像标签；删除返回非零时，通过 Engine 成功列出该精确标签且结果为空确认其不存在。清理不可确认时，原回执持久化为 interrupted / git-build.image-cleanup-unverified，并保留恢复门禁。若 CLI 进程清理本身不可确认，保留 process-cleanup-failed，避免与未知执行者并发删除。相同幂等键仍读回原回执。已写入的回执继续通过原代次查询；台账写入失败仍隔离代次。该变更未提供解除恢复门禁的自动流程。

测试机以既有固定主机密钥连接。普通 SSH 身份无 Docker socket 权限，以现有 sudo 凭据做资源预检。Engine 29.8.2、Buildx 0.37.1、默认驱动内置 BuildKit 0.33.1；实际验证了缓存 Alpine 3.22 上随机操作标签的删除，以及对已不存在标签的删除失败和精确空列表。测试只添加和删除该随机标签。该检查覆盖 Engine 命令语义；完整服务的 BuildKit 构建、取消、断连、重启与 ALC 联合验收仍开放。现有节点为默认 docker 驱动，缺少 Git 门禁要求的专用受限 remote builder；测试机默认出站代理无法访问镜像仓库，清空代理环境后直连 Docker Hub 与 GitHub 均超时。仍需准备可验证的 rootless BuildKit 制品和可达的专用构建仓库，再进行执行中故障验收。

Linux Release 后台工作专项 15 项通过（--module-background-work-checks），覆盖终态前的原代次租约、不可确认清理后返回原回执且禁止休眠，以及持久化失败隔离。其中 CLI 取消使用真实子进程，构建与清理异常使用受控命令执行器。

MB-05 预检发现同一 Engine 有其他运行中的容器，且测试机存在 Nginx。后续使用独立 Engine 实例、独立 Nginx 配置与端口、可控 CA 和独立挑战域进行故障注入。共享服务重启、CA 订单未知结果、真实响应排空与三域原操作恢复本轮未验收。原 MB-05A 阶段证据见 §39。

## 44. MB-03 离线 BuildKit 与 MB-05 隔离设施续核（2026-11-06）

### MB-03 离线制品与启动阻塞

从 Docker Hub 的 Moby 仓库取得 `moby/buildkit:v0.34.1-rootless`。以 Registry 返回的内容摘要核对索引，并按摘要核对 linux/amd64 清单、镜像配置及各压缩层，同时核对解压层的有序 diffID。平台清单 SHA-256 为 `ace6811c21334fa502644b9711e0ea48e8539c5c2a843c2325dfb7b987ab260b`，配置 SHA-256 为 `9cca736f4c5c924382d15804fdd3e1702823c83ec19546e569e18ae33e700380`。通过固定 SSH 主机密钥离线传输并复核归档 SHA-256，Engine 导入后验证有序 diffID、入口、环境和 UID/GID 1000:1000。本次校验基于官方 Registry 的 HTTPS 与摘要链，未验证发布者签名。

专用 remote builder 使用随机容器和内部网络、独立 Docker CLI 配置、1 GiB 内存、1 CPU 和 128 PID 上限，未使用 privileged、宿主网络或绑定挂载。RootlessKit 在 fork/exec /proc/self/exe 时收到 permission denied，宿主 kernel.apparmor_restrict_unprivileged_userns 实测为 1。Buildx 引导等待未建立连接，按失败记录。容器、网络和本次导入的镜像标签已核对清理。共享 Engine 持续运行，宿主 AppArmor 和 sysctl 未变更。后续需要允许专用 RootlessKit 创建 user namespace 的隔离 Linux 实例或经核准的专用 AppArmor 策略，以及宿主 Git CLI 和 builder 均可达、信任其证书的 HTTPS 443 测试仓库。构建中取消、断连、原回执恢复及 ALC 联合验收仍开放。

### MB-05 外部设施隔离预检

- Engine/Compose 使用缓存 Alpine 3.22 和随机项目，无外部网络。只向测试容器注入 SIGKILL，真实 Engine 观测到 exited:137。启动后容器 ID 相同、启动时间更新、原命名卷内容可读；按项目标签确认容器和卷已清空。
- Nginx 以普通用户和独立配置在回环高端口提供 TLS，客户端用测试证书验证响应。向该实例发 TERM 后确认 PID 文件移除和 HTTPS 请求失败；使用原配置及证书启动新 PID，原 HTTPS 响应恢复。正常退出并清理临时目录。
- 两项均直接调用外部设施。RelaxKonOS 的真实管理 Task、响应及原 operationId 持久化恢复、Engine daemon 重启、真实 ACME CA 订单和挑战连续性尚未覆盖。测试证书由 OpenSSL 本地生成。下一阶段以独立 Engine 实例、独立 Nginx 执行配置、可控 CA 和专用挑战域运行应用级故障矩阵。

本机原始探测日志保存在 .artifacts/mb03-buildkit/。

## 45. MB-05 Docker 应用层在途中断与回执恢复（2026-11-06）

Compose 命令取消或超时时，生产执行器终止尚在运行的 CLI 进程树，并等待 CLI 退出和输出流排空后返回。无法核实清理时，栈协调器把异常交给工作 owner 隔离，保留原活动台账。启动对账的只读观察若无法核实 CLI 清理，同样保留责任。关闭时已取消且本地清理完成的操作保留原 Running 回执，供下次启动观察 Engine 后记录 Interrupted。

Ubuntu 测试机使用固定 SSH 主机密钥、现有 sudo 凭据、缓存 Alpine 镜像 ID 和随机专属 Compose 项目。Linux Release 的 --stack-live-only 通过真实 DockerStackOperationCoordinator、DockerComposeService、持久台账和 resident 工作租约执行。容器的 TERM 日志证明 Engine 已接受停止请求，且当时实查 Docker CLI 仍在运行、回执为 Running、代次有工作租约。调用协调器 StopAsync 后，实查原 CLI 已退出，工作租约已归还，原操作仍留待对账。新协调器重新打开同一台账，在工作租约保护下观察真实服务并以原 operationId 记录 Interrupted / docker.stack_interrupted。最终项目为 ad04live93e97f1b，恢复操作为 de667356-ceb6-4b4b-a3f6-7bae6bc610b9。

测试等待 Engine 已接受的停止动作自行结束，再用独立新操作显式启动原容器。以原停止请求的幂等键重试，返回同一 Interrupted 回执，容器继续运行且 TERM 记录只有一次。按项目标签确认容器、网络和卷已清理。共享 Engine 未重启，宿主 AppArmor 未变更。本机 --stack-operations-only 台账专项通过。

此切片验收了真实 CLI 在途保护、协调器停机排空与启动恢复。协调器在同一测试进程内重建，未注入整个 Server 进程崩溃或 Engine daemon 重启。Engine 已接受的动作可在 CLI 断开后继续完成，恢复快照不保证 Engine 已稳定。CLI 清理无法核实的保护路径已实现，未做真实进程清理失败注入。HTTP 响应排空、真实 CA/Nginx 应用层联合矩阵及生产内存仍开放。实机原始日志与制品哈希见 .artifacts/mb05-docker-live.log 和 .artifacts/mb05-docker-provenance.json。

## 46. MB-06 与 Terminal 安装、崩溃和恢复续核（2026-11-06）

当前阶段从 `b41f91ef` 继续，保留 §40 的已有实机进程树证据。安装 ModuleHost 在每次启动重新读取信任同意和验证包，并将同意绑定的版本及摘要交给隔离启动器。在途和后续准入按当前同意修订核对，撤销后重新授权也不能恢复旧令牌。Linux 由有权限的监控进程在真实树清理后发退出证明；Windows Helper 只在 Job 原生确认后发送认证树退出回执。控制断开、超时或单个父 PID 退出均不能替代该证明。

**已实现且已验证。** Windows 当前账户 Job 11 项通过，覆盖根先退、脱离后代、禁止 Job breakaway、原生活动数归零、Job 句柄关闭与同账户无关进程存续。Windows、Ubuntu 本地 Terminal 父端终止测试先执行真实 PTY 输入输出，只终止拥有 PTY 的父进程，再核对根、子、孙退出；Ubuntu 还核对 worker 退出、后代已 reap。Ubuntu 的本地树 17 项、真实控制输入取消 2 项通过。测试机沿用原有 SSH 主机密钥核验。原始日志见 `.artifacts/terminal-recovery-20261106`。

Windows 本地 Terminal 树 17 项、生命周期帧 11 项、驻留 15 项通过。帧测试覆盖实际阻塞的命名管道输出写入取消。沙箱内跨模块套件停在 MB-05 Certificate 恢复断言，普通 Windows 令牌下重跑完整 `--module-boundary-milestones-only` 通过。

Windows ModuleHost 专项 36 项通过。Ubuntu ModuleHost 专项 40 项和原生树专项 4 项通过。使用独立 root-owned 当前启动器、现有机器策略和专用模块 UID，完成合作关闭、控制断开和 SIGKILL 模块根进程各两组（普通后代及独立 session 后代）。注入前观察后代读写，注入后检查监控器树证明、根与后代 reap 以及该 UID 无存活进程。崩溃组保留实际非零监控器退出。旧代回调和信任修订检查由真实 Broker 端点及受控代次测试覆盖。正式固定 sudo 启动器未替换，临时源包已移除，原数据已还原。证据见 `.artifacts/mb06-linux-crash-20261106`。

**已实现、安装环境未实测。** Windows 安装夹具更新到 schema 3，覆盖正式运行时快照置备、已签名包启动、LocalSystem/S4U、已观测的实际后代输出、Core 崩溃、Helper 崩溃与信任撤销后树核对，以及原 Broker 回执和副作用不重放。Windows Terminal Helper 增加启动应答和输出/控制通道异常的清理保护，安装入口补启动拒绝后的复测。当前 Windows 执行令牌未提升，未置备 SCM 测试服务、S4U 账户或正式 Terminal Helper。管理员执行条件、独立制品和清理恢复见[模块安装验收](RelaxKonOS.ServerModuleV2.Progress.md)和[Terminal 应用文档](../applications/RelaxKonOS.Terminal.md)。

**仍开放。** Windows 安装后的全树崩溃和恢复矩阵、Linux 完整安装和监督器本身的故障恢复、未知责任的正式运维恢复以及跨代应用副作用矩阵。Linux Terminal 的 EPERM 实机证据沿用 §40。自动审批拒绝本轮特权复测，理由是清理失败可能留下 root 所有的测试后代。需管理员在隔离宿主运行 `--terminal-tree-privilege-checks` 并接管该夹具的清理。保留无权终止后代的未知责任，不扩大权限。SDK 后台责任申报和合作 drain 尚未验收，ModuleHost 保持 resident，未启用闲时 kill、退出或重启。Terminal 保持当前驻留策略，SystemPerformance S1 保持冻结。进程树证明、对象释放与 ALC 回收均不计为生产内存下降。

## 47. 固定启动器与跨代故障验收预检（2026-11-06）

**已验证。** 在 Ubuntu 测试机使用既有固定 SSH 主机密钥，确认正式 `relaxkonos-server` 用户的无密码 sudo 授权绑定已安装的 ModuleHost 路径和 `--isolated` 参数。以该用户通过原规则启动 0.2.7 的固定启动器，空请求返回 `module-host-isolation-failed`，退出码 77。只验证了真实安装入口与拒绝路径。共享服务和机器级策略未变更。另以普通用户创建、观察并回收实际子进程，验证新夹具在进程处于僵尸状态时保留未知责任，待原 PID/starttime 消失后才接受回收。

**夹具已实现，完整故障矩阵待执行。** `Tests/ServerModules/verify-linux-v2-response-loss.py --core-tree-crash` 在原真实 Broker 回执矩阵上加入故障前的实际后代输出、仅终止 Core 后的原 ModuleHost 树回收，以及新代次的真实输出。以原计划核对 RecoveryRequired 和禁止重放，最后用新明确批准产生一次新动作。预检要求机器级源包根和 sudo 路径都指向专用测试安装。当前 Ubuntu 的独立服务处于停止状态，机器级启动器绑定共享安装。需要已用当前制品和签名夹具置备的隔离 Linux 主机，不能在共享主机上重跑会改写固定配置的置备脚本。清理无法确认时保留证据并停止重启夹具 Core。

**Windows 待执行。** 新的按需测试机未能连接 SSH，本地 Windows 令牌未提升。LocalSystem/S4U 与正式 Terminal Helper 矩阵仍需启动测试机、核验主机密钥，并取得管理员执行条件。沿用 §46 产物和清理入口。SDK 后台申报与合作 drain 仍开放。

## 48. Windows LocalSystem/S4U 与正式 Terminal Helper 续核（2026-11-06）

使用经核验并固定 ED25519 主机密钥的 Windows 测试机。SSH Administrator 可使用已提升令牌，普通测试账户未提升。测试机无共享 .NET 10 运行时，隔离 ModuleHost 夹具使用当前源码的 Windows x64 自包含制品。验收脚本支持直接复用 SSH 的提升令牌，仍使用唯一服务、临时账户和隔离根。

正式 Terminal 使用测试机已有的 `1.0.0+c58de8e9` 安装，未替换 Server/Helper。LocalService Server 和 LocalSystem Helper 分别注入单进程终止。两轮均先经真实 Hub 与 PTY 读写取得根 shell 和实际子进程，核对父子关系及 PID/启动时刻，再分别观测旧实例消失和传输结束。恢复后通过无效目录拒绝、普通及管理员身份、真实读写、并发、退出与重连检查。最终确认 Server 停止、Helper 运行，临时 Terminal 用户无残留。验证范围为已有安装及一层后代，当前制品的正式安装与更复杂的安装后代拓扑继续开放。[Terminal 应用文档](../applications/RelaxKonOS.Terminal.md)

ModuleHost 夹具改用当前安装计划的资源声明和审计台账格式，并按系统支持选择原生管理员或工作站 owner-device 确认。Windows Server 上没有启用工作站专用能力。未改变 SystemPerformance S1 或 ModuleHost 闲时策略，未测量生产内存收益。

当前 ModuleHost Windows 安装夹具最终 **78 项**通过。生产运行时安装函数复用不可变快照并拒绝额外文件及损坏制品。实际 LocalSystem Helper 通过 S4U 启动不同的普通模块 SID，验证私有读写、跨模块和 Helper 管道拒绝。通过已签名包、实际 Core 及真实 Broker 执行，覆盖仅终止 Core、仅终止 LocalSystem Helper、信任撤销三组。每组先读到真实后代 stdout，再观察原根及后代离场。持久回执在中断后保留 RecoveryRequired，重复查询及旧确认未重放 SCM 动作，恢复动作需新明确确认。清理报告为 true，独立查询确认临时 SCM 服务、临时管理员和 Terminal 账号无残留。原服务状态已还原。Windows 当前账户原生 Job 另通过 11 项。

本轮 Core 由提升的测试调用者运行，ModuleHost 使用隔离测试服务和生产运行时安装函数。整套正式发行安装服务、Windows 工作站 owner-device 分支和 ModuleHost 根单独崩溃的 Windows 带内回执未在本机验收。Linux §47 的跨代矩阵仍待独立安装环境。实机证据保存在 `.artifacts/mb06-windows-native-remote/remote-rerun3.log`、`.artifacts/windows-terminal-Server-crash.log` 和 `.artifacts/windows-terminal-helper-crash-retry.log`。

## 49. 三层 Terminal 树、正式制品与 Linux 固定启动器（2026-11-06）

**Windows Terminal 实机通过。** 在已有正式安装 `1.0.0+c58de8e9` 上分别只终止 Server 和 Helper。实际孙进程写入 PTY 标记，故障前核对两级父子关系及根、子、孙三个进程的启动时刻，故障后确认三个原实例均消失，并分别观察传输结束。Helper 组完整通过恢复。Server 组恢复检查在最终关闭会话时遇到 SSH 转发断开，独立复测完整通过。恢复包括无效目录拒绝、普通和管理员执行、真实读写、并发及重连。独立复查确认原 Server 停止、Helper 运行，临时账号为零。当前制品的正式 Terminal 安装待验收。[应用文档](../applications/RelaxKonOS.Terminal.md)

**Windows 正式制品已核验，正式发行服务链待执行。** 生产打包生成不同源码的 Windows x64 自包含 0.2.7 和 0.2.8，两个包均通过生产清单核验，相关本地回归 44 项通过。测试机已有正式服务名，验收入口按设计拒绝接管。该 VMware 客体没有可用的嵌套虚拟机工具。需在正式服务名未占用的隔离 Windows 实例运行安装、升级、恢复与清理。[正式制品及执行条件](RelaxKonOS.ServerModuleV2.Progress.md)

**Linux 固定启动器两代实机通过。** 在私有挂载命名空间中使用生产安装脚本生成策略和精确 sudo 规则，以正式 Server 账号通过实际 `sudo -n --` 启动当前 launcher。两个代次都完成实际后代读写，分别以合作关闭和控制断开结束。独立 session 的后代忽略 SIGTERM，仍获得明确树证明并核实专用 UID 无残留进程。宿主正式策略哈希保持一致。原始证据见 `.artifacts/mb06-linux-fixed-namespace.log`。

**Linux 固定启动器跨代回执实机通过。** 私有命名空间中使用当前 Core 与实际 Helper、PAM、签名包和固定 sudo 启动。待原回执为 Executing、独立 systemd 测试服务正在停止后，仅 SIGKILL 原 Core，确认原根和独立 session 后代已回收且专用 UID 无残留。重启 Core 后读到同一 RecoveryRequired 回执，旧批准与重复读取没有重复服务动作。新代次完成真实读写，新明确批准只执行一次新动作。最后确认新代树回收、宿主正式策略哈希保持一致。夹具运行记录见 `.artifacts/mb06-linux-fixed-core-recovery.log`，现场标识 `core-BNBKVP3J`。此结论覆盖本次隔离固定启动器及真实持久回执路径，宿主共享发行服务未参与故障注入。

缺少实际退出证明时保留未知清理责任。ModuleHost SDK 后台申报及合作 drain 仍开放，未启用闲时 kill、退出或重启。SystemPerformance S1 保持冻结。本轮未测量生产内存变化。

## 50. MB-03/05 隔离测试设施复核（2026-11-06）

从 `8ed2cc74` 继续，选 MB-05 跨进程连续性作为下一项。以既有固定 SSH 主机密钥连接 Ubuntu 测试机，完成只读预检。共享 Docker 与 Nginx 服务均处于 active。测试账户无法访问共享 Docker socket，未发现其用户级 Docker socket。宿主有 dockerd、Nginx 与 .NET，PATH 中未找到 RootlessKit、slirp4netns、Pebble 或 step-ca。AppArmor 的非特权 user namespace 限制为 1，普通测试账户执行 `unshare -Ur true` 失败。没有启动第二个 daemon、修改安全策略或注入共享设施故障。

**已实现且已验证**：沿用 §45 的真实 Engine 在途 CLI 排空与同进程协调器重建证据，以及 §44 的独立 Nginx TLS 连续性证据。本次新增证据仅为上述只读资源检查。

**已实现但实机未验证**：§39 的 WebServer 与证书后台责任测试仍使用受控外部动作。实际 CA 请求或 Nginx 发布中断时的完整 Task、响应、终态持久化及清理责任仍待观察。

**仍有阻塞**：MB-05 需要可独立重启的 Engine 和专属工作负载，以及可控 ACME 目录、受信 CA 和仅用于验收的挑战名称及解析。可用隔离 Linux 实例同时解决 Engine 和 MB-03 的 RootlessKit 前提。MB-03 还需要 Git CLI 与 builder 均可达且信任其证书的专属 HTTPS 443 仓库。后续只对专属测试实例注入故障，以原 operationId 核对回执与副作用次数。MB-04 的安全 owner 交接与真实网络故障验收仍开放，Proxy 保持 resident。此次没有生产内存测量。


### MB-05 Docker 本地跨进程回执续核

从 `d255af3e` 继续。Windows Release 聚合入口 `--module-boundary-milestones-only` 通过，包含新增 `--docker-process-recovery-only` 夹具。夹具在独立进程使用生产栈协调器与持久台账，替身 Compose 在工作租约内向临时文件记录一次部署后阻塞。父进程确认原回执为 Running 且副作用见证已落盘，再强制终止原进程并确认异常退出。新进程读取同一台账，执行一次只读观察，以原 operationId 记录 Interrupted。相同请求键返回与恢复查询一致的回执，部署执行次数为零。父进程再次打开台账，确认终态与观察结果持久化，副作用见证仍只有一次。

本轮日志 `.artifacts/mb05-release-final.log` 中的 operationId 为 `ac1cda96-23e8-453a-8b5c-b69cf8d919d1`。沙箱令牌下的既有聚合回归在证书恢复断言处失败，普通 Windows 令牌下完整 Release 重新构建和回归通过。该夹具覆盖跨进程持久责任与幂等回执，外部动作由文件见证替身提供。真实 Engine daemon/Server 联合故障、HTTP 响应排空、CA 与 Nginx 应用层中断仍开放。

### MB-05 既有 Linux 测试机续核

以 `37fe327b` 发布 Linux x64 Release 制品。沿用固定 SSH 主机密钥，在随机临时目录核对归档 SHA-256 `dd06f7c1561084a63c295bbadbc4f0ff01f54f0656f874212e39ef267fbc9d3c` 后执行两项测试。

- 普通账户跨进程夹具通过。原 operationId `e8a6ae83-63c0-4bda-83bf-0f26ba3592b3` 恢复为 Interrupted，文件副作用见证为一次。
- 真实 Engine 使用专属 Compose 项目 `ad04live870f40d4`。Engine 接受停止后，实际 CLI 在租约保护下排空。同一进程内重建的协调器以原 operationId `da1fc630-653a-41f0-8524-b93b3ce07ce1` 恢复 Interrupted。显式启动容器后，同键请求返回原回执，容器保持运行且停止见证仅一次。部署的即时快照为 Succeeded，独立后续观察确认 worker 退出码 7，原回执未改写。按项目标签核对容器、网络及卷清理完毕，上传目录已删除。

证据见 `.artifacts/mb05-linux-current-run.log`、`.artifacts/mb05-linux-process-current.log` 和 `.artifacts/mb05-linux-engine-current.log`。两项分别覆盖跨进程台账和真实外部动作，真实 Engine 下的整个 Server 崩溃仍待联合验收。共享 Engine 和 Nginx 未重启。CA 与 Nginx 应用层中断、独立 Engine daemon 重启及 MB-03 RootlessKit 阻塞继续开放。

### MB-05 真实 Engine 与工作进程崩溃联合验收

Linux x64 Release 在既有测试机的专属项目 `ad04live4722012f` 通过。独立测试进程使用生产 Docker 栈协调器、Compose 服务及常驻工作租约，提交真实停止请求。父测试进程观察容器收到 TERM，确认对应 Docker CLI 仍在运行、原回执已持久化为 Running，再仅终止该测试进程。确认工作进程异常退出、原 CLI PID 消失且 Engine 报告容器 exited 后，新协调器才读取原台账并观察 Engine。原 operationId `a73b18b4-f9c3-40cf-a18e-c017fc08efb5` 持久化为 Interrupted。显式启动原容器后，同键请求返回与持久回执一致的结果，容器继续运行，崩溃组只产生一次停止见证。

同一轮通过原正常停机排空、受控跨进程台账回归和专属项目资源清理。Windows Release 聚合回归通过。Linux 制品归档 SHA-256 为 `c48dc98798a2f998469c59946887b0d15408f2e23fa2b45d521b0bf21dd70323`，原始证据见 `.artifacts/mb05-linux-crash-run.log` 和 `.artifacts/mb05-crash-windows.log`。共享服务持续运行。测试进程承载生产 Docker 协调器，完整 Server HTTP 传输、Engine daemon 重启、CLI 无法核实退出、CA 与 Nginx 应用层中断仍开放。

### MB-05 真实 Nginx 重载与管理工作进程崩溃

既有 Linux 测试机以普通账户运行 Linux x64 Release 的 `--nginx-live-recovery-only /usr/sbin/nginx`。专属 Nginx 仅绑定随机回环端口，配置、PID、日志及临时目录独立。独立工作进程使用生产 WebServerOperationStore 接收并持久化重载请求，向专属 Nginx 发送真实重载信号，然后在提交终态前停在受控屏障。测试观察到新配置 HTTP 内容、一次实际重载事件及原 Running 回执后才终止工作进程。

恢复进程读取原台账，将操作 `94c83391-b03f-4dad-870f-3a627f5a0705` 持久化为 Failed / interrupted / webserver.operation_interrupted。同键重试与重新打开台账均返回同一回执，原 Nginx 继续提供新内容，重载事件仍为一次。专属实例正常退出，临时目录清理完毕。制品 SHA-256 为 `1a83a7e8f646edbab6bb568bf763c2b2dc2f6ef82646d84ef1cf8b6c3df7ba42`，日志见 `.artifacts/mb05-nginx-final.log`。首轮日志事件计数断言失败，修正后复测通过。

本项覆盖生产 WebServer 操作台账和真实 Nginx 动作，重载命令由专属夹具调用。完整 Nginx provider、网站发布、TLS/ACME 及 HTTP 响应排空仍待联合验收。共享 Nginx 持续运行。

### MB-05 直接 WebServer 操作取消与排空

直接 Nginx 操作沿用原 WebServer 操作台账，并在写入 Queued 前取得 WebServer 常驻工作租约。取消请求返回当前回执并发出取消信号，实际动作结束后才写入 Cancelled。桌面端继续轮询已提交的操作。停机等待实际后台工作，停机取消保留原活动回执供启动恢复；终态保存或 CLI 清理无法确认时，工作 owner 保留故障责任。停用检查同时读取直接操作和网站发布的未终结台账。Linux 本地 Nginx 命令取消后等待 CLI 退出及输出排空。

Windows Release 聚合回归通过。新增受控取消测试确认实际动作仍在运行时，原回执保持 Running，租约仍在，台账仍列为未终结；放行动作后才持久化 Cancelled 并释放租约。证据见 `.artifacts/mb05-web-drain.log`。实际 provider 取消、真实 CLI 清理无法核实的故障路径及网站发布联合验收继续开放。

### MB-05 WebServer 启动恢复准入

直接操作台账从构造时读取和改写迁到显式 hosted 启动恢复，复用同一 WebServer 常驻工作 owner。恢复完成前拒绝直接操作准入和回执读取。原 Queued/Running 按原 operationId 写成 interrupted；解析、读取或持久化失败时保持恢复责任并拒绝新操作。停用检查使用同一只读解析路径，损坏文件、目录占位及权限错误按不可核实处理。

Windows Release 聚合回归通过。新增损坏台账路径确认保留原文件、持有一份待恢复租约且拒绝新动作。此路径未清除台账或重放副作用。生产 provider 及网站发布的完整实机故障矩阵继续开放。

既有 Linux 测试机的真实 Nginx 重载与崩溃夹具以显式启动恢复复测通过。原 operationId `fa2bbf90-cb26-4280-819e-0cefc9635442` 持久化 interrupted，重载仍为一次。归档 SHA-256 为 `958d06c78edddaa53751c6a28de1924c1e6363522e282f145bb0e618d46f6378`，日志见 `.artifacts/mb05-web-startup-nginx.log`。

### MB-05 Nginx provider 和门面的原回执恢复

WebServer 门面将生命周期及重载请求交给生产 provider 核对原回执。Nginx provider 按原 actor、操作类型、实例 ID 和幂等键查询台账，再执行实例发现。实例不再被发现时，两条重载入口仍可返回原回执。

既有 Linux 测试机通过生产 WebServerManager、NginxWebServerManager、PrivilegedNginxOperations 和 WebServerOperationStore 的联合测试。夹具替换 Helper 传输，只接受封闭的 Reload 请求并重载独立回环 Nginx；没有连接共享 Helper 或调用 systemd。真实重载生效且原操作持久化为 Running 后终止工作进程。恢复后移除专属发现 marker，确认实例不在发现结果中。生命周期及独立重载入口以原键取得 `bb3cc3d1-e9dd-40e0-9c75-83b85eb50593` 的同一 interrupted 回执，传输调用为零，实际重载仍为一次。专属 Nginx 正常退出，临时目录已清理。Windows Release `--webserver-only` 通过。

日志见 `.artifacts/mb05-provider-final.log`，归档 SHA-256 为 `d38b7b36458e78889f24a64a5aa5298ec41d36383c3e73ab8a97b9732e810a1d`。真实 Helper 响应中断、完整 HTTP、网站发布及 TLS/ACME 联合验收继续开放。

### MB-05 网站发布站点应用后的停机责任

生产 WebsitePublicationCoordinator 在站点应用已返回成功后，先完成应用目录关联，或等待原有的关联失败补偿，再响应停机取消。目录关联先于关联阶段日志和后续外部验证。停机取消仍保留原活动回执供恢复，恢复只记录 Interrupted。同键请求继续取原回执。

Windows Release 聚合新增验证覆盖停机等待尚未返回的站点动作、原工作租约、原台账跨实例重开，以及只执行一次站点应用。站点 provider 与证书为受控边界。完整生产 provider 的网站发布、真实 HTTPS 响应中断和补偿失败仍待实机联合验证。

既有 Linux 测试机也通过 Release 聚合回归，新增检查为后台 owner 第 33 至 35 项。首次 Linux 执行通过新增项，随后因 Windows 打包未保留测试 apphost 执行位而在 Terminal 停止。补专属临时目录中的执行位后，整组复测通过。证据见 `.artifacts/mb05-publication.log` 和 `.artifacts/mb05-publication-linux-final.log`，Linux 制品归档 SHA-256 为 `cd32c94a1e4fb6a69f95d0230c367902f6771213de873d8be9f19a6fae68382f`。

### MB-05 应用记录消失后的发布回执

网站发布在同一台账准入区间先按原 actor、幂等键和请求身份查找回执。只有新操作需要核对应用仍存在。原应用记录已删除时，原请求仍返回 Interrupted 回执，新请求返回 404 且不创建台账记录或再次应用站点。验证使用生产协调器、应用目录和发布台账，站点动作由受控 provider 记录。

Windows 与既有 Linux 测试机的 Release 聚合回归均通过，新增及更新的断言为后台 owner 第 35、36 项。日志见 `.artifacts/mb05-publication-replay-win.log` 与 `.artifacts/mb05-publication-replay-linux.log`。

### MB-05 网站发布真实 HTTPS 验证响应中断

生产发布协调器使用真实回环 HTTPS 请求执行网站验证。夹具在随机端口使用专属证书，客户端核对指纹和主机名，系统信任库保持原状。先通过 TLS 探针，再让服务收到验证请求后挂起响应头。原操作进入 Verifying 并持有 WebServer 工作租约。触发停机后，测试等待协调器排空，并由服务端独立观察 RequestAborted。原台账仍活动，重新打开后以原 operationId 记录 Interrupted，同键重试既不发起第二次验证，也不再次应用站点。

站点应用、证书目录和 Docker 工作负载观察仍由受控边界提供。本项覆盖发布协调器的真实出站验证请求，完整管理 API 的响应写入、生产 Nginx 网站发布、真实 CA 和未知补偿仍待验收。首轮证书未完成握手，修正夹具证书加载并通过独立 TLS 探针后复测。

Windows 与既有 Linux 测试机的 Release 聚合回归通过，新增为后台 owner 第 37 至 39 项。日志见 `.artifacts/mb05-https-win-final.log` 与 `.artifacts/mb05-https-linux.log`。Linux 制品归档 SHA-256 为 `92a435a95330e215516aeae6e696377249929c34fdd656e017dbda0539fafec5`。

### MB-05 网站发布补偿结果未知

站点已应用且应用目录关联失败时，原发布工作租约一直持有到补偿返回。补偿结果无法确认时，原回执持久化为 Failed / website.application_association_recovery_failed，并带 website.recheck_required。原台账保留这份责任，常规历史裁剪不会移除它。停用检查可见该责任，同一应用或同一 WebServer 与域名组合的新发布被拒绝。同键仍返回原回执，启动及重试均不重放站点应用或补偿。

本项使用生产协调器、目录和台账，站点补偿使用受控 provider。操作员确认并解除门禁的正式入口、补偿执行过程中被强制终止的故障矩阵及实际 Nginx 补偿仍待验证。

Windows 与既有 Linux 测试机的 Release 聚合回归通过，新增为后台 owner 第 40 至 42 项。首轮并发删除触发目录通用异常并绕过补偿，目录改用明确的应用不存在错误后复测通过。日志见 `.artifacts/mb05-compensation-win-final.log` 与 `.artifacts/mb05-compensation-linux.log`。

### MB-05 站点配置阶段中断的持久责任

恢复读取原活动回执时，若最后持久阶段为 Configuring，仍以原 operationId 写入 Interrupted，并记录 website.site_application_unverified。此阶段可能已经改变 Nginx，恢复保留原站点 ID 和证书引用，禁止同一应用或同一 WebServer 与域名的新发布，停用检查和历史保留规则继续承担责任。同键请求读取原回执，恢复不执行站点写入或补偿。关联已提交后的阶段和单纯验证阶段沿用各自的恢复语义。

测试使用生产发布协调器和持久台账，受控 provider 在 Configuring 阶段接收停机信号并返回取消。真实 Nginx 写入中被强制终止及人工核实后的解除入口仍开放。

Windows 与既有 Linux 测试机 Release 聚合回归通过，新增为后台 owner 第 43 至 45 项。日志见 `.artifacts/mb05-configure-win.log` 与 `.artifacts/mb05-configure-linux.log`。

## 51. MB-04 sing-box 授权到期执行边界

Core 唯一到期 worker 先在原加密领域台账和原 gate 下读取最早到期时间、失效凭据及 pending publication 信号。扫描只向调度循环返回截止时间和待恢复标志，未到期时不构造发布执行器。已到期、已禁用或已经替换的发布凭据，以及 pending publication，仍进入原单一发布执行器。执行器重新读取领域状态并按原规则核对 Helper。共享服务及原事务恢复语义不变。

这是原持久台账的调度投影，仍在每轮反序列化台账，完整持久调度索引、运行时安全责任交接、真实网络故障和动态休眠继续开放。未停用既有 worker，未改变 30 秒扫描节奏，也未测量生产内存。

Windows 与既有 Linux 测试机的 Release Proxy Core 安全 25 项及关联驻留和作用域专项通过。验证了截止边界、空闲不构造执行器、待确认发布和禁用凭据仍需要执行，以及受控 Helper 回执丢失后只有一次发布。日志见 `.artifacts/mb04-expiry-win.log`、`.artifacts/mb04-expiry-linux.log`。同机只读检查发现 `/usr/bin/sing-box` 和 `/dev/net/tun`，未启动实例或变更网络。

### MB-04 sing-box 授权到期的真实网络续核

既有 Linux 测试机用普通账户运行已安装的 `/usr/bin/sing-box`。专属实例和源站仅监听随机回环端口，TLS 验证核对专属证书指纹和主机名。真实 sing-box 使用生产渲染的 VLESS/TLS 入站和用户配置。夹具仅替换自身路由，放行一个回环源端口并拒绝其他目的地址。共享配置、服务和网络保持原状。

原凭据通过实际 VLESS/TLS 转发。写入未来到期时间后，等系统时钟达到该截止点，再调用生产到期扫描与原发布执行器。专属 Helper 传输完成真实 sing-box 配置切换后注入回执丢失。通过真实连接确认过期 UUID 无法到达源站，未到期 UUID 仍可双向传输。重开原加密台账后，发布执行器核对已应用材料，清除 PendingPublication，实际应用调用总计一次。

本项验证真实网络授权行为及生产台账的回执丢失恢复。运行时应用和状态证明由夹具 Helper 传输提供，正式 Helper、系统代理/TUN、既有连接撤销及动态休眠仍开放。

Linux x64 Release 日志见 `.artifacts/mb04-expiry-live-final.log`，制品归档 SHA-256 为 `799158891686780343c27b08dc2c085e917db963f0445588fb5d1b7629ce6d8e`。专属进程退出后临时目录已清理。

既有连接续核通过。到期前分别为即将到期和无到期时间的凭据建立真实 VLESS/TLS 双向流，并保持客户端和回环源站两端打开。专属实例切换后，两条旧流的客户端和源站均实际观察到 EOF 或断开。新连接中，过期 UUID 不能到达源站，未到期 UUID 能重新转发。故障回执恢复后仍只应用一次。此结果量化了夹具停旧启新的连接影响，正式 Helper/systemd 的运行时切换仍待验收。日志见 `.artifacts/mb04-streams-live.log`，归档 SHA-256 为 `9080c25e5cab568d85d2e4e48e1588861e90014f13ff7fd03722aa9d06f0b0ad`。

### MB-04 sing-box 持久到期索引

原 ProxyServerStore 在写入加密领域台账时同步生成独立受保护的到期索引。索引只包含原密文的 SHA-256、最早到期时间及待确认发布标志，不含凭据。两份文件分别原子替换，索引先写。启动或扫描只信任能通过数据保护校验、且摘要与所打开原台账一致的索引。索引失配、损坏、缺失或写入失败时，原台账继续提供到期和恢复责任。领域台账格式不变。

匹配的扫描按流计算密文摘要，跳过完整台账解密和对象反序列化。单一 Core worker、原 gate、原发布执行器和 30 秒扫描节奏继续承担授权及未知结果恢复。本项只交付 sing-box 到期索引，尚未将 Proxy 安全职责整体交接到持久调度，也未验证动态休眠或生产内存收益。

Windows 与既有 Linux 测试机的 Release Core Proxy 安全各 31 项、关联驻留与作用域专项通过。确认匹配索引不解密原状态，旧索引、损坏、缺失和写入失败均不遗漏待恢复或已禁用凭据。Linux 专属 sing-box 实机回归再次通过，原应用调用一次。日志见 `.artifacts/mb04-index-win.log`、`.artifacts/mb04-index-linux.log`、`.artifacts/mb04-index-live.log`。同机网络命名空间预检确认普通 user namespace 映射被拒绝，既有提权路径可创建进程私有网络命名空间。尚未在其中创建 TUN 或注入故障。

### MB-04 TUN 私有网络命名空间故障恢复

既有 Linux 测试机使用提权后的进程私有网络命名空间，启动前核对其与宿主命名空间不同且无既有接口。生产 ProxyTunSafetyService 与 HostProxyNetworkSafetyPlatform 观察真实 veth 默认网关。专属 runtime 夹具在原 marker 持久化后创建真实 TUN，再停在进程终止屏障。强制终止该工作进程后，原 TUN 和未完成 marker 仍存在。

移除私有默认路由后恢复原操作 `b99267b4-e215-49ac-9086-e6abb6d5e3c2`，实际删除 TUN 一次，但因原网关不可核实而保留 marker，新启用被拒绝。恢复默认路由后复查原操作并清除 marker，未再删除设备。新代启用后，重试原恢复引用不会移除新 TUN。专属接口和目录已清理，宿主路由与共享服务未修改。日志见 `.artifacts/mb04-tun-live.log`，Linux Release 制品 SHA-256 为 `21a96189d17ca64b3c8608a50911be7f047483b55997c7f7f20a13bd5a74ae64`。

本项验证生产 marker 和真实内核设备、路由状态。设备切换由夹具 runtime 提供，Mihomo 控制器、DNS、系统代理和动态休眠联合故障仍待验收。

### MB-04 TUN 未知 marker 的紧急停用边界

自动恢复和紧急停用均拒绝无法核实的 marker。损坏内容或缺少可信管理网关时，原文件保留，Mihomo 切换和路由恢复均不调用。新 marker 在原子替换前刷新到磁盘，停机取消保留先前已提交的证据。

Windows Release TUN 专项通过，覆盖损坏 JSON 和不可核实网关在自动恢复及紧急停用入口的拒绝。同机只读确认正式 Mihomo 1.19.33 二进制存在且共享服务 active，后续可使用独立配置和控制端口验证实际 Mihomo 切换，无需修改共享服务。

Linux Release 私有命名空间的真实 TUN 崩溃恢复复测通过，原操作为 `f3fcb878-dbe5-4eed-adb1-48a6e9bc1816`。Windows 与 Linux 日志见 `.artifacts/mb04-tun-marker-win.log`、`.artifacts/mb04-tun-marker-live.log`。

### MB-04 真实 Mihomo 控制器与 TUN 恢复

既有 Linux 测试机在全新私有网络命名空间复用已安装的 Mihomo 1.19.33 二进制。专属配置、控制端口和目录均独立。生产 MihomoTunRuntimeController、MihomoControllerClient、ProxyTunSafetyService 和路由验证平台共同执行。真实重载创建内核 TUN 后终止管理工作进程，保留未完成 marker。默认路由不可核实时，原恢复删除真实设备但保留 marker；恢复私有默认路由后清除原 marker。迟到的原恢复引用不会移除新会话。专属 Mihomo 在整个管理崩溃及恢复期间保持可达，最后随夹具清理退出。

原操作为 `380b5c87-bf26-45b2-ad4a-7d1024f3453a`，日志见 `.artifacts/mb04-mihomo-tun-live.log`，制品归档 SHA-256 为 `a95640fef4bc7bb30800a6868dab36eb22e5d90abdb2cdf101b2e2321b70968b`。本轮核对实际设备只删除一次，控制器重载次数尚未核对。夹具关闭自动路由，真实自动路由、DNS、系统代理、动态休眠及生产内存收益仍开放。共享 Mihomo、宿主网络和安装状态持续运行。

### MB-04 TUN 路由重试去重与设备清理证明

恢复发现磁盘配置已关闭 TUN 时，先查询真实控制器。控制器也确认关闭且原设备已不活动时，只继续管理路由核对，无需再写配置或发重载。控制器不可观测或设备仍活动时保留 marker 和恢复责任。首次关闭重载确认后同样等待设备清理证据。Linux 要求设备消失，Windows 接受设备消失或明确停止状态。

私有命名空间实机夹具记录生产控制器的 PUT 次数，将首次关闭和仅恢复管理路由的后续重试分开计数。自动路由、DNS、系统代理和动态休眠仍待联合验收。

Windows Release TUN 边界 32 项及关联专项通过。真实 Linux Mihomo 复测操作 `9c7336bf-4f89-4741-95e6-ff1d532e8156`，首次恢复 PUT 为一次，恢复原网关后的重试没有新增 PUT。无效控制器响应保持未知。日志见 `.artifacts/mb04-tun-reload-win-final.log` 与 `.artifacts/mb04-tun-reload-live-final.log`。

### MB-04 真实 Mihomo 自动路由恢复

同机 Linux 私有网络命名空间开启 Mihomo 自动路由。配置应用后实际内核路由查询对保留测试地址选择 TUN，对管理网关仍选择原 veth。故障前确认策略规则偏离基线，随后终止管理工作进程。删除私有默认路由后原恢复保留 marker，恢复原网关后同一操作完成。IPv4/IPv6 策略规则与初始快照相同，测试地址再次通过原默认网关。原关闭重载仍为一次。

原 operationId 为 `86899a7b-b6d0-425d-869e-5f40da657a40`，日志见 `.artifacts/mb04-auto-route-live.log`，归档 SHA-256 为 `ab4961e556d7654ffaec89eee67b67abe74c5db3b7440ea63d66b0f74e81aa19`。本轮验证真实内核选路和规则清理；没有向外网发送流量，DNS 和系统代理行为另行验收。

### MB-04 真实 Mihomo TUN DNS 劫持

既有 Linux 测试机在私有网络命名空间以实际 UDP 查询向保留地址 `198.51.100.53:53` 发出 DNS 请求。内核选路指向 TUN，Mihomo 对夹具记录 `mb04.test` 返回预设 A 记录 `203.0.113.42`。检查核对事务 ID、成功状态和应答地址。管理进程崩溃恢复后，策略规则回到基线，同一 DNS 目的地址回到原 veth 默认路由，原恢复仍只发送一次关闭重载。使用独立配置及无外网出口的命名空间，宿主解析器和共享 Mihomo 持续运行。

原 operationId 为 `505d8b43-bdb3-427a-bb52-65a2a527e7c7`，日志见 `.artifacts/mb04-tun-dns-live.log`，Linux Release 制品 SHA-256 为 `c39749a1db2f4069208db7f0836f281f62fe7b7ad41fc2b3fabf05f187907286`。本轮恢复后验证内核选路及规则清理，未向原上游发查询。系统代理和动态休眠仍待联合验收。

### MB-04 Linux 系统代理后端跨进程恢复

既有 Linux 测试机在私有挂载及网络命名空间使用生产 LinuxSystemProxyOperations、原事务和环境文件后端。独立挂载的 `/etc` 和 `/var/lib` 提供专属环境文件和恢复台账，独立 `/run` 隔离宿主桌面会话。测试在原进程确认写入代理键且恢复台账存在后强制终止它。在恢复前，用生产环境接口写入一个竞争代理值和无关键。恢复只撤销原操作仍拥有的值，保留竞争编辑；重复停用后环境文件字节一致，台账已清理。

Windows Release 事务专项及 Linux 实机均通过。实机前后核对宿主环境文件和恢复台账摘要一致。日志见 `.artifacts/mb04-system-proxy-live-final.log`，制品归档 SHA-256 为 `8d5413332ae0441ece4279fd092b7958571a1f66e209952a48eab0b33b4c5f03`。本轮使用真实文件后端，未连接共享 Helper 服务或桌面会话；guard 与动态休眠的联合验收仍开放。

### MB-04 Linux 系统代理 guard 与真实文件后端

生产 Core guard、MihomoSystemProxyReconciler、HostSystemProxyService 和 Linux Helper 文件后端在私有挂载中联合执行。测试未加载 Proxy 管理门面。启用 guard 后，已确认且未变更的环境键不再改写环境或所有权台账。仅对环境文件优化，桌面会话通知路径仍保持原行为。竞争编辑触发 guard 回到配置值；停止 guard 后，启动恢复只还原仍由原台账拥有的键，保留更新的竞争值。

系统代理文件后端和 Core guard 使用生产实现。Helper 传输由同进程专属适配器转接，正式独立 Helper、桌面会话和动态休眠联合验收仍开放。

Windows Release 事务专项通过，Linux 实机核对宿主环境文件和台账摘要前后一致。日志见 `.artifacts/mb04-guard-final-win.log` 与 `.artifacts/mb04-guard-final-live.log`。Linux 制品归档 SHA-256 为 `34e49e11f7f8c85c8319aafe857f92451811d174900a333ca5f3357fbd1c9664`。

### MB-04 独立 Linux Helper 的系统代理 IPC

既有 Linux 测试机在私有挂载和网络命名空间启动当前 Release 的真实 Linux Helper 服务入口。专属 root-owned 配置、独立 socket 及授权 UID 0 仅用于测试。生产 UnixSocketPrivilegedOperationTransport 经实际握手和帧传输执行原启动、崩溃恢复、周期 guard 与停止路径。记录七次成功的 LinuxSystemProxyApply 请求。原操作进程异常退出后恢复只撤销仍拥有的键，重复恢复无改写，竞争写入保留，稳定 guard 扫描不重写台账。

专属 Helper 收到 TERM 后正常退出且 socket 移除。宿主环境和恢复台账前后摘要一致。日志见 `.artifacts/mb04-guard-ipc-live.log`，归档 SHA-256 为 `0e57085a42b85a484ea2c6678c9ae514ed13e09c3e1abf69cb38e9711b87070e`。本轮没有测试正式非 root Server 身份、共享 systemd 服务重启、桌面会话或动态休眠。

### MB-04 系统代理 IPC 非 root 身份

同机私有命名空间复用现有 Server UID 999 及 GID 983。专属 Helper 仅授权该 UID 和恢复观察者 UID 0。未授权 UID 65534 使用同一 socket 组，Helper 在 peer 校验中拒绝，环境文件和台账无变化。已授权 Server UID 通过实际 socket 写入系统代理，Helper 回执操作 ID 为 `276c7916-634f-48a4-95ac-ea8a724cba42`。强制终止操作进程后，专属 root 观察者走同一 socket 完成原台账恢复和 guard 检查。

首轮拒绝事件正确，但客户端收到 socket 提前关闭映射的 HelperUnavailable。最终夹具以 Helper 拒绝日志和无副作用联合判定，仍校验非 root 成功事件。专属 Helper 正常退出，宿主环境和台账摘要一致。日志见 `.artifacts/mb04-guard-peer-final.log`，归档 SHA-256 为 `af8e8054e237c3946bff93a13691baaed06ecb3e8645be7c2e07b9652ae018e7`。正式 systemd 服务、非 root 常驻 guard 与桌面会话未纳入本轮。

### MB-04 非 root Core guard 与恢复

在同一隔离 Linux Helper 夹具中，周期 guard、生产对账和停用恢复改由现有 Server UID 999 的独立非 root 进程经真实 IPC 执行。root 观察者仅在 guard 阶段负责核对文件和注入竞争编辑。首轮写入后，稳定扫描保持环境值和台账；竞争写入被 guard 纠正。停止 guard 后写入新的竞争值，非 root 启动恢复撤销受管值并保留该编辑。夹具要求 Helper 日志恰有五次成功的 UID 999 操作，包括先前的崩溃组初始写入，以及 guard 的三轮和停用恢复。崩溃组本身的首轮台账恢复仍由 root 测试观察者执行。

共享 systemd 服务、桌面会话和 Proxy 管理模块动态休眠继续开放。

Linux Release 验收通过，Helper 日志确认五次 UID 999 成功请求并拒绝未授权 UID。专属 Helper 正常退出，宿主环境和台账摘要不变。日志见 `.artifacts/mb04-guard-nonroot-live.log`，归档 SHA-256 为 `7e47efcf9290814db088268c43b1ffd501b80792eaee596d78cbf2c622565f58`。

## 52. MB-05 真实 ACME CA 签发与取消

既有 Linux 测试机在无外网接口的私有网络命名空间运行 [Pebble 2.10.1 与挑战 DNS 官方制品](https://github.com/letsencrypt/pebble/releases/tag/v2.10.1)。两份下载归档通过官方发布 SHA-256 校验。临时 HTTPS 证书仅由本次进程信任，签发根仅用于链校验。保留真实 HTTP-01 验证，未开启跳过挑战验证。生产 Anvil 适配器、HTTP-01 listener、证书存储和操作台账参与执行。

签发操作 `2871c43c-2d29-4543-bc3e-bada02becc84` 保存活动证书，部署链通过专属 CA 根与主机名校验。第二个订单的真实 HTTP-01 GET 到达后，夹具暂停响应。取消时工作租约和 Running 回执保留，直到实际响应任务结束才记录 Cancelled。复开台账后，同键返回原操作 `95543702-fb76-4d31-a964-cdd867c1ed6e` 的回执，未签发取消订单。Pebble 确认总计两个订单。共享服务和宿主信任库持续原状。

Linux x64 Release 日志见 `.artifacts/mb05-acme-verified.log`，归档 SHA-256 为 `0a07730143f476e9e5de19cdec8d02abf046ebb166b602184996aa93026e3aab`。首轮两份二进制缺执行位，后续仅在专属解包目录修正后通过。整进程崩溃后的 CA 未知结果、实际 Nginx 绑定及网站发布联合矩阵仍开放。

### MB-05 真实 CA 挑战中的操作进程崩溃

同一私有网络中的 Pebble 发起第三个订单的真实 HTTP-01 请求。专属操作进程在等待响应时保留 Running 回执。父进程读取原持久台账确认该操作是唯一活动项，再强制终止工作进程并确认异常退出。HTTP-01 端口随后能重新绑定。新工作 owner 读取原台账，将原 operationId 持久化为 Failed / interrupted。相同幂等键返回同一回执，CA 日志要求恰有三个订单，恢复不提交新订单。

本项故障发生在真实 HTTP-01 验证期间，仍需验证 CA 已签发但客户端丢失最终响应，以及生产 Nginx 绑定和网站发布联合恢复。

最终 Linux x64 Release 回归通过。崩溃组原操作为 `65cf6873-5791-4866-bf8c-acfb1deaea3b`，原证书目录只有正常签发组证书。Pebble 最终记录该崩溃订单验证失败，未收到第四个订单。日志见 `.artifacts/mb05-acme-crash-final.log`，制品归档 SHA-256 为 `4994febeae9afca1a58c114466289a67a7a55a3ea13e80439c96459da6a9af3e`。

### CA 已签发后的响应丢失

在同一隔离 Linux 环境中，第四个订单通过真实 HTTP-01。Pebble 签发证书后，夹具在真实证书下载响应交给 Anvil 前暂停，验证返回的 PEM 与测试主机名匹配。工作进程仍持有 Running 回执。终止该进程后，挑战端口可重新绑定。新进程将原操作 `034517e3-15aa-48df-bad2-381590d26bdb` 持久化为 Failed / interrupted，同键查询返回原回执。证书存储只包含正常签发组的活动证书，Pebble 日志恰有四个订单。

Linux x64 Release 实机日志为 `.artifacts/mb05-acme-response-live.log`，归档 SHA-256 为 `c887deec74b97eebdb3ca21f3d13ca051e9d3046295dc5b4d481fbefd8e02db4`。外部 CA 已签发，本地未激活证书。该路径验证中断恢复与防重放，未验证从原 CA 订单补取并部署证书。

## 53. MB-05 Nginx 重载中断后的 TLS 连续性

既有 Linux 测试机运行独立的 Nginx master、回环端口、配置和临时证书。客户端校验对端证书指纹和回环地址。生产 WebServer 重载入口提交操作后，隔离 transport 对专属 master 执行真实 reload，等 HTTPS 返回新内容且原回执仍为 Running 再终止管理工作进程。原 master 继续提供 HTTPS，新 owner 持久化原操作 `dbe8c470-e6c2-4050-84de-4ccf2490603c` 为 Failed / interrupted。两条重载入口使用相同幂等键均返回原回执，master 日志仅有一次重载。仅清理专属 master 和临时目录。

Windows Release 构建和 Linux x64 Release 实机验收通过。实机日志为 `.artifacts/mb05-nginx-tls-live.log`，制品归档 SHA-256 为 `d6530f4e0fdaca000a963ad8dfa62acc9259a13b485b13e7706e061e6437f01b`。本轮的 TLS 配置由夹具提供，生产站点绑定和网站发布协调器的实机恢复仍待验收。

### 生产站点绑定

既有 Linux 测试机在私有网络和挂载命名空间运行独立 Nginx，使用私有 `/etc/nginx` 和 443 端口。生产 WebServerManager 与 Nginx provider 通过站点保存入口验证本地证书文件、渲染配置并持久化站点元数据。测试 transport 限定在私有配置树，执行真实配置检查和重载。HTTPS 客户端校验证书指纹并读取生产站点配置指定的静态内容，原 master 日志确认一次 reload。

Windows Release 构建和 Linux x64 Release 实机验收通过。日志为 `.artifacts/mb05-nginx-site-live.log`，归档 SHA-256 为 `f914d7871113188fbe605eed44ca1c77fe503425a19152f989d1a4a0f42a4f78`。Nginx 报告现有 listen http2 写法弃用警告，配置检查通过。本轮覆盖本地证书文件绑定，证书仓库 ID 绑定及网站发布协调器崩溃恢复仍开放。

### 证书仓库 ID 绑定

Linux x64 Release 实机使用生产 FileCertificateStore 保存第二张自签证书。站点更新按原更新时间提交，持久元数据记录证书 ID，生成配置引用证书仓库的稳定 PEM 路径。客户端以第二张证书的指纹完成新 TLS 连接并读回站点内容。站点更新增加一次重载。

PASS NGINX LIVE SITE TLS: local and managed certificate bindings applied, certificate=00ffc68b-f14d-4959-bbad-6ce61d6d8dd3; reloads=2

日志为 .artifacts/mb05-nginx-managed-live.log，归档 SHA-256 为 c38990ce7d3e5f6f0be35307e66d3b230ccd599c3353499b81d64befafcf9b3d。暂存配置检查期间出现重复 server_name 警告，最终配置检查和实际 TLS 均通过。网站发布协调器跨进程恢复仍待验收。

### 网站发布协调器进程崩溃

Linux x64 Release 在私有网络及挂载命名空间运行生产发布协调器、证书仓库、站点 provider 和独立 Nginx。应用运行状态由 Docker 观察夹具提供，上游为真实回环 HTTP 服务。发布进入 Verifying 后，工作租约仍在，原台账和应用关联已落盘。父进程使用 SNI 及指定证书指纹验证 Nginx 已代理实际流量，再强制终止发布工作进程。

启动恢复将原操作 61f0e452-39ff-4e1c-b144-bfcc5a1a62b6 持久化为 Interrupted。同键返回原操作和问题码。原 master 在恢复后继续通过 HTTPS 代理同一上游，应用关联保留，发布只触发一次重载。日志为 .artifacts/mb05-nginx-publication-live-san.log，制品 SHA-256 为 f9c77392701e801a355058bf3e8a4c9937cc22d9e6b22f7e7b4a1823b5cf7c78。初次测试的 PEM 读取及 TLS 探测失败在夹具中修正，最终实机通过。发布协调器的证书查询 DTO 和验证阻断使用夹具，独立 CA 签发见 §52。

### Nginx 已重载、发布元数据未提交

在同一隔离 Linux 环境，真实 Nginx reload 成功后暂停 transport 响应。实际 HTTPS 已代理回环上游，站点元数据中尚无新站点，应用关联尚未写入，原发布回执仍为 Configuring 且工作租约在位。终止发布进程后，启动恢复按原 operationId 11415427-0caf-4545-9018-2a201877ab83 持久化 Interrupted 和 website.site_application_unverified。同键重试取回相同回执。站点元数据内容和应用关联保持中断时的状态，原 master 继续代理 HTTPS，发布只触发一次重载。

Linux x64 Release 实机日志为 .artifacts/mb05-nginx-configuring-live.log，制品 SHA-256 为 2d1f70095edd08bf32ac0db5cbd042d7358da80a2920e00997a9577b53463503。该路径保留未知结果供复核。

## 54. MB-05 独立 Engine 重启与持久卷

既有 Linux 测试机的私有挂载和网络命名空间运行 Docker Engine 29.8.2。私有挂载隐藏宿主 containerd socket，日志确认 daemon 连接专属 exec-root 下的 containerd。Docker socket、客户端配置和 vfs 数据目录均专属。镜像从共享 Engine 已缓存的 Alpine 标签只读导出，并导入专属 Engine。首轮日志发现 daemon 默认尝试使用共享 containerd，独立验收改为隐藏宿主 socket。离线导入后使用导出的标签处理两种镜像存储返回的 ID 差异。

专属 daemon 正常停止后通过原数据目录重启。同一容器 27703da4ed8b0466ea8952768a22b1ace3ea9673df0ef256d8ceace762925a48 按 restart policy 重新运行，启动时间更新，命名卷中的原内容仍可读取。容器与卷随后清理。证据为 .artifacts/mb05-private-engine-workload-verified.log。此轮验证了外部运行设施，原操作回执与真实 daemon 中断的联合验收仍开放。

### 执行中 Engine 与操作进程中断

独立 Engine 上真实停止请求进入容器 TERM handler 后，终止专属 daemon 和管理进程。新进程在原 ledger 读取 Running，daemon 重启且 witness 恢复运行后，通过生产协调器观察原项目，按原 operationId 1f3aa469-0012-4b43-86e6-f51e2ef68bf1 持久化 Interrupted。同键重试返回原回执。停止日志在恢复前后均为两条，容器持续运行。首轮按一次停止信号断言失败，最终以 daemon 中断完成后的实际计数为恢复基线。专属项目容器和卷已清理。

Linux x64 Release 日志为 .artifacts/mb05-daemon-operation-final.log，归档 SHA-256 为 e044cb4bcfc2563a67ce2eac59bc413a8a2c4eb253b4789326286a074e973746。此次联合验收使用 daemon 正常终止信号和管理进程强制终止，完整 Server HTTP 传输仍待验证。

### dockerd 强制终止

同一实机增加私有 PID 命名空间和 cgroupfs。真实停止请求进入容器后，专属 dockerd 收到 SIGKILL 并以 137 退出，管理工作进程也被终止。新 daemon 和协调器从原数据目录及台账恢复，原操作 f053458d-6456-421a-8deb-978119d2894e 持久化 Interrupted。同键重试返回原回执，容器持续运行，恢复前后的停止日志均为两条。测试移除专属容器和卷，卸载残留数据挂载并删除临时目录。

Linux x64 Release 实机日志为 .artifacts/mb05-daemon-kill-final.log，归档 SHA-256 为 273f8a3dde8229c9a1884fa5965032b0db1b07fc2d26e5760c889eba714f9d87。首次私有 PID 运行遇到 systemd cgroup 的 PID 映射问题。切换 cgroupfs 后通过业务断言，修复残留挂载清理后整套通过，早期遗留目录已清除。完整 Server HTTP 响应排空仍开放。

### Docker HTTP 客户端断开

专属 Engine 与回环 Kestrel 使用生产 Docker 路由、协调器和 CLI。夹具身份及响应头屏障用于在 HTTP 202 写出前触发真实客户端断开。容器已收到 TERM，原回执为 Running，工作租约仍在。Kestrel 观察到 RequestAborted 后，真实停止操作继续执行，终态落盘后释放工作租约。原操作 e563f5a7-2316-4269-84e5-6bf8741c5b68 持久化为 Succeeded，HTTP 同键重试返回同一回执。相对恢复后的停止计数仅增加一次。

Linux x64 Release 日志为 .artifacts/mb05-docker-http-live.log，归档 SHA-256 为 dfe0b1fa60f8a4d3db103639cb5a784c5f490ed17501dd9e9db10000f76f3233。本轮由 Kestrel 测试宿主承载生产路由，使用观测租约和夹具身份。完整 Server 启动、认证和模块响应排空仍开放。

HTTP 断开使用生产 Docker resident 工作租约复测通过。客户端断开时 ModuleResidencyCoordinator 仍报告活动工作，终态事件发生在租约保护下，持久回执落盘后排空。原操作 f7503653-b5ce-4cbc-b07e-da09b4874237 同键重试成功，停止次数未增加。Linux x64 Release 日志为 .artifacts/mb05-docker-http-lease-live.log，归档 SHA-256 为 d271d7731579b3c16fea1c546b8c4b14306a932e21f2860d28d8a50df7e1d87e。Kestrel 测试宿主仍提供身份和响应屏障，尚未验收完整 Server 启动及认证链。

### MB-04 Proxy Server 授权到期回归

Windows Release 的独立 Proxy Server 测试已接入现行持久调度索引。使用原台账中的待处理发布触发生产 30 秒调度器，验证失败释放作用域、下一轮新建执行器、取消时等待异步清理，以及 HTTP 队列原回执。日志为 .artifacts/mb04-proxy-server-release-final.log。此轮为本地回归，Proxy 安全责任交接和动态休眠仍开放。

### MB-04 到期索引从原台账重建

到期扫描在原状态 gate 内从权威密文重建失配、损坏或缺失的索引，后续匹配扫描跳过台账解密。索引不可写时继续按原台账判断，未引入新的发布执行者。Windows Release Proxy Core 安全与生命周期作用域专项通过，日志为 .artifacts/mb04-index-repair-win.log。Linux 实机复测、安全责任交接和动态休眠仍开放。

Linux x64 Release 复测通过。Proxy Core 安全专项确认失配索引重建后不再解密原台账。同一制品的专属 sing-box 实机验收确认到期前流量可达、切换后旧连接关闭、过期凭据拒绝、保留凭据可重新连接，丢失发布响应后只执行一次 apply。两项分别覆盖索引恢复和实际网络切换。日志为 .artifacts/mb04-index-repair-linux.log，归档 SHA-256 为 18ea160a65fb22fd1de1aa4575775ac8f07c68619801b743f808053c8829ed97。实机仍使用专属运行时切换夹具，跨进程发布恢复和调度责任交接仍开放。

### MB-04 sing-box 到期发布跨进程恢复

Linux x64 Release 隔离夹具复用持久保护密钥。发布进程通过生产执行器落盘候选配置并停在 Helper 响应前。父进程用原候选配置切换真实 sing-box，随后强制终止发布进程。原 PendingPublication 保留。实际网络观察到旧流关闭、过期凭据拒绝和保留凭据重连。移除派生索引后，恢复进程重新打开原加密台账，重建索引并按实际应用配置摘要确认原候选，apply 共一次。

日志为 .artifacts/mb04-expiry-crash-live.log，归档 SHA-256 为 2b2143ce124c35224c72502ad569334be77c22bb7bd0371ded73adec28f18e9d。运行时切换及状态响应由专属 transport 夹具承载。此处调度依据原 PendingPublication，授权到期 worker 没有用户操作回执。生产 Helper 事务联合验收及动态休眠仍开放。

### MB-04 Helper 发布后运行状态核验

生产 sing-box Helper 在提交新配置前核对运行状态与发布前一致。原先运行的服务若在启动返回后已退出，事务进入原补偿路径。Windows Release 受管服务专项验证旧配置摘要、旧状态版本和旧运行状态恢复。实机 Helper 联合验收仍开放。

Linux x64 Release 实机复测通过。生产 SingBoxManagedRuntime 发布事务和原生版本、配置检查使用已安装 sing-box 的专属副本。在私有网络中候选进程实际监听后被终止，Helper 观察到退出后恢复旧配置、旧状态版本和原运行状态。服务控制由专属进程夹具承载，未连接共享 systemd。REALITY 探针仅验证本地监听，私有网络无外部握手路由。日志为 .artifacts/mb04-helper-live-final.log，归档 SHA-256 为 38db85bba4743acfe71a1ced176ed681e8f3be2c0171bbd44cd08fa7a76209bd。

### MB-04 Helper 在停止失败后补偿

停止失败时发布候选仍可能占用暂存文件。补偿使用独立暂存文件，收尾不继承已取消的请求令牌。返回已补偿前核对原配置摘要、注册程序、启动策略和运行状态。Linux x64 Release 专属 sing-box 通过 13 项，包含夹具注入停止异常后实际重启旧配置，以及候选进程退出后恢复。日志为 .artifacts/mb04-helper-stop-live.log，归档 SHA-256 为 3254d1ab12ea6fff32b3eafb4a0b64679d196e7dc30fc0950ca924cb2654cefd。生产 systemd 故障注入及 Helper IPC 联合验收仍开放。

### MB-04 sing-box 发布事务的 Unix socket 验收

Linux x64 Release 在私有网络中通过生产 Unix socket 服务端、客户端及 peer UID 校验调用生产 SingBoxManagedRuntime。专属 socket 仅允许 UID 0，实际 sing-box 发布、候选退出和停止异常后的补偿共通过 13 项。日志为 .artifacts/mb04-helper-ipc-live.log，归档 SHA-256 为 ff65e52efc7c8498c8c92842d4d5701adc4e06fb96366060cf68a4da4f513934。两端同属测试进程，派发仅接入 sing-box 发布事务，服务控制为专属进程夹具。独立 Helper 进程、生产总派发器和 systemd 仍待联合验收。

Linux x64 Release 同一套 13 项已通过独立客户端进程复测。每次调用由新进程经专属 Unix socket 进入生产 transport 和 peer 校验。日志为 .artifacts/mb04-helper-peer-live.log，制品 SHA-256 为 7974f603d96672ade098f0dcbc834f0457bfc60500a1fb2ee3ce5f52880a39e0。Helper 服务端由父测试进程承载。本次验证进程隔离下的正常请求及服务故障补偿，客户端在途退出后按原台账恢复仍开放。

Linux x64 Release 增加 Helper 已提交、socket 响应尚未发送时的客户端强制终止，16 项通过。新进程通过原 socket 查询确认候选摘要和运行状态。原 Helper 台账版本只增一次，sing-box 只新增一次启动。日志为 .artifacts/mb04-helper-loss-live.log，归档 SHA-256 为 662af074f9ee23b313d1daf5801a5432d72fead0a345f8e4015ce904948a83e4。该验收覆盖 Helper 持久状态，Server 加密发布台账与 IPC 中断的联合恢复仍开放。

Linux x64 Release 联合验收通过 17 项。独立客户端运行生产到期发布执行器，将候选持久化到 Server 原加密台账，再经专属 Unix socket 提交到 Helper。确认 Helper 摘要与原待处理候选一致后，在响应前终止客户端。恢复重新打开 Server 台账，通过同一 Helper 观察确认原候选并清除待处理记录。Helper 版本及实际 sing-box 启动次数各增加一次。日志为 .artifacts/mb04-helper-ledger-live.log，归档 SHA-256 为 834f9c78bf26b8209f2c267cd8d664591df3f51f6a6fcf948826045e8280706a。服务控制为专属进程夹具，生产 systemd、动态休眠与内存验收仍开放。

Linux x64 Release 联合复测改由新进程执行生产到期调度器的一次扫描。原发布客户端退出后删除派生索引，新进程从原加密台账重建索引，在临时作用域内通过原 Helper socket 确认候选。同一 Helper 版本和 sing-box 启动计数只增一次。17 项通过，日志为 .artifacts/mb04-helper-scheduler-live.log，归档 SHA-256 为 a459ca5fee60a0f3651bd661b578799fec6986a42d5d1e2bc850277b5d48ac4b。此项验证调度恢复入口，不涵盖常驻 worker 退役或动态休眠。

Linux Release 验证到期索引路径为符号链接时，调度读取原台账，保留待处理发布且不改写链接目标。原台账及暂存写入使用同一禁止链接路径检查。专属 sing-box 跨进程回归同批通过，apply 仍为一次。日志为 .artifacts/mb04-index-links-linux.log，归档 SHA-256 为 86547d3d5220fcac631834e534894365a71238e01588abfd202d8ba2749317c6。

## MB-03 独立 Engine 的受限 builder 复核

私有 PID、网络及挂载命名空间内，独立 Engine 导入此前核验的 rootless BuildKit，专属容器使用内部网络、1 GiB、1 CPU、128 PID 限制。RootlessKit 创建子进程仍收到 permission denied，Buildx 未建立连接，探测退出 124。容器和网络已删除，宿主 AppArmor 未修改。日志为 .artifacts/mb03-private-builder.log。需允许专属 RootlessKit 创建用户命名空间的隔离 Linux 环境，以及 Git CLI 和 builder 共同可达且可信的 HTTPS 443 测试仓库。

构建准入现在要求 Buildx inspect 精确报告唯一 remote driver 和唯一指定容器端点，拒绝前缀相似或多节点配置。Linux Release 后台工作回归通过，含两类错误端点、原回执、取消和清理。日志为 .artifacts/mb03-builder-gate-live.log，归档 SHA-256 为 cf7f35e4666c61abd6e669d79b4f17059b20e1339962ce400ca6d9b36d45267f。真实 BuildKit 执行中取消及 ALC 联合验收仍受前述条件阻塞。

当前既有测试机只读复核确认 AppArmor 非特权 user namespace 限制仍为 1，无 RootlessKit 专用 profile、KVM 设备及现成 QEMU。实机 BuildKit 阻塞继续按前述条件记录。日志为 .artifacts/mb03-isolation-readiness.log。

### MB-05 Docker 202 响应排空

生产 Docker stack 受理回执在实际执行 HTTP 202 结果时取得独立 resident 租约，直到序列化与写出任务返回才释放。Linux x64 Release 专属 Engine 实测客户端先断开，原停止任务落盘并释放业务责任时，响应屏障仍持有独立租约。释放屏障后响应退出，租约归还。同键重试返回原操作 7b2f20e6-a12e-407c-b6b0-e9bd1d6971e6，未重放停止。日志为 .artifacts/mb05-docker-response-live.log，归档 SHA-256 为 8996424ff81e7112007f0997ad5f96cdebcd2762144c1bb511bfbe308d39b0f3。生产路由和租约运行在 Kestrel 夹具中，完整 Server 认证启动仍开放。

MB-05 将 Docker 已实机验证的响应租约机制复用于证书、WebServer 操作和网站发布 HTTP 202。响应执行独立持有对应 resident 模块租约，直至执行退出。Windows Release 的证书 HTTP 绑定与回执、WebServer 和模块边界专项通过。证书绑定测试确认成功响应取得租约。新路由的实机断线和排空仍待验收。

Linux x64 Release 同制品通过证书 HTTP 绑定、回执、WebServer、模块边界回归，以及隔离 Nginx Configuring 阶段发布恢复。原发布 3c573041-4d2f-436b-98a1-4766d8cd1720 恢复为未知应用结果，发布重载一次。首轮因 Windows 打包未带 Linux apphost 执行位而中止，专属解包目录修正后通过。日志为 .artifacts/mb05-receipt-linux-final.log，归档 SHA-256 为 45f9d3b570a6c265a53b1d21f05fe10bc3aec29939773bfbf51f472a72f544df。此批次的 Nginx 发布故障走协调器入口，新增路由的实机断线联合排空仍开放。

MB-05 证书生产路由在 Linux Kestrel 夹具上通过真实客户端断线测试。原证书绑定成功后，同键回执在响应头屏障处持有租约；客户端断线后租约仍在，实际响应退出后归还。再次提交原键得到原 Succeeded operationId。日志为 .artifacts/mb05-cert-drain-linux.log，归档 SHA-256 为 93c779be34d229c4f9b1a122bdd7f44669089ec06a5eb2100876932844cd081f。此项使用测试身份、响应屏障和观测租约，真实 CA 签发中断证据见 §52。

Linux x64 Release 隔离 Nginx 发布恢复已走生产网站发布 HTTP 同键入口。原操作 663e2f69-beaa-4ea1-88e9-9178bf2d5421 在 Configuring 中断。恢复后的同键响应在写出前遇到真实客户端断线，实际响应退出前保留租约。后续重试返回原 Interrupted 回执及 website.site_application_unverified，HTTPS 继续代理，发布只重载一次。日志为 .artifacts/mb05-website-drain-live.log，归档 SHA-256 为 20a7a124f5311bc7b731d000d5516e87d3f2448eab6c0158ca11427b6740c6a7。故障由原协调器工作进程触发，Kestrel 夹具提供身份、响应屏障和租约观测。

Linux x64 Release 另通过 Verifying 阶段的 HTTP 回执断线实机复测。原操作 d4a11877-ab18-40c6-a980-6785a37820e8 已持久化应用关联后中断，同键 HTTP 响应断线后排空。重试返回原 Interrupted / website.recheck_required，应用关联、HTTPS 和发布重载计数均符合预期，发布重载一次。日志为 .artifacts/mb05-website-verifying-drain-live.log，归档 SHA-256 为 b90a472b452bc97354656380639619cd762a124fed9f54ef77703e2d33dc4ce5。

Linux x64 Release 通过独立 Nginx reload 的生产 HTTP 同键回执断线验收。原操作 4b43a6bc-375d-4257-8765-8b16899e8ee6 在真实 reload 后中断，原实例已从管理发现移除。客户端断开后，租约保持到回执响应退出，重试返回原 Interrupted 回执，原 Nginx 继续提供 HTTPS，日志只有一次 reload。同一制品的 Verifying 发布回归也通过。日志为 .artifacts/mb05-nginx-reload-drain-live.log 和 .artifacts/mb05-nginx-shared-replay-live.log。HTTP 身份、响应屏障和租约观测由 Kestrel 夹具提供。

### MB-05 完整 Server 认证回执

Linux x64 Release 在私有 Engine 和回环网络中启动完整 System Mode Server，先确认匿名 Docker 请求被拒绝，再通过真实 Unix socket peer 校验及固定 PAM 认证操作登录。专属 Helper 只派发认证，其他操作拒绝，避免触及共享服务。真实 Docker 停止完成后重启 Server，再次登录并同键取回原 Succeeded 操作 fb400c0c-210b-42ed-84b9-b7d2ba085362。停止日志仅增加一次。日志为 .artifacts/mb05-auth-only-helper-live.log，归档 SHA-256 为 d779ce3adafcd0ee22836d2df45668afcd5ebcbfaf744c990e0adf47deac1c06。先前 in-process PAM 的 login 和 sshd 路径返回 401，生产 Helper 固定 PAM 路径通过。本次完整认证回执与响应屏障实测为独立路径。

Linux 完整认证 Server 复测中，独立 TCP 连接发送真实停止请求，另一连接按原幂等键确认受理后，客户端关闭原连接且不读取响应。原操作 751cc66c-faad-4626-8c6e-e0f974db740d 完成并落盘，Server 重启和重新认证后返回原 Succeeded 回执。专属容器停止日志仅增加一次。日志为 .artifacts/mb05-auth-response-loss-live.log，归档 SHA-256 为 c518fb25902d0060a3a196c9d694da8ee1e97c8656d785a15fa9b7a7253bdec4。本项验证客户端丢失响应，不提供完整 Server 写出任务阻塞期间的租约观测。

Linux 完整认证 Server 的运行中崩溃路径通过。客户端未读取受理响应，通过原幂等键取到 operationId，确认专属容器的 TERM handler 已执行且原回执仍为 Running 后强制终止 Server。重启并重新认证后，同键返回原操作 39baf3ad-13d6-4333-9649-0310e61778b4 的 Interrupted 回执，停止日志只增加一次。日志为 .artifacts/mb05-auth-server-crash-live.log，归档 SHA-256 为 40c4adf9834c82b3d30cb6e3f50e4530b2ec5ac5b53430b23fa619e075e88416。

完整 Server 去重续核在原 Engine 停止完成后显式重新启动同一专属容器，再启动 Server。登录后同键提交及按 ID 查询均返回原 Interrupted 操作 495e7459-98db-4e69-9d34-cf534fb9cace。容器仍在运行，停止日志只增加一次。Linux 实机日志为 .artifacts/mb05-auth-restarted-witness-live.log，归档 SHA-256 为 930f32b0ab2a0ebeae16a0a272a9b6b7cb45863693a3770e9d42314d4aaa7572。

### MB-04 Mihomo 持久发布调度索引

Core 发布恢复扫描使用加密索引，仅记录权威密文 SHA-256 和原待处理 operationId。匹配时按流核对密文，跳过完整发布图的解密和反序列化。失配、损坏、缺失或不可写时从同一权威文件读取原责任，能写时重建索引。原 gate、发布和恢复执行者及 30 秒扫描周期继续承担职责。Windows Release Mihomo 发布事务 109 项通过，日志为 .artifacts/mb04-mihomo-schedule-win.log。Linux 实机联合复测、动态休眠及生产内存测量仍开放。

Linux x64 Release 的 Mihomo 发布事务专项通过 110 项，含损坏索引重建、不可写时保留原待处理身份，以及未知结果不自动重放。同一制品的隔离 sing-box Helper IPC 与原加密台账联合恢复通过 17 项。首轮外部执行脚本误用 Windows 的 109 项计数，修正后整批通过。日志为 .artifacts/mb04-mihomo-schedule-linux-final.log，归档 SHA-256 为 9a249eeb8322601e1ba964e6bf66a1cdf748f2e646d25946aa44f5865a5f018c。Mihomo 索引专项使用受控事务夹具，生产 systemd、责任交接与动态休眠仍待验收。

Mihomo 原发布台账打开时只将文件或父目录确实缺失视为空；目录占用、权限或其他读取故障保留未知责任。Linux Release 事务专项 111 项通过，验证目录占用时不重放原发布，恢复原文件后继续原操作。隔离 sing-box 17 项同批通过。日志为 .artifacts/mb04-mihomo-authority-live.log，归档 SHA-256 为 7d6c047770d274e1a0ba6322f719aca8f362261780e95d80325dfc2ca8acbb95。Mihomo 权威异常通过受控事务夹具验收，真实网络联合故障仍开放。

Linux x64 Release 在私有网络使用真实 Mihomo TUN、自动路由和 DNS 劫持，验证发布与 TUN 恢复共用的写入门禁。TUN 工作进程退出后，夹具建立待处理但确认未执行的发布 8f0dfd98-7736-4178-9e76-38438a7007cf 并删除派生索引。重建索引得到原发布 ID，原 TUN 恢复 27014fc4-38de-4b46-a797-7264396afc57 保留 marker、设备和原配置，竞争 reload 为零。夹具按未执行证明关闭待处理发布后，原 TUN 恢复仅执行一次真实禁用重载。日志为 .artifacts/mb04-mihomo-tun-gate-live.log，归档 SHA-256 为 1fcb943d66196812d3041de4984b8ca9d3f08798cf328a071d635179750a5b27。此项覆盖真实 TUN 与持久发布门禁竞争，生产发布事务实机恢复和动态休眠仍开放。

Mihomo TUN 重载后核对控制器实际 TUN 标志，连接恢复本身不足以清除原 marker。未知传输结果保留候选及恢复责任。Linux 实机在真实禁用 PUT 成功后丢弃响应，控制器重新连接并确认禁用；管理路由未核实时保留原 marker，随后恢复路由并清除 marker，全程只发一次禁用 PUT。原操作 f3bad259-e1f3-4e1a-9a37-a2fd7a79bbed。日志为 .artifacts/mb04-tun-loss-live.log，归档 SHA-256 为 2ba1951a27246645b4a3aadd31a103dbbd95e5ff5c707cc87c2a62a2e452d43a。Windows Release 专项也验证后续观察确认已禁用时不再重载。

Linux 实机补齐禁用请求未到达控制器的分支。传输夹具在转发前中断请求，真实 Mihomo 仍报告 TUN 开启，原恢复标记和设备保留，原生 reload 为零。同一操作 91aa1220-2def-42d9-9120-d58affea83ff 随后在确认仍需禁用时执行一次真实 reload。成功响应再次被夹具丢弃，原路由恢复后清除标记，未追加 reload。日志为 .artifacts/mb04-tun-unapplied-live.log，归档 SHA-256 为 bb4b55b2c4664a224be05ab46f104860aa2c3549acbf077633dfc8d8a6843d3f。

MB-04 系统代理 guard 完成启动对账后，按轻量设置读取判断 guard 是否启用。空闲扫描不再构造发布验证与平台写入执行图。启动恢复失败仍重试，启用时由原 reconciler 在发布锁内重新读取设置并执行。Linux Release 在管理模块启用与禁用两种状态通过空闲构造断言、启动恢复及原安全回归，同批隔离 sing-box 实测通过。日志为 .artifacts/mb04-idle-guard-linux.log，归档 SHA-256 为 806e9994a8ffdcaccb9b47cbd5d2c49e4427be6b1e6a8b0afe08601ea0e5c800。该切片缩小空闲执行图，原 guard 保持常驻。

Linux 私有网络中，真实 Mihomo 禁用已应用但管理路由仍不可验证时，原 TUN 标记继续保留。恢复路由后启动生产 Core ProxyRecoveryHostedService，自动捕获原标记并完成收尾。原操作 6a38b6b8-1c94-470d-bd74-b544e0610bc5 的禁用 reload 仍为一次，真实 Mihomo 继续运行。日志为 .artifacts/mb04-tun-core-live.log，归档 SHA-256 为 7000596a6e2ab76dc76ec6348afeb0ca37f074ba7203f7875ac488fda95039ec。实机覆盖 Core 启动 worker，完整管理模块休眠交接仍开放。

同一 Core TUN worker 的实机重试通过。worker 在管理默认路由缺失时报告原恢复未完成，继续运行且保留原标记。恢复默认路由后，同一 worker 按生产周期重新验证并关闭原操作 bb3f2bfe-c464-4821-a14d-9d91aafba279。真实禁用重载仍为一次。日志为 .artifacts/mb04-tun-core-retry-live.log，归档 SHA-256 为 c9f6778438045c909ed24d0f66966cc238fe4fe24c9582aada98947a2381b7cf。

系统代理 guard 空闲优化后，Linux Release 专属 Helper IPC 实机回归通过。非 root Server UID 授权与未列入 UID 拒绝、原 journal 跨进程恢复、竞争修改保留、启用 guard 的稳定无写入周期均通过。日志为 .artifacts/mb04-guard-idle-ipc-live.log，归档 SHA-256 为 201e6c81490b2925e6e1a3b590aa670232368dc19eb8ba71fa57659968fbd52c。禁用 guard 的空闲构造证据来自 Core 专项。共享服务和宿主文件持续原状。

MB-04 Proxy 订阅、生命周期、入站与网关队列的 HTTP 202 响应接入原驻留协调器，响应执行持有独立租约。Windows Release Proxy Server 回归验证原提交和同键响应各取得租约，业务只构造一次执行图并完成异步清理。日志为 .artifacts/mb04-proxy-receipt-win.log。Linux 真实断线排空及完整 Proxy 休眠仍开放。

Linux x64 Release 通过 Proxy Server 入站队列生产路由的 TCP 断线验收。同键响应在响应头屏障等待时客户端断开，响应租约持续到实际退出。后续重试返回原 operationId f74d189c-acf2-4701-8ef4-9fd4a2a2c750，业务执行图只构造一次，原队列停机仍等待异步清理。日志为 .artifacts/mb04-proxy-drain-live.log，归档 SHA-256 为 1c976cf0cd3de2ad2be736132a90514c9565cc5c17212327835a4fca163924a8。租约及响应屏障由 Kestrel 夹具观测，动态休眠仍开放。

Core TUN worker 已捕获原恢复标记后，如果后续读取遇到不安全路径，继续保留原代次并按生产周期重试。Linux 隔离实测在 worker 等待路由时临时将标记路径换成符号链接，确认 worker 拒绝读取且继续运行。恢复原文件和管理路由后，同一 worker 关闭原操作 57e96be8-49a4-494e-8d8b-c0e5375262e5，真实禁用重载仍为一次。日志为 .artifacts/mb04-tun-marker-retry-live.log，归档 SHA-256 为 0a24dc0cb2bf30b7a586962a2b2bffed6f5366b978ccfce367777f1f6e9cb2cd。首次启动即无法确认代次仍需人工核验。

Proxy 停用检查先核对原驻留协调器的活动工作租约，覆盖业务清理及独立响应寿命。租约存在时拒绝停用；后续检查继续按原持久台账和 Core 安全责任判断。Linux Release 使用生产协调器验证响应租约持有与归还，同批隔离 sing-box 网络恢复通过。日志为 .artifacts/mb04-proxy-deactivation-live.log，归档 SHA-256 为 26910a3c15986c20acf467630cc17e0d170ba17910db5390dfa83ec709dfedb6。此检查是当前责任快照，完整动态休眠仍开放。

Linux 隔离宿主显式停用 Proxy 管理模块，运行生产停用门禁并确认引擎管理注册表缺席。Core 注册的原 TUN worker 仍接续真实 Mihomo 标记。管理路由缺失和标记路径暂时不安全期间保留责任，恢复后关闭原操作 751f2381-0b1d-4544-ab54-c2998110ad2d，实际禁用重载一次。日志为 .artifacts/mb04-tun-disabled-isolated.log，归档 SHA-256 为 50e125b1e9e17aa67e4cf56409b0427ed5236918076c70152d2565a2372e2baa。隔离宿主只启动 Core TUN worker。早期夹具缺数据保护注册及无关 Docker 台账，修正后通过。运行中动态休眠仍开放。

Linux 隔离 sing-box 到期验收通过生产停用门禁，确认 Proxy 管理引擎注册表缺席。Core 到期路径仍在既有凭据到期后执行。真实网络观察到过期凭据拒绝、保留凭据重连，发布进程退出后按原台账确认候选，apply 为一次。日志为 .artifacts/mb04-expiry-disabled-live.log，归档 SHA-256 为 8c08d76a3c9082a56a41039df6af666fdddb87a67035b31c72cbb88299658177。隔离宿主未启动全部 Core worker，运行时切换仍由专属 transport 承载。动态休眠仍开放。

Linux 私有挂载和网络中的非 root Core guard 通过 Proxy 管理停用门禁，确认管理注册表缺席。停用检查与 guard 共用原 Mihomo 发布台账实例。专属 Helper IPC 实测启用、稳定无写入、竞争值修正、原 journal 恢复及拒绝未授权 UID。日志为 .artifacts/mb04-guard-disabled-final.log，归档 SHA-256 为 549cf67a48a67781c2986056ef3926f4a8a5d26e6a58517cd1b8288950a453f5。TUN、授权到期、系统代理现均有各自停用后的隔离证据，完整宿主联合交接与运行中动态休眠仍开放。

Linux 隔离 sing-box 验收确认停用启动门禁在原待处理发布仍存在时阻止启动，即使真实流量已使用候选配置。原台账经运行时摘要确认后，门禁允许启动，apply 仍为一次。当前完整 Server 在 Core worker 启动前运行该门禁，因此管理已停用的 Server 若在到期发布中崩溃，仍需先让原发布完成核验才能以停用配置启动。日志为 .artifacts/mb04-expiry-gate-live.log，归档 SHA-256 为 74440c2833c410db329d819ca79f0b928848cab959b26aef900d11d27f8125d7。下一边界是明确只读启动恢复与未知结果阻塞之间的交接，不跳过权威检查。

MB-04 停用启动门禁现在可在原台账 gate 内用 Helper 状态摘要确认原入站候选。摘要缺失、不匹配或 Helper 报告恢复未完成时，保留原候选并阻止停用启动。门禁只调用状态查询，确认后按原发布路径写入台账，不调用 apply。Linux Release 专属 sing-box 验证了真实流量、摘要未知时拒绝启动、补回摘要后直接启动准入、到期凭据拒绝及保留凭据重连，apply 为一次。日志为 .artifacts/mb04-disabled-candidate-linux.log，归档 SHA-256 为 91005b1cc62708cc2fd726d2b22c776b02a8cabea2916264ce79ea3b135e29ed。该组合使用隔离 transport，完整宿主与真实 Helper IPC 联合重启待验收。Windows Release 模块边界回归通过，Docker 跨进程夹具恢复原 Interrupted 回执，副作用一次。日志为 .artifacts/mb04-disabled-candidate-win-milestones.log。Proxy 仍为 resident。

MB-04 将停用启动恢复与只读清单诊断分开。Linux 私有网络 Release 使用真实 sing-box 和 Helper Unix socket，通过 18 项。Helper 提交后中止原到期客户端，另起进程验证 socket 不可用时阻止启动并保留原候选，恢复连接后通过生产启动门禁确认原候选。只读诊断在同一条件下保留候选，Helper apply 请求、配置修订和 sing-box 启动次数各增加一次。日志为 .artifacts/mb04-disabled-startup-ipc.log，归档 SHA-256 为 89fcb96608ae9b0e293776ad84b83a9979ab38dabda6fe80b4cc37b9563c3b51。Windows Release Proxy Core 专项通过。测试宿主复用生产启动门禁，Helper 使用专属 socket 和隔离运行时目录，完整 Server 入口与生产 systemd 联合重启仍待验收。

MB-04 完整 Server Linux Release 启动联合验收通过。仓内 Tests/ManagedRuntimes/run-proxy-full-server-linux.sh 将 Server、Helper socket 和 sing-box 测试进程放入私有挂载、网络及 PID 命名空间。Helper 提交后终止原到期客户端。完整 Server 在 Helper 不可达时于监听前退出，保留原加密候选。接回同一 Helper 后，完整 Server 使用同一持久台账启动，所有可选模块保持停用，回环 /healthz 返回 200。Helper apply 请求、配置修订和 sing-box 启动次数各只增加一次。日志为 .artifacts/mb04-full-server-live.log，归档 SHA-256 为 dbe78b1075d7915f88767e81a37b8631508418ced51a8ecdd7a6c9043f33690e。首轮缺少测试宿主生产审计配置，补齐后两轮通过。Helper 在夹具进程内提供真实 Unix IPC，独立 systemd 进程及完整 Server 就绪后的下一次真实授权到期仍待联合验收。Proxy 保持 resident。

MB-04 完整 Server 的后续授权到期接续通过 Linux Release 实测。专属 sing-box 预置两条授权，原到期客户端仅撤销第一条并在提交后中断。停用启动门禁确认原候选，Server 在第二条到期前返回健康响应。随后由完整宿主的 Core 到期 worker 撤销第二条，原加密台账落盘且候选清空。两次到期各增加一次 Helper apply、配置修订和 sing-box 启动，启动恢复未重放第一次发布。日志为 .artifacts/mb04-full-server-expiry.log，归档 SHA-256 为 c2e4ceb96f35e8d58066cd6283e1d402dca3b0214380e299c90645bca6f08b04。Windows Release 原受管服务回归通过 15 项。本项确认真实运行时配置和完整宿主调度，未验证第二条授权的真实认证流量。Helper 仍由专属测试进程承载，生产 systemd、TUN/系统代理与到期责任的同宿主联合故障矩阵及动态休眠继续开放。

MB-04 完整 Server 到期接续补齐真实认证流量。专属网络在回环接口配置 93.184.216.34/32 作为测试上游，使用生产生成的 VLESS TLS 配置及路由规则。Server 停用 Proxy 管理模块启动后，到期授权与长期有效授权均完成实际双向转发。Core 后续到期发布关闭旧连接，过期 UUID 被 sing-box 拒绝且未到达上游，长期有效授权重新连接并完成转发。原候选确认及后续撤销的两次发布各执行一次 Helper apply，配置修订和运行时启动计数对应两次，未重放原发布。Linux Release 日志为 .artifacts/mb04-full-traffic-live.log，归档 SHA-256 为 7ce978de374b438825b707e48ad3ce3aea7502dd68f49c08f242ca4d60a48d80。首轮观察夹具以单元素读取两条授权而失败，修正观察条件后通过 19 项。完整 Server 在隔离 root 身份运行，Helper socket 由测试进程承载；非 root 完整宿主、独立 Helper/systemd 和 TUN/系统代理同宿主联合交接仍待验收。Proxy 继续 resident，动态休眠与生产内存收益保持开放。

MB-04 非 root 完整 Server 联合验收通过。仓内隔离脚本读取已有 nobody 的 UID/GID，以 setpriv 清空附加组并启用 no-new-privs，仅为专属临时内容、密钥和私有 Proxy 状态目录分配所有权。实机 /proc 身份核验确认 Server 的四个 UID 均为 65534。Helper socket 按内核身份接受 UID 65534 的状态查询和到期发布。拥有相同 socket 访问组的 UID 65533 被 Helper 明确记录 caller-rejected，未进入操作执行。非 root Server 保留 Helper 不可达时的原候选，恢复连接后完成启动确认和后续自然到期，真实 TLS 转发、过期拒绝、有效凭据重连与两次撤销各一次 apply 通过。Linux Release 日志为 .artifacts/mb04-full-nonroot-final.log，归档 SHA-256 为 0066c4625f041bde444085a166a5b0f5ac67b636026db595bdff8c0732d7245e。Windows Release 原受管服务路径通过 15 项。Helper 当前仍由 root 测试进程承载，独立 Helper/systemd 及同宿主多项安全职责的联合中断验收继续开放。未启用 Proxy 动态休眠。

MB-04 完整 Server、独立生产 Helper 和真实 systemd sing-box 联合验收通过。既有 Linux 测试机可在随机命名的委派 cgroup 内启动私有 systemd，仓内 Tests/ManagedRuntimes/run-proxy-systemd-linux.sh 隔离 PID、网络、运行时、服务单元和代理环境。仅用已存在的 sing-box 二进制及专属预置受管状态，运行生产服务适配器与 Helper --linux-service。原到期进程在收到真实 Helper 提交响应、写回领域台账前被终止，随后强制终止 Helper。私有 sing-box 单元保持原 PID，Helper 停止期间仍可认证并双向转发。非 root 完整 Server 在实际 socket 不可达时拒绝启动并保留原候选。新 Helper 进程启动后，Server 确认原候选、继续后续到期，过期授权被拒绝、有效授权重连。两个 Helper 进程各收到一次 apply，配置修订仅增加两次。Helper 拒绝未授权 UID。测试结束移除专属单元、cgroup 和目录，宿主 sing-box 状态及代理文件、恢复记录的指纹保持一致。Linux Release 日志为 .artifacts/mb04-real-systemd-verified.log，归档 SHA-256 为 fabb123c733fcfc7506c32b852bff42f20ea2174ae91339b660e510d2207a50a。Windows Release 原受管服务路径通过 15 项。完整宿主的启用系统代理、TUN 与授权到期联合故障仍待验收，Proxy 保持 resident。此处使用预置专属状态，未增加安装或版本生命周期验收。

MB-04 在完整非 root Server、独立生产 Helper 与私有 systemd 的 sing-box 恢复验收中加入启用的系统代理。仓内脚本为 /etc 建立私有可写覆盖层并使用已有 Mihomo 二进制启动专属 HTTP 代理。Helper 中断期间，真实 Mihomo 转发和原系统代理恢复记录仍可用。重启前向私有环境写入竞争值，停用 Proxy 管理模块的完整 Server 由 Core guard 修正该值并恢复真实 HTTP 转发，同时按原台账确认入站候选、处理后续到期。两个 Helper 进程各收到一次 sing-box apply。测试退出后显式恢复原代理环境并核对恢复记录已结清，Mihomo 在恢复结束前保持运行。宿主 sing-box 状态、代理文件和恢复记录通过前后核验，专属 cgroup 已清理。Linux Release 日志为 .artifacts/mb04-systemd-guard-live.log，归档 SHA-256 为 25533c013612a1f4eae70c4f01c9432cf4b47de339b125b7cdec2e2dec4cfe6a。Mihomo 使用专属直连配置，受管 Mihomo 发布补偿及 TUN 同宿主联合故障仍待验收。

MB-04 Linux Release 在同一私有 systemd 和非 root 完整 Server 下联合验证 TUN、系统代理和入站授权到期。生产 TUN 安全服务先完成专属 Mihomo 的实际激活并持久化标记，核对设备和策略规则变化。独立 Helper 中断期间原标记和设备仍在。重启后 Core 恢复原操作 726dcdcc-1454-4ed0-87f0-d1abd99507b0，移除 TUN 并核对管理路由和 IPv4/IPv6 规则回到基线。Mihomo 继续转发，系统代理竞争值修正及 sing-box 两次到期各一次 apply 通过。专属 cgroup 已清理，宿主 sing-box 与代理文件指纹保持一致。日志为 .artifacts/mb04-systemd-tun-live.log，归档 SHA-256 为 380c64ad9aa0c7a8674954755a4a96f753748af4c65f8be569901f3dcfb5c035。TUN 激活在注入 Helper 中断前已完成，Mihomo 采用专属直连配置。受管发布补偿和动态休眠仍开放。

MB-04 停用启动门禁现在对原台账中的 Mihomo 配置发布执行一次既有恢复流程。只读模块诊断保持原行为。恢复持有原发布锁，按原 operationId 查询 Helper 回执并在需要时使用原补偿入口，启动门禁重新读取台账。Helper 忙或仍需人工恢复时保留待处理记录并阻止启动。Windows Release 发布事务 112 项、Linux 113 项通过，覆盖忙后按原 Interrupted 回执补偿且候选提交一次。Linux 完整 Server 的 TUN、系统代理和到期联合回归通过，日志为 .artifacts/mb04-mihomo-native-linux.log。独立私有 systemd 与系统 D-Bus 上的原生 Mihomo 套件通过 171 项，使用生产启动门禁确认真实提交和真实原生拒绝补偿在响应丢失后的原回执，未再次发送配置或调用补偿。该原生分组通过测试进程调用 Helper 生命周期入口，响应丢失由夹具注入。日志为 .artifacts/mb04-mihomo-native-final.log，归档 SHA-256 为 6424b3785a681a1da758f8a3846538e31913eeca82ea9f6e9b29a5052e4ea057。完整 Server 经独立 Helper IPC 的受管 Mihomo 补偿仍待联合验收。Proxy 保持 resident。

MB-04 / MR-03 完整非 root Server、独立生产 Helper 和真实 Mihomo systemd 的联合验收通过。真实 IPC 暴露发布前的同 ID 查询占用短期请求缓存，使随后的配置请求在进入领域台账前被拒绝。Helper 将 Mihomo 只读回执查询交由原领域身份，变更请求仍受传输去重保护。实机验证原配置失败回执 8638a360-3b58-4a8c-85de-c2be899d339d 在响应丢失后按原 ID 确认。另在候选端口已经转发时终止 Helper，候选进程继续转发，Helper 不可达时启动门禁保留原操作。恢复连接后，完整 Server 经独立 IPC 对 6bad5e4c-0912-4574-9744-96e84b57bd1a 执行一次补偿，旧端口恢复真实转发，原 Interrupted 回执落盘，未重发 configure。Windows Release 发布事务通过 112 项，Linux 原生套件通过 171 项及两条完整 IPC 路径。私有 systemd、D-Bus 和 cgroup 已清理，宿主两项服务状态和代理文件指纹一致。日志为 .artifacts/mb04-mihomo-ipc-verified.log，归档 SHA-256 为 f16ff62ab36636c56ac114d1e6219e38d305bf58d8ed5c45319ae67a19c6a3b8。配置回执恢复范围通过，受管 TUN 事务、动态休眠与生产内存测量仍开放。

MB-04 按根引用审计缩短 TUN 控制器执行图的生命周期。Core 启动构造安全服务时，原单例控制器会持有 HTTP 客户端。现在由同一安全服务在实际观察和切换期间解析 scoped 控制器。HTTP 客户端随作用域释放，原 TUN 标记、发布锁、补偿路径及后台 owner 持续负责。Windows 与 Linux Release 注册专项各通过 19 项，实际响应 Task 完成前客户端存活，完成后释放，后续操作重新构造。Windows Proxy Core 安全 37 项、驻留 28 项、工作作用域 31 项通过。Linux 私有 systemd 的完整非 root Server 实测恢复原 TUN 标记和路由规则，Mihomo 转发持续，系统代理和授权到期接续通过。日志为 .artifacts/mb04-tun-scope-linux.log，归档 SHA-256 为 42871c5b40f1da77cee38527cf5c150a8328e9d66ae850fc43920a07c9def91d。此项证据是客户端对象和作用域生命周期，Proxy 动态休眠及生产内存测量仍开放。

### MB-04 完整 Server 接续在途 TUN 激活

Linux x64 Release 在原私有 systemd 夹具中使用生产 TUN 安全服务和实际 Mihomo。子进程已完成真实控制器重载、设备创建和策略规则变更，在写入激活完成标记前强制终止。原操作 `23ff134e-a01c-4970-9268-63ca3b352731` 在同一私有持久状态目录经过独立 Helper 退出及完整 Server 拒绝启动后仍保持未完成。Helper 恢复后，非 root 完整 Server 的 Core 日志确认原 ID，禁用 TUN、恢复管理路由及 IPv4/IPv6 规则，Mihomo 继续提供实际 HTTP 转发。系统代理漂移纠正和 sing-box 后续授权到期同时通过，两代 Helper 各收到一次入站 apply。专属 cgroup 清理完成，宿主 sing-box、Mihomo 及代理文件指纹一致。证据为 .artifacts/mb04-tun-activation-crash-live.log，归档 SHA-256 为 d0a258b8484b3e48cf32517cdb5ca98b5c34a756ad93bd379bf91ce57e128e65。Windows Release 受管服务 15 项及模块边界回归通过，Docker 跨进程夹具返回原 Interrupted 回执，副作用一次。Mihomo 使用专属直连配置。确认受管发布后的 TUN 事务、Proxy 动态休眠和生产内存测量仍开放。

### MB-05 证书自动续期跨日接续

原证书操作台账在准入锁内为自动续期查找同一证书的在途操作和未被后续成功续期替代的中断回执。原键与新的自动日期键返回原 operationId，不写新任务或发新 ACME 订单。显式授权的人工续期仍可独立提交，后续成功才解除自动续期阻止。ACME 服务同时核对授权状态，已有效的授权无需再提交挑战。

Linux x64 Release 在既有测试机的独立 Pebble CA 验证真实自动续期。首次实测在已有效授权的重复挑战处未到达签发屏障，修正客户端授权检查后，以确定性的有效授权复用复测。CA 已签发而客户端尚未接收证书时强制终止续期进程。重开原台账后，经生产 CertificateManager 提交原键和下一日键，均返回同一 Interrupted 回执 `de7400be-25b9-4cc9-8e39-0f695ed05730`，保留原活动证书指纹。整组签发、取消、挑战中断、签发响应丢失和续期共五张订单，恢复没有新增订单。日志为 .artifacts/mb05-renewal-daily-live.log，归档 SHA-256 为 3fd290056f8d217279c4c7809beab1da3e5a82ec3cbfd5f9d5d73cb41afc89ff。Windows Release 模块边界回归通过，覆盖跨日原回执、人工成功后自动续期恢复及 Docker 跨进程原回执、单次副作用。测试通过更换调度键模拟日期跨越，未改机器时钟。远端未知订单的主动对账、独立执行图及生产内存测量仍开放。

### MB-04 TUN 发布准入与恢复标记

TUN 激活在既有发布锁内通过待处理和确认发布门禁，并完成配置读取和计算后才提交恢复标记。恢复标记落盘后才允许首次文件和网络变更。拒绝准入或等待发布锁时取消，不产生虚假的 TUN 恢复责任。已有原标记遇到发布竞争仍保留原责任，不追加 reload。

Windows Release TUN 边界 33 项及模块边界回归通过，覆盖待处理和已确认发布、准入前取消、写入后取消、补偿及锁竞争。Linux x64 Release 私有网络专项首轮夹具使用旧发布修订号，修正为读取原台账后通过。原待处理发布 `b1bd32c1-b530-4990-918a-37cac406a530` 拒绝新的 TUN 激活，新增标记、reload、配置和规则变更均为零。另一条真实 TUN 恢复 `1a6ac90c-aeec-484b-a9ad-113e44ff17b3` 保留原标记直到路由可核实，原禁用 reload 一次。日志为 .artifacts/mb04-tun-admission-linux.log，归档 SHA-256 为 9d4bee3b1861cf0d42f01266c36b79f6d199c999eb15a79f920a425aa293bc18。完整非 root Server、独立 Helper 和私有 systemd 同批复测恢复原激活操作 `0719d031-b697-42db-82ed-d59526826165`，系统代理和授权到期持续，宿主服务状态及代理文件指纹一致。日志为 .artifacts/mb04-tun-admission-systemd-live.log，归档 SHA-256 为 d8e8a862bdefee52516f8ff640c02c9b2e44c881e499ff561a2327aea1ecdf74。此切片验证发布准入和原补偿，不启用已确认受管发布下的 TUN 网络事务。Proxy 保持 resident。

### MB-05 证书 CA 传输中断与原回执

发送订单请求后，ACME 调用遇到无法确认远端结果的传输失败时，原 Task 仍等待 SDK 返回并排空挑战清理，再以原 operationId 持久化 `certificate.operation_interrupted`。API 保留既有 Failed 状态和 interrupted 阶段。续期中断沿用原台账的跨日门禁，当前进程和重开台账均返回原回执，不进入普通失败退避或创建新订单。明确的业务失败仍走原失败路径。CA 账户准备阶段和订单之外的其他操作未纳入本项。

Linux x64 Release 在独立 Pebble 与 DNS 命名空间中通过。TLS 中继收到真实 CA 的订单创建 201 和已签发证书 200 后分别截断响应体并关闭实际连接。证书执行器保持运行，原订单操作 `8597f483-1b6a-437b-acde-7b92669a77c1` 和原证书下载操作 `a840b9a1-b4ae-43c6-9f23-4a47d9c7e163` 均以同一 ID 保留中断回执。原键、下一日键及重开台账后的新日期键均命中原回执。原证书指纹保持，续期普通失败计数为零，确认本地清理后工作租约归零。整组共七张预期 CA 订单，恢复零新增。日志为 .artifacts/mb05-acme-transport-live.log，归档 SHA-256 为 2f5b8c070d2489b6a35b860834265216f5f2ca4cad43b33069a8015e5472f9dc。Windows Release 模块边界回归通过，Docker 跨进程仍为原 Interrupted 回执和一次副作用。测试使用生产证书管理器、Anvil 和台账；完整 Server 认证入口及未知远端订单的主动查询仍开放。

### MB-04 已确认受管发布的真实 TUN 门禁

Linux x64 Release 在私有 systemd、D-Bus 和网络内复测独立 Helper 及非 root 完整 Server。原受管配置中断后只执行一次具名补偿，原 `c130d33d-de2b-493a-b0f4-ce0c41019b5a` 回执为 Interrupted，原端口恢复实际转发。随后生产 TUN 安全服务从已确认的 Server 台账拒绝直接激活。无控制器请求及新恢复标记，受管状态修订、原回执、配置字节、运行 PID、管理路由、IPv4/IPv6 规则和真实转发均通过前后核验。专属 cgroup 已清理，宿主服务状态与代理文件指纹一致。Linux 原生套件通过 171 项及完整 IPC 路径，Windows Release 受管服务 15 项通过。日志为 .artifacts/mb04-confirmed-tun-live.log，归档 SHA-256 为 97ca5ce3b2e788e7ac7d39434f052f42d032c0bb5ff2caf4d048d60bb0ff61fe。此项覆盖受管发布后的拒绝与责任保留，受管 TUN 网络事务与 Proxy 动态休眠仍开放。

### MB-05 原 CA 订单的只读对账

生产签发和续期在取得 CA 订单响应后，于提交挑战和请求签发前把原目录、账户、账户公钥指纹与订单 URI 持久化到同一 operationId 的台账。检查点失败按本地持久化故障保留工作责任。已知中断可通过认证的 `GET /api/v1.0/certificates/operations/{operationId}/acme-order` 查询，响应为原 operation 与 remoteStatus。查询核对当前 CA 目录、原账户密钥及同源 HTTPS 身份，只允许目录读取、nonce 请求与原订单的 POST-as-GET，不跟随重定向，不返回内部 URI，不修改回执、台账或当前证书。无可信检查点返回 identity-unavailable。远端 valid 表示 CA 订单状态，本地依然按原中断回执和证书版本处理。

Linux x64 Release 使用隔离 Pebble、真实 TLS 断流与生产证书管理器验证。创建响应丢失的原操作 `6d002ed8-eb47-4c10-a0b8-64c23bb19b6c` 返回 identity-unavailable。已签发响应丢失的 `6467531e-9b4d-460a-b974-3303381d5f5f` 与实际终止续期进程后的 `f65187c4-2aea-4f6f-830a-e91ff7a5afe6` 均在重开台账后观察到原 CA 订单 valid。传输断流后的查询前后台账字节一致，原证书指纹保持，下一日键继续命中原回执。整组共七张预期订单、四次预期签发提交，对账没有新增。日志为 .artifacts/mb05-acme-observe-live.log，归档 SHA-256 为 f379fec759de54010022835163f13c00815e2b0d50168b2812b13b04e162db18。Windows Release 模块边界回归通过，生产 HTTP 路由的隔离 Kestrel 测试核对原回执、禁止缓存和未知操作 404。已知订单与完整认证 Server 的联合实测、跨源 CA 身份、缺失订单身份的外部核实及独立执行图继续开放。

### MB-05 完整认证 Server 的 CA 断流与重启

Linux x64 Release 以私有网络、挂载及 PID 命名空间启动完整 System Mode Server。专属 Helper 只开放生产 PAM 验证及 sudoers 资格检查，生产认证、授权及证书 HTTP 路由全部参与。专属 Nginx 用实际 WebRoot 响应 Pebble HTTP-01，原活动证书签发后记录指纹。隔离 TLS 中继分别截断 CA 已签发的 200 和订单创建的 201 响应，原 Server 等待本地挑战清理并保存原中断回执后退出。

完整 Server 重启后，匿名查询返回 401，重新登录通过认证路由查询原续期 `f351872d-c279-41ab-b775-cbe29547de85` 得到 valid，原申请 `82f46e93-7950-4b63-9ee9-4dc3932ab6d7` 得到 identity-unavailable。两条同键提交均返回原回执，台账字节、原活动证书指纹保持。独立 CA 共三张订单和两次签发提交，查询和重试零新增。宿主 Nginx、Docker、Mihomo 状态与 PID，以及 /etc/hosts 和 /etc/environment 指纹前后一致。日志为 .artifacts/mb05-auth-acme-live.log，归档 SHA-256 为 2632a6b64707eccbbc11b3497a0851639cf3ed14e459d08c4c5dad73346258ae。Windows Release 模块边界回归通过，Docker 跨进程仍为原回执与一次副作用。完整认证与原订单恢复路径通过。缺少身份的外部核实、自动补取部署及独立执行图继续开放。

同制品的独立 Pebble 回归继续通过原签发、取消、进程中断、跨日和两种传输断流，七张订单和四次签发提交符合原矩阵。日志为 .artifacts/mb05-auth-acme-regression-live.log，归档 SHA-256 为 36fc3ec833fa37b4e8ccb7cea69904400d72cabe2d88368177373c5c31fd76bc。

### MB-05 证书续期持久扫描与执行作用域

同一个续期 HostedService 从原扫描台账读取下次到期时间。每轮在进入证书执行前持久化扫描 ID，已完成扫描的下一次计划为完成后一天。重启继续原未完成扫描和固定准入键。扫描从原证书操作台账补齐已经准入但未关联的回执，即使原证书已不在目录中。原台账继续拥有续期操作、退避和未知结果门禁。关联与无操作回执的拒绝按证书去重。旧扫描历史保留，不推导缺失的关联。

等待期间 HostedService 不解析续期扫描执行图。到期先取得原模块工作租约，再创建执行作用域。扫描取消或失败时保留原扫描，作用域清理完成后才持久化完成和下次到期时间。Core TLS 选择器、证书材料与 Nginx WebRoot 职责仍由原服务持有。

Linux x64 Release 私有 Pebble 中，原续期进程在 CA 已签发、原操作已准入但尚未关联扫描时被终止。生产续期执行作用域接续扫描 `4ca2be2e-39ad-4840-8043-4f6e4fc865d3`，从原台账补回 `ef1056c8-1579-495f-b342-0b1a70cbe815` 并持久化下次到期时间。测试作用域在清理时验证扫描仍开放、工作租约仍有效。原 CA 订单只读返回 valid，原本地中断回执保持。整组七张订单、四次签发提交，恢复零新增。日志为 .artifacts/mb05-cert-schedule-live.log，归档 SHA-256 为 370623bf9106342f41dfa7d2d70a2d88ed73c974b424b1bf0f02dd1aab287d0b。Windows Release 模块边界通过，覆盖跨日原扫描、原准入键、重复关联与拒绝去重、重启后的下次到期。完整 Server 自然计时触发和宿主停机联合实测仍开放。证书模块继续 resident，独立执行程序集及生产内存测量仍开放。

### MB-05 完整 Server 自然续期与停机排空

Linux x64 Release 在既有测试机的私有网络、挂载和 PID 命名空间复用完整认证 Server、独立 Nginx WebRoot 和 Pebble。专属 CA 签发短期证书并关闭可选 ARI 响应与授权复用，以覆盖正常到期兜底和真实 HTTP-01。未改机器时钟、生产启动延迟或扫描台账。生产 Worker 自然计时后持久化扫描 `ce78914f-c226-4a2c-9eb2-5a21640301a5` 和原续期 `370b24cb-72ce-4778-9ed3-78871a4477ca`。

TLS 中继收到实际 CA 挑战响应 200 后保留响应。Server 接收 SIGTERM 后监听退出，原续期仍为 Running，挑战文件仍在，进程持续等待。释放实际响应后，原操作写入 Cancelled，挑战文件清理，Server 正常退出。重启重新认证后，生产 Worker 在原启动周期内记录原下次到期时间并推迟扫描，台账字节和原证书指纹一致，按 ID 读取原回执。CA 共两张预期订单、一次签发提交，停机排空和重启零新增。宿主 Nginx、Docker、Mihomo PID 与主机文件指纹前后一致。日志为 .artifacts/mb05-natural-renewal-live.log，归档 SHA-256 为 c10e20014eef2acc821c11e609450002a8efa8531b28bbf1c73318ea08c78be1。Windows Release 模块边界及 Linux 独立 Pebble 回归通过，后者归档 SHA-256 为 d6e58f4bda1f2aeed11b7226d1157cf582f97a4a3551a9b9f0529825b59d08ef。证书继续 resident，独立程序集和生产内存测量仍开放。

### MB-05 完整 Server 自然续期的认证取消

沿用短期证书、生产自动扫描和独立 Nginx/Pebble 的 Linux Release 夹具。真实 CA 挑战响应 200 被中继暂缓时，匿名取消返回 401，认证取消返回原 `Running / cancel-requested`，重新读取原回执与磁盘均确认操作仍在途，挑战文件仍保留。释放响应后写入原 Cancelled，挑战清理，重复取消返回相同终态。重启重新登录后，读取及重复取消仍返回相同回执，原 CA 订单只读返回 `ready`，生产 Worker 延用原下次到期时间，扫描台账字节和活动证书指纹一致。未跨越下一次实际日扫描。

取消扫描 `126f084a-6176-40ef-9233-168d17f519a0` 的原续期为 `6db6e593-48c5-4ea6-9dfe-6d139b157f9e`。SIGTERM 对照扫描 `da76552f-4183-43f0-9eee-74add6eb5a2e` 的原续期为 `e5256887-2dd9-483f-94b8-098c4c493323`。两轮各两张预期订单、一次签发提交，取消、查询与重启零新增。宿主 Nginx、Docker PID 及代理环境和 Nginx 配置指纹一致。

日志为 `.artifacts/mb05-natural-renewal-cancel-final-schedule-cancel.log` 和 `.artifacts/mb05-natural-renewal-cancel-final-schedule.log`。归档 SHA-256 分别为 `e9f821a67256fde1807d26807b509f4ba6ee4649ad915cd1a5a7cbea5733c3c1` 和 `58af974738dcc21117bdec6b127811d88eeaef83e6f245c1d2659c132f8314d7`。首轮旧 Server 制品未通过取消后查询原 CA 订单，最终使用含 `9b8764db` 的制品通过。证书保持 resident。

### MB-05 网站发布准入与执行作用域

原网站发布协调器继续独占准入、台账恢复与停机排空。只有持久化 queued 的新发布创建执行作用域，执行器在原工作租约内处理上游观察、证书选择、Nginx 配置、应用关联、HTTPS 验证及原补偿。工作 owner 等待终态提交和异步作用域释放。启动恢复、已知键回执和准入拒绝不解析执行器。Core Nginx 管理、TLS 材料与应用关联仍由原服务持有。

同一台账锁同时为活跃发布保留应用及 Nginx 实例内的规范域名。同键先查原回执。不同键命中活跃发布返回 `website.operation_conflict`，命中已有未核验配置继续返回 `website.recheck_required`。域名占用在应用观察、CA 签发及配置执行前生效。

Linux Release 在私有 systemd、Docker、Nginx、Pebble 和 DNS 上启动两个独立应用。原发布 `69f61255-1f9c-407f-b946-25e24e7297fc` 的实际 CA 证书响应被暂扣时，另一运行应用提交带大写和末尾点的同域名请求，收到认证 HTTP 409，历史为空。原键返回原回执，CA 一张订单、一次签发提交。随后终止 Server，重启仍以原发布和原证书操作 `553bf539-e7e7-4321-bcb1-9e44e65047f2` 查询原有效订单。挑战已清理，旧 TLS 和原上游继续，零新增 Nginx 重载。专属 cgroup 清理，宿主服务与配置指纹一致。日志为 `.artifacts/mb05-publication-domain-live.log`，归档 SHA-256 为 `b76242814db9faa538ee0f8281ee5963ba612da11be18458509d294a02eb23b6`。首站自动建路由的后续交付见下文。

同制品正常发布 `fef71822-3ea7-4f02-b996-fc99a7870c7b` 通过真实 HTTPS 和一次重载，日志为 `.artifacts/mb05-publication-domain-success.log`。Windows Release 模块里程碑回归通过，验证并发拒绝没有创建执行作用域或新回执，Docker 跨进程原 `e8d5d6b4-bf59-41b3-bea3-8a7f2252860d` 返回 Interrupted，副作用计数为一。Windows 首轮夹具的应用创建与观察共用屏障，第二轮站点关联检查沿用单应用假设。修正后日志为 `.artifacts/mb05-publication-domain-win-confirmed.log`。

Windows Release 模块边界回归通过。测试确认原协调器启动不构造执行图，停止时等待真实观察任务和异步作用域清理，工作租约持续到清理结束。Linux x64 Release 在两套专属网络、挂载和 PID 命名空间用实际 Nginx 验证。配置提交崩溃 `8caf607a-1400-4dfe-96e8-ad67dbb518a9` 返回原 Interrupted / website.site_application_unverified，HTTPS 验证崩溃 `2d7f8936-648f-474f-8101-5ffe131a3d27` 返回原 Interrupted / website.recheck_required。两条路径的同键 HTTP 响应断线后均完成排空，恢复执行作用域为零，各只重载一次发布配置，真实 HTTPS 转发保持。宿主 Nginx PID 和配置指纹前后一致。首轮远端包装器在进入命名空间前失败，修正后通过。证据为 .artifacts/mb05-website-scope-live.log，归档 SHA-256 为 30c32dd3867bccc7b16ca16664b4d54c179df48e0563584a2afc510ae049968c。该联合验收使用生产执行器与真实 Nginx，HTTP 认证和响应屏障由隔离夹具提供。完整 Server 认证与直接 WebServer 后续验收见文末，网站发布模块保持 resident。

### MB-05 直接 WebServer 操作执行作用域

直接 Nginx 集成、重载、HTTP-01 启用及受管实例启动、停止、重启使用同一 WebServer 操作台账。只有新准入的操作在原实例锁和原工作租约内解析执行器。执行器在既有 Core Nginx 提供者上调用封闭命令，执行任务和异步作用域清理完成后才提交终态。清理不可确认时保持原活跃回执及故障租约。原子准入同时核验原操作者，其他操作者的同键碰撞返回冲突且不暴露原回执。共享提供者及同步站点接口仍归原 Core 服务。

Windows Release WebServer 和模块边界专项通过，覆盖取消期间的真实任务、异步作用域清理、清理失败保留原责任和操作者同键冲突。Linux x64 Release 在私有网络、挂载和 PID 命名空间用实际 Nginx 验证直接重载。专属重载命令退出成功后，传输返回前终止原工作进程，原 `eb74e92c-f9de-4351-9322-85f424d51a0d` 恢复为 Failed / interrupted。原实例不再可发现时，生命周期入口和直接重载 HTTP 同键入口仍返回原回执。客户端断线后响应排空，恢复作用域为零，原 Nginx 继续提供 HTTPS，实际重载一次。宿主 Nginx PID 和配置指纹一致。日志为 .artifacts/mb05-web-direct-scope-live.log，归档 SHA-256 为 b72228bb83eb26db12eda0aecbc5b93fa313c4942db3be2e297eb6740ea47b8e。本轮 Linux 使用生产提供者、原台账和实际 Nginx，封闭重载命令经隔离传输发往专属进程。完整 Server 认证重载见下文，其他直接动作的实机中断仍开放。WebServer 保持 resident。

### MB-05 Docker Compose 准入与执行作用域

原常驻协调器独占准入、项目互斥、回执和恢复。新操作持久化 queued 后才解析 Compose 执行图，异步清理结束后才写终态。清理失败保留原活跃回执和故障责任。启动仅为原 Running 项创建只读观察作用域，已知键重试不创建执行作用域。Core Engine、外部容器及卷由原设施持有。

Windows Release 模块边界与本地跨进程回归通过，覆盖原工作租约与回执跨异步清理、原 operationId 恢复和副作用计数。Linux x64 Release 在独立挂载、网络、PID 命名空间和私有 Docker socket 上通过真实 Engine 与完整 Server 验收。停止期间强制终止 daemon 和工作进程后，原操作 `ac82fe2d-9996-4012-a68d-5381c630801b` 恢复为 Interrupted，观察作用域一次、重放作用域零，见证容器的停止计数前后同为 2。完整 Server 经认证提交停止请求并丢弃原响应，在真实停止过程中强制终止 Server；重启后同键返回原 `2408d31c-3dfd-4ee8-9650-a08246796f28` Interrupted，停止计数只增加一次。私有 Engine 重启也保留原容器和命名卷，夹具清理专属资源，宿主共享服务 PID、容器和环境指纹保持一致。首轮包装器因 CRLF 在启动 Engine 前退出，转为 LF 后通过。证据为 .artifacts/mb05-docker-scope-live.log，归档 SHA-256 为 e0666d40f74d1058d6ca6d940a33903c9eff280cdef33b06a72c6bb9ecfba6b8。Docker 模块保持 resident。独立执行程序集、完整故障矩阵和生产内存测量仍开放。

### MB-05 完整认证 Server 的 Nginx 重载与重启

仓内 `RelaxKonOS.Server.Tests/run-authenticated-nginx-live.sh` 和同名 Python 驱动在独立挂载、网络和 PID 命名空间运行。私有状态、发现入口、配置和证书只供专属 Nginx。完整 Linux x64 Release Server 使用生产 PAM 登录和管理员授权路径，未认证重载被拒绝。专属命令实际向 Nginx 提交一次 reload，客户端验证原证书和新站点响应。命令返回前保留测试屏障，通过生产 API 确认操作仍 Running 后终止整个 Server。重启重新认证，同键和原 ID 查询均返回 `3eb76997-4b04-4849-ad72-a34423de592a` Failed / webserver.operation_interrupted。相同 Nginx master 持续提供 HTTPS，reload 计数保持一次。宿主 Docker 和 Nginx PID 及 Nginx 配置指纹一致。

日志为 .artifacts/mb05-auth-nginx-admin-live.log，Release 归档 SHA-256 为 e0666d40f74d1058d6ca6d940a33903c9eff280cdef33b06a72c6bb9ecfba6b8。前几轮暴露私有状态挂载、专属 Nginx 初始化和测试身份选择问题，修正后通过。测试命令包装器仅限专属集成实例，生产提供者、授权、台账及重启入口均参与。系统包 Nginx 的 Helper/systemd 故障见下文，其他直接动作仍待验收，完整认证的网站发布见文末。WebServer 保持 resident。

### MB-04 Core 接续运行期 TUN 恢复

原 Core TUN worker 在启动后持续扫描原持久标记。同一安全服务在原 gate 内识别当前进程已完成的会话，扫描不会拆除该会话。主动关闭在首次运行时或路由变更前将原标记置为未完成，保留原标记 ID 和路由快照。旧进程遗留或当前未完成的标记由原安全服务恢复。控制器或路由结果不可核实时继续持有原标记，只有控制器、设备和管理路由都核实后才清理。空闲扫描不解析控制器或管理执行图。

Windows Release TUN 边界 34 项、Core 安全所有权 37 项、工作作用域 31 项及模块边界回归通过。Linux x64 Release 在私有网络、挂载和 PID 命名空间用实际 Mihomo 验证。原崩溃操作 `5c9f30ec-7db1-4cee-a8ee-0777fc08b82a` 恢复时经历真实路由缺失、控制器响应丢失与不安全标记路径，原禁用 reload 一次。随后同一 Core worker 保留已完成的新会话。删除专属默认路由后显式关闭会话，原 `bb1ba90e-4e66-4237-ac4b-bf527f374067` 仍以未完成标记存在。路由恢复后，同一 worker 关闭原标记，设备和策略规则回到基线，未追加禁用 reload。宿主 Mihomo、sing-box PID 与代理文件指纹一致。日志为 .artifacts/mb04-tun-rescan-live-final.log，归档 SHA-256 为 befc69710c42c3a2d6844a2037396bb8bae39ab1a59dd646faf78d6caad3eeed。首轮本地重启夹具误用原 owner，已用两个 owner 复测。首轮远端包装器引号错误，在连接前退出，修正后通过。受管发布仍保留网络事务门禁，Proxy 保持 resident。

Linux 完整非 root Server、独立 Helper 与私有 systemd 联合回归也通过。完整 Server 恢复在途激活 `b5a3586c-3afb-4f92-9e67-2e86a5e75518` 的原标记和路由，Mihomo 继续运行，系统代理纠偏、sing-box 授权到期及真实转发接续，专属 cgroup 清理且宿主指纹一致。日志为 .artifacts/mb04-tun-rescan-systemd-live.log，归档 SHA-256 为 1e837ca6a8a4fa02c1b967d37906ee05e183f5cac44a47dfcb102f714e091a19。

### MB-04 实际 TUN 激活期间取消与同进程补偿

Linux x64 Release 在私有网络、挂载和 PID 命名空间中等待实际 Mihomo 接受 reload。设备及策略规则已经创建，生产 TUN 安全任务仍等待响应。测试驱动在响应交付处触发取消，并暂时移除专属默认路由，原任务以取消退出，保留标记 `777a7afa-2958-421d-81cf-c98dadc34745`。管理路由恢复后，原 Core worker 在 Proxy 管理图缺席时接手，同一 Mihomo 的激活和禁用各 reload 一次，设备和规则回到基线。测试屏障仅用于控制取消时点。Windows Release 模块里程碑回归通过，Docker 跨进程原回执保持 Interrupted，副作用一次。Linux 宿主共享服务 PID 和代理文件指纹一致。日志为 .artifacts/mb04-tun-cancel-live.log，归档 SHA-256 为 15ab51cafa0b3fdd7ce9efc1e24118636dc5dc2e350a1aba3417477ded3c5589。

此项验证现有直接 TUN 安全任务取消及 Core 接续。完整 HTTP 已受理任务仍由原队列持有，不随 HTTP 断开取消。受管 TUN 继续开放。当前 Linux Mihomo 受管单元隔离设备且能力集仅含 CAP_NET_BIND_SERVICE，控制器配置入口本身不提供受管的设备、路由和补偿权威。

### MB-05 系统包 Nginx 经生产 Helper 中断与恢复

仓内 `RelaxKonOS.Server.Tests/run-authenticated-nginx-systemd-live.sh` 启动专属 systemd 委派 cgroup，隔离挂载、PID、网络、systemd 单元、PAM 配置和状态。完整 Linux x64 Release Server 走真实 PAM 和生产 Helper Unix socket，调用固定 `nginx.service` 重载。专属单元执行实际 Nginx reload，并在重载后暂缓结束。客户端核验更新后的 HTTPS，生产 API 同时报告原操作 Running。强制终止 Server 和独立生产 Helper 后，原 Helper socket 不可达，原 Nginx master 仍以同一证书提供 HTTPS。释放专属 systemd 重载并重启两进程后，同键与原 ID 均返回 `53b6405f-7cda-41c0-9c46-0e9994f65df6` Failed / webserver.operation_interrupted。原 master PID 保持，实际 reload 一次，专属 cgroup 清理完成。宿主 Nginx、Docker 的服务 PID 和配置指纹一致。

系统包路径日志为 .artifacts/mb05-auth-nginx-systemd-final.log，归档 SHA-256 为 1e837ca6a8a4fa02c1b967d37906ee05e183f5cac44a47dfcb102f714e091a19。原集成路径独立复测保留 `0ddd5092-68a4-476a-aad1-d6345f7c150d` 和一次重载，日志为 .artifacts/mb05-auth-nginx-integrated-final.log。系统包的自然停机和取消联合排空见下文。Helper 自身等待超时后的原 systemd 作业对账仍开放。

### MB-05 已提交 Nginx systemd 作业的取消与停机排空

Linux Unix socket 在 Nginx 固定服务动作提交前仍接受取消。帧提交后等待生产 Helper 的终态响应，原 WebServer 工作任务、实例锁与执行作用域继续持有责任。其余 Helper 操作沿用各自响应等待策略。原版在专属 systemd reload 尚未结束时已把原回执从 Running 推到终态，夹具明确失败。

修复后，真实 reload 更新 HTTPS 后，客户端取消原 `45e7671c-86c1-4146-904c-11bca1078c13`，直到原 systemd 重载结束，回执仍 Running。释放原重载后，同键返回原 Cancelled。该回执记录管理操作取消，已生效的 Nginx 配置继续提供服务。第二次真实 reload `b6c2d4fd-6f01-45b0-97a5-caab66fecd0e` 期间发送 SIGTERM，监听关闭后 Server 仍等原作业结束才正常退出。重启同键返回原中断回执。两条路径合计两次实际 reload，原 master 和 HTTPS 持续，宿主服务指纹一致。Windows Release 模块里程碑通过。

失败和修复日志分别为 .artifacts/mb05-auth-nginx-drain-before.log 和 .artifacts/mb05-auth-nginx-drain-final.log，修复归档 SHA-256 为 39b27952467315dc201df33627780b3ddf79bc9a316ae2ef52be5b9ba497251e。Helper 自身命令超时及传输丢失后的原回执保留见下文，原生终态查询和关账仍开放。

### MB-05 Nginx 原生重载结果待核验

直接系统包 Nginx 重载在派发前持久化原操作检查点。生产 Unix socket 在派发后发现连接丢失、帧不完整或 Helper 自身超时，报告原生结果未核实。存活 Server 保持原 Running / native-recovery-required 回执、工作租约和实例锁。启动遇到未获终态的派发检查点也保留原 ID 和待核验回执，已知键和原 ID 可读，同实例新的后台操作返回 409。收到明确的 Helper 响应时沿原终态与停机规则处理。此阶段尚无 Helper 原作业终态的持久查询入口，后续补齐见下文。待核验回执不会因系统服务仍健康、看不到作业或等待超时而自动关账。桌面和 Android 显示待核验提示。

最终 Linux x64 Release 私有 systemd 实测三条路径。仅终止 Helper 时，存活 Server 保留 `5560ad28-8aef-4374-9b19-1e392586351b`。Helper 自身 30 秒超时且真实 reload 作业仍在时，保留 `fc4e4ac4-993f-474c-b426-f655af7fcf69`。双进程强制终止时，重启从派发检查点保留 `7c6f143f-8e6f-4b61-b45c-5c890f108af8`。每条路径均核对原 ID、同键、不同键拒绝、HTTPS 持续、同一 master 和仅一次实际重载。Windows Release 模块边界通过。Linux 同批取消与 SIGTERM 排空复测通过，收到原 Helper 终态后仍按原规则持久化。专属 cgroup 清理完成，宿主共享服务和配置指纹一致。

日志为 .artifacts/mb05-auth-nginx-checkpoint-unknown.log、.artifacts/mb05-auth-nginx-checkpoint-timeout.log、.artifacts/mb05-auth-nginx-checkpoint-crash.log 及 .artifacts/mb05-auth-nginx-checkpoint-drain.log，归档 SHA-256 为 c663b4d80d62f9d41e313064a74be4105cd7ed3de0e951bc3e88a22a9e1b456b。原 operationId 的 Helper 成功证据和门禁解除见下文。WebServer 保持 resident。

### MB-05 Nginx 原 Helper 回执对账

直接系统包 reload 将原 WebServer operationId 传给生产 Helper 的专属固定操作。Helper 在 root 保护目录内独占写入原意图，再调用固定的 `systemctl reload nginx.service`。原命令明确成功后原子持久化成功证据，再发送响应。同 ID 执行请求若已存在原意图，只读取原回执。只读查询按同一 ID 返回成功证据，超时、非零但无法核实原生终态、文件缺失或身份不符均保留待核验。Server 原后台工作 owner 在运行期与启动时按原 ID 查询，只在匹配证据后持久化成功并释放原责任。未完成查询不构造 Nginx 执行作用域，不再次派发。

Linux x64 Release 私有 systemd 实测。原重载 `c8021da0-0f86-4b20-81b5-093455edc5b4` 已更新真实 HTTPS 时终止 Server，随后生产 Helper 收到实际 systemctl 成功并落盘。重启 Helper 与 Server 后，原 ID 和同键返回 Succeeded，原重载仍为一次。确认原回执后，新键才准入并完成下一次重载。Helper 提前终止 `281f0065-49b7-40ff-bfa1-5b010e1679ce` 和真实 30 秒超时 `ea6aaf34-479e-4192-a0f5-557a42762336` 继续保留同 ID 待核验，实际各一次。正常取消与 SIGTERM 排空复测通过。原 Nginx master、HTTPS 和宿主指纹核验通过，专属 cgroup 清理。Windows Release 模块里程碑通过。Linux 原工作进程崩溃和 HTTP 响应断线夹具也复测通过，原待核验回执和单次重载保持。

联合日志为 .artifacts/mb05-nginx-receipt-receipt-live.log、.artifacts/mb05-nginx-receipt-unknown-live.log、.artifacts/mb05-nginx-receipt-timeout-live.log 和 .artifacts/mb05-nginx-receipt-drain-live.log，归档 SHA-256 为 fa12570bc4d2f4b970b79326639576140bf4f8b94011e83855808d5a448fa790。HTTP 断线夹具日志为 .artifacts/mb05-nginx-receipt-fixture-live.log。该阶段范围为明确成功证据，失败证据见下文。若 Helper 未收到或未写入原终态，系统健康及时间流逝无法解除门禁。此前未向 Helper 传递原 ID 的回执也保持待核验。Helper 回执保留策略和系统重启后的联合验证仍开放。

### MB-05 Nginx 存活进程响应丢失和失败回执

Helper 原 ID 回执支持实际成功及可确认的失败。仅接受固定 `systemctl reload nginx.service` 对所等待原作业的明确失败诊断，读取失败、通用非零、断连和本地超时仍保留原意图。只读查询返回带原 ID 的终态证据，Server 持久化 Succeeded 或 Failed / webserver.reload_failed 后才解除原实例后台准入门禁。原生失败诊断来自 [systemd 作业终态等待实现](https://github.com/systemd/systemd/blob/main/src/shared/bus-wait-for-jobs.c)。

Linux 私有 systemd 上，Unix socket 中继仅截断实际生产 Helper 的首个终态响应，并短暂阻止只读查询，以核对原操作进入待核验、新键被拒绝。释放查询后，完整 Server 和原 Helper 保持存活，成功操作 `1a3516f4-6709-486f-99ef-76f9b478a2bd` 按原 ID 落盘，实际 reload 一次。真实 Nginx 配置检查拒绝 `bdd59a66-2a3a-453c-9488-8f4f0d348ba0`，作业尝试一次，未发重载信号，原 HTTPS 和 master 持续。按原 ID 只读确认失败后，新键才通过并完成下一次真实 reload。两次均核验同键和共享服务指纹，专属 cgroup 清理完成。首轮中继把终态字段名大小写读错，修正后通过。

日志为 .artifacts/mb05-nginx-live-loss-attempts-response-loss.log 和 .artifacts/mb05-nginx-live-loss-attempts-rejected.log，归档 SHA-256 为 7587cfe65e52aa7bf83feaee98548d2eafad09831c9f0dbba1a75fd0effabff5。Windows Release 模块里程碑通过。Linux 本批取消和 SIGTERM 排空复测通过，真实 30 秒超时操作仍保持原待核验回执。日志为 .artifacts/mb05-nginx-live-loss-final-drain.log 和 .artifacts/mb05-nginx-live-loss-final-timeout.log。Helper 未观察到明确原作业终态时保留待核验。Helper 回执保留策略、主机重启及其他 WebServer 直接动作仍待验收。

### MB-05 Nginx 终态回执保留与防重放

Server 在原后台 owner 内同步持久化终态及清理待确认标记后，才以原 ID 通知 Helper。Helper 仅处理有明确终态的原记录，用原子替换将详情压缩为同路径的零字节防重放标记，继续拒绝派发该 ID。未确认记录保持原样。标记已存在时重复确认成功，丢失清理响应时由 Server 启动按原 ID 重试清理。Linux 同步回执文件和目录，Server 的整账替换也同步，避免后续写入削弱已通知 Helper 的原终态持久性。自然停机时若已收到确切原生响应，重启可按 Helper 证据完成原终态。显式取消仍按原取消语义结案。

Linux 私有 systemd 实测原 `04f1f463-a0cb-4197-a271-8ad1afb93c9d` 在 Server 退出时由 Helper 保留完整证据，Server 重启按原 ID 完成后才压缩。Helper IPC 重发该 ID 在本代及 Helper 重启后均被拒绝，原 HTTPS 持续，同键读取 Server 原成功回执。另截断原 `469bb500-0257-4560-a8dd-cb27ccced484` 的真实清理确认。Server 已有原成功回执和待确认标记，Helper 已压缩。两进程重启后只重试原清理，完成原标记，无额外 reload。真实 30 秒超时的原待核验回执拒绝清理。自然停机与取消排空复测通过，自然停机重启后核实原成功。日志为 .artifacts/mb05-nginx-retire-timeout-live.log 和 .artifacts/mb05-nginx-retire-drain-live.log。Windows Release 模块里程碑通过，专属 cgroup 清理与共享服务指纹通过。

日志为 .artifacts/mb05-nginx-retire-receipt-live.log 和 .artifacts/mb05-nginx-retire-ack-retry-live.log，归档 SHA-256 为 fb3aeac412206f5e0771a5b11795534f3345974871ee00907bf2587fc9857ef8。每个已清理 ID 仍占一个防重放目录项，目录项总量、旧终态详情的迁移清理及整机掉电恢复未验收。WebServer 保持 resident，本阶段未测量生产内存。

### MB-05 完整认证网站发布中断与恢复

在既有 Linux 测试机的私有 systemd、挂载、网络和 PID 命名空间运行完整 Release Server、生产 Helper、系统包 Nginx 和独立 Docker Engine。仅从共享 Engine 读取已缓存的 Alpine 镜像。专属应用通过认证路由创建，夹具在私有 Engine 启动带应用所有权标签的 HTTP 上游。生产证书路由生成专属自签证书，Nginx 使用证书仓库绑定，HTTPS 客户端校验证书和实际转发正文。

配置阶段在原生 reload 已生效、systemd 尚未返回结果时终止 Server。重启从原 operationId、应用历史和同键 POST 读回同一 `Interrupted / website.site_application_unverified` 回执。应用未关联，实际 HTTPS 转发持续。验证阶段在应用关联已提交、真实 HTTPS 探测到达容器后由上游暂缓响应。此时终止 Server，重启以同样三个入口读回 `Interrupted / website.recheck_required`，保留原关联，未重发探测。两条路径分别核验原 Nginx master、原上游容器和一次原生 reload。

首轮缺少专属 Nginx 密钥目录，第二轮上游容器提前退出，修正后通过。原系统包 Nginx Helper 回执路径和 Windows Release 模块边界回归也复验。Windows Docker 跨进程专项恢复原 `Interrupted` 回执且副作用一次。夹具清理专属容器和 cgroup，核对宿主 Docker 与 Nginx 进程、运行容器清单和 Nginx 配置。此阶段覆盖生产发布与自签证书绑定，公共 CA 与网站发布的同一事务、整机中断、独立执行程序集和动态休眠仍待验收。WebServer 保持 resident。

最终复验的原操作分别为 `3e073972-f898-42ab-ad2c-ff362435be20`（配置）、`4f891ce5-b66d-4475-8ca3-b4f35e94413f`（验证）和 `2ac65837-94cc-4069-82e5-1deac7f48dea`（直接重载回执）。Linux 日志为 `.artifacts/mb05-auth-website-final-website.log`、`.artifacts/mb05-auth-website-final-website-verifying.log` 和 `.artifacts/mb05-auth-website-final-receipt.log`，归档 SHA-256 为 `fb3aeac412206f5e0771a5b11795534f3345974871ee00907bf2587fc9857ef8`。Windows 日志为 `.artifacts/mb05-auth-website-win.log`，Docker 原操作 `9d25c4d8-aa4e-4f28-92c2-548bae0dfd9c`。

### MB-05 网站发布自动 ACME 的原 Direct HTTP-01 阻塞

旧网站自动签发路径选择 Direct HTTP-01。生产 `HostPrivilegeService` 拒绝把 Server 的进程身份当作权限，现有生产 Helper 无此监听能力。实机在私有系统启动独立 Pebble 和 DNS，已探通 TLS 目录，旧发布在签发前返回 `certificate.port80_elevation_required`。当前网站自动签发路径见下节。

此路径的跨进程回归核对已持久化的原失败回执、同键和应用历史，检查证书操作 ID 缺失、CA 无新订单、没有打开 80 端口或重载 Nginx。原 HTTPS、原 Docker 上游和共享服务继续运行。

Linux 原发布 `929bf5d6-7e68-4df1-baca-8f503bb88af0` 跨重启返回原失败。首轮等待签发响应失败，第二轮通过生产回执确认拒绝原因，最终轮按拒绝路径验收。日志为 `.artifacts/mb05-acme-authorization-final-website-acme.log`，现有证书路径的 Verifying 回归见 `.artifacts/mb05-acme-authorization-final-website-verifying.log`。Release 归档与上一阶段相同。

### MB-05 网站发布 WebRoot 签发与中断恢复

自动签发要求所选实例已有 ACME WebRoot 集成，并检查实例配置权限与证书签发权限。新域名须预先从 CA 的网络通过 TCP 80 到达该 WebRoot。证书操作用原网站 operationId 持久化关联与独立的请求键范围，网站重启恢复可补齐尚未写入的原证书回执。WebRoot 签发和续期在写入前记录挑战文件名与内容摘要，原证书任务完成挑战清理后才提交终态。重启清理核对原路径与摘要，文件无法确认时保留并隔离证书工作准入。

Linux x64 Release 使用完整 PAM/JWT Server、生产 Helper、私有 systemd、真实 Nginx 和 Docker Engine，以及专属 Pebble CA 和测试域名。夹具预置专属 Nginx HTTP-01 路由，CA 实际通过 Nginx 取挑战。在成功路径，正常签发并部署到真实 Docker 上游，TLS 客户端信任专属 CA 并核对实际响应，Nginx reload 一次，原挑战已清理。在中断路径，测试中继暂扣 CA 已签发的实际证书响应，此时终止 Server。重启后按原网站 ID、应用历史和同键返回同一中断回执，原证书操作 ID 可只读查询 CA `valid`。CA 只创建并 finalize 一次，网站未部署，原 TLS、Nginx master 和 Docker 上游继续运行。原挑战路径返回 404，无关测试文件保留，Nginx 无重载。

最终中断发布 `a18aad80-5b6a-48a3-a131-25961501e8d6` 关联证书操作 `a1694919-20d1-4657-84ee-a2a0820a7546`。成功发布为 `7b65af66-eb68-4ac1-8103-491250e4f420`，原 Verifying 回归为 `f9cc0811-4dbd-4816-9bde-53d06c5a18b6`。三个 Linux 运行均清理专属 cgroup，宿主 Nginx/Docker PID 与 Nginx 配置指纹一致。日志为 `.artifacts/mb05-webroot-final--systemd-website-acme.log`、`.artifacts/mb05-webroot-final--systemd-website-acme-success.log` 与 `.artifacts/mb05-webroot-final--systemd-website-verifying.log`，Release 归档 SHA-256 为 `9cb6545d4a170606d6a6f854cb015315263a3dadc964ce4eaed7a61717094f9d`。首轮夹具误读 `remoteStatus`，修正后通过。Windows Release 模块边界回归（含跨进程 Docker 原回执与 WebRoot 台账恢复）及 Android 网站发布请求单测通过。Windows Direct HTTP 实监听在允许本地 URL 注册的运行中通过 16 项。

待验收范围包括公共 CA 与公共 DNS、完整非管理员提升交互及整机掉电。首个独立站点的显式启用及网站发布自动建立路由见后续小节。独立执行程序集与动态休眠仍开放，Certificates 和 WebServer 保持 resident。

### MB-05 首站 HTTP-01 显式启用与重载中断

Linux 系统包 Helper 允许已有暂存文件原子移动为集成目录中的固定 `acme-http01.enabled`。其余 `.enabled` 文件及子目录中的同名文件仍拒绝，原路径和链接检查继续生效。旧 Helper 实测在该移动处返回 `webserver.config_elevation_required`。原有 WebRoot 的网站发布对照继续通过。

既有 Linux 测试机的私有 systemd、PID、网络和挂载命名空间运行完整认证 Server、生产 Helper、真实 Nginx、独立 Pebble 与专属 DNS。夹具从空的 RelaxKonOS 站点目录开始，空实例启用返回 `webserver.acme_no_managed_sites`，没有重载。通过认证 API 保存首个 HTTP 站点并显式启用 WebRoot。正常路径启用 `55e19870-e050-4f17-8c70-7bd28f705e7b`，新证书操作 `b60a2556-83e5-45a7-bda1-f438356e3391`。Pebble 实际通过 Nginx 取挑战并签发一次，挑战清理后绑定到原站点，原 Nginx master 用受信任的 TLS 提供站点正文，旧 HTTPS 站点持续服务。

中断路径在 Nginx 已服务挑战路由、真实 systemd 重载未返回时终止 Server。重启后按原 ID `5350cd4b-bb11-40ac-bc5e-36530f4264b4` 和同键返回相同 `Failed / webserver.operation_interrupted`，没有重放重载。独立检查路由后明确提交新证书操作 `7e7e9144-3b9c-45dd-a7e8-2b7f3745daf0`，一次 CA 签发完成且原挑战清理。当时站点 `.rollback` 阻止随后的 TLS 绑定，返回 `webserver.site_save_failed`，备份字节、原站点元数据和旧 HTTPS 保持，未发生第三次重载。后续恢复见下节。

两轮的专属 cgroup 已清理，宿主 Nginx、Docker PID 和 Nginx 配置指纹一致。日志为 `.artifacts/mb05-first-site-final--systemd-first-site-acme.log` 与 `.artifacts/mb05-first-site-final--systemd-first-site-acme-success.log`，Release 归档 SHA-256 为 `494dec81bf1d019fb0acd57d2bf330a3e7954a2869419883a2301512b3f6bf45`。初轮夹具遗漏向命名空间传入模式参数，修正后复现旧 Helper 拒绝。Windows Release 模块里程碑回归通过，Docker 跨进程恢复 `e3ed673d-e29f-48e5-8429-776513717dc6` 为原 Interrupted，副作用计数为一。

### MB-05 首站 WebRoot 备份核验与授权 TLS 绑定

Linux 系统包 Nginx 的站点更新在持有原配置门禁时，从持久站点定义重建有 WebRoot 和无 WebRoot 两份配置。当前文件和原 `.rollback` 分别逐字节匹配，且 Helper 确认 Nginx 服务作业已排空，新的授权更新才可用当前配置原子替换可重建的旧备份。Helper 的系统服务操作共享执行门禁，空闲检查还查询实际 systemd 作业，以识别跨 Helper 进程的在途作业。文件不匹配、缺少当前文件、链接或作业状态不明时保留原备份。原启用回执仍按原 ID 返回中断，文件证据只用于后续更新准入。其余站点配置中断形态继续保留供核验。

Linux Release 在私有 systemd 上复验。原启用 `d100fc82-3425-404b-993d-ea20587de1d8` 的重载仍在途时终止 Server。重启后由 Pebble 真实验证并签发，证书操作为 `7fbaf778-4c93-4c58-bbd8-9a9dbb34715c`，全程一次订单和一次签发提交。原 Helper 存活、Helper 崩溃并重启但原 systemd 作业仍执行，以及故意修改备份三个条件下，绑定均拒绝并保留当时备份内容及原站点元数据，未新增重载。恢复准确的备份且作业排空后，明确提交的新绑定只重载一次，清理备份，原 Nginx master 用新证书提供站点正文并保持旧 HTTPS。绑定完成后同键仍返回原中断回执。

同制品复验直接重载原回执 `b7054c8b-5d2b-4e88-9795-8f211f4fd158`，Helper 重启后旧 ID 仍拒绝重载。两个专属 cgroup 已清理，宿主 Nginx、Docker 及配置指纹一致。日志为 `.artifacts/mb05-first-site-recovery-final--systemd-first-site-acme.log` 与 `.artifacts/mb05-first-site-recovery-final--systemd-receipt.log`，归档 SHA-256 为 `033dd6f176406d9ee99dd7788509c53f27eb66269a186685c17e25f45c6be121`。Windows Release 模块里程碑回归通过，Docker 跨进程原 `d2699ea5-6353-46df-8efe-0ea63e018a2a` 返回 Interrupted，副作用计数为一。网站发布自动建立首站 HTTP-01 路由见下节，整机掉电仍开放。

### MB-05 网站发布自动建立首站 HTTP-01 路由

Linux 系统包 Nginx 在已集成、运行中且受管站点为空时支持首站自动 HTTP-01。Helper 只读核验现有 TCP 监听和固定 `nginx -T` 的配置摘要条件。80 或 443 被使用，配置包含对应显式监听、隐式监听或无法确定的语法和截断输出时，拒绝在该实例建立路由。完整配置留在 Helper 内。自动路径目前建立 IPv4 路由。

原发布在任何配置写入前持久化站点 ID 和未核验责任。临时配置包含同一发布 ID，仅服务该域名的 HTTP-01，其他 Host 和请求路径返回 404。只有原 ID 的原生重载回执确认成功才允许提交 ACME。最终站点写入前逐字节核验临时配置和原 ID，成功后用正式 TLS 站点替换临时配置、保留该站点的挑战路由，再完成原应用关联。单站点标记独立于实例级 WebRoot 集成。

重启原发布仅恢复原回执与未核验责任。该阶段中断时保留原文件及 Helper 证据，拒绝同目标新发布及直接覆盖。后续授权清理见下节。网站发布和证书仍由原常驻 owner 管理。

Linux Release 在私有 systemd、Docker、Nginx、Pebble 与 DNS 通过三轮。原发布 `612a9f7c-9b01-4cd4-80cc-6a70f63f2bf4` 由原生 Nginx 提供实际挑战，证书操作 `a458416c-b552-4fb2-86ef-c47eee91fe92` 只创建及签发一次，真实 HTTPS 返回容器正文。共两次重载，临时配置和备份均清理，原 Nginx master 的旧 TLS 持续。独立活动监听及未加载的 Nginx 端口配置均在写入和下单前被拒绝。

中断发布 `2701ed88-08aa-43cb-9443-2c77bb5c3819` 在原生重载生效、返回前终止 Server。错误 Host 和非挑战路径均返回 404。重启后同键返回原 Interrupted，原 Helper 回执及同 ID 请求都读到原成功证据。直接覆盖和新键发布被拒绝，原配置字节保持，只有一次重载，CA 零订单，应用未关联。已有 WebRoot 的发布对照 `a65aa654-df57-4a2e-a4dc-02f1c986e5d4` 仍以一次重载完成 HTTPS。三轮专属 cgroup 清理，宿主 Nginx、Docker 和配置指纹一致。

最终日志为 `.artifacts/mb05-website-bootstrap-final--systemd-website-acme-bootstrap-success.log`、`.artifacts/mb05-website-bootstrap-final--systemd-website-acme-bootstrap.log` 和 `.artifacts/mb05-website-bootstrap-final--systemd-website-acme-success.log`，归档 SHA-256 依次为 `d5417d2af3f11a2345250f2f9f91e820d1afe60c39946aa842bd1ae0322dd984`、`f892f7cda6d915e4631b90acf65a3479db16a8a0e24a563c2312cf0faac5c6fe` 和 `74c890658f2077b511f281946627d84ef9ba050c2589520af66fea93e664be18`。

Windows Release 模块边界回归通过，Docker 跨进程原 `dba7519d-8ecb-4b6d-8aa6-a220662a1821` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb05-website-bootstrap-win-final.log`。

### MB-05 首站临时路由的授权清理

授权清理按原发布 ID 提交并在原发布协调器的工作租约中排空。进入前核对原 Helper 重载回执、已结束的本地证书责任、站点未关联、已集成锚点和原临时配置字节。实际移动前在原发布台账持久化独立的清理重载 ID。临时配置移动到专属备份，原清理 ID 确认原生重载完成后才移除备份、提交清理时间并解除站点占用。原发布的状态、错误和关联证书保持。

重新提交清理使用同一 Helper ID。原清理已确认时仅继续删除匹配备份或提交清理终态。原生作业仍在途、Helper 缺原回执、配置改动或证书责任未结束时保留原证据。确认的 CA 验证拒绝在挑战清理后由原发布自动执行同样流程。签发结果未知时仍保留原回执，启动不自动提交 CA 或重载。

Linux Release 使用私有 systemd、Docker、Nginx、Pebble 和 DNS。专属 Pebble 验证端口不可达，实际创建一个订单并返回验证失败，未提交签发。发布 `0a923e1a-027b-4aa3-a483-af157d1c5aa2` 以清理 ID `1072ea25-10e7-43ef-9798-b9d7ee1a725e` 自动完成一次清理重载，原挑战和临时配置移除，原回执保持 `certificate.validation_failed`。正常发布对照 `8fe89344-f862-4a62-9595-5ad004c34479` 完成一次签发及两次预期重载。日志为 `.artifacts/mb05-bootstrap-cleanup-final--systemd-website-acme-bootstrap-failure.log` 与 `.artifacts/mb05-bootstrap-cleanup-final--systemd-website-acme-bootstrap-success.log`，归档 SHA-256 分别为 `4d00c410b9a43ce9e31cbb3d7cf9961119c3588ef848d6a1f508474a1e194e5c` 和 `fe681e2ef6cc09f2c71bb0719317ee7d2c3ed3e7ea8e5231854edd75e92ab2c8`。

Windows Release 模块里程碑回归通过，Docker 跨进程原 `8ce9da8e-6b46-4eba-bec7-e73965d70454` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb05-bootstrap-cleanup-win-final.log`。Linux 首轮夹具的 Pebble 端口变量在外层展开失败，修正后通过。

授权清理最终复验中，发布 `78cf5d3d-bc48-4321-8c51-cc99e9bfd2ef` 的 Server 在原建路由重载期间被终止。重启后匿名清理、在途重载清理和被改动配置清理均被拒绝。原证据恢复后提交授权清理 `83cdfa90-c83a-4865-97f0-1ca16fba95e9`，实际清理重载开始后再次终止 Server。再次重启用原 ID 接续，移除匹配备份并持久化清理终态。同键仍返回原 Interrupted。总计一次建路由重载和一次清理重载，CA 零订单，原 HTTPS 保持。新键请求可进入证书校验并因测试证书不存在而结束，未新增重载或订单。各轮专属 cgroup 已清理，宿主 Nginx、Docker 和配置指纹一致。日志为 `.artifacts/mb05-bootstrap-cleanup-final-admission.log`，归档 SHA-256 为 `395b04e10dd7c5dd68e7a99cefe3fe09858903148d39bf4faa8c04e276a0e421`。

### MB-05 首站授权续接原证书

启动恢复保存原中断阶段。证书阶段中断、原证书操作已成功落盘、原应用仍为未关联的本地 IPv4 上游时，授权续接可以复用该证书。配置门禁内再次核验原路由字节、原锚点、原 Helper 重载成功及当前无原生作业，随后在原发布回执写入续接开始，再执行一次正式 TLS 绑定。原证书与发布 ID 保持，应用关联后持久化续接完成和验证检查。重复请求返回原回执。

若续接启动后再次中断，原回执保留起始时间及未核验责任。后续有原生成功证据的绑定对账见下节。现有自动签发仍由原证书操作负责。缺少已持久化的原中断阶段、原证书成功证据或原配置时保留核验门禁。

Linux Release 使用私有 systemd、Docker、Nginx、Pebble 和 DNS。测试中继在本地证书操作已经终态成功时暂缓交付实际 Helper 只读回执，此时终止 Server。原发布 `5dea1429-9724-4b2b-9bbb-31a330a2e6ad` 重启后仍返回 Interrupted。匿名及配置字节被改动的续接均被拒绝，且未持久化续接开始。恢复原配置后，授权续接使用原证书操作 `74bde98a-8f57-4ce5-8de3-258b14558cc8` 完成绑定，TLS 客户端收到实际 Docker 上游正文，原 Nginx master 继续服务旧 HTTPS。重复续接与原发布同键均返回同一回执，全程一次 CA 订单、一次签发和两次预期重载。

同制品授权清理对照原发布 `43b10f0f-d394-4947-996f-1257f93c9840` 与清理 ID `fad5db52-5da3-4026-9cb8-71e9098ae32f` 通过。对应日志为 `.artifacts/mb05-bootstrap-resume--systemd-website-acme-bootstrap-resume.log` 与 `.artifacts/mb05-bootstrap-resume--systemd-website-acme-bootstrap.log`，归档 SHA-256 分别为 `8accb0476fa556326965474ea2efd9a82897766c7dc6b81a4f7f1beb47419fba` 和 `68c574ff87b456402ccf795b32835bf769a63e476c7a45d981fc81cdf747d63b`。Windows Release 模块里程碑回归通过，Docker 跨进程原 `74250f92-5ba4-4d5e-b5f0-539553f16539` 返回 Interrupted，副作用计数为一。

二次中断最终复验原发布 `4021a173-ec05-40ed-b372-5e9076092baa`，原证书操作 `0ffcaa2d-266b-4410-999d-dfcd6734a43e`。续接已使真实 TLS 可达，原生重载仍在返回屏障内时再次终止 Server。重启后保持原回执、原证书和未核验责任，应用未关联。续接与清理均返回 409，原旧 TLS 和新 TLS 继续，仍为一次订单、一次签发、两次重载。初轮断连在夹具中打印预期的断管异常，收敛日志后复验通过。最终日志 `.artifacts/mb05-bootstrap-resume-final-interrupt.log`，归档 SHA-256 为 `2dc4e30151bb1a2654fa0d5258910aa615ef3b55ada6d3449b3a0b0389e09d84`。三轮专属 cgroup 清理，宿主共享服务和配置指纹一致。后续新增可核验的绑定证据见下节。

### MB-05 首站续接绑定的原生回执对账

授权续接在任何正式配置写入前，原子持久化独立 Helper 重载 ID、站点定义、正式配置 SHA-256 与证书指纹。续接使用该 ID 执行原生重载。失去终态时保留正式配置、原临时配置备份和原回执。再次授权续接只读取原生回执，核对站点定义、正式配置、证书指纹、原临时备份和当前上游。确认成功才补齐站点元数据及应用关联，并在二者持久化后移除匹配备份。备份已移除时必须已有匹配站点元数据和关联。原发布状态和原证书回执保持。

Helper 无成功证据、现有配置不匹配或原证书及上游已变更时，保留待核验责任。授权续接的正式 TLS 绑定先采用该流程，初次发布接入见下节。

Linux Release 在既有隔离环境用真实 Pebble、Nginx 和 Docker 复验。原发布 `549a59f9-fd3a-4814-973f-b124aaa37005` 在续接绑定 `81b6c554-6b35-435b-8d1e-ac09c41ad24b` 已服务 TLS、Helper 尚未完成原生重载时二次终止 Server。重启后仍拒绝正在运行的原作业，且拒绝正式配置或原备份被改动的路径。原文件恢复一致后，按原绑定回执补写站点和应用关联并清理备份。重复续接和发布原键返回同一原回执，原证书操作 `305c1a55-5b24-4672-9028-9952c9d9a9c5` 保持。旧 TLS 与新 TLS 都从原 Nginx master 返回真实正文，全程一次 CA 订单、一次签发和两次预期重载。

正常续接 `d5b1b454-8708-4a14-a135-c6b9575d8f50` 与清理对照 `9b51f6c7-d3b9-4554-b477-fe67a1a7d24a` 通过。日志为 `.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap-resume-interrupt.log`、`.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap-resume.log` 与 `.artifacts/mb05-bootstrap-bind-reconcile--systemd-website-acme-bootstrap.log`，归档 SHA-256 依次为 `b752f417b07621a16fdb91d459a6af556001cfd4302cde4d58a5ee379526d94c`、`d00afbe2adc4ebcc854bb83c115c5de10bdb5576571d24bdcb15b261bf9387dd` 和 `58faa3ce04f32455b1fb77feb6802c0ecb5b2fce47b165ef2c1459a860ac10eb`。三轮清理专属 cgroup，宿主 Nginx、Docker 和配置指纹一致。Windows Release 回归通过，Docker 跨进程原 `6a2602e1-01a7-40fd-be61-44d125f1ed31` 返回 Interrupted，副作用计数为一。首次构建因证书摘要类型误用失败，修正后通过。

### MB-05 首站首次 TLS 绑定的原生回执对账

自动建立 WebRoot 的首站首次发布与授权续接共用正式绑定记录。首次路径在写入正式配置前持久化独立 Helper 重载 ID、证书指纹、站点定义和正式配置摘要。正常发布在原生重载成功后核验这些证据，随后补写站点和应用关联、移除原临时配置备份。原回执按正常流程完成，首次成功路径没有续接时间。

首次正式绑定中断后仍按原发布 ID 授权续接。已记录绑定时，续接只核验原 Helper 成功回执、当前证书及上游、正式配置和备份，完成本地关联并保留原中断回执。原生结果未确认时保留证据并拒绝清理或继续写入。既有 WebRoot 和手动指定证书的发布保持原流程。

Linux Release 使用既有私有 systemd、Docker、Nginx、Pebble 和 DNS。首次发布 `04524788-ec36-4957-bc74-d2efd67a15fb` 在原 TLS 绑定 `1b64ec26-9b19-41f2-b0b2-fbd0c6b0dacd` 已提供真实 HTTPS、原生结果仍在途时终止 Server。重启读回原 Configuring 中断，续接和清理在 Helper 无终态时拒绝。原 Helper 成功后，续接保留原证书操作 `3b786c6e-cf4d-4716-b359-9539f88d00be` 和正式配置字节，补写站点及应用关联并移除备份。同键和续接重试返回同一回执，全程一次订单、一次签发、两次预期重载。原 Nginx master 与旧 HTTPS 持续服务。

正常首次发布 `553aa6d4-7a9e-4d11-8cfc-b9a62ba15165` 和授权续接二次中断 `12448c61-9fec-4155-ada5-d5111b225607` 同批通过。三轮均清理专属 cgroup，宿主 Nginx、Docker 与配置指纹一致。日志为 `.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-bind-interrupt.log`、`.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-success.log`、`.artifacts/mb05-first-bind-reconcile--systemd-website-acme-bootstrap-resume-interrupt.log`，归档 SHA-256 依次为 `3662d74aa8ecf9517313f2d1ff57f954c141c637d8007eabe4229e6c8460ebff`、`506ae122963dedc178334f597dbc17ee96c0612a7e82f088fa3f4a7bc4b3acb4`、`c74e6c315f2c2066c7a12441ae6fe5e6af994935a8d087387c31841d86b88298`。Windows Release 模块回归通过，Docker 跨进程原 `84c84432-35e1-43d2-b0b9-e670b703b28e` 返回 Interrupted，副作用计数为一。

### MB-05 网站 WebRoot 签发的取消与停机排空

已记录 CA 订单的 Cancelled 回执保留原订单身份。原 ACME 只读对账路由也接受该回执。续期已记录订单时，下一次自动扫描沿用原回执，直到另一次明确授权的续期成功。

同一 Linux 私有 systemd 设施中，Pebble 已实际签发，中继暂扣真实证书响应。认证取消返回原 Running / cancel-requested，挑战文件仍在。释放响应后，证书原回执为 Cancelled，网站发布原回执为 Failed / certificate.operation_cancelled。另一轮向完整 Server 发 SIGTERM，Server 先停止接收 HTTP，继续等待原 CA 响应且保留挑战文件。交付响应后，挑战清理与原证书回执落盘，Server 正常退出。重启后原网站发布标记 Interrupted。两轮均按原证书 operationId 查询 CA valid，原挑战清理，未新增订单、重载 Nginx 或提交新应用关联，旧 TLS 和真实 Docker 上游持续运行。

取消发布为 `aa53d0b9-acc2-42cd-9347-f74b67e7a2e9`，证书操作为 `9222c5ac-cd33-4477-b8a1-129fdef81146`。SIGTERM 发布为 `d9257bbe-d069-4461-ab3e-835a206a586c`，证书操作为 `e446e203-0669-4b79-a358-a2b4c654c792`。日志为 `.artifacts/mb05-acme-drain--systemd-website-acme-cancel.log` 和 `.artifacts/mb05-acme-drain--systemd-website-acme-drain.log`，归档 SHA-256 为 `00bd22b753e8caa35ff9bfe68de0572bc7c305223c96a5ee9833edc25d4f20c0`。Windows Release 模块边界回归覆盖重开台账后下一次自动扫描保留原取消回执。自然续期取消的 CA 实测见上文，主机掉电仍开放。

### MB-04 入站授权到期的停机排空

Core 继续使用原受保护台账和摘要绑定的到期索引，只有到期或存在待确认候选才创建执行作用域。入站发布写入候选后，原 Helper 调用及终态提交继续执行，调用方取消不截断响应。到期 worker 停止接收新扫描并等待实际任务与异步作用域释放，停机调用者的等待期限不充当清理完成证明。管理队列继续使用原操作租约，Core 到期执行不依赖管理模块启用。

Linux x64 Release 在私有 systemd、生产 Helper 和真实 sing-box 上通过。原 Helper apply `d14b9cfa-b26d-42ed-ac99-01a91ff64514` 生效后，夹具暂缓向 Core 交付实际响应。用已取消的停机令牌调用 worker Stop，原发布仍持有领域锁和持久候选。交付响应后确认原终态已落盘，Stop 继续等待异步作用域清理。此次实际配置修订仅增加一次，保留授权完成真实转发。同批用另一次到期 `207e2563-c2ed-422c-9b04-77fc03bd4f80` 复验跨进程恢复。Helper 不可达时完整 Server 保留原候选，重启 Helper 后按原摘要确认，后续授权自然到期并拒绝真实认证。系统代理、TUN 和保留授权接续通过，宿主状态与文件指纹一致，专属 cgroup 已清理。

Linux 日志为 `.artifacts/mb04-expiry-drain-live.log`，归档 SHA-256 为 `9aaa2a7a57f4dc4fb1d7ab013d28ff5a5f44da0923f8486fa1b9c30858e37751`。Windows Release 独立 Proxy Server 与模块边界回归通过，日志为 `.artifacts/mb04-expiry-drain-proxy-win.log`、`.artifacts/mb04-expiry-drain-milestones-win.log`。本次停机直接驱动生产 worker，响应交付与作用域清理屏障由夹具注入，完整 Server 的 SIGTERM 同时命中这两个边界仍待实测。

原入站台账仍是恢复权威。此阶段没有为 Core 到期发布新增独立 Helper 终态回执。候选摘要可用于确认已提交的配置，尚缺原配置摘要仍存在时区分未派发和已补偿的原操作证据。Proxy 保持 resident。

### MB-04 入站发布按原 Helper 回执恢复

原入站加密台账在派发前保存 Helper 操作 ID、候选和旧配置摘要。受管 sing-box Helper 在原归档状态及实例锁中记录同一 ID。进入原生变更前写入在途标记，随后记录已提交、已补偿或未触及原生配置的明确拒绝。只读回执验证归档与实际配置摘要，Server 重新核对原 ID 和两个摘要，才提交候选或确认原配置。停用启动门禁调用同一恢复路径。旧摘要单独出现、原回执缺失、旧候选缺少 ID 或回执仍在途时保留责任，不自动重发。

在途标记阻止同一受管实例进入新的发布、切换和移除，原紧急停止入口仍可用。Helper 目前保存最近一次原生发布证据。若恢复期间该证据已被后续受理操作取代，Server 保留原未知结果。Helper 在原生变更中被终止后的按 ID 补偿入口和旧数据缺失证据的人工关账仍待实现。Proxy 保持 resident。

Linux x64 Release 使用生产 Helper、完整非 root Server 和私有 systemd 的真实 sing-box。原到期 `2fc60685-3fb8-4b15-a60b-6540abfa401e` 在 Helper 提交后、Server 写回前中断。重启前后原 ID 返回相同回执。Helper 停止期间有效授权仍可转发。恢复接通后完整 Server 确认原候选，Helper 只接收之后自然到期的新 apply，未重发原发布。TUN、系统代理、真实流量及专属 cgroup 清理回归通过，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-proxy-receipt-final-live.log`，归档 SHA-256 为 `3289e4001a0a2edb2fec28985313160d703dc7885322b0e7def93aa550e366ec`。

Windows Release 独立 Proxy Server、Proxy Core、模块边界及受管服务分组通过。受控原生适配器的明确补偿按原 ID 返回两个配置摘要，未将该夹具计作 Linux 外部故障。首轮 Linux 暴露通用保存强制推进安装修订，改用原归档锁内的回执专用保存后通过。成功配置提交仍推进一次安装修订。

### MB-04 在途入站发布的原 ID 补偿

原归档在原生变更前记录服务是否已注册及是否运行。显式恢复走原 Proxy Server 恢复入口，以原 ID 读取待处理发布和 Helper 证据。在原实例锁内核对原程序、现有注册、候选或旧配置摘要及旧配置备份。若原进程仍在运行且能证实已加载旧配置，则保留进程。需要还原时先解除开机启动，恢复原配置和注册状态并保持停止。后续由正常启动路径重新校验授权。补偿校验和临时文件清理完成后按原 ID 保存 Compensated，原生配置修订不前进。再次恢复返回同一回执。后台扫描、停用门禁与诊断仍不能派发补偿。

旧在途记录缺少服务基线、配置或注册发生不可核实漂移时保留责任。管理请求原排队记录本身中断后，通用 Proxy 队列仍保留原准入门禁。该情况下原用户回执的人工关账及完整认证 HTTP 恢复仍需单独验收。Proxy 保持 resident。

Linux x64 Release 最终复验在私有 systemd 上使用实际 sing-box。隔离进程执行生产发布逻辑和实际原生配置写入，候选由 systemd 注册后、启动前强制终止进程。独立生产 Helper 拒绝错误 ID，后台扫描保留原候选。生产 Server 恢复服务使用原 ID `893ead6b-4252-400e-8836-eb421c8ec565` 恢复旧配置和注册，关闭专属服务原已启用的开机启动，并保持停止。重试返回同一补偿回执，未重发 apply 或推进修订。通过正常启动入口后原监听器恢复真实认证转发。原到期、Helper 重启、TUN、系统代理和专属清理回归均通过，宿主指纹一致。故障屏障在隔离发布进程内，补偿经生产 Helper Unix IPC 执行。日志为 `.artifacts/mb04-proxy-native-compensation-final-live.log`，归档 SHA-256 为 `7eeae813c532824adc37b9321f63f6df4c44c75cfafd6fba51c5be72c4b1e2f7`。Windows Release 受管服务 16 项、独立 Proxy Server 与模块边界回归通过。Windows SCM 原生中断和旧进程仍在运行的恢复分支待实测。

### MB-04 原管理发布队列的恢复

管理发布和回滚共用 HTTP 队列的原操作 ID 与 Helper 操作 ID。加密入站台账保留同 ID 的已确认结果，覆盖领域已经写回、队列尚未完成异步清理和终态写入的间隔。显式恢复仅认领有对应入站证据的原发布队列条目，继续使用原工作租约、审计与清理。无法核实的结果保持中断责任。不同领域的未知条目仍保留原准入门禁。原发布键和显式恢复键均指向同一个用户回执。补偿完成后原发布为 Failed，已提交的原发布为 Succeeded。

Linux x64 Release 在私有 systemd 中通过两条跨进程生产路由验收。Kestrel 首进程受理后在原生在途 `77cd82df-64c6-4d29-b621-c5e7c0e41b60` 或领域已提交 `6a8c29a4-e52a-492d-9412-fc87c25c01ca` 的屏障被强制终止。新进程拒绝未认证的恢复请求，认证恢复返回原 ID 和对应 Failed 或 Succeeded，原 apply 各一次，补偿只在前一路径执行一次。原发布键与恢复键返回同一持久终态。HTTP 组使用测试认证方案与受控 Helper。另以同一 Release 复验生产 Helper、真实 sing-box 原生补偿、授权到期和 TUN/系统代理，专属 cgroup 清理且宿主指纹一致。首轮远端包装器漏传 HTTP 组程序集，原生回归完成，补齐参数后两组同批通过。日志为 `.artifacts/mb04-queued-recovery-final-live.log`，归档 SHA-256 为 `460f7814b2766fd3e6a4a079b35f399f9d6b7004fc20ab6a4ad6dbe5f8d0f6f5`。Windows Release 独立 Proxy Server 和模块边界回归通过。完整 Server 经生产认证、队列和生产 Helper 的同一次跨进程事务仍待实测。未有候选或领域终态证明的队列记录保持原准入门禁。

### MB-04 完整认证 Server 的原入站发布回执

Linux 私有 systemd 在同一次事务中运行非 root 完整 Server、生产 PAM/JWT、生产 Proxy HTTP 队列、独立生产 Helper 和真实 sing-box。隔离夹具使用预置专属证书和原有测试机账户。私有 Unix 中继按原字节转发请求，仅暂扣本次真实 apply 的成功响应。生产 Helper 已提交新配置，新增授权已验证真实流量，Server 的入站候选仍处于原 ID 的待确认状态，此时终止 Server。首次连接由隔离中继代发，重启后的 Server 以非 root UID 直接连接生产 Helper。

Server 重启后重新通过 PAM 登录。匿名恢复返回 401，原发布键返回原 Interrupted，显式恢复及原恢复键返回同一 ID，持久化终态为 Succeeded。真实 apply 一次，原配置修订只推进一次，管理进程重启期间 sing-box 继续运行。保留和新增授权均完成真实双向转发。此次联合验收针对原生已经提交、Server 尚未收到结果的路径。原生仍在途时的显式补偿已独立实测，完整 PAM HTTP 与该分支的同事务联验仍待执行。

最终 Linux x64 Release 的原操作为 `467fad21-7888-4888-8234-7583f4175db7`。中继使用私有 `/run`，实测 Server 重启前后的原 sing-box PID 相同。Linux 证据为 `.artifacts/mb04-full-auth-proxy-final-live.log`，归档 SHA-256 为 `a76e44b9c9d066c84c7588fd691e3c2a06586ade32742d7aede61e704d62c63b`。同批 TUN、系统代理、后续授权到期和原生补偿回归通过，宿主服务与代理文件指纹一致，专属 cgroup 已清理。Windows Release 受管服务 16 项、独立 HTTP 队列和模块边界回归通过。

### MB-04 完整认证 Server 的在途入站发布补偿

完整非 root Server 通过 PAM/JWT 受理入站发布，经 Unix socket 直达生产 Helper。私有挂载命名空间中的 `systemctl` 夹具只在本次实际候选已经写入并注册后，暂扣专属 sing-box 的启动调用。旧进程已经实际停止，Helper 归档与 Server 队列记录同一在途 ID。核对原候选证据后强制终止 Server 和 Helper，等待拦下的启动调用退出。重启两进程后，原发布键返回原 Interrupted，显式恢复按原 ID 补偿一次，原回执结案为 Failed / `proxy_server.config_apply_failed`。两个原键均返回相同持久终态。

真实配置和服务注册回到原摘要，安装修订未推进，开机启动关闭且服务保持停止。生产 HTTP 启动路径重新核验授权后，原有效授权完成真实双向转发，本次候选授权被拒绝。生产 Helper 重启后未执行 apply。中断时点由隔离命令屏障控制，实际配置变更、补偿、启动与流量均在专属 systemd 服务中执行。Proxy 仍保持 resident。

最终 Linux x64 Release 原操作 `f44e0d99-db0b-4aba-ae1d-f404f43d06ca`。Helper 日志确认非 root Server 按原 ID 查询并补偿一次，后续另发正常启动。真实已提交响应丢失、TUN、系统代理、到期和原生补偿回归同批通过。宿主服务与代理文件指纹一致，专属 cgroup 已清理。日志为 `.artifacts/mb04-auth-inflight-final-live.log`，归档 SHA-256 为 `c5b9593373fb8b679e2b97552ef3e780230c853ac97ce86364d444f491acb806`。Windows Release 受管服务 16 项及模块边界回归通过，Windows SCM 中断仍待实测。
### MB-04 Core TUN 恢复的停机排空

Core TUN worker 停止调度后仍等待已接受的恢复任务。接受前尊重取消。原标记写入未完成状态并发出实际禁用后，控制器响应、管理路由验证和原标记清理在同一次执行内完成。等待方的停机期限不会归还责任。无法确认禁用或路由时，原标记仍是恢复权威。

Linux x64 Release 私有网络命名空间使用真实 Mihomo。夹具暂扣实际禁用响应，并用已取消的等待令牌调用生产 worker 的 Stop。原 Task 和标记均保持活跃，交付响应后恢复基线路由与规则、移除设备并结清标记 `af65c4e5-5881-470f-a6af-6739756adec5`。实际禁用 reload 一次，Mihomo 继续运行。同批复验发布门禁、响应丢失、DNS 劫持、路由故障、旧恢复引用与激活取消。宿主服务 PID 和代理文件指纹一致。日志为 `.artifacts/mb04-tun-drain-final-live.log`，归档 SHA-256 为 `d714219fad46c5866967d4d2a577af5380ef7990fc4a1474ca00c3a85f2fefe3`。首轮新增用例通过，旧累计计数断言因统计范围扩大失败，调整断言位置后全组通过。

Windows Release TUN 34 项、Core 安全 37 项、驻留 28 项、工作作用域 31 项和模块边界回归通过。停机专项直接驱动生产 worker。完整 Server 的 SIGTERM 联验、其他 Core 安全 worker 的停机交接和 Proxy 动态休眠仍开放。

### MB-04 Core 系统代理 guard 的停机排空

Core guard 停止调度后等待实际对账任务与执行作用域。对账取得原发布锁并重新核验设置后，派发前仍可取消。已派发的系统代理写入使用原发布锁等待 Helper 返回。Helper 的环境所有权日志继续负责中断恢复和外部修改保护。

Linux x64 Release 在私有挂载和网络命名空间使用非 root Server 身份与独立生产 Helper Unix socket。先核验未变化的 guard 扫描不改写所有权日志，再注入专属环境值冲突。Helper 实际纠正冲突后，测试传输暂扣交付给 guard 的结果。用已取消的等待令牌调用生产 guard 的 Stop，任务与原发布锁保持活跃，私有环境和原恢复日志可核验。交付结果后同一任务释放锁，未追加 apply。随后停用恢复保留了更新的外部设置。真实 Helper 纠正请求为 `72564240-2c8f-468c-9a99-b17447eabfbc`，日志为 `.artifacts/mb04-system-proxy-drain-live.log`，归档 SHA-256 为 `b78e7956222ade99b316dd85aab1f067cf2f52b149b542a5c4a58ffa9467a83b`。宿主环境和恢复日志指纹一致。

Windows Release 系统代理、Core Proxy 安全与模块边界回归均通过。此次故障屏障在 guard 的传输边界，覆盖停机等待取消。完整 Server 的 SIGTERM、真实 IPC 断流时的联合排空、Windows 原生代理环境和 Proxy 动态休眠仍待验收。

### MB-04 Core Mihomo 发布恢复的停机排空

原调度器从持久索引发现原操作，继续使用同一领域锁和 Helper 回执。停止扫描可取消等待准入和后续重试。已经取得领域恢复锁并接受的实际恢复，连同原回执核验、应用台账持久化及异步作用域释放，在停机期限后仍由同一调度器任务排空。

Linux x64 Release 私有 systemd 与真实 Mihomo 用原配置操作 `d3397e41-3413-43f5-b9df-8a339b52b75b` 验证。原生配置已生效，SQLite 选择记录首次提交失败。新调度器从持久索引发现原 ID，读取真实 Helper 回执，响应暂扣期间实际监听器仍能转发，已取消的 Stop 等待令牌未结束任务或释放原锁。交付结果后原 profile 选择落盘，Stop 继续等待异步作用域清理。实际 configure 未增加。该路径直接驱动生产调度器，在测试进程内调用生产 Helper 领域入口。

同一归档复验完整非 root Server 与独立生产 Helper 的启动回执查询及中断补偿，原操作分别为 `fea2da8c-c881-4e87-823e-1b82ba5ad97f` 和 `133314fd-b76a-448a-8ad1-b03fddb4e75a`。Linux 原生 171 项、Windows 发布事务 112 项通过。已完成请求的旧夹具改用 SIGTERM 排空 Helper 日志，实际中断仍强制终止。专属 cgroup 已清理，宿主共享服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-publication-drain-verified.log`，归档 SHA-256 为 `ada60f63cd5a884270d7b3391e16354efb078d441db97d2abc07fe74e0f7469e`。

完整 Server 的 SIGTERM 与此排空屏障的联合实测仍开放。安装和移除使用同一调度器停机契约，各自的在途停机尚未纳入本轮。Proxy 保持 resident。

### MB-04 Mihomo 运行时与安装执行作用域

根引用审计定位到两个长期持有者。Mihomo 运行时管理器持有直接 HTTP 控制器，安装台账适配器经引导和移除协调器持有发布执行图。运行时管理器继续持有原实例锁，只在状态探测或整个启动就绪循环创建控制器作用域。安装台账适配器继续管理原执行与取消身份，在实际执行或读取原待恢复事务时解析发布协调器，收尾后释放。Core 安全调度器、原发布锁、原领域台账及 Helper 协议继续承担各自责任。

Windows 与 Linux Release 的注册检查各 23 项通过。空闲解析两个根服务不会创建执行图。状态探测持有控制器直至实际响应，401 仍按原规则表明控制器在监听，取消后释放本次作用域。Windows 安装发布 88 项、移除发布 92 项、安装协调器 47 项和配置发布 112 项通过，Core Proxy 安全与模块边界回归也通过。首轮注册夹具在异步文件读取完成前读取控制器列表，改为等待创建信号后复测通过。

Linux 私有 systemd 172 项通过。连续状态读取使用真实认证控制器，持续验证真实 HTTP 转发，Helper 原配置修订与派发次数一致。原配置/profile 恢复排空 `bfe20c26-8db8-4c9d-9671-c2d149604702`、独立 Helper 原失败回执 `a2b6710c-746b-4033-a683-f2b6ed970226` 与真实中断补偿 `32bd2440-6fc3-4f31-97c0-3d5ee95ca8dc` 均复验。已有受管安装、移除回执沿用原台账。首轮 Linux 状态夹具注入的凭据替身拒绝控制器读取，改由生产凭据存储读取夹具预置的加密凭据。专属 cgroup 清理，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-graph-final-live.log`，归档 SHA-256 为 `26e3a6df41adfc20eeeda8f6d6147fef4c612eeb5382395b7ef770b18e2f83f4`。

原生 Windows 启动就绪循环、整体管理图的动态休眠与生产内存测量仍待验收。Proxy 保持 resident。

### MB-04 Proxy 管理面的配对驻留基线

冻结产品提交 `b3ceadcd5f0c9ed3249ce0a454c6de543fea06a9`，在同一 Linux 测试机的私有挂载、网络及 PID 命名空间运行完整 Release Server。非 root Server 使用独立生产 Helper 和 PAM。每轮先启用、再停用 Proxy 管理面，其他管理模块关闭。每次启动使用新内容根，Core 保留原安全门禁。六个测量进程均正常退出，宿主 Nginx、Docker、Mihomo、sing-box PID 及代理环境/日志指纹一致。

下表为 Server 进程树 PSS，单位 MiB。35 秒闲时列为启用减停用。读取列为相对前一阶段的变化，再扣除同轮停用对照的变化。每组认证阶段采五点中位数。

| 轮次 | 35 秒闲时差 | 设置增量 | 概览与运行时增量 | 档案与订阅增量 |
| --- | ---: | ---: | ---: | ---: |
| 1 | 4.58 | 1.88 | 6.79 | 0.84 |
| 2 | 3.10 | 1.64 | 7.62 | 0.66 |
| 3 | 5.79 | 0.21 | 7.49 | 0.81 |

概览与运行时组在三轮中增量最大，下一项优先拆开概览组合读取、控制器健康查询和运行时观测，定位各自的执行依赖。固定顺序会累积 JIT、路由、SQLite 和 HTTP 缓存，现有数据只能定位请求组。此轮使用空私有状态，未启动 Mihomo，未涵盖活动连接、非空档案或订阅转换。专属 Helper 未计入 Server 树。静态启停差用于候选比较，Proxy 继续 resident，动态释放及生产内存收益尚未测量。

复现和口径见[测量方案](RelaxKonOS.ServerModuleBaseline.Protocol.md#固定输入与运行清单)。日志为 `.artifacts/mb04-proxy-measure-final-live.log`。产品与采样包 SHA-256 为 `1033667d69d1bd0a5dc55b8adc83d97d7c4823979245764367e0e7557fedd854`，原始样本包 `.artifacts/mb04-proxy-measure-raw.tar.gz` 的 SHA-256 为 `8c546a81f2340cb28f8da0540d560414e98750258f512c7b572634998fc72c02`，配对摘要 `.artifacts/mb04-proxy-measure-summary.json` 的 SHA-256 为 `33776e04b576ce3fc4c40984b657e366b72dfcb00d1280b2b9d257eb655353b6`。首轮因测试包目录缺少 Python 导入所需的读取权限在采样前退出，修正后完整复跑。

### MB-04 Proxy 概览与启停执行边界

概览与启停分成独立请求和后台执行图。启停协调器只依赖运行时状态和固定特权动作。概览通过 SQLite 直接查询一个活动档案，连接数复用详情的解析与记录筛选，最多统计前 2,000 条记录中的有效连接 ID。读取仍使用原请求取消、控制器认证和 Core 恢复状态。该阶段的完整缓冲由下节流式快照路径替代。

Linux Release 私有 systemd 173 项通过。生产概览服务在真实 Mihomo HTTP 代理连接等待源站响应期间读取同一活动档案，计数与连接详情一致。源站交付响应后，真实转发完成，配置派发次数未增加。停机排空原操作 `fdb15096-a0b8-495a-9c18-8b2b6a078a9b`、完整 Server 的独立 Helper 原失败回执 `8f271099-5dde-4e9e-a2c3-1f40acbc98a0` 和原中断补偿 `a9dc3553-2757-47ed-8d8c-f660add07521` 同批通过。宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-overview-live.log`，归档 SHA-256 为 `105f4018620b9a1b3f70e66a43109aef2ba115d5a78460658613d7a0492cd654`。

Windows Release 控制器、宿主档案库、配置、Core Proxy 安全和模块边界回归通过。旧档案库夹具仍预期迁移 16，已对齐现有迁移 17。活动连接专项直接调用生产概览服务，完整认证 HTTP 联验与改动后同口径驻留复测仍开放。Proxy 保持 resident。

### MB-04 Mihomo 连接快照的流式读取

概览与详情共用一个增量 JSON 读取器。读取器保留当前待解析片段，并在需要映射时只解析当前连接记录。概览保留计数，详情最多保留 2,000 条投影。请求使用原控制器凭据和取消信号，配置的控制器期限覆盖响应体。待解析片段和已投影的单条记录上限为 256 KiB，总响应上限为 32 MiB。超过投影数量后仍验证原 JSON 至响应结束，截断、超限及重复连接字段不返回部分成功。

Windows Release 控制器专项覆盖分片、记录上限、截断、超长记录、重复字段、响应体超时和请求取消，模块边界回归通过。Linux Release 专属 systemd 的 173 项实测通过，真实持有的代理连接在流式计数与详情中的结果一致，源站交付后继续转发。原发布排空 `c1958e4c-7637-4520-8130-ca4f1c06d83a`、独立 Helper 原失败回执 `f7424d8f-25af-49e4-ad6c-8302e714bf55` 和原中断补偿 `60f92769-7b13-4e96-af50-102e0d4e5988` 同批通过，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-stream-live.log`，归档 SHA-256 为 `7be3fb280a983e91fd9513bc1868ecc3ab20d96a177bc0d5541ecc030c808b26`。

本阶段实机路径直接驱动生产概览和控制器。完整认证 HTTP 见下节，真实高连接数配对测量和动态休眠继续开放。片段上限为读入边界，本阶段未测量生产 PSS。

### MB-04 完整认证 Server 的 Mihomo 活动连接

私有 systemd 夹具在专属 Mihomo 代理已经转发请求后暂缓源站响应。完整非 root Server 通过生产 PAM/JWT 读取概览与连接详情，匿名请求返回 401。管理 Server 被强制终止并重新认证后，两个读接口继续报告同一个真实连接。夹具核对 Mihomo PID、系统代理所有权日志与 sing-box 安装修订，然后释放源站响应，验证原连接完成转发。

最终 Linux Release 同批通过原入站发布回执 `431c7040-91cd-44af-b062-dfbae93210c4`、原中断补偿 `095d574d-84d1-4b71-b9a6-b759ede28f5d` 和 Helper 重启后的原到期回执 `93a01e49-81f6-4cef-a095-0a9b2d27d17d`。TUN 路由恢复、系统代理与真实认证转发也通过。专属 cgroup 清理完成，宿主共享服务和代理文件指纹一致。日志为 `.artifacts/mb04-auth-overview-final-live.log`，归档 SHA-256 为 `411d0d0178654114a9c5e0f7fbff63c747a60ab0fedabc47c6315ec47f999049`。

首轮测试客户端未使用仓库的 JSON 枚举选项，在解析已收到的概览时失败。改用正式客户端选项后全组通过。Windows Release 注册 23 项与模块边界回归通过。高连接数配对测量见下节。

### MB-04 Mihomo 512 条真实连接的配对测量

专属 Linux systemd 中保持同一 Mihomo 和 512 条已抵达源站的代理连接。缓冲版和流式版各启动三次非 root Server，经真实 PAM 访问概览和连接详情。每次都验证原连接 ID 集合及 sing-box 修订。第二对交换顺序，六次结束后释放源站响应，512 条均通过原连接完成转发。

下表 PSS 单位为 MiB。增量为同一运行的观测峰值减去认证预热后采样点。延迟为 24 组概览加详情顺序读取的客户端中位数，单位 ms。

| 轮次 | 缓冲 PSS 起点 / 增量 | 流式 PSS 起点 / 增量 | 缓冲 / 流式延迟 |
| --- | ---: | ---: | ---: |
| 1 | 180.51 / 15.29 | 176.20 / 2.91 | 64.40 / 47.11 |
| 2 | 178.56 / 11.19 | 176.42 / 3.41 | 44.29 / 49.32 |
| 3 | 179.66 / 11.02 | 176.96 / 3.62 | 38.64 / 45.84 |

在该负载下，流式版的 Server PSS 增量每轮低 7.40–12.39 MiB，私有驻留增量同向。两组的请求延迟有重叠，尚无一致延迟改善。六轮共用私有状态和运行时，Server PSS 不包含 Mihomo、Helper 或压测客户端。进程样本反映此隔离负载，动态休眠和生产内存验收保持开放。

缓冲制品含 `6368616a` 的产品改动，DLL SHA-256 为 `9408eff90603893466102cb3b86e9f4aa12ef0c3c91e200650f9625eb4237d57`。流式制品含 `b6da7423` 的产品改动，DLL SHA-256 为 `89a9327aad197658a2cb8d8d5589c7bc0230cd8791a50075f9b6a2c276ec00f4`。两者沿用原验收前构建，程序集信息版本记录各自父提交。原始逐点数据和单组时延见 `.artifacts/mb04-proxy-connection-bench-live.log`，归档 SHA-256 为 `1c8d8a9a814820c4904332f1833bcc2edeade457ce7fbaffa5b9604ce3b05dfd`。

同批原发布 `59540474-8d65-4793-871a-98732e4a3195`、原补偿 `17fc8f80-7bd6-4362-9275-0f56378b3724` 和到期回执 `73157e88-1a6a-4289-a747-a83b9b77f0f4` 通过。TUN 与系统代理恢复同批验证，宿主共享服务及代理文件指纹一致，专属 cgroup 已清理。

### MB-04 Mihomo 快照解析代码的闲时释放

连接快照 JSON 解析移入现有 Proxy 独立程序集，跨边界使用框架缓冲、取消令牌和同步投影回调。Host 保留控制器认证、真实响应流、管道、返回 DTO 和错误映射。一次读取持有解析代次直到响应体和异步清理结束，并发读取复用同一代。最后一次归还后闲置 30 秒可卸载，再次读取创建新代。活跃及待回收代次合计上限为四个。旧代尚未回收时保留最后一个可用代次，继续接受读取并定期复核。生产路径不强制 GC。

此边界只回收解析代码。Core 系统代理、TUN、发布恢复及授权到期保留原持久责任。Proxy 整体保持 resident。

Windows Release 控制器专项验证并发读共享代次、实际异步清理阻止闲时卸载、旧代可回收及预算满时继续服务。Core Proxy 和模块边界回归通过，程序集依赖守卫 16 项通过。Linux Release 私有 systemd 最终 175 项通过。生产概览读取真实 Mihomo 连接后自然等待 30 秒卸载，测试 GC 确认旧 ALC 可回收。Mihomo 持续转发，后续真实连接唤醒新代，原配置未重发。

同批原发布排空 `4492521f-9520-4f11-a1a9-b3503bda6118`、独立 Helper 原失败回执 `0cb059c0-7a5d-4328-b2d2-deee3960ed8f` 与原中断补偿 `87b33f2b-d44e-4649-ad3b-48325d470b07` 通过。专属 cgroup 已清理，宿主共享服务和代理文件指纹一致。日志为 `.artifacts/mb04-proxy-parser-alc-final-live.log`，归档 SHA-256 为 `a678895dec4489408d566301e0247e625da9c823ce845c7d81a4f364672d8e19`。

完整 PAM/JWT Server 随后在 Linux 私有 systemd 复验。杀掉并重启 Server 后，通过认证 HTTP 读取同一条真实 Mihomo 连接的概览和详情，匿名请求仍返回 401。在重启后的同一 Server 进程中停止读取，测试启动钩子观察原解析代次的卸载事件。再次读取时观察不同代次加载，原连接 ID、Mihomo PID、发布修订和系统代理所有权日志均保持一致，原 HTTP 转发成功收到上游正文。启动钩子只记录加载与卸载事件，该路径不强制 GC。弱引用回收由上一轮专项验证。整个管理面休眠和生产内存仍待验收。

Linux 同批复验原发布 `c3736273-43a5-46a1-bab1-edba453528e4`、原补偿 `f7b20917-f4d9-4071-ac9c-6e38fa48ac79`、到期回执 `037a4904-8444-4f44-970b-5caac8a71fe8` 及 TUN 原操作 `3ea56d53-b983-4ca8-90a0-5a5f456319ec`。专属 cgroup 已清理，宿主共享服务与代理文件指纹一致。日志为 `.artifacts/mb04-auth-parser-idle-live.log`，归档 SHA-256 为 `6cb6959dc0a705a85afabfd1150f75408bd9970883d949bf11977326e869536c`。Windows Release 注册 23 项和模块里程碑回归在沙箱外通过。Docker 跨进程恢复读回原 `cf59a730-2f4d-448e-826c-e52e9e155ea6` 的 Interrupted 回执，副作用计数为一。首次沙箱运行遇到 TUN 测试控制器等待超时及证书台账恢复断言失败。

### MB-04 Mihomo 概览的单次观测

概览直接使用当前请求的控制器，省去完整管理引擎注册表。运行时在原所有权门禁内读取安装事实，若需确认监听状态，就复用同一请求的控制器观测。健康与概览使用同一份 Core TUN 恢复状态。观测只在当前调用有效，独立运行时接口继续即时探测。真实 401 保留原本的监听与认证区分，未核验的受管状态仍沿用原未知状态。该改动只调整读取执行图，Proxy 继续 resident。

Linux 私有 systemd 中的完整认证 Server Release 验证了概览与独立运行时接口一致。Server 重启及随后解析器闲时卸载与重载期间，同一 Mihomo 连接继续转发。原发布 `f6bd88d6-a0a8-40ca-9389-c361f09b391b`、原补偿 `368512f1-2501-4618-a8c5-0c9686a6d838`、到期回执 `d7176156-bcad-4029-88da-366a7034159e` 和原 TUN 操作 `58bdc3eb-86cb-4ec2-a484-d679f6adc742` 保持各自原回执及安全责任。专属 cgroup 已清理，宿主共享运行服务及系统代理文件保持原状态。日志为 `.artifacts/mb04-overview-observation-auth-final-live.log`，归档 SHA-256 为 `c04d37752f1a6e1d8b8ac1335190b8d7a9a3083463a1b0e4ef5faba27fe58cbb`。

Windows Release 的控制器、Core 安全、运行时注册 24 项及模块边界专项通过。MB-05 跨进程回归从原 operationId `b5e6d001-d454-451e-8271-e09a51e74db6` 读回 Interrupted，受控副作用计数为一。证书 HTTP 原回执排空 `adc9d7ff-3e0c-41a7-a91c-8a4899999282` 通过。

Linux 真实受管 Mihomo 在私有 systemd 中通过 177 项。连续两次独立状态查询发出两次 `/version`。有活动连接的概览只发出一次 `/version`、读取一次 Core 恢复状态，解析器唤醒后的请求重新采样。给夹具内控制器客户端使用临时错误凭据时，真实 Mihomo 返回一次 401，运行时仍为 Running，管理健康报告认证失败，代理转发及发布保持原状态。原发布排空 `13da3374-a827-4bc4-953b-810a43e3b68b`、原失败回执 `f0d32f11-fbca-4501-b13e-7cd0c563c2d8` 及原中断补偿 `57c17fd1-15ad-47b8-8fff-b05598a01e0b` 同批通过。日志为 `.artifacts/mb04-overview-observation-native-disk.log`，归档 SHA-256 为 `f93fb8366b7f432c047c045a186d8dd0249efb63bf0c3a8c9f2e70b0d20c13a7`。

首轮外层脚本混用了两套验收的通过标记。拆开后，受管专项先遇到临时文件系统上的归档前置检查失败，随后遇到长临时路径超过 Unix 套接字限制。只在私有挂载命名空间内为 `/tmp` 绑定独立磁盘目录后复测通过。本阶段只确认请求执行图及网络安全连续性。下一轮驻留复测应使用已核验的受管 Mihomo 和相同活跃连接负载。Proxy 整体动态休眠与生产 PSS 验收继续开放。

### MB-04 受管 Mihomo 概览的配对驻留测量

同一私有 Linux systemd 内复用受管 Mihomo PID `9842`、独立生产 Helper、PAM 和 512 条已到达源站的代理连接。基线和候选各启动三次非 root Release Server，第二对反转顺序。每次预热后读取 24 组概览及详情，随后闲置 35 秒采五点并再次核对连接。各次概览均返回 `Managed / Running / IntegrityVerified`。全部原连接收到源站响应，受管修订、发布身份及原 `16ea012a-0af1-456d-8403-5e08a2e34374` Interrupted 回执保持一致。测量期间 Helper 未收到配置或补偿请求。真实受管套件 177 项通过，专属 cgroup 已清理，宿主共享服务及代理文件指纹一致。

PSS 为 Server PID 的 MiB 值。峰值增量为请求窗口采样峰值减去预热单点，闲置值为 35 秒后五点中位数。延迟为每组概览与详情的客户端中位数，单位 ms。

| 轮次 | 基线 / 候选 PSS 峰值增量 | 基线 / 候选 35 秒 PSS | 基线 / 候选延迟 |
| --- | ---: | ---: | ---: |
| 1 | 22.31 / 20.58 | 196.77 / 194.82 | 322.87 / 320.18 |
| 2 | 21.29 / 21.03 | 195.13 / 194.94 | 307.32 / 326.64 |
| 3 | 16.86 / 15.22 | 195.59 / 190.60 | 314.22 / 310.05 |

候选的闲置 PSS 在三对中低 0.19–4.99 MiB，峰值增量低 0.26–1.73 MiB。差值波动较大，延迟未一致改善。采样范围限管理 Server PID，Helper、Mihomo 和客户端均不计入。未强制 GC。该结果只描述隔离负载，Proxy 整体继续 resident，生产环境与动态休眠未验收。下一项沿概览与详情 HTTP 的请求边界验证从控制器读取到响应、请求清理的工作责任，再考虑扩大可回收执行范围。

基线源码为 `26b050cd`，候选为 `11daf211`。使用各次提交前已发布的测试制品，程序集信息版本分别记录其父提交。Server DLL SHA-256 分别为 `12c8fc0a827f94acad0b3604b576d2f60aee8f8a96fd98ccdce514091803ae0b` 和 `558124c4171f8bcc1a7d683b896cea1f8045fc4dbf7fff46b117e6a2a7d38bea`。原始样本见 `.artifacts/mb04-managed-overview-paired-live.log`，归档 SHA-256 为 `16947bfd658254594498e8b50fb09bd8f6c2fee05818ac2f95254d2857023d1d`，配对摘要为 `.artifacts/mb04-managed-overview-paired-summary.json`。Windows Release 注册 24 项和模块边界回归通过，Docker 跨进程夹具按原 ID `e693812c-5443-4fd4-808b-0175a930a748` 返回 Interrupted，副作用计数为一。

### MB-04 Mihomo 概览和连接详情的 HTTP 工作责任

两个认证 GET 在解析读取服务前从原驻留协调器取得工作租约，按请求及宿主停止信号取消控制器操作。租约覆盖实际读取、响应写入及完成和独立读取作用域的异步释放。客户端断开只发出取消信号，清理确认后归还租约；清理失败保留租约并让原协调器记录恢复阻塞。只读查询没有待提交的操作台账，Core 继续负责系统代理、TUN、授权到期和发布恢复。

Windows Release 使用实际 Kestrel 和驻留协调器。可控控制器传输分别挂起读取和响应启动，断线后等待实际任务结束及异步 scope 释放。匿名请求未创建读取图。另注入清理失败，检查原工作计数和恢复阻塞仍在。模块边界同批通过，MB-05 跨进程夹具按原 ID `b8a51393-3f2a-4f61-98fd-720208df2137` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-observation-lease-final-win.log`。

Linux 私有 systemd 下，完整 PAM/JWT Server 经回环中继读取真实 Mihomo。中继收到包含原活跃连接 ID 的原生详情后暂缓响应，模块目录显示一份活动租约。客户端实际断线后，中继观察到控制器请求取消，模块目录最终回到零。认证重读仍观察到同一连接和运行 PID，发布修订一致，原连接完成真实转发。同批概览、Server 重启及解析代次卸载与唤醒通过。受控响应写入与异步清理故障由 Windows 专项单独验证。

原入站发布 `0ea3e69f-92f3-46ee-a796-bb21744e3bf3`、补偿 `3a661d97-e365-4279-b762-1f6b70f1a998`、授权到期 `736faad2-3208-4e6f-9ee7-5b513e987a3e` 和 TUN 恢复 `f902de54-8e11-4401-819e-77a682262402` 同批通过。专属 cgroup 清理完成，宿主共享服务及代理文件指纹一致。日志为 `.artifacts/mb04-observation-lease-final-live.log`，归档 SHA-256 为 `7fe39a883b528bb9ae030c9bf7a1a8bdfef6c61744c67a9aadb08512f836f52c`。Windows Core 安全及控制器专项也通过。本轮未扩大可回收代码范围，Proxy 保持 resident，整模块动态休眠及生产 PSS 仍开放。

### MB-04 Mihomo 连接计数与详情投影随代次释放

连接记录的 ID 筛选、计数、端点格式化和详情投影迁入已有可回收 Proxy 执行程序集。计数请求只读取计数，详情请求从共享值构建原协议 DTO。JSON 记录在代次内解析并释放。Host 仍负责控制器鉴权、流和管道的完成、错误映射与 HTTP 工作租约。投影代次一直持有到流清理结束，现有 30 秒门禁和代次预算继续适用。

Windows Release 控制器专项验证分片、上限、坏数据、取消及超时，也核对 IPv6 源地址、主机名、规则、链和开始时间的投影。Host 保留共享投影值后旧代次仍可回收，新计数唤醒新代次，满预算继续使用当前代次。Windows 模块边界回归通过，MB-05 跨进程按原 ID `4d1e4b9f-e502-4c13-bfef-6a7a32a3707d` 返回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-projection-win.log` 和 `.artifacts/mb04-projection-boundaries-win.log`。

Proxy 程序集边界守卫通过 16 项。完整 PAM/JWT Server 在 Linux 私有 systemd 中重启后继续读取同一真实 Mihomo 连接，解析代次卸载与唤醒时原连接仍可转发。截留真实控制器响应并取消请求后，原租约正常归还。原发布 `9dfa7fdd-11f4-4ae9-8df0-2691406377c1`、补偿 `01a0d9f9-c3c6-4c91-a5ee-5730b9f04eda`、授权到期 `c0fd364e-f038-44b4-969b-bc3faa05f69b` 和 TUN 恢复 `b1afdb4d-fc42-450e-a0cc-f89edd2b0448` 同批通过。专属 cgroup 清理完成，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-projection-auth-live.log`，归档 SHA-256 为 `d438c52e688dda5c20a784799e5859d25724f6a9de69481618319f1cbafe28c2`。

真实受管 Mihomo 套件通过 177 项。Host 保留真实详情 DTO，代次自然闲置卸载后通过测试 GC 与弱引用确认回收。之后的原生转发及新代次读取成功。原发布排空 `8203808c-3e16-42f2-b4fa-8a612542ebb9`、失败回执 `642e94c0-ccfe-4d37-8118-d2715c17192c` 及原中断补偿 `29449349-a407-4e4f-972e-1a9c7f69ebc2` 同批通过，补偿一次且未重发配置。专属 cgroup 清理完成，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-projection-native-live.log`，归档 SHA-256 为 `47cae6ce81f171ad657aecf0509f772b7ce49d777824ee51177b19cace89085a`。

本轮证据覆盖读取代码回收与网络连续性，整模块动态休眠和生产内存仍开放。下一项验证计数路径能否省去逐条详情 JSON 对象，同时保留完整快照验证，再用同一真实连接负载配对测量。

### MB-04 Mihomo 概览按 JSON token 计数

概览计数在现有可回收代次中扫描 JSON token，只解码每条记录最后一个顶层 `id`。详情继续使用原记录投影。计数仍检查完整快照，跨片段重读时只在记录完整且合规后累加。保持 2,000 条计数范围、记录与响应大小上限，以及取消、超时和错误映射。Windows Release 控制器专项验证跨片段、重复和转义字段、嵌套与数组 ID、大小限制及截断。模块边界回归通过，MB-05 跨进程原 `63940d23-7544-4e4d-9308-3aa380608732` Interrupted 回执的受控副作用计数为一。

本地 Release 分配探针预热 200 次后，对 512 条记录的固定内存快照同步计数 1,000 次。线程托管分配由每快照 78,096 字节降至 24,848 字节。该数据只覆盖受控解析调用。日志为 `.artifacts/mb04-token-count-alloc.log`。

Linux 私有 systemd 复用同一受管 Mihomo PID `9841`、Helper、私有台账及 512 条真实连接，执行六轮完整 PAM/JWT Server 测量。两组发布树逐文件比对，只有 Proxy 执行 DLL 不同。基线及候选 Proxy DLL SHA-256 分别为 `d112f66fc955462456dadc6bf7006c358991c425cc333131035b3aaf78f5f4f9`、`5f4d714f506e974a4d5f4412dd2c8739b97abeddeddcf4f9cf7ad6c77115e251`。Server DLL 共用 `6f7e08f01eaee863aff6152f35fb0b0d41bd4eb509bd9f4247e3321d868c2108`。

| 轮次 | 基线 / 候选 PSS 峰值增量（MiB） | 基线 / 候选 35 秒 PSS（MiB） | 基线 / 候选延迟（ms） |
| --- | ---: | ---: | ---: |
| 1 | 23.92 / 21.15 | 197.42 / 195.54 | 348.83 / 322.75 |
| 2 | 21.08 / 20.95 | 195.37 / 195.11 | 316.66 / 306.39 |
| 3 | 21.21 / 21.53 | 195.03 / 194.98 | 307.63 / 313.14 |

候选 35 秒 PSS 在三对中低 0.05–1.88 MiB，峰值增量和请求延迟均无一致改善。采样只覆盖 Server PID，延迟为概览加详情的顺序请求中位数。全部原连接收到响应，原 `64672698-7947-45e9-aa9b-d08914ea71b2` Interrupted 回执及发布修订保持一致。测量期间无配置或补偿派发。真实受管套件通过 177 项，专属 cgroup 已清理，宿主共享服务及代理文件通过核验。原始日志为 `.artifacts/mb04-token-count-live.log`，归档 SHA-256 为 `f18dd56fde63fab8e5c5374a7cc05055ea89c60eb9662b43c01193c4e8031cc3`，摘要为 `.artifacts/mb04-token-count-summary.json`。

Proxy 继续 resident，整模块动态休眠及生产内存仍开放。下一项复验完整 Server 在真实 Helper 原回执等待期间收到 SIGTERM 时，Core 是否继续承担发布恢复与排空。

### MB-04 完整 Server 在真实 Mihomo 回执等待时排空

私有 Linux systemd 内，测试通过原发布服务提交真实 Mihomo 配置命令。Helper 记录一次派发，原生拒绝后补偿到原配置，应用侧故意丢弃响应并保留原待处理记录。完整非 root Server 启用 Proxy 启动，由原 Core 调度器按原 ID 查询。测试中继只截留该真实 Helper 终态响应，Helper 看到的是私有中继 UID，完整 Server 本身仍为非 root。

SIGTERM 后观察到 Server 关闭监听，进程仍存活。释放回执前，原操作仍待处理，同一 Mihomo PID 保持真实 HTTP 转发。放行原回执后，Server 先确认并持久化清理，再正常退出。重启读取同一份台账，原待处理已清理、修订未推进，Helper 恢复阶段仅查询原回执一次，无配置或补偿派发。该验收使用已有 Core 调度器和原台账。

Windows Release 发布事务 112 项及模块边界回归通过。MB-05 跨进程夹具按原 ID `aa6cb9a7-7559-403e-931a-9963dfe2e1ed` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sigterm-drain-publication-win.log` 与 `.artifacts/mb04-sigterm-drain-boundaries-win.log`。

真实受管套件通过 177 项。完整 Server 排空原 `0630dd48-c886-4e22-820a-acc46fed1786` Failed 回执，原中断补偿 `f17e3bcb-15a0-44df-8813-2bf8e8e87b61` 同批复验通过。专属 cgroup 清理完成，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-sigterm-drain-native-live.log`，归档 SHA-256 为 `f5eee4a33cdc36302ab92add7254d866a57a289c0106003e5d209599f4796f47`。

复用响应中继的完整认证回归通过，入站发布原 `70b314f0-daca-47fa-b8f0-68950f0ff6b4` 及补偿 `69188c47-0bbc-43d3-8874-503e82dec4bb` 未重放。概览重启、闲时唤醒和真实控制器取消通过，宿主指纹一致。日志为 `.artifacts/mb04-sigterm-drain-auth-live.log`，归档 SHA-256 为 `a99bb9167238760ae1b54a763b5c173f05fca1c6d906a286b3f2c0bf67b157e5`。本次验收限配置发布，其他 Core worker 的完整 Server 停机边界及整模块休眠仍开放。下一项验收系统代理 guard 在真实 Helper 已写入代理环境、响应尚未返回时的停机排空。

### MB-04 完整 Server 的系统代理 guard 停机排空

验收使用私有 Linux systemd、网络和可写 `/etc` 覆盖层。将专属代理环境改为竞争值，再启动停用 Proxy 管理面的非 root Server，由 Core guard 调用原 Helper 修正漂移。响应中继截留真实成功回执。夹具同时保持一条已到源站的真实代理连接，并以代理文件与原归属日志的内容、写入时间区分重启核对与再次写入。

Windows Release 系统代理和模块边界回归通过。MB-05 跨进程夹具按原 `06a4b7dc-2bdd-43bd-9dc8-74a766317f17` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-guard-sigterm-proxy-win.log` 和 `.artifacts/mb04-guard-sigterm-boundaries-win.log`。

真实 Helper 原操作 `8b4ae9fc-604f-45c0-b75a-9c8241e5bcd5` 在回复前已修正漂移，保留原归属。SIGTERM 后 Server 关闭监听并继续等待；放行原回执后正常退出。重启的启动核对使用新 ID `8af1379b-3f50-4508-a4b5-abb9f7192626`，原代理文件和归属日志内容、写入时间一致。Mihomo PID、原入站修订均保持，源站响应通过最初的连接交付。测试退出显式恢复私有环境，专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-guard-sigterm-auth-live.log`，归档 SHA-256 为 `cce50caac87aae2084f223915f708fa8ff7b003d65d97fcd5b8c382eace6bce1`。

共用停机测试工具的受管 Mihomo 套件复验通过 177 项，原发布排空回执为 `1fabc0ea-f689-452f-885e-97b6534241fd`。日志为 `.artifacts/mb04-guard-sigterm-native-live.log`，归档 SHA-256 为 `027cb2e33dd22efb95086a6e6a69874a836f40a24594e23ad4ba56deb966318e`。本轮证据限 Linux 原 guard 与发布恢复。Windows 原生停机及整模块休眠仍开放。下一项验证真实 TUN 已撤销但原控制器响应尚未返回时，完整 Server 是否保留原恢复标记直至排空。

### MB-04 完整 Server 的真实 TUN 停机排空

私有 Linux systemd 和网络内，测试工作进程在真实激活后、完成标记提交前终止。停用 Proxy 管理面的完整非 root Server 使用原 Core worker 读取同一标记。回环中继将真实 reload 和后续读取转发至原 Mihomo，截留显示 TUN 已禁用的真实控制器响应。夹具在发送 SIGTERM 前核验设备、IPv4/IPv6 规则、管理路由和原标记。

Windows Release TUN 边界 34 项及模块边界通过。MB-05 跨进程夹具按原 `41612b18-54ab-412e-a743-3f7c0ba8ef09` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-tun-sigterm-win.log` 与 `.artifacts/mb04-tun-sigterm-boundaries-win.log`。

原 TUN 操作 `44a0b476-2e7f-4dd5-98f8-3a55504a7ab9` 在设备已撤销、管理路由及策略规则恢复后仍保留标记。SIGTERM 关闭监听，Server 持续等待原禁用确认。放行响应后，Core 记录原操作，完成标记清理，Server 正常退出。重启后确认没有第二次 reload，Mihomo PID、原入站修订及已到达源站的代理连接保持，原连接收到响应。

同批系统代理 guard 排空原 `06219350-801c-44e7-a8ba-c0d5f084b691`、入站发布恢复 `9f5c1791-a665-448f-9c74-b76ef2fc8cad` 及补偿 `a3adbc1e-2a5e-47d1-a08d-a7a6c56cc4b9` 通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-tun-sigterm-live.log`，归档 SHA-256 为 `7735b1b31fdda665e473272707268afc25eee1fdbae60c59869eee79da9d48aa`。本次验收使用独立直连 Mihomo 配置。已确认受管发布的 TUN 事务、Windows 原生停机及整模块休眠仍开放。下一项用完整 Server 联验入站授权到期在真实 Helper 提交后、原回执仍在途时的 SIGTERM。

### MB-04 完整 Server 的入站授权到期停机排空

私有 Linux systemd 中，先使用已发布授权建立真实 VLESS TLS 连接，再为该授权设置临近的到期时间。停用 Proxy 管理面的完整非 root Server 通过原 Core 持久索引发现到期，真实 Helper 提交撤销。回环中继截留原成功响应，夹具从 Helper 另行读取同一 operationId 的原生回执，同时核对原应用候选和修订。撤销后检查原连接关闭、到期授权被拒绝、保留授权可转发。

Windows Release Core 安全与模块边界回归通过。MB-05 跨进程夹具按原 `7f407d28-1991-4966-89de-df88ce2ea2e3` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-expiry-sigterm-core-win.log` 与 `.artifacts/mb04-expiry-sigterm-boundaries-win.log`。

真实原回执 `b2e22e1a-55e5-45cc-b859-5a38168a0644` 为 Committed。SIGTERM 后 Server 停止监听，仍持有同一待处理操作；保留授权继续转发，原生 PID 仍是撤销提交后的进程。放行原响应后，Server 持久化已发布结果并正常退出。重启核对索引无待处理或到期工作，仍从原 Helper 按 ID 读到完全一致的回执。到期授权继续被拒绝，保留授权可重连，原生修订仅增加一次，Helper 日志确认这一阶段仅一次 apply。

同批系统代理 guard 和 TUN 停机、入站发布原恢复及补偿路径通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。首轮认证 runner 在 150 秒上限到达后未产生终态；延长认证窗口至 210 秒后全套通过，仍要求实际结果与全部标记。失败日志为 `.artifacts/mb04-expiry-sigterm-live.log`，通过日志为 `.artifacts/mb04-expiry-sigterm-final-live.log`，归档 SHA-256 为 `ede13aa743ab9f232db9f10d08c3bb4c005b499a6509d4cfd1b97ca0e6637a67`。

四条 Core 安全路径已有 Linux 完整 Server 停机证据。Proxy 仍 resident，Windows 原生与动态休眠保持开放。下一项核对管理队列的准入、真实收尾和停机期限之间的所有权，保证已受理工作能在原台账完成终态。

### MB-04 Proxy 管理队列的 Host 停机所有权

原 `ProxyOperationStore` 仍是唯一管理队列和工作租约持有者。直接等待可由调用方取消，Host 通过 `IHostedService` 停机时关闭准入并等待原任务，包括业务执行、异步作用域清理和终态持久化。Host 等待期限到期时已受理任务仍由原队列排空。已有失败隔离与原台账恢复逻辑继续适用。

Windows Release 中，调用方先取消自己的等待，再用同一已取消令牌启动 Host 停机。任务在异步清理期间保持 Running，清理后 Host 等待原终态。订阅边界 39 项、责任及模块边界回归通过。原 HTTP 夹具补齐现有响应租约注册后通过。MB-05 跨进程按原 `bd57fd89-6c13-4f44-a811-c0aeba8fecd1` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-queue-drain-final-win.log` 与 `.artifacts/mb04-queue-drain-boundaries-win.log`。

隔离 Linux 的完整 PAM/JWT Server 受理入站管理发布 `77bd3fa5-9aef-4653-9d62-734d3361ff46`。真实 Helper 完成后，中继截留响应。HTTP 读取仍显示 Running，SIGTERM 关闭监听后原候选仍待处理，新授权已可真实转发。放行后 Server 持久化终态并正常退出。重启后同键返回原 ID，HTTP Succeeded 回执及原生 Committed 回执一致，原生修订只增加一次，保留及新增授权可转发。真实 SIGTERM 覆盖原响应在途，已过期的停机等待令牌由 Windows 专项单独验证。

首轮 Linux 夹具在阻塞原发布回执时，同键 POST 还需要取得原领域锁，导致该测试调用超时。复跑把同键检查放在重启后并通过。同批原入站恢复、补偿、系统代理 guard、TUN 与自然到期排空均通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。失败日志为 `.artifacts/mb04-queue-drain-live.log`，通过日志为 `.artifacts/mb04-queue-drain-final-live.log`，归档 SHA-256 为 `44e15d1454cd3cfdb15ba24637ff1b3c41bdad3ccf1da9ea9ccdf7b9e8f28e9d`。

Proxy 继续 resident。下一阶段沿原工作租约和台账拆出可释放的管理执行图，再独立验收卸载、唤醒和 Core 安全职责连续性。

### MB-04 订阅响应体的可回收执行边界

Host 按原规则校验来源地址、DNS、系统代理路由、TLS 与响应头。经授权的响应流交给已有可回收订阅代次，代次负责实际字节上限、严格 UTF-8 与转换，向 Host 返回共享值。解码直接使用累积缓冲，省去一份完整字节副本；临时租用的读块归还时清除。流实际异步释放后才请求卸载。原 HTTP 接口、工作队列、审计及领域台账继续沿用，Core 安全职责与网络准入都在 Host。

Windows Release 驻留专项验证流清理未结束时仍保留代次、保留结果时旧代码可回收、100 次转换代次循环及超限、无效 UTF-8 的原错误分类。Host 档案、Core 安全和模块边界回归通过，依赖守卫 16 项通过。MB-05 跨进程按原 `3fd51576-ebc8-48e4-82f5-06e98e11caa4` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sub-body-win.log`、`.artifacts/mb04-sub-body-storage-win.log`、`.artifacts/mb04-sub-body-boundaries-win.log` 与 `.artifacts/mb04-sub-body-boundary-guard.log`。

Linux 使用私有网络中的可控分段 HTTP 源站和生产下载器及连接策略。已见代次加载而源站仍暂停正文时，下载任务和代次均在途。放行后得到原转换格式，观察卸载；第二次读取加载独立代次并得到相同结果，最初的真实 Mihomo 连接完成转发。该下载路径在验收进程内使用生产注册，HTTP 源仅在专属夹具中启用。本轮证据分开记录真实流的卸载事件和本地弱引用回收。

完整认证同批复验 Core 系统代理、TUN 和到期，以及原管理操作 `453002c0-267c-4da0-a48e-51e7864e15ea` 与原到期回执 `324a1e3f-c1c0-4093-8a93-1f1bc94d293f`，各一次原生 apply。专属 cgroup 清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-sub-body-live.log`，归档 SHA-256 为 `c63115ccae7c18baa421d9bbc0d5a341b000cdd98eb4f0ee9aa436841b9aa2f4`。Proxy 继续 resident，生产 PSS 与整模块休眠仍开放。下一步联验真实受管 Mihomo 的完整认证订阅队列，核对转换代次释放与同一领域台账和发布责任的关系。

### MB-04 真实受管 Mihomo 的完整认证订阅队列

隔离 Linux 夹具在非 root 完整 Server 中使用 PAM/JWT 调用原订阅导入与刷新接口。测试源站提供真实分段 HTTP 响应，且仅在专属配置中启用 HTTP 源。导入的是不激活的订阅档案，刷新沿用原 HTTP operationId、工作租约和 Host 数据库。测试启动钩子观察订阅代次的加载与卸载事件。

Windows Release 订阅边界 39 项与模块边界回归通过。MB-05 跨进程按原 `f9ca85a6-a130-47c5-a078-e7ed539ab26f` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sub-queue-win.log` 与 `.artifacts/mb04-sub-queue-boundaries-win.log`。

原刷新 `9dcac25e-dce2-4b52-88c1-82cefcd05cf8` 在源站暂停正文时为 Running，模块目录显示一份活动租约，转换代次保持加载。放行后观察到卸载，Host 数据库中的原档案更新，任务成功且活动租约归零。SIGTERM 正常退出后，重启用同键得到原 ID、完全一致的 HTTP 成功回执及相同档案内容。源站请求数仍为两次，重启未加载新转换代次。

真实受管套件通过 177 项，原发布身份、修订、进程 PID 与转发通过前后核对，订阅刷新期间 Helper 未收到配置或补偿命令。原中断补偿 `ce2dc5ca-f98d-4002-a1f4-20d70d680725` 与完整 Server 发布排空 `9f4c9eff-e4d8-478d-8882-902e7b0bb01a` 同批复验。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-sub-queue-live.log`，归档 SHA-256 为 `015e6681a03a89d8e87c5e7789d3fb1c807214b2ba13c4ac0023ec6f802792b6`。

该路径限未激活档案刷新，Proxy 整体继续 resident。下一步验证活动订阅刷新在真实配置已提交、原回执仍在途时的重启对账，保留原队列 ID 并区分下载与配置副作用。

### MB-04 活动订阅刷新原生提交后的中断边界

活动刷新在原队列中下载并更新档案，受管发布另生成配置操作 ID，订阅更新时间在发布返回后写入。当前两个操作 ID 缺少持久关联，Core 确认原生配置和档案选择后无法据此结束原订阅队列。验收分别观察两个原 ID，区分配置确认和仍未知的队列终态。

Windows Release 订阅边界 39 项及模块边界回归通过。MB-05 跨进程按原 `f3c5e5e5-be97-4df5-90fc-29740233709f` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-active-sub-crash-win.log` 与 `.artifacts/mb04-active-sub-crash-boundaries-win.log`。

Linux 私有 systemd 上先通过完整 PAM/JWT Server 激活测试订阅，再触发活动刷新。真实 Helper 原回执 `36aee130-c235-4269-8df2-630b546e7979` 为 Succeeded 时，中继截留响应并强制终止 Server，原队列 `0e3870d2-dc66-4277-bff9-2c4a7938864e` 仍为 Running。重启由 Core 按原 Helper ID 对账确认配置和原档案，已提交修订保持。原队列用同键返回原 ID 和 Interrupted，新键被拒绝。源站请求数始终为三次，Helper 日志确认激活和刷新各一次配置调用，无补偿或重放。真实转发通过，验收随后用独立操作恢复专属测试配置。

首轮夹具把一次配置误计为原生修订只推进 1，原生事务在多个持久化阶段推进修订。复跑以原命令计数、已提交回执和重启后修订一致为准。真实受管 177 项通过，宿主共享服务及代理文件指纹一致，专属 cgroup 清理完成。日志为 `.artifacts/mb04-active-sub-crash-final-live.log`，归档 SHA-256 为 `5f03ee6a89c9a6402f29f1bc361b89a6c62e5d97e1e20e377da22fad453f2bea`。

配置确认与避免重放已验证。订阅队列缺少能证明整个刷新完成的原生和应用证据，仍保持 Interrupted。下一步把队列身份及订阅更新时间纳入原发布意图，并在原档案提交后按原 ID 对账，缺失证据时继续保留责任。Proxy 整体继续 resident。

### MB-04 活动订阅刷新恢复原队列终态

单条活动订阅刷新使用同一队列与受管 Mihomo 配置 operationId。发布意图冻结订阅、档案、已下载内容对应的配置身份和刷新时间。另一写入覆盖草稿时，该次刷新拒绝发布。确认配置时，现有 SQLite 事务一并提交档案选择、档案修订、订阅时间和原操作标记。原生配置、应用状态和原回执均由现有发布服务核验，原发布权威保存确认或补偿结果。

Core 继续用原调度器处理待发布事务。队列在查原回执、重试原键或准入新操作时，仅以原 operationId 的完成证据补写终态。实际执行仍在清理时不接管回执，补写自身也持有工作租约直至落盘。缺少匹配证据、旧版双 ID 和显式清理失败继续保留中断责任。无法核实的原生结果不重试下载、配置或补偿。批量刷新、未激活刷新、未受管引擎及发布意图写入前中断的回执保持各自原有边界。

Windows Release 通过 45 项 Proxy 责任检查、39 项订阅边界、配置回滚、23 项 SQLite 档案发布和 112 项 Mihomo 发布事务。SQLite 在拒绝原操作标记写入时一并回滚刷新时间和档案修订，重试相同发布只写一次，使用同一 ID 更改冻结时间会被拒绝。MB-05 模块边界跨进程夹具按原 `9e12d3ea-aec2-4815-a69a-0edf8b2a088b` 读回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb04-active-sub-loss/`。

中间版复验未到达 Helper，原因是用源文本比对已应用 GEO 策略的落盘草稿。修正为共用落盘转换，本地 Release 验证等价草稿通过、替换草稿被拒绝。该轮 Linux 超时记为失败，专属 cgroup 清理完成。

最终 Linux Release 使用私有 systemd、真实 Mihomo、生产 Helper 与完整 PAM/JWT Server。原刷新 `71b30f5e-cf5a-4339-ae5c-bf4f60931cc5` 在真实 Helper 已提交、响应在途时强制终止 Server。重启从原键读回同一 ID 和 Succeeded，订阅时间与原发布意图一致，Helper 回执和原生修订保持。原刷新 `3d8e2702-188f-43a9-a79c-7f5dbaba5928` 只截断真实 Unix socket 的回执，Server 继续运行。待核验阶段原回执为 Interrupted，完成时间为空，新键被拒绝。同一个 Server 随后用原证据补写 Succeeded，重复查询及原键均返回原回执。源站共四次请求，Helper 对激活和两次刷新各派发一次配置，恢复无额外配置或补偿命令，真实转发通过。

同批真实 Mihomo systemd 套件通过 177 项，专属 cgroup 已清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-active-sub-loss/live.log`，归档 SHA-256 为 `0d2aaca11a6df1c9a5454b5fcfb0d084a653655afd003d35a399eae3dbb95fd6`。活动刷新补偿的原队列终态映射已实现，该组合尚无独立真实故障验收。Proxy 整体继续 resident，整模块休眠和生产 PSS 保持开放。下一项用真实原生失败验证活动刷新保持原转发、按原回执补写失败且不重放。

### MB-04 活动订阅原生失败与补偿回执

Linux Release 在同一专属设施通过完整 PAM/JWT Server 下载一份含 Mihomo 不支持规则的测试订阅。生产 Helper 返回真实 Failed 回执 `a9eee171-6755-4e44-991a-cc5c2fe529f7` 后，专属 Unix 中继暂扣结果。原队列仍为 Running 并持有工作租约，原配置身份和实际转发已恢复。强制终止 Server 后重启，Core 按原 Helper ID 对账，原队列持久化 Failed / `proxy.config_apply_failed`。原键和原 ID 均返回同一回执，原订阅时间、配置身份、Helper 回执和原生修订保持。

同批复验成功重启与存活 Server 断线。测试源共五次请求，Helper 对激活和三次刷新各派发一次配置，恢复无额外配置或补偿命令。Mihomo systemd 套件通过 177 项，专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-active-sub-compensation/live.log`，归档 SHA-256 为 `2c95db44e293ac3fb4e5e459624199eec6c54ce742a1ee22323ae5426ea7ccb0`。Windows Release 模块边界复跑通过，MB-05 原 `b1ff571b-7191-45ba-a05e-fe17a52bc41f` 的跨进程回执为 Interrupted，受控副作用计数为一。下一步联合验收可回收代次的闲时释放和唤醒、真实转发及 Core 恢复责任。Proxy 整体继续 resident，生产驻留未测。

### MB-04 读取代次闲时卸载与原发布恢复

在同一完整认证 Server 中，专属 Unix 中继暂扣生产 Helper 对原发布的实际查询响应。通过真实 Mihomo 建立到测试源的 HTTP 连接，源站先保留响应。认证详情路由在 Core 持有原 pending 时读取该连接，随后观察解析代次的实际卸载事件。再次查询加载不同代次，读到同一连接 ID，原发布仍 pending，Mihomo PID 保持。Server 收到 SIGTERM 后先关闭监听，继续等原 Helper 响应。放行后原发布收尾并正常退出，原连接收到源站正文。重启核对同一持久回执、配置身份和修订，恢复期间只查询原 Helper 回执一次。

Windows Release 的控制器流和连接专项、31 项 Proxy 驻留专项及模块边界专项通过。MB-05 跨进程夹具按原 `fd100c77-d236-41ac-9987-ef84d6efc4b5` 读回 Interrupted，受控副作用计数为一。首轮 Linux 已通过该联合场景，后续订阅夹具重复读取一次性 PAM 输入导致整套失败，修正为测试进程内复用一次读取的输入。

最终 Linux Release 原发布 `1019960f-134b-4fe7-b479-736f86d89269` 和原连接 `5df3369e-17a5-494e-a80a-d2ca8b634f3b` 联验通过。Helper 恢复阶段无配置或补偿派发，Mihomo systemd 套件共 177 项通过，包括活动订阅的成功、断线和失败恢复。专属 cgroup 清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-parser-recovery/live.log`，归档 SHA-256 为 `36631e5edb98d8568562843908edab26d8f4308c94af341605e2542f19afee0d`。本轮 Linux 直接观测卸载事件和新代次，Proxy 模块整体仍 resident，整模块动态休眠和生产驻留指标保持开放。

### MB-03 Git 构建派发检查点与重启对账

原 Git 构建台账在 queued 时写入尚未派发的检查点。重新校验 builder 的 CLI 运行期间保留未知责任，预检完整退出后重新持久化未派发。调用 BuildKit CLI 前先持久化可能已派发。写盘失败由原工作租约隔离执行代次。恢复仍由同一构建服务在打开台账时处理，保留原 operationId 和原键。只有明确未派发且无 CLI 清理责任的记录才在持久化 interrupted 后解除准入及卸载门禁。已可能派发、预检中断及缺少新检查点的旧记录继续保留责任。

跨进程 Release 夹具向完整 Git 模块的原台账提供排队、未派发运行、已可能派发运行及未知运行四种状态，两次启动独立进程从原入口读取同一回执，验证第二次打开保持台账字节、检查点和原 ID。已证明未派发的路径可卸载实际 Git 执行代次，未知路径继续拒绝新构建和代次释放。该夹具验证持久恢复及门禁。

Windows 与指定 Linux 测试机均通过跨进程及 100 轮实际 Git ALC 回归。Linux 的受控命令执行器验证预检清理被挂起时 Host 等待并保留工作租约，清理结束后持久化同一中断回执，BuildKit 派发和镜像清理均为零。另验证 BuildKit 命令调用前已读到持久派发检查点，实际子进程取消等待退出的专项通过。Windows MB-05 模块边界按原 `3458e5b7-b363-4d4c-97d7-aefa90270fb8` 读回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb03-dispatch-recovery/`，Linux 归档 SHA-256 为 `45df763eee4c2d069f15065dce44b60c08f427ed788509c23712b34104cd4e66`。

同机只读复核 AppArmor 非特权用户命名空间限制为 1，`unshare -Ur true` 因写入 uid_map 被拒绝。主机无 RootlessKit、QEMU 和 KVM 设备。真实 BuildKit 在途结果、远端构建清理和动态回收的联合矩阵仍需允许专属 RootlessKit 用户命名空间的隔离环境及受信 HTTPS 测试仓库。生产内存保持未测。

### MB-05 Nginx 原回执清理确认与租约

原 Server 终态及清理待确认标记先持久化，再按同一 ID 通知 Helper。清理确认丢失时，原后台 owner 在存活 Server 内重试清理，保留工作租约直至确认写盘。停机时将尚未确认的清理交给同一持久标记，重启只重试原清理。停用检查也会读取带清理标记的终态。恢复原生重载结果时，实例执行锁保护到原终态持久化；之后只保留清理工作租约，已核实的同实例操作可继续准入。

Linux 私有 systemd 的最终 Release 验证三个原 ID。原 `41069cb8-6c70-4a8c-b72d-0e7ad7c94721` 的 Helper 已压缩详情但首次清理确认被截断，Server 与 Helper 均存活。暂扣第二次清理时，原终态仍可查且管理端报告活跃租约；放行后待确认标记先落盘，租约随后归零。原 `a1759fe6-af9f-4c97-afda-e112fa5bc382` 在首次清理确认丢失后停止 Server 和 Helper，重启只重试原清理。两条路径均保持原 Succeeded、同键和 HTTPS，各一次真实重载，Helper 拒绝旧 ID 再次重载。另复跑原重载响应丢失 `9053e79e-6151-45c5-b362-870034e38af9`，两进程存活，原回执确认成功。三轮专属 cgroup 清理，宿主 Nginx、Docker PID 和配置指纹一致。

Windows Release 验证实际工作租约覆盖终态后的清理重试，并用受控后续工作确认原生对账完成后释放实例执行锁。MB-05 跨进程按原 `bc26ad61-03d3-4d55-a360-32a69f4d01cf` 读回 Interrupted，受控副作用计数为一。首轮 Linux 因夹具重试事件作用域错误失败，修正后通过。日志为 `.artifacts/mb05-nginx-retirement-final/`，归档 SHA-256 为 `4ea113c4a83357709b27078b65313e704363cddfbee97b3194f70162254b93a9`。整机掉电及防重放目录长期容量仍开放。WebServer 继续 resident，生产内存未测。

### MB-05 WebRoot 挑战内容变化时保留原清理责任

WebRoot 文件摘要不匹配时，原证书操作持久保留 `Running / challenge-cleanup-required`、原订单身份和原挑战记录。存活进程继续保留故障工作责任。重启时读取同一台账，保留内容不匹配的文件及清理阻塞，同时开放原回执、取消状态与原订单的认证只读查询。同键返回原证书或网站发布回执，新证书操作拒绝准入。文件恢复与原摘要一致后，在下次启动中完成原清理并写入原中断回执。远端订单有效性和本地签发、TLS 部署状态继续分别核实。

Linux Release 在私有 Pebble、DNS、Nginx、Docker 和完整认证 Server 上验证发布 `73e1a7e3-4ba7-41da-befa-db788ed9c022` 与证书操作 `2ae08813-0c24-479d-93f9-7b20007b7e04`。CA 已实际签发，响应暂扣期间换入不同字节。放行后原任务保留待清理状态，强制终止 Server。重启仍可查原回执与原 CA `valid`，授权的新证书操作返回 HTTP 409，替换文件保持且可由原 WebRoot 读回。恢复原字节后再启动，原文件被清理并持久化原证书中断回执，测试哨兵保留。旧 TLS 与 Docker 上游保持，CA 一次创建及签发提交，Nginx 零重载。

正常 SIGTERM 对照发布 `c0240e22-2771-427b-9e47-b7f6a386fec7` 同批通过，原订单可查、挑战移除、无额外重载。两轮专属 cgroup 清理，宿主 Nginx、Docker PID 与配置指纹一致。日志为 `.artifacts/mb05-acme-challenge-final/live.log`，归档 SHA-256 为 `aaa08de7ce0c574ed4ff83bc32bd1b3424f3cfd6e815929f485d02deb3522f62`。Windows Release 验证清理阻塞时自动续期扫描保持待处理，同键回执稳定，MB-05 原 `a06c0e90-266d-4a46-9baa-257df0da32eb` 跨进程回执为 Interrupted，受控副作用计数为一。归属阻塞与实际自然日扫描的联合路径尚未实测，Certificates 与 WebServer 保持 resident，生产内存未测。

### MB-05 续期扫描跨挑战归属阻塞恢复

在私有 Pebble、DNS 和 Nginx 上签发短期证书。真实续期挑战响应在途时换入不同内容并终止 Server。重启后生产续期 worker 按原启动周期创建到期扫描，发现清理阻塞后保留扫描未完成及原证书操作。恢复匹配文件后再启动，由同一 worker 接续原扫描并关联原证书回执。测试使用 Pebble 短期证书和原到期回退规则，隔离网络内的 PAM 夹具只承载认证与管理员检查。

Linux Release 验证原扫描 `a0ae423b-b106-480a-b0fe-cca9fdadcf65` 和原续期 `aba20dc2-767d-488e-900a-16cd868920ff`。第一次实际扫描保留空完成时间及零拒绝，旧文件和活动证书指纹保持。恢复原挑战字节后，下一次自然扫描使用同一 ID 和开始时间，关联原 Interrupted 回执。原键和原订单仍可读取，活动证书指纹一致。Pebble 共两张预期订单、一次签发提交，恢复零新增。强制终止首次 Server 后，测试中继向已关闭 socket 交付原响应得到 BrokenPipe，后续真实扫描和 CA 计数均通过。宿主 Nginx、Docker PID 与环境文件指纹保持。日志为 `.artifacts/mb05-natural-renewal-ownership/live.log`，归档 SHA-256 为 `34652357bb4a0846d608c2d5e82d704ded4877a8c7fe902d2e751a0f24e9304e`。Windows Release MB-05 夹具按原 `a1c1ff2d-364f-4d94-869d-fbedabee9988` 返回 Interrupted，受控副作用计数为一。

### MB-04 Mihomo 连接详情读取执行图

依据概览与运行时基线，连接详情路由直接从原工作作用域取得 Mihomo 控制器。该路径无需实例化管理引擎、配置验证器或引擎注册表。工作租约覆盖控制器任务、实际 HTTP 响应和作用域清理，原控制器认证、错误分类及可回收解析代次继续负责各自工作。Windows Release 使用实际 Kestrel，配置构造即失败的引擎注册表来验证连接路由的依赖边界，并覆盖控制器认证错误返回 HTTP 503、客户端断线、响应排空和清理失败。

Linux 私有 systemd 使用同一批 512 条真实连接、三对交错制品和每轮 24 次概览加详情请求。候选的峰值 PSS 在三对中高 0.10、2.27、6.99 MiB，35 秒闲置 PSS 差分别为 −1.65、−3.37、+0.40 MiB，延迟变化方向不一致。采样限管理 Server PID，原 512 条连接均收到源站响应，原回执 `1bb7d583-4ddb-4d0d-a6e1-41459fcd280b`、原生 PID 和发布修订保持，测量期间配置与补偿派发均为零。保留有确定证据的执行边界调整，隔离数据未显示稳定的内存下降。

同批原发布 `ed2602cc-e610-401e-a337-ea9df28d4e01` 在解析代次闲时卸载、唤醒及 Host 排空期间完成恢复。真实 Mihomo systemd 套件通过 177 项，专属 cgroup 清理，宿主共享服务及代理文件指纹一致。Windows Release Core 安全 37 项、驻留和生命周期作用域各 31 项通过，MB-05 跨进程按原 `9576e451-5cfb-4771-9cf7-4404bb69fbac` 返回 Interrupted，受控副作用计数为一。首轮实机夹具编译暴露一个仍调用旧引擎读取方法的测试，更新后完整运行。日志及摘要在 `.artifacts/mb04-connection-read-graph/`，Linux 归档 SHA-256 为 `ff2b275528460a3cba6904d051c14e33b5ed25e123decd1396a55cc1d7e14a8e`。Proxy 整体继续 resident。下一项审计流量与日志读取的工作租约和控制器响应生命周期。

### MB-04 Mihomo 流量与日志读取租约

流量和日志路由复用连接详情的工作租约与实际响应作用域。流量按原顺序采集控制器流量与内存样本。日志保留控制器采样、脱敏、本地诊断合并及条数限制。两条路由从作用域直接取得读取依赖。Windows Release 的 Kestrel 夹具在控制器请求和实际 HTTP 响应两个位置分别注入客户端断线，核对原任务及异步作用域清理结束前租约持续，匿名请求无需构造读取执行图。

Linux 私有 systemd 的完整 Server 在 Core 等待原发布 `0699bc25-8445-49d4-ace6-70052ed23b47` 时取得真实 Mihomo 日志，并保留原流量错误回执。原连接 `e04036b8-a270-4058-9f9c-1a200d042c66` 跨解析代次与 Server 排空收到源站响应。首轮流量成功断言失败，复核实际字段确认 1.19.10 的 `/traffic` 只有速率，1.19.30 同时带累计量。原适配器要求累计量，因此旧版返回 `proxy.controller_response_invalid`。两版 `/connections` 均提供累计量及内存字段，后续按共同数据源设计有界读取。Windows 的受控完整流量样本和日志诊断回退通过，旧版真实流量完整指标仍开放。

最终 Linux 真实 Mihomo 套件通过 177 项，原发布和订阅恢复未重放，专属 cgroup 已清理，宿主共享服务与代理文件指纹一致。Windows Release 模块边界、控制器和 Core 安全专项通过，MB-05 原 `d25a004b-3a7f-42bc-9c3c-ff8ecdab9e97` 回执为 Interrupted，受控副作用计数为一。日志和实机字段探测位于 `.artifacts/mb04-traffic-log-drain/`，Linux 归档 SHA-256 为 `f80e488513d80ffb7910ee646f63237e735ae1d78f4bac6ebee32119e5a0ff9c`。Proxy 继续 resident，生产内存未测。

### MB-04 Mihomo 流量累计量使用有界连接快照

流量路由从两版共有的 `/traffic` 字段读取上传与下载速率，从同一份 `/connections` 快照读取累计上传、累计下载和内存。统计分支复用已有可回收执行代次和流清理，校验快照结构、字段唯一性、非负整数、32 MiB 响应及 256 KiB 连接记录上限。它扫描连接数组而不解码 ID、创建连接详情或构造记录 DOM。原控制器身份校验、HTTP 回执和工作租约覆盖两个请求及作用域清理。流量轮询期间也会使用该解析代次。

Windows Release 控制器专项覆盖分片传输的 2,001 条连接、内存字段位于数组之后，以及字段缺失、重复、负数和响应截断。模块边界、Core 安全 37 项、驻留及生命周期作用域各 31 项通过。Core 安全首轮受默认临时目录的文件移动权限阻塞，切换到工作区内的测试目录后复验。MB-05 跨进程按原 `b2bfeebb-8cd4-4a4e-88c7-b3e6a427043b` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 实机 178 项通过。Mihomo 1.19.10 在完整 Server 等待发布 `eb57a883-ab66-432c-b6e6-7357fe3ee82d` 时取得真实日志和完整流量回执，累计上传 442 字节、下载 365 字节。原连接 `bf9d3468-a732-47af-9846-9d2ab0710c0f` 跨解析代次及 SIGTERM 排空收到源站响应。重启保留原 Failed 回执，无重复发布或补偿。1.19.30 使用同一读取路径取得真实流量，累计上传及下载各 73 字节。两版原生内存样本均为 0，首轮错误地要求正值，复验接受原生非负值并继续要求实际流量。订阅崩溃、响应丢失、补偿同批通过。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

日志位于 `.artifacts/mb04-traffic-statistics/`，Linux 归档 SHA-256 为 `e6d5bcd9e028903e3942db5c5044c06e8bbe2dfe7db219d4c4a221bb7cea31d7`。流量轮询仍需扫描有界快照。Proxy 整体继续 resident，生产 PSS、整模块动态休眠和统计轮询在高连接数下的资源成本仍待验收。下一项优先验证连接详情按需解码，并沿用 512 条真实连接负载配对测量。

### MB-04 Mihomo 连接详情按需解码评估

候选在现有可回收代次内保留所需属性的位置，完整校验记录后解码最后匹配的属性，省去逐记录 JSON DOM。交付保留原有有界详情投影和字段回归。候选源文件、受控测量及原始样本保存在 `.artifacts/mb04-token-details/`。

Windows Release 控制器专项通过。分片回归覆盖重复和转义字段、IPv6 回退、数组字段和最终元数据类型错误。固定 512 条记录的受控分配测量使用相同输入、两组交错和每次 1,000 份快照。旧投影每份分配 524,664 字节，候选每份 213,368 字节。该数据限当前解析线程的托管分配。

Windows Release 模块边界、Core 安全 37 项、驻留及生命周期作用域各 31 项在候选上通过。模块边界首轮在受限环境无法连接 Kestrel 回环端口，开放测试所需的本地回环后通过。保留生产解析器后复跑控制器及模块边界通过，MB-05 跨进程按原 `8ecf44f0-d7a6-4c21-a4d2-78d8a32712c1` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 用同一 Host 制品，只替换 Proxy 解析程序集。三对交错运行各执行 24 次概览加详情请求，保持相同 512 条真实连接。下表为候选减基线，采样仅含管理 Server PID。全部连接收到原源站响应。原回执 `5ab8a083-eb8b-413a-9683-da1617e0b8ee`、原生 PID 和发布修订一致，测量期间配置与补偿派发均为零。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 请求中位延迟差（ms） |
| --- | ---: | ---: | ---: |
| 1 | +0.10 | −0.43 | −0.51 |
| 2 | +3.27 | +1.25 | +13.14 |
| 3 | +0.12 | +0.58 | +1.83 |

候选未达到配对负载的内存目标，继续使用上阶段已验证的生产解析器。候选 Linux 原生套件报告 178 项通过，原发布 `5b369c07-88c6-4529-b085-7c8ae662c183` 跨解析代次与 SIGTERM 排空确认。专属 cgroup 清理，宿主 sing-box 和代理文件指纹一致。外层驱动误将订阅模式的标记用于配对模式，退出码为 1。订阅故障验收沿用上阶段单独套件的证据。配对归档 SHA-256 为 `28397fd000d2928180e424e12385d6694acd7ba7ac20b2ca1295db0165b215b0`。Proxy 整体保持 resident，生产 PSS 与动态休眠仍开放。

### MB-04 Mihomo 高连接数流量轮询基线

隔离 Linux 使用同一个 Release Server 和同一批 512 条真实连接进行三对交错测量。两组都先通过认证概览及详情读取确认原连接。20 秒窗口内，轮询组按桌面端两秒节拍执行十次流量请求，静置组等待相同时间。轮询回执累计量单调，三组请求中位耗时为 1.014–1.015 秒。表中为轮询减静置，PSS 只采管理 Server PID，CPU 为窗口内进程 CPU 时间。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒后 PSS 差（MiB） | Server CPU 差（ms） | Mihomo CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +2.39 | +1.28 | +720 | +40 |
| 2 | +4.69 | +4.28 | +1180 | +30 |
| 3 | +4.49 | +2.55 | +1160 | +30 |

512 条原连接均收到源站响应。原发布回执 `9ef35a38-4eef-46c7-b4cc-88163dd482e1`、原生 PID 和发布修订一致，期间配置和补偿派发均为零。Linux 原生套件通过 178 项，同批按原 `2e9495ee-558b-4b60-9962-d8d6e4e1195b` 验证解析代次切换和 SIGTERM 排空。专属 cgroup 已清理，宿主 sing-box 与代理文件指纹一致。Windows Release 注册及模块边界回归通过，MB-05 跨进程原 `1cc6e310-b3f8-421c-bd16-36c6783ae271` 为 Interrupted，受控副作用计数为一。

复验通过私有 systemd 内设置 `RELAXKONOS_TEST_MIHOMO_TRAFFIC_POLLING=1` 选择轮询负载。原始采样和摘要在 `.artifacts/mb04-traffic-polling/`，Linux 归档 SHA-256 为 `be3aa7ae91f1e1c6720b19fae0e96a1dfbbc6f74d8d1c355500de16bba2bffee`。结果限单认证客户端和隔离负载。多客户端、慢控制器导致的重叠轮询、生产资源及整模块动态休眠继续开放。Proxy 保持 resident。

### MB-05 Docker Engine 离线时的原启动对账

`docker ps` 非零退出时，栈服务读取返回 `503 / docker.unavailable`。启动对账仅在实际观察完成后结束原 Running 操作。Engine 不可达时，由原协调器持有恢复租约和项目互斥，保持原 operationId 与原键可查。原 owner 在独立观察作用域内重试只读查询，异步清理和终态持久化完成后才归还租约。停机等待实际观察结束，仍待核验的操作由原台账交给下一次启动。原 Queued 操作按未派发中断处理。

Linux 私有 Engine 中，完整 Server 经 PAM 登录提交专属容器的真实停止请求。在原操作为 Running 且容器已接到停止信号时终止 Server。原停止结束后关闭专属 daemon，再启动 Server。Engine 离线时服务查询返回 HTTP 503，同键返回原 `3810f922-ce3f-499a-a42e-1e6f54dbf3e8` Running，新键对同项目返回 HTTP 409。启动原 daemon 后，同一 Server 取得实际容器观察并将原操作持久化为 Interrupted。同键与按 ID 查询一致，专属容器保持运行，停止计数未增加。原 daemon 重启和命名卷、专属容器清理同批通过，宿主服务 PID、容器清单及代理文件指纹一致。

首轮夹具缺少私有持久状态目录，完整 Server 在 Proxy 安全启动门禁处终止。复验使用私有 `/var/lib`、`/etc` 覆盖层和独立发布的完整 Server。Windows Release 模块边界回归通过，受控用例验证恢复 worker 在 Engine 离线时停止，下一任 owner 接续同一记录。原 `5be1e27c-7be8-4cf8-a990-c5ea69065f90` 跨进程返回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb05-engine-offline-recovery/`，归档 SHA-256 为 `c38ed10257a4d99545b0b0292d61686872c0e1c4e03547aa848d8e5782610257`。Docker 保持 resident。存活 Server 在原变更完成后丢失 Engine 观察的连续性、整机掉电与独立执行程序集继续开放。

### MB-05 Docker 变更返回后的 Engine 观察中断

变更 CLI 返回后，首次 Engine 观察失败会在原台账保留 Running / Observing 和原项目互斥。协调器结束原变更作用域，继续持有同一个工作租约，并在独立作用域内重试只读观察。原操作进入只读恢复时关闭取消入口。观察作用域清理并写入原 Interrupted 终态后才归还租约。Host 停机时等待在途观察清理，仍待核验的原记录由下一次启动接续。

Windows Release 模块边界回归通过。用例验证原回执和工作租约持续，同一项目的不同键拒绝准入，Engine 恢复后只读终结，原变更调用一次。MB-05 跨进程原 `75eb7cdf-99a6-4437-8fd0-286d669b7080` 返回 Interrupted，受控副作用计数为一。

Linux Release 使用完整 PAM/JWT Server、独立 Docker socket、私有 Engine 和专属 Compose 项目。原容器收到真实停止后，测试屏障暂扣首次 `docker ps` 观察调用。关闭专属 daemon 后放行真实 CLI，命令非零退出。Server 保持同一进程，原 `a8c0b873-c124-42c6-a4a6-d190409873c5` 仍为 Running / Observing，同键返回原回执，列表返回 503，同项目新键返回 409。Engine 重新启动后，同一 Server 以真实观察提交原 Interrupted 回执，原 ID 与同键查询一致。容器显式重新启动后保持运行，停止计数未增加。

同一制品复验 Engine 离线时的完整 Server 启动，原 `917dcfc6-9308-4fe6-88b2-d4c0a8a6a765` 恢复为 Interrupted。专属容器及卷清理完成，宿主服务 PID、运行容器清单和代理文件指纹一致。日志在 `.artifacts/mb05-live-observation-outage/`，归档 SHA-256 为 `3a578140199fe33ee8bda9b2d0c46fe8585e903aceb175b3a7290a45a0356f3d`。本轮覆盖正常返回后的首次观察丢失。用户取消与首次观察失联同时发生、整机掉电及独立执行程序集仍开放。Docker 保持 resident。

### MB-05 Docker 用户取消与观察断线交叠

变更可能已派发时，用户取消先让原 CLI 任务及输出清理结束，再使用 Host 停机信号约束只读观察。Engine 可观测时沿用原取消终态。观察失败时保留原 Running / Observing 回执、诊断、项目互斥及工作租约。Engine 恢复后，同一个 owner 只读观察、清理作用域并以原 operationId 写入 Interrupted。Host 停机可交接仍待观察的原记录。

Windows Release 模块边界回归通过，含在首个观察阻塞时取消、原项目继续受保护、恢复后原变更仍只有一次。MB-05 跨进程原 `5f7f3cc4-878f-4df4-8813-91ca210b750e` 返回 Interrupted，受控副作用计数为一。

Linux Release 使用完整认证 Server 和专属 Engine。原容器真实停止后，夹具挂起第一次观察进程，停止专属 daemon，再通过生产 HTTP 路由取消。Server 返回原 `d754692b-b1c2-4d88-9c9c-18633f8e6728` Running 回执，首次观察进程已回收，随后真实 `docker ps` 非零退出。原回执保持 Running / Observing，诊断显示观察失败，列表返回 503，同项目新键返回 409。专属 Engine 恢复后，同一 Server 完成只读观察并提交原 Interrupted 回执，按 ID 和同键查询一致，容器保持运行，停止计数未增加。

同制品复验了未取消的首次观察断线 `a5aafdfa-7349-413b-993a-809c08247a3d` 和完整 Server 离线启动 `38b69a9a-5cb6-4712-8325-798951830581`。专属项目和卷已清理，宿主服务 PID、容器清单及代理文件指纹一致。日志位于 `.artifacts/mb05-cancelled-observation/`，归档 SHA-256 为 `d016b37fadfcfc9996cd527288cfaedc6b5113f9a1a6e108b029b1a50acaa5b7`。验收限变更返回后的取消窗口。变更 CLI 自身无法证实清理、整机掉电、生产内存和独立执行程序集仍开放。Docker 保持 resident。

### MB-04 桌面 Mihomo 流量轮询排空

桌面概览在进入页面后开始两秒流量轮询，通用工作区刷新不再并行发起同一路径。每个工作区只允许一个流量 HTTP Task。慢请求跨过计时周期时跳过该周期，离开概览取消原调用，重新进入仍等待原 Task 完成。已离开页面的迟到结果不会更新当前显示。Server 继续用原工作租约保护控制器任务、响应与异步清理。

Windows Release 的 Avalonia 无头回归使用实际 Dispatcher 计时和页面 attach/detach，验证慢请求、取消、迟到结果丢弃和重新进入后的下一次请求。首轮夹具只运行待处理任务，未驱动计时器，改为实际 Dispatcher 循环后通过。完整桌面回归通过 105 个页面布局及本专项。模块边界回归通过，MB-05 跨进程原 `09c5a30c-f7e7-4215-a174-7cc946c80930` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 通过真实 Mihomo 连接和完整认证 Server 联验。HTTP 中继收到并暂扣真实 `/traffic` 样本，生产模块状态接口显示一个工作租约。客户端取消后，实际控制器请求中断，清理后状态接口显示零租约，新请求取得样本。原连接 `c8ced245-b35e-4de9-b483-1a7336e7349e` 持续转发，原发布 `0d11a773-00da-4ae9-864f-f76c1cb32a69` 跨 SIGTERM 排空及重启保持原回执，发布和补偿均未重放。Linux 原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

证据位于 `.artifacts/mb04-traffic-polling-cancel/`，归档 SHA-256 为 `fa9489a66edaa9f789958013784bf1a4c1e56d7555f77f29b27c28b3c682adf5`。并发上限按桌面工作区计算，多工作区共享采样、生产内存和整模块动态休眠仍开放。下一项核对同一概览仍使用的运行时、代理组、路由和 DNS 读取，补齐实际任务和响应的工作责任。

### MB-04 Mihomo 运行时与控制面读取责任

运行时、代理组、路由及 DNS 的认证 GET 接入原 Proxy 工作租约。先准入，再在独立作用域解析读取依赖，最后等待实际响应和异步清理。运行时继续由 Core 原管理器读取。代理组、路由及 DNS 直接使用控制器，代理组顺序复用已有配置读取。原接口继续在控制器认证失败时分别返回空代理组、路由 HTTP 503 和带问题码的 DNS 回执。

Windows Release 模块边界和控制器回归通过。实际 Kestrel 夹具逐路由覆盖匿名拒绝、控制器任务阻塞及响应阻塞时断线、异步作用域清理和租约归还。MB-05 跨进程原 `264ca97b-7f91-45ed-873f-87350fa10b22` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 使用完整认证 Server 和真实 Mihomo。暂扣 `/proxies` 和两条 `/configs` 的实际响应，分别取消代理组、路由和 DNS 请求。原发布待确认时，运行时返回公开 `RuntimeStateUnknown`。原发布对账后，在重启进程中暂扣真实 `/version` 并取消运行时请求。每条请求持有一个租约，控制器连接中断、清理后归零，随后新请求成功。原发布 `4b244d12-b158-48bd-974f-1f2b39cee684` 保留原 Failed 回执，原连接 `2bebae61-8023-4e87-b5aa-2dc4d17c5708` 完成真实转发。原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

前两轮夹具分别误等候待确认状态下的 `/version`，以及误断言内部问题码。复验以公开 DTO 核对待确认状态，并把运行时控制器断线放到对账完成后。Windows 的代理组排序、TUN 34 项、Core 安全 37 项、驻留及生命周期作用域各 31 项也通过。日志在 `.artifacts/mb04-control-observation/`，Linux 归档 SHA-256 为 `267021248d17e526f2b3683449434592ef42a6e7d40cc77c8a608fe0dcf28adb`。Proxy 整体保持 resident，读取图配对资源复测和生产内存仍开放。

### MB-04 Mihomo 控制面读取配对测量

基线为 `6f735426`，候选为 `5981a0e0`，使用各自发布的完整 Server 制品。私有 Linux systemd 保持同一受管 Mihomo PID 和同一批 512 条真实连接。三对交错运行先预热，再依序请求运行时、代理组、路由和 DNS 共 24 批，逐次比较 JSON 语义，采集管理 Server PID 的 PSS、CPU 时间和每批延迟。闲置 35 秒后复查原连接集合，原发布回执与发布修订由生产 Helper 和 Server 台账共同核对。通过 `RELAXKONOS_TEST_MIHOMO_CONTROL_READS=1` 与既有配对基线变量选择该负载。

| 轮次 | 峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +0.14 | −0.35 | −8.86 | +470 |
| 2 | +1.69 | +0.57 | +8.09 | +310 |
| 3 | −0.22 | −0.24 | −1.30 | −200 |

差值为候选减基线。两份制品各自编译，含不同二进制摘要；生产源码差异限四条读取路由和代理组投影，测量口径为完整 Release 制品。四条接口逐次返回相同的 JSON 语义，512 条原连接均收到源站响应。原回执 `4074de47-1761-4679-873c-27523ab2b562`、原生 PID 与发布修订一致，配置和补偿派发均为零。各项资源差值方向不一致，保留已验证的工作租约，生产内存与整模块动态休眠继续开放。

同制品复验四路取消和原发布 `1c5776cf-cb18-4031-9277-8454dfa4893a`，Linux 原生套件 178 项通过。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。Windows Release 注册和模块边界回归通过，MB-05 跨进程原 `286c684d-ce4f-457e-ab8b-2cea434d0237` 为 Interrupted，受控副作用计数为一。原始数据、制品摘要和配对结果在 `.artifacts/mb04-control-read-paired/`，Linux 归档 SHA-256 为 `a8ce68a15d95ae48984354b9ced4ff4624dc8b8ee96599ff87b0a1afa10f57dc`。

桌面刷新当前同时请求路由和 DNS，Server 分别读取相同控制器 `/configs`。下一项验证同一配置样本能否提供两项结果、减少请求并保持授权及工作责任。

### MB-04 Mihomo 路由和 DNS 单次采样

桌面刷新改为认证 `GET /api/v1.0/proxy/controller-config`。Server 在一个工作租约内读取并解码一份 Mihomo `/configs`，返回路由和 DNS 两项结果及各自的问题码。路由字段无效时仍返回有效的 DNS 投影，桌面保留原路由显示。合并接口接替原有两条 GET，路由写入继续使用原 PUT。

Linux 私有 systemd 通过完整认证 Server 和真实 Mihomo 核对请求计数。合并读取、取消和随后新请求各触发一次 `/configs`，控制器清理后原租约归零。原发布 `0c93c1a2-5128-4766-b6f1-2a76dd473d37` 跨 SIGTERM 及重启保留原回执，原连接 `aed2c9a6-9fee-4094-9024-21e3ec04c20c` 完成转发，发布与补偿未重放。原生套件通过 178 项，专属 cgroup 清理，宿主 sing-box 和代理文件指纹一致。日志位于 `.artifacts/mb04-single-config/`，归档 SHA-256 为 `0de8c26494f62fb18703c721391f0728d4b956260e3571272d2e1e86f93210bc`。

Windows Release 模块边界、控制器和桌面布局回归通过。Kestrel 夹具覆盖一次采样、认证失败及无效路由字段下的独立 DNS 投影。MB-05 跨进程原 `289f23ad-2b74-410a-bf43-684ddace71f5` 返回 Interrupted，受控副作用计数为一。Android 代理 HTTP、仓储和 ViewModel 共 46 项通过，设备测试源码已编译。Android 首轮编译发现未决路由写入的核对仍调用旧读取，更新后复验确认 DNS 成功不能解除未知路由标记。Android 工作流见[应用文档](../../Client/RelaxKonOS.Client.Android/docs/features/Proxy.md)。

此项验证实际请求数从两次降为一次。Proxy 保持 resident，配对内存及整模块动态休眠仍开放。

### MB-04 Mihomo 配置合并读取配对测量

基线 `5981a0e0` 的桌面负载并发读取路由与 DNS，候选 `0fdbb4ca` 从合并接口读取。两份完整 Linux Release 制品在私有 systemd 中完成三对交错运行，每轮 24 批读取运行时、代理组、路由和 DNS。每批配置 HTTP 请求由两次降为一次，四项 JSON 语义逐批相等。使用 `RELAXKONOS_TEST_MIHOMO_CONTROL_READS=1`、`RELAXKONOS_TEST_MIHOMO_COMPARE_SPLIT_CONFIG=1` 和 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE` 选取旧制品负载。

| 轮次 | 读取期间峰值 PSS 差（MiB） | 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: |
| 1 | +0.39 | −5.61 | −0.83 | +570 |
| 2 | −0.34 | −2.43 | +4.15 | +90 |
| 3 | +1.37 | −1.13 | +12.12 | +300 |

差值为候选减基线。三轮 35 秒 PSS 均较低，读取期间的峰值方向不一，Server CPU 三轮均增加。结果限本次隔离负载，Proxy 整体继续 resident，生产内存和动态休眠开放。原生 PID 9830 与原回执 `ea2bf217-a5a4-4d2a-a045-7f3285587632` 保持，配置及补偿派发均为零，512 条原连接均收到源站响应。

同制品复验 `/configs` 单次请求、取消排空及原发布 `a06ae54c-70b0-4c64-b066-566b702a3e1e` 的 SIGTERM 与重启恢复。Linux 原生套件通过 178 项，专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。归档 SHA-256 为 `9ef3033da678a4a40977d5c82f413d9831e62e00bafda5d5eadac8c22d3bebbe`，原始样本与两份制品摘要见 `.artifacts/mb04-single-config-paired/`。

Windows Release 模块边界与 Mihomo 注册专项通过。MB-05 跨进程按原 `23bf6ea0-6b10-4486-a7f0-393c53236d0a` 返回 Interrupted，受控副作用计数为一。ManagedRuntimes 广跑停在伪造制品布局断言，父提交发布制品的 `runtime-lifecycle-only` 复现同一失败。

### MB-03 专属 HTTPS 源仓库夹具

既有 Linux 测试机在私有 PID、网络与挂载命名空间内启动独立 Engine。只读 Git smart HTTP 后端在私有桥接地址监听 443，使用专用名称 `repo.mb03.test`、本轮临时 CA 和可离线构建的 `scratch` 上下文。名称解析只写入私有 `/etc/hosts` 挂载和专属容器，信任仅通过 Git 进程的 CA 文件提供。夹具从已核验的本地归档导入原 rootless BuildKit 镜像，无需公网或共享 Docker socket。

宿主 Git 与镜像内非 root Git 都在缺少专属 CA 时拒绝连接，分别报告证书签发者不受信和找不到签发证书。配置 CA 后，两端均通过真实 TLS 浅克隆同一个 `8f29460479e3cc33108fa645c89c671eb7bdc684` 提交，容器中确认构建上下文标记。独立 Engine 中已核对删除专属容器和网络。执行前后共享 Docker、Nginx 的状态及 PID、宿主 hosts 与 AppArmor 设置指纹一致。执行脚本为 `Tests/ManagedRuntimes/run-git-build-https-linux.sh`，在隔离命名空间内传入宿主网络与挂载命名空间 ID、本地镜像归档与镜像标签。原始日志见 `.artifacts/mb03-https-live.log`，镜像归档 SHA-256 为 `3b931425a738347c19895990381c4aa102a8e94d9caaa9fa2d167f4b30529f78`。

专属仓库、名称解析及 Git 客户端信任已在现有机器验证。BuildKit daemon 未参与本次传输。联合验收仍需要允许专属 RootlessKit 创建用户命名空间的隔离执行环境，以验证实际 builder 的证书接入及在途取消、断连、构建清理、原回执和 ALC。共享 AppArmor 策略维持现有配置。

### MB-04 同次停机的 Proxy 安全责任与恢复

完整非 root Server 停用 Proxy 管理面，在私有 Linux systemd、网络及宿主代理文件覆盖层中运行。原 Core TUN worker 恢复真实未完成标记，控制器已禁用 TUN 并恢复管理路由和策略规则，真实确认响应暂扣。原到期 worker 同时经 Helper 提交 sing-box 撤销，原回执另按 operationId 读取，主响应暂扣。撤销关闭到期连接并拒绝该凭据，保留授权仍可通过真实 TLS 转发。

在 TUN 持有共享发布锁时写入专属系统代理的竞争值，再发送 SIGTERM。Server 停止监听。先放行到期响应并核对原应用终态、原生回执和调度索引，Server 仍存活并保留原 TUN 标记，guard 未覆盖竞争值。随后放行真实 TUN 确认，原 worker 完成清理。重启使用原系统代理归属记录。若原 guard 已在释放发布锁后提交修复，重启仅核对文件和日志指纹；若竞争值仍在，由启动 guard 修复。到期撤销和 TUN reload 各一次，原 Mihomo 连接贯穿停机与重启。

最终实机复验的到期原回执为 `06a3af0b-905c-4976-a586-881df5da8272`，TUN 原标记为 `5712993f-0771-459e-8eb6-92dc4b41e35f`。guard 在重启后以 `714b8016-a916-4562-a85f-f93957e09900` 修正测试漂移，原归属保留。最终制品未观测到 guard 在重启前提交的交错顺序。同批原入站恢复 `d4f8e6b3-01a1-4146-910e-2941865f5424`、原补偿 `60285cab-6dc6-4aa9-90f3-884fe5a68612` 与管理队列停机 `b359ee89-caf5-4b80-a565-28b77504e09c` 通过，均核对原回执与派发次数。专属 runtime 注册及 cgroup 已清理，宿主 runtime 服务和代理文件指纹一致。

首轮联合夹具在准备非 root Server 后又写入私有台账，Server 启动退出。调整为先写台账再赋予专属状态目录和文件的非 root 所有权，随后两轮实机通过。日志在 `.artifacts/mb04-joint-safety/`，最终 Linux 归档 SHA-256 为 `b81cedb11c2a4ca891da7a80b9c175572954d44fe2ae1d511b6d95c46598c4fc`。Windows Release Core 安全 37 项、TUN 边界 34 项、Proxy Server 和模块边界回归通过，MB-05 跨进程原 `b091593e-78b6-4316-a626-632defedf9e0` 返回 Interrupted，受控副作用计数为一。

联合实测使用独立直连 Mihomo 的 TUN，受管发布下的 TUN 网络事务、整个 Proxy 的动态休眠和生产内存继续开放。下一项核对设置写入的工作租约、真实响应和清理边界，以及现有原发布回执在 HTTP 断线后的恢复。

### MB-04 Proxy 设置写入的工作租约

认证设置 PUT 通过原驻留协调器准入，在独立作用域解析设置执行器。工作租约覆盖实际任务、原审计、HTTP 响应与异步清理，沿用 204 和 400 响应契约。受管设置继续由原发布事务持久保存 operationId 和设置意图，已受理的原 Helper 事务独立完成。直连配置在替换活动文件前检查取消，替换后在原发布锁内完成应用或补偿，guard 仍从同一锁读取最终设置。

Windows Release Kestrel 回归覆盖无写入权限、204 和 400、真实请求断开及实际响应暂扣。工作租约持续到原审计落盘和异步清理。系统代理专项验证直连设置替换后取消时，原写入继续持有共享锁，guard 不会重新应用旧设置。Mihomo 发布事务 112 项通过。MB-05 跨进程原 `1f676f5f-5ab8-470e-94c3-aaaf380637c3` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 由非 root 完整 Server 执行认证设置 PUT。真实 Helper 提交后暂扣成功响应，夹具按原 `5a036973-72c3-4643-a659-4d4392ee38cd` 读取成功回执。断开 HTTP 时仍有一个工作租约，原设置意图待对账。终止 Server 后，已提交的 Mihomo PID 和转发持续。重启从原回执确认同一 operationId，公开设置、原生修订和配置身份一致，configure 一次，recover 零次。专属夹具使用另一个明确操作恢复测试基线，随后 Linux 原生套件通过 178 项。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

首轮实机夹具误把一次原生 configure 的多个检查点计作单次修订增量。诊断轮证实原回执为成功、修订从 105 增到 111、候选继续转发。最终轮核对提交后的实际修订跨重启保持，同时独立统计派发。原始日志见 `.artifacts/mb04-settings-lease/`，最终归档 SHA-256 为 `a16bcbee31d6c319f019367def83a3bb290f5dea0013e3a681bdf2027318a502`。

直连设置取消的验证范围为本地夹具，真实宿主断线和进程崩溃连续性仍开放。Proxy 保持 resident，动态休眠和生产内存仍未验收。下一项推进档案激活与配置应用，核对直接路径替换活动文件后仍使用请求取消令牌的边界，以及受管原档案事务的租约和恢复。

### MB-04 Proxy 档案发布的工作租约

档案激活及配置应用沿用原认证路由和响应，准入后才在独立作用域构造执行器。租约覆盖实际写入、审计、响应和异步清理。直连写入在发布草稿和替换活动 YAML 前检查取消。替换后在原发布锁内完成 reload、健康核对和档案提交，存在原配置备份时沿用原补偿。受管路径继续在原加密发布台账中保存档案意图，使用原 Helper 回执与 SQLite 选择标记接续。

Windows Release Kestrel 验证两条档案入口拒绝只读用户，保留激活的 200 档案响应、配置应用的 204 以及失败的 400。实际请求断开和响应暂扣均保留租约至原审计及异步清理。直连配置夹具在活动 YAML 替换后取消，成功时执行一次 reload 并增加一次档案修订，拒绝时执行一次候选 reload 和一次补偿，原 YAML 与两份档案元数据保持。受管档案发布专项通过 23 项。MB-05 跨进程原 `e6fdc0a4-c01e-47bc-a867-8977c625ef0d` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 使用非 root 完整 Server，经认证配置应用入口写入真实受管 Mihomo。Helper 提交后暂扣原响应，HTTP 断开时仍有一个租约，原档案意图保留，档案保持未激活。终止并重启 Server 后按原 `ccace5ba-700b-4e3f-ba2f-2a1d270f0407` 确认 Helper 回执，档案修订只增加一次，实际路由模式为 Direct，提交后的原生修订、PID 和真实转发保持。configure 一次，recover 零次。同轮设置原 `cf73fb38-e0da-4531-ac12-bf595a64e2ac` 也按原回执恢复。显式测试恢复完成后，Linux 原生套件通过 178 项。专属 cgroup 已清理，宿主 sing-box 与代理文件指纹一致。日志见 `.artifacts/mb04-profile-lease/`，归档 SHA-256 为 `b2e479ea333bdd3891e5e1f5dea11ed125ddffedce31a85c55a1140c46f94783`。

直连取消及补偿的证据来自本地夹具。控制器连接恢复目前不足以证明指定 YAML 已生效，真实直连响应丢失与 Server 崩溃后的配置权威仍开放。Proxy 保持 resident，动态休眠及生产内存未验收。下一项验证直连 reload 回执丢失时的判定和原配置保护，保持同一个执行者与原领域记录。

### MB-04 Mihomo 直连 reload 回执丢失

档案和设置的控制器 reload 要求原 HTTP 成功响应。传输中断或控制器 5xx 返回 `RecoveryRequired`。执行器在原发布锁内原子恢复原磁盘配置，保持原档案与设置元数据，随后返回原有待恢复问题码。此分支没有补发 reload，也不会用 `/version` 的可达性确认原 YAML。明确拒绝时仍沿用原补偿。TUN 保留原安全标记，响应中断后可在有界等待内只读重查实际 TUN 标志，再验证设备及管理路由。

Linux 私有 systemd 使用真实独立 Mihomo 和回环 TCP 中继执行两次专属直连设置请求。第一次在中继转发前断开，控制器仍报告原日志级别。第二次实际收到 Mihomo 成功响应后丢弃回复，控制器报告候选日志级别。两次均返回 `RecoveryRequired`，各发出一次 PUT，原磁盘字节、原设置元数据和 Mihomo PID 保持，未派发系统代理变更。明确的夹具清理操作加载原磁盘配置，原 HTTP 连接完成转发。同批 Core 联合停机的到期 `218a81b5-d015-4ca5-b8f9-014f6f990ed5` 和 TUN `be8bab8f-fed0-4d75-ae42-a0d97c5768f0` 保留原责任，管理队列 `a9935e66-7a9b-4b80-9065-c22099ffea2f` 保留原回执。专属 cgroup 已清理，宿主 runtime 与代理文件指纹一致。主套件归档 SHA-256 为 `f9b0a65ce407d4b76583f195f420e69a0a20602218accbd28ad5a793e55bf3cd`。

Windows Release 控制器、直连档案、系统代理和模块边界专项通过。直连档案夹具核对未知结果时不增加档案修订，原文件保持且只有一次 reload。TUN 边界 35 项通过，包含一次只读查询失败后恢复，以及无额外 reload。MB-05 跨进程原 `0ebd1049-d684-4b55-ab93-9ae549541f82` 返回 Interrupted，受控副作用计数为一。日志见 `.artifacts/mb04-direct-reload-final/`。

独立 Linux 网络命名空间复验真实 Mihomo TUN、自动路由及 DNS。原 `c596587e-0589-4ee2-809b-961c4bcdd464` 在禁用请求未到达时保留标记，原生 reload 为零。随后实际禁用成功但回执丢失，真实设备只撤销一次。管理路由恢复后原 Core worker 清理同一标记，禁用 reload 仍为一次。同批通过真实激活取消和恢复排空，宿主服务 PID 与代理文件指纹一致。专项日志为 `tun-live.log`，归档 SHA-256 为 `ecf3d0f91a1d08179cf40a7421239d261c0678460522907ca9c2c793f7d92de8`。

原生直连 reload 接口未提供可查询的操作回执或完整生效配置摘要。当前实测覆盖存活的直连设置执行器遇到未知结果后的原文件保护。进程在还原前崩溃、后续直连写入的持久恢复门禁，以及真实直连档案响应丢失仍开放。下一阶段在原发布权威内推进持久直连检查点，明确 TUN 紧急恢复的接续权限。Proxy 整体保持 resident，动态休眠及生产内存尚未验收。

### MB-04 Mihomo 直连 reload 持久检查点

直连档案与设置在活动文件替换前，通过原发布锁将操作 ID、原 YAML 与候选摘要写入同一加密发布台账。正常路径在已知结果和应用提交完成后移除检查点。结果未知时保留原检查点，并阻止新直连写入、发布与启动或重启进入同一配置。停止运行时仍可执行。发布索引调度原 ID，Core 恢复只在磁盘匹配候选摘要时还原原文件，并保留检查点，不向控制器发送 reload。独立修改过的文件保持原样。

存在直连检查点时，Core 继续使用已保存设置承担系统代理 guard。TUN 启用关闭准入，紧急禁用仍由原安全标记和执行者接续。它在确认磁盘身份后，使用原 YAML 生成禁用配置，并核对实际 TUN 标志、设备和路由。TUN 标记可按自身结果清理，直连检查点独立保留。停用预览显示原 ID，已停用的 Server 仍可启动 Core 安全工作。

Windows Release 的直连档案、系统代理、TUN 边界 38 项、Core 安全 40 项和模块边界回归通过。验证匹配的原检查点拦住下一次派发，外部改写文件保持原样，Core 启动和 guard 不清除原检查点。Mihomo 注册 24 项、配置发布 112 项、安装发布 88 项和移除发布 92 项通过。MB-05 跨进程原 `32b843a5-601f-4b71-acad-0a45aa09d8e1` 返回 Interrupted，受控副作用计数为一。

独立 Linux 网络命名空间使用真实 Mihomo、TUN 设备和策略路由。直连检查点 `30aa48b6-dd00-4086-b9f4-5b06e1b84f0a` 待确认时，紧急禁用从原 YAML 处理 TUN 标记 `71e78ee2-08df-4da5-a2f3-cd6a7302ff8a`，仅执行一次 reload。设备及策略路由恢复，TUN 标记清理，直连检查点继续保留。宿主服务 PID 和代理文件指纹一致，归档 SHA-256 为 `e5c15e1e35b75df49585a8dfe35aae1e891cd1f0a112b9cb1c3e94c88ca5b8d5`。

Linux 私有 systemd 主套件通过真实直连设置写入。中继先核验请求未转发的分支，再对独立检查点暂扣实际 Mihomo 成功响应，强制终止写入子进程。重新打开台账，Core 按原 `6dba7d99-2d82-45f0-b466-0d33fe7d7cf6` 还原磁盘并保留检查点，后续设置写入被拦截。控制器仍显示候选日志级别，实际 PID 保持，只有一次候选 PUT 到达 Mihomo。明确的夹具清理另行还原运行状态。联合停机的原撤销 `ade94836-d0c8-4d2f-9a1c-326f4d6e280b` 和原 TUN `5a79327d-84ba-4115-85e7-60e043e04a13` 各执行一次，专属 cgroup 已清理。主套件归档 SHA-256 为 `4b456dc56371b49baf92d79d12ccbe68a831dd7e2d5ddd32e3bf8055d05a41af`。

首次并行展开触发测试账户配额。两轮主套件还因原撤销耗时接近或超过 TUN 控制器 30 秒期限，未通过联合屏障。串行执行并先暂扣真实撤销回执，再创建真实 TUN 恢复标记后，联合停机及直连崩溃均通过，生产控制器期限保持原配置。失败和通过日志均在 `.artifacts/mb04-direct-checkpoint-final/`。Windows ManagedRuntimes Release 广跑通过，进程级 CA 覆盖不可用的可信下载探针跳过。

应用级显式核验及结案仍待实现。原生控制器缺少可查询的直连回执；恢复只能证明磁盘处理，无法确认原候选是否已经生效。外部自行重启的运行时仍由外部设施管理。当前实机覆盖真实设置写入进程崩溃及独立真实 TUN，直连档案崩溃和整机掉电仍开放。Proxy 保持 resident，生产内存未验收。

### MB-04 Mihomo 直连具名恢复

运行时恢复查询直接呈现原加密台账中的直连检查点 ID、待恢复状态与专用问题码。Helper 不参与直连查询，响应中的原生阶段与服务观察保持未知。人工重试需要原运行时管理权限和宿主提权，仅接受当前检查点 ID，复用 Core 的发布锁和磁盘恢复。候选摘要匹配时恢复原文件，其他磁盘内容保持。响应始终使用原 ID 及 `RecoveryRequired`，原检查点和调度索引保留。原生紧急停止和关账入口拒绝把该检查点当作 Helper 事务，普通生命周期停止和 Core 网络安全恢复走原入口。

Windows Release 的 ManagedRuntimes 广跑和模块边界回归通过。Mihomo HTTP 54 项核对读权限、提权、错 ID、停止与关账拒绝、候选磁盘还原、外部改写保护及零 Helper 调用。原生紧急停止仍能处理 Server 台账不可读时已存在的 Helper 事务。MB-05 跨进程夹具按原 `c0efdaa8-fc99-4b72-8003-aa600c4de00a` 返回 Interrupted，受控副作用计数为一。可信下载探针因进程级测试 CA 条件跳过。桌面组件中心广跑在 Mihomo 面板之前停于已有 HTTP 503 错误码断言，客户端映射 `component.package_index_unavailable`，夹具期望 `component.state_unavailable`。日志在 `.artifacts/mb04-direct-named/`。

同一 Linux 测试机的私有 systemd 套件通过。真实 Mihomo 应用候选后，中继暂扣成功响应，写入进程被强制终止。重新打开加密台账，Core 还原磁盘。具名恢复再次读取原 `61847f23-969c-4ab1-bd66-6b276343acf1`，返回 `RecoveryRequired` 并保留检查点。真实候选 PUT 仅一次，重试未发出 PUT。运行时仍显示候选日志级别，PID 和流量保持。独立的夹具清理随后还原运行状态。联合到期、TUN 和系统代理恢复通过，专属 cgroup 已清理，宿主服务和代理文件指纹一致。归档 SHA-256 为 `eb9518ede3030cf52035e1fc8350deab9c83fbb4e7c7e759ec7f23fe23d7efd7`。

显式关账需要可归因的原生结果，或有独立检查点及可核验回执的补偿。原控制器的 HTTP 可达性、磁盘哈希和部分生效字段均不足以证明原 reload 结果。下一项先区分已收到原生确认后的应用提交中断，复用原档案 SQLite 标记及设置权威核对提交。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 Mihomo 直连档案提交恢复

直连激活在派发前冻结原档案和目标档案的修订与创建身份。收到本次 reload 的成功响应并通过健康核对后，在同一加密发布台账记录确认，再进入原 SQLite 档案事务。直连和受管激活共用一个 SQLite 写入路径。选中档案、修订、订阅刷新时间及操作标记在同一 SQLite 事务中提交。队列刷新沿用已受理的操作 ID。

Core 与具名重试共用发布锁。收到确认的档案操作在候选摘要仍匹配时接续 SQLite 提交，已提交的原 SQLite 操作标记阻止第二次修改。加密台账持久化原 ID 成功回执后才释放待处理检查点，刷新队列也可从原 ID 的确认记录补齐终态。复读已完成的原 ID 返回同一结果。缺少原生确认、磁盘漂移或档案身份冲突均保留待恢复责任。启动与具名重试均无控制器写入。

Windows Release 通过档案应用与回滚、Core 安全、模块边界和 ManagedRuntimes 广跑。正向检查确认重复读取同一回执不增加修订；外部改写文件时，候选不被覆盖，原档案修订和原调度 ID 保留。MB-05 跨进程原 `10ea1520-7410-4249-9772-0bfb4bd9f36f` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。日志在 `.artifacts/mb04-direct-profile/`。

Linux 私有 systemd 套件使用真实 Mihomo 二进制校验 YAML、发送认证 PUT 并读取实际运行模式。写入进程分别在 SQLite 提交前和提交后被强制终止。Core 重新打开加密台账与 SQLite，在两条路径各确认一次档案修订、一次真实候选 PUT 和零恢复 PUT。原 `9a66df8d-55f1-48dd-9bd9-64af43cd34bc` 和 `8bf6f461-6ad4-4b6b-ad5d-74bab25c8f1f` 均可重复读回成功回执。原 PID、跨中断 HTTP 转发和系统代理归属记录保持。原生响应丢失的对照组继续返回 `RecoveryRequired`。专属 cgroup 已清理，宿主服务和代理文件指纹一致。归档 SHA-256 为 `86c7178292a12edaac0545550d68968657cff806e3e50d1a587cb37bd1f8b319`。

这条路径证明原操作已被控制器确认及其应用提交结果。外部运行时后续变更由外部设施管理。TUN 仍先保障原网络恢复，当安全恢复改变候选摘要时，档案提交留待核对。直连设置尚未进入此确认阶段，设置的系统代理宿主效果仍需要独立核验。Proxy 保持 resident。

### MB-04 Mihomo 直连设置提交恢复

直连设置保持宿主代理策略一致时，在派发前捕获原设置文件摘要和候选内容。收到控制器 reload 和宿主代理操作的成功应答后，将提交意图写入原加密发布台账。Core 与具名重试在原发布锁下核对候选 YAML 和原设置文件身份，使用同一原子设置文件写入。文件已匹配候选时只写原 ID 回执。恢复无控制器 reload 和 Helper 代理写入。候选 YAML 或设置文件出现独立变化时保留待处理责任。

Windows Release 的系统代理、TUN 边界 38 项、Core 安全 40 项、ManagedRuntimes 和模块边界回归通过。独立写入设置文件时，重试返回 `runtime.settings_drift` 并保留原文件和原 ID。夹具恢复原始文件身份后可完成本地提交，宿主调用计数仍为一。MB-05 跨进程原 `05954880-d24c-49a2-a8b8-6c228f17e24a` 返回 Interrupted，受控副作用计数为一。进程级测试 CA 覆盖缺失，可信下载探针跳过。日志在 `.artifacts/mb04-direct-settings/`。

同一 Linux 测试机的私有 systemd 环境使用已启用的真实系统代理。Mihomo 更新日志级别，真实 Helper 返回成功。测试分别在设置文件替换前和替换后终止实际写入进程。Core 重新打开台账后，原 `d4482b4d-eea0-4528-8e8e-003d16accc77` 和 `07156515-9431-469f-aacc-2128c033e60b` 均可重复读回成功回执。两条路径各只有一次候选 reload 和一次真实 Helper 代理操作，恢复阶段两者均为零。已替换的设置文件字节和时间戳保持，运行时 PID、跨中断转发和原宿主代理归属记录保持。专属 cgroup 已清理，共享服务和代理文件指纹一致。归档 SHA-256 为 `d919b0261b1a462e9420254db0b21607ba860ced5e44221cd8724f3534aee12f`。

系统代理 guard 仍依据原持久策略维护宿主归属，提交前后宿主策略一致。TUN 仍按原安全记录独立恢复。宿主代理策略改变、控制器应答未知、以及 Helper 效果未得到成功应答的路径没有获得本地提交证明。下一项为实际改变宿主代理策略的路径提供按原 ID 的宿主效果核对。Proxy 继续 resident，动态休眠与生产内存未验收。

### MB-04 Linux 系统代理原生回执

Linux Helper 的具名代理写入仍在原环境互斥锁内使用已有归属事务和后端。在写入前以原 ID 持久记录请求摘要。完成原生写入和回读后，再保存目标范围、代理键和归属台账的效果摘要。启用核对当前代理键及其归属，禁用核对归属已清理并保留实际恢复结果，包括原事务保留的外部值。回执目录由 Helper 持有，查询只返回原 ID、历史提交和当前匹配两个独立状态。

查询未知 ID 返回空。未确认的已受理 ID 继续待核对。同 ID 改变请求返回冲突。同 ID 重复写入只核对回执及现状，已出现独立修改时返回历史已提交与当前不匹配。查询和重复请求均不写代理键。连续系统代理 guard 仍使用原安全事务和所有权记录。Server 直连设置尚待将策略变化接入具名 Helper 操作和本地提交门禁，端到端恢复暂未交付。

Windows Release 的系统代理专项、ManagedRuntimes 和模块边界回归通过。受控 GNOME 和 KDE 后端核对原 ID 去重、独立修改和禁用恢复。MB-05 跨进程原 `96dae2e0-c3cb-497d-aca1-dc6b72854590` 返回 Interrupted，受控副作用计数为一。设置广跑在未改动的 UI 硬编码颜色检查处停止，可信下载探针缺少进程级测试 CA 覆盖。日志在 `.artifacts/mb04-linux-host-receipt/`。

同一 Linux 测试机以私有挂载和网络启动真实 Helper。非 root 客户端派发原 `630a7065-c07e-4f8b-97c3-464c047397c8` 后断开套接字，真实环境代理改到专属端口。强制终止并重启独立 Helper 后，原回执可读。同 ID 重复派发后，环境文件字节、修改时间及归属台账字节保持。改变请求被拒绝，未知 ID 为空。独立修改后，原回执仍确认历史提交，当前效果报告不匹配。专属禁用 `056b5f58-2379-4d46-a635-1394dbde2945` 保留外部代理值并留下回执。共享环境文件和归属台账指纹一致。实机覆盖 Linux 环境后端。首轮脚本启动参数错误，随后实机发现通用进程内缓存拦截原 ID，修正由持久台账承担这两个动词的防重放并通过 AF_UNIX 回归。归档 SHA-256 为 `4aa881369dc15b60976ecbab55b78cdad7a40516421067903bb7ab4a3d38a5bf`。

### MB-04 Linux 直连宿主策略变更恢复

Linux 直连设置校验代理策略后，持有原发布锁执行控制器 reload。收到控制器确认后，先持久化冻结设置与原 ID，再以该 ID 派发一次 Helper 变更。宿主应答未知时保留候选文件和待处理意图。具名恢复、Core 启动和 guard 共用原发布锁，核对候选文件、原 Helper 回执及当前效果。匹配才提交本地设置并保存原 ID 回执。恢复只查询 Helper，控制器结果缺失和外部文件改写继续待处理。

guard 在自己需要执行安全策略时先尝试原回执核对。原效果仍无法确认时，guard 在接管前持久记录该事实，随后独立执行已保存的安全策略。后续即使宿主值再次与候选相同，原操作也不会据此结案。环境后端继续使用原有的同值跳过规则，GNOME 和 KDE 仍按原流程维护桌面通知。TUN 安全恢复仍使用原标记。

Windows Release 的 Linux 代理协议夹具、系统代理与 guard、TUN 边界、Core 安全、ManagedRuntimes 和模块边界通过。非法 Linux 旁路值在写入检查点前拒绝。受控原效果未知时，独立 guard 写入只执行一次，之后的同 ID 重试仍待恢复。MB-05 跨进程原 `deefdc2e-b7a3-41fc-8571-00728d93a5f7` 返回 Interrupted，受控副作用计数为一。缺少进程级测试 CA 覆盖，可信下载探针跳过。

同一 Linux 测试机私有 systemd 环境两轮通过。最终制品以真实 Mihomo 和 Helper 执行旁路变化，Helper 完成后在本地提交前暂扣应答并强制终止写入进程。原 `36d5198c-1fc7-4931-9813-42939eb0cb47` 在 guard 接续时只查询一次并提交设置，环境文件字节与时间戳保持，额外 guard 写入为零。独立修改 `no_proxy` 的原 `5a0d6688-929d-4575-9e39-e84971906890` 持续返回 `RecoveryRequired`，guard 以独立调用执行一次已保存策略。两条路径各一次候选 Mihomo reload、一次原 Helper 变更，原运行时 PID 和跨中断转发保持。专属 cgroup 已清理，共享服务和代理文件指纹一致。最终归档 SHA-256 为 `970fe49854efaa7488f76a99c0f0af5b835d62707eaf72a74f297c73711cfedc`，日志在 `.artifacts/mb04-tracked-settings-final/`。

本阶段覆盖 Linux 环境代理旁路策略变化。监听端口变化时，guard 独立执行旧策略之后的转发连续性与显式补偿仍待验收。Windows 仍使用现有宿主路径。Proxy 保持 resident，动态休眠与生产内存未验收。

### MB-04 Linux 宿主代理切换验收矩阵

在同一私有 Mihomo 与 Helper 环境中，将原回执恢复矩阵扩展为旁路、启用、禁用和监听端口变化。每个场景先收到真实 Helper 成功并暂扣响应，再终止写入进程。重启路径按原 ID 核对真实宿主效果和本地文件。持续的旧连接与新建连接分别检查，端口漂移回退还检查 guard 写入的目标和实际监听状态。夹具恢复原策略及运行配置后再结束。

Windows Release ManagedRuntimes 广跑和模块边界回归通过。MB-05 跨进程原 `aee02c9f-e638-45ac-8000-245e576d1090` 返回 Interrupted，受控副作用计数为一。进程级测试 CA 覆盖缺失，可信下载探针跳过。

隔离 Linux 两轮通过六场景矩阵。最终轮的禁用 `58ea48ff-30a0-49d2-bf07-ecfe0186f314`、启用 `75c22ff5-ebee-469b-bb61-55052acdcf79`、监听端口切换 `c10ab973-0a85-4252-96bf-d5d54ed61a8e` 均按原 ID 返回成功。每次只有一次原 Helper 变更和一次候选 reload，恢复中无额外宿主写入。禁用后实际归属台账移除且 Mihomo 仍接收新连接，启用后宿主指向真实代理端口。端口切换后，新端口完成真实 HTTP 转发，旧端口拒绝新 TCP 连接。

最终轮旁路漂移 `d711ec92-94df-4d09-9136-e11686e1f90e` 和端口漂移 `a24374ec-2bf1-438e-a5f2-6da9ed256a03` 都保留 `RecoveryRequired`，各有一次独立 guard 操作和零原变更重放。跨场景既有连接完成转发，专属资源已清理，宿主服务和代理文件指纹一致。最终归档 SHA-256 为 `4de199b81f2859dbc20d45620f6192f799b63bb1da32d5c8890e32926b2ce127`。日志在 `.artifacts/mb04-host-policy-matrix-final/`。

监听端口漂移路径已在 Linux 复现。guard 按保存策略把宿主环境指回旧端口后，真实 Mihomo 仍在候选端口转发，新连旧端口被明确拒绝。原 ID 保持 `RecoveryRequired`。既有连接是否继续传输单独核对，不能作为新连接可用的证据。显式补偿仍需单独操作 ID，核对原持久策略、监听和宿主效果后才能结清。Proxy 保持 resident，动态休眠与生产内存未验收。

### MB-04 Linux 直连监听显式补偿

组件中心以原管理权限和宿主提权提交确认请求。补偿只接受已由 guard 接管的原 Linux 直连设置操作，核对原保存设置和候选 YAML，保留原 guard 归属。任何 TUN 安全标记、已启用或无法核实的运行时 TUN、控制器凭据缺失、配置漂移均阻止派发。

在原加密台账内持久记录独立补偿 ID，再原子还原旧 YAML 并派发一次补偿 reload。收到原生应答后，核对已认证控制器的实际端口和旧地址的新 TCP 连接，再用补偿 ID 派发宿主策略恢复。Helper 效果和旧监听均核实后，原 ID 终态为 `Interrupted` 并附补偿 ID。原设置文件保留原身份。独立补偿和 guard 共用原发布锁。

补偿 reload 回执未知时，重试保留原 ID 及补偿 ID，阻止再次 reload 或派发宿主变更。Helper 应答丢失时，重复确认请求只读核对原补偿 ID。普通 Retry 仍只读补偿状态和原终态。对仍未知的 reload，无独立原生回执可确认完整配置，保持待处理。Proxy 保持 resident。

Windows Release ManagedRuntimes 广跑、Mihomo HTTP 57 项、系统代理、TUN 边界 38 项、Core 安全 40 项及模块边界通过。未提权、未确认和非具名直连设置均未进入补偿。不可读 TUN 标记保留，原发布权威不变。桌面 Release 编译和补偿协议专项通过。Android 线协议与 HTTP 15 项通过，设备测试源码已编译，设备交互未验收。MB-05 跨进程原 `5b0d918f-417d-4337-ac03-5f0c33cac006` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 两轮通过。最终轮原 `8e46e263-b9b1-4b1e-9d29-0b3965c9b764` 经独立补偿 `9cdc31cb-fea3-4d96-8ac8-43ecb9c41ee1` 恢复旧监听和宿主策略，原回执为 `Interrupted`。Helper 应答丢失的原 `08ee17a3-2bc1-4829-bdb7-57741d973d0f` 以补偿 `5e457875-d240-40c6-b44a-2b5af0f0750f` 查回实际效果后同样结清。控制器补偿 reload 应答丢失的原 `a4792419-e6e4-4147-93d3-41ab55528b24` 和补偿 `b59e0d14-82e7-4808-a5ac-a1bf6fed0e27` 持续待核对，实际旧端口恢复可转发，宿主补偿调用为零。三条路径均只执行一次补偿 reload，重复请求无重放，旧端口真实新建 HTTP 转发通过。专属 cgroup 已清理，共享服务和代理文件指纹一致。最终归档 SHA-256 为 `c3204f87525b7597e4fd5c68d738cea7cb5a802ca778b55f691a3c0123bae5d8`。日志在 `.artifacts/mb04-port-compensation-final/`。

### MB-04 直连活动订阅队列的进程中断恢复

完整 Linux Server 通过 PAM/JWT 认证导入、激活并刷新隔离源站的订阅。测试中继等待真实 Mihomo 成功返回，夹具在交付应答前取得同一个 SQLite 数据库的写锁。确认原刷新 ID 的加密检查点已落盘，HTTP 回执仍为 Running，Proxy 仍有一个工作租约后，强制终止实际 Server 进程。SQLite 写锁阻止了原进程提交档案。

重启后，Core 使用原检查点提交档案并恢复原订阅时间，原 `890eca76-d4e6-429c-a3d6-13a9662c0efa` 通过操作查询和同一幂等键返回同一个 Succeeded 回执。刷新只下载一次、只派发一次真实 reload、只增加一次档案修订。原 Mihomo PID、跨中断 HTTP 转发和系统代理归属保持。私有 systemd 套件其余验收通过，专属 cgroup 已清理，宿主代理文件和运行时服务指纹一致。

首轮因私有档案目录未交给非 root Server，在导入时返回 400，未进入恢复场景。调整夹具属主后复验通过。Linux 日志在 `.artifacts/mb04-direct-subscription/live.log`，归档 SHA-256 为 `04fdebe69500ee6e7a5dc76081a9100d455e9e370df4fab2b3b592f422cefdf7`。Windows Release 模块边界与 ManagedRuntimes 回归通过。MB-05 跨进程原 `c8883f12-13fa-47c0-919a-f6e91695ec8c` 返回 Interrupted，受控副作用计数为一。

本项实测覆盖已收到原控制器确认、SQLite 尚未提交的活动订阅刷新。原控制器结果未知的实测见下节。批量刷新未纳入本轮。Proxy 保持 resident，动态休眠与生产内存未验收。

### MB-04 直连活动订阅重载应答未知

队列在查询或受理请求时对账原发布状态。匹配同一刷新 ID 的持久发布检查点、且当前进程已无该操作时，原队列使用真实工作租约保存 `Interrupted / publication_pending`，完成时间留空。持久终态证据仍可结清原记录。无关的检查点不会替换历史回执。

同一私有 Linux systemd 环境中，真实 Mihomo 成功加载候选订阅后，中继暂扣应答并强制终止完整认证 Server。原 `7d58f819-5b2e-4d5f-bf2c-50f676b5e86e` 在终止前仍为 Running 并持有一个工作租约。重启恢复旧磁盘 YAML，实际控制器仍报告候选节点。原 SQLite 档案修订和订阅时间保持，原 ID 与原键返回同一条待恢复记录，新的刷新键被拒绝，恢复没有重新下载或重载。原运行时 PID、跨中断 HTTP 转发及系统代理归属保持。

同批已确认刷新 `86f6c8fb-7bac-4252-ae71-61143db47cdb` 仍完成原档案事务。隔离资源清理完成，宿主运行时服务和代理文件指纹一致。Linux 日志在 `.artifacts/mb04-direct-subscription-unknown/live.log`，归档 SHA-256 为 `8667bdcb02dd64eb85f5c9fe0855826bdfe39dbe614d8aea436b5592710cdcd5`。Windows Release 原回执责任 47 项、订阅后台边界 39 项、ManagedRuntimes 与模块边界回归通过。MB-05 跨进程原 `f74bef20-409e-4e9e-8f35-07acee5cee3a` 返回 Interrupted，受控副作用计数为一。

独立的受管 Mihomo 私有 systemd 套件通过 178 项。崩溃恢复 `193de4f5-a85c-40f3-870f-0d693315dfa9`、存活进程应答丢失 `61ddc11b-5fcb-445e-8739-a6ef3d0e172c` 和真实失败补偿 `aac22140-70ac-46bf-9f23-687c815ee387` 均读取原回执，恢复无重放。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志在 `.artifacts/mb04-direct-subscription-unknown/managed-live.log`，归档 SHA-256 为 `d05974cdac5200de23c4f7f0660cdd87ebc21eabca0343bba43978ad7a0fc545`。

原控制器缺少可查询的完整 reload 回执，未知路径继续占用写入与停用门禁。批量刷新的分项恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 批量订阅刷新分项回执

批量刷新沿用原队列、幂等键、执行作用域和工作租约。原队列在准入时记录批次身份，在任何分项开始前保存有序订阅列表及各自的子操作 ID。每项派发前保存 Running，完成后保存分项结果。活动订阅沿用原发布台账及同一子 ID。草稿订阅以同一条 SQLite 更新提交刷新时间和子 ID，恢复只读取该提交证据。

进程中断后，查询和受理路径在原队列锁内对账。未开始的分项记录为 Skipped，已有确定回执的分项保留对应结果，缺证据的原分项仍为 Running。原批次保持 `Interrupted / batch_pending` 且完成时间为空，继续占用写入和停用门禁。所有已派发分项都能核对时，原批次写入 `Interrupted / batch_interrupted`，释放该批次的未决门禁。恢复既不派发跳过分项，也不重做原下载、档案写入或重载。只有正常工作、审计、作用域清理及终态持久化全部完成时才报告整批成功。

隔离 Linux 完整认证 Server 使用真实源站和 Mihomo，依次刷新草稿、活动项和尾项。第一轮在活动项真实重载确认落盘、SQLite 提交前强制终止。原批次 `a29508f7-88a7-4e3e-bb0d-fb16ed5d2760` 以原分项 ID `448e9315-a0fb-4495-852e-37d60d5eba2e` 完成活动档案提交，草稿和活动项各一次，尾项跳过。第二轮在控制器已载入候选、应答未交付时终止，原批次 `36b77395-55a4-4c99-b065-da5021b0e771` 和子操作 `e4df5fe6-4f40-4bf9-997f-443c2641ba7d` 保留待恢复。旧 YAML 回到磁盘，原档案修订和时间保持，候选节点在原进程仍可读取，新键被拒绝。两次重启均以原键读回原批次，零额外下载和重载，原进程 PID、实际转发及宿主归属保持。

最终 Linux 日志在 `.artifacts/mb04-subscription-batch/live-final.log`，归档 SHA-256 为 `616c872354e235bec62053ba9fac2d9ce616558696b4ebb8b096fcaeb36c16b7`。专属 cgroup 已清理，宿主服务和代理文件指纹一致。一次上传因私有制品占满宿主临时内存盘剩余空间而失败，最终改用独立磁盘临时目录。Windows Release 存储、订阅边界 40 项、ManagedRuntimes 与模块边界通过。SQLite 夹具确认草稿回执与时间同提交、仅按匹配子 ID 对账，以及未规划批次无派发。回执写入受阻时原队列保持。MB-05 跨进程原 `9ce90bd5-d03c-44bb-92ef-ab76cff87901` 返回 Interrupted，受控副作用计数为一。

同制品受管 Mihomo 对照通过 178 项。原活动刷新崩溃 `0ae04dde-8fcf-41d5-b086-849af2989be1`、响应丢失 `60a0dd18-c793-4a77-af2e-82c4b4160be6` 和失败补偿 `37f1e54f-e567-4705-b92b-f763757a3afb` 继续按原回执核对，零重放。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-subscription-batch/managed-live.log`，归档 SHA-256 为 `48815aac93e918d7a6bf921b0dac0e802014f17c0d5a1dd82df39ec831232e89`。

旧在途记录缺少分项计划、原证明被后续独立刷新覆盖，以及直连重载没有原确认时，继续保留未知责任。草稿文件已替换但原 SQLite 回执尚未提交的恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 草稿文件替换后的原回执恢复

未激活订阅先验证候选 YAML。替换前，Server 在原 SQLite 订阅行保存同一操作 ID、候选完整摘要和原抓取时间。原子替换的文件包含该操作 ID 的内部 YAML 注释，同一正文的旧文件因此无法冒充新替换。订阅内容读取会隐藏该注释。实时提交与重启恢复共用一个带身份条件的 SQLite 更新，提交原抓取时间、保存原回执并移除该意图。

恢复沿用原队列锁、工作租约及 Mihomo 发布锁。它核对实际文件的操作标记及完整摘要，再确认同一订阅仍指向未激活档案。只补原 SQLite 回执，草稿文件不改写，源站和运行时均无派发。锁忙、文件缺失或漂移、档案已激活或 SQLite 更新失败均保留原意图和门禁。恢复工作不等候其他发布持有的锁。旧在途记录无该意图时仍按既有证据处理。

隔离 Linux 完整认证 Server 先下载真实订阅并替换草稿，私有 SQLite 触发器拒绝原回执写入。核对新 YAML 已落盘、原意图可读且 SQLite 无回执后，强制终止 Server。恢复写入能力并重启后，原批次 `14b81cd2-9057-47f5-a814-e27106a2f0ff` 按原分项 `db45c54d-02f8-4415-9413-01c1389fca88` 补交回执。原抓取时间、YAML 字节和修改时间一致，内容接口隐藏操作标记。原刷新只下载一次，运行时零重载，Mihomo 保持真实转发。同批活动刷新确认和应答未知的两条路径通过。专属 cgroup 已清理，宿主运行时及代理文件指纹一致。日志在 `.artifacts/mb04-draft-commit/live.log`，归档 SHA-256 为 `4a477c0ec0db307e3a474d06bb40a83c2ccfc5329b36a881813e129a64bfbcb1`。

Windows Release 存储、订阅边界 40 项、配置、ManagedRuntimes 和模块边界通过。旧文件正文相同、标记相同但摘要漂移，以及发布锁忙均保持待恢复。单项草稿的同路径对账通过本地验证。MB-05 跨进程原 `130cad76-6b26-48d0-b717-204b212a2793` 返回 Interrupted，受控副作用计数为一。

受管 Mihomo 隔离对照通过 178 项，包括原草稿 `687207cf-3cf8-468f-82e5-7eddf193b292` 及活动刷新崩溃、丢失响应和失败补偿。专属 cgroup 清理后宿主指纹一致。日志为 `.artifacts/mb04-draft-commit/managed-live.log`，归档 SHA-256 为 `666ee9d06996bc5c63a705da9e780b6bd1bfc0cf792fa901d3aaf5259ff08e3d`。本轮故障实测覆盖批次中的草稿替换后窗口。草稿替换前的恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 草稿替换未应用的原回执

未激活订阅的候选文件使用原操作 ID 派生的独占暂存路径。替换前在同一 SQLite 订阅行保存原草稿摘要和候选摘要。意图落盘后，写入失败会保留原暂存文件作为证据。正常替换消耗该文件，应用回执要求目标含有原操作标记、完整摘要匹配，且暂存文件已消耗。

原草稿与暂存文件都能核对时，恢复在原发布锁和工作租约内先持久化清理决定，只删除匹配的暂存文件，再按原 ID 写入未应用回执。原草稿、抓取时间和发布进程保持。若清理后回执提交中断，下次按原决定继续核对旧目标和写入回执。单项以 Failed 收尾，批次分项记录失败，原中断批次在所有已派发分项核对后释放未决门禁。任一证据漂移、清理决定前暂存缺失或旧意图无原草稿摘要，继续待恢复。

Windows Release 存储、订阅边界 40 项、配置、ManagedRuntimes 和模块边界通过。存储夹具覆盖暂存缺失、内容漂移、无效摘要及清理后的回执写入故障。无效摘要最初触发读取异常，最终验证保留原待恢复回执。单项原 ID 返回 Failed，原草稿和上次抓取时间保持。MB-05 跨进程原 `fa33b83c-52ad-47cd-aa1a-ded18656c6a5` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 使用私有 bind mount 让实际 Server 的草稿替换失败，确认原文件与暂存文件均保留后强制终止。首次重启允许清理决定和暂存清理，SQLite 触发器仍拒绝未应用回执，此时再次强制终止。第二次重启按原批次 `5b936c50-b61d-4752-9e90-f4aac1099cbe`、原分项 `ea44a3a4-e0e7-4e52-b8ec-3ec94fbdb6ef` 完成未应用回执。原键回读一致，旧 YAML 字节、修改时间和上次抓取时间保持，原刷新只下载一次，恢复零重载。跨两次中断的真实 HTTP 转发通过。专属 cgroup 清理后宿主运行时与代理文件指纹一致。同批复验草稿已替换的原回执及活动订阅两条路径。日志为 `.artifacts/mb04-draft-unapplied/live-final.log`，归档 SHA-256 为 `93b4908818eee58d310f452dbc22382a4e36fc3c19dcce51f10dbf1f15feef9e`。

受管 Mihomo 最终制品通过 178 项，含草稿原 `c2ffa66a-9d45-4564-a872-faa67095039b` 和活动刷新原回执、丢失响应与失败补偿。专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-draft-unapplied/managed-live-final.log`，归档 SHA-256 为 `79a28414cf6c492656ef7bfe6979c611fc56b5355e1f8eefe1e1d3df1ba5af5b`。双中断实测覆盖批次草稿，单项未应用对账限本地验证。意图落盘前遗留、无法关联的暂存内容仍需单独核对。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 排队订阅激活的原发布回执

订阅激活沿用原队列操作 ID，直连检查点、受管 Helper 请求和档案提交均使用同一 ID。发布意图冻结原订阅和档案关联，SQLite 在派发前和档案事务内核对关联及待恢复草稿门禁。激活沿用已保存的草稿与原抓取时间。原发布权威保存激活的确认或失败结果，后续查询使用该结果补齐原队列回执。

未知原发布结果继续占用 `publication_pending`，完成时间留空。具名恢复查询已结清的原 ID 时返回原应用结果，无控制器或 Helper 派发。队列在真实工作、审计和作用域结束后才释放执行责任，重启接续原持久发布与档案提交。

Windows Release 配置、订阅边界 40 项、ManagedRuntimes 和模块边界通过。订阅与档案不匹配时零重载，明确回滚后具名查询返回原失败。未确认直连重载通过原键返回稳定待恢复回执，具名查询保持原 ID 且无重载。MB-05 跨进程原 `f62a9628-1a31-40cc-8103-405b9d74048f` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 直连激活 `9ec3b9b0-0ddc-4721-8ac3-7bfe7b621c03` 使用真实 Mihomo。夹具暂扣真实重载应答并持有 SQLite 写锁，随后让原确认落盘。在原任务仍持有工作租约时强制终止 Server。重启补齐一次档案修订，原键与原 ID 读回同一成功回执。全程一次重载、激活阶段零订阅下载、原抓取时间与运行时进程保持。跨中断 HTTP 转发和专属资源清理通过，宿主指纹一致。日志为 `.artifacts/mb04-subscription-activation/live.log`，归档 SHA-256 为 `d8a2c69062d8480f6b7b5eee8ed32ea3264f35f33c8d1b1f85d62567b08a8e8a`。

受管 Mihomo 使用同一隔离主机，生产 Helper 已提交激活 `e05a5a41-cda0-4cab-b2a3-67642325ff5f` 时中继暂扣原应答并强制终止 Server。重启按同一 ID 查询真实 Helper 回执并完成档案提交。原键返回原成功，Helper 原修订与订阅时间保持，激活只派发一次，源站没有激活下载。受管套件通过 178 项，真实转发持续，专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-subscription-activation/managed-live.log`，归档 SHA-256 为 `672b40e3ab76d48f6a04d8e41abddd9428ce6a9be7642b0af8a464026e695c81`。

本轮实机覆盖原生结果已确认的激活中断。激活结果未知与确定失败的实测见下节。Proxy 保持 resident，动态休眠与生产内存未验收。

### MB-04 订阅激活未知与失败的实机回执

Windows Release 配置、订阅边界 40 项、ManagedRuntimes 和模块边界回归通过。MB-05 跨进程原 `0d45dd11-c28a-42b1-a354-703f56dacc7b` 返回 Interrupted，受控副作用计数为一。直连私有 systemd runner 可用 `RELAXKONOS_TEST_ACTIVATION_UNKNOWN=1` 选择激活故障，默认保留活动批次的未知故障路径。

受管激活 `15469b9e-4a69-472f-bbf9-503417a18cd8` 使用先前保存的无效规则，由真实 Mihomo 在切换前拒绝。生产 Helper 保留原失败回执和原配置身份，中继暂扣回执时强制终止 Server。重启按同一激活 ID 和原键读回 Failed，Helper 修订、订阅时间与实际转发保持，激活零额外下载且只派发一次。此路径验证切换前的确定失败和准备清理。受管隔离套件通过 178 项，专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-activation-failure/managed-live.log`，归档 SHA-256 为 `34be917c180cf47ff3ed438df0d4fecd0d99bf7b2a54288b60bca0d9c7481e41`。

直连激活 `8db17423-7d06-4def-a445-d4c7eec3268a` 在 Server 已替换配置、真实控制器尚未读取时损坏隔离候选 YAML。真实 Mihomo 返回 HTTP 400，原事务重载旧配置一次。认证控制器读到旧节点，原档案修订和草稿时间保持，原失败回执跨进程读取一致。另一原激活 `de7c6260-9a03-4ccb-91c3-3993c31aa063` 实际加载候选后暂扣应答并强制终止 Server。重启恢复旧磁盘配置，真实进程仍报告候选节点，原激活保持 `publication_pending`、无完成时间，目标档案仍未激活，新键被拒绝。两条路径均无重复下载或重载，原进程跨中断 HTTP 转发通过。

直连首轮在并行运行期间触及 250 秒运行窗口，未产生终态。最终轮在同一运行窗口内通过，两轮专属 cgroup 均已清理，宿主运行时与代理文件指纹一致。最终日志为 `.artifacts/mb04-activation-failure/live-final.log`，归档 SHA-256 为 `e2030e4f66464a7157124c511301c80e603797f8f9269b6b2e6c96690a10c973`。受管激活切换后的故障补偿尚未联合实测。Proxy 保持 resident，动态休眠和生产内存未验收。

### MB-04 Mihomo 重叠流量采样

依据 512 连接轮询基线，同一 Server 内通过认证的重叠流量请求共享一次 `/traffic` 样本、一次连接快照读取和一个控制器作用域。采样拥有独立工作租约，清理失败沿原驻留协调器保留恢复责任。各请求分别持有响应租约。单个客户端取消后其余读者仍可接收结果，最后一个客户端取消后，中止采样并等待实际任务和清理。新请求等待该采样排空后读取新样本。共享范围为在途请求，完成后无样本缓存和后台轮询。

Windows Release 模块边界回归验证 Kestrel 的匿名拒绝、重叠请求、最后一个客户端取消、异步清理期间的新读者等待及响应排空。MB-05 跨进程按原 `bb848743-bdbb-4130-8aa7-2344c239620f` 读回 Interrupted，受控副作用计数为一。首轮 Windows 沙箱拒绝 loopback 连接，使用允许本地监听的执行权限复验通过。

隔离 Linux 由完整 PAM/JWT Server 通过真实 Mihomo 验证。首个客户端取消时，另一客户端取得原采样，实际 `/traffic` 与 `/connections` 各调用一次。最后一个客户端取消时，控制器请求断开，工作租约归零后新请求成功。原发布 `ee01ad12-71b0-468d-93ed-86101d4c8fd9` 跨 SIGTERM 按原 ID 读取 Failed，原连接 `30cef037-cfed-4cfb-a0f2-dbaef93a37f7` 跨解析代次完成真实转发。原生套件 178 项通过，专属 cgroup 已清理，宿主运行时和代理文件指纹一致。日志在 `.artifacts/mb04-shared-traffic/`，原生验收归档 SHA-256 为 `0c3d4663bbb13d439f2aac0a1feff3ab96baacbbd4321d34864b7e0e1028b3a5`。

同机配对保持 Mihomo PID 9855 和同一批 512 条真实连接，基线与候选各运行三次，第二对反转顺序。每轮四个重叠认证请求组成一批，按两秒节拍执行十批。真实控制器中继在三对均记录每轮 `/traffic` 和 `/connections` 各由 40 次降为 10 次，候选每批四个回执相等。512 条原连接均收到源站响应。原回执 `a21dbf06-6f4e-4195-ae66-ccdb9d9c64e3`、原生修订和发布身份一致，测量期间没有配置或补偿派发。

| 轮次 | Server 峰值 PSS 差（MiB） | Server 35 秒 PSS 差（MiB） | 批次中位延迟差（ms） | Server CPU 差（ms） | Mihomo CPU 差（ms） |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1 | −6.72 | −5.88 | −10.75 | −1230 | −110 |
| 2 | −3.85 | −4.56 | −5.81 | −530 | −60 |
| 3 | −4.88 | −4.21 | −7.17 | −950 | −90 |

差值为候选减基线。基线为 `2837ebcd`，两份发布树仅替换 Server DLL，使用同一个 Proxy 解析 DLL 和 Helper。以 `RELAXKONOS_TEST_MIHOMO_SHARED_TRAFFIC=1` 和 `RELAXKONOS_TEST_MIHOMO_OVERVIEW_BASELINE` 选择该测试。隔离归档 SHA-256 为 `925b64534f3a9c2f9d5879dc502dace107ce850ebbbe907dacda8e1f1f59f154`。六轮数据、制品摘要和复核结果见 `.artifacts/mb04-shared-traffic/{paired.log,summary.json}`。本轮测量限重叠请求和隔离负载，生产资源、Windows 原生控制器断线与 Proxy 整体动态休眠继续开放。

### MB-05 Docker Compose 只读 HTTP 工作责任

Compose 列表、预览、定义和服务读取在认证后先取得 Docker 工作租约，再解析独立请求作用域。实际 CLI 及输出排空、HTTP 响应和异步作用域清理均完成后才归还租约。CLI 进程清理无法核实时，原驻留协调器保留故障责任。Proxy 读取复用同一响应作用域机制。Compose 变更与恢复继续由原持久台账和协调器执行，共享 Engine 传输由 Core 持有。

Windows Release Kestrel 夹具验证匿名请求无准入、四条路由的实际任务与响应、异步清理和未确认 CLI 清理的故障租约。模块边界回归通过，MB-05 跨进程以原 `d665dd05-3d97-4cc5-a29c-a44afda94652` 返回 Interrupted，受控副作用计数为一。

Linux 私有 Engine 以完整 PAM/JWT Server 验证服务读取。夹具收到真实 `docker ps` 输出后暂扣交付，模块接口显示一个工作租约。断开 HTTP 后，CLI 包装进程已退出、工作租约归零，新请求取得原容器服务。停止见证次数保持。随后复验原用户取消 `86c05a1a-a0cf-46b6-a5cd-85aa4064b61f`、存活 Server 观察断线 `93e69d33-cb16-44ad-9bdc-926578edca1b` 和重启后 Engine 离线 `72f0b280-d9a7-4435-9168-e2fcd8a98010`，按各自原回执完成只读对账，停止副作用均未重放。三个专属 Engine 场景均清理容器和卷，宿主服务、容器清单与代理文件指纹一致。

首轮混用旧协议与测试 Helper 制品，PAM 登录返回 HTTP 503。统一制品版本后通过，日志在 `.artifacts/mb05-compose-read/`，验收归档 SHA-256 为 `f5753c5aaea7ae1c014a5a57b17097078a8c27f8e6d6c799a82de628ec967cb9`。真实断线验收覆盖服务读取，其他三条路由的任务与响应边界使用本地夹具验证。Docker 保持 resident，Compose 独立执行程序集、ALC 回收与生产资源测量继续开放。

### MB-05 Docker Compose 独立执行程序集

固定 CLI 调用、文件操作及输出解析进入 bundled collectible 程序集。原 HTTP 路由和栈协调器通过同一个作用域入口使用该程序集。Core 继续持有共享 Engine 传输、唯一原操作台账和终态确认。共享契约保留 DTO、领域错误与准入校验规则。执行项目及契约项目的构建守卫分别约束依赖，Server 发布目录携带独立执行 DLL。

请求作用域在实际 CLI、HTTP 响应与清理结束后释放执行对象。后台作用域完成执行清理后由原 Core 租约负责终态落盘。最后一个作用域离开 30 秒后卸载代码，新工作按需唤醒。代次预算保护回收滞后时的可用性。预览文件或 CLI 清理无法核实时，执行代次和原工作责任继续保留，拒绝新执行。跨边界失败只传递共享错误数据或 Host 生成的异常。

Windows Release 模块边界回归通过。独立检查在保留成功与失败 Task 的情况下收集五个代次，活动作用域跨闲时窗口保持原代次，受控清理未知检查保留故障代次。MB-05 跨进程按原 `e392d5da-42c1-48ae-b491-a92435198b9d` 返回 Interrupted，受控副作用计数为一。

Linux Release 的五代收集检查同样通过。完整认证 Server 在专属 Engine 中暂扣真实 `docker ps` 输出，跨 30 秒观察窗口保持原代次与一个工作租约。客户端断线后 CLI 包装进程已退出，工作租约归零，原代次报告闲时卸载，新请求加载下一代并读到原服务。新代次也通过真实列表、原定义读取及预览，预览目录确认清空。Core 的原操作 `e24ef921-2592-4b39-a596-df1f772d0c91`、`20168d97-2f95-43b1-99b5-d6c430e43845` 和 `3f21603a-a081-4e11-acc4-bb1f1af2b6ed` 分别在取消、存活 Server 失联和重启后 Engine 离线场景按原 ID 对账，停止未重放。专属资源清理完成，宿主服务、容器和代理文件指纹一致。

首轮外层握手在空闲卸载窗口结束前停止专属 Engine，修正等待条件的期限后通过。构建守卫反向拒绝额外程序集引用。最终日志在 `.artifacts/mb05-compose-execution/`，Linux 归档 SHA-256 为 `7190796694ca3ffd9f27e3c898d7258b25f9e0d2be50976c25f421dd09020816`。未知清理保留代次在本地检查验证。Docker 管理模块继续 resident，生产资源测量及其余管理执行图继续开放。

### MB-05 ACME 原订单观察的 HTTP 工作责任

认证订单读取从原证书操作 owner 取得一个工作租约，涵盖台账快照、真实 CA Task、HTTP 响应和异步作用域清理。未知操作返回 404，缺少可核实的原订单身份返回 `identity-unavailable`，两条路径均无需解析 ACME 客户端。停机通过同一个 owner 排空，返回 `Cache-Control: no-store`，原回执和证书事实维持原权威。

Windows Release Kestrel 检查通过，覆盖匿名拒绝、客户端断线后的真实 Task 等待、响应及清理期间持有同一租约和台账字节一致。模块边界回归通过，Docker 跨进程原 `1ce0b949-7009-4c97-887c-9b4c44b342ae` 为 Interrupted，受控副作用计数为一。

Linux 完整 PAM/JWT Server 使用私有 Pebble、Nginx 和域名解析。中继取得真实原订单结果并暂扣响应。HTTP 断线后仍有一个租约，放行后归零。再次暂扣时发送 SIGTERM，监听关闭后原进程继续等实际 CA Task，放行后正常退出。重启按原 `cb0406e3-6622-4449-a534-4580d5f05f34` 读到原回执和 CA `valid`，缺失身份的原 `bb03eaff-cc8e-4880-a816-d38bf105c279` 仍为 `identity-unavailable`。原台账字节和活动证书指纹一致，CA 共三张订单、两次签发提交，观察与同键查询没有新增。宿主服务和文件指纹一致。

同制品独立 Pebble 回归通过签发、取消、进程中断、跨日及两种传输断流，七张订单与四次签发提交符合原矩阵。首两轮包装器分别在临时目录路径校验和框架依赖入口处失败，修正后通过，测试目录已清理。日志在 `.artifacts/mb05-acme-observation-drain/`，归档 SHA-256 为 `bb0102b661e02cab73dc82cb31b6f0c286deeee1f5e468376a8bd685c4cdf4d4`，复验启动包装器 SHA-256 为 `38e5704912726a90c5c4e6eb22924f6be4d158d126bd8b25d55b22d1123bef7b`。Certificates 保持 resident，ACME SDK 执行图、ALC 与生产资源验收继续开放。

### MB-05 ACME SDK 独立执行程序集

签发、撤销、原订单读取和 ARI 调用按需加载 bundled collectible 执行器，Anvil、Bouncy Castle 和 SDK 的 JSON 依赖从私有目录解析。Server 保持同一个账户门禁、账户元数据写入、挑战存储、续期 owner、原操作台账及证书部署权威。执行器复用原账户文件和订单身份，跨边界返回共享值或由 Server 重建的异常。持久检查点失败、挑战归属冲突和取消仍沿原责任路径处理。

执行代次保护实际 SDK Task 和挑战清理，最后一个执行调用结束后开始 30 秒闲时计时。HTTP、终态提交和未确认清理继续由原 Core 工作租约负责。账户连接池使用 Host 的框架传输，每次 SDK 调用的客户端在调用结束时处置。Windows Release 模块边界回归通过，五代检查在保留成功和失败 Task 时收集旧代次。MB-05 跨进程原 `2d0b8448-53b3-49dc-bb71-572ac12d8a9b` 为 Interrupted，受控副作用计数为一。

首轮真实 CA 验收已观察到卸载事件，随后 GC 检查发现旧代次仍存活。GC 根定位到默认上下文的类型描述缓存。Json.NET 使用的 TypeConverter 框架程序集改为从当前运行时目录加载到同一代次，缓存随代次释放。归档不携带框架私有副本，生产路径不主动触发 GC。构建守卫反向拒绝未批准引用。

最终 Linux Release 通过五代收集和真实签发 `1e845d1b-cd25-4e4e-91f1-bb744c7607c4` 后的代次收集。保留真实 CA 观察 Task 时，原 `3d6c2fa2-2a44-4936-8f55-73b3eb0c5824` 仍完成代次收集。完整 PAM/JWT Server 观察真实响应在途时保留同一 SDK 代次，断线排空后卸载，新代次按原 `045c90af-8342-4f77-bf45-92ce51101246` 读到同一订单。SIGTERM 排空、重启、同键回执和原活动证书核对通过。认证套件保持三张订单、两次签发提交，独立签发与中断套件保持七张订单、四次签发提交。

自然续期的挑战归属冲突经独立 Nginx 和 Pebble 复验。恢复原扫描 `6074c140-4d42-4b78-a530-2150e7d90888` 和原操作 `e2b8a113-f400-4a42-b52f-67743bdde797`，CA 保持两张订单、一次签发提交。宿主服务和文件指纹一致，证据在 `.artifacts/mb05-acme-execution/`，最终 Linux 归档 SHA-256 为 `44b885410dfad52994f360c70bf5408b89a3f4337cadbe826f18da04a6acb855`。撤销与 ARI 的独立实机故障、Certificates 整体休眠及生产资源测量继续开放。

### MB-05 网站发布独立执行程序集

已准入的网站发布、具名首站续接及清理由同一个 bundled collectible 执行器运行，包含流程决策、补偿和 DNS/TLS/HTTP 验证。Host 端口只传入发布所需的应用投影、协议值和原服务调用。原协调器独占准入、停机及恢复，原台账负责检查点和终态。Nginx 提供者、Helper 原生回执、证书 owner 和应用目录继续持有各自的写入及安全权威。

执行作用域涵盖实际任务、补偿和终态提交。最后一个作用域释放 30 秒后可卸载，新操作按需加载，恢复读取原键时不会派发原副作用。跨边界失败使用共享错误字段或 Host 重建的异常。Windows Release 模块边界回归通过，56 项后台所有权检查包含真实 HTTPS 响应等待期间代次保活、停机排空、GC 收集和原回执恢复。Docker 跨进程夹具原 `6a6b82a2-c9bd-4155-bb1b-92a5b4157ed1` 为 Interrupted，受控副作用计数为一。

Linux 完整 PAM/JWT Server 使用专属 Nginx、Docker 上游和 Pebble。在原发布 `2e1ac707-f033-438c-96c6-f5704a3e0b85` 的已签发证据读取在途时，模块保持一个工作租约与原执行代次。进程中断后，启动恢复和未认证请求均未加载执行器。授权续接使用原证书 `589aca3d-fbbe-4ab5-a701-a58c7c6b1bb4`，闲时卸载期间原 Nginx PID、HTTPS 转发和上游保持。下一次具名请求唤醒新代次并返回原回执。Pebble 保持一张订单、一次签发提交，Nginx 保持一次引导和一次绑定重载。

同制品复验绑定回执应答中断 `cf5e7123-2a98-49a9-8710-b94c4aab5880`、已知 CA 拒绝与清理 `c7ff6404-3e1a-4b70-b462-da65c3dcdd46`、配置中断 `d5136a5e-d136-4c4b-bca7-d567beb32559` 和 HTTPS 验证中断 `b91afdca-2d08-4064-b1a6-60772ec104f9`。各自保留原回执，恢复未重放原签发或站点变更。五个专属 cgroup 已清理，宿主服务、Nginx 配置和宿主文件指纹一致。首轮夹具读错启动日志名，修正后通过。构建守卫反向拒绝未批准引用。最终证据在 `.artifacts/mb05-website-execution/`，归档 SHA-256 为 `794ba3419f3a1f861af46ba7d9a02ae6eba2218a460868f59c7a6c52a86e7340`。

Linux 证据包含真实工作中的保活、闲时卸载事件及按需唤醒。真实 HTTPS 任务后的 GC 收集由 Windows Release 检查覆盖。直接重载的后续边界见下节。WebServer 整体动态休眠和生产资源测量继续开放。

### MB-05 直接 Nginx 重载独立执行程序集

直接重载与网站发布复用 WebServer 的可回收执行代次和 30 秒闲时窗口。仅获准并进入原实例锁的新重载创建执行器。Helper 调用、原 operationId 和回执由 Host 提供，原 WebServer 台账独占检查点、对账和终态写入。执行代次由原后台工作持有，覆盖实际命令、异步作用域清理、终态持久化和 Helper 回执压缩确认。启动恢复只对账原 Helper 回执，同键查询读取原台账。

Windows Release 模块边界和 WebServer 专项通过。跨进程夹具以原 `13d1f234-ff24-4c45-9740-3af503e58687` 读回 Interrupted，受控副作用计数为一。

Linux 完整认证 Server 使用专属 Nginx、生产 Helper 和私有 systemd。原重载 `82644a5b-5077-4e17-8dfb-d0a3aa1cd398` 终态已落盘时，故障注入扣住原回执清理确认超过 30 秒，原工作租约和执行代次仍在。确认后观测到旧代次卸载并经测试探针确认 GC 收集。同键请求未加载执行代码，另一次明确授权的新重载才加载下一代。整个过程保持原 Nginx master、TLS 和已应用配置。首轮夹具误将新重载的清理回执纳入原 ID 阻断，修正后复验确认两项各自收尾。

同制品另通过取消 `11d74e23-072b-435d-a4d5-9628c1ee3c0e`、SIGTERM `daef5fa8-b23b-4499-aa15-1eb047ae78b3`、进程中断后匹配原 Helper 证据 `70e4fb54-bcde-45f3-9b85-60917a196dc7`，以及应答丢失后的成功和已知拒绝。Server 与 Helper 同时中断且缺少终态证据时，`52b9a55e-d960-41da-8bfb-4493ccdb3c7f` 仍待核验并拦截同实例新工作，实际重载一次。网站配置中断 `e1639586-961e-43fc-9519-75e314a0d5e1` 继续保留原回执与 HTTPS 上游，实际重载一次。七个隔离场景的专属 cgroup 均已清理，共享服务与宿主文件指纹一致。日志见 `.artifacts/mb05-nginx-execution/linux-final.log`，归档 SHA-256 为 `eefbed73be0be3a1f30805a342bba36bfd0f6ada04630bd41b865de07d7ace3b`。

WebServer 继续 resident。Nginx 集成、WebRoot 启用、启停及安装图由 Host 持有。非系统包重载的可回收边界已实现，仍待原生实机复验。整机掉电及生产内存继续开放。

### MB-04 订阅后台执行图的可回收交接

排队刷新、激活与批量刷新由同一 Proxy 可回收程序集执行。刷新用同一代次完成真实响应体的有界解码与规范化、草稿或活动档案分流以及原批次分项执行。每个已准入操作使用一个代次，批次不为各分项另建代次。导入仍在原 HTTP 作用域内为响应体取得短期代次。两者共用既有 16 代次保护上限。

Host 继续独占下载授权、网络策略、配置发布门禁和持久写入。队列仍在执行前落盘原回执，批次先持久化原分项计划，后续按分项 ID 对账。执行代次跨实际任务、原审计、异步作用域清理和结果写入。结果未知时先持久化原待核验责任，再由原台账和 Core 发布恢复接续。重启及同键查询沿原台账和原发布或 SQLite 草稿证据恢复，证据不足时继续保留原待恢复责任。

Windows Release 订阅后台边界 40 项、独立程序集与驻留 31 项、构建依赖守卫 16 项通过。Core 安全、系统代理、TUN、存储、档案配置和模块边界回归通过。MB-05 跨进程夹具原 `13925e47-153e-4d4b-af73-2ec64a544c5a` 返回 Interrupted，受控副作用计数为一。

完整认证 Linux Server 用真实分段订阅响应验证草稿刷新 `330ad5eb-9b8e-483d-bb3a-2c780298531b`。响应在途时原工作租约与执行代次保持，原终态可读后观测到卸载并确认 GC 收集。重启同键取得原草稿和终态，源站请求没有增加。受管 Mihomo 激活 `5a6e5fe7-13ef-4890-be49-2029dc6f196b` 和活动刷新 `34b20dd7-f4dc-49f4-9708-02d52f285c74` 在原生提交后中断，重启从原 Helper 证据补齐原终态，未再次下载或配置。

受管 Mihomo 实机套件通过 178 项。活动刷新 `2c3b64d6-a83d-48e5-b57d-56185fb8db6b` 丢失应答后，原 Server 按同一证据核对终态。失败刷新 `3740d4b7-535b-4088-af37-4f80452b6e9b` 和失败激活 `ce101eb2-c3f0-4cdc-b66b-d0bb6165a91b` 保持原 Failed 回执，原转发和时间戳保持。

同制品的完整认证联合验收覆盖系统代理 guard、真实 TUN、授权到期、发布补偿和 SIGTERM 排空。批次 `cf4155e4-4450-4207-a7db-7b3125ba45b1` 按原子项回执确认草稿与活动提交，未开始的尾项跳过。未知批次 `12c1c652-eba9-42b8-ad5d-e319a4ea88c4` 保持原待核验回执，无新增下载或 reload。补偿 reload 丢失应答的原 `6fb4c8a5-4526-41d0-853a-9fde55a9bdc6` 保持 RecoveryRequired。两个私有 cgroup 已清理，宿主运行时与代理文件指纹一致。日志见 `.artifacts/mb04-subscription-execution/linux.log`，归档 SHA-256 为 `236f2033079a5a05842ef470b7b1a08e5ee9ecc252a68e8e16eb79ecd5f79da5`。

本切片完成订阅后台执行图交接。Proxy 整体保持 resident，系统代理、TUN、到期与发布补偿继续由原 Core owner 持有。整模块休眠及生产内存继续开放。

### MB-04 直连档案与设置发布执行器

直连档案激活和设置写入使用同一个 Proxy 独立执行程序集。确认 reload、识别未知结果、恢复原文件和选择补偿均在可回收代次执行。Host 继续持有原发布锁、文件和权限检查、SQLite 档案提交、系统代理 Helper 与原回执。只在原门禁通过后加载，代码不可用时在记录新检查点或替换活动文件前失败。每次执行使用一个代次，采用独立的 16 代次保护上限。

活动文件替换后，执行等待真实控制器应答、宿主效果和持久确认，临时文件清理完成后释放执行代次。外部 reload 结果未知时保留原 ID 供 Core 核验，后续读取无副作用派发。HTTP 工作租约继续覆盖原审计、HTTP 响应和作用域清理。系统代理、TUN、授权到期和发布恢复沿用原 Core owner。

Windows Release 档案配置激活与回滚、设置边界 31 项、系统代理、TUN 38 项、Proxy 驻留 31 项、订阅后台边界 40 项和模块边界回归通过。依赖守卫 16 项通过。MB-05 跨进程夹具按原 `c35f5104-6c9c-437b-b9d2-8c5e602e1ef3` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 用真实 Mihomo 验证原设置操作 `9ffde079-16ba-494b-b979-27ecc58e4e30`。控制器已加载候选，回复暂扣期间原检查点与代次继续在途。丢失应答后恢复原文件并保留待核验 ID，已完成调用仍被测试保留时旧代次经 GC 收集。同一待核验请求未再次派发 reload。

档案在 SQLite 提交前后中断的原 `f06880b1-fde2-4bb7-a0ca-ddd5e2d59128` 与 `beee4f10-8761-4a3e-8b44-e27703a3fb06` 各保持一次 reload、一次档案修订。设置文件提交前后中断各保持一次 reload 和一次真实宿主确认。原补偿 `e72436b8-231e-4604-ba0d-072a1a77b563` 的 reload 应答丢失后仍为 RecoveryRequired，未重放。TUN、系统代理、授权到期、发布补偿和订阅批次同制品通过，专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志见 `.artifacts/mb04-direct-publication-execution/linux.log`，归档 SHA-256 为 `577f358a2782f806d816ba1029417ca1a89609e28ffdd3839229ce8e0d5b8800`。

本切片覆盖直连档案与设置的提交和补偿流程。受管运行时安装、版本生命周期和受管发布事务由原实现负责。Proxy 整体保持 resident，整模块休眠及生产内存继续开放。
