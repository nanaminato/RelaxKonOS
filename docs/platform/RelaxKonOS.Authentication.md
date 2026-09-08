# RelaxKonOS Authentication & Identity Design

> 本文档定义 RelaxKonOS 登录系统、用户身份模型以及**跨平台**操作系统用户集成方式。
>
> RelaxKonOS Server 跨平台支持 **Ubuntu（Linux）** 与 **Windows Server**。RelaxKonOS 不实现独立操作系统级用户体系，而是在宿主 OS 用户系统之上提供统一登录体验和 Workspace 管理。
>
> RelaxKonOS 的定位是云原生桌面操作系统：Server 端跨平台运行，复用宿主 OS 用户与权限体系；Client 端提供跨平台桌面 Shell。主要应用场景为个人服务器、小型团队服务器的桌面化管理。
>
> 本文档属服务端身份层设计。**落地状态**：`RelaxKonOS.Server` 已实现 auth 端点（login/refresh/logout/me）+ JWT（HMACSHA256）+ `IIdentityProvider` 抽象（Windows `LogonUser`、Linux PAM + NSS）+ EF Core/SQLite 持久化（User/Workspace/Device），以及登录端点限流、三维失败计数/递增冷却和认证安全审计。安全提权（sudo/UAC）等能力将随系统逐步实现。详见 [`RelaxKonOS.Login.md`](./RelaxKonOS.Login.md) 与 [`RelaxKonOS.Storage.md`](./RelaxKonOS.Storage.md)。
>
> 相关文档：
>
> - [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - [`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md)
> - [`RelaxKonOS.Security.md`](./RelaxKonOS.Security.md)
> - [`RelaxKonOS.md`](../README.md)

---

## 1. 设计目标

RelaxKonOS 面向：

- 个人服务器用户
- 网站管理员
- 小型团队服务器

而不是：

- SaaS 多租户平台
- 云计算租户隔离系统

因此 RelaxKonOS 不重新设计用户、权限、文件系统。

核心目标：

> 提供类似 Windows / macOS 的服务器桌面操作体验，同时复用宿主 OS（Linux 或 Windows）已有用户和权限体系。

### 1.2 已实现的登录保护

认证请求先经过每 IP 的 HTTP Token Bucket，再检查 IP、账号及账号+IP 三个维度的失败状态；连续失败按递增时间冷却，不会由远程请求永久锁定账号。账号状态和不含密码/令牌的安全事件持久化到 SQLite。具体阈值、`Retry-After` 客户端行为以及受信反向代理配置见 [`RelaxKonOS.Login.md`](./RelaxKonOS.Login.md) §4.6。

### 1.1 跨平台支持

RelaxKonOS Server 支持两种宿主 OS：

| 宿主 OS | 用户体系 | 认证机制 | 权限提升 | Home 目录 |
|---------|---------|----------|----------|-----------|
| Ubuntu (Linux) | Linux User | PAM | sudo | `/home/<user>` |
| Windows Server | Windows Account | LogonUser (Win32) | UAC / RunAs | `C:\Users\<user>` |

设计原则：**RelaxKonOS Server 单一代码库 + OS 抽象层**。平台差异封装在抽象接口之后，上层业务逻辑保持一致。

---

## 2. 用户模型

RelaxKonOS 用户模型：

```text
RelaxKonOS Identity
    |
    v
Platform User (Linux User / Windows Account)
    |
    v
Operating System
```

**RelaxKonOS Identity** 负责：

- 登录体验
- Workspace 管理
- Session 管理
- Device 管理

**Platform User** 负责（由宿主 OS 管理）：

- 文件权限
- Process 权限
- Service 权限
- 权限提升（Linux: sudo / Windows: UAC）

---

## 3. Identity Provider（OS 抽象层）

RelaxKonOS 通过 `IIdentityProvider` 抽象获取用户身份，各平台提供实现：

```text
IIdentityProvider
    |
    +-- LinuxPamProvider        (Ubuntu: PAM 认证)
    +-- WindowsLogonProvider    (Windows Server: LogonUser API)
    +-- LdapProvider            (未来扩展)
    +-- CloudIdentityProvider   (未来扩展)
```

运行时根据宿主 OS 选择对应 Provider（依赖注入 + 平台检测）。

### 3.1 认证流程（跨平台）

```text
RelaxKonOS Login
    |
    v
IIdentityProvider.Authenticate
    |
    +-- Linux   → PAM Authentication  → Linux User Context
    +-- Windows → LogonUser API       → Windows Token / Identity
```

### 3.2 Windows 凭据验证（已验证）

Windows 平台使用 Win32 `LogonUser` API 验证账号密码，支持：

- 本地账户（`MACHINE\user`）
- 域账户（`DOMAIN\user` / `user@domain`）
- 纯用户名默认验证本机

错误码映射：用户名或密码错误 / 用户不存在 / 账户禁用 / 账户锁定 / 密码过期 / 账户过期 / 账户受限 / 未授予网络登录权限。

> 参考实现：`RelaxKonOS.Server/Identity/WindowsLogonProvider.cs`（迁移自 `Windows Server Test` 测试床，现为 Server 端 `IIdentityProvider` 的 Windows 实现，单一真源；`Windows Server Test` 项目改为引用 Server 调用 `IIdentityProvider` 验证）。

### 3.3 Linux 凭据验证

Linux 平台通过 PAM（Pluggable Authentication Modules）验证账号密码，复用系统标准认证链。

---

## 4. 登录流程

### 4.1 Client 登录

```text
RelaxKonOS.Client Start
    |
    v
Authentication Request
    |
    v
RelaxKonOS.Server
    |
    v
IIdentityProvider.Authenticate (Linux PAM / Windows LogonUser)
    |
    v
Create Session
    |
    v
Load Workspace
    |
    v
Desktop Ready
```

---

## 5. User

User 表示 RelaxKonOS 身份对象，与宿主 OS 用户建立映射。

例如：

```text
RelaxKonOS User
  Id:              550e8400
  Username:        alice
  Platform:        Windows   (或 Linux)
  PlatformIdentity: MACHINE\alice   (Linux: alice)
```

- RelaxKonOS **不保存**宿主 OS 密码。
- 密码认证由宿主 OS 处理（Linux: PAM / Windows: LogonUser）。

---

## 6. Workspace

一个 User 默认拥有一个 Workspace。

关系：

```text
User
  |
  v
Workspace
  |
  v
Session
  |
  v
Device
```

Workspace **不等同于**宿主 OS 的 Home 目录。

Workspace 保存：

- Desktop State
- Application State
- RelaxKonOS Preference
- Session State
- Device Binding
- Platform Identity Context

宿主 OS Home 目录保存（跨平台）：

| OS | Home 路径 | 保存内容 |
|----|-----------|----------|
| Linux | `/home/<user>` | User Files、Application Data、System Files |
| Windows | `C:\Users\<user>` | User Files、Application Data、System Files |

---

## 7. Permission Model

RelaxKonOS 不实现独立权限系统。所有实际权限由宿主 OS 管理。

模型：

```text
RelaxKonOS Application
    |
    v
Platform User Context
    |
    v
Platform Permission System (Linux Permission / Windows ACL)
    |
    v
Operating System
```

宿主 OS 已提供的能力（RelaxKonOS 不重复实现）：

| 能力 | Linux | Windows |
|------|-------|---------|
| 用户/组 | User / Group | Account / Group |
| 文件权限 | rwx / ACL / Capability | NTFS ACL |
| 权限提升 | sudo | UAC / RunAs |
| 强制访问控制 | SELinux / AppArmor | (Windows Integrity Level) |

---

## 8. 权限提升

当用户执行需要更高权限的操作时，RelaxKonOS 请求宿主 OS 权限提升。

```text
Delete /etc/nginx  (Linux)      或   Delete C:\Windows\System32 (Windows)
    |
    v
Platform Permission Check
    |
    +-- Success
    |
    +-- Permission Denied
            |
            v
      Request Privilege Escalation
            |
            +-- Linux   → sudo (PAM)
            +-- Windows → UAC / RunAs
            |
            v
        Execute
```

类似体验：Windows UAC、macOS Administrator Authentication、Linux sudo。

---

## 9. Database Design

RelaxKonOS 数据库只保存 RelaxKonOS 状态。

**保存**：RelaxKonOS User、Platform User Mapping、Workspace、Session、Device。

**不保存**：宿主 OS 密码、宿主 OS 权限、ACL。

数据库：SQLite / PostgreSQL。首批实现推荐 SQLite。

---

## 10. User Table

Table: `users`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | RelaxKonOS User ID |
| username | string | RelaxKonOS 用户名 |
| platform | string | 宿主 OS（`linux` / `windows`） |
| platform_identity | string | 宿主 OS 用户标识（Linux: username；Windows: `domain\user`） |
| created_at | datetime | 创建时间 |
| last_login_at | datetime | 最后登录时间 |

Example:

```text
id:                550e8400
username:          alice
platform:          windows
platform_identity: MACHINE\alice
```

---

## 11. Workspace Table

Table: `workspace`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Workspace ID |
| user_id | uuid | 所属用户 |
| name | string | Workspace 名称 |
| state | string | 状态 |
| created_at | datetime | 创建时间 |

Example:

```text
Alice Workspace
State: Running
```

---

## 12. Session Table

Table: `session`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Session ID |
| workspace_id | uuid | Workspace |
| device_id | uuid | 设备 |
| created_at | datetime | 创建时间 |
| last_active_at | datetime | 最后活动 |
| status | string | 状态 |

状态：见 [`RelaxKonOS.Security.md`](./RelaxKonOS.Security.md) §16（`Created` → `Active` → `Disconnected` → `Expired`）

---

## 13. Device Table

Table: `device`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Device ID |
| name | string | 设备名称 |
| platform | string | 平台 |
| client_version | string | 客户端版本 |
| last_login_at | datetime | 最后连接 |

---

## 14. Platform Integration

RelaxKonOS Server 运行于宿主 OS 之上：

```text
relaxkonos-server
    |
    v
Host OS (Ubuntu / Windows Server)
```

RelaxKonOS 不管理用于登录的宿主 OS 用户或密码数据库。Linux 部署脚本唯一的例外是创建/复用不允许交互登录的 `relaxkonos-server` **服务账户**，用于以最小权限运行 Server；它不属于 RelaxKonOS 登录账户，不写入业务用户资料。Windows 不操作 SAM / AD。

---

## 15. RemoteTerminal

RemoteTerminal 使用当前登录用户的宿主 OS 身份执行。

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
RelaxKonOS User:  alice
Platform:       Windows
Terminal:       PowerShell -- MACHINE\alice

RelaxKonOS User:  alice
Platform:       Linux
Terminal:       bash -- alice
```

权限提升：

```text
Linux:    sudo command      → PAM Authentication → Execute
Windows:  elevated command  → UAC prompt         → Execute
```

> 开发期便利：Server 跑在本地 Windows Server 时，RemoteTerminal 直接操作本机，无需传输代码到 Ubuntu。

---

## 16. 实现路线

首批实现（双平台）：

- Linux User 登录（PAM）
- Windows Account 登录（LogonUser）
- Workspace 创建
- Session 管理
- Device 管理
- Platform Identity Mapping

> **落地状态（2026-08-09）**：Windows Account 登录由 `WindowsLogonProvider` 调用 `LogonUser`；Linux 登录由 `LinuxPamProvider` 调用宿主 `libpam.so.0` 的 `pam_authenticate` + `pam_acct_mgmt`，并通过 NSS `getpwnam_r` 读取 UID、显示名和 Home（兼容 `/etc/passwd`、SSSD/LDAP 等 NSS 数据源）。Workspace 创建 / Device 管理 / Platform Identity Mapping 已通过 EF Core/SQLite 持久化仓储落地；Session 维持内存。auth 端点（login/refresh/logout/me）+ JWT 已实现。详见 [`RelaxKonOS.Login.md`](./RelaxKonOS.Login.md) 与 [`RelaxKonOS.Storage.md`](./RelaxKonOS.Storage.md)。

后续逐步完善：

- 独立密码系统（不实现，复用宿主 OS）
- RelaxKonOS ACL / Role（不实现，复用宿主 OS 权限）
- 多租户隔离（不实现）
- LDAP / Cloud Identity Provider

---

## 17. AI Agent Rules

实现登录系统时：

**必须**：

- 使用宿主 OS User 作为最终执行身份
- 通过 `IIdentityProvider` 抽象，平台差异封装在 Provider 实现
- 保留 Workspace 模型
- 保留 Session / Device 模型
- 不复制宿主 OS 权限体系

**禁止**：

- 创建新的 OS 替代用户体系
- 创建 RelaxKonOS ACL 系统
- 将 Workspace 等同于宿主 OS Home
- 实现 SaaS 多租户权限模型
- 存储宿主 OS 密码
