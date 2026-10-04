# Android 当前实现状态

使用记忆已接入统一宿主/文件提权、Android SAF 选择器、远程路径选择器和 SSH 安装包选择。“更多 → 账户与安全”可清理当前账户的非秘密默认值；具体行为见 [设置](../features/Settings.md#使用记忆)，验证证据与提供程序边界见 [Verification](Verification.md)。

登录页已支持普通 SSH 隧道：密码/私钥认证、首次与变化的主机指纹确认、独立 SSH 保险箱、测试连接、自动本地端口、保存配置与连接管理一键登录；可显式共用 Server 用户名与密码，连接保险箱只保存一份共用密码，解锁一次供 SSH 和 Server 使用；共用模式随隧道记录恢复。隧道复选框支持整行点击及换行文字垂直居中。登录内容使用适配输入法高度的纵向滚动视口，展开 SSH 配置后可滑动访问连接按钮。隧道由应用会话持有并随退出释放；HTTPS 证书信任绑定稳定连接配置身份。详见 [登录页 SSH 隧道](../features/LoginSshTunnel.md)，设备验证范围见 [Verification](Verification.md)。

宿主授权已统一：系统认证管理员/root 由 Server 动态检查资格，非文件操作不再重复输入密码；普通用户/Alias 显式认证所选管理员。账户提示只使用当前服务器的已保存账户，无候选时留空。防火墙已移除独立当前用户密码及旧请求字段，复用统一授权和一次重试；跨账户 Guardian/脚本仍需本次显式审批。验证范围见 [Verification](Verification.md)。

> 更新：2026-10-02。本文件维护当前实现事实与代码缺口；BP 级测试进度、未关闭检查与缺陷统一见 [Verification](Verification.md)；详细行为见 [文档目录](../README.md)，未实现功能见 [部署后续工作](../plans/Deployment.md) 与 [内置应用补齐计划](../plans/BuiltInParity.md)。
>
> 已移除重复修复流水账、旧环境路径、过时“待实现”步骤和已完成目标。历史完整记录可查 Git；未关闭检查与缺陷见 Verification，未验证项目不标成通过。

PN-01–10 应用内导航已实现：Docker、Mihomo、FRP、Git、Web、SMB、证书、部署详情、任务管理器和设置均提供固定分类入口；分类切换保留已访问面板，性能/进程观察按可见页控制。当前行为见 [应用内功能导航](../features/ApplicationNavigation.md)，设备证据及剩余矩阵见 [导航对照状态](PhoneNavigationParity.md)。

Mihomo 设置页已按桌面端分为 GeoData、系统设置和常规设置卡片；TUN、系统代理、Mihomo 分别编辑，日志级别和协议栈使用下拉选择，DNS 诊断默认折叠，恢复异常与紧急关闭保留在主页面。

Mihomo 概览启停按钮、各分类顶部间距已调整，隐藏页面不再累计留白。连接页独立观察、并行读取流量，展示明确空状态与协议/端点/代理链；每 3 秒刷新不再受日志或流量失败阻断。

Mihomo 节点页已改为横向分组标签、按宽度显示 1–4 列的紧凑节点网格、点击直接选择，以及闪电按钮整组测速。结果和进度在节点区显示，最多四个测速请求并发，自动组保持只读。全量 JVM 与 SM-X510 定向 UI 验证见 [Verification](Verification.md)。

Android 选择控件与标签统一垂直居中；按钮操作产生的拒绝、错误和结果未知提示改为弹窗，关闭保留草稿与未决事实。成功反馈、字段有效性、会话限制和历史诊断继续在相关内容处展示。验收范围见 [Verification](Verification.md)。

「不再提醒」已接入：结论由本机能力决定的提醒（本机无法保存密码、本机无法用指纹/锁屏解封）在弹窗内提供勾选框，勾选并关闭即静音，且在**源头**丢弃而不是渲染时隐藏；用户取消、锁定、密钥失效、网络失败、debug 明文保存提示和会话限制一律不可静音。提权对话框里的同一句解锁结论提供**同一个**勾选，但它不参与源头丢弃（那里的句子同时是「授权尚未生效」的唯一指示）；它自己的「管理员密码未保存」由用户取消指纹确认产生，因此不给勾选。偏好只记键、只影响本机，恢复入口在「更多 → 账户与安全 → 提醒」。规则见 [Shell 设计 §3.4](../design/Shell.Design.md) 与 §5.4，设备验收见 [Verification](Verification.md)。

服务器中心安装向导现支持官方目录、本地 ZIP、服务器 ZIP、自定义 HTTPS ZIP 及 SHA-256；端口、自定义目录、独立文件权限白名单、Docker 和非标准 Linux 选项均已接入结构化部署请求。完整参数对应见[维护者指南](../../../../deployment/README.md)，真实宿主和设备验证见 [Verification](Verification.md)。

## 1. 当前功能

| 领域 | 已实现范围 | 仍缺代码的部分 |
| --- | --- | --- |
| 基础 Shell | Kotlin/Compose/Material 3，Compact/Medium/Expanded 导航、五个顶级目的地、三语/多主题、语言设置、监控/进程与服务器信息 | 逐页面平板分栏及桌面差异按 BP 计划核对；Shell 断点不代表全部页面已分栏 |
| 登录与本机安全 | 稳定 serviceId/账号键、密码与保险箱登录、两种独立保险箱、指纹/设备锁、token 刷新与注销、切换登录、动态系统徽标、debug 无锁屏明文兜底 | 设备密钥找回等尚未接入路径按独立协议设计 |
| Windows 10/11 设备密钥 | 现有配对载荷、扫码/图片/粘贴确认、P-256 Keystore、nonce 签名登录 | 不宣称覆盖 Windows Server 或所有设备找回流程 |
| 文件 | 浏览/详情/目录与文件操作、路径浏览器、流式落盘下载、缩略图/图片缓存、有界分块上传与续传、单次上传及下载后台服务、应用级进度/通知（50 项传输/续传 JVM 测试通过，真机后台与锁屏待验） | BP11/BP12 已接入：筛选排序/历史、多选剪贴板与逐项结果、完整属性/POSIX 编辑及图片缩放/适配/旋转/切换；见 [文件与图片](../features/Files.md) |
| 服务器中心 / AD01 | 宿主资料、SSH 密钥核验/显式替换、可选保险箱凭据、应用进程内多会话、三来源首次安装/升级/修复/卸载、系统安装的局域网自签证书修复与确认、四页维护首页、操作记录/日志与清除、独立健康核实、BP19 转发/SFTP | 应用级安装观察、上传恢复、登录回填和恢复上版 UI；完整设备/真实宿主验收见 Verification |
| 应用部署 / AD02 | 四类来源、七步向导、完整定义编辑/原版本冲突与回执读回、流式归档暂存/服务器引用、日志、生命周期、修订与回滚、原操作恢复 | BP16 既有实例镜像/归档新修订已接入；新安装依赖不由登录隐式安装 |
| 模板目录 / AD03 | 可信内置目录、动态受限字段、兼容阻断、精确版本安装、实例/修订版本关联 | BP16 更新说明/差异/显式版本更新已接入；M3 是验收任务 |
| Docker/Compose / AD04 | 资源浏览、生命周期、日志、受限导入、definitionVersion 预览、持久 Stack 操作、部分失败/重启核实、卷保护 | BP09-M1/M2 引擎/安装/账户镜像源及完整资源动作已接入；设备/真实宿主验收见 Verification |
| 网站 / AD05 | Nginx 安装/接管/生命周期、通用站点增删改与版本冲突核实、确认式 HTTPS 发布、权威站点关联、持久操作恢复、带观察位置/时间的 DNS/TLS/HTTP 结果 | DNS/内网集成 M4 |
| 证书 / BP04-M1/M2 | 独立列表/详情与到期提示、ACME 预检/签发、自签名、续期/撤销/删除、站点证书状态/有效期/SAN 选择、Kestrel 部署与实际选择器核实、原键/ID 恢复、运维观察/取消 | DNS-01 当前契约不可提交 |
| FRP / BP05-M1/M2 | 受管/外部状态与探测、固定版本三来源安装/升级/修复/回滚/卸载、profile/Token/四协议隧道编辑、revision 冲突、应用/停止、实际应用版本投影、日志与同步未知请求核实；frps 完整配置/秘密替换/版本冲突、启动/停止/重启、应用 revision、日志/审计、编辑 Token 读取 | 真实 FRP/宿主/设备验收见 Verification |
| Mihomo / BP06-M1/M2 | 运行时安装/生命周期、配置/订阅/节点、TUN/系统代理/恢复、流量/连接/日志/DNS/GeoData | 真实宿主/设备验收见 Verification |
| SMB 文件服务 / BP08-M1 | Samba 安装/服务、受管共享/权限/路径、Samba 用户/密码、未知写入核实及安装恢复 | Windows 外部/漂移共享只读，真实宿主/设备见 Verification |
| 防火墙 / BP07-M1 | UFW 状态/启停、默认策略、规则增删改、精确提权/账号确认、快照复核和未知提交核实 | 当前 Server 其他宿主不支持，设备/真实 UFW 验收见 Verification |
| Git / AD06 | 七页职责分离、统一仓库选择、分组批量暂存、自适应提交布局、分支筛选、历史/差异详情与构建折叠面板；BP10 工作区分支/暂存/差异/历史/冲突、安装/原键恢复，BP11 共用编辑器；引用/固定 SHA、受限 Ubuntu BuildKit 任务、镜像发布关联 | 当前行为见 [Git](../features/Git.md)；M4 模板扩展与安全产物回收 |
| 终端/脚本/守护 / AD07 | Server Hub 会话/恢复/扩展键、固定活动屏幕与有界历史、200ms 稳定期后的串行尺寸同步、独立 SSH 终端、持久结构化脚本任务、Agent 工作负载管理 | BP13 双栏/草稿/搜索/工作区外观和 VT 字符单元已接入；BP14 完整守护字段/审批/回执读回与状态/日志观察已接入 |
| 任务/告警/恢复 / AD08 | 按领域 ID 观察（含运行时安装）、账号隔离无秘密索引、取消与本机隐藏、诊断导出、前台通知策略、备份创建/清单/只读预检 | Android 恢复提交、卷/数据库适配器、跨安装秘密、事件源持久重放与可靠后台通知 |

服务端已有无卷/无秘密定义恢复为新停机实例的路径，Android 未接入提交 UI。定义备份已验证不代表数据卷/数据库可以恢复。

## 2. BP 实现进度

> 更新：2026-10-01。只追踪实现；BP 级测试状态与未关闭检查独立维护在 [Verification](Verification.md#1-bp-测试进度)。未执行测试可继续下一项，缺测试不回退实现状态。

状态使用“未开始 / 进行中 / 部分实现 / 已实现 / 不实施”。BP01-M1 公共安装链路、BP02-M1 宿主自定义出站代理与 BP03-M1/M2 Nginx 与站点管理已接入，实际行为见 [Installations](../features/Installations.md) 、[OutboundProxy](../features/OutboundProxy.md) 、[Nginx](../features/Nginx.md) 与 [WebSites](../features/WebSites.md)。独立证书与站点/Kestrel 联动 BP04-M1/M2 亦已接入，行为见 [Certificates](../features/Certificates.md)。FRP 客户端/frps 与运行时 BP05-M1/M2 已接入，行为见 [Tunnels](../features/Tunnels.md)。Mihomo、受管出站代理、首批运维、UFW 防火墙、SMB 文件服务及 Docker 引擎/镜像源/资源管理已接入。BP11 共用编辑器和 BP10 Git 工作区已接入，BP12 文件与图片和 BP13 终端已接入，BP14 进程守护已接入，BP15 已接入，BP16 已接入，BP17 事件与运维中心已接入，BP19 SSH 与 SFTP 已接入，BP20 服务访问已接入，BP21 移动包方案已完成，BP22 宿主设置与应用管理、BP23 帮助已接入；本轮实现推进至 BP23，BP24 验证独立跟踪。不把桌面/Server 已有实现记为 Android 已实现，也不把既有 Android 部分功能当作整个 BP 项完成。

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
| BP06-M1 | 已实现 | `Proxy.kt/Repository/RequestJournal/ViewModel/Screen/InstallEditor/Labels`、Gateway/API、导航/运维中心：三来源安装/回滚、生命周期原键/ID、配置 CAS/YAML 应用、订阅刷新/激活、路由/Selector/延迟；Server 激活先加载健康再改活动标记；三语同步 | BP06-M2；自动化证据见 Verification，设备/宿主验收待执行 |
| BP06-M2 | 已实现 | `ProxyDiagnostics.kt/ProxyNetworkPanel.kt`、Gateway/API、Repository/Journal/ViewModel：独立 Mihomo/TUN/系统代理编辑、完整设置保留、Windows 与 Linux 登录环境/GNOME/KDE 系统代理实际能力门控、TUN/原键紧急恢复、三秒可见采样、连接关闭、100 条日志、DNS 折叠、GeoData；三语同步 | BP02-M2；手机视觉与真实 Linux 桌面/终端联网待验收 |
| BP02-M2 | 已实现 | 共用出站页受管来源选择/秘密清空/四范围保留、管理跳转与未保存保护；Server 即时重解析状态、不可用来源拒绝所选 HTTP 消费；本地代理实际请求夹具通过 | 真实宿主/设备验收见 Verification |
| BP17-M1 | 已实现（第一批聚合） | 安装/Web/站点/证书/FRP/Mihomo 原记录及待核实聚合；Nginx 未知提交、首批安装管理入口、能力门控固定告警修复目标、Mihomo 本地化详情及离页停止观察 | 后续领域交付同步 BP17；设备验收见 Verification |
| BP09-M1 | 已实现 | `DockerControl.kt/Repository/Journal/ViewModel/Screen`、Gateway/API/导航/运维：引擎启停/重启、Linux 安装与原键/ID、Windows 主机说明、账户镜像源 CRUD/选择、提交/认证重试事实复核和无正文未知标记；同步结果直接读取当前 logLines/logTruncated；三语同步 | 设备/真实宿主验收见 Verification |
| BP09-M2 | 已实现 | `DockerResources/ResourceRepository/Journal/MutationGate/ResourceViewModel/Screen/Draft`、Gateway/API/导航/运维：完整容器详情/统计/创建/重命名/生命周期、镜像拉取/删除、网络详情/创建/删除、卷创建/引用保护、归属跳转、跨同步未知/安装门禁及重试事实复核；共享网络 labels/桌面显示同步，Server 读取失败/损坏拒绝与完整镜像 ID 修正 | BP11/BP10/BP12 已接入；继续 BP16；设备/真实 Docker 验收见 Verification |
| BP08-M1 | 已实现 | `Smb.kt/Validation/Repository/MutationJournal/ViewModel/Screen`、Gateway/API/导航/运维：Samba 安装与原任务恢复、生命周期、共享 CRUD/权限/远端目录、Samba 用户/密码、精确提权与快照复核；Server 失败/损坏集合读取拒绝写入；三语同步 | BP09；真实 Samba/Windows SMB/设备验收见 Verification |
| BP07-M1 | 已实现 | `Firewall.kt/Repository/MutationJournal/ViewModel/Screen`、Gateway/API/导航/运维：UFW 状态/默认策略/规则、结构校验、一次账号确认与精确提权、提交/提权后快照复核、未知标记与显式事实采用；Server 失败规则读取返回 503；三语同步 | BP09；真实 UFW/设备与并发验收见 Verification |
| BP11 | 已实现 | `TextEditor/Repository/Dialog/ViewModel/TextEditing`、Files/Git 入口；共享 `TextFileCodec`、Files text GET/PUT/POST、Git 同一 DTO、Helper 1.5 固定有界读取：新建/另存为、编码/BOM/换行、查找替换/语法着色、CAS/冲突/未知结果、未保存关闭和配置重建保护；三语与文档同步 | BP10 已接入；设备/真实身份/文件系统检查见 Verification |
| BP10 | 已实现 | `GitWorkspace/Repository/Journal/ViewModel/Section`、Gateway/API/导航/运维：Git 检测/安装与原键/ID、分支修改、fetch/pull、暂存/取消、索引提交/推送、差异/历史/冲突编辑/继续/中止、未知结果/普通身份/安装/事实复核；共享 SHA/configVersion/diff version、Helper 1.5 普通身份冲突 I/O、历史与 Unicode 路径修正；三语与功能说明同步 | BP12 已接入；实际宿主/设备验收见 Verification |
| BP12 | 已实现 | `FileBrowserPolicy/BatchRunner/Controls/FilesViewModel/ViewerTransform/ImagePreview`、当前文件 Gateway/API：筛选排序/历史与路径、500 项多选/远端剪贴板/逐项报告/停止、属性/POSIX 权限、账户与迟到请求隔离、按账户独立暂存/有界图片缓存、缩放适配/旋转/同目录切换；沿用 BP11 编辑器与分块上传 | BP16；设备/真实文件系统与身份验收见 Verification |
| BP13 | 已实现 | `TerminalPresentation/Tools/ScreenLayout/Transcript`、Controller、当前 workspace 设置 Gateway/Repository：双栏、会话草稿/确认目标、搜索复制与本地清除、完整外观设置、修饰键、ANSI 样式/CJK 固定单元/备用屏幕/有界设备响应；行为见 [终端、脚本与守护](../features/TerminalAutomation.md) | BP16；833 JVM 例及两个 APK 构建通过，设备与真实 PTY 验证独立追踪 |
| BP14 | 已实现 | `GuardianDraft/Repository/LogObserver/LogConnection/Screen`、当前 Gateway/API、桌面 Editor、Server/Agent：完整定义与精确参数、审批归属、预检/此次回执/完整读回、动作确认/未知核实、健康/恢复状态及有界实时日志/离页停止；行为见 [进程守护](../features/Guardian.md) | BP16；861 JVM 例/两个 APK 与 22 项 Server/桌面/实际 Agent 定义专项通过；真实生命周期、跨账号审批、Hub 与设备验收见 Verification |
| BP15 | 已实现 | `PerformanceModels/Wire/Connection/Observer/MonitorPresentation/ViewModel/Screen`、`SystemProcessWire/Repository/ProcessPresentation/ManageViewModel/ProcessesScreen`、当前 Shared/Server/桌面：完整指标/静态身份/能力、真实历史/实时重连与快照降级、手机详情/平板双栏、进程筛选排序/分页/实例终止与原登录/前台隔离；严格时间工具同步修复证书/FRP/代理；行为见 [任务管理与监控](../features/TaskManager.md) | BP16；894 JVM/两个 APK、当前 .NET 采样专项通过；12 项实例终止及桌面构建证据保留，设备、真实 Linux/HTTP/Hub 验收见 Verification |
| BP16 | 已实现 | `DeploymentDefinition/Draft/Dialog`、`DeploymentRevisionSource/Dialog`、`CatalogApplicationUpdate/UpdateDialog`、Gateway/API/Repository、Shared/Server/桌面：完整定义编辑、既有实例新镜像/归档修订、精确参数、必传部署版本的原子检查与操作预留、完整预览与明确模板版本更新、成功激活/回滚真实模板绑定；模板安装显式宿主端口修正；三语同步 | Android APK/仪器 APK 构建、71 项关联 JVM 及 Server 两专项通过，见 BP24-V5；实际 Docker/数据恢复/设备待验 |
| BP17 | 已实现 | `EventAlerts/Repository/Browser/AlertPanel`、Gateway/API：完整事件/告警/动作历史与汇总、状态/等级/来源/精确类型筛选、游标/去重/500 条上限、可见首批观察、受控确认/解决/抑制/解除、完整基线复核/回执读回/未知不重发、固定 capability 目标和三语；既有领域恢复/前台策略保留 | Android 构建、23 项关联 JVM 与 Server 事件专项通过，见 BP24-V5；设备/生产通知待验，可靠后台通知仍归 AD08-M4 |
| BP19-M1 | 已实现 | `SshLocalForwards/ForwardsScreen/ForwardForegroundService`、SSH Transport/Contracts、Coordinator/AppContainer：独立固定主机密钥/手机与远端 loopback、自动/显式端口/冲突拒绝、创建/修改重启/停止/测试/移除、有界内存状态、可停止前台通知、断线关闭/无自动重连和工作区清理；受管登录独立 | Android 构建及 6 项转发 JVM 通过，见 BP24-V5；真实 SSH/前台服务/设备待验 |
| BP19-M2 | 已实现 | `SshFileTransfers/AndroidSshDocuments`、SSH Transport、`SshFilesController/Screen/BundlePicker、SshTransferForegroundService`：同宿主复制/剪切/批量删除、SAF 多文件/目录上传与 ZIP 导出、上传/下载/复制离页与后台继续及独立可取消通知、条目/层数/实际字节预算、名称/链接/来源复核、路径/历史/搜索排序、多选可见项、文本草稿确认、工作区/选择器/迟到回执隔离、停止连接和未知读取/明确事实采用；桌面逐动作表见 ServerCenter | Debug APK、Android 测试代码编译及 10 项传输 JVM 测试通过；新增后台服务的真机生命周期验收见 BP19-T6，原记录见 BP24-V5；最低支持 API 29，排序 API 兼容性缺陷已随支持范围调整消除；真实 SSH/SAF 待验，AD01 独立跟踪 |
| BP22 | 已实现 | `HostSettings` DTO/API/Repository/Journal/Screen：宿主环境/时区/名称、当前预览/应用/revision/原操作/回滚、持久未知门禁；Server 稳定错误码/权威过期终态；MobileApplicationsScreen 原生目录/版本/系统应用管理 | Android 构建、12 项关联 JVM 及 Server 宿主设置专项通过，见 BP24-V5；设备/真实宿主写入待验 |
| BP20 | 已实现 | `ExternalServiceAddresses/ServiceAccess/DeploymentServiceAccess`：实际绑定/端口/转发地址、外部选择器、会话复核、关联站点前台读取、三语提示；见 ServiceAccess | Android 构建及 6 项地址 JVM 通过，见 BP24-V5；lint 发现 ServiceAccess.kt:33 Compose 资源读取问题待修；外部浏览器/真实可达性待验 |
| BP21 | 方案已完成 | 当前桌面 manifest/package manager/安装器已调查；ApplicationPackages.Design 明确逐类执行平台、权限/版本/更新/移除，当前拒绝 .roapp 与远端桌面代理 | 不声称实现手机第三方运行时/包检查 UI；设置边界见 BP22 |
| BP23 | 已实现 | `HelpScreen/MobileFeatureCatalog` 与登录/首页/更多路由：三语连接/安装边界/任务/恢复、仅可用任务、既有指南外部链接；手机与平板路由分流 | Android 构建及 3 项目录 JVM 通过，见 BP24-V5；设备/导航/外部浏览器待验；AD01 安装观察/上传恢复/登录回填等剩余工作独立跟踪 |
| BP18 | 不实施 | 本轮排除独立注册表应用；编号保留 | BP22 直接接具体设置契约 |
| BP24 | 进行中 | 本次已建立实现/测试分离与第一批测试表；功能变更的测试代码和文档随各项更新 | lint 16 错误待修、真实宿主与设备矩阵未关闭，见 [Verification](Verification.md#1-bp-测试进度) |

每次实现交付更新对应行，注明文件/提交、已完成动作、剩余代码与下一项；后续拆分任务时替换组合行，编号保持稳定。代码缺陷关联对应 BP 编号，测试未执行不单独作为代码缺口。

## 3. 当前限制与下一步

- 手机操作便利性仍有结构缺口：8 个应用将桌面多功能页合并为单个滚动页或有限切换，任务管理器与设置另有入口分散。2026-10-02 已完成代码结构检查，PN-01–10 的具体差异、代码依据、改进目标与待执行手机复验见 [手机与桌面页面结构差异](PhoneNavigationParity.md)。本轮仅记录，尚未修改实现或完成手机验收；BP 功能“已实现”不能据此关闭交互问题。
- BP06-M1 已接入，行为见 [Mihomo 代理管理器](../features/Proxy.md)。BP06-M2 已接入。BP02-M2 和 BP17-M1 首批聚合已接入，BP07-M1 已接入，BP08-M1 已接入，BP09-M1 已接入，BP09-M2、BP11 和 BP10 已接入，BP12 已接入，BP13 已接入，BP15 已接入，BP16 应用部署及模板已接入，BP17 事件与运维中心已接入，BP19 SSH 与 SFTP 已接入，BP20 服务访问已接入，BP21 移动包方案已完成，BP22 宿主设置与应用管理、BP23 帮助已接入；本轮实现推进至 BP23，BP24 验证独立跟踪。BP03-M1/M2、BP04-M1/M2、BP05-M1/M2 已接入；剩余宿主/设备检查独立追踪，不阻止后续实现。
- Android 证书管理/部署已接入现有 API；Server 证书写入仍调用固定拒绝的 `HostPrivilegeService`，需服务端迁移至受授权 Helper 后才能形成生产写入闭环，未伪造 capability 或绕过权限。
- 公共安装链路已接入 Nginx、FRP 和 Mihomo 表单；其他服务表单仍随领域交付。完全丢失首次响应且已结束的任务不能从活动列表推断终态。
- 现有能力表不等于全手机首次安装到公网访问闭环通过；首次安装已接入，应用级观察/上传恢复/登录回填仍待实现，其他领域的真实宿主/设备检查见集中验收清单。
- Server 终端支持原生 VT 字符单元屏幕与 xterm.js，保留 BP13 平板会话双栏；复杂全屏程序、CJK 字体和 IME 组合仍须设备核对。
- SSH 工作区已提供设置页，复用应用语言、主题及高对比度；原生终端（原 Server 文本模式）与 APK 内置 xterm.js 6.0.0 / addon-fit 0.11.0 可持久切换，并同时作用于 SSH 与 Server Hub。切换保留当前 PTY、SSH 草稿与输出；工作区按 600/840dp 使用底栏/紧凑侧栏/带文字侧栏，平板终端键盘展开保留侧栏。原始 VT 在进程内累计用于渲染器恢复，大量输出的有界状态保存仍需优化；实体宿主与完整设置切换验收见 Verification。
- 登录密码被服务端拒绝不会自动删除；管理员提权凭据被明确拒绝会删除，网络/5xx 无结论保留。弱生物识别设备只能通过设备锁解封连接凭据，不能保存提权密码。debug 无锁屏兜底明文且标注未加密，release 不提供。
- 原登录“已知系统不再查询”、没有 SignalR、终端/Docker/守护待实现、创建备份按钮未开放等描述均已被当前实现取代，不保留旧结论。
- 已完成修复不再追加流水账；发生行为变化时修改对应规范和实现表，未关闭检查与缺陷统一维护在 Verification。构建方式与图标同步见 [开发发布](../development/android-release.md)。
