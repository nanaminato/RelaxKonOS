# RelaxKonOS 代理管理器（Goal 执行版）

> 状态：实施中（Goal 0–8 与 Goal 10 已实现；Goal 9 需要受控 Windows/Ubuntu 环境验证）
> 建立日期：2026-08-31  
> 适用范围：`.NET 10` Server、Avalonia Client、Windows / Windows Server / Ubuntu / Ubuntu Server  
> 架构依据：[代理管理器实现规范](./RelaxKonOS.ProxyManager.Design.md)；[实现调研](./RelaxKonOS.ProxyManager.Discovery.md)

本文是 Proxy Manager 的 `/goal` 执行基线。它将设计规范与已完成的仓库调研合并为可独立构建、验证、回滚的实施目标；设计规范仍是产品行为和安全原则的权威来源，调研文档仍是当前代码基线与可复用设施的权威来源。

实施必须按 Goal 顺序推进。每个 Goal 完成后，先运行与该阶段相称的测试，并保持 `dotnet build RelaxKonOS.sln -c Debug` 可通过；若现有架构、受支持的 Mihomo CLI 契约或宿主特权模型与本文件不符，先更新本文件和设计文档并重新评审，不得通过临时代码绕过冲突。

## 1. 目标与交付边界

首个可发布版本（V1）提供一个宿主机全局的 `remoteos.proxy` 内置应用。它以 Mihomo 为首个 Engine，在 Windows、Windows Server、Ubuntu 和 Ubuntu Server 上管理已验证的 Managed Runtime 或明确选择的 External Runtime，并提供 Profile、原始 YAML 的事务化应用、服务生命周期、节点组、连接、受限日志、基础 DNS 状态、审计和网络恢复。

TUN 是 V1 的一级能力，而不是 UI 开关：只有在管理流量保护、路由/DNS 快照、可恢复标记、串行化操作和紧急恢复均已实现并完成平台验证后，才能公开启用入口。

V1 必须同时满足：

- Avalonia 只通过 RelaxKonOS API；Mihomo Controller 仅 Server 本机访问，Controller secret 不离开 Server。
- UI、API 与领域模型保持 Engine-neutral；`MihomoEngine` 只是 `IProxyEngine` 的第一个实现。
- Runtime、活动 Profile、恢复标记、网络快照、控制器配置、操作和审计均为主机级状态；不能存入 Workspace 偏好或按用户拥有。
- Managed Runtime 使用固定版本/平台/架构/来源/哈希的受信任清单，版本目录切换、健康检查及 active/previous 回滚；首次安装和健康检查默认 TUN 关闭。
- 高风险操作使用真实的 Server 端逐操作授权、幂等键、持久 Operation ID 和无秘密审计；AppPermissions 仅控制客户端入口。

V1 明确不包括：

- sing-box、Xray、集中式多主机代理编排、自动购买或订阅市场。
- 完整 YAML 可视化编辑器、规则/Provider 可视化设计器、完整路由表/DNS/防火墙管理、流量统计数据库或 MetaCubeXD 克隆。
- Client 直接访问 Controller、公开绑定 Controller、把 Mihomo 作为 RelaxKonOS.Server 的长期子进程，或将其接入 FRP 的子进程监管模型。
- 关闭 Defender、SmartScreen、防火墙或增加宽泛安全排除；防火墙在本范围仅诊断，不写入 UFW、nftables、iptables 或 Windows Firewall。
- 任意命令、可执行文件、参数或密码的提权透传；不得创建通用 privileged-command executor。

## 2. 当前代码基线与落点

Phase 0 结论是：仓库还不存在 Proxy API、服务、Mihomo Controller Client、Runtime Installer、Profile Store 或 Avalonia UI。下表给出必须复用的现有模式及新增边界。

| 关注点 | 既有模式 / 首选落点 | 本模块要求 |
|---|---|---|
| 协议与路由 | `Shared/RelaxKonOS.Protocol`、`/api/v1.0`、按模块 DTO/route constants | 添加 `Proxy/` 合约；端点使用 `/api/v1.0/proxy`，不在 Client 或 Endpoint 重复路由。 |
| Server API | `RelaxKonOS.Server/Endpoints`、`Map*Endpoints`、JWT、角色 policy | 添加 `MapProxyEndpoints`；读、管理、Runtime、TUN、恢复操作均有明确 Server policy。 |
| 长操作 | Web Server operation store / host operation journal | 复用或有意识地提取其幂等、阶段、取消、持久化、锁和中断恢复语义；额外实现全局 TUN lock 与 recovery marker。 |
| 秘密与审计 | `ISecretStore` / Data Protection、Tunnel audit | 新建 Proxy 专用 secret purpose/entity 和审计；不复用 Tunnel secret 实体，任何安全 DTO 仅暴露 `*Configured`。 |
| Runtime 安全 | FRP Runtime 的固定清单、下载验证、暂存、版本、回滚模式 | 只复用模式，不扩展 FRP 类或由 FRP 监管 Mihomo；Proxy 使用受保护的平台路径和原生服务。 |
| 服务和特权 | `INativeServiceAdapter`、Web Server privilege check、Firewall typed helper | 提取可复用的 allowlisted 服务能力即可；另建严格限定的 Proxy 特权操作边界，绝不提供 shell/命令执行。 |
| Client | `RemoteApplicationBase`、typed `HttpClient`、Bootstrapper、Docker 多页 workspace、`ShowDialogAsync` | 添加 `IProxyRepository` / `RemoteProxyRepository` 与独立 AXAML 页面；ViewModel 不构造 `HttpClient` 或了解 Mihomo JSON。 |
| 本地化与主题 | `LocalizationService`、semantic DynamicResource、managed modal | 所有键提供 `en-US`、`zh-CN`、`ja-JP`；不增加 Mihomo 专属色板、硬编码 UI 文案或原生系统弹窗。 |

建议的新增模块如下；实际目录可随既有命名微调，但职责不得散入 `Program.cs`、Endpoint 或 ViewModel：

```text
Shared/RelaxKonOS.Protocol/Proxy/
RelaxKonOS.Server/Proxy/
RelaxKonOS.Server/Proxy/Platform/
RelaxKonOS.Server/Proxy/Operations/
RelaxKonOS.Server/Proxy/Secrets/
RelaxKonOS.Server/Endpoints/ProxyEndpoints.cs
Client/RelaxKonOS.Client/Apps/Proxy/
```

`IProxyEngine`、`IProxyRuntimeManager`、Profile/configuration/recovery 服务、Engine registry、`IProxyPlatformService` 和平台路径抽象必须是窄接口。Windows/Linux 命令只能封装在平台或受限特权边界内，业务层不得散布 `systemctl`、`sc.exe`、`netsh`、PowerShell 或 `ip` 调用。

## 3. 必须冻结的架构契约

### 3.1 Engine、Runtime 与 Controller 边界

领域和 Client 只依赖 Engine-neutral 的 `Proxy*` 合约。`IProxyEngine` 负责能力、健康、配置验证/重载、组选择、连接和日志映射；`IProxyRuntimeManager` 负责 Managed/External Runtime 生命周期与完整性。Mihomo Controller client 只在 Server 中以 loopback/local IPC 通信，默认 local-only，并将 Controller JSON 映射为中性 DTO。

Mihomo 必须作为原生 OS Service 生命周期运行，而非 Server 持有的长期子进程。External Runtime 只允许检测、验证和在管理员明确要求时使用 RelaxKonOS 私有配置受管启动；不得覆盖、升级、卸载或停止用户原有进程。

### 3.2 主机级状态、原始 YAML 与配置事务

实施前必须选定并记录 Proxy 主机级 schema/migration 路径。元数据保存 Profile、活动 Profile、Runtime 状态、Operation/Audit 引用与安全状态；原始 YAML、备份、二进制、日志和恢复文件保存在平台受保护路径。Mihomo 完整配置保留为 raw YAML 加 RelaxKonOS-managed overlay，不尝试 V1 全量 DTO 化。

同一活动配置的变更必须串行化，固定流程为：

```text
读取 → 领域校验 → 备份 → 临时写入 → Mihomo 验证
    → 原子提交 → reload/restart → 健康检查 → 成功或回滚并再次验证
```

验证、写入、reload 或健康检查失败时，最后一个可用配置和服务状态必须保持或恢复；不得留下半写入配置，且“已保存未应用”不得显示为已运行。

### 3.3 Runtime 信任链与特权边界

Managed Mihomo Runtime 只能由固定清单安装：版本、RID/CPU 架构、官方下载 URL、预期 SHA-256、来源和取得时间均在下载前确定。流程是私有暂存、下载/归档大小限制、路径遍历过滤、哈希验证、预期二进制检查、版本目录安装、受限健康检查，再原子切换 active 指针。新版本通过运行和健康检查前，不得替换 active；失败必须保留 previous。

服务安装、删除、更新、受保护配置写入、服务启动策略和网络恢复可能需要 OS 权限。Phase 3 前必须完成一个只有这些具名结构化操作的跨平台部署/Helper 设计；当前仓库没有可直接复用的通用 elevation workflow。RelaxKonOS 不请求或收集 OS 密码，也不接受客户端提交的命令、参数或可执行文件。

### 3.4 TUN 安全与恢复是发布前置条件

启动 TUN 前，平台服务必须解析出口接口并捕获当前会话的客户端/服务端地址、端口、协议、网关和接口，生成管理路径快照。默认并且不可由 V1 Client 关闭的 System Bypass 至少覆盖 loopback、RelaxKonOS 监听端点、活动客户端、默认网关、LAN、已配置管理网段、SSH 与 RDP 管理路径。

固定启用顺序：

```text
校验 Profile/Runtime/Service/Controller → 解析出口接口 → 捕获网络快照
→ 生成受保护 TUN 配置 → 持久 recovery marker → start/reload Mihomo
→ 等待 Controller 与 TUN → 校验代理出口与管理连接 → 标记 Active
```

任何网络改动前都写 recovery marker，包含先前 Runtime/Profile、路由/DNS 快照、时间戳与 Operation ID。Server 重启时发现未完成 marker 或 Operation 必须进入 recovery evaluation。失败、服务崩溃、Server 崩溃或重启后均需可回滚；`Emergency Disable TUN` 独立于卸载，恢复安全路由/DNS 并保留 Profile。

### 3.5 权限、问题码、日志与审计

建议最小能力族为 `proxy.read`、`proxy.manage`、`proxy.profile.read/manage`、`proxy.connection.read/close`、`proxy.tun.read/manage`、`proxy.runtime.read/manage` 与 `proxy.recovery.execute`。`proxy.tun.manage`、`proxy.runtime.manage`、`proxy.recovery.execute` 为危险操作，必须具备 Server authorization；Manifest 中的 `Network.Proxy.*` 仅用来限制 UI 功能。

公开 problem code 必须采用仓库现有的**小写点分**约定（例如 `proxy.runtime_not_installed`、`proxy.management_route_unsafe`），不要混用规范示例中的大写形式。后端只返回安全诊断，Client 按稳定 code 本地化。

Controller secret、订阅 URL token/认证头、代理凭据、UUID、WireGuard/private key、完整配置和敏感命令行不得出现在 DTO、日志、异常 Detail、审计、UI state、导出或普通备份中。Install/update/rollback/uninstall、start/stop、TUN enable/disable、紧急恢复、Profile/configuration 修改、节点选择和连接关闭均审计 actor、session、host、engine、profile、result、problem code、时间和 correlation ID，但不记录秘密。

## 4. Goal 执行计划

### Goal 0：确认调研结论、冻结决策与威胁模型

**状态**：已完成。冻结记录见 [Goal 0 decision record](./RelaxKonOS.ProxyManager.Goal0.md)。

**工作**：复核 `RelaxKonOS.ProxyManager.Discovery.md` 中的 Solution、授权、Service、特权、持久化、操作、秘密、Client 与 UI 结论。冻结 V1 Windows/Linux/CPU 发布矩阵、Mihomo 支持版本范围、固定发布清单与哈希材料来源、主机级 schema/migration 方案、平台路径/保留策略、问题码表、权限矩阵、审计事件和不支持能力的返回约定。明确当前缺少通用 elevation workflow 是 Phase 3 的设计门槛。

**验收**：没有未决的“假定已有”服务或权限抽象；威胁模型覆盖恶意归档、路径遍历、校验失败、YAML/命令注入、秘密泄露、Controller 暴露、操作中断、路由/DNS 损坏、管理连接中断、Defender/组织策略拒绝；关键设计决定写入本文或设计文档。

### Goal 1：协议、授权与无秘密领域骨架

**状态**：已完成。`Shared/RelaxKonOS.Protocol/Proxy` 定义了无秘密的 Engine-neutral 合约、路由和问题码；`RelaxKonOS.Server/Proxy` 仅包含窄的 Server 领域边界，尚未接入 Mihomo、Runtime、Service、TUN、API 或 UI。

**工作**：在 `Shared/RelaxKonOS.Protocol/Proxy` 添加 Engine/platform capabilities、operating/runtime/TUN/health/operation 状态、Profile、Runtime、Groups、Connections、Logs、DNS、Recovery DTO、路由常量与小写点分 problem code。添加 Engine-neutral Server 接口、engine registry、`IProxyPlatformService` 和平台路径抽象，但不实现下载、原生服务、Controller、TUN 或 UI。增加 AppPermissions/manifest 所需稳定 capability 声明，并设计 Server read/manage/dangerous policies。

**验收**：Protocol 保持零 PackageReference；Client/Server 无重复字符串路由；DTO 序列化、problem-code 与无秘密测试通过；App 权限不被当作 HTTP 授权；Firewall 仅能诊断、不能由 Proxy 功能改写。

### Goal 2：Mihomo Adapter 与本地 Controller 安全

**状态**：已完成。`RelaxKonOS.Server/Proxy/Mihomo` 以本机 REST Controller 适配器、受保护 Controller secret、bounded/sanitized logs 和中性 DTO 映射实现；仅注册 Server DI，尚未开放 Endpoint 或 Client 访问。

**工作**：实现 Server-only `MihomoEngine` 和 local-only Controller client，生成并以 Proxy-scoped SecretStore 保存 Controller secret；提供能力、状态、组/节点、选择、连接、关闭连接、受限日志、配置验证和 reload 映射。限制日志体积和保留时间，实施统一 sanitizer。必要时先使用 bounded REST；实时能力仅在有需求时复用既有 SignalR 合约。

**验收**：Client 永不访问 `127.0.0.1:9090` 或其他 Controller 地址，Controller JSON 不离开 Server；Controller secret 未出现在任何序列化结果、日志或异常；Controller 不可用、未知字段、超时和不安全日志均返回稳定、安全结果；不新增 WebSocket/socket 框架。

### Goal 3：Runtime、原生服务与受限特权操作

**状态**：已完成。`MihomoRuntimeManager` 仅接受源代码固定的 Mihomo 发布清单，以有界下载、SHA-256、归档条目限制、架构/版本探测与 immutable 版本目录处理 Managed Runtime；激活只有在 TUN 关闭的 bootstrap 配置、受限生命周期操作和本机 Controller health 均通过后才写 active/previous state。Linux 只使用固定 systemd 操作；Windows 使用 `WindowsMihomoProcessHost` 让 `RelaxKonOS.Server` 持有 Mihomo 子进程及其完整进程树、异常重启和宿主停止清理。无主机权限时安全返回 `proxy.privileged_operation_unavailable`，不请求密码或退化为 shell。平台真实安装/回滚验证留给 Goal 9。

**工作**：实现 External 只读检测与 Managed Mihomo Runtime 的固定清单、暂存、归档检查、SHA-256 验证、版本目录、active/previous、健康检查、升级、回滚和卸载。建立 Windows/Linux `IProxyPlatformService` 及强类型 Proxy 生命周期边界：Linux 安装/删除并控制 systemd 服务，Windows 由 Server 内的受控子进程宿主控制。首次安装成功标准为 TUN 关闭状态下的 Controller health check。

**验收**：不存在/非文件/不可执行/不匹配架构的 External 路径不会被接管；校验失败、归档穿越、超限、缺少二进制、锁定文件、启动超时和健康失败都不能激活新版本；升级失败保留 active/previous 工作版本；没有 shell 拼接、OS 密码收集或通用执行 API；Windows Defender 与防火墙未被修改。

### Goal 4：主机级 Profile 与配置事务

**状态**：已完成。host-global schema v8 存储 Proxy Profile 元数据与配置审计引用，绝不使用 Workspace preferences；raw YAML 仅位于平台受保护目录。配置事务以全局串行锁执行临时写入、Engine 验证、备份、原子提交、reload/health check，并在失败时恢复最后一个工作 YAML；无法恢复则返回 `proxy.recovery_required`。Endpoint/UI 仍未暴露 raw YAML。

**工作**：实现 Proxy metadata/repository、活动 Profile、raw YAML 受保护读写、Managed overlay、配置验证、临时写、备份、原子提交、reload/restart、健康检查和回滚。Profile 删除 active 项时阻止或要求先显式切换；V1 不提供完整结构化 YAML 编辑器。订阅仅在其 SecretStore、授权、更新/失败策略完成后纳入，不能以明文 URL/token 作为普通 Profile 字段。

**验收**：无效配置在提交前拒绝；写入/reload/Controller timeout/health-check/rollback failure 均有明确状态且不破坏最后一个工作配置；并发变更串行化；备份和 YAML 不可被普通 API 下载；Profile 元数据、操作和安全状态不进入 Workspace preferences。

### Goal 5：TUN 安全、恢复与无 UI 验证

**工作**：实现平台 capability 检测、出口接口解析、活动会话与管理路线保护、不可编辑 System Bypass、路由/DNS 快照、host-wide TUN lock、recovery marker、事务化 enable/disable、Server 启动 recovery evaluation、自动 rollback 与 Emergency Disable。此 Goal 仅实现并测试 Server 领域/平台能力，尚不向 API 或 Client 暴露危险开关。

**验收**：任何激活路径都先写 marker 和网络快照；无有效管理路径、无效接口或缺失平台能力时拒绝且不改变网络；Mihomo/Server 在激活中崩溃与机器重启后可检测并恢复；验证当前 RelaxKonOS 管理会话、LAN、网关、SSH/RDP 路径保持可达；Ubuntu/Windows Server 无 GUI 情况下可运行。

### Goal 6：API、授权、操作与审计

**工作**：在 `Program.cs` 注册服务并添加 `/api/v1.0/proxy` Endpoint family，覆盖 Overview、Runtime、Lifecycle、TUN、Profiles、Groups、Connections、Logs、DNS 与 Recovery。Runtime/TUN/recovery mutation 必须要求 Idempotency-Key，立即返回持久 Operation ID，并沿用现有 operation 语义提供阶段/进度/取消/中断恢复。应用 authenticated + 逐操作 policy；记录无秘密审计。

**验收**：未授权/只读/危险操作权限不足均有稳定安全的响应；相同 Idempotency-Key 不执行两次网络或 Runtime 变更；操作重启后可恢复或明确中断；所有危险 Endpoint 仅接受结构化数据；API 响应、审计、日志和 problem detail 均通过秘密扫描/序列化测试。

### Goal 7：Avalonia 内置应用

**工作**：注册 typed repository、`remoteos.proxy` manifest 和单窗口内置应用。实现独立的 Overview、Profiles、Proxies、Connections、DNS、Logs、Settings 页面及 Profile/config/recovery managed dialogs；页面按 capability 驱动而非 Mihomo 名称驱动。复用 MVVM、取消/轮询、theme、`ShowDialogAsync` 和三语本地化。

**验收**：ViewModel 不直接使用 `HttpClient`，不解析 Mihomo JSON，也不持有 Controller secret；无 Runtime、外部路径失效、服务崩溃、操作中、恢复必需、权限拒绝和 API 断线均显示真实状态；TUN 卡片明确显示管理保护与 Emergency Restore；关闭窗口只取消 Client 请求/轮询，不停止 Server Runtime；不使用原生弹窗、硬编码 UI 文案或硬编码颜色。

### Goal 8：安全收尾与跨层测试

**工作**：完成 Proxy SecretStore 生命周期、sanitizer、Controller/Endpoint authorization、审计与 Operation 边界测试。复核不泄露模型、日志、异常、备份和本地化错误呈现；确认任何从设计继承的 uppercase error 示例均未意外进入 public API。

**验收**：安装、更新、回滚、Profile/configuration、节点选择、连接关闭、TUN 与恢复操作均有无秘密审计；普通 GET 无法读取凭据或 raw YAML；攻击性输入、日志注入、未授权 Controller、过期 operation 和恢复 marker 的测试通过；Server policy 而不是 App ID 决定授权。

### Goal 9：Windows 与 Ubuntu 的真实集成验证

**工作**：在 Windows/Windows Server 与 Ubuntu/Ubuntu Server 执行 Managed/External、Service install/start/stop/restart、Runtime update/rollback、Profile transaction、TUN enable/disable、reboot/crash recovery 与 emergency restore。使用受控环境验证 route/DNS 和管理路径，不让真实生产主机作为首次试验场。

**验收**：Windows TUN、Ubuntu `/dev/net/tun` 和 systemd 场景均通过；启用 TUN 后当前 RelaxKonOS API/登录会话继续可用；Server 无 `DISPLAY`、DBus 桌面会话或交互用户时仍可运行；失败路径不会遗留错误路由/DNS、孤儿服务或损坏 active Runtime。

### Goal 10：文档、发布检查与后续扩展边界

**工作**：新增 `docs/proxy/` 下的 architecture、mihomo、tun、recovery、security、installation 与 troubleshooting 文档，更新文档索引和本 Goal 状态。记录平台目录、权限、恢复步骤、支持矩阵、已知限制与操作员演练。将 subscription 自动刷新、延迟测试、规则可视化、per-app/system proxy 和 `SingBoxEngine` 作为后续独立 Goal。

**验收**：管理员无需查看源码即可安全安装、配置、启用、禁用并恢复 TUN；所有 V1 限制明确；未来接入 `SingBoxEngine` 不要求更改 Avalonia、public API 或 Engine-neutral domain model；构建、自动化测试、平台测试和发布清单均通过。

## 5. 测试、观测与验收口径

### 5.1 自动化测试

- **协议/纯函数**：DTO 无秘密序列化、problem code、状态迁移、路径与 Profile 校验、YAML/日志脱敏、版本选择、SHA-256、归档条目与大小限制、平台能力和 management-route plan。
- **Runtime/服务**：替换 HTTP、进程、文件系统、时间和平台适配器，覆盖 External 只读检测、下载失败、归档穿越、错误 RID、服务失败、超时、cancel、active/previous 回滚和 interrupted operation。
- **配置/恢复**：有效/无效配置、临时写入失败、reload/Controller/health timeout、回滚成功/失败、recovery marker、Server crash/reboot 与 Emergency Disable。
- **Endpoint/安全**：认证、read/manage/dangerous policy、Idempotency-Key、operation 状态、审计、secret refusal、敏感日志与 error detail。测试不得连接真实 Controller 或下载生产 Runtime。
- **Client**：repository 路由/认证、本地化动态更新、命令重入/取消、权限禁用和异常状态呈现；不模拟 Controller 协议。

### 5.2 手工验证矩阵

当前环境未执行的、需要特权和隔离网络的 Goal 5/9 平台测试，逐项记录在
[`docs/testing/RelaxKonOS.ProxyManager.SkippedTests.md`](../testing/RelaxKonOS.ProxyManager.SkippedTests.md)。
在该清单全部完成前，Proxy Manager 不得标为 V1 完成。

| 场景 | 需要确认的结果 |
|---|---|
| Windows / Windows Server Managed | 受验证 Runtime、SCM 服务、启动/停止、升级/回滚与无 TUN 首次健康检查均成功；Defender 不被修改。 |
| Ubuntu / Ubuntu Server Managed | systemd、受保护目录、`/dev/net/tun`、服务生命周期、升级/回滚和无 GUI 运行均成功。 |
| External Runtime | 仅检测用户已有 Runtime；显式受管时仅控制 RelaxKonOS 创建的服务/实例。 |
| TUN 成功路径 | 当前 Client、RelaxKonOS 监听端点、网关、LAN、SSH/RDP 仍可达；DNS 和出口状态真实。 |
| TUN 失败与恢复 | 无效接口、Controller 不可用、运行时崩溃、Server crash 和 reboot 后，marker 能驱动安全恢复或显式 recovery-required。 |
| 安全/权限 | 未授权用户无法获取 Controller/订阅凭据、raw YAML 或执行危险操作；日志、审计和诊断无秘密。 |

### 5.3 发布级完成定义

Proxy Manager 仅在以下条件全部满足后才可标为 V1 完成：

1. Goal 0–10 的验收均通过，且 V1 非目标没有被隐式实现或承诺。
2. Mihomo Controller 仅本机访问；Avalonia 从不直连 Controller，Controller secret 和其他凭据不进入 Client/API/日志/审计/异常。
3. Runtime 经过固定清单和 SHA-256 验证；更新不覆盖可用版本，失败可回滚；External Runtime 不被擅自接管。
4. 所有配置写入均验证、备份、原子提交、reload/restart 与健康检查；失败不破坏最后一个可用配置。
5. TUN 有管理流量保护、网络快照、recovery marker、全局锁、自动/紧急恢复，并已在 Windows 与 Ubuntu 上证明不切断 RelaxKonOS 管理连接。
6. Server 逐操作授权、幂等长操作和无秘密审计均落实；AppPermissions 不作为最终授权。
7. 没有通用 command executor、shell 拼接、公开 Controller、Defender/Firewall 绕过或业务层散布 OS 命令。
8. Avalonia 应用采用既有 workspace、MVVM、typed repository、主题、本地化和 modal 模式，且 UI 真实呈现失败、恢复与权限状态。

## 6. 后续 Goal 提示

后续在 Goal 模式中应使用以下任务描述，并把本文件、实现规范和调研一并作为约束：

> 依据 `docs/applications/RelaxKonOS.ProxyManager.Goal.md`、`docs/applications/RelaxKonOS.ProxyManager.Design.md` 与 `docs/applications/RelaxKonOS.ProxyManager.Discovery.md` 实现 RelaxKonOS 代理管理器。严格按 Goal 0–10 顺序推进：先确认已完成调研并冻结主机级持久化、权限、问题码和受限特权边界；再建立 Engine-neutral contracts、Server-only Mihomo adapter、受验证 Runtime 和配置事务。TUN 安全（管理路径保护、快照、recovery marker、回滚与紧急恢复）必须先完成 Server/平台验证，才可开放 API/UI。Mihomo 必须以原生 OS Service 运行，Controller 仅本机访问；禁止 Client 直连、秘密泄露、shell/任意提权、Defender/Firewall 绕过以及业务层 OS 命令散布。每个 Goal 只有在构建、测试和本文件验收通过后才能进入下一项。
