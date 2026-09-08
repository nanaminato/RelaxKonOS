# RelaxKonOS Network Inspector（设计）

> 状态：已实现（V1）
> 目标：提供一个由 RelaxKonOS Shell 直接拥有的系统调试窗口，用很低的常驻成本查看 **RelaxKonOS 客户端到 RelaxKonOS Server** 的 REST API 与 SignalR 调用。它参考 Chrome DevTools 的 Network 面板，但不试图成为通用抓包器。

相关文档：[Developer Mode](../development/RelaxKonOS.DeveloperMode.md)、[Localization](../desktop/RelaxKonOS.Localization.md)、[Settings](../desktop/RelaxKonOS.Settings.md)、[Protocol](../architecture/RelaxKonOS.Protocol.md)。

---

## 1. 决策摘要

网络调试器采用“**系统窗口、宿主采集**”的单一信任边界：

- `NetworkInspectorWindowService` 是 RelaxKonOS Shell 的系统服务，直接通过 `IWindowManager` 创建 `relaxkonos.system.network-inspector` 窗口；它不是内置应用，也不是可安装的 `.roapp`。
- 采集器 `NetworkDiagnosticsService` 是客户端宿主的 singleton。它负责内存上限、脱敏、分类和事件订阅；检查器窗口直接读取其已脱敏事件流。
- V1 只承诺监视通过 RelaxKonOS 宿主通信栈发起的调用：已注册 typed `HttpClient` 的 REST 请求、以及明确接入包装器的 SignalR Hub 生命周期和协商请求。
- 默认不录制；仅当 **Developer Mode 已开启、用户开始录制** 时采集。关闭录制、关闭开发者模式或登出都会立即清空内存中的记录。

这个边界很重要：嵌入 Browser 的网页流量、任意第三方包自行创建的 `HttpClient`、宿主 OS 的其他进程流量都不属于“所有 API 调用”，也不能在不引入代理/VPN/证书拦截的前提下可靠捕获。V1 不做这些高权限且高风险能力。

---

## 2. 用户入口与范围

### 2.1 打开方式

| 入口 | 行为 |
| --- | --- |
| **Settings → Developer → Network Inspector** | 显示状态与“打开检查器”。开发者模式关闭时，说明原因并禁用操作。 |
| **Ctrl+Shift+I** | 在 `MainWindow` 的隧道路由键盘事件中处理；不依赖当前子窗口是否有焦点。满足前置条件时启动或聚焦检查器；否则显示一次本地化提示。 |

首版固定 `Ctrl+Shift+I`，与 Chrome 的习惯一致；在浏览器 WebView 捕获该快捷键的情况下，宿主的隧道路由优先。V2 再考虑可配置快捷键。`F12` 暂不占用，避免与宿主、WebView 和平台调试器冲突。

### 2.2 明确包含与排除

| 项目 | V1 |
| --- | --- |
| RelaxKonOS REST：登录、设置、文件、浏览器数据、任务管理、能力令牌等 | 包含，只要 client 注册到受监视管线 |
| SignalR：终端 Hub 的连接、协商、调用结果和连接状态 | 包含，见第 6 节 |
| 下载、上传、媒体播放 | 只保留摘要，不保存正文 |
| REST 请求/响应 JSON | 可选小型预览，严格脱敏与截断 |
| WebView 中的网站流量、外部包任意出站流量、服务器侧内部 HTTP | 排除 |
| SignalR 消息帧、终端输入/输出、WebSocket payload | 排除 |

“所有”指所有纳入该受控通信边界的 RelaxKonOS API 调用，而不是整个设备的网络流量。

---

## 3. 交互与信息架构

检查器是独立可缩放窗口，建议初始尺寸 `1180 × 720`。界面只做开发调试需要的最小集合：

```text
┌ Network Inspector ─ Record ●  Clear  Preserve log  Filter ─────────────┐
│ All  Fetch/XHR  Media  SignalR  Errors       42 requests · 3 failed     │
├────┬────────┬────────┬──────────────┬──────┬────────┬─────────┬────────┤
│ #  │ Time   │ Source │ Method / Name│ Type │ Status │ Duration│ Size   │
├────┴────────┴────────┴──────────────┴──────┴────────┴─────────┴────────┤
│ Request list (virtualized; one row per completed REST request or Hub event) │
├──────────────────────────────────────────────────────────────────────────┤
│ Details: Overview | Request | Response | Timeline                         │
│ method, sanitized URL, status/error, timings, allowed headers, preview     │
└──────────────────────────────────────────────────────────────────────────┘
```

- 筛选支持文本、`status:>=400`、`type:media`、`source:terminal`、`signalr`；V1 不做 HAR 导入/导出、瀑布图、请求重放、请求阻断或编辑。
- 颜色只作为辅助：2xx/3xx 成功、4xx/5xx 失败、取消、传输异常；同时显示文本状态，保证主题与无障碍可用。
- 选择一条媒体记录时，Details 只显示结果摘要与安全 Header，不显示 Request/Response 正文 Tab。
- “Preserve log”默认关闭；开启后仅允许跨检查器窗口存活，仍会在登出、关开发者模式或达到环形缓冲区上限时清空/淘汰。

---

## 4. 数据模型、内存与隐私

### 4.1 宿主内部契约

`RelaxKonOS.Client.Services.Diagnostics` 中的内部契约由系统窗口直接使用：

```csharp
public sealed class NetworkDiagnosticsService
{
    NetworkDiagnosticsState State { get; }
    event EventHandler<NetworkDiagnosticsState>? StateChanged;
    NetworkDiagnosticsSnapshot GetSnapshot(NetworkDiagnosticsQuery? query = null);
    event EventHandler<NetworkDiagnosticEntry>? EntryCompleted;
    Task<NetworkDiagnosticsCommandResult> StartRecordingAsync(CancellationToken ct = default);
    Task StopRecordingAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
```

每个 `NetworkDiagnosticEntry` 为已完成的不可变摘要，至少包含：`Id`、`StartedAt`、`Duration`、`Kind`（`Http` / `SignalR`）、`Source`、`Name`、`Method`、已净化的 `PathAndQuery`、`Outcome`、可空 `StatusCode`、`ContentType`、`DeclaredContentLength`、`IsMedia`、`ErrorKind` 和受限的 Header/预览字段。`Source` 由调用方显式给出（如 `auth`、`explorer`、`terminal`、`settings`），不能从未受信任的 URL 推断。

### 4.2 上限与采样

- 环形缓冲区固定为 **500 条记录或 4 MiB 估算负载**，任一上限先到即淘汰最旧条目；不写盘、不上传、不跨进程共享。
- 非媒体 HTTP 的请求、响应预览各至多 **8 KiB UTF-8**；只允许 `application/json`、`text/*`、`application/problem+json`，无法安全解码时不预览。
- Header 最多各 32 项、单项值最多 512 字符；`Authorization`、`Cookie`、`Set-Cookie`、`X-RelaxKonOS-Dev-Token`、含 `token` / `secret` / `password` / `key` 名称的 Header 与 JSON 字段均替换为 `[redacted]`。URL 中同名 query 参数也脱敏。
- 媒体/二进制/流式响应永不读取或缓冲 body；只记录方法、净化路径、成功/失败、HTTP 状态、耗时、Content-Type 和声明的 Content-Length。未知长度显示 `—`，不为测量它而读取流。
- 默认 `BodyPreviewMode = Off`；检查器中可在当前会话临时改为“文本 API 预览”。它不是 Workspace 偏好，也不随下次启动保留。

取消与网络异常同样要落为一条事件：`Outcome = Cancelled` 或 `TransportError`，`StatusCode = null`。不能把异常文本原样保存；仅保留异常类别（例如 `Timeout`、`Connection`、`Authentication`）和经长度限制的安全说明。

### 4.3 安全生命周期

开始录制需要同时满足：Developer Mode 开启、调用包已安装、拥有本地用户批准的权限、当前仍在认证会话。服务主动订阅 `DeveloperModeService.Changed` 与 `IAuthSession.StateChanged`；任一前置条件不满足即停止、清空并通知订阅者。这样已打开的外部窗口不能在权限撤销后继续读取旧的敏感记录。

---

## 5. REST 采集设计

`NetworkDiagnosticsService` 不直接依赖具体业务 client。新增 `NetworkDiagnosticsHandler : DelegatingHandler`，以 `Stopwatch.GetTimestamp()` 记录请求开始，在以下位置生成摘要：

1. 调用前创建轻量临时上下文（方法、分类、净化 URL、开始时间）；绝不复制 `HttpContent`。
2. `base.SendAsync` 成功返回后，从 response header 读取状态、内容类型和长度，并按 MIME/type 分类为 media/binary/text。
3. 若是允许预览的小型文本内容，**仅在业务 client 本来就将内容缓冲/反序列化时**由显式 helper 提供预览；handler 本身不得提前读取 response body，否则会破坏流式下载。
4. 异常、超时与取消在 catch/finally 中写入完成事件，再按原语义重新抛出。

在 `Bootstrapper` 中将 handler 加到每个已有 `AddHttpClient` 注册，并保持 `AcceptLanguageHandler` 的现有行为。推荐顺序：

```text
NetworkDiagnosticsHandler（计时、结果）
  → AcceptLanguageHandler（添加 Accept-Language）
    → HttpClient transport
```

每个 typed client 注册额外指定固定 `source`（可用命名 handler 或小型 source handler）：`auth`、`terminal-settings`、`explorer`、`browser`、`task-manager`、`settings`、`window-layout`、`capabilities`。以后新增 client 的 code review 检查项是：要么进入该管线，要么在代码注释中说明为何不属于 RelaxKonOS API。

采集器只处理目标为当前 `IAuthSession.ServerUrl` 的 RelaxKonOS API 与本地 Developer Bridge；不把诸如服务器网页转发、第三方站点或 `HttpClient` 的任意域名误记为 RelaxKonOS API。Developer Bridge 可显示来源 `developer-bridge`，但其配对 Header 必须无条件隐藏。

---

## 6. SignalR：简化但有诊断价值的实现

SignalR 不能像普通 HTTP 一样以“每个数据包”为单位记录，也不应记录终端输入/输出。V1 的单位是 **Hub 生命周期事件 + Hub 调用结果**：

| 事件 | 记录内容 | 不记录 |
| --- | --- | --- |
| Build / Connect started | Hub 名称、净化路径、连接序号、开始时间 | access token、协商 body |
| `/negotiate` HTTP | 状态、耗时、错误类别 | negotiate response 和 connection token |
| Connected / Closed | 结果、耗时、关闭类别 | WebSocket payload |
| Invoke（如 `Start`、`ListSessions`、`Input`、`Resize`、`Close`） | 方法名、开始/完成、耗时、成功/失败 | 参数与返回值；尤其不记录终端字节 |
| Reconnecting / Reconnected（未来使用自动重连时） | 连接序号、重试次数、间隔、最终结果 | token、帧和服务端缓冲 |

通过受控的 `TerminalHubConnection.Build` 接入 SignalR。它在 `WithUrl` 中通过 `HttpMessageHandlerFactory` 包装协商 HTTP handler，并订阅 `Closed`；`StartAsync`、`StopAsync` 和会话列表调用由小型包装方法上报。

当前 `TerminalHubConnection` **明确没有启用 `WithAutomaticReconnect`**：重连后服务端不会自动重新附着 PTY，会导致半附着状态。因此 V1 仅显示“断开后需要重新打开终端”的状态，不改变现有恢复语义，也不为了显示重连而启用自动重连。未来若业务实现“重新连接后重新 Attach + 服务端回放”这一完整协议，才启用自动重连并记录重连时间线。

实际 WebSocket upgrade 的底层细节在 SignalR transport 内部并不总能从公共 API 获得；V1 不伪造“已升级/已降级”的结论。界面显示：`Negotiation HTTP result`、`Hub Connected`、以及（可获知时）配置的 transport policy。若 `HttpMessageHandlerFactory` 观察到协商/long-poll HTTP，可显示真实 HTTP 状态；否则以 Hub 状态为准。这样能调试连接故障，同时避免依赖库内部实现。

---

## 7. 系统集成与国际化

### 7.1 系统窗口

Settings 的 Developer 页只负责“是否允许开启/打开”，不向 Workspace 同步该状态；Developer Mode、录制状态和日志全是设备本地状态。系统窗口不出现在应用列表、不需要安装、也不向外部应用 SDK 暴露该能力。

### 7.2 本地化契约

- 设置页新增 `settings.network_inspector.*` 键；系统窗口新增 `network_inspector.*` 键到 client 的 `en-US`、`zh-CN`、`ja-JP`，英语 fallback 始终存在。
- `NetworkInspectorWindowService` 订阅 `LocalizationService.LanguageChanged`，刷新窗口标题和全部系统 UI 文本。
- 网络条目的协议字段使用稳定 enum/代码（`TransportError`、`Media`、`SignalR`），由检查器 UI 映射成本地化标签；不要把已经本地化的字符串存入记录，以便切换语言后历史列表也能即时重绘。

---

## 8. 实施顺序与验证

1. **宿主最小采集器**：实现不可变 entry、环形缓冲、净化器、生命周期清除和单元测试（Header/JSON/query 脱敏、500/4 MiB 淘汰、取消/异常）。此阶段先不公开 SDK。
2. **REST 接入**：加入 handler 并覆盖所有当前 typed clients。验证正常 JSON、401/500、下载、上传、媒体租约和大文件均不读取/保存 body。
3. **SignalR 接入**：仅改终端连接构造路径，验证 `/negotiate`、连接、`Start`、`Input`、断开和失败连接。确认不启用自动重连，终端恢复行为不变。
4. **系统窗口与入口**：实现 `NetworkInspectorWindowService`、设置 Developer 页入口、`Ctrl+Shift+I` 宿主路由与三语文案。

验收标准：

- 未开始录制时，无记录分配且普通 API 行为/响应流不变。
- 一次 JSON API 调用显示方法、已脱敏路径、状态、耗时及（仅在启用时）不超过 8 KiB 的预览。
- 一次媒体下载/播放只显示摘要；检查断点证明正文没有被监视器读取。
- 终端连接失败能看到协商/连接失败，终端数据、命令和 token 永不出现。
- 关闭开发者模式或登出后，检查器立即变为空状态且之后不再收到 entry。
- en-US、zh-CN、ja-JP 下设置入口、权限说明、过滤项和详情状态均可切换，不依赖重启。

---

## 9. 后续方向（非 V1）

在完整的“自动重连 + 显式重新 Attach + 回放确认”协议落地后，再增加 SignalR reconnect timeline；在有明确隐私方案后，再讨论按需、受权限保护的 HAR 导出。请求重放、编辑、阻断、设备全局代理、TLS 解密和 WebView/任意第三方流量捕获不在本设计路线内。
