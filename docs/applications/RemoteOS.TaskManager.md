# RemoteOS TaskManager 模块设计

> **迁移状态（2026-08-24）**：本文以下内容描述第一代 REST 轮询实现。当前架构见 [`RemoteOS.TaskManager.Rewrite.md`](./RemoteOS.TaskManager.Rewrite.md)：性能页使用“Server 统一采样 + 内存历史 + SignalR 推送”，进程页使用独立低频采样与分页查询。旧 `/api/v1.0/system/metrics` 与非分页 `/processes` 已删除。

> 内置任务管理器：参考 Windows 任务管理器 / GNOME 系统监视器，两个标签页（性能 / 进程）。性能页实时展示 CPU / 内存 / 磁盘 / 网络 / GPU 占用与历史柱状图；进程页列出当前可见进程，可结束任务（权限不足提示需在宿主 OS 提权）。数据经 Server REST API 拉取，服务端以宿主 OS 进程身份采集（复用宿主用户/权限，不另建 ACL）。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](../architecture/RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](../README.md)（§6 内置应用）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md)（TaskManager 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](../architecture/RemoteOS.Protocol.md)（§SystemMonitor DTO 与路由）
> - 服务端持久化见 [`RemoteOS.Storage.md`](../platform/RemoteOS.Storage.md)（TaskManager 不落库——指标与进程列表均为实时采样）
> - 安全设计见 [`RemoteOS.Security.md`](../platform/RemoteOS.Security.md)（§权限提升委托宿主 OS——结束进程不自动提权）

---

## 1. 定位

TaskManager 是 RemoteOS 的内置系统监控应用，参考 Windows 任务管理器 / GNOME 系统监视器。

- **架构归属**：§6.2 Remote Service Application —— UI 完全在 Client 本地渲染；指标与进程列表**真源在 Server 端实时采集**（不持久化，每次请求都是当下快照）。
- **复用宿主 OS 权限**（硬约束）：Server 端 `ISystemMetricsProvider` 以宿主 OS 进程身份读取 `/proc`（Linux）或 Win32 API（Windows），复用宿主用户/权限，不另建 ACL。结束进程权限不足时返回 `RequiresElevation=true`，**RemoteOS 不自动提权**——用户需在宿主 OS 提权（`sudo kill` / UAC 运行）。
- **不存储密码**：认证委托宿主 OS，TaskManager 仅消费 `IAuthSession.Tokens.AccessToken`。
- **跨平台抽象**：与 `IIdentityProvider` 同模式——`ISystemMetricsProvider` 接口 + `WindowsMetricsProvider` / `LinuxMetricsProvider` 实现，平台差异封装在 Provider 之后，Server 端单一代码库跨 Ubuntu + Windows Server。

**两个标签页**：

| 标签页 | 能力 | 数据源 |
|--------|------|--------|
| 性能 | CPU（整机 + 每核 + 60 采样柱状图）/ 内存（占用 + 柱状图）/ 磁盘（每盘已用·总计·占比）/ 网络（每接口上下行速率）/ GPU（nvidia-smi）/ 运行时间 | `GET /api/v1.0/system/performance/info` + snapshot/history + SignalR |
| 进程 | 当前可见进程列表（名称 / PID / CPU% / 内存 / 用户 / 线程），按名称/PID/用户过滤，选中后「结束任务」 | `GET /api/v1.0/system/processes/query` + `DELETE /api/v1.0/system/processes/{id}` |

---

## 2. 嵌入方式

`TaskManagerMainView` 作为 `UserControl` 塞进 `RemoteWindow`，与 Notepad / Explorer / Browser / Terminal / Settings 同构：

```text
TaskManagerApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("任务管理器", view, bounds=980x680)
    |
    WindowManager.Create → RemoteWindow
    |
    TaskManagerMainView (UserControl)
        ├── 顶部工具栏（性能/进程标签切换 + 刷新 + 自动刷新 + 状态 + 关闭）
        └── 内容区（性能面板 / 进程面板叠加，按 ActiveTab 切换可见）
```

`TaskManagerApp.Activate` 注入 `IAuthSession` + `ITaskManagerClient`（从 `context.Services`）。未登录时弹 `TextBlock` 提示窗（460x180，不可缩放），不崩溃；登录则构造 `TaskManagerViewModel` + `TaskManagerMainView`，`context.ShowWindow`（bounds 980x680）后 `_ = viewModel.StartAsync()` 异步启动实时刷新。

---

## 3. 协议契约（`Shared/RemoteOS.Protocol/SystemMonitor/`）

9 个文件，沿用 Protocol 约定（`sealed record` + `[property: JsonPropertyName]`，零 PackageReference）：

### 3.1 路由（`SystemMonitorApiRoutes.cs`）

路径含 `/api/v1.0` 前缀，Server 注册路由与 Client 拼接 URL 共用：

```text
PerformanceInfo     = /api/v1.0/system/performance/info       (GET)
PerformanceSnapshot = /api/v1.0/system/performance/snapshot   (GET)
ProcessQuery        = /api/v1.0/system/processes/query        (GET)
ProcessKill  = /api/v1.0/system/processes/{id}       (DELETE, query: force)
```

### 3.2 DTO

| DTO | 字段 | 说明 |
|-----|------|------|
| `SystemMetricsDto` | Cpu / Memory / Disks / Networks / Gpus / UptimeSeconds / Timestamp | 整机资源占用聚合快照 |
| `CpuUsageDto` | TotalPercent / PerCorePercent / CoreCount | CPU 占用（0-100），每核列表 |
| `MemoryUsageDto` | TotalBytes / UsedBytes / AvailableBytes / Percent | 内存占用（字节） |
| `DiskUsageDto` | Name / TotalBytes / UsedBytes / FreeBytes / Percent | 单个磁盘/挂载点（Windows 盘符 / Linux 挂载路径） |
| `NetworkUsageDto` | Name / BytesSent / BytesReceived / SendRateBytesPerSec / ReceiveRateBytesPerSec | 单个网络接口累计字节 + 瞬时速率（字节/秒，相邻采样差分） |
| `GpuUsageDto` | Name / UsagePercent? / MemoryTotalBytes? / MemoryUsedBytes? / TemperatureCelsius? | 单个 GPU（字段可空：无 NVIDIA 驱动时为 null） |
| `ProcessInfoDto` | Id / Name / CpuPercent / MemoryBytes / UserName? / StartTime? / ThreadCount | 单个进程（CPU% 由相邻采样差分，首次为 0） |
| `KillProcessResultDto` | Success / RequiresElevation / Error? | 结束进程结果（权限不足时 RequiresElevation=true） |

---

## 4. 服务端

### 4.1 跨平台抽象（`Server.SystemMonitor/`）

与 `IIdentityProvider` 同模式——接口 + 平台实现，`Program.cs` 按宿主 OS 选择：

```text
ISystemMetricsProvider (接口)
    │
    ├── SystemMetricsProviderBase (抽象基类，Singleton)
    │     ├── 跨平台共享：进程列表 / 结束进程 / 磁盘 / 网络 / GPU / 运行时间
    │     └── 抽象：GetCpuUsageAsync / GetMemoryUsageAsync / GetProcessUserName
    │
    ├── WindowsMetricsProvider : Base
    │     ├── CPU：GetSystemTimes (kernel32 P/Invoke) 相邻采样差分
    │     └── 内存：GlobalMemoryStatusEx (kernel32 P/Invoke)
    │
    └── LinuxMetricsProvider : Base
        ├── CPU：/proc/stat 解析（聚合 cpu 行 + 每核 cpuN 行）差分
        ├── 内存：/proc/meminfo（MemTotal / MemAvailable）
        └── 进程属主：/proc/[pid]/status 解析 Uid + /etc/passwd 映射 uid→用户名
```

**注册**（`Program.cs`）：

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    builder.Services.AddSingleton<ISystemMetricsProvider, WindowsMetricsProvider>();
else
    builder.Services.AddSingleton<ISystemMetricsProvider, LinuxMetricsProvider>();
```

**Singleton 的理由**：Provider 持有相邻采样差分状态（CPU% 与网络速率都需要「上一次采样」作基准）。Singleton 保证跨请求的状态连续性。

### 4.2 指标采集（`SystemMetricsProviderBase.GetMetricsAsync`）

单次采样组装 `SystemMetricsDto`：

```text
GetMetricsAsync
    ├── GetCpuUsageAsync(ct)        ← 平台子类实现
    ├── GetMemoryUsageAsync(ct)     ← 平台子类实现
    ├── GetDiskUsage()              ← DriveInfo.GetDrives() 跨平台
    ├── GetNetworkUsage()           ← NetworkInterface.GetAllNetworkInterfaces() + 相邻采样差分
    ├── GetGpuUsageAsync(ct)        ← nvidia-smi 子进程（best-effort）
    └── UptimeSeconds = Environment.TickCount64 / 1000
```

**CPU% 差分**（核心算法）：

- **整机**（Linux）：`/proc/stat` 聚合 `cpu` 行 `usage% = (1 - idle_delta/total_delta) * 100`，`idle_all = idle + iowait`。
- **整机**（Windows）：`GetSystemTimes` 返回 idle/kernel/user 三组 FILETIME，`total = kernel + user`（kernel 已含 idle），`usage% = (1 - dIdle/dTotal) * 100`。
- **每核**（Linux）：`/proc/stat` 的 `cpu0..cpuN-1` 行各算一次。Windows 当前暂以整机占比填充每核（`GetSystemTimes` 仅返回聚合；逐核需 `NtQuerySystemInformation`，留待后续）。
- **进程 CPU%**：`Process.TotalProcessorTime` 相邻采样差分 `/ (elapsed * cores) * 100`（相对整机，单进程上限 100%）。

**网络速率差分**：`NetworkInterface.GetIPv4Statistics()` 的 `BytesSent` / `BytesReceived` 相邻采样差分 `/ elapsed`（字节/秒）。计数器可能因接口重置而回绕/归零，做下界保护（`sent >= prev ? delta : 0`）。

**GPU（best-effort）**：通过 `nvidia-smi --query-gpu=name,utilization.gpu,memory.total,memory.used,temperature.gpu --format=csv,noheader,nounits` 子进程解析。Linux/Windows 通用；非 NVIDIA 或无驱动返回空列表（客户端隐藏 GPU 卡片显示提示文案）。3 秒超时强制 kill 子进程。

**内存**（Linux）：`/proc/meminfo` 的 `MemTotal` / `MemAvailable`（`used = total - available`）。（Windows）：`GlobalMemoryStatusEx` 的 `ullTotalPhys` / `ullAvailPhys` / `dwMemoryLoad`。

### 4.3 进程列表（`SystemMetricsProviderBase.ListProcessesAsync`）

```text
ListProcessesAsync
    ├── Process.GetProcesses()
    ├── 逐进程：
    │     ├── TotalProcessorTime → 相邻采样差分 → CpuPercent（首次为 0）
    │     ├── WorkingSet64 → MemoryBytes
    │     ├── Threads.Count → ThreadCount
    │     ├── StartTime → StartTime（DTO，可空——读取失败则 null）
    │     ├── ProcessName → Name（读取失败则 "pid:{id}"）
    │     └── GetProcessUserName(process) → UserName（Linux 解析 /proc；Windows 暂返回 null）
    ├── 持有 next 字典，替换 _procSamples（供下次差分）
    └── 默认按内存降序排序（CPU 占用高的易于定位，客户端可重排）
```

单个进程读取失败静默跳过（`catch` + `p.Dispose()`），不影响整体列表。

### 4.4 结束进程（`SystemMetricsProviderBase.KillProcessAsync`）

```text
KillProcessAsync(processId, force)
    ├── Process.GetProcessById(processId)
    ├── p.Kill(entireProcessTree: false)     ← 仅终止目标进程，不波及子进程
    ├── p.WaitForExit(3000)
    └── 异常映射：
          ├── ArgumentException → Success=false, "进程不存在"
          ├── Win32Exception (NativeErrorCode 5/1/13) → RequiresElevation=true
          │     "权限不足，无法结束进程 {id}（需在宿主 OS 提升权限，例如 sudo kill / UAC 运行）。"
          └── 其他 Exception → Success=false, ex.Message
```

**权限不足错误码**：Windows `ERROR_ACCESS_DENIED=5`；Linux `EPERM=1` / `EACCES=13`——映射为 `RequiresElevation=true`。符合硬约束「权限提升委托宿主 OS」——RemoteOS 不存储宿主密码、不自动提权。

### 4.5 REST 端点（`Server.Endpoints/SystemMonitorEndpoints.cs`）

5 个端点，全 `RequireAuthorization()`，错误统一 RFC 7807（与 Browser/Files 端点同风格）：

| Method | Route | 用途 |
|--------|-------|------|
| GET | `/api/v1.0/system/performance/info` | 低频性能信息与能力 |
| GET | `/api/v1.0/system/performance/snapshot` | 最近有效实时性能快照 |
| GET | `/api/v1.0/system/performance/history?seconds=` | 最近 60 秒性能历史 |
| GET | `/api/v1.0/system/processes/query` | 分页、过滤与排序的进程查询 |
| DELETE | `/api/v1.0/system/processes/{id}?force=` | 结束进程（`force` 可选，默认 false；返回 `KillProcessResultDto`） |

`Program.cs` 注册：`app.MapSystemMonitorEndpoints()`。

> **不持久化**：TaskManager 不接入 `RemoteOsDbContext`——指标与进程列表均为实时采样，每次请求返回当下快照。无 SQLite 表、无增量补齐。

---

## 5. 客户端

### 5.1 客户端 HTTP（`ITaskManagerClient` / `TaskManagerClient`）

- typed HttpClient（`Bootstrapper` 注册 `services.AddHttpClient<ITaskManagerClient, TaskManagerClient>()`）。
- **不 mutate `HttpClient.BaseAddress`**（避免共享实例并发竞态），每请求用 `IAuthSession.ServerUrl` 构造绝对 URI。
- `Authorization: Bearer {AccessToken}` 从 `IAuthSession.Tokens` 取；未登录抛 `InvalidOperationException`。
- 路由常量共用 `SystemMonitorApiRoutes`，`{id}` 用 `processId.ToString(InvariantCulture)` 替换，禁止硬编码字符串。
- 失败读 `ProblemDetails` 抛 `RemoteOsAuthException`（与 `BrowserClient` / `ExplorerClient` / `SettingsClient` 同源模式）。
- JSON 用 `RemoteOsJsonOptions.Default`（与线协议一致）。

### 5.2 应用入口（`TaskManagerApp`）

`RemoteApplicationBase`：`Manifest`（Id=`remoteos.taskmanager`，Icon=`📊`）+ `Activate(AppContext)`。

- 未登录：弹 `TextBlock` 提示窗（460x180，`canResize/canMinimize/canMaximize=false`），不崩溃。
- 登录：构造 `TaskManagerViewModel(client)` + `TaskManagerMainView`，`context.ShowWindow`（bounds 980x680），注入 `CloseAction`，`_ = viewModel.StartAsync()` 异步启动刷新。

### 5.3 ViewModel（`TaskManagerViewModel`）

`CommunityToolkit.Mvvm`（`[ObservableProperty]` + `[RelayCommand]`）。

**刷新机制**：

- `DispatcherTimer`（2s 间隔）触发 `RefreshAsync`。
- `StartAsync()`：立即采集一次 + 启动定时器（若 `IsAutoRefresh`）。
- `Stop()`：停止定时器（View `Unloaded` 时调用，避免对已关闭视图继续刷新）。
- **重入保护**：`Interlocked.CompareExchange(ref _refreshing, 1, 0)`——上一次刷新未完成时跳过本次 tick（避免请求堆积）。

**RefreshAsync 数据流**：

```text
RefreshAsync (Interlocked 重入保护)
    ├── Task.WhenAll(GetMetricsAsync, ListProcessesAsync)   ← 并行拉取
    ├── Metrics = metrics → 触发性能页绑定更新
    ├── UpdateCharts(metrics) → CpuHistory/MemoryHistory 追加采样（上限 60）
    ├── HasGpu = metrics.Gpus.Count > 0 → 控制 GPU 卡片可见性
    ├── UpdateProcesses(procs) → 按过滤词刷新 FilteredProcesses（保留选中项）
    └── StatusText = $"已更新 — {HH:mm:ss}　CPU {x}%　进程 {n}"
```

**性能页历史柱状图**：`CpuHistory` / `MemoryHistory`（`ObservableCollection<double>`，上限 60 个采样）。XAML 用 `ItemsControl` + 横向 `StackPanel` + `Rectangle`（宽 3px，高 = 采样值，`VerticalAlignment=Bottom`）渲染实时柱状图。`AppendHistory` 做 `Math.Clamp(0, 100)` 保护。

**进程页**：

- `FilteredProcesses`（`ObservableCollection<ProcessInfoDto>`）按 `ProcessFilter` 过滤（名称 / PID / 用户，不区分大小写）。
- `SelectedProcess` 双向绑定 `ListBox.SelectedItem`，`OnSelectedProcessChanged` 通知 `KillProcessCommand.NotifyCanExecuteChanged()`。
- `UpdateProcesses`：刷新后保留选中项——按 `(Id, StartTime)` 匹配（DTO 是新实例，靠 Id+StartTime 识别同一运行中进程）；选中项已被过滤掉时清空。
- `KillProcessAsync`：调 `KillProcessAsync(proc.Id, force: false)`，按 `Success` / `RequiresElevation` / `Error` 更新 `KillFeedback`，结束后立即 `RefreshProcessesAsync` 刷新列表。

**标签页切换**：`ActiveTab`（`TaskManagerTab.Performance` / `Processes`）+ `SwitchToPerformance` / `SwitchToProcesses` 命令。XAML 用 `TabVisibilityConverter` + `ConverterParameter` 控制两个面板叠加的可见性。

**过滤**：`ProcessFilter` 双向绑定 TextBox，`OnProcessFilterChanged` → `ApplyFilter`。`ClearFilter` 命令清空。View code-behind `FilterBox_KeyDown` 处理 Esc 清除。

### 5.4 视图（`TaskManagerMainView.axaml`）

- 顶部工具栏：标签页切换按钮（`TabBgConverter` 高亮激活页）+ 「⟳ 刷新」+ 「自动刷新(2s)」CheckBox + 状态文本 + 关闭按钮。
- 性能面板（`ScrollViewer`）：CPU 卡片（整机占比大字 + 柱状图 + 每核 ProgressBar 网格）/ 内存卡片（占比 + 柱状图 + 已用/可用/总计）/ 磁盘卡片（每盘 ProgressBar + 已用/总计）/ 网络卡片（每接口 ↓接收速率 / ↑发送速率）/ GPU 卡片（`IsVisible={Binding HasGpu}`，每 GPU 利用率/显存/温度）/ 运行时间。GPU 不可用时显示 `GpuHint` 提示文案。
- 进程面板：工具栏（过滤 TextBox + 清除 + 结束任务）+ 反馈条（`StringNonEmptyVisibilityConverter`）+ 进程列表（表头 + ListBox + DataTemplate 6 列网格）。
- `Border.card` 样式选择器统一卡片外观（`#FAFAFA` 背景 + `#E5E5E5` 边框 + 4px 圆角）。

### 5.5 转换器（`Converters/TaskManagerConverters.cs`）

| 转换器 | 用途 |
|--------|------|
| `BytesConverter` | 字节 → 人类可读（1024 进制，B/KB/MB/GB/TB） |
| `RateConverter` | 字节/秒 → 速率文本（如 "1.5 MB/s"） |
| `PercentTextConverter` | double → "45.0%"；null → "—" |
| `NullableDoubleTextConverter` | double? → 文本（GPU 利用率/温度等可空指标），ConverterParameter 指定格式 |
| `TabVisibilityConverter` | `TaskManagerTab` + ConverterParameter → bool 可见性 |
| `TabBgConverter` | `TaskManagerTab` + ConverterParameter → 激活页背景（白）/ 非激活（透明） |
| `BoolToVisibilityConverter` | bool → 可见性（HasGpu 控制 GPU 区块） |
| `UptimeConverter` | 秒数 → "d天 HH:mm:ss"（不足 1 天则 "HH:mm:ss"） |
| `StringNonEmptyVisibilityConverter` | 非空字符串 → true（结束任务反馈条可见性） |

---

## 6. 数据流

### 6.1 实时刷新流

```text
DispatcherTimer (2s tick) 或 用户点「⟳ 刷新」
    ↓
RefreshAsync (Interlocked 重入保护)
    ├── 并行：GET /api/v1.0/system/metrics  +  GET /api/v1.0/system/processes (JWT)
    │
    ├── 性能页更新
    │     ├── Metrics = {Cpu, Memory, Disks, Networks, Gpus, Uptime}
    │     └── CpuHistory / MemoryHistory 追加采样（上限 60，Clamp 0-100）
    │
    └── 进程页更新
          ├── _allProcesses = procs（按内存降序）
          ├── 保留选中项（按 Id + StartTime 匹配）
          └── ApplyFilter(ProcessFilter) → FilteredProcesses
```

### 6.2 结束任务流

```text
用户选中进程 → 点「结束任务」
    ↓
KillProcessCommand (CanExecute = SelectedProcess != null)
    ↓
DELETE /api/v1.0/system/processes/{id}?force=false (JWT)
    ↓
KillProcessResultDto
    ├── Success=true      → KillFeedback="已结束进程..."，SelectedProcess=null
    ├── RequiresElevation → KillFeedback="权限不足...需在宿主 OS 提升权限"
    └── 其他失败           → KillFeedback="结束进程失败：{Error}"
    ↓
立即 RefreshProcessesAsync（GET processes）刷新列表
```

---

## 7. 关键技术坑

1. **Singleton Provider 持差分状态**：`ISystemMetricsProvider` 必须为 Singleton——CPU% 与网络速率都依赖「上一次采样」作基准，跨请求状态连续性靠单例保证。Scoped/Transient 会丢失基准导致首次采样恒为 0。
2. **CPU% 差分算法**：整机 `usage% = (1 - idle_delta/total_delta) * 100`；进程 `cpu% = cpuDelta / (elapsed * cores) * 100`（相对整机，`Process.TotalProcessorTime` 是所有核累计，需除以核数）。首次调用无前次采样，CPU% 为 0。
3. **Windows 逐核 CPU 当前限制**：`GetSystemTimes` 仅返回聚合 idle/kernel/user，无法直接得每核。当前暂以整机占比填充每核列表（`Enumerable.Repeat`）；逐核需 `NtQuerySystemInformation`，留待后续。
4. **进程属主仅 Linux 解析**：`GetProcessUserName` 基类返回 null；`LinuxMetricsProvider` 解析 `/proc/[pid]/status` 的 `Uid:` 行 + `/etc/passwd` 映射 uid→用户名（`/etc/passwd` 不可读时退化为 uid 数字）。Windows 暂返回 null（`Process.UserName` 在 .NET 10 不可用，需 WMI/P/Invoke，留待后续）。
5. **GPU best-effort + 超时**：`nvidia-smi` 子进程 3 秒超时强制 kill（`proc.WaitForExit(3000)` 失败则 `proc.Kill()`）。非 NVIDIA 或无驱动返回空列表——客户端 `HasGpu` 控制卡片显隐，不可用时显示 `GpuHint` 提示。
6. **重入保护**：`RefreshAsync` 用 `Interlocked.CompareExchange(ref _refreshing, 1, 0)` 防止上一次刷新未完成时本次 tick 堆积请求。`finally` 中 `Interlocked.Exchange(ref _refreshing, 0)` 释放。
7. **DispatcherTimer 停止**：View `Unloaded`（窗口关闭/卸载）时调 `viewModel.Stop()` 停止定时器，避免对已关闭视图继续刷新（`DispatcherTimer` 不会随视图卸载自动停止）。
8. **选中项跨刷新保留**：每次刷新 DTO 是新实例，按 `(Id, StartTime)` 匹配保留选中项（同 Id 但 StartTime 变化 = 进程重启，不视为同一实例）。选中项被过滤掉时清空 `SelectedProcess`。
9. **`IsAutoRefresh` 双向绑定**：CheckBox `IsChecked` 双向绑定驱动；`OnIsAutoRefreshChanged` 直接启停定时器（避免 Command + IsChecked 双触发）。`StartAsync` 仅在 `IsAutoRefresh=true` 时启动定时器。
10. **`Kill(entireProcessTree: false)`**：结束进程仅终止目标进程，不波及子进程（与 Windows 任务管理器「结束任务」一致）。`WaitForExit(3000)` 等待退出，超时不阻塞返回。
11. **网络计数器回绕保护**：`NetworkInterface.GetIPv4Statistics()` 的 `BytesSent`/`BytesReceived` 可能因接口重置而回绕/归零，差分时做下界保护（`sent >= prev ? delta/elapsed : 0`）。
12. **不持久化**：TaskManager 不接入 `RemoteOsDbContext`——指标与进程列表均为实时采样，无 SQLite 表、无增量补齐。Server 重启后下次请求重新开始差分（首次 CPU% 为 0，第二次起正常）。

---

## 8. 后续演进

- **Windows 逐核 CPU**：当前以整机占比填充每核。后续用 `NtQuerySystemInformation(SystemPerformanceInformation)` 获取逐核 idle/kernel/user。
- **Windows 进程属主**：当前返回 null。后续通过 WMI `SELECT * FROM Win32_Process` 或 `QueryFullProcessImageName` + token 查询获取属主用户名。
- **非 NVIDIA GPU**：当前仅 nvidia-smi。后续按平台接入 AMD ROCm SMI / Intel GPU Tools。
- **进程树视图**：当前扁平列表。后续引入 PPID 字段 + 树形展示。
- **历史曲线 / 性能日志**：当前仅 60 采样柱状图（约 2 分钟）。后续引入可滚动曲线 + 服务端可选持久化性能日志。
- **磁盘 IOPS / 速率**：当前仅空间占用。后续接入 `PerformanceCounter`（Windows）/ `/proc/diskstats`（Linux）。
- **服务管理**：当前仅进程列表。后续加 systemd 服务列表（Linux）/ Windows Services 列表。
- **启动项管理**：当前未含。后续按需新增。

---

## 9. AI Agent Rules

> 实现与维护本模块时必须遵守的规则。

1. **真源在 Server 实时采集**：指标与进程列表均由 `ISystemMetricsProvider` 以宿主 OS 进程身份实时读取，**不持久化**（不接入 `RemoteOsDbContext`，无 SQLite 表）。每次请求返回当下快照。禁止为指标/进程新建数据库表。
2. **跨平台抽象**：与 `IIdentityProvider` 同模式——`ISystemMetricsProvider` 接口 + `WindowsMetricsProvider` / `LinuxMetricsProvider` 实现，`Program.cs` 按 `RuntimeInformation.IsOSPlatform` 选择。平台差异（CPU/内存/进程属主）封装在子类，磁盘/网络/GPU/进程列表/结束进程跨平台共享在 `SystemMetricsProviderBase`。
3. **Provider 必须 Singleton**：`ISystemMetricsProvider` 持有相邻采样差分状态（CPU% 与网络速率的基准），必须 Singleton 保证跨请求状态连续性。禁止 Scoped/Transient。
4. **复用 `IAuthSession` JWT**：`ITaskManagerClient` 不持有独立凭据；未登录时 `TaskManagerApp.Activate` 弹提示窗，不崩溃。`TaskManagerClient.RequireSession` 检查 `State == Authenticated`。
5. **不 mutate `HttpClient.BaseAddress`**：每请求用绝对 URI（避免共享 typed HttpClient 实例并发竞态），与 `BrowserClient` / `ExplorerClient` / `SettingsClient` 同模式。
6. **路由常量共用 `SystemMonitorApiRoutes`**：Server 注册路由与 Client 拼接 URL 必须用同一常量，`{id}` 用 `processId.ToString(InvariantCulture)` 替换，禁止硬编码字符串。
7. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），JSON 用 `RemoteOsJsonOptions.Default`。
8. **结束进程不自动提权**：权限不足（Win32 错误码 5/1/13）返回 `RequiresElevation=true`，提示用户在宿主 OS 提权（`sudo kill` / UAC 运行）。RemoteOS 不存储宿主密码、不自动提权（硬约束「权限提升委托宿主 OS」）。`Kill(entireProcessTree: false)` 仅终止目标进程。
9. **错误统一 RFC 7807**：Server `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`；Client `TaskManagerClient` 解析 `ProblemDetails` 抛 `RemoteOsAuthException`，VM catch 后写 `StatusText`。
10. **DispatcherTimer 生命周期**：View `Unloaded` 时必须调 `viewModel.Stop()` 停止定时器。`RefreshAsync` 用 `Interlocked` 重入保护防止请求堆积。
11. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi / SQLitePCLRaw.lib.e_sqlite3 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
