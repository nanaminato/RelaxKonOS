# RelaxKonOS 无 sudo 用户模式 Server Goal

> 状态：提案，尚未实现  
> 建立日期：2026-09-16  
> 适用范围：Linux 上没有 sudo 权限的个人账号；首要场景为大学、HPC、实验室和共享 GPU 训练服务器。  
> 前置依据：[部署说明](../../deployment/README.md)、[跨平台特权操作 Goal](../platform/RelaxKonOS.PrivilegedOperations.Goal.md)、[认证模型](../platform/RelaxKonOS.Authentication.md)。

本 Goal 将 Linux Server 明确拆分为两种**互斥的安装模式**：保留现有的系统级 `System Mode`，并新增不修改系统、不请求 sudo 的 `User Mode`。User Mode 不是降级后偷偷尝试提权的安装；它是以当前 Linux 账号为唯一权限边界的正式产品形态。

不要把 `Debug` 当成第三种部署模式。它只影响诊断、热重载等开发体验；**不决定** Server 的运行 UID、PAM transport 或是否可以调用特权 Helper。运行身份与特权能力必须分别配置，因此开发者本地运行的 Server 通常就是 User Mode 的同一执行形态，只是管理员可以额外部署并授权 Helper。

本文只定义 User Mode 的目标、边界和实施顺序，不在本轮修改 Server、安装器或 Client 代码。项目尚未正式发布，涉及的协议、路由和配置在实施时直接升级全部仓库调用方；不保留旧接口或双格式兼容层。

## 1. 目标与非目标

### 1.1 成功标准

一个普通 Linux 账号可以在不使用 `sudo`、不修改 `/etc`、`/opt`、`/var`、system systemd unit、PAM 配置、sudoers、系统用户或防火墙的前提下，完成安装、启动、停止、升级、卸载和日常使用：

```text
student（已有 Linux / SSH 账号）
  └─ RelaxKonOS.Server（同一 UID）
       ├─ 文件、终端、Git、训练任务和日志
       ├─ 当前账号可访问的 GPU / CPU / 内存信息
       └─ 仅绑定 loopback，Client 经 SSH 隧道连接
```

必须同时满足：

- 所有 Server 进程、子进程、文件 I/O 和 Git/终端命令都以安装者的 UID/GID 执行；RelaxKonOS 权限永远不高于该账号的 Linux 权限。
- 默认监听 `127.0.0.1`，不自动暴露校园网/公网端口、不修改防火墙；推荐连接路径为 SSH local forwarding。
- Server 显式返回主机模式与功能能力；Client 在 User Mode 隐藏或禁用不可用功能，而不是调用后再显示泛化的 `Permission denied`。
- User Mode 的安装、启动、停止、状态、升级和卸载均可在没有 user systemd、没有 linger 的 SSH/HPC 环境使用。
- User Mode 直接使用宿主已配置的 PAM service 验证当前账号密码，但绝不写入或改写 PAM 配置；当前系统级安装保持完整功能，且其 root Helper、专用 PAM service、Guardian system service 和 sudoers 规则绝不进入 User Mode。

### 1.2 非目标

- 不绕过学校、Slurm、容器、ACL、配额、cgroup、SELinux/AppArmor 或管理员的网络策略。
- 不把用户加入 `docker` 组，也不把可等价于 root 的 Docker socket 当作普通用户功能。
- 不承诺机器重启、用户退出登录或调度器回收后仍常驻；这需要管理员为该用户启用 linger，或由该环境的调度/会话系统明确保证。
- 不在 User Mode 自动安装、配置或调用 PrivilegedHelper；也不以 passwordless sudo、setuid、capabilities、pkexec 或任意 shell 命令作为替代。
- 不支持系统级 SMB/FTP、UFW/防火墙、Linux 用户管理、系统服务管理、系统环境修改、Web Server/证书宿主部署、受管代理 TUN、系统 Docker daemon、系统级依赖安装或跨用户进程控制。User Mode Guardian 只守护当前 UID 的工作负载，不是例外。

## 2. 当前基线与需要拆开的耦合

现有 `deployment/bootstrap/install-relaxkonos.sh` 会在非 root 时重新以 `sudo` 执行，要求 systemd、`sudo`、`visudo` 和 `openssl`，并固定安装到 `/opt/relaxkonos`、`/var/lib/relaxkonos`。它还要求发布包包含 Server、Guardian 和 PrivilegedHelper。

现有 `deployment/linux/install-relaxkonos-services.sh` 会创建系统账号、`/etc/relaxkonos`、PAM service、sudoers 规则和两个 systemd system unit。虽然 Server 已通过 `User=` 以专用低权限账号运行，但整条发行路径仍是系统安装路径。这是 System Mode 的正确边界，不应被 User Mode 复用。

Linux 登录的默认 `LinuxPamProvider` 目前依赖 root-owned Helper 的固定 PAM 操作；旧的进程内 PAM 入口又被误绑到 `Development` 环境。User Mode 必须直接调用宿主已有的 `login` PAM service，因此认证 transport 要从环境名中拆出。故 User Mode 不能只“跳过安装器中的 sudo”——安装布局、进程生命周期、认证和能力协商都必须独立设计。

## 3. 安装模式合同

| 项目 | User Mode（新增） | System Mode（保留） |
| --- | --- | --- |
| 调用 | `./install-relaxkonos.sh --mode user` | `sudo ./install-relaxkonos.sh --mode system` |
| 安装身份 | 当前登录用户 | root 安装；Server 用专用服务账号 |
| 写入范围 | XDG 用户目录 | `/opt`、`/var/lib`、`/etc` 和 systemd system 路径 |
| 常驻方式 | 内置 launcher；可选 user systemd | systemd system service |
| 特权助手 / sudoers | 不安装、不检测 | 按现有受限 Helper 合同 |
| PAM | 进程内调用已有的 `login` service；不写、不改 PAM | 经受限 root Helper 调用专用 service |
| 网络默认值 | `127.0.0.1` + HTTP；SSH 隧道 | 现有 local / lan / reverse-proxy 选择 |
| 宿主能力 | 当前账号的可访问范围 | 专用服务账号 + 经 Helper 审批的系统操作 |

`--mode` 必须显式指定；不依据 `EUID` 静默切换模式。安装器可在未传入模式时显示说明并拒绝继续，以避免“没有 sudo 时意外得到功能不同的部署”。`--mode user` 收到 root 身份时必须失败；`--mode system` 不以隐式 `sudo` 重新执行，向调用者返回明确的 root 要求。这样脚本永远不会在用户不知情时改变权限模型。

## 4. User Mode 目录、发行包与命令

所有默认目录遵循 XDG；安装器允许以绝对路径覆盖每个根目录，但必须拒绝重叠根目录、符号链接逃逸和 group/world-writable 安装目录。

```text
$XDG_DATA_HOME/relaxkonos/            # 默认为 ~/.local/share/relaxkonos
  server/                              # Server publish 输出，非运行时可写
  runtime/                             # 用户态运行时/受管子工具（如以后需要）
  apps/

$XDG_CONFIG_HOME/relaxkonos/           # 默认为 ~/.config/relaxkonos
  appsettings.user.json                # mode=user、loopback 绑定和用户可配置项
  secrets/                              # 0700 目录；仅当前 UID 可读

$XDG_STATE_HOME/relaxkonos/            # 默认为 ~/.local/state/relaxkonos
  server/relaxkonos.db
  logs/
  run/server.pid + control socket
  install-state.json

$XDG_CACHE_HOME/relaxkonos/            # 默认为 ~/.cache/relaxkonos
  downloads/                            # 可安全清理的已校验发行包暂存
```

发布流水线新增 `user-server` 包：仅含 Server 必需 publish 输出、用户 launcher、用户安装/卸载脚本和 manifest；不得包含或要求 Guardian、PrivilegedHelper、sudoers 模板或系统 service 安装引擎。manifest 必须包含 `packageKind: "user-server"`、RID、版本、文件清单和 SHA-256。现有 `server` 包继续代表 System Mode，不能用文件存在与否猜测模式。

安装器提供下列固定命令，均禁止接收任意命令、shell 片段或特权参数：

```text
relaxkon install --bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256
relaxkon start [--foreground]
relaxkon stop
relaxkon status
relaxkon upgrade --bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256
relaxkon uninstall
```

`start` 使用私有、0600 的锁和控制 socket 防止重复实例；`status` 通过该 socket 和 PID 的 UID/启动时间双重验证，绝不能仅凭可复用 PID 判定存活。`stop` 只终止由当前 UID 启动且与 install-state 匹配的进程。升级先停止、校验完整包、原子切换 `server/current`，启动并请求 `/ready`；失败恢复到上一个已验证版本。卸载只能删除已规范化且由 install-state 记录的 XDG 目录，不触及用户其他文件。

user systemd 可作为**可选增强**：只有 `systemctl --user` 可用且用户明确选择时才写入 `~/.config/systemd/user/relaxkonos-server.service`。安装器必须说明它不创建 linger，也不承诺重启后自动启动；不能因缺少 user systemd 而拒绝 User Mode。

## 5. 认证、运行身份与特权能力

### 5.1 三个正交的配置维度

| 维度 | User Mode | System Mode | 本地开发的典型选择 |
| --- | --- | --- | --- |
| 安装范围 | 当前用户的 XDG 目录 | 系统目录和 system service | XDG/checkout，取决于开发者 |
| Server 运行身份 | 当前 Unix 账号 | 专用低权限 service account | 当前 Unix 账号 |
| PAM transport | 进程内 `login` service | root Helper 的专用 PAM service | 与实际运行身份相同 |
| 特权能力 backend | Disabled | 已安装的 root Helper | Disabled，或由管理员单独配置 Helper |

`ASPNETCORE_ENVIRONMENT=Development` 不得改变表中的任何一行。它不是安全边界，也不是权限开关。

### 5.2 User Mode 直接 PAM 登录

User Mode 采用 `LinuxPamProvider` 的 `in-process` transport，调用宿主**已经存在**的 PAM `login` service 来验证当前账号的密码。密码仅在当前请求的 PAM conversation 内存中存在，验证后继续沿用现有 JWT/refresh-token 会话；不保存 SSH 密码、Linux 密码或 PAM 结果。

进程内 `LinuxPamProvider` 会将 NSS 查询限制为实际 eUID 的身份：登录用户名必须规范化后与该身份一致，`Lookup`/`LookupIdentity` 也只返回该账号。这样，账号 A 启动的 Server 不能通过“验证了账号 B 的密码”建立一个权限仍属于 A 的错误会话。User Mode 不支持别名、多用户映射和 `runAs`。

User Mode installer 写入的最小身份配置如下；`LinuxPamService` 默认 `login`，但允许管理员预先提供的、通过白名单验证的服务名。安装器不创建 `/etc/pam.d/relaxkonos`，不修改 `common-auth`、`common-account` 或任何 PAM 文件。

```json
{
  "Identity": {
    "LinuxPamTransport": "in-process",
    "LinuxPamService": "login"
  },
  "Privileges": {
    "Backend": "disabled"
  }
}
```

首版仍保持 loopback-only。Client 经 SSH tunnel 访问后，输入与 SSH 相同的当前 Linux 账号和密码；SSH 隧道只提供网络通道，不代替 Server 登录。若将来允许 LAN 绑定，必须先另立 Goal，提供 TLS、配对确认、重放防护和明确的威胁模型。

### 5.3 Debug 与 Helper 的边界

开发者在本机以自己的账号运行 Server 时，直接使用上面的 in-process PAM transport；它不是“debug-only compatibility path”。如果该开发者也拥有 root 权限，管理员可按现有封闭操作与 sudoers 合同部署 Helper，令 `Privileges:Backend=helper`。这只增加明确列入 Helper 的特权操作，**不改变** Server 的 UID，也不让 PAM 登录获得 root。

反过来，服务器上的无 sudo 用户安装固定 `Privileges:Backend=disabled`，即使环境名为 Development 也不能启用 Helper。System Mode 继续使用 Helper transport；本 Goal 不以 User Mode 为由放宽其 root Helper、PAM 或授权合同。

## 6. 功能能力、API 与 Client 行为

现有应用包 capabilities 不是主机部署能力，不能复用。新增只读、认证后可见的主机能力契约：

```text
GET /api/v1.0/server/capabilities

ServerCapabilitiesDto =
  mode: "user" | "system",
  executionIdentity: { uid, username, homeDirectory },
  listener: { scope: "loopback" | "lan" | "reverseProxy" },
  authentication: { kind: "currentUnixUser" | "hostAccount", pamTransport: "in-process" | "helper" },
  capabilities: { ...冻结的布尔字段... },
  limitations: [稳定、可本地化的 reason code]
```

所有内置应用在打开前读取并缓存该契约；能力缺失时不注册启动项或展示不可用说明。Server 端仍必须在每个 endpoint 和后台操作处强制能力检查，Client 隐藏从来不是授权机制。协议中新增 `user-mode-not-supported`、`user-mode-loopback-required`、`privileged-feature-unavailable` 等稳定 problem code；不得以异常文本或 Linux errno 作为 UI 合同。

| 功能域 | User Mode V1 | 规则 |
| --- | --- | --- |
| 文件、上传下载、编辑 | 支持 | 仅当前 UID 本来可访问的路径；默认根包括 `$HOME`，额外根由用户显式添加并规范化。 |
| 终端、PTY、Git、代码与日志 | 支持 | 子进程继承当前 UID、受控环境和用户 shell；不允许 `runAs`。 |
| 训练任务、GPU/CPU/RAM、自己的进程 | 支持且过滤 | 只展示/终止当前 UID 的进程；指标读取失败明确降级。 |
| 端口转发 | 支持 | 仅 Client 本机 SSH forwarding；Server 不创建反向隧道或开放防火墙。 |
| Guardian | 支持：用户态 Agent | Agent、Server 与全部子进程同 UID；仅管理此 UID 的工作负载，不控制原生服务或跨用户 `RunAs`。 |
| Docker | 默认不支持 | 即使用户能访问 socket，也要报告 `root-equivalent-docker-access` 并要求未来单独确认。 |
| 防火墙、SMB/FTP、系统服务、用户管理、系统设置、证书/Web server、TUN | 不支持 | 不显示入口、不调用 Helper、不尝试 sudo。 |

安装时应显示该矩阵和当前账号可用的硬件/文件范围摘要；Client 连接到 User Mode 时显示“当前 Linux 用户模式”，不要称为“管理员服务器”。

### 6.1 Guardian 的双路径模型

Guardian 不应因 User Mode 而被整体禁用；它应根据 Agent 实际运行身份选择封闭路径：

```text
User Mode
  student Server ── authenticated local IPC ──> student Guardian Agent
                                                 └─ 仅启动 / 停止 / 重启 student 的进程

System Mode / 经管理员配置的开发机
  Server ── Helper + 一次性授权 ──> privileged Guardian Agent
                                      └─ 可为已批准的其他 Unix 账号创建进程
```

User Mode launcher 生成仅当前 UID 可读的 Guardian IPC secret，并将 Agent 的数据、socket、日志与 workload 定义放在 `$XDG_STATE_HOME/relaxkonos/guardian/`。它以相同 UID 启动 Agent；Agent 必须拒绝 `RunAs` 不等于实际 eUID 的定义，Server 也必须在发送 IPC 前拒绝。这是双重强制，不依赖 Client 隐藏字段。

User Agent 保留 Guardian 已有的结构化启动、stdout/stderr 捕获、退出退避、健康检查、工作负载持久化与 Server 重启后独立存活能力；禁用原生服务读取/控制、受保护 Server service monitor、`runuser`、管理员认证和 Agent 安装/修复入口。`EnabledOnBoot` 仅表示“Agent 下次被启动时恢复工作负载”，不承诺机器开机自动启动 Agent。

跨用户的 Guardian 定义只可在特权 backend 可用时创建：Server 先经闭合的 Helper 操作和一次性管理员授权确认目标账号，特权 Agent 再执行受控 UID/GID 切换。不存在 Helper 时，Server 返回稳定的 `guardian.cross_user_unavailable`；不得降级为让 User Mode Agent 调用 `runuser`、`sudo` 或接收目标密码。

## 7. 安全与数据边界

- 安装、配置、状态和日志目录使用当前 UID/GID，目录至多 `0700`、秘密和数据库至多 `0600`。安装前拒绝所有权不属于当前 UID 的既有路径，避免共享目录劫持。
- Server 的内容根、临时上传、归档解压和子进程工作目录都必须在当前 UID 可控目录中；继续使用现有文件服务的规范化、链接检查、大小限制和审计要求。
- User Mode 在登录请求中只将 Linux 密码交给进程内 PAM conversation，随后立即清零；不持久化或记录 Linux/sudo/SSH 密码、PAM 对话内容、系统服务密钥或 docker socket 凭据。
- `--listen 0.0.0.0`、特权功能开关、Helper 路径、`sudo` 路径和 system service 名称是 User Mode 的无效配置，应在配置绑定时 fail closed。PAM service 名称仅允许受校验的单一服务标识，且必须是安装前已存在的主机配置。
- 任何由 Slurm/cgroup/ACL/配额拒绝的请求都直接返回其领域的普通失败；不得尝试替代身份、改变资源限制或自动重试提权。

## 8. 实施顺序

1. **冻结模式与发行契约。** 在 Protocol 中定义 `ServerMode`、`ServerCapabilitiesDto`、冻结 capability 名称/限制码和 `/api/v1.0/server/capabilities`；在 Server 用一个 mode resolver 统一配置校验。更新 Client 的连接握手和应用目录，删除任何以平台名猜测功能的分支。
2. **拆分用户发行与安装器。** 新建 User Mode manifest、打包目标和 XDG 安装器/launcher；将现有 bootstrap 明确命名为 System Mode。加入 checksum、原子升级、PID/控制 socket、前台运行、故障恢复和安全卸载测试。
3. **完成当前用户 PAM 身份。** 已将 `LinuxPamProvider` 的选择从 `ASPNETCORE_ENVIRONMENT` 移至 `Identity:LinuxPamTransport=helper|in-process`，将进程内 transport 更名为中性实现、验证 PAM service 名称，并限制进程内 PAM 只解析/验证实际 eUID 的账号。仍需在 User Mode 组合根中移除 alias/multi-user、`runAs`、Helper transport 与 HostAdministratorAuthenticator 入口，并补齐无 Helper 登录测试。
4. **按能力收敛 Server。** 为文件、终端、进程、训练、Docker、Guardian、防火墙、安装服务、证书、Web Server、代理与设置端点加 Server 侧 mode guard；User Mode 启动同 UID Guardian 并保留其监督闭环，但移除 Guardian 的跨用户、原生服务和受保护服务监控路径，避免后台服务在启动后才发现无权限。
5. **完成 Client 体验。** 连接后获取能力快照；隐藏不适用应用，显示受限说明和 SSH tunnel 连接建议；把模式/身份/监听范围放入 Settings 的只读连接信息。
6. **发布验证。** 在无 sudo 的 Ubuntu、Rocky/Alma、Slurm 登录节点或等价容器、无 user systemd 的 SSH 环境分别验证安装、前台/后台生命周期、SSH 隧道、当前账号 PAM 登录、训练任务、GPU 可见性、文件 ACL、升级/回滚和卸载。System Mode 的既有 Ubuntu systemd 回归必须独立通过。

## 9. 验收清单

- [ ] 使用没有 sudo 的新建账号，可从已校验本地 bundle 或 HTTPS 发行包安装到 XDG 路径；安装过程不调用 `sudo`、`su`、`pkexec`、`systemctl`（system manager）、`visudo` 或 PAM 修改命令。
- [ ] `find`/审计证明安装前后没有创建或修改 `/etc/relaxkonos`、`/etc/pam.d/relaxkonos`、`/etc/sudoers.d/relaxkonos-helpers`、`/opt/relaxkonos`、`/var/lib/relaxkonos` 或 systemd system unit。
- [ ] Server、PTY、Git 和训练子进程均以安装者 UID 运行；访问其他用户私有目录、终止其他用户进程和管理系统服务均失败且没有提升尝试。
- [ ] User Mode Guardian 与 Server 同 UID，能够恢复、健康检查、重启并记录当前 UID 的已登记训练工作负载；提交其他 `RunAs`、原生服务操作或受保护服务监控均被 Server 和 Agent 双重拒绝。
- [ ] 有效的特权 backend 才能走跨用户 Guardian 路径；缺失时返回 `guardian.cross_user_unavailable`，没有 `sudo`、`runuser`、目标账号密码或其他回退执行。
- [ ] 默认仅 `127.0.0.1` 监听；经 `ssh -L` 可用运行 Server 的同一 Linux 账号完成 PAM 登录并建立正常 Client 会话；同机另一 UID、错误用户名/密码和无隧道的非 loopback 来源均不能登录。
- [ ] `/api/v1.0/server/capabilities` 与 Client 启动项一致；直接调用禁用端点仍得到稳定的能力错误，不会访问 Helper 或执行宿主级命令。
- [ ] 缺少 user systemd 时 `relaxkon start/stop/status` 可用；用户明确选择 user systemd 时才安装 user unit，且文档说明重启持久性取决于管理员提供的 linger/环境策略。
- [ ] User Mode 在 Production 和 Development 都可直接使用 `Identity:LinuxPamTransport=in-process` 登录；环境名不会改变 PAM transport 或特权能力。User Mode 安装不改 PAM，System Mode 仍只经 Helper transport 登录。
- [ ] System Mode 的安装、Helper、PAM、Guardian 和完整能力回归测试仍通过，且不会被 User Mode bundle/配置误启用。

## 10. 需要在实现前确认的产品决定

1. 首版是否仅支持 SSH tunnel，还是为受信任实验室 LAN 另开有 TLS/配对设计的后续目标；本 Goal 默认前者。
2. 训练任务的首版范围：仅管理由 User Mode Guardian 启动的进程，或允许枚举当前 UID 的全部进程；无论选择哪一种，守护、重启与日志只交给同 UID Guardian。
3. Docker 是否永远不在 User Mode 出现，还是以后在明确标为 root-equivalent、由用户二次确认的独立 Goal 中支持。
4. 是否让 `Privileges:Backend` 作为独立的 Server capability 协商字段，并在本地开发的 Helper 已部署时显示其封闭操作清单；此项不能由 Development 环境自动推断。

完成条件：User Mode 成为一条可验证、无隐式权限升级的正式 Linux 发行路径；研究生可以将其当作自己的远程训练工作环境使用，而管理员仍可选择 System Mode 获得完整服务器管理能力。
