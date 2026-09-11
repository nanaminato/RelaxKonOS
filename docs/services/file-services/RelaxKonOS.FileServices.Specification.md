# RelaxKonoS File Services 设计与实现规格

## 1. 目标

为 RelaxKonoS 增加统一的 **File Services（文件服务）** 管理能力。

RelaxKonoS **不实现 SMB、SFTP、FTP/FTPS 协议栈，也不代理实际文件传输流量**。

RelaxKonoS 只负责：

- 检测宿主机文件服务是否可用
- 安装或配置文件服务组件
- 启动、停止、重启文件服务
- 配置监听端口与绑定地址
- 创建与删除共享
- 管理文件服务用户
- 管理共享目录访问权限
- 管理认证方式
- 查看服务运行状态
- 管理必要的防火墙规则
- 对配置进行验证
- 提供统一 UI

实际文件传输由成熟第三方服务完成：

| 协议 | Linux 推荐实现 | Windows 推荐实现 |
|---|---|---|
| SMB | Samba | Windows SMB Server |
| SFTP | OpenSSH Server | OpenSSH Server |
| FTP / FTPS | vsftpd 等 | Windows/IIS FTP 或未来 Provider |

第三方客户端直接连接这些服务，例如：

- Windows Explorer
- macOS Finder
- WinSCP
- FileZilla
- Cyberduck
- `sftp`
- `scp`
- SMB Mount
- NAS / Backup Software

RelaxKonoS.Server 不应参与文件数据流。

---

# 2. 核心架构原则

整体架构：

```text
┌──────────────────────────────┐
│      RelaxKonoS Client       │
│                              │
│     File Services UI         │
└──────────────┬───────────────┘
               │
               │ RelaxKonoS Protocol/API
               ▼
┌──────────────────────────────┐
│      RelaxKonoS.Server       │
│                              │
│ FileServiceManager           │
│ Provider abstraction         │
│ Validation                   │
│ Status                       │
└──────────────┬───────────────┘
               │
               │ Strongly typed privileged request
               ▼
┌──────────────────────────────┐
│ RelaxKonoS.PrivilegedHelper  │
│                              │
│ 修改系统配置                  │
│ 管理 systemd / Windows svc    │
│ 修改 ACL                     │
│ 修改 Firewall                │
└──────────────┬───────────────┘
               │
       ┌───────┼────────┐
       ▼       ▼        ▼
     Samba   OpenSSH   FTP Server
       │       │        │
       └───────┼────────┘
               ▼
          Host FileSystem
```

必须遵守：

> RelaxKonoS.Server 是控制面，不是数据面。

客户端访问：

```text
Third-party Client
       │
       ├── SMB ──────> Samba / Windows SMB
       ├── SFTP ─────> OpenSSH
       └── FTPS ─────> FTP Server
```

不得设计为：

```text
Third-party Client
       ↓
RelaxKonoS.Server
       ↓
SMB / SFTP / FTP
```

原因：

- 避免大文件占用 ASP.NET Core 资源
- 避免 RelaxKonoS 成为传输瓶颈
- 不重复实现成熟协议
- 减少安全风险
- 保留 SMB/SFTP 原生性能
- 原生支持第三方客户端的 Resume / Range / Random Access 等能力

---

# 3. 第一阶段支持范围

首期实现：

```text
File Services
├── SMB
└── SFTP
```

第二阶段：

```text
File Services
├── SMB
├── SFTP
└── FTPS
```

FTP 明文模式可以支持，但必须标记为：

```text
⚠ Unencrypted
```

未来 Provider：

```text
WebDAV
NFS
```

当前架构必须允许未来增加协议，而不需要修改 File Services 核心业务逻辑。

---

# 4. 非目标

以下内容当前明确不实现。

## 4.1 不实现文件协议

RelaxKonoS 不自行实现：

- SMB protocol
- SSH protocol
- SFTP protocol
- FTP protocol
- FTPS protocol
- WebDAV protocol
- NFS protocol

---

## 4.2 不实现客户端

RelaxKonoS 当前不负责提供：

- SMB Client
- FTP Client
- SFTP Client

用户使用第三方客户端。

---

## 4.3 不代理文件数据

例如用户：

```text
PC
 │
 │ 500 GB
 ▼
SFTP Server
```

500 GB 数据不得经过：

```text
RelaxKonoS.Server
```

---

# 5. 模块建议

建议增加：

```text
RelaxKonoS.Server/
└── FileServices/
    ├── Abstractions/
    │   ├── IFileServiceProvider.cs
    │   ├── IFileServiceManager.cs
    │   └── IFileServiceValidator.cs
    │
    ├── Models/
    │   ├── FileServiceProtocol.cs
    │   ├── FileServiceStatus.cs
    │   ├── FileServiceConfiguration.cs
    │   ├── FileShare.cs
    │   ├── FileServiceUser.cs
    │   └── FileSharePermission.cs
    │
    ├── Services/
    │   └── FileServiceManager.cs
    │
    └── Providers/
        ├── Smb/
        │   ├── SambaFileServiceProvider.cs
        │   └── WindowsSmbFileServiceProvider.cs
        │
        ├── Sftp/
        │   └── OpenSshSftpFileServiceProvider.cs
        │
        └── Ftp/
            └── FtpFileServiceProvider.cs
```

PrivilegedHelper：

```text
RelaxKonoS.PrivilegedHelper/
└── FileServices/
    ├── Operations/
    │   ├── StartFileServiceOperation.cs
    │   ├── StopFileServiceOperation.cs
    │   ├── RestartFileServiceOperation.cs
    │   ├── ConfigureFileServiceOperation.cs
    │   ├── CreateFileShareOperation.cs
    │   ├── UpdateFileShareOperation.cs
    │   ├── DeleteFileShareOperation.cs
    │   ├── CreateFileServiceUserOperation.cs
    │   ├── DeleteFileServiceUserOperation.cs
    │   └── SetFileSharePermissionOperation.cs
    │
    └── Handlers/
```

Client：

```text
RelaxKonoS.Client/
└── Apps/
    └── FileServices/
        ├── FileServicesView.axaml
        ├── FileServicesViewModel.cs
        │
        ├── Smb/
        ├── Sftp/
        └── Ftp/
```

---

# 6. Protocol 类型

```csharp
public enum FileServiceProtocol
{
    Smb,
    Sftp,
    Ftps,
    Ftp
}
```

未来：

```csharp
WebDav,
Nfs
```

---

# 7. Provider 抽象

不同协议不得在 Manager 中通过大量：

```csharp
if (protocol == ...)
```

来实现。

必须通过 Provider。

建议：

```csharp
public interface IFileServiceProvider
{
    FileServiceProtocol Protocol { get; }

    Task<FileServiceCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    Task<FileServiceStatus> GetStatusAsync(
        CancellationToken cancellationToken);

    Task<FileServiceConfiguration> GetConfigurationAsync(
        CancellationToken cancellationToken);

    Task StartAsync(
        CancellationToken cancellationToken);

    Task StopAsync(
        CancellationToken cancellationToken);

    Task RestartAsync(
        CancellationToken cancellationToken);

    Task ApplyConfigurationAsync(
        FileServiceConfiguration configuration,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FileShare>> GetSharesAsync(
        CancellationToken cancellationToken);

    Task CreateShareAsync(
        FileShare share,
        CancellationToken cancellationToken);

    Task UpdateShareAsync(
        FileShare share,
        CancellationToken cancellationToken);

    Task DeleteShareAsync(
        string shareId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FileServiceUser>> GetUsersAsync(
        CancellationToken cancellationToken);
}
```

不是所有协议都支持 Share 概念。

因此必须同时提供：

```csharp
FileServiceCapabilities
```

例如：

```csharp
public sealed record FileServiceCapabilities(
    bool SupportsShares,
    bool SupportsUsers,
    bool SupportsGroups,
    bool SupportsReadOnly,
    bool SupportsChroot,
    bool SupportsPasswordAuthentication,
    bool SupportsPublicKeyAuthentication,
    bool SupportsEncryption,
    bool SupportsTls,
    bool SupportsGuestAccess);
```

UI 根据 capabilities 动态显示功能。

---

# 8. 服务状态

建议：

```csharp
public enum ServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}
```

状态：

```csharp
public sealed record FileServiceStatus(
    FileServiceProtocol Protocol,
    ServiceState State,
    bool Installed,
    string? Version,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    string? Error);
```

UI：

```text
SMB

● Running

Samba 4.x

Port
445

Shares
3

[Restart] [Stop]
```

---

# 9. SMB

## 9.1 Linux

Linux 使用：

```text
Samba
```

Provider：

```text
SambaFileServiceProvider
```

负责：

- 检测 Samba 是否安装
- 检测 smbd 服务
- 查看 Samba 版本
- 管理共享
- 管理 Samba 用户
- 配置 guest
- 配置 read only
- 配置 allowed users/groups
- 配置 SMB 加密策略
- 禁止 SMB1
- reload / restart Samba

---

## 9.2 SMB 默认安全策略

默认：

```text
SMB1                 Disabled
Minimum Protocol     SMB2
SMB3 Encryption      Supported
Guest                Disabled
Anonymous Write      Disabled
```

不得默认开放匿名写入。

---

## 9.3 SMB Share 模型

```csharp
public sealed record FileShare
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; }

    public bool ReadOnly { get; init; }

    public bool GuestAccess { get; init; }

    public IReadOnlyList<FileSharePermission> Permissions { get; init; }
}
```

---

# 10. SFTP

SFTP 基于：

```text
OpenSSH
```

RelaxKonoS 不安装独立 SFTP Protocol Server。

---

## 10.1 功能

支持：

```text
SFTP service
├── Enable / Disable
├── Port
├── Bind Address
├── Password Authentication
├── Public Key Authentication
├── User Access
├── Root Directory
├── SFTP-only Account
└── Chroot
```

---

# 11. SFTP 用户模式

建议支持：

```text
Normal SSH User

SFTP Only User
```

SFTP Only：

```text
SSH Shell       Disabled
SFTP            Enabled
Root Directory  /srv/sftp/user1
```

安全上应优先支持：

```text
ForceCommand internal-sftp
```

或对应 OpenSSH 隔离配置。

---

# 12. FTP / FTPS

第一阶段不要求完成。

第二阶段优先：

```text
FTPS
```

UI 应显示：

```text
FTPS
Encrypted with TLS
```

如果开启明文：

```text
FTP
⚠ Authentication and data may be transmitted without encryption.
```

不得把 FTP 放在 Recommended 分类。

---

# 13. 用户模型

必须明确：

> RelaxKonoS Account 与 File Service Account 是不同概念。

例如：

```text
RelaxKonoS User
admin@example

          ≠

SFTP User
backup

          ≠

SMB User
media
```

当前阶段禁止自动把 RelaxKonoS 用户映射为系统账号。

---

# 14. FileServiceUser

```csharp
public sealed record FileServiceUser
{
    public required string Id { get; init; }

    public required string Username { get; init; }

    public bool Enabled { get; init; }

    public string? HomeDirectory { get; init; }

    public bool AllowPasswordAuthentication { get; init; }

    public bool AllowPublicKeyAuthentication { get; init; }

    public bool SftpOnly { get; init; }
}
```

密码不得返回客户端。

API 永远不得存在：

```text
GetPassword()
```

---

# 15. 密码设计

File Service 用户密码属于 Secret。

必须：

- 输入时使用 Secure UI
- API 只允许 set/change
- 永不返回原密码
- 不写普通日志
- 不记录 Audit 请求中的 plaintext password
- 不存 RelaxKonoS 普通配置 JSON

例如：

```text
POST password

Client
 ↓
Server
 ↓
PrivilegedHelper
 ↓
system account / samba password backend
```

完成后立即丢弃。

---

# 16. 权限模型

统一 UI 可以使用：

```text
None
Read
ReadWrite
```

模型：

```csharp
public enum FileAccessLevel
{
    None,
    Read,
    ReadWrite
}
```

```csharp
public enum FilePermissionPrincipalType
{
    User,
    Group,
    Everyone
}
```

```csharp
public sealed record FileSharePermission
{
    public required FilePermissionPrincipalType PrincipalType { get; init; }

    public required string Principal { get; init; }

    public required FileAccessLevel Access { get; init; }
}
```

但是：

> RelaxKonoS 权限不能替代底层操作系统权限。

必须同时验证：

```text
Protocol Permission
        +
Filesystem Permission
        =
Effective Permission
```

例如：

```text
Samba允许 alice ReadWrite

但是：

/data/private

Unix ACL:
alice = read only

最终结果：
不能写
```

UI 应能够提示权限冲突。

---

# 17. 文件系统安全

创建共享前必须校验路径。

禁止默认共享：

```text
/
C:\
/etc
/root
C:\Windows
```

除非未来提供明确的 Advanced override。

需要校验：

- 路径存在
- 是否是目录
- Symbolic Link 风险
- 路径是否逃逸
- 用户是否有访问权限
- 是否属于系统敏感目录
- 是否已经存在冲突共享

PrivilegedHelper 不接受任意：

```text
shell command
```

例如禁止：

```csharp
Execute($"chmod {input}")
```

必须使用强类型操作。

---

# 18. PrivilegedHelper 边界

RelaxKonoS.Server 不允许直接：

```text
sudo
systemctl
net user
chmod
chown
setfacl
ufw
firewall-cmd
netsh
修改 /etc/*
修改 Windows service
```

必须：

```text
Server
 ↓
Strongly Typed Request
 ↓
PrivilegedHelper
```

例如：

```csharp
public sealed record RestartFileServiceOperation(
    FileServiceProtocol Protocol);
```

而不是：

```csharp
ExecuteCommandOperation(
    "systemctl restart smbd");
```

禁止创建通用 root shell 执行 API。

---

# 19. 配置写入策略

禁止直接盲目覆盖整个系统配置文件。

例如：

```text
/etc/samba/smb.conf
/etc/ssh/sshd_config
```

推荐优先：

```text
include
drop-in
managed configuration
```

例如：

```text
/etc/samba/relaxkonos.d/
```

或者：

```text
RelaxKonoS managed section
```

必须明确区分：

```text
User Managed
RelaxKonoS Managed
```

RelaxKonoS 不应删除管理员自己写的配置。

---

# 20. 配置修改流程

任何配置修改都采用：

```text
Generate
   ↓
Validate
   ↓
Backup
   ↓
Apply
   ↓
Reload / Restart
   ↓
Health Check
   ↓
Success
```

失败：

```text
Apply
 ↓
Service failed
 ↓
Rollback
 ↓
Restart previous configuration
```

不得：

```text
直接写配置
↓
restart
↓
失败后留着坏配置
```

---

# 21. 配置验证

Samba：

```text
testparm
```

OpenSSH：

使用 OpenSSH 自身配置检查机制。

FTP：

调用对应 Server 的 validation。

必须先验证再 reload。

---

# 22. 防火墙

文件服务页面可以提供：

```text
Firewall

✓ Allow required port
```

但：

> Firewall 是独立 subsystem。

File Services 不直接实现防火墙。

调用：

```text
FirewallManager
```

而不是：

```text
SambaProvider → ufw
```

这样避免 Provider 和 Firewall 强耦合。

---

# 23. 默认端口

UI 默认：

```text
SMB
TCP 445

SFTP
TCP 22

FTP
TCP 21

FTPS
根据服务器模式配置
```

用户修改端口时必须：

```text
Validate
Check Conflict
Update Service
Update Firewall
Health Check
```

---

# 24. UI 总览

主页面：

```text
File Services

Share and access files using standard network protocols.

──────────────────────────────────────

SFTP                              ● Running
Secure file transfer over SSH

Port                              22
Users                             3

[Manage]

──────────────────────────────────────

SMB                               ● Running
Network file sharing

Port                              445
Shares                            4

[Manage]

──────────────────────────────────────

FTPS                              ○ Disabled
FTP over TLS

[Configure]
```

---

# 25. SMB UI

```text
SMB

Status
● Running

Version
Samba x.x

Port
445

Security
Minimum protocol     SMB2
Guest Access         Disabled

────────────────────────

Shares

Name        Path                 Access

Public      /srv/public          Everyone Read
Backup      /srv/backup          backup Read/Write
Media       /srv/media           media Read

[Add Share]
```

---

# 26. 创建共享

```text
Add SMB Share

Name
[ Backup              ]

Path
[ /srv/backup         ] [Browse]

Description
[                     ]

Guest Access
[ OFF ]

Permissions

backup
Read / ReadWrite

media
Read / ReadWrite

[Cancel] [Create]
```

---

# 27. SFTP UI

```text
SFTP

Status
● Running

OpenSSH
9.x

Port
22

Authentication

Password       ON
Public Key     ON

────────────────────────

Users

backup
SFTP Only
/srv/sftp/backup

developer
SSH + SFTP
/home/developer
```

---

# 28. 创建 SFTP 用户

```text
Create SFTP User

Username
[ backup ]

Mode

○ SSH + SFTP
● SFTP only

Root directory
[ /srv/sftp/backup ]

Authentication

✓ Password
✓ SSH Public Key

[Create]
```

---

# 29. Connection Information

每个服务提供：

```text
Connection

Protocol
SFTP

Host
192.168.1.10

Port
22

Example

sftp backup@192.168.1.10
```

SMB：

```text
Windows

\\192.168.1.10\backup

macOS

smb://192.168.1.10/backup
```

只展示连接方式。

RelaxKonoS 不负责启动第三方客户端。

---

# 30. 断点续传

RelaxKonoS 不自行实现 Resume。

由：

```text
SMB Server
OpenSSH SFTP
FTP Server
Third-party Client
```

共同提供。

因此 RelaxKonoS 不设计：

```text
UploadSession
TransferProgress
ChunkUpload
ResumeToken
```

这些属于文件客户端/传输代理职责。

---

# 31. Audit Log

所有管理操作必须记录。

例如：

```text
FileService.Smb.Start
FileService.Smb.Stop

FileService.Smb.Share.Created
FileService.Smb.Share.Modified
FileService.Smb.Share.Deleted

FileService.Sftp.User.Created
FileService.Sftp.User.Disabled

FileService.Sftp.Configuration.Modified
```

记录：

```text
Actor
Timestamp
Protocol
Resource
Operation
Result
```

不得记录：

```text
Password
Private Key
Sensitive Configuration
```

---

# 32. Server API / Protocol

如果 RelaxKonoS.Protocol 是唯一通信入口，则 File Services 所有 DTO 必须定义于 Protocol。

禁止 Client：

```text
直接调用 HTTP endpoint
直接调用 system service
直接 SSH 到 server
```

推荐 DTO：

```text
GetFileServicesRequest
GetFileServiceStatusRequest

StartFileServiceRequest
StopFileServiceRequest
RestartFileServiceRequest

GetFileSharesRequest
CreateFileShareRequest
UpdateFileShareRequest
DeleteFileShareRequest

GetFileServiceUsersRequest
CreateFileServiceUserRequest
UpdateFileServiceUserRequest
DeleteFileServiceUserRequest

ChangeFileServiceUserPasswordRequest
```

---

# 33. 跨平台 Provider

架构必须允许：

```text
SMB
├── Linux
│    └── Samba
│
└── Windows
     └── Windows SMB
```

SFTP：

```text
SFTP
├── Linux
│    └── OpenSSH
│
└── Windows
     └── OpenSSH Server
```

Manager 不关心当前实现。

例如：

```csharp
IFileServiceProvider provider =
    providerResolver.Resolve(FileServiceProtocol.Smb);
```

---

# 34. Provider Resolver

建议：

```csharp
public interface IFileServiceProviderResolver
{
    IFileServiceProvider Resolve(
        FileServiceProtocol protocol);
}
```

内部根据：

```text
Protocol
OperatingSystem
Installed Backend
```

进行选择。

---

# 35. 安装策略

File Services 必须区分：

```text
Installed
Not Installed
Unsupported
```

例如：

```text
SMB

Samba is not installed.

[Install Samba]
```

安装属于 privileged operation。

但实现安装功能时不得写死：

```text
apt install
```

需要系统 Package Manager abstraction。

未来：

```text
apt
dnf
yum
pacman
zypper
Windows Features
```

---

# 36. 第一阶段可以限制平台

如果跨 Linux/Windows 同时实现工作量过大，则 MVP 优先：

```text
Linux
├── Samba
└── OpenSSH
```

但是 interface 和 domain model 必须保持跨平台。

不得把 Samba 类型暴露到：

```text
FileServiceManager
Client
Protocol DTO
```

例如禁止：

```csharp
SambaShareDto
```

应该：

```csharp
FileShareDto
```

只有 Provider 内部出现 Samba。

---

# 37. Security

必须遵守 Principle of Least Privilege。

Server：

```text
普通服务账户
```

PrivilegedHelper：

```text
root / LocalSystem
```

Helper 只接受：

```text
Allowlisted
Strongly Typed
Validated
Operations
```

禁止：

```text
ExecuteShell(string command)
RunAsRoot(string executable, string args)
```

作为公共能力。

---

# 38. 输入验证

所有：

```text
Username
Share Name
Path
Port
Group
Hostname
```

均必须验证。

特别防止：

```text
Shell injection
Path traversal
Config injection
Newline injection
Option injection
```

例如 Share Name：

```text
foo
```

可以。

类似：

```text
foo
[global]
admin users = root
```

必须拒绝。

---

# 39. Secret Handling

以下内容属于敏感数据：

```text
Passwords
SSH private keys
TLS private keys
```

公钥：

```text
authorized_keys
```

不是 Secret，但仍属于安全配置。

PrivilegedHelper 日志不得输出 Secret。

---

# 40. 错误模型

不要直接把：

```text
Process exited with code 1
```

展示给普通用户。

建议统一：

```csharp
FileServiceProblem
{
    Code,
    Message,
    TechnicalDetail,
    Recoverable
}
```

例如：

```text
PortAlreadyInUse
ConfigurationInvalid
ServiceNotInstalled
PermissionDenied
DirectoryNotFound
UserAlreadyExists
ServiceStartFailed
UnsupportedPlatform
```

UI 给友好说明。

Debug/Audit 保存技术信息。

---

# 41. 服务健康检查

Start / Restart 后：

```text
Service process running
        ↓
Port listening
        ↓
Configuration valid
        ↓
Healthy
```

不要仅仅依赖：

```text
systemctl returned 0
```

就认为服务完全正常。

---

# 42. 并发

FileServiceManager 对同一 Protocol 的修改必须串行化。

例如禁止：

```text
Request A
Restart SMB

Request B
Update SMB share

同时执行
```

建议：

```text
per-protocol operation lock
```

例如：

```text
SMB lock
SFTP lock
FTP lock
```

---

# 43. 配置 Drift

用户可能通过 SSH 手动修改：

```text
smb.conf
sshd_config
```

因此 RelaxKonoS 不应该假设自己的数据库永远是事实来源。

优先：

```text
Actual system configuration
```

作为 Source of Truth。

或者明确区分：

```text
Desired State
Actual State
```

未来可以显示：

```text
Configuration modified outside RelaxKonoS
```

---

# 44. 推荐产品结构

最终 RelaxKonoS 可以表现为：

```text
RelaxKonoS

System
Users
Files
Terminal

Services
├── Docker
├── Proxy
├── File Services
│   ├── SFTP
│   ├── SMB
│   ├── FTPS
│   ├── WebDAV      Future
│   └── NFS         Future
│
└── Process Supervisor

Network
├── Firewall
└── Port Forwarding
```

---

# 45. 推荐级别

UI 可以将协议分类：

```text
Recommended

SFTP
SMB

Additional

WebDAV
NFS

Compatibility

FTPS
FTP
```

其中：

```text
FTP
⚠ Unencrypted
```

---

# 46. 实施顺序

Codex 不应一次同时实现所有协议。

按照以下阶段实现。

## Phase 1

实现 Domain 与 Abstraction：

```text
FileServiceProtocol
FileServiceStatus
FileServiceCapabilities
FileServiceConfiguration
FileShare
FileServiceUser
FileSharePermission

IFileServiceProvider
IFileServiceProviderResolver
FileServiceManager
```

---

## Phase 2

实现 Linux SFTP Provider：

```text
OpenSSH detection
Status
Start
Stop
Restart
Port
Authentication configuration
SFTP-only users
```

---

## Phase 3

实现 Linux SMB Provider：

```text
Samba detection
Status
Start
Stop
Restart
Shares
Users
Permissions
Configuration validation
```

---

## Phase 4

实现 UI：

```text
File Services overview
SMB management
SFTP management
Share editor
User editor
Connection information
```

---

## Phase 5

实现：

```text
Firewall integration
Audit log
Rollback
Health checks
```

---

## Phase 6

实现 Windows Providers。

---

## Phase 7

增加：

```text
FTPS
```

未来：

```text
WebDAV
NFS
```

---

# 47. MVP 验收标准

第一阶段完成后应至少满足以下条件。

## SFTP

用户可以在 RelaxKonoS：

1. 查看 OpenSSH 是否安装
2. 查看 SFTP/SSH 服务状态
3. 启动服务
4. 停止服务
5. 重启服务
6. 修改监听端口
7. 创建一个 SFTP 用户
8. 设置 SFTP-only
9. 设置用户根目录
10. 开启密码或 SSH Key 认证

然后使用 WinSCP：

```text
sftp://server
```

成功连接。

文件数据不经过 RelaxKonoS.Server。

---

## SMB

用户可以：

1. 查看 Samba 是否安装
2. 查看 Samba 状态
3. 启动
4. 停止
5. 重启
6. 创建 SMB Share
7. 删除 SMB Share
8. 配置 Read
9. 配置 ReadWrite
10. 配置用户访问权限

Windows 可以：

```text
\\server\share
```

正常访问。

文件数据不经过 RelaxKonoS.Server。

---

# 48. 自动化测试

至少增加：

```text
FileServiceManagerTests
ProviderResolverTests
FileShareValidatorTests
FileServiceUserValidatorTests
```

Provider 的系统操作必须尽可能抽象，使测试不真正修改：

```text
/etc
systemd
Windows Services
```

例如：

```text
IServiceController
IFileSystem
IProcessRunner
IPackageManager
```

可以 Mock。

---

# 49. 集成测试

Linux CI / VM 中可以：

```text
Install Samba
Install OpenSSH

Apply config

Validate

Start

Check listening port
```

但 CI 不要求执行真正的大文件传输。

协议通信测试可以后续加入。

---

# 50. Codex 实现要求

Codex 在实现时必须遵守以下要求：

1. 先阅读现有 RelaxKonoS architecture 与 PrivilegedHelper 设计。
2. 不破坏现有分层。
3. RelaxKonoS.Protocol 仍然是 Client 与 Server 的唯一通信契约。
4. 不允许 Client 直接调用 system API。
5. 不允许 Server 直接执行 sudo/root 操作。
6. 所有高权限操作必须通过 PrivilegedHelper。
7. 禁止增加通用 privileged shell execution API。
8. FileServiceManager 不应依赖 Samba/OpenSSH 具体类型。
9. 所有协议必须通过 Provider abstraction。
10. Protocol DTO 中不得出现 Samba-specific 类型。
11. 密码不得保存在普通配置中。
12. 密码不得记录日志。
13. RelaxKonoS.Server 不代理文件数据。
14. 不自行实现 SMB/SFTP/FTP protocol。
15. 不自行实现文件客户端。
16. 配置必须先 validate 后 apply。
17. Apply 失败必须尽可能 rollback。
18. 不覆盖用户未由 RelaxKonoS 管理的系统配置。
19. 所有输入必须进行安全验证。
20. 优先完成 Linux SFTP + SMB MVP，不要一次扩展全部协议。

---

# 51. Codex 开始工作前

在修改代码前：

1. 分析当前 Solution 结构。
2. 找到现有：
   - Protocol
   - Server
   - PrivilegedHelper
   - Client
   - Firewall
   - Audit
   - Service management
3. 优先复用现有 abstraction。
4. 不重复创建已经存在的系统管理接口。
5. 给出拟新增/修改文件列表。
6. 给出 dependency changes。
7. 再开始实现。

如果现有架构与本文档存在冲突：

> 优先保持现有 RelaxKonoS 的架构原则，并说明冲突，不要为了照搬本文档而破坏已有模块边界。

---

# 52. 最终目标

RelaxKonoS File Services 应成为：

> Standard file service control plane.

而不是：

> File transfer implementation.

最终模型：

```text
                         ┌───────────────┐
                         │ RelaxKonoS UI │
                         └───────┬───────┘
                                 │
                                 ▼
                        FileServiceManager
                                 │
                ┌────────────────┼────────────────┐
                ▼                ▼                ▼
          SMB Provider      SFTP Provider      FTP Provider
                │                │                │
                ▼                ▼                ▼
             Samba            OpenSSH         FTP Server
                ▲                ▲                ▲
                │                │                │
       ─────────┴────────────────┴────────────────┴─────────
                      Actual file traffic
                              ▲
                              │
                       Third-party clients
```

RelaxKonoS 负责：

```text
Install
Configure
Start / Stop
Users
Permissions
Shares
Security
Firewall
Status
Audit
```

成熟文件服务负责：

```text
Protocol
Authentication handshake
Encryption
File transfer
Resume
Random access
Locking
Large file handling
Network performance
```

这应作为 File Services 模块长期保持的架构边界。