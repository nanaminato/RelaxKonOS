# RelaxKonOS 任务管理器重写方案（Goal 执行版）

> 状态：实施中（性能采样、协议、REST/Hub、Avalonia 主链路已迁移）
> 建立日期：2026-08-24
> 适用范围：`.NET 10` Server、Avalonia Client、Windows/Linux 宿主机
> 本文是后续 Goal 模式的执行基线。现有实现与设计见 [`RelaxKonOS.TaskManager.md`](./RelaxKonOS.TaskManager.md)；实施完成前，以现有文档描述的行为为准。

### 当前实施备注

- 已完成：统一 1 秒 `PerformanceSampler`、60 点内存 RingBuffer、Linux `/proc`/`/sys` 原始采集、Windows CPU/内存/网络与 `IOCTL_DISK_PERFORMANCE` 原始采集、性能 REST API、`/hubs/performance` 推送、客户端重连/历史回补，以及独立的 5 秒进程采样器。
- 当前 API：性能页使用 performance API，进程页使用分页查询 API；旧 metrics 和非分页进程列表端点已删除。
- 降级：Windows 服务账户若无权读取物理磁盘性能，或宿主机未提供该计数器时 `diskIo=false`；UI 不显示伪造 0 值。GPU 与传感器仍为后续可选提供方。

## 1. 结论与目标

任务管理器应从“客户端定时拉取的一组即时指标”重写为**服务端统一采样、缓存历史、按需实时推送**的系统性能诊断应用。

目标不是复刻 Windows 任务管理器，而是提供“Windows 性能页 + Linux `htop` / `iostat` 的服务器观测能力”：基础 CPU、内存、磁盘、网络在 Windows/Linux 上一致可用；GPU、温度等依赖硬件与驱动的能力以可选扩展提供。

必须满足：

- Avalonia 不感知宿主机是 Windows 还是 Linux。
- CPU、磁盘 I/O、网络速率等差分指标由服务端的单一采样器计算，不能由每位客户端或每个 HTTP 请求各自计算。
- 性能页以 SignalR 接收实时数据；REST 用于初始化、静态信息、进程查询及降级。
- 进程管理独立于性能流，不能因性能刷新而反复传输完整进程列表。
- 不引入 `PerformanceCounter` 作为跨平台主方案；CPU、内存、磁盘、网络由 RelaxKonOS 直接适配稳定 OS 接口。
- 指标不写入数据库。内存中的有限环形历史只用于 UI 重连与图表回放；长期留存另立需求。

## 2. 现状与重写原因

现有实现已具备第一版：`ISystemMetricsProvider` 按平台读取 CPU/内存，跨平台读取磁盘空间和网络计数；客户端每 2 秒并行请求 `GET /system/metrics` 与 `GET /system/processes`，并在客户端维护 60 个 CPU/内存点。

| 现状 | 问题 | 重写后的决定 |
|---|---|---|
| 每个打开的客户端每 2 秒轮询指标 | 采样频率与客户端数量耦合，慢请求影响差分精度 | 一个服务端 `PerformanceSampler` 统一采样，客户端订阅推送 |
| 差分状态保存在 `ISystemMetricsProvider` | 采样由 HTTP/进程请求驱动，首点为 0，难以回放历史 | 差分状态只归 `PerformanceSampler` 所有 |
| `DiskUsageDto` 只表达空间 | “磁盘”混合文件系统容量与设备性能两个概念 | 分离 `Filesystem`（容量）和 `Disk`（I/O） |
| 网络以接口名称作键 | 改名、重启、虚拟接口会造成身份不稳定 | 对外使用稳定 `InterfaceId`，名称只作显示 |
| 性能请求与进程列表绑定刷新 | 进程数量大时浪费带宽且影响图表 | 性能流、进程列表、进程操作三个独立通道 |
| GPU 直接是总快照一部分 | 驱动差异会拖累核心采样路径 | GPU/传感器是独立的 best-effort 能力 |

## 3. 范围

### 3.1 V1（本重写必须完成）

| 域 | 能力 |
|---|---|
| CPU | 总利用率、每逻辑 CPU 利用率、用户/内核/空闲/iowait（可用时）、型号、物理核/逻辑核、当前频率（可用时）、运行时间 |
| 内存 | 总量、可用、已用、缓存/缓冲（可用时）、Swap/Page File；Linux 已用量按 `MemTotal - MemAvailable` 计算 |
| 文件系统 | 挂载点/盘符、容量、已用、可用、使用率 |
| 磁盘 I/O | 设备读写 B/s、读写 IOPS、忙碌率、队列/响应时间（平台支持时） |
| 网络 | 接口收发 B/s、累计字节/包、错误/丢包、链路速度与地址（可用时） |
| 实时性 | 1 秒采样、服务端保存最近 60 个点、SignalR 推送、REST 初始快照/降级 |
| 进程 | 独立按需列表、服务端分页/过滤/排序、结束单个进程、清楚的权限不足结果 |
| 可靠性 | 取消、超时、慢订阅者隔离、连接重连、平台能力缺失不影响其余指标 |

### 3.2 明确不属于 V1

- GPU 细分引擎、非 NVIDIA GPU 完整支持、GPU 温度。
- CPU/磁盘/风扇/电压/功率等硬件传感器。
- 进程树、服务管理、启动项、容器逐项指标。
- 指标数据库、告警、长期报表、多主机聚合。

上述能力不得阻塞 V1；它们通过第 11 节的扩展点逐步加入。

## 4. 目标架构

```text
OS 原始计数器（Windows API / Linux /proc、/sys）
                    ↓
    ISystemPerformanceSource（只读取原始数据与静态信息）
                    ↓
 PerformanceSampler（1 秒、差分、归一化、时间戳）
                    ↓
       PerformanceHistory（内存 RingBuffer，60 点）
                    ↓
 ┌──────────────────┴──────────────────┐
 REST：info / snapshot / processes      SignalR：realtime snapshots
 └──────────────────┬──────────────────┘
                    ↓
       TaskManagerClient + 实时订阅客户端
                    ↓
       Avalonia：性能页 / 进程页（独立刷新策略）
```

### 4.1 服务端职责

1. `ISystemPerformanceSource`：读取本机原始累计计数器与静态信息；不保存上次读数，不计算速率。
2. `PerformanceSampler`：Singleton `BackgroundService`，拥有采样节奏、前后快照、派生速率与单调递增序列号。
3. `PerformanceHistory`：线程安全环形缓冲，保存最近 60 个已归一化实时快照；服务重启后自然清空。
4. `PerformanceSubscriptionService` / Hub：向已授权订阅者广播。采样循环绝不能等待客户端网络 I/O。
5. `IProcessService`：进程查询、结束进程等按需操作，与性能采样器分离。

采样周期固定为 1 秒。第一份原始数据只用于建立基线，不广播含有伪造 0% 速率的“有效样本”；Hub 在首个有效样本后才开始推送。页面暂停应只停止客户端渲染/订阅，不停止全局采样。

### 4.2 平台实现边界

```text
ISystemPerformanceSource
├── WindowsPerformanceSource
│   ├── CPU/Memory：GetSystemTimes、GlobalMemoryStatusEx；逐核采用可验证的原生查询
│   ├── Disk/Network：Windows 原生计数器或 IP Helper API
│   └── 静态信息：WMI/CIM 或原生 API（失败时字段标记 unavailable）
└── LinuxPerformanceSource
    ├── CPU：/proc/stat；频率：/sys/devices/system/cpu（可用时）
    ├── Memory：/proc/meminfo
    ├── Disk：/proc/diskstats 与 /sys/block/*/stat
    └── Network：/sys/class/net/*/statistics 与地址信息
```

禁止在 Endpoint、Hub、Avalonia ViewModel 中出现 `OperatingSystem.IsWindows()`、`/proc` 路径、P/Invoke 或计数器差分逻辑。禁止用 `System.Diagnostics.PerformanceCounter` 作为新跨平台架构基础；若未来 Windows 适配器局部使用 PDH，仍必须藏在 `WindowsPerformanceSource` 内。

## 5. 领域模型与协议

### 5.1 静态信息与实时数据必须分离

`PerformanceInfoDto` 仅在进入性能页、显式刷新或服务端重连时获取，包含低变化信息：

- `CpuInfoDto`：型号、物理核、逻辑 CPU、基准/最大频率（可空）、虚拟化状态（可空）。
- `MemoryInfoDto`：物理内存总量、Swap/Page File 总量。
- `DiskInfoDto`：稳定 `Id`、显示名、型号（可空）、容量（可空）、关联挂载点。
- `NetworkInterfaceInfoDto`：稳定 `Id`、显示名、MAC（按安全策略决定是否返回）、链路速度、地址。
- `GpuInfoDto`：仅在已支持的提供方可用时返回。

`PerformanceRealtimeSnapshotDto` 每秒推送，仅包含高变化数值、`Timestamp`、连续 `Sequence` 与能力/缺失标识。数值不以格式化字符串传输；Client 负责本地化和字节单位展示。

```text
PerformanceInfoDto
  Cpu, Memory, Filesystems[], Disks[], Networks[], Gpus[], Capabilities

PerformanceRealtimeSnapshotDto
  Sequence, Timestamp, Cpu, Memory, Disks[], Networks[], Gpus[]?, Health
```

`Capabilities` 是显式能力位而非“0 或 null 的猜测”，如 `diskLatency=false`、`gpu=false`、`cpuTemperature=false`。无法采集的数据用 `null` / 未包含项表示；**0 永远只表示实际测得的 0**。

### 5.2 文件系统与物理磁盘分离

不要再用一个 `DiskUsageDto` 同时承载所有磁盘含义：

| 模型 | 代表什么 | 示例 |
|---|---|---|
| `FilesystemUsageDto` | 用户可见的容量空间 | `C:\`、`/`、`/var/lib/docker` |
| `DiskRealtimeMetricsDto` | 物理/逻辑块设备的 I/O | `PhysicalDrive0`、`nvme0n1`、`sda` |

Linux 适配器必须过滤或标记 loop、ram、zram、重复的 device-mapper/分区统计，避免把同一 I/O 叠加多次。若无法可靠建立“挂载点 → 设备”的一对多映射，UI 只分别呈现两组数据，不推测关联。

### 5.3 API 与 Hub

| 通道 | 路由/事件 | 目的 |
|---|---|---|
| REST | `GET /api/v1.0/system/performance/info` | 静态信息与能力 |
| REST | `GET /api/v1.0/system/performance/snapshot` | 当前有效快照；首次进入、重连和测试的降级路径 |
| REST | `GET /api/v1.0/system/performance/history?seconds=60` | 最近有效点；上限 60，不能作长期查询 |
| SignalR | `/hubs/performance` | 性能实时订阅 Hub |
| Server → Client | `performanceSnapshot` | `PerformanceRealtimeSnapshotDto` |
| Client → Server | `subscribePerformance` / `unsubscribePerformance` | 显式控制接收；断开自动取消 |
| REST | `GET /api/v1.0/system/processes?...` | 按需、可分页的进程查询 |
| REST | `DELETE /api/v1.0/system/processes/{id}` | 结束进程；保持不自动提权 |

所有路径、Hub 名和事件名必须定义在 `Shared/RelaxKonOS.Protocol`；Server 与 Client 禁止硬编码。Hub 使用 JWT 认证并遵循现有 SignalR JSON 选项。初始渲染顺序：取 `info` → 取 `history`/`snapshot` → 建连并订阅 → 按 `Sequence` 丢弃过期或重复事件。

### 5.4 进程 API

进程数据是独立资源，不进入每秒实时快照。建议请求参数：`page`、`pageSize`（设上限）、`sort`、`direction`、`filter`、`includeSystem`；返回 `ProcessPageDto { Items, TotalCount?, SampledAt }`。

进程 CPU 由独立的服务端进程采样缓存计算，使用 `(PID, StartTime)` 作为进程实例键，避免 PID 复用。进程列表仅在首次打开、用户刷新、过滤/分页/排序变化或低频自动刷新时请求；性能页不再触发它。

## 6. 指标定义（验收口径）

所有速率使用相邻两份原始累计值与实际单调时间间隔计算，不允许假设正好经过 1 秒。时钟跳变不能影响差分；采样器使用 `Stopwatch` 或等价单调时间源。

| 域 | 计算与口径 |
|---|---|
| CPU 总利用率 | `100 × (1 - Δidle / Δtotal)`；Linux idle 包含 `idle + iowait`，同时单列 `iowait`（可用时） |
| 每逻辑 CPU | 每个逻辑 CPU 独立差分；Windows 不得再把整机百分比复制到每一核 |
| Linux 内存已用 | `MemTotal - MemAvailable`；缓存/缓冲是附加明细，不能把 `MemFree` 当“可用” |
| Windows 内存已用 | `TotalPhysical - AvailablePhysical`；Page File 独立显示 |
| 磁盘 B/s | `Δsectors × confirmedSectorSize / Δelapsed`，或等价字节累计值；无法确认扇区大小时不报导速率 |
| IOPS | `ΔcompletedOperations / Δelapsed`，区分读、写 |
| 磁盘忙碌率 | `100 × ΔioBusyTime / Δelapsed`，裁剪至 0–100；不把队列长度误作百分比 |
| 网络 B/s | `Δrx_bytes / Δelapsed`、`Δtx_bytes / Δelapsed`；接口重置时该周期标记重置/不可用，不能误导为真实 0 |
| 时间戳 | UTC `DateTimeOffset`；客户端显示本地时区 |

`DiskLatencyMs`、`QueueLength`、CPU 频率、用户/内核/steal、链路速度等均是“平台能力增强”，只在数据源真实提供且口径可验证时显示。

## 7. 客户端体验

性能页默认包含 CPU、内存、文件系统、磁盘 I/O、网络，按可用能力显示 GPU。每个图表采用最近 60 秒曲线；历史由服务端返回，客户端仅维护渲染状态，不再作为事实来源。

- 更新速度：正常 1 秒；可选高 500 ms（仅服务端允许时）、低 5 秒、暂停渲染。V1 可先实现正常与暂停。
- 显示“实时 / 正在重连 / 已降级为快照 / 数据不可用”，不能把断连误显示为 0%。
- 页面隐藏或窗口关闭时取消 Hub 订阅与进程自动刷新；重新显示时补一次 `history` 消除空洞。
- 图表和列表节流批量更新，避免每个点多次触发 Avalonia 布局。
- 进程页显示采样时间、排序字段和结果数量；结束任务保持现有“不自动提权”和清楚反馈。

## 8. Goal 执行计划

每个 Goal 必须单独可构建、可测试、可回滚；除非该 Goal 的验收项全部通过，不进入下一项。

### Goal 1：冻结边界与建立新协议

**工作**：新增 `PerformanceInfo`、实时快照、历史、能力、文件系统/磁盘分离 DTO，新增性能 Hub 契约与路由；为 `/metrics` 制定废弃策略。

**验收**：Protocol 保持零 PackageReference；所有 DTO 有稳定 JSON 名称；Client 与 Server 只引用共享常量；兼容期限和移除版本写入变更说明。

### Goal 2：原始采集抽象与 Linux 实现

**工作**：建立无差分 `ISystemPerformanceSource`，从 `/proc/stat`、`/proc/meminfo`、`/proc/diskstats`、`/sys` 和网络统计读取原始数据；将解析提取为纯函数。

**验收**：录制的 `/proc` 与 `/sys` fixture 覆盖正常、缺字段、计数器回退、热插拔设备；不依赖开发机实时数值；磁盘过滤规则有测试。

### Goal 3：Windows 实现与逐核正确性

**工作**：实现等价 Windows 数据源，完成真实逐逻辑 CPU 利用率与内存；选择并验证 Windows 磁盘和网络原始计数来源。

**验收**：Windows 每核数据不再复制全局值；在空闲、单核负载、磁盘/网络压力下与可信 OS 工具做合理误差比对；P/Invoke 有平台隔离和失败回退。

### Goal 4：统一采样器、历史与健康模型

**工作**：实现 1 秒 `BackgroundService`、单调时间差分、60 点环形缓存、有效样本门槛、能力/采样错误状态和并发读取。

**验收**：任意数量 REST/Hub 客户端不改变采样次数；首个有效样本不伪造速率；慢消费者不阻塞采样；重启后正常建立基线；采样器测试覆盖公式与边界。

### Goal 5：REST、Hub 与授权/重连

**工作**：提供 info/snapshot/history REST 端点和 `/hubs/performance`；实现订阅管理、背压策略、Client 重连与序列去重。

**验收**：未授权访问被拒绝；订阅者只收到递增序列；断线重连可从 history 补齐；Hub 不产生按用户重复采样；REST 可在无 WebSocket 环境降级。

### Goal 6：Avalonia 性能页重写

**工作**：替换 2 秒 metrics 轮询，接入 info/history/realtime；重做卡片、60 秒图表、能力缺失、重连和暂停状态；将文件系统容量与磁盘 I/O 分区呈现。

**验收**：一个或多个窗口都平滑、无请求堆积；断线不显示假 0；关闭窗口后无活跃订阅；Windows/Linux 服务端显示同一模型。

### Goal 7：进程页独立化与安全收尾

**工作**：分页/排序/过滤 API，独立的进程采样缓存，优化列表虚拟化与低频刷新；审计结束进程的异常分类与日志边界。

**验收**：性能页不请求进程列表；大量进程下界面可用；PID 复用不会混淆 CPU；权限不足仍不提权、不泄露宿主机敏感信息。

### Goal 8：端到端验证、文档迁移与删除旧路径

**工作**：Windows/Linux 冒烟、负载比对、断线重连、兼容性测试；更新主设计、协议文档、本地化和运维文档；兼容期后删除旧路径。

**验收**：`dotnet build RelaxKonOS.sln -c Debug` 为 0 错误；新增测试稳定；文档不再把客户端轮询描述为目标架构；旧代码无死引用。

## 9. 测试与观测要求

- **单元测试**：CPU/内存/磁盘/网络公式、计数器回退、首次基线、RingBuffer、序列号、能力降级。
- **适配器测试**：Linux 文本 fixture；Windows 原生调用包装为可替换接口，验证失败与边界。
- **集成测试**：认证、REST、Hub 订阅/取消、重连、无 WebSocket 降级。
- **手工对照**：Linux 与 `top`/`free`/`iostat`/`ip -s link`，Windows 与任务管理器/资源监视器在同时间窗口比对；差异必须能解释采样窗口与口径。
- **性能测试**：多订阅者、进程数较多、慢客户端、网络抖动；记录采样耗时、失败次数、订阅数和最近有效样本时间。

运行时采集错误必须归类到安全日志/健康状态；API 不返回 `/proc` 原文、命令行、完整环境变量、MAC 地址等未经确认允许暴露的敏感信息。

## 10. 迁移策略

1. 新协议和 Hub 直接替换旧任务管理器路径。
2. metrics 和非分页进程列表端点不再提供。
3. `DELETE /processes/{id}` 的不自动提权语义保持不变；接口发生变化时同步更新所有调用方。

## 11. 后续扩展点

GPU 与硬件传感器使用独立接口，不能污染基础性能路径：

```csharp
public interface IGpuPerformanceProvider { /* optional, driver-specific */ }
public interface IHardwareSensorProvider { /* temperature, fan, power, voltage */ }
```

优先实现 NVIDIA（`nvidia-smi` / NVML）可选提供方；AMD、Intel 与 Windows 硬件传感器仅在数据源和授权模型明确后加入。服务器扩展可包括 Linux load average、steal、TCP 状态、磁盘队列、Docker CPU/内存和 cgroup 指标，但都应以能力标记呈现。

## 12. 不可违反的规则

1. Server 是性能数据唯一真源；Client 不计算跨样本速率。
2. 差分状态只存在于 `PerformanceSampler`，不属于 HTTP 请求、Hub 连接或 ViewModel。
3. 基础 CPU/内存/磁盘/网络不依赖单个第三方“万能跨平台”库。
4. 平台差异局限于适配器；协议和 UI 只消费统一 DTO 与能力标记。
5. 静态信息、实时性能、进程查询和进程控制是不同资源，不能合并成万能 Endpoint。
6. 未采集到的数据不是 0；必须显式表达 unavailable / unsupported / stale。
7. RelaxKonOS 不保存宿主机密码、不自动 `sudo` 或 UAC 提权；进程结束受宿主 OS 权限约束。
8. 不将瞬时指标写入业务数据库；长期指标与告警必须作为独立需求设计。
