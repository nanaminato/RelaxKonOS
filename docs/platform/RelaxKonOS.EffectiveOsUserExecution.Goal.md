# RelaxKonOS 有效 OS 执行身份（Effective OS User Execution）Goal

> 状态：实施中（2026-09-26：把「User Mode 只能以自己的身份执行」补齐到 Windows，Server 侧的 `EnableWindowsUserExecution` 布尔升级为 `PrivilegedHelper:UserExecutionBackend`（`helper` / `local-identity` / `disabled`，默认 `helper`），新增仅限开发机的 `local-identity` 后端（承重守卫＝目标身份必须等于 Server 进程自身 OS 身份；Production 或特权进程启动期拒绝），安装器永不写入该值；User Mode 已验证路径与 Windows 有效用户执行验收结论均未改变。2026-09-25：在 Windows 开发宿主上完成全解决方案构建、`RelaxKonOS.Server.Tests` 全部专项与完整套件、客户端/Framework 测试工程实测；据此修复两处只在 Windows 宿主暴露的问题——身份资格校验误用宿主路径语义、应用部署验证宿主缺少 `IFileService` 注册；Windows 契约检查与用户执行通道关闭态行为已可在 Windows 上执行。2026-09-24：Linux 文件、批处理文件作业、Git、Terminal（含 resize）、Guardian 与部署源文件已接入有效用户执行；Linux 文件操作与事务恢复已收紧到目录/文件描述符；Windows 本地账户文件 impersonation 代码已接入但默认关闭，真实多用户集成验证仍未完成。）
>
> 建立日期：2026-09-22
>
> 适用范围：System Mode 的普通用户文件操作；后续统一 Terminal、Git、PTY、Guardian 和由登录用户拥有的子进程。Linux 与 Windows Server。

## 1. 目标

让已认证的 RelaxKonOS 用户在 **System Mode** 中以其对应宿主 OS 身份执行普通用户操作，而不是以 `RelaxKonOS.Server` 服务账号执行。

例如，`nanami` 登录后：

```text
RelaxKonOS JWT subject → nanami 的稳定 OS 身份
                         ↓
                    有效 OS 执行身份：nanami
                         ↓
    ~/Desktop、Explorer、Terminal、Git、Guardian 子进程
```

因此，访问控制、文件所有者、默认组、ACL/POSIX 权限和子进程归属均由 `nanami` 的宿主账户决定。管理员提权是完全独立的路径：它可在用户明确授权后以 root/LocalSystem 执行受限宿主操作，但绝不能被用作普通用户文件 I/O 的隐式替代。

本 Goal 是设计与实施计划。实施进度和经批准暂缓的验证项记录在本文末尾。

## 2. 当前基线与问题

现有实现已正确解析“属于谁的桌面”：[`FileEndpoints.cs`](../../RelaxKonOS.Server/Endpoints/FileEndpoints.cs) 根据 JWT 的 `sub` 找到 RelaxKonOS 用户，再通过 `IIdentityProvider.GetUserInfo(user.Username).HomeDirectory` 调用 `IFileService.GetSpecialLocations(...)`。`LocalFileService` 也会解析 Linux XDG 用户目录。

但普通文件操作仍由 [`LocalFileService.cs`](../../RelaxKonOS.Server/Files/LocalFileService.cs) 中的 `DirectoryInfo`、`File` 和 `FileStream` 直接完成，实际身份为 Server 进程账号。若 System Mode 服务以 `relaxkon-server` 运行，而用户家目录为 `0700 nanami:nanami`，`nanami` 登录后尝试读取 `/home/nanami/Desktop` 会被 `relaxkon-server` 拒绝；桌面客户端当前可将此类失败收敛为空列表。

当前的 `IPrivilegedFileService` 仅在普通 I/O 触发 `UnauthorizedAccessException` 后、用户完成管理员认证并取得短期 elevation 后，交由 Helper 以高权限操作。它的语义是“管理员操作受保护对象”，不是“以当前登录用户执行”。把前者当作后者会导致用户文件以 root/LocalSystem 创建，也会把正常的 POSIX/NTFS 拒绝错误地变成管理员提示。

| 场景 | 当前实际执行身份 | Goal 后身份 |
| --- | --- | --- |
| System Mode，`nanami` 浏览自己的桌面 | `relaxkon-server` | `nanami` |
| System Mode，`nanami` 创建/修改自己的文件 | `relaxkon-server` | `nanami` |
| System Mode，用户明确取得管理员授权 | root / LocalSystem Helper | root / LocalSystem Helper |
| Linux User Mode | 启动 Server 的当前 Unix 用户 | 不变，直接 I/O |

## 3. 已冻结的原则

1. **登录主体决定普通操作身份。** 每次请求从已验证的 JWT 主体解析稳定 OS 身份；不得从客户端传入用户名、UID、SID、主目录或目标账户。
2. **普通执行与管理员提权分离。** `RunAsAuthenticatedUser` 不消耗 elevation、不显示管理员认证窗口、不以 root/LocalSystem 访问；管理员操作仍须使用既有 capability、`jti`、范围和短期授权模型。
3. **User Mode 不改变。** Linux User Mode 进程已是实际用户，且 [`LinuxPamProvider`](../../RelaxKonOS.Server/Identity/LinuxPamProvider.cs) 已限制进程内 PAM/NSS 只能解析 eUID 对应账户。它不调用 Helper、不支持跨用户 `runAs`。
4. **稳定身份而非展示名。** Linux 使用 NSS 验证的 UID 与 canonical username；Windows 使用 SID 与 canonical account。显示名、Alias、裸用户名、主目录字符串都不能作为授权键。
5. **权限由宿主裁决，提权必须由用户发起。** 目标账户对路径没有权限时，先返回 `elevation-required`，但绝不自动运行 elevated operation 或自动弹出认证；客户端仅在用户明确选择提权后，才以该次操作的精确 scope 请求管理员认证。认证成功后才可走既有 root/LocalSystem capability 重试。不得放宽家目录权限、修改 ACL、递归 `chown` 或回退到 Server 身份。
6. **封闭能力，不提供通用命令执行。** 该能力只承载明确的文件/进程领域操作，不能演变为“以任意用户运行任意命令”。Git arguments 只来自既有 Git domain；Terminal 只接受 allowlisted shell；两者均没有 caller-supplied executable 或 environment。所有路径、文件名、大小、链接/reparse point、操作类型和执行身份仍由 Server 与 Helper 双层验证。
7. **不保留旧接口或双语义。** 按仓库 API 演进政策，实施时同时升级所有内部调用方、测试、文档与协议；不保留旧的“Server 身份普通 I/O”兼容开关、路由或解析回退。

## 4. 目标架构

### 4.1 上层模型

新增一个 Server 内部的、与平台无关的 `EffectiveOsUser` / `IUserExecutionService` 边界。它接收**经服务端解析的主体**和封闭 operation，不接收客户端声明的身份。

```text
Client request
  → JWT / RelaxKonOS User
  → Canonical OS identity resolver (UID + username，或 SID + account)
  → User execution service
       ├─ User Mode: direct LocalFileService / child process (same OS identity)
       └─ System Mode: authenticated local Helper → RunAsAuthenticatedUser(identity, operation)
  → OS permission check as that identity

Explicit administrator elevation
  → existing elevation capability + short-lived jti scope
  → privileged Helper operation as root / LocalSystem
```

文件 API 不应自行保存或猜测 `EffectiveOsUser`。端点/领域服务在认证边界解析一次请求主体，向执行服务传递不可由 HTTP 覆盖的内部 `UserExecutionContext`。后续 Terminal、Git、Guardian 和应用部署器复用同一解析器与执行服务，但不得在本 Goal 首阶段借此接收任意 executable、arguments 或 environment。

### 4.2 Linux System Mode

`RelaxKonOS.Server` 保持低权限服务账号。已安装的 root-owned one-shot Helper 对每个普通用户操作：

1. 验证结构化请求、协议版本、允许的 operation、规范化路径和链接边界。
2. 使用 NSS 重新解析 canonical username，确认它仍映射到请求中的 UID、primary GID 与 home；拒绝 UID 0、系统/保留账户和身份漂移。
3. 在专用子进程中按正确顺序设置 supplementary groups、GID、UID（例如 `initgroups`/`setgroups`、`setgid`、`setuid`），并确认不能恢复特权。
4. 仅在已降权子进程中执行对应文件或进程领域操作；把结构化结果回传给 Server。

因为身份切换是一次性、不可逆的，运行时不应在长期 root Helper 进程或 ASP.NET 请求线程中改变身份。实现前必须决定是每项请求启动隔离 worker，还是 LocalSystem/root Helper 派生受监控的短生命周期 worker；两者都不得让已降权代码返回到特权 dispatcher。

### 4.3 Windows System Mode

`RelaxKonOS.Server` 保持普通服务账户；已安装的 LocalSystem `RelaxKonOS.PrivilegedHelper` Windows Service 通过现有受 ACL 保护且双向认证的本机命名管道承载结构化请求。

Helper 重新解析/验证目标 SID，并通过 Windows access token 执行受限操作（例如 `LogonUserW` 获得临时 token，`WindowsIdentity.RunImpersonated` 在受限委托中调用文件 API）。token、密码和任何可重放凭据不得持久化、缓存或进入日志；操作结束即释放 token。Windows 具体 logon type、域账户支持、profile 加载、网络访问和 Session 0 行为必须经真实 Windows Server 验证后冻结，不能根据 Linux 路径类比假设。

Windows Helper 以 LocalSystem 运行不代表普通操作拥有管理员语义：线程/操作的有效 token 必须是目标用户 token；否则返回普通访问拒绝。不得把 LocalSystem 打开的句柄在 impersonation 边界外泄给 Server。

## 5. 新旧 Helper 语义

现有 `PrivilegedOperationKind.File*` 与 `IPrivilegedFileService` 表示 elevation 后的 root/LocalSystem 文件操作。实施时应以新协议直接表达两类互斥语义，而不是在现有请求中增加可选 `RunAsUser` 字段：

| 执行通道 | 调用前提 | 有效 OS 身份 | 失败含义 |
| --- | --- | --- | --- |
| User execution | 已认证当前用户；无 elevation | 当前主体对应的 UID/SID | 该用户没有权限或身份不可用 |
| Elevated operation | 已认证当前用户 + 对应 capability 的有效 `jti` elevation | root / LocalSystem | 管理员范围不足或 Helper policy 拒绝 |

建议新增专用的版本化 User Execution contract（身份引用、operation kind、路径/内容/目标、operation ID、结果与稳定 problem code），并移除普通文件端点对“先用 Server 身份尝试、失败后才决定谁执行”的依赖。协议不包含密码、token、shell、任意环境变量、UID/SID 自由文本或可由调用者扩大的根目录。

是否将全部 `File*` elevation operations 迁移到独立 `ElevatedFile*` 枚举，或将 User Execution contract 放在新的 `Protocol/UserExecution/` 命名空间，需在 Phase 0 一次性定稿；不得长期保留“同一 kind + 可选身份字段”的双格式解析。

## 6. 分阶段计划

### Goal 0：基线审计、信任边界与契约冻结

**工作：** 清点所有会读取、写入、创建、删除、重命名、复制、上传、下载或启动用户拥有资源的路径：Explorer、Desktop、文本编辑、媒体、Terminal/PTY、Git、Guardian、应用部署器和后台任务。为每条标记 User Mode direct、System Mode user execution、显式 elevation 或不支持。冻结 Linux UID/GID/groups 解析策略、可 impersonate 的 Windows 帐户范围、系统账户拒绝规则、身份漂移处理、Windows 域范围、操作超时/内容上限、审计字段和 stable problem-code 表。

**验收：** 没有未分类的 Server-identity 用户资源操作；威胁模型覆盖伪造 JWT 主体、Server/Helper IPC 伪造、身份重用/改名、UID/SID 漂移、符号链接/reparse point、TOCTOU、跨文件系统移动、并发、Helper 崩溃、token 泄露、Windows token 句柄泄露与 Linux 特权恢复。

### Goal 1：身份解析与纯领域接口

**工作：** 在 Server 建立单一 `UserExecutionContext` resolver，从已认证 `User` 和 `IIdentityProvider` 取得 canonical UID/SID 身份；为失效、改名、身份不匹配、不可执行与平台不支持定义稳定错误。建立内部 `IUserExecutionService` 与无副作用 fake，实现 User Mode direct adapter。设计新的 Protocol contract、JSON/帧大小限制及测试，但此阶段不接入真实 Helper。

**验收：** 任何 HTTP body/query/header 都不能选择 execution identity；相同 UID/SID 的别名/等价账号汇聚到同一 context；User Mode 只能生成当前 eUID context；身份查询不可用时 fail closed；新协议不含密码、JWT、shell 或通用命令字段。

### Goal 2：Linux 降权执行 Helper

**工作：** 实现 root-owned Helper 的封闭 user-execution dispatcher、NSS 二次校验、groups/GID/UID 降权 worker、文件 operation adapters、结构化错误和审计。维护现有 elevated 文件操作的独立语义；所有调用保留 operation ID、内容和路径限制。

**验收：** `relaxkon-server` 无权读取的 `nanami:0700` 家目录可由 `nanami` 会话按 `nanami` 权限访问；新建/上传/复制文件的 UID/GID 是目标账户；`nanami` 不能读取 `alice` 私有目录、以 root 运行、绕过 symlink 边界或获得管理员 elevation；Helper/worker 失败不会留下 root-owned 用户文件。

### Goal 3：Windows impersonation Helper

**工作：** 在 LocalSystem Helper 服务实现 SID 二次验证、短暂 token 获取、受限 impersonation scope、文件 operation adapters、token lifetime 管理、审计和稳定错误。只先支持在隔离 Windows Server 上实测的本地账户；域账户须单独达到可验证的 SID、LogonUser、profile/ACL 和恢复标准后才扩展。

**验收：** 两个并发本地 Windows 用户请求按各自 NTFS ACL 独立成功/失败；创建文件继承目标账户上下文而非 LocalSystem；token 在异常、超时、取消与 Helper 重启后释放；普通账户无法借命名管道请求其他 SID 或管理员身份。

### Goal 4：文件 API 切换与客户端错误体验

**工作：** 将 Explorer、Desktop、下载、上传、编辑、创建、删除、重命名、移动和复制统一切到 User Execution。普通用户访问拒绝显示正常拒绝状态；只有专门的宿主管理员操作才请求 elevation。删除现有普通 I/O 的 Server-account fallback 语义，更新内置应用与三语本地化。

**验收：** 登录用户的 Desktop/Home 不因 Server 服务账号而空白；同一 API 在 User Mode 与 System Mode 产生同样的“用户自身权限”结果；管理员认证窗口不会因用户自己的 `0700` 家目录出现；客户端不显示 Helper stderr、UID/SID、token 或平台内部细节。

### Goal 5：扩展到用户拥有的进程能力

**工作：** 逐域迁移 Terminal/PTY、Git、Guardian 和应用工作负载到同一 context，并分别定义命令 allowlist/启动策略、工作目录、受控环境、生命周期、输出和取消语义。不得把文件执行服务泛化成任意 `runAs` RPC。

**验收：** 同一用户通过文件、Terminal、Git 和 Guardian 创建的资源有一致的 owner/ACL/UID/SID；跨用户请求严格拒绝；管理员操作仍只走专门的 elevated capability；每个领域的执行面都有单独的安全测试与文档。

### Goal 6：平台集成、运维与发布

**工作：** 在隔离 Linux 与 Windows Server 环境完成服务账号、两个普通用户、受保护家目录、ACL、用户改名/删除、Helper 不可用、身份 provider 不可用、重启、并发、取消和升级演练。更新安装器、Helper 配置、管理员手册、架构/认证/权限模型文档和发行说明。

**验收：** Server 始终不以 root/LocalSystem/Administrator 运行；User Mode 不依赖 Helper；System Mode 不要求放宽用户家目录权限；升级不会保留旧协议、旧调用路径或未审计的 Server-identity 普通操作。

## 7. 测试要求

- **单元与协议：** identity context 解析、UID/SID canonicalization、用户名改名/UID reuse、操作 contract 序列化、大小限制、路径和链接检查、problem-code 映射、无秘密日志断言。
- **授权：** JWT 主体与请求字段不匹配、同 username 不同 UID/SID、过期/无效身份、User Mode 非 eUID、无 elevation 的普通路径、elevation 与 user-execution 通道隔离。
- **Linux 集成：** 服务账号 + `nanami`/`alice` 私有家目录与 supplementary groups；创建 owner、读写/移动/复制、NSS 不可用、Helper 缺失、worker 失败和 root-owned 残留检查。
- **Windows 集成：** LocalSystem Helper + 普通 Server 服务账号 + 至少两个本地账户；NTFS deny/allow、并发 impersonation、token 释放、pipe ACL、服务重启。域账户测试未完成前，必须显式 `unsupported`。
- **回归：** 现有 User Mode 直接 I/O、管理员文件 elevation、DesktopShell、File endpoints、Terminal/Git/Guardian（完成迁移后）和完整 `dotnet build RelaxKonOS.sln -c Debug`。

## 8. 完成标准

- [ ] System Mode 中，登录用户对自己的受保护家目录执行文件操作时，结果与该 OS 用户直接执行相同。
- [ ] 创建、上传、复制和解压产生的所有用户资源归属正确的 UID/GID 或 Windows 安全上下文。
- [ ] 普通用户操作不显示管理员认证，也不以 root/LocalSystem 创建文件。
- [ ] 不允许通过 HTTP、IPC、用户名、Alias、路径、UID/SID 或重放请求冒充另一用户。
- [ ] User Mode 仍只允许 Server 进程所属 Unix 用户登录，并完全不依赖跨用户 Helper。
- [ ] Windows 与 Linux 均只在已经真实验证的平台范围内启用；未知平台/域能力 fail closed。
- [ ] 所有已纳入范围的文件与进程领域使用同一 execution-context 解析器；没有 Server-account fallback 或旧 protocol shim。
- [ ] 审计可区分 user execution 与 administrator elevation，但不记录完整敏感路径、密码、JWT、token 或 token handle。

## 9. 实施前仍需决策

1. Linux System Mode 首版支持的账户来源：仅本地 NSS 账户，还是也承诺 LDAP/SSSD；后者必须在身份、supplementary groups 和 UID reuse 场景实测。
2. Windows 首版范围是否明确限制为本地账户；域用户应在何种 token/logon 与 offline 语义验证后启用。
3. 已决：User execution 维持 Explorer 的全路径能力，和 Ubuntu 桌面一致；不以 canonical home 或应用 allowlist 限制普通浏览。降权完成后由 OS 权限作最终裁决。
4. Linux worker 模型及其资源限制：one-shot Helper 内 fork、独立 worker binary，或受控 service worker；需要在 Goal 0 根据 .NET/POSIX 互操作风险、取消与审计要求确定。
5. 哪些后台任务在文件迁移完成前必须 fail closed，而不是继续以 Server 服务账号运行；Terminal、Git、Guardian 和应用部署不应被无意地遗留为不同身份模型。

这些决策在 Phase 0 定稿前，不应开始改变现有文件端点或 Helper 协议。

## 10. 实施记录

### 2026-09-22：Goal 0 决策冻结

首版边界已冻结如下，后续实现不得通过兼容开关放宽：

- Linux System Mode 只接受本地 NSS 可重新解析的非 root、UID ≥ 1000 账户；解析时必须同时验证 canonical username、UID 和绝对 home directory。LDAP/SSSD、UID 小于 1000 的服务账户及身份漂移均 fail closed。
- 初始文件范围与 Ubuntu 桌面一致：允许浏览任意绝对路径，不设 RelaxKonOS 自己的 home-only allowlist。降权完成后由内核以目标 UID、supplementary groups、ACL 和目录 traversal permission 裁决；因此用户可浏览如 `/etc` 中实际可读的内容、使用有权限的组共享目录或项目目录，但不能读 `/root`、其他用户私有目录或无权写入的系统位置。root dispatcher 在降权前不打开、解析或验证调用者路径。
- Windows 首版不启用 user execution；本地账户 SID impersonation 在真实 Windows Server 验证完成前返回 `unsupported-platform`/`helper-unavailable`，域账户明确不支持。
- Linux worker 使用独立的一次性降权执行单元；root dispatcher 不执行用户 I/O，也不允许降权代码回到特权 dispatcher。
- Explorer/Desktop 文件 API 是首个迁移域。Terminal/PTY、Git、Guardian、应用部署和后台文件任务在迁移前不得宣称已具备跨用户执行能力。

### 已完成：Goal 1 与 Linux 文件 API 的首个垂直切片

- 新增独立 `Protocol/UserExecution/` 版本化 contract：包含封闭的文件、Git 与 Terminal operation kind 和 Server 派生的 stable identity；没有密码、JWT、token、任意 executable 或 environment 字段。Git arguments 及 Terminal shell 仅是 Server domain 构造、Helper 再验证的受限字段，不能由 HTTP 直接扩展为通用执行。
- 新增 `UserExecutionContextResolver`：仅从验证后的 JWT `sub` 找到 RelaxKonOS 用户，再经 `CanonicalUserResolver` 对 username 与 UID/SID 重新绑定验证；HTTP body/query/header 不参与身份选择。
- 新增 `IUserExecutionService` 的 User Mode 验证 adapter。它不会切换 Server 身份。
- Linux one-shot Helper 新增独立 `--user-execution` 入口：以 root 重新解析 UID、canonical username 与 home，拒绝 UID < 1000 及 nobody UID 65534；在 `initgroups` 后以 `setresgid`/`setresuid` 同时替换 real/effective/saved IDs，复核六个 ID，主动确认不能回到 UID 0，并设置 `PR_SET_NO_NEW_PRIVS`。只有这些检查全部成功才执行一项封闭操作；降权后没有特权 dispatcher 可返回。
- `IFileService` 已替换为有效用户路由：User Mode 仍调用本地服务；Linux System Mode 通过独立 transport 调用 Helper。目标用户的权限拒绝首先返回 `elevation-required`；只有客户端在用户明确确认并完成管理员认证后才取得短期 capability，并通过既有 root/LocalSystem Helper 重试对应的受控操作；绝不静默提权或自动认证。
- 特殊位置、目录枚举、读写、上传、删除、重命名、移动、复制、创建目录、属性和 POSIX mode 均覆盖这一文件通道。System Mode batch file jobs 也会在 HTTP 请求存活时冻结有效用户 context；每一次后台枚举、冲突检查、复制、移动或删除均通过同一 one-shot Helper 完成，绝不在 Server 服务账号下打开用户路径。既有冲突决定、取消和 elevated retry 语义保持；目录合并移动按用户身份逐项执行。
- Git 的 Linux 通道已迁移为专用 `GitExecute` operation：Helper 只使用固定位置的 Git binary，并在降权后以当前用户身份运行服务端 Git domain 生成的 arguments 与工作目录。System Mode Helper 与 User Mode 直连通道共用同一子命令 allowlist 和进程环境：拒绝全局 `-c`、写配置、external diff/textconv、自定义 upload/receive-pack 和 `ext::` remote；执行时固定禁用 repository hooks，默认拒绝未知 transport，只显式允许 file/git/http/https/ssh。没有 generic executable、shell 或环境字段。需要临时 AskPass 凭据的远程操作暂时 fail closed，直到凭据可在不进入 User Execution wire contract 的前提下安全注入。
- Terminal 的 Linux System Mode 已迁移到专用 `--user-terminal` Helper 入口：它读取一次结构化、allowlisted shell 请求，重新验证身份并降权，然后才创建真正 PTY，通过独立的输入/resize/close 帧桥接到现有 SignalR 会话。当前只支持 bash/sh；尺寸边界由 Server 和 Helper 双层验证，resize 已传递到用户 PTY。
- User Execution Helper 现在对首请求强制帧大小上限，并按 operation 拒绝所有无关字段；Terminal 的 JSON 首帧不再经可预读的文本 reader，避免吞掉后续二进制控制帧。协议版本已直接升级为 `1.1`，不接受旧格式回退。
- Helper 读文件时会在分配完整内容前检查并流式执行上限；Git stdout/stderr 也共享有界预算，避免大文件或巨量 Git 输出在降权 Helper 内无界占用内存。
- Linux one-shot transport 已将 HTTP/job 取消与有界超时传递到 Helper 进程生命周期，超时返回稳定 `TimedOut` 分类；完成审计只记录 operation ID、operation、身份哈希、资源哈希和结果，不记录完整路径。
- Linux 生产与开发安装器的 sudoers 已从“只写 apphost 路径”收紧为三个精确命令形状：无参数 privileged protocol、`--user-execution` 与 `--user-terminal`。不允许通配参数、额外 option 或自定义入口；开发安装还会对允许/拒绝形状做 smoke check。
- Linux System Mode 引导安装器现在会校验发布包的完整逐文件 SHA-256 inventory，拒绝未列入清单的文件、符号链接与特殊文件；Server、Guardian 和 Helper 先组成一个完整 `runtime` staging 快照，再以目录 rename 替换旧快照。固定 sudo Helper 发布目录与开发 Helper 也使用 staging 替换，升级不会保留新版已删除的旧 DLL；新服务健康后会删除旧布局的组件目录。
- Helper 的用户文件复制现在先在目标同目录创建隐藏 staging，内容完整后才以 rename 提交最终名称；覆盖时先保留旧目标，提交或旧目标清理失败都尝试恢复。目录内的符号链接会复制链接本身、绝不递归进入链接目标。跨 filesystem 移动在 `EXDEV` 后改用同样的 staged copy，只在目标提交后删除源。
- Helper 的普通写入与上传也已改用同目录事务 staging，不再直接截断最终文件。权限为 `0700` 的事务目录和原子切换的清单在写入内容前落盘，目录名与清单共同记录创建进程 PID/启动时间和最终目标；同目录的后续操作或目录枚举会在目标用户身份下回收已退出进程的事务，包括清单尚未完成的初始化窗口。提交前中断则恢复旧目标，提交后中断则保留新目标并清除旧备份，存活中的并发 Helper 事务不会被误删。替换已有普通文件时保留其 Unix mode；伪造、损坏或包含非清单内容的相似隐藏目录不会被自动删除。
- 文件事务的目标侧已开始采用 openat 风格加固：Helper 在事务开始时打开父目录描述符，并以 `mkdirat`/`openat(O_NOFOLLOW)` 创建及锁定事务目录；旧目标备份、staging 提交与事务目录删除分别使用 `renameat`/`unlinkat` 相对该描述符完成。执行中即使父目录被整体改名、原字符串路径被同名替代目录占用，提交仍只落到最初打开的目录，不会重新解析并写入替代路径。专项测试会在 staging 中用 `SIGSTOP` 暂停真实子进程、替换父路径后再恢复，验证替代目录未被修改。
- Linux 文件源侧也已收紧到 descriptor-relative 执行：递归复制只在已打开的源目录上使用 `statx`/`openat(O_NOFOLLOW)` 遍历，符号链接用 `readlinkat` 复制链接本身；删除、同目录重命名和直接移动分别使用 `unlinkat`/`renameat2(RENAME_NOREPLACE)`/`renameat`。目录枚举先打开目录再生成快照；跨文件系统移动只在 device/inode 仍与已复制源对象一致时删除源。真实子进程测试会用 FIFO 阻塞源遍历、替换源父路径后恢复，确认复制仍来自原已打开目录；递归删除测试确认不跟随树内符号链接。
- 单路径读取、metadata/特殊位置查询、递归创建目录与 POSIX mode 修改也已转为 descriptor-relative 实现；读取在打开后即使父路径被替换仍返回原 inode，递归创建保留用户已有中间符号链接的桌面语义，mode 修改通过 `O_PATH` 文件描述符锁定最终对象。
- 中断事务的发现、清单读取、备份恢复和事务目录删除现在共用同一已打开父目录。清单格式直接升级为 v2，只记录目标 basename 而不记录完整路径；不解析 v1 旧清单。专项测试验证事务父目录被改名后仍可在新路径安全恢复，且旧格式不会被接受或当作可删除事务。
- Guardian workload 创建入口现在从 JWT `sub` 解析 canonical OS account；未声明 `runAs` 时默认当前用户，跨账户仍需要单独管理员批准。Server 会把 canonical launch account 与稳定 Linux UID/Windows SID 写入定义；Agent 在接收定义及每次启动前重新解析该账号并比对 UID/SID。旧定义缺少稳定身份时 fail closed，必须重新保存；Linux 继续使用 `runuser` 完成 child UID/GID/groups transition。
- 应用部署的本地文件引用现在通过 `IFileService` 以当前登录用户读取，并立刻复制到 deployment-owned staging；后续 Docker build 只使用 staging 副本，不会在后台以 Server 服务账号重新读取用户项目文件。
- 媒体播放 lease 在创建时冻结 Server 派生的有效 OS identity 与文件修改时间；后续 bearer lease URL 没有 JWT 时，仍通过该 identity 的 user-execution 通道读取，而不是因为缺少 HTTP 主体回退到 Server 服务账号。
- Windows System Mode 已增加与管理员操作分离的 `<privileged-pipe>-user` 本机管道。LocalSystem Helper 会重新把 SID 解析为规范 `MACHINE\\user`、拒绝域/系统账户并复核 Profile 路径，然后通过 MSV1_0 S4U 创建不含密码的一次性 network logon token；只有 SID 匹配、impersonation level 精确为 Impersonation 且管理员组未在 token 中启用时才接受。token 仅在同步 `WindowsIdentity.RunImpersonated` 文件操作作用域内存在，操作后立即释放，不进入协议、缓存或日志。文件列表、元数据、特殊位置、读写、上传、创建、删除、重命名、移动和 staged copy 已接入；Git、Terminal 与 POSIX mode 明确返回 unsupported。
- Windows user execution 仍由 Helper 侧的能力门与 Server 侧的执行后端共同 fail closed：Helper 侧保留布尔 `enableWindowsUserExecution`（默认 `false`），Server 侧由 `PrivilegedHelper:UserExecutionBackend`（`helper` / `local-identity` / `disabled`，默认 `helper`）选择执行者，安装器只在显式传入 `-EnableWindowsUserExecution` 时两侧同开，否则写入 `disabled`（见 2026-09-26 条）。这不是兼容开关，而是尚未通过目标平台验收前的能力门；通过真实 Windows Server 验证后应直接移除门控并更新安装器，不保留双行为。
- 添加 contract/context 单元检查，覆盖 canonical identity、请求身份替换拒绝、System Mode fail-closed 以及无敏感/通用命令字段。

### 2026-09-25：Windows 宿主实测与随之修复的两处问题

本轮把此前只在 Linux 上跑过的验证搬到 Windows 宿主（win32 + .NET SDK 10.0.400），逐项执行构建、专项与完整套件。实测暴露两个只在 Windows 宿主出现的问题，均已修复：

- **身份资格校验误用宿主路径语义。** `UserExecutionContextResolver` 用 `Path.IsPathFullyQualified` 判断家目录是否为绝对路径，而该 API 按**宿主**规则判定：Windows 上 `"/home/nanami"` 被判为非完全限定，Linux 身份因此在 Windows 宿主上被误拒为 `IdentityNotExecutable`，使 `--user-execution-only` 在 Windows 上直接抛异常。改为 `UserExecutionProtocol.IsEligibleHomeDirectory(platform, home)`，按**身份所属平台**的规则校验（POSIX 家目录以 `/` 开头、Windows 侧才用 Win32 完全限定判定）。资格判定不再取决于 Server 恰好运行在哪个宿主。同时把 `User Mode` 的 `geteuid()` 调用包进 `IsServerEffectiveUnixUser`：Server 的有效 Unix 用户只在 Linux 上存在，非 Linux 宿主对 Linux 身份直接拒绝，不再触碰不存在的 libc。
- **应用部署验证宿主缺少 `IFileService` 注册。** 文件引用路由在本轮改为经 `IFileService` 以登录用户读取源文件，`ApplicationDeploymentProgressVerification` 映射了生产路由却未注册该服务，导致 ASP.NET 端点参数推断失败、进程在 `app.Build()` 阶段崩溃（完整套件因此在应用部署验证处终止）。宿主现注册显式的 `SourceReferenceFileService` 替身（只暴露一条可读路径，绝不回退到 Server 服务账号），并新增两项真实 HTTP 断言：可读源文件必须被**复制**进 deployment-owned staging（而不是登记路径），登录用户读不到的源文件必须返回 404 `file-reference-unavailable`。该失败与宿主平台无关，只是此前 Linux 完整套件停在更早的代理 GEO 落盘步骤、从未走到这里。

同时补上此前缺失的两项 Windows 侧检查：

- `DisabledUserExecutionTransport` 与真实请求作用域的 `UserExecutionFileService` 组合下的 fail-closed 行为：列举、特殊位置、metadata、读取、创建目录、删除六类操作全部必须失败，不得有任何一次以 Server 服务账号被服务；这正是 Windows 当前发布默认路径（能力门关闭）。
- Windows 用户执行管道本身的关闭态：Helper 未监听、机器密钥不可用、管道名未配置三种情况下，`WindowsNamedPipeUserExecutionTransport` 必须返回关闭态失败且不得转向管理员管道。

### 尚未实施（明确不跳过）

- Windows LocalSystem named-pipe/SID impersonation 的代码路径已完成首个文件垂直切片，但尚未在真实 Windows Server 验证 S4U logon type、NTFS deny/allow、UAC/管理员本地账户语义、token 释放、并发、取消、服务重启、profile/known-folder 与网络/映射盘行为；验收前保持默认关闭，不能标记 Goal 3/4 完成。（2026-09-25 补充：Windows 开发宿主上的构建、身份契约与用户执行通道关闭态已实测通过，见文末 Windows 宿主验证；上述多用户项仍未验证。）
- Git 的临时 AskPass 远程凭据路径；Windows terminal impersonation。
- 应用部署的完整用户工作负载 owner model：当前只保证从文件选择器导入源 archive 时按登录用户读取、随后由 deployment-owned staging 使用；Docker Engine 容器本身仍是宿主级资源。
- Linux user-execution 的文件代码路径已完成本轮 descriptor-relative 收尾；仍需安装为 root-owned Helper 后的真实多用户残留演练。代码已用真实子进程 SIGKILL 验证 staging 恢复与清单初始化窗口回收，并用源/目标/恢复父路径替换验证 descriptor anchoring；隔离环境仍须验证 root Helper、`relaxkon-server`/`nanami`/`alice` 三账户、共享目录、supplementary groups 与 owner/group 结果。
- 安装器、Helper 配置和真实 Linux/Windows integration 环境。

### 本次验证与暂缓项

| 项目 | 状态 | 说明 |
| --- | --- | --- |
| `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Debug --no-restore` | 通过 | 新增协议和 Server 代码可编译；仅有既存的 Linux/Windows 平台分析警告。 |
| `dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj -c Debug --no-restore` | 通过 | Linux 降权 dispatcher 与独立 Helper 入口可编译。 |
| `dotnet build RelaxKonOS.Guardian.Agent/RelaxKonOS.Guardian.Agent.csproj -c Debug --no-restore` | 通过 | Guardian stable UID/SID binding 与 Agent-side revalidation 可编译。 |
| System Mode batch file jobs | 已完成代码迁移，集成暂缓 | Job 在入队时冻结 execution context；后台每一次路径读取/修改都经 User Execution Helper。真实多用户、冲突决定、取消与跨文件系统演练仍需要隔离 Linux 环境。 |
| `dotnet build Framework/RelaxKonOS.Core/RelaxKonOS.Core.csproj -c Debug --no-restore` | 通过 | 共享 framework 回归构建。 |
| `RelaxKonOS.Server.Tests` | 专项通过 | 普通 project-reference restore graph 仍在当前桌面 SDK 中无诊断失败；使用项目既有的预构建 Server/Core assembly 路径后，测试工程可从源码重建，`--user-execution-only` 通过，包含 identity contract 与 reserved UID、Windows 独立管道/默认关闭能力门、严格 request-shape 拒绝、Git allowlist 及危险选项拒绝、Terminal 输入/resize/close 帧往返、Helper 取消/超时、复制与写入原子覆盖、Unix mode 保留、提交前/后中断恢复、真实子进程 SIGKILL、`0700` staging、v2 清单与未完成清单回收、旧清单 fail closed、并发事务保护、伪造 staging 拒绝、源/目标/恢复父路径替换时的 descriptor anchoring、打开后读取路径替换、metadata/递归创建/POSIX mode、递归删除不跟随符号链接、重命名 no-replace，以及工作区到 `/tmp` 不同设备号之间的 staged move。同时 `--file-operations-only` 35 项和 `--git-conflicts-only` 47 项全部通过。 |
| `dotnet build RelaxKonOS.sln -c Debug --no-restore -m:1 -p:MSBuildEnableWorkloadResolver=false` | 通过 | Server、Helper、Guardian Agent、Client/Desktop 及 Framework 全部编译成功；最新整体构建为 0 warning / 0 error。 |
| Linux installer / sudoers | 通过 | 四个受影响的安装/卸载脚本均通过 `bash -n`；三个精确命令形状组成的 sudoers 条目通过 `visudo -cf -`。发布 inventory 的缺失、篡改与多余文件拒绝以及 runtime 快照清理使用临时目录 smoke test 验证。 |
| Linux System Mode 集成 | 暂缓 | 需要安装 root-owned Helper 与 sudoers 规则，以及 `relaxkon-server`、`nanami`、`alice` 三账户隔离环境；当前工作区不应修改宿主账户或 sudoers。 |
| Windows impersonation 代码 | 首个文件切片完成、默认关闭 | 独立认证管道、本地 SID/account/profile 二次验证、一次性 MSV1_0 S4U token、同步 impersonation 文件操作和 replay/大小限制已接入；域账户、Git、Terminal 与 POSIX mode fail closed。 |
| Windows impersonation 集成 | 暂缓 | 安装器省略 `-EnableWindowsUserExecution` 时写入 `UserExecutionBackend=disabled`（Helper 侧能力门同时为 `false`）；需真实 Windows Server + LocalSystem Helper + 两个普通本地账户完成 NTFS ACL、owner、token、并发、取消与重启验收后才可启用。 |

### 2026-09-25 Windows 宿主验证

宿主：Windows（win32）、.NET SDK 10.0.400、Debug 配置。

| 项目 | 状态 | 说明 |
| --- | --- | --- |
| `dotnet build RelaxKonOS.sln -c Debug -m:1 -p:MSBuildEnableWorkloadResolver=false` | 通过 | 0 错误；3 个既存告警（`Program.cs` 两处 CA1416 平台注册、`DockerImageMirrorsView.axaml` 的 AVLN3001），均与本 Goal 无关。 |
| `RelaxKonOS.Server.Tests`（普通 project-reference 还原图） | 通过 | Windows 上可直接按项目引用构建（0 警告 0 错误），无需 `-p:UsePrebuiltServerAssembly=true`。 |
| `RelaxKonOS.Server.Tests --user-execution-only` | 通过 | 身份契约、System Mode fail-closed、请求形状拒绝、Git allowlist、Terminal 帧、helper 取消/超时之外，本机新增执行：真实 `UserExecutionFileService` + `DisabledUserExecutionTransport` 的六类操作 fail-closed，以及 Windows 用户执行管道在 Helper 缺失/密钥不可用/管道名未配置下的关闭态。 |
| `RelaxKonOS.Server.Tests --deployment-progress-only` | 通过 | 含新增的文件引用路由断言：可读源文件被复制进 staging、不可读源文件返回 404。 |
| `--file-operations-only`（34 项）/ `--git-conflicts-only`（47 项）/ `--settings-only` / `--alias-only`（51 项）/ `--file-services-only` / `--helper-allowlist-only` / `--proxy-geodata-only` / `--proxy-tun-only` | 通过 | 全部通过。`--alias-only` 附带 Windows 本机账户只读资格判定：可用、本地账户合规，未改动任何 OS 账户。文件作业 34 项（少于 Linux 的 35 项，差异是本机跳过的符号链接用例，属既有平台分支）。 |
| `RelaxKonOS.Server.Tests`（无参数完整套件） | 通过 | 输出 `RelaxKonOS.Server backend verification passed.`；Linux 上曾停在代理 GEO 落盘步骤，Windows 上该步骤及之后的应用部署、WebServer、Docker、代理隧道检查全部通过。 |
| `Client/RelaxKonOS.Settings.Tests` / `Client/RelaxKonOS.Installation.Tests` / `Client/RelaxKonOS.Explorer.Tests` | 通过 | Explorer 回归 137 项，含本分支新增的「重命名提交进行中」两项断言。 |
| `Framework/RelaxKonOS.Core.Tests` | 环境受限 | 失败于 `Directory.CreateSymbolicLink`：本机未开启开发者模式/未提升权限，无符号链接创建特权。与本 Goal 无关（该工程未被本分支修改）。 |
| `Client/RelaxKonOS.FileServices.Tests` | 无法构建 | 既存破损：该工程仅以 `Compile Include` 链接 `FileServicesViewModel.cs`，而该 VM 依赖未链接的 `LocalizedStatus`/`LocalizedObservableObject`/`RelaxKonOS.Client.Services`/`InstallationTaskViewModel`；master 上同样如此，与本 Goal 无关。 |
| `deployment/windows/Install-RelaxKonOSServices.ps1` | 通过 | `Parser::ParseFile` 语法检查通过；安装器仅在显式传入 `-EnableWindowsUserExecution` 时才会同时在 Server 段写入 `UserExecutionBackend=helper` 并在 Helper 段写入 `enableWindowsUserExecution=true`；省略该参数时 Server 段写 `disabled`、Helper 段保持关闭，且任何情况下都不会写入 `local-identity`。 |
| Windows impersonation 真实多用户集成 | 仍未验证 | 本轮只在 Windows 开发宿主上验证了构建、契约与关闭态；S4U logon type、NTFS ACL/owner、token 释放、并发、取消、服务重启、profile/known-folder 与网络盘行为仍需隔离 Windows Server + LocalSystem Helper + 两个本地账户，能力门在此期间保持 `false`。 |

### 2026-09-25：用户执行通道接入统一可观测性

`feature_log` 引入的可观测性基础设施（事件目录、`CorrelationContext`、`ISecurityAuditWriter`、`IEventLogger`）此前只覆盖管理员提权通道。本次把有效用户执行通道补到同一标准：

- `ObservabilityEventCatalog` 在「特权与 IPC」区间（1300–1399）新增 `user.execution.request.accepted`(1310) 与 `user.execution.request.completed`(1311)，封闭 action 集合新增 `user.execution`。目录变更直接更新本仓库调用方、测试与文档，不保留旧名或别名。
- `UserExecutionRequest` 增加只含安全字段的 `Correlation`（`CorrelationContext`，无 JWT/密码/账户），协议版本直接升级为 `1.2`。Linux one-shot 与 Windows 用户执行管道都在 `UserExecutionRequestPolicy` 之后再校验关联元数据，缺失或非法即 `InvalidRequest` fail closed——与提权通道同构，不保留双格式解析。
- Server 侧 Linux 与 Windows 用户执行 transport 现在与提权 transport 同构：延续请求作用域的 correlation、以 `user.execution` 动作写 `accepted` 审计（审计不可用时**失败关闭**，不启动 Helper），完成后写 `completed` 审计，并保留既有的身份/资源哈希运行日志（不记录账户名与完整路径）。`actorReference` 由 `SecurityAuditWriter` 做逐实例 HMAC 转换。
- `UserExecutionContextResolver` 对每一次身份解析拒绝写 `security.authorization.denied`（`Information`，带稳定 problem code），不记录账户、subject 或家目录；成功解析刻意不逐请求记录，避免正常只读浏览产生日志噪声。
- 审计因此可区分 user execution 与 administrator elevation，满足本 Goal 完成标准中「审计可区分两类通道但不记录敏感路径/凭据」的要求。

尚未覆盖，明确记录为后续工作：Guardian 的 `RunAsAuthorizationService` 跨账户授权决定目前只返回结果、不写审计；`DisabledUserExecutionTransport`（Windows 能力门关闭时的发布默认路径）对每次被拒绝的请求不写审计；媒体 lease 读取路径未单独埋点。三项都需要各自的 operation/correlation 上下文与可失败关闭策略，不在本次范围内。

### 2026-09-26：本地调试执行方式（已实施）

在 Windows 开发宿主上，`Server.Mode=system` + `EnableWindowsUserExecution=false`（默认且安装器固定写入）使日常功能不可用：文件操作返回 `HelperUnavailable`（客户端显示「加载失败: User-execution Helper is unavailable.」），Terminal 在 `PlatformPtyFactory` 上直接 `PlatformNotSupportedException`。根因是 Windows 侧的有效用户通道必须由常驻 LocalSystem Helper 提供——非 SYSTEM 进程无法为其他本地账户取得令牌（S4U 需要 `SeTcbPrivilege`），而 Linux 侧同一条通道只是按请求派生的一次性降权 worker，因此 Linux 本地调试不需要常驻服务。

实施（**未改变任何冻结原则**）：把本 Goal 原则 3 已承认的「User Mode 只能以自己的身份执行」规则（`IsServerEffectiveUnixUser`）补齐到 Windows，并提升为一个可选择的执行后端 `PrivilegedHelper:UserExecutionBackend = helper | local-identity | disabled`。`local-identity` 的承重守卫是**目标身份必须等于 Server 进程自身的 OS 身份**（Windows 比 SID，Linux 比 `geteuid()`），因此访问控制仍由目标账户裁决，不构成「回退到 Server 服务账号」。守卫在 transport 层实现，故 `UserExecutionFileService`、后台文件任务、媒体 lease、上传暂存清理、Git 与 Terminal 共用同一道判定。

本次落地的边界：`helper` 仍为默认；安装器只在显式传入 `-EnableWindowsUserExecution` 时两侧同开，否则写入 `disabled`，**永不写入 `local-identity`**；`local-identity` 在 Production 环境或 Server 进程本身为特权（root/SYSTEM/管理员）时拒绝启动（`UserExecutionBackend.Resolve` 抛错），因此它只可能出现在开发机。Windows 首版 user execution 仍不启用，本文 Goal 3/4 的验收项不受影响。

详见 [RelaxKonOS.LocalDebugging.md](../development/RelaxKonOS.LocalDebugging.md)。

### 2026-09-26：身份不适用的结论与文案前移（root 登录体验）

Linux 上用 root（或任何 UID < 1000 的账户、nobody）登录时，认证按宿主 PAM 委派成功、桌面正常，
但第一个普通操作就被身份解析拒绝，返回 503（`/api/v1.0/files/special` 即此路径），
客户端状态栏只显示 `The OS identity is not eligible for user execution.`。
**拒绝本身是既定规则**（本文 Linux System Mode 只接受非 root、UID ≥ 1000 账户；root/LocalSystem 只留给明确授权后的提权），
问题在上下文：登录端不校验资格、`ServerCapabilitiesDto.limitations` 也没有标记，
用户只能从一次失败的文件夹打开里倒推，而那句话既不点明原因也不给出路。

本次**未改动任何资格规则**，只把结论与文案前移：

- **规则唯一化**：`UserExecutionEligibilityRules.Evaluate(identity, mode)` 是「该身份能否以有效用户执行」的唯一实现，
  `UserExecutionContextResolver` 与登录（`LoginAuthenticationService`）共用，两侧不可能再给出不同答案。
- **一码一义**：新增 `UserExecutionProblemCode.IdentityNotEligible`（身份本身不适用：root / 保留身份 / 系统账户 /
  无法验证的家目录 / 非本地 Windows 账户）；`IdentityNotExecutable` 只保留「身份合格但执行边界拒绝」
  （`local-identity` 后端账户不匹配、Helper 无法切换身份）。类型 URI 由 `UserExecutionProblemTypes` 统一定义为
  kebab-case，文件 / 文件操作 / 媒体租约端点共用 `UserExecutionProblemResult`——此前同一码在不同路由会得到两种拼法。
- **登录期声明**：`LoginResponse.executionEligibility` 返回该身份能否执行普通操作及稳定原因码
  （`ServerExecutionEligibilityReasons`）。登录**不因不合格而失败**：这是刻意保留的行为，宿主管理类功能仍然可用。
  客户端把它存进 `IAuthSession.ExecutionEligibility`，Explorer 打开窗口时即显示本地化的原因与出路。
- **文案归属**：客户端按问题类型 / 原因码映射到 `common.problem.identity_not_eligible` 与
  `common.problem.identity_not_executable`（三语包齐备，`verify-localization.py` 转绿）；服务端 `detail` 仍点明出路，
  但不再被直接拼进中文状态栏。
- **断言**：`--user-execution-only` 新增 `VerifyUserExecutionEligibility`（原因与码一一对应、文案点明 uid 1000 的出路、
  解析器对 root 抛 `IdentityNotEligible`）；`--alias-only` 断言登录响应带资格声明；
  `RelaxKonOS.Explorer.Tests` 断言问题类型 / 原因码到本地化键的映射，未知原因保留服务端 detail。

仍未实施（明确记录）：登录期**不**记录该身份不可执行的审计事件——资格声明随响应返回，
真正被拒的操作仍按既有 `authorization.check` 审计；若将来要求"登录即审计"需单独决定。

