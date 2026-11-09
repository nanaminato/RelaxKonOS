# MB-04 Proxy 订阅队列与激活

Mihomo 订阅下载、转换、排队、发布激活、草稿和分项原回执。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [17. 订阅后台任务独立作用域与完整寿命（2026-11-03，MB-04 部分）](#17-订阅后台任务独立作用域与完整寿命2026-11-03mb-04-部分)
- [21. 订阅转换待回收代码预算（2026-11-04，MB-04 部分）](#21-订阅转换待回收代码预算2026-11-04mb-04-部分)
- [MB-04 订阅响应体的可回收执行边界](#mb-04-订阅响应体的可回收执行边界)
- [MB-04 真实受管 Mihomo 的完整认证订阅队列](#mb-04-真实受管-mihomo-的完整认证订阅队列)
- [MB-04 活动订阅刷新原生提交后的中断边界](#mb-04-活动订阅刷新原生提交后的中断边界)
- [MB-04 活动订阅刷新恢复原队列终态](#mb-04-活动订阅刷新恢复原队列终态)
- [MB-04 活动订阅原生失败与补偿回执](#mb-04-活动订阅原生失败与补偿回执)
- [MB-04 直连活动订阅队列的进程中断恢复](#mb-04-直连活动订阅队列的进程中断恢复)
- [MB-04 直连活动订阅重载应答未知](#mb-04-直连活动订阅重载应答未知)
- [MB-04 批量订阅刷新分项回执](#mb-04-批量订阅刷新分项回执)
- [MB-04 草稿文件替换后的原回执恢复](#mb-04-草稿文件替换后的原回执恢复)
- [MB-04 草稿替换未应用的原回执](#mb-04-草稿替换未应用的原回执)
- [MB-04 排队订阅激活的原发布回执](#mb-04-排队订阅激活的原发布回执)
- [MB-04 订阅激活未知与失败的实机回执](#mb-04-订阅激活未知与失败的实机回执)
- [MB-04 受管 Mihomo 切换后的订阅激活补偿](#mb-04-受管-mihomo-切换后的订阅激活补偿)
- [MB-04 受管订阅激活期间 Helper 中断](#mb-04-受管订阅激活期间-helper-中断)
- [MB-04 订阅后台执行图的可回收交接](#mb-04-订阅后台执行图的可回收交接)

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

## 21. 订阅转换待回收代码预算（2026-11-04，MB-04 部分）

§20 的每次转换独立 ALC 原来只请求 Unload，没有限制自然 GC 尚未回收的上下文数量。Host 现在通过同一同步准入边界记录弱引用，合计最多允许 16 个活动或待回收上下文；扫描时移除已死亡引用，达到上限则在加载实现代码前返回固定 Host 异常，不创建更多代次、不回退内置实现、不强制 GC。转换完成仍立即请求卸载，根预算不持有实现对象、类型、异常或强上下文引用。

自然 GC 延迟也可能暂时耗尽预算，之后的新转换会失败，待回收后才可重新准入；长期无法回收时需安排宿主恢复。16 是代码代次的防积累上限，不是字节预算、吞吐承诺或已验证的最优值。它不改变 queued 工作租约、原领域失败回执或 Core 安全责任，也不使整个 Proxy 可休眠。MB-04/RES-05 继续开放，生产内存与延迟测量仍待完成。

Windows Release Server.Tests 构建通过（0 错误、3 个既有告警），驻留/独立代码专项 **26 项**通过。新增检查主动保留真实转换上下文，验证预算饱和时 32 个并发调用全部拒绝；释放测试强引用后上下文可回收，同一个准入 gate 再执行 100 轮真实转换并逐轮确认旧 ALC 消失。GC 只用于测试观察。原订阅完整寿命及任务责任专项同时通过。证据位于 `.artifacts/proxy-code-budget-20261104/`；首次沙箱内测试在临时 ledger 文件原子替换处被拒绝，最终同一隔离测试在沙箱外通过，首次失败不计作成功。此续核尚未执行 Linux、新测试机、生产资源或真实网络故障验收，未提交、推送或部署。

## MB-04 订阅响应体的可回收执行边界

Host 按原规则校验来源地址、DNS、系统代理路由、TLS 与响应头。经授权的响应流交给已有可回收订阅代次，代次负责实际字节上限、严格 UTF-8 与转换，向 Host 返回共享值。解码直接使用累积缓冲，省去一份完整字节副本；临时租用的读块归还时清除。流实际异步释放后才请求卸载。原 HTTP 接口、工作队列、审计及领域台账继续沿用，Core 安全职责与网络准入都在 Host。

Windows Release 驻留专项验证流清理未结束时仍保留代次、保留结果时旧代码可回收、100 次转换代次循环及超限、无效 UTF-8 的原错误分类。Host 档案、Core 安全和模块边界回归通过，依赖守卫 16 项通过。MB-05 跨进程按原 `3fd51576-ebc8-48e4-82f5-06e98e11caa4` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sub-body-win.log`、`.artifacts/mb04-sub-body-storage-win.log`、`.artifacts/mb04-sub-body-boundaries-win.log` 与 `.artifacts/mb04-sub-body-boundary-guard.log`。

Linux 使用私有网络中的可控分段 HTTP 源站和生产下载器及连接策略。已见代次加载而源站仍暂停正文时，下载任务和代次均在途。放行后得到原转换格式，观察卸载；第二次读取加载独立代次并得到相同结果，最初的真实 Mihomo 连接完成转发。该下载路径在验收进程内使用生产注册，HTTP 源仅在专属夹具中启用。本轮证据分开记录真实流的卸载事件和本地弱引用回收。

完整认证同批复验 Core 系统代理、TUN 和到期，以及原管理操作 `453002c0-267c-4da0-a48e-51e7864e15ea` 与原到期回执 `324a1e3f-c1c0-4093-8a93-1f1bc94d293f`，各一次原生 apply。专属 cgroup 清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-sub-body-live.log`，归档 SHA-256 为 `c63115ccae7c18baa421d9bbc0d5a341b000cdd98eb4f0ee9aa436841b9aa2f4`。Proxy 继续 resident，生产 PSS 与整模块休眠仍开放。下一步联验真实受管 Mihomo 的完整认证订阅队列，核对转换代次释放与同一领域台账和发布责任的关系。

## MB-04 真实受管 Mihomo 的完整认证订阅队列

隔离 Linux 夹具在非 root 完整 Server 中使用 PAM/JWT 调用原订阅导入与刷新接口。测试源站提供真实分段 HTTP 响应，且仅在专属配置中启用 HTTP 源。导入的是不激活的订阅档案，刷新沿用原 HTTP operationId、工作租约和 Host 数据库。测试启动钩子观察订阅代次的加载与卸载事件。

Windows Release 订阅边界 39 项与模块边界回归通过。MB-05 跨进程按原 `f9ca85a6-a130-47c5-a078-e7ed539ab26f` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-sub-queue-win.log` 与 `.artifacts/mb04-sub-queue-boundaries-win.log`。

原刷新 `9dcac25e-dce2-4b52-88c1-82cefcd05cf8` 在源站暂停正文时为 Running，模块目录显示一份活动租约，转换代次保持加载。放行后观察到卸载，Host 数据库中的原档案更新，任务成功且活动租约归零。SIGTERM 正常退出后，重启用同键得到原 ID、完全一致的 HTTP 成功回执及相同档案内容。源站请求数仍为两次，重启未加载新转换代次。

真实受管套件通过 177 项，原发布身份、修订、进程 PID 与转发通过前后核对，订阅刷新期间 Helper 未收到配置或补偿命令。原中断补偿 `ce2dc5ca-f98d-4002-a1f4-20d70d680725` 与完整 Server 发布排空 `9f4c9eff-e4d8-478d-8882-902e7b0bb01a` 同批复验。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-sub-queue-live.log`，归档 SHA-256 为 `015e6681a03a89d8e87c5e7789d3fb1c807214b2ba13c4ac0023ec6f802792b6`。

该路径限未激活档案刷新，Proxy 整体继续 resident。下一步验证活动订阅刷新在真实配置已提交、原回执仍在途时的重启对账，保留原队列 ID 并区分下载与配置副作用。

## MB-04 活动订阅刷新原生提交后的中断边界

活动刷新在原队列中下载并更新档案，受管发布另生成配置操作 ID，订阅更新时间在发布返回后写入。当前两个操作 ID 缺少持久关联，Core 确认原生配置和档案选择后无法据此结束原订阅队列。验收分别观察两个原 ID，区分配置确认和仍未知的队列终态。

Windows Release 订阅边界 39 项及模块边界回归通过。MB-05 跨进程按原 `f3c5e5e5-be97-4df5-90fc-29740233709f` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-active-sub-crash-win.log` 与 `.artifacts/mb04-active-sub-crash-boundaries-win.log`。

Linux 私有 systemd 上先通过完整 PAM/JWT Server 激活测试订阅，再触发活动刷新。真实 Helper 原回执 `36aee130-c235-4269-8df2-630b546e7979` 为 Succeeded 时，中继截留响应并强制终止 Server，原队列 `0e3870d2-dc66-4277-bff9-2c4a7938864e` 仍为 Running。重启由 Core 按原 Helper ID 对账确认配置和原档案，已提交修订保持。原队列用同键返回原 ID 和 Interrupted，新键被拒绝。源站请求数始终为三次，Helper 日志确认激活和刷新各一次配置调用，无补偿或重放。真实转发通过，验收随后用独立操作恢复专属测试配置。

首轮夹具把一次配置误计为原生修订只推进 1，原生事务在多个持久化阶段推进修订。复跑以原命令计数、已提交回执和重启后修订一致为准。真实受管 177 项通过，宿主共享服务及代理文件指纹一致，专属 cgroup 清理完成。日志为 `.artifacts/mb04-active-sub-crash-final-live.log`，归档 SHA-256 为 `5f03ee6a89c9a6402f29f1bc361b89a6c62e5d97e1e20e377da22fad453f2bea`。

配置确认与避免重放已验证。订阅队列缺少能证明整个刷新完成的原生和应用证据，仍保持 Interrupted。下一步把队列身份及订阅更新时间纳入原发布意图，并在原档案提交后按原 ID 对账，缺失证据时继续保留责任。Proxy 整体继续 resident。

## MB-04 活动订阅刷新恢复原队列终态

单条活动订阅刷新使用同一队列与受管 Mihomo 配置 operationId。发布意图冻结订阅、档案、已下载内容对应的配置身份和刷新时间。另一写入覆盖草稿时，该次刷新拒绝发布。确认配置时，现有 SQLite 事务一并提交档案选择、档案修订、订阅时间和原操作标记。原生配置、应用状态和原回执均由现有发布服务核验，原发布权威保存确认或补偿结果。

Core 继续用原调度器处理待发布事务。队列在查原回执、重试原键或准入新操作时，仅以原 operationId 的完成证据补写终态。实际执行仍在清理时不接管回执，补写自身也持有工作租约直至落盘。缺少匹配证据、旧版双 ID 和显式清理失败继续保留中断责任。无法核实的原生结果不重试下载、配置或补偿。批量刷新、未激活刷新、未受管引擎及发布意图写入前中断的回执保持各自原有边界。

Windows Release 通过 45 项 Proxy 责任检查、39 项订阅边界、配置回滚、23 项 SQLite 档案发布和 112 项 Mihomo 发布事务。SQLite 在拒绝原操作标记写入时一并回滚刷新时间和档案修订，重试相同发布只写一次，使用同一 ID 更改冻结时间会被拒绝。MB-05 模块边界跨进程夹具按原 `9e12d3ea-aec2-4815-a69a-0edf8b2a088b` 读回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb04-active-sub-loss/`。

中间版复验未到达 Helper，原因是用源文本比对已应用 GEO 策略的落盘草稿。修正为共用落盘转换，本地 Release 验证等价草稿通过、替换草稿被拒绝。该轮 Linux 超时记为失败，专属 cgroup 清理完成。

最终 Linux Release 使用私有 systemd、真实 Mihomo、生产 Helper 与完整 PAM/JWT Server。原刷新 `71b30f5e-cf5a-4339-ae5c-bf4f60931cc5` 在真实 Helper 已提交、响应在途时强制终止 Server。重启从原键读回同一 ID 和 Succeeded，订阅时间与原发布意图一致，Helper 回执和原生修订保持。原刷新 `3d8e2702-188f-43a9-a79c-7f5dbaba5928` 只截断真实 Unix socket 的回执，Server 继续运行。待核验阶段原回执为 Interrupted，完成时间为空，新键被拒绝。同一个 Server 随后用原证据补写 Succeeded，重复查询及原键均返回原回执。源站共四次请求，Helper 对激活和两次刷新各派发一次配置，恢复无额外配置或补偿命令，真实转发通过。

同批真实 Mihomo systemd 套件通过 177 项，专属 cgroup 已清理，宿主服务与代理文件指纹一致。日志为 `.artifacts/mb04-active-sub-loss/live.log`，归档 SHA-256 为 `0d2aaca11a6df1c9a5454b5fcfb0d084a653655afd003d35a399eae3dbb95fd6`。活动刷新补偿的原队列终态映射已实现，该组合尚无独立真实故障验收。Proxy 整体继续 resident，整模块休眠和生产 PSS 保持开放。下一项用真实原生失败验证活动刷新保持原转发、按原回执补写失败且不重放。

## MB-04 活动订阅原生失败与补偿回执

Linux Release 在同一专属设施通过完整 PAM/JWT Server 下载一份含 Mihomo 不支持规则的测试订阅。生产 Helper 返回真实 Failed 回执 `a9eee171-6755-4e44-991a-cc5c2fe529f7` 后，专属 Unix 中继暂扣结果。原队列仍为 Running 并持有工作租约，原配置身份和实际转发已恢复。强制终止 Server 后重启，Core 按原 Helper ID 对账，原队列持久化 Failed / `proxy.config_apply_failed`。原键和原 ID 均返回同一回执，原订阅时间、配置身份、Helper 回执和原生修订保持。

同批复验成功重启与存活 Server 断线。测试源共五次请求，Helper 对激活和三次刷新各派发一次配置，恢复无额外配置或补偿命令。Mihomo systemd 套件通过 177 项，专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-active-sub-compensation/live.log`，归档 SHA-256 为 `2c95db44e293ac3fb4e5e459624199eec6c54ce742a1ee22323ae5426ea7ccb0`。Windows Release 模块边界复跑通过，MB-05 原 `b1ff571b-7191-45ba-a05e-fe17a52bc41f` 的跨进程回执为 Interrupted，受控副作用计数为一。下一步联合验收可回收代次的闲时释放和唤醒、真实转发及 Core 恢复责任。Proxy 整体继续 resident，生产驻留未测。

## MB-04 直连活动订阅队列的进程中断恢复

完整 Linux Server 通过 PAM/JWT 认证导入、激活并刷新隔离源站的订阅。测试中继等待真实 Mihomo 成功返回，夹具在交付应答前取得同一个 SQLite 数据库的写锁。确认原刷新 ID 的加密检查点已落盘，HTTP 回执仍为 Running，Proxy 仍有一个工作租约后，强制终止实际 Server 进程。SQLite 写锁阻止了原进程提交档案。

重启后，Core 使用原检查点提交档案并恢复原订阅时间，原 `890eca76-d4e6-429c-a3d6-13a9662c0efa` 通过操作查询和同一幂等键返回同一个 Succeeded 回执。刷新只下载一次、只派发一次真实 reload、只增加一次档案修订。原 Mihomo PID、跨中断 HTTP 转发和系统代理归属保持。私有 systemd 套件其余验收通过，专属 cgroup 已清理，宿主代理文件和运行时服务指纹一致。

首轮因私有档案目录未交给非 root Server，在导入时返回 400，未进入恢复场景。调整夹具属主后复验通过。Linux 日志在 `.artifacts/mb04-direct-subscription/live.log`，归档 SHA-256 为 `04fdebe69500ee6e7a5dc76081a9100d455e9e370df4fab2b3b592f422cefdf7`。Windows Release 模块边界与 ManagedRuntimes 回归通过。MB-05 跨进程原 `c8883f12-13fa-47c0-919a-f6e91695ec8c` 返回 Interrupted，受控副作用计数为一。

本项实测覆盖已收到原控制器确认、SQLite 尚未提交的活动订阅刷新。原控制器结果未知的实测见下节。批量刷新未纳入本轮。Proxy 保持 resident，动态休眠与生产内存未验收。

## MB-04 直连活动订阅重载应答未知

队列在查询或受理请求时对账原发布状态。匹配同一刷新 ID 的持久发布检查点、且当前进程已无该操作时，原队列使用真实工作租约保存 `Interrupted / publication_pending`，完成时间留空。持久终态证据仍可结清原记录。无关的检查点不会替换历史回执。

同一私有 Linux systemd 环境中，真实 Mihomo 成功加载候选订阅后，中继暂扣应答并强制终止完整认证 Server。原 `7d58f819-5b2e-4d5f-bf2c-50f676b5e86e` 在终止前仍为 Running 并持有一个工作租约。重启恢复旧磁盘 YAML，实际控制器仍报告候选节点。原 SQLite 档案修订和订阅时间保持，原 ID 与原键返回同一条待恢复记录，新的刷新键被拒绝，恢复没有重新下载或重载。原运行时 PID、跨中断 HTTP 转发及系统代理归属保持。

同批已确认刷新 `86f6c8fb-7bac-4252-ae71-61143db47cdb` 仍完成原档案事务。隔离资源清理完成，宿主运行时服务和代理文件指纹一致。Linux 日志在 `.artifacts/mb04-direct-subscription-unknown/live.log`，归档 SHA-256 为 `8667bdcb02dd64eb85f5c9fe0855826bdfe39dbe614d8aea436b5592710cdcd5`。Windows Release 原回执责任 47 项、订阅后台边界 39 项、ManagedRuntimes 与模块边界回归通过。MB-05 跨进程原 `f74bef20-409e-4e9e-8f35-07acee5cee3a` 返回 Interrupted，受控副作用计数为一。

独立的受管 Mihomo 私有 systemd 套件通过 178 项。崩溃恢复 `193de4f5-a85c-40f3-870f-0d693315dfa9`、存活进程应答丢失 `61ddc11b-5fcb-445e-8739-a6ef3d0e172c` 和真实失败补偿 `aac22140-70ac-46bf-9f23-687c815ee387` 均读取原回执，恢复无重放。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志在 `.artifacts/mb04-direct-subscription-unknown/managed-live.log`，归档 SHA-256 为 `d05974cdac5200de23c4f7f0660cdd87ebc21eabca0343bba43978ad7a0fc545`。

原控制器缺少可查询的完整 reload 回执，未知路径继续占用写入与停用门禁。批量刷新的分项恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 批量订阅刷新分项回执

批量刷新沿用原队列、幂等键、执行作用域和工作租约。原队列在准入时记录批次身份，在任何分项开始前保存有序订阅列表及各自的子操作 ID。每项派发前保存 Running，完成后保存分项结果。活动订阅沿用原发布台账及同一子 ID。草稿订阅以同一条 SQLite 更新提交刷新时间和子 ID，恢复只读取该提交证据。

进程中断后，查询和受理路径在原队列锁内对账。未开始的分项记录为 Skipped，已有确定回执的分项保留对应结果，缺证据的原分项仍为 Running。原批次保持 `Interrupted / batch_pending` 且完成时间为空，继续占用写入和停用门禁。所有已派发分项都能核对时，原批次写入 `Interrupted / batch_interrupted`，释放该批次的未决门禁。恢复既不派发跳过分项，也不重做原下载、档案写入或重载。只有正常工作、审计、作用域清理及终态持久化全部完成时才报告整批成功。

隔离 Linux 完整认证 Server 使用真实源站和 Mihomo，依次刷新草稿、活动项和尾项。第一轮在活动项真实重载确认落盘、SQLite 提交前强制终止。原批次 `a29508f7-88a7-4e3e-bb0d-fb16ed5d2760` 以原分项 ID `448e9315-a0fb-4495-852e-37d60d5eba2e` 完成活动档案提交，草稿和活动项各一次，尾项跳过。第二轮在控制器已载入候选、应答未交付时终止，原批次 `36b77395-55a4-4c99-b065-da5021b0e771` 和子操作 `e4df5fe6-4f40-4bf9-997f-443c2641ba7d` 保留待恢复。旧 YAML 回到磁盘，原档案修订和时间保持，候选节点在原进程仍可读取，新键被拒绝。两次重启均以原键读回原批次，零额外下载和重载，原进程 PID、实际转发及宿主归属保持。

最终 Linux 日志在 `.artifacts/mb04-subscription-batch/live-final.log`，归档 SHA-256 为 `616c872354e235bec62053ba9fac2d9ce616558696b4ebb8b096fcaeb36c16b7`。专属 cgroup 已清理，宿主服务和代理文件指纹一致。一次上传因私有制品占满宿主临时内存盘剩余空间而失败，最终改用独立磁盘临时目录。Windows Release 存储、订阅边界 40 项、ManagedRuntimes 与模块边界通过。SQLite 夹具确认草稿回执与时间同提交、仅按匹配子 ID 对账，以及未规划批次无派发。回执写入受阻时原队列保持。MB-05 跨进程原 `9ce90bd5-d03c-44bb-92ef-ab76cff87901` 返回 Interrupted，受控副作用计数为一。

同制品受管 Mihomo 对照通过 178 项。原活动刷新崩溃 `0ae04dde-8fcf-41d5-b086-849af2989be1`、响应丢失 `60a0dd18-c793-4a77-af2e-82c4b4160be6` 和失败补偿 `37f1e54f-e567-4705-b92b-f763757a3afb` 继续按原回执核对，零重放。专属 cgroup 已清理，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-subscription-batch/managed-live.log`，归档 SHA-256 为 `48815aac93e918d7a6bf921b0dac0e802014f17c0d5a1dd82df39ec831232e89`。

旧在途记录缺少分项计划、原证明被后续独立刷新覆盖，以及直连重载没有原确认时，继续保留未知责任。草稿文件已替换但原 SQLite 回执尚未提交的恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 草稿文件替换后的原回执恢复

未激活订阅先验证候选 YAML。替换前，Server 在原 SQLite 订阅行保存同一操作 ID、候选完整摘要和原抓取时间。原子替换的文件包含该操作 ID 的内部 YAML 注释，同一正文的旧文件因此无法冒充新替换。订阅内容读取会隐藏该注释。实时提交与重启恢复共用一个带身份条件的 SQLite 更新，提交原抓取时间、保存原回执并移除该意图。

恢复沿用原队列锁、工作租约及 Mihomo 发布锁。它核对实际文件的操作标记及完整摘要，再确认同一订阅仍指向未激活档案。只补原 SQLite 回执，草稿文件不改写，源站和运行时均无派发。锁忙、文件缺失或漂移、档案已激活或 SQLite 更新失败均保留原意图和门禁。恢复工作不等候其他发布持有的锁。旧在途记录无该意图时仍按既有证据处理。

隔离 Linux 完整认证 Server 先下载真实订阅并替换草稿，私有 SQLite 触发器拒绝原回执写入。核对新 YAML 已落盘、原意图可读且 SQLite 无回执后，强制终止 Server。恢复写入能力并重启后，原批次 `14b81cd2-9057-47f5-a814-e27106a2f0ff` 按原分项 `db45c54d-02f8-4415-9413-01c1389fca88` 补交回执。原抓取时间、YAML 字节和修改时间一致，内容接口隐藏操作标记。原刷新只下载一次，运行时零重载，Mihomo 保持真实转发。同批活动刷新确认和应答未知的两条路径通过。专属 cgroup 已清理，宿主运行时及代理文件指纹一致。日志在 `.artifacts/mb04-draft-commit/live.log`，归档 SHA-256 为 `4a477c0ec0db307e3a474d06bb40a83c2ccfc5329b36a881813e129a64bfbcb1`。

Windows Release 存储、订阅边界 40 项、配置、ManagedRuntimes 和模块边界通过。旧文件正文相同、标记相同但摘要漂移，以及发布锁忙均保持待恢复。单项草稿的同路径对账通过本地验证。MB-05 跨进程原 `130cad76-6b26-48d0-b717-204b212a2793` 返回 Interrupted，受控副作用计数为一。

受管 Mihomo 隔离对照通过 178 项，包括原草稿 `687207cf-3cf8-468f-82e5-7eddf193b292` 及活动刷新崩溃、丢失响应和失败补偿。专属 cgroup 清理后宿主指纹一致。日志为 `.artifacts/mb04-draft-commit/managed-live.log`，归档 SHA-256 为 `666ee9d06996bc5c63a705da9e780b6bd1bfc0cf792fa901d3aaf5259ff08e3d`。本轮故障实测覆盖批次中的草稿替换后窗口。草稿替换前的恢复见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 草稿替换未应用的原回执

未激活订阅的候选文件使用原操作 ID 派生的独占暂存路径。替换前在同一 SQLite 订阅行保存原草稿摘要和候选摘要。意图落盘后，写入失败会保留原暂存文件作为证据。正常替换消耗该文件，应用回执要求目标含有原操作标记、完整摘要匹配，且暂存文件已消耗。

原草稿与暂存文件都能核对时，恢复在原发布锁和工作租约内先持久化清理决定，只删除匹配的暂存文件，再按原 ID 写入未应用回执。原草稿、抓取时间和发布进程保持。若清理后回执提交中断，下次按原决定继续核对旧目标和写入回执。单项以 Failed 收尾，批次分项记录失败，原中断批次在所有已派发分项核对后释放未决门禁。任一证据漂移、清理决定前暂存缺失或旧意图无原草稿摘要，继续待恢复。

Windows Release 存储、订阅边界 40 项、配置、ManagedRuntimes 和模块边界通过。存储夹具覆盖暂存缺失、内容漂移、无效摘要及清理后的回执写入故障。无效摘要最初触发读取异常，最终验证保留原待恢复回执。单项原 ID 返回 Failed，原草稿和上次抓取时间保持。MB-05 跨进程原 `fa33b83c-52ad-47cd-aa1a-ded18656c6a5` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 使用私有 bind mount 让实际 Server 的草稿替换失败，确认原文件与暂存文件均保留后强制终止。首次重启允许清理决定和暂存清理，SQLite 触发器仍拒绝未应用回执，此时再次强制终止。第二次重启按原批次 `5b936c50-b61d-4752-9e90-f4aac1099cbe`、原分项 `ea44a3a4-e0e7-4e52-b8ec-3ec94fbdb6ef` 完成未应用回执。原键回读一致，旧 YAML 字节、修改时间和上次抓取时间保持，原刷新只下载一次，恢复零重载。跨两次中断的真实 HTTP 转发通过。专属 cgroup 清理后宿主运行时与代理文件指纹一致。同批复验草稿已替换的原回执及活动订阅两条路径。日志为 `.artifacts/mb04-draft-unapplied/live-final.log`，归档 SHA-256 为 `93b4908818eee58d310f452dbc22382a4e36fc3c19dcce51f10dbf1f15feef9e`。

受管 Mihomo 最终制品通过 178 项，含草稿原 `c2ffa66a-9d45-4564-a872-faa67095039b` 和活动刷新原回执、丢失响应与失败补偿。专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-draft-unapplied/managed-live-final.log`，归档 SHA-256 为 `79a28414cf6c492656ef7bfe6979c611fc56b5355e1f8eefe1e1d3df1ba5af5b`。双中断实测覆盖批次草稿，单项未应用对账限本地验证。意图落盘前遗留、无法关联的暂存内容仍需单独核对。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 排队订阅激活的原发布回执

订阅激活沿用原队列操作 ID，直连检查点、受管 Helper 请求和档案提交均使用同一 ID。发布意图冻结原订阅和档案关联，SQLite 在派发前和档案事务内核对关联及待恢复草稿门禁。激活沿用已保存的草稿与原抓取时间。原发布权威保存激活的确认或失败结果，后续查询使用该结果补齐原队列回执。

未知原发布结果继续占用 `publication_pending`，完成时间留空。具名恢复查询已结清的原 ID 时返回原应用结果，无控制器或 Helper 派发。队列在真实工作、审计和作用域结束后才释放执行责任，重启接续原持久发布与档案提交。

Windows Release 配置、订阅边界 40 项、ManagedRuntimes 和模块边界通过。订阅与档案不匹配时零重载，明确回滚后具名查询返回原失败。未确认直连重载通过原键返回稳定待恢复回执，具名查询保持原 ID 且无重载。MB-05 跨进程原 `f62a9628-1a31-40cc-8103-405b9d74048f` 返回 Interrupted，受控副作用计数为一。

隔离 Linux 直连激活 `9ec3b9b0-0ddc-4721-8ac3-7bfe7b621c03` 使用真实 Mihomo。夹具暂扣真实重载应答并持有 SQLite 写锁，随后让原确认落盘。在原任务仍持有工作租约时强制终止 Server。重启补齐一次档案修订，原键与原 ID 读回同一成功回执。全程一次重载、激活阶段零订阅下载、原抓取时间与运行时进程保持。跨中断 HTTP 转发和专属资源清理通过，宿主指纹一致。日志为 `.artifacts/mb04-subscription-activation/live.log`，归档 SHA-256 为 `d8a2c69062d8480f6b7b5eee8ed32ea3264f35f33c8d1b1f85d62567b08a8e8a`。

受管 Mihomo 使用同一隔离主机，生产 Helper 已提交激活 `e05a5a41-cda0-4cab-b2a3-67642325ff5f` 时中继暂扣原应答并强制终止 Server。重启按同一 ID 查询真实 Helper 回执并完成档案提交。原键返回原成功，Helper 原修订与订阅时间保持，激活只派发一次，源站没有激活下载。受管套件通过 178 项，真实转发持续，专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-subscription-activation/managed-live.log`，归档 SHA-256 为 `672b40e3ab76d48f6a04d8e41abddd9428ce6a9be7642b0af8a464026e695c81`。

本轮实机覆盖原生结果已确认的激活中断。激活结果未知与确定失败的实测见下节。Proxy 保持 resident，动态休眠与生产内存未验收。

## MB-04 订阅激活未知与失败的实机回执

Windows Release 配置、订阅边界 40 项、ManagedRuntimes 和模块边界回归通过。MB-05 跨进程原 `0d45dd11-c28a-42b1-a354-703f56dacc7b` 返回 Interrupted，受控副作用计数为一。直连私有 systemd runner 可用 `RELAXKONOS_TEST_ACTIVATION_UNKNOWN=1` 选择激活故障，默认保留活动批次的未知故障路径。

受管激活 `15469b9e-4a69-472f-bbf9-503417a18cd8` 使用先前保存的无效规则，由真实 Mihomo 在切换前拒绝。生产 Helper 保留原失败回执和原配置身份，中继暂扣回执时强制终止 Server。重启按同一激活 ID 和原键读回 Failed，Helper 修订、订阅时间与实际转发保持，激活零额外下载且只派发一次。此路径验证切换前的确定失败和准备清理。受管隔离套件通过 178 项，专属 cgroup 已清理，宿主指纹一致。日志为 `.artifacts/mb04-activation-failure/managed-live.log`，归档 SHA-256 为 `34be917c180cf47ff3ed438df0d4fecd0d99bf7b2a54288b60bca0d9c7481e41`。

直连激活 `8db17423-7d06-4def-a445-d4c7eec3268a` 在 Server 已替换配置、真实控制器尚未读取时损坏隔离候选 YAML。真实 Mihomo 返回 HTTP 400，原事务重载旧配置一次。认证控制器读到旧节点，原档案修订和草稿时间保持，原失败回执跨进程读取一致。另一原激活 `de7c6260-9a03-4ccb-91c3-3993c31aa063` 实际加载候选后暂扣应答并强制终止 Server。重启恢复旧磁盘配置，真实进程仍报告候选节点，原激活保持 `publication_pending`、无完成时间，目标档案仍未激活，新键被拒绝。两条路径均无重复下载或重载，原进程跨中断 HTTP 转发通过。

直连首轮在并行运行期间触及 250 秒运行窗口，未产生终态。最终轮在同一运行窗口内通过，两轮专属 cgroup 均已清理，宿主运行时与代理文件指纹一致。最终日志为 `.artifacts/mb04-activation-failure/live-final.log`，归档 SHA-256 为 `e2030e4f66464a7157124c511301c80e603797f8f9269b6b2e6c96690a10c973`。受管激活切换后的故障补偿见下节。Proxy 保持 resident，动态休眠和生产内存未验收。

## MB-04 受管 Mihomo 切换后的订阅激活补偿

完整认证 Server 提交有效的新订阅档案。私有挂载、网络与 systemd 夹具暂扣一次真实 `systemctl start` 的完成，确认候选新进程的实际凭据配置摘要等于原 pending 候选，并通过真实上游转发。随后实际停止该候选，再放行生产 Helper 的健康核验。Helper 按原发布事务重启旧配置并通过转发。候选停止到旧进程就绪期间存在服务切换窗口。

中继暂扣原 Helper 失败回执时，Server 仍显示 Running 并持有工作租约。强制终止 Server 后，重启按原 operationId 和原键读取 Failed，活动订阅和候选草稿记录一致，恢复后的原配置身份、Helper 修订及 Mihomo PID 保持，工作租约排空。Helper 日志按原 ID 核对一次配置派发，零额外恢复派发，激活无新增订阅下载。生产补偿与恢复仍由原 Helper 和发布台账负责。

隔离夹具在 `RELAXKONOS_TEST_MIHOMO_SUBSCRIPTION=1` 时以 `RELAXKONOS_TEST_MIHOMO_POST_SWITCH=1` 启用切换故障。在私有挂载、PID、网络和 systemd 命名空间内安装测试用 `mihomo-post-switch-systemctl.sh`，并检查宿主指纹。首轮候选补偿通过，但套件仍按旧配置派发总数校验而失败。第二轮发现 `systemctl start` 完成早于候选监听就绪，随后改为先核对实际 TCP 监听，再完成 HTTP 转发并验证进程身份。

最终 Linux 原生套件 178 项通过。新故障原激活 ID 为 `64e3b00e-896b-4a3a-bbf5-6499c1e3176d`，专属 cgroup 已清理，宿主运行时和代理文件指纹一致。证据为 `.artifacts/mb04-post-switch/linux-verified.log`，归档 SHA-256 为 `9a78d3336c6ac27cfe4a5c1b6f39b16a99dcf6461efce6497f5772e76f7f684b`。

Windows Release 订阅边界 40 项、配置与模块边界回归通过。MB-05 跨进程夹具按原 `f7b5875c-bd0e-427c-a555-55a2c64191bd` 返回 Interrupted，受控副作用计数为一。补偿本身失败后的原 ID 接管、Proxy 整体动态休眠及生产内存仍待验收。

## MB-04 受管订阅激活期间 Helper 中断

Linux Release 完整 PAM/JWT Server、生产 Helper 与真实 Mihomo 运行在私有 systemd 中。测试在 systemd 已启动候选、Helper 尚未确认激活时暂扣返回。原激活 `dcc0f2af-bb92-42cf-a895-00c1daf04146` 为 Running，持有一个工作租约。核对候选进程 `15116` 的实际配置摘要等于原意图，并验证真实上游转发。随后终止 Server 与 Helper。原 Helper 台账仍在 Activating，无原生终态，候选仍以同一 PID 转发。

重启后由原 Core 发布恢复器触发一次具名补偿。原 Helper 台账得到同一 operationId 的 `Interrupted`，恢复原配置摘要和真实转发。原队列回执完成为 `Failed / proxy.config_apply_failed`，保持原活动订阅、候选草稿及订阅时间。同键和原 ID 返回同一回执，工作租约归零。夹具计数一次原配置请求，恢复 Helper 只收到一次补偿请求，零新增配置或下载。原生套件 178 项通过，专属 cgroup 清理，宿主运行时和代理文件指纹一致。

前两轮原回执未完成。第二轮确认原 Helper 已执行补偿，但紧接的原生只读查询超时使 IPC 返回协议错误，Server 将发布留待核验。Helper 现将该只读超时报告为 TimedOut，原恢复器按既有暂时不可用规则继续核对原台账。最终系统测试通过，故障原回执与原配置身份均可读。Linux 定向生命周期回归 39 项覆盖只读超时分类。Windows Release 的发布事务 112 项、注册 24 项和运行时 HTTP 57 项通过。日志在 `.artifacts/mb04-helper-interruption/`，实机归档 SHA-256 为 `17c94947229a2a4f048c457c4125e927fab22d7ae35c67d72f053a8ecf894413`。

此路径验证真实进程中断、原工作租约与候选切回旧配置。期间可能有服务切换窗口。旧执行对象释放、该故障路径的 ALC 收集、隔离负载与生产内存收益未单独测量。Proxy 保持 resident。

## MB-04 订阅后台执行图的可回收交接

排队刷新、激活与批量刷新由同一 Proxy 可回收程序集执行。刷新用同一代次完成真实响应体的有界解码与规范化、草稿或活动档案分流以及原批次分项执行。每个已准入操作使用一个代次，批次不为各分项另建代次。导入仍在原 HTTP 作用域内为响应体取得短期代次。两者共用既有 16 代次保护上限。

Host 继续独占下载授权、网络策略、配置发布门禁和持久写入。队列仍在执行前落盘原回执，批次先持久化原分项计划，后续按分项 ID 对账。执行代次跨实际任务、原审计、异步作用域清理和结果写入。结果未知时先持久化原待核验责任，再由原台账和 Core 发布恢复接续。重启及同键查询沿原台账和原发布或 SQLite 草稿证据恢复，证据不足时继续保留原待恢复责任。

Windows Release 订阅后台边界 40 项、独立程序集与驻留 31 项、构建依赖守卫 16 项通过。Core 安全、系统代理、TUN、存储、档案配置和模块边界回归通过。MB-05 跨进程夹具原 `13925e47-153e-4d4b-af73-2ec64a544c5a` 返回 Interrupted，受控副作用计数为一。

完整认证 Linux Server 用真实分段订阅响应验证草稿刷新 `330ad5eb-9b8e-483d-bb3a-2c780298531b`。响应在途时原工作租约与执行代次保持，原终态可读后观测到卸载并确认 GC 收集。重启同键取得原草稿和终态，源站请求没有增加。受管 Mihomo 激活 `5a6e5fe7-13ef-4890-be49-2029dc6f196b` 和活动刷新 `34b20dd7-f4dc-49f4-9708-02d52f285c74` 在原生提交后中断，重启从原 Helper 证据补齐原终态，未再次下载或配置。

受管 Mihomo 实机套件通过 178 项。活动刷新 `2c3b64d6-a83d-48e5-b57d-56185fb8db6b` 丢失应答后，原 Server 按同一证据核对终态。失败刷新 `3740d4b7-535b-4088-af37-4f80452b6e9b` 和失败激活 `ce101eb2-c3f0-4cdc-b66b-d0bb6165a91b` 保持原 Failed 回执，原转发和时间戳保持。

同制品的完整认证联合验收覆盖系统代理 guard、真实 TUN、授权到期、发布补偿和 SIGTERM 排空。批次 `cf4155e4-4450-4207-a7db-7b3125ba45b1` 按原子项回执确认草稿与活动提交，未开始的尾项跳过。未知批次 `12c1c652-eba9-42b8-ad5d-e319a4ea88c4` 保持原待核验回执，无新增下载或 reload。补偿 reload 丢失应答的原 `6fb4c8a5-4526-41d0-853a-9fde55a9bdc6` 保持 RecoveryRequired。两个私有 cgroup 已清理，宿主运行时与代理文件指纹一致。日志见 `.artifacts/mb04-subscription-execution/linux.log`，归档 SHA-256 为 `236f2033079a5a05842ef470b7b1a08e5ee9ecc252a68e8e16eb79ecd5f79da5`。

本切片完成订阅后台执行图交接。Proxy 整体保持 resident，系统代理、TUN、到期与发布补偿继续由原 Core owner 持有。整模块休眠及生产内存继续开放。

### 后台受理对象与执行闭包分离

**已实现且已验证。** 订阅和运行时受理移入请求作用域。后台订阅闭包携带原队列台账、作用域工厂及参数，运行时与具名 TUN 闭包携带作用域工厂及参数。Windows Release 的 Proxy 核心、订阅边界与模块里程碑通过。弱引用断言在原工作暂停且仍持有工作租约时确认请求受理对象已回收，随后核对原任务的清理、回执和终态。

完整 Linux Release Server 在私有 systemd 下保持真实 Mihomo TUN 转发。私有源收到两次草稿下载和一次队列刷新下载。原刷新 ID 为 `ab71980b-9927-4aa6-8fce-63c8d8aeb421`，同键返回同一 ID，下载计数无增加。原 TUN 发布 `d5d2f19cc11f44d8b42699f281dcb409` 和原 Squid 发布 `378b0ecd-d6e3-4a84-b8e9-3838e8394d63` 还通过同次恢复写回故障回归，Squid 只重载一次，Core guard 修复原配置。

独立用例丢弃具名启动 `bdc7ec6898eb43c0bcb960319ed809e8` 的原 IPC 终帧。实测原发布和配置摘要相符，原查询在途可见两项租约，新键受原门禁保护。恢复及 Server 重启后，原运行时 PID `951`、HTTP 上游和分流 DNS 连续通过。两次用例均核对进程退出及专属 cgroup 清理，宿主共享运行时服务、代理及 resolver 指纹一致。日志 `.artifacts/mb04-tun-full-zero-dns/linux-scoped-acceptance.log` 的 SHA-256 为 `12eaef36356bb12d237fe542d599854a61d0f8bc65ecfdba079e6296b45ae223`，归档为 `4cd35a9935c1ee556d94362d18cd578a439e53472a9acf8bc60e14f3374ff7f0`。

**已实现但实机未验证。** 对象释放由 Windows Release 弱引用验证，Linux 验证执行与进程退出。ALC 回收、隔离负载内存差值、生产内存收益各自待测。

**未实现或仍受门禁保护。** 整模块安全交接仍开放。Proxy 保持 resident。

### 订阅持锁与前台写入独立排空

**已验证。** Linux Release 专属 systemd 的后台订阅激活 `beafc02d-8376-4c96-a60a-793ac41c8565` 已产生原生配置效果，Helper 应答被截留，原队列为 Running。前台设置写入竞争同一发布锁。客户端断开后，夹具暂扣前台作用域的异步清理，观察两项租约同时存在，原操作、原配置及真实转发保持。

释放前台清理后，工作租约由两项降为一项，设置文件逐字节保持，原配置仅一次派发。随后强制退出 Server，重启按原 ID 只读恢复订阅终态，同键返回同一成功回执。未增加订阅下载，Helper 回执和安装修订保持，真实 HTTP 转发持续。受管 Mihomo 矩阵共 180 项通过，专属进程和 cgroup 已清理，宿主共享服务及代理文件指纹一致。

Windows Release 订阅边界 44 项与模块里程碑通过。原始日志 `.artifacts/mb04-tun-full-zero-dns/linux-shared-gate-managed.log` 的 SHA-256 为 `8603e8c6c92f6f6d15de91c3f5347acc04f95fedd69f3916e14a451af616d887`，归档为 `4d639741da2325364ca621c6c54532dfb01619eabaa7c9034eac38b928afb63e`。Windows 模块日志 `.artifacts/mb04-tun-full-zero-dns/windows-shared-gate-module-boundary-milestones.log` 的 SHA-256 为 `3a7aaadcbc7e5bd803463bb0d3edb8dcad8e0c57106dd626f50efa87f3b75d25`。

**剩余验收。** 前台先持发布锁时后台订阅的等待和排空需独立实测。本轮确认专属进程退出，对象释放、ALC 回收、隔离内存差值与生产收益分别待测。受管 TUN 管理选路和 HTTP/DNS 连续性沿用[前台配置写入实测](Proxy-Publication.md#前台配置写入的原代次)。整模块安全交接仍开放，Proxy 保持 resident。
