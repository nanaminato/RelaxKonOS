# RelaxKonOS 证书管理器设计

> MB-05A（2026-11-04）：证书后台操作已有实际 Task owner、真实 resident 工作租约与 Host 排空，续期扫描也持有自己的执行保护。取消 API 只记录 `cancel-requested` 并通知动作，动作与终态持久化实际结束后才报告 Cancelled；未知挑战清理或终态写入失败保留租约、隔离该代并阻止新副作用。Anvil 不支持取消的调用直接等待真实 Task 后核验取消，不以 `WaitAsync` 丢弃网络工作；挑战发布前即登记清理责任，清理失败不能当作成功签发。Core TLS 存储、Kestrel 选择器与已部署 HTTPS 仍独立存续。此阶段不等于管理域休眠、真实 CA 未知结果恢复或生产内存收益，整体状态由[模块边界设计](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md)拥有。

> 此阶段的启动恢复由显式 HostedService StartAsync 在工作租约内执行；构造与禁用检查不改写操作账本。HTTP 准入取消和后台动作停止分开，断开已受理请求不会丢失动作。桌面点击取消后继续观察原 OperationId 的实际终态，停止本地观察只表示结果未知，不显示为服务端 Cancelled。Android 交互与验收由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/README.md) 拥有，不在本文件复制。

> 证书账本恢复失败会记录并隔离证书工作，保持未就绪，相关业务与依赖的网站发布拒绝新任务；不会为了管理域恢复失败而关闭 Core TLS 或其他独立管理面。停机仍会报告未解决责任，不清坏文件、不自动重放。

> **模块归属（2026-10-28）**：ACME 签发、ARI 续期循环、操作账本与 `/api/v1.0/certificates` 归官方模块 `relaxkonos.certificates`，默认启用。**Kestrel 启动证书不随模块走**：`ICertificateStore`、`CertificateDeploymentRepository`、`KestrelCertificateRegistry`、`KestrelCertificateStartupService` 与 HTTP-01 WebRoot 存储留在 Core，禁用模块不会让 HTTPS 监听起不来，已部署到 Kestrel 的证书继续生效。网站发布模块硬依赖本模块，因此单独设置 `Modules:Certificates:Enabled=false` 会让启动失败并给出「requires enabled module」诊断；需两者一起关闭。详见[模块设计实施记录](../architecture/RelaxKonOS.ModulesAndExtensions.Design.md#certificate-与-webserver-模块2026-10-28)。

> 状态：**后端核心 V1 与基础管理界面已实现，待目标平台验收**。已提供 ACME v2、HTTP-01（Direct/WebRoot）、受保护的 PEM 存储、RSA/ECDSA、Kestrel 部署、ARI 优先的自动续期、HostGlobal SQLite 元数据，以及包含概览/证书列表和申请对话框的客户端界面。DNS-01、Wildcard、IIS/Nginx/Apache 部署仍属后续阶段；Ubuntu 24.04 与 Windows Server 的真实 CA/监听/ACL 验收仍须在目标主机执行。

## 1. 项目背景

RelaxKonOS 是一个**仅管理当前主机**的服务器管理程序，后端采用 **.NET 10**，客户端采用 **Avalonia**，通过 HTTP/HTTPS 远程连接 RelaxKonOS 服务端。

当前设计目标：

- RelaxKonOS 只管理自己所在的服务器，不管理其他远程服务器。
- 支持 **Linux** 和 **Windows**。
- 支持自动申请、安装和续期 TLS 证书。
- 优先支持 ACME 协议，例如 Let's Encrypt。
- RelaxKonOS 本身可以运行在 `443`、`8443` 或其他 HTTPS 端口。
- 不要求 RelaxKonOS 永久占用 TCP 80 端口。
- 应兼容服务器上已经存在的 IIS、Nginx、Apache、Caddy 等 Web Server。
- 后续可以扩展 DNS-01、Wildcard 证书和更多 ACME CA。

---

## 2. 总体设计原则

RelaxKonOS 的证书管理器应定位为：

> **本机证书生命周期管理器（Local Certificate Lifecycle Manager）**

不需要采用 Controller / Agent、多服务器调度、跨服务器私钥分发等复杂架构。

推荐总体结构：

```text
Avalonia Client
       │
       │ HTTPS / HTTP
       ▼
┌───────────────────────────┐
│      RelaxKonOS Server      │
│         .NET 10           │
│                           │
│  ┌─────────────────────┐  │
│  │ Certificate Manager │  │
│  └──────────┬──────────┘  │
│             │             │
│        ACME Client        │
│             │             │
└─────────────┼─────────────┘
              │
              ▼
        ACME Certificate
            Authority
```

核心原则：

1. **私钥始终在本机生成和保存。**
2. **ACME 协议层与 RelaxKonOS 业务层解耦。**
3. **证书管理与证书部署分离。**
4. **HTTP-01 不要求 RelaxKonOS 自己永久监听 80。**
5. **Windows / Linux 尽量采用统一存储模型。**
6. **自动续期优先采用 ACME ARI，而不是固定“剩余 30 天续期”。**
7. **不要把 Let's Encrypt 写死，应通过 ACME Directory URL 支持不同 CA。**

---

## 3. 推荐技术选型

### 3.1 ACME

推荐：

```text
Webprofusion.Certify.ACME.Anvil
```

但 RelaxKonOS 不应让业务代码直接依赖该库。

建议定义自己的抽象：

```csharp
public interface IAcmeService
{
    Task<AcmeOrder> CreateOrderAsync(
        IReadOnlyCollection<string> identifiers,
        CancellationToken cancellationToken);

    Task<AcmeChallenge> GetChallengeAsync(
        Guid orderId,
        AcmeChallengeType type,
        CancellationToken cancellationToken);

    Task ValidateAsync(
        Guid orderId,
        CancellationToken cancellationToken);

    Task<byte[]> FinalizeAsync(
        Guid orderId,
        byte[] csr,
        CancellationToken cancellationToken);

    Task<RenewalInfo?> GetRenewalInfoAsync(
        X509Certificate2 certificate,
        CancellationToken cancellationToken);
}
```

具体实现内部再调用 Anvil。

这样未来即使替换 ACME SDK，RelaxKonOS 其他模块也不需要修改。

---

## 4. 模块结构

建议第一版保持适度简单：

```text
RelaxKonOS.Server
│
├── Api
│
├── Certificate
│   │
│   ├── ICertificateManager.cs
│   ├── CertificateManager.cs
│   ├── IAcmeService.cs
│   ├── AnvilAcmeService.cs
│   ├── CertificateStore.cs
│   ├── CertificateRenewalWorker.cs
│   │
│   └── Challenges
│       ├── IHttp01ChallengeProvider.cs
│       ├── DirectHttp01ChallengeProvider.cs
│       ├── WebRootHttp01ChallengeProvider.cs
│       └── IDns01ChallengeProvider.cs
│
├── Security
│
└── Platform
    ├── Windows
    └── Linux
```

不要在第一版拆出过多接口和服务。

当功能增加后，再进一步拆分：

```text
Certificate
├── Acme
├── Challenges
├── Storage
├── Renewal
└── Deployment
```

---

## 5. 证书签发流程

推荐流程：

```text
Avalonia Client
       │
       │ 申请 example.com
       ▼
RelaxKonOS API
       │
       ▼
CertificateManager
       │
       ├── 创建 / 加载 ACME Account
       │
       ├── 创建 ACME Order
       │
       ├── 完成域名验证
       │
       ├── 本机生成 Private Key
       │
       ├── 生成 CSR
       │
       ├── Finalize Order
       │
       ├── 下载 Certificate Chain
       │
       ├── 保存证书
       │
       └── 更新 HTTPS 服务
       │
       ▼
Avalonia Client
    显示签发结果
```

私钥绝不需要离开当前服务器。

---

## 6. HTTP-01 设计

### 6.1 80 端口的真正要求

HTTP-01 的关键要求是：

> ACME CA 必须能够通过公网访问
> `http://example.com/.well-known/acme-challenge/{token}`

外部访问端口固定为：

```text
TCP 80
```

但这**不代表 RelaxKonOS 必须自己监听 80**。

RelaxKonOS 可以正常运行在：

```text
https://example.com:8443
```

而 HTTP-01 验证由其他服务的 80 端口完成。

---

## 7. IIS / Nginx 在 HTTP-01 中的角色

IIS / Nginx 本身通常**不会主动与 CA 协商证书**。

真正与 Let's Encrypt 等 CA 通信的是：

```text
RelaxKonOS ACME Client
```

IIS / Nginx 的任务只是：

> 把 RelaxKonOS 生成的 challenge 内容通过公网 80 端口返回给 CA。

流程如下：

```text
RelaxKonOS ACME Client
        │
        │ 创建 ACME Order
        ▼
Certificate Authority
        │
        │ 返回 challenge
        ▼
RelaxKonOS
        │
        │ 写入 token
        ▼
IIS / Nginx :80
        │
        │ 响应：
        │ /.well-known/acme-challenge/{token}
        ▼
Certificate Authority
```

因此：

```text
Nginx / IIS 占用 80
```

并不会阻止 RelaxKonOS 申请证书。

---

## 8. HTTP-01 的三种实现模式

建议 RelaxKonOS 支持以下三种模式。

| 模式 | RelaxKonOS 自己监听 80 | 说明 |
|---|---:|---|
| Direct | 是，临时 | 80 空闲时由 RelaxKonOS 临时监听 |
| WebRoot / WebServer | 否 | IIS / Nginx 等已有 Web Server 提供 challenge |
| DNS-01 | 否 | 完全不依赖 80 端口 |

推荐默认提供：

```text
验证方式

● 自动
○ HTTP-01
○ DNS-01
```

---

## 9. Direct HTTP-01

当 TCP 80 空闲时，RelaxKonOS 可以临时监听：

```text
0.0.0.0:80
```

只处理：

```text
/.well-known/acme-challenge/*
```

流程：

```text
RelaxKonOS
   │
   ├── 临时监听 :80
   │
   ├── 返回 ACME token
   │
   ├── CA 完成验证
   │
   └── 释放 :80
```

RelaxKonOS 主服务仍可以运行：

```text
:8443
```

这种方式适合作为一个 fallback，但不应成为唯一实现。

原因包括：

- Linux 低端口权限问题。
- Windows 服务权限。
- 防火墙。
- 已有 Web Server 占用端口。
- Docker / NAT 等网络环境。

---

## 10. WebRoot HTTP-01

当服务器已经运行：

```text
Nginx
IIS
Apache
```

推荐采用 WebRoot 模式。

RelaxKonOS 创建一个固定目录：

### Linux

```text
/var/lib/relaxkonos/acme-challenge/
```

### Windows

```text
C:\ProgramData\RelaxKonOS\acme-challenge\
```

RelaxKonOS 只需要：

```text
token
    ↓
写入文件
```

例如：

```text
acme-challenge/
├── abc123
├── def456
└── ...
```

定义接口：

```csharp
public interface IHttp01ChallengeStore
{
    Task PutAsync(
        string token,
        string keyAuthorization,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        string token,
        CancellationToken cancellationToken);
}
```

---

## 11. Nginx 示例

可以让 Nginx 将 ACME 路径映射到 RelaxKonOS 的 challenge 目录。

```nginx
server {
    listen 80;
    server_name remote.example.com;

    location /.well-known/acme-challenge/ {
        root /var/lib/relaxkonos;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}
```

关键目标是让：

```text
http://remote.example.com/.well-known/acme-challenge/{token}
```

能够返回 RelaxKonOS 写入的：

```text
keyAuthorization
```

---

## 12. DNS-01

DNS-01 应作为 RelaxKonOS 的重要后续能力。

它完全不依赖：

```text
TCP 80
TCP 443
```

RelaxKonOS 可以运行：

```text
https://remote.example.com:8443
```

同时通过 DNS TXT Record 完成验证：

```text
_acme-challenge.remote.example.com
        ↓
TXT <challenge-value>
```

DNS-01 适合：

- 80 端口无法暴露。
- 服务器位于 NAT 后。
- 使用非标准 HTTPS 端口。
- 需要 Wildcard Certificate。
- 不希望修改 IIS / Nginx 配置。

建议抽象：

```csharp
public interface IDns01ChallengeProvider
{
    Task PresentAsync(
        string domain,
        string value,
        CancellationToken cancellationToken);

    Task CleanupAsync(
        string domain,
        string value,
        CancellationToken cancellationToken);
}
```

后续可以实现：

```text
CloudflareDnsProvider
AliyunDnsProvider
TencentCloudDnsProvider
Route53DnsProvider
AzureDnsProvider
```

---

## 13. 自动验证策略

可以设计：

```text
                   Auto
                    │
              Port 80 free?
               /          \
             Yes          No
              │            │
        Direct HTTP-01   已有 Web Server?
                         /           \
                       Yes           No
                        │             │
                   WebRoot         DNS-01
```

注意：

RelaxKonOS 不一定能够安全、可靠地自动修改所有 IIS / Nginx 配置。

因此第一版更适合：

```text
Direct HTTP-01
+
WebRoot HTTP-01
+
手动配置 Web Server
```

后续再开发：

```text
Nginx 自动配置
IIS 自动配置
Apache 自动配置
```

---

## 14. 证书与端口的关系

证书本身与端口无关。

例如证书包含：

```text
remote.example.com
```

它可以用于：

```text
https://remote.example.com:443
https://remote.example.com:8443
https://remote.example.com:9443
```

证书验证的是：

```text
Hostname
```

不是：

```text
Hostname + Port
```

HTTP-01 固定使用 80，仅仅是 ACME 的**域名所有权验证过程**。

---

## 15. Windows / Linux 跨平台存储策略

不建议把 Windows Certificate Store 作为 RelaxKonOS 的核心证书存储方式。

推荐使用统一的文件存储模型。

### Linux

```text
/var/lib/relaxkonos/
└── certificates/
```

### Windows

```text
C:\ProgramData\RelaxKonOS\
└── certificates\
```

目录示例：

```text
certificates/
│
├── acme/
│   └── account.key
│
└── remote.example.com/
    ├── private.key
    ├── certificate.pem
    ├── chain.pem
    ├── fullchain.pem
    └── metadata.json
```

推荐内部 canonical format：

```text
PEM
```

---

## 16. 私钥保护

### Linux

建议：

```text
Directory: 700
Private Key: 600
```

并确保 owner 为：

```text
relaxkonos
```

或 RelaxKonOS service account。

### Windows

使用 NTFS ACL，仅允许：

```text
RelaxKonOS Service Account
SYSTEM
```

访问私钥目录。

原则：

> 私钥不应通过 RelaxKonOS API 返回给 Avalonia 客户端。

正常管理接口只返回：

```text
Domain
Issuer
Serial Number
Not Before
Not After
Status
Thumbprint
Renewal Status
```

---

## 17. Certificate Store

建议统一封装：

```csharp
public interface ICertificateStore
{
    Task SaveAsync(
        ManagedCertificate certificate,
        CancellationToken cancellationToken);

    Task<ManagedCertificate?> GetAsync(
        Guid certificateId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedCertificate>> ListAsync(
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid certificateId,
        CancellationToken cancellationToken);
}
```

底层：

```text
Windows
   │
   └── FileCertificateStore

Linux
   │
   └── FileCertificateStore
```

尽量避免核心逻辑出现：

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

平台差异应尽量封装在：

```text
Platform/
```

模块中。

---

## 18. 证书部署

证书签发和证书部署应保持逻辑分离。

### Certificate Manager

负责：

```text
ACME
↓
Private Key
↓
CSR
↓
Certificate
```

### Deployment

负责：

```text
Certificate
      │
      ├── RelaxKonOS Kestrel
      ├── Nginx
      ├── IIS
      └── Apache
```

第一版只需要支持：

```text
RelaxKonOS 自身 HTTPS
```

后续再考虑：

```text
Nginx Deployment
IIS Deployment
Apache Deployment
```

---

## 19. RelaxKonOS 自身 HTTPS

RelaxKonOS 可以使用：

```text
certificate.pem
private.key
```

或转换为：

```text
certificate.pfx
```

供 Kestrel 使用。

建议 CertificateManager 完成续期后通知 HTTPS 层：

```text
Certificate Updated
        │
        ▼
Reload Certificate
```

应优先采用可热更新的方式。

如果无法热更新，再考虑：

```text
Graceful Restart
```

不要让证书续期过程直接强制终止 RelaxKonOS。

---

## 20. ACME Account

建议独立保存 ACME Account：

```text
AcmeAccount
├── DirectoryUrl
├── AccountUrl
├── ContactEmail
├── AccountKey
└── CreatedAt
```

其中 Account Key 同样属于敏感信息。

不要每次签发证书都创建新的 ACME Account。

---

## 21. 不要写死 Let's Encrypt

错误设计：

```csharp
public class LetsEncryptService
{
}
```

推荐：

```csharp
public interface IAcmeService
{
}
```

配置：

```json
{
  "Certificate": {
    "Acme": {
      "DirectoryUrl": "https://acme-v02.api.letsencrypt.org/directory"
    }
  }
}
```

以后可以支持：

```text
Let's Encrypt
ZeroSSL
Google Trust Services
Private ACME CA
Enterprise CA
```

---

## 22. 自动续期

不要使用简单逻辑：

```text
每天检查一次
↓
证书剩余 < 30 天
↓
Renew
```

推荐采用：

```text
ACME ARI
```

即：

```text
ACME CA
   │
   │ 返回建议续期窗口
   ▼
RelaxKonOS
   │
   ├── RenewalWindowStart
   └── RenewalWindowEnd
```

RelaxKonOS 在窗口中选择合适时间执行续期。

---

## 23. Renewal Worker

推荐结构：

```text
CertificateRenewalWorker
          │
          ▼
      Load Certificate
          │
          ▼
       Query ARI
          │
          ▼
    Renewal Window?
       /       \
     Yes       No
      │         │
  Schedule   Fallback
      │
      ▼
     Renew
      │
   ┌──┴───┐
   │      │
 Success Failure
   │      │
 Deploy  Retry
   │      │
 Reload Alert
```

即使支持 ARI，也应保留：

```text
NotAfter
```

作为兜底机制。

---

## 24. 重试策略

续期 Worker 在服务启动约一分钟后读取持久扫描台账。到期时才创建续期执行作用域，已完成扫描的下一次计划为完成后一天。重启时未完成的扫描继续使用原扫描 ID 和固定准入键，按原操作台账找回已经准入但尚未关联的回执。扫描和作用域清理完成后才落盘完成时间。

扫描本身发生错误时保留原扫描，稍后从原状态继续。已准入的续期继续受原操作回执、退避和未知结果门禁约束。分钟级退避会在下一次每日扫描时重新核对。

读取 ARI 发生传输错误且无法取得有效窗口时，扫描按 `NotAfter` 兜底判断。若进程在 ARI 读取期间退出，启动后沿用原扫描 ID，必要时重新读取只读 ARI 资源。续期成功后的 ARI 替换通知属于提示，通知失败时保留成功的替代证书与原回执。见[ARI 断流与原扫描恢复](../architecture/server-module-evidence/Certificates.md#mb-05-ari-断流与原扫描恢复)。

ACME 请求失败时，应支持：

```text
Exponential Backoff
Jitter
Retry-After
Maximum Retry Count
```

例如：

```text
1 min
2 min
4 min
8 min
...
```

再增加随机 jitter，避免所有实例在同一时刻重试。

如果 CA 返回：

```text
Retry-After
```

应优先尊重该值。

---

## 25. 建议的数据模型

第一版不需要复杂数据库。

可以采用：

```text
CertificateRecord
────────────────────────
Id
PrimaryDomain
SANs
Issuer
SerialNumber
Thumbprint
NotBefore
NotAfter
Status
CertificatePath
PrivateKeyPath
CreatedAt
UpdatedAt
RenewalWindowStart
RenewalWindowEnd
LastRenewalAt
LastRenewalStatus
```

以及：

```text
AcmeAccountRecord
────────────────────────
Id
DirectoryUrl
AccountUrl
ContactEmail
AccountKeyPath
CreatedAt
```

如果 RelaxKonOS 已经有 SQLite 等内部数据库，则建议元数据进入数据库，证书和私钥仍然保存为文件。

---

## 26. 建议的状态模型

Certificate：

```text
Pending
Validating
Issued
Active
Renewing
Failed
Expired
Revoked
```

ACME Order：

```text
Pending
Ready
Processing
Valid
Invalid
```

UI 不应该只显示：

```text
Success / Failed
```

应该让用户知道失败发生在哪一步。

---

## 27. Avalonia 管理界面

管理器采用与 Docker 管理器一致的“左侧导航 + 右侧工作区”多页结构，当前页面为：

```text
概览
证书
续期记录
```

概览展示受管理证书数量、刷新状态和申请入口；证书页展示列表，并按当前选择提供部署、续期、吊销和删除操作。详情、部署目标、续期历史、ACME 与 DNS 设置应以新增页面加入导航，而不是继续堆叠到列表页面。

“申请新证书”使用模态对话框。域名、联系邮箱、验证类型、密钥算法、条款和公网可达性确认均在可滚动的内容区内；预检结果与签发进度也显示在该区域。预检、申请证书和取消按钮固定在对话框右下角、滚动区之外。取消会请求取消服务端 `OperationId`，不会仅停止客户端轮询。

证书页面可以展示：

```text
证书

域名
remote.example.com

状态
有效

签发机构
Let's Encrypt

有效期
2026-08-01 ～ 2026-10-30

自动续期
已启用

验证方式
HTTP-01 / WebRoot

HTTPS 端口
8443
```

操作：

```text
[申请证书]
[立即续期]
[重新部署]
[删除]
[查看详情]
```

高级设置：

```text
ACME Directory
Challenge Type
WebRoot Path
DNS Provider
Key Algorithm
Renewal Policy
```

---

## 28. 第一版推荐范围

### V1

实现：

```text
ACME v2
HTTP-01
Direct HTTP-01
WebRoot HTTP-01
PEM Certificate Store
RSA / ECDSA
RelaxKonOS HTTPS Deployment
Automatic Renewal
ARI
```

目标：

> RelaxKonOS 可以独立为自己的 HTTPS 服务申请和自动续期证书。

---

## 29. 第二阶段

增加：

```text
DNS-01
Wildcard Certificates

Cloudflare
Aliyun
Tencent Cloud
Route53
Azure DNS
```

此阶段解决：

```text
*.example.com
80 无法开放
NAT
非标准网络环境
```

---

## 30. 第三阶段

增加：

```text
Nginx 自动集成
IIS 自动集成
Apache 自动集成
Caddy 检测
多 ACME CA
私有 CA
证书导入 / 导出
证书吊销
证书监控 / 告警
```

---

## 31. 最终推荐架构

```text
                         Avalonia
                            │
                            │ HTTPS
                            ▼
                    ┌───────────────┐
                    │   RelaxKonOS    │
                    │    .NET 10    │
                    └───────┬───────┘
                            │
                   CertificateManager
                            │
             ┌──────────────┼──────────────┐
             │              │              │
             ▼              ▼              ▼
        AcmeService   ChallengeProvider  CertificateStore
             │              │              │
             │       ┌──────┴──────┐       │
             │       │             │       │
             │    HTTP-01       DNS-01     │
             │       │             │       │
             │   ┌───┴────┐        │       │
             │   │        │        │       │
             │ Direct   WebRoot   DNS API   │
             │                              │
             └──────────────┬───────────────┘
                            │
                            ▼
                    Deployment Service
                            │
                            ▼
                     RelaxKonOS HTTPS
```

---

## 32. 最重要的设计结论

### 证书签发

```text
RelaxKonOS
   ↓
ACME Client
   ↓
Certificate Authority
```

---

### HTTP-01

外部验证必须经过：

```text
TCP 80
```

但：

> **不要求 RelaxKonOS 自己占用 TCP 80。**

已有 IIS / Nginx 可以继续监听 80。

RelaxKonOS 只需要让：

```text
/.well-known/acme-challenge/*
```

能够返回正确 challenge。

---

### DNS-01

```text
完全不依赖 80 / 443
```

因此是 RelaxKonOS 后续非常重要的能力。

---

### 私钥

```text
只在当前服务器生成
↓
只保存在当前服务器
↓
不返回给 Avalonia Client
```

---

### 跨平台

统一：

```text
PEM + File Certificate Store
```

避免让 Windows Certificate Store 成为核心依赖。

---

### 自动续期

优先：

```text
ACME ARI
```

而不是：

```text
固定剩余 30 天续期
```

同时保留证书到期时间作为 fallback。

---

## 33. 推荐开发顺序

```text
1. IAcmeService
2. AnvilAcmeService
3. CertificateStore
4. Private Key / CSR
5. HTTP-01 Direct
6. HTTP-01 WebRoot
7. CertificateManager
8. Kestrel HTTPS Deployment
9. Renewal Worker
10. ARI
11. DNS-01
12. DNS Provider Plugins
13. IIS / Nginx 自动集成
```

这样可以避免一开始为了 IIS、Nginx、DNS Provider 等外围能力拖慢核心证书流程的实现。

---

## 34. 总结

RelaxKonOS 当前只管理本机，因此证书管理功能应保持为一个**本地化、跨平台、协议解耦**的证书生命周期模块。

推荐最终方案：

> **RelaxKonOS 自身作为 ACME 客户端，使用 Anvil 实现 ACME 协议，本机生成和保存私钥，以 PEM 作为统一证书格式；HTTP-01 支持临时监听 80 和 WebRoot 两种方式，已有 IIS / Nginx 只负责暴露 challenge，而不是负责与 CA 协商；后续增加 DNS-01 和 Wildcard；续期采用 ARI 驱动，并保留到期时间兜底。**

该方案既能够满足 RelaxKonOS 当前“单机服务器管理器”的需求，也为未来扩展更多 Web Server、DNS Provider 和 ACME CA 保留了清晰的边界。

---

## 35. 落地约束（当前管理员模式）

### 35.1 操作者与高权限

证书列表选中证书后，点击“续期记录”打开独立窗口，按证书 ID 读取完整续期任务历史，显示自动/手动、开始/完成时间、状态与失败原因。未选择证书时按钮禁用，窗口打开后固定查看原证书，刷新重新读取服务器记录。列表不再内嵌折叠历史。

左侧“续期记录”页显示自动续期每次扫描和手动续期的执行时间、完成时间、成功、失败、进行中及取消数量。自动扫描记录独立持久化，按实际操作 ID 去重，并从持久任务账本计算结果；扫描已结束但任务仍执行时不显示完成时间。没有到期证书的扫描显示零计数。手动续期每次作为一条执行记录。点击刷新更新统计。

新增 `GET /api/v1.0/certificates/renewal-runs` 和 `GET /api/v1.0/certificates/{id}/renewal-history`；现有 `CertificateDto.renewal` 仍作为证书摘要提供最近五个任务及重试策略。

自签证书不参与自动续期，私钥与 ACME 凭据不进入记录 DTO。历史自动扫描无法从旧任务准确还原，因此新增扫描汇总从升级后的执行开始记录。

RelaxKonOS 面向单台服务器的网站管理员。证书、ACME account 和部署目标均为**当前宿主机全局资源**，客户端入口受声明的证书读取和管理应用权限约束，服务端还验证宿主能力和操作授权。

申请、自签名创建、续期、吊销、删除与服务器 HTTPS 换证均通过 HTTP 入口的精确资源会话授权执行，材料写入服务账号拥有的存储，不依赖管理员 Server 进程。申请使用 `CertificateIssue` / `certificates/issue`；续期、吊销、删除分别使用 `CertificateRenew`、`CertificateRevoke`、`CertificateDelete` / `certificates/{id:D}/renew|revoke|delete`。不同动作和目标的授权不能互用。Direct HTTP-01 预检仍要求实际绑定 TCP 80 的权限。

读取元数据和 Kestrel 部署事实仍按当前宿主 feature/JWT 提供；客户端不得伪造提权 capability、收集 sudo/UAC/服务账号密码或把 HTTP API 变成任意命令执行入口。私钥永不出现在 DTO、日志、审计或错误详情中。

### 35.2 HTTP-01 可用性预检

“本机 80 端口空闲”不是 Direct HTTP-01 的充分条件。每次申请前需进行预检并返回结构化结果：

- 域名格式、IDN/Punycode 规范化、重复 SAN 和 wildcard 规则校验；wildcard 只能选择 DNS-01。
- 解析 A 与 AAAA 记录；任一会被 CA 访问的地址都必须能正确提供 token。
- 检查 TCP 80 的监听权限、占用、宿主防火墙、云防火墙/NAT/CDN 或上游反向代理。
- Direct 模式仅在管理员权限可用时短暂监听，并且仅路由 `/.well-known/acme-challenge/{token}`；签发完成、取消或超时后确定性释放监听器和 token。
- WebRoot 模式在写入后读取回 token，并在 CA 验证结束后清理；已有 Web Server 的重定向、默认站点和 IPv6 路由必须在预检中显示，不假定本地路径映射一定可公网访问。

预检不能伪造“公网可达”的保证。DNS、NAT 或 CDN 状态无法确定时，UI 必须标识为需要管理员确认，并允许选择 WebRoot 或 DNS-01。

### 35.3 Kestrel 启动、部署与轮换

Kestrel 部署必须先实现，不能只在签发成功后尝试替换文件。实现应明确以下闭环：

1. 首次启动没有受信证书时，按部署配置运行 HTTP、管理员提供的初始证书或显式开发证书；不得宣称已经提供受信 HTTPS。
2. 证书文件写入版本目录后完成权限设置和完整性校验，再以原子指针/rename 切换“当前版本”；不可覆盖正在使用的文件。
3. HTTPS 层通过受控的证书选择器或等价机制读取当前完整版本。新连接使用新证书，已有连接自然结束；若运行时不支持热加载，执行有健康检查和回退的 graceful restart。
4. 部署失败保留前一可用版本，记录稳定问题码与关联操作 ID；证书签发成功不等同于部署成功。
5. 一张证书可具有多个 DNS 名称和部署目标；SNI、端口、绑定、目标版本和最后一次健康检查均应作为部署元数据保存。

当前部署查询 `GET /api/v1.0/certificates/{id}/deployments/kestrel` 返回 `KestrelCertificateDeploymentDto`：元数据存在、HTTPS 配置、选择器注册/默认、实际 SNI 名称、运行时材料 SHA-256 与有效期、观察时间；无私钥、PEM 或服务器文件路径。已删除/未知 ID 也返回事实，区分元数据与运行时注册，不将签发状态当作部署状态。

部署要求 Issued/Active 且当前有效，选择器校验实际材料私钥与有效期。精确名称优先，再匹配单层 DNS 通配符，最后使用默认证书。服务器 HTTPS 换证显式切换并持久化默认选择；其他证书续期保持原默认，删除默认后选择剩余证书。新连接使用新选择，已建立连接可保留旧材料。启动恢复跳过已撤销记录，失败不替换现有有效选择。实际选择器事实不证明公网前端或客户端信任；部署不改变监听地址/端口，也不改客户端 URL 或证书信任。

Kestrel 随选中的活动叶证书发送对应中间链。Linux URL 与命名 HTTPS 端点通过仅信任私有 CA 根的握手验收，覆盖续期期间的旧连接、替代证书的新握手，以及确认撤销后对新握手的影响。Windows 托管 TLS 和 HTTP/3 仍待实机验证。见[Kestrel 动态证书链](../architecture/server-module-evidence/Certificates.md#mb-05-kestrel-动态证书链)。

Nginx 的稳定 PEM 文件随续期更新。使用 SQLite 并启用 WebServer 管理模块时，协调器会为绑定该证书的受管实例提交独立重载。原续期与原重载各有操作回执，证书指纹变化本身不能确认重载终态。见[Nginx 续期自动部署](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-nginx-续期自动部署)。

停用 WebServer 管理模块期间，成功续期仍在证书台账保留待部署版本。重新启用后协调器可接续原续期。见[准入前进程中断验收](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-nginx-自动部署准入前的进程中断)。

目标已保存而重载未能写入 WebServer 台账时，证书台账保留原续期回执、版本和目标。Linux 实测确认进程重启后接续原回执，CA 无新增订单或签发提交。见[目标检查点恢复](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-nginx-自动部署的目标检查点恢复)。

自动重载已分配持久回执但在派发前中断时，证书续期保留成功回执，部署记录指向原重载的失败终态。见[派发前的原回执恢复](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-nginx-自动重载在派发前的原回执恢复)。

Nginx 的受管证书站点绑定在配置副作用前校验状态/有效期与每个绑定的 SAN 覆盖，IDN/IP 规范化，通配符仅覆盖一层 DNS 名称；宿主 PEM 路径仍由配置语法/文件校验，不能把该模式当作受管元数据已核实。

### 35.4 Protocol 与操作模型

Certificate 模块新增的路由常量、DTO、枚举和 JSON 约定必须位于 `Shared/RelaxKonOS.Protocol/Certificates/`，不得在 UI 或 Endpoint 中硬编码字符串。所有变更请求携带 `Idempotency-Key`，并返回：

```text
OperationId
State: queued | running | succeeded | failed | cancelled
Stage
ProblemCode
StartedAt / CompletedAt
```

签发、续期、部署、导入、删除和撤销属于长任务。Client 可轮询 operation 端点或订阅后续定义的事件契约；关闭窗口、断线和取消不得丢失服务端操作状态。审计记录操作者、目标、确认、结果和 OperationId，但绝不记录私钥、account key、DNS token、CSR 或完整 CA 响应。

当前签发与自签名的请求身份由账号、动作和原 `Idempotency-Key` 决定，不包含每次尝试临时生成的证书 ID；重试返回原证书/操作 ID，不重复创建。续期、撤销、删除和 Kestrel 部署的身份另包含目标证书 ID。客户端必须冻结原请求内容并复用原键；更改请求应等待原任务核实后再建立新意图。取消端点不要求幂等键，按原操作 ID 返回实际状态；预检也不要求幂等键。

撤销进入 `revocation-dispatched` 后，取消返回原回执，CA 响应仍由 Server 排空。CA 已确认而本地清理未完成时返回 `Running / revocation-cleanup-required`，原证书退出 Kestrel 选择器，Server 启动时继续清理对应部署记录。CA 结果未知时返回 `Running / revocation-recovery-required`，保留原部署事实，阻止同一证书的新写入。使用原 operationId 或原键查询，Server 不会再次提交撤销。见[撤销确认与本地恢复](../architecture/server-module-evidence/Certificates.md#mb-05-acme-撤销确认与本地恢复)。

当前只有按操作 ID 查询和取消，没有操作集合或按请求键查询接口。在宿主权限检查后，先查询当前账号/动作/目标/原键的持久账本；已知请求直接返回原任务，再次预检或目标已删除不会遮蔽原结果。首次请求才执行当前预检和目标有效性校验。权限拒绝或记录缺失不能据此推断原任务失败或成功。记录取消不证明副作用已回滚，仍需读取证书及关联服务事实。

### 35.5 持久化、并发与保留期

证书元数据进入 SQLite，PEM 和私钥仍保存在受保护文件系统。新增实体必须通过版本化迁移创建，而不是依赖 `EnsureCreated()`：

```text
certificate_records              certificate_deployment_records
acme_account_records             certificate_operations
certificate_renewal_attempts     certificate_audit_entries
```

记录需包含 schema version、创建/更新时间、当前版本、上次成功版本、部署目标、问题码和乐观并发 revision；对同一 ACME account、同一证书和同一部署目标使用互斥锁，避免手动续期与后台 Worker、重复请求或重试并发执行。挑战文件、失败任务和旧证书版本应定义保留期；删除先解除部署并二次确认，私钥清除使用平台允许的安全删除策略或记录为无法保证物理擦除。

### 35.6 ACME 与秘密管理

- ACME Directory 由管理员配置，生产与 staging account/order 严格分离；首次使用必须确认 CA 条款和联系方式。
- 处理 CA 的 `Retry-After`、速率限制、ARI 不可用、订单失效、撤销和换钥；`NotAfter` 兜底必须有明确的最晚重试截止时间和告警状态。
- 文件型私钥/account key 目录需要同时满足 RelaxKonOS 与实际部署目标（Kestrel/Nginx）的最小读取 ACL。密钥版本、DNS Provider 凭据或导入密码不得写入 SQLite 明文、appsettings、导出包或日志；后续 DNS Provider 使用 OS 安全存储引用。
- 反向代理或 WebRoot 配置中的域名、路径、证书路径均由服务端规范化和白名单校验，拒绝相对路径、符号链接逃逸和 URI 中的凭据。

### 35.7 平台范围与验收

V1 的支持目标为 **Ubuntu 24.04 LTS** 与 **Windows Server 2016 及以上**。实现前分别验证：管理员检测、文件 ACL、短暂 TCP 80 监听、Kestrel 换证、证书目录恢复、IPv4/IPv6 WebRoot、取消/断线恢复和权限不足降级。Anvil 引入前还需在中央包管理中锁定版本，并记录许可证、.NET 10 与两个目标平台的兼容性、离线部署和升级策略。

自签名证书创建在 HTTP 入口验证 `CertificateCreateSelfSigned` 能力、`certificates/self-signed` 精确目标的会话授权。服务器 HTTPS 换证验证 `CertificateReplaceServerHttps` 能力与 `certificates/{id:D}/server-https` 精确证书目标。

所有证书写入动作缺少授权均返回 HTTP 403 / `elevation-required`，桌面与 Android 客户端通过统一提权流程认证后以原幂等键重试一次；不要求 Server 进程以 root/Administrator 运行。

Linux 服务部署在 `server.env` 中设置 `Certificate__StorageRoot=$DATA_ROOT/server/managed-certificates`，目录由服务账号拥有，权限为 `0700`，用于证书管理器创建的 PEM、私钥及版本记录。该目录在 HTTP 和 HTTPS 部署中均创建。

启动用的 `$DATA_ROOT/server/certificates/bootstrap.pfx` 保持 root 所有、服务账号只读，不作为证书管理器的写入目录。已有安装如未配置存储路径，应备份 `server.env` 后补充该配置、创建对应目录并重启 Server；不得通过放宽整个数据根目录权限解决。

Windows 服务与个人模式均在 `appsettings.host.json` 设置 `Certificate.StorageRoot=$DataRoot\server\managed-certificates`。服务模式 ACL 仅授予 SYSTEM、Administrators 和 Server 服务 SID（修改权限）；个人模式授予安装用户、SYSTEM 与 Administrators。管理证书与部署时生成的 `certificates\bootstrap.pfx` 分开存储。

桌面端服务器换证入口位于“设置 → 系统 → 服务器 HTTPS”，证书管理器列表仅提供证书申请、创建、续期、吊销和删除。换证界面展示域名、到期时间和 SHA-256 指纹，提示重连或重新信任证书；只有用户点击“更换服务器 HTTPS 证书”才提交。成功换证同时更新 SNI 绑定与无 SNI 连接使用的默认证书，默认选择独立持久化并在重启时恢复；普通续期保持原默认选择。该操作不会修改部署用启动 PFX，也不会把 HTTP 监听自动改为 HTTPS。


2026-11-06 MB-05 隔离预检：当前共享测试机 PATH 中未发现 Pebble 或 step-ca。真实 ACME 中断恢复需提供可控 ACME 目录、受信 CA 及专属挑战名称和解析。 [验收台账 §50](../architecture/server-module-evidence/Milestones.md#50-mb-0305-隔离测试设施复核2026-11-06)

既有 Linux 测试机通过隔离 Pebble 的真实 HTTP-01 签发和取消。取消持有原工作租约直至实际响应排空，复开台账后同键返回原回执，CA 未收到新增订单。见边界设计 §52。

真实 ACME HTTP-01 请求在途时强制终止操作进程后，原回执会在重启时持久化为 interrupted，同键不会再向 CA 提交订单。见边界设计 §52。

隔离 Pebble 也通过了挑战响应期间进程退出，以及 CA 已签发、下载响应尚未交给 Anvil 时进程退出的恢复验收。两条路径均释放挑战监听器并保留原 operationId 和中断回执。CA 已签发的证书未在本地激活，同键恢复未提交新订单。证据见边界设计 §52。

证书操作的 HTTP 202 在响应执行期间持有证书模块租约。Windows Release 证书绑定与回执专项已通过。

Linux Release 证书 HTTP 绑定与原回执回归已通过。证书响应在真实断线时的排空仍待联合验收。

Linux Release 证书绑定 HTTP 回执已通过真实客户端断线验收。原操作成功后，同键响应在断线后仍持有租约，实际响应退出后归还，重试取回原终态。

自动续期换日后仍查询同一证书的原在途或中断回执。中断回执保留原 operationId，后续明确成功的续期解除自动续期阻止。Linux 私有 Pebble 实测覆盖 CA 已签发、续期进程未接收证书的情况。重启以原键和下一日键查询，原活动证书指纹保持，CA 无新增订单。见[模块边界台账](../architecture/server-module-evidence/Certificates.md#mb-05-证书自动续期跨日接续)。

CA 已接单或签发后遇到无法确认结果的传输失败，证书操作等待真实 SDK Task 和挑战清理，再记录原 operationId 的中断回执。自动续期用下一日键也返回原回执，保留原活动证书。Linux 私有 Pebble 已验证订单和证书响应截断，以及重开台账后的原回执。见[模块边界台账](../architecture/server-module-evidence/Certificates.md#mb-05-证书-ca-传输中断与原回执)。

已有中断回执可用认证的 `GET /api/v1.0/certificates/operations/{operationId}/acme-order` 查询。响应包含原 `operation` 和 `remoteStatus`。状态为 `pending`、`ready`、`processing`、`valid`、`invalid`、`identity-unavailable` 或 `unavailable`。查询使用相同 CA 和原账户密钥，只读取已记录的原订单。订单创建响应丢失且无身份时返回 `identity-unavailable`。远端状态单独呈现，原中断回执与当前证书保持，自动续期继续遵守原门禁。实测见[模块边界台账](../architecture/server-module-evidence/Certificates.md#mb-05-原-ca-订单的只读对账)。

Server 切换到不同 CA 目录后，查询旧订单返回 `identity-unavailable`，同键仍取得原操作回执，活动证书保持。恢复原目录和账户身份后可再次读取同一原订单。见[双 CA 实机验收](../architecture/server-module-evidence/Certificates.md#mb-05-切换-ca-时保留原订单身份)。

已记录同源订单时，Server 在提交 CSR 前为原操作加密保存证书密钥。证书响应未知时保留检查点供核验，原活动证书继续使用。检查点不随操作回执返回。部署原订单证书仍需独立授权与证据。见[原密钥实机证据](../architecture/server-module-evidence/Certificates.md#mb-05-提交-csr-前保存原证书密钥)。

对已有原订单和原密钥检查点的中断回执，可用认证的 `GET /api/v1.0/certificates/operations/{operationId}/acme-order/certificate` 核验。Server 从原 CA 读取已签发证书链，核对原订单的 DNS 名称、叶证书密钥和链路。`verified` 返回原操作及叶证书 SHA-256。其他状态保留原回执。响应只包含核验结果，活动证书与部署保持。见[原证书链实机证据](../architecture/server-module-evidence/Certificates.md#mb-05-按原订单只读补取证书链)。

WebRoot 签发和续期在发布 HTTP-01 文件前，在原证书操作记录挑战文件名及内容摘要。任务等待挑战清理后才提交终态。跨进程恢复在写入原中断回执前清理内容匹配的原文件。路径或内容无法确认时保留文件和恢复责任，并隔离证书工作准入。网站自动签发使用此路径，网站发布与原证书操作的关联同样持久化。CA 返回 `valid` 时仍应通过原发布和证书回执核实本地签发、部署各自的状态。见[模块边界台账](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-网站发布-webroot-签发与中断恢复)。

挑战文件内容已变化时，原回执保持 `Running / challenge-cleanup-required`。重启仍可查询原操作及已知 CA 订单，新证书操作返回 `certificate.challenge_ownership_unconfirmed`。确认文件归属后，原清理在下次启动接续，原操作 ID 保持。见[挑战文件归属验收](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-webroot-挑战内容变化时保留原清理责任)。

自动续期扫描遇到上述阻塞时保留原扫描的待处理状态。隔离 Linux 上的生产 worker 在恢复后沿用同一扫描并关联原续期回执，未新增 CA 订单。见[续期扫描跨阻塞验收](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-续期扫描跨挑战归属阻塞恢复)。

用户取消或 Server 停机时，已记录的 CA 订单仍可按取消回执的原 operationId 只读查询。任务完成并清理挑战后才写入 Cancelled。若被取消的续期已有订单身份，自动扫描沿用原回执，后续经明确授权完成的成功续期才解除该门禁。

Linux 完整认证 Server 已通过真实 CA 断流与重启验收。专属 Nginx 承载 HTTP-01，重启后须重新登录查询原订单。同键重试返回原回执，原活动证书指纹保持，CA 无新增订单或签发提交。见[模块边界台账](../architecture/server-module-evidence/Certificates.md#mb-05-完整认证-server-的-ca-断流与重启)。

Linux 私有 Pebble 实测覆盖自动续期已准入、扫描尚未关联原回执时的进程中断。恢复完成原扫描并持久化下次到期，未提交新订单。见[模块边界台账](../architecture/server-module-evidence/Certificates.md#mb-05-证书续期持久扫描与执行作用域)。

完整 Server 的自然续期停机路径已在 Linux 私有 Pebble 和 Nginx 上通过验收。收到 SIGTERM 后，Server 等待原 CA 挑战响应和清理完成，再提交原取消回执。重启后读取原下次到期时间，保留当前证书和原回执。见[模块边界台账](../architecture/server-module-evidence/Milestones.md#mb-05-完整-server-自然续期与停机排空)。

认证用户取消正在进行的自然续期时，原回执先进入 `Running / cancel-requested`，待挑战清理后成为 Cancelled。Linux 实测中，重启后重复取消返回相同终态，按原 operationId 只读查询原订单返回 `ready`，活动证书与下次扫描时间保持，CA 未创建新订单。见[模块边界台账](../architecture/server-module-evidence/Milestones.md#mb-05-完整-server-自然续期的认证取消)。

认证原订单观察使用一个工作租约，覆盖 CA 调用、HTTP 响应和异步清理。客户端断线或 Server 停机时，仍在途的 CA 调用排空后才释放责任。Linux 私有 CA 已验证断线与停机后按原 ID 读取同一回执，活动证书保持。见[原订单观察](../architecture/server-module-evidence/Certificates.md#mb-05-acme-原订单观察的-http-工作责任)。

ACME SDK 在签发、撤销、订单观察或 ARI 工作开始时按需加载。最后一个 SDK 调用及挑战清理结束 30 秒后可卸载，新工作会唤醒执行代码。原账户、证书存储、回执和 TLS 部署仍由 Server 原服务负责。见[ACME SDK 独立执行程序集](../architecture/server-module-evidence/Certificates.md#mb-05-acme-sdk-独立执行程序集)。

网站发布的自动签发采用所选 Nginx 的 WebRoot HTTP-01 集成。旧 Direct HTTP-01 发布路径曾因缺少经能力授权的生产监听入口而返回 `certificate.port80_elevation_required`。当前路径的签发、挑战清理及中断恢复已通过私有 Pebble 实测。见[模块边界台账](../architecture/server-module-evidence/WebServer-TLS.md#mb-05-网站发布-webroot-签发与中断恢复)。
