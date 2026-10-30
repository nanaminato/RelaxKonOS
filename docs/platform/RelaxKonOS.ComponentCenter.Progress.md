# 组件中心实施进度

## 系统包外部生命周期与显式登记（PO-01～PO-05，2026-10-30）

组件中心的 Linux APT/dpkg 入口按[系统包授权设计](RelaxKonOS.PackageOwnership.Design.md)接入外部包身份核验、外部更新/卸载、受管外部版本变化提示和显式“纳入管理”。Helper 只在 dpkg 状态、程序文件归属、`dpkg --verify`（仅允许配置文件变化）、服务归属和 `dpkg --audit` 全部成立时返回 `ComponentInstalledPackage`；查询失败保持未知，不降级为未安装。外部更新同时冻结候选/依赖与当前安装身份并在执行前复查；外部卸载只冻结当前身份，不依赖仓库候选或旧下载地址。受管包被外部 apt 更新后保留首次授权并提示版本变化，读取状态不把新版本当成原安装回执。

“纳入管理”经统一安装台账的 `Repair` 操作和产品提权执行，不重装、不接管配置、不改变服务启停。`ComponentPackageRegistration` 的 `OperationId` 是首次授权且不被外部变化或再次确认改写，`Identity` 只记录最近一次核验/授权身份；登记成功写入绑定当前台账 operationId 的恢复证明，重复提交同键同身份幂等，身份变化冲突。桌面端新增三语“纳入管理/确认当前安装”入口与确认文案；Android 同步解析身份和外部变化，外部系统包在身份可核验时可更新/卸载但不取得管理权。台账以 `service:<组件>` 与 `linux:apt-dpkg` 资源键串行化，Helper 的 APT 调用另有跨组件全局独占锁，组件文件锁与统一台账顺序不变。

本机验证：`dotnet build RelaxKonOS.sln --no-restore -m:1` 0 错误；Server.Tests `--components-only` 253 项通过；Tailscale 全量检查通过（新增外部卸载 `Target=null`、缺少安装身份/更新候选的 pending 判定）；Client Installation `--component-refresh-only` 12 项、`--component-lifecycle-only` 17 项通过；WindowPreviews `--component-installs-only` 121 项通过；Android `ComponentWireTest` 通过，新增外部包身份与变化解析。完整 Android 单测中 `ProxyViewModelTest` 与 `SingBoxRuntimeViewModelTest` 共 12 项失败在干净 `HEAD` 工作树同样复现，与本轮无关。未在真实 Linux 主机执行外部登记、受管外部更新、外部更新或保留数据卸载，也未验证真实 APT/dpkg 中断恢复、跨组件并发和 `dpkg --verify` 的真实文件损坏/usrmerge 场景；PO-05 保持未验收，不宣称实机交付。

## CC-05 / MR-11：Tailscale 进入组件中心（2026-10-30）

桌面控制端已接入 Linux `systemPackage` 与 Windows `nativeInstaller` 的计划、确认、安装、较新版本更新和保留数据卸载；Windows MSI 使用独立冻结安装器目标。Linux 来源须显式确认固定签名密钥与官方发行版仓库，不自动覆盖管理员信任；受管卸载后的 Debian mask 仅凭自有卸载证明及系统标记识别。包事务与控制/登录操作共用原生锁，未决或损坏包记录阻止新变更。外部实例不会被隐式接管。

移动控制端已复用同一 API 与领域安装台账，沿用冻结计划幂等键，并提供来源确认、索引刷新及绑定原操作的停止/关账；详细设计和验证规则只维护在 [Android 组件中心](../../Client/RelaxKonOS.Client.Android/docs/features/ComponentCenter.md)。移动端是控制端，目标宿主仍为 Linux 或 Windows。

FRP 下载允许经过批准的 GitHub release 资产重定向携带签名查询串，初始归档查询串、任意域名与通用清单仍拒绝。通过显式配置代理真实下载 v0.71.0 的 13,932,092 字节并核对固定摘要；直连网络超时不计通过。Windows 原生 MSI 与完整移动设备矩阵尚未验收；Linux 实机操作、日志和具体边界见[本轮报告](../testing/RelaxKonOS.Tailscale.ComponentCenter.20261030.md)。

## CC-01 / CC-05、MR-05 / MR-11：Ubuntu 26.04 实机安装与控制（2026-10-30）

192.168.1.11 的 Server 0.1.8 通过生产安装台账安装 Nginx `1.28.3-2ubuntu1.12`，原操作成功、受管所有权和提交操作 ID 一致，配置检查及 HTTP 200 通过。APT 解析现显式请求 `Acquire::ForceHash=SHA256`，保持既有 SHA-256 准入；已构建并部署修复后的 Linux Helper，撤下测试期间临时的全局 APT 设置。

FRP 配置绑定现在以显式目录替换默认目录，避免数组追加造成重复条目和合法计划 500；新增回归验证配置不能保留未配置的默认目标。源码修复已构建；实机以移除重复的配置目录、保留同一内置与 Helper 批准 pin 的方式验证计划。服务器下载仍被 GitHub 签名重定向查询串拦截；通过官方归档上传、固定摘要校验和生产安装台账安装 frpc/frps v0.71.0 成功，同键重放返回原操作。不据此宣称服务器下载已修复、跨版本或穿透互通已验收。

Tailscale 1.104.4 从官方签名 APT 仓库外部安装，用户完成账户授权；该阶段尚未实现组件中心安装适配器；后续实现与实机结果见本文首节。修复 Linux socket / Windows pipe 将持久 Tailscale 操作 ID 的观察、执行重放和恢复误判为传输重放的问题：闭集动词交给既有受保护台账校验身份和幂等性，普通操作继续由传输层拒绝重放。Linux Helper 已部署，Windows 仅构建和共享策略回归，不作为 Windows 实机证据。

首次登录原操作保留 Issued 及其原始回执，经正式恢复预览与确认关账解除门禁，没有将后来 Running 改写成原操作成功。真实 DNS 偏好变更/恢复、断开/重连、原回执读取及同 ID 重放均成功；Server/Helper 重启后只读复核四个 Applied 原操作、配置恢复及在线状态通过，设备间直连 ping 约 1 ms。完整范围、操作 ID、构建摘要与尚存限制见[实机测试记录](../testing/RelaxKonOS.Tailscale.ComponentCenter.20261030.md)。MR-05/MR-11 不因单版本安装和本次控制验收关闭。

更新：2026-10-25。本表记录源码实现与验收边界；领域生命周期要求见 [受管运行时设计](RelaxKonOS.ManagedRuntimes.Design.md)，入口与任务设计见 [组件中心设计](RelaxKonOS.ComponentCenter.Design.md)。

## CC-01 / MR-03/04/05：Windows 多版本与文件安装（2026-10-27）

Windows 的 Nginx、Mihomo、frpc/frps 现在直接提供官方版本选择；不再把单一固定归档或待配置的签名目录作为日常安装入口。新增共享 `GET /api/v1.0/components/{componentId}/releases`，按宿主平台/架构解析官方页面或分页 GitHub Releases，15 分钟缓存，手动刷新重取。Nginx 默认选择最新稳定分支，同时允许选择主线/历史版本；GitHub draft/prerelease 不列入默认列表。2026-10-27 实际请求返回 Nginx 19 个、Mihomo 46 个、frpc/frps 各 89 个 Windows x64 版本；这是发现证据，不表示每个历史版本都已通过业务配置/互通验收。

安装来源恢复为“由服务器下载 / 由本机下载后上传 / 从本机文件安装 / 从服务器文件安装”。“我要自己下载”打开所选版本的官方链接；取得文件后选择本机或服务器文件即可。文件通过既有单次、操作者绑定的短期引用交给同一安装台账。断网时可输入已有归档版本，不要求录入摘要或建立签名源。后台保留权限、归档边界、原生版本及配置检查；元数据有摘要则自动校验，没有摘要或管理员主动提供归档时不以缺少目录证明阻止安装。取消选择文件不会提交操作。sing-box 在组件目录及来源声明中配置为仅支持 Linux，组件中心据此统一过滤 Windows 下的不支持组件；已移除按 sing-box 名称隐藏的特例。回归同时断言配置本身、Windows 不列出及 Linux 仍列出。

Nginx 与 FRP 接回领域安装器和归档导入；FRP Windows Helper 支持所选数字版本并保存实际导入程序的完整性记录。Windows Mihomo 接回已有独立 SCM 宿主，安装请求使用当前 `MihomoInstallationRequest`（签名计划与 Windows 版本/文件引用互斥），不再受旧固定版本约束；Linux 继续使用签名计划。Mihomo Windows 中断仅报告 Interrupted，不借当前健康推导原任务成功。此次不新增 Nginx 覆盖升级、通用归档卸载或完整恢复事务。

验证：Server、Client 和运行时测试工程构建通过；官方列表解析/文件传输 7 项及四组件在线目录检查通过；新增四组件来源/取消/断网客户端检查 48 项通过，原安装入口 37 项通过，安装台账 47 项、Mihomo 安装发布 86 项及完整安装客户端回归通过；组件中心中英日 × 明暗主题渲染通过。尚未在本次运行中安装或切换真实 Windows 服务，MR-03/04/05 与 CC 实机生命周期验收保持未完成。需要同步部署 Client、Server、Helper；旧远端宿主不会因客户端代码修改而获得新接口。

| 组件 | Linux 安装策略 | Windows 安装策略 | 当前接入 | 下一步与验收缺口 |
| --- | --- | --- | --- | --- |
| sing-box | 可信受管归档 + systemd | 可信受管归档 + SCM | 批量草稿、冻结计划、审批、执行与回执；Debian 七步生命周期及重启只读复核已有证据 | 执行中断、补偿故障、晚到取消和 Windows 实机闭环仍需验收 |
| mihomo | 可信受管归档 + systemd | 可信受管归档 + SCM | 组件中心复用领域运行时页；新增通用 state/plan 适配，复用发布感知的领域状态与冻结计划；来源、版本、更新、修复、回退、卸载与恢复 | 尚未进入批量 TaskPlan；不得将 sing-box 验收扩展到此组件 |
| nginx | Debian/APT 系统包；由包拥有 nginx.service | 官方 ZIP 受管目录 + 自有服务；不能套用 APT | Linux 包候选解算、冻结、明确确认、提权与统一持久台账首次安装已接线；阶段进度和重开观察已接入。Windows 增加领域发现到组件状态的只读映射，归档规划/执行仍拒绝 | Linux 首次安装操作台账已接入；两平台来源信任、失败恢复与实机验收仍未收口；Linux 更新/修复/回退/卸载保持拒绝 |
| frpc | 计划：可信受管归档 | 计划：可信受管归档 | 独立目录项、所属应用跳转与 frpc 程序只读探测，沿用隧道领域已有安装基础 | 独立版本/所有权、可信目录、组件适配器与生命周期验收 |
| frps | 计划：可信受管归档 | 计划：可信受管归档 | 独立目录项与 frps 服务只读观测，版本和程序所有权未知 | 与 frpc 分别报告能力和状态，不能共享安装成功结论 |
| Squid | 系统包 | 未声明支持 | 目录、应用跳转与只读安装/服务/待恢复状态；领域已有 Linux 包/服务操作 | 仓库版本、所有权、冻结事务、更新预检与回退边界 |
| Docker Engine | 官方源或系统包，需专属适配 | 未声明组件中心安装支持；外部 Desktop/Engine 单列 | 目录、应用跳转与只读 Engine API 观测；领域已有检测与受约束 Linux 安装 | Engine/Compose 分开建模，版本、卸载及数据保留、外部实例边界 |
| Samba | 系统包 | 未声明 Samba 安装支持 | 目录与文件服务跳转 | 探测、所有权、可信包源与专属事务；不能将 Windows 文件共享当作 Samba 安装 |
| Tailscale | 官方签名系统包适配器未接入 | 官方 MSI；专属系统安装器尚未接入 | 官方 MSI 发现及签名／包属性校验已验证；Helper 私有暂存／持锁租约已实现，尚无组件目录／来源界面或 state/plan/execute 适配器 | 生产暂存正向权限及实包串联验收、来源信任、所有权、系统安装事务及完整生命周期；MSI 不能按归档执行 |

## 本轮 Nginx 处理

- Windows 使用既有 Web 服务器实例及接入候选发现，单一实例映射为受管归档模式；仅 `Managed` 实例报告受管，已集成与未接入的外部实例均保持外部所有权。没有发现或存在多个实例时保持状态未知，不将空列表当作允许覆盖安装的证据。
- Windows 的计划请求返回 `component.execution_unavailable`，表示平台存在但当前组件归档执行未接入；不再混同为系统不支持 Windows。不会调用 Linux 包解析。
- Linux 首次安装计划同时拒绝已有受管包和外部包，避免借首次安装路径自动接管系统 nginx。外部接入仍须走领域明确确认。
- 现有 `InstallFrozenNginxPackageAsync` 会刷新索引、重新解算并比较候选，然后执行 APT 与实测版本核对。它尚不能作为完整冻结事务验收：依赖当前仅列文件名、安装额外请求 acl、解析与落盘间缺乏不可变制品证明，也没有持久操作/幂等回执及补偿。因此本表仅称其为服务端安装代码，不宣称可信多版本生命周期已完成。
- 组件通用路由当前已有 state、plan、execute 三项；修正测试仍断言两项的陈旧预期。桌面包面板仍只发 state/plan，不会因为存在 execute 路由自动提供安装按钮。

后续顺序：先完善 Linux 包事务的依赖身份、可信仓库与持久回执，再接 Windows 可信版本目录和归档事务；之后接组件中心明确执行和进度恢复界面，分别执行两平台安装/失败恢复验收。既有 Web 服务器安装入口不等于上述组件生命周期已经完成。

## 本轮验证

Server `dotnet build --no-restore -m:1` 通过。组件专项使用 `.tmp/nginx-component-checks/checks.csproj` 引用刚构建的 Server 与同一份 `ComponentLifecycleChecks.cs` 执行，63 项 PASS，覆盖三个路由的授权、包冻结/执行准入以及 Windows 空/外部/受管/多实例与版本修订。正常 Server.Tests 工程及其预构建模式本机均生成失败，未得到可定位的编译错误，因此不宣称完整测试工程通过。临时工程仅为本地验证工具。本轮没有实际安装或卸载宿主 nginx，也没有 Linux/Windows 安装事务实机验收。

## 其他组件推进：mihomo 通用适配（2026-10-25）

mihomo 已注册到组件生命周期闭集，`/api/v1/components/mihomo/state|plan` 复用 `MihomoRuntimeService`。状态保留发布未确认时的待恢复事实，不直接读取 Helper 绕过 Server 发布状态；计划原样传递安装身份、宿主、状态/目录/信任修订、releaseId、幂等键和数据保留选择，领域拒绝码原样返回。管理授权继续使用 Mihomo 产品权限。

sing-box 与 mihomo 共享 `ArchiveComponentAdapter`，防止两份归档准入逻辑漂移；适配层增加运行时身份核对，拒绝把其他运行时的观察映射成当前组件。组件 execute 对两者始终拒绝，不绕过 sing-box 任务审批或 mihomo 安装/卸载/配置发布协调器。mihomo 尚不能进入批量草稿与 TaskPlan，既有运行时页的执行能力不变。

验证：Server 构建通过；隔离组件专项引用实际构建程序集及同一份检查源码，新增两运行时各六项检查，合计 75 项 PASS。覆盖待恢复事实、冻结请求传递、发布拒绝、过期修订、错误安装模式、直接执行拒绝与跨运行时状态隔离。未执行实际宿主安装，不新增 Windows 或 Linux 实机生命周期验收结论。frp、Squid、Docker、Samba 的安装迁入仍按上表逐项推进。

## Docker 只读接入（2026-10-25）

桌面新增 `DockerComponentObserver`，显式读取主机状态时只调用既有 Docker 状态 GET。API 可用才显示实测引擎版本；不可用时不展示可能过期的版本，不把权限/连接/服务问题解释为未安装。此接口不能证明安装所有权，面板明确显示所有权未核实。引擎报告的 Linux 容器操作系统不能替代连接宿主的 Windows/Linux 平台。

组件中心现在通过 DI 解析全部 `IComponentHostObserver`，同时保留 nginx 与 Docker 观测器；原先单个服务解析在新增第二个注册后会只保留最后一个。新增中英日提示。Docker 没有新增安装、卸载、规划、审批或服务启停入口，也没有改变应用启动门槛。

Docker 验证：桌面 Client 与 Client Installation 测试工程构建通过；实际运行新增四项 Docker 观测检查均通过。完整客户端回归在 `ComponentDraftPersistenceChecks.cs:18` 中止（草稿保存后重读为空，`Single()` 失败），因此不报告全量回归通过。本轮没有修改该草稿存储路径，也没有实际安装或控制 Docker 服务。

## Squid、frpc 与 frps 独立观测（2026-10-25）

- Squid 复用既有配置快照 GET 的状态：支持范围、安装、运行、领域受管标志和待恢复。待恢复优先于运行；不从配置修订伪造程序版本。该受管标志不证明系统包所有权。组件中心不读取账户列表，也不新增生命周期写操作。
- frp 目录拆成 frpc 与 frps 两个独立组件，没有保留旧 `frp` 组件别名。隧道领域现有归档内部 runtimeId 仍是 `frp`，它不是组件目录身份；其他部署清理中的 frp 产品标识不变。
- frpc 只读取客户端运行时 GET；有效程序报告独立版本和受管/外部模式。缺失、外部无效和未知状态不显示残留版本，也不推导安全覆盖安装。
- frps 只读取现有非编辑用服务快照 GET，不调用带凭据的编辑接口。Running 仅证明观测到运行服务，版本和程序所有权仍未知；停止、未配置和运行时不可用均不能用作安装存在/不存在证据。输出不包含 Token、控制台密码、路径和原始诊断。
- 新增中英日说明及观测器注册。包冻结面板收窄到已有适配器的 nginx，其他组件不再向未注册组件发送解算请求。批量草稿、启动门槛和各领域写权限不变。

下一步仍需分别实现 frpc/frps 的可信版本目录、安装身份与事务，以及 Squid 的可信仓库、依赖冻结和包生命周期；上述只读观测不构成这些能力的交付。

本轮验证：Client 与 Client Installation 检查工程构建通过；`--component-observers-only` 专项 26 项 PASS（新增 Squid/frpc/frps 16 项、既有 Docker 4 项和导航 6 项）。这不是完整客户端回归，也不构成真实主机安装验收。

布局验证：WindowPreviews `--component-center-only` 中英日 × 明暗主题通过，检查了生成的中文浅色目录渲染。此布局回归不等于新增领域接口的真实宿主验收。

## nginx Linux 冻结安装与持久台账接线（2026-10-25）

组件中心不再停留在只读冻结：对于明确为 Absent 的 Linux nginx，可解算候选后点击「安装此冻结版本」。确认框列出宿主、版本、仓库、主包摘要与每个依赖工件摘要，并说明服务启用、安装不可取消和无自动回滚。Windows 归档仍不可执行；已有受管或外部包、待恢复和修订变化仍拒绝首次安装。

安装通过现有 `/installations/Nginx/Install` 提交 `NginxInstallationRequest.FrozenPackage`，沿用产品权限、主机提权、按操作者和请求摘要的幂等台账、`service:Nginx` 与 `linux:apt-dpkg` 资源排他及后台 worker。客户端保持冻结幂等键，响应丢失重试不生成新安装身份；安装客户端绑定原登录与原服务器，提权等待或连接切换不能把旧窗口变更发往新宿主。组件路由只保留 state/plan，删除无台账的 component execute 路由，不提供旧路由别名。领域适配器执行方法仅由台账 worker 调用。

worker 在安装前再次实测宿主、修订和完整主包身份及每个依赖的文件名、URI、字节数与 SHA-256；Helper 刷新索引后独立复核。将实际安装需要的 acl 一并加入解算，不再在确认后额外夹带未声明 acl。安装阶段禁止以杀死 APT 的方式取消，随后核对实装版本与原生配置。领域阶段经安装上下文进入统一进度。终态和原 operationId 写入现有持久台账。

组件中心重新打开只观察原活动操作，不自动安装或重试。失败/中断保留在工作区，不能因当下 nginx 健康就补写原任务成功；NginxInstallationService 的重启恢复一律返回 Interrupted，等待核对。没有实现包变更补偿，也不提供虚假回滚。系统包安装失败可能已改变宿主，不能将 Failed/Interrupted 解释成已恢复原状。

**仍未收口的事务能力**：当前台账持久化请求摘要与阶段，不是 Helper 保护的包事务提交证明；依赖现已冻结每个工件的 URI、字节数与 SHA-256，但尚无预下载隔离制品事务、独立仓库签名配置或 dpkg 中断后的人工修复向导。因此本切片是安装执行与操作台账接线，不宣称完整包生命周期或 MR-05 已完成。Windows nginx、frpc/frps、Squid、Docker、Samba 的组件中心安装事务仍分别待接入；既有领域安装页不作为它们的新组件事务交付证据。

依赖冻结契约同步改为结构化工件列表，不保留字符串依赖解析：缺失摘要、非 HTTP(S)、重复/超量工件直接拒绝，依赖摘要或下载位置变化会作废冻结计划。APT 继续根据宿主受保护的软件源与签名元数据验证实际下载；本轮未新增或扩大软件源信任。

验证：Server、PrivilegedHelper 与 Client 测试工程构建通过；隔离 Server 组件专项 90 项 PASS，覆盖持久台账重载后的同键操作身份、请求冲突、跨组件 APT 资源排他、中断终态保留、完整依赖摘要漂移和准入拒绝。Client `--package-transactions-only` 34 项 PASS，包含原键重试、精确冻结请求传输、旧登录和旧宿主请求阻断。组件中心三语言 × 明暗布局检查包含可安装按钮、活动任务互斥和 Interrupted 反馈预览；没有实际 Linux 包安装、Server 执行中断故障注入或 Windows 安装验收，不能报告实机生命周期已完成。

目录接入程度新增 Package，nginx 的 Linux 系统包来源标记为已接入，官方源和 Windows 归档保持计划中；安装台账面板不再同时显示“仅目录、无安装按钮”的提示。最后一轮三语言 × 明暗布局回归通过，包含 PackageInstall 与 PackageInterrupted 渲染。


## Linux 当前 Nginx 与桌面入口（2026-10-26）

CC-01：Linux 桌面组件中心提供当前 Nginx 的发现、外部实例显式集成与选择；安装沿用组件中心已有冻结包计划。选择通过 `GET/PUT /api/v1.0/webservers/current-nginx` 存入宿主全局 SQLite（迁移 17），同一宿主的客户端共享；只允许选择已受管或已集成的 Nginx。未显式选择且只有一个实例时直接使用；多个实例须选择，已保存实例消失后保持不可用，不切换到其他实例。

Linux 桌面 Web 服务器管理器隐藏实例选择、发现、集成、安装和卸载入口，提供组件中心跳转；保留服务器状态、配置检查、启停/重载和站点管理。窗口打开与刷新时读取当前选择，并每三秒检查变化，变化时清空旧站点目标并重新读取。未配置或不可用时提示到组件中心处理。Windows 入口保持现有行为。此变更不增加系统包更新、回滚或补偿能力，也不改变外部实例的所有权。Android UI 尚未迁移，仍按 Android 自有文档追踪。当前验证范围为本地构建与自动化，尚无 Linux 桌面实机验收。

验证：Server/Client 构建通过；`RelaxKonOS.Server.Tests --current-nginx-only` 的 8 项持久化/解析断言与桌面 `--current-nginx-only` 的 7 项联动/门禁断言通过，并检查 1060/520 宽度的四张布局预览。完整 `--webserver-only` 在既有 Windows 静态目录验证断言处失败，未据此宣称整组通过。


## 桌面组件安装入口统一（2026-10-26）

CC-01：Nginx（Windows/Linux）、Docker Engine、SMB/Samba、Squid、frpc/frps、Git 的安装入口现统一到组件中心；Mihomo 的重复运行时安装/更新页也已从代理管理器移除，保留定位 Mihomo 的跳转。sing-box 沿用既有集中入口。业务应用保留依赖事实、服务/业务配置及“在组件中心管理依赖”导航，不再提供组件安装命令、版本输入、下载或上传安装表单。RelaxKonOS 自身部署向导、应用包安装器不属于第三方宿主依赖，继续由原应用负责。

Nginx 当前实例选择扩展到 Windows 与 Linux，宿主全局选择直接驱动服务器管理器；服务器管理器仅呈现运行状态与站点操作，Nginx 卸载入口也归组件中心。外部实例仍须明确确认集成，失效选择不切换到别的实例。组件中心按所选 Nginx/Git 页面请求对应产品权限，导航绑定原登录和宿主，不执行安装。

Nginx、Docker、Samba、Squid 使用已有组件状态/冻结计划与安装台账，frpc/frps 已补齐 `FrozenArchive` 提交分支并复用同一 FRP 安装器；这些组件既有平台、信任来源、版本及恢复限制不变。Git 使用既有 Linux 软件包安装器与确认/提权台账，Windows 仅观察已有引擎，不声明冻结版本、多版本更新或回退。Mihomo/sing-box 的签名目录与运行时事务不变。FRP 与 Squid 的原领域维护动作保持原能力范围，此次未扩展生命周期或实机验收结论。Android 的安装界面未在本次桌面迁移中变更。

验证：Client/Server 与相关测试工程构建通过；安装客户端回归 499 项（临时目录置于工作区，避免 Windows 沙箱对临时目录原子替换的限制）、组件中心安装入口 31 项客户端 HTTP 提交/取消断言（模拟宿主响应）、Windows/Linux 当前 Nginx 各 8 项、宿主选择存储 8 项、SMB/Squid 回归及融合窗口检查通过。组件中心三语言×双主题布局、Git 与 Nginx 宽窄预览已检查。未实际安装/卸载第三方组件，未扩大 Linux/Windows 实机验收范围。


## CC-01 / CC-06：日常页面简化与任务弹窗（2026-10-26）

已将批量场景、组件勾选、草稿、预检、冻结计划、受管任务历史、结果、进度及恢复从主页面迁入独立任务弹窗。主页面仅保留普通组件浏览/安装，技术详情与日志默认折叠；新建草稿和任务/场景定位链接打开任务弹窗，审批及凭据确认嵌套到正确父弹窗。关闭弹窗保留草稿和已提交任务，后台观察生命周期不变。空历史不再展示空结果与恢复区。此项是桌面表现与交互收口，不改变 CC-03 的真实宿主验收范围，不新增通用脚本执行器。

验证：客户端构建、安装/任务客户端 499 项回归、组件安装与任务弹窗入口 34 项检查通过。组件中心三语言×双主题布局检查覆盖普通页面没有任务/勾选/草稿栏、运行中任务不泄露到普通页面、窄/矮弹窗的预览与草稿滚动、关闭弹窗保留后台任务与草稿、显式请求期间关闭门禁及无草稿时仍显示拒绝原因。已检查主界面和任务弹窗预览；未执行第三方组件真实安装，不扩大真实宿主验收范围。

## CC-01：单平台安装方式与宿主平台归属（2026-10-26）

- **平台归属**：`ComponentCenterViewModel.HostPlatform` 改成“登录描述符优先、观测兜底”。描述符没有给出平台时，依次取所选组件状态的目标 OS、主运行时安装快照、所选运行时快照；三者都没有才视为未知。上一轮把平台只绑定到构造参数，导致缺少描述符时判据为空、目录不再按平台过滤（Windows 宿主重新出现 Squid/Samba/Docker），本次恢复观测兜底并在状态读取后重建目录。
- **目录按平台过滤**：`Eligible` 在平台已知时只保留声明支持该平台的组件。Windows 宿主不再列出 squid、samba、docker；Linux 宿主照常列出它们。搜索、已安装、可更新、工作负载四个入口共用同一判据。
- **安装方式不再混显**：包冻结面板（`ShowPackageFreeze`）要求平台已知且该组件在当前平台确有已声明的来源，未知平台时整块面板不出现，避免把 Linux 包管理器显示在 Windows 宿主上。
- **Windows = 直接下载**：面板标题为“直接下载”，提示改为“从可信发布源下载唯一指定归档并核对 SHA-256，安装写入同一台账，不经过系统包管理器”；固定版本候选单独标注“版本”；只显示安装按钮，并明确“此平台只接入安装，更新与卸载尚未接入本面板”。Git 在 Windows 仍只有既有程序观测，提示改为“可用 winget 或官方安装包装好后回来刷新”，不声明组件中心能安装。winget 本身未接入，不能据此宣称已支持。
- **Linux = 系统包**：面板标题为“系统包管理”，新增一行“本机方式：{管理器} 系统包”。宿主只报告一个已接入管理器（当前为 apt）时直接写明管理器名，不做单选项下拉；宿主真的报告多个时才出现选择框。下方保留安装、更新、卸载三个动作，分别走计划、确认与持久台账。
- **稳定性修复**：`PackageManagers`、`PinnedArchiveCandidates` 改为内容不变时返回同一实例，避免下拉框在自身选择更新过程中被重新绑定源而写回选择（会抛 `BatchUpdateOperation` 异常）；解算时固定管理器的赋值不再反过来清掉本次刚冻结的计划。安装准入放宽为“冻结目标是宿主已报告的包（未选管理器时也允许）或固定归档”，不再硬编码 `apt` 字面量。
- **Mihomo 安装入口**：订阅页“缺少运行时”横幅的按钮改为“在组件中心管理依赖”，提示文案改为“安装与版本管理统一在组件中心完成”；代理管理器不再有任何 Mihomo 安装、更新或上传安装包入口，安装、更新、修复、回退、卸载只在组件中心的运行时页执行。

验证：`dotnet build` 桌面客户端与整个解决方案通过（0 错误）。组件安装入口专项 **37 项** PASS（原 34 项因上一轮删除 `InstallGitCommand` 后与实现不一致而无法编译，本次改为断言“无独立 Git 安装命令、Linux 走共享包计划、Windows 只观测并拒绝包计划”）。WindowPreviews `--component-center-only`（中英日 × 明暗）通过，新增断言：Windows 宿主的可见目录不含 squid/samba/docker 且描述“直接下载”、无包管理器选择、只提示安装；Windows 冻结单一固定归档后可点安装；Linux 宿主列出三者且“系统包管理”并给出安装/更新/卸载；nginx 的宿主来源 2 项（Linux 包 + 计划中的官方源）、另一平台 1 项。新增 `PlatformWindows` 预览图逐语言主题渲染，已查看中文浅色：目录无 Linux 专属组件，面板为“直接下载 / 本机方式：直接下载（可信归档）/ 版本 1.26.3 / 安装”，不再出现包管理器。Client Installation **499 项** PASS，`--package-transactions-only` 通过。未在 Windows/Linux 实机安装或卸载任何第三方组件，Windows 归档安装与 FRP 安装仍只有代码路径与模拟回执，不能据此宣称实机生命周期完成；winget 未接入。


## CC-01：组件版本检查交互（2026-10-27）

桌面选择组件时自动读取组件状态并准备版本信息；页面显示已装版本与来源提供的可用版本，仍可手动重新检查。安装或更新前继续在后台固定目标并在执行时重新核对；用户不再需要操作“解算”。切换组件时丢弃旧页面的检查结果，选择归档版本后重新准备计划。Linux 的 frpc/frps 使用上游固定归档，界面按直接下载显示，而非误称系统包。

平台来源盘点：Windows Nginx 使用受保护配置中的 nginx.org 官方固定归档；Linux Nginx、Squid、Samba、Git 通过宿主 apt，Docker 使用受授权的 Docker 仓库和 apt；frp 使用固定的上游 GitHub 归档，sing-box/Mihomo 使用各自的受信任运行时目录。Windows Git 此处仍仅观察，安装未接入。固定归档只表示配置中可用的版本，不代表实时上游最新版；Windows Nginx 和 frp 在本面板仍只支持安装。apt 的更新路径仅适用于已核实受管所有权且满足准入的组件；仓库无更新、状态不明或事务待恢复时不得声称可更新。未新增发布源、跨版本归档更新或实际主机验收。

验证：桌面 Client 构建通过；组件包事务客户端专项通过，组件中心中英日 × 明暗布局检查通过。未连接截图中的宿主复现其错误，也未实际安装或更新组件；这两种错误是否由宿主版本、状态或网络造成仍需现场日志确认。

## CC-01 / MR-05：Windows 空版本来源与误报修复（2026-10-27）

代码确认 Windows Nginx 未配置 `NginxReleases` 时默认空目录，客户端又将空目录的非零来源修订写成 0，导致无版本和状态冲突同时出现。Helper 默认提供已核验官方签名和 SHA-256 的固定 1.30.6 归档；显式空列表不被默认值覆盖。Nginx/frp 对空的批准列表报告 `HostPolicyRequired`，客户端保留真实来源修订、清理旧版本选择，并在重新检查开始时撤销旧计划，失败后不能继续安装旧目标。

验证：Server/Client 构建通过，Windows 版本/状态专项 7 项通过；组件中心中英日 × 明暗回归覆盖正常固定版本、目录变空和检查失败撤销计划。官方归档在 Windows 的 `nginx.exe -v` 输出 1.30.6；归档签名、摘要和部署要求见 [Nginx 应用文档](../applications/RelaxKonOS.WebServerManager.Design.md#windows-可用版本修复2026-10-27)。完整 `--components-only` 在原有 Linux 卸载断言（仍要求 ExecutionUnavailable，与当前更新/卸载代码不符）处失败，未据此宣称全套通过。未更新截图中远端宿主，未执行第三方安装或服务变更，不扩展 MR-05 生命周期验收范围。


### Windows 官方版本列表本机辅助（2026-10-27，MR-03/04/05）

桌面客户端先通过已认证组件状态读取服务器系统及架构，再并发启动服务器与本机官方版本查询，采用首先返回的非空、目标架构匹配列表；失败或空结果继续等待另一侧，成功后取消剩余请求。两侧复用固定官方发布源解析和按组件/RID 缓存，刷新同时作用于两侧。本机使用 .NET 默认系统代理设置，不能以本机架构替代服务器架构。会话变化、权限拒绝及未知目标不允许借本机查询绕过。

此修改只改善版本发现；服务器官方版本发现目前未接入宿主下载代理偏好。Nginx 使用 nginx.org，Mihomo/frpc/frps 使用 GitHub API，后者网络不可达或限流均可能失败；未取得目标服务器网络诊断前，不断定为缺少代理。未执行本次真实服务安装或跨版本验收。

验证：桌面客户端构建成功；官方版本解析专项 9 项通过，包含服务器 ARM64 筛选及跨系统资源拒绝。构建存在既有代码警告与 NuGet 漏洞源不可达警告；尚未验证真实远端服务器的双路网络竞争和代理连通性。


2026-10-27（MR-03/04/05）：桌面“我要自己下载”在未解析出版本或刷新失败时显示并打开当前组件固定的官方发布页面；已选择有效版本时打开具体归档地址。没有浏览器打开处理器或组件不支持官方下载安装时禁用命令，避免可点却无动作。此改动只涉及手动下载导航，不改变安装校验及平台支持范围。


2026-10-27（MR-03/04/05）：Windows 官方安装卡片调整为先选来源，再选择版本或文件，最后安装。本机文件通过系统文件选择器上传，服务器文件通过服务器文件浏览器建立文件引用，界面显示选中文件名或路径。文件来源在选择文件前禁用安装，隐藏在线版本列表并要求输入文件实际版本；切换来源或组件清空文件引用，异步选择完成后核对当前选择修订与权限。中英日文及主题使用公共资源。此改动不扩大运行时支持和实机生命周期验收范围。

验证：组件安装入口专项通过，覆盖四组件文件必选、选中文件提交、取消及异步切换来源后丢弃旧结果；中英日文、明暗主题共 12 张在线/文件安装截图已生成，抽查中文浅色文件安装及英文深色在线安装无截断。未执行真实第三方服务安装。

## Android 组件状态、安装与当前 Nginx（2026-10-28）

Android 新增“组件中心”入口，补上此前“Android UI 尚未迁移”的空缺。页面按组件读取 `GET /api/v1.0/components/{componentId}/state`，显示所有权、实测版本、安装模式、目标平台、问题码与待处理操作；对服务器报告为系统包的 nginx/docker/samba/squid 提供安装、更新、卸载，并把组件链接到消费它的应用。

变更流程与桌面端一致：客户端用用户看到的宿主、模式与 `expectedRevision` 调 `POST /components/{componentId}/plan` 冻结目标，确认后把 `ComponentExecuteRequest` 作为 `frozenPackage` 交给该组件的安装台账（复用以至同一幂等键、提权和持久操作记录）；成功后显示操作 ID 并进入“任务与恢复”。托管归档（Windows Nginx、frpc/frps）不在此页选版本或文件，仍由应用/运行时页完成；Mihomo 与 sing-box 保持运行时页；`git` 不在 Android 目录中。

Nginx 卡片提供“当前 Nginx”：读取宿主全局选择与受管/已集成实例，写入 `GET/PUT /api/v1.0/webservers/current-nginx`；单实例无显式选择时由服务器解析，失效选择不自动切换。

范围限制：本页不提供修复、回退、批量变更、TaskPlan 草稿、归档版本选择、安装包上传或数据删除；不绕过提权；卸载默认保留业务数据与配置。

验证：Android `:app:compileDebugKotlin`、`:app:assembleDebug` 通过；`ComponentWireTest` 5 项、`ComponentRepositoryTest` 11 项通过。完整 `:app:testDebugUnitTest` 1745 项中 12 项失败，全部位于 `ProxyViewModelTest` 与 `SingBoxRuntimeViewModelTest`，并在干净 `HEAD` 工作树上以同一清单复现，因此不是本轮引入；未执行真实宿主组件安装、更新或卸载，也未在设备上完成 UI 复查。

细节由 Android 文档维护：[组件中心（Android）](../../Client/RelaxKonOS.Client.Android/docs/features/ComponentCenter.md)。

## 应用内组件入口与生命周期收口（2026-10-28）

代理管理器移除了工作区里的“组件中心”标签页与订阅页的跳转按钮；Mihomo 运行时的安装/版本管理入口只保留在“设置 → 运行时 → 在组件中心管理依赖”。订阅页在运行时未安装时只显示说明（指向设置中的入口），不再直接跳转和选择组件。此改动只调整入口位置，不改变组件中心已有的 Mihomo 运行时页、安装台账或权限。

验证：桌面 Client 构建通过；`WindowPreviews --component-installs-only` 37 项通过。其余应用的应用内安装/更新入口、设置页依赖跳转与“进入应用时提示”仍需逐应用迁移，见[应用组件入口迁移计划](RelaxKonOS.ComponentCenter.ApplicationEntryPlan.md)。

## CC-01：通用组件启动门禁与共享依赖区（2026-10-29）

`ComponentCatalog.LaunchGate` 不再只认批量接入（sing-box）：它由组件目录自身的 `Applications` 关联生成，同一应用的多个组件是**可替代**关系（Tunnel Manager 的 frpc/frps 任一已安装即满足），没有关联组件的应用不产生虚假依赖。`ApplicationAvailabilityService` 改用 `IComponentLifecycleClient.StateAsync` 读取通用组件状态，按“待恢复 / 执行中 / 所有权”的顺序分类，不再读取 sing-box 专属的运行时安装与服务状态；权限改用应用声明的 `server.components.read`。宿主声明该平台不提供组件时归为 `NotRequired` 并允许打开应用，避免把平台差异当成未安装；未知/读不到仍为 `Unknown`，不诱导入场安装。提示文案按应用名与组件名格式化，未知/无权限不提供组件中心跳转。

新增共享 `ComponentDependencySettingsView`（组件名、宿主实测状态、前往组件中心、重新检查），放在应用设置页：代理管理器、代理服务端、Docker、SMB、Squid、FRP、Web 服务器新增或复用“设置”页；Project Editor 从“工具 → 组件依赖”打开同一区域。应用内遗留生命周期 UI 一并删除：FRP 的 `InstallationPanel` 与运行时卸载/回退、Squid 的安装提示与卸载、Docker 的顶部横幅与安装按钮、SMB 的跳转按钮、Git 安装对话框按钮、Web 服务器的跳转按钮、代理管理器工作区的“组件中心”导航项。服务启停、配置、站点、隧道、镜像等业务能力保持不变。

验证：桌面 Client 构建通过；Client Installation 专项 **520 项 PASS**，新增 12 项共享依赖区检查（目录替代关系、逐组件状态、无安装/修复/回退/卸载命令、无权限/未知/平台不提供、导航、不冻结计划、未登录）与 40 项通用门禁检查（替代关系合并、`NotRequired`、未知/无权限、生命周期失效、切换宿主、不规划）；`WindowPreviews --component-installs-only` 37 项、`--component-center-only` 三语言×明暗、`--readiness-only`（含 `NotRequired` 无徽标）与 `--websites-only`、`--git-dock-only`、`--desktop-actions-only`、`--fused-apps-only` 通过。未执行任何真实宿主组件安装/卸载，也未在真实桌面逐应用点击缺失提示，因此不标记桌面实机验收完成。Android 迁移仍待进行。

## 组件中心 Android 受管归档安装（2026-10-29）

Android 组件中心现在能从服务器固定的受管归档版本安装 FRP（frpc/frps）。状态读取保留 `pinnedArchives`、`pinRevision` 与 `committedOperationId`；客户端校验每个固定版本的 `releaseId`/版本非空、SHA-256 为 64 位十六进制、来源为 HTTPS，并且该固定版本确实声明适用于当前组件，否则整份状态按不可信拒绝。计划请求带 `sourceId=host-pins`、`expectedSourceRevision=pinRevision`、所选 `releaseId` 与 `requestedVersion`，绝不上传 URL 或摘要；冻结请求仍原样提交并通过 `frozenArchive` 交给 FRP 安装台账，复用同一幂等键、提权与持久操作。未安装且服务器列出固定版本时才显示“选择版本 + 安装”；Windows Nginx 等本页不能执行的归档只显示状态与说明；更新与卸载仍按服务器能力显示。

验证：Android `:app:compileDebugKotlin` 通过；`ComponentWireTest` 9 项（新增固定归档解析、跨组件固定版本拒绝、归档计划体、系统包计划不带归档身份）与 `ComponentRepositoryTest` 通过。未在设备或真实宿主上执行归档安装，也未删除应用内安装页；应用设置入口与进入应用提示、Mihomo/sing-box 运行时计划仍按[迁移计划](RelaxKonOS.ComponentCenter.ApplicationEntryPlan.md)待办。

## 组件中心 Android 应用依赖入口与进入提示（2026-10-29）

Android 的 7 个依赖宿主组件的应用（站点、SMB、Squid、代理管理器、代理服务端、隧道、Docker 工作区）在各自动作行增加共享“组件依赖”入口：只读取 `GET /components/{id}/state`，显示组件名称、所有权与实测版本，并提供一个前往组件中心的跳转，不安装、不修改、不上传文件。Squid 应用内的安装/卸载按钮与确认对话框已删除，改由组件中心执行。

进入应用时读取同一状态路由：只有**实测未安装、待恢复或执行中**才拦截，未安装可前往组件中心、待恢复/执行中先处理“任务与恢复”，取消返回上一页而不打开应用；已安装（含服务停止）、外部安装、平台不支持和未知/读取失败都不拦截，避免把未知当作未安装或把用户锁在应用外。隧道把 frpc/frps 视为可替代依赖，任一就绪即可进入；同组件的待恢复/执行中优先于另一组件的缺失。

验证：Android `:app:compileDebugKotlin`、`:app:assembleDebug` 通过；`ComponentWireTest` 9 项、`ComponentRepositoryTest` 11 项、新增 `ComponentDependencyRulesTest` 6 项全部通过（强制重跑）。未在设备上复查缺失提示与返回路径，也未删除 Docker/SMB/Nginx/代理运行时页中的应用内安装入口；这些仍按[迁移计划](RelaxKonOS.ComponentCenter.ApplicationEntryPlan.md)待办。

## 组件中心 Android Docker 安装入口收口（2026-10-29）

Docker 工作区删除了应用内安装入口：引擎管理页不再提供“安装/重试”按钮与安装确认对话框，记录页也不再提供安装重试；安装只由组件中心在服务器报告系统包且具备管理资格时提交同一 `DockerInstallationRequest`，应用侧保留镜像源、引擎启停、资源操作以及对组件中心已发起安装的进度、取消与恢复观察。`DockerControlViewModel.install()` 一并删除，避免保留第二条安装通道。

验证：Android `:app:compileDebugKotlin` 通过；Docker 相关单元测试与组件测试重跑通过。仪器测试中与 SMB/Nginx 安装表单相关的用例仍针对尚未迁移的页面，未执行；未在设备上复查。

## 组件中心 Android SMB 安装入口收口（2026-10-29）

SMB 应用删除了安装按钮、安装确认对话框与“操作记录”中的安装重试；`SmbViewModel` 的安装审阅（`SmbInstallationReview`、`requestInstallation`、`install`）一并删除。Samba 安装只在组件中心进行，应用内保留状态、共享/账户管理、操作记录，以及对组件中心已发起安装的进度、取消与按 ID 恢复观察。同步更新了 Android SMB 文档与两条仅测试安装审阅的仪器测试；`SmbViewModelTest` 中安装审阅用例删除，取消与恢复用例保留。

验证：Android `:app:compileDebugKotlin`、`:app:compileDebugAndroidTestKotlin` 通过（后者顺带修复了既有 `mobileApplications` 新参数未传入的三处仪器测试夹具）；SMB、Docker、组件规则/Wire/仓库单元测试重跑通过。未在设备上执行安装或取消流程。

## 组件中心 Android FRP 服务器文件安装（2026-10-29）

Android 组件中心在固定版本选择之外，为 FRP 增加“从文件安装”：操作者选择服务器上已有的归档、填写归档中的版本并建立文件引用（`POST /installations/FileReference`），随后以 `FrpInstallationRequest(confirmed, version, fileReferenceId)` 进入同一安装台账，沿用同一幂等键、提权与持久记录；成功后显示操作 ID 并可进入“任务与恢复”。引用与当前操作者绑定并会过期，服务器仍校验平台、身份和内容；本页不提供本机文件上传，也不为本页不能执行的归档（如 Windows Nginx）显示安装动作。新增 `validArchiveFileInstall` 规则测试（组件必须属于可执行归档、版本格式合法、引用非空）。

验证：Android `:app:compileDebugKotlin` 通过；组件规则/Wire/仓库单元测试重跑通过（规则测试 7 项）。未在设备或真实宿主上执行文件安装；Tunnels 应用内仍保留本机文件上传的运行时安装页，因此该页尚未删除。

## 组件中心 Android FRP 本机文件安装（2026-10-29）

组件中心的“从文件安装”现在同时支持本机归档上传与服务器已有归档：本机文件经文档选择器交给同一操作者绑定的上传接口换取文件引用，服务器文件通过路径与文件引用接口建立引用；两者再以 `FrpInstallationRequest(confirmed, version, fileReferenceId)` 进入同一安装台账。引用过期、权限变化、会话切换或上传失败都只显示稳定问题码，不提交安装。至此 FRP 的固定版本、服务器文件与本机文件安装都在组件中心；FRP 的更新/修复/回退仍由服务端组件计划拒绝执行，因此 Tunnels 应用内运行时页暂时保留。

验证：Android `:app:compileDebugKotlin` 通过；组件规则/Wire/仓库单元测试重跑通过。未在设备上选择真实文件或执行安装。

## 组件中心 Android Nginx 文件安装与按组件服务分派（2026-10-29）

组件中心的“从文件安装”不再只服务 FRP：Nginx 也可选择服务器归档或上传本机归档，并按组件选择所属安装服务（Nginx / FRP）提交 `NginxInstallationRequest(confirmed, version, fileReferenceId)` 或 `FrpInstallationRequest(confirmed, version, fileReferenceId)`。文件引用、上传、版本规则、引用过期与失败提示沿用同一实现；本页仍不解析归档内部版本，服务器继续校验平台、身份和内容。至此 Nginx 的两条安装路径（Linux 系统包冻结计划、Windows 固定归档计划）与文件安装都可在组件中心完成，应用内安装表单已具备移除条件。

验证：Android `:app:compileDebugKotlin` 通过；组件规则/Wire/仓库单元测试重跑通过（文件安装规则覆盖 Nginx、FRP 与拒绝其他组件）。未在设备上选择真实归档或执行安装。

## 组件中心 Android Nginx 安装入口收口（2026-10-29）

网站应用删除了 Nginx 安装表单、版本/文件选择、安装确认与“操作记录”中的安装重试；`NginxViewModel` 的安装意图、下载目录、文件引用/上传与安装提交一并删除，卸载改为直接提交一次 `NginxInstallationRequest(confirmed)` / Uninstall（Windows 受管归档与 Linux 系统包卸载仍只在应用内，待组件中心具备同等卸载能力再迁移）。应用保留实例发现、接管、生命周期、卸载，以及对组件中心已发起安装任务的观察与取消。组件中心的 Nginx 安装路径（Linux 系统包冻结计划、Windows 固定归档、服务器/本机文件引用）已在上一轮接通。

验证：Android `:app:compileDebugKotlin`、`:app:compileDebugAndroidTestKotlin` 通过；网站与组件单元测试重跑通过；删除仅针对安装表单的 `NginxInstallFormTest` 仪器测试。未在设备上执行安装或卸载。

## 组件中心 Android FRP 安装入口收口（2026-10-29）

隧道应用的运行时编辑器不再提供“安装”类型、安装包来源或自行下载链接，未安装时只显示安装引导与“组件依赖”入口；已安装时仍可升级、修复、回退与卸载，并继续观察组件中心发起的安装任务、取消与按 ID 恢复。`TunnelsViewModel.install` 明确拒绝 `Install` 类型，防止应用内出现第二条安装通道。FRP 首次安装由组件中心承接（固定版本、服务器文件、本机文件三条路径均已在位）。

验证：Android `:app:compileDebugKotlin`、`:app:compileDebugAndroidTestKotlin` 通过；组件与网站/隧道相关单元测试重跑通过。未在设备上执行安装或维护操作。

## 应用内组件入口迁移收官（2026-10-29）

按[应用组件入口迁移计划](RelaxKonOS.ComponentCenter.ApplicationEntryPlan.md)，桌面端与 Android 端的实现均已落地：

- **桌面**：8 个组件应用统一由 `ComponentCatalog.LaunchGate` + `IComponentLifecycleClient` 状态门禁覆盖；共享 `ComponentDependencySettingsView` 进入各应用设置页（Git 为设置菜单对话框）；应用内安装/更新/修复/回退/卸载 UI 与常驻横幅全部删除；仅保留业务配置、服务启停、日志与恢复。
- **Android**：7 个应用的动作行接入共享“组件依赖”入口；进入应用时检查状态并以“前往组件中心/任务与恢复/取消返回”处理；Docker、SMB、Squid、Nginx 安装与 FRP 首次安装从应用内删除；组件中心支持 FRP/Nginx 的固定版本归档、服务器文件与本机文件安装；Mihomo/sing-box 采用计划允许的运行时页唯一入口。
- **边界**：FRP 的升级/修复/回退/卸载与 Nginx Windows 受管卸载仍保留在应用内——服务端组件计划当前只允许归档首次安装，此为用户可见的能力边界，不是入口重复。所有应用依赖区只观察与跳转。

验证：桌面 Client 构建通过，Installation 专项 **520 项 PASS**；WindowPreviews `--component-installs-only` 37 项、`--component-center-only`（三语言×明暗）、`--readiness-only`、`--websites-only`、`--git-dock-only`、`--desktop-actions-only`、`--fused-apps-only` 通过；ApplicationLayout 105 组真实页面布局通过。Android `:app:compileDebugKotlin`、`:app:assembleDebug`、`:app:compileDebugAndroidTestKotlin` 通过；组件规则/Wire/仓库、SMB、Docker、网站、隧道相关单元测试通过；完整单元套件 1758 项中 12 项失败，全部位于 `ProxyViewModelTest` 与 `SingBoxRuntimeViewModelTest`，与本迁移无关且已在干净工作树上复现（见组件中心 Android 文档）。

未执行：真实 Linux/Windows 宿主上的组件安装与卸载、Android 设备 UI 复查（缺失提示、取消返回、安装后返回、三语言）。这些不得写成已完成。

## 组件中心承接 Android FRP 维护动作（2026-10-29）

组件中心对已安装的受管 FRP 增加升级（输入目标版本）、回退（修复到上一版本）与卸载：动作通过领域安装台账提交 `FrpInstallationRequest(confirmed, version, rollback)`，沿用同一提权、幂等、待恢复与取消语义；新增 `validFrpMaintenance` 规则并覆盖升级版本、带版本修复、无版本回退、卸载及拒绝非 FRP 组件/安装类型。隧道应用删除运行时维护编辑器（`TunnelInstallEditor`）、安装/维护提交与重试方法，只保留状态、操作记录、按 ID 恢复与取消；应用内不再存在 FRP 生命周期入口。受管运行时的版本目录与冻结计划仍未实现，本文按“入口已集中、计划待补齐”记录，不声称完成多版本受管更新。

验证：Android `:app:compileDebugKotlin`、`:app:assembleDebug`、`:app:compileDebugAndroidTestKotlin` 通过；组件规则测试 8 项通过（含新增维护规则）；隧道相关单元测试通过。未在设备或真实宿主上执行升级/回退/卸载。

## 组件中心承接 Android Nginx 卸载（2026-10-29）

网站应用删除受管 Nginx 的卸载按钮与 `NginxViewModel.uninstall`，受管实例的卸载改由组件中心发起：以 `NginxInstallationRequest(confirmed)` 的 Uninstall 进入同一领域安装台账，沿用提权、幂等、待恢复与取消语义；新增 `validNginxUninstall` 规则（仅 nginx）并覆盖测试。应用保留实例发现、接管、生命周期操作与安装任务观察。至此 Android 应用内不再存在任何组件的安装、升级、修复、回退或卸载入口。

验证：Android `:app:compileDebugKotlin`、`:app:assembleDebug`、`:app:compileDebugAndroidTestKotlin` 通过；组件规则测试 9 项、网站与隧道相关单元测试通过。未在设备或真实宿主上执行卸载。

## CC-05 / MR-11：Tailscale 官方 MSI 发现（2026-10-29）

共享 releases 路由已接入 Tailscale Windows 官方 stable 元数据和 SHA-256 发现，按 OS 架构精确选择 MSI；内部可重新解析明确稳定版本。传输使用公共运行时网络与代理门禁，只有固定官方元数据查询参数被精确许可。在线只读核对 1.104.4 与 1.102.2 元数据及摘要通过，没有下载或执行安装包。

当前没有 Tailscale 组件目录／来源界面、state/plan/execute 适配器、发布者签名验证或系统安装事务；不把 MSI 当 ZIP，也不因发现来源而开放安装或接管外部实例。生命周期缺口由 MR-11 跟踪。专项与证据边界见 [Tailscale 当前记录](../applications/RelaxKonOS.Tailscale.Design.md#helper-公共锁修复与-windows-官方来源发现2026-10-29)。

后续补入受限 MSI 暂存／摘要复核及 Helper 只读 WinTrust／发布者校验原语，win-x64 1.104.4 实包探测通过，未执行安装。此原语尚未注册产品安装入口，包属性、保护目录、来源信任登记和系统安装事务仍缺失；不能将其观察证据作为执行许可。专项共 512 项通过，详细边界见 [MSI 暂存与签名原语](../applications/RelaxKonOS.Tailscale.Design.md#windows-msi-受限暂存与只读签名原语2026-10-29)。

包属性随后已纳入同一次只读验证，精确核对产品、厂商、目标版本及架构，拒绝无效或矛盾的产品／升级／包 GUID；原生 x64 实包路径通过。专项现共 529 项通过。升级 GUID 仅为包证据，仍缺保护暂存、来源信任登记、升级集合与所有权策略、系统安装事务。见 [MSI 产品属性校验](../applications/RelaxKonOS.Tailscale.Design.md#msi-产品属性校验接入2026-10-29)。

Helper 私有暂存与持锁租约随后已实现，串联独立官方解析、受限下载、落盘与只读验证，拒绝覆盖原目录，失败仅清理已知文件及空目录。专项现共 555 项通过，包含真实 Windows ACL 拒绝普通用户可写目录；生产目录正向权限与官方实包完整串联尚未验收。仍无安装入口或系统安装事务。见 [暂存与租约边界](../applications/RelaxKonOS.Tailscale.Design.md#helper-私有-msi-暂存与租约2026-10-29)。

随后修复 Windows 祖先 ACL 未核对、NULL DACL 误判及创建目录后才收紧 ACL 的缺口。专项现共 576 项通过，本机 Program Files 及祖先链只读检查通过，未写入该目录或安装软件；私有根目录创建及完整实包串联验收仍待完成。见 [Windows ACL 修复](../applications/RelaxKonOS.Tailscale.Design.md#windows-保护目录-acl-缺口修复2026-10-29)。

Windows Installer 全用户／全上下文只读注册观察已实现，错误不降级为未安装，注册不代表 RelaxKonOS 所有权。专项共 598 项通过，本机非管理员原生探针明确拒绝访问；完整特权枚举、文件／服务组合及自有台账尚未验收或实现，不开放生命周期入口。见 [注册观察边界](../applications/RelaxKonOS.Tailscale.Design.md#windows-installer-注册观察2026-10-29)。

安装前组合观察已加入固定程序／保留数据路径及 SCM 服务；任一读取失败均阻止“未发现”结论，任何已有对象也不被取得所有权。专项现共 616 项通过，本机路径及服务未发现，但注册拒绝导致整体不可用，未写入系统或操作服务。仍无自有台账与安装事务。见 [组合观察范围](../applications/RelaxKonOS.Tailscale.Design.md#windows-安装前组合观察2026-10-29)。

暂存已加入不可覆盖的同版本制品身份记录，验证完成后保存；后续摘要变化在下载前报冲突，签名／产品证据变化及损坏记录也拒绝且保留原记录。专项共 644 项通过，不构成来源批准、安装所有权或断电耐久性验收。见 [制品身份绑定](../applications/RelaxKonOS.Tailscale.Design.md#msi-同版本制品身份绑定2026-10-29)。

官方 ARM64 1.104.4 MSI 现已通过 x64 宿主上的下载／摘要／WinTrust／产品属性只读验证，x64 实包也在固定产品家族校验下复核通过；两者共用 UpgradeCode，不能据此授予跨架构升级或所有权。专项 645 项通过，仍无 ARM64 原生安装／驱动／服务验收或完整生命周期。见 [ARM64 实包范围](../applications/RelaxKonOS.Tailscale.Design.md#arm64-官方-msi-实包与产品家族2026-10-29)。

## 2026-10-30 Linux 包入口、全目录刷新与 FRP 生命周期（MR-02/03/04、CC-02）

桌面进入组件中心及点击页眉刷新时，读取当前宿主支持的全部组件 state；逐项更新目录，单项失败撤下该项旧观察并继续其他组件，不因选中条目才探测，也不在此阶段生成安装计划。读取失效会话的响应不得写回。版本列表仍按所选组件加载，不把刷新状态当作刷新软件源或安装。

Linux sing-box、mihomo 的桌面安装入口改用宿主现有 APT 源，包名分别为 `sing-box`、`mihomo`，复用受保护包安装凭据、冻结候选、统一安装台账、保留数据更新/卸载及人工恢复；不自动配置源，无候选时明确提示。Windows mihomo 保留官方下载/文件入口，sing-box 保持 Linux-only。已有归档受管安装不由包入口覆盖或自动接管；归档领域接口继续拥有其自身事务。外部包更新、卸载、转受管仍未开放，待产品讨论。

FRP Linux 面板增加更新和卸载；二者始终针对 frpc/frps 整个包。更新只接受 Server 与 Helper 同时认可的、更高版本 pin，同版本不视为更新；卸载不需要选归档目标。更新/卸载先停止受管隧道和 frps，更新后需按业务入口重新启动。配置保留。Linux 版本来源仍是明确配置的可信 pin，并未改成任意 URL 或自动信任所有 GitHub 版本。

本次有源码接线与自动化检查，尚未做真实 Linux APT 包安装、跨版本更新、配置发布配合、服务健康及中断恢复验收，也未做真实 FRP 跨版本更新/卸载。MR-02/03/04 保持实施中；此前归档生命周期证据不能代替新增包路径的验收。Android 界面保持原领域运行时流程，不据桌面接线宣称移动端 APT 入口已交付。

本轮本机验证：Client、Server 测试工程及 Helper 构建通过；`--components-only`、`--component-refresh-only` 与包提交相关检查通过，新增用例覆盖两核心包身份、原键重试、全目录读取/失败隔离及 FRP 真正较高版本准入。完整客户端回归在 `ComponentDraftPersistenceChecks` 的重开读取断言失败（空结果），其后用例未执行；不报告完整套件通过。当前未进行真实 Linux 包事务或 FRP 生命周期验收。
