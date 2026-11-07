# Server 模块资源所有权清单（MB-01）

审计基线：`443b0b7e1143fca826a51239afd19ea17022ca6a`，2026-11-05。本文件记录该提交的源码事实；测量结果、发布文件摘要和环境必须另附运行证据。它不证明生产内存下降，也不以源码审计代替堆转储或原生内存归因。

目录入口为 [`ServerModules.Catalog.cs`](../../RelaxKonOS.Server/Startup/ServerModules.Catalog.cs)：9 个内置模块，另加实际安装的官方进程内包/第三方 ModuleHost。Proxy 的三个产品面仍是同一个模块，不能计成三个独立可释放模块。以下源码链接相对本文件；同名目录内的 partial 文件共同构成所有者。

释放层级定义：**对象**是断开并销毁本代对象；**代码**是请求 collectible ALC 卸载、有限预算等待自然回收（测试弱引用证明另列）；**进程树**是确证全部受管后代终止；**resident**表示仍被根 DI/HostedService 持有。任何层级均不自动代表 RSS/PSS 或私有内存下降。静态共享框架、契约程序集和 Core 基础设施不随模块释放。

## 1. 当前根与责任矩阵

| 模块 | 根引用及执行图 | 后台/连接责任及释放阻止项 | 当前释放层级 |
| --- | --- | --- | --- |
| Git `relaxkonos.git` | [GitModule](../../RelaxKonOS.Server/Startup/Modules/GitModule.cs) 根注册 `IGitRepositoryService`/`IGitBuildService` 租约代理、Host 存储/身份窄端口；部署端口代理和 scoped `GitTextEditor` 不保存执行实例。[GitModuleRuntime](../../RelaxKonOS.Server/Git/GitModuleRuntime.cs) 持有本代 `CollectibleGitExecution` | 请求、构建、CLI、镜像清理及终态写入持有同代租约；`UnloadBlocker` 拒绝存在未结束工作的释放；不能因 HTTP 已返回而释放 | 对象 + Git 仓库/CLI/构建实现代码；4 个存活/待回收代次预算；Core、业务磁盘及外部 Engine 保留 |
| Firewall `relaxkonos.firewall` | [FirewallModule](../../RelaxKonOS.Server/Startup/Modules/FirewallModule.cs) 仅根注册 `IHostFirewallService` 代理；[FirewallModuleRuntime](../../RelaxKonOS.Server/Firewall/FirewallModuleRuntime.cs) 每代 facade、边界和 collectible 执行器，窄端口借用 Core Helper 运输 | 完整 Task 请求保护；没有模块自有 timer、HostedService、流或恢复循环；外部 OS 规则不属于 facade | 对象 + 执行代码；4 代预算；Helper、系统防火墙及规则不释放 |
| Tailscale `relaxkonos.tailscale` | [TailscaleModule](../../RelaxKonOS.Server/Startup/Modules/TailscaleModule.cs) 根仅三个租约代理；[TailscaleModuleRuntime](../../RelaxKonOS.Server/Tailscale/TailscaleModuleRuntime.cs) 本代容器拥有 authorization、control/login coordinator、两个 journal 和 Helper host adapter | 本地请求 Task/连接结束才归还租约；持久回执和外部登录/控制责任不因对象释放消失；未核实结果必须继续可查询且不重放 | 对象；实现仍在 Server 默认 ALC，没有独立执行程序集；外部 daemon/系统服务保留 |
| Terminal `relaxkonos.terminal` | [TerminalModule](../../RelaxKonOS.Server/Startup/Modules/TerminalModule.cs) 根 singleton `IPtyFactory`、`TerminalSessionManager`（同实例 HostedService）；Hub 引用根 manager。[TerminalSessionManager](../../RelaxKonOS.Server/Terminal/TerminalSessionManager.cs) 拥有会话字典 | 每会话 PTY、输出/Hub、重连资格、停止任务和树清理；断线不等于会话关闭。失败/未知树清理继续占责任；不得提前建立替代执行者 | 模块 resident；显式关闭能释放会话及核验其进程树，不是模块闲时退出；PTY 依赖未独立代码回收 |
| SystemPerformance `relaxkonos.system-performance` | [SystemPerformanceModule](../../RelaxKonOS.Server/Startup/Modules/SystemPerformanceModule.cs) 根持有平台 metrics/source、history、subscription registry、sampler、process sampler、work lease source、Hub connections；sampler/process/connections/broadcast 为 HostedService | 实际采集/静态信息/REST 等待及响应执行、进程查询/终止/图标、连续 Hub 连接及真实发送均有当前工作保护。停止等待实际工作；不是物理网络送达证明 | resident，S1 冻结；实例缓存可清理，未实现模块动态释放、完整代码卸载 |
| Docker `relaxkonos.docker` | [DockerModule](../../RelaxKonOS.Server/Startup/Modules/DockerModule.cs) 根 compose service、stack coordinator（HostedService）、proxy/control/configurator 与平台 reader；镜像解析器 scoped。Core 单独持有 Engine/installer/proxy resolver/出站 factory 和 stack store | compose queued/running、取消/清理、启动后实际 Engine 对账；领域 work owner 保护到真实结束。未读台账按未知阻止停用 | resident；后台工作生命周期已接入，不是管理对象/代码释放；Core Engine 能力始终保留 |
| Certificate `relaxkonos.certificates` | [CertificateModule](../../RelaxKonOS.Server/Startup/Modules/CertificateModule.cs) 根 ACME/ARI provider（同实例）、direct challenge store、manager、operation store（HostedService）、renewal repositories/worker。Core 拥有文件挑战、证书存储/metadata/deployment、Kestrel registry/startup | 续期循环、ACME 请求、HTTP-01 listener/挑战与清理、签发/部署及 operation startup recovery；活动/故障责任持续保护 | resident；三域 owner/真实 listener 寿命验收不等于独立程序集或闲时释放；HTTPS 证书不能随管理面消失 |
| WebServer `relaxkonos.webserver` | [WebServerModule](../../RelaxKonOS.Server/Startup/Modules/WebServerModule.cs) 根 publication store/coordinator（HostedService）和命名验证 HttpClient；硬依赖 certificates。Core 根 `IWebServerManager`/Nginx provider、metadata/operation store/package store | 网站发布、证书/挑战引用、真实验证请求和启动核实；未知或 pending 发布不能丢弃；重启核实实际网站状态，不重放请求 | resident；Nginx 共享管理/部署权威仍在 Core，外部网站服务持续 |
| Proxy `relaxkonos.proxy` | [ProxyModule](../../RelaxKonOS.Server/Startup/Modules/ProxyModule.cs) 根 operation/audit stores、work source、订阅/lifecycle operations、downloader、platform、server queue、Squid store；engine/registry/lifecycle/profile/configuration 用 scoped/transient；[ProxyRuntimeRegistration](../../RelaxKonOS.Server/Startup/ProxyRuntimeRegistration.cs) 保留 Core runtime、发布、共享读取与安全图 | 管理操作独立 scope/租约；Core 唯一拥有 TUN recovery、system proxy guard、Mihomo publication scheduler、sing-box expiry。TUN 控制器与 HTTP 客户端在实际观察和切换期间使用临时作用域，安全服务和原台账锁常驻，Core worker 扫描原 TUN 标记并跳过已完成的当前会话。持续安全/恢复责任没有转交前不得撤除 | 聚合模块 resident；独立订阅转换代码可回收且有待回收代次预算；scope 释放不等于整个 Proxy 休眠 |

## 2. 缓存、私有依赖和外部执行

“未设统一字节预算”表示源码无法推出总字节上限，不等于已测得泄漏。磁盘保留条目上限也不是活跃责任上限；不得为满足预算删除待恢复记录。

| 模块 | 内存/缓存及已知预算 | 私有实现、原生或第三方依赖 | 外部进程/共享服务与测量归属 |
| --- | --- | --- | --- |
| Git | 本代 repository write-lock 字典；build 活动 CTS/操作/日志。diff patch 200 KiB；整体对象图未设统一字节预算。源码：[Git 实现目录](../../ServerModules/RelaxKonOS.ServerModule.Git/) | 独立 Git 实现，依赖共享 Git Contracts/ASP.NET framework，无私有 NuGet 包声明；Core 借用 DB/身份/秘密保护/日志 | git CLI、docker/buildx CLI 为执行子进程；Docker daemon/BuildKit 容器可能是预存共享服务，独立计量并声明归属，不能只量 CLI；构建实机矩阵仍开放 |
| Firewall | Linux parser/静态 regex 位于执行 ALC；没有模块自有重缓存；查询结果物化后返回 | 独立 Firewall Contracts/执行程序集，无私有 NuGet 包；Helper 特权运输仍在 Core | Helper/ufw/平台查询进程与 Windows Firewall 服务；规则不随代次销毁。不为测量修改真实规则 |
| Tailscale | 本代 authorization/journal/coordinator；状态解析临时字典；无独立、已量化的长期缓存字节预算 | Server 内实现及共享 Helper/协议依赖；不能把默认 ALC 代码归为已释放 | tailscale CLI/Helper 连接与外部 tailscaled；daemon 若非 Server 子进程须另列共享服务，不将其从比较中悄悄删除 |
| Terminal | [TerminalSession](../../RelaxKonOS.Server/Terminal/TerminalSession.cs)：每会话 1 MiB ring，发送排队合计 1 MiB/128 项；会话字典没有整机/每用户总数预算；不能相加当作精确实际内存 | RoyalTerminal PTY 平台包；Windows ConPTY/Job；Unix PTY/Helper transport；均尚无模块 ALC 边界 | Windows shell/ConPTY 及 Helper 路径；Linux 本地每会话独占 .NET worker、PTY shell 和全部后代（[LocalUserTerminalPty](../../RelaxKonOS.Server/Terminal/LocalUserTerminalPty.cs)、[LinuxUserTerminalPty](../../RelaxKonOS.Server/Terminal/LinuxUserTerminalPty.cs)）。分列 worker、shell、Helper/运输成本与整树；worker RSS 不能代替净成本 |
| SystemPerformance | [PerformanceHistory](../../RelaxKonOS.Server/SystemPerformance/PerformanceHistory.cs) 60 帧；普通/管理员快照和 CPU 基线隔离。每个 [ProcessIconCache](../../RelaxKonOS.Server/SystemPerformance/ProcessIconCache.cs) 512 项/8 MiB 完成载荷；Server/Helper 分属实例。图标工作 4 并行+32 等待；[Hub connections](../../RelaxKonOS.Server/Hubs/PerformanceHubConnections.cs) 128 名额；广播 4 帧。输入/索引预算见边界设计 §23～37 | 平台性能计数器/句柄、SkiaSharp 及 Linux native；Skia 同时用于 Core 文件缩略图，不能把整个库算模块独占 | Helper 管理员查询/图标；OS 性能读取。无需也不得为 MB-01 改 S1 实现；图标缓存载荷预算不覆盖解码临时峰值、传输字符串和全部原生分配 |
| Docker | coordinator running/CTS 字典；并发由 `DockerComposeOptions.MaximumConcurrentOperations` 限制。stack store 保留终态200、audit1000、诊断120行×512字符，活跃项保留（[store](../../RelaxKonOS.Server/Docker/DockerStackOperationStore.cs)）；无统一内存字节预算 | Server 内 CLI/Compose 实现，共享 HTTP/JSON/出站代理；无独立执行程序集 | Docker CLI/Compose 子进程、Engine daemon、BuildKit、业务容器；Engine 与业务容器须按独占/共享另列，不能重复算进每模块树 |
| Certificate | [operation store](../../RelaxKonOS.Server/Certificate/CertificateOperationStore.cs) 内存 operation/idempotency 字典、按证书 gate 与 CTS；ACME account gate；未确认统一总字节预算，历史规模属于数据集维度 | `Webprofusion.Certify.ACME.Anvil` 当前为 Server 根项目 NuGet 引用；HTTP/TLS/X509；direct listener 原生 socket | 外部 CA/ACME 网络端点、验证路径及 Nginx 为协作服务；Kestrel/TLS/Core 证书状态不随管理面释放；无需为基线执行真实签发副作用 |
| WebServer | [publication store](../../RelaxKonOS.Server/WebsitePublishing/WebsitePublicationStore.cs) 内存 entries，终态保留200且不丢活跃项；验证 HttpClient/handler；无统一字节预算 | Server 内发布/配置实现，Core Nginx/证书端口；没有独立私有程序集 | nginx 及 Helper/CLI 检查；外部 nginx 不是休眠 kill 目标；Core 共享服务成本须与管理模块区分 |
| Proxy | 根 operation store 只跟踪活动/故障 owner，历史按需从盘读取；订阅转换 ALC 存活+待回收预算，不能强制 GC 腾位。[diagnostic log](../../RelaxKonOS.Server/Proxy/ProxyDiagnosticLogStore.cs) 磁盘500条×消息1000字符；配置/订阅 materialization 和 HttpClient handler 无统一总字节预算 | 订阅转换独立 Proxy 实现/Contracts；其余 Mihomo/client/profile/Squid/sing-box/安全仍在 Server；四种 subscription HttpClient，handler 池由 Host 工厂管理 | Mihomo、sing-box、Squid、Helper/配置验证子进程；存在系统代理/TUN承诺时继续运行，不以管理页闲时终止。后台恢复与 Engine 成本必须纳入环境清单 |

以上依赖依据 [Server.csproj](../../RelaxKonOS.Server/RelaxKonOS.Server.csproj) 与各实现 csproj。PackageReference 在 Server 并不证明库只为该模块使用；JIT/框架共享页不按 DLL 文件大小计内存。发布 RID/文件摘要及运行时实载库需由测量 manifest 补充。

## 3. 持久权威与恢复所有者

| 域 | 保留在磁盘/Core/外部的权威 | 当前负责者与释放要求 |
| --- | --- | --- |
| Git | Core repository storage/身份与 workspace；GitBuild ledger、构建日志/产物和实际仓库；Engine 实际镜像 | 本代构建执行器及工作租约负责直到终态/清理。停用检查 `GitBuildLedger.InspectLedger`；重开不能靠清空台账伪造无责任 |
| Firewall | OS 实际防火墙规则、Core 特权授权/Helper 操作回执 | 请求 facade 无私有恢复循环；规则和授权不从 facade 内存重建 |
| Tailscale | control/login 两类持久 journal、Helper 已执行事实、daemon/tailnet 实际状态 | 本地 coordinator 只在本次调用中执行；跨代读取原 operation/恢复证明，未核实不能变成成功或自动重放。对象释放不清 journal |
| Terminal | OS 身份、Core workspace 环境和现存会话/原生树 | manager/session 是当前运行态权威；会话不是可在重启后自动重放的 shell 命令。明确 close 后树实际结束才释放 lease；EPERM/未知保持故障责任 |
| SystemPerformance | 采样历史与差分为易失态；OS 是进程/性能实际权威；授权和 Helper 权限属于 Core | owner 停止准入并等待工作，清理实例快照/缓存；不把历史当需重放的业务事务；S1 resident 不变 |
| Docker | Core `DockerStackOperationStore` 持久 stack/audit、Compose 定义，真实 Engine 状态 | 模块 coordinator startup reconcile；不自动重跑中断的 mutation；未知存储/Engine 结果阻止停用 |
| Certificate | Core certificate files/metadata/deployment、`HostOperationJournal`；模块 `data/certificate-operations.json`、renewal attempt/run repositories；CA 实际订单 | operation store/renewal worker 与挑战 owner；失败清理留保护。尚未完成轻调度及 CA/Nginx 联合中断恢复矩阵，不可宣称已可整体休眠 |
| WebServer | `data/website-publications.json`、Core Nginx metadata/operation journal、真实配置/服务和证书部署 | publication coordinator startup observe/reconcile；不重放原发布请求；证书/挑战依赖和故障清理结束前保留 owner |
| Proxy | Mihomo publication store/授权/设置及 TUN marker；SQLite profile/subscription repositories；Proxy operation/audit；sing-box store/授权到期状态；Squid configuration | Core publication scheduler/guard/TUN recovery/expiry 为唯一持续安全 owner；管理操作保存完整执行责任。Mihomo 实例锁和安装台账常驻，状态/就绪控制器及安装/移除发布执行器按任务解析。模块停用或作用域释放后，原恢复索引继续负责 |

## 4. 安装模块和不可漏算的 Host 根

[InstalledServerModule](../../RelaxKonOS.Server/Startup/InstalledModuleStore.cs) 当前把官方包代码加载进非 collectible `InstalledModuleLoadContext`，启动期注册根服务/路由；其私有依赖和后台责任取决于安装清单与包实现，不能预填为零。测量前必须保存实际 installed/trust/enabled 元数据摘要和每包资源清单；当前不支持把这种包作为已回收代码统计。

[InstalledIsolatedModule](../../RelaxKonOS.Server/Startup/InstalledIsolatedModule.cs) 只保存元数据/监督器；[ModuleHostSupervisor](../../RelaxKonOS.Server/Startup/ModuleHostSupervisor.cs) 根 HostedService 持有 entries、代次、连接及 probe；[ModuleHostGeneration](../../RelaxKonOS.Server/Startup/ModuleHostGeneration.cs) 拥有准入、IPC、进程树与停机责任。第三方 runtime 仍 resident、随 Server 启停，没有合作闲时退出协议。每个实际配置/受信安装项分别列 ModuleHost 私有运行时、依赖、缓存、线程与后代；原生树结束证明不等于空闲内存下降。

[HostServiceRegistration](../../RelaxKonOS.Server/Startup/HostServiceRegistration.cs) 的 Docker Engine/代理、Nginx、Kestrel证书、Frp/ProcessGuardian 以及 [ProxyRuntimeRegistration](../../RelaxKonOS.Server/Startup/ProxyRuntimeRegistration.cs) 的发布/安全服务是 Host 常驻成本。数据库工厂、身份/认证、审计、安装/组件管理、模块协调器/目录/计划、共享 Helper 和 HTTP handler 池也属于根图。它们可能因测量业务首次触发而增长，不能把整个 Server 前后差额全归给刚唤醒模块。外部长期服务不一定是 Server 的 OS 后代，须额外做共享服务附表并标明权限不可见项。

## 5. 审计验收与后续决策约束

**实现且已验证（源码审计）**：9 个编译目录模块和两种安装运行模式已覆盖到注册入口、后台所有者、缓存/依赖、外部执行、恢复权威与当前释放层级。现有 Git/Firewall 代码回收、Tailscale 对象和 Terminal/ModuleHost 树的运行验证仍由[边界设计 §39～41](RelaxKonOS.ServerModuleBoundaries.Design.md)拥有；本表不扩大它们的验证范围。

**实现但实机未验证**：本清单不能单独确认运行时不存在额外根引用、全部原生分配/私有库可回收或生产完整树成本。安装包私有资源、特定身份不可读进程以及按业务数据规模增长的内存，必须由实际 manifest、运行指标/必要堆审计补证。

**未实现或仍有阻塞**：同构建双平台交错至少三轮真实业务/释放/唤醒基线及全部指标，不因清单完成而关闭 MB-01/RES-01。会话/包/数据/连接数必须在运行前固定，无法读取的 PSS/私有驻留页/堆指标标缺。

下一批候选先比较 Terminal 独占 worker 的整树稳定态与会话伸缩成本、Git/Firewall 自然释放后的总进程指标及唤醒延迟，再考虑 Tailscale 独立代码是否值得；Docker/Certificate/WebServer/Proxy 的下一步先过持续责任和持久调度交接门槛。优先级须结合基线噪声、独占成本与实现风险，不按此表中条目数、DLL 大小或旧单次 RSS 排序。SystemPerformance S1 继续冻结。
