# RelaxKonOS 文件服务：SMB Goal 执行版

> 状态：代码与自动化验收完成；Goal 7 受控集成验证待隔离 VM 执行<br>
> 建立日期：2026-09-10<br>
> 首轮适用范围：`.NET 10` Server、Avalonia Client、**Linux（Debian/Ubuntu 系 + Samba 4）与 Windows Server 2019+（Windows SMB Server）**<br>
> 架构依据：[File Services 设计与实现规格](./RelaxKonoS%20File%20Services%20设计与实现规格.md)、[特权操作与 Helper](./platform/RelaxKonOS.PrivilegedOperations.Goal.md)、[安全模型](./platform/RelaxKonOS.Security.md)

本文是 File Services 的首个 `/goal` 执行基线。首轮交付 Linux Samba 与 Windows SMB Server 的 SMB 控制面；SFTP、FTP/FTPS、WebDAV、NFS 不是本轮功能，不能以空 Provider、隐藏开关、预留 API 或半成品 UI 的形式进入代码。

原始 File Services 规格仍是长期架构和产品原则的权威来源。本文把其范围收敛为可构建、验证、回滚的 SMB 闭环；两者冲突时，以本文的首轮范围和既有特权操作安全契约为准。实现开始前必须重新核对当前 Solution 与下列依赖的实际状态。

## 1. 交付结论与不可变边界

首轮交付一个宿主机全局的 `relaxkonos.file-services` 内置管理应用。它发现、配置和管理**由 Samba 或 Windows SMB Server 实际提供**的 SMB 服务，并呈现真实状态、共享、账户能力和连接信息。

数据面必须始终保持在 RelaxKonOS 之外：

```text
Windows Explorer / macOS Finder / smbclient / NAS
                    │ SMB
          ┌─────────┴─────────┐
          ▼                   ▼
    Samba (smbd)    Windows SMB Server (LanmanServer)
          │                   │
          └─────────┬─────────┘
                    ▼
              Host file system

RelaxKonOS Client → Server → PrivilegedHelper  # 仅管理和状态控制
```

因此 V1 必须具备：

- Samba 或 Windows SMB Server 的可用性、版本、服务状态、管理状态和健康状态的真实检测。
- 受支持 Linux 发行版上的受控 Samba 安装；安装源、包名和包管理器在 Server 平台适配层固定，Client 不传递包名或命令。Windows 使用随系统提供的 SMB Server / `LanmanServer`，首轮只检测、启用和管理它，不安装 Windows Server role 或 Feature。
- 服务启动、停止、重启；Linux 额外支持安全 reload。所有写操作使用已存在的受限 Helper transport。
- 只管理 RelaxKonOS 拥有的 Samba 配置片段和共享；读到的系统实际配置是状态真源。
- 创建、修改、禁用和删除 RelaxKonOS 托管 SMB 共享；支持只读、禁用、描述、guest 开关以及用户/组的 Read 或 ReadWrite 访问规则。
- Linux：列出、启用/禁用已存在的宿主本地账户的 Samba 凭据，并只允许设置或更改 Samba 密码，绝不读取或回显密码。Windows：使用现有 Windows local/domain account 或 group 的 SID 作为 share ACL principal，不管理 Windows 帐户或密码。
- Linux 使用 `testparm` 验证、原子配置应用、配置备份、reload 后端口/服务健康检查，以及失败回滚；Windows 使用受限系统 API 的 preflight、受管资源快照、apply、服务/端口健康检查和失败回滚。
- 明确显示 SMB 连接示例（如 `\\host\share` 与 `smb://host/share`），但不启动客户端、不代理或上传文件。
- 管理审计、稳定 problem code、每协议串行化和测试替身；所有管理操作经 Server 授权与短期主机管理员能力授权。

V1 不包括：

- SMB、NetBIOS、NTLM、Kerberos 或任何文件传输协议/客户端实现；Server 不承接字节流、断点续传、锁或传输进度。
- SFTP、FTP、FTPS、WebDAV、NFS，或任何这些协议的 DTO、Provider、Endpoint、App 页面和 AppPermission。
- 任意宿主账户的创建、删除、组成员关系、Unix ACL/chown/chmod 管理。SMB 账户只关联**已经存在的本地宿主账户**；未来若要管理宿主账户，必须另行审查身份、密码和权限模型。
- 自动添加、防火墙规则修改或端口转发。File Services 只能诊断 TCP 445 和给出防火墙应用的受控入口；Firewall 仍是独立子系统。
- 管理管理员手写的 Samba share 或任意 `smb.conf` 字段；不接管现有未标记配置，也不覆盖完整 `smb.conf`。
- 让 RelaxKonOS 帐户自动成为 Samba/Unix/Windows 帐户，保存普通密码配置，或通过 Helper 运行 shell、`systemctl`、`smbpasswd`、`testparm`、PowerShell 或 Windows command 的任意文本命令。

## 2. 首轮冻结的产品与安全决定

| 决定 | V1 行为 |
| --- | --- |
| 平台 | Linux：Debian/Ubuntu 系 + systemd + Samba 4。Windows：Windows Server 2019+、`LanmanServer` 与 Windows `SmbShare` 管理能力。其他平台/发行版返回明确“不受支持”，不猜测其 service/package 命令。 |
| 服务后端 | Linux 为 `smbd`；Windows 为 `LanmanServer`。`nmbd`/WS-Discovery、域控制器/AD 管理、打印服务和 cluster/Scale-Out File Server 不在范围。 |
| 安装 | Linux 仅由平台适配器选择固定受信任包与固定包管理器，Helper 接受 `SmbPackageInstall`，不接受包名、版本、仓库、参数或 shell 文本。Windows 不安装 role/feature，只验证和管理已有 SMB Server 能力。 |
| 管理模式 | 仅 `RelaxKonOS managed`。Linux 不编辑非托管 share，不能安全建立托管 include 时返回 `smb.configuration_unmanaged`。Windows 通过 HostGlobal ownership ledger 识别由 RelaxKonOS 创建的 share；实际 API 状态仍是真源，账本不重建、接管或覆盖外部 share。 |
| 配置真源 | Linux 为已解析、已验证的 Samba 实际配置；Windows 为 SMB 系统 API 返回的实际 service、share、share ACL 与 server configuration。数据库只保存 ownership、审计和操作记录，不能成为 shares 的第二个 Desired State。 |
| 身份 | Linux 的 Samba user 关联现有本地系统账户；Windows share ACL 使用现有 local/domain account 或 group SID。它们都不是 RelaxKonOS 登录账户；本轮不管理任何宿主账户或密码，Linux 的 Samba password backend 设置是唯一例外。 |
| 默认安全策略 | SMB1 禁用，最低协议 SMB2，guest 默认关闭，匿名写入禁止；SMB3 encryption 只在后端与 Windows Server 版本明确支持时声明/配置，不能伪造“已加密”。 |
| 防火墙 | 只读诊断或跳转到现有 Firewall 应用；不由 Samba Provider 写 UFW/nftables/iptables。 |
| 特权模型 | Server 保持最小权限。Linux 的写配置、安装、服务控制和 Samba password backend 经 root-owned Helper；Windows 的 share/ACL/service/security 策略经现有 LocalSystem named-pipe Helper。HTTP Endpoint、ViewModel 和 Manager 均不得启动特权进程或 PowerShell。 |

所有新线协议与本地 Helper contract 均直接采用本 Goal 定义的当前接口。项目尚未正式发布，不保留旧路由、旧 DTO 字段、兼容别名、双格式解析或 legacy Provider adapter；同一提交必须更新全部仓内调用方、测试、示例和文档。

Windows 适配器的能力依据 Windows Server 的 SMB Share / SMB Server 管理面：系统能够读取/创建 share、授予 share access，并配置 SMB server 安全策略；实现必须通过受限的编译绑定 API，而非把这些管理命令当作可拼接的 PowerShell 文本。[Microsoft 的 SMB Share 文档](https://learn.microsoft.com/en-us/powershell/module/smbshare/new-smbshare?view=windowsserver2025-ps)与 [SMB server security 文档](https://learn.microsoft.com/en-us/windows-server/storage/file-server/smb-security)仅用作能力与行为参考，不构成对任意 Windows 命令执行的授权。

## 3. 架构与职责

### 3.1 分层

```text
Avalonia File Services App
  → typed IFileServicesClient (Protocol DTO only)
  → /api/v1.0/file-services/smb endpoints + Server policy
  → IFileServiceManager (protocol-neutral orchestration and per-protocol lock)
  → IFileServiceProviderResolver → IFileServiceProvider
      ├─ LinuxSambaFileServiceProvider → ISambaPlatformAdapter
      └─ WindowsSmbFileServiceProvider → IWindowsSmbPlatformAdapter
  → IPrivilegedOperationTransport
  → Linux: root-owned one-shot Helper → Samba/systemd/package manager
  → Windows: LocalSystem named-pipe Helper → SMB API/LanmanServer
```

`IFileServiceManager` 负责选择 Provider、授权后的工作流顺序、同协议互斥、操作结果和审计协调；它不得引用 Samba 可执行文件、Samba 配置段名、Windows registry/CIM 类型或平台命令。两个 Provider 都将中性的 SMB 模型映射为各自后端语义，但不得把 `ProcessStartInfo`、shell、PowerShell 或特权文件 I/O 直接散入 Provider。只有平台适配器和 Helper 可以了解固定的 Samba/systemd/package-manager 或 Windows SMB/service/API 细节。

首轮仍建立窄的 Provider 边界，避免未来协议进入 Manager 的 `if (protocol == ...)` 分支；仅注册 `Smb` 协议，Resolver 在支持的平台选择 Linux Samba 或 Windows SMB Provider。SFTP/FTP 类型在其独立 Goal 批准前不创建。

建议落点：

```text
Shared/RelaxKonOS.Protocol/FileServices/
  FileServiceProtocol.cs                  # V1 仅 Smb
  SmbContracts.cs                         # 无 Samba-specific DTO 名称
  FileServiceApiRoutes.cs
  FileServiceProblemCodes.cs

RelaxKonOS.Server/FileServices/
  Abstractions/IFileServiceProvider.cs
  Abstractions/IFileServiceProviderResolver.cs
  Abstractions/IFileServiceManager.cs
  FileServiceManager.cs
  Smb/LinuxSambaFileServiceProvider.cs
  Smb/WindowsSmbFileServiceProvider.cs
  Smb/ISambaPlatformAdapter.cs
  Smb/LinuxSambaPlatformAdapter.cs
  Smb/IWindowsSmbPlatformAdapter.cs
  Smb/WindowsSmbPlatformAdapter.cs
  Smb/IPrivilegedSmbOperations.cs
  Smb/PrivilegedSmbOperations.cs
  Smb/SmbValidators.cs
  Smb/SmbAudit.cs
  FileServiceProviderResolver.cs

RelaxKonOS.Server/Endpoints/FileServiceEndpoints.cs
RelaxKonOS.PrivilegedHelper/Smb/             # parser, transaction and typed operation handlers
Client/RelaxKonOS.Client/Apps/FileServices/  # UI, typed client proxy and localization
```

实际目录可遵循仓库当前命名而微调，但不能把业务放进 `Program.cs`、Endpoint、ViewModel 或现有 Explorer 的 `IFileService`。Explorer 的本地文件浏览 API 与本模块的宿主 SMB 服务控制是不同职责；二者不得互相扩展为隐式依赖。

### 3.2 对外协议与中性模型

所有 Client↔Server 数据类型、路由常量和 JSON 名称放在 `Shared/RelaxKonOS.Protocol/FileServices`，Protocol 继续保持零 `PackageReference`。首轮公开模型使用 `FileServiceProtocol.Smb`、`FileServiceStatusDto`、`FileServiceCapabilitiesDto`、`FileShareDto`、`FileSharePermissionDto`、`FileServiceUserDto`、`FileServiceOperationResultDto` 和连接信息 DTO；禁止 `SambaShareDto`、`SmbConfDto`、Samba 命令输出或 Linux process error 出现在 HTTP 响应。

最小路由族固定为（`install` 和 `users/*` 由 capabilities 标记为仅 Linux Samba 可用；Windows 不显示或调用它们）：

```text
GET    /api/v1.0/file-services/smb/status
POST   /api/v1.0/file-services/smb/install
POST   /api/v1.0/file-services/smb/start
POST   /api/v1.0/file-services/smb/stop
POST   /api/v1.0/file-services/smb/restart
GET    /api/v1.0/file-services/smb/shares
POST   /api/v1.0/file-services/smb/shares
PUT    /api/v1.0/file-services/smb/shares/{shareId}
DELETE /api/v1.0/file-services/smb/shares/{shareId}
GET    /api/v1.0/file-services/smb/users
POST   /api/v1.0/file-services/smb/users/{username}/enable
POST   /api/v1.0/file-services/smb/users/{username}/disable
PUT    /api/v1.0/file-services/smb/users/{username}/password
GET    /api/v1.0/file-services/smb/connection
```

读操作要求 `FileServicesRead`，变更要求 `FileServicesManage`；安装、服务生命周期、配置/共享和凭据变更还必须验证当前 JWT `jti` 的精确 `HostElevationCapability.SmbManage`、目标 `smb:managed` 的五分钟授权。扩展现有 elevation enum、Endpoint 和 session-store 测试时，应直接迁移当前契约，不增加独立密码确认或宽泛“file services admin”票据。App permission 仅控制内置 App 的客户端入口，不能成为 HTTP 授权依据。

密码更新请求只在 Linux Samba capability 启用时存在；它在 TLS 请求体中短暂存在，映射为 Helper 的一次性强类型请求后立即丢弃。Windows 不提供密码相关 Endpoint 或 UI。密码不得进入日志、审计、异常 detail、DTO、持久化实体、operation replay payload 或 UI state。所有 API 返回小写点分的稳定 problem code，例如 `file-services.smb.not_installed`、`file-services.smb.port_in_use`、`file-services.smb.configuration_invalid`、`file-services.smb.configuration_unmanaged`、`file-services.smb.share_conflict`、`file-services.smb.system_account_not_found`、`file-services.smb.windows_api_unavailable` 和 `file-services.platform_unsupported`。

### 3.3 Helper contract 与最小权限

在既有 `PrivilegedOperationKind` / `PrivilegedOperationRequest` 中增加封闭的 SMB 操作及结构化字段，而非复用通用 `FileWrite` 或 `NativeServiceAction`：

```text
SmbDetect
SmbPackageInstall                           # Linux only
SmbServiceAction(action = Start | Stop | Restart | Reload)
SmbReadManagedConfiguration                 # Linux only
SmbApplyManagedConfiguration(managedShares, globalSecurityPolicy, operationId) # Linux only
SmbApplyWindowsShare(managedShare, expectedSnapshot, operationId)              # Windows only
SmbRemoveWindowsShare(shareId, expectedSnapshot, operationId)                   # Windows only
SmbSetWindowsServerSecurity(policy, expectedSnapshot, operationId)              # Windows only
SmbSetUserEnabled(username, enabled)        # Linux Samba only
SmbSetUserPassword(username, password)      # Linux Samba only; input never enters results/audit
```

该列表表达形状，不要求按名字保留现有 flat request；实现应把 SMB 请求建模为明确的 typed payload，Helper 再次校验协议版本、operation ID、用户名/SID、share 名、路径、principal、访问级别、允许的固定系统资源和操作前置条件。Windows Helper 只能调用经编译绑定的 Windows SMB/Service/ACL API（或等价的受限系统 API），不能接受或拼接 PowerShell 文本。两平台都不得接受 `executable`、arguments、command、shell、working directory、environment、任意 package/service id、任意 config path 或未受限的 base64 配置文本。

Linux Helper 固定拥有的资源仅包括受支持发行版中的 Samba 包、`smbd` 单元、主配置文件的唯一 RelaxKonOS include marker、`/etc/samba/relaxkonos.conf`（或最终冻结的等价 root-owned 路径）、该文件的备份/临时文件和 Samba password backend。Windows LocalSystem Helper 固定拥有的资源仅包括 `LanmanServer`、Windows SMB Server configuration、由 ownership ledger 标识的 Windows shares 及其 share ACL；不得修改 NTFS ACL、Windows 帐户、组、任意注册表项或非托管 share。Server 服务账户、Client、普通文件 Explorer 与任何用户输入都不能扩大这些资源范围。

### 3.4 配置所有权、验证与恢复

Linux 只管理一个 root-owned include 文件和其中带稳定 ID 的 share section。首轮允许在主 `smb.conf` 的 `[global]` 中维护一个唯一、精确、可识别的 RelaxKonOS include marker；首次接管前必须解析并确认该 marker 可安全插入。若主配置存在冲突、重复 marker、无法确定 `[global]`、include 被管理员改成其他目标或 `testparm` 不能验证，则停止并返回 `file-services.smb.configuration_unmanaged`，绝不猜测或覆盖管理员配置。

Windows 不写 `smb.conf` 或 PowerShell 脚本。它通过系统 SMB API 创建/更新 share 和 share ACL，并把由 RelaxKonOS 创建的 share ID、名称、路径哈希、首次观察到的 security descriptor/hash 与 revision 写入 HostGlobal ownership ledger。账本只定义“是否允许本模块修改/删除此资源”，不是 share Desired State：每次变更均先从 Windows API 读取实际对象，发现 share 被删除、改名、替换、路径或 ACL 在外部变更时，显示 drift 并要求用户重新确认或解除管理，绝不静默覆盖。系统默认管理共享（如 `ADMIN$`、`C$`、`IPC$`）和 cluster share 永远不可管理。

Linux 配置事务在持有 SMB protocol lock 时严格执行：

```text
读取并验证当前主配置/托管 include
→ 生成候选托管 include（固定序列化，不接受原始 Samba 指令）
→ 写私有临时文件（root-only）
→ testparm 验证完整候选配置
→ 保存受限备份并原子替换
→ smbd reload；必要时受控 restart
→ 检查 service active、TCP 445 listening、testparm
→ 成功并审计
```

替换、reload、restart 或健康检查失败时，Linux Helper 必须原子还原旧 include/marker，并重新验证、重载旧配置；若旧配置也无法恢复，返回不可恢复的稳定 problem code 并留下不含秘密的管理员诊断。不能把 `systemctl` 返回 0 当作健康，也不能在坏配置上继续显示运行中。

Windows 事务在同一 SMB lock 内执行：读取并验证实际 share/server security snapshot → 验证目标目录、SID 和 share ACL → 通过固定 Windows API apply → 重新读取实际对象/ACL、检查 `LanmanServer` 和 TCP 445 → 写入 ownership ledger 与审计。任何 apply 或 health failure 都要使用 snapshot 回滚已修改的 share/ACL/server security setting；无法安全回滚时，将资源标记为 `reconciliation-required`，返回稳定 problem code，绝不写入“成功”。Windows API 的共享权限与 NTFS ACL 分别检查、分别显示，share ACL 允许访问不意味着文件系统访问一定成功。

共享路径必须为存在的绝对目录，位于受支持宿主的允许共享根目录内，且不得为 Linux 的 `/`、`/etc`、`/root`、`/proc`、`/sys`、`/dev` 或 Windows 的系统盘根、Windows 目录、Program Files、ProgramData、RelaxKonOS 私有配置目录及其他冻结的敏感目录。对路径、现有父级和目标均做规范化、符号链接/reparse-point 检测及边界验证；share 名、说明、用户名和 principal 拒绝控制字符、换行、Samba section/option 注入、路径分隔符和 option 前缀。协议权限与实际文件系统权限分别检测、分别显示：Samba rule 或 Windows share ACL 允许写入不意味着 Unix ACL 或 NTFS ACL 一定允许写入。

## 4. Goal 执行计划

每个 Goal 结束时都必须保持 `dotnet build RelaxKonOS.sln -c Debug` 可通过，运行受影响的测试，并在本 Goal 的验收达成前不推进下一个 Goal。每个 PR/提交更新所触及的 Caller、测试、文档和本地化；不得为未实现的下一协议引入兼容层或占位实现。

### Goal 0：基线审计、发布矩阵与威胁模型

**工作**：核验当前 Protocol、`IPrivilegedOperationTransport`、Host elevation、Linux 与 Windows Helper、Server policy、Firewall、审计/HostGlobal 持久化、服务控制、Client app 注册与本地化模式。冻结支持的 Debian/Ubuntu 版本、Samba 最低版本、systemd unit 名、固定包源策略、Samba 配置/include 位置、Windows Server 最低版本、`LanmanServer`/SMB API 可用性、Windows ownership ledger schema、两平台允许共享根、安装与回滚策略、健康超时、操作/审计保留和 stable problem-code 表。确认现有 Helper request 的演进方案，并清点所有 `ProcessStartInfo`、`systemctl`、`smbpasswd`、PowerShell、Windows SMB API、配置/registry 写入可能性。

**验收**：不存在“假定已有”的 Samba、Windows SMB API、包管理或权限能力；威胁模型覆盖配置/section 注入、路径穿越、symbolic link/reparse point、恶意 share、密码/SID 泄露、服务/包名/API 参数注入、端口冲突、TOCTOU、Linux 配置与 Windows share/ACL drift、失败回滚、并发请求、JWT refresh/expiry、Helper/pipe 篡改和日志泄露；每一项未支持平台/发行版均有明确返回行为。

### Goal 1：Protocol、授权和纯领域骨架

**工作**：建立 `Protocol/FileServices` 的 SMB-only DTO、路由、problem code 与序列化测试；实现中性 Provider/Resolver/Manager 接口、SMB operation/result、状态和 capability 模型。Capabilities 必须显式表达 Linux-only install/Samba credentials 与 Windows 的 managed share/security 支持。新增 `FileServicesRead` / `FileServicesManage` Server policies、内置 App permissions、`HostElevationCapability.SmbManage` 与精确 `smb:managed` scope。添加空的 Linux 与 Windows platform adapter 和 fake Provider，仅返回真实的“不支持/未安装/能力不可用”状态，尚不写宿主配置或运行服务。

**验收**：Protocol 零 PackageReference；Client/Server 不硬编码重复路由；无 Samba-specific HTTP DTO；没有密码输出字段；同 JWT+scope 的授予可用五分钟，而不同 jti、角色、capability、target 或过期授权均被拒绝；Manager 只依赖 Provider abstraction，未注册 Provider 时安全失败。

### Goal 2：受限 SMB Helper 与双平台检测

**工作**：在当前 local Helper contract 中直接加入封闭 SMB operation/payload，完成 Linux 与 Windows Helper 的二次验证以及各自 platform adapter / `IPrivilegedSmbOperations` 映射。Linux 实现 Samba 安装检测、版本读取、systemd 状态、TCP 445 probe、受支持发行版/包管理检测；Windows 实现 `LanmanServer`、Windows SMB Server API/module capability、TCP 445、服务状态与 Server SMB security snapshot 检测。把 Server DI、Endpoint 映射和读状态接通；未安装、Helper 缺失、pipe/API 不可用、无授权、平台/发行版不支持和服务失败必须可区分。

**验收**：Server、Provider、Endpoint 不直接执行 `sudo`、shell、`systemctl`、`testparm`、`smbpasswd`、PowerShell 或 Windows command；Helper 不接受任意服务名、包名、路径、SID 或命令；在无 Samba 的受支持 Linux 上显示 NotInstalled，在 Windows Server 上准确报告 `LanmanServer`/SMB capability，在未知发行版安全返回不支持；检测和状态测试不要求测试机具有 root/Samba/LocalSystem。

### Goal 3：安装与安全服务生命周期

**工作**：Linux 实现固定 Samba 包安装、重新检测、Start/Stop/Restart/Reload 与端口冲突检查；Windows 实现 `LanmanServer` Start/Stop/Restart、SMB security preflight 与端口冲突检查。Windows 不尝试安装 File Server role/feature，缺失 API/服务返回可操作状态。服务动作绑定 `SmbManage` elevation 和 operation ID；所有非幂等执行采用每 SMB protocol lock 串行化。写入真正配置前先建立服务/操作审计基础。

**验收**：未获 elevation 的管理请求不触及 Helper；Linux 安装输入不能改变包、源或命令；服务动作不会影响非 `smbd` 或 `LanmanServer`；状态和健康分别报告 service stopped、service failed、port unavailable、port conflicted、Windows API unavailable 与 Helper unavailable；重复/并发 start/restart 不产生竞争或错误的成功状态。

### Goal 4：托管配置、共享与验证事务

**工作**：Linux 实现唯一 include marker、managed include reader/writer、严格共享/权限验证、Samba 规则映射与 deterministic serialization。Windows 实现受限 SMB API 的 share/ACL 映射、ownership ledger、实际 object/ACL snapshot 与 rollback。两平台完成创建、更新、禁用、删除托管 share，分别使用 Linux `testparm`/备份/原子替换/reload 或 Windows preflight/snapshot/API apply/reconciliation；两者均执行端口与服务健康检查及失败恢复。系统现有的非托管 shares 仅在状态中标为 external/不可管理，永不编辑。

**验收**：用户输入不能添加 Samba option、section、include、PowerShell 或换行；敏感路径、symbolic link/reparse point 逃逸、重复 share、未知 user/group/SID、无效访问等级和真实 Unix/NTFS 文件系统权限冲突均被阻止或明确提示；Linux 候选配置失败时活动配置和可工作服务保持不变，Windows apply/health failure 后 share/ACL/security snapshot 能恢复或返回 reconciliation-required，绝不显示伪成功。

### Goal 5：平台身份能力、审计和连接信息

**工作**：Linux 实现对现存本地系统账户的 existence/eligibility 验证、Samba credential enable/disable、一次性 password set/change 和列表状态映射；Windows 实现 local/domain SID principal 验证与 share ACL display/mapping，不管理 Windows 帐户或密码。完成有界的 SMB connection-information API。为 install、lifecycle、share、Linux credential 和 Windows ACL/security 变更实现主机级 audit 记录，包含 actor、JWT 安全引用、操作 ID、protocol、受控资源 ID/路径哈希、结果、problem code、时间与 Helper 版本。

**验收**：Samba 密码从不出现在 GET、日志、审计、异常、数据库、operation payload 或 UI 重载状态；不存在的或不合格的 Linux 本地账户不能被启用，未知 Windows SID/principal 不能写入 ACL；禁用/删除 Samba 凭据不会删除宿主系统账户；Windows 不会返回、设置或保存帐户密码；审计可追溯管理动作但不保存完整路径、SID display name、密码或原始 Samba 配置；连接信息只展示主机、端口和 share 名，不启动外部客户端。

### Goal 6：Avalonia 管理应用与可观测性

**工作**：完成 File Services 内置 App 的 SMB 概览、按 capability 显示的安装/状态、生命周期、共享列表/编辑、Linux Samba 用户与密码对话框、Windows ACL principal 选择、权限冲突/外部配置或 share-drift 提示、连接信息和安全状态。使用已有 typed HTTP client、应用注册、对话框、取消/加载模式和 `en-US`、`zh-CN`、`ja-JP` 本地化。无权限、Linux 未安装、Windows API/服务缺失、外部配置、待授权、服务故障、回滚失败和不支持平台必须显示实际状态。

**验收**：ViewModel 不构造 `HttpClient`、不拼 URL、不接触系统 API、Samba/Windows native 细节或密码持久化；Client 从不显示 Helper stderr/command；Windows 不显示 Linux Samba 凭据功能，Linux 不显示 Windows ACL 专属能力；取消请求不会中断不可逆 Helper 事务而使 UI 声称已取消；不支持的 SFTP/FTP 等协议不出现卡片、菜单或 feature flag。

### Goal 7：受控集成验证、运维文档与发布收尾

**工作**：在隔离的 Debian/Ubuntu VM 与 Windows Server VM 上，以普通 Server 服务账户验证平台对应的安装/检测、share CRUD、Linux Samba 凭据或 Windows ACL、服务控制、人工 drift、坏配置/API apply 失败、端口冲突、Helper 缺失/拒绝、JWT 到期、并发操作和回滚。补齐两平台的管理员部署、configuration/share ownership、允许共享根、备份恢复、故障诊断、卸载和后续协议范围说明。

**验收**：两平台的 `RelaxKonOS.Server` 均无需 root/LocalSystem/Administrator 运行；实际第三方 SMB 客户端能够访问受控 share，且其文件数据不经过 Server；所有成功写操作都关联受限 Helper 审计；不开放网络 Helper、通用命令/PowerShell API 或未受管配置/share 覆盖；构建和自动化测试稳定通过。

## 5. 测试与完成定义

自动化测试至少覆盖：

- Protocol JSON、路由与 problem-code 稳定性；任何响应序列化都不包含密码、Samba-native 类型、Windows CIM/registry 类型或安全 descriptor。
- Provider resolver、Manager per-protocol lock、授权 jti/capability/target/TTL 隔离、Endpoint policy 与 elevation-required 路径。
- share、username、group/SID、描述、路径、symbolic link/reparse point、敏感目录、port 和 permission validators；特别覆盖换行、`[global]`、`include`、`=`、控制字符、option injection 和 Windows default share/SID 边界。
- Linux managed include parser/serializer、marker 冲突、external drift、`testparm` failure、atomic apply、rollback；Windows ownership ledger、share/ACL snapshot、drift、API apply/reconciliation；两平台 health 状态转换。
- Helper 对每个 SMB operation 的 allowlist、Linux 系统账户/Windows SID 检验、固定 package/service/API 资源、请求大小/超时/operation ID、secret redaction 和 fail-closed 行为。
- fake adapter/transport 的 install、service、share、Linux credential、Windows ACL/security 和审计集成测试；CI 不要求 root、LocalSystem、真实 Samba、Windows SMB 或 TCP 445。

隔离 VM 手工验证至少覆盖：Linux 的 Samba 未安装/安装、stop/start/restart、Samba password 改动、主配置人工 drift、无效 share 配置回滚；Windows 的 `LanmanServer`、share/ACL CRUD、外部 share/ACL drift、security policy change/recovery；两平台均验证只读/读写 share、关闭 guest、Windows Explorer 或 `smbclient` 的真实访问、445 被占用、Server 非 root/LocalSystem 运行、Helper 不可用和授权过期。不要在开发机或生产服务器首次验证 root-owned Helper 或 LocalSystem SMB mutation。

SMB V1 只有同时满足以下条件才可标记完成：

1. Goal 0–7 的验收全部满足，且 Linux/Samba 与 Windows Server SMB 支持范围清楚可操作。
2. RelaxKonOS 是 SMB 控制面而非数据面：没有协议栈、代理、传输会话或文件客户端实现。
3. 所有特权变更经封闭、版本化的 Helper contract；无 shell、任意服务/包、任意路径或 generic config-write 接口。
4. Linux 托管配置先验证后原子应用，Windows 受管 share/ACL/security 先 snapshot 再 apply；失败时保留或恢复最后一个已知可工作状态；管理员未托管配置/share 不被覆盖。
5. SMB1/guest/anonymous-write 安全默认值、路径边界、系统账户验证和文件系统权限提示均已落实。
6. 密码和其他秘密不出现在 API、Client 状态、日志、审计、异常或普通持久化中。
7. UI/API/审计中的状态可反映实际 Samba 或 Windows SMB 服务与配置，不把“请求已接受”伪装为“SMB 已健康”。

## 6. 后续范围（不在本轮实施）

SMB V1 稳定后，后续 Goal 按独立设计审查推进：

1. Linux SFTP / OpenSSH；
2. Windows OpenSSH SFTP；
3. Firewall 与端口变更的显式跨模块工作流；
4. FTPS（优先）及明文 FTP 的安全警告；
5. WebDAV、NFS、Windows File Server role 安装、宿主账户生命周期或更高级 Samba/AD/cluster 能力。

每项都必须复用本文件确立的控制面、Provider、最小权限、秘密、配置事务和审计原则，但必须重新定义自己的协议、host scope、配置所有权、威胁模型和验收；不得通过在本次 SMB 实现中提前塞入未验证的泛化接口来“预实现”。

## 7. 后续 Goal 模式提示

> 依据 `docs/RelaxKonOS.FileServices.Smb.Goal.md` 与 `docs/RelaxKonoS File Services 设计与实现规格.md` 实现 RelaxKonOS 的首轮 File Services。严格按 Goal 0–7 顺序，实现 Linux（Debian/Ubuntu 系）Samba 与 Windows Server 2019+ Windows SMB Server 的 SMB 控制面；SFTP、FTP/FTPS、WebDAV、NFS 和 Windows File Server role 安装全部留到后续独立 Goal。RelaxKonOS 不实现或代理 SMB 数据面，所有 Client↔Server 契约置于 Protocol，所有高权限操作经封闭的 `IPrivilegedOperationTransport` / PrivilegedHelper，禁止 shell、PowerShell、任意命令、任意服务/包/路径/SID 或 generic config write。Linux 只管理 RelaxKonOS 拥有的 Samba include/share，先 `testparm` 验证、原子应用、reload 和 health check；Windows 只管理 ownership ledger 标识的 share/ACL/security snapshot，经受限系统 API apply、health check 和失败回滚。不得覆盖管理员配置/share、泄露 Samba 密码或管理宿主账户。每个 Goal 的构建、测试和验收完成后才能进入下一项。
