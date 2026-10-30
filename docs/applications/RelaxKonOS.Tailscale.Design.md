# Tailscale 管理器

## Ubuntu 实机控制验收（2026-10-30，MR-11）

Ubuntu 26.04 x64 从官方签名 APT 仓库外部安装 1.104.4，用户完成浏览器授权，设备在线。发现持久 Helper 传输层把领域 operationId 的后续读取/恢复误判为重放；Linux socket 和 Windows pipe 现让闭集 Tailscale 持久动词进入既有身份绑定台账，普通操作仍拒绝传输重放。新增真实 socket 回归通过；修复后的 Linux Helper 已部署，Windows 不计为实机验收。

原登录挑战发出后保留 Issued，不从后来 Running 推导原操作成功；经原操作恢复预览及确认关账解除门禁。已实测生产 API 的 DNS 接收关闭/恢复、断开/重连、四个原 Applied 回执和幂等执行重放；Server/Helper 重启后只读复核通过，最终配置为接受 DNS、不接收路由，组网直连测试通过。初次链接经原生状态交付用户；修复后的新一轮浏览器挑战交付未重新注册验收。

这证明该 Linux 版本的观察、控制与原任务恢复切片，不证明组件中心拥有 Tailscale 安装，也不关闭跨版本安装/更新/卸载、Windows 原生控制和故障中断验收。证据与构建摘要见[实机测试记录](../testing/RelaxKonOS.Tailscale.ComponentCenter.20261030.md)。

状态：桌面、服务端条件控制与未知操作核对/关账、首次登录 Helper 证据链与服务端协调/HTTP 路由、桌面及 Android 登录确认/浏览器交接/关账、Android 宿主观察/条件控制/原操作恢复已实现；运行时生命周期与真实宿主验收尚未完成。

Android 当前交互与生命周期规则由 [Android Tailscale 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Tailscale.md) 维护，设备及前后台验证要求见 [Android 验证文档](../../Client/RelaxKonOS.Client.Android/docs/development/Verification.md)。

使用当前 IServerModule 启动快照注册 relaxkonos.tailscale；禁用后不注册服务或路由，不启动后台工作。认证后的 GET /api/v1.0/tailscale/status 观察宿主 CLI 的 status --json。使用固定官方安装位置、不接受命令或路径输入，执行上限 10 秒、每流上限 1 MiB；失败与未安装分别表达。当前已有独立能力声明及桌面内置入口。

官方 CLI 契约：https://tailscale.com/docs/reference/tailscale-cli

运行时清单为 tailscale CLI 与 tailscaled（Windows 官方服务）。当前可观察外部实例并经授权变更其连接与 DNS/路由接收设置，不取得安装所有权，不安装、更新、修复、回滚或卸载。Linux /usr/bin 与 Windows Program Files/Tailscale 为当前探测位置，其他布局不在本切片范围。尚无真实平台或版本验收；不声明受支持版本范围。

后续实施与验收：组件中心来源界面、平台制品验证和生命周期事务，以及真实首次网页登录。Windows 官方 MSI 元数据与摘要发现已接入组件 releases 路由，尚不构成安装授权。安装来源限定官方签名系统仓库或已验证官方安装包，外部安装不得被自动接管。跨版本更新、同版本修复、保留数据卸载、回滚及故障恢复须分别实测；以下历史切片按追加记录阅读，以文末最新范围为准。

当前验证以文末最新实施记录为准，真实 daemon 验证尚未完成。

## 2026-10-28 实施进展

已实现：共享结构化 DTO 和当前协议路由 /api/v1.0/tailscale/status；server.tailscale 能力随模块快照发布；桌面内置应用注册、认证 typed HttpClient、设备状态列表、刷新重入与窗口移除取消、三语言资源。原始 JSON 不再外传，认证 URL、密钥、用户信息与底层诊断不进入 DTO。

Server 与桌面客户端构建通过（使用 -m:1）；独立 Tests/Tailscale 六项解析/契约检查通过。NuGet 漏洞元数据获取受网络限制。不声明真实 Windows/Linux daemon 验收。

当前为只读观察版本，不提供登录、断开、配置或安装按钮。完整控制链、Helper 授权、组件中心生命周期及真实宿主验收仍计划中。界面属于单用途观察页，采用单页设备列表作为多页工作区的例外；未来配置功能需拆出独立页面。宿主事实不持久化；窗口关闭停止等待，重新打开重新获取。未安装仅表示固定 CLI 位置缺失，不证明系统不存在其他安装布局。


## 按需驻留接入（2026-10-28）

已实现：TailscaleModule 实现 IServerModuleResidency；Host 仅持有 ITailscaleManager 的租约代理，由 TailscaleModuleRuntimeFactory 在首次业务调用创建领域对象。查询执行期间持有租约；空闲释放仅销毁模块对象，不停止 tailscaled、不删除配置、不卸载程序集。释放后下次调用创建新实例；宿主的模块状态查询不触发加载。沿用 ModuleResidency 的策略、空闲宽限和唤醒超时配置，不另建生命周期开关。

验证：Server 与专项工程构建通过；6 项契约/解析和 7 项驻留检查通过，覆盖初始休眠、持租约时不可释放、租约计数、空闲释放、重新创建实例与取消创建。NuGet 漏洞元数据不可达及既有代码警告未消除。尚未执行真实 daemon 控制或双平台验收。

连接控制与配置仍计划中：须先补齐固定 Helper 动词、HostNetworkChange 的资源范围授权、审计、幂等操作及断网回执，再开放桌面动作。当前模块对象驻留不代表取得外部 Tailscale 安装所有权。

## 状态观测边界与桌面重挂载（2026-10-28）

已实现：固定 status --json 命令抽为 ITailscaleStatusCommand；仅 Windows/Linux 探测固定路径，其他平台显式 unsupported。标准输出与错误分别按字节限制为 1 MiB，任一读取失败立即取消其余读取与进程等待，最终清理子进程；请求取消保持取消语义。命令失败统一返回安全问题码，畸形 JSON 与错误顶层类型返回 invalid-response。桌面新增 unsupported 三语言状态，刷新按钮提供本地化辅助名称；语言切换直接重绘已获取状态，不发送新查询。旧窗口请求完成时不得修改重新挂载窗口的刷新可用性。

验证：专项 24 项通过（解析/契约 6、驻留 7、命令/失败边界 11），Server 构建通过。命令专项验证精确 1 MiB 接受、超限拒绝并取消同伴、预先取消、失败诊断不外传与畸形数据。未执行真实 CLI 子进程超限、真实 daemon 或桌面交互验收；不扩大平台/版本支持声明。NuGet 漏洞元数据获取仍受网络限制。连接控制、配置、Helper 授权及组件中心生命周期继续为计划中，当前工作不构成完整产品验收。


## Helper 状态边界与设备授权（2026-10-28）

已实现：生产驻留对象改用 HelperTailscaleManager，经现有认证、关联与安全审计通道调用封闭的 TailscaleStatus 动词。Helper 只接受 operationId、当前协议版本和有效关联元数据；拒绝路径、任意参数、修订及其他领域字段。CLI 输出在 Helper 内转换为结构化状态，原始 JSON 和底层诊断不跨 IPC。Helper 失败、缺少结构化响应或协议不一致只显示不可用，不回退为 Server 直接执行。Windows 状态响应读取限定 15 秒并可停止等待；Helper 内命令仍独立限定 10 秒。

状态路由仍要求认证；设备列表另核对 HostNetworkChange 能力和精确资源 host/tailscale，每次请求重新判断。无授权时保留本机状态，移除 Peers 并设置 PeersRestricted；桌面用三语言说明该限制。授权沿用当前宿主身份和会话策略，本次未引入新的授权缓存。没有可用 Helper 的宿主会报告不可用，不能将其误报为未安装。外部安装仍不被取得所有权。

验证：专项 42 项通过（原 24 项、Helper 请求/响应边界 14 项、真实回环 HTTP 认证及资源过滤 4 项），Server、Helper 和桌面客户端构建通过。HTTP 测试使用测试认证处理器及替代的 CLI 结果，不构成真实用户登录、Helper 服务 IPC 或 daemon 验收；沙箱禁止回环连接，已在解除该限制的同一测试命令中完成验证。NuGet 元数据不可达及既有编译警告仍存在。

下一阶段仍需固定连接/断开/配置动词、资源范围内新鲜授权、配置修订与共同串行锁、提交前耐久记录及断网后原操作回执。读状态接入 Helper 不等于完成控制能力，也不构成运行时安装、更新、修复、回滚或卸载验收。

## 持久控制协调器（2026-10-28）

已实现但尚未接入生产路由：Shared 定义 Connect、Disconnect、Configure 的结构化意图、配置快照及操作回执；当前配置模型仅包含接收 DNS 与接收路由两个布尔开关，不接受原生参数、登录密钥或路径。TailscaleOperationCoordinator 将预览与明确确认后的执行分开，以 operationId、操作者、动作、预期修订和配置共同判定重复提交；同键不同内容拒绝。预览有效期五分钟，执行时在宿主共同文件锁内重新读取基线，并在读取后再次检查 HostNetworkChange/host/tailscale 授权。连接要求既有登录状态，不通过本链自动登录。

TailscaleOperationJournal 在 Server data/tailscale-operations 下保存受 Data Protection 保护的 SQLite 回执，synchronous=FULL，使用跨进程排他文件锁。只保存操作与预览基线，不把台账当成实时宿主事实源。调用 Helper 执行接口前先持久提交 Applying；此后 HTTP 取消不撤销宿主动作。只有返回匹配的原 operationId、完成证据和符合意图的观察才提交 Applied：连接/断开必须保留原配置，配置必须保留原连接状态。异常、身份或回读不匹配保留 Unknown。重开时 Applying 转为 Unknown，不能从新状态推导原任务成功，也不会自动重放。终态重复执行只返回原回执；查询原回执仍校验操作者身份。

验证：新增 38 项持久控制专项通过，全套累计 80 项；Server 与专项工程构建通过。使用真实 SQLite、Data Protection 和文件锁，覆盖加密保存、重新构造协调器读取、同键并发、提交前耐久记录、HTTP 取消边界、过期、授权撤销、修订冲突、结果身份和无关状态保持。宿主动作使用测试替代实现；重开是同进程新实例与同一保护提供者，不等于真实 Server/Helper 中断及重启验收。现有 NuGet 元数据网络警告未消除。

未完成：ITailscaleControlHost 的生产 Helper 实现、配置修订生成与 Helper 内二次比较、特权执行共锁及独立回执、控制 HTTP 路由、桌面预览/风险确认/提权和断线后操作重开。当前公开界面继续只读，不因协调器专项通过而宣称已经可以连接、断开或配置 Tailscale。组件中心来源与完整运行时生命周期、真实 Windows/Linux daemon 验收继续待实施。


## Helper 条件控制与原始回执接入（2026-10-28）

已实现：Helper 的 TailscaleControlRead、TailscaleControlExecute、TailscaleControlReceipt 固定动词，当前版本、关联 operationId 和请求形状在特权边界复核。原生控制固定为 tailscale up、tailscale down，以及 tailscale set --accept-dns=<bool> --accept-routes=<bool>，不支持任意参数、退出登录或密钥登录。控制观察使用官方 get --json 和前后两次 status --json，要求可识别的已登录 Running/Stopped 节点；认证 URL、已过期节点或不认识的输出不准入。完整公开偏好按属性名规范化后与节点身份、版本和连接状态生成 SHA-256 修订，设备列表变化不改变修订。缺少 get 能力的 CLI 明确不可控制，不尝试其他格式。

每次执行在 Helper 自有跨进程文件锁内重新核对修订；在保护目录发布不可覆盖的 <operationId>.intent.json 后才调用 CLI，实测成功及回读符合目标后发布独立 result.json。文件先 Flush(true)，Linux 发布后另同步目录。Windows 目录为 Program Files/RelaxKonOS-TailscaleControl，Linux 为 /var/lib/relaxkonos-tailscale，仅管理员/root 可写；执行前核对固定 CLI 及父目录的所有权、写权限与链接。命令使用固定最小环境，输出不跨越 Helper。状态观察也接入相同的可执行文件保护检查。操作证据不自动清理，保留期为宿主安装存续期间；不持久化原生偏好正文、登录 URL 或秘密。

同一意图重复提交只读取原结果；相同 ID 的不同意图拒绝。缺失结果或结果未知的意图阻止其他新变更，状态会报告 recovery_required，不通过重试覆盖证据。异常、执行超时或不符合回读的操作不生成成功回执。尚无人工核对关账入口，因此这类故障需要后续恢复流程，不能删除台账绕过门禁。

Server 的 ITailscaleControl 使用驻留租约代理；内部授权桥每次创建短生命周期作用域，重新检查当前 HostNetworkChange/host/tailscale 授权，不捕获请求作用域对象。已接通 GET /api/v1.0/tailscale/control、POST /api/v1.0/tailscale/operations、POST /api/v1.0/tailscale/operations/{operationId}/execute 和 GET /api/v1.0/tailscale/operations/{operationId}。所有路由要求认证；预览与执行要求当前宿主授权，执行必须明确 Confirmed。读取原回执校验原操作者。

Server 响应丢失后，重开仅接受 Helper 为原 operationId 保存的完成证据；Helper 结果不兼容、缺失或未知不能根据当前宿主状态补判成功，也不重放执行。Helper 控制路径总预算 45 秒，单个 CLI 仍限定 10 秒；Windows 可取消的只读 IPC 观察限定 50 秒，已提交变更继续等待其独立完成。两侧文件锁约束 RelaxKonOS 自身操作；上游 CLI 没有对外部同时修改者提供原子 CAS，因此仍需真实平台竞争验收，不把读取修订描述成对所有宿主写入者的全局锁。

当前已验证：原生修订/参数/Helper 证据专项、Server→Helper 原始回执链与响应丢失恢复专项、真实回环 HTTP 的预览/确认/执行/重开；使用替代原生命令，不改变本机 Tailscale 状态。Helper 和桌面客户端构建通过。最新完整专项数量与最终构建结果将在最终重跑后记录；模块注册文件的并行拆分曾暂时导致缺失/重复成员编译错误，未据此覆盖其他工作树改动。

仍未完成：桌面配置独立页面、风险预览/明确确认/提权及原任务重开、未知 Helper 操作的人工核对恢复、首次网页登录流程、组件中心官方来源与所有生命周期事务、真实 Windows/Linux CLI/daemon、权限及跨版本验收。本实现没有取得外部安装的生命周期所有权，也不宣布任何实测支持版本范围。

原生命令依据：[官方 CLI 文档](https://tailscale.com/docs/reference/tailscale-cli)，get 的 JSON 属性依据[上游 get 实现](https://github.com/tailscale/tailscale/blob/main/cmd/tailscale/cli/get.go)。这些文档依据不代替本产品的版本实测。

本阶段最终验证补充：Tailscale 专项 **136 项通过**（解析/契约 6、状态驻留 7、生产控制驻留/作用域 3、命令失败 11、状态 Helper 边界 14、持久控制 38、原生修订/Helper 回执 35、Server/Helper 原操作证据链 11、HTTP 认证与控制 11）。包括 ValidateScopes/ValidateOnBuild 与真实模块租约代理；HTTP 使用测试认证、宿主命令使用替代实现，不执行本机组网变更。

最后一次专项编译使用 `dotnet build Tests/Tailscale/RelaxKonOS.Tailscale.Tests.csproj --no-restore --no-dependencies -m:1`，针对本轮此前已成功构建的 Server 程序集运行；随后的整棵 Server 重建被工作树中另行拆分的 ServerModules 和 FileOperationService 暂态缺失/重复成员阻断。未修改这些其他工作。故 136 项不能替代最新整棵工作树编译证明。Helper、桌面客户端本轮构建通过，NuGet 元数据网络警告和既有编译警告仍存在。真实 CLI、daemon、OS 权限保护与中断验收仍未执行。


## 桌面条件控制、确认与原操作重开（2026-10-28）

已实现：桌面工作区拆为独立 AXAML 的设备、连接与配置、操作记录三页，替代早期单用途观察页。设备表格使用剩余弹性区域和可调整的固定初始列宽；宽度小于 720 DIP 时导航移到顶部。配置编辑与最终风险确认均使用可滚动正文、固定右下决策页脚。确认列出原宿主、动作、DNS 与路由接收前后值，并明确可能中断当前远程连接且没有自动回滚。取消编辑、配置未改变、无确认回调、取消确认、预览过期或不匹配的响应均不执行。

桌面使用 ITailscaleClient/RemoteTailscaleClient，复用 HostSettingsService 的宿主、账户及登录会话绑定与令牌刷新；在提权、预览、编辑、确认及响应返回后重新检查当前连接。所有变更经统一 IHostElevationBroker 的 HostNetworkChange/host/tailscale 入口，只在 Server 明确要求授权时尝试一次授权后重试。Tailscale HTTP 授权拒绝已对齐统一入口：403、问题类型 elevation-required，业务问题码仍为 tailscale.elevation_required。未添加旧状态码或旧类型兼容路径。

本地只保存宿主/账户与原 operationId 引用，在提交变更前必须成功耐久保存；保存失败不执行。设备路径为当前 OS 用户 LocalApplicationData/RelaxKonOS/tailscale-operations.json；不含令牌、权限授予、配置或结果。按宿主及账户保留最近 30 项，全设备最多 256 项，重开新登录仍可读原账户引用；使用排他锁与原子发布，跨进程争用时拒绝写入而不冒险提交无引用变更。损坏记录不被当作成功历史。清理客户端此文件只清理引用，不更改 Server/Helper 回执。

执行响应丢失后，当前操作标记为待核验，控制动作暂时禁用；重开只调用原操作 GET，不重放执行。读取其他历史回执不清除当前操作的未决提交标记。重新打开窗口只载入引用，结果由服务端重新读取。窗口移除取消等待，切换宿主、账户或登录使旧观察与控制状态失效，迟到结果不能覆盖新窗口或新连接。语言切换原地重绘状态与设备行，不重新查询宿主。首次打开不主动提权，只读状态；控制页由用户明确请求读取/授权。

验证：Server/Helper 专项 **136 项**、桌面行为/传输/布局专项 **107 项**通过，共 **243 项**。已通过统一入口 `./Tools/verify.ps1 -Suite tailscale-server,tailscale-client -NoBuild -KeepLogs`，日志位于本次 .tmp/verify/20261028-220735。完整 Server/专项与桌面构建本轮通过，已消除前一阶段整树构建暂态阻断；既有编译警告仍存在。新增测试项目使用仓库既有 Avalonia.Headless/Desktop 依赖，不新增产品运行时依赖。

桌面专项使用替代宿主命令与 HTTP 处理器，覆盖危险确认、配置未变、持久引用失败、宿主切换、响应身份、迟到响应、窗口关闭、原操作重开、历史引用隔离和保留上限、Bearer/路由与会话绑定。已渲染三语言、980/540 DIP 三页，以及配置和确认对话框，并检查打开状态下语言切换与深色主题；共产出 27 张预览，路径为 Tests/Client/RelaxKonOS.Tailscale.Client.Tests/bin/Debug/net10.0/layout-qa。预览经过人工图像检查。这些是本地无界面渲染与替代服务验证，不代替真实窗口系统、真实宿主权限或 Tailscale daemon 验收。

仍未完成：未知 Helper 操作人工核对/恢复与安全关账、首次网页登录流程、组件中心来源与完整运行时生命周期、真实 Windows/Linux 控制/权限/版本及故障中断验收。当前不得以桌面操作链和模拟证据宣称完整产品已经完成。



## 未知操作核对与人工关账（2026-10-28）

已实现：原操作者可在操作记录中读取原回执、核对当前宿主状态，并在新鲜 HostNetworkChange/host/tailscale 授权和明确确认后关账。Server 新增 GET /api/v1.0/tailscale/operations/{operationId}/recovery 与 POST /api/v1.0/tailscale/operations/{operationId}/recovery/close；路由与请求的 operationId 必须一致。核对计划在受保护 Server 台账保存预期修订和五分钟有效期；关账前重新检查授权、计划期限及 Helper 实测修订。提交前耐久保存 ClosingRequest，之后 HTTP 取消只停止等待。

Helper 新增封闭的 TailscaleRecoveryRead、TailscaleRecoveryClose、TailscaleRecoveryReceipt 动词，拒绝无确认、非 GUID 操作者、错配身份及其他领域字段。操作者由 Server 的已认证身份提供，HTTP 关闭请求不能指定 Actor。核对与关账均持有同一原生操作文件锁；必须找到原始意图且不存在成功结果，检查没有名为 tailscale 的 CLI 实例活动，连续两次完整原生观察相同并可解析，关账时再次核对预期修订。活动 CLI、状态漂移、过期核对、权限撤销及身份不符均拒绝关账。进程检查保守覆盖所有同名 CLI，不自动终止其他进程。

成功关账在原 Helper 保护目录新增不可覆盖的 <operationId>.closed.json，保存原 operationId、实测状态、操作者和关闭时间。它只解除该意图的未决变更阻断；原 intent.json 与 result.json 不修改，缺失的 result.json 不补造。Server 操作 State 仍为 Unknown、原问题码保留，以独立 Closure 字段展示人工接受当前状态的证据。关账不重放连接/配置、不回滚状态，也不生成原操作成功证明。缺少关闭时间、配置观察或其他必需身份字段的关账证据拒绝使用。相同操作者和修订的重复请求返回原关账证明；不同修订或操作者不能借用该证明。

关账响应丢失后，GET 原操作从 Helper 的原关账回执对账；不执行新的原生状态查询来推导关账成功。已关账操作不再根据之后的当前状态改判为 Applied。其他尚未关账的未知意图仍阻止新变更。Helper 没有原始意图、原操作者 Server 台账缺失、保护目录或 CLI 不可核验时，公开接口拒绝推断关账；不能通过删除原证据绕过门禁。该链不承担 Server 台账丢失后的恢复或运行时安装事务补偿。

桌面已增加核对与关账入口、当前连接/DNS/路由的观察摘要，以及独立固定页脚确认对话框。未核对、观察不稳定、取消确认、预览过期、切换宿主/账户或未知回执身份不匹配都不提交。关账后显示“原结果仍为未知”；响应丢失时保持待核验，重新读取原操作或核对原关账证据均可收口，不重试关账。所有新增文字均提供三语言资源并随语言切换刷新。

验证：Server/Helper 专项 **185 项**、桌面专项 **130 项**，共 **315 项通过**。新增恢复专项 38 项覆盖权限、原操作者、CLI 活动、状态漂移、缺失/过期计划、执行前授权撤销、原始文件摘要不变、关账幂等、响应丢失、重开、无原始结果的中断意图，以及关账后新操作准入；HTTP 认证/控制/恢复组增至 20 项。使用真实文件与 SQLite，但原生组网行为由替代实现提供。另有 2 项在当前 Windows 环境使用隐藏的测试自有系统工具进程验证生产同名 CLI 活动检测；只终止该测试子进程，不调用或修改真实 Tailscale。

三语言资源各 91 个 key，当前渲染预览增至 30 张，包含新增人工关账确认；已人工检查其滚动正文与固定页脚。Server、Helper、桌面及两个专项工程构建通过，既有编译/元数据网络警告未消除。统一验证入口 ./Tools/verify.ps1 -Suite tailscale-server,tailscale-client -NoBuild -KeepLogs 通过，日志为 .tmp/verify/20261028-224845。

CLI 进程检测及连续观察只用于安全关账准入，不表示对上游 daemon 异步工作或其他宿主写入者拥有全局事务锁。尚未执行真实 Tailscale Windows/Linux daemon 中断、远程断线、权限保护或版本验收。首次网页登录、组件中心官方来源与完整安装/多版本更新/修复/回滚/卸载及故障恢复仍待实施，完整产品目标未收口。

## 首次登录原生适配与链接隐私（2026-10-28）

已实现但尚未接入公开操作：TailscaleNativeLogin 使用固定 status/get --json 读取前后基线，只有 NeedsLogin 且没有现存认证链接时才尝试固定 up --json --timeout=5s；再次核验可执行文件信任，适配总预算 45 秒，单个进程仍限定 10 秒及每流 1 MiB。未加入 auth-key、force-reauth、reset 或自定义 login-server 参数。原生登录可能改变 daemon 状态；本切片不是对任何未验证配置或版本的生产准入许可。

根据官方 up 实现，JSON 输出由连续的多个对象组成，CLI 自身等待超时的非零退出前可能已经输出认证链接。解析器按当前结构读取最多四个完整对象、限制嵌套深度，拒绝重复或未知字段、尾随杂音、矛盾终态、轮换链接及原生 Error，不尝试解析自然语言错误。只有正式退出并得到有效结构的输出才可解释；进程被外层超时或取消时，不从残留输出恢复链接。Running 必须同时具有原生命令零退出与回读 Running，不能把非零退出包装为完成。

认证链接限定原始字符串精确匹配 https://login.tailscale.com/a/ 后的 10～256 位 ASCII 不透明令牌；拒绝非 HTTPS、用户信息、显式端口、相似域名、查询、片段、百分号、反斜线及空白，不扩大到任意官方域名或自建控制端。链接产生后须与 daemon 当前 AuthURL 再次精确相等，偏好摘要保持不变；否则拒绝发布结果。QR 与原生错误正文不返回。非默认配置与首次启动默认行为仍须独立验证，回读发现变化不表示已经具备自动补偿能力。

链接仅保留于可单次取用、可释放引用的内部内存容器；普通 JSON 序列化和 ToString 只暴露安全状态。基础命令捕获也对原生 stdout/JSON 增加隐式序列化与字符串诊断脱敏，stderr 继续丢弃。基线只含版本/节点/状态/链接指纹及公开偏好的摘要，不保存认证 URL。没有新增包含认证 URL 的 Shared 状态 DTO、Helper 路由或 HTTP 接口。

验证：新增 **53 项**首次登录原生/隐私专项通过，Server/Helper 总计 **238 项**、桌面 **130 项**，共 **368 项通过**。覆盖链接来源、连续对象、重复字段、格式及错误输出、单次取用与释放、隐式序列化、既有挑战拒绝、修订冲突、命令参数白名单、偏好漂移、外来挑战、原生完成回读、发起前和发起后取消、进程超时与普通 CLI 等待超时的区别。原生命令使用注入替代实现；未在真实 daemon 上登录。Server/专项与 Helper 构建通过，既有警告仍存在。统一验证日志为 .tmp/verify/20261028-231843。

后续仍需：把登录发起作为独立的、确认及授权绑定的持久操作；登录链接仅通过专用短期授权通道交付，绑定原宿主、账户、会话、operationId 与已核对的挑战摘要；不得进入普通状态、日志、磁盘回执或长期缓存。“请求已发起”的原回执与后来观察到的已登录状态必须分开，不得凭未来 Running 补写原请求成功。取消/过期须明确区分停止等待、停止展示链接与 daemon 端认证撤销，当前适配器并不承诺撤销已产生的上游认证。上述操作协调、短期授权、未知发起对账、桌面浏览器交接及真实版本/平台准入仍未完成，因此公开界面继续不提供首次登录动作。

依据：[官方 up 实现及输出契约](https://github.com/tailscale/tailscale/blob/main/cmd/tailscale/cli/up.go)。上游明确提示 JSON 格式可能变化；此解析与源码检查不构成本产品的支持版本声明。组件中心可信来源与完整运行时生命周期及双平台真实验收仍保持原目标，尚未完成。

## 登录挑战受限重读（2026-10-28）

内部原生适配新增只读挑战重取：输入原挑战 SHA-256 摘要与完整偏好摘要，要求 daemon 仍为 NeedsLogin 且已有挑战；读取前后完整基线相同，当前 URL 通过精确官方来源检查并匹配原摘要后，才放入单次取用容器。总预算 30 秒，取消立即拒绝交付。偏好变化、挑战轮换、读取中漂移、已完成登录及错误摘要均拒绝；不会再次执行 up，也不会凭当前状态补写原操作结果。

新增 10 项检查通过，首次登录专项共 63 项，Server/Helper 共 248 项、桌面 130 项，总计 378 项。专项与 Helper 构建通过（既有警告保留）；统一验证日志 .tmp/verify/20261028-232925。测试使用替代原生命令，未执行真实认证。

此切片没有持久登录发起回执、会话授权或有效期门禁，也未开放公共调用。调用者必须先验证原宿主、账户、会话、操作和期限；这些协调层仍待实现，不能把内部摘要匹配当作授权。完整首次网页登录、运行时生命周期和真实双平台验收仍未完成。

## 持久登录发起回执（2026-10-29）

已实现但尚未接入公开调用：Helper 新增内部登录台账，登录发起被建模为绑定原宿主、账户、会话、operationId、预期修订和五分钟内到期的持久意图，并要求显式确认。意图在调用固定 `tailscale up --json --timeout=5s` 之前发布到保护目录；意图耐久后，调用方取消只停止等待，不撤销已提交的原生动作。结果只能记录为 issued（仅保存认证链接的 SHA-256 摘要）、completed、approval-required 或 unknown；缺少结果时，之后观察到 Running 也不会补写成功。重复提交只返回原回执，同一 ID 的不同身份或内容拒绝；同一 ID 已存在控制意图、登录结果或关账证据时不得复用。

任何未完成且未关账的登录意图都会使 Tailscale 控制读报告 recovery_required，并阻止新的连接、断开、配置及新的登录发起。链接重取要求原回执为 issued、意图未过期、当前挑战摘要与偏好摘要相符、CLI 无活动进程，并在读取前后再次核对；链接只进入一次性、可释放的内存容器，不进入台账、序列化文本或诊断。核对与关账使用连续两次一致的原生观察并复核预期修订；成功关账写入独立不可覆盖的 `<operationId>.login-closed.json`，不改写原意图或结果，不重放登录，也不把 Running 解释为原发起成功。

台账只保存身份、修订、状态、时间与摘要，认证链接、原生偏好正文、登录密钥及底层诊断均不落盘；回执读取拒绝未知字段、重复属性与超过 64 KiB 的文档。测试新增 66 项，覆盖未确认、无效身份、过期/超期计划、非静默 CLI、共享锁取消、意图先于原生调用、取消后仍发布结果、重复提交幂等、外来身份重取与关账、过期后重取、completed 后禁止恢复/重取、关账幂等、损坏或重复身份证据、未知字段结果、进程丢失后的 unknown、捕获超时 unknown、调用方取消后 issued、遗留证据拒绝复用，以及关账后控制恢复。Server/Helper 专项累计 **314 项**；桌面专项 130 项未变，共 **444 项**。Helper 与专项工程构建通过，既有编译警告与 NuGet 元数据网络警告仍在；统一验证入口 `./Tools/verify.ps1 -Suite tailscale-server -NoBuild -KeepLogs` 通过，日志为 `.tmp/verify/20261029-064945`。

验证使用替代原生命令、临时目录和真实文件锁，不调用真实 Tailscale，不改变本机状态；未执行真实 daemon 登录、浏览器认证、权限保护或版本验收。

未完成：Helper 尚未注册登录动词，Server 尚无登录协调器、会话授权与 HTTP 路由，桌面无发起、浏览器交接与关账界面。链接交付通道必须绑定当前认证会话、权限与 operationId，并由 Server 再次校验；不得以内部摘要匹配代替授权。首次网页登录、运行时生命周期及双平台真实验收仍保持原目标。

## 首次登录 Helper 动词与服务端协调（2026-10-29）

已实现：Helper 注册封闭登录动词（Observe、Begin、Receipt、Challenge、RecoveryRead、Close、RecoveryReceipt），全部复用同一原生文件锁。请求必须携带与服务端持久计划一致的宿主、账户、会话、operationId、预期修订和期限，Begin 另需显式确认；形状不符、身份不符或未确认均在特权边界拒绝。挑战只在原回执为 issued、意图未过期、CLI 无活动且摘要匹配时重取；跨 IPC 的挑战类型在 `ToString` 中隐藏 URL，HTTP 挑战响应标记 `Cache-Control: no-store`。Helper 冲突映射为稳定的 conflict，超时或不可用映射为 unavailable。

共享契约新增登录意图、无 URL 回执（仅挑战摘要）、观察、关账与挑战交付类型；服务端操作从不保存 URL。服务端新增 TailscaleLoginCoordinator 与独立加密台账 `data/tailscale-login`：准备阶段要求当前 HostNetworkChange/host/tailscale 授权，绑定 `sub`、`sid` 会话与 ServerRuntimeSettingsStore 的实例 ID，观察必须仍可发起，计划五分钟有效；执行前再次观察并要求修订一致，耐久写入 Applying 后才调用 Helper，HTTP 取消不再撤销已提交的宿主动作；Applying 重开转为 Unknown 且不重放。结果只接受 issued/completed/approval-required，其他或异常保持 Unknown。

认证链接仅通过 GET `/api/v1.0/tailscale/login/operations/{id}/challenge` 交付，要求同一会话、当前授权、状态 issued、未过期且未关账；链接不进入操作记录、日志或普通状态。恢复读要求连续稳定观察并保存五分钟预览；关账要求明确确认与预期修订一致，写入独立关账证据，原状态保持 unknown/issued 不变，重复关账返回原证明。新增路由 `/tailscale/login/operations`、`…/execute`、`…/{id}`、`…/challenge`、`…/recovery`、`…/recovery/close`，全部要求认证，变更操作要求新鲜授权；响应丢失后以原 operationId 读取，不重放。

验证：新增 Helper 登录桥 20 项、服务端登录协调 33 项、HTTP 登录 11 项，登录相关共 64 项；Server/Helper 专项 **379 项**通过（含 4 项生产模块图解析与登录代理驻留），桌面 130 项未变，共 **509 项**。统一验证入口 `./Tools/verify.ps1 -Suite tailscale-server -NoBuild -KeepLogs` 通过，日志为 `.tmp/verify/20261029-072023`。测试使用替代原生命令、真实文件锁、加密 SQLite 与真实回环 HTTP，不改变本机 Tailscale 状态。

未完成：桌面尚无首次登录入口、风险确认、浏览器交接与关账界面；没有真实 daemon 或浏览器认证验收。已有挑战时不允许发起新登录，也不提供 auth-key、force-reauth 或 reset，因此重置尚未登录的旧挑战仍需上游完成或人工关账。组件中心官方来源与完整运行时生命周期仍未完成。

## 登录交付边界修复与 Android 观察入口（2026-10-29）

服务端在挑战交付前重新核对有效期、operationId、精确官方链接格式、NeedsLogin 状态、观察修订格式及原偏好摘要；等待 Helper 期间过期不得再交付链接。固定官方链接规则由 Shared Protocol 定义，Helper 原生解析复用同一规则，不扩大允许来源。HTTP 挑战 DTO 的 ToString 隐藏认证链接。原登录回执对账拒绝错配 operationId 或缺少记录时间的证据，不把错误回执升级为成功状态。登录执行和人工关账在等待宿主观察后再次检查计划有效期；过期时不调用 Begin/Close。协调器使用可注入时钟验证这些等待边界，生产默认使用系统 UTC 时钟。

Android 新增按 server.tailscale 能力可见的只读入口。移动端当前流程和支持边界由 [Android Tailscale 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Tailscale.md) 维护，不在此复制详细交互规范。

验证：Server/Helper 专项 **390 项通过**，其中登录协调器由 33 项增至 44 项；Android 新增 **6 项单元测试通过**，覆盖当前状态解析、设备字段、受限设备过滤、未知状态拒绝、能力缺失和响应期间切换宿主。Server、Helper 和 Android 主代码/测试代码编译通过。日志为 `.tmp/tailscale-goal-build.log`、`.tmp/tailscale-goal-tests.log`、`.tmp/tailscale-helper-build.log`、`.tmp/tailscale-android-build.log` 和 `.tmp/tailscale-android-tests.log`。Android 使用专用 `.tmp/tailscale-build` 输出目录避开原构建产物占用。保留既有编译警告和 NuGet 元数据网络警告。

以上测试使用替代宿主命令、临时文件与回环 HTTP，不操作真实 Tailscale。尚无本轮 Android 设备界面、真实 daemon 登录/浏览器交接或组网验收；桌面首次登录界面、移动端控制与持久操作恢复、组件生命周期和双平台真实验收继续未完成，目标不据此收口。

## 桌面首次登录与原操作重开（2026-10-29）

桌面新增独立首次登录页，使用会话绑定的 typed HTTP 客户端调用当前登录路由。只在用户发起后准备计划，经统一 HostNetworkChange/host/tailscale 提权入口、响应身份核对、本地耐久引用保存及明确风险确认后执行。未确认、保存失败、计划过期、宿主/账户/会话切换或窗口移除均不提交后续动作。原操作响应丢失保持待核验，读取其他历史不能解除该标记；重开只 GET 原 ID，不自动重放登录。登录页只加载本地引用，不在打开工作区时发起登录或提权。

登录引用存于当前 OS 用户 LocalApplicationData/RelaxKonOS/tailscale-login-operations.json，复用排他写入、原子发布与保留上限，按宿主、账户和原登录会话隔离；仅保存身份引用，不含 URL、配置、授权或结果。普通控制历史保持原有宿主/账户作用范围，两个文件互不混用。重新登录后的新会话不能借用旧会话的登录挑战。

仅 issued、未关账且未过期的原任务提供“打开官方登录页”。用户单独点击后重新向服务器请求短期挑战，复核原 operationId、计划期限及精确官方 HTTPS 来源，再交给当前设备的系统浏览器；不遵循把普通链接发送到远端宿主的偏好。不自动打开、不复制到剪贴板、不把链接放入 ViewModel 属性、磁盘或操作记录。网络诊断的采集与入库两层均排除此挑战接口，避免开发模式缓存认证链接。浏览器交接只说明已请求打开，不代表用户认证或 daemon 登录完成。

原任务为 issued、unknown 或 approval-required 时可经新鲜授权读取稳定观察，再明确确认关账。修复了服务端只允许 issued/unknown 关账、但 Helper 将 approval-required 同样视为未决意图而导致永久阻断的缺口；该状态现在具有相同核对和关账入口。客户端检查原操作、操作者、会话、修订、期限与关闭证据；关账响应丢失只重读原回执。原状态不改写为 completed，取消等待或人工关账均不承诺撤销上游认证挑战。

验证：Server/Helper 专项 **393 项**、桌面行为/HTTP/布局专项 **198 项通过**。服务端新增 3 项等待设备授权关账检查；桌面新增登录确认/取消、坏计划、本地引用失败、失去响应重开、历史隔离、浏览器来源与迟到响应、关闭证据及 approval-required 恢复、窗口移除、HTTP 路由/认证/会话绑定和诊断排除检查。桌面与专项工程编译通过，保留既有警告。日志为 `.tmp/tailscale-login-server-build.log`、`.tmp/tailscale-login-server-tests.log`、`.tmp/tailscale-login-client-build.log` 和 `.tmp/tailscale-login-client-tests.log`。

实际 AXAML 渲染覆盖三语言、980/540 DIP 四页及登录发起/关账确认对话框；已检查窄屏导航、滚动正文和固定页脚，并修正重复介绍文字及关账观察的语言刷新。预览位于 `Tests/Client/RelaxKonOS.Tailscale.Client.Tests/bin/Debug/net10.0/layout-qa`。使用替代 HTTP/宿主与本地无界面渲染，没有打开真实浏览器或操作真实 Tailscale；不构成真实认证、远程断线、OS 权限或版本验收。移动端仍为只读观察，组件中心来源、完整运行时生命周期及真实双平台验收尚未完成。

## Android 条件控制与原操作恢复（2026-10-29）

Android 已接入当前条件控制、预览／明确确认、原操作回执读取及未知结果核对关账路由，使用统一宿主网络提权与耐久引用门禁。移动端界面、操作隔离、保留上限和当前不支持的功能以 [Android Tailscale 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Tailscale.md) 为唯一详细规范；没有新增或保留旧格式 API。

本轮 Android 主代码、测试代码编译及 **42 项 Tailscale 单元测试通过**（状态／会话 6、控制与存储 21、协议 7、界面状态机 8）。覆盖确认和期限、精确提权资源、提权等待期间过期、提交前存储、原回执错配、响应丢失及原 ID 读取、并发读取、取消后的迟到响应、跨账户隔离、原生临时文件排他锁与重开、保留期不淘汰未决引用，以及未知结果关账回执恢复。三语言资源各 56 个 key，已检查无重复。最终离线 Gradle 命令为 `:app:testDebugUnitTest --tests '*Tailscale*'`，使用 `.tmp/tailscale-build.init.gradle` 独立输出配置；日志为 `.tmp/tailscale-mobile-control-tests.log`。

测试不操作真实 Tailscale；文件测试在 Windows 临时目录验证文件同步、原子发布和排他锁，Android 目录 fsync 使用可替换回调检查执行顺序与失败门禁，未宣称已经验证真实 Android 断电耐久性。ADB 当前无连接设备，没有执行本轮手机／平板、旋转、TalkBack、真实授权或断线交互验收。移动端首次登录、运行时来源及完整生命周期、真实双平台 daemon／版本／权限验收仍未完成，目标保持进行中。

## Android 首次登录及新会话恢复（2026-10-29）

Android 已接入首次登录计划、明确确认、原操作读取、显式浏览器交接和登录核对关账，详细规则由 [Android Tailscale 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Tailscale.md) 维护。挑战不进入 ViewModel 状态或持久引用；移动端登录记录与普通控制记录分离，错误诊断隐藏认证 URL。

修复登录恢复的会话门禁缺口：此前客户端重新登录会导致旧 sid 不再可用，原意图又可能继续阻止所有宿主变更。现行授权规则为：发起计划、执行以及挑战交付仍要求原账户及原会话；读取原操作允许同一账户的新认证会话，核对与关账另要求当前新鲜 HostNetworkChange/host/tailscale 授权及明确确认。Helper 继续读取原 intent，不替换原 SessionId，也不重新执行登录。其他账户不能读取原回执。同账户的新会话不能执行旧计划或领取旧链接。桌面和 Android 的本地登录引用均按宿主／账户保存以便恢复；界面明确告知挑战只可由原登录会话领取。

验证：Server/Helper 专项 **401 项**、桌面行为／HTTP／布局专项 **198 项**、Android Tailscale 专项 **71 项通过**。服务端新增同账户新会话读取／关账、原会话专属执行／挑战、其他账户拒绝和当前授权门禁检查，含真实回环 HTTP、替代认证处理器。Android 增加登录协议 6 项、登录操作／隐私 15 项、登录界面状态机 8 项，覆盖原任务持久引用、响应丢失不重放、原链接来源和期限、会话切换、晚到响应、后台／离页不打开浏览器、等待设备授权关账及新会话恢复。三语言资源各 71 项。Server、桌面及 Android 主代码／测试代码编译通过，既有编译警告与 NuGet 元数据网络警告保留。

日志为 `.tmp/tailscale-login-session-server-build.log`、`.tmp/tailscale-login-session-server-tests.log`、`.tmp/tailscale-login-session-client-build.log`、`.tmp/tailscale-login-session-client-tests.log`、`.tmp/tailscale-mobile-login-build.log` 和 `.tmp/tailscale-mobile-login-tests.log`。测试不打开真实认证页，不调用真实 daemon，不构成真实用户认证、Android 设备界面、权限保护、断电耐久性或远程断线验收。运行时来源、完整安装／多版本更新／修复／回滚／卸载，以及双平台真实 daemon／版本／中断验收仍未完成。

## Helper 公共锁修复与 Windows 官方来源发现（2026-10-29）

修复特权控制入口未获取已有 native.lock 的实现缺口。控制读取、连接／断开／配置、原回执读取和核对／关账现在实际持有与登录操作相同的跨进程文件锁；首次登录观察也纳入锁，并在存在未决意图时撤下 CanStart。读操作不会在同一 Helper 操作的原生执行中途读取意图并解除阻断，重复 operationId 在锁内读取原证据，竞争变更重新核对修订。该锁只约束 RelaxKonOS 的特权操作，不能代替上游 CLI 对其他宿主写入者的原子 CAS。

控制回执读取改为严格当前 JSON 结构：拒绝重复属性、未知字段、遗漏必需字段、完成回执缺少有效观察／配置／修订，以及完成证据附带问题码的矛盾形状。损坏证据不会被视为已完成或解除未决门禁；不通过回填当前状态、覆盖原回执或删除意图恢复。

Windows 官方 MSI 发现已接入认证组件 releases 路由 `/api/v1.0/components/tailscale/releases`：按 OS 架构选择 win-x64／win-arm64；未知平台或架构明确拒绝，不静默当作 x64。固定读取官方 stable 的 `MSIsVersion` 和 `MSIs` 当前结构，文件名须精确匹配版本及架构；另读取官方 `.sha256`。共享解析器内部支持明确历史版本重新解析，不从文件名猜测成功，不接受旧字段替代、任意 URL、预发布标签或奇数 minor 的不稳定版本。

传输复用运行时 HTTPS 的 DNS／实际地址校验、大小和总时限、每跳重定向与重试策略，并使用服务端解析的宿主下载代理配置；代理不可用时不退回直连。只有固定官方元数据请求获得精确查询参数许可，普通签名目录及归档仍拒绝查询参数。元数据至多 64 KiB、摘要至多 128 字节，单次候选解析总预算 45 秒。组件列表仅提供当前稳定候选；内部明确版本解析已验证，不等于已有版本列表界面或可执行多版本更新。

依据：[官方 stable 元数据](https://pkgs.tailscale.com/stable/?mode=json&os=windows)、[包服务器与摘要规则](https://pkgs.tailscale.com/stable/)、[Windows MSI 安装说明](https://tailscale.com/docs/install/windows/msi)、[版本与发行通道](https://tailscale.com/docs/reference/tailscale-client-versions)。HTTPS 元数据和摘要只用于发现制品身份，不代表独立发布者签名已验证或取得系统安装所有权。尚无 Tailscale 组件 state/plan/execute 适配器或组件中心来源界面；MSI 未套用归档安装路径，也不开放安装按钮。MR-11 继续跟踪官方签名验证、系统安装事务和完整生命周期。

验证：Tailscale Server/Helper 专项 **476 项通过**，新增公共锁 20 项、官方来源 43 项、损坏控制回执 12 项。覆盖锁被占用时所有控制及登录观察入口等待、两个 Helper 实例重复／竞争／关账并发、原请求只执行一次、旧修订拒绝、格式／来源／架构／版本错配、摘要边界与取消，以及无效代理不得绕过。公共下载／代理专项 **60 项通过**；Windows 的可信传输探针因没有进程级测试 CA 覆盖按原规则跳过。测试代理的 407 响应已明确声明 Connection: close，与其立即关闭连接的行为一致，保留原凭据隔离断言。Server、Helper 与专项工程构建通过，既有编译和元数据网络警告保留。

在线只读探测通过生产传输解析了 win-x64 的当前 1.104.4 和历史 1.102.2 官方 MSI 元数据与摘要，并核对当前版本的明确版本解析相同；没有下载 MSI、验证其签名、调用安装器或操作真实 Tailscale。证据为 `.tmp/tailscale-official-source-smoke.json`。专项日志为 `.tmp/tailscale-lock-source-build.log`、`.tmp/tailscale-lock-source-tests.log`、`.tmp/tailscale-lock-helper-build.log`、`.tmp/tailscale-source-runtime-build.log` 和 `.tmp/tailscale-source-runtime-tests.log`。真实 daemon、Android 交互、操作中断、OS 权限保护、完整安装／更新／修复／回滚／卸载验收仍未完成。

## Windows MSI 受限暂存与只读签名原语（2026-10-29）

新增专属 MSI 传输原语：严格复用官方解析器的稳定版本／架构／精确文件名规则，只接受对应固定 HTTPS 下载地址和完整 SHA-256，不接受查询参数、片段、任意重定向或归档格式替代。使用公共运行时 DNS／实际地址／宿主代理规则；目标必须是空的可读写、可定位流，下载上限 128 MiB、总预算三分钟。下载完成后重新读取暂存内容并以固定时间比较摘要；取消、空包、摘要错误或传输失败后清空暂存流，不返回可用制品。

Helper 新增只读签名校验原语，未注册 IPC、HTTP 或组件安装入口。校验重新计算文件摘要，检查期间以同一文件句柄禁止写入和删除，调用 Windows WinVerifyTrust 验证明确的首个 Authenticode 签名，并从同一验证状态读取签名者证书；不通过另一次文件解析独立选择证书。禁用 UI 和 MD2／MD4，要求非根证书链撤销检查，仅使用本机缓存，不触发证书网络获取；缺少可用撤销证据时明确拒绝，不降级为仅摘要或跳过撤销检查。API 依据见 [WINTRUST_DATA](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_data) 与 [已验证签名者读取](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-wthelpergetprovsignerfromchain)。

发布者比较严格解析 DER RDN，逐项匹配加拿大注册实体 Tailscale Inc.、注册号 1131559-5、Toronto／Ontario、Private Organization 及注册国家 CA；拒绝遗漏、重复、附加或不匹配的字段，不能只凭显示名称授信。当前匹配基线来自官方 1.104.4 实包观察，不新增可由客户端提交的信任配置，也不代表生产发布源已登记或未来证书身份可自动扩大。返回的签名证据只是该次只读观察，不可持久化后当作执行许可；真实安装必须在 Helper 自有受保护暂存、来源信任修订和事务锁下重新核对制品。

生产传输实际下载并校验 win-x64 1.104.4 MSI，共 38,796,288 字节，SHA-256 为 `1d12cd8b1d6dbb447da8579b1a5268ae2fc050967bebfaa02e32d2bb645ef6ee`；本机 WinTrust、撤销状态与发布者匹配均通过，证书 SHA-256 为 `E5A8E8BFF302C4690B693F0D437FBA183FB1ACC49EB00E551DD2705E003949E2`。另以 Windows Installer 数据库只读模式观察到 ProductName=Tailscale、Manufacturer=Tailscale Inc.、ProductVersion=1.104.4、Template=x64;1033；这些包属性尚未接入代码门禁。没有调用安装器、自定义动作、服务控制或真实 Tailscale。该结果不外推至 ARM64、其他版本、其他宿主或缺少有效撤销缓存的机器。

验证：Tailscale Server/Helper 专项 **512 项通过**，本轮新增传输／摘要检查 19 项、发布者与 Windows 原生拒绝检查 17 项；覆盖固定制品身份、非空暂存保护、错误摘要、取消／部分传输清理、合法实体 RDN 排序、伪装显示名称／法律身份错配、重复／额外字段、无签名文件和哈希错误拒绝。构建通过，既有警告保留。证据为 `.tmp/tailscale-msi-stage-smoke.json`、`.tmp/tailscale-msi-signature-smoke.json`、`.tmp/tailscale-msi-properties.json`；日志为 `.tmp/tailscale-msi-signature-build.log`、`.tmp/tailscale-msi-signature-tests.log`、`.tmp/tailscale-msi-helper-build.log`。

MR-11 仍未完成：需要 MSI 产品／升级标识及架构属性门禁、受保护暂存与来源信任登记、系统安装所有权及原事务回执、安装／多版本更新／修复／回滚／卸载／恢复，之后才可开放组件安装入口。Android 本轮无新增变化，真实设备与 daemon 验收仍待完成。

## MSI 产品属性校验接入（2026-10-29）

只读检查现已在同一文件锁的摘要、WinTrust 和发布者检查之后打开 MSI 数据库，明确使用 MSIDBOPEN_READONLY 及只读 summary，不打开安装会话、不加载 transform、不提交数据库、不执行自定义动作。每个属性查询要求恰好一条记录，文本上限 256 字符，summary 要求字符串类型；所有 MSI 数据库、视图、记录和 summary 句柄均在 finally 中关闭。API 依据见 [Microsoft MSI 数据库接口](https://learn.microsoft.com/en-us/windows/win32/msi/database-functions)。

产品名精确要求 Tailscale、厂商 Tailscale Inc.、产品版本等于已解析的目标稳定版本，模板按 win-x64／win-arm64 分别要求 x64;1033／Arm64;1033。ProductCode、UpgradeCode 和 PackageCode 必须是规范的大写非空 GUID，且互不相同；缺失、错误架构、不同版本或非 MSI 数据库均拒绝。升级 GUID 只是原包观察，不单独作为已安装产品的所有权证明或卸载依据；后续事务仍须建立受保护台账和明确升级集合策略。

Tailscale 专项 **529 项通过**，新增属性验证 17 项，包含错误产品／厂商／版本／架构、无效／空／非规范／重复标识、原生非数据库拒绝和句柄释放后的文件重命名。Server 与 Helper 构建通过，既有警告保留。官方 1.104.4 x64 实包经过新增代码路径验证成功，读取到 ProductCode={54939609-8ED7-550F-95F0-CDE77EC4499A}、UpgradeCode={C8F0A9CE-39DE-47FE-93B4-F33AC23790B5}、PackageCode={24386ABE-CC73-4964-9DFD-CC19646DC31B}、Template=x64;1033。证据为 `.tmp/tailscale-msi-properties-smoke.json`；日志为 `.tmp/tailscale-msi-properties-build.log`、`.tmp/tailscale-msi-properties-tests.log`、`.tmp/tailscale-msi-properties-helper-build.log`。

没有安装 MSI 或运行真实 daemon；ARM64 当前只有纯属性规则检查，尚未进行实包验证。受保护暂存、来源信任登记、系统安装事务、所有权及多版本完整生命周期仍未实现，不因本次属性校验而开放安装入口或关闭 MR-11。

## Helper 私有 MSI 暂存与租约（2026-10-29）

新增 Helper 内部暂存组件，生产工厂固定使用控制根目录下的 msi-staging，复用现行目录保护规则，拒绝可重解析路径、非受信任所有者和普通用户可写对象。不接受客户端路径、URL 或摘要；明确版本／架构先做语法检查，再由 Helper 独立解析固定官方源的目标元数据与摘要。关键交接点重新核对保护。

准备流程持有跨进程 staging.lock，按 operationId 的规范 GUID 创建专属目录，以 CreateNew 写入固定 package.msi，受限下载后 Flush(true)，在私有目录内关闭写句柄、打开并持续持有禁止写入删除的只读句柄，再检查签名及产品属性。返回租约保留文件与暂存锁，其他准备请求等待；准备过程使用五分钟协作取消预算，锁等待、解析、复制及检查后均检查取消，不交付晚到结果。锁路径损坏为目录时明确拒绝，不误当锁竞争。

租约销毁后拒绝访问其路径／描述／证据，释放文件句柄，重新检查保护并仅删除本次 package.msi 和空操作目录，最后释放暂存锁。失败／取消走相同清理边界，不递归删除未知内容；清理异常仍释放锁。已有操作目录（包括中断遗留目录）拒绝覆盖，不自动恢复执行、更新证据或接管。暂存不是安装事务，租约不持久化为安装许可，也不代表安装所有权或允许跨重启执行。

验证：Tailscale 专项 **555 项通过**，新增暂存 26 项。覆盖 Windows 文件读锁拒绝写入、跨实例等待／取消、持锁期间不重复解析、显式／重复释放、销毁后拒绝复用、原目录保留、检查失败／晚到结果清理、未知内容保留、清理失败后释放锁、损坏锁路径及非法选择器拒绝。真实 Windows ACL 检查拒绝了测试自建 Everyone 可写目录，且未调用源解析。下载／签名使用替身，不能据此声称生产 Program Files 目录正向权限或官方实包已在完整暂存流程中验收；前一阶段真实包证据保持原范围。

Server 与 Helper 构建通过，既有警告保留。日志为 `.tmp/tailscale-msi-staging-build.log`、`.tmp/tailscale-msi-staging-tests.log`、`.tmp/tailscale-msi-staging-helper-build.log`。没有新增 IPC／HTTP 安装入口，没有安装或控制真实 Tailscale。MR-11 继续跟踪生产暂存正向权限与实包串联验收、来源信任修订、所有权／升级集合、原事务回执及完整安装／多版本更新／修复／回滚／卸载／恢复；Android 设备验收仍待完成。

## Windows 保护目录 ACL 缺口修复（2026-10-29）

修复通用于控制证据、CLI 路径和 MSI 暂存的 Windows 权限判断：此前只检查目标 ACL，祖先仅检查 reparse 标志；现在逐层核对受信任所有者及 ACL，拒绝普通身份删除／替换祖先、删除子项、更改权限／所有者以及 GENERIC_WRITE／ALL。目标对象继续拒绝普通身份的写权限；祖先仅允许创建其他同级项的权限不被误判为可替换已有受保护路径。InheritOnly ACE 不作用于当前对象，未知 ACE 形状明确拒绝，不把条件 allow 自动当成无效权限。

原实现枚举 allow 规则，未识别没有 DACL／NULL DACL 的放行语义；现直接解析原始安全描述符，明确拒绝缺失或 NULL DACL，并与真正不授予访问的空 DACL 区分。依据见 [Microsoft NULL 与空 DACL 规则](https://learn.microsoft.com/en-us/windows/win32/secauthz/null-dacls-and-empty-dacls)。新建 Windows 控制根目录也改为创建时附带受限安全描述符，不再先继承宽松权限后调用 SetAccessControl。

专项 **576 项通过**，新增 Windows ACL 21 项，覆盖 NULL／缺失／空 DACL、非受信任所有者、祖先删除／删除子项／权限更改／所有权／generic 写权限、仅继承项及同级创建权限差异。本机 C:\Program Files 与其完整祖先链通过生产 Require 的只读检查，证据为 `.tmp/tailscale-protection-probe.json`，没有写入该目录。该探针不证明新建私有根目录、其持久化或完整 MSI 暂存串联的正向权限已验收。

Server／Helper 构建通过，保留既有 1／9 项警告；日志为 `.tmp/tailscale-protection-build.log`、`.tmp/tailscale-protection-tests.log`、`.tmp/tailscale-protection-helper-build.log`。未安装或控制真实 Tailscale，安装所有权、来源信任、事务及移动设备验收仍待完成，MR-11 保持实施中。

## Windows Installer 注册观察（2026-10-29）

Helper 新增内部只读注册观察，使用 MsiEnumProductsEx 的 Everyone／ALL 范围覆盖机器和其他账户上下文，不降级为当前账户枚举。按枚举取得的 productCode／用户 SID／上下文读取对应实例；已安装项查询 InstalledProductName、VersionString、Publisher，advertised 项读取 ProductName 并明确保留 advertised 状态，不把缺少已安装属性当作未安装。仅记录名称为 Tailscale 的注册项，发布者和版本字符串只是注册观察，不是制品验证或所有权证明。用户 SID 不进入输出产品记录，实例的 JSON 与 ToString 也不暴露 SID。

全部枚举至多 512 个产品，文本／SID 长度分别限制 512／256 字符，并使用十秒协作检查预算和取消门禁。访问拒绝有专门安全问题码；其他枚举／属性错误、超限、重复实例、无效 GUID／上下文／SID 形状、控制字符或晚到结果均拒绝，不返回部分列表冒充完整观察。不访问缓存 MSI 路径，不调用配置、修复、安装或卸载 API。

依据见 [Microsoft MSI 注册枚举](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msienumproductsexw) 与 [实例属性读取](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msigetproductinfoexw)。Windows Installer 的 managed 上下文不等于 RelaxKonOS 拥有该安装；空列表仅表示这次完整枚举未匹配注册名称，不能证明 CLI／服务／其他布局不存在。后续必须组合受保护安装台账、固定文件与 SCM 观察、原操作回执和当前修订，才能决定安装或变更准入，外部实例不得被接管。

专项 **598 项通过**，新增注册观察 22 项；覆盖机器／其他账户并存、advertised 差异、SID 隐私、无效或重复身份、枚举与属性失败、文本与产品数量边界、取消及晚到响应。Server／Helper 构建通过，既有警告保留。本机真实只读探针明确返回 component.tailscale_inventory_access_denied（当前进程非管理员令牌），没有报告未安装或取得所有权，也没有写入文件；此证据只覆盖原生访问拒绝路径，完整特权枚举仍待验收。

日志为 `.tmp/tailscale-inventory-build.log`、`.tmp/tailscale-inventory-tests.log`、`.tmp/tailscale-inventory-helper-build.log`，探针输出为 `.tmp/tailscale-inventory-probe.json`。未注册 IPC／HTTP 生命周期入口，未执行真实安装／更新／修复／回滚／卸载；MR-11 与 Android 设备验收保持未完成。

## Windows 安装前组合观察（2026-10-29）

新增 Helper 内部组合观察，覆盖完整 MSI 注册、原生及 x86 Program Files 下的 Tailscale 对象、CommonApplicationData 下的保留数据对象和固定 Tailscale SCM 服务。只读工厂限 Windows x64／ARM64 操作系统和 64 位进程，不接受路径或服务名入参。目录父路径要求本地盘符及正常目录，拒绝 reparse 祖先；保留名称上出现任意对象（含链接或空目录）均视为已有对象，不遍历目录内容或读取私钥。不存在只根据明确 FileNotFound 判断，权限／路径／读取失败不通过 File.Exists 降级成不存在。

SCM 仅取得本机 manager 的 CONNECT 和固定服务的 QUERY_STATUS 权限，由 SafeHandle 释放句柄；只有明确 ERROR_SERVICE_DOES_NOT_EXIST 才视为服务不存在，访问拒绝或其他错误报告不可用，不调用启动、停止或删除。各观察前后检查取消，取消不交付晚到结果。注册列表复制为只读快照，避免后续集合变化改写本次观察。

任一来源不完整时整体为 Unavailable，即使其他来源均未发现；完整且有任意注册／程序／保留数据／服务时为 ExistingUnowned，不能因为注册 publisher、Windows Installer managed 上下文或服务名就接管。完整且全部未发现时为 NoKnownArtifacts，只证明当前支持路径和注册范围未发现相关对象，不证明其他布局不存在，也不授予创建目录或安装权限。观察不是宿主原子快照；安装仍须在实际事务内核对受保护自有台账、权限、文件／服务身份及当前修订，处理并发变化和保留数据所有权。

专项 **616 项通过**，新增组合判断 18 项，覆盖每类已有对象、advertised／MSI managed 不授予所有权、单源失败不误报缺失、身份快照不可变、非法观察值和取消。本机只读探针取得 Programs=Absent、Data=Absent、Service=Absent，但全用户 MSI 查询访问拒绝，整体正确返回 Unavailable，未写入目录或变更服务。证据为 `.tmp/tailscale-installation-probe.json`；该探针只覆盖原生服务／固定路径未发现及注册拒绝，不能证明完整特权宿主验收或已具备安装许可。

Server／Helper 构建通过，既有警告保留；日志为 `.tmp/tailscale-installation-observe-build.log`、`.tmp/tailscale-installation-observe-tests.log`、`.tmp/tailscale-installation-observe-helper-build.log`。未开放生命周期 IPC／HTTP，尚缺自有台账、原操作事务、来源信任及完整安装／多版本生命周期，MR-11 不收口。

## MSI 同版本制品身份绑定（2026-10-29）

暂存流程新增不可覆盖的制品观察记录，按明确 rid／version 保存官方描述、SHA-256、经 WinTrust 读取的发布者／证书摘要和已验证 MSI 产品属性。记录位于 Helper 私有暂存根的 pins 子目录，仅在摘要、签名及包属性全部通过之后创建；原有 staging.lock 覆盖检查和发布。先刷新临时文件内容，再不可覆盖移动至固定名称，不通过更新旧记录接受新摘要。

后续重新解析同版本时，若描述或摘要与已观察记录不一致，在下载前返回 component.tailscale_artifact_conflict；摘要相同但本次验证所得签名／产品证据变化时同样拒绝。重复观察一致则保留原文件。记录读取限制 16 KiB／16 层并使用当前严格结构，拒绝重复／未知／遗漏字段、无效发布者／属性／摘要、损坏 JSON 和错配身份；拒绝后不删除或补写原记录。每次准备仍重新取得官方元数据及重新校验实际文件，不使用历史记录跳过当前签名或撤销检查。

该记录只是已验证制品的身份绑定，不是发布源批准、自有安装台账或执行许可。没有管理员确认变更摘要的产品入口，不自动扩大信任；来源管理仍须单独实现。当前刷新与移动流程没有断电耐久性验收，不能由普通文件重开测试推断任意故障下记录必然保留。

专项 **644 项通过**，新增身份绑定 28 项；覆盖首次有效证据发布、无效证据不创建目录、重新打开读取、相同观察不改写、摘要／证书冲突不覆盖、损坏记录保留及完整暂存流程在下载前拒绝同版本摘要变化。既有暂存测试改用完整结构的模拟签名证据，仍不代表真实 WinTrust 或生产目录验收。Server／Helper 构建通过，保留既有警告。日志为 `.tmp/tailscale-artifact-pin-build.log`、`.tmp/tailscale-artifact-pin-tests.log`、`.tmp/tailscale-artifact-pin-helper-build.log`。

未执行系统安装或真实 daemon 操作。MR-11 继续跟踪来源信任、自有台账、原事务回执、生产权限与实包串联及完整生命周期；Android 当前流程与设备验证要求由 Android 文档维护。

## ARM64 官方 MSI 实包与产品家族（2026-10-29）

通过生产官方解析／传输下载 win-arm64 1.104.4 MSI，共 36,429,312 字节，SHA-256 为 `929fa96795d7fd8c1c1c7fa9060ae487c69095a89214c47e2e6a276fc847684c`。在当前 x64 Windows 宿主上只读验证 WinTrust、缓存撤销状态、发布者法律身份及 MSI 属性成功，模板为 Arm64;1033，ProductCode={67715B6E-1FF9-5805-8CE2-31E3C94E1334}、PackageCode={47D80D96-AB0B-4CED-A8EA-426C9B986C47}。证书 SHA-256 与已验证 x64 包相同，未安装或执行 ARM64 程序。

两架构包的 UpgradeCode 均为 {C8F0A9CE-39DE-47FE-93B4-F33AC23790B5}；产品属性验证现在明确要求该已知产品家族，拒绝名称／厂商正确但升级标识属于其他家族的包。架构仍必须由明确 rid 和模板独立匹配；共同 UpgradeCode 不是跨架构升级授权，也不是既有实例的所有权证明。未知家族不猜测接受，来源批准和升级集合策略仍待实现。

专项 **645 项通过**，新增错误产品家族拒绝检查。当前规则下 x64 和 ARM64 官方 1.104.4 均重新通过只读验证；以 win-x64 身份检查 ARM64 实包明确拒绝。只读 MSI 工具现要求显式 rid，签名／属性拒绝返回非零退出码，非法参数不再落入普通回归流程。当前命令为：

```powershell
dotnet run --project Tests/Tailscale/RelaxKonOS.Tailscale.Tests.csproj --no-build -- --stage-official-tailscale-msi win-arm64 1.104.4 .tmp/tailscale-1.104.4-arm64-validation.msi
dotnet run --project Tests/Tailscale/RelaxKonOS.Tailscale.Tests.csproj --no-build -- --inspect-official-tailscale-msi win-arm64 1.104.4 .tmp/tailscale-1.104.4-arm64-validation.msi
```

下载目标必须尚不存在，不覆盖已有文件。证据为 `.tmp/tailscale-arm64-stage.json`、`.tmp/tailscale-arm64-inspect.json`、`.tmp/tailscale-x64-family-inspect.json`、`.tmp/tailscale-arm64-mismatch.json`；日志为 `.tmp/tailscale-arm64-build.log`、`.tmp/tailscale-arm64-regression.log`、`.tmp/tailscale-arm64-helper-build.log`。Server／Helper 构建通过，保留既有警告。

以上不证明 ARM64 宿主上的 MSI 安装、驱动、服务或组网，不证明生产受保护目录的完整暂存／安装事务验收。没有运行真实 daemon、建立安装所有权或开放安装入口；MR-11 保持实施中。

## 控制与关账读取后的期限复核（2026-10-29）

修复控制协调器的两处等待边界：执行只在读取基线前检查计划期限，读取期间过期后仍可能写入 Applying 并提交；关账也只在读取当前恢复观察前检查预览期限。现在两条路径均在 await 完成、重新检查授权与取消之后，写入执行／关账意图之前再次检查期限，边界相等即拒绝。

过期控制计划持久化为 Rejected／tailscale.plan_expired，原生 Execute 不被调用，读取原操作继续返回拒绝终态；过期关账预览返回 tailscale.recovery.preview_required，不写 ClosingRequest，也不产生 Helper 闭合证据。已被受理的原生操作仍沿用既有断线独立执行与原回执规则，不因这个检查承诺撤销。

协调器时间读取统一经可注入 TimeProvider，生产默认系统 UTC；持久状态、计划及恢复期限使用同一时钟。专项 **649 项通过**，新增 4 项检查，使用可控时钟在宿主读取回调中推进到期限边界，验证拒绝结果及原生执行／关账意图未发生。日志为 `.tmp/tailscale-control-deadline-build.log` 与 `.tmp/tailscale-control-deadline-tests.log`；Server 及专项构建通过，保留既有 NuGet 元数据网络警告。不涉及新 HTTP 契约，不执行真实 daemon；安装生命周期及真机验收仍待完成。

## 桌面未决提交与关账回执门禁（2026-10-29）

修复桌面当前视图读取原任务时过早清除未决标记的问题：提交响应丢失后，Prepared 只是读取时尚未提交的观察，不能证明旧 POST 不会稍后到达，故保留提交门禁；原操作返回已受理状态或终态后沿用对应恢复规则。关账响应丢失时，原操作即使可以读取，但没有原 Closure 就不能解除关账门禁；首次登录另区分当前未决是否来自关账，防止读取仍 Issued 的操作重新开放浏览器交接。

此改动不改变 HTTP 契约，不重放请求，也不把 Prepared 变成终态；服务端期限与原任务幂等规则继续适用。桌面引用文件仍只记录身份，当前视图的未决状态不因此宣称具备跨进程持久化。Android 当前规则及验证用例由 [Android Tailscale](../../Client/RelaxKonOS.Client.Android/docs/features/Tailscale.md) 和 [Android 验证要求](../../Client/RelaxKonOS.Client.Android/docs/development/Verification.md) 维护。

桌面行为／传输／布局专项 **202 项通过**，新增控制／登录 Prepared 观察不解除门禁、控制／登录缺少原 Closure 不解除关账的检查；构建通过。日志为 `.tmp/tailscale-client-prepared-build.log` 与 `.tmp/tailscale-client-prepared-tests.log`。替身响应验证不等于真实网络排队、断线或 daemon 验收；完整运行时生命周期仍未完成。
