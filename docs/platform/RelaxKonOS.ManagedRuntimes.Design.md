# 内置应用受管运行时、版本目录与发布源

> 2026-11-02（MR-01 / CC-05）：已接入[声明式组件目录与机制派发](RelaxKonOS.ComponentCatalog.Design.md)，并实现可选签名 HTTPS 组件来源与桌面用户信任登记。第三方目录只描述平台/架构和已注册机制，不能登记新的宿主领域适配器或 Helper 授权；远程实际安装接入 `.roapp`，宿主冻结计划、领域安装/恢复与 Helper 复核继续由原实现负责。这不构成第三方宿主运行时安装/跨版本/恢复验收。

> 当前逐组件实施状态与 Nginx 平台边界见[组件中心实施进度](RelaxKonOS.ComponentCenter.Progress.md)；下文历史切片的验收范围保持原记录。
> 签名目录的生产与登记流程见[发布签名运行时目录](RelaxKonOS.RuntimeCatalog.Publishing.md)。

2026-11-03（MB-04 / MR-01 边界）：安装引导与宿主下载只依赖轻量设置 reader，写入/对账权威保持原实现；损坏初始设置在生成凭据或冻结发布前返回 `runtime.settings_drift`，已有操作继续按冻结材料重放。冻结、Helper、回执及补偿规则未放宽，未扩展真实运行时生命周期验收；代码与证据由[模块边界 §12](../architecture/server-module-evidence/Proxy-Publication.md#12-proxy-共享读取与写入安全边界2026-11-03mb-04-部分)追踪。

2026-11-03（MB-04 / MR-03 续核）：guard 对账已拆为窄实现，仍在原发布锁内复用 writer 的既有身份核验，不重新授信、不生成凭据，也不承担安装/移除/配置补偿执行权。Core 原发布恢复及 TUN 标记恢复继续保留，MR-03 未新增真实生命周期验收；两平台夹具结果和引用审计见[模块边界 §14](../architecture/server-module-evidence/Proxy-Safety.md#14-系统代理-guard-窄执行边界与安全责任审计2026-11-03mb-04-部分)。

2026-11-03（MB-04 / MR-03 TUN 边界）：TUN runtime controller 与设置 writer 已分离，继续共用 publication gate；原受管 publication 对直接 TUN 转换的拒绝保持，未新增受管 TUN 事务支持。安装/移除/配置发布补偿执行权未转移，MR-03 不关闭，详见[边界设计 §15](../architecture/server-module-evidence/Proxy-Safety.md#15-tun-运行控制与设置写入执行边界2026-11-03mb-04-部分)。

2026-11-03（MB-04 / MR-03 发布恢复作用域）：startup worker 每轮按 pending 类型临时解析协调器；原共享 gate、冻结计划、受理后收尾、Helper 同操作证明、profile/settings 提交与补偿规则不变。安装/人工恢复的独立根引用仍保留，未增加真实生命周期验收，MR-03 不关闭，见[边界设计 §16](../architecture/server-module-evidence/Proxy-Publication.md#16-发布恢复临时作用域与-profile-执行图审计2026-11-03mb-04-部分)。

2026-11-07（MB-04 / MR-03 发布恢复排空）：Core 调度器停止新扫描后，等待原领域恢复响应、终态写入和异步作用域清理。配置、安装与移除仍由原领域协调器和原事务回执裁决。验收范围见[模块边界台账](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-core-mihomo-发布恢复的停机排空)。

2026-11-07（MB-04 / MR-03 执行作用域）：安装台账适配器仅在实际执行或恢复时解析安装引导、安装发布及移除发布。运行时管理器只在状态探测和启动就绪检查中创建 HTTP 控制器。锁、冻结计划、原 operationId 和 Helper 回执仍由原服务负责。验收见[模块边界台账](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-mihomo-运行时与安装执行作用域)。

2026-11-08（MB-04 / MR-03 配置补偿）：Linux 专属 systemd 验证有效订阅候选开始转发后停止、原 Helper 补偿并恢复旧配置，以及 Server 按原回执恢复。范围限现有受管配置事务。见[模块边界台账](../architecture/server-module-evidence/Proxy-Subscriptions.md#mb-04-受管-mihomo-切换后的订阅激活补偿)。

2026-11-09（MB-04 / MR-03 TUN 恢复）：Helper 的原生检查点拒绝重复或未定义字段。当前归属无法核验时保留原历史回执和发布、停用门禁。见[检查点字段实机验收](../architecture/server-module-evidence/Proxy-Safety.md#拒绝歧义检查点字段)。

## Windows 日常安装入口调整（2026-10-27，MR-03/04/05）

按产品要求，Windows Nginx、Mihomo、frpc/frps 的日常安装采用官方版本列表和文件安装，不再要求管理员先部署签名目录、录入摘要或确认发布源。Nginx 解析 nginx.org 下载页，Mihomo/FRP 解析固定官方 GitHub 仓库的 Releases API（最多三页、每页 5 条，取得首批匹配当前宿主架构的稳定版即返回；启用 HTTP 压缩，排除 draft/prerelease），选择具体版本后走既有安装台账。sing-box 的组件及来源平台配置仅声明 Linux，不支持 Windows；组件中心按通用平台配置过滤，不为 sing-box 编写隐藏特例。此配置描述 RelaxKonOS 的组件支持范围，不表示上游没有 Windows 二进制。

支持服务器下载、本机下载后上传、本机文件、服务器文件；另提供所选版本官方下载链接。已有文件的安装允许手填版本，离线不要求联网取得官方摘要或签名清单；文件不再为校验而重复下载一份官方归档。官方元数据提供 SHA-256 时在下载时自动核对；没有摘要时允许继续。保留后台的归档大小/解压路径限制、原生版本与配置检查、宿主权限、固定服务/目录和安装记录。这一明确的产品决定取代下文对这些 Windows 日常入口“必须有签名目录/预登记归档”的要求；Linux 签名运行时与系统包事务的既有规则继续适用。

Windows Mihomo 接回既有独立 SCM 服务宿主，选择版本不再受编译期单版本清单限制；保留配置和控制器凭据，失败调用既有恢复路径。Nginx/frp 复用原领域安装器与文件引用。代码路径、HTTP 提交及版本发现已经验证；尚未执行本次 Windows 真实服务安装、跨版本切换、补偿及重启恢复，不据此关闭 MR-03/04/05。详细记录见[组件中心实施进度](RelaxKonOS.ComponentCenter.Progress.md)。

## 原操作成功提交证据（2026-10-25）

受管归档在成功提交前，将原 operationId、终态安装修订、活动修订、配置摘要和实测版本写入受保护的 `state/commits/<operationId>.json`。文件不可覆盖，先耐久发布证明再替换主状态；主状态尚无同一成功回执时忽略孤立证明。读取缺失证明不得补写或从当前状态推导；失败、人工关账和补偿不生成成功证明。该证明不改变已有归档状态或回执格式。

Linux 在替换主台账前同步 `commits` 目录及其父目录。重用原操作的精确见证时也遵守此顺序，确保存活的成功回执可以引用已同步的目录项。见[见证落盘顺序](../architecture/server-module-evidence/Proxy-Safety.md#原成功见证的目录持久化顺序)。

受管 Mihomo TUN 激活前，Helper 同步含原 `Activating` 检查点的主台账目录。目录同步失败时返回原待核验操作，当前调用不进入原生激活或补偿。见[激活前目录同步门禁](../architecture/server-module-evidence/Proxy-Safety.md#激活前的原事务目录同步门禁)。

MR-03 的受管 TUN 生命周期要求原生检查点包含启动 ID、网络命名空间链接及内核 cookie。新激活在切换前核对内核能力并持久保存原网络归属，随后以 pidfd 锁定旧进程，并保存绑定该 PID、systemd 调用 ID 与启动时刻的首次停止派发。恢复只以原归属授权。 受管 TUN 切换前须可读取本次启动的 systemd journal。补偿启动将原 StartUnit 作业 ID 绑定到 PID 1 的终态日志与调用 ID，再将原操作 ID、旧配置摘要、PID 和启动时刻写入进程见证。原见证缺失或与当前进程不符时继续待核验。见[首次停止前实测](../architecture/server-module-evidence/Proxy-Safety.md#首次停止前持久化原网络归属)。恢复、具名启动与停止、以及撤销注册均要求原证据相符。缺少 cookie 的历史检查点保留待核验，当前 Helper 无法从现有命名空间补证。

原操作补偿同样先同步主台账目录，再写入回滚阶段和调用原生适配器。同步失败时保留原检查点，原观察、停止、还原和清理留待同一操作继续核验。见[补偿前目录同步门禁](../architecture/server-module-evidence/Proxy-Safety.md#补偿前的原事务目录同步门禁)。

验证旧配置还原后，Helper 同步 `PreviousRestored` 主台账目录才开始清理。TUN 还原检查点记录当时是否存在原生检查点。续接时，原 Helper 按同一操作只读复核旧配置、服务和原生还原证据。预期的原生证据缺失、历史记录缺少该判据或实测漂移时，保留原待核验及清理材料。通过后完成剩余清理和原回执。见[清理前目录同步门禁](../architecture/server-module-evidence/Proxy-Safety.md#清理前的已还原检查点同步门禁)和[跨进程还原复核](../architecture/server-module-evidence/Proxy-Safety.md#清理前的跨进程还原复核)。

受管 TUN 在准入切换前同步原配置快照所在目录，先同步静止数据备份树和备份目录项，再进入候选数据变更。补偿从原备份生成完整还原树，先同步子目录，再替换活动数据并同步源、目标父目录。旧配置替换后也先同步父目录，随后才重新配置、启动原服务并记录 `PreviousRestored`。还原期间目录同步失败保留原事务及备份，待同一操作续接。见[原配置与数据的还原落盘顺序](../architecture/server-module-evidence/Proxy-Safety.md#原配置与数据的还原落盘顺序)。

受管 TUN 已确认原生网络还原后，原 Helper 在同一操作目录持久化服务注册还原记录。记录绑定原 TUN 和旧服务的注册内容，unit 由各自的注册生成。按记录续接时，只接受原组合、已替换注册文件的中间组合或完整还原组合，分别同步两个文件的父目录后再确认 systemd 已重载。旧服务启动前保存原派发标记。启动效果未知时保留待核验，只有原进程及其配置可核实时才继续确认。见[原服务注册的跨进程还原](../architecture/server-module-evidence/Proxy-Safety.md#原服务注册的跨进程还原)。

新 TUN 配置发布在创建设备前持久化候选注册转换和两个文件的目录项。只持有原 `Intent` 时，原 Helper 先核验启动尚未派发、设备和专用网络效果均缺席、管理选路等于原快照，以及活动配置摘要属于原事务，再按精确注册记录逆序还原。已经越过该窗口的未知状态继续由原检查点门禁裁决。见[候选注册中断与原启动准入](../architecture/server-module-evidence/Proxy-Safety.md#候选注册中断与原启动准入)。

新发布在核对实际网络效果与管理选路后，先持久化专用规则见证，再允许候选启动派发。仍处于 `NetworkDispatched` 时，原 Helper 核对 broker 已退出、原设备与地址和专用路由均已消失，以及受管 unit 停止。残留规则只有在精确见证和管理选路相符时才可按原操作删除。删除派发先持久化，未知结果只核对缺席。见[候选启动派发前的原规则回收](../architecture/server-module-evidence/Proxy-Safety.md#候选启动派发前的原规则回收)。

在 `LaunchDispatched` 后，Helper 等待原 `systemctl start --no-block` 发送进程成功退出及输出排空，再持久化原发送端退出见证。描述符尚未派发时，原操作还需核对 systemd 的任务和 PID 均为零、unit 停止、broker 已退出及原网络效果符合恢复证据。发送端退出见证缺失或任务未结束时保留原待核验。见[启动已派发且描述符未交付的原操作恢复](../architecture/server-module-evidence/Proxy-Safety.md#启动已派发且描述符未交付的原操作恢复)。

原检查点为 `DescriptorDispatched` 时，仍运行的候选受原阶段、进程、配置和描述符门禁约束。原接收 PID 与 broker 均已退出，且原操作尚未进入 DNS 派发时，Helper 可以凭启动发送端与规则见证、systemd 任务归零、设备与专用路由缺席及原管理选路，按原操作清理。描述符交付结果仍未知时也不重发。见[描述符派发未确认且原进程已退出](../architecture/server-module-evidence/Proxy-Safety.md#描述符派发未确认且原进程已退出)。

候选分流 DNS 的三个原发送进程均成功退出并排空输出后，Helper 持久化对应 `DnsDispatched` 的发送端见证。三条调用均已派发且候选退出时，原规则清理需要该见证、原系统总线及 resolver owner、原链路 DNS 和域缺席、其他 DNS 设置吻合。读取 DNS 设置前后均核对解析器身份。见证缺失、解析器换主或设置漂移时保留待核验。见[DNS 派发后候选已退出的原解析器核验](../architecture/server-module-evidence/Proxy-Safety.md#dns-派发后候选已退出的原解析器核验)。

新发布逐条保存 DNS 变更的派发及发送端退出见证。原候选已退出且原解析器与网络证据相符时，连续已确认的前序调用及后续派发记录缺席，可证明部分变更已结束。已派发但缺少退出见证的调用仍保留原待核验。Helper 不补发剩余 DNS 变更。见[分流 DNS 部分变更的原证据](../architecture/server-module-evidence/Proxy-Safety.md#分流-dns-部分变更的原证据)。

候选仍运行且只有已确认的 DNS 前序调用时，原 Helper 核对原 broker 已退出、进程和描述符身份、磁盘与运行配置、管理选路，以及原解析器上与前序调用精确对应的设置。通过后持久化原停止派发，按 pidfd 停止原候选。再次进入恢复只核对停止结果和原规则清理。见[分流 DNS 部分变更时停止原候选](../architecture/server-module-evidence/Proxy-Safety.md#分流-dns-部分变更时停止原候选)。

新发布在描述符交付前将原解析器基线绑定到候选进程和原网络检查点。恢复时若描述符已交付而 DNS 尚未派发，可用这份原基线核对解析器，并把同一基线写入原停止派发记录，供进程退出后的核验使用。缺少原基线或当前设置漂移仍保持待核验。见[DNS 派发前停止已接收描述符的候选](../architecture/server-module-evidence/Proxy-Safety.md#dns-派发前停止已接收描述符的候选)。

TUN 配置事务把网络意图摘要保留在原终态回执中。发布或补偿完成后，Helper 替换主台账并同步其目录，随后才返回终态。同步失败返回原 ID 的待核验。查询和后续写入在使用这一主台账快照前也同步目录，同步失败时阻止继续。同步恢复后沿用原回执，Server 仍核对原操作、配置身份及成功见证。见[原终态替换后的目录同步](../architecture/server-module-evidence/Proxy-Safety.md#原终态替换后的目录同步)。

当前安装/任务证据 HTTP 与 Helper IPC 快照必须显式包含 `commitWitness`（IPC 使用 DTO 属性名）；允许 null，但拒绝省略、重复或身份错配。保存的历史观察不回填证明。查询 sing-box 成功原操作时，在安装锁内重新核对程序文件摘要、原生版本、自有服务、配置摘要和当前状态；其他操作造成修订变化或实际文件漂移不能借用旧证明报告恢复成功。Mihomo 沿用其程序/服务核验并核对保留配置摘要。

本地提交、卸载、配置与物理观察专项共 225 项通过；实际执行中断和补偿故障仍须单独验收，MR-02 与 CC-03 不因此收口。

> 状态：MR-01 实现项已收口，MR-02 已有真实跨版本与协议验收。已落地公共契约、计划准入、Helper 保护的签名目录发现、受限归档/制品准备、持久归档切换/恢复组件、归档安装下的已发布配置发布与服务控制、待恢复事务的人工恢复入口（重试/紧急停止/关账）、准备阶段的下载取消、宿主依赖预检（平台/服务宿主/磁盘/受管目录/IPv6）、显式宿主下载代理与有界自动重试、声明式原生格式范围（`sing-box-v1`，只开放实测过的稳定版区间），以及客户端版本计划界面、宿主环境面板与卸载/数据保留。Debian 13 x64 上用真实 1.13.21/1.14.3 归档走通安装、跨版本更新、双向协议互通、回退与卸载。
> 更新：2026-10-24（组件中心入口设计；领域实现证据日期保持原记录）。
> 适用：管理宿主机软件或服务的内置应用。Android 的界面、支持范围和实现文档继续由 `Client/RelaxKonOS.Client.Android/docs/` 维护。

## 1. 目标与范围

管理员应能在应用内检查依赖、选择可信版本、安装、更新、修复、回滚及卸载受管软件，并知道数据是否保留、哪些服务会中断、失败后如何恢复。新增上游版本和受信任发布源不应每次都要求重新编译 RelaxKonOS。

内置管理应用随 Client/Server 发布；它管理的第三方运行时具有独立版本和安装生命周期。卸载 sing-box 不删除代理服务端应用入口，更新 Nginx 不等于更新 RelaxKonOS。没有独立运行时的应用不人为增加安装、卸载按钮。

统一管理元数据、信任、下载、操作记录和事务规则；各运行时仍拥有自己的原生配置生成器、服务控制和健康检查适配器。Docker、Squid 等软件包安装不能被伪装成解压一个二进制文件。

## 2. 统一组件中心与应用入口

目标产品入口统一为[组件中心](RelaxKonOS.ComponentCenter.Design.md)：集中安装、卸载、版本选择、更新、修复、回退及发布源管理。各应用仅提供依赖状态和“在组件中心管理”跳转，不再分别维护完整安装页面；业务配置、发布、服务启停和业务日志仍由领域应用负责。默认保留可发现的应用入口，未安装时统一提示并定位组件中心；可选择仅显示已就绪应用。

桌面 sing-box 生命周期入口已迁入组件中心，代理服务端保留管理跳转；其他应用及 Android 的入口集中尚未完成，由 CC-01～CC-06 追踪。MR 各项仍跟踪领域生命周期，CC 跟踪入口集中，不改变现有验收结论。组件中心至少包含：

| 内容 | 要求 |
| --- | --- |
| 当前状态 | 未安装、外部安装、受管安装、运行中、已停止、更新中、故障、待恢复；状态未知不得显示未安装 |
| 当前版本 | 实测版本、目标版本、安装来源、平台/架构、安装模式、活动修订、可回退版本 |
| 可用版本 | 稳定版默认；明确选择后才显示预发布版；显示可信状态、配置支持状态、阻止原因及发布时间 |
| 安装 | 选择来源与版本，预检依赖、磁盘、权限、服务和端口；不能只有写死默认版本的按钮 |
| 更新 | 刷新版本目录、比较当前和目标版本、预览配置变化及重启影响，再提交明确目标版本 |
| 修复 | 验证或重新安装同版本；同版本重装不得报告为“已升级” |
| 回滚 | 只显示可恢复且允许使用的版本；说明配置、数据格式和凭据撤销约束 |
| 卸载 | 默认保留业务数据；删除数据单独确认；列出服务、程序、证书副本及自有防火墙规则的处理范围 |
| 操作记录 | 下载、校验、安装、配置验证、切换、健康检查、补偿阶段，以及可恢复的操作 ID |

未安装时仍能看到安装与发布源入口。能力或权限不足时给出原因；状态查询失败时禁止根据空状态执行覆盖安装。已有外部实例先展示所有权，不能自动接管、更新或卸载。开机启动与当前是否运行分别显示。

## 3. 发布源与信任

“信任 github.com”或“使用 Nginx”不能构成完整信任策略。源必须限定发布者、仓库或站点根路径、元数据格式、签名/摘要依据及允许的下载位置。Nginx 可以托管发布清单和归档；Nginx 自身的发行源则是另一项具体运行时来源。

| 源适配器 | 发现方式 | 信任边界 |
| --- | --- | --- |
| GitHub Releases | 固定发布者/仓库的结构化 Releases API、tag 和资源列表 | 固定仓库及允许的资源规则；发布资源仍须验证；不自动信任其他仓库、源码附件或整个 GitHub 域名 |
| HTTPS 发布清单 | Nginx 等静态站点提供版本化、签名的结构化清单 | 固定源 ID、HTTPS 基址、签名公钥及资源位置约束；不把 autoindex/HTML 目录当作可信目录 |
| 官方下载站 | 明确的运行时专属解析适配器，或转换为可信发布清单 | 只解析声明的页面/格式；仅 HTTPS 和同站摘要不足以证明独立发布者身份 |
| 系统软件仓库 | OS 包管理器的签名仓库元数据、发行版及包版本 | 校验仓库身份、发行版、签名密钥和明确包集合；安装依赖及移除影响遵循包管理器事务 |
| 镜像与离线包 | 复用已受信任版本的制品身份 | 镜像只更换传输位置；同一制品必须与可信目录的摘要一致，不凭文件名或管理员上传直接授信 |

源配置最少包括 `sourceId`、类型、发布者/仓库或基址、启用状态、信任公钥/摘要依据、通道、元数据大小与期限、允许的重定向目标，以及可选受保护认证引用。认证信息不进入普通 DTO、客户端日志或审计。

信任配置持久化在 Server 的受保护宿主范围，记录修订与操作者。客户端提交的 URL、公钥或摘要不是 Helper 的独立信任依据；信任配置变更必须先经过获准的宿主管理操作，在特权边界同步受保护的信任修订，再允许安装。

首次加入源必须展示身份、地址和信任依据并明确确认。应用更新不能静默扩大源、替换公钥或跨发布者信任。密钥轮换需要已有可信密钥签署或管理员重新确认。源被禁用/撤销后停止从中接受新安装，不自动删除正在运行的软件。

同一标签的资源摘要发生变化时标记冲突，不覆盖已确认制品；管理员须重新审查。撤销的制品不能作为安装或回滚目标。清单具有签名、修订、有效期和已接受高水位，拒绝过期与重放；过期离线目录只用于显示，安装需重新取得有效信任依据。

## 4. 多版本目录与配置能力

版本发现与安装许可分离。发现新版本后，按以下独立维度展示：

- **来源可信度**：未确认、已确认、已撤销。
- **目标支持**：OS、架构、发行版、libc/最低系统要求、安装模式和服务宿主是否匹配。
- **配置能力**：原生格式、构建特性、协议组合和本应用当前生成器是否支持。
- **验证证据**：未验证、仅原生配置校验、指定平台/客户端互通通过。

版本记录使用不透明 `releaseId`，并包含 `runtimeId`、原始版本、排序版本、通道、`sourceId`、制品 ID、平台/架构、归档格式、长度上限、SHA-256、签名依据、可执行入口、配置能力标识及目录修订。审计保留实际安装的不可变制品身份，不能只记录 `latest`。

上游版本按运行时自己的规则解析。保留原始 tag，拒绝含糊的平台映射；语义版本、发行版修订和日期构建不能混作字符串排序。预发布版默认排除，未知命名的资源展示为不支持，不能猜测后执行。

一个运行时可以声明多个明确测试过的配置能力范围；范围外版本可以被发现和展示，但不得自动安装或宣称能管理。范围内版本安装前也须用目标二进制执行配置校验和必需特性探测。版本号不能替代构建特性证据。

sing-box 当前只声明一个能力 `sing-box-v1`：`1.13.0 ≤ v < 1.15.0` 的稳定版（不含预发布）。声明在 `Shared/RelaxKonOS.ManagedRuntimes/SingBoxRuntimeSupport.cs`，目录条目与 Helper 前置检查共用同一判定，目录发布者无法自行声明支持；范围内的新补丁版本无需改动声明即可被发现和安装，但每次安装仍执行目标二进制的 version/check 与构建特性探测。范围之外（更旧、`1.15` 及以后、任何预发布）只展示为不支持。

支持第三方软件的多个版本，不代表保留 RelaxKonOS 旧 API 或旧包契约。Shared Protocol、客户端、Server、Helper 和清单使用唯一当前契约，变更时同步更新全部调用者，不增加旧路由、双格式解析或迁移适配。业务配置保持当前结构化模型；按明确能力生成目标原生配置，不静默丢字段或靠反复试旧格式取得成功。

## 5. 生命周期事务

所有变更绑定受管安装 ID、目标主机、预期状态修订、目录/信任修订、明确的 `releaseId` 和幂等键。刷新目录不触发安装；提交后目录中的 `latest` 变化也不能改变已受理目标。

安装、更新、修复、配置发布、卸载和回滚按运行时/实例共用串行锁。下载阶段可取消；切换阶段按事务边界处理取消，不能因客户端关闭就丢失后台操作。取消按请求传递而不是中断连接：Server 记录请求、由主事务在切换点之前自行中止，因此客户端断开或取消都不会留下半途的后台操作（见 [下载取消](RelaxKonOS.ManagedRuntimes.Transactions.md#下载取消)）。

1. 核对权限、所有权、源信任、目标能力、依赖和变更影响；冻结目标制品及操作计划。依赖由主机实测（见[宿主依赖预检](RelaxKonOS.ManagedRuntimes.Transactions.md#宿主依赖预检)），签名清单不能声明它。
2. 在受保护暂存区下载，限制正文总时间、大小、重定向和解压后大小/数量；处理重试及限流。
3. 验证签名/摘要，拒绝路径穿越、链接逃逸、重复入口和非声明文件；验证原生版本及必需构建特性。
4. 用目标版本生成并校验现行业务配置；检查当前已撤销/过期凭据、证书、端口和依赖，不夹带无关未发布草稿。配置决定的那部分依赖（例如 IPv6 监听对应的内核特性）与发布无关的依赖在同一次前置检查里一起测量。
5. 持久化事务、旧配置、原服务与开机启动状态；归档安装使用不可变版本目录和原子活动指针，包管理器安装使用专属事务适配。
6. 切换程序和配置，按实际需要受控重启；核对版本、配置身份、监听和应用特定健康检查，再提交成功状态。
7. 失败补偿程序、配置、服务状态及本次自有规则；补偿不完整进入待恢复状态，并保留重试、紧急停止与操作员关账入口（见 [人工恢复入口](RelaxKonOS.ManagedRuntimes.Transactions.md#人工恢复入口)）。

启动时重放事务核对，不重复生成秘密、不以进程存活代替生效确认。回滚必须重新核对当前授权、证书和数据格式；不能恢复已撤销凭据。包管理器无法保证旧包或数据回退时，明确报告不支持回滚，不提供虚假的通用回滚按钮。

卸载只处理可证明属于该安装的资源，禁止全局 purge/autoremove 或删除用户自有服务。保留数据重装必须保留保护密钥和引用；删除数据要明确处理备份、缓存及秘密，不影响其他运行时。

## 6. Server、Helper 与下载边界

Server 提供目录、预检、计划和操作查询；客户端只选择结构化对象，不提交 shell、任意可执行路径或原生安装参数。统一操作台复用现有安装与操作记录基础设施，避免每个应用新造一套进度/恢复机制。

Helper 依据自身受保护的信任配置，复核已授权源与不可变制品身份、受管路径、摘要、目标平台和固定动作。Server 下载后可向 Helper 流式交付或暂存归档，但 Helper 必须再次验证内容，不能直接信任 Server 传入的临时文件名。

安装下载可显式使用宿主下载代理设置；内核运行的出站代理是另一个配置项，不自动继承安装下载环境。下载代理认证不得被转发到资源服务器。凭据只发送到声明的 API/下载源，跨域重定向不携带 Authorization。

已落地：Server 从宿主下载代理偏好解析出结构化路由，只在取字节的操作（目录刷新、运行时执行）上转给 Helper；Helper 再次校验后才连接，路由不进冻结计划、幂等摘要或台账，切换路由不会作废已受理安装。偏好已启用但不可用时返回 `runtime.download_proxy_unavailable`，不静默退回直连。代理凭据只用于代理连接（407 挑战），源站请求只带 `User-Agent`；`noProxy` 按主机名边界与端口匹配，命中即直连。直连仍逐跳校验 DNS 与连接地址；隧道模式下代理解析并连接，因此改为在开连前拒绝目标里的字面回环/私网/元数据地址，代理端点本身是管理员声明而非请求值。传输层故障按上限自动重试（3 次尝试、500 ms→1500 ms 退避、`Retry-After` 封顶 10 s 且受总时限约束），证书不受信任、策略拒绝与 4xx 一律终态。细节见[下载路由与自动重试](RelaxKonOS.ManagedRuntimes.Transactions.md#下载路由与自动重试)。

新来源的发现和下载执行 SSRF 限制，校验 DNS 结果及实际连接地址、端口和每跳重定向，默认阻止回环、链路本地和元数据地址。内网 Nginx 镜像由管理员显式授权精确地址/网段和端口；该授权不开放代理流量访问管理网络，也不绕过制品签名与摘要校验。

GitHub API 配额耗尽、网络断开、清单过期、摘要变化、目标不支持和原生校验失败分别返回稳定问题码。可显示缓存目录及其新鲜度；不能把无法刷新报告成“当前已是最新版”。

## 7. 当前盘点与补齐任务

以下是截至 2026-10-20 的源码盘点，不替代每个平台的验收。表中“计划中”均指本文要求的补齐，不能作为已交付功能展示。

| 任务 | 应用/组件 | 已有基础 | 必须补齐 | 状态 |
| --- | --- | --- | --- | --- |
| MR-01 | 公共基础设施 | 公共契约/计划准入；来源账本/签名目录；Server 查询/刷新；受限制品准备；持久事务；严格 SemVer；新鲜授权通道、完整冻结记录复核、私有锁和固定配置路径共锁；安装台账、公开计划和实测阶段进度；归档配置发布、卸载/数据保留、人工恢复入口、下载取消、宿主依赖预检、显式宿主下载代理与有界自动重试、声明式原生格式范围 | 其他源类型、包管理器依赖（需包管理器适配器） | 已收口；Debian 13 x64 常规 614 项 + systemd 12 项通过，详见事务证据；下载路由与重试另由 60 项专项检查覆盖 |
| MR-02 | 代理服务端 / sing-box | Android 接入签名来源/目录、精确版本冻结计划、修复/版本回滚/卸载、公共提权台账与事务恢复（设备及真实主机验收另行执行）； 真实归档/原生命令适配器；Windows SCM 与 Linux systemd 固定宿主；Helper 生命周期组合、Server 新鲜授权提供者及门禁；1.14.3 Windows x64 真实归档检查；受管安装的已发布配置发布、启停、开机启动与配置回滚；待恢复事务的重试/紧急停止/关账入口；准备阶段下载取消及客户端取消按钮；客户端版本计划界面（含阻止原因、待恢复与刷新失败）及卸载/数据保留二选一；声明式原生格式范围与真实跨版本/协议验收；组件中心桌面 TaskPlan 冻结预览、生命周期规划与内部审批组合、冻结复核、实时策略/期限与派发 journal/原键查询、只读结果对账及逐阶段任务授权桥与显式安装工作线程与只读 NoOp/依赖推进及持久取消投递/未受理关闭屏障及审批/TaskRun HTTP/严格桌面传输客户端及桌面统一审批/执行、持久任务引用和显式继续、显式恢复结果核对与保留原证据的审计（任务专用恢复授权/补偿协调已接入；桌面历史宿主证据可读；真实桌面宿主验收待完成） | 其他协议互通矩阵（Hysteria2/Trojan/WebSocket）、Windows 端到端 | 已完成（Linux x64 证据）：真实 1.13.21→1.14.3 更新、双向 VLESS/REALITY 互通、回退与卸载通过 24 项验收，详见事务证据 |
| MR-03 | 代理管理器 / Mihomo | 原有固定清单安装；公共签名目录/gzip/`mihomo-v1`；Helper 准备/切换/移除、数据修订/保留重装、原生沙箱、Linux 固定宿主；运行时绑定授权与闭合入口、Server 加密发布权威/新鲜授权；有界 YAML/资源路径门禁；共享配置持久事务及 Mihomo 数据快照、专属授权/命名恢复；Server 配置待发布授权/确认补偿与中断对账编排、默认 DI/启动恢复及写入共锁隔离；首次安装候选/发布确认与安装 Pending 恢复编排；卸载发布对账、保留材料重装与匹配镜像清理；观察/计划 HTTP 与实时归属/完整性准入；受管档案保存/激活、设置与宿主设置意图持久提交/启动对账；活动档案选择的加密意图与 SQLite 原子操作标记/启动对账；明确计划的安装台账适配/命名恢复与真实多动作消息丢失对账（具体类型 DI，生产注册未切换）；卸载冻结设置/所有权感知宿主清理/启动提交恢复与公开安装/恢复状态投影；具备权限/提权门禁的命名恢复查询、重试、紧急停止与关账 HTTP；受控 GEO 资源预备（Helper 按编译期摘要表放置包内资产，离线、无路径入参）；生产台账注册切换（台账只受理冻结计划，旧固定清单安装器/请求/成员/下载路由删除，代理状态投影 Helper 实测事实）与桌面/Android 运行时界面（版本目录、计划、执行、取消、恢复） | 资源档案（provider/凭据）支持、Android 设备端到端验收、其他发行源资源分发、受管 TUN 整模块交接与独立断电验收、Windows 端到端 | 实施中；2026-10-19 Debian 13 x64 常规 1269 项（配置核心 90、Server 配置发布事务 85、SQLite 档案选择 19、安装发布/台账组合 86、卸载发布/宿主组合 92、HTTP/恢复观察 41、GEO 资源 11、Helper/Server 集成 41、默认 DI 5）及真实归档/Helper+Server/systemd/HTTP/沙箱 167 项通过；设置宿主代理副作用使用记录适配器，卸载宿主检查使用真实所有权算法及隔离内存后端，独立 Helper IPC/生产 HTTP 的 DIRECT 基线生命周期已通过 81 项，provider/凭据资源分发尚未完成；桌面与 Android 已按新台账提交，2026-10-20 Android 已通过完整 Debug APK/测试源码编译及 Debian 上 1566 项 JVM 检查，生产 HTTP/Helper 观察与拒绝门禁另通过 12 项，Android 发布源/目录归属与计划门禁、冻结计划确认、运行时/资源准备弹窗及恢复归属校验、运行时状态本地化见 [Android 公共安装规则](../../Client/RelaxKonOS.Client.Android/docs/features/Installations.md)及[代理页面规则](../../Client/RelaxKonOS.Client.Android/docs/features/Proxy.md)，JVM/模拟器验证不替代真实宿主取消与恢复验收，独立 Helper 资源载荷修复后常规检查为 1274 项（GEO 16），设备交互仍未验收；Linux 无 provider/TUN 的归档生命周期已实测，不能扩大为全部配置支持。见[应用进度](../applications/RelaxKonOS.ProxyManager.Design.md#受管运行时公共目录接入进度mr-032026-10-20) |
| MR-04 | 隧道 / frpc、frps | 发布清单、受管/外部区分，部分安装及回滚接口；组件中心独立 frpc 程序观测及 frps 服务观测；Android 组件中心已承接安装、升级、修复、回退与卸载（经领域安装台账），应用内维护编辑器已删除 | 两个组件分别报告生命周期能力，统一版本选择、源信任、更新和卸载验收 | 实施中（入口已集中；受管运行时的版本目录/冻结计划待补齐） |
| MR-05 | Web Server / Nginx | 领域服务与配置管理；组件中心 Linux 包来源与依赖摘要冻结、首次安装台账/明确确认/提权/进度与重开观察接线；Windows 单一实例只读状态映射；Android 组件中心承接安装、文件安装与受管卸载（领域台账），应用内安装/卸载入口已删除 | Linux 包依赖完整冻结、持久回执与失败恢复；Windows 可信归档目录与事务；桌面执行与两平台实机验收，详见[组件进度](RelaxKonOS.ComponentCenter.Progress.md) | 实施中；不能视为完整多版本生命周期 |
| MR-06 | Squid 网关 | 组件中心统一执行 Linux 系统包冻结计划，绑定原安装台账 ID 与 Helper 包回执。Ubuntu 26.04 amd64 专属根已验证固定可信 7.7 包升级、应答中断、同键及原回执。dpkg 已升级而 APT 仍在途时，Helper 终态缺失会维持原包门禁。保留数据卸载已验证原卸载回执、同键和单次 dpkg 派发。无 Squid 包的专属根已验证签名固定包的首次安装、启动、原回执及单次下载与派发 | 整机掉电和其他平台的独立实机矩阵。任意归档版本和包回退未提供 | 实施中。见[原包回执证据](../architecture/server-module-evidence/Proxy-Squid.md#固定-squid-包升级的原回执) |
| MR-07 | Docker 管理器 | Engine 检测、Linux 受约束包安装；Android 安装取消确认、按 ID 恢复归属及显式恢复动作范围见 [引擎管理](../../Client/RelaxKonOS.Client.Android/docs/features/DockerEngine.md) | Engine/Compose 等组件版本与依赖、更新/卸载能力及数据保留；外部 Desktop/Engine 明确边界 | 计划中 |
| MR-08 | 文件服务及其他宿主依赖 | 各模块独立探测与平台实现；Git 为宿主探测/调用，2026-10-25 Windows 验收手工准备的校验便携 Git 不代表受管生命周期；Android 不提供 Git 运行时管理，边界见 [移动端功能精简](../../Client/RelaxKonOS.Client.Android/docs/design/MobileSimplification.md) | 逐一盘点 Samba 等依赖；声明受管安装、仅检测或不支持，不默认拥有系统包 | 计划中 |
| MR-09 | Guardian、Server、Helper | 服务器中心安装、修复和更新流程；Windows 独立宿主状态已改用当前进程启动证明契约 | 第一方信任与组件依赖；宿主更新时重新发布/验证当前格式的独立组件服务；应用页链接统一维护入口（组件中心 CC-07 聚合，部署仍归服务器中心） | 计划中；当前 Windows 状态契约不保留旧格式解析 |
| MR-10 | 无独立运行时的内置应用 | 随客户端/Server 发布 | 明确“不适用”；证书申请/续期、容器镜像更新不能冒充应用自身的安装/更新 | 计划中 |
| MR-11 | Tailscale / tailscaled | 桌面与移动控制端组件中心；Linux 官方签名 APT 来源、冻结包安装/更新/保留数据卸载；Windows 原生 MSI 事务、来源/发布者/产品/架构核验、私有台账与恢复门禁；控制/登录共用原生锁 | Windows 原生 MSI 跨版本、驱动/重启/故障恢复与 ARM64 实机验收；完整移动设备矩阵 | 实施中；实现与模拟专项不等于 Windows 原生安装验收，Linux 结果见实机报告 |

实现顺序为 MR-01 → MR-02 → MR-03/04/05 → 软件包适配和其余盘点。每项交付同时更新本表、应用设计、当前契约与所有调用者，记录支持的平台、源类型和版本范围；不因公共接口存在就把全部应用标记完成。

### MR-01 当前实现边界

`Shared/RelaxKonOS.Protocol/ManagedRuntimes/` 定义唯一当前公共契约。来源信任、目标平台、配置支持和验证证据分别建模；计划请求携带宿主/安装身份、状态/目录/信任修订、明确 releaseId 和幂等键。`RuntimePlanning` 是供后续 Server/Helper 集成复用的纯准入校验，不是可执行授权，也不接受 URL、shell 或安装参数。

当前已检查未知/外部所有权、待恢复状态、修订冲突、目录过期/重放、来源禁用/撤销、重复 releaseId、制品信任/摘要元数据、精确平台匹配、配置支持及预发布显式选择。计划冻结明确制品；更新/回滚拒绝当前 releaseId，同版本修复必须选择当前 releaseId。卸载默认保留数据，并允许在来源失效时继续提出自有安装的卸载计划。

调用方必须在实例串行锁内提供受保护快照。`SignatureVerified` 只能由可信适配器赋值，不能从客户端取得；计划校验函数本身不验证签名、不持久化高水位、不实现幂等记录。`Shared/RelaxKonOS.ManagedRuntimes/` 已补充独立的 ECDSA P-256 签名清单适配器和原子来源账本；Helper 读取自身受保护配置，Server 只选择已登记 sourceId 查询或刷新，不能通过此边界建立新信任。来源确认、密钥变更、禁用记录操作者和修订，保留目录高水位、制品冲突和撤销。HTTPS 下载限制大小、总时限、每跳 DNS/连接地址和重定向；内网镜像由精确私网/端口授权控制。

本机管理员命令、清单格式、Server 路由及准确实现边界见 [签名目录与本机信任配置](RelaxKonOS.ManagedRuntimes.Manifest.md)。宿主导入代理与有界自动重试已接通（见[下载路由与自动重试](RelaxKonOS.ManagedRuntimes.Transactions.md#下载路由与自动重试)）：路由由 Server 从宿主偏好解析、只在取字节的操作上传递、Helper 复校验，不进计划/台账；传输层故障有上限重试，证书拒绝与策略拒绝终态。未完成的只剩网页端发布源管理与其他源类型（GitHub Releases 适配器、包管理器依赖）。公共目录现在把声明范围内（`sing-box-v1`）的稳定版标为配置支持，范围外仍为不支持；平台/架构、服务宿主和入口文件名仍在 Helper 前置检查里逐项核对。sing-box 目录已按其明确的 SemVer/v 标签规则排序并拒绝预发布通道误标；未知名称只显示为不支持，不把其他软件的日期/发行版版本混入此排序。sing-box 的目录安装、跨版本更新、回退与事务补偿已按真实归档验收（见[事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md#真实-sing-box-跨版本与协议验收)）；其他运行时的版本策略与适配器仍需逐项接入。归档安装的配置发布与服务控制、卸载/数据保留、待恢复事务的人工恢复入口、准备阶段的下载取消、宿主依赖预检均已接通。系统包依赖要等包管理器适配器，当前词表不包含它，也不会假装探测过。发布源网页管理与 GitHub Releases 等源类型仍未完成；下载代理与自动重试已由本节前述实现取代旧的“未完成”表述。

`RuntimeArtifact.Layout` 现在强制携带签名文件清单、解压总量和条目上限，不保留缺少布局的旧格式。`RuntimeArchiveExtractor` 校验归档摘要后，只向宿主保护暂存根下的新随机目录写入已声明文件；逐文件核对长度和摘要，拒绝链接、设备、重复/歧义路径、未声明文件及膨胀超限。`RuntimeArtifactPreparer` 从保护账本解析已允许的明确目标，流式限制下载，完成解包后再次核对信任与目录修订，失败/取消清理本次资源。这些是公共组件，已由 sing-box 的 Helper 安装适配器使用；它们本身不执行任意二进制，也不注册服务。

`RuntimeArchiveSwitch` / `RuntimeArchiveStateStore` 已实现特权侧原子活动指针、阶段检查点、幂等回执、取消边界和补偿核对；复用 Server 安装 operationId 与阶段，不新增用户可见台账。程序修订和当前发布配置身份分开保存，捕获漂移会拒绝覆盖；恢复重新核对授权和服务/配置/启动状态，不完整则保留待恢复。实现边界及适配器职责见 [归档事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md)。这些组件现已用于真实 sing-box 服务（Linux systemd），包管理器/卸载不能复用归档切换冒充实现。

当前固定版本 sing-box 路径也已接入原生版本、OS/架构与构建标签解析；配置检查在原生 check 前核对 REALITY/Hysteria2 所需特性。特权子进程使用固定可信环境，超限输出拒绝而非截断后误判，原生诊断不进入错误消息。此门禁不意味着公共目录中的任何版本已被授权安装，也不扩大配置能力或互通支持范围。

`SingBoxArchiveAdapter` 已有真实文件/原生命令实现；Windows 固定 SCM 宿主与 Linux 固定 systemd 宿主均已实现进程、实际加载配置和监听归属核对。Windows 状态使用进程启动时捕获的应用身份；Linux 核对 `/proc` 程序、参数和 systemd 本次运行的配置凭据，不能用新注册元数据给旧进程授予新证明。Linux 监听按进程文件描述符中的 socket inode 关联 TCP/UDP 表，前后核对进程启动时间。Helper 固定生命周期组合、Server 新鲜授权提供者、每操作认证通道和既有配置路径共锁已落地；冻结版本的完整记录与保护账本比较，不能伪造布局或验证证据。安装操作台账与公开计划入口已接通；受管活动修订的已发布配置发布、启停、开机启动和配置回滚也已接通（见 [事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md#受管安装的配置发布与服务控制)），卸载按保留/删除数据两种语义走独立归档移除事务（见 [事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md#卸载与数据保留)），补偿失败后的重试、紧急停止与操作员关账走独立恢复入口（见 [事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md#人工恢复入口)），仍没有开放新生产安装功能。可选 Windows 真实归档测试仍为既有 12 项证据；Linux systemd 检查使用受控测试程序。跨版本与协议验收由 Linux 上的真实 sing-box 检查单独提供（24 项，见[事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md#真实-sing-box-跨版本与协议验收)）。详情见 [事务组件](RelaxKonOS.ManagedRuntimes.Transactions.md)。

验证命令：`dotnet run --project Tests/ManagedRuntimes/RelaxKonOS.ManagedRuntimes.Tests.csproj`。2026-10-20 在 Debian 13 x64 上通过 **1344 项**常规检查（逐组：准入 33、签名/账本 76、Helper/Server 集成 41、归档 78、制品准备 24、归档事务 97、版本/原生输出 102、Mihomo 数据修订 11、YAML/配置策略 34、子进程 4、Linux 监听 18、授权 31、类型化 Mihomo 授权 34、安装授权 17、安装发布 86、卸载发布 92、发布权威 33、Server 发布事务 85、档案发布 19、默认注册 6、运行时 HTTP 46、GEO 资源 20、Mihomo 生命周期边界 38、生命周期边界 30、安装台账 49、受管服务 13、归档卸载 15、恢复入口 16、配置事务 90、取消 16、依赖预检 30、**下载路由与重试 60**）。其中下载专项以真实套接字验证：端点/旁路/重试策略的接受与拒绝、宿主偏好解析（含禁用、未含运行时下载、不可用、非法值）、Helper 只允许取字节的动词携带路由并对非法路由返回 `runtime.download_proxy_invalid`、真实 CONNECT 代理收到声明源站的隧道、失败隧道按 3 次尝试重试、旁路目标完全不经代理、代理凭据只在 407 挑战后发往代理连接、证书不受信任只握手一次即终态、隧道可达源站。受信任源经代理**成功**下载需要进程能采纳测试 CA，本机无法做该环境覆盖，因此该两条断言按 SKIP 呈现，其余 60 项全部执行。2026-10-18 在 Debian 13 x64 上通过 614 项常规检查：33 项准入、72 项签名/账本与能力范围、29 项 Helper/Server 集成、68 项归档、24 项制品准备、97 项事务、69 项版本/原生输出、4 项子进程、18 项 Linux 监听、31 项新鲜授权、30 项生命周期边界、49 项安装台账、13 项受管服务、15 项归档卸载、16 项恢复入口、16 项取消、30 项依赖预检。真实 sing-box 验收命令 `native-sing-box-runtime` 另有 24 项（真实 1.13.21/1.14.3 归档、真实 systemd 单元、双向 VLESS/REALITY 互通、回退与保留数据卸载）。显式命令 `linux-systemd-host` 另有 12 项真实 systemd 检查（安装时不启动、不启用开机启动、启停、开机启动变更不改启停、实际配置身份、真实监听健康、配置漂移拒绝、可写程序拒绝、自有注册移除及固定配置路径共锁）。客户端 `Tests/Client/RelaxKonOS.Installation.Tests` 的 38 项检查中，版本计划页 14 项覆盖非法来源不发请求、阻止原因、活动版本预选修复、无权限不提供操作、更新/计划体精确、计划被拒不入台账、卸载先确认且不带发布、缺确认不静默删除、刷新失败不冒充“已是最新”、待恢复状态禁用动作，恢复面板 7 项覆盖命名事务与补偿失败、运行中只给重试与紧急停止、重试带 operationId、停止先要提权再重试一次、实测停止后才开放关账、缺确认不静默关账、关账体带确认，取消 3 项覆盖仅当主机报告可取消时才提供按钮、取消带幂等键并立即撤下、主事务真正取消后按“已取消”而非失败呈现，依赖 3 项覆盖环境面板只呈现能力名与字节数、拒绝时点名未满足的前置条件并给出两侧数值、未测量即拒绝。Windows 上同一工程通过 33/68/28/68/24/97/68/4/27/13/31 项后，在生命周期边界的"伪造制品布局"检查处失败（基线同样失败，属既有 Windows 平台差异），因此当前以 Linux 测试机结果为准。上述检查均不执行第三方 sing-box 服务，也不证明协议互通；通用 Server 测试在 Windows 的证书 CNG 导入处失败。

## 8. 新增应用与版本支持的门禁

- 设计文档必须声明运行时清单、安装所有权、支持版本/平台、默认源、发布物验证、更新/修复/回滚/卸载语义；不适用项说明原因。
- UI 覆盖未安装、外部安装、已安装无更新、可更新、无法刷新、不支持目标和待恢复状态；三语言与主题一致。
- 自动化覆盖版本排序、平台资源解析、分页/限流、过期/重放、密钥与摘要变化、镜像重定向、内网授权、恶意归档、幂等及并发修订。
- 在每个声明支持的平台验证首次安装、实际跨版本更新、同版本修复、保留数据重装、删除数据、外部实例保护、失败补偿和中断恢复。
- 每个开放版本范围验证原生配置和服务健康；协议互通单独记录，不能用安装成功或配置检查替代。

验收证据与未完成项分开记录。新增受支持版本不以取消校验、放开任意 URL 或新增旧格式回退作为实现手段。

## 9. 与已有规范的关系

- [内置应用开发约束](../development/RelaxKonOS.BuiltInApplication.Conventions.md)：本规范为需要受管运行时的应用补充强制门禁。
- [安装组件、设置与修复](RelaxKonOS.InstallationManagement.Design.md)：继续管理 RelaxKonOS 自身安装，第三方运行时复用其操作和恢复原则。
- [代理服务端设计](../applications/RelaxKonOS.ProxyServer.Design.md)：当前固定版本实现是 MR-02 的起点；多版本及源管理以本文目标为准，不能继续用编译期唯一版本代替更新功能。

2026-10-25 桌面组件中心已补充任务专用人工恢复的权限/任务切换门禁、当前会话响应校验及服务状态/请求回执展示。恢复动作与结果核对分离；模拟桌面检查不扩大 MR-02 已有真实宿主验收范围。详见[桌面恢复验证](../platform/RelaxKonOS.ComponentCenter.Design.md#桌面人工恢复门禁与回执展示2026-10-25)。

2026-10-25 sing-box 组件任务现支持读取持久初始/恢复对账证据；客户端检查原宿主、安装及 operationId，展示历史实测版本和匹配回执。读取不执行恢复，也不扩大已验证平台/版本范围，见[组件中心历史证据](RelaxKonOS.ComponentCenter.Design.md#桌面历史宿主证据读取2026-10-25)。

2026-10-25 MR-02 新增使用生产桌面客户端与 TaskPlan 的显式专用宿主验收入口，覆盖七步归档生命周期与持久重开观察。仅工具构建和门禁已验证，尚无本次宿主执行证据，不改变 Linux/Windows 实测范围；见[入口及运行条件](../../Tests/Client/RelaxKonOS.Installation.Tests/ComponentCenter.HostAcceptance.md)。

2026-10-25 MR-02/CC-02 桌面更新候选现在要求新鲜、未过期且匹配当前选择的目录，以及可解析的受管实测版本；刷新失败撤下更新候选并显示未知，缓存仍在来源详情可读。未改变安装后端或信任能力。已取回专用测试机此前 87 项生命周期记录并单独记录来源与局限，缺少仓库提交身份，不能证明当前工作树已经实机验收，详见[组件中心更新状态及证据](RelaxKonOS.ComponentCenter.Design.md#桌面更新状态可信度2026-10-25)。

2026-10-25 后续 MR-02/CC-03 实测：当前工作树发布的桌面核心、Server 和 Helper 已在 Debian 13 x64 独立 System 实例走通生产 TaskPlan 七步生命周期（87 项），覆盖真实 1.13.21→1.14.3 更新、修复、回退、保留数据重装及删除测试数据卸载；源码和程序集摘要随证据保存。Server 实际重启后，新客户端进程通过只读入口重开原七个任务（28 项），来源禁用后再次通过，最终事实绑定原宿主与安装身份。客户端回归 381 项通过。未验证真实执行中断、取消、补偿或 Windows；不扩大原协议互通证据，详见[当前构建闭环](RelaxKonOS.ComponentCenter.Design.md#桌面当前构建真实宿主闭环2026-10-25)。

2026-10-25 MR-02/CC-03 后续取消验收：桌面修复取消与领域受理修订竞争的有限重试及权限/任务切换门禁；默认客户端回归 410 项通过。独立 Debian 13 x64 宿主通过 27 项排队取消与真实签名归档传输中取消检查，原操作 Cancelled 回执和无安装/保留数据/待恢复均核对，来源与临时环境已撤下。尚未验证切换后的晚到取消、执行中断或补偿故障，不扩大协议或平台范围，见[桌面取消证据](RelaxKonOS.ComponentCenter.Design.md#桌面取消修订竞态与真实传输取消2026-10-25)。

当前提交证明构建在 Debian 13 x64 完成桌面 TaskPlan 七步生命周期 94 项及 Server 重启后新客户端只读复核 28 项，未测试执行中断/补偿；证据与临时环境清理记录见[组件中心](RelaxKonOS.ComponentCenter.Design.md#桌面原操作成功提交证据2026-10-25)。

2026-10-25 桌面组件中心按组件切换详情运行时页，MR-03 的 mihomo 运行时（来源、版本、安装/更新/修复/回退/卸载与恢复）与 MR-02 的 sing-box 共用同一入口，未新增执行路径或权限；目录同时声明了文件服务 Samba、Squid、Docker Engine、Nginx、frpc/frps 的计划安装来源，但这些条目的 MR-04～MR-08 适配器仍未实现，界面不提供安装动作。见[多组件目录与来源声明](RelaxKonOS.ComponentCenter.Design.md#多组件目录来源声明与桌面界面重做2026-10-25)。

2026-10-25 新增按组件的通用状态/计划路由 `/api/v1/components/{componentId}/state|plan` 与适配器注册表，`ComponentState`/`ComponentPlanResult` 成为组件层归一化契约（归档运行时与系统包各由自己的适配器翻译）。首个系统包来源落地在 Nginx/Linux：特权助手新增只读 `NginxPackageQuery`，用 `apt-cache policy` 与 `apt-get --print-uris` 冻结候选版本、发布仓库身份、pool 地址与 SHA-256，并由 `NginxPackagePlan` 附加 `component.execution_unavailable` 使计划恒为不可执行。这属于 CC-05 的「源解析/冻结」部分，**不含**包管理器安装事务、仓库签名独立校验与回滚语义，也不改变 MR-01 中「包管理器依赖需适配器」的边界。见[组件通用计划路由与 Linux 包来源冻结](RelaxKonOS.ComponentCenter.Design.md#组件通用计划路由与-linux-包来源冻结2026-10-25)。

### mihomo 组件通用接口（2026-10-25）

mihomo 新增通用组件状态/计划适配，复用发布感知领域服务；与 sing-box 共用归档准入与运行时身份核对。直接组件执行保持拒绝，安装、卸载和配置仍由领域发布协调器拥有；尚未接入批量 TaskPlan。实现与验证范围见[组件实施进度](RelaxKonOS.ComponentCenter.Progress.md)。

### Squid 与 frp 分项观测（2026-10-25）

组件中心新增 Squid 状态观测，并将目录中的 frp 拆成 frpc/frps；客户端程序版本与服务端配置/运行事实分别报告。没有新增安装能力、系统包所有权或可信版本事务，详见[组件实施进度](RelaxKonOS.ComponentCenter.Progress.md)。

### nginx 冻结安装接入台账（2026-10-25）

Linux nginx 首次冻结安装从组件中心经产品提权进入统一安装台账，提供原键幂等提交、资源排他、阶段进度与重开观察。无台账 component execute 路由已删除；重启时不以当前健康推导原任务成功，返回中断待核对。Windows、包补偿及逐依赖制品证明尚未完成，详见[实施范围与验证](RelaxKonOS.ComponentCenter.Progress.md)。

## 2026-10-30 桌面 Linux 包来源选择（MR-02/03/04）

产品明确要求桌面 Linux sing-box、mihomo 使用服务器已配置的 APT 源，不自动加源，无候选包时提示；Windows 仍按既有独立平台来源处理。本轮接入通用冻结包计划与安装台账、受管更新/保留数据卸载和恢复。已有受管归档不通过 APT 自动覆盖或接管。FRP Linux 的已有可信 pin 路径补充受管更新和卸载，仍按整个 frpc/frps 包操作。此前签名归档能力不是新包路径的实机验收，MR-02/03/04 继续开放，详见[当前实施记录](RelaxKonOS.ComponentCenter.Progress.md)。

## 系统包外部生命周期与登记（2026-10-30）

产品授权对已验证的外部系统包提供明确确认后的更新、保留数据卸载和独立纳入管理；受管包经外部包管理器更新后应核验同一软件包身份并提示变化，不仅因版本/摘要变化撤销管理。此决定替代本文对系统包一律禁止外部更新/卸载的旧规则；归档、MSI 及业务配置所有权边界不变。实现与接续清单见[系统包授权设计](RelaxKonOS.PackageOwnership.Design.md)，未完成项不得视为交付。2026-10-30 状态：PO-01～PO-04 的代码与自动化检查已完成，PO-05 的真实 Linux APT 生命周期尚未执行；证据与限制见该设计的实施状态和[组件中心实施进度](RelaxKonOS.ComponentCenter.Progress.md)。

## 2026-11-03 Proxy 发布恢复所有权收尾

Core 唯一 `MihomoPublicationRecoveryScheduler` 持续扫描原持久 pending，按类型在独立 async scope 中执行配置/安装/移除恢复；busy/Helper 不可用仍重试同一意图，operator-required 身份保留并不自动重放，新操作身份可再次扫描。原冻结计划、共享 publication gate、摘要/回执核实、补偿和原生生命周期 authority 不变；不重复 prepare/install 或以当前健康认定原任务成功。sing-box 到期的 Core 窄执行器也沿用原 protected state 与 Helper 证明。MR-02/03/04 及真实 OS 服务验收不因 scope/注册检查而关闭；当前 Proxy 仍 resident，完整证据与限制见[边界设计 §19](../architecture/server-module-evidence/Proxy-Publication.md#19-proxy-常驻责任与完整执行寿命收尾2026-11-03)。

2026-11-07（MB-04 / MR-03 配置恢复）：Proxy 停用启动门禁复用原配置发布恢复入口，核对同一 operationId 的 Helper 回执和配置权威。原生发布及补偿验证见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md)。本阶段范围为已受理配置发布恢复，MR-03 安装和版本生命周期沿用各自验收记录。

2026-11-07（MB-04 / MR-03 配置恢复）：真实独立 Helper IPC 验收确认 Mihomo 同一发布 ID 可在配置前后用于只读查询。候选转发期间 Helper 中断后的补偿由完整非 root Server 按原 operationId 执行，恢复旧配置及原回执。证据见[模块边界台账](../architecture/RelaxKonOS.ServerModuleBoundaries.Design.md)。

2026-11-07（MR-03 / MB-04）已确认受管发布后的直接 TUN 拒绝路径通过 Linux 私有 systemd 验收。原具名配置补偿回执和真实流量持续，拒绝未生成新恢复标记或控制器请求。受管 TUN 网络事务仍开放。见[模块边界台账](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-已确认受管发布的真实-tun-门禁)。

2026-11-07（MR-02 / MB-04）受管 sing-box 入站发布将最近一次原生发布回执保存在原归档及实例锁中。入站台账按原 ID 恢复。原生结果仍在途时，归档切换和移除门禁保留责任。证据及待实现的在途补偿入口见[模块边界台账](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-入站发布按原-helper-回执恢复)。

2026-11-07（MR-02 / MB-04）在途入站发布可通过原 ID 显式补偿。原归档实例锁、程序核验和配置备份共同限定恢复范围。还原时保持停止，后续启动重新核验授权。此项属于入站配置事务恢复。见[模块边界台账](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-在途入站发布的原-id-补偿)。

2026-11-08（MR-02 回归）Windows Release 生命周期夹具使用 `sing-box.exe` 作为签名制品入口，使伪造归档布局和证据信任用例通过平台预检并到达 Helper 独立信任复核。专项 28 项通过，使用 Windows apphost 的 ManagedRuntimes 广跑也通过。可信下载传输探针因该主机缺少进程级 CA 覆盖而跳过。此次验证范围为本地回归夹具。

2026-11-08（MR-03 / MB-04）直连配置待恢复时，Mihomo 受管计划和发布保持原恢复门禁。既有直接安装入口在激活前复核同一检查点，下载阶段不占用 Core 安全共用发布锁。见[直连 reload 持久检查点](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-mihomo-直连-reload-持久检查点)。

2026-11-08（MR-03 / MB-04）Mihomo 运行时恢复入口按原 ID 区分 Server 直连检查点与 Helper 事务。直连查询和磁盘重试可独立于 Helper；恢复响应保留原 ID 和 `RecoveryRequired`。特权事务的停止、关账和已有版本生命周期按各自台账处理。见[直连具名恢复](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-mihomo-直连具名恢复)。

2026-11-08（MR-03 / MB-04 配置恢复）受管订阅激活在候选开始真实转发且 Helper 尚无终态时强制中断，重启按原配置事务 ID 完成具名补偿。原生只读超时报告为暂时不可用，由原发布恢复器继续核对同一 ID。该证据覆盖配置激活的中断恢复。见[受管订阅激活实机证据](../architecture/server-module-evidence/Proxy-Subscriptions.md#mb-04-受管订阅激活期间-helper-中断)。

2026-11-08（MR-03 / MB-04 配置恢复）直连档案发布已持久记录原生确认时，按原 ID 接续已有 SQLite 档案提交，完成后可读回原成功回执。未记录确认的原 reload 继续待恢复。见[直连档案提交恢复](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-mihomo-直连档案提交恢复)。

2026-11-08（MR-03 / MB-04 配置恢复）宿主代理策略一致，且已收到运行时与宿主成功应答的直连设置，可按原 ID 完成本地提交及回执。见[直连设置提交恢复](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-mihomo-直连设置提交恢复)。

2026-11-08（MR-03 / MB-04 配置恢复）Linux 直连宿主代理策略变更在原运行时确认后冻结意图，按原 ID 核对 Helper 回执及实际代理效果。guard 独立接管时保留待处理结果。见[Linux 直连宿主策略变更恢复](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-linux-直连宿主策略变更恢复)。

2026-11-08（MR-03 / MB-04 配置恢复）guard 已接管的 Linux 直连设置可经具名授权执行独立补偿。原操作保留中断及补偿 ID。应答丢失后，仅端口与日志级别的可观察变更可凭原配置和真实监听证据续接，其他未知结果保持待处理。见[Linux 直连监听显式补偿](../architecture/server-module-evidence/Proxy-Publication.md#mb-04-linux-直连监听显式补偿)。

2026-11-08（MR-03 / MB-04）Linux 私有 systemd 复核受管 Mihomo TUN 门禁，直接读取进程启动凭据摘要和内核管理路由。两次拒绝均保留原配置补偿回执和真实转发。受管网络事务仍须证明原生设备与网络效果归属。见[门禁证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-linux-tun-标记持久化及受管门禁复核)。

2026-11-08（MR-03 / MB-04）隔离夹具已验证 Mihomo v1.19.33 通过已打开的 Linux TUN 描述符转发和退出。生产 Helper 还须将设备及网络效果归属持久绑定到原运行时操作，受管配置准入维持现有门禁。见[描述符交付证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受限-mihomo-的-tun-描述符交付)。

2026-11-08（MR-03 / MB-04）共享配置事务在旧配置和服务恢复已核验并持久记录后，具名恢复只续接清理和原回执，避免再次停启已恢复的服务。专属 TUN 夹具验证按原记录核对激活与停止的中断结果。生产受管 TUN 门禁仍生效。见[双中断核对证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-tun-原生效果的双中断核对)。

2026-11-08（MR-03 / MB-04）Mihomo 配置补偿在原 Helper 台账内持久记录停止派发。停止结果不确定时先只读核对，服务仍在运行则维持待恢复，显式应急停止共用原实例锁。此规则用于受管配置补偿，受管 TUN 网络效果仍需独立归属证明。见[停止与关账](RelaxKonOS.ManagedRuntimes.Transactions.md#mihomo-停止与关账-http)。

2026-11-08（MR-03 / MB-04）Helper 程序集的 Linux 原生描述符收发已在 x64 私有 systemd 夹具验证。非持久设备由原操作独占创建，收发核对原操作 ID、对端进程和设备身份，未知结果禁止重发。测试启动器以同一 PID 将描述符交给受限 Mihomo。生产固定受管单元、持久设备及网络归属事务待接入，受管 TUN 准入门禁继续生效。见[描述符实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-helper-原生-tun-描述符交付)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证 Helper 的 TUN 设备标签、源地址和精确目标路由写入，以及按原操作 ID 只读核对。路由使用独占添加，归属冲突时夹具保留原操作和进程。原归档事务中的持久网络检查点、固定单元与规则及 DNS 归属继续开放，生产准入门禁维持。见[设备和路由归属证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-helper-tun-设备和精确路由归属)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证原归档实例锁内的 TUN 追加检查点及两次中断，半写记录与重复停止派发均拒绝。生产普通配置补偿遇到该记录时保持待核验。生产网络事务写入入口、固定单元与规则及 DNS 归属仍待接入，准入门禁继续生效。见[原归档操作检查点证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-helper-tun-原归档操作检查点)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证生产固定单元和 Helper 启动器。原操作启动派发先落盘，固定单元核对凭据、制品、进程和描述符，拒绝重复派发和无原派发的重启。生产归档事务、规则及 DNS 归属、同键回执和工作租约仍待联合验收。见[固定单元证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-mihomo-固定-tun-单元)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证归档宿主的原 TUN broker、管理选路复核和具名停止。停止绑定 pidfd 并先保存原派发，重启只读确认清理。生产归档配置授权、网络规则与 DNS 归属、同键回执和工作租约仍待联合验收，准入门禁继续生效。见[交付与具名停止证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-helper-接管-tun-交付与具名停止)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证同一配置事务按幂等键返回原待核验和终态回执。验收使用真实归档事务和 Helper 归档宿主，冻结网络意图及授权由夹具提供。生产发布授权、同键 HTTP、规则及 DNS 归属与实际工作租约仍待联合验收。见[同键回执证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-tun-归档事务同键回执)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证冻结网络意图绑定原配置计划，完整意图经加密台账和原候选授权回读。归档宿主调用仍由测试适配器完成。生产生成意图、配置事务调用、规则及 DNS 归属和同键 HTTP 及工作租约待联合验收，准入门禁继续生效。见[冻结意图证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-冻结-tun-意图授权)。

2026-11-08（MR-03 / MB-04）生产归档配置适配器已在 Linux x64 私有 systemd 验证固定 TUN 宿主调用与两次中断恢复。仍需生成生产意图并验证规则及 DNS 归属、提交后生命周期、同键 HTTP 和工作租约，公开准入与成功提交继续受门禁保护。见[生产适配器恢复证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-生产归档配置适配器的-tun-恢复)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证非特权 Server 规划器与 root Helper 使用原 Server UID 复核管理选路。仅该 UID 受影响的策略漂移保留原操作，后续从同一回执恢复。生产范围、发布入口、规则及 DNS 写入归属与提交后生命周期仍待联合验收。见[管理 UID 复核证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-server-生成-tun-意图及管理-uid-复核)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 systemd 已验证原操作专用路由表、策略规则冲突拒绝及删除派发中断后的只读清理核对。生产范围和发布入口、DNS 写入归属、提交后生命周期、同键 HTTP 及工作租约仍待联合验收。见[策略规则证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-tun-策略规则归属及删除中断)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 resolver 已验证原 TUN 接口的分流 DNS、冲突拒绝和清理，真实上游通过。DNS 写入尚未确认时的断点、resolver 换代、生产范围和发布入口、提交后生命周期及工作租约待联合验收。见[分流 DNS 证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-tun-分流-dns-归属)。

2026-11-08（MR-03 / MB-04）Linux x64 私有 resolver 已通过 DNS 写入后应答中断和 resolver 换代验收。原身份可只读确认及恢复，换代保留原待核验。生产范围及发布、成功提交后生命周期、同键 HTTP 和工作租约仍待联合验收。见[应答中断与换代证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-tun-dns-应答中断与-resolver-换代)。

2026-11-08（MR-03 / MB-04）生产 Server 规划器从 Linux 固定宿主策略冻结受管 TUN 范围，Helper 在候选激活前独立核对。原操作的恢复仍使用原归档回执、旧配置授权和原网络归属证据。成功提交后的启停、重启及移除、公开发布与实际工作租约仍需联合验收。见[宿主范围证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-宿主范围)。

2026-11-08（MR-03 / MB-04）受管 TUN 的活动归属可随原成功回执原子写入归档台账。Helper 重新核对当前网络效果，历史回执独立保留。生产成功提交和普通后续生命周期写入继续受门禁保护，需在原归属下验证启停、重启和移除。见[提交态归属证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-提交态归属)。

2026-11-08（MR-03 / MB-04）Linux Helper 已接入提交后具名停止，复用原实例锁、原归属及原生停止检查点。进程信号及规则删除应答未知时不重复派发。仍需验证具名启动、重启、移除及公开发布。见[具名停止证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-提交后具名停止)。

2026-11-08（MR-03 / MB-04）具名启动和重启在原归档锁下持久记录新执行 ID，并保留原发布归属。重启先接续原执行的停止检查点。新启动派发后只核对原执行和原生效果，证据不足时保留待核验。移除、公开发布和生产成功提交仍受门禁保护。见[具名启动与重启证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-提交后具名启动与重启)。

2026-11-08（MR-03 / MB-04）提交态 TUN 复用归档移除事务，先核对当前执行的停止及网络清理。撤销注册和清理的已派发效果按原移除 ID 对账，终态保留发布及执行归属。生产成功提交和公开发布仍受门禁保护。见[提交后移除证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-提交后移除)。

2026-11-08（MR-03 / MB-04）Server 内部 TUN 发布与 Helper 原生成功提交受 root 宿主显式许可保护。Server 只依据原提交见证及当前网络归属确认成功，原应答未知时按原 ID 恢复。客户端公开入口、独立进程 IPC 和同键 HTTP 仍待联合验收。见[Server 发布协调证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-server-发布协调)。

2026-11-08（MR-03 / MB-04）Linux 非 root Server 到独立 root Helper 的 AF_UNIX 路径覆盖发布、恢复及提交后具名启停和重启。客户端公开入口和同键 HTTP 继续开放。见[分进程实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-af_unix-发布与恢复)。

2026-11-08（MR-03 / MB-04）现有认证 TUN HTTP 路由接入受管发布及具名启停。首次发布绑定原队列 ID，宿主显式准入继续生效。同键查询依据原确认回执，副作用未知时保留原责任。见[认证 HTTP 实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http)。

2026-11-08（MR-03 / MB-04）认证 HTTP 已验证首次启用在 Helper 无终态时按原发布 ID 补偿。原队列在恢复响应交付前保留待核验阶段和准入门禁，恢复旧配置及原回执后结案。见[认证 HTTP 补偿实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http-补偿)。

2026-11-08（MR-03 / MB-04）具名停用在原 Helper 锁内持久记录新操作 ID、原发布 ID 和目标原生执行，原生停止及清理核对后确认同一回执。Server 按原队列 ID 只读核对具名启停终态，缺证据时继续待核验。见[具名启停实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http-具名启停回执)。

2026-11-08（MR-03 / MB-04）Linux 完整 Server 已验证生产 PAM/JWT、受管发布及具名停用的 SIGTERM 排空。原队列终态与 Helper 停止回执在重启后保持，重新启用沿用原配置。范围见[完整 Server 停机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-完整-server-的受管-tun-停机排空)。

2026-11-08（MR-03 / MB-04）同次 SIGTERM 验证受管 TUN、系统代理 guard 与独立 sing-box 到期的原执行排空。重启保持 Mihomo 配置身份、原停止回执、guard 归属与 sing-box 到期回执。范围见[同次停机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-与-proxy-安全责任同次停机)。

2026-11-08（MR-03 / MB-04）管理模块停用计划和禁用启动读取已确认受管 TUN 的 Helper 原见证及当前归属。独立 Helper 不可用、漂移及无 Server 队列的原生待核验均保留准入门禁。保留数据移除的判定要求原移除见证和当前缺席。范围见[模块停用门禁](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-模块停用门禁)。

2026-11-08（MR-03 / MB-04）Linux 认证管理停用经重启生效，受管 Mihomo TUN 保留原执行、配置、检查点和实际流量。Core guard 与 sing-box 到期在管理入口禁用时完成并独立排空。范围见[禁用态安全交接](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-禁用-proxy-管理入口后的安全交接)。

2026-11-08（MR-03 / MB-04）保留数据移除的 Server 恢复和 Proxy 禁用启动已在 Linux 私有 systemd 联验。Core guard 在保存的宿主代理设置为禁用时读取原移除见证和当前缺席，凭据、配置、回执或归属无法核实时保留原门禁。见[保留数据移除实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-保留数据移除与禁用启动)。

2026-11-08（MR-03 / MB-04）完整安装 HTTP 已验证保留数据移除的冻结计划、匿名拒绝和普通 PAM 账号提权拒绝。拒绝未建立安装记录或派发 Helper。2026-11-09 已在单次授权下验证真实管理员卸载、保留数据、同键在途回执及按原 ID 的启动对账。同日复验确认重启后按原 ID 读回 HTTP 成功终态、同键返回同一终态，跨 Helper 代次只派发一次卸载。见[管理员卸载重启复验](../architecture/server-module-evidence/Proxy-Safety.md#重启后原安装-http-回执与单次派发)。见[安装 HTTP 准入实机证据](../architecture/server-module-evidence/Proxy-Safety.md#mb-04-保留数据移除的安装-http-准入)。
