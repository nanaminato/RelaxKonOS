# RelaxKonOS 本地调试执行方式（Local Debugging Execution）

> 状态：**已实施**（2026-09-26）。实施记录与验证结果见第 8 节；第 7 节记录本次做出的决策与仍然开放的问题。
>
> 建立日期：2026-09-26
>
> 关联文档：[有效 OS 执行身份 Goal](../platform/RelaxKonOS.EffectiveOsUserExecution.Goal.md)、
> [特权操作](../platform/RelaxKonOS.PrivilegedOperations.Operations.md)、
> [开发调试指南](RelaxKonOS.Develop.md)

本文只回答一个问题：**本机开发时，怎样不安装服务也能跑通日常功能，且 Windows 与 Linux 用同一套姿势。**

---

## 1. 问题：本地调试现在为什么"必须装服务"

Repo 里存在 **两条互相独立**的执行通道，它们对服务/提权的依赖程度完全不同：

| 通道 | 承载功能 | Windows 机制 | Linux 机制 | 本地调试能否绕开服务 |
| --- | --- | --- | --- | --- |
| 特权通道 `IPrivilegedOperationTransport` | 防火墙、SMB、系统服务、主机名/时区/环境、软件包、提权文件 | 本机命名管道 → Service / `--console` Helper | 每个请求 `sudo -n <helper>` 一次性 root worker | **能**：只在需要验证特权功能时才启动 |
| 有效用户通道 `IUserExecutionTransport` | 文件浏览/读写、上传、媒体租约、后台文件作业、Git、Terminal | `<pipe>-user` 管道 → **LocalSystem** Helper 的 MSV1_0 S4U 令牌 | `sudo -n <helper> --user-execution` 一次性降权 worker | Windows **不能**，Linux 能 |

Linux 侧的"有效用户"通道是一条按请求派生的一次性降权进程，Server 不需要提权、也不需要有常驻进程，因此
**Linux 本地调试只需要一个开发安装脚本，日常甚至可以完全不装**。

Windows 侧同一条通道却必须由常驻的 LocalSystem Helper 提供。原因不是实现偷懒，而是平台硬约束：

> 为非 SYSTEM 进程获取"另一个本地账户"的访问令牌，只有两条路——知道该账户密码（`LogonUser` / `CreateProcessWithLogonW`），
> 或使用不依赖密码的 S4U（`MsV1_0S4ULogon` + 受信 LSA 连接）。后者要求 `SeTcbPrivilege`，
> **只有 LocalSystem（及 LocalService/NetworkService）持有**。管理员令牌没有它。
> 见 [WindowsS4ULogon.cs](../../RelaxKonOS.PrivilegedHelper/WindowsS4ULogon.cs) 的类型注释。

于是 Windows 本地调试的现状是：

- `Server.Mode=system` + `EnableWindowsUserExecution=false`（默认、且安装器固定写入）
  → `Program.cs` 注册 `DisabledUserExecutionTransport`；
- 任何文件操作返回 `UserExecutionProblemCode.HelperUnavailable`，
  经 [UserExecutionFileService.Throw](../../RelaxKonOS.Server/UserExecution/UserExecutionFileService.cs) 抛出
  `UserExecutionException`，客户端显示 **"加载失败: User-execution Helper is unavailable."**；
- Terminal 更早就断了：[PlatformPtyFactory.cs](../../RelaxKonOS.Server/Terminal/PlatformPtyFactory.cs)
  在 System Mode + 非 Linux 上直接 `PlatformNotSupportedException`。

也就是说：**今天在 Windows 上做本地调试，文件浏览器和终端这两块日常功能必然不可用**，
除非安装 Windows 服务并打开那个尚未通过真实多用户验收的能力门。

---

## 2. 关键观察：本地调试不需要"任意账户"

要打开能力门的是"以**别的**账户执行"——这是 System Mode 在服务器上的产品语义。
但本地调试时：

```text
开发者登录客户端时用的那个 RelaxKonOS 账户
        ↓ 绑定
开发者自己正在用的那个宿主账户  ==  Server 进程自身的账户
```

开发机上，"有效用户"与"Server 自身身份"**本来就是同一个账户**。此时需要的不是 impersonation，
而仅仅是"不要在进程里换身份，直接做"。

这个语义在 repo 里已经存在，而且已经被冻结为合法：Linux **User Mode** 就是它。

> Goal 原则 3：**User Mode 不改变。** Linux User Mode 进程已是实际用户……
> `LinuxPamProvider` 已限制进程内 PAM/NSS 只能解析 eUID 对应账户。它不调用 Helper、不支持跨用户 `runAs`。

[UserExecutionContextResolver.cs](../../RelaxKonOS.Server/UserExecution/UserExecutionContextResolver.cs) 里的
`IsServerEffectiveUnixUser(uid)` 就是这个规则的实现（`uid == geteuid()`）。
[DirectUserExecutionService.cs](../../RelaxKonOS.Server/UserExecution/DirectUserExecutionService.cs) 是它的校验器，
`UserExecutionFileService.DirectAsync` 是它的执行体。

**设计因此不是发明新机制，而是把这条"只能是自己"的规则补齐到 Windows，并把它从"User Mode 专属"提升为一个可选择的执行后端。**

这样做的正当性在于：Goal 禁止的是"普通文件 I/O 回退到 Server 服务账号"，
因为那会让访问控制由 `relaxkon-server` 而不是目标用户裁决。而在"身份相等"的硬前提下，
访问控制**仍然由目标账户裁决**——因为 Server 进程就是那个账户。语义没有被放宽，只是不再需要一次同账户的 impersonation。

---

## 3. 设计：把"执行后端"显式化

### 3.1 一个配置键

Server 侧 `PrivilegedHelper` 段用**后端选择**取代今天的布尔能力门：

```json
"PrivilegedHelper": {
  "PipeName": "relaxkonos-privileged-helper-dev",
  "SharedSecret": "…",
  "UserExecutionBackend": "helper"
}
```

| 取值 | Windows | Linux | 允许场景 |
| --- | --- | --- | --- |
| `helper`（默认） | `<pipeName>-user` 管道 → LocalSystem Helper（S4U） | `sudo -n <HelperPath> --user-execution` 一次性降权 worker | 生产、以及需要"以别的账户执行"的开发机 |
| `local-identity` | 进程内直通，**仅当目标 SID == Server 进程 SID** | 进程内直通，**仅当目标 UID == `geteuid()`** | 本地调试（本文档） |
| `disabled` | 显式关闭（今天 `EnableWindowsUserExecution=false` 的等价语义） | 同左 | User Mode 强制值、以及需要复现关闭态的测试 |

要点：

- **`Server.Mode=user` 不受此键影响**：User Mode 强制直通并强制 `disabled`，保持 Goal 原则 3 不变。
- **Helper 侧保留 `enableWindowsUserExecution` 布尔**。它只表达"是否监听 `<pipe>-user`"这一件事，
  真值映射就是 `helper` / `disabled`。`local-identity` 对 Helper 无意义，因此
  `userExecutionBackend` 一旦出现在 Helper 配置（控制台或服务）里就**启动即失败**，
  理由是它只可能来自"Server 配置被复制进了 Helper 配置"，静默忽略会让两侧对"谁在执行"产生分歧。
- 键名与既有的 `Privileges:Backend`（`helper` / `disabled`）保持同一套词汇。
- 解析只发生一次（`UserExecutionBackendResolver.Resolve`），结果以 `UserExecutionBackendSelection`
  发布为 DI 服务，供终端工厂与 Git 域共用；不在各调用点重新读配置。

### 3.2 硬守卫（顺序即优先级）

`local-identity` 的每一次执行都必须依次通过：

1. **配置显式选择**：未配置即 `helper`，不存在"探测不到 Helper 就自动直通"的隐式回退。
2. **启动期校验**（`UserExecutionBackendResolver.Resolve`，用即抛错）：
   - 未知取值 → 启动失败，不落回默认；
   - `ASPNETCORE_ENVIRONMENT=Production` → 启动失败。环境变量未设置时 `EnvironmentName` 默认就是
     `Production`，所以要在生产主机上启用它必须**显式**写 `Development`，不是误配就能发生的事；
   - Linux 上 `geteuid() == 0` → 启动失败（调试后端不能变成"把整个 Server 跑成 root"的途径）。
3. **身份相等**（承重守卫，`ServerProcessIdentity.Matches`，在每次执行时判定）：
   - Windows：目标 `identity.StableIdentity`（规范 SID）== 当前进程 `WindowsIdentity.GetCurrent().User`，
     按 SID 比较（大小写不敏感），且平台必须为 Windows；
   - Linux：平台必须为 Linux 且 `uint.Parse(identity.StableIdentity) == geteuid()`，且不为 0；
   - 平台不匹配（在 Windows 上声称自己是 Linux 身份，或反之）一律不匹配；
   - 读不到自身令牌（异常）时返回"不匹配"而**不是**放行。
   不相等 → `IdentityNotExecutable`，客户端消息点明"本 Server 只能以自己的 OS 账户执行，
   要管理其他账户请走 Helper"。（身份**本身**不适用是另一码：`IdentityNotEligible`，见 3.5。）
4. **可观测**（本次刻意不做逐请求区分）：
   - `Describe()` 的 `limitations` 增加 `user-execution-local-identity`，客户端可据此提示
     "当前未验证有效用户边界"；
   - 运行日志每条完成记录都带 `Backend=local-identity`，与 Helper 通道的日志可直接区分；
   - 启动时打印后端与生效身份（见 4.1）。
   - **审计事件不变**：仍然写 `user.execution.request.accepted/completed`、`Action=user.execution`。
     这是有意选择而非遗漏：审计通道的职责是区分"普通用户执行"与"管理员提权"，而"由谁执行"是部署属性，
     逐请求新增一类审计记录只会给开发机制造噪声。若将来要求逐请求可审计，应新增
     `user.execution.local-identity` 动作并同步更新封闭 action 集合与文档，而不是在本次顺手改掉。

第 3 条是唯一真正承重的守卫：即使前两条被误配绕过，直通也只会**以自己的身份**执行，
不会变成"提权后以任意账户执行"。第 2 条是纵深防御，并且把"误配"变成启动失败而不是线上静默降级。

### 3.3 覆盖范围

直通实现在 **transport 层**，不只改 `UserExecutionFileService`：`IUserExecutionTransport`
还被 [FileOperationService](../../RelaxKonOS.Server/Files/FileOperationService.cs)（后台批处理文件作业）、
[MediaLeaseFileReader](../../RelaxKonOS.Server/Files/MediaLeaseFileReader.cs)、
[UploadSessionSweeper](../../RelaxKonOS.Server/Files/UploadSessionSweeper.cs)、
[LocalGitRepositoryService](../../RelaxKonOS.Server/Git/LocalGitRepositoryService.cs) 直接注入。
放在 transport 上，这些调用方就自动共用同一个守卫，而不是只有 `IFileService` 一条路径被覆盖。

- 新增 `LocalIdentityUserExecutionTransport`：守卫 → `UserExecutionRequestPolicy` 形状校验 →
  进程内执行 → base64 结果；异常到 problem code 的映射与 Helper 通道逐一对应
  （`ContentTooLarge` / `AccessDenied` / `NotFound` / `Conflict` / `InvalidRequest` / `InternalError`），
  使两个后端只能靠守卫、不能靠错误分类来区分。
- 抽出共享的 `DirectUserExecutionOperations`（operation → `LocalFileService` 的映射）。
  `UserExecutionFileService` 的 User Mode 分支改用它，**但执行路径本身不变**；
  `DirectUserExecutionService`（User Mode 校验器）**未改动**，因为直通的守卫在 transport 上，
  不需要它多知道一个后端。
- Terminal：`PlatformPtyFactory` 在 `local-identity` 下走进程内 PTY（Windows `ConPty`、Unix `forkpty`）；
  只有"System Mode + `helper` 后端 + 非 Linux"才抛 `PlatformNotSupportedException`。
- Git：`LocalGitRepositoryService` 只在 `helper` 后端借用 Helper 通道；`local-identity` 下走它本来就有的
  进程内 git 路径（进程**就是**目标用户），因此连带保住了 Helper 通道不支持的凭据式操作。

### 3.4 明确不覆盖、且必须给出可诊断失败的部分

| 能力 | `local-identity` 下的行为 | 原因 |
| --- | --- | --- |
| 以其他账户执行（跨用户文件、终端） | `IdentityNotExecutable`，消息指向 `helper` 后端 | 需要 S4U / 降权 worker |
| POSIX mode、Git 凭据、Windows 有效性验收项 | 维持既有 fail closed | 不在本次范围 |
| 特权通道 | 不变：仍需 Service 或提权 `--console` Helper | 提权与普通执行必须分离 |

`local-identity` **不能**被当作"Windows 有效用户执行的验收替代"：
Goal 里 Goal 3/4 的验收项（NTFS ACL、token 释放、并发、服务重启、profile/known-folder）
仍然只由 `helper` + LocalSystem 服务路径满足。

### 3.5 身份资格与执行边界分属两码，且资格结论在登录期就给出

`IdentityNotExecutable` 容易被当成"身份不适用"的通用码，但两者语义不同，混用会让客户端只能靠猜：

- `IdentityNotEligible`（**身份本身**不适用）：root、UID < 1000 的系统账户、nobody、
  家目录无法验证、非本地 Windows 账户。规则唯一实现在
  [UserExecutionEligibilityRules](../../RelaxKonOS.Server/UserExecution/UserExecutionEligibility.cs)，
  `UserExecutionContextResolver` 与登录共用它；文案按原因生成并点明出路（例如 uid 1000 以上）。
- `IdentityNotExecutable`（身份合格，但**执行边界**拒绝）：`local-identity` 后端的目标账户不是本进程账户、
  Helper 无法切换身份。

两码在 `UserExecutionProblemTypes` 里各有 kebab-case 类型（`identity-not-eligible` /
`identity-not-executable`），所有端点经 `UserExecutionProblemResult` 统一产出，客户端
`UserExecutionProblemText` 据此映射到 `common.problem.identity_not_eligible` /
`..._identity_not_executable`（三语包齐备）——服务端 `detail` 不再被直接拼进中文状态栏。

资格结论同时随登录返回（`LoginResponse.executionEligibility`，原因码见 `ServerExecutionEligibilityReasons`），
因为**登录本身不因身份不合格而失败**：宿主管理类功能仍然可用。客户端把它存进
`IAuthSession.ExecutionEligibility`，Explorer 在打开窗口时就把原因与出路显示出来，
不再让用户从第一次 503 里倒推。这也是 Linux 上用 root 登录时看到
`identity-not-eligible` 的完整链路。

---

## 4. 本地调试姿势（两平台同构）

统一的三步，只有第 1 步的平台机制不同：

```text
① 选择执行后端：UserExecutionBackend = local-identity
② 启动 Server（普通权限，IDE 或命令行）
③ 用绑定了"自己账户"的 RelaxKonOS 用户登录客户端
   → 文件、终端、Git、媒体、上传全部按自己的身份工作
```

**Windows**（无需安装任何服务）：

```powershell
dotnet run --project RelaxKonOS.Server
```

只有需要验证特权功能（SMB、系统服务、主机设置、软件包）时，才按
[PrivilegedHelper/README.md](../../RelaxKonOS.PrivilegedHelper/README.md) 提权启动一次性控制台 Helper，
并在 `developerUserSids` 里保留自己的 SID。

**Linux**（日常也不需要 `sudo`）：

```bash
dotnet run --project RelaxKonOS.Server
```

只有需要"以另一个账户执行"时，才执行一次
`deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER"`
（每次重建 Helper 后重跑），并把后端切回 `helper`。

**切回生产语义**：`UserExecutionBackend=helper`。安装器只在显式传入 `-EnableWindowsUserExecution`
时写入 `helper`，否则写入 `disabled`，且永不写入 `local-identity`。

### 4.1 启动自检（消除"不可用"的黑盒感）

今天的失败信息对开发者毫无指向性（`User-execution Helper is unavailable.`）。
增加一次启动自检，只写日志、不改行为：

- 后端为 `helper` + Windows + `<pipe>-user` 管道不存在 → 警告并给出两条明确出路
  （启动提权 Helper，或本地调试改 `local-identity`）；
- 后端为 `helper` + Linux + `HelperPath` 未配置/不存在 → 警告并指向开发安装脚本；
- 后端为 `local-identity` → 信息级日志说明"仅以 Server 自身账户执行"，并打印该账户。

---

## 5. 实施清单（已落地）

| 文件 | 改动 |
| --- | --- |
| `RelaxKonOS.Server/UserExecution/UserExecutionBackend.cs` | 新增：`UserExecutionBackend` 枚举、`UserExecutionBackendSelection`、`UserExecutionBackendResolver`（取值校验 + Production/root 启动期拒绝） |
| `RelaxKonOS.Server/UserExecution/ServerProcessIdentity.cs` | 新增：承重守卫（Windows 比 SID / Linux 比 `geteuid`），外加 `Describe()` 与 `IsPrivileged()` |
| `RelaxKonOS.Server/UserExecution/LocalIdentityUserExecutionTransport.cs` | 新增：守卫 + 形状校验 + 进程内执行 + 与 Helper 通道一致的问题码映射 |
| `RelaxKonOS.Server/UserExecution/DirectUserExecutionOperations.cs` | 新增：从 `UserExecutionFileService.DirectAsync` 提取的共享映射（含 `ContentTooLargeException : IOException`，保证 User Mode 的异常契约不变） |
| `RelaxKonOS.Server/UserExecution/UserExecutionFileService.cs` | 改用共享映射；`IdentityNotExecutable` 增加指向 Helper 的具体消息；**User Mode 分支与执行路径未变** |
| `RelaxKonOS.Server/Privileged/PrivilegedHelperOptions.cs` | 删除 `EnableWindowsUserExecution`（无兼容别名） |
| `RelaxKonOS.Server/Program.cs` | 解析一次后端并传入 `ServerModeResolver`；按后端注册 transport；发布 `UserExecutionBackendSelection`；启动日志 + 自检提示（`ReportUserExecutionBackend`） |
| `RelaxKonOS.Server/Terminal/PlatformPtyFactory.cs` | `local-identity` 下用进程内 PTY |
| `RelaxKonOS.Server/Git/LocalGitRepositoryService.cs` | 仅 `helper` 后端走 Helper 通道 |
| `RelaxKonOS.Server/HostMode/ServerModeResolver.cs` | 构造参数加入后端；limitations 增加 `user-execution-local-identity` |
| `RelaxKonOS.PrivilegedHelper/WindowsPrivilegedHelperConsoleHost.cs` | 把 `userExecutionBackend` 列入"属于部署/Server 的键"，出现即启动失败 |
| `RelaxKonOS.PrivilegedHelper/WindowsPrivilegedHelperService.cs` | 同上 |
| `RelaxKonOS.Server/Properties/launchSettings.json` | `http` / `https` 两个开发 profile 设为 `local-identity` |
| `deployment/windows/Install-RelaxKonOSServices.ps1` | 由 bool 开关改为写 `UserExecutionBackend`：开关**开启**写 `helper`，**省略**写 `disabled`（与今天的 Helper 侧能力门一一对应，永不写 `local-identity`） |
| `RelaxKonOS.Server.Tests/{ServerCoreChecks,Program,GitConflictChecks,AliasLoginVerification}.cs` | 见第 6 节 |

按仓库 API 演进政策：**不保留** `EnableWindowsUserExecution` 的兼容别名，同一变更内更新全部调用方、安装器、测试与文档。

## 6. 测试

`RelaxKonOS.Server.Tests --user-execution-only` 中新增 `VerifyUserExecutionBackendSelectionAsync`，覆盖：

- **配置矩阵**：未配置 → `helper`；Production 下 `helper` / `disabled` 合法；
  Development 下 `local-identity` 可选中；Production + `local-identity` 抛错；
  未知取值抛错（不落回默认）。
- **守卫正向**：以本进程真实身份（`ServerProcessIdentity.CurrentStableIdentity()`）执行
  `FileGetSpecialLocations` 必须成功并返回结果。
- **守卫反向**：对"另一个账户"（Windows 用不存在的 SID、Linux 用 `4294967294`）执行
  special locations / 列举 / 读 / 写 / 删五类操作，全部必须返回 `IdentityNotExecutable`；
  任何一次成功或返回别的问题码都会失败。这一条同时证明守卫在 transport 上、位于整个操作集合之前，
  而不是只在某一条端点上。
- **既有 fail-closed 回归**：`DisabledUserExecutionTransport` + 真实 `UserExecutionFileService` 的
  六类操作与 staging 断言保持不变。

尚未断言的（明确记录，不声称已覆盖）：媒体租约读取、后台文件作业与 Git 在 `local-identity` 下的
端到端行为。它们共用同一个 transport 守卫，但本次只做了代码级路由改动，没有构造对应集成断言。

同一开关下另有 `VerifyUserExecutionEligibility`（3.5 的规则），覆盖 root / nobody / UID < 1000 /
非绝对家目录 / 非本地 Windows 账户各自的原因与码，以及解析器对 root 抛 `IdentityNotEligible`；
`--alias-only` 断言登录响应携带资格声明；`RelaxKonOS.Explorer.Tests` 断言问题类型与原因码到
本地化键的映射（未知原因保留服务端 detail）。

## 7. 本次决策与仍然开放的问题

1. **已决（可回退）**：`local-identity` **只作为开发期后端**。没有同时把它产品化为 Windows 的
   "个人桌面模式"——那等于把 User Mode 扩展到 Windows，要一并处理
   `ServerExecutionIdentityDto.Uid`（Windows 现在固定 `-1`）、`ServerModeResolver` 的三处 Linux-only 守卫与
   `currentUnixUser` 认证模式命名，并需要一份新的验收矩阵。建议作为独立 Goal。
2. **开放**：是否让客户端把 `limitations` 里的 `user-execution-local-identity` 显示成一条
   "当前未验证有效用户边界"的横幅。协议已带该标记，本次未动客户端。
3. **仍待办**：是否最终统一执行路径——让 `UserExecutionFileService` 也完全走 transport，
   消除 User Mode 的直连分支。本次刻意不动已验证路径；若要统一，应作为独立改动并带完整 Linux 回归。
4. **残留风险**：`local-identity` 长期留在开发机上时，开发者看到的是"以自己的身份工作"，
   与生产语义一致，但**没有**走过 token/ACL 代码路径。补偿手段是启动警告、`Describe()` 的 limitations
   与运行日志中的 `Backend=local-identity`；生产环境则在启动期直接拒绝。

## 8. 实施记录（2026-09-26）

- 触发：Windows 开发宿主上文件浏览报 `加载失败: User-execution Helper is unavailable.`
  （`UserExecutionProblemCode.HelperUnavailable`）；根因见第 1 节，不是密钥或管道名问题。
- 改动见第 5 节。行为上最关键的三点：默认仍是 `helper`；User Mode 完全不受影响；
  `helper` 后端在 Helper 缺失时的行为与今天完全一致（fail closed，不新增回退）。
- 验证：`RelaxKonOS.sln`（Debug，`-m:1`）0 错误 0 新增告警；
  `RelaxKonOS.Server.Tests --user-execution-only` 通过（含第 6 节新增断言）；
  `RelaxKonOS.Server.Tests` 完整套件 EXIT 0，末行 `RelaxKonOS.Server backend verification passed.`；
  `deployment/windows/Install-RelaxKonOSServices.ps1` 通过 `Parser::ParseFile`（0 错误）。
  由于本机有常驻的 Server / Helper / 客户端进程占用 `bin/Debug` 输出，验证使用
  `-p:BaseOutputPath=obj/verify/` 独立输出目录完成，未改动仓库既有产物。
- 文档同步：`RelaxKonOS.Develop.md`（Windows 有效用户一段）、`RelaxKonOS.EffectiveOsUserExecution.Goal.md`
  （状态行、能力门两条、安装器验证行、2026-09-26 条）、`RelaxKonOS.PrivilegedHelper/README{,.en}.md`
  均已改写为「Helper 侧 `enableWindowsUserExecution` + Server 侧 `UserExecutionBackend`」的双侧表述。
