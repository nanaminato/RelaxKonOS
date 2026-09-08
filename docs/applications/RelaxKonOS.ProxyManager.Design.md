# RelaxKonOS 内置代理管理器实现规范

> 文档类型：Implementation Specification / Codex Execution Document  
> 模块名称：RelaxKonOS Proxy Manager
> 首选代理核心：Mihomo  
> 主客户端：Avalonia  
> 服务端：.NET 10  
> 目标平台：Windows / Windows Server / Ubuntu / Ubuntu Server  
> 首要运行模式：TUN  
> 状态：Initial Implementation Specification

---

# 1. 文档目的

本文档用于指导 Codex 在现有 RelaxKonOS 项目中实现一个正式的内置应用：

```text
Proxy Manager
代理管理器
```

该模块负责在 RelaxKonOS 所管理的当前主机上安装、配置、启动、停止、监控和升级代理核心，并通过 RelaxKonOS Avalonia 客户端提供完整管理 UI。

第一阶段仅正式支持：

```text
Mihomo
```

但架构必须允许未来增加：

```text
sing-box
Xray
其他 headless proxy core
```

禁止把模块实现成：

```text
MihomoManager
ClashManager
```

整个功能必须围绕：

```text
IProxyEngine
```

建立抽象。

Mihomo 只是第一种 Engine。

---

# 2. RelaxKonOS 既有架构约束

实现过程中必须遵循 RelaxKonOS 已确定的架构，而不是为了 Proxy Manager 建立第二套体系。

主要约束：

```text
Client
    Avalonia
    MVVM
    RelaxKonOS-owned workspace/modal infrastructure

Server
    .NET 10
    Windows + Linux

Communication
    RelaxKonOS API

Platform abstraction
    Windows/Linux 差异封装在 Server

Privilege
    RelaxKonOS 统一权限系统
    RelaxKonOS 统一 elevation workflow
```

Avalonia 是 RelaxKonOS 的主要客户端实现。

Flutter 客户端即使存在，也不得影响本模块的领域模型和 Server API 设计。

本规范主要要求：

```text
RelaxKonOS.Client
RelaxKonOS.Server
RelaxKonOS.Shared / Contracts
RelaxKonOS.Helper（如现有）
```

---

# 3. 核心设计原则

Proxy Manager 必须遵循以下原则。

## 3.1 Client 永远不直接连接 Mihomo

禁止：

```text
Avalonia
    │
    └── http://server:9090
             │
             ▼
          Mihomo
```

正确结构：

```text
Avalonia
    │
    │ RelaxKonOS API
    ▼
RelaxKonOS.Server
    │
    │ localhost / local IPC
    ▼
Mihomo
```

Mihomo Controller 默认只允许本机访问。

例如：

```yaml
external-controller: 127.0.0.1:9090
```

或者未来 Windows 可考虑：

```text
Named Pipe
```

Linux 可考虑：

```text
Unix Domain Socket
```

RelaxKonOS Client 不应知道 Mihomo Controller secret。

---

# 4. 总体架构

目标结构：

```text
┌─────────────────────────────────────┐
│          RelaxKonOS Avalonia          │
│                                     │
│ Proxy Manager                       │
│                                     │
│ Overview                            │
│ Proxies                             │
│ Profiles                            │
│ Rules                               │
│ Connections                         │
│ DNS                                 │
│ Logs                                │
│ Settings                            │
└───────────────────┬─────────────────┘
                    │
             RelaxKonOS API
                    │
                    ▼
┌─────────────────────────────────────┐
│          RelaxKonOS.Server            │
│                                     │
│ ProxyManager                        │
│                                     │
│ ├── ProxyEngineManager              │
│ ├── ProxyRuntimeManager             │
│ ├── ProxyProfileManager             │
│ ├── ProxySubscriptionManager        │
│ ├── ProxyConnectionManager          │
│ ├── ProxyRoutingManager             │
│ ├── ProxyDnsManager                 │
│ ├── ProxyRecoveryManager            │
│ ├── ProxySecurityManager            │
│ └── ProxyAuditService               │
│                                     │
│             IProxyEngine            │
│                    │                │
│        ┌───────────┴──────────┐     │
│        ▼                      ▼     │
│  MihomoEngine          future engine│
└────────┬────────────────────────────┘
         │
         │ localhost API
         ▼
┌─────────────────────────────────────┐
│              Mihomo                 │
│                                     │
│ TUN / DNS / Rules / Proxy           │
└───────────────────┬─────────────────┘
                    │
                    ▼
              Operating System
```

---

# 5. TUN 是一级功能

本模块不得按照传统桌面 Clash GUI 的思路：

```text
System Proxy = main
TUN = optional advanced feature
```

RelaxKonOS 的设计应按照：

```text
TUN = primary server-wide proxy mode
Listener Proxy = secondary mode
RelaxKonOS-only Proxy = scoped mode
```

定义以下运行模式：

```csharp
public enum ProxyOperatingMode
{
    Tun,
    ListenerOnly,
    RelaxKonOSOnly
}
```

第一阶段至少实现：

```text
Tun
ListenerOnly
```

RelaxKonOSOnly 可以第二阶段实现。

---

# 6. Mihomo TUN 基础配置

初始生成配置可以采用：

```yaml
mode: rule

external-controller: 127.0.0.1:9090

tun:
  enable: true
  stack: mixed
  auto-route: true
  auto-detect-interface: true

dns:
  enable: true
```

注意：

这只是配置生成器的基础模板。

不得直接硬编码成为不可修改配置。

Mihomo 当前 TUN 支持 Windows 和 Linux；`auto-route` 可以自动将流量路由进入 TUN。Linux 还支持 `auto-redirect`，该功能依赖 `auto-route`。citeturn938106search3turn938106search4

因此平台能力模型必须存在：

```csharp
public sealed record ProxyPlatformCapabilities
{
    public bool SupportsTun { get; init; }

    public bool SupportsAutoRoute { get; init; }

    public bool SupportsAutoRedirect { get; init; }

    public bool SupportsDnsHijack { get; init; }

    public bool SupportsNamedPipeController { get; init; }

    public bool SupportsUnixSocketController { get; init; }
}
```

例如：

```text
Windows
    TUN                 YES
    auto-route          YES
    auto-redirect       NO

Linux
    TUN                 YES
    auto-route          YES
    auto-redirect       YES
```

UI 必须根据 capability 显示选项。

不得在 Avalonia 中判断：

```csharp
if (OperatingSystem.IsLinux())
```

平台判断属于 Server。

---

# 7. Mihomo 运行时生命周期

Windows 由长期运行的 `RelaxKonOS.Server` 直接托管 Mihomo；不再额外注册
`relaxkonos-mihomo` SCM 服务：

```text
Windows SCM（如 RelaxKonOS.Server 作为 Windows Service）
    ↓
RelaxKonOS.Server / WindowsMihomoProcessHost
    ↓
mihomo.exe
```

进程宿主只从受保护的活动版本和配置启动 Mihomo，持有唯一 `Process`，捕获标准
输出/错误、异常退出后延迟重启，并在 Server 停止、卸载或更新时以完整进程树终止。
因此 Mihomo 的 Windows 生命周期与 Server 绑定；系统重启后由 Server 的既有启动
策略恢复。

Linux 保持独立 systemd 服务：

```text
systemd
    ↓
mihomo.service
```

平台不强行共享相同的服务语义；两者都必须在 TUN 操作前后保留网络恢复机制。

---

# 8. Helper 的职责边界

RelaxKonOS 已经存在/计划存在权限提升体系。

Proxy Manager 不得把 Helper 扩展成：

```text
RelaxKonOS root shell daemon
```

或者：

```text
ExecuteAnythingAsRoot(command)
```

禁止提供类似：

```csharp
RunPrivilegedCommand(string command)
```

这样的通用接口。

---

# 9. Helper 只允许处理明确的特权操作

Proxy Manager 可以申请以下明确操作：

```text
InstallProxyRuntime
RemoveProxyRuntime

InstallProxyService
RemoveProxyService

StartProxyService
StopProxyService
RestartProxyService

ReplaceProxyBinary

WriteProtectedProxyConfiguration

RestoreNetworkConfiguration

RepairProxyService

SetProxyServiceStartup
```

对应接口必须是强类型：

```csharp
Task InstallProxyServiceAsync(...);

Task RemoveProxyServiceAsync(...);

Task StartProxyServiceAsync(...);

Task StopProxyServiceAsync(...);

Task RepairProxyNetworkAsync(...);
```

不能变成：

```csharp
Task ExecuteAsync(string executable, string arguments);
```

---

# 10. 日常运行不应频繁触发提权

理想生命周期：

第一次：

```text
User
  ↓
Enable TUN
  ↓
RelaxKonOS detects Mihomo not installed
  ↓
Permission check
  ↓
Elevation request
  ↓
User approval
  ↓
Helper
  ↓
Install Mihomo
Install system service
Create protected directories
Initialize config
  ↓
Done
```

以后：

```text
User
  ↓
Enable / Disable
  ↓
RelaxKonOS.Server
  ↓
Service Manager
  ↓
Mihomo
```

不应该每次：

```text
Enable TUN
    ↓
UAC / sudo password
```

运行时安装完成后，Linux 由 systemd 持续运行；Windows 由长期运行的
`RelaxKonOS.Server` 进程宿主持续管理。

---

# 11. RelaxKonOS 权限 ≠ OS 权限

必须保留两层权限。

```text
RelaxKonOS authorization
            +
OS privilege/elevation
```

即使 Mihomo 已经以：

```text
root
LocalSystem
```

运行，也不能意味着任意 RelaxKonOS 用户都可以控制它。

建议能力：

```text
proxy.read

proxy.manage

proxy.profile.read
proxy.profile.manage

proxy.subscription.read
proxy.subscription.manage

proxy.connection.read
proxy.connection.close

proxy.rules.read
proxy.rules.manage

proxy.tun.read
proxy.tun.manage

proxy.runtime.read
proxy.runtime.manage

proxy.recovery.execute
```

其中危险权限：

```text
proxy.tun.manage
proxy.runtime.manage
proxy.recovery.execute
```

必须经过 RelaxKonOS Server authorization。

---

# 12. Built-in App 权限模型

Proxy Manager 是 RelaxKonOS：

```text
Built-in Application
```

因此不采用第三方 Installed App 的普通授权流程。

但：

```text
Built-in
```

不等于：

```text
Unlimited permissions
```

Built-in App 可以声明其系统能力：

```text
Network.Proxy.Read
Network.Proxy.Manage
Network.Proxy.Tun
Network.Proxy.Runtime
```

最终权限仍由：

```text
Authenticated RelaxKonOS user
    ↓
role / permission
    ↓
Server authorization
```

决定。

Client 不负责最终授权。

---

# 13. 配置文件不能完全结构化重写

不要尝试第一版把整个 Mihomo YAML 转换成 RelaxKonOS DTO。

原因：

Mihomo 配置格式：

```text
proxies
proxy-groups
rules
rule-providers
proxy-providers
dns
tun
sniffer
hosts
listeners
```

非常复杂，而且会持续演化。

推荐：

```text
Raw Configuration
        +
RelaxKonOS Managed Overlay
```

---

# 14. Profile 模型

例如：

```csharp
public sealed record ProxyProfile
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public string EngineId { get; init; } = "mihomo";

    public string ConfigFile { get; init; } = "";

    public bool Managed { get; init; }

    public Guid? SubscriptionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
```

RelaxKonOS 管理：

```text
profile metadata
subscription relation
active profile
runtime state
managed TUN overlay
managed controller configuration
```

Mihomo 自己继续管理完整 YAML。

---

# 15. 配置修改流程必须事务化

沿用 RelaxKonOS 对系统配置的标准策略：

```text
Read
 ↓
Validate
 ↓
Backup
 ↓
Write temporary file
 ↓
Validate new configuration
 ↓
Commit
 ↓
Reload / Restart
 ↓
Health check
```

如果失败：

```text
Rollback
 ↓
Restart
 ↓
Health check
```

禁止：

```text
直接覆盖 config.yaml
然后希望 Mihomo 能启动
```

---

# 16. Mihomo 配置验证

更新配置前调用 Mihomo 自身的配置验证能力。

抽象：

```csharp
public interface IProxyConfigurationValidator
{
    Task<ProxyConfigurationValidationResult> ValidateAsync(
        ProxyEngineRuntime runtime,
        string configuration,
        CancellationToken cancellationToken);
}
```

如果失败：

```text
do not commit
```

返回：

```csharp
public sealed record ProxyConfigurationValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<ProxyConfigurationError> Errors { get; init; }
}
```

---

# 17. 配置目录

## Windows

建议：

```text
C:\ProgramData\RelaxKonOS\Proxy\
```

结构：

```text
Proxy\
├── engines\
│   └── mihomo\
│       ├── current\
│       └── versions\
│
├── profiles\
│
├── subscriptions\
│
├── runtime\
│
├── backups\
│
├── logs\
│
└── state\
```

---

# 18. Linux 目录

推荐遵循 Linux 目录语义：

```text
/etc/relaxkonos/proxy/

/var/lib/relaxkonos/proxy/

/var/log/relaxkonos/proxy/

/opt/relaxkonos/proxy/
```

例如：

```text
/etc/relaxkonos/proxy/
    configuration metadata

/var/lib/relaxkonos/proxy/
    profiles
    subscriptions
    runtime state
    backups

/var/log/relaxkonos/proxy/
    logs

/opt/relaxkonos/proxy/engines/
    mihomo binary
```

具体目录应通过已有 RelaxKonOS PlatformPaths abstraction 实现。

不得在业务代码中散布绝对路径。

---

# 19. Engine 抽象

创建：

```csharp
public interface IProxyEngine
{
    string EngineId { get; }

    string DisplayName { get; }

    Task<ProxyEngineCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    Task<ProxyRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken);

    Task ValidateConfigurationAsync(
        string configPath,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProxyGroup>> GetGroupsAsync(
        CancellationToken cancellationToken);

    Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProxyConnection>> GetConnectionsAsync(
        CancellationToken cancellationToken);

    Task CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken);
}
```

生命周期安装不要完全塞进 `IProxyEngine`。

建议单独：

```csharp
IProxyRuntimeManager
```

因为：

```text
Engine API
```

和：

```text
binary lifecycle
```

是两个领域。

---

# 20. Runtime Manager

```csharp
public interface IProxyRuntimeManager
{
    Task<ProxyRuntimeInfo> GetInstalledRuntimeAsync(
        string engineId,
        CancellationToken cancellationToken);

    Task InstallAsync(...);

    Task UpdateAsync(...);

    Task RollbackAsync(...);

    Task UninstallAsync(...);

    Task VerifyIntegrityAsync(...);
}
```

必须支持：

```text
Managed Runtime
External Runtime
```

---

# 21. Managed Runtime

Managed Runtime 表示：

```text
RelaxKonOS download
RelaxKonOS verify
RelaxKonOS install
RelaxKonOS update
RelaxKonOS rollback
RelaxKonOS remove
```

RelaxKonOS 必须保存：

```text
engine
version
architecture
download source
checksum
install time
previous version
```

---

# 22. External Runtime

允许管理员指定：

```text
mihomo
```

已由操作系统安装。

例如 Linux：

```text
/usr/bin/mihomo
```

RelaxKonOS 只进行：

```text
detect
validate
control
```

不得擅自：

```text
overwrite
upgrade
uninstall
```

External Runtime UI：

```text
Runtime

Type
External

Path
/usr/bin/mihomo

Version
1.x.x

Managed updates
Unavailable
```

---

# 23. Runtime 下载安全

禁止：

```text
download latest
execute immediately
```

Managed Runtime 必须：

```text
Download
 ↓
Verify expected artifact
 ↓
Verify checksum
 ↓
Write versioned directory
 ↓
Validate executable
 ↓
Atomic switch current runtime
```

失败：

```text
keep current runtime
```

更新必须支持 rollback。

---

# 24. 不修改 Windows Defender

RelaxKonOS 不得为了 Mihomo：

```text
disable Defender
add global Defender exclusion
disable SmartScreen
```

如果操作系统阻止某个二进制：

RelaxKonOS 应显示诊断信息。

不能偷偷修改安全策略。

---

# 25. Mihomo Controller

Controller 必须默认为 local-only。

建议：

```yaml
external-controller: 127.0.0.1:9090
secret: "<generated>"
```

secret：

```text
Server only
```

不得返回 Client。

REST 调用：

```text
Avalonia
    ↓
RelaxKonOS
    ↓
Mihomo
```

RelaxKonOS Server 提供自己的 DTO。

不得直接：

```text
return mihomo json
```

给 Client。

---

# 26. Controller Secret

Secret：

```text
randomly generated
minimum sufficient entropy
protected on disk
never logged
never returned through normal API
```

例如：

```csharp
ISecretStore
```

管理。

Client UI 永远不需要展示。

---

# 27. TUN 网络安全是最高优先级

远程服务器最大的风险不是代理失败。

而是：

```text
启用 TUN
    ↓
route changed
    ↓
RelaxKonOS connection enters proxy
    ↓
proxy unavailable
    ↓
RelaxKonOS disconnected
    ↓
server unreachable
```

因此必须实现：

```text
Management Traffic Protection
```

作为 TUN 的核心基础设施。

---

# 28. Management Traffic Protection

默认必须保护：

```text
Loopback

RelaxKonOS Server listening endpoints

active RelaxKonOS client source

default gateway

local LAN

configured management network

SSH management path

RDP management path
```

具体哪些流量可安全旁路需要由平台层计算。

不要仅靠 UI 写几个固定 CIDR。

---

# 29. Active Session Protection

Server 应知道当前 RelaxKonOS Session：

```text
RemoteAddress
LocalAddress
LocalPort
Protocol
```

启用 TUN 前生成：

```text
ManagementRouteSnapshot
```

至少保存：

```csharp
public sealed record ManagementRouteSnapshot
{
    public IPAddress ClientAddress { get; init; }

    public IPAddress ServerAddress { get; init; }

    public IPAddress? Gateway { get; init; }

    public string? InterfaceId { get; init; }

    public DateTimeOffset CapturedAt { get; init; }
}
```

TUN activation plan 应验证：

```text
current management route will remain reachable
```

---

# 30. 不可删除的 System Bypass

规则层可以展示：

```text
System Rules
```

例如：

```text
RelaxKonOS Management
DIRECT

Loopback
DIRECT

LAN
DIRECT
```

这些规则必须：

```text
non-editable by default
```

高级管理员可以关闭保护，但必须：

```text
explicit warning
explicit privilege
audit event
```

第一版甚至可以完全不允许关闭。

安全优先。

---

# 31. TUN 启动流程

禁止：

```text
change route
start proxy
```

正确顺序：

```text
Validate profile
 ↓
Validate runtime
 ↓
Check service
 ↓
Check controller availability
 ↓
Resolve outbound interface
 ↓
Capture network snapshot
 ↓
Generate protected TUN config
 ↓
Persist recovery marker
 ↓
Start / Reload Mihomo
 ↓
Wait for controller
 ↓
Wait for TUN
 ↓
Verify proxy outbound
 ↓
Verify RelaxKonOS management connectivity
 ↓
Mark TUN Active
```

---

# 32. Recovery Marker

在改变网络配置之前写：

```text
proxy-network-recovery.json
```

至少包含：

```text
previous runtime state
previous profile
previous route information
previous DNS information
activation timestamp
operation id
```

如果 RelaxKonOS.Server 在启动时检测：

```text
unfinished activation marker
```

必须进入：

```text
Recovery evaluation
```

不得直接忽略。

---

# 33. 自动恢复

以下情况考虑执行自动 rollback：

```text
Mihomo fails to start

Controller unavailable

TUN interface unavailable

default route invalid

health check fails

management route validation fails
```

流程：

```text
Stop proxy
 ↓
Restore previous config
 ↓
Restore network
 ↓
Start previous state
 ↓
Verify
```

---

# 34. Emergency Disable

必须提供独立操作：

```text
Emergency Disable TUN
```

作用：

```text
Disable Mihomo TUN
Restore networking
Restore DNS
Restore safe route
Keep profiles intact
```

该操作不等价于：

```text
Uninstall Mihomo
```

API：

```text
POST /api/proxy/recovery/disable-tun
```

需要：

```text
proxy.recovery.execute
```

权限。

---

# 35. Server 启动自检

RelaxKonOS.Server 启动时检查：

```text
Mihomo runtime running?
Controller reachable?
TUN expected?
TUN exists?
configuration state consistent?
unfinished operation?
recovery marker?
runtime version?
```

产生：

```csharp
ProxyHealthState
```

---

# 36. 状态模型

禁止只使用：

```text
Running
Stopped
```

使用：

```csharp
public enum ProxyRuntimeState
{
    NotInstalled,

    Installing,

    Stopped,

    Starting,

    Running,

    Reloading,

    Stopping,

    Updating,

    Recovering,

    Degraded,

    Failed
}
```

TUN 独立：

```csharp
public enum ProxyTunState
{
    Disabled,

    Enabling,

    Enabled,

    Disabling,

    Recovering,

    Failed
}
```

这样允许：

```text
Mihomo Running
TUN Failed
```

而不是错误地把整个 Engine 标记为 stopped。

---

# 37. Health 状态

定义：

```csharp
public sealed record ProxyHealth
{
    public ProxyRuntimeState RuntimeState { get; init; }

    public ProxyTunState TunState { get; init; }

    public bool ControllerReachable { get; init; }

    public bool NetworkReachable { get; init; }

    public bool ManagementRouteSafe { get; init; }

    public string? ErrorCode { get; init; }
}
```

---

# 38. 错误必须稳定编码

API 不应只返回：

```text
"Failed"
```

例如：

```text
PROXY_RUNTIME_NOT_INSTALLED

PROXY_CONFIG_INVALID

PROXY_SERVICE_START_FAILED

PROXY_CONTROLLER_UNAVAILABLE

PROXY_TUN_CREATE_FAILED

PROXY_TUN_PERMISSION_REQUIRED

PROXY_ROUTE_CONFLICT

PROXY_DNS_CONFIGURATION_FAILED

PROXY_MANAGEMENT_ROUTE_UNSAFE

PROXY_RUNTIME_INTEGRITY_FAILED

PROXY_RECOVERY_REQUIRED
```

Avalonia 根据 ErrorCode 国际化。

Server message 主要用于诊断。

---

# 39. API 设计

建议前缀：

```text
/api/proxy
```

---

# 40. Overview API

```http
GET /api/proxy/status
```

返回：

```json
{
  "engine": "mihomo",
  "runtimeState": "Running",
  "tunState": "Enabled",
  "profile": "Default",
  "mode": "Rule",
  "version": "...",
  "uploadBytesPerSecond": 0,
  "downloadBytesPerSecond": 0,
  "activeConnections": 0
}
```

---

# 41. Runtime API

```text
GET    /api/proxy/runtime

POST   /api/proxy/runtime/install

POST   /api/proxy/runtime/update

POST   /api/proxy/runtime/rollback

DELETE /api/proxy/runtime
```

Runtime mutations：

```text
proxy.runtime.manage
```

---

# 42. Lifecycle API

```text
POST /api/proxy/start

POST /api/proxy/stop

POST /api/proxy/restart
```

---

# 43. TUN API

```text
GET  /api/proxy/tun

POST /api/proxy/tun/enable

POST /api/proxy/tun/disable
```

Enable 请求建议：

```json
{
  "profileId": "...",
  "managementProtection": true
}
```

第一版：

```text
managementProtection
```

应始终强制为 true。

字段只是为未来扩展保留时，也不要允许客户端关闭。

---

# 44. Profiles API

```text
GET    /api/proxy/profiles

GET    /api/proxy/profiles/{id}

POST   /api/proxy/profiles

PUT    /api/proxy/profiles/{id}

DELETE /api/proxy/profiles/{id}

POST   /api/proxy/profiles/{id}/activate

POST   /api/proxy/profiles/{id}/validate
```

---

# 45. Groups / Nodes

```text
GET /api/proxy/groups
```

返回：

```csharp
ProxyGroupDto
{
    Name
    Type
    Selected
    Proxies[]
}
```

切换：

```text
POST /api/proxy/groups/{group}/select
```

Body：

```json
{
  "proxy": "..."
}
```

---

# 46. Connections

```text
GET    /api/proxy/connections

DELETE /api/proxy/connections/{id}

DELETE /api/proxy/connections
```

Avalonia 应支持实时刷新。

如果 RelaxKonOS 已有 WebSocket / streaming infrastructure：

复用。

不要专门建立另一套 socket framework。

---

# 47. Logs

```text
GET /api/proxy/logs
```

实时日志：

优先复用 RelaxKonOS 已存在的：

```text
event stream
websocket
streaming API
```

禁止建立：

```text
ProxyWebSocketManager
```

如果 RelaxKonOS 已有统一 event transport。

---

# 48. 日志脱敏

以下内容禁止进入普通日志：

```text
subscription URL token

Authorization

Mihomo controller secret

proxy password

UUID

private key

WireGuard private key

authentication header
```

定义统一：

```text
ProxyLogSanitizer
```

所有：

```text
Mihomo logs
HTTP logs
audit detail
exception detail
```

通过 sanitizer。

---

# 49. Subscription

Subscription 是敏感数据。

模型：

```csharp
public sealed record ProxySubscription
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public SecretReference UrlSecret { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public TimeSpan? UpdateInterval { get; init; }

    public bool Enabled { get; init; }
}
```

API 不返回原 URL。

返回：

```text
https://example.com/********
```

或者：

```text
Configured
```

---

# 50. Subscription 更新流程

```text
Download
 ↓
Validate HTTP response
 ↓
Parse / identify config
 ↓
Validate Mihomo configuration
 ↓
Backup current profile
 ↓
Commit
 ↓
Reload
 ↓
Health check
 ↓
Rollback on failure
```

禁止下载后直接覆盖当前配置。

---

# 51. DNS

TUN 模式必须把 DNS 作为独立状态显示。

Avalonia：

```text
DNS

Status
Enabled

Mode
Fake IP / Redir Host / ...

Hijack
Enabled

Listen
...

Cache
...
```

第一版不需要完整实现所有 Mihomo DNS 参数编辑。

采用：

```text
Common Settings
+
Advanced Raw Config
```

---

# 52. Windows 特殊实现

创建：

```text
IProxyPlatformService
```

Windows：

```text
WindowsProxyPlatformService
```

负责：

```text
Windows Service integration

network adapter inspection

route snapshot

DNS snapshot

management route validation

TUN diagnostics

firewall diagnostics
```

不要把：

```text
sc.exe
netsh
PowerShell
```

散落在业务层。

---

# 53. Linux 特殊实现

Linux：

```text
LinuxProxyPlatformService
```

负责：

```text
systemd

ip route

ip rule

nftables / iptables diagnostics

DNS environment

network interface detection

TUN availability
```

Mihomo 当前 `auto-redirect` 仅 Linux 可用，并且依赖 `auto-route`。citeturn938106search3turn938106search4

UI 只有在 Server capability 返回：

```text
SupportsAutoRedirect = true
```

才显示。

---

# 54. Linux TUN 检测

检查：

```text
/dev/net/tun
```

存在性。

必要时验证系统是否允许创建 TUN。

容器环境应返回：

```text
Unsupported
```

或：

```text
PrivilegeMissing
```

而不是简单显示：

```text
Failed
```

---

# 55. Windows TUN 诊断

Server 至少能够区分：

```text
runtime missing

service permission failure

TUN adapter creation failure

firewall interference

route failure
```

Mihomo 官方当前文档对 Windows TUN 防火墙场景也明确要求确保核心程序允许通过防火墙。citeturn938106search3

RelaxKonOS 可以提供诊断提示。

不得未经明确设计自动：

```text
disable firewall
```

---

# 56. Firewall 集成边界

Proxy Manager 第一阶段：

```text
detect
diagnose
recommend
```

而不是：

```text
automatically rewrite firewall
```

未来如需要修改防火墙：

必须调用 RelaxKonOS 已存在/未来的 Firewall Manager。

不能：

```text
Proxy Manager
    ↓
直接写 nftables
```

形成另一套 firewall subsystem。

---

# 57. Route 操作边界

同样：

Proxy Manager 可以拥有：

```text
route safety abstraction
```

但不要演化成第二套 Network Manager。

公共网络能力应逐渐下沉：

```text
RelaxKonOS.Network
```

Proxy Manager 调用它。

---

# 58. Avalonia 页面结构

导航：

```text
Network
├── Interfaces
├── Firewall
├── DNS
└── Proxy
```

Proxy 内：

```text
Overview

Profiles

Proxies

Rules

Connections

DNS

Logs

Settings
```

---

# 59. Overview 页面

建议布局：

```text
┌───────────────────────────────────────────────┐
│ Proxy                                         │
│                                               │
│ ● Running                                     │
│                                               │
│ Engine      Mihomo                            │
│ Version     x.x.x                             │
│ Profile     Default                           │
│ Mode        Rule                              │
│                                               │
│ TUN         Enabled                           │
│ DNS         Healthy                           │
│ Route       Protected                         │
│                                               │
│ ↓ 42.8 MB/s        ↑ 5.2 MB/s                 │
│                                               │
│ Connections         128                       │
│                                               │
│ [Stop] [Restart]                              │
└───────────────────────────────────────────────┘
```

---

# 60. TUN 状态卡片

必须单独展示：

```text
TUN

Status
Enabled

Interface
Mihomo

Stack
Mixed

Auto Route
Enabled

Management Protection
Enabled

Outbound Interface
eth0

[Disable TUN]
```

失败时：

```text
TUN
Failed

Management route could not be protected.

[View Details]
[Emergency Restore]
```

---

# 61. 节点页

```text
Proxy Groups

GLOBAL
  Japan-01            42ms
  Japan-02            65ms
  Singapore-01        88ms

AUTO
  Selected: Japan-01
```

功能：

```text
Select
Latency test
Search
Filter
```

不要第一阶段重复实现 MetaCubeXD 所有高级功能。

---

# 62. Connections 页面

字段：

```text
Host

Source

Destination

Network

Rule

Chain

Upload

Download

Start Time
```

支持：

```text
Search
Filter
Close connection
Close all
```

---

# 63. Profile 页面

列表：

```text
Default
Subscription
Custom
```

编辑仍然使用 RelaxKonOS 已有的：

```text
custom desktop modal
```

不能突然使用系统原生对话框。

---

# 64. 创建 Profile Modal

字段：

```text
Name

Source
○ Local Configuration
○ Subscription

Engine
Mihomo
```

如果 local：

```text
configuration editor
```

如果 subscription：

```text
URL
update interval
```

---

# 65. 删除确认

使用 RelaxKonOS modal infrastructure：

```text
Delete Profile?

Default

This operation does not uninstall Mihomo.

[Cancel]
[Delete]
```

如果删除 active profile：

必须阻止或者要求先切换。

不要静默切换。

---

# 66. Settings 页面

建议：

```text
Runtime

Engine
Mihomo

Runtime type
Managed

Version
...

[Check Update]
[Rollback]
```

然后：

```text
Startup

Start proxy with system
ON

Enable TUN automatically
ON
```

然后：

```text
TUN

Stack
Mixed

Auto Route
ON

Auto Detect Interface
ON

Auto Redirect
ON      Linux only
```

然后：

```text
Safety

Management Traffic Protection
ON
Locked
```

---

# 67. Advanced Configuration

必须允许高级用户查看：

```text
Raw Mihomo YAML
```

但保存前：

```text
validate
backup
commit
reload
health check
```

UI：

```text
Advanced Configuration

[Editor]

[Validate]

[Save & Reload]
```

---

# 68. MVVM 结构

例如：

```text
Features/
└── Proxy/
    ├── Views/
    ├── ViewModels/
    ├── Models/
    ├── Services/
    ├── Repositories/
    └── Navigation/
```

不要：

```text
ProxyPage.axaml.cs
3000 lines
```

ViewModel 也不要变成 god object。

建议：

```text
ProxyOverviewViewModel

ProxyProfilesViewModel

ProxyGroupsViewModel

ProxyConnectionsViewModel

ProxyDnsViewModel

ProxyLogsViewModel

ProxySettingsViewModel
```

---

# 69. Client Repository

```csharp
public interface IProxyRepository
{
    Task<ProxyOverview> GetOverviewAsync(...);

    Task StartAsync(...);

    Task StopAsync(...);

    Task RestartAsync(...);

    Task EnableTunAsync(...);

    Task DisableTunAsync(...);

    Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(...);

    ...
}
```

HTTP 实现：

```text
RemoteProxyRepository
```

ViewModel 不直接：

```text
HttpClient.GetAsync(...)
```

---

# 70. Localization

不得在代码中增加：

```csharp
"Enable TUN"
"Proxy failed"
```

作为最终 UI 文本。

遵循 RelaxKonOS 现有资源体系。

例如 key：

```text
proxy.title

proxy.status.running

proxy.status.stopped

proxy.tun.title

proxy.tun.enable

proxy.tun.disable

proxy.tun.managementProtection

proxy.error.routeUnsafe

proxy.recovery.emergencyDisable

proxy.runtime.install
```

---

# 71. Theme

所有界面：

```text
use existing RelaxKonOS theme resources
```

禁止 Proxy Manager：

```text
hard-coded color
hard-coded dark theme
hard-coded Clash color scheme
```

控件使用 RelaxKonOS 已有：

```text
Card
Button
Toggle
StatusBadge
Modal
Tab
DataGrid
```

若不存在，再建立通用组件。

不要建立：

```text
ProxyButton
ProxyCard
```

除非组件确实只适用于代理领域。

---

# 72. 审计

以下操作必须产生 Audit Event：

```text
Install runtime

Update runtime

Rollback runtime

Uninstall runtime

Start proxy

Stop proxy

Enable TUN

Disable TUN

Emergency restore

Change active profile

Modify configuration

Update subscription

Change proxy node

Close connections
```

尤其：

```text
Enable TUN
Disable TUN
Recovery
Runtime install/update
```

属于高价值事件。

---

# 73. Audit 信息

记录：

```text
User

Session

Host

Operation

Engine

Profile

Result

Timestamp

CorrelationId
```

禁止记录：

```text
subscription URL token
controller secret
proxy password
```

---

# 74. Operation / Correlation

长任务：

```text
Install

Update

TUN enable

Recovery
```

应使用统一 operation id。

例如：

```text
OperationId
```

Client 可查询进度。

不要通过 HTTP request 一直挂着几十秒。

如果 RelaxKonOS 已经有 Job/Operation infrastructure：

直接复用。

---

# 75. 安装流程

Managed Mihomo：

```text
User Install
 ↓
Check RelaxKonOS permission
 ↓
Resolve platform / architecture
 ↓
Download artifact
 ↓
Verify
 ↓
Elevation request if required
 ↓
Install versioned runtime
 ↓
Install OS service
 ↓
Create config
 ↓
Start without TUN first
 ↓
Controller health check
 ↓
Mark Installed
```

推荐第一次安装时：

```text
TUN OFF
```

安装成功后再执行独立：

```text
Enable TUN
```

这样容易区分：

```text
runtime problem
```

和：

```text
network problem
```

---

# 76. 启用 TUN 流程

```text
EnableTunCommand

1 authorization

2 validate runtime

3 validate profile

4 validate service

5 collect current network state

6 determine outbound interface

7 calculate management protection

8 validate route plan

9 write recovery marker

10 generate managed config overlay

11 apply configuration

12 start/reload Mihomo

13 wait controller

14 detect TUN interface

15 verify route

16 verify DNS

17 verify outbound network

18 verify RelaxKonOS management path

19 clear activation transaction

20 report Enabled
```

任何步骤失败：

进入：

```text
rollback
```

---

# 77. 禁用 TUN 流程

```text
1 authorization

2 write recovery transaction

3 disable TUN

4 reload Mihomo

5 wait TUN disappearance

6 verify OS routes

7 verify DNS

8 verify management route

9 clear transaction
```

---

# 78. Stop Proxy 与 Disable TUN 区别

必须定义清楚。

```text
Disable TUN
```

可以：

```text
keep Mihomo running
```

用于 Listener 模式。

而：

```text
Stop Proxy
```

代表：

```text
stop the managed Mihomo runtime
```

UI 不要混为同一个 toggle。

---

# 79. Startup

服务器重启后：

Linux 由 systemd 负责 Mihomo 启动；Windows 由 Server 的既有启动策略启动
`RelaxKonOS.Server`，再由其进程宿主恢复 Mihomo。

RelaxKonOS Server 启动之后：

```text
discover current state
```

而不是假设：

```text
RelaxKonOS stopped
=> Mihomo stopped
```

这是重要状态恢复原则。

---

# 80. Fail-safe Startup

如果配置显示：

```text
TUN expected enabled
```

但是 RelaxKonOS 检测：

```text
recovery marker exists
```

则优先：

```text
Recover
```

不要直接再次启用。

---

# 81. Engine Capability

定义：

```csharp
public sealed record ProxyEngineCapabilities
{
    public bool SupportsTun { get; init; }

    public bool SupportsRules { get; init; }

    public bool SupportsProxyGroups { get; init; }

    public bool SupportsConnections { get; init; }

    public bool SupportsDns { get; init; }

    public bool SupportsSubscriptions { get; init; }

    public bool SupportsReload { get; init; }
}
```

以后 sing-box 接入不允许修改 Avalonia 页面业务判断：

```text
if engine == mihomo
```

应该：

```text
if capability.SupportsConnections
```

---

# 82. 禁止 Engine 泄漏

以下类型禁止进入 Client：

```text
MihomoProxyGroupResponse

ClashConnection

MihomoConfig
```

必须转换：

```text
ProxyGroupDto

ProxyConnectionDto

ProxyProfileDto
```

这样第二 Engine 才不会污染 UI。

---

# 83. 第一阶段范围

必须实现：

```text
Proxy built-in app

Mihomo runtime detection

Managed Mihomo runtime

External Mihomo runtime

Install

Uninstall

Start

Stop

Restart

OS service integration

Mihomo controller integration

Status

TUN enable

TUN disable

TUN safety protection

Profile list

Profile activation

Raw YAML editing

Config validation

Proxy groups

Proxy selection

Connections

Logs

Basic DNS status

Recovery

Audit

Permissions
```

---

# 84. 第一阶段非目标

第一阶段不要实现：

```text
sing-box

Xray

full YAML visual editor

rule provider visual designer

proxy provider visual designer

full firewall management

full routing table editor

full DNS server manager

traffic statistics database

multi-host centralized proxy orchestration

automatic proxy purchase

subscription marketplace

MetaCubeXD clone
```

保持范围。

---

# 85. 第二阶段

以后可以增加：

```text
Subscriptions

automatic subscription refresh

latency testing

rule visualization

per-application integration

RelaxKonOS-only proxy

Git proxy

Docker proxy

APT proxy

system proxy integration
```

---

# 86. 第三阶段

实现：

```text
SingBoxEngine
```

用于验证：

```text
IProxyEngine abstraction
```

是否真正成立。

如果接入 sing-box 时需要大规模修改：

```text
Avalonia ViewModels
RelaxKonOS API
domain model
```

说明第一阶段 Engine abstraction 失败。

---

# 87. 单元测试

至少：

```text
ProxyRuntimeManagerTests

ProxyProfileManagerTests

ProxyConfigurationTransactionTests

ProxyRecoveryManagerTests

ProxyPermissionTests

ProxySecretSanitizerTests

MihomoEngineTests

ProxyRoutingProtectionTests
```

---

# 88. 配置事务测试

覆盖：

```text
valid config

invalid config

write failure

reload failure

controller timeout

health check failure

rollback success

rollback failure
```

---

# 89. TUN 安全测试

必须重点测试：

```text
remote client IP preserved

LAN preserved

gateway preserved

management route conflict detected

invalid outbound interface rejected

Mihomo crash during activation

RelaxKonOS.Server crash during activation

machine reboot during activation
```

---

# 90. Windows Integration Tests

覆盖：

```text
Windows Server

install service

remove service

start

stop

restart

TUN creation

route health

restart OS

runtime update

runtime rollback
```

---

# 91. Ubuntu Integration Tests

覆盖：

```text
systemd

headless Ubuntu

/dev/net/tun

TUN startup

auto-route

auto-redirect capability

restart OS

runtime update

runtime rollback
```

---

# 92. 无 UI 环境测试

Proxy Manager Server 功能不得依赖：

```text
desktop session

DISPLAY

DBus desktop session

Windows interactive user session
```

Ubuntu Server 无 GUI：

```text
must work
```

Windows Server 无 GUI：

```text
must work
```

Client 在另一台机器运行即可。

---

# 93. 安全要求

必须做到：

```text
Mihomo controller not exposed publicly

controller secret not returned to client

subscription secret encrypted/protected

logs sanitized

runtime verified

dangerous operations authorized

privileged operations strongly typed

TUN recovery available

management route protected
```

---

# 94. 禁止设计

Codex 不得实现：

## 禁止 1

```csharp
RunAsRoot(string command)
```

## 禁止 2

```text
Client -> Mihomo controller directly
```

## 禁止 3

```text
Mihomo Controller 0.0.0.0
```

默认暴露。

## 禁止 4

```text
Disable Windows Defender
```

## 禁止 5

```text
Disable firewall
```

## 禁止 6

```text
Enable TUN without recovery state
```

## 禁止 7

```text
Enable TUN without management traffic protection
```

## 禁止 8

```text
Store subscription token in normal log
```

## 禁止 9

```text
Hard-code OS commands throughout business layer
```

## 禁止 10

```text
Avalonia directly knows Mihomo JSON
```

---

# 95. Codex 实现方式

Codex 不得一次性重写 RelaxKonOS。

实施必须：

```text
incremental
reviewable
buildable
testable
```

每一个阶段完成后：

```text
build
test
```

再继续。

---

# 96. Codex 开始前检查

Codex 首先必须检查现有：

```text
solution structure

Server service architecture

authorization system

elevation/helper implementation

platform abstraction

service manager

HTTP API conventions

error response conventions

operation/job framework

audit framework

secret storage

localization

theme system

modal system

navigation

repository/service patterns
```

不得假设不存在。

如果已有：

```text
IServiceManager
```

必须复用。

不要创建：

```text
ProxyServiceManagerForWindows
```

重复已有基础设施。

---

# 97. 实现 Phase 0

仅调查，不进行大规模修改。

输出：

```text
docs/applications/RelaxKonOS.ProxyManager.Discovery.md
```

内容：

```text
Relevant projects

Existing abstractions

Reusable services

Permission system

Elevation path

Service management

Platform layer

UI conventions

Required new components

Potential conflicts
```

完成之后开始 Phase 1。

---

# 98. Phase 1：Domain + Contracts

实现：

```text
Proxy domain models

DTO

capabilities

runtime state

TUN state

error codes

interfaces
```

暂时：

```text
no UI
no Mihomo download
```

要求：

```text
build passes
tests pass
```

---

# 99. Phase 2：Mihomo Adapter

实现：

```text
MihomoEngine

Controller client

status

groups

select proxy

connections

logs

config validation
```

Controller：

```text
local only
```

测试 Adapter。

---

# 100. Phase 3：Runtime

实现：

```text
detect external Mihomo

managed runtime directory

install

service install

start

stop

restart

uninstall
```

Windows/Linux 分平台。

---

# 101. Phase 4：Profile

实现：

```text
profile metadata

active profile

config read

config validate

transaction write

backup

rollback
```

---

# 102. Phase 5：TUN

实现：

```text
network snapshot

management protection

recovery marker

enable

disable

health check

rollback

emergency restore
```

这是整个功能风险最高的 Phase。

不要与 UI 同时实现。

先 Server test。

---

# 103. Phase 6：API

实现：

```text
status

runtime

lifecycle

profiles

groups

connections

tun

recovery
```

全部：

```text
authorization protected
```

---

# 104. Phase 7：Avalonia

加入导航：

```text
Network
    Proxy
```

实现：

```text
Overview

Profiles

Proxies

Connections

Logs

Settings
```

遵循已有：

```text
MVVM

Repository

Theme

Localization

Modal
```

---

# 105. Phase 8：Audit + Security

完成：

```text
audit

secret masking

controller security

permission tests

runtime verification
```

---

# 106. Phase 9：Integration Tests

在：

```text
Windows

Ubuntu
```

执行实际 TUN 测试。

特别验证：

```text
启用 TUN 后 RelaxKonOS 当前连接不会断开
```

这是发布的必要条件。

---

# 107. Phase 10：Documentation

增加：

```text
docs/proxy/

architecture.md

mihomo.md

tun.md

recovery.md

security.md
```

用户文档：

```text
安装

启用

配置

TUN

恢复

故障排查
```

---

# 108. 验收条件

只有同时满足以下条件才视为第一阶段完成。

## Runtime

```text
[ ] Windows 可以安装 Mihomo

[ ] Ubuntu 可以安装 Mihomo

[ ] 可以使用 External Runtime

[ ] 可以 Start

[ ] 可以 Stop

[ ] 可以 Restart

[ ] 可以 Update

[ ] Update 失败不会破坏现有版本
```

## TUN

```text
[ ] Windows TUN 可用

[ ] Ubuntu TUN 可用

[ ] Enable TUN 有权限检查

[ ] Disable TUN 正常

[ ] management protection 生效

[ ] TUN 失败能够 rollback

[ ] 有 Emergency Restore
```

## Remote Management

```text
[ ] 开启 TUN 后 RelaxKonOS 当前连接保持可用

[ ] Client 不直接访问 Mihomo

[ ] Controller 不暴露公网

[ ] Controller secret 不返回 Client
```

## Profiles

```text
[ ] 创建

[ ] 编辑

[ ] Validate

[ ] Activate

[ ] Delete

[ ] Rollback
```

## Proxy

```text
[ ] 查看 groups

[ ] 查看 nodes

[ ] 选择 node

[ ] 查看 connections

[ ] close connection

[ ] 查看 logs
```

## Security

```text
[ ] subscription secret protected

[ ] logs sanitized

[ ] Runtime verified

[ ] privileged API strongly typed

[ ] authorization tests pass
```

## UI

```text
[ ] Avalonia MVVM

[ ] Existing RelaxKonOS Theme

[ ] Existing localization

[ ] Existing modal system

[ ] No Mihomo-specific DTO leaks into UI
```

---

# 109. Definition of Done

Proxy Manager 的第一阶段最终体验应为：

```text
RelaxKonOS
    ↓
Network
    ↓
Proxy
```

管理员可以：

```text
Install Mihomo

Choose profile

Start proxy

Enable TUN

Select proxy node

Inspect connections

Inspect logs

Restart

Disable TUN

Recover network
```

在：

```text
Windows
Ubuntu
```

均可完成。

服务器本身不要求存在 GUI。

---

# 110. 最终架构目标

最终关系必须保持：

```text
RelaxKonOS Avalonia
        │
        │ RelaxKonOS API
        ▼
RelaxKonOS.Server
        │
        ├── Authorization
        ├── Audit
        ├── Proxy Domain
        ├── Runtime Management
        ├── Network Safety
        └── Platform Abstraction
                 │
                 ▼
            IProxyEngine
                 │
                 ▼
              Mihomo
                 │
                 ▼
                TUN
                 │
                 ▼
          Operating System
```

而权限提升关系是：

```text
RelaxKonOS.Server
       │
       │ explicit privileged operation
       ▼
RelaxKonOS privileged subsystem/helper
       │
       ├── install service
       ├── remove service
       ├── protected runtime update
       └── emergency network recovery
```

而不是：

```text
RelaxKonOS.Helper
       │
       └── everything requiring admin forever
```

Helper 应继续保持：

```text
small
auditable
strongly typed
least privilege
```

Proxy Manager 的业务逻辑、Mihomo Controller 操作、Profile 管理、节点选择、Connections 和普通配置逻辑全部留在：

```text
RelaxKonOS.Server
```

而不是 Helper。

---

# 111. Codex 最重要的执行规则

> 不要为了快速实现代理功能破坏 RelaxKonOS 已有架构。

优先：

```text
reuse
extend
abstract
test
```

而不是：

```text
duplicate
special-case
hard-code
```

任何需要：

```text
root
administrator
LocalSystem
```

权限的功能，都必须首先判断：

```text
是否真的需要 privilege？
```

如果不需要：

```text
keep it in RelaxKonOS.Server
```

如果需要：

```text
use existing elevation architecture
```

不得扩大 Helper 权限范围来换取开发便利。

最终设计应允许以后添加：

```text
SingBoxEngine
```

时仍保持：

```text
RelaxKonOS Avalonia
RelaxKonOS API
Proxy domain model
```

基本不变。

---

# 112. Codex 首条执行指令

将本文档放入仓库后，可以向 Codex 下达：

```text
Read this Proxy Manager implementation specification completely.

Then inspect the existing RelaxKonOS repository and its architecture before
making changes.

Pay particular attention to the existing permission/elevation system,
service management abstractions, platform abstractions, Avalonia MVVM
patterns, theme/localization infrastructure, modal infrastructure,
API conventions, audit infrastructure and operation/job infrastructure.

Do not create parallel implementations when equivalent RelaxKonOS
infrastructure already exists.

Start with Phase 0 only.

Create `docs/applications/RelaxKonOS.ProxyManager.Discovery.md` describing how this specification
maps onto the actual repository.

Do not begin the full implementation until the discovery document is
complete.

After discovery, implement the phases incrementally. Keep every phase
buildable and testable.

Mihomo is the first engine, but no UI or domain contract may depend directly
on Mihomo-specific response types.

TUN is a primary feature. Remote management connectivity protection and
network rollback are mandatory requirements, not optional enhancements.

Do not introduce a generic privileged command executor.
```
