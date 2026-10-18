# RelaxKonOS 内置代理服务端应用设计

> 状态：首版实现与待验收设计；已实现范围见 §13，真实平台验收尚未完成。
> 应用名称：代理服务端 / Proxy Server
> 应用 ID：`relaxkonos.proxy-server`（拟定）
> 首选内核：sing-box；管理端：Avalonia；服务端：RelaxKonOS.Server。
> 目标宿主：Windows / Windows Server、Linux；能力按实际发行版、架构和服务管理方式开放。
> 上游资料核对日期：2026-10-17。

> 生命周期补齐：当前固定版本实现尚不具备通用多版本更新。安装、卸载、版本选择、发布源管理和实际跨版本升级按 [受管运行时统一规范](../platform/RelaxKonOS.ManagedRuntimes.Design.md) 的 MR-02 追踪；该目标取代下文编译期唯一版本清单的长期设计约束，当前实现边界仍以 §13 为准。
> 宿主及接线进展（2026-10-18）：新增固定 systemd 宿主、实际配置凭据证明、进程 socket inode 监听核对、Helper 固定生命周期组合、Server 新鲜发布授权和每操作认证通道。固定配置路径与生命周期已共锁，启动前再次核对授权，完整冻结记录不得伪造制品布局或验证证据。安装操作台账/API 和实际阶段进度已接通，支持目标绑定的启动恢复；受管活动修订上的已发布配置发布、启停、开机启动与配置回滚已接通，归档安装的卸载（保留或删除数据）与客户端版本计划页也已接通；公共目录仍不允许安装。Debian 检查的范围和数字见[事务证据](../platform/RelaxKonOS.ManagedRuntimes.Transactions.md#linux-固定宿主与进程证明)，systemd 测试程序不构成 sing-box 跨版本或协议验收。
> MR-01/MR-02 进度（2026-10-17）：公共契约、可信目录、制品准备、持久事务及 Windows 固定服务宿主代码已落地，385 项常规检查通过。状态现核对进程启动时的配置证明，不能给旧进程套用新注册元数据；监听归属使用原生 PID 表。实际生命周期、当前授权、配置共锁和 UI 尚未接入新框架，SCM 与跨版本端到端仍待验收，详见 [事务组件](../platform/RelaxKonOS.ManagedRuntimes.Transactions.md)。

> 原生探测依据：[上游 1.14.3 的 version 输出](https://github.com/SagerNet/sing-box/blob/v1.14.3/cmd/sing-box/cmd_version.go)。REALITY 服务端要求 `with_utls`，Hysteria2 要求 `with_quic`，分别由 [REALITY 实现](https://github.com/SagerNet/sing-box/blob/v1.14.3/common/tls/reality_server.go) 和 [QUIC 注册实现](https://github.com/SagerNet/sing-box/blob/v1.14.3/include/quic.go) 的构建门禁核对。Helper 仍须执行目标二进制的原生 check；标签不是配置生效或协议互通的替代证据。常规测试覆盖解析器及可信子进程边界，真实归档验证见下一项，不扩大版本/平台验收范围。
> 原生归档接入进展：`SingBoxArchiveAdapter` 以固定摘要的 Windows x64 1.14.3 通过 12 项真实归档检查，包含程序 version、REALITY 密钥生成及配置 check、同版本修复和授权中断恢复；这些测试的服务仍是测试宿主。Windows 宿主另有实际 PID 表检查，但未操作 SCM。Linux 宿主已补充受控测试程序的 systemd 验证（见上）；生产生命周期/授权提供者及配置共锁待接通，不构成 sing-box 跨版本或系统服务验收。

## 1. 产品定位与边界

让管理员在 RelaxKonOS 中部署代理服务、创建独立连接凭据，并把连接配置交给手机或电脑上的代理客户端。

```text
管理员 → RelaxKonOS 客户端 → Server → 受管 sing-box 服务
手机 / 电脑代理客户端 ── VLESS / Hysteria2 等 ──→ sing-box → 目标网络
```

本应用是主机全局内置管理应用，在应用启动器中以独立入口出现。安装内置管理应用不等于自动下载内核或开放公网端口；首次进入显示运行时未安装状态。

| 现有功能 | 与本应用的关系 |
| --- | --- |
| [代理管理器](RelaxKonOS.ProxyManager.Design.md) | Mihomo 管理宿主出站、订阅、策略组和 TUN；保留原入口和职责 |
| [Squid 网关](../proxy/squid.md) | HTTP 代理网关及其访问控制继续独立管理 |
| 宿主出站代理 | 控制 Server 下载等请求；不自动成为本应用的流量出口 |
| 防火墙、证书、安装操作、审计 | 复用既有模块，通过受约束接口协作 |

独立建立 ProxyServer 业务边界，不把服务端用户、入站和订阅发布塞进现有出站 `IProxyEngine`。仅提取确实通用的安装验证、操作台账与服务控制基础设施。首版只实现 sing-box，暂不添加无实现的多内核切换界面。

本设计不包含手机本地 VPN/TUN、设备自动连接、商业计费、集群调度、任意原生 JSON 编辑、透明代理或自动接管外部 sing-box。Android 只提供已连接服务器上的管理与导出（发布、回滚、运行时、凭据、导出、订阅），入站服务定义仍只在桌面编辑；Android 专属交互与支持范围由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/README.md) 单独维护，见 [代理服务端](../../Client/RelaxKonOS.Client.Android/docs/features/ProxyServer.md)。

## 2. 协议与运行时能力

首版目标协议如下。此表定义实施范围，不能直接作为运行时能力响应；每种组合必须在受信任版本和目标平台上验证后开放。

| 协议预设 | 监听传输 | 必需配置 | 首版导出目标 |
| --- | --- | --- | --- |
| VLESS + REALITY + Vision | TCP | 用户 UUID、REALITY 密钥、short ID、握手目标及服务器名称 | VLESS URI、sing-box JSON、Mihomo YAML |
| VLESS + TLS | TCP；可选择 WebSocket | 用户 UUID、证书引用、服务器名称；WS 路径 | VLESS URI、sing-box JSON、Mihomo YAML |
| Trojan + TLS | TCP | 每凭据独立密码、证书引用、服务器名称 | Trojan URI、sing-box JSON、Mihomo YAML |
| Hysteria2 + TLS | UDP / QUIC | 每凭据独立密码、证书引用、服务器名称 | Hysteria2 URI、sing-box JSON、Mihomo YAML |

Vision 不与 WebSocket 组合。REALITY 的私钥仅留在服务端，导出只包含公钥；REALITY 握手目标与对外连接地址分别保存。Hysteria2 的密码按所选客户端格式编码，不能假定不同实现的认证字段完全相同。

TUIC、AnyTLS、Shadowsocks 等为后续独立扩展项，初版不显示可保存但无法运行的表单；XHTTP 及 Xray 专属新扩展不宣称兼容。添加协议时同时补充校验、生成器、客户端导出和互通验证。

运行时能力包括内核版本、构建特性、已验证协议组合、统计支持、平台安装与服务控制支持。统计属于可选能力：上游 V2Ray API 并非默认构建包含，不能仅根据版本号推断可用；缺失时允许代理服务工作，统计显示“不支持”。

内核只选 Server 维护的受信任稳定版本清单，清单固定平台、架构、下载源、摘要和已验证特性。预发布版不作为默认选项。升级按选中版本重新生成和校验当前配置，不维护旧格式解析器、兼容路由或双格式配置。

## 3. 应用页面与主要交互

桌面使用现有内置应用、MVVM、工作区及模态编辑器基础设施，界面文本接入现有多语言与主题系统。

| 页面 | 内容与操作 |
| --- | --- |
| 概览 | 服务健康、内核版本、当前配置修订、启用入站数、统计可用性、最近失败；安装或创建入站引导 |
| 入站服务 | 协议、监听地址、TCP/UDP 端口、对外地址、启用状态；创建、编辑、停用、删除及发布 |
| 访问凭据 | 名称、所属用户、授权入站、到期时间、状态；生成、轮换、停用及撤销 |
| 连接与订阅 | 选择凭据和客户端格式；显示兼容性、生成二维码、下载配置、签发或撤销订阅 |
| 流量与日志 | 有能力时显示时间范围与采样新鲜度；日志分页、筛选、脱敏；不支持或断联单独展示 |
| 运行时与诊断 | 安装、升级、启动、停止、重启、开机启动、配置验证、回滚和恢复 |

首次创建流程：选择协议 → 填写监听与对外地址 → 配置 TLS 或 REALITY → 创建或选择访问凭据 → 预览脱敏差异与端口影响 → 校验 → 发布 → 查看本机健康 → 导出客户端配置。

监听地址与对外地址不可混用：`0.0.0.0`、`::` 不能成为分享链接地址。公网域名或 IP 由管理员明确填写，不根据请求 Host 自动推导。端口冲突提示已有监听者；TCP 与 UDP 分别检测，允许同一数字的不同传输端口。

保存草稿不会改变运行服务。发布前展示受影响入站、连接中断风险、凭据撤销效果及待执行的防火墙变更。停用凭据只有在发布并确认运行时生效后显示“已停用”；发布失败显示“待生效”，不能给出已撤销的成功提示。

未安装、已停止、启动中、运行中、降级、故障、恢复待处理分别呈现；API 无法连接不能显示为零流量或空连接。轮询离开页面即停止，操作 ID 可在安装操作或本应用恢复查看。

## 4. 数据模型与状态

所有记录绑定当前受管主机；客户端切换服务器必须切换缓存域。首版每主机一个受管 sing-box 实例，支持多个入站。

| 领域对象 | 关键字段及规则 |
| --- | --- |
| `ProxyServerRuntime` | 安装版本、受管路径、平台能力、观测状态、运行世代、失败问题码 |
| `ProxyServerInbound` | ID、名称、协议预设、监听地址与端口、对外地址与端口、TLS/REALITY 引用、启用状态、修订 |
| `ProxyServerPrincipal` | 代理使用者 ID、显示名、可选 RelaxKonOS 用户关联；不是操作系统账号 |
| `ProxyServerCredential` | ID、principal ID、入站授权集合、凭据类型、secret 引用、启用状态、UTC 到期时间 |
| `ProxyServerSubscription` | ID、credential ID、允许导出格式、token 哈希、有效期、撤销状态；一个 token 只代表一份凭据授权 |
| `ProxyServerConfiguration` | desired/published/applied 修订、配置摘要、运行世代、事务状态、上一个可恢复版本 |
| `ProxyServerTrafficSample` | 运行世代、采样区间、对象 ID、上下行增量、质量及最后成功时间 |

同一使用者可拥有多个设备凭据。UUID、密码和内核统计用户名不使用显示名或邮箱，内部使用稳定且唯一的标识。首版多入站共享凭据仅允许相同认证类型；密码或 UUID 在不同协议间不隐式转换。

数据库存结构化业务对象与秘密引用，秘密通过现有受保护存储加密。密码需可供内核与授权导出使用，不能只保存不可逆哈希；订阅 bearer token 则只存哈希。删除凭据同步撤销其全部订阅。

`desired` 表示已保存草稿，`published` 表示事务发布到受管配置，`applied` 表示当前运行世代已确认加载。进程存活不足以证明 applied；首版由固定受管配置启动并完成健康检查确认，无法确认时保持未知。手工磁盘改动标记漂移并阻止覆盖，需管理员明确重新发布受管配置。

## 5. Server、Helper 与运行时

拟定目录为 `RelaxKonOS.Server/ProxyServer/`、`Shared/RelaxKonOS.Protocol/ProxyServer/`、`Client/RelaxKonOS.Client/Apps/ProxyServer/`。领域服务负责配置、凭据、导出、统计和事务，平台适配器负责固定服务管理。

Linux 使用独立 systemd unit `relaxkonos-sing-box.service` 和专用低权限服务账号。Windows 使用独立 SCM 服务 `RelaxKonOS.SingBox`，通过现有受信任服务宿主运行内核，不能假设普通 CLI 二进制原生支持 SCM。平台不能满足服务宿主约束时返回能力不支持。

Server 不执行用户输入的命令、参数或任意二进制。Helper 仅支持固定的安装、配置校验、原子发布、服务动作和清理操作；在提升权限端再次验证受管路径、版本摘要、所有权、参数和配置。不能通过原生 JSON 注入任意文件读取、脚本、控制端口或服务管理动作。

运行目录、配置目录、证书副本与状态目录使用专属所有权标记和 ACL，与 Mihomo/Squid 隔离。低端口需要的权限由平台适配器最小化授予；默认建议使用未占用的高端口。配置、备份、证书私钥和内核诊断均限制访问并设保留上限。

控制与统计接口只绑定回环地址，且与宿主不受信任进程隔离；如某接口缺少认证，必须采用受限本地 IPC 或宿主访问控制，不能认为回环本身足以保护用户流量数据。做不到时关闭该接口并降级能力。客户端只调用 Server 结构化 API。

外部实例只检测，不修改、不停止。安装、升级使用临时目录、归档路径校验、大小限制和摘要验证。卸载默认保留数据并说明独立服务的处理结果；删除数据先停止实例，再按所有权删除本模块文件和防火墙规则，不清理其他组件。

## 6. 配置发布、失败补偿与撤销

每主机安装、发布、凭据轮换与服务动作共用串行锁，写操作使用修订比较和幂等键。流程如下：

1. 校验权限、预期修订、运行时能力、端口、证书有效性及引用完整性。
2. 从结构化对象生成完整候选 JSON，临时文件使用与正式配置相同的访问限制；日志和普通预览仅使用脱敏投影。
3. 用选中且受验证的二进制执行固定 `sing-box check` 校验；命令超时、有界捕获输出并脱敏。
4. 写持久化事务标记及受保护的上一版本，按管理员选择准备本应用拥有的防火墙规则。
5. 原子替换配置。首版运行中的配置变更统一受控重启；不承诺无损热更新或动态用户 API。
6. 核对运行世代、服务状态、预期 TCP/UDP 监听及可执行的协议探测，确认 applied 后提交事务和收紧旧规则。
7. 失败恢复上一版本、原服务状态和本次自有规则，记录具体补偿结果；恢复失败进入 recovery-required，阻止后续普通写操作。

服务原本停止时，发布只更新 published，不能自行启动或标记 applied。启动时重新校验并完成健康检查。健康检查区分配置检查、本机监听、协议认证和外部可达性；未执行外部探测时不显示“公网可用”。

重启会中断已有连接，界面发布预览必须说明。单用户撤销若发布失败，尝试恢复旧配置意味着该凭据仍可能有效：操作显示撤销失败并告警；提供明确的“停止整个代理服务”紧急操作，由管理员决定是否影响其他用户。禁止自动把失败撤销包装为成功。

Server 重启后扫描未完成事务并核对实际运行态；幂等重放返回原操作，不重复轮换密码或生成 token。备份恢复需要保留服务账号的数据保护密钥，无法解密时阻止启动并给出可操作错误，不静默重建凭据。

## 7. 证书、防火墙和路由

TLS 复用现有证书模块的证书 ID，Server 验证域名、有效期、私钥及算法兼容性；Helper 将所需材料发布到专属受保护路径。证书更新触发校验和受控应用，失败显示仍在使用的证书版本。首版不再内置另一套 ACME 申请流程。

REALITY 使用独立密钥对和 short ID；其握手目标必须通过地址与端口策略校验，并限制配置探测的 SSRF 风险，默认禁止回环、链路本地、云元数据和管理网络目标。

防火墙默认不自动开放。发布预览可选择创建本应用所有权范围内的入站规则，指定接口、来源 CIDR、TCP/UDP 与端口；规则失败时发布不能报告网络配置完成。云安全组、上游 NAT 和域名解析只做提示及显式探测，不宣称自动配置。

首版只支持代理流量直接出站，默认阻止访问回环、链路本地、私有网络及宿主管理地址；规则同时覆盖域名解析后的地址、IPv4 和 IPv6，不能仅匹配请求中的 IP 字面量。管理员可显式添加最小范围目的网络例外，敏感管理端点仍须受保护。

禁止自动继承 Server 的 HTTP_PROXY、Mihomo 策略或 Docker 代理配置。若宿主启用了 Mihomo TUN，必须验证 sing-box 出站进程或专属账号的绕过机制，防止被透明代理截获；无法确认时阻止并行启用并提示处理冲突。服务端首版不修改宿主默认路由或 DNS。

## 8. 连接导出与订阅发布

导出由 Server 根据已应用配置、有效凭据及明确的客户端能力生成。完整 sing-box JSON 和 Mihomo YAML 必须包含必要的出站、选择与路由结构，不能把单个 outbound/proxy 片段标成可直接运行的配置。

UI 先选择目标：VLESS/Trojan/Hysteria2 分享 URI、sing-box 配置、Mihomo 配置；兼容检查不通过返回具体字段和原因，不静默丢弃 TLS、REALITY、flow 或传输参数。不提供“任何客户端通用”的含糊格式。二维码编码分享 URI；内容过长时改为配置下载，不自动上传第三方短链接服务。

普通列表不返回 UUID、密码、token 或完整链接。导出需要独立权限，响应 `Cache-Control: no-store`，界面显式展示、复制或下载；二维码与敏感弹窗关闭后清理临时状态。导出的凭据本身具有连接权限，不能当作普通诊断附件。

订阅使用高熵随机 bearer token（至少 256 位），可单独轮换、到期或撤销。原始 token 只在签发响应返回，丢失后重签。公开读取端点仅有这一种鉴权用途，不复用管理 API 登录；使用 HTTPS、限流和统一失败响应，边缘与 Server 日志都屏蔽 token 路径，禁止第三方分析和缓存。

订阅每次读取只导出 applied 修订中该凭据被授权的入站，并即时核对当前数据库中的停用、撤销与到期状态；未应用、到期、停用、撤销或服务故障时不发布看似有效的新节点。订阅撤销只阻止继续下载，已下载的节点仍能连接；彻底撤销必须同时撤销代理凭据并应用到内核。轮换代理凭据后旧客户端需重新获取配置。

订阅发布基址由管理员配置并校验为 HTTPS，不信任客户端 Host 或任意 Forwarded 头。订阅格式采用显式路由参数，禁止按 User-Agent 猜测格式或维护旧格式回退。

## 9. 统计、到期与配额边界

能提供 V2Ray 统计的受信任构建按稳定内部用户名采集入站、出站及用户流量。是否能可靠归因到所选协议必须通过互通验证；不满足时只显示可验证的总量，不编造逐用户统计。Clash API 不等于设备识别或用户配额接口。

累计统计使用运行世代和单写采集器，持久化高水位与增量；读取计数器不清零，重启或回退造成的计数下降开启新世代，不产生负流量。采样失败显示 stale/unavailable，无法覆盖的时段标记数据缺口。上传/下载以代理使用者视角定义，并以验证流量校准方向。

首版流量仅用于观测，不支持硬流量配额、计费、设备数限制或精确同时在线人数；密码共享和连接数都不能可靠表示设备数。日志默认不记录完整访问目标或载荷。

到期支持 UTC 时间、后台定时检查、启动时补查及到期触发配置发布。由于首版通过受控重启移除过期用户，到期不是内核逐请求鉴权的精确截止点；界面显示“到期待应用 / 已应用 / 失败”，并记录延迟。管理服务停止期间不能保证准时撤销；有严格截止需求时当前设计不宣称支持。

## 10. API 与权限草案

以下均为拟定新契约，实施时在 Shared Protocol 建立唯一 DTO 与路由常量，并同步 Server、客户端、测试和文档。不得增加旧接口别名或兼容解析。

前缀：`/api/v1.0/proxy-server`。主机能力标识拟定为 `proxyServer`，应用入口根据服务器能力和权限开放。读权限复用现有 observer、变更复用 controller 的授权流程；秘密导出另行定义细粒度 policy，不能因可读状态就获得秘密。

| 方法与相对路由 | 契约用途 |
| --- | --- |
| `GET /capabilities`、`GET /status` | 平台、协议、构建特性与观测状态 |
| `GET /runtime/releases`、`POST /runtime/install`、`POST /runtime/upgrade` | 受信任版本选择与安装操作 |
| `POST /runtime/actions/{action}` | 固定枚举 start/stop/restart/enable/disable |
| `POST /runtime/uninstall` | 明确保留或删除数据的卸载请求 |
| `GET /configuration`、`PUT /configuration` | 无秘密结构化草稿及 expectedRevision |
| `POST /configuration/validate`、`POST /configuration/apply` | 校验与事务发布 |
| `POST /configuration/rollback`、`POST /recovery` | 已知版本回滚和未完成事务恢复 |
| `GET /principals`、`POST /principals`、`PUT /principals/{id}`、`DELETE /principals/{id}` | 代理使用者；仍有凭据引用时拒绝删除 |
| `GET /credentials`、`POST /credentials`、`PUT /credentials/{id}`、`DELETE /credentials/{id}` | 凭据元数据、创建、授权、到期与撤销 |
| `POST /credentials/{id}/rotate` | 轮换并触发受控发布 |
| `POST /credentials/{id}/export` | 单次授权获取敏感连接配置 |
| `GET /subscriptions`、`POST /subscriptions`、`DELETE /subscriptions/{id}` | 无 token 列表、签发与撤销 |

已实施的受管运行时契约与上表不同，按 [受管运行时规范](../platform/RelaxKonOS.ManagedRuntimes.Design.md) 收敛为两组固定路由：`/api/v1.0/managed-runtimes`（`sources`、`sources/{sourceId}/catalog`、`sources/{sourceId}/refresh`、`sing-box/installation`、`sing-box/plan`，以及待恢复事务的 `sing-box/recovery`、`sing-box/recovery/retry`、`sing-box/recovery/stop`、`sing-box/recovery/close`）与既有安装台账 `/api/v1.0/installations/SingBox/{kind}`（`Install`/`Upgrade`/`Repair`/`Rollback`/`Uninstall`）。卸载没有独立的 `/runtime/uninstall` 路由，而是台账的 `Uninstall` 操作；请求只带冻结计划与导出回执操作 ID，不带 URL、可执行路径或原生参数。恢复入口只作用于已存在的失败事务：重试不重新准备、紧急停止不读凭据、关账只在补偿失败且实测服务未运行时成立，详见 [人工恢复入口](../platform/RelaxKonOS.ManagedRuntimes.Transactions.md#人工恢复入口)。
| `GET /traffic`、`GET /logs`、`GET /operations/{id}` | 有界分页观测与持久化操作查询 |

公开订阅读取路由拟定为 `GET /api/v1.0/proxy-subscriptions/{token}/{format}`，与管理权限和数据投影隔离。入站增删改由配置草稿统一处理；凭据变更发布前也必须验证其绑定入站存在及协议兼容。

异步变更返回 202、operation ID 和 Location；校验及一次性导出可同步返回。所有有副作用 POST/PUT/DELETE 要求幂等键；创建秘密的重复请求返回原资源和操作元数据，不再次暴露秘密，丢失时走显式轮换。过期幂等键返回冲突而非隐式重复执行。修订冲突返回 409；数据校验返回字段级问题；权限失败与能力不支持分开表达。

订阅签发同步返回 201，并要求 `Idempotency-Key`；同键同请求重放返回原订阅元数据，`token` 为空字符串、`urls` 为空数组，不重复签发或披露秘密。订阅撤销与其他异步变更一致返回 202，客户端按操作 ID 查询结果。

拟定问题码前缀 `proxy_server.*`，至少覆盖 runtime_unavailable、feature_unsupported、config_invalid、revision_conflict、port_in_use、certificate_invalid、credential_apply_failed、export_unsupported、statistics_unavailable、network_conflict、recovery_required。审计仅保存操作者、对象 ID、动作、修订、操作 ID 与结果，不存原生配置、链接或秘密。

## 11. 实施顺序与验收要求

首版分层已落地（见 §13 实施状态），以下是按依赖排序的交付顺序与仍需在真实主机上补齐的验收项：

1. Shared Protocol、独立应用注册、权限及能力发现；受管运行时安装和固定服务宿主。
2. VLESS + REALITY + Vision、结构化配置、凭据、发布补偿和桌面创建流程；打通第一台真实客户端。
3. TLS 证书集成、VLESS TLS/WS、Trojan、Hysteria2，以及逐种格式导出与互通。
4. HTTPS 订阅、轮换撤销、到期调度、可选统计、诊断和卸载恢复。

交付前必须验证：

- Windows SCM 和受支持 Linux systemd 上的安装、启动、重启、开机启动、升级失败回滚及保留数据重装。
- 每种开放协议的生成配置通过目标二进制校验；Android v2rayNG、桌面 v2rayN 及 sing-box 图形客户端按具体版本建立支持矩阵，Apple 客户端另测；不以名称相同代替互通证据。
- TCP/UDP、IPv4/IPv6、端口冲突、Nginx 443 共存、证书过期、DNS 失败、宿主 Mihomo TUN 冲突及外部不可达的状态表达。
- 并发编辑、幂等重放、Server 崩溃、Helper 失联、发布或补偿中断后可恢复；失败不能误报 applied。
- observer 不能读取秘密，越权用户不能导出他人配置；订阅不能访问其他凭据，token 不出现在日志、审计、异常或普通 DTO。
- 停用、到期、删除和轮换真正影响内核连接；撤销失败保持失败状态，紧急停止确实停止代理服务。
- 统计重启、采样缺口和缺失构建特性不伪造数据；保留数据与删除数据只影响本应用拥有的资源。

## 12. 上游依据

上游文档可能包含比选定稳定版更新的字段。实现必须以受信任清单中的确切版本、构建特性与实测结果为准。

- [sing-box 发布记录](https://github.com/SagerNet/sing-box/releases)
- [入站类型](https://sing-box.sagernet.org/configuration/inbound/)
- [VLESS 入站与 Vision](https://sing-box.sagernet.org/configuration/inbound/vless/)
- [TLS 与 REALITY](https://sing-box.sagernet.org/configuration/shared/tls/)
- [Hysteria2 入站与认证兼容性](https://sing-box.sagernet.org/configuration/inbound/hysteria2/)
- [V2Ray API 与构建要求](https://sing-box.sagernet.org/configuration/experimental/v2ray-api/)
- [Clash API](https://sing-box.sagernet.org/configuration/experimental/clash-api/)

## 13. 实施状态

能力标识 `server.proxy-server`，应用 ID `relaxkonos.proxy-server`，路由前缀 `/api/v1.0/proxy-server`，公开订阅前缀 `/api/v1.0/proxy-subscriptions/{token}/{format}`。以下按层记录已实现内容与边界，未列出的条目视为未实现。

| 层 | 已实现 | 边界 |
| --- | --- | --- |
| Shared Protocol | `ProxyServerRoutes`、结构化 DTO、`ProxyServerPresets` 闭集（五预设、UDP/证书/UUID-密码形状）、`SingBoxConfiguration` 的校验、渲染与脱敏投影 | 无 TUIC/AnyTLS/Shadowsocks，无原生 JSON 入口 |
| Server | 草稿/发布/上一版三段修订、`ProxyServerStore` 受保护状态文件、事务发布与回滚、恢复、到期回收、幂等受理记录、订阅签发与匿名读取 | `/traffic`、`/logs` 未实现，能力响应中统计与日志恒为不支持 |
| 特权 Helper | 固定操作：安装/升级/卸载、配置校验、原子发布、服务动作、REALITY 密钥生成 | 不接受任意参数或原生配置 |
| 桌面端 | 概览、入站服务（含编辑器）、访问凭据、连接与订阅、校验；发布/回滚/恢复/运行时/卸载；三格式导出与订阅签发 | 统计与流量页按能力隐藏，操作结果留在状态栏与校验面板 |
| Android | 概览、入站服务（只读）、访问凭据、连接与导出、订阅、记录；操作按 ID 恢复 | 不编辑入站定义、不生成二维码、不签发证书 |

安全不变量由 `Tests/ProxyServer`（服务层：修订冲突、幂等重放与冲突、凭据形状约束、订阅 token 只存哈希、匿名读取与撤销、发布前拒绝导出、到期不夹带草稿、回滚单步、脱敏投影与受保护状态文件）与 Android `ProxyServerRepositoryTest`、`OperationCenterTest` 覆盖。真机验收项（Windows SCM / Linux systemd 安装与升级回滚、端口与防火墙共存、各协议互通矩阵、崩溃与断联恢复）仍需在目标主机执行。

受限联网的 Linux/Windows 宿主可由管理员在 Helper 的固定受保护运行目录放置 `runtime.archive`。安装仍验证当前平台固定版本清单中的大小限制、SHA-256、归档条目和原生版本；缓存不提供自定义下载源或二进制路径。在线下载的四分钟总期限覆盖响应正文。删除数据卸载同时删除该缓存。

2026-10-17 的 Linux systemd、REALITY/Vision 实际连接、轮换/停用/到期、端口冲突补偿及原生配置检查结果见 [验收记录](../testing/RelaxKonOS.ProxyServer.Acceptance.md)。该记录单独列出未验收的平台与协议组合，不扩大运行时已验证协议清单。

2026-10-18 起桌面端「运行时」页直接读取签名目录：每个版本分别显示来源可信度、目标支持、配置能力与验证证据，阻止原因就地显示；安装/更新/修复/回退先向宿主申请冻结计划再写入安装台账，卸载分「保留数据」「删除数据」两种确认语义。刷新失败保留上次目录并提示失败，待恢复状态下不提供变更操作。UI 本身不提交 URL、可执行路径或原生参数，也不因目录出现新版本就开放安装。事务与检查数字见[事务证据](../platform/RelaxKonOS.ManagedRuntimes.Transactions.md)。
