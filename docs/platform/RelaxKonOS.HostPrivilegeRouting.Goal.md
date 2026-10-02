# RelaxKonOS 宿主管理员身份与执行路由改造（Goal 执行版）

> 状态：实施中。Linux 路由、三类文件根策略及客户端契约已接入代码；Linux 多账户实机、安全重试与发布验收尚未完成，不能据此宣称目标全部可用。
>
> 建立日期：2026-09-26。范围：System Mode 的宿主身份、文件路由和非文件封闭能力授权；Windows 管理员身份已按 canonical SID 查询，Helper 副作用仍需实机验收。
>
> 提案来源：[「梳理管理员密码逻辑」分享会话](https://chatgpt.com/share/6ab7a84a-f770-83e9-9045-261242543513)。用户已确认采用会话最后提出的“三层执行身份 + 自动路由”方向；前半段“所有 Linux 用户均再次输入管理员密码”由本方案取代。

## 1. 改造结论

Server 在生产部署中继续以低权限 `relaxkonos-server` 运行。登录账户决定普通执行身份和可用的宿主管理能力；需要 root 权限的操作仍由 root-owned `RelaxKonOS.PrivilegedHelper` 执行。root 的受限 Helper 策略拒绝文件列举、读取或元数据时，可退回 Server **实际运行的非 root 宿主账户**进行只读观察；它不是可配置或任意选择的“普通执行用户”，也绝不用于写入操作。

| Linux System Mode 登录账户 | 普通执行 | 受保护文件操作 | 再次输入管理员密码 |
| --- | --- | --- | --- |
| StandardUser，例如 `nanana` | 以 `nanana` 身份执行 | 默认拒绝；用户对当前操作显式认证后，按 capability 与目标取得短期授权 | 需要 |
| HostAdministrator，例如经宿主策略确认的 `nanami` | 先以 `nanami` 身份执行 | 仅遇到明确的 `AccessDenied` 时，按该操作的 capability 和目标自动路由至 Helper | 不需要 |
| HostRoot，即 UID 0 的 `root` | 不进入普通 user-execution worker；受限 Helper 拒绝只读操作时，按 Server 实际非 root 身份观察 | 在已批准的封闭 Helper 操作中直接以 root 执行；写操作不降级 | 不需要 |

`HostAdministrator` 的普通文件仍归该账户所有。仅在必要时转入 Helper，不能把管理员的所有文件操作预先改为 root。`HostRoot` 不是放宽 `UserExecutionProtocol.IsEligibleLinuxUserId` 或让 UID 0 进入降权 worker，而是独立的特权执行路由。Terminal、Git、Guardian、部署等操作需分别完成封闭能力、安全审查与实机验收；文件路由完成不等于这些领域自动获得 root 执行。

## 2. 当前实现

- `HostAccountPrivilegeService` 从 canonical OS binding 推导身份；只有 `amr=system` 会话可自动取得管理员资格，Alias 需显式补充宿主管理员认证。
- Linux 非 root 管理员资格由 root Helper 查询当前 sudoers 是否允许该 NSS 账户/UID **以 root 执行固定 Helper**；不凭 `sudo`/`wheel` 组名推断。该规则表示 RelaxKonOS 的封闭操作管理委托，不等于任意 root shell 授权。
- Windows 用 Authz 按 canonical SID 查询当前本地/间接/域组中的 Administrators SID，不依赖英文组名或 Administrator 账户名。策略查询失败拒绝自动授权。
- `HostElevationSessionStore.IsGranted` 对非文件能力在每次请求重新分类系统账户；自动授权不写入五分钟缓存，撤权后的下一次请求重新需要显式授权。普通用户仍使用 capability、target、subject 和 jti 绑定的五分钟 grant。
- 文件保持独立的 Standard/Administrator/Root 授权来源和 Helper 范围；文件权限不足不会预先把普通操作改成 root 执行。Windows 文件自动路由仍需 Helper 实机验证。
- 桌面及 Android 不再硬编码 root/Administrator；未知候选账户留空，Android 仅建议当前服务器已保存的管理员账户。服务端省略管理员账户时验证当前规范化会话账户。
- Windows 当前用户自己的环境变量无需管理员认证；系统环境变量仍须管理员资格/精确 grant。手动环境授权仅覆盖所请求的 store，不向另一个 store 扩展。
- 防火墙统一使用 `firewallChange` / `ufw` 授权；移除独立当前用户密码确认、旧密码 DTO 和 DELETE 请求体。保留操作确认与安全的一次重试。
- Guardian/脚本跨账户运行继续要求本次显式管理员审批（不授予任意 root 命令），但管理员凭据与资格检查复用统一认证器，不再凭组名授予权限。
- User Mode 不接受自动或手动宿主 grant，也不弹出可跨账户提权的入口。

## 3. 冻结的身份和授权模型

### 3.1 区分三个概念

1. **宿主账户等级**：`StandardUser | HostAdministrator | HostRoot`。仅从 Server 验证过的 canonical OS identity、UID 和可信宿主管理策略推导；客户端、manifest、请求参数和 JWT 自报角色都不能决定该等级。
2. **普通用户执行资格**：决定是否可进入以非 root UID 运行的 user-execution worker。UID 0 永不进入该 worker；其他系统/保留账户仍按现有资格规则拒绝。
3. **特权操作授权**：决定当前已认证会话能否执行某个封闭 capability 和目标。它可以来自 root 会话、管理员会话的自动授权，或标准用户经管理员认证取得的五分钟、`jti` 绑定临时 grant。等级本身不跳过业务授权、Helper 路径策略或审计。

登录可展示账户等级和路由能力，但每次特权请求仍须重新校验当前用户、会话有效性、canonical UID、宿主管理策略与目标授权。账户被降权、删除、UID 映射改变、会话撤销或身份服务不可用时失败关闭。不能只依据登录时写入的 JWT claim 长期信任管理员身份。

### 3.2 Linux 管理员资格

`root` 以 UID 0 判定。非 root 管理员不能仅凭 `sudo` 或 `wheel` 组名判定，因为发行版和 sudoers 配置各异。Goal 0 必须冻结并实现一个**由 root 管理、可验证、可审计**的 Linux administrator policy：它明确列出 RelaxKonOS 可接受的管理员账户/组与宿主 sudo 授权的关系，并在资格查询时验证 canonical UID、组和策略仍有效。安装器不得从客户端请求或用户可写文件读取此策略；不认识的发行版、策略错误或 NSS 不可用时不得授予自动提权。验收须覆盖 Ubuntu/Debian 的 sudo 配置、非 sudo 管理员、直接 sudoers 用户项、root 密码被锁定及账户撤权。

管理员账户输入不按平台猜测默认值；root 没有可用 PAM 密码时，可使用其他**已获认可**的管理员账户及其自身密码。PAM 成功仅证明密码正确，不能代替管理员资格检查。无效凭据与“账户并非管理员”返回不同稳定 problem code；不能回退验证当前普通用户的密码。

### 3.3 按请求选择执行路径

```text
已认证 JWT → Server 查询 canonical OS identity + 当前宿主等级
  ├─ StandardUser → 当前用户的 user-execution
  │     └─ AccessDenied → elevation-required → 用户针对本次 capability/目标认证
  │                       → 5 分钟 jti grant → Helper 重试一次
  ├─ HostAdministrator → 当前用户的 user-execution
  │     └─ AccessDenied → 核验本次 capability/目标的自动授权 → Helper 重试一次
  └─ HostRoot → 核验本次 capability/目标的 root 授权 → Helper 直接执行
                    └─ 仅 FileList/Read/Info/Properties 被策略拒绝 → Server 实际非 root 身份只读一次
```

自动回退只允许结构化的 `AccessDenied`。`NotFound`、`InvalidPath`、`IdentityNotExecutable`、协议错误、Helper 不可用、超时和业务拒绝不得触发回退；不得通过异常文案或 stderr 猜测。写入、上传、移动、复制等操作在用户执行失败后可能已发生部分副作用，必须先定义安全重试边界、操作 ID、暂存/清理和幂等策略，再启用该操作的自动回退。每次请求最多一次用户路径和一次 Helper 路径；不能循环重试。

从用户路径切换到 Helper 前重新规范化 source/destination、检查路径链接与文件句柄边界。Helper 二次验证 operation kind、目标路径、内容上限、scope 和当前策略。文件目录列表、读取、下载、属性、权限修改、写入、创建、删除、重命名、复制、移动、上传/续传及后台文件任务逐项接入同一路由，不能只改 Explorer 的一个端点。

### 3.4 Helper 文件范围分层

单一全局 `--file-access full` 不能表达不同会话的授权来源。目标策略至少区分：

| 来源 | 允许范围 |
| --- | --- |
| StandardUser 的五分钟 grant | 当前 capability 与已规范化的精确目标/目录；仍受该来源的 root-owned 文件根策略限制 |
| HostAdministrator 自动授权 | 管理员文件根策略内、当前请求所需的封闭文件操作 |
| HostRoot 会话 | root 文件根策略内的封闭文件操作；若产品要求管理整机文件系统，可由安装时的显式配置允许 `/`。策略拒绝的列举、读取与元数据仅可按 Server 实际非 root 身份观察；写入、权限修改及所有变更操作仍拒绝 |

首次发布默认不得因本改造把所有来源的根目录同时设为 `/`。若配置 root 会话的 `/` 范围，必须说明：当前 Server 是会话身份的信任边界；只靠 Server 传来的“我是 root”标签，Helper 无法抵抗已被攻陷的 Server 伪造身份。实施时需为 Helper 请求设计可验证的会话授权证据和回放/过期约束，或把此风险作为明确的部署选项记录，不能把来源分层描述成能防 Server compromise 的隔离。无论如何，Server 服务账号不得获得任意 `sudo`/shell 权限，Helper 仍只接受封闭操作。

## 4. 范围与非目标

- **本轮必须完成**：Linux System Mode 的账户等级与管理员认证；root 文件直达 Helper 及策略拒绝后的 Server 身份只读观察；非 root 管理员文件权限不足后的安全自动路由；标准用户精确临时授权；Helper 分层文件策略；桌面 Explorer 和相关客户端提示；安装、运维、测试和现有 Goal 文档同步。
- **按领域另行验收**：Terminal、Git、Guardian、应用部署与系统服务的实际副作用。现有非文件 Host capability 已复用动态管理员决策，仍不授予 root shell、任意命令或任意目标权限。
- **Windows 对齐**：产品规则可对应“普通账户显式管理员认证、管理员账户以自己身份优先、确需权限时走 LocalSystem Helper”；Windows SID 分类及非文件自动授权已接入；具体用户执行与 Helper 副作用仍须通过 Windows Server 实机验证。
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
- 无剩余“Linux 管理员账户字段被忽略”“root 登录成功但文件功能不可用”路径；只有 root 的策略拒绝只读操作可按 Server 实际非 root 身份重试，其他身份和所有变更操作均不得如此降级。
- 现有文档、桌面与 Android 文案、协议、测试和安装器对实际能力的描述一致；所有发布级平台验收完成后，才将本 Goal 状态改为已完成。

## 7. 2026-09-26 实施记录与待验收项

- 已接入 canonical UID 复核、PAM 管理员账户认证、root 与非 root 管理员分类。非 root 资格由 root Helper 对指定 NSS 用户及 UID 查询当前 sudoers 是否允许运行安装好的固定 Helper；不凭组名推断。账户或策略查询失败时拒绝授权。
- Server 文件 API、后台任务和续传路径已接入标准用户短期 grant、管理员结构化 `AccessDenied` 回退及 root 直达。root 的 Helper 范围拒绝列举、读取或元数据时，只读请求可按 Server 实际非 root 身份观察；受保护续传会话记录授权来源，分片与提交重新校验；清理仅对索引拥有的暂存名执行。Helper 文件操作按来源读取独立 root-owned 范围。桌面与 Android 客户端已接入 Linux 管理员账户输入和 root 文件可用标记。
- `/etc/relaxkonos/privileged-helper-roots`、`-administrator`、`-root` 分别约束三类来源。默认均为 restricted，root 额外包含 `/root`；三个 `full` 配置互不继承。显式启用 `--root-file-access full` 表示信任低权限 Server 进程的会话判断：现有 Helper 接收 Server 提供的授权来源，无法抵抗已被攻陷的 Server 伪造来源。该选项只适用于接受此信任边界的部署。
- 特权文件 Helper 现会在每次 Linux 文件请求开始时以 `openat(O_NOFOLLOW)` 固定授权根目录，并以其目录描述符逐层解析后续组件；父目录替换、叶子链接读取和链接 chmod 均拒绝，不能在路径验证后被改写到策略范围外。写入、复制与删除使用同父目录事务；删除一经移入隐藏事务即为逻辑提交，异常后的递归清理由恢复流程完成。跨文件系统移动在目标复制提交后若源删除失败，仍以冲突失败且绝不自动重放。
- 尚须在 Linux 隔离环境验证 PAM 锁定 root、sudoers 直接用户项和撤权、UID 漂移、多账户 owner、Helper 停止及并发重试。上述实机安全验收前，不应将本 Goal 标为完成或在生产启用广泛 `/` 文件范围。

## 8. 统一宿主授权验证（2026-10-02）

解决方案构建、管理员授权矩阵、当前 Windows SID/UAC 令牌比对、文件授权来源及主机设置协调器回归通过。HTTP 提权回归覆盖管理员撤权、Alias 补充认证和目标归属。Android JVM 测试验证新版防火墙请求不含密码、一次授权/一次重试及未知结果不重放。Linux PAM、root 锁定、sudoers 撤权与真实 UFW/Windows Helper 的多账户副作用仍需隔离实机验收。

### Linux 局域网实测

2026-10-02，在 Ubuntu 26.04 x64 主机部署提交 `c631d379` 的 `0.2.0-privilege-c631d379` 包，Server 保持 `relaxkonos-server` 身份；Server/Guardian active，HTTPS `/ready` 返回 200。沿用主机已有三类 full 文件策略，没有扩大范围。

- `nanami`、root 和新建普通账户通过真实 PAM 登录。管理员/root 的系统环境变更及 UFW capability 授权无需密码；普通账户缺密码和提供自身密码分别返回 `elevation-password-required`、`elevation-account-not-administrator`，指定有效管理员后取得五分钟授权。
- 临时账户增加仅允许以 root 运行固定 Helper 的 sudoers 用户项后，同一会话立即取得自动授权；删除该项后下一次请求立即拒绝。未修改现有管理员的组或规则。
- root-owned 0700 目录内的 0600 测试文件：管理员/root 读取成功，普通账户未经认证返回 403；显式管理员认证后指定文件可读，另一个文件仍返回 403，新登录会话也不能继承授权；五分钟到期后原会话再次读取返回 403。
- 管理员与普通账户各自在家目录创建目录成功，owner 分别保持本人 UID/GID。
- 用户批准后，`nanami` 无需二次认证即可读取系统环境快照、预览并写入独立测试变量，操作返回 Applied；Helper 写入后的 `/etc/environment` 保持 root:root 0644。通过对应 operation rollback 返回 RolledBack，恢复后的文件与 root 私有备份逐字节一致，重新登录后的快照 revision 也恢复为原值。手工提交错误 revision 返回 409，没有覆盖当前文件。
- 未执行 UFW 实际变更、root 锁定、UID 漂移、Helper 停止、并发重试及完整文件操作矩阵；不可将本次结果视为全部发布验收。平板证据见 Android 自有 [Verification](../../Client/RelaxKonOS.Client.Android/docs/status/Verification.md)。

安装器两项缺陷已修复并实测：Linux 与 PowerShell 包统一采用 `manifest.json` 逐文件清单，System/User Mode 共享 JSON inventory 验证器；Linux upgrade 默认复用已安装 PFX 和密码，显式 repair 重新生成证书的入口仍保留。部署 Python 回归 29 项通过，Windows 包来源、版本引擎和启动健康检查通过；新 server ZIP 的 816 个文件通过发布校验器。主机直接升级至 `0.2.0-privilege-c631d379-fix1`，未使用 `--skip-file-checks`；证书 SHA-256 与升级前一致，Server/Guardian active、HTTPS `/ready` 200。

测试账户清理同时移除本轮创建的数据库账户、workspace、默认 registry 和登录凭据，保留审计记录与现有用户；修改前建立 root 私有 SQLite 备份并核对其他账户不变、外键完整性正常。不能只删除 OS 测试用户而保留数据库绑定，否则下一次 Server 启动的身份预检会拒绝启动。系统测试账户、测试文件及临时 sudoers 已全部移除。
