# MB-07 SystemPerformance S1 验收

S1 交付时的性能采样、图标预算、请求与连接生命周期记录。当前冻结规则见进度页。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [23. 进程图标缓存实例所有权与载荷预算（2026-11-04，MB-07 部分）](#23-进程图标缓存实例所有权与载荷预算2026-11-04mb-07-部分)
- [24. desktop 图像输入与目标位图分配预算（2026-11-04，MB-07 部分）](#24-desktop-图像输入与目标位图分配预算2026-11-04mb-07-部分)
- [25. desktop 流式扫描与索引工作预算（2026-11-04，MB-07 部分）](#25-desktop-流式扫描与索引工作预算2026-11-04mb-07-部分)
- [26. 图标并发准入、取消与 owner 停机排空（2026-11-04，MB-07 部分）](#26-图标并发准入取消与-owner-停机排空2026-11-04mb-07-部分)
- [27. 图标工作接入常驻协调器租约（2026-11-04，MB-07 部分）](#27-图标工作接入常驻协调器租约2026-11-04mb-07-部分)
- [28. 进程查询/终止工作保护与快照 owner 排空（2026-11-04，MB-07 部分）](#28-进程查询终止工作保护与快照-owner-排空2026-11-04mb-07-部分)
- [29. 实际采样工作租约与需求代次（2026-11-04，MB-07 部分）](#29-实际采样工作租约与需求代次2026-11-04mb-07-部分)
- [30. 性能静态信息读取的工作保护（2026-11-04，MB-07 部分）](#30-性能静态信息读取的工作保护2026-11-04mb-07-部分)
- [31. REST 快照等待者的完整调用保护（2026-11-04，MB-07 部分）](#31-rest-快照等待者的完整调用保护2026-11-04mb-07-部分)
- [32. Hub 广播实际发送的工作寿命（2026-11-04，MB-07 部分）](#32-hub-广播实际发送的工作寿命2026-11-04mb-07-部分)
- [33. 连续 Hub 连接与组成员 owner（2026-11-04，MB-07 部分）](#33-连续-hub-连接与组成员-owner2026-11-04mb-07-部分)
- [34. 历史与最新快照的异步读取保护（2026-11-04，MB-07 部分）](#34-历史与最新快照的异步读取保护2026-11-04mb-07-部分)
- [35. HTTP 性能响应执行保护（2026-11-04，MB-07 部分）](#35-http-性能响应执行保护2026-11-04mb-07-部分)
- [36. 连续 Hub 连接的准入数量预算（2026-11-04，MB-07 部分）](#36-连续-hub-连接的准入数量预算2026-11-04mb-07-部分)
- [37. 管理员图标 Base64 解码前预算（2026-11-04，MB-07 部分）](#37-管理员图标-base64-解码前预算2026-11-04mb-07-部分)

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
