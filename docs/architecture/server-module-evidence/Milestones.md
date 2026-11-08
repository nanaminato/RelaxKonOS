# 模块联合阶段验收与测试设施

跨模块阶段门槛和共享隔离设施的原始记录。历史跟踪表按记录日期解读，当前状态见进度页。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [8. 实施顺序与跟踪项](#8-实施顺序与跟踪项)
- [20. Linux ModuleHost 目标修复、Proxy 工作租约与订阅独立代码（2026-11-04）](#20-linux-modulehost-目标修复proxy-工作租约与订阅独立代码2026-11-04)
- [38. 模块里程碑与阶段性交付（2026-11-04）](#38-模块里程碑与阶段性交付2026-11-04)
- [39. MB-05A、MB-06A 与 Terminal-T1 阶段验收（2026-11-04）](#39-mb-05amb-06a-与-terminal-t1-阶段验收2026-11-04)
- [42. MB-01A：当前资源清单与分层测量（2026-11-05）](#42-mb-01a当前资源清单与分层测量2026-11-05)
- [44. MB-03 离线 BuildKit 与 MB-05 隔离设施续核（2026-11-06）](#44-mb-03-离线-buildkit-与-mb-05-隔离设施续核2026-11-06)
- [MB-05 外部设施隔离预检](#mb-05-外部设施隔离预检)
- [50. MB-03/05 隔离测试设施复核（2026-11-06）](#50-mb-0305-隔离测试设施复核2026-11-06)
- [MB-05 既有 Linux 测试机续核](#mb-05-既有-linux-测试机续核)
- [MB-05 Docker 202 响应排空](#mb-05-docker-202-响应排空)
- [MB-05 完整 Server 自然续期与停机排空](#mb-05-完整-server-自然续期与停机排空)
- [MB-05 完整 Server 自然续期的认证取消](#mb-05-完整-server-自然续期的认证取消)

## 8. 实施顺序与跟踪项

以下保留原 §8 跟踪表及迁移前的增补。当前状态见[实施进度](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。原表中的章节号沿用旧文档编号，可从[旧章节入口](../RelaxKonOS.ServerModuleBoundaries.Design.md#旧章节入口)定位对应证据页。

| 编号 | 实际完成与证据 | 剩余阻塞 | 整项验收条件 |
| --- | --- | --- | --- |
| MB-01 | **部分完成，整项开放**。MB-01A 资源清单与分层基线见 §42。[MB-01B](../RelaxKonOS.ServerModuleBaseline.20261106.md) 验证双平台启动期采树、交错认证 Git、自然释放和 Terminal Hub，Windows 另完成真实 Firewall 三轮 | Linux 同制品的真实 Helper 联合矩阵仍缺，System 模式探索中终端退出证明缺失且共享 Helper 触发宿主代理恢复。外部共享服务归属、长时间与非空仓库负载继续开放 | 隔离 Helper 和宿主持久恢复责任后补 Linux 全量联合矩阵，保留缺失与原始证据，关联 RES-01/03 |
| MB-02 | **部分交付**。Git Contracts/独立实现和窄 Host 端口，Proxy 订阅转换契约/私有实现，Firewall-F1 独立契约/执行代码及构建门禁；回收验证见 §10/11/20～22/41 | 全官方包仍非唯一 collectible ABI；其他模块尚有根服务和实现依赖 | 全契约不引用 Server/实现；Host/模块互引构建门禁；发布包与私有依赖按唯一当前 ABI 验证 |
| MB-03 | **Git 有限切片交付**。真实仓库/CLI/凭据/控制台与构建执行项目，100轮 ALC 和保留完成请求验证（§10/11）；镜像清理责任与真实 Engine 标签检查见 §43；派发检查点和跨进程恢复见[构建派发对账](Git.md#mb-03-git-构建派发检查点与重启对账)，专属仓库见[HTTPS 源仓库夹具](Git.md#mb-03-专属-https-源仓库夹具) | 真实 Docker/BuildKit 执行中取消、清理、恢复与 ALC 联合矩阵仍受 RootlessKit 启动限制；生产内存/冷启动缺失 | 真实任务/流/持久故障责任结束才回收；两平台循环不累积句柄/代码代次；实际引擎恢复与资源目标达到门槛 |
| MB-04 | **常驻边界阶段交付**。共享读取、guard/TUN、到期与发布恢复单一 owner，后台工作租约、订阅转换预算及独立代码（§12～22）。完整认证入站发布恢复、[Core 同次停机排空](Proxy-Safety.md#mb-04-同次停机的-proxy-安全责任与恢复)、[宿主代理切换](Proxy-Safety.md#mb-04-linux-宿主代理切换验收矩阵)和[直连监听显式补偿](Proxy-Publication.md#mb-04-linux-直连监听显式补偿)已有 Linux 实机证据。[订阅后台执行图](Proxy-Subscriptions.md#mb-04-订阅后台执行图的可回收交接)使用原持久批次计划和原操作 ID，[直连档案与设置执行器](Proxy-Publication.md#mb-04-直连档案与设置发布执行器)共用原发布检查点 | Proxy 整体仍 resident。[可观察的直连监听补偿](Proxy-Publication.md#mb-04-直连监听补偿应答丢失的效果对账)可凭原证据续接，其他未知配置、完整网络故障矩阵、管理执行图交接和动态休眠未完成。系统代理的临时端口选择见[guard 监听切换](Proxy-Safety.md#mb-04-系统代理-guard-监听切换)，重叠的概览读取见[共享概览采样](Proxy-Observation.md#mb-04-mihomo-重叠概览采样)和[跨路由运行时采样](Proxy-Observation.md#mb-04-mihomo-跨路由运行时采样) | 系统代理/TUN/到期责任在重启及休眠后连续可靠；未知结果不重放；正式包/代码释放与生产收益分别验证 |
| MB-05 | **MB-05A 常驻后台责任阶段交付**（§39）。三域真实工作租约、准入/Stop 同锁、实际任务及清理排空。Docker 在途停止与原回执恢复见 §45，独立 Pebble、Nginx、Engine 实测见 §52～54。完整认证网站 WebRoot 签发、崩溃恢复、取消与 SIGTERM 联验通过。[Docker Compose](Docker.md#mb-05-docker-compose-独立执行程序集)、[ACME SDK](Certificates.md#mb-05-acme-sdk-独立执行程序集)、[撤销恢复](Certificates.md#mb-05-acme-撤销确认与本地恢复)、[ARI 断流恢复](Certificates.md#mb-05-ari-断流与原扫描恢复)、[Kestrel TLS](Certificates.md#mb-05-kestrel-tls-轮换与撤销连续性)、[动态证书链](Certificates.md#mb-05-kestrel-动态证书链)、[Nginx 证书轮换](WebServer-TLS.md#mb-05-nginx-显式证书轮换与原重载回执)、[Nginx 续期自动部署](WebServer-TLS.md#mb-05-nginx-续期自动部署)、[网站发布](WebServer.md#mb-05-网站发布独立执行程序集)、[直接 Nginx 重载](WebServer.md#mb-05-直接-nginx-重载独立执行程序集)、[直接 WebRoot 启用](WebServer-TLS.md#mb-05-直接-webroot-启用执行器)和[Nginx 集成](WebServer.md#mb-05-nginx-集成检查点与执行代次)已接入独立执行程序集。[WebRoot 具名补偿](WebServer-TLS.md#mb-05-webroot-已知拒绝的具名补偿)已通过真实 reload 中断恢复 | 三域仍 resident。Linux 系统包 Nginx 的[启停与重启](WebServer.md#mb-05-linux-nginx-启动停止与重启的原回执)接入独立执行器。其他 Nginx 形态、完整故障矩阵、缺失身份的 CA 订单核实与整机掉电开放 | 后台 owner、终态与禁用门槛通过。按真实工作与安全责任边界推进独立执行器和动态休眠 |
| MB-06 | **MB-06A/06B 阶段交付**（§39/40），Windows LocalSystem/S4U 安装夹具及 Core／Helper 崩溃恢复实测通过（§48） | 正式发行服务全链、Linux 固定启动器跨代故障、SDK 合作 drain/后台任务申报仍开放 | 整项仍须合作 drain/后台申报、正式安装多代信任/撤销/崩溃矩阵与旧回调隔离，验收后才启用闲时退出/重启 |
| MB-07 | **SystemPerformance S1 已交付并冻结范围**（§23～37）；Firewall-F1 代码回收与 Tailscale TS-01 持久责任跨对象代次验收交付（§41）；Terminal-T1/T2 阶段交付（§39/40） | SystemPerformance 与 Terminal 仍 resident，生产收益/完整原生压力未测；Terminal 当前账号 Windows 树与 Linux 本地/永久降权 Helper 树已实测，Windows 安装 Helper/S4U、宿主崩溃/孤儿恢复矩阵仍开放；其余模块代码边界未验证 | Terminal-T1/T2 会话/当前原生树阶段已验收，断线不杀会话；整项还须安装/崩溃/压力矩阵及收益证据，再决定独立程序集，不能将 S1/T2 当作整项关闭 |
| MB-08 | **未完成**。已有当前诊断 DTO/跨端管理契约及局部打包/ModuleHost 验证 | 安装包非完整目标 ABI；真实业务同构建内存/冷启动门槛、完整平台故障矩阵未有 | 发布包/私有依赖/各端调用一致；Windows/Linux 整个进程树与冷启动 p50/p95、错误率达到预先记录门槛，关联 RES-06/07 |

首个官方代码卸载试点采用 Git，不直接从 Proxy 全量拆分开始。MB-04 的依赖审计可与 MB-01 期间推进，但安全执行器迁移必须依赖已验证的持久调度与租约；不能先删旧 worker 再补恢复。MB-06 可在官方 ALC 试点之外独立推进，其结果不改变官方宿主选择。

每个实施项提交必须同步模块、应用和追踪文档；涉及受管软件安装/更新/恢复时，先遵循受管运行时设计并更新相应 MR/CC 状态。包维护继续固定当前有效制品，睡眠和唤醒期间不得换成 latest。模块 ID 或配置键拆分直接更新所有内仓调用、测试和示例，不添加旧 `proxy` 别名或并存配置解析。

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

## 38. 模块里程碑与阶段性交付（2026-11-04）

### 当前执行约定

MB-01～08 的实际状态、阻塞和整项验收以 §8 为准。本节拥有下一阶段模块里程碑，旧章节保留当时证据，不再将后续小项优化当成新的默认推进方向。里程碑必须同时交付所有权边界、故障/停止行为、仓内调用、应用文档与相应验证；达到阶段门槛后继续下一模块，不等待逐个小问题确认。共享契约/根注册的变更由主执行者统一集成，独立领域实施可并行。

### SystemPerformance S1：交付并冻结

当前可交付范围是 **resident 模块的有界缓存/输入和完整实际工作 owner**：进程查询/控制、图标队列、采样、REST 等待/缓存、连续 Hub 连接/组成员及广播和 HTTP 结果执行均已取得相应真实工作保护，关闭不提前释放真实 Task；已验证的预算与完整限制见 [SystemPerformance S1 证据](SystemPerformance.md)。最后代码交付基线为 `f16521fd`。不将方法租约、队列预算或受控分配回归称为整个模块可休眠、程序集已回收或生产内存收益。

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

## 42. MB-01A：当前资源清单与分层测量（2026-11-05）

本阶段以 `443b0b7e1143fca826a51239afd19ea17022ca6a` 为产品源码基线，开始时master工作区干净；本次不修改产品模块行为、默认宽限期或SystemPerformance S1。[资源清单](../RelaxKonOS.ServerModuleResources.Inventory.md)覆盖9个内置模块、官方安装包和第三方ModuleHost的根引用、后台责任、缓存/私有依赖、外部进程、持久恢复权威与释放层级。[测量方案](../RelaxKonOS.ServerModuleBaseline.Protocol.md)固定完整矩阵及工具限制，[本批报告](../RelaxKonOS.ServerModuleBaseline.20261105.md)拥有逐轮数值、环境、证据、阻塞与下一阶段门槛。

当前验收严格区分完整宿主启动/闲时控制与Git/Terminal局部生产服务业务夹具。后者核验真实CLI、Dormant/新代唤醒和真实PTY输出/关闭，但没有完整Host/认证/Hub/Helper，手动TrySleep与5秒窗口不等于原300秒自然闲时与长期稳态。Linux静态全关因无法核实固定主机路径Proxy恢复责任被原门禁拒绝；未清账、改权限、改防火墙/Tailscale或移除必要责任。

MB-01A不关闭MB-01/RES-01：真实Firewall只读及回收前后指标、完整宿主认证业务、自然释放/再唤醒、Hub重连、启动峰值、外部共享服务归属和退出后全树核验仍缺。对象释放、ALC仍待自然回收、会话树关闭或静态配置差额均不解释为生产内存下降。下一步先补MB-01B正式宿主矩阵，再按证据评估Terminal容量/worker、Git/Firewall收益与常驻领域责任迁移；SystemPerformance S1继续冻结。

## 44. MB-03 离线 BuildKit 与 MB-05 隔离设施续核（2026-11-06）

### MB-05 外部设施隔离预检

- Engine/Compose 使用缓存 Alpine 3.22 和随机项目，无外部网络。只向测试容器注入 SIGKILL，真实 Engine 观测到 exited:137。启动后容器 ID 相同、启动时间更新、原命名卷内容可读；按项目标签确认容器和卷已清空。
- Nginx 以普通用户和独立配置在回环高端口提供 TLS，客户端用测试证书验证响应。向该实例发 TERM 后确认 PID 文件移除和 HTTPS 请求失败；使用原配置及证书启动新 PID，原 HTTPS 响应恢复。正常退出并清理临时目录。
- 两项均直接调用外部设施。RelaxKonOS 的真实管理 Task、响应及原 operationId 持久化恢复、Engine daemon 重启、真实 ACME CA 订单和挑战连续性尚未覆盖。测试证书由 OpenSSL 本地生成。下一阶段以独立 Engine 实例、独立 Nginx 执行配置、可控 CA 和专用挑战域运行应用级故障矩阵。

本机原始探测日志保存在 .artifacts/mb03-buildkit/。

## 50. MB-03/05 隔离测试设施复核（2026-11-06）

从 `8ed2cc74` 继续，选 MB-05 跨进程连续性作为下一项。以既有固定 SSH 主机密钥连接 Ubuntu 测试机，完成只读预检。共享 Docker 与 Nginx 服务均处于 active。测试账户无法访问共享 Docker socket，未发现其用户级 Docker socket。宿主有 dockerd、Nginx 与 .NET，PATH 中未找到 RootlessKit、slirp4netns、Pebble 或 step-ca。AppArmor 的非特权 user namespace 限制为 1，普通测试账户执行 `unshare -Ur true` 失败。没有启动第二个 daemon、修改安全策略或注入共享设施故障。

**已实现且已验证**：沿用 §45 的真实 Engine 在途 CLI 排空与同进程协调器重建证据，以及 §44 的独立 Nginx TLS 连续性证据。本次新增证据仅为上述只读资源检查。

**已实现但实机未验证**：§39 的 WebServer 与证书后台责任测试仍使用受控外部动作。实际 CA 请求或 Nginx 发布中断时的完整 Task、响应、终态持久化及清理责任仍待观察。

**仍有阻塞**：MB-05 需要可独立重启的 Engine 和专属工作负载，以及可控 ACME 目录、受信 CA 和仅用于验收的挑战名称及解析。可用隔离 Linux 实例同时解决 Engine 和 MB-03 的 RootlessKit 前提。MB-03 还需要 Git CLI 与 builder 均可达且信任其证书的专属 HTTPS 443 仓库。后续只对专属测试实例注入故障，以原 operationId 核对回执与副作用次数。MB-04 的安全 owner 交接与真实网络故障验收仍开放，Proxy 保持 resident。此次没有生产内存测量。

### MB-05 既有 Linux 测试机续核

以 `37fe327b` 发布 Linux x64 Release 制品。沿用固定 SSH 主机密钥，在随机临时目录核对归档 SHA-256 `dd06f7c1561084a63c295bbadbc4f0ff01f54f0656f874212e39ef267fbc9d3c` 后执行两项测试。

- 普通账户跨进程夹具通过。原 operationId `e8a6ae83-63c0-4bda-83bf-0f26ba3592b3` 恢复为 Interrupted，文件副作用见证为一次。
- 真实 Engine 使用专属 Compose 项目 `ad04live870f40d4`。Engine 接受停止后，实际 CLI 在租约保护下排空。同一进程内重建的协调器以原 operationId `da1fc630-653a-41f0-8524-b93b3ce07ce1` 恢复 Interrupted。显式启动容器后，同键请求返回原回执，容器保持运行且停止见证仅一次。部署的即时快照为 Succeeded，独立后续观察确认 worker 退出码 7，原回执未改写。按项目标签核对容器、网络及卷清理完毕，上传目录已删除。

证据见 `.artifacts/mb05-linux-current-run.log`、`.artifacts/mb05-linux-process-current.log` 和 `.artifacts/mb05-linux-engine-current.log`。两项分别覆盖跨进程台账和真实外部动作，真实 Engine 下的整个 Server 崩溃仍待联合验收。共享 Engine 和 Nginx 未重启。CA 与 Nginx 应用层中断、独立 Engine daemon 重启及 MB-03 RootlessKit 阻塞继续开放。

## MB-05 Docker 202 响应排空

生产 Docker stack 受理回执在实际执行 HTTP 202 结果时取得独立 resident 租约，直到序列化与写出任务返回才释放。Linux x64 Release 专属 Engine 实测客户端先断开，原停止任务落盘并释放业务责任时，响应屏障仍持有独立租约。释放屏障后响应退出，租约归还。同键重试返回原操作 7b2f20e6-a12e-407c-b6b0-e9bd1d6971e6，未重放停止。日志为 .artifacts/mb05-docker-response-live.log，归档 SHA-256 为 8996424ff81e7112007f0997ad5f96cdebcd2762144c1bb511bfbe308d39b0f3。生产路由和租约运行在 Kestrel 夹具中，完整 Server 认证启动仍开放。

MB-05 将 Docker 已实机验证的响应租约机制复用于证书、WebServer 操作和网站发布 HTTP 202。响应执行独立持有对应 resident 模块租约，直至执行退出。Windows Release 的证书 HTTP 绑定与回执、WebServer 和模块边界专项通过。证书绑定测试确认成功响应取得租约。新路由的实机断线和排空仍待验收。

Linux x64 Release 同制品通过证书 HTTP 绑定、回执、WebServer、模块边界回归，以及隔离 Nginx Configuring 阶段发布恢复。原发布 3c573041-4d2f-436b-98a1-4766d8cd1720 恢复为未知应用结果，发布重载一次。首轮因 Windows 打包未带 Linux apphost 执行位而中止，专属解包目录修正后通过。日志为 .artifacts/mb05-receipt-linux-final.log，归档 SHA-256 为 45f9d3b570a6c265a53b1d21f05fe10bc3aec29939773bfbf51f472a72f544df。此批次的 Nginx 发布故障走协调器入口，新增路由的实机断线联合排空仍开放。

MB-05 证书生产路由在 Linux Kestrel 夹具上通过真实客户端断线测试。原证书绑定成功后，同键回执在响应头屏障处持有租约；客户端断线后租约仍在，实际响应退出后归还。再次提交原键得到原 Succeeded operationId。日志为 .artifacts/mb05-cert-drain-linux.log，归档 SHA-256 为 93c779be34d229c4f9b1a122bdd7f44669089ec06a5eb2100876932844cd081f。此项使用测试身份、响应屏障和观测租约，真实 CA 签发中断证据见 §52。

Linux x64 Release 隔离 Nginx 发布恢复已走生产网站发布 HTTP 同键入口。原操作 663e2f69-beaa-4ea1-88e9-9178bf2d5421 在 Configuring 中断。恢复后的同键响应在写出前遇到真实客户端断线，实际响应退出前保留租约。后续重试返回原 Interrupted 回执及 website.site_application_unverified，HTTPS 继续代理，发布只重载一次。日志为 .artifacts/mb05-website-drain-live.log，归档 SHA-256 为 20a7a124f5311bc7b731d000d5516e87d3f2448eab6c0158ca11427b6740c6a7。故障由原协调器工作进程触发，Kestrel 夹具提供身份、响应屏障和租约观测。

Linux x64 Release 另通过 Verifying 阶段的 HTTP 回执断线实机复测。原操作 d4a11877-ab18-40c6-a980-6785a37820e8 已持久化应用关联后中断，同键 HTTP 响应断线后排空。重试返回原 Interrupted / website.recheck_required，应用关联、HTTPS 和发布重载计数均符合预期，发布重载一次。日志为 .artifacts/mb05-website-verifying-drain-live.log，归档 SHA-256 为 b90a472b452bc97354656380639619cd762a124fed9f54ef77703e2d33dc4ce5。

Linux x64 Release 通过独立 Nginx reload 的生产 HTTP 同键回执断线验收。原操作 4b43a6bc-375d-4257-8765-8b16899e8ee6 在真实 reload 后中断，原实例已从管理发现移除。客户端断开后，租约保持到回执响应退出，重试返回原 Interrupted 回执，原 Nginx 继续提供 HTTPS，日志只有一次 reload。同一制品的 Verifying 发布回归也通过。日志为 .artifacts/mb05-nginx-reload-drain-live.log 和 .artifacts/mb05-nginx-shared-replay-live.log。HTTP 身份、响应屏障和租约观测由 Kestrel 夹具提供。

## MB-05 完整 Server 自然续期与停机排空

Linux x64 Release 在既有测试机的私有网络、挂载和 PID 命名空间复用完整认证 Server、独立 Nginx WebRoot 和 Pebble。专属 CA 签发短期证书并关闭可选 ARI 响应与授权复用，以覆盖正常到期兜底和真实 HTTP-01。未改机器时钟、生产启动延迟或扫描台账。生产 Worker 自然计时后持久化扫描 `ce78914f-c226-4a2c-9eb2-5a21640301a5` 和原续期 `370b24cb-72ce-4778-9ed3-78871a4477ca`。

TLS 中继收到实际 CA 挑战响应 200 后保留响应。Server 接收 SIGTERM 后监听退出，原续期仍为 Running，挑战文件仍在，进程持续等待。释放实际响应后，原操作写入 Cancelled，挑战文件清理，Server 正常退出。重启重新认证后，生产 Worker 在原启动周期内记录原下次到期时间并推迟扫描，台账字节和原证书指纹一致，按 ID 读取原回执。CA 共两张预期订单、一次签发提交，停机排空和重启零新增。宿主 Nginx、Docker、Mihomo PID 与主机文件指纹前后一致。日志为 .artifacts/mb05-natural-renewal-live.log，归档 SHA-256 为 c10e20014eef2acc821c11e609450002a8efa8531b28bbf1c73318ea08c78be1。Windows Release 模块边界及 Linux 独立 Pebble 回归通过，后者归档 SHA-256 为 d6e58f4bda1f2aeed11b7226d1157cf582f97a4a3551a9b9f0529825b59d08ef。证书继续 resident，独立程序集和生产内存测量仍开放。

## MB-05 完整 Server 自然续期的认证取消

沿用短期证书、生产自动扫描和独立 Nginx/Pebble 的 Linux Release 夹具。真实 CA 挑战响应 200 被中继暂缓时，匿名取消返回 401，认证取消返回原 `Running / cancel-requested`，重新读取原回执与磁盘均确认操作仍在途，挑战文件仍保留。释放响应后写入原 Cancelled，挑战清理，重复取消返回相同终态。重启重新登录后，读取及重复取消仍返回相同回执，原 CA 订单只读返回 `ready`，生产 Worker 延用原下次到期时间，扫描台账字节和活动证书指纹一致。未跨越下一次实际日扫描。

取消扫描 `126f084a-6176-40ef-9233-168d17f519a0` 的原续期为 `6db6e593-48c5-4ea6-9dfe-6d139b157f9e`。SIGTERM 对照扫描 `da76552f-4183-43f0-9eee-74add6eb5a2e` 的原续期为 `e5256887-2dd9-483f-94b8-098c4c493323`。两轮各两张预期订单、一次签发提交，取消、查询与重启零新增。宿主 Nginx、Docker PID 及代理环境和 Nginx 配置指纹一致。

日志为 `.artifacts/mb05-natural-renewal-cancel-final-schedule-cancel.log` 和 `.artifacts/mb05-natural-renewal-cancel-final-schedule.log`。归档 SHA-256 分别为 `e9f821a67256fde1807d26807b509f4ba6ee4649ad915cd1a5a7cbea5733c3c1` 和 `58af974738dcc21117bdec6b127811d88eeaef83e6f245c1d2659c132f8314d7`。首轮旧 Server 制品未通过取消后查询原 CA 订单，最终使用含 `9b8764db` 的制品通过。证书保持 resident。
