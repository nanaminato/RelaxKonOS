# RelaxKonOS FRP 内网穿透集成（Goal 执行版）

> 状态：待实施  
> 建立日期：2026-08-25  
> 适用范围：`.NET 10` Server、Avalonia Client、Windows / Windows Server / Linux  
> 架构依据：[`RelaxKonOS.FRP_Integration.Design.md`](./RelaxKonOS.FRP_Integration.Design.md)

本文是后续 Goal 模式的执行基线。它把 FRP 集成设计拆分为可独立构建、验证和回滚的目标；设计文档仍是架构、安全原则和产品取舍的权威来源。实现开始前必须重新检查 Solution、现有权限模型、FRP CLI 兼容性和官方发布物的校验材料；本文的范围、边界、安全规则和验收标准是约束性要求。

## 1. 目标与交付边界

RelaxKonOS 要提供一个可管理的 FRP 隧道能力，但 FRP 始终是独立 Runtime：`frpc` / `frps` 由 RelaxKonOS 下载、生成配置和监管，实际流量只在 FRP 进程之间转发。RelaxKonOS Server、认证、局域网访问和修复路径不得依赖 FRP 存活。

首个可发布闭环（V1）必须同时具备：

- 可保存、校验和审计的隧道 Desired State；数据库是事实来源，生成的 TOML 不是。
- 面向 Provider 的隧道模型，FRP 只是第一个 `ITunnelProvider` 实现。
- `frpc` 的 Managed Runtime：已验证的官方二进制、版本目录、启动/停止、状态、日志、升级和回滚。
- External Runtime 的只读检测与显式受管启动模式；绝不接管用户未授权的配置或进程。
- 连接 RelaxKonOS 管理、自建或第三方兼容 `frps` 的服务器配置；首个版本支持 `tcp`、`udp`、`http`、`https` 隧道。
- 秘密信息不通过普通 DTO、日志、生成配置下载接口或客户端回显泄露。
- Avalonia 内置“内网穿透”单窗口应用，拥有概览、隧道、服务器、Runtime/日志和安全状态页面。

V1 明确不包括：

- 嵌入或改写 FRP 协议、把 `frpc` / `frps` 链入主进程，或让 RelaxKonOS 参与数据转发。
- STCP、XTCP、P2P visitor、插件、用户自定义 TOML 片段、任意环境变量或任意 CLI 参数透传。这些均需要独立的秘密、网络暴露和输入模型后再设计。
- 自动把 RelaxKonOS Backend 暴露到公网；该能力应在专门的远程访问 / TLS / 身份审查 Goal 中以显式、可撤销的系统隧道实现。
- 自动安装或强制启用 `frps`；`frps` 是后续可选目标，不能阻塞 `frpc` 主路径。
- 关闭 Defender、加壳、篡改 FRP 二进制、加密落盘、内存执行、静默添加排除项或绕过组织策略。
- Cloudflare Tunnel、Tailscale 等其他 Provider 的实现；V1 只提供它们所需的抽象边界。

## 2. 当前代码基线与落点

当前 Solution 还没有 Tunnel、FRP、通用 Runtime Manager 或 SecretStore 的生产实现。实施应复用已有产品边界，而不是建立第二套通信、存储或进程调用习惯：

| 关注点 | 既有模式 / 首选落点 |
|---|---|
| 线协议 | `Shared/RelaxKonOS.Protocol`：DTO、路由常量和 JSON 名称；该项目保持零 PackageReference。 |
| Server API | `RelaxKonOS.Server/Endpoints`：按模块 `Map*Endpoints`，JWT `RequireAuthorization()`，并在 `Program.cs` 注册。 |
| 持久化 | `RelaxKonOS.Server/Domain` + `Storage` + `Storage/Sqlite/RelaxKonOSDbContext.cs`；Runtime 状态和短期日志不得伪装成 Workspace 偏好。 |
| 受管二进制与进程 | 参考 `DockerRuntimeInstaller`、`NginxManagedOptions` 和 Web Server 操作存储；使用 `ProcessStartInfo.ArgumentList`，禁止 shell 拼接。 |
| Client 应用 | `Client/RelaxKonOS.Client/Apps/<App>`、`IRemote*Client`、Bootstrapper 注册、`ApplicationManifest` 和现有 Avalonia / 本地化模式。 |
| App 权限 | 在 `Framework/RelaxKonOS.Core/Applications/AppPermissions.cs` 增加读/管理权限，并在 Manifest 和 UI 使用；注意这只是本地应用授权，不能替代 Server 端的操作授权。 |

建议的新增模块为：

```text
Shared/RelaxKonOS.Protocol/Tunnels/
RelaxKonOS.Server/Tunnels/
RelaxKonOS.Server/Runtimes/
RelaxKonOS.Server/Secrets/
RelaxKonOS.Server/Antivirus/
RelaxKonOS.Server/Endpoints/TunnelEndpoints.cs
Client/RelaxKonOS.Client/Apps/Tunnels/
```

目录可以随现有命名微调，但不能把 FRP 业务散入 `Program.cs`、Endpoint 或 Client ViewModel。`Runtimes` 管理版本、完整性、进程和安全状态；`Tunnels` 管理 Desired State、Provider 路由与 FRP TOML；`Secrets` 管理秘密材料；四者相互通过窄接口交互。

## 3. 必须冻结的架构契约

### 3.1 Provider 与 Runtime 分离

业务层只依赖隧道 Provider。建议至少建立以下语义，并让所有异步操作携带 `CancellationToken`：

```csharp
public interface ITunnelProvider
{
    string ProviderId { get; }
    Task<TunnelProviderStatus> GetStatusAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TunnelInfo>> ListAsync(CancellationToken cancellationToken);
    Task<TunnelOperationResult> ApplyAsync(TunnelDefinition definition, CancellationToken cancellationToken);
    Task<TunnelOperationResult> DeleteAsync(Guid tunnelId, CancellationToken cancellationToken);
}
```

`FrpTunnelProvider` 只负责将已验证的 Desired State 转换为 FRP 配置、请求 Runtime supervisor 重载和解析可公开的状态。它不得下载二进制、直接修改 Defender、拥有 Server 密码或把 FRP 特有字段泄漏到通用 Tunnel UI。

Runtime 以稳定的 `RuntimeId = "frp"` 标识，并将“安装 / 已安装版本 / active 版本 / previous 版本 / 完整性 / 防病毒状态 / 进程状态”建模为独立资源。升级切换必须是版本目录的指针变更，而不是覆盖现有二进制。

### 3.2 Desired State、生成物与原子应用

持久化对象至少包含：

```text
FrpServerProfile
├── Id, Name, Host, Port
├── AuthKind, TlsMode, TransportOptions（非秘密部分）
├── SecretReferenceIds（只在 Server 内部使用）
└── CreatedAt, UpdatedAt

TunnelDefinition
├── Id, Name, ProviderId="frp", ServerProfileId
├── Protocol, LocalHost, LocalPort
├── RemotePort / Domain、Enabled
├── Encryption, Compression
└── CreatedAt, UpdatedAt, Revision
```

对同一 `frpc` 实例的配置变更必须串行化，流程固定为：

```text
API 输入 → 领域校验 → 保存 Desired State → 生成临时 TOML
       → FRP CLI 验证 → 原子替换受管配置 → reload 或受控 restart
       → 读取实际状态 / 记录审计结果
```

验证或启动失败时保留旧的 active 配置和进程，返回稳定的问题代码；不得留下半写入文件，也不得把客户端输入写入任何可由其他用户访问的路径。启动失败后的数据库状态须明确表达“已保存但未应用”，不能把它伪装为已运行。

每个 FRP Server Profile 在 V1 对应一个隔离的 `frpc` 配置和受管进程实例，避免不同服务器的认证、重启和故障互相影响。配置和日志属于主机级运行数据，不作为 Workspace 同步偏好；业务记录须明确宿主机归属和当前身份的可见范围。

### 3.3 Secret 与 API 边界

Token、OIDC client secret、TLS 私钥、STCP secret 和任何等价凭据必须保留在 Server 的 SecretStore 中。普通表只保存不可猜测的引用和 `Configured` 状态；列表与普通展示 DTO 使用 `tokenConfigured: true` 等布尔状态，绝不返回原文、掩码可逆值或可下载的 FRP TOML。已授权的 Controller 可通过专用编辑读取流程回显其 Profile 或托管 FRPS Token；该读取必须逐用户授权并写入审计记录。

第一个 Goal 必须先实现一个受保护的 SecretStore 抽象及当前平台实现，再允许保存包含认证的 Profile。生产实现使用服务器受保护的持久化机制（例如 Data Protection 保护的受限文件或表字段）；密钥来源、轮换、删除、备份恢复和访问失败都必须有明确行为。不得把秘密放进 `appsettings.json`、Workspace JSON、日志、异常 `Detail`、诊断 ZIP 或 Client 本地设置。

所有 Endpoint 仍须 Server 端鉴权和逐操作授权。应用 Manifest 权限只决定该 Client 应用能否发起 UI 操作，不能成为 HTTP 的信任依据。删除隧道、写入秘密、安装/更新 Runtime、启用 Defender compatibility 和变更 `frps` 配置都属于高风险操作，必须分别审计请求者、动作、目标、结果和稳定 problem code，且日志不得记录秘密、完整 TOML 或命令行敏感参数。

### 3.4 受管 Runtime 的信任链

Managed Runtime 只能安装明确版本、平台和架构的官方发布物。安装清单必须在下载前包含固定的版本、URL、目标 RID/CPU 架构、预期 SHA-256、来源和取得时间；校验值应来自经过验证的官方发布校验材料，不能把下载到的二进制自身当作信任来源。

安装顺序为：下载到私有临时目录 → 限制大小和解压条目 → SHA-256 强制校验 → 检查归档中仅有预期 `frpc` / `frps` 文件 → 安装到新的版本目录 → 以受限参数执行版本/配置检查 → 标记为可激活。任何一步失败均不得启动该版本；临时文件清理由受控恢复逻辑处理。保留 active 与 previous 版本，只有新版本成功运行并通过健康检查后才改变 active 指针；失败立即回到 previous，且不删除可工作的版本。

External Runtime 绝不下载、升级、删除或修改给定可执行文件。V1 先支持路径存在性、版本和能力检测、只读状态；随后才支持用户明确选择的“RelaxKonOS 生成配置并启动此路径”。External 模式的受管配置必须在 RelaxKonOS 自己的数据目录中，停止操作只能终止由 RelaxKonOS 保存且可验证身份的子进程，不能按名称扫描或杀死系统中其他 `frpc`。

### 3.5 Windows 防病毒与宿主权限

标准模式不修改 Defender。若检测到安装后文件被隔离或删除，应报告可操作的状态（含可公开的检测名称/系统错误），保留安装失败记录，并提供重新尝试或查看管理员指引。

Defender compatibility 只能作为后续独立 Goal：Windows 专用、默认关闭、逐次明确确认、显示精确排除目标、实际读取回显验证、可撤销并写审计。优先文件级、其次具体版本目录，禁止排除 `C:\ProgramData\RelaxKonOS`、RelaxKonOS 数据根目录或整个磁盘；不得使用 Process Exclusion 替代文件/目录排除。企业策略、Tamper Protection、GPO、Intune 或 Defender for Endpoint 拒绝操作时，必须如实返回拒绝，不尝试绕过。

RelaxKonOS 不自行请求 UAC、`sudo`、宿主密码或管理员凭据。需要特权的下载目录、服务注册、端口绑定或 Defender 设置必须采用已批准的宿主操作路径，且 HTTP 客户端不能提交任意待执行命令。所有外部进程参数必须由结构化模型映射到 `ArgumentList`；无 shell、无 `cmd.exe` / `sh -c`、无用户提供的可执行文件参数拼接。

## 4. Goal 执行计划

每个 Goal 必须保持 `dotnet build RelaxKonOS.sln -c Debug` 可通过，包含对应测试，并在前一 Goal 的验收全部通过后再进入下一项。若实际 FRP CLI 契约、现有权限架构或宿主特权模型与本文假设冲突，应先更新本文件和架构设计，经审查后再继续；不能在代码中静默改变边界。

### Goal 0：基线、威胁模型与发布矩阵

**工作**：确认目标 OS/CPU RID、FRP 支持版本范围、官方发布物及 SHA-256 校验材料的获取/缓存策略；记录 Windows/Linux 受管目录、日志保留上限、配置权限和运行账户。建立 V1 问题代码、审计事件、权限矩阵和不支持能力的返回约定。检查当前 AppPermissions 仅为 Client 本地能力的事实，并选定 Server 端逐操作授权方案。

**验收**：发布矩阵不使用“下载最新版本”或未固定的 URL；威胁模型覆盖恶意归档、校验失败、路径遍历、配置注入、秘密泄露、子进程 PID 复用、日志泄露、Defender 拒绝和组织策略；任何未决的宿主提权需求均已明确为阻塞项或后续范围。

### Goal 1：协议、权限和无秘密领域模型

**工作**：在 `Shared/RelaxKonOS.Protocol/Tunnels` 建立稳定 JSON DTO、路由常量和 problem-code 约定；定义 Provider 状态、Server Profile（无秘密视图）、Tunnel Definition、Runtime 状态、操作结果和日志元数据。新增 `AppPermissions.ServerTunnelsRead` / `ServerTunnelsManage`，再创建 `relaxkonos.tunnels` 单窗口 Manifest、空状态和最小 Client Proxy 骨架。注册 Endpoint 映射，但尚不执行 FRP。

**验收**：Protocol 保持零 PackageReference；Client / Server 不硬编码路由；任何 DTO、API 响应或序列化测试都不包含秘密字段；无权限 UI 不暴露管理操作；Server API 的授权策略不是从客户端传来的 app id 推断。

### Goal 2：持久化、SecretStore 与输入校验

**工作**：实现 Tunnel / Profile / Runtime 元数据仓储及 SQLite 映射、并发 revision 和用户/主机归属校验；实现受保护的 SecretStore 和秘密引用生命周期。实现端口、主机、域名、隧道名、协议组合、重复 remote port / domain、TLS / auth 组合和数量上限校验；提供 Profile 与 Tunnel 的 CRUD Endpoint。

**验收**：普通 GET 永远只能看到 `*Configured` 状态；创建、更新、删除和并发写入不产生孤儿秘密；删除前已被引用的 Profile 返回明确冲突或显式级联确认；无效配置在落库前拒绝；仓储和 Endpoint 测试覆盖租户隔离、revision 冲突与秘密不泄露。

### Goal 3：通用 Runtime 基础与 External 只读检测

**工作**：实现 `IRuntimeManager`、版本目录布局、Runtime 状态持久化、进程身份记录、受限日志读取和 `IRuntimeSupervisor`。先实现 External `frpc` 的绝对路径规范化、文件存在性、版本/能力检测与只读状态；进程调用统一走固定可执行文件和 `ArgumentList`。

**验收**：不存在、非文件、目录、相对路径、超长路径、不可执行文件和超时均有稳定结果；External 检测不会写入、删除或启动用户的 FRP；日志不含配置秘密；停止逻辑只能操作带有已验证进程标识和启动时间的 RelaxKonOS 子进程。

### Goal 4：FRP Desired State、TOML 生成与 `frpc` 生命周期

**工作**：实现 `FrpTunnelProvider`、受控 TOML generator、临时配置校验、原子替换、按 Profile 隔离的 supervisor 和可公开状态解析。完成 Managed Configuration 模式和 External 的显式受管启动模式；实现 `tcp`、`udp`、`http`、`https`，并为未实现协议返回 `tunnel.protocol_unsupported`。启动、停止、重启、重连和状态探测必须经过同一个串行化应用管道。

**验收**：保存一个 Tunnel 不会立即假称连接成功；坏 TOML / `frpc` verify 失败 / restart 失败都保留旧 active 配置与进程；多个 Profile 的更新互不影响；生成器的快照测试覆盖特殊字符、IPv4/IPv6、端口边界、域名、TLS 和无秘密输出；任何用户字符串都无法成为额外 CLI 参数或 TOML 键。

### Goal 5：Managed Runtime 安装、升级和回滚

**工作**：实现受限下载、归档检查、SHA-256 验证、安装清单、版本目录、active / previous 指针、健康检查、升级与回滚。将 Runtime 安全状态暴露为安全的只读 DTO；安装和更新需要显式确认与 Server 端高风险授权。实现失败恢复和过期版本的保留/手动清理策略。

**验收**：校验失败、归档路径遍历、缺失二进制、错误平台/架构、下载超限、被锁定文件、启动超时和健康检查失败均不能激活版本；升级失败后 previous 版本仍可工作；不会覆盖 active Runtime；测试使用本地 fixture / 可替换 HTTP handler，不依赖网络或真实 FRP 下载。

### Goal 6：Avalonia 管理应用与可观测性

**工作**：完成概览、隧道、FRP 服务器、Runtime、日志和设置页面；管理操作使用现有对话框、加载/失败/取消模式，并添加 `en-US`、`zh-CN`、`ja-JP` 文案。显示“已保存未应用 / 启动中 / 已连接 / 已断开 / 未知 / Runtime 不可用”等真实状态，提供最少必要的日志尾部和审计摘要。

**验收**：无 Runtime、External 路径失效、FRP 进程崩溃、网络断连、服务器认证失败、配置未应用和权限被拒都不会显示伪造绿色或 0 值；普通 UI 不显示 Token、私钥、完整配置或敏感命令行；关闭窗口取消 Client 请求/轮询而不终止 Server Runtime；单窗口策略和重新登录流程正常。

### Goal 7：可选 `frps`、RelaxKonOS 远程访问与 Defender compatibility（分别评审）

**工作**：本 Goal 只在 V1 `frpc` 闭环稳定后拆成三个独立、可发布子目标：`frps` 管理、RelaxKonOS 自身远程访问、Windows Defender compatibility。`frps` 需要独立 bind / allow-port / TLS / dashboard / 认证模型和宿主端口冲突检测；RelaxKonOS 远程访问需要单独的公网暴露、证书和身份威胁审查；Defender compatibility 必须严格遵循 §3.5。

**验收**：任一子目标均不能改变“RelaxKonOS Backend 不依赖 FRP”的可用性；默认没有公网 Dashboard、宽泛端口范围或 Defender 排除；失败与撤销都有审计且能恢复安全默认值。未经这些独立验收，不得把它们标记为 V1 已完成。

### Goal 8：端到端验证、运维文档与发布收尾

**工作**：在 Linux、Windows 和 Windows Server 执行 Managed / External、第三方 `frps`、多 Profile、故障回滚、Client 重连和权限测试；以本地测试 `frps` 或受控集成环境验证 TCP、UDP、HTTP、HTTPS。更新本设计、主文档索引、部署/运维指南和本地化；发布前复查 Defender 和 SecretStore 规则。

**验收**：构建为 0 错误，新增测试稳定；安装/升级/回滚的审计可定位且不含秘密；停止 `frpc` 后 RelaxKonOS 局域网 API 和登录仍可使用；升级/回滚和进程崩溃不会遗留重复转发进程；文档能让管理员在不关闭安全软件的前提下诊断问题。

## 5. 测试、观测与验收口径

### 5.1 自动化测试

- **纯函数 / 单元测试**：地址、端口、域名、协议组合、TOML escaping、秘密掩码、SHA-256、归档条目过滤、版本选择、状态迁移、PID 启动时间匹配和日志截断。
- **仓储 / Endpoint 测试**：鉴权、逐操作授权、用户隔离、revision 冲突、Profile 删除约束、秘密引用、problem code 和 DTO 无秘密序列化。
- **进程适配器测试**：替换进程工厂与时间源，覆盖成功、非零退出、超时、取消、崩溃、PID 复用、配置验证失败和原子替换回滚；测试不得调用 shell 或依赖本机已安装 FRP。
- **Runtime 安装测试**：本地归档 fixture 覆盖错误 SHA-256、条目穿越、压缩炸弹限制、缺少预期二进制、错误架构和升级失败回滚。
- **集成测试**：使用受控 `frps` 验证四种 V1 协议；多 Profile 隔离；Client API 断线重试不会触发重复启动或重复配置应用。

### 5.2 手工验证矩阵

| 场景 | 需要确认的结果 |
|---|---|
| Linux Managed Runtime | 下载、校验、运行、停止、日志、升级和回滚均不需 Client 提供 shell 命令。 |
| Windows / Windows Server 标准模式 | Defender 未修改；被拦截时显示明确可恢复状态，RelaxKonOS 继续可用。 |
| Windows compatibility（若实施） | 用户明确确认后仅添加最小范围排除；策略拒绝时不绕过，撤销后恢复原状态。 |
| External Runtime | 只读检测不接管既有进程；受管启动只影响 RelaxKonOS 启动的实例。 |
| 第三方 `frps` | Token/TLS 配置可连接；失败不泄露认证材料。 |
| 故障与恢复 | 杀死 `frpc`、断开网络、无效配置、升级失败后，状态真实且 RelaxKonOS API / 登录不受影响。 |

### 5.3 发布级完成定义

FRP 集成只有同时满足以下条件才可标为完成：

1. 所有 V1 Goal 的验收均通过，`frps` 与 Defender compatibility 未实施时明确显示为可选后续能力。
2. RelaxKonOS 不实现 FRP 协议、不转发流量，`frpc` / `frps` 不以库或主进程内组件运行。
3. Managed Runtime 的每一次激活都经过强制 SHA-256 校验；升级不会覆盖工作版本，失败可回滚。
4. 数据库是唯一 Desired State；配置写入经过校验和原子替换，错误不会破坏最后一个可工作的配置。
5. 所有秘密均不出现在 API、Client 状态、日志、错误、审计、配置下载或常规备份明文中。
6. Client 权限、Server 授权和高风险审计均已落实；未授权用户不能读取或改变隧道、Runtime 或秘密状态。
7. Windows 默认不改变 Defender；不存在宽泛排除、静默排除、关闭防护或规避检测的实现。
8. Linux、Windows 和 Windows Server 的成功与失败路径均已验证，且 `frpc` 故障不影响 RelaxKonOS 自身启动、登录或局域网管理。

## 6. 后续 Goal 提示

后续在 Goal 模式中应使用以下目标，并把本文和原始设计一并作为约束：

> 依据 `docs/applications/RelaxKonOS.FRP_Integration.Goal.md` 与 `docs/applications/RelaxKonOS.FRP_Integration.Design.md` 实现 RelaxKonOS 的 FRP 内网穿透。严格按 Goal 0–8 顺序推进：先冻结信任链、协议、权限、Desired State 和 SecretStore，再实现 External 检测、FRP 配置/生命周期、Managed Runtime、Avalonia 应用和跨平台验证。`frpc` / `frps` 必须保持独立进程；数据库是 Desired State，TOML 仅为受控生成物；不得泄露秘密、执行 shell、自动提权、关闭或规避 Defender，也不得让 RelaxKonOS Backend 依赖 FRP。每个 Goal 只有在构建、测试和本文件验收通过后才能进入下一项。
