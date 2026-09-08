# RelaxKonOS Security Design

> 本文档定义 RelaxKonOS 安全设计原则。
>
> RelaxKonOS Server 跨平台支持 **Ubuntu（Linux）** 与 **Windows Server**。RelaxKonOS 不实现独立权限系统，而是基于宿主 OS 原生用户、权限、权限提升机制提供安全操作体验。
>
> RelaxKonOS 的目标不是替代宿主 OS Security，而是将 OS 管理能力包装成类似现代桌面操作系统的安全交互模型。
>
> 本文档属服务端安全层设计。**落地状态**：`RelaxKonOS.Server` 已实现 auth 端点 + JWT + `IIdentityProvider`（复用宿主 OS 认证，不存储密码）+ 登录端点限流、三维暴力破解防护及认证安全审计 + 文件管理端点（复用宿主 OS 权限，不另建 ACL）+ 终端 PTY（以宿主 OS 用户身份执行）。权限提升（sudo/UAC）、危险操作二次确认等安全交互能力将随系统逐步实现。
>
> 相关文档：
>
> - [`RelaxKonOS.Authentication.md`](./RelaxKonOS.Authentication.md)
> - [`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md)
> - [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - [`RelaxKonOS.ProcessGuardian.md`](../applications/RelaxKonOS.ProcessGuardian.md)
> - [`RelaxKonOS.md`](../README.md)

---

## 1. 设计目标

RelaxKonOS 面向：

- 个人服务器用户
- 网站管理员
- 小型团队服务器

主要场景：

- 文件管理
- 网站部署
- 服务管理
- Docker 管理
- Terminal 操作

RelaxKonOS 不追求：

- 多租户安全隔离
- 云平台级权限控制
- 企业 IAM 系统

核心目标：

> 让用户可以安全地管理服务器（Linux 或 Windows），同时减少误操作风险。

### 1.1 跨平台支持

| 能力 | Ubuntu (Linux) | Windows Server |
|------|----------------|----------------|
| 用户身份 | Linux User | Windows Account |
| 文件权限 | rwx / ACL / Capability | NTFS ACL |
| 权限提升 | sudo (PAM) | UAC / RunAs |
| 服务管理 | systemctl | Windows Service (sc.exe / ServiceController) |
| 强制访问控制 | SELinux / AppArmor | Windows Integrity Level |

设计原则：平台差异封装在 OS 抽象层之后，RelaxKonOS 上层提供统一安全交互模型。

---

## 2. Security Model

RelaxKonOS 安全模型：

```text
RelaxKonOS Application
    |
    v
Platform User Context (Linux User / Windows Identity)
    |
    v
Platform Security Model (Linux Permission / Windows ACL)
    |
    v
Operating System
```

RelaxKonOS 不拥有最终权限。最终决定权在 **宿主 OS Kernel**（Linux Kernel / Windows NT Kernel）。

当前产品采用“单机网站管理员”模式：第一方内置应用视为可信管理工具，不实施应用级 RBAC、租户隔离或 `AppPermissions` 授权页。该简化仅适用于已认证、由管理员运行的 RelaxKonOS 实例；不会绕过宿主 OS 的权限、API 认证、危险操作确认、输入校验或审计。将来提供非管理员账户或第三方应用前，必须恢复细粒度授权和资源隔离。

---

## 3. Responsibility Boundary

### RelaxKonOS 负责

- 操作确认
- 风险提示
- 用户交互
- 权限提升请求（sudo / UAC）
- 操作日志
- 状态恢复

### 宿主 OS 负责

- 用户身份
- 文件权限
- Process 权限
- Service 权限
- Network 权限
- Kernel Security

---

## 4. Principle

### 4.1 默认最小权限

RelaxKonOS Application 默认使用当前登录用户身份。

例如：

```text
Login:       alice
Application: RemoteExplorer
Process:     alice   (Linux: alice / Windows: MACHINE\alice)
```

不会默认使用 `root`（Linux）或 `Administrator`（Windows）。

### 4.2 权限不足时提升

RelaxKonOS 不提前判断所有权限，直接执行操作：

```text
User Action
    |
    v
Platform Operation
    |
    +-- Success
    |
    +-- Permission Denied
            |
            v
      Request Privilege Escalation
```

原因——宿主 OS 权限模型已经非常复杂：

- Linux：ACL、Group、Capability、SELinux、AppArmor
- Windows：NTFS ACL、UAC、Integrity Level、Token Privileges

RelaxKonOS 不重复实现。

---

## 5. Privilege Escalation

权限提升采用宿主 OS 原生机制。

```text
RelaxKonOS
    |
    +-- Linux   → sudo → PAM Authentication  → root Capability
    +-- Windows → UAC  → RunAs / Consent UI  → Elevated Token
```

### 5.1 ProcessGuardian `RunAs`

进程守护的 `RunAs` 只保留两条规则：任何已登录用户指定自己时无需额外验证；指定任何其他宿主账户（即使请求者是 `root` 或管理员）时，必须在该次提交中重新验证一名宿主管理员的账户名和密码。客户端默认填入 Linux 的 `root`、Windows 的 `Administrator`，但密码必须由用户输入。用户工作负载不设 Owner、路径白名单或逐工作负载 ACL；任意已登录用户可以配置和启停任意工作负载。该规则仍不能绕过宿主 OS 的账户状态、启动令牌和文件 ACL 检查。

管理员密码只通过受保护请求即时交给 `IIdentityProvider` 验证，随后立即丢弃；不得写入数据库、定义、日志、审计、浏览器存储、缓存或 Guardian Agent IPC。完整的字段、内置 Server/Agent 的处理及跨平台启动限制见 [`RelaxKonOS.ProcessGuardian.md`](../applications/RelaxKonOS.ProcessGuardian.md) §2.3。

---

## 6. File Operation Security

RemoteExplorer 管理文件。

**普通操作**（Create File / Rename File / Move File）：直接执行。

**危险操作**（Delete System File / Delete Directory / Modify Configuration）流程：

```text
User Click Delete
    |
    v
Check Platform Permission (Linux rwx / Windows ACL)
    |
    +-- Allowed
    |
    +-- Denied
            |
            v
      Request Authentication (sudo / UAC)
            |
            v
        Execute
```

---

## 7. Dangerous Operation Confirmation

RelaxKonOS 对危险操作提供二次确认。

例如删除系统目录，提示：

```text
This operation requires administrator permission.
Target: /etc/nginx          (Linux)
       C:\Windows\System32   (Windows)
Continue?
```

确认后执行提权删除（Linux: `sudo rm` / Windows: elevated delete）。

---

## 8. Operation Risk Level

RelaxKonOS 可以根据操作风险提供提示。风险分级与平台无关，由 RelaxKonOS 上层统一定义。

### Level 0 — 普通操作

Read File / Open Application / View Status。无需确认。

### Level 1 — 用户目录操作

Delete User File / Modify Application Data。普通确认。

### Level 2 — 系统配置

Modify：

- Linux：`/etc`、`/usr`、`/var`、System Service
- Windows：`C:\Windows`、`C:\Program Files`、Registry、Windows Service

需要管理员确认。

### Level 3 — 高风险操作

Delete Disk / Modify Boot / Change Firewall / Remove User。需要：

- 明确确认
- 管理员认证
- 操作记录

---

## 9. RemoteTerminal Security

RemoteTerminal 不创建新的 Shell 权限。

模型：

```text
RemoteTerminal
    |
    v
PTY (Linux) / ConPTY (Windows)
    |
    v
Shell (bash / PowerShell / cmd)
    |
    v
Platform User (Linux User / Windows Account)
```

例如：

```text
Linux:    whoami → alice
Windows:  whoami → machine\alice
```

> 开发期便利：Server 跑在本地 Windows Server 时，RemoteTerminal 直接使用本机 PowerShell/cmd，无需传输代码到 Ubuntu。

---

## 10. Privilege Command Handling

RelaxKonOS Server normally runs as an unprivileged service account. Linux packages also install
`RelaxKonOS.PrivilegedHelper`: a root-owned local executable invoked through the dedicated
`sudo -n` rule. It accepts local structured operations for protected file reads/writes and a
generic `run` operation for future host-management capabilities; it is not an HTTP server and
is never exposed on a network port.

For RemoteExplorer, a failed direct read prompts a non-root user for their current host password.
PAM validates the password, then grants the exact path to the current JWT for five minutes. For a
failed copy, move, delete, rename, or upload, the same prompt grants the affected directory scope
to that JWT for five minutes, including its descendants, so a folder upload does not repeatedly
prompt. Root users skip this prompt. Subsequent protected reads and writes for that path or
authorized directory are passed to the helper. This is a desktop-style confirmation flow, not an isolation boundary: the generic helper
means compromise of the RelaxKonOS Server service account can lead to root command execution.

用户执行提权命令时，RelaxKonOS 不拦截，交给宿主 OS：

```text
Linux:    sudo apt install nginx
              |
              v
           PAM Authentication → Execute

Windows:  elevated command (需 UAC)
              |
              v
           UAC Consent UI → Execute
```

RelaxKonOS 只负责：显示认证界面、保存 Session 状态、显示执行结果。

---

## 11. Application Security

RelaxKonOS Application 不直接访问系统。应用访问系统资源：

```text
Application
    |
    v
RelaxKonOS App SDK
    |
    v
System API (via OS Abstraction)
    |
    v
Host OS (Linux / Windows)
```

禁止：

```text
Application
    |
    v
Direct Shell / Direct OS API
```

---

## 12. Application Capability

未来 Application 可以声明能力。

例如：

```text
Manifest:
  Application: RemoteExplorer
  Capabilities:
    - filesystem.read
    - filesystem.write
```

但是：**Capability 不替代宿主 OS Permission**。最终：

```text
Application Capability
    +
Platform Permission (Linux / Windows)
    |
    v
Allowed Operation
```

---

## 13. Docker Security

Docker 是特殊资源——默认拥有接近 root / Administrator 权限。

RelaxKonOS 不应该默认允许：`docker exec` / `docker rm` / `docker run --privileged`。

操作流程：

```text
Docker Operation
    |
    v
Check Platform Docker Permission (Linux docker group / Windows Docker)
    |
    v
Require Confirmation
    |
    v
Execute
```

---

## 14. Service Management

管理系统服务：

```text
Linux:    systemctl restart nginx
Windows:  Restart-Service nginx  (或 sc.exe)
```

流程：

```text
RelaxKonOS Settings
    |
    v
Service Manager (systemctl / ServiceController)
    |
    v
Platform Permission
    |
    v
Service Change
```

危险服务操作需要：确认、管理员认证。

---

## 15. Audit Log

RelaxKonOS 保存用户操作记录。

例如：

```text
AuditLog
  User:   alice
  Action: Restart nginx
  Time:   2026-01-01
  Result: Success
```

**记录**：用户、时间、操作、目标资源、执行结果。

**不记录**：密码、Token、私密数据。

---

## 16. Session Security

Session 保存：User、Device、Workspace、Authentication State。

Session 生命周期：

```text
Created → Active → Disconnected → Expired
```

---

## 17. RelaxKonOS Server Security

RelaxKonOS Server 应：

- 最小宿主 OS 权限运行
- 不默认 root（Linux）/ 不默认 Administrator（Windows）
- 使用 sudo / UAC 执行需要权限的任务

当前 Linux 部署脚本会创建（或复用）`relaxkonos-server` 系统账户，以该账户运行 HTTP Server，并仅通过 root-owned 的受限 helper 执行防火墙操作。推荐模式为：

```text
Linux:    relaxkonos-server → sudo limited commands
Windows:  relaxkonos-server → elevated limited commands (via UAC)
```

而不是：

```text
Linux:    relaxkonos-server → root
Windows:  relaxkonos-server → Administrator
```

---

## 18. 实现路线

首批实现（双平台）：

- Platform User Context（Linux / Windows）
- 权限提升支持（sudo / UAC）
- 文件权限处理（Linux rwx / Windows ACL）
- Terminal 用户隔离
- 基础操作确认

后续逐步完善：

- RelaxKonOS ACL / RBAC / IAM
- 多租户隔离
- 自定义安全策略

---

## 19. AI Agent Rules

实现 RelaxKonOS 安全相关功能时：

**必须**：

- 使用宿主 OS Security Model（Linux 或 Windows）
- 通过 OS 抽象层封装平台差异
- Linux 使用 sudo / PAM，Windows 使用 UAC / RunAs
- 保持最小权限
- 高风险操作需要确认

**禁止**：

- 创建新的权限体系
- 绕过宿主 OS Permission
- 默认使用 root / Administrator
- 将 RelaxKonOS 设计为多租户平台
- 存储宿主 OS 密码
