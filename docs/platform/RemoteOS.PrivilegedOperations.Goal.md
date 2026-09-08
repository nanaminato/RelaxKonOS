# RemoteOS 跨平台特权操作与 Helper（Goal 执行版）

> 2026-09-07 设置能力补充：[`SettingsSystem.Goal`](../desktop/RemoteOS.SettingsSystem.Goal.md) 允许新增结构化宿主环境变量、时区、主机名与 DNS 操作。环境配置数据与特权进程启动环境必须隔离；禁止通用执行的原则保持不变。本文部分“当前状态”为早期基线，实施前应核对已存在的 Windows 管道与封闭操作实现。

> 状态：待实施
>
> 建立日期：2026-09-04
>
> 适用范围：`.NET 10` Server、Avalonia Client、Ubuntu/Linux、Windows Server
>
> 前置依据：[安全模型](./RemoteOS.Security.md)、[认证模型](./RemoteOS.Authentication.md)、[Web Server Manager 设计](../applications/RemoteOS.WebServerManager.Design.md)

本文是“将所有需要宿主 OS 提升权限的 RemoteOS 操作收敛到受限 Helper，并支持 Windows Server”的 Goal 执行基线。它不是把 Server 进程改为 root、LocalSystem 或 Administrator 的方案；Server 必须继续以最小权限服务账户运行。

设计或实现与本文冲突时，必须先更新本文并重新审查。不得以 `catch UnauthorizedAccessException` 后直接在 Server 内运行 `sudo`、`cmd.exe`、PowerShell 或任意命令作为临时绕过。

## 1. 可行性结论与必须接受的约束

结论：**可行，但 Windows 需要不同于 Linux sudo 的架构，且“所有操作”必须先完成清单化。**

### 1.1 Linux

当前 Linux 已有可复用基础：`RemoteOS.PrivilegedHelper` 是 root-owned 的本地可执行文件，`LocalPrivilegedOperationRunner` 通过固定 sudoers 规则调用它；文件资源管理器已将直接 I/O 被拒绝后的认证授予绑定到当前 JWT、有效期五分钟。

这条路径可以扩展为统一的 Linux Helper transport，但存在两个必须修复的边界：

- 早期 Helper 的通用 `run` 操作不能成为产品级 API；当前实现应维持封闭操作集，不接受任意 executable、参数、shell 文本或特权执行环境注入。专用环境配置操作按 SettingsSystem Goal 的目标绑定与校验规则扩展。
- Nginx、受限 native service、Proxy 生命周期、Git/Docker 安装等仍会由 Server 直接启动 `systemctl`、`apt-get` 或其他宿主进程，不能满足本 Goal。

### 1.2 Windows Server

不能在 Windows Server 的 `RemoteOS.Server` Windows Service 中按需显示 UAC：服务运行在 Session 0，通常没有可交互桌面；`runas` / `ProcessStartInfo.Verb = "runas"` 既不可靠，也不能把远程 Client 的管理员确认安全地映射到服务器控制台。

Windows 的可行模型是：安装时由已提升的签名安装程序部署一个 **LocalSystem 的 `RemoteOS.PrivilegedHelper` Windows Service**。普通 `RemoteOS.Server` 通过本机命名管道发送强类型请求。UAC 只用于安装、升级、修复或卸载该 Helper 服务；业务操作不依赖 GUI UAC。

Windows Helper 服务必须同时满足：

- 命名管道 ACL 仅允许 Server 服务 SID / 指定服务账户、LocalSystem 与 Administrators；拒绝 `Everyone`、普通交互用户和远程网络客户端。
- Server 与 Helper 还要使用安装时生成、仅双方可读的随机共享密钥或 Windows SSPI/服务身份认证，防止同机低权限进程伪装 Server 连接管道。
- Helper service binary、目录、配置和密钥使用 Program Files / ProgramData 下由 LocalSystem 与 Administrators 独占的 ACL；安装/启动时验证可执行文件签名或版本清单。
- Helper 只监听本机命名管道，不公开 HTTP、TCP、RPC endpoint，也不以用户会话进程的形式运行。

### 1.3 管理员认证

当前 `IIdentityProvider.Verify` 足以复验密码，但 Windows 的“当前登录用户密码”不必然是管理员密码。Windows 版本必须新增一个 Server-only 的宿主管理员验证边界：使用 `LogonUser` 得到临时 token，并检查其是否属于 local/domain Administrators；token 立即释放，密码、token 和 SID 不写日志或持久化。

客户端需要复用已有系统认证窗口，但 Windows 允许输入管理员账户名和密码（默认可预填当前用户名，不能假定是 `Administrator`）。Linux 可继续使用当前登录用户密码的现有体验；是否允许 Linux 以另一个 sudo 管理员认证必须作为独立安全决策，不能静默扩权。

认证成功后，Session Store 为**当前 access-token 的 `jti`** 写入精确的操作能力与目标范围，TTL 固定五分钟。JWT 刷新、退出登录、失效或 Helper 拒绝都使授权不可用；不得把授权绑定为长期用户名、Workspace 或 Client 进程状态。

## 2. 当前代码基线与缺口

| 范围 | 当前状态 | Goal 后状态 |
|---|---|---|
| Explorer 受保护文件 | Linux sudo Helper；文件读写与复制/移动/删除/重命名/上传的 JWT 短期授权已接入 | Linux 与 Windows 都经统一 transport；所有 helper 操作有固定目录作用域和审计 |
| Helper transport | Linux 为 stdin/stdout 一次性进程；Windows 直接启动 Helper，**不会提升** | `IPrivilegedOperationTransport` 选择 Linux sudo one-shot 或 Windows LocalSystem named-pipe service |
| Nginx | `IHostPrivilegeService.IsAdministrator` 直接要求 Server 本身为 root/Administrator；安装、启停和配置写入在 `NginxWebServerManager` 直接执行 | 所有写配置、安装/卸载、启停/reload 通过 Nginx 专用 helper operation |
| Native services | `NativeServiceAdapter` 直接运行 `systemctl` / `sc.exe` | 仅 allowlist 服务名及 start/stop/restart 通过 helper |
| Proxy / Mihomo | Linux 直接执行 systemctl；Windows 由 Server 持有子进程 | 需要权限的安装、受保护配置、服务与网络操作通过 Proxy 专用 helper；普通受限进程可保留在 Server |
| Firewall | Linux 已有独立、语法受限的 UFW Helper；Windows 不支持 | 保留操作语法，迁入统一 transport / 审计，不引入任意防火墙命令 |
| Certificate、Docker、Git、安装器 | 部分直接以 Server 身份执行，或仅返回 elevation-required | 完成清单审计后，所有实际提升操作迁入对应专用 helper capability |

本 Goal 的“所有需要提升权限”是指：**RemoteOS 发起且执行时需要 root/LocalSystem/Administrator 或等价特权的写入、服务、包、证书、网络、注册表和安全产品操作**。普通读取、用户目录文件、无特权子进程、检测和明确由用户在终端自行执行的命令不应经过 Helper。

## 3. 不可变安全契约

### 3.1 不提供通用命令执行

特权操作的公开或内部协议不得提供 `executable`、`arguments`、shell 文本、PowerShell 脚本、命令行、工作目录、特权执行环境覆盖或任意路径白名单等通用执行输入。不得恢复 `PrivilegedOperationRequest.run` 生产能力。允许按 [`SettingsSystem.Goal`](../desktop/RemoteOS.SettingsSystem.Goal.md) 提交强类型的环境变量配置变更；这些值仅用于目标用户/机器配置，绝不注入 Helper 或特权子进程的启动环境。

每项能力必须是封闭的结构化请求，例如：

```text
file.copy(source, destination, overwrite)
webserver.nginx.install(managedPackageId, version)
webserver.nginx.service(action = start|stop|restart|reload)
native-service.action(serviceId, action = start|stop|restart)
firewall.ufw.create(validated rule fields)
proxy.mihomo.service(action)
```

Helper 自己再次验证 operation kind、枚举值、绝对路径根、受管资源标识、大小上限和状态前置条件。Server 验证不是 Helper 的替代品。

### 3.2 分层职责

```text
Avalonia Client
  → RemoteOS API（JWT、业务确认、管理员认证窗口）
  → Server authorization + IHostElevationSessionStore（jti、能力、目标、5 分钟）
  → 专用领域服务（Nginx / 文件 / 服务 / Proxy / Firewall）
  → IPrivilegedOperationTransport
      ├─ Linux: sudo -n → root-owned one-shot Helper
      └─ Windows: ACL + authenticated named pipe → LocalSystem Helper Service
  → 宿主 OS
```

Endpoint 不传递密码给 Helper；Helper 不解析 JWT，不接触 HTTP，也不决定 UI 权限。认证密码只用于 `IHostAdministratorAuthenticator`，验证后即丢弃。

### 3.3 授权模型

每个高风险 Endpoint 首先执行原有普通权限检查与业务确认，再尝试非特权路径。仅当确实得到可识别的权限不足结果时返回稳定的 `elevation-required` problem code。Client 才显示系统管理员认证窗口，并携带**结构化 capability + 已规范化目标**申请 5 分钟授权，然后重试一次。

授权记录至少为：

```text
jti, subject/userId, capability, target scope hash or canonical target,
issuedAt, expiresAt, authentication method, correlationId
```

- 授权只允许当前 JWT 的同一 capability 和目标；不能由已认证的 `file.copy` 扩展为 Nginx 安装。
- 文件目录授权可覆盖该目录的子项；服务、包、证书和网络操作必须使用精确资源标识，不能以“全部管理员操作”作为 scope。
- Root/LocalSystem 运行 Server 的开发配置不得跳过输入校验、审计或 Helper 路径；仅可省略密码挑战。
- 认证取消、密码无效、账号不是管理员、授权过期、Helper 缺失、transport 认证失败与 Helper 拒绝均返回不同的稳定 problem code 和不泄露秘密的 detail。

### 3.4 审计、幂等和恢复

所有 Helper 调用记录 actor、JWT `jti` 的安全引用、capability、资源 ID/路径哈希、operation ID、Helper 版本、平台、开始/结束时间、结果与 problem code；不得记录密码、token、命令行、私钥、完整文件内容或共享密钥。

安装、卸载、升级、服务切换、网络变更、证书部署和批量文件写入必须使用持久 Operation ID / 幂等键，并定义进程中断后的查询、重试或回滚行为。Helper response 采用版本化结构化 JSON/pipe contract，而非 stderr 文本匹配。

## 4. 建议模块与迁移落点

```text
Shared/RemoteOS.Protocol/Privileged/
  PrivilegedCapability.cs
  PrivilegedOperationRequest.cs          # 删除 generic run 字段
  PrivilegedOperationResult.cs
  ElevationContracts.cs

RemoteOS.Server/Privileged/
  IPrivilegedOperationTransport.cs
  LinuxSudoPrivilegedOperationTransport.cs
  WindowsNamedPipePrivilegedOperationTransport.cs
  IHostAdministratorAuthenticator.cs
  HostAdministratorAuthenticator.{Linux,Windows}.cs
  IHostElevationSessionStore.cs
  PrivilegedCapabilityAuthorizer.cs

RemoteOS.PrivilegedHelper/
  Core/                                   # 纯 operation dispatcher + validation
  Linux/                                  # one-shot host
  WindowsService/                         # LocalSystem service + named-pipe host
  Operations/FileOperations.cs
  Operations/NginxOperations.cs
  Operations/NativeServiceOperations.cs
  Operations/ProxyOperations.cs
  Operations/FirewallOperations.cs

deployment/linux/
deployment/windows/
```

不要让 Endpoint 或 ViewModel 出现 `OperatingSystem.IsWindows()`、`sudo`、`systemctl`、`sc.exe`、PowerShell 或命名管道细节。平台选择只发生在 transport / installer 注册层。

## 5. Goal 执行计划

每个 Goal 完成后必须保持 `dotnet build RemoteOS.sln -c Debug` 可通过，并运行该阶段的自动化测试。任何 Windows 实机验证均使用隔离 Windows Server VM；不得在生产主机首次测试 LocalSystem Helper。

### Goal 0：特权操作清单、威胁模型与冻结决策

**工作**：对 Server 全仓执行 `ProcessStartInfo`、文件系统写入、服务控制、包管理、注册表、网络和证书绑定审计。为每条路径标记“无特权 / 必须 Helper / 明确不支持 / 用户终端自行执行”。冻结支持的 Windows Server 版本、Server/Helper 服务账户、Windows 管理员判定（本地组与 domain group）、Linux sudoers 部署、问题码和审计字段。

**验收**：不存在未分类的可能提升路径；`run` generic helper 的处置已决定；威胁模型覆盖 Server compromise、伪造本地 IPC client、路径穿越、TOCTOU、重放、JWT refresh、授权混淆、Helper downgrade、安装篡改、服务名注入、命令注入、日志泄露和崩溃恢复。

### Goal 1：跨平台协议、管理员认证与 JWT 授权

**工作**：定义 versioned capability / request / result / problem-code 协议；以 capability 而非裸路径扩展现有 `IFileElevationSessionStore` 为 `IHostElevationSessionStore`。实现 `IHostAdministratorAuthenticator`：Linux 保持现有 PAM 语义，Windows 使用 `LogonUser` 后验证 Administrators 成员资格。将 Explorer 迁移到新 capability 授权但保持打开、下载、保存体验不变。

**验收**：同一 JWT、同 capability、同目标可在五分钟内复用；不同 `jti`、不同 capability、同级目录、不同用户与过期记录均被拒绝；Windows 普通用户凭据不能获得管理员 capability；密码不会写入日志、DTO、数据库或异常；Client 三语文案清楚说明认证取消/失败时未执行操作。

### Goal 2：Linux 统一受限 Helper 与 generic-run 移除

**工作**：将 Linux one-shot helper 改为 capability dispatcher；为文件、Nginx、native service、Proxy 与 Firewall 建立各自强类型 operation。更新 `install-remoteos-services.sh`，使发布目录 root-owned、sudoers 仅允许 Helper apphost；删掉独立路径或将 Firewall 迁入同一 transport。为每个操作定义输入大小、路径根、超时和并发限制。

**验收**：Server 服务账户无法通过任何请求执行 `/bin/sh`、`bash -c`、任意 `systemctl` 参数或任意文件路径；已批准的 Nginx/service/file 操作在 unprivileged Server 下可成功执行；非批准资源稳定拒绝；sudoers、Helper 可执行文件和父目录不可由 Server 用户写入。

### Goal 3：Windows LocalSystem Helper Service 与部署

**工作**：实现 Windows Service host、named-pipe protocol、ACL、服务身份/共享密钥认证、请求大小限制、取消/超时与版本握手。扩展 `deployment/windows/Install-RemoteOSServices.ps1`：安装 Helper、生成并保护密钥、配置 Server 服务账户与服务 SID ACL、启动顺序、健康检查、升级与卸载。禁止 Server 直接启动 elevated `.exe`。

**验收**：以非管理员 Server 服务账户运行时，Windows 受保护文件与 allowlisted SCM 操作可经 Helper 执行；普通本地用户不能连接或伪造管道请求；停止/缺失/版本不兼容 Helper 时 Server 安全失败且不降级为直接 Administrator 运行；安装、升级、修复、卸载均可回滚或给出管理员可操作错误。

### Goal 4：Nginx / Web Server Manager 迁移

**工作**：移除 `NginxWebServerManager` 对 `IHostPrivilegeService.IsAdministrator` 的行为性依赖。将受保护配置写入、集成、install/uninstall、enable/disable/start/stop/restart/reload 迁入 `webserver.nginx.*` capability；保留发现、只读状态和配置测试的非特权路径。Linux 的 apt/systemd 和 Windows Nginx 文件/进程行为均固定参数、固定路径、固定受管资源。

**验收**：Server 不再必须以 root/Administrator 启动才能安装或启动受管 Nginx；非管理员 JWT 在没有五分钟授权时得到明确提示；认证后仅能影响选定的受管/allowlisted Nginx instance；外部 Nginx 不被接管；安装失败、配置失败和 reload 失败均不留下半应用状态。

### Goal 5：服务、Proxy、Firewall、Certificate、Docker、Git 等迁移

**工作**：按 Goal 0 清单逐项迁移。优先顺序：`INativeServiceAdapter`、Mihomo privileged service/network operation、Firewall、证书部署/端口绑定、Docker runtime install、Git package installation。每个领域服务只依赖 capability-specific interface，不得共享“万能管理员执行器”。对无法安全建模的能力，先返回 `not-supported` / `manual-host-action-required`。

**验收**：代码审计中所有会要求 root/Administrator 的 RemoteOS 写入或进程调用都位于 Helper operation 或明确的无特权/手工例外清单；任一 Client 输入不能变成可执行命令、服务名、注册表路径或防火墙参数；每项操作具备授权、确认、审计、幂等与失败恢复测试。

### Goal 6：跨平台端到端验证与运维收尾

**工作**：在 Ubuntu 和 Windows Server VM 以最小权限 Server 服务账户验证文件、Nginx、allowlisted native service、Proxy、Firewall（Linux）及已迁移能力。执行 JWT 到期/刷新、错误密码、非管理员凭据、Helper 断开、管道伪造、服务重启、并发操作、取消、升级和恢复测试。补全管理员部署、轮换共享密钥、排障和卸载文档。

**验收**：两平台均无须以 root/Administrator 运行 `RemoteOS.Server`；所有高权限成功操作均可关联到受限 Helper 审计记录；没有开放网络 Helper 或 generic command API；错误信息可操作但不泄露秘密；回归套件覆盖所有 capability、授权隔离和操作失败路径。

## 6. 测试要求

- **协议/授权单测**：capability 枚举、JSON 向后兼容、目标规范化、路径边界、`jti` 隔离、TTL、JWT refresh/expiry、管理员与非管理员认证、problem code。
- **Helper 单测**：每项 operation 的输入拒绝、固定资源验证、路径穿越、符号链接/重解析点、TOCTOU 防护、大小/超时/取消、结构化结果与审计净化。
- **Transport 测试**：Linux sudo runner 不接受 generic operation；Windows pipe ACL/认证/重放/最大消息/断连/版本不匹配；所有失败 fail-closed。
- **领域集成测试**：Nginx install/reload/uninstall、native service action、受保护文件批量上传、Firewall 和 Proxy 的 helper 调用；测试使用 fake transport，不要求 CI 具有 root 或 LocalSystem。
- **实机烟雾测试**：仅隔离 VM，验证 Linux sudoers 和 Windows Service SID ACL、Server 最小权限账户、真实 Nginx / SCM；不在 CI 或开发桌面上授予宽泛管理员权限。

## 7. 明确非目标

- 不把 `RemoteOS.Server`、Guardian 或普通 Client 改为 root、LocalSystem 或 Administrator。
- 不在远程 Client 或 Server Service 中显示/驱动 Windows UAC。
- 不保存宿主 OS 密码、Windows access token、sudo ticket、共享密钥或完整命令行。
- 不提供自定义 shell、PowerShell、可执行文件上传后运行、任意服务名、任意注册表路径或任意包名的提升接口。
- 不将“已认证的 RemoteOS 用户”自动视为宿主管理员。
- 不以“全部管理员权限”的五分钟票据替代 capability + target scope 授权。

## 8. 开始实现前的决策门

在 Goal 1 之前必须由产品/安全负责人明确确认：

1. Windows 管理员认证是否允许输入与当前 JWT 用户不同的宿主管理员账户；推荐允许，并审计其安全引用而非密码。
2. Windows Helper 的服务账户是否固定为 LocalSystem；推荐固定，避免额外可管理高权限账户。
3. Linux 是否继续只接受当前登录用户的 PAM 密码，还是支持显式 sudo 管理员账户；推荐先保持当前语义。
4. Firewall Helper 是迁入统一 service 还是作为等价的受限 Helper 保留；两者都必须满足统一审计与 capability 授权。
5. Goal 0 审计后哪些历史“安装器”能力不能安全结构化，需先降级为 `manual-host-action-required`。

未完成这些决策或 Goal 0 清单前，不应开始 Windows Helper Service 或批量替换 Nginx/服务代码。
