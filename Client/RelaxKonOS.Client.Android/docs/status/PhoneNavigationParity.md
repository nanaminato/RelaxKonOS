# Android 手机与桌面应用的页面结构差异

> 检查日期：2026-10-02。依据：当前仓库的桌面 Avalonia 页面、Android Compose 页面和实际导航接线。
> 用户反馈：手机端与桌面端操作差异很大，使用不方便。
> 状态：已完成代码结构检查，以下交互问题待改进；本次没有执行手机真机操作，也没有修改应用实现。设备表现、实际操作次数和状态恢复仍需按 [Verification](Verification.md) 验证。

## 1. 结论与判定范围

当前有 **8 个应用存在桌面多功能页、手机单个滚动页面或有限功能切换的结构差异**：Docker、Mihomo 代理、FRP 隧道、Git、Web 服务器、SMB 文件服务、证书、应用部署。另外，任务管理器和设置虽然已有多个手机页面，但同一桌面应用的入口在手机上分散，需要单独追踪。

主要问题是功能分类、入口位置和切换路径变化：桌面用户可以按应用内导航直接进入目标页，手机用户需要在长页面里寻找区块，或返回管理/更多目录再进入另一功能。列表、编辑、日志和恢复工具混排时，条目增长会继续拉长页面。以上是代码结构支持的可用性风险，用户已反馈操作不便；具体滚动距离、触达成本及错误操作频率尚无本轮设备测量。

本检查按**可独立选择的功能区域**比较页面，不按源文件数量或路由常量数量计算。手机的列表→详情、弹窗和分步向导分别记录；局部 Chip/按钮切换计为有限功能切换，不能一律称为“没有分页”。单栏布局仍可承载应用内多页导航，手机无需复制桌面窗口或宽表格。

功能接入和交互改进分别追踪。[BuiltInParity](../plans/BuiltInParity.md) 中的 BP“已实现”表示当前任务能力接入，不代表这里的页面组织问题已解决，也不代表手机操作便利性已验收。

## 2. 桌面多页、手机单页或有限切换

下列 PN 编号用于稳定追踪。优先级为本次建议：P1 优先处理频繁操作和长页混排，P2 处理较小的组织差异；所有条目当前均为**待改进**。目标分类是后续设计建议，不是已交付页面。

| ID / 优先级 | 应用 | 桌面当前组织 | Android 手机当前组织 | 操作不便与建议方向 |
| --- | --- | --- | --- | --- |
| PN-01 / P1 | Docker 管理器 | 8 个导航页：概览、容器、编排、镜像、镜像源、代理、网络、卷 | `manage/docker` 为运行时/Compose 与资源摘要；另有资源页和引擎/镜像源页。资源页用 4 个 Chip 切换容器/镜像/网络/卷，宿主代理跳到 `more/network` | 同一应用的分类分散在多个入口，摘要和可操作资源列表并存；镜像源混在引擎控制页。建议提供统一 Docker 功能导航，把现有页面接入相应分类，并保留资源归属跳转。 |
| PN-02 / P1 | Mihomo 代理管理器 | 6 个导航页：概览、订阅、节点、连接、日志、设置 | `manage/proxy` 用“配置/网络”两个按钮切换；运行时与恢复信息共用顶部，订阅/配置/节点纵向串联，网络面板继续串联设置、GeoData、流量、连接和日志 | 桌面的独立目的地在手机上变为长页区块，节点和日志难以直接定位。建议按概览、配置/订阅、节点、连接、日志、设置提供可直达分类，安装和恢复按需进入。 |
| PN-03 / P1 | FRP 隧道管理器 | 5 个导航页：概览、隧道、服务器、frps、运行时 | `manage/tunnels` 仅切换客户端/frps；客户端页包含运行时、安装恢复、配置列表、选中配置的隧道与日志；手机列表/详情在页内切换 | 查看隧道需要先定位所属配置，运行时管理和诊断缺少与桌面对应的独立分类。建议保留客户端/frps 的区别，再提供概览、隧道/配置、运行时和日志/诊断入口，显示当前配置上下文。 |
| PN-04 / P1 | Git | 概览、工作区、日志、远程仓库；有冲突时提供冲突页 | `manage/git` 在一个滚动列中先渲染 `GitWorkspaceSection`，再渲染隔离构建；工作区含安装、仓库注册、分支、变更、提交、冲突和历史。分支/变更只在较宽布局并列 | 手机上的分支、暂存、历史和构建入口受前面内容长度影响；仓库操作与另一套构建表单同时占据页面。建议提供工作区、分支、历史、冲突及构建的清晰入口，切换时保留仓库和草稿；是否补齐独立远程仓库管理仍按 BP10 逐动作核对。 |
| PN-05 / P1 | Web 服务器管理器 | 实例、站点 2 个导航页 | `manage/websites` 在同一滚动列内依次放置 `NginxManager`、应用网站发布表单、服务器/站点区块；站点管理挂在实例内容下 | 查找站点、维护 Nginx 与发布应用网站相互穿插，不能像桌面一样直接切换实例/站点。建议以实例、站点、应用网站发布组织功能，明确选中实例和站点，编辑及发布单独进入。 |
| PN-06 / P2 | SMB 文件服务 | 概览、共享、用户；用户页按 Samba 凭据能力显示 | `manage/smb` 顶部串联服务状态、连接地址、生命周期、安装恢复，下面用共享/用户 Chip 切换；共享列表/详情/编辑复用同一区域 | 已有共享/用户切换，但导航在公共状态和恢复内容后面，没有独立概览分类。建议把概览、共享、用户作为稳定入口，保留平台能力门控，编辑从所选共享进入。 |
| PN-07 / P2 | 证书管理器 | 概览、证书列表 2 个导航页；概览展示数量、状态和快捷动作 | `manage/certificates` 已有手机列表/详情切换，签发/自签名/恢复入口共用顶部，任务信息继续放在同一滚动列；没有独立概览页 | 差异较小，不属于没有详情页；概览与任务恢复缺少独立组织。建议先核对是否需要紧凑概览，保留可直达列表/详情与任务入口，避免为追求页数新增空页面。 |
| PN-08 / P1 | 应用部署 | 实例列表/详情工作区，详情下分版本、操作、日志 3 个标签；新建有向导 | `manage/deployments` / `manage/deployments/detail` 已有手机列表→详情，创建也有分步向导；详情将概览、备份恢复、活动操作、日志、版本、历史操作放入同一 `LazyColumn` | 加载日志或增加版本会把后续内容推远，查看版本/操作需要跨越其他区块。建议保留现有列表/详情和向导，在实例详情中提供概览、版本、操作、日志及备份恢复分类，切换时保持实例身份。 |

### 2.1 代码证据

下面的链接对应实际页面；Android 路由及跨页跳转同时核对了 [Routes.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/nav/Routes.kt) 与 [MobileNavHost.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/nav/MobileNavHost.kt)。页内状态切换不要求已有独立路由。

| ID | 桌面证据 | Android 证据 |
| --- | --- | --- |
| PN-01 | [DockerManagerWorkspace.axaml](../../../RelaxKonOS.Client/Apps/Docker/Views/DockerManagerWorkspace.axaml) | [DockerScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/docker/DockerScreen.kt)、[DockerResourceScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/docker/DockerResourceScreen.kt)、[DockerControlScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/docker/DockerControlScreen.kt) |
| PN-02 | [ProxyManagerWorkspace.axaml](../../../RelaxKonOS.Client/Apps/Proxy/Views/ProxyManagerWorkspace.axaml) | [ProxyScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/proxy/ProxyScreen.kt)、[ProxyNetworkPanel.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/proxy/ProxyNetworkPanel.kt) |
| PN-03 | [TunnelManagerView.axaml](../../../RelaxKonOS.Client/Apps/Tunnels/Views/TunnelManagerView.axaml) | [TunnelsScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/tunnels/TunnelsScreen.kt)、[ManagedFrpsManager.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/tunnels/ManagedFrpsManager.kt) |
| PN-04 | [GitClientWorkspace.axaml](../../../RelaxKonOS.Client/Apps/Git/Views/GitClientWorkspace.axaml) | [GitScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/git/GitScreen.kt)、[GitWorkspaceSection.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/git/GitWorkspaceSection.kt) |
| PN-05 | [WebServerManagerWorkspace.axaml](../../../RelaxKonOS.Client/Apps/WebServers/Views/WebServerManagerWorkspace.axaml) | [WebsitesScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/websites/WebsitesScreen.kt)、[NginxManager.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/websites/NginxManager.kt)、[WebSiteManager.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/websites/WebSiteManager.kt) |
| PN-06 | [FileServicesWorkspace.axaml](../../../RelaxKonOS.Client/Apps/FileServices/Views/FileServicesWorkspace.axaml) | [SmbScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/smb/SmbScreen.kt) |
| PN-07 | [CertificateManagerWorkspace.axaml](../../../RelaxKonOS.Client/Apps/Certificates/Views/CertificateManagerWorkspace.axaml)、[CertificateOverviewView.axaml](../../../RelaxKonOS.Client/Apps/Certificates/Views/CertificateOverviewView.axaml) | [CertificatesScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/certificates/CertificatesScreen.kt) |
| PN-08 | [ApplicationDeploymentsWorkspace.axaml](../../../RelaxKonOS.Client/Apps/ApplicationDeployments/Views/ApplicationDeploymentsWorkspace.axaml) | [DeploymentsScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/deployments/DeploymentsScreen.kt)，重点为 `DeploymentDetail` |

## 3. 已有多页但入口分散

| ID / 优先级 / 状态 | 应用 | 当前差异及操作影响 | 建议方向与证据 |
| --- | --- | --- | --- |
| PN-09 / P2 / 待改进 | 任务管理器 | 桌面在同一应用内切换性能/进程；手机从管理目录分别进入 `manage/monitor`、`manage/processes`，两页没有彼此的直接切换入口。手机已有指标/进程详情，问题在跨功能切换路径 | 建议提供共同的任务管理导航，切换保留指标选择、进程筛选和列表位置。桌面：[TaskManagerMainView.axaml](../../../RelaxKonOS.Client/Apps/TaskManager/Views/TaskManagerMainView.axaml)；手机：[ManageScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/ManageScreen.kt)、[MonitorScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/monitor/MonitorScreen.kt)、[ProcessesScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/processes/ProcessesScreen.kt)。 |
| PN-10 / P2 / 待改进 | 设置 | 桌面在同一设置应用的分类导航中选择页面；手机分散到更多目录的账户/连接/宿主设置/代理/外观/应用/诊断等页。宿主设置自身用 Chip 切换时间、身份、环境变量，已有分类 | 建议统一分类层级和返回上下文，清楚区分 Android 本机与远端宿主设置；无需照搬桌面专属设置。桌面：[SettingsView.axaml](../../../RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml)；手机：[MoreScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/more/MoreScreen.kt)、[HostSettingsScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/more/HostSettingsScreen.kt)。 |

## 4. 对照边界与已有合理拆分

- **服务器中心 / SSH 工作区**：桌面有主机、部署、历史页；Android 已有文件、终端、部署、系统、设置、本地转发 6 个入口，安装维护又有概览、环境检查、维护操作、操作记录子页。本轮不将其归为“手机单页”。证据：[ServerCenterWorkspace.axaml](../../../RelaxKonOS.Client/Apps/ServerCenter/Views/ServerCenterWorkspace.axaml)、[SshWorkspaceScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/servercenter/SshWorkspaceScreen.kt)、[ServerMaintenanceScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/servercenter/ServerMaintenanceScreen.kt)。
- **端口转发**：桌面有转发/连接两页；手机转发是上述已选 SSH 主机工作区的一页，连接管理由服务器中心承担，页内编辑使用弹窗。这是连接上下文整合的结构差异，不能据此判定缺少连接能力；后续验证从转发页管理目标主机的路径即可。证据：[PortForwardingMainView.axaml](../../../RelaxKonOS.Client/Apps/PortForwarding/Views/PortForwardingMainView.axaml)、[SshForwardsScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/servercenter/SshForwardsScreen.kt)、[ServerCenterScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/servercenter/ServerCenterScreen.kt)。
- **运维中心**：Android 已有任务/告警标签，告警按能力显示；不属于本次桌面多页压成手机单页的主要问题。证据：[OperationsScreen.kt](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/operations/OperationsScreen.kt)。
- **文件、终端、编辑器、图片查看器**：列表/详情、会话选择、编辑弹窗及全屏预览应按各自任务流验收；不因手机单栏就要求复制桌面多窗口。防火墙与进程守护的长表单便利性可继续跟踪，但本次没有桌面应用内多页被合并的同类证据。
- **欢迎、记事本、内置浏览器、注册表、桌面扩展包安装器**：独立应用是否实施以及平台运行边界仍由 [BuiltInParity](../plans/BuiltInParity.md) 和 [ApplicationPackages.Design](../design/ApplicationPackages.Design.md) 维护，不当作分页问题。

## 5. 共用改进目标

后续改进保留 [Product.Design](../design/Product.Design.md) 的手机原生导航和宽度断点，围绕桌面的功能分类与任务顺序组织手机操作：

1. 应用入口能看到主要功能分类及当前选中项，进入分类不依赖滚动穿过其他功能区块。分类较多时使用可滚动标签或分类选择器；手机单栏承载当前页，宽屏再按任务显示列表/详情。
2. 列表→详情→编辑逐层进入，页内返回按钮与系统返回键一致；关闭编辑先处理未保存草稿，再返回原分类和原列表位置。
3. 切换分类保留当前服务器、账户、资源/仓库/配置身份、筛选和各分类滚动位置；切换宿主或账户按现有隔离规则清理，不能误用旧上下文。
4. 日志、历史记录及高级恢复独立进入或按需展开；活动操作、待核实状态和必要警告始终能在当前目标上下文中找到，不因拆页隐藏。
5. 安装、编辑、发布继续使用现有向导与确认流程。页面重组不得重复提交请求、重新创建操作 ID、绕过提权或弱化危险操作确认；功能缺口另归对应 BP 条目。

## 6. 待执行的手机复验

以下检查尚未执行；结果统一写入 [Verification](Verification.md)，PN 条目只有在实现和设备证据具备后才能关闭。已有 [平板巡检](TabletUsability.md) 的 TAB 问题保留原编号，不能代替手机检查。

| 检查 | 场景 | 通过标准 |
| --- | --- | --- |
| 分类触达 | 逐一从应用入口寻找桌面对应的主要功能，记录进入路径、点击次数、是否跨区块滚动 | 分类可见且可直接选择；未实现功能和平台差异明确，不把隐藏区块误当不存在 |
| 长内容切换 | 使用多容器/配置/站点/共享/版本和长日志，分别切换节点↔日志、隧道↔运行时、实例↔站点、日志↔版本 | 数据增长不把分类入口推到长页末尾；类别切换不要求浏览无关列表或日志 |
| 返回与草稿 | 列表选中资源、进入编辑、打开键盘，再按页面返回和系统返回；切换分类并回到原页 | 草稿有明确处理，回到原分类/列表位置；服务器及资源身份一致，没有意外写入 |
| 运维上下文 | 活动任务、未知结果、安装恢复和告警修复跳入相应应用，再切换功能页 | 保留原操作关联与必要提示；不丢失待核实状态、不重复提交 |
| 手机布局 | 竖屏/横屏、旋转/后台返回，三语、大字体、TalkBack，软键盘展开 | 标签可达且选中状态可识别；关键动作不被键盘遮挡，恢复后上下文正确 |

建议先处理 PN-01–05 与 PN-08，再处理 PN-06–07、PN-09–10。这里只记录问题和目标，本轮不启动页面重构，也不把新增文档记为这些交互问题已修复。
