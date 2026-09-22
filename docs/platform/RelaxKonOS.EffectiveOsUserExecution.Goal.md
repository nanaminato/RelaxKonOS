# RelaxKonOS 有效 OS 执行身份（Effective OS User Execution）Goal

> 状态：实施中（2026-09-22：Linux 文件、批处理文件作业、Git、Terminal、Guardian 与部署源文件已接入有效用户执行；Windows 及若干安全收尾项仍未完成。）
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
- Linux worker 将使用独立的一次性降权执行单元；root dispatcher 不执行用户 I/O，也不允许降权代码回到特权 dispatcher。此项尚未实现。
- Explorer/Desktop 文件 API 是首个迁移域。Terminal/PTY、Git、Guardian、应用部署和后台文件任务在迁移前不得宣称已具备跨用户执行能力。

### 已完成：Goal 1 与 Linux 文件 API 的首个垂直切片

- 新增独立 `Protocol/UserExecution/` 版本化 contract：包含封闭的文件、Git 与 Terminal operation kind 和 Server 派生的 stable identity；没有密码、JWT、token、任意 executable 或 environment 字段。Git arguments 及 Terminal shell 仅是 Server domain 构造、Helper 再验证的受限字段，不能由 HTTP 直接扩展为通用执行。
- 新增 `UserExecutionContextResolver`：仅从验证后的 JWT `sub` 找到 RelaxKonOS 用户，再经 `CanonicalUserResolver` 对 username 与 UID/SID 重新绑定验证；HTTP body/query/header 不参与身份选择。
- 新增 `IUserExecutionService` 的 User Mode 验证 adapter。它不会切换 Server 身份。
- Linux one-shot Helper 新增独立 `--user-execution` 入口：以 root 重新解析 UID、canonical username 与 home，拒绝 UID < 1000；在 `initgroups`、`setgid`、`setuid` 和 eUID/eGID 复核成功后才执行一项封闭文件操作。降权后没有特权 dispatcher 可返回。
- `IFileService` 已替换为有效用户路由：User Mode 仍调用本地服务；Linux System Mode 通过独立 transport 调用 Helper。目标用户的权限拒绝首先返回 `elevation-required`；只有客户端在用户明确确认并完成管理员认证后才取得短期 capability，并通过既有 root/LocalSystem Helper 重试对应的受控操作；绝不静默提权或自动认证。
- 特殊位置、目录枚举、读写、上传、删除、重命名、移动、复制、创建目录、属性和 POSIX mode 均覆盖这一文件通道。System Mode batch file jobs 也会在 HTTP 请求存活时冻结有效用户 context；每一次后台枚举、冲突检查、复制、移动或删除均通过同一 one-shot Helper 完成，绝不在 Server 服务账号下打开用户路径。既有冲突决定、取消和 elevated retry 语义保持；目录合并移动按用户身份逐项执行。
- Git 的 Linux 通道已迁移为专用 `GitExecute` operation：Helper 只使用固定位置的 Git binary，并在降权后以当前用户身份运行服务端 Git domain 生成的 arguments 与工作目录。没有 generic executable、shell 或环境字段。需要临时 AskPass 凭据的远程操作暂时 fail closed，直到凭据可在不进入 User Execution wire contract 的前提下安全注入。
- Terminal 的 Linux System Mode 已迁移到专用 `--user-terminal` Helper 入口：它读取一次结构化、allowlisted shell 请求，重新验证身份并降权，然后通过固定 PTY broker 桥接 shell 标准输入输出到现有 SignalR 会话。当前只支持 bash/sh；窗口 resize 尚未传递给 broker，必须在正式发布前补齐。
- Guardian workload 创建入口现在从 JWT `sub` 解析 canonical OS account；未声明 `runAs` 时默认当前用户，跨账户仍需要单独管理员批准。Server 会把 canonical launch account 与稳定 Linux UID/Windows SID 写入定义；Agent 在接收定义及每次启动前重新解析该账号并比对 UID/SID。旧定义缺少稳定身份时 fail closed，必须重新保存；Linux 继续使用 `runuser` 完成 child UID/GID/groups transition。
- 应用部署的本地文件引用现在通过 `IFileService` 以当前登录用户读取，并立刻复制到 deployment-owned staging；后续 Docker build 只使用 staging 副本，不会在后台以 Server 服务账号重新读取用户项目文件。
- 媒体播放 lease 在创建时冻结 Server 派生的有效 OS identity 与文件修改时间；后续 bearer lease URL 没有 JWT 时，仍通过该 identity 的 user-execution 通道读取，而不是因为缺少 HTTP 主体回退到 Server 服务账号。
- 添加 contract/context 单元检查，覆盖 canonical identity、请求身份替换拒绝、System Mode fail-closed 以及无敏感/通用命令字段。

### 尚未实施（明确不跳过）

- Windows LocalSystem named-pipe/SID impersonation。
- Git 的临时 AskPass 远程凭据路径；Terminal resize 与 Windows terminal impersonation。
- 应用部署的完整用户工作负载 owner model：当前只保证从文件选择器导入源 archive 时按登录用户读取、随后由 deployment-owned staging 使用；Docker Engine 容器本身仍是宿主级资源。
- Linux user-execution 的 install/upgrade audit、openat-style TOCTOU hardening、跨 filesystem 行为、取消处理和 root-owned 残留演练。
- 安装器、Helper 配置和真实 Linux/Windows integration 环境。

### 本次验证与暂缓项

| 项目 | 状态 | 说明 |
| --- | --- | --- |
| `dotnet build RelaxKonOS.Server/RelaxKonOS.Server.csproj -c Debug --no-restore` | 通过 | 新增协议和 Server 代码可编译；仅有既存的 Linux/Windows 平台分析警告。 |
| `dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj -c Debug --no-restore` | 通过 | Linux 降权 dispatcher 与独立 Helper 入口可编译。 |
| `dotnet build RelaxKonOS.Guardian.Agent/RelaxKonOS.Guardian.Agent.csproj -c Debug --no-restore` | 通过 | Guardian stable UID/SID binding 与 Agent-side revalidation 可编译。 |
| System Mode batch file jobs | 已完成代码迁移，集成暂缓 | Job 在入队时冻结 execution context；后台每一次路径读取/修改都经 User Execution Helper。真实多用户、冲突决定、取消与跨文件系统演练仍需要隔离 Linux 环境。 |
| `dotnet build Framework/RelaxKonOS.Core/RelaxKonOS.Core.csproj -c Debug --no-restore` | 通过 | 共享 framework 回归构建。 |
| `RelaxKonOS.Server.Tests` | 暂缓 | 当前桌面 SDK 在 restore graph 的 `RelaxKonOS.Core` 项目阶段无诊断即失败；该项目文件已注明可使用预构建 Server assembly 的本地 smoke 路径，但本环境的项目图仍未生成。待恢复环境修复后必须运行新增的 `VerifyUserExecutionContextContract`。 |
| `Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj` | 暂缓 | 本环境以 `--no-restore` 构建时同样在项目图阶段以 `0 errors` 失败，未进入编译；Guardian Agent 已单独成功构建，待 restore graph 可用后仍须完成 Client/Explorer 回归。 |
| Linux System Mode 集成 | 暂缓 | 需要安装 root-owned Helper 与 sudoers 规则，以及 `relaxkon-server`、`nanami`、`alice` 三账户隔离环境；当前工作区不应修改宿主账户或 sudoers。 |
| Windows impersonation 集成 | 暂缓 | 首版 Windows user execution 尚未启用，需真实 Windows Server + LocalSystem Helper 环境。 |
