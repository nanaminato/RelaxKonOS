# Android 内置应用功能补齐计划

> 更新：2026-10-01。状态：公共安装 BP01-M1、宿主自定义代理 BP02-M1、Nginx 与通用站点管理 BP03-M1/M2 已接入，独立证书与站点/Kestrel 联动 BP04-M1/M2 已接入，FRP 客户端/frps/运行时 BP05-M1/M2 已接入，BP06-M1/M2、BP02-M2 和 BP17-M1 第一批聚合已接入，BP07-M1、BP08-M1 已接入，BP09-M1/M2 已接入；BP11 共用编辑器、BP10 Git 工作区已接入，BP12 文件与图片已接入，BP13 终端已接入，BP14 进程守护已接入，BP15 任务管理与监控已接入，BP16 应用部署及模板已接入，下一项为 BP17 事件与运维中心。
> 用户已确认对照对象是 Android 手机端与桌面端。
> 本文维护差异清单、实施范围与推进顺序；实现进度见 [Progress](../status/Progress.md#2-bp-实现进度)，测试进度见 [Verification](../status/Verification.md#12-bp-测试进度)。两者独立记录，未测试不阻止下一步实现，也不代表验收通过。

## 1. 对照依据与范围

以桌面端 [`BuiltInApplicationRegistry.cs`](../../../RelaxKonOS.Client/Services/BuiltInApplicationRegistry.cs) 注册的 **25 个内置应用**为清单，以实际 ViewModel、Android 页面、Gateway 实现和共享 Protocol 为功能证据。`examples/`、外置应用和仅有设计文档的功能不计作已实现内置应用。

Android 对照入口：

- [`ManageScreen.kt`](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/ManageScreen.kt)、[`Routes.kt`](../../app/src/main/java/app/relaxkonos/mobile/ui/nav/Routes.kt)：手机管理目录与导航。
- [`MoreScreen.kt`](../../app/src/main/java/app/relaxkonos/mobile/ui/more/MoreScreen.kt)：设置及账户入口。
- [`RelaxKonGateway.kt`](../../app/src/main/java/app/relaxkonos/mobile/core/net/RelaxKonGateway.kt)、[`RelaxKonApi.kt`](../../app/src/main/java/app/relaxkonos/mobile/core/net/RelaxKonApi.kt)：实际远端调用范围。接口中的默认失败实现不算交付。
- [`WebPublishing.kt`](../../app/src/main/java/app/relaxkonos/mobile/core/net/WebPublishing.kt)：已有网站和证书投影；现已包含 Nginx 实例及生命周期投影，页面见 [Nginx 管理](../features/Nginx.md)；独立证书生命周期与部署见 [证书管理](../features/Certificates.md)。
- [`Mobile Progress`](../status/Progress.md)：既有实现和验证限制。

补齐目标是手机能完成与桌面等价的管理任务，采用原生 Compose 页面、手机导航和手机/平板布局。服务器上的 Nginx、FRP、Mihomo、SMB 等仍由 Server 管理；客户端本地的 SSH 转发、浏览器和桌面扩展包按其真实运行位置单独设计。

本轮不实现独立欢迎、记事本、内置浏览器和注册表应用。BP11 提供文件/Git 共用的轻量编辑能力，BP20 提供系统浏览器或外部应用打开服务的入口，BP23 复用登录/首页/帮助完成引导；BP18 保留编号并标为“不实施”。BP22 所需共享设置仍按对应契约接入，不依赖独立注册表页面。

## 2. 全部 25 个内置应用的差异清单

“已有”只说明存在实现，不能替代真机或实际宿主验收。“缺少”指没有对应可完成的手机任务流；在首页看到能力名称或在其他流程里看到数据，不等于独立应用已交付。

| 序号 | 桌面内置应用 / BuiltIn key | Android 当前实现 | 缺口或处理方式                                                                                  | 实施编号 |
| --- | --- | --- |-------------------------------------------------------------------------------------------------| --- |
| 01 | 欢迎 / `welcome` | 登录页、首页、关于承担部分引导与介绍 | 不实现独立欢迎应用                                                                              | BP23 |
| 02 | 记事本 / `notepad` | Git 页有受限文本编辑 | 不实现独立记事本应用                                                                            | BP11 |
| 03 | 代码编辑器 / `codeeditor` | 文件/Git 共用编辑器已实现 | 当前 Unicode 文本/查找替换/语法/冲突行为见 [编辑器](../features/TextEditor.md)                    | BP11 |
| 04 | 图片查看器 / `imageviewer` | 缩略图、缓存与完整有界图片查看已接入 | 当前任务流见 [文件与图片](../features/Files.md)，设备验收独立追踪                                        | BP12 |
| 05 | 设置 / `settings` | 账户、连接、服务器信息、外观、诊断、关于 | 自定义/受管宿主出站代理已有；缺环境变量、宿主时间/身份及应用管理等设置                                       | BP02、BP22 |
| 06 | 终端 / `terminal` | Server PTY、会话管理、恢复、扩展键已有 | BP13 双栏/输入/搜索/外观及 VT 单元已接入；行为见 [终端](../features/TerminalAutomation.md)，设备与真实 PTY 验证独立追踪                                        | BP13 |
| 07 | 服务器中心 / `server-center` | 已有 SSH 接入、部署、系统、文件、终端 | 逐项核对部署操作、安装记录、连接恢复与宿主生命周期；不重写已有流程                              | BP19 |
| 08 | SSH 文件浏览器 / `ssh-files` | 服务器中心已有 SFTP 文件页 | 核对文件操作、传输取消、恢复与大文件边界，补齐缺口                                              | BP19 |
| 09 | 文件资源管理器 / `explorer` | 列表筛选排序/多选剪贴板、文件操作/属性权限、传输、预览与编辑已有 | 当前行为见 [文件与图片](../features/Files.md)，设备/真实宿主验收独立追踪                                 | BP11、BP12 |
| 10 | 浏览器 / `browser` | 没有独立内置浏览器 | 不实现独立内置浏览器                                                                            | BP20 |
| 11 | 端口转发 / `port-forwarding` | 受管登录隧道已有内部用途 | 缺少供用户管理的手机本地 SSH 转发列表、创建/修改/停止和连通检查                                 | BP19 |
| 12 | 任务管理器 / `taskmanager` | 完整指标、历史/Hub、手机详情/双栏、原实例终止/回执、过滤排序分页和前台观察已接入 | 当前行为见 TaskManager，宿主/设备验证独立追踪                                                | BP15 |
| 13 | Docker 管理器 / `docker` | 引擎/镜像源、容器完整资源动作/详情/统计、镜像/网络/卷与 Compose 已有 | BP09 已接入；设备/真实宿主验收独立追踪   | BP02、BP09 |
| 14 | 进程守护 / `processguardian` | 工作负载定义、生命周期、日志及一次性脚本已有 | 完整字段/精确参数、审批与保存读回、状态观察和有界实时日志已接入，行为见 [进程守护](../features/Guardian.md)                       | BP14 |
| 15 | 防火墙 / `firewall` | BP07-M1 状态、启停、默认策略、规则增删改、确认/提权及未知事实核实已接入 | 真实 UFW/设备与并发验收独立追踪 | BP07 |
| 16 | 证书管理器 / `certificates` | 独立列表/详情、预检/ACME、自签名、生命周期、站点显式选择与 Kestrel 实际部署、原任务观察/取消/恢复已有 | BP04 已接入；真实设备与宿主验收独立追踪 | BP04 |
| 17 | Web 服务器管理器 / `webservers` | 网站页已有 Nginx 安装/发现/接管、实例生命周期/卸载、配置检查、通用站点增删改、版本冲突/断线核实及应用网站发布 | BP03 已接入；真实设备与宿主验收独立追踪                     | BP03 |
| 18 | 文件服务 / `file-services` | BP08-M1 Samba 安装/服务、共享 CRUD/权限/目录、Samba 用户与未知写入核实已接入 | 真实 Samba/Windows SMB 与设备验收独立追踪 | BP08 |
| 19 | 隧道管理器 / `tunnels` | FRP 运行时、frpc 配置/Token/隧道、应用/停止，frps 完整配置/启停/重启/日志/审计、修订冲突与未知请求核实已有 | BP05 已接入；真实 FRP、Windows Helper 和设备验收独立追踪 | BP05 |
| 20 | 代理管理器 / `proxy` | BP06-M1/M2 运行时安装/配置/订阅/节点、系统代理/TUN/恢复、流量/连接/日志、DNS/GeoData 已接入 | BP06 已接入；真实设备/宿主验收独立追踪                      | BP06 |
| 21 | Git / `git` | 工作区分支/暂存/差异/历史/冲突、Git 安装与恢复、共用编辑器及隔离构建已接入 | 当前任务流见 [Git](../features/Git.md)，真实宿主/设备验收独立追踪 | BP10 |
| 22 | 应用安装器 / `appinstaller` | 已有服务端应用部署与模板目录；没有桌面扩展包安装器 | 桌面扩展包与服务端部署包是不同产品；先定义移动包管理能力，不能将 Avalonia 包当 Android 应用运行 | BP21 |
| 23 | 注册表 / `registry` | 无对应管理页和调用 | 不实现注册表应用                                                                                | BP18 |
| 24 | 应用部署 / `application-deployments` | 镜像/归档/模板新建、完整定义编辑、Git 产物更新/回滚、操作、日志和定义备份已有 | 既有实例新镜像/归档修订、模板更新说明、差异预览与显式精确版本更新已接入 | BP16 |
| 25 | 事件与告警中心 / `event-alerts` | 运维中心已有告警列表/详情/确认和修复跳转 | 核对桌面提供的筛选、策略/通知设置、事件观察；缺失项按现有契约补齐                               | BP17 |

## 3. 编号实施计划

P0 为安装、网络和发布的优先批次；P1 为其他远端管理与已有功能补齐；P2 为需要独立平台方案的应用。全部编号保持稳定，后续按 `BPxx-Mn` 拆分，不能因为执行顺序调整而重编号。实时状态统一记录在 Progress，测试状态统一记录在 Verification。

下表“完成条件”描述目标行为；实现完成依据当前契约、实际代码和可完成任务流记录，测试是否证明这些行为单独追踪。后续步骤依赖前项的实际实现，不依赖其测试全部完成；未执行测试、缺设备或宿主环境均不构成实现顺序的阻塞。测试发现的真实代码缺陷另列实现待办，涉及缺陷的依赖按实际影响判断。

| 编号 | 优先级 | 工作项 | 具体交付 | 依赖 | 完成条件 |
| --- | --- | --- | --- | --- | --- |
| BP00 | P0 | 全量差异基线 | 对照 25 个注册应用、手机实际路由/调用与共享契约；记录已有、缺功能、缺应用及平台边界 | 无 | 本文清单建立；每个实现任务开工时继续逐动作核对，遗漏追加子编号 |
| BP01 | P0 | 共用安装与任务能力 | 接入统一 Installations 契约：安装/升级/修复/卸载、在线/手机包上传/服务器文件引用、任务状态/取消/断线恢复；加入运维中心 | BP00 | 六类安装服务按各自支持动作门控；幂等提交、提权确认、包引用过期及取消可验证 |
| BP02 | P0 | 宿主出站代理（已实现） | BP02-M1/M2 行为见 [宿主出站代理](../features/OutboundProxy.md)；验证见 Verification | BP06 | 后续消费问题关联具体领域 |
| BP06 | P0 | Mihomo 代理管理器（已实现） | BP06-M1/M2 行为见 [Mihomo 代理管理器](../features/Proxy.md)；验证见 Verification | BP01 | 真实设备/宿主验收独立追踪 |
| BP07 | P1 | 宿主防火墙（已实现） | BP07-M1 当前 UFW 流程见 [宿主防火墙](../features/Firewall.md)；验证见 Verification | BP00 | 不支持宿主按当前能力显示不可用 |
| BP08 | P1 | SMB 文件服务（已实现） | BP08-M1 当前行为见 [SMB 文件服务](../features/Smb.md)；验证见 Verification | BP01、既有 Directory 选择器 | 真实宿主/设备验收独立追踪 |
| BP09 | P1 | Docker 功能补齐（已实现） | BP09-M1/M2 行为见 [引擎与镜像源](../features/DockerEngine.md) 和 [Docker 资源](../features/DockerResources.md)；验证见 Verification | BP01、BP02 | 真实设备/宿主验收独立追踪 |
| BP10 | P1 | Git 功能补齐（已实现） | 当前工作区/安装/恢复行为见 [Git](../features/Git.md) | BP01、BP11 | 真实宿主/设备验收独立追踪 |
| BP11 | P1 | 文件/Git 共用编辑器（已实现） | 当前行为见 [共用编辑器](../features/TextEditor.md) | BP00 | 设备/真实身份与文件系统验收独立追踪 |
| BP12 | P1 | 文件与图片体验补齐（已实现） | 当前行为见 [文件与图片](../features/Files.md) | BP00、BP11 | 设备/实际宿主验收独立追踪 |
| BP13 | P1 | 终端体验补齐（已实现） | 当前双栏/输入/搜索/设置/VT 行为见 [终端](../features/TerminalAutomation.md) | BP00 | 设备与真实 PTY 验证独立追踪 |
| BP16 | P1 | 应用部署及模板（已实现） | 完整定义编辑、既有实例来源修订与显式精确模板版本更新见 [应用部署与模板](../features/ApplicationDeployments.md) | BP00、BP11 | 设备与实际 Docker/数据恢复检查见 Verification |
| BP17 | P1 | 事件与运维中心（已实现） | 完整事件/告警读取、汇总/筛选/分页、受控动作/读回/未知门禁、前台策略及领域恢复见 [任务与恢复](../features/OperationsRecovery.md) | BP01；各领域接入 | 可靠后台通知和事件源持久重放仍归 AD08-M4；验证见 Verification |
| BP18 | — | 注册表应用（不实施） | 本轮不增加独立注册表页面、入口或通用配置树；保留编号 | 无 | 排除范围明确；BP22 按具体设置契约接入 |
| BP19 | P1 | SSH 本地转发与服务器中心（已实现） | 独立用户转发/后台可停止通知、SFTP 批量/目录/复制剪切/搜索排序/目录历史与工作区隔离见 [服务器中心](../features/ServerCenter.md) | BP00 | 可信安装执行仍属 AD01；Android/真实 SSH 与传输验收见 Verification |
| BP20 | P2 | 服务访问入口（已实现） | 当前地址、外部应用与 TLS/凭据边界见 [服务访问](../features/ServiceAccess.md) | BP03、BP19 | 真实可达性与设备检查见 Verification |
| BP21 | P2 | 应用安装器移动方案（方案已完成） | manifest/运行时调查、逐类执行平台和权限/版本/更新/移除边界见 [移动包方案](../design/ApplicationPackages.Design.md) | BP00 | 不声称手机第三方运行时/检查器或远端桌面代理已经实现 |
| BP22 | P1 | 其他设置（已实现） | 宿主环境/时区/名称、预览/原操作/版本/回滚、Android 应用管理边界见 [设置](../features/Settings.md) | BP02；当前共享设置契约 | Android/真实宿主验证见 Verification；不复制桌面窗口/包运行时 |
| BP23 | P2 | 欢迎与帮助（已实现） | 登录/首页/更多三语原生引导、能力门控任务及既有项目指南链接见 [帮助](../features/Help.md) | 核心管理任务交付 | 设备/外部浏览器检查见 Verification；首次 Server 安装仍属 AD01 |
| BP24 | P0/P1/P2 | 分批验证与文档同步 | 每批 Kotlin/Protocol/Server 必要测试、三语键集、能力门控、手机/平板/IME/大字体、Ubuntu/Windows 与断线/取消验收；更新进展与设计 | 每批实现 | 记录真实执行命令与结果；未测平台/环境明确保留待验收；不能用编译通过代替真实安装和联网验收 |

## 4. 第一批拆分与执行顺序

第一批围绕用户举出的五项：Nginx 安装、FRP、证书管理器、Mihomo、设置代理集成。先落公共安装能力和自定义出站代理，再接各管理应用；受管 Mihomo 来源在代理管理完成后联调。

第一批拆分已交付，当前行为保存在功能文档；剩余设备/宿主检查保存在 Verification。

BP01-M1 已接入安装任务观察；BP17-M1 首批聚合已交付，后续领域交付时继续同步运维入口。BP24-M1 独立追踪第一批测试，从每次实现变更开始记录，不作为上述步骤之间的前置条件。

第二批：BP07–BP10、BP11/BP12、BP14/BP15/BP16/BP22，随各应用交付同步 BP17，并独立追踪 BP24。第三批：BP13/BP19/BP20/BP21/BP23，并继续追踪 BP24。BP18 不实施。独立任务可调整先后，但不跳过实现依赖；测试未执行仍可进入下一项。

### 4.1 当前交付与后续边界

BP01-M1 的公共数据链路、安装任务恢复/观察/取消和功能文档已交付，已完成拆分不再保留在本计划。当前行为见 [公共运行时安装](../features/Installations.md)，实现证据见 [Progress](../status/Progress.md#2-bp-实现进度)，未执行检查见 [Verification](../status/Verification.md#12-bp-测试进度)。公共链路不替代各服务表单；Nginx 服务表单和实例闭环已由 BP03-M1 接入，其他服务仍随其领域交付。

BP02-M1 已交付宿主自定义代理，当前行为见 [宿主出站代理](../features/OutboundProxy.md)，已完成拆分从本计划移除。BP03-M1 已交付 Nginx 安装、发现、接管与实例生命周期，已完成交付从本计划移除；行为见 [Nginx 管理](../features/Nginx.md)。BP03-M2 已交付通用站点增删改、版本冲突与同步提交事实核实，已完成交付从本计划移除，行为见 [站点管理](../features/WebSites.md)。BP04-M1 已交付独立证书生命周期与原任务恢复，已完成交付从本计划移除；行为见 [证书管理](../features/Certificates.md)。BP04-M2 已交付显式证书选择、状态/有效期/SAN 门控、Kestrel 实际部署查询与原键/ID 恢复，已完成拆分从本计划移除；行为见 [证书管理](../features/Certificates.md) 和 [站点管理](../features/WebSites.md)。BP05-M1 已交付固定版本运行时管理、frpc 配置/Token/四协议隧道、应用/停止与事实核实，已完成拆分从本计划移除，行为见 [FRP 客户端与运行时](../features/Tunnels.md)。BP05-M2 已交付 frps 配置/秘密/生命周期/日志/审计与版本/未知事实核实，已完成拆分从本计划移除，行为见 [FRP 隧道与运行时](../features/Tunnels.md)。BP06-M1 已接入运行时安装/生命周期、配置/订阅和节点，行为见 [Mihomo 代理管理器](../features/Proxy.md)。BP06-M2 宿主网络/恢复和诊断也已接入。BP02-M2 受管来源与消费联动、BP17-M1 首批聚合也已交付，行为见 [宿主出站代理](../features/OutboundProxy.md) 和 [任务与恢复](../features/OperationsRecovery.md)。BP07-M1 已接入，行为见 [宿主防火墙](../features/Firewall.md)。BP08-M1 已接入，行为见 [SMB 文件服务](../features/Smb.md)。BP09-M1/M2 已接入，行为见 [引擎与镜像源](../features/DockerEngine.md) 和 [Docker 资源](../features/DockerResources.md)。BP11 文件/Git 共用编辑器和 BP10 Git 工作区已接入，BP12 文件与图片也已接入，BP13 终端已接入，BP14 进程守护已接入，BP15 任务管理与监控已接入，BP16 应用部署及模板已接入，BP17 事件与运维中心已接入，BP19 SSH 本地转发与 SFTP 已接入，BP20 服务访问已接入，BP21 移动包方案已完成，BP22 设置与 BP23 帮助已接入；本轮 BP 实现推进至 BP23，BP24 验证独立跟踪。BP01-M1/BP02-M1/BP03-M1/M2/BP04-M1/M2/BP05-M1/M2 剩余检查不作为后续实现依赖；各提交的测试证据见 Verification；若后续检查发现真实代码缺陷，在 Progress 关联受影响的实现待办。

## 5. 实施边界与共用规则

### 5.1 远端管理与手机本地能力

- Nginx、FRP、Mihomo、证书、防火墙、SMB 和宿主设置走现有 Server API。手机不直接运行宿主命令或读取其运行时文件。
- Mihomo TUN 和系统代理配置作用于服务器。若未来增加手机 VPN，需要独立 Android 平台方案，不在宿主代理功能中隐式增加。
- `port-forwarding` 是客户端本地 SSH 转发，需要 Android SSH 和生命周期设计，不能误接宿主 FRP API。
- `registry` 是 RelaxKonOS 的配置控制面，有 User/Workspace/Device 作用域，不是 Windows 注册表；本轮不实施独立应用。具体宿主设置由 BP22 按其当前共享契约提供。
- `appinstaller` 安装桌面扩展包，与 Docker 应用模板、Java/.NET/Python 服务部署不同。先审核 manifest 和平台运行时，不添加虚假的可安装入口。

### 5.2 代理集成的两个配置面

[`NetworkPageViewModel.OutboundProxy`](../../../RelaxKonOS.Client/Apps/Settings/ViewModels/NetworkPageViewModel.cs) 使用 [`DockerProxyViewModel`](../../../RelaxKonOS.Client/Apps/Docker/DockerProxyViewModel.cs)，对应共享 [`DockerProxyContracts`](../../../../Shared/RelaxKonOS.Protocol/Docker/DockerProxyContracts.cs)。它是宿主级出站偏好，设置页和 Docker 页必须共用同一状态。

[`ProxyContracts`](../../../../Shared/RelaxKonOS.Protocol/Proxy/ProxyContracts.cs) 则管理 Mihomo 引擎、系统代理、TUN、订阅、配置和流量。两者通过“受管代理来源”关联，但不能复制成两份出站设置，也不能让改订阅隐式修改 Docker 重启范围。

### 5.3 安装与操作契约

使用 [`InstallationContracts`](../../../../Shared/RelaxKonOS.Protocol/Installations/InstallationContracts.cs) 的统一入口，服务种类为 Smb/Nginx/Frp/Mihomo/Docker/Git；每种服务可用动作根据 Server 支持与宿主能力显示，不假定所有种类都支持全部操作。

安装包使用限时 FileReferenceId 或当前受控上传端点，不把任意服务器路径塞入安装请求。手机上传通过系统文档选择器读取，服务器文件通过现有远端路径选择器选择。领域操作与安装操作的状态、枚举、取消条件分别按当前契约读取，不强行共用错误的状态转换。

### 5.4 导航、数据和安全

- 新入口位于“管理”或“更多”，每个入口必须有可完成的任务流；没有实现时不展示占位按钮。
- 手机使用列表 → 详情 → 模态编辑，平板按可用宽度分栏；表单可滚动，决策按钮始终可达；适配 IME 和大字体。
- 所有新增可见文案同步中文/英文/日文；枚举与稳定问题码本地化，秘密与第三方原始错误不直接进入常规界面。
- typed Gateway/Repository 复用 AuthSession、刷新、提权与确认；401 重试不重复发起长任务；只有支持幂等的契约发送稳定幂等键；SMB 等同步 API 不伪造键或重放。
- 任务观察绑定当前登录宿主/身份。切换、注销、离页和取消不能让旧响应覆盖新会话，也不能把停止观察误当服务端任务已取消。
- 对共享契约有必要变更时，同步 Protocol、Server、桌面、Android、测试和文档，直接采用当前格式，不加旧路由、别名或双格式解析。

## 6. 实现与测试独立追踪

- 本文只维护范围、依赖和未实现交付；[Progress](../status/Progress.md#2-bp-实现进度) 是实现状态的唯一记录，包含编号、实现状态、代码证据、剩余代码与下一步。
- [Verification](../status/Verification.md#12-bp-测试进度) 是测试状态的唯一记录，分别追踪自动化构建/测试、设备交互、Ubuntu/Windows 宿主验收，并保留真实执行证据。
- 实现状态使用“未开始 / 进行中 / 部分实现 / 已实现 / 不实施”；测试状态使用“未执行 / 进行中 / 部分通过 / 通过 / 失败 / 环境受限 / 不适用”。环境受限写明缺失条件，不视为通过；不适用写明原因。
- 更新实现后即可按实现依赖推进下一项；即使没有执行任何测试，也不把当前项锁定为未实现或要求停下等待环境。实现进度不因测试缺失回退，测试通过也不自动补齐缺失代码。
- 测试发现真实缺陷时，在 Progress 关联缺陷及受影响动作；已实现部分保留，仍需修改的部分标为“部分实现”。失败结果继续保留在 Verification，不以推进下一项掩盖失败。
- 记录实现状态时附动作/文件证据；记录测试结果时附日期、提交、命令、环境、范围与结果。不能用编译成功代替真实安装和联网验收，也不能将未测写成通过。
- 目标实现后将当前行为写入 features，并从计划移除已完成交付；只剩测试时保留 Verification 检查。所有 Android 专属记录维护在本项目 docs，不在仓库根 docs 建第二份目录或历史存档。

已实现部署领域的说明见 [文档目录](../README.md)，首次安装及部署剩余工作见 [部署后续计划](Deployment.md)。共享 schema 与执行语义由对应领域契约维护。
