# Android FRP 隧道与运行时管理

运行时安装支持宿主下载、服务器文件、手机文件及直接提供 URL 的“我要自行下载”，统一交互见 [公共安装来源](Installations.md#安装来源与自行下载)。自行下载使用宿主确认的版本链接。

受管运行时明确未安装时只显示安装引导和“安装 FRP”入口，不展示没有归档可验证时的完整性错误。服务器配置仍可查看与编辑，已有安装任务或未知操作集中到“操作记录”；历史安装查询以“恢复之前的操作”进入。已安装、外部路径无效或状态未知仍保留各自的诊断。

> 当前功能对应 BP05-M1/M2。入口“管理 → 隧道管理器”，路由 `manage/tunnels`，消费服务器 `server.tunnels` 能力；所有运行时和转发进程位于当前 Server。frpc 与宿主 frps 分页，独立配置、生命周期、日志和审计均已接入。构建、测试和待设备验收证据见 [Verification](../status/Verification.md)。

## 界面与操作层级

页面刷新固定在操作栏右侧，创建连接配置等操作位于左侧并支持换行。frps 页只保留一个页面刷新入口，直接重新读取 frps 状态；安装记录卡片的刷新也位于右侧。隧道页使用较紧凑的内容边距，保留的隐藏分页放在无间距容器内，切换标签页后不产生额外空白，也不清除编辑状态。

概览用运行时卡片展示当前状态和版本，高级信息折叠显示路径、上一版本与完整性；安装和运行时维护入口在对应卡片内。配置数量与隧道数量只使用成功读取的集合，不把未读取当作空集合。只读会话显示权限说明。

连接配置列表采用可点击卡片，突出名称、服务器地址、运行时模式、TLS 和已报告的隧道状态，选中项以主题色加粗边框标记。详情与每条隧道分别成卡，显示本地宿主服务到远端端口/域名的转发关系、启用状态和连接事实；明确本地服务不位于手机上，状态不证明公网可达。原有手机详情与 600 dp 平板分栏保留。

配置编辑分为基本信息、安全连接与运行时卡片；隧道编辑分为名称/协议、转发目标和行为选项；frps 编辑分为监听/端口、安全与 Dashboard。认证、TLS、运行时模式和隧道协议采用可换行选项，复选项整行可点击且最小高度为 48 dp。frps 状态与生命周期操作集中在状态卡片内，安全和端口配置单独成卡；配置 revision、已应用 revision、启动时间可展开查看，已保存/已应用事实始终保留。

“操作记录”独立展示安装、升级、修复、卸载任务及进度、取消、恢复和原请求重试；提交运行时操作时选中此页。frpc/frps 的最近动作结果、待核实标记与 frps 显式日志/审计读取也集中在此页。frpc 日志仍在独立日志页。其他页面仅在有待核实操作时显示跳转提示，详情不重复展示。记录页不依赖配置集合成功读取，未知 frps 写入可直接在此重新读取 frps 并确认采用；写入门禁、原幂等键与秘密清理流程保留。页面展示已有内存结果和恢复标记，不新增完整持久历史。

## 1. 连接配置与隧道

[Tunnels.kt](../../app/src/main/java/app/relaxkonos/mobile/core/net/Tunnels.kt)、Gateway/API 和 [TunnelRepository](../../app/src/main/java/app/relaxkonos/mobile/data/TunnelRepository.kt) 直接采用共享 [TunnelContracts](../../../../Shared/RelaxKonOS.Protocol/Tunnels/TunnelContracts.cs) 和当前路由，不提供旧接口。安全列表展示服务器名、主机/端口、认证/TLS、受管/外部路径、Token 是否已设置、revision 与隧道状态；Profile 读取返回已有 Token，编辑表单直接显示。只读身份可读取列表、运行时和日志；写入按钮与 Repository 要求宿主特权能力，Server 仍独立检查 Controller、资源归属及实际宿主条件。

配置编辑支持主机名/IDN/IP、服务器端口、None/Token、默认/禁用/强制 TLS、受管运行时或外部绝对路径。外部检测为显式请求，只验证指定宿主文件，不扫描手机 PATH；结果不自动改变配置。Token 在独立掩码表单替换，提交后清空界面值，只在当前请求内存中持有，不进入草稿、持久摘要、日志、诊断、OperationIndex 或保险箱。普通配置保存不设置 Token、不启动进程。

隧道支持 TCP/UDP 的远端端口，HTTP/HTTPS 的域名，以及本地主机/端口、启用、加密和压缩。改变协议后请求只发送该协议适用的端口或域名；字段编辑保留原 revision，服务器冲突后保留本机草稿，须明确回读替换，不能自动采用新 revision 覆盖别人修改。未保存编辑离开需确认，草稿不随进程恢复或账号切换持久化。

删除隧道只删除期望状态，旧运行配置中的转发仍可能继续，需再应用或停止。删除 profile 前必须移除关联隧道，Repository 再读 revision/关联事实，先停止其 frpc，取得明确停止结果后才删除配置和秘密；停止响应丢失时不继续删除。当前删除 API 不提供 revision CAS，跨客户端并发窗口仍由服务端归属/引用检查约束。

## 2. 保存、应用与连接事实

应用/停止均需针对名称和 ID 明确确认；应用核验并提交整份 profile 的启用隧道，可能重启 frpc、中断已有转发和管理连接。Windows 受管生命周期复用 `frpLifecycle` 与 profile ID 的授权挑战。页面分别显示本次同步操作的 `succeeded/state` 和新读取的隧道事实，不把 HTTP 200、期望状态已保存或进程启动当作连接成功。

服务端对应用的 profile revision、隧道 ID/revision 集合和受保护 Token 版本保留内存指纹。修改配置、增删移动隧道或替换 Token 后，仍在运行的旧连接投影为“已保存，尚未应用”；重新应用成功后才采用新指纹。缺少已应用指纹时显示未知，即使 Helper 报告进程在连接，也不能证明当前期望状态已应用。禁用项不显示为已连接。旧子进程迟到的日志/退出事件不能覆盖新实例状态。

`Connected` 仅证明 frpc 报告成功登录 frps，不证明每个 proxy 注册、远端端口/域名公网可达、TLS 客户端信任或管理通道安全。选择的 profile 含 Starting/Connected 项时页面每三秒只读观察；离页停止观察，不停止服务器进程。日志由用户显式读取，保留当前内存中的最后 200 行及观察时间，采用服务器脱敏结果，不保存或自动导出。

## 3. 同步请求丢失响应

FRP 配置、Token、删除、应用和停止是同步 API，没有领域 operation ID 或请求幂等键。请求前 [TunnelMutationJournal](../../app/src/main/java/app/relaxkonos/mobile/data/TunnelMutationJournal.kt) 在 `noBackupFilesDir` 原子保存 serviceId/账号、动作、目标及 profile ID；不保存秘密、正文、路径或 Token 哈希。每个身份只允许一个未决 FRP 写入，存储损坏/失败会阻止新写入。

明确成功或前置拒绝结束标记；传输、5xx、错误目标或未推进的 revision 保留待核实，阻止继续写入。恢复先读最新安全配置/隧道事实；用户确认采用事实时再次读取并只清除本机标记，不宣称原请求成功，不自动重发。TokenConfigured 不能证明曾提交的某个 Token 生效。运维中心单独列待核实动作并返回本页，不伪造 FRP 操作 ID。

## 4. FRP 运行时

统一 [Installations](Installations.md) 链路接入安装、升级、修复、卸载；回滚采用 Repair + rollback，安装和升级/修复要求固定版本。界面可按版本查询服务器信任清单的下载信息，显示当前/上一版本、路径和完整性，不自行接受任意 URL 或 latest。

Install 支持宿主下载、服务器包引用和系统文档选择器的手机包上传；升级/修复采用宿主可信来源。引用失效需重选，上传显示字节进度，上传成功不代表安装完成。Server 校验平台、发布清单、SHA-256 和归档边界。改变运行时前确认可能停止受管 frpc/frps、中断转发或管理路径，不承诺自动重启。

提交保留原 InstallationSubmission 和稳定键；未知结果只在明确重试时用原选项/原键。已接受任务显示原 ID、动作、状态、阶段进度和问题提示，活动任务在页内读取、按服务端 cancellable 确认取消；查询失败展示未知，不从缓存推断完成。首次响应完全丢失且无活动任务时保持未决，可重建完全相同选项复用原键，或由用户明确识别原操作 ID，校验服务/动作/已有 ID 后解除对应未决提交。另一个同服务任务或空活动列表都不构成原请求成功的证据。

## 5. 交互与范围

手机为 profile 列表 → 详情，600dp 起为列表/详情双栏。表单滚动并处理 IME，动作使用换行布局；切换宿主/账号清空草稿、秘密、日志、确认框与内存提交，旧响应不能覆盖新会话。新增文案与稳定状态/问题提示同步中文、英文、日文。实际手机/平板、大字体、旋转和恢复表现仍待执行验证。

共享执行语义见 [FRP 当前实现边界](../../../../docs/applications/RelaxKonOS.FRP_Integration.Implementation.md) 和 [Protocol](../../../../docs/architecture/RelaxKonOS.Protocol.md)。本功能不提供手机 SSH 本地转发、手机 FRP 进程、任意 TOML/插件/OIDC/visitor 配置；后续内置应用实现见 [BuiltInParity](../plans/BuiltInParity.md#41-下一轮起点)。

## 6. 宿主 frps

“受管 frps”分页读取安全配置，包括绑定 IP/端口、最多 64 个允许端口/范围、可选 HTTP/HTTPS vhost 端口、强制 TLS、Token 与 TokenConfigured、Dashboard 开关/绑定/账号/密码与 PasswordConfigured、状态、启动和观察时间。宿主 frps 为 HostGlobal 资源，不按某个 frpc profile 归属；它的运行状态和日志不能当作某个客户端隧道已连接的证据。

[ManagedFrps.kt](../../app/src/main/java/app/relaxkonos/mobile/core/net/ManagedFrps.kt) 与 [ManagedFrpsDraft/Manager](../../app/src/main/java/app/relaxkonos/mobile/ui/manage/tunnels/ManagedFrpsManager.kt) 直接采用当前契约。保存带原 `expectedRevision`（首次配置为 0），服务器按锁内当前版本校验并推进 revision；冲突保留非秘密草稿，须确认丢弃后回读替换。响应的 `revision/appliedRevision` 分别表示保存版本和当前进程的配置身份，不能把“保存成功”解释为运行配置已切换。活跃进程已应用版本不同或缺失时分别显示未应用或未核实。

Token 和 Dashboard 密码随普通读取和保存响应返回，配置编辑器直接显示已保存的值，刷新后回填。FRP 服务器 Token 同样回填并显示。关闭或切换会话清理编辑状态；日志、诊断和 Journal 不记录凭据正文。输入仍限制为长度受限的单行值，修订冲突需重新加载当前配置。

启动、停止和“重启并应用”须确认具体监听目标与影响。启动使用当前保存配置，不能绕过宿主端口/TLS/秘密/运行时校验；已经运行且配置版本不同的启动不会静默替换，要求停止后重启。重启先取得明确 `succeeded + disconnected` 停止结果再启动，丢失/异常/失败的停止结果不进入启动阶段。各阶段独立做认证/提权重试，启动挑战不能重复已完成的停止阶段。Windows 复用 `frpLifecycle + frps` 精确授权，profile 的授权不能替代它。停止可能断开全部 FRP 客户端和转发的管理路径；本页不会修改防火墙、客户端信任或手机网络设置。

Linux Server 重新打开已有配置但缺少原进程归属时返回 Unknown；不能根据文件存在宣称已停止或按名称杀进程。未知归属的停止明确失败，需在宿主核实/恢复原进程；新启动仍检查监听端口，成功建立新受管进程后才重新取得归属证明。Windows 从 Helper 读取实际进程状态，但 Server 缺少已应用 revision 时仍显示配置身份未核实。运行中的观察每三秒只读取安全状态，编辑期间暂停；离页/切会话停止观察。

frps 保存/启停/重启仍为同步 API，没有领域 operation ID 或幂等键。与 frpc 共用账号隔离的待核实写入标记，任何未决 FRP 写入阻止继续写入。frps 标记只在显式重新读取 frps 安全事实并确认采用后结束，frpc 列表不能解除它；秘密配置标记不证明某个秘密值已生效。运维中心继续按动作返回隧道页，不创建虚构长任务。

日志和审计由用户显式读取，每来源最多 200 条，仅保留当前内存和观察时间；审计显示保存、启动、停止、Token 编辑读取、成功/失败及本地化问题提示。运行状态只证明进程启动，不证明真实 frpc 认证、proxy 注册、vhost/Dashboard 公网可达或信任链。Android/设备、真实 FRP 二进制与 Windows Helper 验收仍按 [Verification](../status/Verification.md) 单独追踪。
