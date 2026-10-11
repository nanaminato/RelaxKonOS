# RelaxKonOS 任务管理器

> 当前实现：2026-10-01。Server 统一性能采样、内存历史与 SignalR；进程列表独立按需查询。Android 行为与验收由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/README.md) 管理。

## 性能采样

`ISystemPerformanceSource` 隔离 Windows 原生 API 与 Linux `/proc`、`/sys` 原始读取。`PerformanceSampler` 统一使用相邻计数器和实际单调时间间隔计算速率，每秒采样，保存最多 60 点内存历史；不接入数据库。至少一个订阅者存在或 REST 申请有界 demand 时才采样，无订阅者后清除基线与历史。首次原始读取只建立基线，不把差分未就绪显示为 0%。

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

查询和图标请求使用 elevated 布尔查询参数，终止正文使用 elevated 字段；默认 false。管理员请求每次以 ProcessManage 能力和 host/processes 目标检查授权，检查先于缓存、图标读取和终止。切换只探测读取并复用统一提权入口；Windows 11 User 模式（包括远程连接）按既有当前账户/owner-device 身份免密码授权。Windows Server、Linux 和 Windows 10/11 System 模式的普通账户需要管理员密码；已经系统认证的管理员/root 或仍有效的精确授权无需重复密码。普通模式即使当前账户是管理员也不自动切换。助手协议 v1.1 使用 ProcessSnapshot、ProcessIcon、ProcessTerminate 固定操作，不开放通用命令执行。客户端在确认时固定原登录会话、PID、启动时间，失败或传输结果未知不记录成功、不自动重放；重新读取列表用于核实。成功/明确回执后使进程缓存失效并刷新。未知响应不意味着进程一定还在运行。

原生语义依据：[Windows GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)、[Linux pidfd_open](https://www.man7.org/linux/man-pages/man2/pidfd_open.2.html)、[pidfd_send_signal](https://www.man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)。

## 客户端与维护约束

Avalonia `TaskManagerClient` 使用当前共享 DTO/路由，性能页静态信息、历史与实时流独立于低频进程查询；Server 提供排序与分页，桌面进程树取齐当前可见快照后本地过滤以保留祖先行。进程选择按 PID 与启动时间保留，缺少实例身份时无法执行结束命令；problemCode 映射为本地化反馈。

维护时同步 Shared、Server、桌面、Android、测试与文档；发布前直接采用当前协议，不保留旧路由/旧正文/双格式解析。数据不持久化，平台差异只归数据源与终止实现，客户端不自行计算 OS 计数器差分。编译、专项测试与真实设备/宿主验收分开记录。
