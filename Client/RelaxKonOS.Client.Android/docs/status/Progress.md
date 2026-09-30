# Android 当前实现状态

> 更新：2026-09-30。本文件维护当前实现事实与代码缺口；测试进度和已有验证证据统一见 [Verification](Verification.md)；详细行为见 [文档目录](../README.md)，未关闭测试见 [验收清单](Verification.md)，未实现功能见 [部署后续工作](../plans/Deployment.md) 与 [内置应用补齐计划](../plans/BuiltInParity.md)。
>
> 已移除重复修复流水账、旧环境路径、过时“待实现”步骤和已完成目标。历史完整记录可查 Git；各轮真实验证范围见 Verification，未验证项目不标成通过。

## 1. 当前功能

| 领域 | 已实现范围 | 仍缺代码的部分 |
| --- | --- | --- |
| 基础 Shell | Kotlin/Compose/Material 3，Compact/Medium/Expanded 导航、五个顶级目的地、三语/多主题、语言设置、监控/进程与服务器信息 | 逐页面平板分栏及桌面差异按 BP 计划核对；Shell 断点不代表全部页面已分栏 |
| 登录与本机安全 | 稳定 serviceId/账号键、密码与保险箱登录、两种独立保险箱、指纹/设备锁、token 刷新与注销、切换登录、动态系统徽标、debug 无锁屏明文兜底 | 设备密钥找回等尚未接入路径按独立协议设计 |
| Windows 10/11 设备密钥 | 现有配对载荷、扫码/图片/粘贴确认、P-256 Keystore、nonce 签名登录 | 不宣称覆盖 Windows Server 或所有设备找回流程 |
| 文件 | 浏览/详情/目录与文件操作、路径浏览器、流式落盘下载、缩略图/图片缓存、有界分块上传与续传、应用级进度/通知 | 普通文本编辑及完整图片/文件操作差异见 BP11/BP12 |
| 服务器中心 / AD01 | 宿主资料、SSH 主机密钥固定、SSH/SFTP/终端、隧道与稳定受管登录、部署选项、固定启动器回执读取、可选保存的 SSH 凭据（勾选后写入独立保险箱，指纹/锁屏解封；只在用户本次输入密码时询问保存）、主机列表点开直连与工作区系统页「切换主机」（工作区里唯一入口）、握手失败按原因归类上报（认证被拒/超时/端口不可达/主机名/算法/主机密钥）、主机密钥变更可在核对旧指纹后显式替换（不再是死路） | 可信首次安装执行、发布资产/校验器接入、完整维护 UI；安装按钮仍不可用；账号安全页尚未列出/清除 `VaultKind.Ssh` 记录（目前只能按主机「忘记已保存密码」）；删除主机记录不会解除该端点已固定的指纹（按端点而非按主机记录保存） |
| 应用部署 / AD02 | 四类来源、七步向导、流式归档暂存/服务器引用、日志、生命周期、修订与回滚、原操作恢复 | 新安装依赖不由登录隐式安装 |
| 模板目录 / AD03 | 可信内置目录、动态受限字段、兼容阻断、精确版本安装、实例/修订版本关联 | M4 更新说明/差异/显式版本更新（BP16）；M3 是验收任务 |
| Docker/Compose / AD04 | 资源浏览、生命周期、日志、受限导入、definitionVersion 预览、持久 Stack 操作、部分失败/重启核实、卷保护 | 宿主自定义出站代理已接入；完整引擎/资源创建编辑与受管代理联动见 BP02-M2/BP09 |
| 网站 / AD05 | Nginx 安装/接管/生命周期、通用站点增删改与版本冲突核实、确认式 HTTPS 发布、权威站点关联、持久操作恢复、带观察位置/时间的 DNS/TLS/HTTP 结果 | DNS/内网集成 M4 |
| 证书 / BP04-M1/M2 | 独立列表/详情与到期提示、ACME 预检/签发、自签名、续期/撤销/删除、站点证书状态/有效期/SAN 选择、Kestrel 部署与实际选择器核实、原键/ID 恢复、运维观察/取消 | DNS-01 当前契约不可提交 |
| FRP / BP05-M1/M2 | 受管/外部状态与探测、固定版本三来源安装/升级/修复/回滚/卸载、profile/Token/四协议隧道编辑、revision 冲突、应用/停止、实际应用版本投影、日志与同步未知请求核实；frps 完整配置/秘密替换/版本冲突、启动/停止/重启、应用 revision、日志/审计、编辑 Token 读取 | 真实 FRP/宿主/设备验收见 Verification |
| Git / AD06 | 注册既有仓库、受限 UTF-8 编辑/差异/条件保存/单文件提交/推送；引用/固定 SHA、受限 Ubuntu BuildKit 任务、镜像发布关联 | 基础分支/暂存/历史/冲突补齐 BP10/BP11；M4 模板扩展与安全产物回收 |
| 终端/脚本/守护 / AD07 | Server Hub 会话/恢复/扩展键、固定活动屏幕与有界历史、200ms 稳定期后的串行尺寸同步、应用进程内 SSH 多会话/独立草稿/重启清空及 IME 自适应输入/IO 尺寸请求、持久结构化脚本任务、Agent 工作负载管理 | SSH 原 PTY 不能跨进程恢复；平板会话双栏 BP13；完整桌面字段/动作按 BP14 核对 |
| 任务/告警/恢复 / AD08 | 按领域 ID 观察（含运行时安装）、账号隔离无秘密索引、取消与本机隐藏、诊断导出、前台通知策略、备份创建/清单/只读预检 | Android 恢复提交、卷/数据库适配器、跨安装秘密、事件完整接入与可靠后台通知 |

服务端已有无卷/无秘密定义恢复为新停机实例的路径，Android 未接入提交 UI。定义备份已验证不代表数据卷/数据库可以恢复。

## 2. BP 实现进度

> 更新：2026-09-30。只追踪实现；测试状态与执行证据独立维护在 [Verification](Verification.md#12-bp-测试进度)。未执行测试可继续下一项，缺测试不回退实现状态。

状态使用“未开始 / 进行中 / 部分实现 / 已实现 / 不实施”。BP01-M1 公共安装链路、BP02-M1 宿主自定义出站代理与 BP03-M1/M2 Nginx 与站点管理已接入，实际行为见 [Installations](../features/Installations.md) 、[OutboundProxy](../features/OutboundProxy.md) 、[Nginx](../features/Nginx.md) 与 [WebSites](../features/WebSites.md)。独立证书与站点/Kestrel 联动 BP04-M1/M2 亦已接入，行为见 [Certificates](../features/Certificates.md)。FRP 客户端/frps 与运行时 BP05-M1/M2 已接入，行为见 [Tunnels](../features/Tunnels.md)。下一项为 [BP06-M1](../plans/BuiltInParity.md#41-下一轮起点)。不把桌面/Server 已有实现记为 Android 已实现，也不把既有 Android 部分功能当作整个 BP 项完成。

| 编号 | 实现状态 | 当前证据 / 剩余实现 | 下一步 |
| --- | --- | --- | --- |
| BP00 | 已实现（差异基线） | BuiltInParity 第 2 节已建立 25 应用差异清单；本次统一排除范围与推进规则 | 各功能开工时继续逐动作核对 |
| BP01-M1 | 已实现 | 以下五项公共安装交付完成；独立服务表单随各领域接入，测试证据见 Verification | BP03-M1 |
| BP01-M1-1 | 已实现 | `Installations.kt`：六服务/动作/状态/阶段/问题码、严格 wire、typed 请求、当前路由与限时包引用 | 验证单独追踪 |
| BP01-M1-2 | 已实现 | `RelaxKonGateway/RelaxKonApi`、`InstallationRepository`、AppContainer：提交/按 ID 与活动查询/取消/服务器引用/受限手机包流式上传 | 服务表单随 BP03 等接入 |
| BP01-M1-3 | 已实现 | `InstallationRequestJournal`、OperationIndex：稳定提交/取消键、持久原 ID、未知结果留待核实；AuthSession 与 Host 提权按会话隔离，User Mode 能力门控 | 完全丢失的终态响应按当前契约保留未知 |
| BP01-M1-4 | 已实现 | OperationCenter/OperationsScreen：发现/恢复/阶段进度/取消终态查询、离页停止观察；三语文案；安装记录无虚假领域跳转 | 设备与宿主检查见 Verification |
| BP01-M1-5 | 已实现 | `features/Installations.md`、运维说明、索引及进度/验证记录同步；已完成拆分移出计划 | 验证单独追踪 |
| BP02-M1 | 已实现 | `OutboundProxy.kt`、Gateway/API、DockerRepository、OutboundProxyScreen/Labels：设置与 Docker 共用 GET/PUT/DELETE；HTTP/HTTPS/NO_PROXY、四范围、引擎/构建实际状态与 daemon/Desktop 回读、写入确认、结果不明确先刷新、会话隔离；三语与功能文档已同步 | BP03-M1；受管来源新选择/管理另归 BP02-M2/BP06；测试证据见 Verification |
| BP03-M1 | 已实现 | `WebServerManagement/WebPublishing`、Gateway/API、`WebServerRepository/RequestJournal`、`NginxViewModel/Manager`：Ubuntu APT/Windows 版本与三来源安装、发现/接管、能力门控生命周期、ACME include、受管卸载、原键/ID 恢复及会话隔离；OperationCenter 已接入 Web 操作；三语和功能文档已同步 | BP04-M1；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP03-M2 | 已实现 | `WebServerSites/WebPublishing`、Gateway/API、`WebSiteRepository/MutationJournal`、`WebSiteDraft/Manager`、Nginx ViewModel 与运维中心：静态/SPA、多绑定、反代/缓冲、TLS/IPv6、远端路径、未保存保护、删除、原版本冲突回读和同步未知结果核实；Protocol/Server/桌面调用与补偿同步采用 `expectedUpdatedAt`，三语和功能文档已同步 | BP04-M1；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP04-M1 | 已实现 | `Certificates.kt`、Gateway/API、`CertificateRepository/RequestJournal`、`CertificateDraft/ViewModel/Screen/Labels`：独立元数据、ACME 预检/签发、自签名、续期/撤销/删除、原意图摘要与原键/ID、手机/平板和三语；运维及深链接入证书领域；Server 修正创建重放身份并验证账本重开恢复 | BP04-M2；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP04-M2 | 已实现 | `CertificateUsage/Binding`、网站显式选择、详情 Kestrel 部署/实际选择器与观察时间、丢失响应原键/ID 核实；Protocol/Server/桌面客户端接入只读部署 DTO，Server 校验证书状态/有效期/SAN 并保证事实变化后原请求重放，三语与功能文档已同步 | BP05-M1；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP05-M1 | 已实现 | `Tunnels.kt`、Gateway/API、`TunnelRepository/MutationJournal`、`TunnelsViewModel/Screen/Draft/Editors/Labels`：固定版本三来源安装及原键/ID、外部检测、profile/写入式 Token/四协议隧道、revision 冲突、应用/停止、日志/待核实与运维跳转；Server 应用指纹投影和进程事件归属已修正，三语/协议/功能文档同步 | BP05-M2；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP05-M2 | 已实现 | `ManagedFrps.kt/Draft/Manager`、Gateway/API、Tunnel Repository/Journal/ViewModel：宿主配置/Token/dashboard 秘密、显式编辑读取、CAS/应用 revision、启动/停止/重启、日志/审计与待核实；Protocol/Server/桌面同步当前字段和安全 PUT，停止不再返回 connected，进程归属未知不伪报停止，三语/文档同步 | BP06-M1；基本自动化结果见 Verification；真实宿主/设备待验收 |
| BP06-M1 | 未开始 | 缺 Mihomo 运行时与配置/订阅/节点管理 | 接安装与代理基础任务流 |
| BP06-M2 | 未开始 | 缺系统代理/TUN/恢复等宿主管理 | BP06-M1 后接能力门控与恢复 |
| BP02-M2 | 未开始 | 缺受管 Mihomo 出站来源联动 | BP06 实现后联动设置与 Docker |
| BP17-M1 | 部分实现 | 安装任务随 BP01-M1、Web 生命周期/接管任务随 BP03-M1、同步站点待核实标记随 BP03-M2 接入；证书生命周期及未知请求随 BP04-M1、FRP 同步待核实随 BP05-M1 接入，frps 随 BP05-M2 接入，代理等新增领域尚未接入 | 随其余领域交付补齐 |
| BP07、BP08、BP09、BP10、BP11、BP12、BP14、BP15、BP16、BP22 | 未开始（本轮补齐） | 既有实现见第 1 节；新增动作见计划，按第二批拆分 | 不重写既有功能，按实现依赖推进 |
| BP13、BP19、BP20、BP21、BP23 | 未开始（本轮补齐） | 新增动作见计划，按第三批拆分 | 复用既有终端/SSH/引导，遵守平台与排除范围 |
| BP18 | 不实施 | 本轮排除独立注册表应用；编号保留 | BP22 直接接具体设置契约 |
| BP24 | 进行中 | 本次已建立实现/测试分离与第一批测试表；功能变更的测试代码和文档随各项更新 | 测试执行进度仅在 Verification 更新 |

每次实现交付更新对应行，注明文件/提交、已完成动作、剩余代码与下一项；后续拆分任务时替换组合行，编号保持稳定。代码缺陷关联对应 BP 编号，测试未执行不单独作为代码缺口。

## 3. 当前限制与下一步

- 下一项实现为 BP06-M1 Mihomo 安装、生命周期、配置/订阅与节点。BP03-M1/M2、BP04-M1/M2、BP05-M1/M2 已接入；剩余宿主/设备检查独立追踪，不阻止后续实现。
- Android 证书管理/部署已接入现有 API；Server 证书写入仍调用固定拒绝的 `HostPrivilegeService`，需服务端迁移至受授权 Helper 后才能形成生产写入闭环，未伪造 capability 或绕过权限。
- 公共安装链路已接入 Nginx 和 FRP 表单；其他服务表单仍随领域交付。完全丢失首次响应且已结束的任务不能从活动列表推断终态。
- 现有能力表不等于全手机首次安装到公网访问闭环通过；首次安装还缺实现，其他领域的真实宿主/设备检查见集中验收清单。
- Server 终端是有界常用 VT 文本实现，复杂全屏程序、CJK 单元格和 IME 组合仍须设备核对；平板会话双栏尚未实现。
- 登录密码被服务端拒绝不会自动删除；管理员提权凭据被明确拒绝会删除，网络/5xx 无结论保留。弱生物识别设备只能通过设备锁解封连接凭据，不能保存提权密码。debug 无锁屏兜底明文且标注未加密，release 不提供。
- 原登录“已知系统不再查询”、没有 SignalR、终端/Docker/守护待实现、创建备份按钮未开放等描述均已被当前实现取代，不保留旧结论。
- 已完成修复不再追加流水账；发生行为变化时修改对应规范和实现表，验证证据与未关闭检查统一维护在 Verification。构建方式与图标同步见 [开发发布](../development/android-release.md)。
