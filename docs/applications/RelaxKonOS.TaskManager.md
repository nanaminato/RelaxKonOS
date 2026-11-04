# RelaxKonOS 任务管理器

> 当前实现：2026-10-01。Server 统一性能采样、内存历史与 SignalR；进程列表独立按需查询。Android 行为与验收由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/README.md) 管理。

> 模块归属（2026-10-28）：性能采样与进程链路由官方模块 `relaxkonos.system-performance` 拥有并默认启用。设置 `Modules:SystemPerformance:Enabled=false` 并重启后，`PerformanceSampler` 后台采样、`/api/v1.0/system/performance/*`、`/api/v1.0/system/processes/*`、`/hubs/performance` 与 `server.metrics`/`server.processes` 能力同时消失；桌面端声明这两项能力，Server 未公布时拒绝启动而不是打开空窗口。宿主网络地址 `/api/v1.0/system/network-addresses` 属于 Core，不受该开关影响。详见[模块设计实施记录](../architecture/RelaxKonOS.ModulesAndExtensions.Design.md#firewall-与-system-performance-迁移2026-10-28)。

## 性能采样

`ISystemPerformanceSource` 隔离 Windows 原生 API 与 Linux `/proc`、`/sys` 原始读取。`PerformanceSampler` 统一使用相邻计数器和实际单调时间间隔计算速率，每秒采样，保存最多 60 点内存历史；不接入数据库。至少一个订阅者存在或 REST 申请有界 demand 时才采样，无订阅者后清除基线与历史。首次原始读取只建立基线，不把差分未就绪显示为 0%。

2026-11-04：实际采样读取至同步事件分发使用驻留协调器工作租约，停机等待超时不提前释放未完成的源读取。每个连接独立登记，需求代次阻止旧读取晚到后污染新基线/历史。Windows 新增 18 项检查通过，本轮 Linux 尚未运行；静态信息读取、REST 等待者与 Hub 网络发送的完整寿命仍待接入，模块继续 resident，范围见[边界设计 §29](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#29-实际采样工作租约与需求代次2026-11-04mb-07-部分)。

后续静态信息读取也已接入工作租约与独立请求 owner：关闭准入后拒绝新读取，实际源 Task 完成前保留保护，不创建实时采样 demand。Windows 驻留检查累计 28 项通过，Linux 本轮未执行；REST 等待者和 Hub 网络发送仍待接入，详见[边界设计 §30](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#30-性能静态信息读取的工作保护2026-11-04mb-07-部分)。

REST 快照后续：缓存命中和等待均接入工作租约，取消/超时释放本请求需求，关闭能唤醒等待；底层未完成的采样读取继续独立受保护。Windows/Linux 驻留检查累计各 42 项与原性能专项通过，Windows 默认模块回归通过。Hub 连续订阅与网络发送仍待接入，模块保持 resident，范围见[边界设计 §31](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#31-rest-快照等待者的完整调用保护2026-11-04mb-07-部分)。

Hub 广播后续：实际发送 Task 已受工作租约保护，保持 4 条 DropOldest 队列，慢网络不阻塞采样；停机等待超时不提前释放在途发送。Windows/Linux 新增广播各 15 项通过，原性能驻留各 42 项及专项通过；连续连接/组成员仍待接入，详见[边界设计 §32](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#32-hub-广播实际发送的工作寿命2026-11-04mb-07-部分)。

连续连接后续：Hub 连接从 OnConnected 到组清理实际结束持有工作租约；仅连接不触发采样，退订后连接仍受保护。重复订阅不重复入组，断线/停机的晚到入组不会恢复采样需求；未知退组失败保留责任，不自动重放。Windows/Linux 最终连接各 25 项及原专项通过，详见[边界设计 §33](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#33-连续-hub-连接与组成员-owner2026-11-04mb-07-部分)。模块仍 resident，真实网络故障及生产内存收益尚未验收。

只读接口后续：最新快照和历史采用可取消的 `GetLatestAsync` / `GetHistoryAsync`，包括空缓存和命中均先取得工作保护，HTTP 历史路由与 JSON 不变，不触发额外采样。Windows/Linux 性能驻留各 53 项及原专项通过，详见[边界设计 §34](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#34-历史与最新快照的异步读取保护2026-11-04mb-07-部分)。保护至物化返回，不包含后续响应传输，模块仍 resident。

HTTP 响应后续：已授权结果的实际 JSON 序列化、图标写入等执行另持工作租约，取消不提前释放未完成的响应 Task，结束后恢复原请求令牌。响应不可用不重放已执行的终止操作。Windows/Linux 新增 HTTP 各 15 项及原专项通过，详见[边界设计 §35](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#35-http-性能响应执行保护2026-11-04mb-07-部分)；不宣称物理网络送达或整体模块回收，模块仍 resident。

连接预算后续：性能 Hub owner 最多持有 128 个准入中、存活或故障连接，超限直接拒绝；正常清理可补位，取消通知不提前释放名额，未知故障继续占用。Windows/Linux 新增预算各 13 项及原专项通过，范围见[边界设计 §36](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#36-连续-hub-连接的准入数量预算2026-11-04mb-07-部分)；不是整个 Server 的内存上限，模块仍 resident。

`PerformanceInfoDto` 包含低频 CPU、内存、文件系统、磁盘、网络身份及显式能力位；`PerformanceRealtimeSnapshotDto` 包含递增 sequence、UTC timestamp、CPU、内存、文件系统容量、磁盘 I/O、网络计数与速率、uptimeSeconds、health。文件系统容量和块设备 I/O 分开显示。未知增强指标保留 null；能力 false 时不显示伪造的数值。当前性能契约没有 GPU 实时数据，GPU/传感器为后续扩展。

## 当前 REST 与 Hub

所有端点需要 JWT，路由来自 `SystemMonitorApiRoutes`。

| 方法 | 路由 | 输入/输出 |
| --- | --- | --- |
| GET | `/api/v1.0/system/performance/info` | `PerformanceInfoDto` |
| GET | `/api/v1.0/system/performance/snapshot` | `PerformanceRealtimeSnapshotDto`；有界 demand 后无有效样本返回 503 |
| GET | `/api/v1.0/system/performance/history?seconds=60` | 最近有效点数组，上限 60 秒 |
| GET | `/api/v1.0/system/network-addresses` | 非回环、非 IPv6 link-local 的当前接口地址数组 |
| GET | `/api/v1.0/system/processes/query` | page、pageSize（1–500）、filter、sort（cpu/name/pid/memory）、direction（asc/desc）；`ProcessPageDto` |
| GET | `/api/v1.0/system/processes/{id}/icon` | 必需 `startTime` 查询参数；返回宿主进程图标 PNG，无法解析时 404 |
| DELETE | `/api/v1.0/system/processes/{id}` | 必需 JSON `TerminateProcessRequest`；`KillProcessResultDto` |

性能 Hub 为 `/hubs/performance`，方法为 `Subscribe` / `Unsubscribe`，事件为 `OnPerformanceSnapshot`。断开会移除订阅；客户端离页/窗口卸载取消观察，重新进入或重连补取静态信息与历史。序列号仅在同一次 Server 生命周期内递增，重连不能永久沿用旧生命周期的序列基线。

## 进程查询与实例终止

`ProcessSampler` 只在查询时枚举进程，缓存周期 5 秒，CPU 相对整机计算。差分键为 `(PID, StartTime)`，长时间无查询后重建基线。返回 `items`、`totalCount` 与 `sampledAt`；每项包含 id、name、cpuPercent、memoryBytes、userName、startTime、threadCount、parentProcessId。桌面客户端仅将同名、同属主且父实例时间有效的父子进程组成默认折叠的进程组，组内后代平铺为一层，避免系统父进程形成巨大的展开树；名称显示组内进程数，资源列与结束操作仍针对该行的单个实例。过滤时保留匹配项的组首行，并临时展示匹配成员，不修改原有折叠状态，并逐页取齐快照以免分页拆散树；跨页采样时间或总数变化时重试一次，持续变化时明确失败。折叠节点的后代保持隐藏，仅脱离正常根节点的循环关系使用兜底根节点。无法读取启动时间时 startTime 为 null，客户端禁用终止和图标查询。Linux User 模式只允许宿主执行 UID 所属进程。默认普通模式使用 Server 当前 OS 账户读取和终止；管理员模式通过权限助手读取全机信息、图标并终止已确认实例。两个模式分别缓存并维护 CPU 基线；助手返回累计 CPU ticks，由长生命周期 Server 差分计算，兼容 Linux 一次性助手。管理员模式的助手不可用、协议不匹配或快照无效时返回 503，不静默降级。部署时 Server 与助手必须同时更新至当前协议并重启。

进程图标单独按 `(PID, StartTime)` 请求，避免进程列表轮询反复传输图像。客户端仅在虚拟化列表需要显示该行时请求；Server 验证当前实例并按可执行文件缓存图标。Windows 在选定执行上下文中核对同一进程句柄的创建时间，并使用 QueryFullProcessImageName 读取可执行文件路径、提取 PNG；Ubuntu 从匹配的 `.desktop` 项查找 PNG/SVG 图标。图标缺失、权限不足或不支持的图像格式返回 404，客户端使用类别图标回退。

2026-11-04：普通图标缓存归 Server `ProcessSampler` 的 provider 实例，管理员图标归 Helper 独立 owner；每个缓存最多 512 条（含缺失结果）及 8 MiB 已完成图像载荷，按 FIFO 淘汰。文件路径、长度或修改时间变化产生新键。销毁 sampler 时清理图标及 desktop 索引引用，在途加载完成不能重新写回。授权与 PID/启动时间验证仍逐请求执行，不因缓存命中省略。当前没有闲时缓存清理或整个性能模块休眠，预算不涵盖 desktop 索引、在途解码峰值及调用者持有的数据；验证范围见[Server 边界设计 §23](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#23-进程图标缓存实例所有权与载荷预算2026-11-04mb-07-部分)。

desktop 图像读取另有限额：SVG 原文最多 256 KiB；PNG 编码最多 1 MiB，且源图每边最多 1024px，在分配目标像素前检查。正常 PNG 仍输出有透明边距的 32×32 图标；超限、损坏或编码不符时使用原缺失图标回退。此预算不等于整个解码过程、目录索引或服务进程的内存上限，验证见[Server 边界设计 §24](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#24-desktop-图像输入与目标位图分配预算2026-11-04mb-07-部分)。

desktop 索引现按有限根目录、深度和条目流式扫描：每文件 64 KiB、总读取 8 MiB，最多保留 2048 项及 512 KiB 键值文本；主题发现和路径探测也有整轮预算。超限时保留已找到的图标，其余使用默认图标，不影响已授权进程列表。目录符号链接不递归，desktop 文件链接仍经过有界读取；当前未增加定时刷新或模块休眠。具体工作预算与验收见[Server 边界设计 §25](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#25-desktop-流式扫描与索引工作预算2026-11-04mb-07-部分)。

普通和管理员图标读取现在共享每个 Server owner 的 4 个执行名额及 32 个额外等待名额，provider/Helper 各自另有限额。排队取消不启动读取，超载或停机使用原默认图标回退；已开始的读取不会因客户端取消而提前释放资源，实际完成后才报告取消。ProcessSampler 的 Host Stop 阶段关闭图标准入并等待在途读取，超时仅取消等待，资源在真实工作结束后清理。进程终止行为、权限、PID/启动时间核验及图标缓存预算不变；这尚不是整个性能模块休眠，验证范围见[Server 边界设计 §26](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#26-图标并发准入取消与-owner-停机排空2026-11-04mb-07-部分)。

图标等待/执行已进一步接入同一驻留协调器的真实工作租约；排队保护也会计入模块目录的 activeLeases，不等于同时执行的 OS 读取数。协调器关闭能撤销等待，实际读取结束前仍保留在途保护；准入失败不会继续调用 Helper 或原生读取。SystemPerformance 仍 resident，不支持闲时释放配置；采样和 Hub 等整体生命周期尚未全部接入，详见[Server 边界设计 §27](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#27-图标工作接入常驻协调器租约2026-11-04mb-07-部分)。

进程查询（包括缓存命中）和有效终止请求也已取得同一协调器的工作保护；停止 owner 后拒绝新调用，等待已准入调用完成后清理普通/管理员快照与 CPU 差分缓存。原查询/终止请求令牌、PID/完整启动时间核验、权限失败及未确认回执不变，不增加自动提升或终止重放。已返回的物化结果不因缓存清理而失效；这仍不表示整个性能模块可休眠。范围与验证见[Server 边界设计 §28](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#28-进程查询终止工作保护与快照-owner-排空2026-11-04mb-07-部分)。

请求只接受当前实例契约：

```http
DELETE /api/v1.0/system/processes/123
Authorization: Bearer <token>
Content-Type: application/json

{"expectedStartTime":"2026-10-01T00:00:00.1234567Z","elevated":false}
```

客户端保留查询提供的完整时间精度。不存在旧 force 参数、正常/强制两种模式或 PID-only 终止。缺失/默认启动时间被拒绝；同 PID 的另一实例返回 `process.instance_changed`。

Server 通过 `ProcessInstanceTermination` 校验并终止单个实例。Windows 创建时间读取、终止与退出等待使用同一内核进程句柄；管理员模式将这个固定操作交给权限助手执行。Linux x64/arm64 必须先取得 pidfd，再核对启动时间，通过该描述符发送 SIGKILL 并观察退出；不支持 pidfd 的宿主返回 `process.platform_unsupported`，不回退到 PID kill。只终止目标实例，不递归终止子进程。

回执的四个字段均为当前契约：`success`、`requiresElevation`、`problemCode`、`error`（可空）。只有观察到目标退出才返回 `success=true`，同时 requiresElevation=false、problemCode 为空。权限不足返回 `process.permission_denied` 与 requiresElevation=true；未确认退出返回 `process.termination_unverified`；缺失/变化/无法验证实例分别明确失败。HTTP 200 的失败回执不能当作操作成功。

查询和图标请求使用 elevated 布尔查询参数，终止正文使用 elevated 字段；默认 false。管理员请求每次以 ProcessManage 能力和 host/processes 目标检查授权，检查先于缓存、图标读取和终止。切换只探测读取并复用统一提权入口；桌面结束进程在普通模式收到明确 RequiresElevation 回执后，自动通过统一提升入口以管理员权限重试原 PID/启动时间实例一次，不切换整个列表的模式。管理员模式的结束命令也经过统一提升入口，授权过期且服务端明确拒绝时可重新授权；取消提升、会话变化或传输结果未知均不重放结束操作。失败和未知结果后刷新列表核实状态；Windows 11 User 模式（包括远程连接）按既有当前账户/owner-device 身份免密码授权。Windows Server、Linux 和 Windows 10/11 System 模式的普通账户需要管理员密码；已经系统认证的管理员/root 或仍有效的精确授权无需重复密码。普通模式即使当前账户是管理员也不自动切换。助手协议 v1.1 使用 ProcessSnapshot、ProcessIcon、ProcessTerminate 固定操作，不开放通用命令执行。客户端在确认时固定原登录会话、PID、启动时间，失败或传输结果未知不记录成功、不自动重放；重新读取列表用于核实。成功/明确回执后使进程缓存失效并刷新。未知响应不意味着进程一定还在运行。

原生语义依据：[Windows GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)、[Linux pidfd_open](https://www.man7.org/linux/man-pages/man2/pidfd_open.2.html)、[pidfd_send_signal](https://www.man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)。

## 客户端与维护约束

Avalonia `TaskManagerClient` 使用当前共享 DTO/路由，性能页静态信息、历史与实时流独立于低频进程查询；Server 提供排序与分页，桌面进程树取齐当前可见快照后本地过滤以保留祖先行。进程选择按 PID 与启动时间保留，缺少实例身份时无法执行结束命令；problemCode 映射为本地化反馈。

维护时同步 Shared、Server、桌面、Android、测试与文档；发布前直接采用当前协议，不保留旧路由/旧正文/双格式解析。数据不持久化，平台差异只归数据源与终止实现，客户端不自行计算 OS 计数器差分。编译、专项测试与真实设备/宿主验收分开记录。
