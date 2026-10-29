# Tailscale 管理器

状态：桌面、服务端条件控制与未知操作核对/关账、首次登录 Helper 证据链与服务端协调/HTTP 路由已实现；桌面登录入口、运行时生命周期与真实宿主验收尚未完成。

使用当前 IServerModule 启动快照注册 relaxkonos.tailscale；禁用后不注册服务或路由，不启动后台工作。认证后的 GET /api/v1.0/tailscale/status 观察宿主 CLI 的 status --json。使用固定官方安装位置、不接受命令或路径输入，执行上限 10 秒、每流上限 1 MiB；失败与未安装分别表达。当前已有独立能力声明及桌面内置入口。

官方 CLI 契约：https://tailscale.com/docs/reference/tailscale-cli

运行时清单为 tailscale CLI 与 tailscaled（Windows 官方服务）。当前可观察外部实例并经授权变更其连接与 DNS/路由接收设置，不取得安装所有权，不安装、更新、修复、回滚或卸载。Linux /usr/bin 与 Windows Program Files/Tailscale 为当前探测位置，其他布局不在本切片范围。尚无真实平台或版本验收；不声明受支持版本范围。

后续实施：首次网页登录、组件中心官方来源、平台制品验证和生命周期事务。安装来源限定官方签名系统仓库或已验证官方安装包，外部安装不得被自动接管。跨版本更新、同版本修复、保留数据卸载、回滚及故障恢复须分别实测；以下历史切片按追加记录阅读，以文末最新范围为准。

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
