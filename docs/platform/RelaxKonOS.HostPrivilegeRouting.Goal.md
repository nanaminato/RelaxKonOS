# RelaxKonOS 宿主管理员身份与执行路由改造（Goal 执行版）

> 状态：实施中。Linux 路由、三类文件根策略及客户端契约已接入代码；Linux 多账户实机、安全重试与发布验收尚未完成，不能据此宣称目标全部可用。
>
> 建立日期：2026-09-26。范围：Linux System Mode 的登录身份、文件执行路由与特权文件策略；Windows 仅统一产品语义，具体启用仍受既有 Windows Helper 验收门约束。
>
> 提案来源：[「梳理管理员密码逻辑」分享会话](https://chatgpt.com/share/6ab7a84a-f770-83e9-9045-261242543513)。用户已确认采用会话最后提出的“三层执行身份 + 自动路由”方向；前半段“所有 Linux 用户均再次输入管理员密码”由本方案取代。

## 1. 改造结论

Server 在生产部署中继续以低权限 `relaxkonos-server` 运行。登录账户决定普通执行身份和可用的宿主管理能力；需要 root 权限的操作仍由 root-owned `RelaxKonOS.PrivilegedHelper` 执行。无需为 root 新建一个“普通执行用户”。

| Linux System Mode 登录账户 | 普通执行 | 受保护文件操作 | 再次输入管理员密码 |
| --- | --- | --- | --- |
| StandardUser，例如 `nanana` | 以 `nanana` 身份执行 | 默认拒绝；用户对当前操作显式认证后，按 capability 与目标取得短期授权 | 需要 |
| HostAdministrator，例如经宿主策略确认的 `nanami` | 先以 `nanami` 身份执行 | 仅遇到明确的 `AccessDenied` 时，按该操作的 capability 和目标自动路由至 Helper | 不需要 |
| HostRoot，即 UID 0 的 `root` | 不进入普通 user-execution worker | 在已批准的封闭 Helper 操作中直接以 root 执行 | 不需要 |

`HostAdministrator` 的普通文件仍归该账户所有。仅在必要时转入 Helper，不能把管理员的所有文件操作预先改为 root。`HostRoot` 不是放宽 `UserExecutionProtocol.IsEligibleLinuxUserId` 或让 UID 0 进入降权 worker，而是独立的特权执行路由。Terminal、Git、Guardian、部署等操作需分别完成封闭能力、安全审查与实机验收；文件路由完成不等于这些领域自动获得 root 执行。

## 2. 与当前实现的差异

| 位置 | 当前基线 | 目标变化 |
| --- | --- | --- |
| `HostAdministratorAuthenticator` | Linux 仅 `Verify(currentUsername, password)`，忽略 `administratorUsername`，也不检验管理员资格 | 标准用户手动提权时，使用指定管理员账户的 PAM 凭据，并独立检验宿主管理资格；默认建议 `root`，允许修改 |
| `UserExecutionEligibilityRules` | Linux UID 0 一律 `ReservedIdentity`，登录响应与执行解析器共用此结果 | 将“普通 worker 可执行”与“会话可进行封闭特权执行”拆开；root 在 System Mode 获得直接 Helper 路由，User Mode 不变 |
| `UserExecutionFileService` 与 `FileEndpoints` | System Mode 文件操作先走当前用户 transport；权限不足时，仅现有 `IFileElevationSessionStore` grant 命中才走 `IPrivilegedFileService` | 使用统一的路由决策：Standard 显式 grant、Administrator 严格的权限不足自动回退、Root 直接 Helper |
| `PrivilegedOperationPolicy` 与 Linux 安装器 | Helper 用一份全局 `FileAllowedRoots`；Linux 默认 `restricted`，`full` 把 `/` 对全部可达特权文件请求开放 | 文件 scope 按授权来源与身份等级分开；任何扩大到 `/` 的配置不得顺带扩大标准用户临时 grant |
| 桌面 Explorer | Linux 隐藏管理员账户框，因服务端忽略该值 | 标准用户手动提权时显示可编辑的 Linux 管理员账户；对管理员/root 会话不弹重复认证窗 |

既有 [有效 OS 执行身份 Goal](./RelaxKonOS.EffectiveOsUserExecution.Goal.md) 中“权限不足不得自动提权、root 不可作为执行身份”的规则，以及 [跨平台特权操作 Goal](./RelaxKonOS.PrivilegedOperations.Goal.md) 中“Linux 只验证当前登录用户密码”的决定，在本 Goal 实施范围内将由上表的新规则取代。它们目前仍描述已实现行为；实施时必须同步修订，不能留下两个互相矛盾的规范。

## 3. 冻结的身份和授权模型

### 3.1 区分三个概念

1. **宿主账户等级**：`StandardUser | HostAdministrator | HostRoot`。仅从 Server 验证过的 canonical OS identity、UID 和可信宿主管理策略推导；客户端、manifest、请求参数和 JWT 自报角色都不能决定该等级。
2. **普通用户执行资格**：决定是否可进入以非 root UID 运行的 user-execution worker。UID 0 永不进入该 worker；其他系统/保留账户仍按现有资格规则拒绝。
3. **特权操作授权**：决定当前已认证会话能否执行某个封闭 capability 和目标。它可以来自 root 会话、管理员会话的自动授权，或标准用户经管理员认证取得的五分钟、`jti` 绑定临时 grant。等级本身不跳过业务授权、Helper 路径策略或审计。

登录可展示账户等级和路由能力，但每次特权请求仍须重新校验当前用户、会话有效性、canonical UID、宿主管理策略与目标授权。账户被降权、删除、UID 映射改变、会话撤销或身份服务不可用时失败关闭。不能只依据登录时写入的 JWT claim 长期信任管理员身份。

### 3.2 Linux 管理员资格

`root` 以 UID 0 判定。非 root 管理员不能仅凭 `sudo` 或 `wheel` 组名判定，因为发行版和 sudoers 配置各异。Goal 0 必须冻结并实现一个**由 root 管理、可验证、可审计**的 Linux administrator policy：它明确列出 RelaxKonOS 可接受的管理员账户/组与宿主 sudo 授权的关系，并在资格查询时验证 canonical UID、组和策略仍有效。安装器不得从客户端请求或用户可写文件读取此策略；不认识的发行版、策略错误或 NSS 不可用时不得授予自动提权。验收须覆盖 Ubuntu/Debian 的 sudo 配置、非 sudo 管理员、直接 sudoers 用户项、root 密码被锁定及账户撤权。

管理员用户名默认显示 `root` 只是 UI 建议值；root 没有可用 PAM 密码时，用户可以输入其他**已获认可**的管理员账户。PAM 成功仅证明密码正确，不能代替管理员资格检查。无效凭据与“账户并非管理员”返回不同稳定 problem code；不能回退验证当前普通用户的密码。

### 3.3 按请求选择执行路径

```text
已认证 JWT → Server 查询 canonical OS identity + 当前宿主等级
  ├─ StandardUser → 当前用户的 user-execution
  │     └─ AccessDenied → elevation-required → 用户针对本次 capability/目标认证
  │                       → 5 分钟 jti grant → Helper 重试一次
  ├─ HostAdministrator → 当前用户的 user-execution
  │     └─ AccessDenied → 核验本次 capability/目标的自动授权 → Helper 重试一次
  └─ HostRoot → 核验本次 capability/目标的 root 授权 → Helper 直接执行
```

自动回退只允许结构化的 `AccessDenied`。`NotFound`、`InvalidPath`、`IdentityNotExecutable`、协议错误、Helper 不可用、超时和业务拒绝不得触发回退；不得通过异常文案或 stderr 猜测。写入、上传、移动、复制等操作在用户执行失败后可能已发生部分副作用，必须先定义安全重试边界、操作 ID、暂存/清理和幂等策略，再启用该操作的自动回退。每次请求最多一次用户路径和一次 Helper 路径；不能循环重试。

从用户路径切换到 Helper 前重新规范化 source/destination、检查路径链接与文件句柄边界。Helper 二次验证 operation kind、目标路径、内容上限、scope 和当前策略。文件目录列表、读取、下载、属性、权限修改、写入、创建、删除、重命名、复制、移动、上传/续传及后台文件任务逐项接入同一路由，不能只改 Explorer 的一个端点。

### 3.4 Helper 文件范围分层

单一全局 `--file-access full` 不能表达不同会话的授权来源。目标策略至少区分：

| 来源 | 允许范围 |
| --- | --- |
| StandardUser 的五分钟 grant | 当前 capability 与已规范化的精确目标/目录；仍受该来源的 root-owned 文件根策略限制 |
| HostAdministrator 自动授权 | 管理员文件根策略内、当前请求所需的封闭文件操作 |
| HostRoot 会话 | root 文件根策略内的封闭文件操作；若产品要求管理整机文件系统，可由安装时的显式配置允许 `/` |

首次发布默认不得因本改造把所有来源的根目录同时设为 `/`。若配置 root 会话的 `/` 范围，必须说明：当前 Server 是会话身份的信任边界；只靠 Server 传来的“我是 root”标签，Helper 无法抵抗已被攻陷的 Server 伪造身份。实施时需为 Helper 请求设计可验证的会话授权证据和回放/过期约束，或把此风险作为明确的部署选项记录，不能把来源分层描述成能防 Server compromise 的隔离。无论如何，Server 服务账号不得获得任意 `sudo`/shell 权限，Helper 仍只接受封闭操作。

## 4. 范围与非目标

- **本轮必须完成**：Linux System Mode 的账户等级与管理员认证；root 文件直达 Helper；非 root 管理员文件权限不足后的安全自动路由；标准用户精确临时授权；Helper 分层文件策略；桌面 Explorer 和相关客户端提示；安装、运维、测试和现有 Goal 文档同步。
- **按领域另行验收**：Terminal、Git、Guardian、应用部署、系统服务与其他 Host capability。它们可复用身份等级和授权决策，但不因文件路由完成而自动获得 root shell、任意命令或任意目标权限。
- **Windows 对齐**：产品规则可对应“普通账户显式管理员认证、管理员账户以自己身份优先、确需权限时走 LocalSystem Helper”；Windows 具体用户执行和自动路由必须通过既有 Windows Server 实机验证门，不在设计文档中宣称已启用。
- **User Mode**：保持单用户、无 root Helper 的现有边界；不支持“登录 root 后静默跨账户提权”。
- 不为 root 新建普通执行账户，不把 Server 服务改为 root，不提供通用 `run`、shell 或由客户端指定可执行文件的接口。

## 5. 分阶段执行与验收

### Goal 0：冻结威胁模型、管理员策略和操作清单

清点所有文件及后台文件路径、当前 grant 检查点和 Helper scope；确定 Linux administrator policy 的配置格式、更新/撤权机制、受支持发行版和“root `/` 文件范围”部署选择。定义标准用户临时授权、管理员自动授权、root 会话三者的能力矩阵与审计字段。对 Server 被攻陷、身份伪造、JWT 盗用、权限撤销、路径竞争、重试部分副作用做威胁分析。

**验收：** 每个文件 operation 都有确定路由、目标 scope 和失败语义；没有用组名或客户端角色作为唯一管理员证明；扩大到 `/` 的风险和启用条件明确。

### Goal 1：身份判定与 Linux 管理员认证

把宿主等级、普通 worker 资格和特权路由拆成独立内部模型；改造 `HostAdministratorAuthenticator`，让 Linux 使用输入的管理员账户进行 PAM 验证并检验资格。登录与刷新返回准确的 root 可用状态；每次特权请求重新校验身份等级。同步稳定 problem code、协议 DTO 和客户端多语言文案。

**验收：** root、有效非 root 管理员、普通用户、错误密码、非管理员密码、锁定 root、撤权和 UID 漂移行为均正确；root 可登录且不会被派进普通 worker；User Mode 规则不变。

### Goal 2：统一文件路由和安全重试

在 Server 文件领域建立单一 `HostFileExecutionRouter` 或等价边界；让 API、批处理、续传/暂存清理等调用共享路由，不在端点复制等级判断。增加结构化 `AccessDenied` 结果与可安全重试的操作日记。标准用户继续使用精确、五分钟、`jti` 绑定 grant；管理员自动授权与 root 授权只在本次 operation/scope 内生效，不写成可复用的全局 root session。

**验收：** 普通管理员在家目录创建的文件 owner 保持该管理员 UID；访问无权路径只在准确的 `AccessDenied` 后走一次 Helper；root 浏览受管范围直接成功；普通用户未经认证无法访问；失败/取消不留下重复或半写入结果。

### Goal 3：Helper 策略与部署

将文件 policy 拆分为授权来源对应的 root-owned 范围；协议携带经验证的授权依据和操作 ID，由 Helper 再次校验。安装器升级配置与 sudoers，默认保持低权限 Server 和收窄文件根；为整机 root 文件管理提供明确的安装配置。更新管理员手册，标明可用范围及 Server 信任边界。

**验收：** 标准用户的临时 grant 不会继承 root 会话的 `/` 范围；伪造或过期的授权依据、未允许路径、链接逃逸与 Helper 配置缺失均失败关闭；Server 进程仍无 root 权限。

### Goal 4：客户端、文档与跨平台回归

Explorer 对 Standard 显示可修改管理员账户和密码；对 Administrator/Root 自动重试或直接执行，不再弹重复认证。更新 Windows 对应 UX、桌面三语本地化、Android-owned 文档和 Android 客户端（若其文件操作使用相同端点），以及 [有效用户执行 Goal](./RelaxKonOS.EffectiveOsUserExecution.Goal.md)、[特权操作 Goal](./RelaxKonOS.PrivilegedOperations.Goal.md)、安全与部署文档。按仓库 API 演进政策，同步升级全部调用方和测试，不保留旧双语义接口。

**验收：** Linux 隔离环境分别用 root、可用 sudo 管理员和普通账户执行读、写、上传、复制、移动、删除与权限修改；验证 owner、审计、撤权、Helper 停止、PAM 错误、并发及重试。Windows 的普通/管理员账户路径在启用前完成 LocalSystem Helper 实机验证。每阶段保持 `dotnet build RelaxKonOS.sln -c Debug` 与相关测试通过。

## 6. 发布完成条件

- 三类 Linux 会话的文件执行路径与上表一致；root 无需代用普通账户，管理员日常文件不变成 root-owned。
- 标准用户无法因为任何管理员会话扩大文件根而获得同范围权限；每个 Helper 文件请求可追溯 actor、授权来源、capability、目标引用、operation ID、结果和 problem code，日志不含密码、JWT、文件内容或完整敏感路径。
- 无剩余“Linux 管理员账户字段被忽略”“root 登录成功但文件功能不可用”“权限不足直接以 Server 身份重试”路径。
- 现有文档、桌面与 Android 文案、协议、测试和安装器对实际能力的描述一致；所有发布级平台验收完成后，才将本 Goal 状态改为已完成。

## 7. 2026-09-26 实施记录与待验收项

- 已接入 canonical UID 复核、PAM 管理员账户认证、root 与非 root 管理员分类。非 root 资格由 root Helper 对指定 NSS 用户及 UID 查询当前 sudoers 是否允许运行安装好的固定 Helper；不凭组名推断。账户或策略查询失败时拒绝授权。
- Server 文件 API、后台任务和续传路径已接入标准用户短期 grant、管理员结构化 `AccessDenied` 回退及 root 直达。受保护续传会话记录授权来源，分片与提交重新校验；清理仅对索引拥有的暂存名执行。Helper 文件操作按来源读取独立 root-owned 范围。桌面与 Android 客户端已接入 Linux 管理员账户输入和 root 文件可用标记。
- `/etc/relaxkonos/privileged-helper-roots`、`-administrator`、`-root` 分别约束三类来源。默认均为 restricted，root 额外包含 `/root`；三个 `full` 配置互不继承。显式启用 `--root-file-access full` 表示信任低权限 Server 进程的会话判断：现有 Helper 接收 Server 提供的授权来源，无法抵抗已被攻陷的 Server 伪造来源。该选项只适用于接受此信任边界的部署。
- 特权文件 Helper 现会在每次 Linux 文件请求开始时以 `openat(O_NOFOLLOW)` 固定授权根目录，并以其目录描述符逐层解析后续组件；父目录替换、叶子链接读取和链接 chmod 均拒绝，不能在路径验证后被改写到策略范围外。写入、复制与删除使用同父目录事务；删除一经移入隐藏事务即为逻辑提交，异常后的递归清理由恢复流程完成。跨文件系统移动在目标复制提交后若源删除失败，仍以冲突失败且绝不自动重放。
- 尚须在 Linux 隔离环境验证 PAM 锁定 root、sudoers 直接用户项和撤权、UID 漂移、多账户 owner、Helper 停止及并发重试。上述实机安全验收前，不应将本 Goal 标为完成或在生产启用广泛 `/` 文件范围。
