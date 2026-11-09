# MB-04 Proxy 发布与恢复

Mihomo 档案、设置、发布检查点与原回执恢复，以及 sing-box 管理发布。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [12. Proxy 共享读取与写入/安全边界（2026-11-03，MB-04 部分）](#12-proxy-共享读取与写入安全边界2026-11-03mb-04-部分)
- [16. 发布恢复临时作用域与 profile 执行图审计（2026-11-03，MB-04 部分）](#16-发布恢复临时作用域与-profile-执行图审计2026-11-03mb-04-部分)
- [18. Proxy 任务准入、停机与禁用责任门禁（2026-11-03，MB-04 部分）](#18-proxy-任务准入停机与禁用责任门禁2026-11-03mb-04-部分)
- [19. Proxy 常驻责任与完整执行寿命收尾（2026-11-03）](#19-proxy-常驻责任与完整执行寿命收尾2026-11-03)
- [22. Proxy 依赖守卫与测试机回收续核（2026-11-04，MB-02/04 部分）](#22-proxy-依赖守卫与测试机回收续核2026-11-04mb-0204-部分)
- [MB-04 Helper 发布后运行状态核验](#mb-04-helper-发布后运行状态核验)
- [MB-04 Helper 在停止失败后补偿](#mb-04-helper-在停止失败后补偿)
- [MB-04 sing-box 发布事务的 Unix socket 验收](#mb-04-sing-box-发布事务的-unix-socket-验收)
- [MB-04 Mihomo 持久发布调度索引](#mb-04-mihomo-持久发布调度索引)
- [MB-04 入站发布按原 Helper 回执恢复](#mb-04-入站发布按原-helper-回执恢复)
- [MB-04 在途入站发布的原 ID 补偿](#mb-04-在途入站发布的原-id-补偿)
- [MB-04 原管理发布队列的恢复](#mb-04-原管理发布队列的恢复)
- [MB-04 完整认证 Server 的原入站发布回执](#mb-04-完整认证-server-的原入站发布回执)
- [MB-04 完整认证 Server 的在途入站发布补偿](#mb-04-完整认证-server-的在途入站发布补偿)
- [MB-04 Core Mihomo 发布恢复的停机排空](#mb-04-core-mihomo-发布恢复的停机排空)
- [MB-04 Mihomo 运行时与安装执行作用域](#mb-04-mihomo-运行时与安装执行作用域)
- [MB-04 完整 Server 在真实 Mihomo 回执等待时排空](#mb-04-完整-server-在真实-mihomo-回执等待时排空)
- [MB-04 Proxy 管理队列的 Host 停机所有权](#mb-04-proxy-管理队列的-host-停机所有权)
- [MB-04 Proxy 设置写入的工作租约](#mb-04-proxy-设置写入的工作租约)
- [MB-04 Proxy 档案发布的工作租约](#mb-04-proxy-档案发布的工作租约)
- [MB-04 Mihomo 直连 reload 回执丢失](#mb-04-mihomo-直连-reload-回执丢失)
- [MB-04 Mihomo 直连 reload 持久检查点](#mb-04-mihomo-直连-reload-持久检查点)
- [MB-04 Mihomo 直连具名恢复](#mb-04-mihomo-直连具名恢复)
- [MB-04 Mihomo 直连档案提交恢复](#mb-04-mihomo-直连档案提交恢复)
- [MB-04 Mihomo 直连设置提交恢复](#mb-04-mihomo-直连设置提交恢复)
- [MB-04 Linux 直连监听显式补偿](#mb-04-linux-直连监听显式补偿)
- [MB-04 直连监听补偿应答丢失的效果对账](#mb-04-直连监听补偿应答丢失的效果对账)
- [MB-04 直连档案与设置发布执行器](#mb-04-直连档案与设置发布执行器)

## 12. Proxy 共享读取与写入/安全边界（2026-11-03，MB-04 部分）

### 已实现

原 `IProxySettingsService` 将读取、修改设置和系统代理对账组合在同一接口中；`DockerProxyResolver`、订阅下载策略、安装材料选择等只读调用因而依赖 `MihomoSettingsService`，构造时连带控制器与发布执行图。现已直接拆成 `IProxySettingsReader`、`IProxySettingsWriter`、`IProxySystemProxyReconciler`，同步所有内仓调用、测试和验收工具，不保留旧接口、旧 Get 入口或类型转发。公开 HTTP 路由/DTO 和权限保持当前契约；GET 使用 reader，PUT 使用 writer。

Core 的 `ProxySettingsReader` 只依赖 `IProxyPlatformPaths`，读取 `mihomo-settings.json`，不构造引擎、控制器、安装/发布执行器，也不改设置、系统代理或 TUN。Docker 与宿主下载路由、订阅下载策略、配置事务的设置观察以及安装引导均改用该端口。该切片中配置写入、系统代理对账和 TUN 运行控制映射到同一个 `MihomoSettingsService`；§14 随后拆出对账实现，仍保持同一发布锁和副作用执行权威。

读取采用 64 KiB 上限和有界、固定文件共享语义；拒绝路径链接、空文件、目录替代、无效 JSON、重复属性、未知字段和无效端口/日志级别/宿主地址。只有文件或父目录确实缺失才返回产品默认值，损坏/不可读不能回落为默认监听端口。默认值、可选设置规范化及严格解析由 reader 统一拥有，冻结设置意图也复用该解析；不改变设置文件的有效字段、凭据保护或冻结身份。

受管代理被选中但设置不可读时，Docker/运行时出站解析返回原 `ManagedProxyUnavailable`，不继续默认端口或直连；独立的自定义代理选择仍按原作用域/旁路规则解析。保存地址必须为字面 IP；出站可用性还核对该地址属于本机，显式监听器匹配保存地址，不能凭其他本地监听器把远端地址标成受管可用。修复元数据并按原规则刷新解析后可以恢复。首次安装的只读材料选择遇到损坏设置映射为 `runtime.settings_drift`，在创建控制器凭据、原生工作或发布意图之前拒绝；已受理安装重放继续只使用原冻结材料，不额外读取当前设置。

Windows 用户态系统代理的 HKCU 原语及 bypass 拼接从管理类移至 `HostSystemProxyService`，消除 Host 平台适配器对 `MihomoSettingsService` 的静态引用。执行条件和行为沿用原实现：服务账户/非交互会话不得借此改用户代理，Linux 仍走原 Helper 的封闭操作。本轮没有实改 Windows 注册表或宿主系统代理的验收，不扩展原平台支持范围。

### 当前所有权与尚未交接的职责

以下记录为共享读取切片时的所有权；后续 guard 对账实现与引用图见[系统代理 guard 审计](Proxy-Safety.md#14-系统代理-guard-窄执行边界与安全责任审计2026-11-03mb-04-部分)。

| 职责 | 当前所有者 | 本轮结果与限制 |
| --- | --- | --- |
| 已保存代理设置观察 | Core `ProxySettingsReader` | 已与写入/控制器构造图分离；读不唤醒管理对象 |
| 设置写入、系统代理对账、TUN 运行控制 | Core 同一 `MihomoSettingsService` 的三个类型化接口 | 保留单例/发布锁；尚未拆成可回收执行器 |
| 持久配置/安装/移除发布恢复 | Core `MihomoPublicationRecoveryService` 与原领域台账 | 未迁移执行权威；冻结、撤销、补偿和原操作回执仍由原实现复核 |
| 系统代理 guard / TUN 标记恢复 | `ProxyModule` 注册的 `SystemProxyGuardHostedService` / `ProxyRecoveryHostedService` | 原注册时机/循环保持；guard 分别持有 reader 和 reconciler，没有提前停止或新增第二个执行者 |
| sing-box 授权到期执行 | `ProxyModule` 的 `ProxyServerExpiryService` | [每轮临时解析执行实例](Proxy-Safety.md#13-sing-box-到期调度与执行实例分离2026-11-03mb-04-部分)；未交接到 Core 调度，仍不能随模块停机丢弃责任 |
| OS 系统代理写入 | Core `HostSystemProxyService` / 既有 Helper | 平台原语归属已纠正，权限未扩大 |
| profile/订阅、Squid、入站业务管理 | 现有聚合 `ProxyModule` 服务图 | 仍有根服务/Scoped 服务和背景责任，未实施新模块 ID 或代码回收 |

Proxy **仍为 resident**，未新增可释放标记、休眠工厂或“已释放整个 Proxy”的状态。默认启用 Proxy 时，guard/恢复职责仍可能构造并保留管理图；本轮证明的是只读消费者不再引入该图，不证明默认 Server 常驻内存已下降。MB-04 的持久调度索引、职责交接、动态执行器及真实故障矩阵继续开放，不能用读端拆分跳过这些条件。

### 验证证据

- Windows 设置边界 **31 项**、Linux **32 项**（增加真实 symlink 拒绝）通过。测试把管理 writer 的构造器替换成会失败的计数器，验证未注册 Proxy 模块时的真实监听器/出站查询不会构造它；也覆盖损坏、重复/未知字段、超限、取消、目录替代与修复后恢复。
- 两平台系统代理能力/PAC 准入、apply/persist 失败补偿、guard 与禁用竞争、待发布拒绝检查通过；受控真实 HTTP 代理请求、旁路、各作用域和停止监听器拒绝直连通过。
- 配置发布专项 Windows **84** / Linux **85** 项；默认注册 **8** 项验证三类写入/安全接口为同一单例、guard 仅注册一次；安装发布 **88**、卸载发布 **92** 项通过（Linux 全部，Windows 安装/卸载亦通过）。未调用真实宿主安装或服务变化来冒充这些台账/传输夹具的范围。
- Server、Server.Tests、ManagedRuntimes.Tests 和 ServerModules.Tests 已构建；framework-dependent .NET 10 Linux 发布通过。未重新部署生产 Server，没有改 appsettings 或组件数据。

证据位于 `.artifacts/proxy-boundaries-20261103/`，Linux 最终合并记录为 `linux-final.log`；独立远端目录为 `/home/nanami/component-catalog-20261102/phase23/proxy-boundary-{server,runtimes}/`。Windows 首次受限沙箱运行安装夹具遇到文件替换权限错误，允许沙箱外文件操作后最终 88 项通过；出站测试夹具的 Accept/Dispose 停机竞态亦已修正为仅在取消后接纳 ObjectDisposedException。不把这些失败运行作为通过证据。物理内存/长期 CPU 与整个 Proxy 生命周期仍须由 MB-01/04/08 后续实测。

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

## 22. Proxy 依赖守卫与测试机回收续核（2026-11-04，MB-02/04 部分）

保留 §21 及其余全部未提交实现。Proxy 转换实现项目现在在构建准备阶段只允许引用仓库内精确路径的共享 Proxy.Contracts 项目；契约项目只允许 SDK 隐式 .NET 框架依赖。两个项目均拒绝额外项目、显式二进制、包、框架和 COM 引用。当前转换器没有私有依赖；未来增加依赖须先设计其共享类型身份、加载与释放规则，再更新守卫，不能由默认上下文隐式托管新的私有实现。此约束是开发期防回归规则，不是受信官方代码的安全沙箱，也不是所有模块的完整 ABI 验收。

新增 `Tests/ServerModules/Check-ProxyModuleBoundary.ps1`：两个当前项目正向通过，分别注入 Server、Git 实现、其他目录同名契约项目、二进制、包、ASP.NET 框架和 COM 引用，全部在预期守卫失败，共 **16 项**。夹具通过临时导入注入依赖，不修改实际工程或还原不存在的包；可用 `pwsh -NoProfile -File Tests/ServerModules/Check-ProxyModuleBoundary.ps1` 复跑。首次守卫未排除 SDK 隐式框架、随后路径条件引用错误的失败已修正，最终正常构建和 Linux 发布通过，不把调试失败计作通过。

驻留专项增加真实转换失败路径：主动使私有实现抛异常，保留返回的 Host 中性异常和堆栈后观察代码回收，确认无 InnerException 泄漏或异常强引用残留。Windows 与 Ubuntu 26.04 x64 测试机最终各 **28 项**通过，包含 §21 的预算饱和、32 个并发拒绝、回收后重新准入及 100 轮真实 ALC 回收。测试机 SSH 公钥与此前已验证机器一致，产物上传到新建独立目录并核对 SHA256；使用 framework-dependent linux-x64 产物、远端 .NET/ASP.NET 10.0.14。Linux 任务责任 **46**、订阅全寿命 **37**、Core 安全所有权 **17**、lifecycle/TUN scope **31**、settings **32** 与原系统代理回归同时通过。

证据和复跑命令在 `.artifacts/proxy-boundary-verification-20261104/`，远端目录及上传摘要见 `remote-provenance.json`，环境见 `remote-environment.log`，最终驻留日志为 `proxy-residency-{windows,linux}-final.log`。连接凭据只在 Git 忽略的本地文件保存，不进入实现或共享文档。测试使用隔离状态与受控后端，未改动生产 Server/Helper、系统代理、TUN 或固定服务；未提交、推送或部署。MB-02/04、RES-05/07 的整体范围继续开放，Proxy 仍 resident，生产进程树内存与延迟收益尚无验收结论。

## MB-04 Helper 发布后运行状态核验

生产 sing-box Helper 在提交新配置前核对运行状态与发布前一致。原先运行的服务若在启动返回后已退出，事务进入原补偿路径。Windows Release 受管服务专项验证旧配置摘要、旧状态版本和旧运行状态恢复。实机 Helper 联合验收仍开放。

Linux x64 Release 实机复测通过。生产 SingBoxManagedRuntime 发布事务和原生版本、配置检查使用已安装 sing-box 的专属副本。在私有网络中候选进程实际监听后被终止，Helper 观察到退出后恢复旧配置、旧状态版本和原运行状态。服务控制由专属进程夹具承载，未连接共享 systemd。REALITY 探针仅验证本地监听，私有网络无外部握手路由。日志为 .artifacts/mb04-helper-live-final.log，归档 SHA-256 为 38db85bba4743acfe71a1ced176ed681e8f3be2c0171bbd44cd08fa7a76209bd。

## MB-04 Helper 在停止失败后补偿

停止失败时发布候选仍可能占用暂存文件。补偿使用独立暂存文件，收尾不继承已取消的请求令牌。返回已补偿前核对原配置摘要、注册程序、启动策略和运行状态。Linux x64 Release 专属 sing-box 通过 13 项，包含夹具注入停止异常后实际重启旧配置，以及候选进程退出后恢复。日志为 .artifacts/mb04-helper-stop-live.log，归档 SHA-256 为 3254d1ab12ea6fff32b3eafb4a0b64679d196e7dc30fc0950ca924cb2654cefd。生产 systemd 故障注入及 Helper IPC 联合验收仍开放。

## MB-04 sing-box 发布事务的 Unix socket 验收

Linux x64 Release 在私有网络中通过生产 Unix socket 服务端、客户端及 peer UID 校验调用生产 SingBoxManagedRuntime。专属 socket 仅允许 UID 0，实际 sing-box 发布、候选退出和停止异常后的补偿共通过 13 项。日志为 .artifacts/mb04-helper-ipc-live.log，归档 SHA-256 为 ff65e52efc7c8498c8c92842d4d5701adc4e06fb96366060cf68a4da4f513934。两端同属测试进程，派发仅接入 sing-box 发布事务，服务控制为专属进程夹具。独立 Helper 进程、生产总派发器和 systemd 仍待联合验收。

Linux x64 Release 同一套 13 项已通过独立客户端进程复测。每次调用由新进程经专属 Unix socket 进入生产 transport 和 peer 校验。日志为 .artifacts/mb04-helper-peer-live.log，制品 SHA-256 为 7974f603d96672ade098f0dcbc834f0457bfc60500a1fb2ee3ce5f52880a39e0。Helper 服务端由父测试进程承载。本次验证进程隔离下的正常请求及服务故障补偿，客户端在途退出后按原台账恢复仍开放。

Linux x64 Release 增加 Helper 已提交、socket 响应尚未发送时的客户端强制终止，16 项通过。新进程通过原 socket 查询确认候选摘要和运行状态。原 Helper 台账版本只增一次，sing-box 只新增一次启动。日志为 .artifacts/mb04-helper-loss-live.log，归档 SHA-256 为 662af074f9ee23b313d1daf5801a5432d72fead0a345f8e4015ce904948a83e4。该验收覆盖 Helper 持久状态，Server 加密发布台账与 IPC 中断的联合恢复仍开放。

Linux x64 Release 联合验收通过 17 项。独立客户端运行生产到期发布执行器，将候选持久化到 Server 原加密台账，再经专属 Unix socket 提交到 Helper。确认 Helper 摘要与原待处理候选一致后，在响应前终止客户端。恢复重新打开 Server 台账，通过同一 Helper 观察确认原候选并清除待处理记录。Helper 版本及实际 sing-box 启动次数各增加一次。日志为 .artifacts/mb04-helper-ledger-live.log，归档 SHA-256 为 834f9c78bf26b8209f2c267cd8d664591df3f51f6a6fcf948826045e8280706a。服务控制为专属进程夹具，生产 systemd、动态休眠与内存验收仍开放。

Linux x64 Release 联合复测改由新进程执行生产到期调度器的一次扫描。原发布客户端退出后删除派生索引，新进程从原加密台账重建索引，在临时作用域内通过原 Helper socket 确认候选。同一 Helper 版本和 sing-box 启动计数只增一次。17 项通过，日志为 .artifacts/mb04-helper-scheduler-live.log，归档 SHA-256 为 a459ca5fee60a0f3651bd661b578799fec6986a42d5d1e2bc850277b5d48ac4b。此项验证调度恢复入口，不涵盖常驻 worker 退役或动态休眠。

Linux Release 验证到期索引路径为符号链接时，调度读取原台账，保留待处理发布且不改写链接目标。原台账及暂存写入使用同一禁止链接路径检查。专属 sing-box 跨进程回归同批通过，apply 仍为一次。日志为 .artifacts/mb04-index-links-linux.log，归档 SHA-256 为 86547d3d5220fcac631834e534894365a71238e01588abfd202d8ba2749317c6。

## MB-04 Mihomo 持久发布调度索引

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

## MB-04 入站发布按原 Helper 回执恢复

原入站加密台账在派发前保存 Helper 操作 ID、候选和旧配置摘要。受管 sing-box Helper 在原归档状态及实例锁中记录同一 ID。进入原生变更前写入在途标记，随后记录已提交、已补偿或未触及原生配置的明确拒绝。只读回执验证归档与实际配置摘要，Server 重新核对原 ID 和两个摘要，才提交候选或确认原配置。停用启动门禁调用同一恢复路径。旧摘要单独出现、原回执缺失、旧候选缺少 ID 或回执仍在途时保留责任，不自动重发。

在途标记阻止同一受管实例进入新的发布、切换和移除，原紧急停止入口仍可用。Helper 目前保存最近一次原生发布证据。若恢复期间该证据已被后续受理操作取代，Server 保留原未知结果。Helper 在原生变更中被终止后的按 ID 补偿入口和旧数据缺失证据的人工关账仍待实现。Proxy 保持 resident。

Linux x64 Release 使用生产 Helper、完整非 root Server 和私有 systemd 的真实 sing-box。原到期 `2fc60685-3fb8-4b15-a60b-6540abfa401e` 在 Helper 提交后、Server 写回前中断。重启前后原 ID 返回相同回执。Helper 停止期间有效授权仍可转发。恢复接通后完整 Server 确认原候选，Helper 只接收之后自然到期的新 apply，未重发原发布。TUN、系统代理、真实流量及专属 cgroup 清理回归通过，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-proxy-receipt-final-live.log`，归档 SHA-256 为 `3289e4001a0a2edb2fec28985313160d703dc7885322b0e7def93aa550e366ec`。

Windows Release 独立 Proxy Server、Proxy Core、模块边界及受管服务分组通过。受控原生适配器的明确补偿按原 ID 返回两个配置摘要，未将该夹具计作 Linux 外部故障。首轮 Linux 暴露通用保存强制推进安装修订，改用原归档锁内的回执专用保存后通过。成功配置提交仍推进一次安装修订。

## MB-04 在途入站发布的原 ID 补偿

原归档在原生变更前记录服务是否已注册及是否运行。显式恢复走原 Proxy Server 恢复入口，以原 ID 读取待处理发布和 Helper 证据。在原实例锁内核对原程序、现有注册、候选或旧配置摘要及旧配置备份。若原进程仍在运行且能证实已加载旧配置，则保留进程。需要还原时先解除开机启动，恢复原配置和注册状态并保持停止。后续由正常启动路径重新校验授权。补偿校验和临时文件清理完成后按原 ID 保存 Compensated，原生配置修订不前进。再次恢复返回同一回执。后台扫描、停用门禁与诊断仍不能派发补偿。

旧在途记录缺少服务基线、配置或注册发生不可核实漂移时保留责任。管理请求原排队记录本身中断后，通用 Proxy 队列仍保留原准入门禁。该情况下原用户回执的人工关账及完整认证 HTTP 恢复仍需单独验收。Proxy 保持 resident。

Linux x64 Release 最终复验在私有 systemd 上使用实际 sing-box。隔离进程执行生产发布逻辑和实际原生配置写入，候选由 systemd 注册后、启动前强制终止进程。独立生产 Helper 拒绝错误 ID，后台扫描保留原候选。生产 Server 恢复服务使用原 ID `893ead6b-4252-400e-8836-eb421c8ec565` 恢复旧配置和注册，关闭专属服务原已启用的开机启动，并保持停止。重试返回同一补偿回执，未重发 apply 或推进修订。通过正常启动入口后原监听器恢复真实认证转发。原到期、Helper 重启、TUN、系统代理和专属清理回归均通过，宿主指纹一致。故障屏障在隔离发布进程内，补偿经生产 Helper Unix IPC 执行。日志为 `.artifacts/mb04-proxy-native-compensation-final-live.log`，归档 SHA-256 为 `7eeae813c532824adc37b9321f63f6df4c44c75cfafd6fba51c5be72c4b1e2f7`。Windows Release 受管服务 16 项、独立 Proxy Server 与模块边界回归通过。Windows SCM 原生中断和旧进程仍在运行的恢复分支待实测。

## MB-04 原管理发布队列的恢复

管理发布和回滚共用 HTTP 队列的原操作 ID 与 Helper 操作 ID。加密入站台账保留同 ID 的已确认结果，覆盖领域已经写回、队列尚未完成异步清理和终态写入的间隔。显式恢复仅认领有对应入站证据的原发布队列条目，继续使用原工作租约、审计与清理。无法核实的结果保持中断责任。不同领域的未知条目仍保留原准入门禁。原发布键和显式恢复键均指向同一个用户回执。补偿完成后原发布为 Failed，已提交的原发布为 Succeeded。

Linux x64 Release 在私有 systemd 中通过两条跨进程生产路由验收。Kestrel 首进程受理后在原生在途 `77cd82df-64c6-4d29-b621-c5e7c0e41b60` 或领域已提交 `6a8c29a4-e52a-492d-9412-fc87c25c01ca` 的屏障被强制终止。新进程拒绝未认证的恢复请求，认证恢复返回原 ID 和对应 Failed 或 Succeeded，原 apply 各一次，补偿只在前一路径执行一次。原发布键与恢复键返回同一持久终态。HTTP 组使用测试认证方案与受控 Helper。另以同一 Release 复验生产 Helper、真实 sing-box 原生补偿、授权到期和 TUN/系统代理，专属 cgroup 清理且宿主指纹一致。首轮远端包装器漏传 HTTP 组程序集，原生回归完成，补齐参数后两组同批通过。日志为 `.artifacts/mb04-queued-recovery-final-live.log`，归档 SHA-256 为 `460f7814b2766fd3e6a4a079b35f399f9d6b7004fc20ab6a4ad6dbe5f8d0f6f5`。Windows Release 独立 Proxy Server 和模块边界回归通过。完整 Server 经生产认证、队列和生产 Helper 的同一次跨进程事务仍待实测。未有候选或领域终态证明的队列记录保持原准入门禁。

## MB-04 完整认证 Server 的原入站发布回执

Linux 私有 systemd 在同一次事务中运行非 root 完整 Server、生产 PAM/JWT、生产 Proxy HTTP 队列、独立生产 Helper 和真实 sing-box。隔离夹具使用预置专属证书和原有测试机账户。私有 Unix 中继按原字节转发请求，仅暂扣本次真实 apply 的成功响应。生产 Helper 已提交新配置，新增授权已验证真实流量，Server 的入站候选仍处于原 ID 的待确认状态，此时终止 Server。首次连接由隔离中继代发，重启后的 Server 以非 root UID 直接连接生产 Helper。

Server 重启后重新通过 PAM 登录。匿名恢复返回 401，原发布键返回原 Interrupted，显式恢复及原恢复键返回同一 ID，持久化终态为 Succeeded。真实 apply 一次，原配置修订只推进一次，管理进程重启期间 sing-box 继续运行。保留和新增授权均完成真实双向转发。此次联合验收针对原生已经提交、Server 尚未收到结果的路径。原生仍在途时的显式补偿已独立实测，完整 PAM HTTP 与该分支的同事务联验仍待执行。

最终 Linux x64 Release 的原操作为 `467fad21-7888-4888-8234-7583f4175db7`。中继使用私有 `/run`，实测 Server 重启前后的原 sing-box PID 相同。Linux 证据为 `.artifacts/mb04-full-auth-proxy-final-live.log`，归档 SHA-256 为 `a76e44b9c9d066c84c7588fd691e3c2a06586ade32742d7aede61e704d62c63b`。同批 TUN、系统代理、后续授权到期和原生补偿回归通过，宿主服务与代理文件指纹一致，专属 cgroup 已清理。Windows Release 受管服务 16 项、独立 HTTP 队列和模块边界回归通过。

### 入站受理作用域与活跃 TUN 的原回执恢复

**已实现且已验证。** 入站 HTTP 受理对象随请求解析。跨请求共用 Core 的 `ProxyServerStore` 及其准入锁，队列租约仍由原 `ProxyOperationStore` 持有。Release 注册检查验证同一请求作用域复用、跨作用域分离和唯一 Core 权威。Windows HTTP 队列的受理、同键、异步排空及跨进程恢复通过。

2026-11-10 在既有 Linux x64 私有 systemd 中，完整 PAM/JWT Server 在原 Mihomo TUN 活跃时受理入站发布 `82253ca7-c148-402b-835f-39fc3fe2cfd9`。Helper 提交时，测试核对原 Server 发布候选、原 Helper 回执及 sing-box 配置修订。已保留和新授权都完成真实 VLESS 转发。原 Server 在 IPC 应答暂扣、原队列仍持有租约时被强制终止。

重启后匿名恢复返回 401，原发布键读回同一待核验 ID。认证恢复按原 ID 结案，发布键和恢复键返回同一终态。Helper 原回执、sing-box PID `836` 和配置修订保持，实际发布只派发一次，两份授权再次转发。原 TUN 发布 `366d3bb04d9a4ae9aff00aebd708bbac`、Mihomo PID `503`、管理选路、配置摘要 `3775d067a92814b6db79ef55469e4aebc4ae747433b818650f94ec4b4ac41421`、执行检查点、上游 HTTP 及分流 DNS 在整个窗口均通过。

Linux Release 构建及专属 systemd 验收通过。专属进程和 cgroup 已清理，宿主共享服务、代理、resolver 和账号指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-inbound-tun.log` SHA-256 为 `cdb4117427a749ea809a9a7c0aad772a249ca3ec334daa2cae3a59938690f8fd`，归档为 `36580a292138addc0dcabf347cee3e6b1afd22656b0ded7994774a1d2a9f59da`。

**已实现但实机未验证。** 此批验证请求作用域、原生进程连续性及专属进程退出。对象 GC、ALC 回收、隔离负载内存和生产收益分别待测。

**未实现或仍受门禁保护。** 整模块安全交接仍待验收。Proxy 保持 resident。

## MB-04 完整认证 Server 的在途入站发布补偿

完整非 root Server 通过 PAM/JWT 受理入站发布，经 Unix socket 直达生产 Helper。私有挂载命名空间中的 `systemctl` 夹具只在本次实际候选已经写入并注册后，暂扣专属 sing-box 的启动调用。旧进程已经实际停止，Helper 归档与 Server 队列记录同一在途 ID。核对原候选证据后强制终止 Server 和 Helper，等待拦下的启动调用退出。重启两进程后，原发布键返回原 Interrupted，显式恢复按原 ID 补偿一次，原回执结案为 Failed / `proxy_server.config_apply_failed`。两个原键均返回相同持久终态。

真实配置和服务注册回到原摘要，安装修订未推进，开机启动关闭且服务保持停止。生产 HTTP 启动路径重新核验授权后，原有效授权完成真实双向转发，本次候选授权被拒绝。生产 Helper 重启后未执行 apply。中断时点由隔离命令屏障控制，实际配置变更、补偿、启动与流量均在专属 systemd 服务中执行。Proxy 仍保持 resident。

最终 Linux x64 Release 原操作 `f44e0d99-db0b-4aba-ae1d-f404f43d06ca`。Helper 日志确认非 root Server 按原 ID 查询并补偿一次，后续另发正常启动。真实已提交响应丢失、TUN、系统代理、到期和原生补偿回归同批通过。宿主服务与代理文件指纹一致，专属 cgroup 已清理。日志为 `.artifacts/mb04-auth-inflight-final-live.log`，归档 SHA-256 为 `c5b9593373fb8b679e2b97552ef3e780230c853ac97ce86364d444f491acb806`。Windows Release 受管服务 16 项及模块边界回归通过，Windows SCM 中断仍待实测。

## MB-04 Core Mihomo 发布恢复的停机排空

原调度器从持久索引发现原操作，继续使用同一领域锁和 Helper 回执。停止扫描可取消等待准入和后续重试。已经取得领域恢复锁并接受的实际恢复，连同原回执核验、应用台账持久化及异步作用域释放，在停机期限后仍由同一调度器任务排空。

Linux x64 Release 私有 systemd 与真实 Mihomo 用原配置操作 `d3397e41-3413-43f5-b9df-8a339b52b75b` 验证。原生配置已生效，SQLite 选择记录首次提交失败。新调度器从持久索引发现原 ID，读取真实 Helper 回执，响应暂扣期间实际监听器仍能转发，已取消的 Stop 等待令牌未结束任务或释放原锁。交付结果后原 profile 选择落盘，Stop 继续等待异步作用域清理。实际 configure 未增加。该路径直接驱动生产调度器，在测试进程内调用生产 Helper 领域入口。

同一归档复验完整非 root Server 与独立生产 Helper 的启动回执查询及中断补偿，原操作分别为 `fea2da8c-c881-4e87-823e-1b82ba5ad97f` 和 `133314fd-b76a-448a-8ad1-b03fddb4e75a`。Linux 原生 171 项、Windows 发布事务 112 项通过。已完成请求的旧夹具改用 SIGTERM 排空 Helper 日志，实际中断仍强制终止。专属 cgroup 已清理，宿主共享服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-publication-drain-verified.log`，归档 SHA-256 为 `ada60f63cd5a884270d7b3391e16354efb078d441db97d2abc07fe74e0f7469e`。

完整 Server 的 SIGTERM 与此排空屏障的联合实测仍开放。安装和移除使用同一调度器停机契约，各自的在途停机尚未纳入本轮。Proxy 保持 resident。

## MB-04 Mihomo 运行时与安装执行作用域

根引用审计定位到两个长期持有者。Mihomo 运行时管理器持有直接 HTTP 控制器，安装台账适配器经引导和移除协调器持有发布执行图。运行时管理器继续持有原实例锁，只在状态探测或整个启动就绪循环创建控制器作用域。安装台账适配器继续管理原执行与取消身份，在实际执行或读取原待恢复事务时解析发布协调器，收尾后释放。Core 安全调度器、原发布锁、原领域台账及 Helper 协议继续承担各自责任。

Windows 与 Linux Release 的注册检查各 23 项通过。空闲解析两个根服务不会创建执行图。状态探测持有控制器直至实际响应，401 仍按原规则表明控制器在监听，取消后释放本次作用域。Windows 安装发布 88 项、移除发布 92 项、安装协调器 47 项和配置发布 112 项通过，Core Proxy 安全与模块边界回归也通过。首轮注册夹具在异步文件读取完成前读取控制器列表，改为等待创建信号后复测通过。

Linux 私有 systemd 172 项通过。连续状态读取使用真实认证控制器，持续验证真实 HTTP 转发，Helper 原配置修订与派发次数一致。原配置/profile 恢复排空 `bfe20c26-8db8-4c9d-9671-c2d149604702`、独立 Helper 原失败回执 `a2b6710c-746b-4033-a683-f2b6ed970226` 与真实中断补偿 `32bd2440-6fc3-4f31-97c0-3d5ee95ca8dc` 均复验。已有受管安装、移除回执沿用原台账。首轮 Linux 状态夹具注入的凭据替身拒绝控制器读取，改由生产凭据存储读取夹具预置的加密凭据。专属 cgroup 清理，宿主服务及代理文件指纹一致。日志为 `.artifacts/mb04-mihomo-graph-final-live.log`，归档 SHA-256 为 `26e3a6df41adfc20eeeda8f6d6147fef4c612eeb5382395b7ef770b18e2f83f4`。

原生 Windows 启动就绪循环、整体管理图的动态休眠与生产内存测量仍待验收。Proxy 保持 resident。

## MB-04 完整 Server 在真实 Mihomo 回执等待时排空

私有 Linux systemd 内，测试通过原发布服务提交真实 Mihomo 配置命令。Helper 记录一次派发，原生拒绝后补偿到原配置，应用侧故意丢弃响应并保留原待处理记录。完整非 root Server 启用 Proxy 启动，由原 Core 调度器按原 ID 查询。测试中继只截留该真实 Helper 终态响应，Helper 看到的是私有中继 UID，完整 Server 本身仍为非 root。

SIGTERM 后观察到 Server 关闭监听，进程仍存活。释放回执前，原操作仍待处理，同一 Mihomo PID 保持真实 HTTP 转发。放行原回执后，Server 先确认并持久化清理，再正常退出。重启读取同一份台账，原待处理已清理、修订未推进，Helper 恢复阶段仅查询原回执一次，无配置或补偿派发。该验收使用已有 Core 调度器和原台账。

Windows Release 发布事务 112 项及模块边界回归通过。MB-05 跨进程夹具按原 ID `aa6cb9a7-7559-403e-931a-9963dfe2e1ed` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sigterm-drain-publication-win.log` 与 `.artifacts/mb04-sigterm-drain-boundaries-win.log`。

真实受管套件通过 177 项。完整 Server 排空原 `0630dd48-c886-4e22-820a-acc46fed1786` Failed 回执，原中断补偿 `f17e3bcb-15a0-44df-8813-2bf8e8e87b61` 同批复验通过。专属 cgroup 清理完成，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-sigterm-drain-native-live.log`，归档 SHA-256 为 `f5eee4a33cdc36302ab92add7254d866a57a289c0106003e5d209599f4796f47`。

复用响应中继的完整认证回归通过，入站发布原 `70b314f0-daca-47fa-b8f0-68950f0ff6b4` 及补偿 `69188c47-0bbc-43d3-8874-503e82dec4bb` 未重放。概览重启、闲时唤醒和真实控制器取消通过，宿主指纹一致。日志为 `.artifacts/mb04-sigterm-drain-auth-live.log`，归档 SHA-256 为 `a99bb9167238760ae1b54a763b5c173f05fca1c6d906a286b3f2c0bf67b157e5`。本次验收限配置发布，其他 Core worker 的完整 Server 停机边界及整模块休眠仍开放。下一项验收系统代理 guard 在真实 Helper 已写入代理环境、响应尚未返回时的停机排空。

## MB-04 Proxy 管理队列的 Host 停机所有权

原 `ProxyOperationStore` 仍是唯一管理队列和工作租约持有者。直接等待可由调用方取消，Host 通过 `IHostedService` 停机时关闭准入并等待原任务，包括业务执行、异步作用域清理和终态持久化。Host 等待期限到期时已受理任务仍由原队列排空。已有失败隔离与原台账恢复逻辑继续适用。

Windows Release 中，调用方先取消自己的等待，再用同一已取消令牌启动 Host 停机。任务在异步清理期间保持 Running，清理后 Host 等待原终态。订阅边界 39 项、责任及模块边界回归通过。原 HTTP 夹具补齐现有响应租约注册后通过。MB-05 跨进程按原 `bd57fd89-6c13-4f44-a811-c0aeba8fecd1` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-queue-drain-final-win.log` 与 `.artifacts/mb04-queue-drain-boundaries-win.log`。

隔离 Linux 的完整 PAM/JWT Server 受理入站管理发布 `77bd3fa5-9aef-4653-9d62-734d3361ff46`。真实 Helper 完成后，中继截留响应。HTTP 读取仍显示 Running，SIGTERM 关闭监听后原候选仍待处理，新授权已可真实转发。放行后 Server 持久化终态并正常退出。重启后同键返回原 ID，HTTP Succeeded 回执及原生 Committed 回执一致，原生修订只增加一次，保留及新增授权可转发。真实 SIGTERM 覆盖原响应在途，已过期的停机等待令牌由 Windows 专项单独验证。

首轮 Linux 夹具在阻塞原发布回执时，同键 POST 还需要取得原领域锁，导致该测试调用超时。复跑把同键检查放在重启后并通过。同批原入站恢复、补偿、系统代理 guard、TUN 与自然到期排空均通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。失败日志为 `.artifacts/mb04-queue-drain-live.log`，通过日志为 `.artifacts/mb04-queue-drain-final-live.log`，归档 SHA-256 为 `44e15d1454cd3cfdb15ba24637ff1b3c41bdad3ccf1da9ea9ccdf7b9e8f28e9d`。

Proxy 继续 resident。下一阶段沿原工作租约和台账拆出可释放的管理执行图，再独立验收卸载、唤醒和 Core 安全职责连续性。

## MB-04 Proxy 设置写入的工作租约

认证设置 PUT 通过原驻留协调器准入，在独立作用域解析设置执行器。工作租约覆盖实际任务、原审计、HTTP 响应与异步清理，沿用 204 和 400 响应契约。受管设置继续由原发布事务持久保存 operationId 和设置意图，已受理的原 Helper 事务独立完成。直连配置在替换活动文件前检查取消，替换后在原发布锁内完成应用或补偿，guard 仍从同一锁读取最终设置。

Windows Release Kestrel 回归覆盖无写入权限、204 和 400、真实请求断开及实际响应暂扣。工作租约持续到原审计落盘和异步清理。系统代理专项验证直连设置替换后取消时，原写入继续持有共享锁，guard 不会重新应用旧设置。Mihomo 发布事务 112 项通过。MB-05 跨进程原 `1f676f5f-5ab8-470e-94c3-aaaf380637c3` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 由非 root 完整 Server 执行认证设置 PUT。真实 Helper 提交后暂扣成功响应，夹具按原 `5a036973-72c3-4643-a659-4d4392ee38cd` 读取成功回执。断开 HTTP 时仍有一个工作租约，原设置意图待对账。终止 Server 后，已提交的 Mihomo PID 和转发持续。重启从原回执确认同一 operationId，公开设置、原生修订和配置身份一致，configure 一次，recover 零次。专属夹具使用另一个明确操作恢复测试基线，随后 Linux 原生套件通过 178 项。专属 cgroup 已清理，宿主 sing-box 和代理文件指纹一致。

首轮实机夹具误把一次原生 configure 的多个检查点计作单次修订增量。诊断轮证实原回执为成功、修订从 105 增到 111、候选继续转发。最终轮核对提交后的实际修订跨重启保持，同时独立统计派发。原始日志见 `.artifacts/mb04-settings-lease/`，最终归档 SHA-256 为 `a16bcbee31d6c319f019367def83a3bb290f5dea0013e3a681bdf2027318a502`。

直连设置取消的验证范围为本地夹具，真实宿主断线和进程崩溃连续性仍开放。Proxy 保持 resident，动态休眠和生产内存仍未验收。下一项推进档案激活与配置应用，核对直接路径替换活动文件后仍使用请求取消令牌的边界，以及受管原档案事务的租约和恢复。

## MB-04 Proxy 档案发布的工作租约

档案激活及配置应用沿用原认证路由和响应，准入后才在独立作用域构造执行器。租约覆盖实际写入、审计、响应和异步清理。直连写入在发布草稿和替换活动 YAML 前检查取消。替换后在原发布锁内完成 reload、健康核对和档案提交，存在原配置备份时沿用原补偿。受管路径继续在原加密发布台账中保存档案意图，使用原 Helper 回执与 SQLite 选择标记接续。

Windows Release Kestrel 验证两条档案入口拒绝只读用户，保留激活的 200 档案响应、配置应用的 204 以及失败的 400。实际请求断开和响应暂扣均保留租约至原审计及异步清理。直连配置夹具在活动 YAML 替换后取消，成功时执行一次 reload 并增加一次档案修订，拒绝时执行一次候选 reload 和一次补偿，原 YAML 与两份档案元数据保持。受管档案发布专项通过 23 项。MB-05 跨进程原 `e6fdc0a4-c01e-47bc-a867-8977c625ef0d` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 使用非 root 完整 Server，经认证配置应用入口写入真实受管 Mihomo。Helper 提交后暂扣原响应，HTTP 断开时仍有一个租约，原档案意图保留，档案保持未激活。终止并重启 Server 后按原 `ccace5ba-700b-4e3f-ba2f-2a1d270f0407` 确认 Helper 回执，档案修订只增加一次，实际路由模式为 Direct，提交后的原生修订、PID 和真实转发保持。configure 一次，recover 零次。同轮设置原 `cf73fb38-e0da-4531-ac12-bf595a64e2ac` 也按原回执恢复。显式测试恢复完成后，Linux 原生套件通过 178 项。专属 cgroup 已清理，宿主 sing-box 与代理文件指纹一致。日志见 `.artifacts/mb04-profile-lease/`，归档 SHA-256 为 `b2e479ea333bdd3891e5e1f5dea11ed125ddffedce31a85c55a1140c46f94783`。

直连取消及补偿的证据来自本地夹具。控制器连接恢复目前不足以证明指定 YAML 已生效，真实直连响应丢失与 Server 崩溃后的配置权威仍开放。Proxy 保持 resident，动态休眠及生产内存未验收。下一项验证直连 reload 回执丢失时的判定和原配置保护，保持同一个执行者与原领域记录。

## MB-04 Mihomo 直连 reload 回执丢失

档案和设置的控制器 reload 要求原 HTTP 成功响应。传输中断或控制器 5xx 返回 `RecoveryRequired`。执行器在原发布锁内原子恢复原磁盘配置，保持原档案与设置元数据，随后返回原有待恢复问题码。此分支没有补发 reload，也不会用 `/version` 的可达性确认原 YAML。明确拒绝时仍沿用原补偿。TUN 保留原安全标记，响应中断后可在有界等待内只读重查实际 TUN 标志，再验证设备及管理路由。

Linux 私有 systemd 使用真实独立 Mihomo 和回环 TCP 中继执行两次专属直连设置请求。第一次在中继转发前断开，控制器仍报告原日志级别。第二次实际收到 Mihomo 成功响应后丢弃回复，控制器报告候选日志级别。两次均返回 `RecoveryRequired`，各发出一次 PUT，原磁盘字节、原设置元数据和 Mihomo PID 保持，未派发系统代理变更。明确的夹具清理操作加载原磁盘配置，原 HTTP 连接完成转发。同批 Core 联合停机的到期 `218a81b5-d015-4ca5-b8f9-014f6f990ed5` 和 TUN `be8bab8f-fed0-4d75-ae42-a0d97c5768f0` 保留原责任，管理队列 `a9935e66-7a9b-4b80-9065-c22099ffea2f` 保留原回执。专属 cgroup 已清理，宿主 runtime 与代理文件指纹一致。主套件归档 SHA-256 为 `f9b0a65ce407d4b76583f195f420e69a0a20602218accbd28ad5a793e55bf3cd`。

Windows Release 控制器、直连档案、系统代理和模块边界专项通过。直连档案夹具核对未知结果时不增加档案修订，原文件保持且只有一次 reload。TUN 边界 35 项通过，包含一次只读查询失败后恢复，以及无额外 reload。MB-05 跨进程原 `0ebd1049-d684-4b55-ab93-9ae549541f82` 返回 Interrupted，受控副作用计数为一。日志见 `.artifacts/mb04-direct-reload-final/`。

独立 Linux 网络命名空间复验真实 Mihomo TUN、自动路由及 DNS。原 `c596587e-0589-4ee2-809b-961c4bcdd464` 在禁用请求未到达时保留标记，原生 reload 为零。随后实际禁用成功但回执丢失，真实设备只撤销一次。管理路由恢复后原 Core worker 清理同一标记，禁用 reload 仍为一次。同批通过真实激活取消和恢复排空，宿主服务 PID 与代理文件指纹一致。专项日志为 `tun-live.log`，归档 SHA-256 为 `ecf3d0f91a1d08179cf40a7421239d261c0678460522907ca9c2c793f7d92de8`。

原生直连 reload 接口未提供可查询的操作回执或完整生效配置摘要。当前实测覆盖存活的直连设置执行器遇到未知结果后的原文件保护。进程在还原前崩溃、后续直连写入的持久恢复门禁，以及真实直连档案响应丢失仍开放。下一阶段在原发布权威内推进持久直连检查点，明确 TUN 紧急恢复的接续权限。Proxy 整体保持 resident，动态休眠及生产内存尚未验收。

## MB-04 Mihomo 直连 reload 持久检查点

直连档案与设置在活动文件替换前，通过原发布锁将操作 ID、原 YAML 与候选摘要写入同一加密发布台账。正常路径在已知结果和应用提交完成后移除检查点。结果未知时保留原检查点，并阻止新直连写入、发布与启动或重启进入同一配置。停止运行时仍可执行。发布索引调度原 ID，Core 恢复只在磁盘匹配候选摘要时还原原文件，并保留检查点，不向控制器发送 reload。独立修改过的文件保持原样。

存在直连检查点时，Core 继续使用已保存设置承担系统代理 guard。TUN 启用关闭准入，紧急禁用仍由原安全标记和执行者接续。它在确认磁盘身份后，使用原 YAML 生成禁用配置，并核对实际 TUN 标志、设备和路由。TUN 标记可按自身结果清理，直连检查点独立保留。停用预览显示原 ID，已停用的 Server 仍可启动 Core 安全工作。

Windows Release 的直连档案、系统代理、TUN 边界 38 项、Core 安全 40 项和模块边界回归通过。验证匹配的原检查点拦住下一次派发，外部改写文件保持原样，Core 启动和 guard 不清除原检查点。Mihomo 注册 24 项、配置发布 112 项、安装发布 88 项和移除发布 92 项通过。MB-05 跨进程原 `32b843a5-601f-4b71-acad-0a45aa09d8e1` 返回 Interrupted，受控副作用计数为一。

独立 Linux 网络命名空间使用真实 Mihomo、TUN 设备和策略路由。直连检查点 `30aa48b6-dd00-4086-b9f4-5b06e1b84f0a` 待确认时，紧急禁用从原 YAML 处理 TUN 标记 `71e78ee2-08df-4da5-a2f3-cd6a7302ff8a`，仅执行一次 reload。设备及策略路由恢复，TUN 标记清理，直连检查点继续保留。宿主服务 PID 和代理文件指纹一致，归档 SHA-256 为 `e5c15e1e35b75df49585a8dfe35aae1e891cd1f0a112b9cb1c3e94c88ca5b8d5`。

Linux 私有 systemd 主套件通过真实直连设置写入。中继先核验请求未转发的分支，再对独立检查点暂扣实际 Mihomo 成功响应，强制终止写入子进程。重新打开台账，Core 按原 `6dba7d99-2d82-45f0-b466-0d33fe7d7cf6` 还原磁盘并保留检查点，后续设置写入被拦截。控制器仍显示候选日志级别，实际 PID 保持，只有一次候选 PUT 到达 Mihomo。明确的夹具清理另行还原运行状态。联合停机的原撤销 `ade94836-d0c8-4d2f-9a1c-326f4d6e280b` 和原 TUN `5a79327d-84ba-4115-85e7-60e043e04a13` 各执行一次，专属 cgroup 已清理。主套件归档 SHA-256 为 `4b456dc56371b49baf92d79d12ccbe68a831dd7e2d5ddd32e3bf8055d05a41af`。

首次并行展开触发测试账户配额。两轮主套件还因原撤销耗时接近或超过 TUN 控制器 30 秒期限，未通过联合屏障。串行执行并先暂扣真实撤销回执，再创建真实 TUN 恢复标记后，联合停机及直连崩溃均通过，生产控制器期限保持原配置。失败和通过日志均在 `.artifacts/mb04-direct-checkpoint-final/`。Windows ManagedRuntimes Release 广跑通过，进程级 CA 覆盖不可用的可信下载探针跳过。

应用级显式核验及结案仍待实现。原生控制器缺少可查询的直连回执；恢复只能证明磁盘处理，无法确认原候选是否已经生效。外部自行重启的运行时仍由外部设施管理。当前实机覆盖真实设置写入进程崩溃及独立真实 TUN，直连档案崩溃和整机掉电仍开放。Proxy 保持 resident，生产内存未验收。

## MB-04 Mihomo 直连具名恢复

运行时恢复查询直接呈现原加密台账中的直连检查点 ID、待恢复状态与专用问题码。Helper 不参与直连查询，响应中的原生阶段与服务观察保持未知。人工重试需要原运行时管理权限和宿主提权，仅接受当前检查点 ID，复用 Core 的发布锁和磁盘恢复。候选摘要匹配时恢复原文件，其他磁盘内容保持。响应始终使用原 ID 及 `RecoveryRequired`，原检查点和调度索引保留。原生紧急停止和关账入口拒绝把该检查点当作 Helper 事务，普通生命周期停止和 Core 网络安全恢复走原入口。

Windows Release 的 ManagedRuntimes 广跑和模块边界回归通过。Mihomo HTTP 54 项核对读权限、提权、错 ID、停止与关账拒绝、候选磁盘还原、外部改写保护及零 Helper 调用。原生紧急停止仍能处理 Server 台账不可读时已存在的 Helper 事务。MB-05 跨进程夹具按原 `c0efdaa8-fc99-4b72-8003-aa600c4de00a` 返回 Interrupted，受控副作用计数为一。可信下载探针因进程级测试 CA 条件跳过。桌面组件中心广跑在 Mihomo 面板之前停于已有 HTTP 503 错误码断言，客户端映射 `component.package_index_unavailable`，夹具期望 `component.state_unavailable`。日志在 `.artifacts/mb04-direct-named/`。

同一 Linux 测试机的私有 systemd 套件通过。真实 Mihomo 应用候选后，中继暂扣成功响应，写入进程被强制终止。重新打开加密台账，Core 还原磁盘。具名恢复再次读取原 `61847f23-969c-4ab1-bd66-6b276343acf1`，返回 `RecoveryRequired` 并保留检查点。真实候选 PUT 仅一次，重试未发出 PUT。运行时仍显示候选日志级别，PID 和流量保持。独立的夹具清理随后还原运行状态。联合到期、TUN 和系统代理恢复通过，专属 cgroup 已清理，宿主服务和代理文件指纹一致。归档 SHA-256 为 `eb9518ede3030cf52035e1fc8350deab9c83fbb4e7c7e759ec7f23fe23d7efd7`。

显式关账需要可归因的原生结果，或有独立检查点及可核验回执的补偿。原控制器的 HTTP 可达性、磁盘哈希和部分生效字段均不足以证明原 reload 结果。下一项先区分已收到原生确认后的应用提交中断，复用原档案 SQLite 标记及设置权威核对提交。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 Mihomo 直连档案提交恢复

直连激活在派发前冻结原档案和目标档案的修订与创建身份。收到本次 reload 的成功响应并通过健康核对后，在同一加密发布台账记录确认，再进入原 SQLite 档案事务。直连和受管激活共用一个 SQLite 写入路径。选中档案、修订、订阅刷新时间及操作标记在同一 SQLite 事务中提交。队列刷新沿用已受理的操作 ID。

Core 与具名重试共用发布锁。收到确认的档案操作在候选摘要仍匹配时接续 SQLite 提交，已提交的原 SQLite 操作标记阻止第二次修改。加密台账持久化原 ID 成功回执后才释放待处理检查点，刷新队列也可从原 ID 的确认记录补齐终态。复读已完成的原 ID 返回同一结果。缺少原生确认、磁盘漂移或档案身份冲突均保留待恢复责任。启动与具名重试均无控制器写入。

Windows Release 通过档案应用与回滚、Core 安全、模块边界和 ManagedRuntimes 广跑。正向检查确认重复读取同一回执不增加修订；外部改写文件时，候选不被覆盖，原档案修订和原调度 ID 保留。MB-05 跨进程原 `10ea1520-7410-4249-9772-0bfb4bd9f36f` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。日志在 `.artifacts/mb04-direct-profile/`。

Linux 私有 systemd 套件使用真实 Mihomo 二进制校验 YAML、发送认证 PUT 并读取实际运行模式。写入进程分别在 SQLite 提交前和提交后被强制终止。Core 重新打开加密台账与 SQLite，在两条路径各确认一次档案修订、一次真实候选 PUT 和零恢复 PUT。原 `9a66df8d-55f1-48dd-9bd9-64af43cd34bc` 和 `8bf6f461-6ad4-4b6b-ad5d-74bab25c8f1f` 均可重复读回成功回执。原 PID、跨中断 HTTP 转发和系统代理归属记录保持。原生响应丢失的对照组继续返回 `RecoveryRequired`。专属 cgroup 已清理，宿主服务和代理文件指纹一致。归档 SHA-256 为 `86c7178292a12edaac0545550d68968657cff806e3e50d1a587cb37bd1f8b319`。

这条路径证明原操作已被控制器确认及其应用提交结果。外部运行时后续变更由外部设施管理。TUN 仍先保障原网络恢复，当安全恢复改变候选摘要时，档案提交留待核对。直连设置尚未进入此确认阶段，设置的系统代理宿主效果仍需要独立核验。Proxy 保持 resident。

## MB-04 Mihomo 直连设置提交恢复

直连设置保持宿主代理策略一致时，在派发前捕获原设置文件摘要和候选内容。收到控制器 reload 和宿主代理操作的成功应答后，将提交意图写入原加密发布台账。Core 与具名重试在原发布锁下核对候选 YAML 和原设置文件身份，使用同一原子设置文件写入。文件已匹配候选时只写原 ID 回执。恢复无控制器 reload 和 Helper 代理写入。候选 YAML 或设置文件出现独立变化时保留待处理责任。

Windows Release 的系统代理、TUN 边界 38 项、Core 安全 40 项、ManagedRuntimes 和模块边界回归通过。独立写入设置文件时，重试返回 `runtime.settings_drift` 并保留原文件和原 ID。夹具恢复原始文件身份后可完成本地提交，宿主调用计数仍为一。MB-05 跨进程原 `05954880-d24c-49a2-a8b8-6c228f17e24a` 返回 Interrupted，受控副作用计数为一。进程级测试 CA 覆盖缺失，可信下载探针跳过。日志在 `.artifacts/mb04-direct-settings/`。

同一 Linux 测试机的私有 systemd 环境使用已启用的真实系统代理。Mihomo 更新日志级别，真实 Helper 返回成功。测试分别在设置文件替换前和替换后终止实际写入进程。Core 重新打开台账后，原 `d4482b4d-eea0-4528-8e8e-003d16accc77` 和 `07156515-9431-469f-aacc-2128c033e60b` 均可重复读回成功回执。两条路径各只有一次候选 reload 和一次真实 Helper 代理操作，恢复阶段两者均为零。已替换的设置文件字节和时间戳保持，运行时 PID、跨中断转发和原宿主代理归属记录保持。专属 cgroup 已清理，共享服务和代理文件指纹一致。归档 SHA-256 为 `d919b0261b1a462e9420254db0b21607ba860ced5e44221cd8724f3534aee12f`。

系统代理 guard 仍依据原持久策略维护宿主归属，提交前后宿主策略一致。TUN 仍按原安全记录独立恢复。宿主代理策略改变、控制器应答未知、以及 Helper 效果未得到成功应答的路径没有获得本地提交证明。下一项为实际改变宿主代理策略的路径提供按原 ID 的宿主效果核对。Proxy 继续 resident，动态休眠与生产内存未验收。

## MB-04 Linux 直连监听显式补偿

组件中心以原管理权限和宿主提权提交确认请求。补偿只接受已由 guard 接管的原 Linux 直连设置操作，核对原保存设置和候选 YAML，保留原 guard 归属。任何 TUN 安全标记、已启用或无法核实的运行时 TUN、控制器凭据缺失、配置漂移均阻止派发。

在原加密台账内持久记录独立补偿 ID，再原子还原旧 YAML 并派发一次补偿 reload。收到原生应答后，核对已认证控制器的实际端口和旧地址的新 TCP 连接，再用补偿 ID 派发宿主策略恢复。Helper 效果和旧监听均核实后，原 ID 终态为 `Interrupted` 并附补偿 ID。原设置文件保留原身份。独立补偿和 guard 共用原发布锁。

本阶段在补偿 reload 回执未知时保留原 ID 及补偿 ID。可观察的监听效果如何续接宿主恢复，见下节。Helper 应答丢失时，重复确认请求只读核对原补偿 ID。普通 Retry 仍只读补偿状态和原终态。对仍未知的 reload，无独立原生回执可确认完整配置，保持待处理。Proxy 保持 resident。

Windows Release ManagedRuntimes 广跑、Mihomo HTTP 57 项、系统代理、TUN 边界 38 项、Core 安全 40 项及模块边界通过。未提权、未确认和非具名直连设置均未进入补偿。不可读 TUN 标记保留，原发布权威不变。桌面 Release 编译和补偿协议专项通过。Android 线协议与 HTTP 15 项通过，设备测试源码已编译，设备交互未验收。MB-05 跨进程原 `5b0d918f-417d-4337-ac03-5f0c33cac006` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 两轮通过。最终轮原 `8e46e263-b9b1-4b1e-9d29-0b3965c9b764` 经独立补偿 `9cdc31cb-fea3-4d96-8ac8-43ecb9c41ee1` 恢复旧监听和宿主策略，原回执为 `Interrupted`。Helper 应答丢失的原 `08ee17a3-2bc1-4829-bdb7-57741d973d0f` 以补偿 `5e457875-d240-40c6-b44a-2b5af0f0750f` 查回实际效果后同样结清。控制器补偿 reload 应答丢失的原 `a4792419-e6e4-4147-93d3-41ab55528b24` 和补偿 `b59e0d14-82e7-4808-a5ac-a1bf6fed0e27` 持续待核对，实际旧端口恢复可转发，宿主补偿调用为零。三条路径均只执行一次补偿 reload，重复请求无重放，旧端口真实新建 HTTP 转发通过。专属 cgroup 已清理，共享服务和代理文件指纹一致。最终归档 SHA-256 为 `c3204f87525b7597e4fd5c68d738cea7cb5a802ca778b55f691a3c0123bae5d8`。日志在 `.artifacts/mb04-port-compensation-final/`。

## MB-04 直连监听补偿应答丢失的效果对账

显式补偿沿用原发布锁、原加密台账和既有补偿 ID。候选设置须与已保存设置仅相差端口和可选日志级别，完整候选 YAML 摘要也须能由原 YAML 的这两项精确还原。原活动文件须已恢复到原摘要，原 TUN 标记须不存在，认证控制器须同时观察到原端口、原日志级别和 TUN 关闭。旧地址须能完成 SOCKS 协商，候选地址须明确拒绝连接。核对过程不发起代理转发。

再次确认原补偿时，证据匹配才在原台账确认监听效果，并通过同一补偿 ID 完成尚未派发的宿主恢复。已经派发的宿主阶段继续只读核对原 Helper 效果。原设置身份保持，终态仍为 `Interrupted`，具名重试和原回执查询使用同一 ID。恢复从不重新发送控制器 reload。超时、候选仍监听或存在其他配置差异时保持待核验。

Windows Release 系统代理、Mihomo 控制器、TUN 边界、Core 安全、ManagedRuntimes 和模块边界专项通过。MB-05 跨进程夹具按原 `fcf7a675-42dc-4a01-a9a3-ecaa9f024761` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。

Linux 私有 Mihomo 上，原 `2e3f45f8-46c3-4822-aabe-e663fd40e902` 的补偿 `496e5376-b96b-4fcc-92c3-a68ecac7f689` 已真实应用后丢失控制器应答。新进程重开原加密台账，凭实际监听状态续接，一次宿主补偿和原 Helper 效果确认后按原 ID 返回 Interrupted。补偿实际重载一次，Mihomo 进程保持，旧端口通过新建 HTTP 转发验证。

补偿请求在到达 Mihomo 前断开时，原 `540228a8-533b-44e6-aa86-93730427c1f1` 和补偿 `868fb757-0c8e-47fe-89c2-6d1bca62d800` 保持 RecoveryRequired。候选端口仍能转发，旧端口关闭，实际补偿重载和宿主补偿均为零。简化配置基线含额外 YAML 差异时，原 `d4427633-eb24-46ae-90d4-4fd2b421b676` 和补偿 `10e26744-8cf3-4941-8758-a5d764c6898c` 即使旧端口已恢复，也保持 RecoveryRequired，宿主补偿为零。首轮把这个反例误列为可结清，最终矩阵单独覆盖它及应用生成的基线。

同批通过原有已确认补偿、Helper 应答丢失、系统代理、TUN、发布和到期回归。专属 cgroup 已清理，宿主代理文件和共享服务指纹一致。日志为 `.artifacts/mb04-listener-effect/linux-final.log`，归档 SHA-256 为 `70bc362d899095a84e2fc06bdf291559f9f7da0ead31341bca4fc684f02f795e`。原日志的 reload 次数标签表示派发尝试，原生次数另有断言。

Proxy 整体保持 resident。不能由控制器证实的配置变化、跨休眠的安全责任交接和生产内存仍开放。待核验端口见 [guard 监听切换](Proxy-Safety.md#mb-04-系统代理-guard-监听切换)。

## MB-04 直连档案与设置发布执行器

直连档案激活和设置写入使用同一个 Proxy 独立执行程序集。确认 reload、识别未知结果、恢复原文件和选择补偿均在可回收代次执行。Host 继续持有原发布锁、文件和权限检查、SQLite 档案提交、系统代理 Helper 与原回执。只在原门禁通过后加载，代码不可用时在记录新检查点或替换活动文件前失败。每次执行使用一个代次，采用独立的 16 代次保护上限。

活动文件替换后，执行等待真实控制器应答、宿主效果和持久确认，临时文件清理完成后释放执行代次。外部 reload 结果未知时保留原 ID 供 Core 核验，后续读取无副作用派发。HTTP 工作租约继续覆盖原审计、HTTP 响应和作用域清理。系统代理、TUN、授权到期和发布恢复沿用原 Core owner。

Windows Release 档案配置激活与回滚、设置边界 31 项、系统代理、TUN 38 项、Proxy 驻留 31 项、订阅后台边界 40 项和模块边界回归通过。依赖守卫 16 项通过。MB-05 跨进程夹具按原 `c35f5104-6c9c-437b-b9d2-8c5e602e1ef3` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 用真实 Mihomo 验证原设置操作 `9ffde079-16ba-494b-b979-27ecc58e4e30`。控制器已加载候选，回复暂扣期间原检查点与代次继续在途。丢失应答后恢复原文件并保留待核验 ID，已完成调用仍被测试保留时旧代次经 GC 收集。同一待核验请求未再次派发 reload。

档案在 SQLite 提交前后中断的原 `f06880b1-fde2-4bb7-a0ca-ddd5e2d59128` 与 `beee4f10-8761-4a3e-8b44-e27703a3fb06` 各保持一次 reload、一次档案修订。设置文件提交前后中断各保持一次 reload 和一次真实宿主确认。原补偿 `e72436b8-231e-4604-ba0d-072a1a77b563` 的 reload 应答丢失后仍为 RecoveryRequired，未重放。TUN、系统代理、授权到期、发布补偿和订阅批次同制品通过，专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志见 `.artifacts/mb04-direct-publication-execution/linux.log`，归档 SHA-256 为 `577f358a2782f806d816ba1029417ca1a89609e28ffdd3839229ce8e0d5b8800`。

本切片覆盖直连档案与设置的提交和补偿流程。受管运行时安装、版本生命周期和受管发布事务由原实现负责。Proxy 整体保持 resident，整模块休眠及生产内存继续开放。

## MB-04 受管 Mihomo 已恢复后的清理续接

共享配置事务保存 `PreviousRestored` 后，按原 ID 恢复只清理剩余准备文件并写入原回执。此前清理失败会令同一次恢复再次执行停止、授权和恢复旧配置。配置事务仍在同一实例锁内，恢复确认未持久记录时沿用原补偿边界。

Linux 私有 systemd 中，候选授权撤销触发恢复，受保护的临时目录路径冲突使清理以 `runtime.linked_path_forbidden` 停在原 `fa85d35c-a40d-495a-b03c-cf9ae3fd12cd`。此时旧配置已持久记录为已恢复，Mihomo 实际转发。解除临时路径冲突后，具名 Helper 恢复沿用同一 PID，授权通道读取为零，清理完成并读回原 `Interrupted` 回执。测试归档 SHA-256 为 `f20c204e923d393b17c3aafa6dedc02328df1367764649136a2e575e62f20ed0`，日志 `.artifacts/mb04-tun-marker-sync/tun-owner-managed-linux.log`。

Linux 私有 systemd 回归 180 项通过，专属 cgroup 移除，宿主服务与代理文件指纹一致。Windows ManagedRuntimes Release 回归及模块边界回归通过，配置事务 92 项包括同键只读原回执与重试只执行清理。缺少进程级测试 CA 覆盖，可信下载探针跳过。该生产回归使用普通受管配置。受管 TUN 网络效果的双中断实证仍限专属夹具。见[原生效果的双中断核对](Proxy-Safety.md#mb-04-tun-原生效果的双中断核对)。

## MB-04 受管 Mihomo 停止结果未知时的原操作核对

原配置事务保存停止派发意图后，在 Linux 同步台账目录，随后只对固定受管单元且应用原操作两份冻结配置之一的进程执行停止。具名重试复核停止状态及程序修订。原记录不能证明停止从未派发时，仍在运行的服务保留待核验，自动派发次数不增加。应急停止使用同一实例锁和原目标 ID。

Linux 私有 systemd 的两个故障窗口均通过。原 `1238b55d-1779-46c3-9691-cd4b598b861a` 的停止在送达系统管理器前失败，原候选 PID 和真实流量持续。原 ID 重试返回 `runtime.recovery_stop_unconfirmed`，停止派发仍一次。显式应急停止后，同一原操作恢复旧配置及真实转发。原 `a415761f-2fda-4dfc-98f8-587511697bc1` 的停止实际生效但返回失败。重试核对服务已停止，恢复旧配置与原 `Interrupted` 回执，停止共一次。聚焦实机 185 项通过，专属 cgroup 移除，宿主服务及代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-stop-scope-linux.log`，归档 SHA-256 `4cfd6a60138776a510cf4bb7220072d1799023cfb21e12e39bac3652c464c755`。

Windows ManagedRuntimes Release 回归通过，配置事务 99 项包括停止未确认及缺少历史检查点时拒绝重放。可信下载探针缺少进程级测试 CA 覆盖。真实 TUN 描述符双中断夹具同批复测通过，原记录 `8bb98aca42da45c8a1d099b41bccb3d9`，日志 `.artifacts/mb04-tun-marker-sync/tun-stop-tun-proof.log`，SHA-256 `577293aa997902f312bc4997440326bb362c2044cef791f3a4d23bbacd7271b9`。生产受管 TUN 设备、规则及 DNS 的持久归属仍未接入，准入门禁继续生效。

完整回归曾在受管订阅的 Helper 中断恢复段超时。只读观测确认原配置已重新运行，但保护路径检查的 `stat` 子进程已退出，Helper 仍在等待回收，原回执保留待恢复。补偿启动命令未确认时改为实测程序修订、运行凭据摘要与健康监听，证据不足时保留原回执待核验。该改动使原订阅 Helper 中断 `8560469c-95fe-4d97-a5f1-c57128b29d9e` 恢复通过。固定单元的保护检查随后复用已有 `statx` 实现，继续核对 root 所有权、链接和组及其他用户写权限。两次超时记录见 `.artifacts/mb04-tun-marker-sync/tun-stop-linux.log` 和 `tun-stop-serial-linux.log`，启动读回复测见 `tun-stop-recheck-linux.log`。

首轮 `statx` 回归揭示 systemd simple 单元报告运行时，进程可尚未执行已登记的 Mihomo 程序。启动等待改为同时核对原生执行文件，随后再做完整的命令行、凭据和健康验证。失败记录 `.artifacts/mb04-tun-marker-sync/tun-stop-statx-linux.log`。

最终完整 Linux Release 私有 systemd 回归通过 185 项。原订阅 Helper 中断 `4bc608bc-cbfb-4d4e-8db2-8ffb9031f17a` 按原回执补偿，停止未知的两条原操作 `00d8792b-7933-45fc-81b4-c122ade32aa3` 和 `ee1187b6-a36e-4ee9-bf2a-1ccc43e7b7e3` 通过。后一条同时注入实际启动后应答失败，仍凭原生证据完成原回执。专属 cgroup 移除，宿主指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-stop-final2-linux.log`，归档 SHA-256 `cda2cdaa4d43f37f11ded690c1e5c828c49c2a710f163698353ff439af043041`。最终 Windows 首轮在 Core 持续恢复调度断言处失败，同一制品串行复测通过，日志 `tun-stop-final2-win.log` 与 `tun-stop-final2-win-retry.log`。Windows 模块边界回归也通过，日志 `tun-stop-server-win.log`。

### 应用配置写入与 Core 恢复端口

**已实现且已验证。** 设置写入与应用配置发布入口由启用的 Proxy 注册为管理作用域服务。应用设置及档案写入通过配置发布端口进入原协调器。Core 启动及具名恢复通过原配置恢复端口继续处理原操作，系统代理守护通过只读端口取回 Helper 原生事实。回执读取端口解析到原 Mihomo 运行时权威，发布与恢复共用原发布台账和锁。

Windows Release 注册 24 项、档案发布 23 项、配置事务 113 项和运行时 HTTP 57 项通过。配置事务覆盖禁用启动、恢复作用域清理、原回执及单次配置派发。早期回归暴露测试夹具仍注册旧具体类型，已迁到配置恢复端口并复验。配置事务日志 `.artifacts/mb04-tun-full-zero-dns/windows-publication-ports-verified-mihomo-publication-only.log` 的 SHA-256 为 `c6fce6ae1518bb58a34885da62ebbc358e54acc7524cef9ea8bf45b95eb4ed4c`。

Linux Release 私有 systemd 实测关闭 Proxy 管理入口后，原 TUN 发布 `0892e53d808b4d018fdbf16477bc5720`、Mihomo PID `415`、管理选路和配置身份保持。Core 接续系统代理守护 `6a6d817c06b14e2c97fd16824b6cfd06` 和授权到期 `8c0db4a7815b43168ae5d1853ee79543`。独立具名启动 `aae00cfe12854fd4a3473719c161a0ea` 丢失原终帧和首次查询应答时，保留两项工作租约和新键门禁。按原 ID 恢复，同键返回相同终态，启动仅派发一次。管理选路、原生记录及真实上游 HTTP 和分流 DNS 跨重启保持。两项均确认专属进程和 cgroup 退出，宿主服务、代理及 resolver 指纹一致。原日志 `.artifacts/mb04-tun-full-zero-dns/linux-publication-ports-tun.log` 的 SHA-256 为 `4961a703a3c5a35957563e3e3bbd498bcd4b8252fffa3d4a8120d84e8836ac9c`，归档为 `79ac021406e1527d10cf551fb3f7669fac40ba75cd41a035de7ffff4d2c50357`。

Linux 受管 Mihomo Release 全量通过 180 项。实际设置写入 `01bb2683-d43c-4cbc-a05a-d8509c945b9e` 在客户端断开后保留租约，按原回执结案，配置仅派发一次，原转发持续。原发布恢复 `8cf25f9f-d8d5-461c-84a6-756b63fc63f7` 排空原回执和作用域。Helper 不可用时启动按原 ID 拒绝，恢复后原 Failed 回执 `95c3214d-b345-4cef-8173-cf7a2d0a44c0` 与中断补偿回执 `0806f3a0-4b18-449e-b2cd-16b183075703` 均通过，原配置各只派发一次。日志 `.artifacts/mb04-tun-full-zero-dns/linux-publication-ports-managed.log` 的 SHA-256 为 `7e6a5dea7dac80e7584a6bb4f4ee0fec421e1b44acfd98fdbea3f30bff54bcba`，归档为 `b2cf4cc6b794311a26d172fc51384e97c53a67bffb035d34a4f25c893f40b0e2`。

**已实现但实机未验证。** 此处验证管理作用域与 Core 权威的端口分工及专属进程退出。管理写入对象回收、写入执行图 ALC 回收、隔离内存差值和生产收益分别待测。

**未实现或仍受门禁保护。** 写入执行器的代次交接和整模块动态驻留仍开放。Proxy 保持 resident。

### 前台配置写入的原代次

**已实现且已验证。** 设置更新、档案应用和档案激活从当前 Proxy 代次取得工作租约和作用域。原代次持有执行、审计、实际响应及清理责任。清理失败时，原代次保留失败作用域和工作租约，协调器拒绝新写入。Core 仍按原发布锁、授权及原回执裁决。

Windows Release 的 Proxy 安全、订阅边界和模块里程碑通过。三个前台路由分别验证客户端断开、原审计和响应、异步清理期间的单一租约。清理失败后，本地夹具确认原代次编号相同，原作用域资源在 GC 后可达，新写入未执行。夹具初次误将协调器的准入响应码当成驻留阻塞原因，改为分别断言后通过。最终日志 `.artifacts/mb04-tun-full-zero-dns/windows-write-owner-module-boundary-final.log` 的 SHA-256 为 `bcaee0b616250b1fb709c563074e868d7521ad1905e27b715902d56682a096d0`。

Linux 私有 systemd 实测关闭 Proxy 管理入口后，原发布 `39c0f6f3f0744484bac8e7f55d44c80a`、Mihomo PID `418` 和配置身份保持，Core 接续原系统代理守护及授权到期。原具名启动 `4c9d590ec4d04acf916ad6b27d17ebb6` 的两次应答中断仍保留原租约与新键门禁。按原 ID 恢复后，同键返回原终态，仅一次启动派发。管理选路、原生记录、配置身份及上游 HTTP 和分流 DNS 跨重启一致。两项均确认专属进程和 cgroup 退出，宿主服务、代理及 resolver 指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-write-owner-tun.log` 的 SHA-256 为 `d5e36ca033d4256e2c161869b0cf61a2e1d7e8e8915d5c765ca7f5bd9fcca56a`，归档为 `07922be8db536e23dbfcec881f0f365b8fcd19b050be7485b243009614003420`。

Linux 受管 Mihomo Release 通过 180 项。真实设置写入 `5fb6368b-45cc-4865-acaf-2619d0b881a3` 与档案配置应用 `8856b2c5-1055-4764-b1c7-daca0bdcd562` 均在原生效果生效、Helper 应答未交付时断开 HTTP。原工作租约和待核验记录保持，强制退出后重启只查询原回执。每项仅一次原生配置派发，配置身份和实际转发保持。档案选择提交一次。原发布恢复 `af7f07f4-fca2-4908-b473-3880ee4c3383` 排空原回执及作用域。日志 `.artifacts/mb04-tun-full-zero-dns/linux-write-owner-managed.log` 的 SHA-256 为 `66018dc6f3d9eac2c21101e421c3720a5b7f4b622400e3be8cccf05bef90dbf9`，归档为 `6628e2e23b3913c0c53e3ef95eacc5706ddcb44995dff5b2f4e125b2e39bb524`。

**已实现但实机未验证。** 前台写入异步清理故障和档案激活的响应排空由本地夹具验证。Linux 本轮验证设置、档案应用中断和专属进程退出。写入对象释放、整体 ALC 回收、隔离内存差值和生产收益分别待测。

**未实现或仍受门禁保护。** 管理执行图的整模块安全交接仍开放。Proxy 保持 resident。
