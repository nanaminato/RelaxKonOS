# RemoteOS WebServerManager / Nginx 集成设计

> 状态：**Nginx Provider V2 已实现**。除发现、配置测试、集成、受管安装和生命周期管理外，已支持 RemoteOS 自有的反向代理与静态站点：创建、修改、删除都会先执行 `nginx -t`，再重载或回滚。站点可绑定证书管理器中已有的证书；私钥仍只保留在服务器本地。证书的签发与自动续期后触发重载仍可在后续阶段进一步自动化。

## 1. 设计背景

RemoteOS 是一个仅管理当前主机的服务器管理程序，后端采用 .NET 10，支持 Windows 和 Linux。

在证书管理器设计中，已经确定以下原则：

- 证书签发与证书部署分离。
- RemoteOS 自身作为 ACME Client。
- Nginx / IIS / Apache 只作为证书部署和 HTTP-01 暴露路径的可选集成对象。
- 不要求 RemoteOS 自己长期占用 TCP 80。
- 不应把 Nginx 作为 RemoteOS 的强依赖。

因此，Web Server 管理模块应设计为一个通用的 Web Server 抽象层，其中 Nginx 只是第一个完整实现的 Provider。

---

## 2. 核心设计目标

### 2.1 不强依赖 Nginx

RemoteOS 的核心功能必须可以在以下场景正常工作：

- 没有安装 Nginx。
- 只有 IIS。
- 只有 Apache。
- 用户自行维护现有 Nginx。
- RemoteOS 仅使用 Kestrel 提供 HTTPS。
- 后续接入 Caddy、OpenResty 等其他 Web Server。

因此，不建议把核心架构命名或设计为：

```text
RemoteOS
   ↓
NginxManager
   ↓
所有 Web 功能
```

推荐：

```text
RemoteOS
   ↓
WebServerManager
   ↓
IWebServerProvider
   ├── Nginx
   ├── IIS
   ├── Apache
   └── ...
```

当前实现对应关系：

```text
HTTP endpoints / Certificate modules
              ↓
IWebServerManager
              ↓
WebServerManager                 ← 产品无关的发现聚合与实例路由
              ↓
IWebServerProvider
              └── NginxWebServerManager  ← Nginx 的探测、配置测试、集成和重载
```

因此，**Web Server 管理器是入口和调度层，Nginx 管理器是其中一个 Provider**。其他模块只依赖前者；新增 IIS、Apache 或 Caddy 时，注册新的 Provider 即可，不需要改变 API 调用方。

---

## 3. 总体模块结构

推荐结构：

```text
RemoteOS.Server
│
├── WebServer
│   │
│   ├── IWebServerManager.cs
│   ├── WebServerManager.cs
│   │
│   ├── Models
│   │   ├── WebServerInstance.cs
│   │   ├── WebServerCandidate.cs
│   │   ├── WebServerCapabilities.cs
│   │   └── WebServerManagementMode.cs
│   │
│   ├── Providers
│   │   ├── Nginx
│   │   │   ├── NginxWebServerProvider.cs
│   │   │   ├── NginxDetector.cs
│   │   │   ├── NginxRuntime.cs
│   │   │   ├── NginxConfigurationManager.cs
│   │   │   ├── NginxInstaller.cs
│   │   │   ├── NginxSiteProvider.cs
│   │   │   ├── NginxConfigRenderer.cs
│   │   │   └── NginxCertificateDeployer.cs
│   │   │
│   │   ├── IIS
│   │   └── Apache
│   │
│   └── Deployment
│       └── CertificateDeploymentCoordinator.cs
│
├── Certificate
│   ├── CertificateManager.cs
│   ├── Acme
│   ├── Challenges
│   ├── Storage
│   └── Renewal
│
└── Platform
    ├── Windows
    └── Linux
```

---

## 4. Web Server 的三种管理模式

建议不要仅使用“已安装 / 未安装”区分状态，而是增加管理模式：

```csharp
public enum WebServerManagementMode
{
    External,
    Integrated,
    Managed
}
```

### 4.1 External

RemoteOS 发现 Web Server，但不修改它。

RemoteOS 可以：

- 检测进程。
- 显示版本。
- 显示配置路径。
- 显示运行状态。
- 读取有限信息。

RemoteOS 不可以：

- 修改配置。
- 升级。
- 卸载。
- 自动创建站点。
- 自动写入 HTTPS 配置。

典型场景：

```text
/usr/sbin/nginx
/etc/nginx/nginx.conf

状态：Running
模式：External
```

---

### 4.2 Integrated

RemoteOS 与现有 Web Server 集成，但不拥有它。

典型原则：

- 不负责安装。
- 不负责升级。
- 不负责卸载。
- 不全面接管主配置。
- 只管理 RemoteOS 自己创建的配置片段。

例如：

```text
/etc/nginx/
├── nginx.conf
├── conf.d/
│
└── remoteos.d/
    ├── remoteos.conf
    ├── acme.conf
    └── sites/
```

RemoteOS 只拥有：

```text
/etc/nginx/remoteos.d/*
```

这是推荐的默认集成模式。

---

### 4.3 Managed

Nginx 由 RemoteOS 安装并完整管理。

支持：

- Install
- Upgrade
- Start
- Stop
- Restart
- Reload
- Config
- Site
- HTTPS
- Uninstall

Windows 示例：

```text
C:\ProgramData\RemoteOS\
└── webserver\
    └── nginx\
        ├── nginx.exe
        ├── conf\
        ├── logs\
        └── ...
```

---

## 5. Provider 架构

不建议让其他模块直接依赖 `INginxManager`。

推荐通用 Provider：

```csharp
public interface IWebServerProvider
{
    string ProviderId { get; }

    WebServerType Type { get; }

    Task<WebServerDetectionResult> DetectAsync(
        CancellationToken cancellationToken);

    Task<WebServerRuntimeStatus> GetStatusAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<TestConfigurationResult> TestConfigurationAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

Nginx：

```csharp
public sealed class NginxWebServerProvider
    : IWebServerProvider
{
}
```

未来：

```text
IISWebServerProvider
ApacheWebServerProvider
CaddyWebServerProvider
```

---

## 6. 能力接口拆分

不要把所有能力都塞到 `IWebServerProvider`。

建议按能力拆分。

### 6.1 生命周期

```csharp
public interface IWebServerLifecycle
{
    Task StartAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task StopAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task RestartAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

### 6.2 安装

```csharp
public interface IWebServerInstaller
{
    Task<WebServerInstallResult> InstallAsync(
        WebServerInstallOptions options,
        CancellationToken cancellationToken);

    Task UpgradeAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task UninstallAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

### 6.3 配置

```csharp
public interface IWebServerConfiguration
{
    Task<TestConfigurationResult> TestAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<WebServerConfigSnapshot> BackupAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        WebServerInstance instance,
        WebServerConfigSnapshot snapshot,
        CancellationToken cancellationToken);
}
```

### 6.4 站点

```csharp
public interface IWebSiteProvider
{
    Task<IReadOnlyList<WebSiteInfo>> ListAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<WebSiteInfo> CreateAsync(
        WebServerInstance instance,
        WebSiteDefinition site,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        WebServerInstance instance,
        WebSiteDefinition site,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        WebServerInstance instance,
        Guid siteId,
        CancellationToken cancellationToken);
}
```

---

## 7. WebServerInstance 与 Provider 分离

Provider 表示一种实现能力。

Instance 表示当前机器上实际存在的 Web Server 实例。

```csharp
public enum WebServerType
{
    Nginx,
    OpenResty,
    IIS,
    Apache,
    Caddy,
    Unknown
}
```

```csharp
public sealed record WebServerInstance
{
    public required Guid Id { get; init; }

    public required WebServerType Type { get; init; }

    public required string ProviderId { get; init; }

    public required WebServerManagementMode ManagementMode { get; init; }

    public required string ExecutablePath { get; init; }

    public string? MainConfigPath { get; init; }

    public string? ConfigDirectory { get; init; }

    public string? Version { get; init; }

    public bool IsRemoteOsManaged { get; init; }
}
```

不要把系统限制为单个 Nginx。

同一主机理论上可能同时存在：

```text
/usr/sbin/nginx
/usr/local/openresty/nginx/sbin/nginx
C:\ProgramData\RemoteOS\webserver\nginx\nginx.exe
```

内部推荐统一使用：

```csharp
IReadOnlyList<WebServerInstance>
```

即使 V1 UI 只选择一个活动实例。

---

## 8. NginxDetector

建议定义：

```csharp
public interface IWebServerDetector
{
    Task<IReadOnlyList<WebServerCandidate>> DetectAsync(
        CancellationToken cancellationToken);
}
```

Nginx 检测来源：

1. PATH 中的 nginx。
2. 常见 Linux 安装路径。
3. systemd nginx.service。
4. systemd openresty.service。
5. Windows PATH。
6. RemoteOS Managed Nginx。
7. 已运行的 nginx 进程。

Linux 常见路径：

```text
/usr/sbin/nginx
/usr/local/nginx/sbin/nginx
/usr/local/openresty/nginx/sbin/nginx
```

Windows 可检测：

```text
C:\nginx\nginx.exe
C:\Program Files\nginx\nginx.exe
C:\ProgramData\RemoteOS\webserver\nginx\nginx.exe
```

最终应执行类似：

```text
nginx -V
```

以确认：

- 是否为 nginx。
- 版本。
- prefix。
- conf-path。
- modules-path。
- 编译参数。

---

## 9. 不自动接管已有 Nginx

RemoteOS 首次发现已有 nginx 时，不应该自动修改配置。

推荐流程：

```text
Detected
   ↓
用户选择
   ├── 忽略
   ├── 仅监控
   └── 启用集成
```

只有用户明确同意，才进入：

```text
Integrated
```

只有用户明确选择安装 RemoteOS 管理版本，才进入：

```text
Managed
```

---

## 10. 配置 Ownership

建议给配置引入 Ownership：

```csharp
public enum ConfigOwnership
{
    External,
    Shared,
    RemoteOs
}
```

规则：

### External

```text
只读
```

例如：

```text
/etc/nginx/nginx.conf
/etc/nginx/conf.d/foo.conf
```

### Shared

```text
可以修改，但必须备份并验证
```

### RemoteOs

```text
RemoteOS 可以完全管理
```

例如：

```text
/etc/nginx/remoteos.d/*
```

这样可以避免删除站点、卸载 RemoteOS、配置恢复时误伤用户配置。

---

## 11. 配置目录策略

对于现有 Nginx，建议只做一次最小侵入式修改：

```nginx
include /etc/nginx/remoteos.d/*.conf;
include /etc/nginx/remoteos.d/sites/*.conf;
```

之后所有 RemoteOS 配置都进入：

```text
/etc/nginx/remoteos.d/
```

不要持续修改：

```text
/etc/nginx/nginx.conf
```

对于 RemoteOS Managed Nginx，则可以完全拥有配置结构。

---

## 12. 配置修改必须事务化

不要采用：

```text
写配置
↓
reload
```

推荐：

```text
生成配置
    ↓
Backup
    ↓
写临时文件
    ↓
nginx -t
    ↓
成功？
 ┌──┴───┐
Yes     No
 │       │
Commit  Rollback
 │
reload
 │
成功？
 ┌──┴───┐
Yes     No
 │       │
Finish  Rollback
```

推荐抽象：

```csharp
public interface IConfigTransaction
{
    Task<ConfigTransactionResult> ExecuteAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken);
}
```

Nginx 典型过程：

1. Snapshot
2. Mutate
3. `nginx -t`
4. Commit
5. Reload
6. 失败时回滚

---

## 13. Nginx 内部拆分

推荐：

```text
Nginx/
│
├── NginxWebServerProvider.cs
│
├── Detection/
│   └── NginxDetector.cs
│
├── Runtime/
│   ├── INginxRuntime.cs
│   ├── LinuxNginxRuntime.cs
│   └── WindowsNginxRuntime.cs
│
├── Installation/
│   ├── NginxInstaller.cs
│   ├── LinuxNginxInstaller.cs
│   └── WindowsNginxInstaller.cs
│
├── Configuration/
│   ├── NginxConfigurationManager.cs
│   ├── NginxConfigValidator.cs
│   ├── NginxConfigTransaction.cs
│   └── NginxConfigRenderer.cs
│
├── Sites/
│   └── NginxSiteProvider.cs
│
└── Certificates/
    └── NginxCertificateDeployer.cs
```

`NginxWebServerProvider` 作为 facade，不承载全部细节。

---

## 14. Windows / Linux 差异

核心逻辑不要到处写：

```csharp
if (OperatingSystem.IsWindows())
{
    ...
}
else
{
    ...
}
```

建议通过平台实现隔离差异。

### Linux

可能使用：

```text
systemctl start nginx
systemctl stop nginx
systemctl reload nginx
```

或者原生命令：

```text
nginx
nginx -s quit
nginx -s reload
```

### Windows

对于 RemoteOS Managed Nginx，可以由 RemoteOS Service 负责 nginx 进程生命周期。

```text
RemoteOS Service
    ↓
nginx.exe
```

Windows 官方 nginx 本身不需要成为 RemoteOS 的核心系统服务依赖。

---

## 15. 与 CertificateManager 的关系

不要设计：

```text
CertificateManager
    ↓
NginxManager
```

推荐：

```text
CertificateManager
       │
       │ CertificateIssuedEvent
       ▼
CertificateDeploymentCoordinator
       │
       ├── KestrelCertificateDeployer
       ├── NginxCertificateDeployer
       ├── IISCertificateDeployer
       └── ApacheCertificateDeployer
```

定义：

```csharp
public interface ICertificateDeployer
{
    string TargetType { get; }

    Task<CertificateDeploymentResult> DeployAsync(
        ManagedCertificate certificate,
        CertificateDeploymentTarget target,
        CancellationToken cancellationToken);
}
```

这样 CertificateManager 不知道 Nginx 的存在。

---

## 16. Certificate 与 Deployment 分离

不要在 CertificateRecord 中加入：

```text
NginxSiteId
NginxConfigPath
```

推荐单独建立：

```text
CertificateDeploymentRecord
```

例如：

```text
Id
CertificateId

TargetType
TargetInstanceId
TargetResourceId

Status
LastDeployedAt
LastError
```

这样一张证书可以同时部署到：

```text
Certificate
   ├── RemoteOS Kestrel
   └── Nginx
```

---

## 17. HTTP-01 与 Nginx 的关联

证书管理器继续保持：

```text
IHttp01ChallengeProvider
├── DirectHttp01ChallengeProvider
└── WebRootHttp01ChallengeProvider
```

WebRoot 只负责写 challenge 文件。

例如：

```text
Linux:
  /var/lib/remoteos/acme-challenge/

Windows:
  C:\ProgramData\RemoteOS\acme-challenge\
```

然后额外增加：

```csharp
public interface IHttp01WebServerIntegrator
{
    Task<Http01IntegrationResult> EnsureConfiguredAsync(
        WebServerInstance instance,
        string domain,
        string webRootPath,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        WebServerInstance instance,
        string domain,
        CancellationToken cancellationToken);
}
```

Nginx 实现仅负责暴露：

```nginx
location /.well-known/acme-challenge/ {
    root /var/lib/remoteos;
}
```

核心关系：

```text
Certificate
     │
     ▼
IHttp01ChallengeProvider
     │
     ▼
challenge file
```

以及：

```text
WebServer
     │
     ▼
IHttp01WebServerIntegrator
     │
     ▼
Nginx / IIS / Apache
```

两者由 Coordinator 协调，不直接相互依赖。

---

## 18. 站点模型

不要使用：

```text
NginxSite
```

推荐统一：

```csharp
public sealed record WebSiteDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> Domains { get; init; }

    public required IReadOnlyList<WebSiteBinding> Bindings { get; init; }

    public WebSiteTarget? Target { get; init; }

    public Guid? CertificateId { get; init; }
}
```

反向代理：

```csharp
public sealed record ReverseProxyTarget
{
    public required Uri Address { get; init; }

    public bool WebSocket { get; init; }
}
```

NginxProvider 负责把通用模型 Render 为 nginx 配置。

未来 IISProvider 可以将同一模型转换为 IIS Binding / Rewrite 配置。

---

## 19. WebServerCapabilities

为了避免前端判断：

```csharp
if (server.Type == WebServerType.Nginx)
{
    ...
}
```

推荐：

```csharp
[Flags]
public enum WebServerCapabilities
{
    None                = 0,

    Lifecycle           = 1 << 0,
    Reload              = 1 << 1,
    Configuration       = 1 << 2,
    Sites               = 1 << 3,
    ReverseProxy        = 1 << 4,
    Https               = 1 << 5,
    Http01Integration   = 1 << 6,
    Logs                = 1 << 7,
    Install             = 1 << 8,
    Upgrade             = 1 << 9,
    Metrics             = 1 << 10
}
```

例如：

```text
External Nginx
──────────────
Configuration      ✓
Reload             ✓
Sites              ✓
Install            ✗
Upgrade            ✗
Uninstall          ✗

Managed Nginx
──────────────
Configuration      ✓
Reload             ✓
Sites              ✓
Install            ✓
Upgrade            ✓
Uninstall          ✓
```

---

## 20. 建议的数据模型

### 20.1 WebServerInstanceRecord

```text
Id
ProviderId
Type
DisplayName

ExecutablePath
MainConfigPath
ConfigDirectory

Version

ManagementMode
Status

CreatedAt
UpdatedAt
```

### 20.2 WebSiteRecord

```text
Id
Name

WebServerInstanceId

Type
Domains
Enabled

ManagedByRemoteOS

CreatedAt
UpdatedAt
```

### 20.3 CertificateDeploymentRecord

```text
Id
CertificateId

TargetType
TargetInstanceId
TargetResourceId

Status
LastDeployedAt
LastError
```

### 20.4 WebServerConfigSnapshot

```text
Id
WebServerInstanceId

Reason
Path
Hash

CreatedAt
```

---

## 21. Source of Truth 原则

RemoteOS 自己创建的站点：

```text
SQLite / Domain Model
        ↓
WebSiteDefinition
        ↓
NginxConfigRenderer
        ↓
*.conf
```

即：

```text
Database = Source of Truth
Nginx Config = Generated Artifact
```

对于用户原有 Nginx 配置：

```text
Nginx Config = External Source of Truth
```

RemoteOS 只读或有限集成。

不要把所有 nginx.conf 解析后强行当作 RemoteOS 数据库。

---

## 22. 状态模型

Runtime 状态：

```csharp
public enum WebServerState
{
    Unknown,

    NotInstalled,
    Installed,

    Starting,
    Running,

    Reloading,
    Stopping,
    Stopped,

    InvalidConfiguration,

    Failed
}
```

集成状态：

```csharp
public enum IntegrationState
{
    None,

    Detected,

    Monitoring,

    Integrated,

    Managed,

    Broken
}
```

示例：

```text
RuntimeState = Running
IntegrationState = Detected
```

表示 Nginx 正在运行，但 RemoteOS 没有接管。

```text
RuntimeState = Running
IntegrationState = Integrated
```

表示用户已有 Nginx，RemoteOS 只管理自己的配置片段。

---

## 23. API 建议

发现 Web Server：

```text
POST /api/v1.0/webservers/discover
```

列出：

```text
GET /api/v1.0/webservers
```

状态：

```text
GET /api/v1.0/webservers/{id}/status
```

启用集成：

```text
POST /api/v1.0/webservers/{id}/integrate
```

测试配置：

```text
POST /api/v1.0/webservers/{id}/config/test
```

Reload：

```text
POST /api/v1.0/webservers/{id}/reload
```

站点：

```text
GET    /api/v1.0/webservers/{id}/sites
POST   /api/v1.0/webservers/{id}/sites
PUT    /api/v1.0/webservers/{id}/sites/{siteId}
DELETE /api/v1.0/webservers/{id}/sites/{siteId}
```

---

## 24. 创建反向代理站点流程

例如：

```text
remote.example.com
        ↓
http://127.0.0.1:8000
```

流程：

```text
Avalonia
   │
   ▼
Website API
   │
   ▼
WebSiteManager
   │
   ├── 检查域名
   ├── 检查端口冲突
   ├── 保存 WebSiteDefinition
   │
   ▼
IWebSiteProvider
   │
   ▼
NginxSiteProvider
   │
   ▼
NginxConfigRenderer
   │
   ▼
生成 temporary config
   │
   ▼
nginx -t
   │
   ├── Failed
   │      ↓
   │   Rollback
   │
   └── Success
          ↓
       Commit
          ↓
       nginx reload
```

---

## 25. 证书部署流程

```text
Avalonia
   │
   ▼
CertificateManager
   │
   ▼
ACME
   │
   ▼
CertificateStore
   │
   ▼
CertificateIssued
   │
   ▼
CertificateDeploymentCoordinator
   │
   ▼
NginxCertificateDeployer
   │
   ▼
修改 RemoteOS-owned site config
   │
   ▼
nginx -t
   │
   ▼
reload
```

CertificateManager 本身不需要知道 Nginx。

---

## 26. 已有 Nginx + HTTP-01 流程

已有 Nginx 占用 80 时：

```text
CertificateManager
       │
       ▼
WebRootHttp01ChallengeProvider
       │
       ▼
acme-challenge directory
```

Nginx 只负责：

```text
/.well-known/acme-challenge/*
```

映射到对应目录。

RemoteOS 不需要停止 Nginx，也不需要自己抢占 80。

---

## 27. 分阶段开发顺序

### 第一阶段：基础 Web Server 抽象

优先实现：

```text
WebServerInstance
IWebServerProvider
WebServerManager
NginxDetector
NginxRuntime
WebServerManagementMode
WebServerCapabilities
```

目标：

- 能发现 Nginx。
- 能查看状态。
- 能区分 External / Integrated / Managed。
- 能安全执行 Reload / Test。

---

### 第二阶段：配置事务与站点

实现：

```text
NginxConfigurationManager
NginxConfigTransaction
NginxConfigRenderer
NginxSiteProvider
WebSiteDefinition
```

目标：

- 创建反向代理站点。
- 自动生成 RemoteOS-owned 配置。
- 支持 nginx -t。
- 支持自动回滚。
- 支持 reload。

---

### 第三阶段：证书自动部署

实现：

```text
CertificateDeploymentCoordinator
NginxCertificateDeployer
IHttp01WebServerIntegrator
```

目标：

- 证书续期后自动部署到 Nginx。
- HTTP-01 WebRoot 自动集成。
- 保持 CertificateManager 与 Nginx 解耦。

---

### 第四阶段：跨 Web Server 扩展

增加：

```text
IISWebServerProvider
ApacheWebServerProvider
CaddyWebServerProvider
```

验证 WebServer 抽象是否合理。

---

## 28. 最终架构

```text
┌──────────────────────────────────────────────────────┐
│                  Avalonia Client                     │
└───────────────────────┬──────────────────────────────┘
                        │
                        ▼
┌──────────────────────────────────────────────────────┐
│                   RemoteOS API                       │
└──────────────┬───────────────────────┬───────────────┘
               │                       │
               ▼                       ▼
     CertificateManager         WebServerManager
               │                       │
       ┌───────┼───────┐        ┌──────┼─────────┐
       │       │       │        │      │         │
      ACME   Store  Renewal   Nginx   IIS     Apache
       │
       ▼
 Certificate
       │
       ▼
CertificateDeploymentCoordinator
       │
       ├─────────────┬─────────────┐
       ▼             ▼             ▼
    Kestrel        Nginx          IIS
    Deployer       Deployer       Deployer
```

HTTP-01：

```text
CertificateManager
        │
        ▼
ChallengeCoordinator
        │
        ├── Direct HTTP-01
        │
        ├── WebRoot HTTP-01
        │        │
        │        └── IWebServerChallengeIntegrator
        │                    │
        │             ┌──────┴──────┐
        │             ▼             ▼
        │           Nginx          IIS
        │
        └── DNS-01
```

---

## 29. 最终结论

RemoteOS 不应该被设计成：

> RemoteOS 自带 Nginx。

也不应该变成：

> RemoteOS 是一个 Nginx 管理面板。

更合适的定位是：

> **RemoteOS 能够发现本机 Web Server，并根据用户授权选择监控、集成或者托管；Nginx 是第一个完整实现的 Web Server Provider。**

推荐关系：

```text
                  RemoteOS
                     │
             WebServer Abstraction
                     │
          ┌──────────┼──────────┐
          ▼          ▼          ▼
        Nginx       IIS       Apache
       可选        可选        可选
```

Nginx 推荐默认策略：

```text
已有 nginx
    ↓
Detected
    ↓
用户允许
    ↓
Integrated
    ↓
RemoteOS 只管理 remoteos.d/
```

如果没有 Nginx：

```text
没有 nginx
    ↓
用户选择“安装 Nginx”
    ↓
Managed
    ↓
RemoteOS 完整管理
```

这一设计保持了以下原则：

- Web Server 与 RemoteOS 核心解耦。
- Nginx 是可选 Provider，而不是基础依赖。
- 证书签发与部署解耦。
- HTTP-01 与具体 Web Server 解耦。
- 用户原有配置默认不被接管。
- RemoteOS 自己创建的资源有明确 Ownership。
- 配置修改全部经过验证、备份和回滚。
- Windows 与 Linux 平台差异被封装在实现层。
- 为 IIS、Apache、Caddy 等未来扩展保留稳定边界。

---

## 30. 落地约束（当前管理员模式）

### 30.1 管理员运行与特权边界

RemoteOS 当前服务于单台服务器的网站管理员。WebServerManager 是内置可信管理功能，不使用 User / Workspace / `AppPermissions` 的细粒度授权；Web Server 实例、站点和配置快照均是**宿主机全局资源**。

发现和只读状态可以在当前进程具有读取权限时运行。集成、安装、升级、卸载、写配置、重载、启动/停止和证书部署只允许 RemoteOS 以管理员身份运行时执行。权限不足时返回稳定问题码并要求管理员以更高权限重新启动/安装 RemoteOS：

```text
webserver.admin_required
webserver.config_elevation_required
webserver.lifecycle_elevation_required
webserver.install_elevation_required
```

客户端不得收集 sudo/UAC/服务账户密码，也不得把请求参数拼接为 shell 命令。Linux 使用参数数组和明确的 systemd/nginx 可执行文件；Windows 使用 SCM 或受控进程 API。任何高权限执行器只能接受本模块定义的结构化操作和允许的路径，不能成为通用命令执行入口。

### 30.2 Provider、能力与输入校验

`IWebServerProvider` 仅描述 Provider 能力；实际可用能力由 `WebServerInstance + ManagementMode + 当前权限` 共同决定。`External` 只能检测、读取和（若 Provider 支持）测试，不得宣称具备“管理站点/修改配置/重载”的能力；`Integrated` 仅可修改 RemoteOS ownership 的目录；`Managed` 才可提供安装、升级和卸载。

`ReverseProxyTarget.Address` 不是可直接写入 Nginx 的任意 URI。服务端必须拒绝 URI 凭据、控制字符、未知 scheme 和未声明端口，规范化主机名并在解析后再次校验地址，防止 DNS rebinding。V1 仅支持显式确认的 `http`/`https` 上游；对 loopback、私网、链路本地和元数据地址的代理采用管理员可见的策略，不能让站点表单成为 SSRF 或内网扫描接口。

### 30.3 可验证的配置事务

`NginxConfigTransaction` 必须在每个 WebServerInstance 上串行运行，采用以下不可分割流程：

1. 解析实际 `nginx -V` 的 `--conf-path` 与 include 图；只允许在确认属于 `http {}` 上下文的位置写入一次 RemoteOS include。
2. 对主配置和 RemoteOS-owned 文件记录 hash、inode/文件标识与版本；若外部修改与快照不一致则中止并要求重新读取，不覆盖用户变更。
3. 在与最终文件相同文件系统的受控 staging 目录生成所有文件，拒绝相对路径、越界路径和符号链接。
4. 通过使用该 staging 文件图的明确 `-c`/`-p` 参数执行 `nginx -t`；不得只测试尚未引用临时文件的旧主配置。
5. 用原子 rename 提交 RemoteOS-owned 文件，再执行 reload 并验证退出码和运行状态。
6. 失败时恢复磁盘上的前一版本；注意 Nginx reload 失败通常继续运行旧 worker，因此“运行中配置”和“磁盘配置”都必须报告并分别恢复。每一步写入 OperationId、快照 ID、问题码和脱敏诊断。

卸载或删除站点只能删除带有 RemoteOS ownership 标记且 hash 匹配的文件，永不递归删除用户目录。

### 30.4 Protocol、异步操作与审计

所有 WebServer DTO、枚举、路由常量和序列化规则位于 `Shared/RemoteOS.Protocol/WebServers/`。Endpoint、Client 和 UI 不硬编码 API 字符串或平台命令。发现、读取状态和测试可同步返回；安装、升级、卸载、集成、站点修改、reload 和证书部署必须携带 `Idempotency-Key` 并创建持久化操作：

```text
OperationId
State: queued | running | succeeded | failed | cancelled
Stage
ProblemCode
SnapshotId
StartedAt / CompletedAt
```

Client 可轮询 operation 或订阅后续定义的事件契约；断线、窗口关闭和重试不应重复执行变更。审计记录管理员、操作、目标实例/站点、确认、结果、快照和 OperationId，不记录私钥、完整 Nginx 配置秘密或命令行中的敏感值。

### 30.5 持久化与迁移

`WebServerInstanceRecord`、`WebSiteRecord`、`CertificateDeploymentRecord` 和 `WebServerConfigSnapshot` 都属于 HostGlobal 作用域。需补充：schema version、乐观并发 revision、Provider 版本/检测时间、所有权标记、配置内容版本、当前/上次成功部署版本、操作状态和保留期。

新增表和索引必须通过版本化 SQLite migration 创建；不可依赖 `EnsureCreated()` 对既有数据库追加表或列。站点与部署记录应有外键/唯一约束（实例 + 规范化域名/绑定、证书 + 目标），配置快照需限制数量和大小，并记录不可恢复/外部修改状态。

### 30.6 Kestrel 与证书协作

WebServerManager 不拥有 Kestrel 的证书或监听配置。证书签发完成后仅通过 `CertificateDeploymentCoordinator` 调用目标 Deployer；Kestrel 的首启、原子证书版本切换、SNI/端口绑定、热加载、健康检查和失败回退由 CertificateManager 文档定义。Nginx Deployer 只能修改 RemoteOS-owned 站点/挑战片段，部署失败不能破坏已有有效证书配置。

### 30.7 平台范围、UI 与验收

V1 支持目标为 **Ubuntu 24.04 LTS** 与 **Windows Server 2016 及以上**。V1 仅交付 Nginx 的发现/只读状态和经管理员确认的最小集成；Nginx 安装、升级、卸载、IIS、Apache、Caddy 和自动 HTTPS 部署均为后续阶段，除非在两个目标平台完成验证。

UI 必须使用 `webserver.*` 三语言本地化 key，显示管理模式、实际能力、权限不足、外部修改冲突、风险确认、操作进度和可恢复建议。验收至少覆盖：两平台检测；External 不写入；Integrated 的 include 上下文；并发修改锁；`nginx -t` 失败；reload 失败回退；取消/断线重连；管理员/非管理员降级；以及配置、日志和审计的秘密脱敏。
