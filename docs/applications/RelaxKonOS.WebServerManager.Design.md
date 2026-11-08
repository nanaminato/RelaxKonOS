# RelaxKonOS WebServerManager / Nginx 集成设计

> MB-05A（2026-11-04）：网站发布协调器已用异步准入和真实 resident 工作租约保护 queued 到实际业务/终态持久化收尾；注册先于执行，Stop 关闭准入并等待实际 Task，超时不提前释放。未知终态写入失败隔离当前代并阻止新副作用。启动仍按原规则把未完成发布标为 interrupted，不重放证书或站点写入。Core `IWebServerManager`、Nginx 共享操作账本与已发布站点继续独立存续；本项不表示 Core Nginx 执行器已可休眠，也不证明真实 Nginx/证书故障或生产内存收益。整体状态由[模块边界设计](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md)拥有。

> 2026-11-02：Nginx 的组件中心元数据已迁入[本地 manifest](../platform/RelaxKonOS.ComponentCatalog.Design.md)，Linux APT 与 Windows 归档通过既有领域路径处理。官方页版本发现/有限模板选择及通用冻结计划/执行经 provider 派发，仓库、制品、冻结期望、所有权与 Helper 校验保持权威；目录不是新的可信来源，也不代表新增实机生命周期验收。

2026-10-30（MR-05）：Ubuntu 26.04 x64 经组件冻结计划与生产安装台账安装 `1.28.3-2ubuntu1.12` 成功，受管提交操作 ID、原回执、`nginx -t` 与默认站点 HTTP 200 验证通过。Helper APT 解算明确要求 SHA-256，修复默认 MD5 URI 计划被拒绝的问题，未降低验证要求；修复已部署到本次测试宿主。此证据仅为首次安装，不覆盖跨版本更新、卸载或补偿，详见[实机记录](../testing/RelaxKonOS.Tailscale.ComponentCenter.20261030.md)。

> **模块归属（2026-10-28）**：`/api/v1.0/webservers/*`、`/api/v1.0/website-publications/*`、站点发布事务与其后台协调器归官方模块 `relaxkonos.webserver`，默认启用，并硬依赖 `relaxkonos.certificates`。**Nginx 的读状态与 conf.d 集成实现留在 Core**（`IWebServerManager`/`NginxWebServerManager`），因为应用部署的站点发布、组件中心的 Nginx 组件状态与安装域都直接使用它；禁用本模块只是不再提供 Web Server Manager 界面与站点发布事务，不会停止或修改现有 Nginx 及其站点配置。详见[模块设计实施记录](../architecture/RelaxKonOS.ModulesAndExtensions.Design.md#certificate-与-webserver-模块2026-10-28)。

> 当前逐组件实施状态与 Nginx 平台边界见[组件中心实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md)；下文历史切片的验收范围保持原记录。

> **停用门禁（2026-10-28）**：站点发布账本（`data/website-publications.json`）里若还有 `Queued`/`Running` 的发布，`Modules:WebServer:Enabled=false` 会让 Server **拒绝启动**并打印发布 ID 与域名——先启用模块让协调器重新核验（结果是 `Interrupted` + `website.recheck_required`），再停用即可。门禁只读账本（`WebsitePublicationStore.Pending()`），不会因为被询问就把操作改成中断。运行中的进程可以先用 `GET /api/v1.0/server/modules` 查 `orphanedWork` 与 `requiredBy`，在改配置之前就知道会不会被拒。

> 2026-10-27 当前 Windows 日常安装入口：组件中心从 nginx.org 下载页提供多版本选择，默认稳定分支；支持服务器下载、本机下载后上传、本机文件、服务器文件及官方下载链接。已有文件不要求建立签名目录或先联网重下归档；Helper 仍限制归档路径/大小并执行原生版本与配置检查。安装后版本由实际 `nginx -v` 报告，不依赖是否存在固定归档计划回执。旧的单一默认归档仍是原受保护固定归档路径的配置，不再限制日常版本选择。此次未新增覆盖已有 Nginx 的升级事务，真实服务安装/切换尚未验收，见[当前进度](../platform/RelaxKonOS.ComponentCenter.Progress.md)。

> 状态：**Nginx Provider V3 已实现**。除发现、配置测试、集成、受管安装和生命周期管理外，一个 RelaxKonOS 站点现在可同时服务静态目录、SPA 回退和多个路径前缀的反向代理；创建、修改、删除都会先执行 `nginx -t`，再重载或回滚。站点可绑定证书管理器中已有的证书；私钥仍只保留在服务器本地。证书的签发与自动续期后触发重载仍可在后续阶段进一步自动化。

## 1. 设计背景

RelaxKonOS 是一个仅管理当前主机的服务器管理程序，后端采用 .NET 10，支持 Windows 和 Linux。

在证书管理器设计中，已经确定以下原则：

- 证书签发与证书部署分离。
- RelaxKonOS 自身作为 ACME Client。
- Nginx / IIS / Apache 只作为证书部署和 HTTP-01 暴露路径的可选集成对象。
- 不要求 RelaxKonOS 自己长期占用 TCP 80。
- 不应把 Nginx 作为 RelaxKonOS 的强依赖。

因此，Web Server 管理模块应设计为一个通用的 Web Server 抽象层，其中 Nginx 只是第一个完整实现的 Provider。

---

## 2. 核心设计目标

### 2.1 不强依赖 Nginx

RelaxKonOS 的核心功能必须可以在以下场景正常工作：

- 没有安装 Nginx。
- 只有 IIS。
- 只有 Apache。
- 用户自行维护现有 Nginx。
- RelaxKonOS 仅使用 Kestrel 提供 HTTPS。
- 后续接入 Caddy、OpenResty 等其他 Web Server。

因此，不建议把核心架构命名或设计为：

```text
RelaxKonOS
   ↓
NginxManager
   ↓
所有 Web 功能
```

推荐：

```text
RelaxKonOS
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
RelaxKonOS.Server
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

## 4. Web Server 的两种管理模式

未受管 Nginx 只作为一次性发现候选项，用于让管理员决定是否创建集成；它不是管理模式、不会作为 Web Server 实例持久化，也不会出现在实例或站点管理中。管理员确认集成后，实例直接进入 `Integrated`。

管理模式只有：

```csharp
public enum WebServerManagementMode
{
    Integrated,
    Managed
}
```

### 4.1 Integrated

RelaxKonOS 与现有 Web Server 集成，但不拥有它。

典型原则：

- 不负责安装。
- 不负责升级。
- 不负责卸载。
- 不全面接管主配置。
- 只管理 RelaxKonOS 自己创建的配置片段。

例如：

```text
/etc/nginx/
├── nginx.conf
├── conf.d/
│
└── relaxkonos.d/
    ├── relaxkonos.conf
    ├── acme.conf
    └── sites/
```

RelaxKonOS 只拥有：

```text
/etc/nginx/relaxkonos.d/*
```

这是推荐的默认集成模式。

---

### 4.2 Managed

Nginx 由 RelaxKonOS 安装并完整管理。

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
C:\ProgramData\RelaxKonOS\
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

    public bool IsRelaxKonOSManaged { get; init; }
}
```

不要把系统限制为单个 Nginx。

同一主机理论上可能同时存在：

```text
/usr/sbin/nginx
/usr/local/openresty/nginx/sbin/nginx
C:\ProgramData\RelaxKonOS\webserver\nginx\nginx.exe
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
6. RelaxKonOS Managed Nginx。
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
C:\ProgramData\RelaxKonOS\webserver\nginx\nginx.exe
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

RelaxKonOS 首次发现已有 nginx 时，不应该自动修改配置。

推荐流程：

```text
发现到未受管 Nginx
   ↓
用户明确确认集成
```

只有用户明确同意，才进入：

```text
Integrated
```

只有用户明确选择安装 RelaxKonOS 管理版本，才进入：

```text
Managed
```

---

## 10. 配置 Ownership

建议给配置引入 Ownership：

```csharp
public enum ConfigOwnership
{
    UserOwned,
    Shared,
    RelaxKonOS
}
```

规则：

### UserOwned

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

### RelaxKonOS

```text
RelaxKonOS 可以完全管理
```

例如：

```text
/etc/nginx/relaxkonos.d/*
```

这样可以避免删除站点、卸载 RelaxKonOS、配置恢复时误伤用户配置。

---

## 11. 配置目录策略

对于现有 Nginx，建议只做一次最小侵入式修改：

```nginx
include /etc/nginx/relaxkonos.d/*.conf;
include /etc/nginx/relaxkonos.d/sites/*.conf;
```

之后所有 RelaxKonOS 配置都进入：

```text
/etc/nginx/relaxkonos.d/
```

不要持续修改：

```text
/etc/nginx/nginx.conf
```

对于 RelaxKonOS Managed Nginx，则可以完全拥有配置结构。

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

对于 RelaxKonOS Managed Nginx，可以由 RelaxKonOS Service 负责 nginx 进程生命周期。

```text
RelaxKonOS Service
    ↓
nginx.exe
```

Windows 官方 nginx 本身不需要成为 RelaxKonOS 的核心系统服务依赖。

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
   ├── RelaxKonOS Kestrel
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
  /var/lib/relaxkonos/acme-challenge/

Windows:
  C:\ProgramData\RelaxKonOS\acme-challenge\
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
    root /var/lib/relaxkonos;
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

    public string? StaticRootPath { get; init; }

    public bool SpaFallback { get; init; }

    public required IReadOnlyList<WebSiteProxyRoute> Routes { get; init; }

    public Guid? CertificateId { get; init; }
}
```

每个代理路由：

```csharp
public sealed record WebSiteProxyRoute
{
    public required string PathPrefix { get; init; }

    public required Uri Upstream { get; init; }

    public bool DisableBuffering { get; init; }
}
```

静态根目录与路由不再是互斥的“站点类型”。静态站点只填写 `StaticRootPath`，纯代理站点只填写 `Routes`（用 `/` 覆盖整个站点），整合站点可以同时填写两者。NginxProvider 负责把通用模型 Render 为 nginx 配置。

站点还可选择为每个监听端口写入 IPv6 listener；启用 HTTPS 后可将 HTTP 请求重定向到 TLS listener。这样同一张 SAN 或通配符证书会绑定到该站点的所有域名。

选择静态目录后可显式授予 Nginx worker 读取权限。保存时先校验路径格式与站点定义，再由权限助手校验目录并授权，最后由 Server 复查目录存在性与符号链接；避免私有父目录使 Server 在授权前误判目录不存在。

Linux 使用 POSIX ACL，给 worker 读取内容、穿越父目录的权限，并给实际 Server 服务账户父目录穿行权限；不修改所有者，也不授予写入权限。`/etc`、`/proc`、`/sys`、`/dev`、`/run` 与 `/root` 不可作为目标。

Windows 使用 NTFS ACL，给启动托管 Nginx 的 Helper 身份读取和执行权限，给机器配置中的 Server/开发客户端 SID 目录属性读取与穿行权限。内容目录的继承规则覆盖后续文件，并显式处理已有受保护子目录与文件；保留原有所有者、继承保护和拒绝规则，不新增写入权限。仅支持已存在的本地目录，不允许卷根、UNC、Windows 系统目录、Helper 私有运行时以及 Nginx 程序/配置目录（允许 `sites` 子目录），拒绝目标、父路径或内容树中的重解析点。内容树最多包含 100,000 个目录和文件。

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
Integrated Nginx
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

ManagedByRelaxKonOS

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

RelaxKonOS 自己创建的站点：

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
用户自有 Nginx 配置 = Source of Truth
```

RelaxKonOS 只在显式集成后管理自己的配置片段。

不要把所有 nginx.conf 解析后强行当作 RelaxKonOS 数据库。

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

    Candidate,

    Integrated,

    Managed,

    Broken
}
```

示例：

```text
RuntimeState = Running
IntegrationState = Candidate
```

表示发现到未受管 Nginx；它只可用于发起显式集成，尚未进入产品管理范围。

```text
RuntimeState = Running
IntegrationState = Integrated
```

表示用户已有 Nginx，RelaxKonOS 只管理自己的配置片段。

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

列出可集成候选项：

```text
GET /api/v1.0/webservers/integration-candidates
```

启用集成：

```text
POST /api/v1.0/webservers/integration-candidates/{candidateId}/integrate
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
DELETE /api/v1.0/webservers/{id}/sites/{siteId}
```

---

当前站点创建/更新共用 POST，直接返回 `WebServerSiteDto`；删除返回 204。站点写入没有长任务 ID 或服务端幂等重放能力。客户端连接中断后必须回读站点定义、运行状态与配置检查，不把站点缺失或字段一致当原请求成功，也不伪造操作 ID。

创建请求的 `expectedUpdatedAt` 为 null；可省略 ID 由名称生成，也可提供稳定 ID。更新必须提供已读站点的完整 `updatedAt`（保留亚毫秒精度）。DELETE 正文为 `{ "expectedUpdatedAt": "<已读原时间戳>" }`。

Provider 在站点变更锁内、任何配置/include/权限副作用之前比较版本：创建撞 ID 返回 `webserver.site_already_exists`，更新/删除版本失配或目标消失返回 409 `webserver.site_changed`，不覆盖更新后的定义，也不把更新当新建。成功更新的时间戳严格递增；站点元数据损坏或不可读时拒绝写入，不能当空目录覆盖。

桌面与 Android 均发送该当前契约；网站发布、应用部署代理联动也带原版本。失败补偿仅能恢复它自己刚写入的版本，不能覆盖其后其他客户端的更新。默认读写不添加旧时间戳格式、无版本更新入口或旧路由。

## 24. 创建可组合站点流程

例如：

```text
relaxkon.com
 ├── /             → /srv/relaxkon/frontend/browser（SPA）
 ├── /api/         → http://127.0.0.1:5062
 ├── /relaxkonos/  → http://127.0.0.1:5062（无缓冲）
 └── /apt/         → http://127.0.0.1:5062（无缓冲）
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
   ├── 检查端口、路径和路由冲突
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
修改 RelaxKonOS-owned site config
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

RelaxKonOS 不需要停止 Nginx，也不需要自己抢占 80。

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
- 能区分 Integrated / Managed。
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

- 创建可组合的静态、代理或整合站点。
- 自动生成 RelaxKonOS-owned 配置。
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

### 第三阶段补充：续期后的 Nginx 自动重载

证书续期更新稳定 PEM 路径。启用 WebServer 模块并使用 SQLite 时，Server 为引用该证书的 RelaxKonOS 受管站点协调重载，同一实例的多个站点共享一次操作，站点配置保持。未集成的发现候选项不进入目标列表。

现有显式轮换路径通过独立的续期和重载回执管理。生产 Helper 的原生回执确认后可恢复原重载终态；只有新证书握手证据时，重载保持待核验。见[Nginx 显式证书轮换](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-显式证书轮换与原重载回执)。

```text
CertificateOperation(renew) succeeded
        │
        ▼
NginxCertificateRenewalCoordinator
        │  从续期台账接管证书版本并记录受管站点实例
        ▼
按 Nginx instanceId 分组并过滤 Integrated / Managed
        │
        ▼
原 WebServer reload 操作
        │  复核版本与配置 → 确认运行状态 → nginx -t → reload
        ▼
写入 certificate_deployment_records 与 WebServerOperation
```

续期在原台账持久化成功版本。WebServer 操作按实例使用 `certificate-renew:{certificateId}:{version}` 幂等键和 `renewal-worker` 执行者。自动操作使用现有 Helper 或固定参数执行路径，无需用户会话授权。已停止、站点配置漂移或证书版本失配时，原操作记录未执行重载的原因。

`certificate_deployment_records` 关联原续期和原重载 ID，保存目标版本、协调器最近确认的成功版本及问题码。首次接管前的历史部署保持原有证据范围。配置检查或重载失败后，新 PEM 与成功续期回执保留，管理员可处理失败原因后显式重载。原生结果未知时，同一原重载保持待核验，后续对账只读原生回执。

Linux 系统包 Nginx 的实际验收见[续期自动部署台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-续期自动部署)。桌面和 Android 的独立 Nginx 部署状态展示及其他平台实机矩阵仍待完成。

WebServer 管理模块停用期间，证书仍可续期，Nginx 继续使用其已加载的证书。重新启用管理模块时，协调器可从证书台账接续尚未准入的原续期。Linux 实测覆盖这期间的 Server 进程中断及重新启用后的单次重载。见[准入前进程中断](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-自动部署准入前的进程中断)。

管理模块启用时，重载台账的准入写入失败会保留工作租约和恢复阻塞，Nginx 继续提供已加载的证书。原进程终止并恢复写入后，协调器用证书台账中原有目标只准入一次重载。见[目标检查点恢复](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-自动部署的目标检查点恢复)。

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
│                   RelaxKonOS API                       │
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

RelaxKonOS 不应该被设计成：

> RelaxKonOS 自带 Nginx。

也不应该变成：

> RelaxKonOS 是一个 Nginx 管理面板。

更合适的定位是：

> **RelaxKonOS 能够发现本机 Web Server，并根据用户授权选择监控、集成或者托管；Nginx 是第一个完整实现的 Web Server Provider。**

推荐关系：

```text
                  RelaxKonOS
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
发现到未受管 Nginx
    ↓
用户允许
    ↓
Integrated
    ↓
RelaxKonOS 只管理 relaxkonos.d/
```

如果没有 Nginx：

```text
没有 nginx
    ↓
用户选择“安装 Nginx”
    ↓
Managed
    ↓
RelaxKonOS 完整管理
```

这一设计保持了以下原则：

- Web Server 与 RelaxKonOS 核心解耦。
- Nginx 是可选 Provider，而不是基础依赖。
- 证书签发与部署解耦。
- HTTP-01 与具体 Web Server 解耦。
- 用户原有配置默认不被接管。
- RelaxKonOS 自己创建的资源有明确 Ownership。
- 配置修改全部经过验证、备份和回滚。
- Windows 与 Linux 平台差异被封装在实现层。
- 为 IIS、Apache、Caddy 等未来扩展保留稳定边界。

---

## 30. 落地约束（当前管理员模式）

### 30.1 管理员运行与特权边界

RelaxKonOS 当前服务于单台服务器的网站管理员。WebServerManager 是内置可信管理功能，不使用 User / Workspace / `AppPermissions` 的细粒度授权；Web Server 实例、站点和配置快照均是**宿主机全局资源**。

发现和只读状态可以在当前进程具有读取权限时运行。集成、安装、升级、卸载、写配置、重载、启动/停止和证书部署只允许 RelaxKonOS 以管理员身份运行时执行。权限不足时返回稳定问题码并要求管理员以更高权限重新启动/安装 RelaxKonOS：

```text
webserver.admin_required
webserver.config_elevation_required
webserver.lifecycle_elevation_required
webserver.install_elevation_required
```

客户端不得收集 sudo/UAC/服务账户密码，也不得把请求参数拼接为 shell 命令。Linux 使用参数数组和明确的 systemd/nginx 可执行文件；Windows 使用 SCM 或受控进程 API。任何高权限执行器只能接受本模块定义的结构化操作和允许的路径，不能成为通用命令执行入口。

### 30.2 Provider、能力与输入校验

`IWebServerProvider` 仅描述 Provider 能力；实际可用能力由 `WebServerInstance + ManagementMode + 当前权限` 共同决定。未受管 Nginx 仅作为集成候选项，不构成 `WebServerInstance`，不得宣称具备“管理站点/修改配置/重载”的能力；`Integrated` 的站点写入仅可修改 RelaxKonOS ownership 的目录。

卸载以实例的 `CanUninstall` 为准：受管安装支持卸载；已接管、使用 `/usr/sbin/nginx` 与 `/etc/nginx/nginx.conf` 且宿主提供 APT 的系统安装也支持通过公共安装契约卸载，Server 执行前重新核实接管状态。外部自定义实例不支持卸载。

`WebSiteProxyRoute.Upstream` 不是可直接写入 Nginx 的任意 URI。服务端必须拒绝 URI 凭据、控制字符、未知 scheme 和未声明端口，规范化主机名并在解析后再次校验地址，防止 DNS rebinding。当前版本仅支持显式确认的 `http`/`https` 上游；对 loopback、私网、链路本地和元数据地址的代理采用管理员可见的策略，不能让站点表单成为 SSRF 或内网扫描接口。路径前缀必须是以 `/` 开头的受限路径，静态根目录必须是服务器上存在、非符号链接的绝对目录。

### 30.3 可验证的配置事务

`NginxConfigTransaction` 必须在每个 WebServerInstance 上串行运行，采用以下不可分割流程：

1. 解析实际 `nginx -V` 的 `--conf-path` 与 include 图；只允许在确认属于 `http {}` 上下文的位置写入一次 RelaxKonOS include。
2. 对主配置和 RelaxKonOS-owned 文件记录 hash、inode/文件标识与版本；若外部修改与快照不一致则中止并要求重新读取，不覆盖用户变更。
3. 在与最终文件相同文件系统的受控 staging 目录生成所有文件，拒绝相对路径、越界路径和符号链接。
4. 通过使用该 staging 文件图的明确 `-c`/`-p` 参数执行 `nginx -t`；不得只测试尚未引用临时文件的旧主配置。
5. 用原子 rename 提交 RelaxKonOS-owned 文件，再执行 reload 并验证退出码和运行状态。
6. 失败时恢复磁盘上的前一版本；注意 Nginx reload 失败通常继续运行旧 worker，因此“运行中配置”和“磁盘配置”都必须报告并分别恢复。每一步写入 OperationId、快照 ID、问题码和脱敏诊断。

删除站点只能删除带有 RelaxKonOS ownership 标记且 hash 匹配的文件，永不递归删除用户目录。受管安装卸载可删除专用安装目录；APT 系统安装卸载在确认停机和配置删除影响后，由 Helper 清除固定 Nginx 系统包及其包配置，不递归删除用户站点内容目录。

### 30.4 Protocol、异步操作与审计

所有 WebServer DTO、枚举、路由常量和序列化规则位于 `Shared/RelaxKonOS.Protocol/WebServers/`。Endpoint、Client 和 UI 不硬编码 API 字符串或平台命令。发现、读取状态和测试可同步返回；安装走统一 Installation 契约，集成与生命周期（包括 reload）使用 `Idempotency-Key` 和持久化操作。当前站点修改/删除采用上文同步版本校验契约，客户端通过事实回读核实未知结果；若后续将其转为持久化操作，必须同步所有调用方，不能将当前同步响应包装成虚假的操作：

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

WebServerManager 不拥有 Kestrel 的证书或监听配置。证书签发完成后仅通过 `CertificateDeploymentCoordinator` 调用目标 Deployer；Kestrel 的首启、原子证书版本切换、SNI/端口绑定、热加载、健康检查和失败回退由 CertificateManager 文档定义。Nginx Deployer 只能修改 RelaxKonOS-owned 站点/挑战片段，部署失败不能破坏已有有效证书配置。

### 30.7 平台范围、UI 与验收

V1 支持目标为 **Ubuntu 24.04 LTS** 与 **Windows Server 2016 及以上**。V1 仅交付 Nginx 的发现/只读状态和经管理员确认的最小集成；Nginx 安装、升级、卸载、IIS、Apache、Caddy 和自动 HTTPS 部署均为后续阶段，除非在两个目标平台完成验证。

UI 必须使用 `webserver.*` 三语言本地化 key，显示管理模式、实际能力、权限不足、用户配置变更冲突、风险确认、操作进度和可恢复建议。验收至少覆盖：两平台检测；未集成候选项不写入；Integrated 的 include 上下文；并发修改锁；`nginx -t` 失败；reload 失败回退；取消/断线重连；管理员/非管理员降级；以及配置、日志和审计的秘密脱敏。

受管 HTTPS 站点保存还需证书 Issued/Active、处于有效期内且 SAN 覆盖全部域名绑定；规范化 IDN/IP，单层通配符不覆盖 apex、多层或 IP。校验在版本检查后、权限/include/配置副作用前执行，不可用返回 `webserver.site_certificate_not_usable`，不覆盖返回 `webserver.site_certificate_domain_mismatch`。宿主 PEM 路径模式不通过元数据推断有效期或客户端信任。

Linux 静态目录自动授权依赖 `acl` 软件包（`/usr/bin/setfacl`）。保存时缺少依赖返回 `webserver.site_acl_package_required`，客户端提示管理员在服务器执行 `sudo apt-get install acl` 后重新保存并保留草稿。此失败不会写入站点配置或元数据。通过内置 APT 流程安装 Nginx 时一并安装 `acl`。

### nginx 冻结安装接入台账（2026-10-25）

Linux nginx 首次冻结安装从组件中心经产品提权进入统一安装台账，提供原键幂等提交、资源排他、阶段进度与重开观察。无台账 component execute 路由已删除；重启时不以当前健康推导原任务成功，返回中断待核对。Windows、包补偿及逐依赖制品证明尚未完成，详见[实施范围与验证](../platform/RelaxKonOS.ComponentCenter.Progress.md)。


## Linux 当前 Nginx 与桌面入口（2026-10-26）

CC-01：Linux 桌面组件中心提供当前 Nginx 的发现、外部实例显式集成与选择；安装沿用组件中心已有冻结包计划。选择通过 `GET/PUT /api/v1.0/webservers/current-nginx` 存入宿主全局 SQLite（迁移 17），同一宿主的客户端共享；只允许选择已受管或已集成的 Nginx。未显式选择且只有一个实例时直接使用；多个实例须选择，已保存实例消失后保持不可用，不切换到其他实例。

Linux 桌面 Web 服务器管理器隐藏实例选择、发现、集成、安装和卸载入口，提供组件中心跳转；保留服务器状态、配置检查、启停/重载和站点管理。窗口打开与刷新时读取当前选择，并每三秒检查变化，变化时清空旧站点目标并重新读取。未配置或不可用时提示到组件中心处理。Windows 入口保持现有行为。此变更不增加系统包更新、回滚或补偿能力，也不改变外部实例的所有权。Android UI 尚未迁移，仍按 Android 自有文档追踪。当前验证范围为本地构建与自动化，尚无 Linux 桌面实机验收。

验证：Server/Client 构建通过；`RelaxKonOS.Server.Tests --current-nginx-only` 的 8 项持久化/解析断言与桌面 `--current-nginx-only` 的 7 项联动/门禁断言通过，并检查 1060/520 宽度的四张布局预览。完整 `--webserver-only` 在既有 Windows 静态目录验证断言处失败，未据此宣称整组通过。


### 桌面安装入口（2026-10-26）

本应用的宿主组件安装已移至组件中心，应用内不再提供安装、更新、修复、回退或卸载入口，也不保留常驻横幅；设置页的共享“组件依赖”区显示宿主实测状态并提供跳转（Project Editor 为“工具 → 组件依赖”）。业务配置与服务管理保留。各平台安装能力、冻结计划与台账路径见[组件中心实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md#桌面组件安装入口统一2026-10-26)。此项仅集中入口，不扩大运行时、信任来源、多版本更新或平台验收范围。

### Windows 可用版本修复（2026-10-27）

未提供 `NginxReleases` 的 Helper 配置现在使用内置固定版本 1.30.6；显式空列表仍表示管理员禁用候选，显式列表仍完全替换默认列表。归档地址为 `https://nginx.org/download/nginx-1.30.6.zip`，SHA-256 为 `c6ee35edb0a861063323f709192ae50d1eefaf2ea7d00678bd2b22d8542dc61e`。已用 nginx.org 官方公开密钥核验归档分离签名，签名者指纹 `D6786CE303D9A9022998DC6CC8464D549AF75C0A`，并在 Windows 执行 `nginx.exe -v` 实测为 1.30.6。默认列表随本次构建发布，版本检查读取宿主批准列表，不抓取上游最新版或修改已部署的显式信任策略。

客户端保留空列表的来源修订，清除失效版本选择；缺少批准版本显示宿主策略要求，检查失败撤下旧安装计划。此项只修复版本候选和计划准入，未实测服务安装、跨版本更新、卸载或回滚。部署需更新 Client、Server 和 Helper；仅更新客户端不能给旧 Helper 增加默认归档。


2026-11-06 MB-05 隔离预检：测试机有共享 Nginx 实例。应用层中断验收须另用专属配置、PID、回环端口及测试证书，检查原发布回执与实际 HTTPS 服务连续性。 [验收台账 §50](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#50-mb-0305-隔离测试设施复核2026-11-06)

既有 Linux 测试机已通过专属 Nginx 的真实重载及操作工作进程崩溃恢复。生产操作台账以原 operationId 持久化 interrupted，重试后原实例持续服务，实际重载仍为一次。完整 provider、网站发布与 CA 连续性继续开放，见边界设计 §50。

直接 WebServer 操作取消接口返回当前回执。客户端继续轮询原操作，实际任务及清理结束后才观察到终态。停机打断时保留原活动记录供启动恢复，详见边界设计 §50。

WebServer 直接操作在显式启动恢复完成后开放。台账不可读取或恢复不可确认时，操作入口保持关闭并保留恢复责任。原回执仍按 operationId 恢复，见边界设计 §50。

Nginx 生命周期和重载会先按原请求身份恢复回执，再检查实例是否可发现。既有 Linux 测试机通过生产 provider 与独立 Nginx 重载的跨进程复测，详见边界设计 §50。

MB-05 在既有 Linux 测试机验证了隔离 Nginx 的 TLS 连续性。真实 reload 后终止管理工作进程，原 master 继续提供 HTTPS。恢复原回执和重试未增加 reload。TLS 配置由测试夹具提供，生产站点绑定与发布联合恢复仍待验收。见边界设计 §53。

生产站点保存入口已在隔离 Linux Nginx 验证本地证书文件绑定。生成配置通过真实检查和一次重载，TLS 客户端验证对端证书并读取站点内容，站点元数据可读回。证书仓库 ID 绑定和网站发布中断恢复仍待实机联合验收。见边界设计 §53。

同一隔离 Nginx 验收也覆盖证书仓库 ID 绑定。站点元数据保留证书 ID，生成配置引用稳定 PEM 路径，重载后客户端通过新证书访问站点。见边界设计 §53。

网站发布已通过隔离 Nginx 的 Verifying 阶段崩溃恢复实机验收。重启按原 operationId 持久化 Interrupted，同键取回原回执，已发布站点继续代理 HTTPS，应用关联保留，发布重载只执行一次。见边界设计 §53。

MB-05 也验证了真实 Nginx 已重载、站点元数据和应用关联尚未写入时的恢复。重启保留原操作及 website.site_application_unverified，已生效的 HTTPS 路由继续服务，同键重试未重载或补写元数据。见边界设计 §53。

WebServer 操作和网站发布的 HTTP 202 在响应执行期间持有 WebServer 模块租约。Windows Release 专项通过，新增路由的实机断线验收仍开放。

Linux Release 回执回归及隔离 Nginx 发布恢复已通过。发布故障经协调器入口注入，HTTP 实机断线排空仍开放。

Linux 隔离 Nginx 已验证网站发布恢复后的生产 HTTP 同键响应。客户端断线后，租约保留到响应退出，重试返回原中断回执，已有 HTTPS 路由继续服务且发布重载未重复。

网站发布 HTTP 断线复测也覆盖应用关联已提交的 Verifying 阶段。恢复保留原关联与 HTTPS，原回执提示重新检查，发布只重载一次。

独立 Nginx reload 的生产 HTTP 回执也通过 Linux 断线验收。原管理实例从发现移除后，同键仍返回原中断回执。响应执行退出后归还租约，原 HTTPS 服务继续运行，reload 未重放。

网站发布以原持久台账准入，并在原工作租约内创建执行作用域。恢复或同键查询直接返回原回执。Linux 隔离 Nginx 已验证配置提交和 HTTPS 验证中的进程崩溃。恢复保留原回执与现有 TLS，未追加发布重载。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布准入与执行作用域)。

直接 Nginx 操作在原 WebServer 台账准入后创建执行作用域，完成任务和作用域清理后提交终态。清理无法确认时保留原活跃回执。Linux 隔离实测覆盖重载后进程中断、同键恢复和 HTTP 断线排空，原 HTTPS 保持，重载一次。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-直接-webserver-操作执行作用域)。

Linux 完整 Server 实机复测通过管理员登录、真实集成 Nginx reload、管理进程强制中断及原回执恢复。同键和原 operationId 返回原中断回执，原 Nginx 持续服务 HTTPS，reload 只执行一次。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整认证-server-的-nginx-重载与重启)。

系统包 Nginx 通过完整 Server、生产 Helper 和私有 systemd 的真实重载中断验收。Server 与 Helper 被终止期间，原 master 持续提供已更新的 HTTPS。重启后按原 ID 和同键返回原中断回执，实际重载一次。自然停机和取消排空见下文。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-系统包-nginx-经生产-helper-中断与恢复)。

Linux 系统包 Nginx 的重载请求提交给生产 Helper 后，取消或自然停机等待原响应。实机验证取消回执在原 reload 完成后才落盘，SIGTERM 后原作业结束才退出，重启未追加 reload。Helper 自身超时后的作业对账仍开放。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-已提交-nginx-systemd-作业的取消与停机排空)。

Linux 系统包直接重载在派发前持久化原检查点。生产 Helper 超时、连接丢失或 Server 在原响应前退出时，原 ID 和同键可查询待核验回执，同实例新后台操作暂缓。Linux 专属 systemd 已验证三条路径及一次实际重载。原生成功终态查询和解除待核验门禁见下文。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-原生重载结果待核验)。

Linux 系统包直接重载现在可以在重启后按原 ID 对账。生产 Helper 持久化原 systemctl 成功证据后，Server 只读核验并完成原回执，同键不会重载。缺少匹配证据时继续显示待核验。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-原-helper-回执对账)。

Linux 系统包重载的原回执对账覆盖明确成功及原生作业明确失败。响应丢失后读取原 ID，原 HTTPS 持续，原作业只尝试一次。Helper 无原作业终态证据时仍显示待核验。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-存活进程响应丢失和失败回执)。

Linux 系统包受管 Nginx 的启动、停止与重启使用原 `operationId` 查询。启动或重启仍会先启用服务。作业派发后取消会等待实际结果，确认成功或失败后保存终态。同键查询返回原操作。Helper 应答丢失时 Server 只读取匹配动作的原持久回执，证据缺失时显示待核验，并拒绝同实例新的后台变更和站点写入。见[启停与重启验收](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-linux-nginx-启动停止与重启的原回执)。

Linux 系统包重载的原回执保存在 Server。原终态持久化后，Helper 压缩原详情并保留防重放标记。清理确认丢失时，存活 Server 按原 ID 重试清理，工作租约持续到确认落盘。停机仍待确认时，由重启后的同一工作 owner 接续。旧 ID 在 Helper 重启后仍拒绝重载，待核验记录保留详情。见[清理确认验收](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-原回执清理确认与租约)。

完整 Server 的认证网站发布已在既有 Linux 测试机通过生产 Helper、私有 systemd、实际 Nginx 和独立 Docker 上游的联合验收。配置阶段中断恢复原 `website.site_application_unverified` 回执且应用尚未关联。真实 HTTPS 探测期间中断恢复原 `website.recheck_required` 回执并保留应用关联。两条路径都保持现有 TLS 与上游服务，重启和同键查询未再次 reload。测试使用认证证书路由生成的专属自签证书。公共 CA 与发布的联合中断仍待验收。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-完整认证网站发布中断与恢复)。

未指定证书 ID 时，自动签发使用所选 Nginx 的 WebRoot HTTP-01。发起者须同时具备所选实例的 `NginxConfigurationWrite` 与 `CertificateIssue`（目标 `certificates/issue`），并确认 CA 能通过申请域名的 TCP 80 到达该实例。已启用的集成或已记录 HTTP-01 的同域名站点沿用原挑战路由。

已集成、正在运行且尚无受管站点的 Linux 系统包 Nginx，可在发布时自动建立首站的 IPv4 挑战路由。Helper 必须确认 80 和 443 当前均未被监听，且磁盘配置也没有占用这两个端口。临时路由仅提供原域名的挑战目录，其余请求返回 404。签发成功后由同一发布将临时配置替换为站点 TLS 配置，保留站点的 HTTP-01 路由用于续期。无法核实前置条件时，在创建 CA 订单前返回 `website.acme_webroot_required`。

临时配置写入前，发布台账已记录原操作的站点责任。原生重载中断后，同键取回原回执。启动恢复保留临时配置及原 Helper 证据，新发布和直接覆盖仍受核验门禁约束。

具备应用部署管理权限和目标 Nginx 配置权限的操作者，可向原发布发送 `POST /api/v1.0/website-publications/operations/{operationId}/cleanup-bootstrap`。清理须确认原生重载结果、原文件内容和证书任务已结束。原回执追加 `bootstrapCleanupOperationId` 与 `bootstrapCleanedAt`。清理请求或 Server 中断时，再次提交同一路径接续同一清理，原发布状态保持。已确认的 CA 验证拒绝在挑战清理后走同一流程。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站临时路由的授权清理)。

同等授权下，`POST /api/v1.0/website-publications/operations/{operationId}/resume-bootstrap` 可用本地已成功落盘的原证书完成 TLS 绑定。原中断阶段必须为证书阶段，当前应用和临时路由须通过核验。原回执保留中断状态，并记录 `bootstrapResumeStartedAt`、`bootstrapBindingOperationId` 与 `bootstrapResumedAt`。已完成续接的重复请求读取原回执。

绑定期间再次中断时，再次提交续接会核对原绑定 ID 的 Helper 成功回执、正式配置摘要、原证书指纹、应用上游和原备份。证据匹配时只补写站点与应用关联并清理备份，原生重载和 CA 请求无新增。缺少证据或内容已变更时返回 `website.recheck_required`，保留原配置和回执。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站续接绑定的原生回执对账)。

自动建立 WebRoot 的首次正式 TLS 绑定也记录 `bootstrapBindingOperationId`。首次绑定重载期间中断后，同一续接 API 使用原成功回执补写本地关联。正常首站发布返回 Succeeded，保留绑定 ID。见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-首站首次-tls-绑定的原生回执对账)。

发布进行期间，同一应用或同一 Nginx 实例内的同一域名只允许一个发布操作。不同应用尝试同时发布到该域名会收到 `409 / website.operation_conflict`。原请求使用同一个幂等键仍可读取原回执。

首个独立 Nginx 站点可先保存 HTTP 配置，再执行实例的 `EnableAcmeHttp01` 操作。空实例会返回 `webserver.acme_no_managed_sites`。若启用操作在原生重载期间中断，同键查询返回原操作回执。后续签发需先独立核验该域名的 HTTP-01 路由，再发起新的证书请求，签发成功后可将证书绑定到原站点。

Linux 系统包 Nginx 的 WebRoot 启用中断后，Server 用原站点 Helper 回执和配置摘要核对清理。证据不足时保留原操作待核验，站点更新返回 `webserver.site_save_failed`。已派发站点核对完成后，未派发的站点保持原配置，父操作报告中断。见[WebRoot 逐站点回执](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-webroot-逐站点回执与清理恢复)。

向 `POST /api/v1.0/webservers/operations/{operationId}/compensate-webroot` 提交 `{"confirmed":true}`，可为已有 Helper 失败回执的 Linux 系统包 WebRoot 操作发起具名补偿。需要原实例的 Nginx 配置写入授权。原生作业须空闲，活动文件与备份须匹配原摘要，且暂存文件须不存在。受理和重复请求均返回原父操作及同一个 `compensationOperationId`。核验补偿成功并清理后，原操作仍为 Failed，问题码为 `webserver.site_reload_failed`。证据不足时操作继续待核验。见[WebRoot 具名补偿](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-webroot-已知拒绝的具名补偿)。

签发和清理完成后才进入站点配置。管理进程在签发中断时，原发布与证书回执可关联查询，证书台账负责清理原挑战文件和查询原 CA 订单。同键请求返回原发布回执。实机证据见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布-webroot-签发与中断恢复)。

取消原证书操作后，网站发布等待证书回执终态，并按实际结果结束。若在签发阶段停止 Server，原证书任务继续排空，启动恢复将未完成发布标为 Interrupted。已知的原订单仍可通过原证书操作只读查询。

专属 WebRoot 挑战文件内容无法与原摘要匹配时，证书操作保留清理责任和原订单。网站发布同键仍返回原中断回执，现有 Nginx 与 TLS 继续服务。见[挑战文件归属验收](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-webroot-挑战内容变化时保留原清理责任)。

网站发布及具名首站恢复按需加载执行程序集。实际执行和终态提交结束、最后一个作用域释放后进入 30 秒闲时卸载窗口。站点转发、TLS 与原回执继续由 Nginx 和 Server 原服务负责。见[网站发布独立执行程序集](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-网站发布独立执行程序集)。

获准的直接 Nginx 重载与网站发布共享按需加载的 WebServer 执行代次。命令、原终态和 Helper 回执清理全部完成后，最后一个作用域释放并开始闲时计时。原 ID 的同键查询和启动对账由 Server 台账处理。闲时执行代码卸载期间，Nginx 保持站点转发和 TLS。见[直接重载独立执行程序集](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-直接-nginx-重载独立执行程序集)。

显式启用 HTTP-01 按需加载 WebServer 执行代码。Linux 系统包 Nginx 的每个站点以独立 Helper ID 跟踪 reload。应答丢失时，Server 按原 ID 查询并核对备份，原父操作可通过同键继续读取。

Linux Nginx 集成在锚点提交前保存原操作与主配置快照。Server 重启后按原 ID 查询 Helper，并核对当前锚点与快照。同键请求返回原回执。缺少成功证据或配置漂移时保持待核验，拒绝同实例新的后台变更。见[Nginx 集成检查点](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-nginx-集成检查点与执行代次)。

可向 `POST /api/v1.0/webservers/operations/{operationId}/compensate-integration` 提交 `{"confirmed":true}`，为已由 Helper 明确记录失败的原集成操作发起具名补偿。需要原实例的 Nginx 配置写入授权。Server 核对锚点、原快照、站点依赖和原生作业状态。受理后原回执关联唯一 `compensationOperationId`，重复请求读取同一回执。成功补偿保留原 Failed 状态并返回 `webserver.integration_compensated`。缺少证据时原操作继续待核验。见[集成具名补偿](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md#mb-05-已知-nginx-集成失败的具名补偿)。
