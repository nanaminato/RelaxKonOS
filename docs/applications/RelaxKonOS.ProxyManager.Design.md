# RelaxKonOS 内置代理管理器实现规范

> 文档类型：应用设计与实现规范
> 模块名称：RelaxKonOS Proxy Manager
> 首选代理核心：Mihomo  
> 主客户端：Avalonia  
> 服务端：.NET 10  
> 目标平台：Windows / Windows Server / Ubuntu / Ubuntu Server  
> 首要运行模式：TUN  
> 状态：代码级能力已实现，发布级平台验证仍有缺口。当前能力与限制见 [实现说明](RelaxKonOS.ProxyManager.Discovery.md)，平台检查见 [跳过测试登记](../testing/RelaxKonOS.ProxyManager.SkippedTests.md)。

---

## 受管运行时公共目录接入进度（MR-03，2026-10-20）

### 2026-10-20：受管下载路由与有界重试（平台层）

受管下载现在只走两种路由：直连，或宿主已保存的下载代理偏好（`ApplyToRuntimeDownloads`）。路由由 Server 解析成 `RuntimeDownloadProxy`，只在取字节的动词（目录刷新、`*RuntimeExecute`）上随操作传给 Helper；Helper 复校验后才连接，路由不进冻结计划、幂等摘要或台账，因此换路由不会作废已受理安装，内核运行时自己的出站代理也不继承该值。偏好启用但不可用返回 `runtime.download_proxy_unavailable`，不静默退回直连；非法值返回 `runtime.download_proxy_invalid`。

传输层故障自动重试 3 次（500 ms→1500 ms 退避，`Retry-After` 封顶 10 s，受 45 s/4 min 总时限约束），每次重试把目标截断回起始偏移；证书不受信任（`runtime.source_tls_invalid`）、地址/重定向拒绝、4xx 与超限一律终态，新增稳定码 `runtime.source_unavailable` 表示“已按上限重试仍不可用”。代理凭据只在 407 挑战后发往代理连接，源站请求仍只带 `User-Agent`；`noProxy` 按主机名边界与端口匹配，命中即直连。

首版按朴素注入在默认 DI 里接到 `MihomoRuntimeService` 上，实测启动即失败：`MihomoRuntimeService → IRuntimeDownloadProxyProvider → IDockerProxyResolver → IProxySettingsService(MihomoSettingsService) → MihomoConfigurationPublicationService → MihomoRuntimeService` 形成构造环。改为在组合根以工厂延迟取得 resolver（路由只在受理操作时读取），并把默认注册检查改成同时注册真实 `DockerProxyResolver`，让该环一旦回归就会在构造期失败。

指定 `192.168.1.7` Debian 13 x64 上，受管运行时常规检查 **1344 项**通过，其中下载专项 **60 项**（真实 CONNECT 代理与真实 HTTPS 源站，见[下载路由与自动重试](../../platform/RelaxKonOS.ManagedRuntimes.Transactions.md#下载路由与自动重试)）。测试机已升级为 `0.1.0-dev20261020-mr03e`（system 模式、LAN、自签证书含 `192.168.1.7`、三类文件策略 full、Guardian 与自启开启、`/healthz` 200、LAN `host-operating-system` 200）。成功路径的两条断言需要进程能采纳测试 CA，本机不具备该环境，按 SKIP 记录。

### 2026-10-20：生产 HTTP/独立 Helper 全生命周期与资源包

指定 Debian 13 x64 上通过生产 HTTPS、PAM 提权、Server 安装台账及独立 Helper 进程验证 `v1.19.10 → v1.19.30` 更新、同版本 Repair、回退、保留数据卸载/重装和删除数据卸载。增强验收共 **81 项**，包含每个动作的冻结计划/键、同键重放同一操作、无未确认发布、实测程序/配置身份、首次安装不启动/不开机启动、更新恢复运行、保留数据重装维持配置与凭据身份，以及五次真实 mixed 监听 HTTP 转发。命令与可信来源准备见 [生产 HTTP 验收](../../Tests/ManagedRuntimes/ProductionHttpAcceptance.md)。配置为 DIRECT 基线，不代表 provider/TUN 支持。

真实部署另发现并修复 GEO 资源来源错误：Linux 安装器把 Helper 复制到 `/usr/local/lib/relaxkonos/privileged-helper`，其资源准备此前假定相邻 Server 目录，返回 `runtime.resource_source_missing`。Helper 现在把编译期闭合的五项 GEO 文件打入自身 `Assets/Mihomo/GeoData` 发布载荷，仅从当前组件目录读取并再次校验摘要，不回退探测 Server 路径。正式资源路由实测返回正确活动修订及五个长度/摘要，重复准备身份不变；专项检查 **16 项**，全部托管运行时常规检查 **1274 项**通过。

测试机当前为 `0.1.0-dev20261020-mr03c`，系统模式、三类文件策略 full、原 IP SAN 自签证书保留，Server/Guardian 运行。验收使用管理员显式 inspect/confirm 的本地测试源，精确授权 `192.168.1.7/32:18443`，签名私钥留在准备机；验收完成已以信任修订 2 禁用来源、保留高水位/历史、停止镜像、撤下验收临时 CA 与 TLS 私钥。最终实测 Mihomo 单元不存在、无待恢复事务，不把测试源当作生产官方来源。

Android 完整 Debug APK 和单元/设备测试 Kotlin 源码已编译通过；完整 Gradle 运行时 classpath 迁移到该 Debian 测试机后，**1566 项 JVM 测试**通过。证书握手回调夹具已允许 JDK PKIX 用 null 请求默认签名提供者，仍调用真实证书签名验证；设备界面尚未执行；Android 当前交互与验证规则继续由 Android 文档维护。provider/凭据资源分发、TUN 网络事务及 Windows 端到端仍未完成，不能把 Linux 基线生命周期验收扩大为全部 MR-03 交付。

### 2026-10-20：Android 当前契约与生产通道核验

Android Mihomo 已接入来源/目录、实测安装观察、计划预览、精确冻结计划台账提交与命名恢复；旧 `MihomoInstallationRequest`、包引用与已删除下载路由调用移除。安装 journal 使用计划已有幂等键，未知提交不能被新计划替换。当前 API 的运行时枚举采用 camelCase，与 Server 实际响应一致，不增加双格式解析。Android 专属交互与验证规则见 [功能说明](../../Client/RelaxKonOS.Client.Android/docs/features/Proxy.md) 和 [验证要求](../../Client/RelaxKonOS.Client.Android/docs/development/Verification.md)。

指定 Debian 13 x64 测试机 `192.168.1.7` 已部署 `0.1.0-dev20261020-mr03`（Server/Helper/Guardian 当前源码的 Linux 自包含构建），系统模式，普通/管理员/root 文件策略均为 full，HTTPS 证书包含该 IP 的 SAN；内核 nftables 实测无规则表，旧 iptables 防火墙工具未安装。生产 HTTP 经 PAM 登录及独立 Helper 验证来源、安装/依赖/恢复观察、旧请求拒绝、计划缺源/不存在安装拒绝与恢复提权门禁，共 12 项通过。

此前 Android 增量 Kotlin/Compose 编译及 65 项专项 JUnit 已通过；当前完整 Debug APK 构建与生产 HTTP 生命周期证据见上节。Android 设备交互仍独立验收，不能由 JVM 或宿主 API 测试替代。

### 2026-10-19：生产台账注册切换（Server + 桌面）

`IInstallationService` 的 Mihomo 注册现为 `MihomoRuntimeInstallationService`，旧固定清单安装器 `MihomoInstallationService` 与 `MihomoInstallationRequest` 一并删除：台账 `POST /api/v1.0/installations/Mihomo/{kind}` 只接受携带冻结计划的 `ManagedRuntimeInstallationRequest`，缺计划、计划运行时/动作/制品绑定不符、幂等键不符都在受理前拒绝，不存在旧请求解析或备用执行入口。`IProxyRuntimeManager` 同时去掉下载、安装、回退与卸载成员，只有只读观察和外部探测；`/proxy/runtime/download` 路由删除。维护期的组件清理不再自行删除运行时目录，只要受管根或旧状态仍存在就返回 `deployment.component_ownership_missing`，由受管台账移除。

代理状态不再依赖旧安装器自己的状态文件：`MihomoRuntimeManager.GetAsync` 在本地状态缺失时投影 Helper 实测的受管安装事实（归属、实测版本、控制器可达性），无法测量时返回 `proxy.runtime_state_unknown` 而不是“未安装”。启动/停止/重启仍走同一 `relaxkonos-mihomo.service`，配置与设置发布此前已走受管事务，因此代理工作区在不改变日常操作的前提下切换到受管运行时。

桌面 Proxy Manager 新增“运行时”页（复用 Proxy Server 的运行时页面与 `IMihomoRuntimeCatalogClient`）：列出已确认版本、阻止原因、目标差异，执行前取得 Host 计划，安装/更新/修复/回退/卸载都经同一台账，支持取消下载、待恢复重试/紧急停止/操作员关账。旧“安装托管运行时／从服务器文件安装／自行下载／回退／卸载”按钮改为进入该页面。Android 已在 2026-10-20 切换到当前冻结计划契约，客户端详细行为由 [Android 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Proxy.md) 维护。

### 2026-10-19：受控 GEO 资源预备（离线、编译期摘要）

`POST /managed-runtimes/mihomo/resources`（统一 API 前缀下）要求 RuntimeManage、Mihomo 安装权限与目标为 `mihomo` 的 MihomoInstall 提权，并在发布 Gate 内拒绝未确认提交（409 `runtime.publication_unconfirmed`）。请求不携带路径、URL 或选择：可放置的集合由 `Shared/RelaxKonOS.ManagedRuntimes/MihomoGeoDataFiles.cs` 的编译期摘要表固定（`geoip.metadb`、`geoip.dat`、`geosite.dat`、`country.mmdb`、`GeoLite2-ASN.mmdb`），Server 原有 GEO 服务与 Helper 共用同一张表，任一侧都不能自行扩大集合。

Helper 从自身版本目录的 `Assets/Mihomo/GeoData` 读取，逐文件核对长度与 SHA-256 后才落地：包内资产被替换返回 `runtime.resource_digest_mismatch`，文件缺失返回 `runtime.resource_source_missing`，链接路径返回 `runtime.linked_path_forbidden`，未安装返回 `runtime.installation_absent`，有未完成事务返回 `runtime.recovery_required`。写入对象是预置基线 `seed-data` 与当前活动修订的 `data/<revisionId>`：基线让后续安装/更新的候选数据与原生 `-t` 预检看到同一份已校验数据，活动修订让正在运行的服务直接可用。文件整份临时写入后原子改名、按 0644 落地，已一致的文件不重写，旧副本被替换整份。Server 只接受闭合集合内的报告，越界名称按无效 Helper 响应处理。

边界：本操作**不下载任何内容**，也不处理 provider 订阅、证书与私钥——它们仍属受控分发层。桌面/Android 尚无该操作入口，`MihomoGeoDataService` 仍服务旧的固定清单运行时目录。

在指定 `192.168.1.7` Debian 13 x64 上常规 **1269 项**通过（GEO 资源 **11**、HTTP **41**、Helper/Server 集成 41），真实 `native-mihomo-systemd` **167 项**通过。日志为 `/home/nanami/mr03-resources-full-final-20261019.log`、`/home/nanami/mr03-resources-systemd3-20261019.log`。生产安装已换为 `0.1.0-dev20261019-mr03b`：`/healthz` 200，新路由已上线并在无提权时返回 403 `elevation-required`；该测试机账户不在管理员组，未做提权后的生产调用。

### 2026-10-19：紧急停止与操作员关账 HTTP

公开 `POST /managed-runtimes/mihomo/recovery/stop` 与 `POST /managed-runtimes/mihomo/recovery/close`（统一 API 前缀下）与重试共用同一门禁：RuntimeManage、Mihomo 安装权限及目标为 `mihomo` 的 MihomoInstall 提权，缺任何一项都不触达 Helper。请求只携带精确操作 ID，不接受计划、路径或来源；关账还须显式 `confirmed`，缺失按 `runtime.recovery_confirmation_required`（400）拒绝。两条动作都不创建新的用户台账。

停止是安全动作：它要求 Helper 停自己拥有的单元并回报实测观察，保留原生事务台账与 Server 发布记录，因此 Server 仍有未确认提交时也允许执行。关账只结算原生台账、不结算 Server 提交；同一操作存在 Server 未确认提交时返回 409 `runtime.publication_unconfirmed`，否则关掉原生事务会留下永远无法确认的发布。发布权威不可读返回 409 `runtime.publication_state_invalid`，不据损坏文件放行。

客户端 `RemoteRuntimeCatalogClient` 现按运行时选择各自闭合的停止/关账路由，不再把这两条动作限定为 sing-box；桌面 Mihomo 页面尚未接入恢复面板。

在指定 `192.168.1.7` Debian 13 x64 上常规 **1249 项**通过（HTTP **34**，此前 22；Helper/Server 集成 39），真实 `native-mihomo-systemd` **167 项**通过。日志为 `/home/nanami/mr03-recovery-verbs-full3-20261019.log`、`/home/nanami/mr03-recovery-verbs-systemd-20261019.log`。生产安装已换为 `0.1.0-dev20261019-mr03`（Linux system 模式、自签证书含 `192.168.1.7`、三种文件访问范围均为 `/`、无防火墙规则）：实测 `/healthz` 200、LAN 可达、`GET /mihomo/installation` 与 `/recovery` 返回实测事实、无提权的关账返回 `elevation-required` 403。生产安装注册、客户端版本入口、资源分发与 TUN 网络事务仍待完成。

### 2026-10-19：命名恢复 HTTP 入口

公开 `GET /managed-runtimes/mihomo/recovery`（统一 API 前缀下）要求 RuntimeRead；`POST /managed-runtimes/mihomo/recovery/retry` 要求 RuntimeManage、Mihomo 安装权限及目标为 mihomo 的 MihomoInstall 提权。请求只携带精确操作 ID。`MihomoRuntimeRecoveryService` 在同一发布 Gate 内检查 Server Pending：分派安装、配置或卸载的原意图恢复；不匹配 ID 立即拒绝，不访问 Helper。没有 Server Pending 时才调用 Helper 原事务恢复。受理后不因 HTTP 断开中断对账，不重新准备制品、安装或生成凭据。响应代表原生及 Server 提交的组合结果，不能将原生成功但 Server 未提交报告为成功。

默认 DI 已接入恢复协调器；Linux 常规 **1233 项**通过（卸载发布/宿主组合 **92**、HTTP **22**、Helper/Server 集成 **35**），覆盖只读公开查询、权限/提权拒绝、空/冲突操作 ID，以及保留与删除数据的已提交原生卸载返回丢失后经命名恢复完成 Server 提交。真实 systemd **167 项**通过，包含实际卸载返回丢失及宿主清理失败后，通过该命名恢复协调器完成提交且不重复卸载。日志为 `/home/nanami/mr03-recovery-endpoints-full-20261019.log`、`/home/nanami/mr03-recovery-endpoints-systemd-20261019.log`。桌面/Android 尚未接入新恢复 UI，生产安装注册/版本入口仍待整体替换；此入口不等于生产多版本更新交付。紧急停止与操作员关账当时仍未开放 Mihomo HTTP，已由上方“紧急停止与操作员关账 HTTP”节推进。

### 2026-10-19：卸载宿主代理清理与公开恢复状态

卸载发布现在必须冻结 `MihomoSettingsPublication`：读取并校验原设置及文件摘要，候选仅将 `SystemProxyEnabled` 置为 false，保留其余偏好。加密 Pending 的 removal 必须包含此设置意图，拒绝缺失或改变其他字段的状态，不提供旧格式回退。坏设置在原生卸载开始前拒绝。

原生卸载成功并核对精确回执、实例及状态修订后，Server 通过所有权感知宿主代理事务清理自己拥有的代理，再原子保存设置；再次核对 Helper 的缺席状态与同一回执，最后清理匹配镜像并确认发布权威。即使原偏好已经关闭，也必须取得宿主清理证明。外部修改的代理值保留；原生补偿时不修改宿主或设置。宿主失败、Helper 暂不可用、返回丢失、设置漂移或确认窗口中断保留同一 Pending，启动对账只补齐剩余提交，不重复卸载，不覆盖后续编辑。

公开运行时观察同时反映 Server 的未确认发布：原生已卸载但宿主提交未完成时，仍显示命名待恢复事务；原生存在不同操作则保留冲突身份。发布权威损坏时撤回归属/活动版本证明并返回未知，不能显示为正常未安装。内部编排单独读取 Helper 实测事实，不能使用公开投影自证提交。恢复查询也合并 Server 待提交的操作身份，不把原生已完成误报为无需恢复；保留实际冲突身份，不从发布意图虚构失败补偿或原生阶段。损坏权威保留已有原生恢复事实并报告状态无效。

指定 `192.168.1.7` Debian 13 x64 常规检查 **1220 项**、真实 `native-mihomo-systemd` **165 项**通过，其中卸载发布/宿主组合 **88 项**、HTTP/恢复观察 **16 项**。宿主检查运行真实 `LinuxSystemProxyTransaction` 所有权及补偿算法，后端为隔离内存实现，未修改机器实际桌面/环境代理；原生验收覆盖真实卸载返回丢失、宿主清理失败、重建 Server 启动恢复及保留重装不重新启用代理。Server 的 `--host-storage-only`、`--proxy-configuration-only`、`--system-proxy-only` 回归通过。最新常规日志为 `/home/nanami/mr03-recovery-observation-full-20261019.log`；卸载组合日志为 `/home/nanami/mr03-host-removal-full-20261019.log`、`/home/nanami/mr03-host-removal-systemd-20261019.log`。

本节完成 Server 卸载宿主提交边界；独立 Helper IPC、生产安装注册/变更 HTTP、桌面与 Android 当前调用者、资源分发和 TUN 网络事务仍待整体接通，未开放生产多版本更新。以下各节为阶段证据，旧的“卸载宿主代理联动待完成”由本节取代。

### 2026-10-19：运行时计划与安装台账组合（生产注册待同步切换）

已增加 `MihomoRuntimeInstallationService`，复用 `ManagedRuntimeInstallationRequest`、`InstallationCoordinator` 和现有操作文件/资源互斥，不增加另一份用户台账。验证明确计划的运行时/动作/制品绑定、确认与幂等键；安装走候选材料引导/发布确认，卸载走专属发布对账，更新/修复/回退走当前已确认材料与 Helper 归档事务，不调用固定版本清单、上传引用、草稿或直接控制器重载。动作仍由 Helper 复核完整冻结制品/信任/状态修订。

台账恢复在同一发布 Gate 内区分 Server 命名安装/卸载 Pending 与普通 Helper 程序事务；已成功的原生安装/卸载还必须核对 Server 对应确认记录及实测配置身份，不能凭 Helper 成功回执或进程存活推断发布完成。没有回执也要取得 Helper 实例锁后才能记录未开始。实例忙或 Helper 暂不可用重试原操作，不重新安装。返回消息丢失时读取原事务证据完成对账，受理后的此项对账不受 Server 停止 token 中断；关机期间也不把已提交成功误写为中断。

取消走请求交付，worker 等待原生取消/补偿结果，只有 Host 报告可取消时才提供按钮；Root 已取消按 Cancelled 落账。新增明确的 `InstallationInterruptedException`，将原生中断与失败、用户取消分别记录；启动恢复也允许受信任域返回已证明的 Cancelled。恢复未完成保留发布/Helper Pending 并报告相应问题码，不抹掉恢复事实。台账仅含请求摘要/阶段/问题码，发布 YAML、控制器秘密和授权通道不进入操作文件。

在指定 Debian 13 x64 上，最终常规 **1159 项**通过，其中安装发布/台账组合 **86 项**、默认注册构造 **5 项**。组合检查覆盖成功/失败/取消/中断、幂等重放、消息丢失、停止 token 与消息丢失竞态、协作取消交付、程序变更不调用安装引导、未开始及 busy 后的命名恢复。真实 `native-mihomo-systemd` **158 项**通过：实际首次安装丢失响应后，由持久 Running 台账启动对账确认；后续 Repair、1.19.10→1.19.30 Update、Rollback、保留数据 Uninstall、保留材料 Install 均经台账 Start 执行，每次丢失真实 Helper 返回消息仍核对原回执/发布状态，实测阶段进入原台账，未重复执行变更。测试依然不自动启动首次安装服务或启用开机启动。沙箱夹具现显式将脚本换行为 LF，避免 Windows 源码换行让 Linux shebang 变为 `/bin/sh\r`；生产沙箱权限没有放宽。

准确边界：适配器已作为具体类型注册以验证默认依赖构造，**尚未替换生产 `IInstallationService` 的 Mihomo 注册**。隔离测试显式选用新适配器，实际 Helper 生命周期仍在测试进程内，未覆盖独立 Helper 传输或生产 HTTP/PAM。正式注册切换必须同一变更同步升级桌面/Android 当前调用者、移除旧固定清单安装请求；没有增加备用 HTTP 入口、旧新请求双解析或兼容适配。宿主代理卸载联动、资源分发、客户端版本界面和 TUN 网络事务仍待完成。日志为 `/home/nanami/mr03-ledger-full-20261019.log`、`/home/nanami/mr03-ledger-systemd-20261019.log`。生产安装未替换，本机 `https://127.0.0.1:5000/healthz` 实测 200。

以下各节保留分阶段证据；“安装台账尚未接通”现指生产注册与客户端入口尚未切换，不能将本节组合验收宣称为生产端到端交付。

### 2026-10-19：活动档案元数据纳入发布提交

受管档案激活现在将 `MihomoProfilePublication` 写入加密 Pending 的必需 `profile` 字段（无选择变更时显式 null），冻结目标档案 ID/修订/创建时间与原活动档案 ID/修订/创建时间，并参与配置发布请求摘要。不读取或解析缺少该字段的旧状态。候选 YAML 及运行时计划继续独立冻结，恢复不从后续草稿重新生成候选。

Host-global SQLite 迁移 **16** 新增单行 `proxy_profile_publication` 提交标记，保存操作 ID、档案 ID、预期/提交修订、配置身份与完整选择意图摘要。元数据提交在一个 SQLite 事务内复核目标档案及原活动选择，切换活动标志、递增目标修订并写入同一操作标记。任何一步失败整体回滚元数据；同一操作的精确标记只核对已提交选择，不重复递增修订。不同操作、配置身份/意图变化、删除/改名修订冲突或同 ID/同修订重建但创建身份变化均不能冒充原选择。此表是当前选择的内部提交证明，不是新增用户操作台账。

`MihomoConfigurationPublicationService` 在实测 Helper 成功并核对候选凭据后提交档案元数据，完成后再次核对 Helper 实例/修订/回执，最后确认发布权威。`ProxyConfigurationTransactionService` 不再在权威确认之后单独调用无操作标记的 `SetActiveAsync`。数据库临时失败或已提交响应丢失保留原 Pending；启动恢复核对原 Helper 回执并补齐/核对 SQLite 标记，不再次切换服务。真实冲突继续待恢复，不覆盖后续编辑。无需改变候选 YAML 的重复选择也持久提交选择意图，但不执行原生事务、不伪造 Helper 回执。

本轮在 `192.168.1.7` Debian 13 x64 的常规检查通过 **1104 项**（Server 配置发布 **85**、真实 SQLite 选择事务 **19**）；最终 `native-mihomo-systemd` **149 项**通过，原生专项新增真实 SQLite 标记失败时元数据回滚但 HTTP 服务保持、重建 Server 启动恢复完成选择、相同配置选择不触发原生切换，详见 `/home/nanami/mr03-profile-systemd-20261019.log`。常规日志为 `/home/nanami/mr03-profile-full-20261019.log`。另通过 Server `--host-storage-only`、`--proxy-configuration-only` 与 `--system-proxy-only`，覆盖迁移/原有档案库以及原配置/系统代理回归。Helper 仍为测试进程内边界，尚未验证独立 IPC 或生产 HTTP/PAM。生产安装保持原版本，本轮未部署替换。

以下设置与档案发布段落保留分阶段记录；其中“活动档案元数据尚无自动对账”已由本节推进。资源分发、卸载宿主代理联动、安装/恢复变更 HTTP、台账/UI 和 TUN 网络事务仍待完成。

### 2026-10-19：受管设置发布与中断对账

`MihomoSettingsService.UpdateAsync` 已对有已确认发布的受管安装接入公共配置发布事务。候选仅从当前 Published 派生，保留当前 TUN 激活与排除范围，使用现有只读控制器凭据；冻结实测安装/程序/状态修订及设置意图。`MihomoPendingPublication.settings` 为当前加密状态的必需字段（无设置变更时显式为 null），保存旧设置文件摘要、旧设置与候选设置，参与幂等请求摘要，不解析缺字段旧格式。持久状态读取再次验证候选设置能从旧 Published 精确生成同一份候选 YAML，不能把其他配置与设置意图混合。

原生切换成功后，在发布共锁内依次核对精确 Helper 回执、替换 Server 配置镜像、复核当前候选授权、通过原有所有权感知宿主代理适配器应用设置，再以私有文件和原子改名持久保存设置；再次核对 Helper 身份/修订后才确认发布权威。宿主代理失败、设置文件写入失败或响应丢失保留同一 Pending，启动恢复重读冻结意图及同一 Helper 回执，继续剩余步骤，不重新执行原生切换、不回写无关设置、不提前报告成功。原生补偿失败继续使用原有恢复门禁；原生已补偿则不应用候选设置。应用设置的宿主代理失败属于 Server 提交待恢复，不能据此伪造 Helper 的 RecoveryRequired/成功回执。

当候选 YAML 与旧发布字节完全一致（例如只修改订阅信任选项），同样持久保存设置意图、复核凭据/配置镜像/安装修订并完成宿主/设置提交，但不调用 Helper 配置切换、不重启服务、不伪造原生回执。启动系统代理对账已允许健康已确认受管安装，Pending、坏设置、凭据撤销或实例漂移仍拒绝。TUN/透明代理网络切换继续不支持；完整 GEO/provider/凭据分发、活动档案元数据对账、卸载时宿主代理联动、安装台账/版本 UI 仍待完成。

最终代码在指定 Debian 13 x64 主机通过 **1071 项常规检查**，其中 Server 配置发布事务 **71 项**；`native-mihomo-systemd` **146 项**。新增覆盖设置成功/原生补偿、响应丢失、宿主失败后中断恢复、无关设置漂移保护、坏设置/凭据拒绝、已有候选设置文件的提交中断窗口、冻结设置与候选不匹配、缺少当前 settings 字段拒绝，以及无需原生切换的宿主设置变更。真实专项新增设置入口改变监听并实际转发 HTTP、宿主设置失败后启动对账、设置文件/权威确认，以及仅改订阅信任时 Helper 状态修订和原生调用数保持不变。宿主代理副作用使用记录适配器及失败注入，未修改测试机实际系统代理；Helper 生命周期仍在测试进程内，未覆盖独立 IPC/生产 HTTP/PAM。日志为 `/home/nanami/mr03-settings-full-20261019.log` 与 `/home/nanami/mr03-settings-systemd-20261019.log`。本次仍以隔离测试包验收，生产安装未替换。 同机 Server 回归 `--proxy-configuration-only` 与 `--system-proxy-only` 均通过，覆盖原有激活/回滚、系统代理能力/PAC 门禁、持久化失败补偿、守护并发及 Pending 阻塞。

以下档案接线及 2026-10-18 段落保留分阶段证据；“设置尚未接线”已由本节推进，其余未完成项仍有效。

### 2026-10-19：应用配置档案发布接线

`ProxyConfigurationTransactionService` 对已确认的受管 Mihomo 发布接通实际档案保存/激活：保存只做有界结构及资源/网络门禁，不调用旧固定清单引擎、不准备旧数据目录；激活在同一发布 Gate 内读取现有凭据与宿主设置，冻结当前 Helper 安装/程序/状态修订及旧候选摘要，再经 `MihomoConfigurationPublicationService` 的持久事务完成目标原生校验、切换、健康核对及 Server 镜像/权威确认。受理后事务不随客户端取消；丢失响应保留 Pending 交由现有启动恢复，不直接回写旧配置或推断成功。只有实测确认后更新活动档案元数据；元数据写入失败报告待恢复，不撤销已提交运行时发布。该元数据尚无独立自动对账，不能称为档案元数据与运行时的跨存储原子提交。

凭据读取只使用 `ReadExistingAsync`，撤销/缺失不生成替代秘密。未知/损坏权威、已有 Pending、Helper 配置漂移及待恢复均拒绝写入。未完成受控资源分发前，文件 provider/证书/密钥引用明确不支持；TUN/透明代理网络切换继续拒绝。未建立发布权威的已有路径没有因此取得新运行时所有权。设置、TUN、系统代理写入及安装台账/版本 UI 仍待后续接线，尚未开放生产多版本更新。

验收使用指定的 `192.168.1.7` Debian 13 x64：常规 **1045** 项通过，Server 配置发布事务由 34 增至 **45** 项，新增应用保存不触达固定运行时、实测成功/补偿/响应丢失、凭据撤销与资源/TUN 拒绝。`native-mihomo-systemd` 由 141 增至 **143** 项，新增应用 `ApplyAsync` → 真实 Helper 生命周期 → systemd 服务的监听变化/HTTP 转发，以及原生无效规则拒绝后保留旧配置与档案选择；档案元数据使用记录夹具，Helper 仍在测试进程内，未覆盖独立 IPC 或生产 HTTP/PAM。测试完成清理测试自有服务与运行时根。

运行命令：`RelaxKonOS.ManagedRuntimes.Tests mihomo-publication-only`（45 项）；`RelaxKonOS.ManagedRuntimes.Tests`（1045 项）；`TMPDIR=/var/tmp RelaxKonOS.ManagedRuntimes.Tests native-mihomo-systemd <1.19.10.gz> <1.19.30.gz>`（143 项）。远端日志为 `/home/nanami/mr03-application-full-20261019.log` 和 `/home/nanami/mr03-application-systemd-20261019.log`。本次用隔离测试包验证，现有生产服务仍为 `0.1.0-dev20261018-mr03`，未将测试二进制覆盖到生产安装。实测现有部署为 Linux system 模式，三种文件访问范围均为 `/`，nftables 空规则，自签证书 SAN 已含 `192.168.1.7`。

以下为 2026-10-18 的分阶段实现记录；其中“实际配置发布尚未接线”已由上述有范围的档案发布接线推进，其他未完成项仍有效。


版本生命周期遵循[公共设计](../platform/RelaxKonOS.ManagedRuntimes.Design.md)。当前已有 Mihomo 的签名目录、单文件 gzip 解包、严格 `v` 标签及 `mihomo-v1` 稳定版能力范围（`1.19.0 ≤ v < 1.20.0`）；目录发布者不能扩大配置支持范围。

已增加 `/managed-runtimes/mihomo/installation` 观察及 `/managed-runtimes/mihomo/plan` 冻结计划 HTTP 入口（均带当前 API 版本前缀）。观察使用 RuntimeRead，计划同时要求 RuntimeManage 和 Mihomo 安装权限，并受系统模式宿主能力限制；计划不执行变更、下载或取得控制器凭据。Server 发布 Pending 在调用第二个 Helper 之前阻止新计划。10 项真实 Kestrel HTTP 检查使用测试认证主体和模拟 Helper，覆盖匿名/仅管理权限/错误安装目标/正确权限、未知状态保留、非法来源与扩展路径字段拒绝、无来源卸载计划、User Mode 拒绝和待发布阻塞；不等于生产 PAM/JWT 或安装 HTTP 端到端验证。实际安装台账/客户端当前仍需替换，未增加旧请求解析或备用执行入口。

终态观察现每次核对固定宿主归属、停止时的程序/配置/数据权限、登记程序与数据路径、不可变程序字节身份、配置摘要和运行进程的已应用配置身份；异常返回 Unknown 并撤回当前版本/活动修订证明，不沿用台账中的“未安装”或“已验证版本”。FIFO 等特殊文件在打开哈希流前拒绝。未结束事务保留其持久阶段事实，不把合法切换中的暂态当作外部接管。查询不改配置/服务或状态修订；计划在同一实例锁内重测，只有可证明的非受管归属变化才更新归属并递增修订，使旧计划失效，不自动接管服务或文件。

恢复另有固定单元归属/零 PID 停止证明，不能用它宣称程序健康。程序权限或映像异常时，恢复状态同时保留完整性问题和单元是否停止；紧急停止/人工确认关账不要求先恢复坏程序或重新授权凭据。真实专项新增停止服务的可写程序拒绝、异常程序权限下的单元停止证明与紧急停止；模拟边界另覆盖外部服务出现/消失、观察失败、字节/配置/应用身份漂移、特殊文件及关账后不虚报健康。

`MihomoRuntimePreparation` 已组合保护账本授权、完整冻结版本记录比较、制品下载/解包、目标原生版本/平台测量及当前 Server 生成配置的 `-t` 校验。仅允许当前宿主、固定服务宿主和精确的 `mihomo`/`mihomo.exe` 入口；原生检查后重查信任与配置身份，失败或取消清理本次暂存。调用者仍须持有安装锁并提供保护目录；该组件不注册服务、不切换活动版本，也不是客户端可直接调用的执行授权。

`MihomoArchiveAdapter` / `MihomoRemovalAdapter` 已接入公共归档切换/移除事务，提供完整冻结记录、当前配置授权、声明的构建特性、依赖与版本排序门禁，以及安装、跨版本更新、同版本修复、显式回滚、补偿、命名恢复和卸载。回滚/卸载失败后的重启必须重新核对当前配置授权及制品信任，不能恢复已失效的发布。观察/计划 HTTP 已接入，实际变更仍未接当前安装台账/客户端。

`MihomoRuntimeLifecycle` / `MihomoRuntimeOperations` 已提供 Linux 固定作用域的查询、计划、执行、配置发布、恢复、取消、紧急停止与确认关账闭合入口，并在通用 Helper 分发器中登记。入口拒绝混合路径/来源字段、跨运行时冻结计划与通道、错误 operationId 和多余确认字段；配置计划只允许专属 `MihomoRuntimeConfigure` 操作，不能夹带到其他 Mihomo 或 sing-box 操作。观察失败保持未知，不初始化为未安装。程序计划和执行共用包含 1 GiB 数据修订空间的依赖要求。取消组件统一为 `RuntimeCancellation`，sing-box 与 Mihomo 分别在自己的保护根和操作 ID 下使用，不保留旧类型别名。

新鲜授权现由共享的 `RuntimeAuthorizationServer<T>` / `RuntimeAuthorizationClient.ReadAsync<T>` 传递。当前唯一格式的描述符强制携带 `RuntimeId`，HMAC v2 上下文绑定运行时、通道、操作、角色和 nonce；sing-box 调用者与测试同步改用当前契约，拒绝跨运行时冒用，不解析 v1。`MihomoPublishedRuntime` 为保护的本机回调材料，包含 YAML、构建标签、精确监听和控制器凭据；不会进入普通状态、冻结计划或操作台账，诊断字符串脱敏，传输与 Helper 均核对结构和大小。

Server 的 `MihomoPublicationStore`、`MihomoRuntimeAuthorizationProvider`、`MihomoRuntimeService` 组合已实现，**已在默认 DI 注册，实际应用写入及变更 HTTP 尚未切换**。发布状态以 Data Protection 加密，使用当前修订和命名事务区分待发布、已确认与已补偿；确认须提供固定 Helper 实测的配置身份，未确认状态不能授权程序切换。授权每次重读已确认状态，核对 Server 自有 active.yaml 的字节身份及现有控制器凭据，租期 30 秒，不读取无关草稿、不创建秘密。生命周期服务全程持有发布域门锁，回调不重复获取锁；配置、设置与 TUN 写入方已共用门锁，仍须替换实际发布调用并完成确认流程后才能开放生产入口。现有秘密存储新增只读 `ReadExistingAsync`，不会创建或修复文件。

共享 `RuntimeArchiveConfiguration` 已实现配置专属的持久事务核心：冻结安装/宿主/程序修订、旧与候选配置摘要及预期状态修订，共用归档实例锁和回执；只变更配置身份，不重新下载程序或新建程序修订。切换点之前可取消，之后必须完成或补偿。恢复不重做准备、不生成秘密；先停止候选、重新授权旧配置，再恢复并核验服务、开机启动和自有资源，核验成功后才提交旧身份。补偿失败持久保留待恢复状态，阻止新配置发布、程序切换和卸载。操作员关账仍须明确确认且服务实测停止，关账不把旧配置恢复或候选健康标成成功。当前状态格式要求显式 `configuration` 字段（无事务时为 null），不兼容缺该字段的旧账本。

`MihomoConfigurationAdapter` 已组合该核心与真实固定宿主：准备使用 seed-data 沙箱，实测停止服务后将有界数据完整复制到暂存目录，再原子改名为操作的正式备份，未完成副本不能用于恢复。提交数据变更标记后才校验/应用候选；备份缺失且已有变更标记时拒绝恢复。补偿先从保留备份准备完整替换，再恢复活动数据与旧配置；重试不消耗备份。原生校验不写入运行中的活动数据。配置发布保留原程序修订和数据路径，数据备份由操作回执保留，尚未自动裁剪。配置预检额外预留两份 1 GiB 数据空间，仍须独立完成资源准备。

配置回调使用专属 `MihomoConfigurationAuthorization`，显式命名原发布操作并分别提供旧/候选授权，适配器与持久事务 ID 比较。恢复通道可以有新的请求 ID，但不得改换原发布目标；普通生命周期 grant 与配置 grant 不能互相解码。候选授权被撤销不妨碍单独授权的旧配置补偿，旧授权撤销则保持停止/待恢复。认证通道在回复前准备下一监听，避免 Unix 上连续读取连接到正在关闭的旧监听；授权提供者仍串行执行。

`MihomoConfigurationPublicationService` 已组合 Server 待发布授权、Helper 配置执行、结果核验、Server 文件镜像与加密发布权威的确认/补偿。全程持有发布 Gate，接受后不随客户端取消中断；传输丢失保留待发布状态。恢复从加密状态重读完整冻结计划，以同一操作的 Helper 回执、安装/活动程序修订、配置身份和待恢复状态对账，不按进程存活或响应缺失推断成功。Helper 成功后先原子替换 Server 镜像再确认；该窗口内专属待发布授权只接受冻结的旧/候选镜像摘要，普通运行时授权仍拒绝待发布状态。补偿结果必须实测旧身份后才能 Abort。若 Server 已接受但 Helper 尚未启动，恢复必须经过 Helper 实例锁并再次观察原状态修订，单独记为 NotStarted，不伪造 Helper 回执。最近一次配置结果仅保留请求摘要和确认/未启动标志供幂等核对，操作记录仍以 Helper/现有安装台账为准。当前加密状态要求显式的 pending configurationPlan 和 lastConfiguration 字段，不解析缺字段旧格式。

`MihomoRuntimeAuthorizationProvider.ReadConfigurationAsync` 每次重读命名待发布事务与现有控制器凭据，分别授权旧/候选材料；凭据轮换不能授权已撤销的一侧，不创建秘密。Server 编排已通过真实 systemd 服务组合验收，**默认 DI 与启动恢复已接通，变更 HTTP 与实际配置/设置发布调用仍未替换**；首次安装已有专属 Server 编排及真实服务组合证据，但默认安装台账/HTTP 入口仍需接线。

默认注册现包含发布权威、授权提供者、运行时/发布编排及 `MihomoPublicationRecoveryService`。启动恢复处理已有命名配置/安装事务；空状态不联系 Helper，不从旧 active.yaml 自动接管或生成秘密。实例忙或 Helper 暂不可用时等待后重试同一事务，已经需要人工处理的结果保留 Pending 并结束自动循环。配置服务、设置与 TUN 写入及系统代理守护已改用同一个发布 Gate；尚未替换的新发布调用不能走旧的文件覆盖路径绕过权威：Pending/不可读状态返回待恢复，健康受管发布返回该直写方式不支持，不误报安装故障。这是写入隔离与启动恢复，**不是**默认应用发布链路已全部替换；旧固定运行时管理、应用安装入口及资源传递仍待整体替换。

首次安装已有专属授权边界：`MihomoInstallationAuthorization` 绑定原操作 ID、完整冻结安装计划摘要及候选材料。Helper 只在同一持久 Install 事务（原程序修订为空，切换后活动修订只能属于该操作）内选择此授权；普通/配置/安装三类 grant 不能互相解码。保留数据重装还要求候选配置身份等于保留身份，不能借重装发布无关配置。恢复可用新通道，但仍绑定原安装事务和计划。真正没有旧配置/旧服务的首次安装在候选撤销后允许停止并清理本次资源、补偿到实测未安装；这不恢复或启动任何撤销配置。有旧配置/程序时仍重新核对其授权。

Server 的加密 Pending 已可保存专属 installationPlan（当前格式必须显式存在，可为 null，不能与 configurationPlan 并存），`ReadInstallationAsync` 每次重读冻结计划和当前凭据，只签发命名候选，绝不提前建立 Published。已有发布的保留重装还要求材料完全一致。`InstallUnderGateAsync` 提供专属类型化安装调用；普通 `ExecuteAsync` 拒绝未经安装发布引导的 Install。`MihomoInstallationPublicationService` 已接通安装确认/幂等/补偿/中断编排，启动服务按 installationPlan 分派恢复；默认应用安装台账和 HTTP 尚未替换，不能称为应用首次安装端到端完成。

安装编排接受后持久保存完整冻结目标，成功必须核对同一操作回执、安装 ID/宿主/目标、该操作的活动修订、目标实测版本和候选配置身份；安装前不创建已确认发布或 Server 镜像，确认时才创建私有镜像。响应丢失保留 Pending；启动恢复先核对同一回执，缺回执/有事务时进入 Helper 命名恢复并复测状态，不重跑下载或安装。未开始结果须经过 Helper 实例锁并核对原状态修订，不伪造回执。首次失败恢复至未安装时不留下已确认发布或镜像；已有保留发布的重装保留原材料。未知现有 Server 文件禁止接管或覆盖，镜像写入后的中断只接受已冻结候选摘要。lastInstallation 显式保留最近安装请求摘要及确认/未开始标志，当前状态格式必须含此字段；安装成功和失败重放均不重新执行目标。真实验收从无已确认发布开始，安装后模拟响应丢失，通过重建 Server 和启动恢复完成确认；没有用预置 Published 绕过首次安装。

每个程序修订对应独立 `data/<revisionId>`。准备检查使用保护的 `seed-data`；更新/修复在实测服务停止后复制最新数据，再用目标二进制检查，失败版本不会写入旧修订的数据。显式回滚从目标的保留修订复制数据，回退会放弃较新修订的缓存/选择变化。复制限制为 1 GiB、4096 项和 32 层目录，拒绝链接及 FIFO 等特殊文件，写入私有文件。磁盘预检包含这部分独立空间。保留数据卸载保留数据修订、配置、原有 Server 凭据引用与保护的 `retained-data.json` 来源记录；重装核对格式与版本，拒绝将较新数据交给较旧二进制。删除数据卸载清理这些副本与来源记录。当前不自动裁剪保留的数据历史。

Linux 固定宿主为 `LinuxMihomoArchiveHost`：`/var/lib/relaxkonos/managed-mihomo` 和 `relaxkonos-mihomo.service`。保护根与旧的 Server 可写 proxy 目录分开；不接管既有外部/旧注册。宿主核对根所有权、精确 unit/程序/数据路径、程序摘要、真实进程参数与启动身份、systemd 本次加载的配置凭据，以及归属该 PID 的混合端口和控制器监听。健康检查还要求本机控制器凭据正确、报告版本匹配，限制正文大小和总耗时，不使用代理或跟随重定向。服务只开放自身数据目录写入，限制设备和能力；当前不授予 TUN 的网络权限。原生 `-t` 在 systemd 临时沙箱执行：宿主与输入只读、候选数据可写、私有临时目录、无网络/设备特权，不在检查中下载 GEO/provider 资源；这些资源须预先准备。

配置路径策略现由共享 `MihomoYaml` / `MihomoConfigurationPolicy` 实现。YamlDotNet 18.1.0 只用于结构解析/生成，限制单文档 4 MiB、64 层、10 万解析事件/展开节点及 4 MiB 展开文本；拒绝循环/前向/重复锚点、重复键及非支持标签，按 YAML 合并优先级展开锚点和合并键，保留字面量与显式标量类型。Server 的控制器、DNS、运行时和 TUN 重写已使用该结构，不再逐行猜测根键；控制器附加监听、UI 下载/目录等字段也归 Server 所有。无效配置映射为应用错误，不输出原始 YAML。

Helper 从实际 YAML 重新派生控制器、监听、TUN/透明代理网络需求和必需构建标签，核对回调声明；不能用假监听、假 secret 或假的“无需网络切换”标志通过门禁。资源只允许保护数据目录下的 `proxy_providers/`、`rule_providers/`、`certificates/`、`keys/` 相对路径，拒绝绝对路径、穿越、链接与 FIFO 等特殊文件；区分 WebSocket URL 路径、WireGuard 内联密钥与真正的文件引用。本地 proxy provider 内的节点及已知传输层凭据同样检查，资源总数最多 128，单文件最多 16 MiB，provider YAML 为 4 MiB 且严格 UTF-8。HTTP provider 必须先由 Server 受控准备成文件，不能在 root 原生检查或服务启动中自行下载；缺资源明确拒绝。自定义入站列表暂不属于已验证的监听/健康适配范围，不能绕过门禁开启。这里交付的是结构和范围门禁，**不是** GEO/provider/凭据文件的自动准备、完整性分发或协议互通。

2026-10-18 在 Debian 13 x64 测试机 192.168.1.7 上通过：

- 常规 1034 项，其中首次安装授权 17 项（持久 Install 绑定/保留身份/三类 grant 分离/Server 冻结候选及撤销）、数据修订 11 项、授权 34 项（含配置/普通 grant 严格分离、命名恢复及 64 次连续读取）、Server 发布权威/组合 33 项、Server 配置发布事务 34 项、Server 安装发布事务 32 项、Server 卸载发布事务 34 项、默认 DI 4 项、Mihomo 生命周期/观察/恢复边界 38 项、HTTP 10 项；YAML/配置路径策略 34 项覆盖规范化语义、合并优先级、锚点/标签/规模边界、TUN 派生、监听与凭据声明、资源路径、本地 provider 内层引用和特殊文件。Server 事务检查覆盖成功/补偿幂等、回执冲突、丢失已提交响应、待恢复补偿、未启动核对、镜像替换后的中断窗口、无关文件漂移、独立凭据撤销与启动恢复（空状态/补偿/busy 重试/人工处理）；使用真实加密文件/授权管道及模拟 Helper。共享配置事务 90 项另覆盖逐阶段失败、取消边界、跨动作阻塞、实例锁、损坏状态拒绝与关账，并以子进程在切换点直接退出验证重启恢复。Server 测试的 `--proxy-configuration-only` 在同一主机通过现有配置激活/回滚回归。
- `TMPDIR=/var/tmp RelaxKonOS.ManagedRuntimes.Tests native-mihomo-systemd <1.19.10.gz> <1.19.30.gz>` 共 141 项：26 项归档/准备，39 项真实二进制配合模拟宿主的事务/补偿/恢复/卸载门禁，47 项 Helper 生命周期/配置入口、systemd 及本地 HTTP 转发，8 项 Server 配置发布编排配合真实服务的确认/响应丢失对账/幂等/原生拒绝补偿，6 项首次安装发布/丢失安装响应/启动确认/重放，11 项 Server 卸载发布/响应丢失/启动对账/镜像删除中断/保留材料重装，4 项原生沙箱隔离。首次安装另验证错误冻结计划授权不创建服务、候选撤销后补偿为未安装；配置专项验证真实监听变更/HTTP 转发、保留程序修订、停止后的数据快照、FIFO 导致快照中途失败后的补偿、候选授权撤销后的配置/数据恢复、旧授权撤销时停止并保留事务、以新通道恢复原事务，以及停止服务发布时保留开机启动。高频显式重启曾实测触发 systemd start-limit-hit；固定宿主在核验归属的显式启动中处理失败锁存，仅对实测限流结果重试一次，不禁用自动重启限流。真实操作经 Helper 计划/执行/配置/恢复入口与逐操作的类型化授权通道，使用隔离签名目录及已发布配置夹具；Helper 入口在测试进程内调用，未覆盖独立 Helper 进程传输或默认生产 HTTP/台账路径。另覆盖安装后不启动/不启用开机启动、1.19.10→1.19.30 更新、同版本修复、回退、数据隔离、独立开机启动、错误控制器凭据、保留数据卸载/重装和删除数据卸载，以及实测阶段/无秘密回执。测试结束移除测试自有 unit 和运行时根。
- 不注册持久 Mihomo 服务的 `native-mihomo-archive` 命令执行前两组（65 项）；原生检查仍需 root/systemd 沙箱。`/tmp` 为不足 1 GiB 的 tmpfs，无法满足真实数据修订磁盘预检，因此真实归档验收使用磁盘上的 `/var/tmp`。

版本范围是配置能力声明，真实二进制证据仅覆盖上述两版；HTTP 混合监听转发不证明所有出站协议或 TUN 互通。

准备门禁阶段已将测试宿主升级至服务端包 `0.1.0-dev20261018-mr03`（Linux system 模式），安装器完成 Server `/healthz` 和 Guardian IPC 核验。TLS SAN 包含 `192.168.1.7`，沿用升级前证书；普通/管理员/root 文件访问范围均为 `/`，nftables 无规则。本节随后新增的事务/沙箱通过独立测试宿主执行，尚未接入该生产安装；部署与健康证据不替代 Mihomo 服务事务验收。

首次安装材料选择已由 `MihomoInstallationBootstrapService` 在发布共锁内实现并注册：首次生成当前设置的 DIRECT 基线、TUN 关闭且无 GEO/provider 资源；待发布重试与保留安装复用冻结/已确认材料，不重新生成凭据。测试包含启用 DNS 的基线，并通过两个真实版本的 systemd 专项；默认安装台账和客户端接线仍待完成。

卸载已增加 `MihomoRemovalPublicationService`：命名冻结计划与旧发布材料一起持久保存，成功须核对同一实例的实际成功回执、递增修订、移除状态及保留/删除配置身份，再保留材料或删除精确匹配的 Server 镜像和加密发布材料。失败只在原安装/配置实测恢复后补偿确认；响应丢失与镜像删除中断由启动恢复对账，不重跑卸载。应用拥有的设置和规范控制器凭据继续保留。默认 DI/启动恢复已注册；默认安装台账及客户端尚未切换。

MR-03 仍在实施：生产 `MihomoRuntimeManager` 尚用固定清单；新 Server/Helper 组合的实际配置/设置/HTTP 发布接线、GEO/provider/凭据资源的受控准备和完整性分发、TUN 网络恢复边界、更新/修复/回滚/卸载台账与客户端版本选择尚未完成，Windows 端到端未验收。当前实际配置或授权声明涉及网络切换时，适配器在停止现有服务之前拒绝；不能借普通程序更新绕过网络恢复。尚不能作为生产多版本更新交付，现有固定版本重装仍不能称为升级。

# 1. 文档目的

面向手机、电脑提供代理服务的应用另见 [代理服务端应用设计](RelaxKonOS.ProxyServer.Design.md)（sing-box，尚未实现）。本文中的引擎扩展指宿主出站代理管理，不包含该独立应用的入站、代理使用者和订阅发布模型。

本文定义代理管理器的职责、接口与安全边界：

```text
Proxy Manager
代理管理器
```

该模块负责在 RelaxKonOS 所管理的当前主机上安装、配置、启动、停止、监控和升级代理核心，并通过 RelaxKonOS Avalonia 客户端提供完整管理 UI。

第一阶段仅正式支持：

```text
Mihomo
```

但架构必须允许未来增加：

```text
sing-box
Xray
其他 headless proxy core
```

禁止把模块实现成：

```text
MihomoManager
ClashManager
```

整个功能必须围绕：

```text
IProxyEngine
```

建立抽象。

Mihomo 只是第一种 Engine。

---

# 2. RelaxKonOS 既有架构约束

实现过程中必须遵循 RelaxKonOS 已确定的架构，而不是为了 Proxy Manager 建立第二套体系。

主要约束：

```text
Client
    Avalonia
    MVVM
    RelaxKonOS-owned workspace/modal infrastructure

Server
    .NET 10
    Windows + Linux

Communication
    RelaxKonOS API

Platform abstraction
    Windows/Linux 差异封装在 Server

Privilege
    RelaxKonOS 统一权限系统
    RelaxKonOS 统一 elevation workflow
```

Avalonia 是 RelaxKonOS 的主要客户端实现。

Flutter 客户端即使存在，也不得影响本模块的领域模型和 Server API 设计。

本规范主要要求：

```text
RelaxKonOS.Client
RelaxKonOS.Server
RelaxKonOS.Shared / Contracts
RelaxKonOS.Helper（如现有）
```

---

# 3. 核心设计原则

Proxy Manager 必须遵循以下原则。

## 3.1 Client 永远不直接连接 Mihomo

禁止：

```text
Avalonia
    │
    └── http://server:9090
             │
             ▼
          Mihomo
```

正确结构：

```text
Avalonia
    │
    │ RelaxKonOS API
    ▼
RelaxKonOS.Server
    │
    │ localhost / local IPC
    ▼
Mihomo
```

Mihomo Controller 默认只允许本机访问。

例如：

```yaml
external-controller: 127.0.0.1:9090
```

或者未来 Windows 可考虑：

```text
Named Pipe
```

Linux 可考虑：

```text
Unix Domain Socket
```

RelaxKonOS Client 不应知道 Mihomo Controller secret。

---

# 4. 总体架构

目标结构：

```text
┌─────────────────────────────────────┐
│          RelaxKonOS Avalonia          │
│                                     │
│ Proxy Manager                       │
│                                     │
│ Overview                            │
│ Proxies                             │
│ Profiles                            │
│ Rules                               │
│ Connections                         │
│ DNS                                 │
│ Logs                                │
│ Settings                            │
└───────────────────┬─────────────────┘
                    │
             RelaxKonOS API
                    │
                    ▼
┌─────────────────────────────────────┐
│          RelaxKonOS.Server            │
│                                     │
│ ProxyManager                        │
│                                     │
│ ├── ProxyEngineManager              │
│ ├── ProxyRuntimeManager             │
│ ├── ProxyProfileManager             │
│ ├── ProxySubscriptionManager        │
│ ├── ProxyConnectionManager          │
│ ├── ProxyRoutingManager             │
│ ├── ProxyDnsManager                 │
│ ├── ProxyRecoveryManager            │
│ ├── ProxySecurityManager            │
│ └── ProxyAuditService               │
│                                     │
│             IProxyEngine            │
│                    │                │
│        ┌───────────┴──────────┐     │
│        ▼                      ▼     │
│  MihomoEngine          future engine│
└────────┬────────────────────────────┘
         │
         │ localhost API
         ▼
┌─────────────────────────────────────┐
│              Mihomo                 │
│                                     │
│ TUN / DNS / Rules / Proxy           │
└───────────────────┬─────────────────┘
                    │
                    ▼
              Operating System
```

---

# 5. TUN 是一级功能

本模块不得按照传统桌面 Clash GUI 的思路：

```text
System Proxy = main
TUN = optional advanced feature
```

RelaxKonOS 的设计应按照：

```text
TUN = primary server-wide proxy mode
Listener Proxy = secondary mode
RelaxKonOS-only Proxy = scoped mode
```

定义以下运行模式：

```csharp
public enum ProxyOperatingMode
{
    Tun,
    ListenerOnly,
    RelaxKonOSOnly
}
```

第一阶段至少实现：

```text
Tun
ListenerOnly
```

RelaxKonOSOnly 可以第二阶段实现。

---

# 6. Mihomo TUN 基础配置

初始生成配置可以采用：

```yaml
mode: rule

external-controller: 127.0.0.1:9090

tun:
  enable: true
  stack: mixed
  auto-route: true
  auto-detect-interface: true

dns:
  enable: true
```

注意：

这只是配置生成器的基础模板。

不得直接硬编码成为不可修改配置。

Mihomo 当前 TUN 支持 Windows 和 Linux；`auto-route` 可以自动将流量路由进入 TUN。Linux 还支持 `auto-redirect`，该功能依赖 `auto-route`。citeturn938106search3turn938106search4

因此平台能力模型必须存在：

```csharp
public sealed record ProxyPlatformCapabilities
{
    public bool SupportsTun { get; init; }

    public bool SupportsAutoRoute { get; init; }

    public bool SupportsAutoRedirect { get; init; }

    public bool SupportsDnsHijack { get; init; }

    public bool SupportsNamedPipeController { get; init; }

    public bool SupportsUnixSocketController { get; init; }
}
```

例如：

```text
Windows
    TUN                 YES
    auto-route          YES
    auto-redirect       NO

Linux
    TUN                 YES
    auto-route          YES
    auto-redirect       YES
```

UI 必须根据 capability 显示选项。

不得在 Avalonia 中判断：

```csharp
if (OperatingSystem.IsLinux())
```

平台判断属于 Server。

---

# 7. Mihomo 运行时生命周期

Windows 使用独立 SCM 服务持有 Mihomo。Server 保持 LocalService 身份，通过认证的本机命名管道请求固定生命周期动作；Helper 只管理服务，不持有 Mihomo 子进程。

```text
Windows SCM
    ↓
RelaxKonOSComponent-Mihomo-<scope>（LocalSystem 独立宿主）
    ↓
mihomo.exe
```

宿主发布目录复制到 `%ProgramData%\RelaxKonOS-Components\host\<hash>`，不依赖 Server/Helper 的安装目录。Helper 与 Server 共享 Mihomo 发布信任清单，Helper 独立校验官方 ZIP 并导入受保护的服务二进制。

独立宿主验证二进制摘要，以 Job Object 约束子进程树；Helper 停止、更新或卸载不终止独立服务。SCM 管理开机启动和失败恢复，Server 不获得通用提权执行能力。实现与隔离主机验收状态见 [独立服务进度](../services/RelaxKonOS.IndependentComponentServices.Progress.md)。

Linux 保持独立 systemd 服务：

```text
systemd
    ↓
mihomo.service
```

平台不强行共享相同的服务语义；两者都必须在 TUN 操作前后保留网络恢复机制。

---

# 8. Helper 的职责边界

RelaxKonOS 已经存在/计划存在权限提升体系。

Proxy Manager 不得把 Helper 扩展成：

```text
RelaxKonOS root shell daemon
```

或者：

```text
ExecuteAnythingAsRoot(command)
```

禁止提供类似：

```csharp
RunPrivilegedCommand(string command)
```

这样的通用接口。

---

# 9. Helper 只允许处理明确的特权操作

Proxy Manager 可以申请以下明确操作：

```text
InstallProxyRuntime
RemoveProxyRuntime

InstallProxyService
RemoveProxyService

StartProxyService
StopProxyService
RestartProxyService

ReplaceProxyBinary

WriteProtectedProxyConfiguration

RestoreNetworkConfiguration

RepairProxyService

SetProxyServiceStartup
```

对应接口必须是强类型：

```csharp
Task InstallProxyServiceAsync(...);

Task RemoveProxyServiceAsync(...);

Task StartProxyServiceAsync(...);

Task StopProxyServiceAsync(...);

Task RepairProxyNetworkAsync(...);
```

不能变成：

```csharp
Task ExecuteAsync(string executable, string arguments);
```

---

# 10. 日常运行不应频繁触发提权

理想生命周期：

第一次：

```text
User
  ↓
Enable TUN
  ↓
RelaxKonOS detects Mihomo not installed
  ↓
Permission check
  ↓
Elevation request
  ↓
User approval
  ↓
Helper
  ↓
Install Mihomo
Install system service
Create protected directories
Initialize config
  ↓
Done
```

以后：

```text
User
  ↓
Enable / Disable
  ↓
RelaxKonOS.Server
  ↓
Service Manager
  ↓
Mihomo
```

不应该每次：

```text
Enable TUN
    ↓
UAC / sudo password
```

运行时安装完成后，Linux 由 systemd 持续运行；Windows 由长期运行的
`RelaxKonOS.Server` 进程宿主持续管理。

---

# 11. RelaxKonOS 权限 ≠ OS 权限

必须保留两层权限。

```text
RelaxKonOS authorization
            +
OS privilege/elevation
```

即使 Mihomo 已经以：

```text
root
LocalSystem
```

运行，也不能意味着任意 RelaxKonOS 用户都可以控制它。

建议能力：

```text
proxy.read

proxy.manage

proxy.profile.read
proxy.profile.manage

proxy.subscription.read
proxy.subscription.manage

proxy.connection.read
proxy.connection.close

proxy.rules.read
proxy.rules.manage

proxy.tun.read
proxy.tun.manage

proxy.runtime.read
proxy.runtime.manage

proxy.recovery.execute
```

其中危险权限：

```text
proxy.tun.manage
proxy.runtime.manage
proxy.recovery.execute
```

必须经过 RelaxKonOS Server authorization。

---

# 12. Built-in App 权限模型

Proxy Manager 是 RelaxKonOS：

```text
Built-in Application
```

因此不采用第三方 Installed App 的普通授权流程。

但：

```text
Built-in
```

不等于：

```text
Unlimited permissions
```

Built-in App 可以声明其系统能力：

```text
Network.Proxy.Read
Network.Proxy.Manage
Network.Proxy.Tun
Network.Proxy.Runtime
```

最终权限仍由：

```text
Authenticated RelaxKonOS user
    ↓
role / permission
    ↓
Server authorization
```

决定。

Client 不负责最终授权。

---

# 13. 配置文件不能完全结构化重写

不要尝试第一版把整个 Mihomo YAML 转换成 RelaxKonOS DTO。

原因：

Mihomo 配置格式：

```text
proxies
proxy-groups
rules
rule-providers
proxy-providers
dns
tun
sniffer
hosts
listeners
```

非常复杂，而且会持续演化。

推荐：

```text
Raw Configuration
        +
RelaxKonOS Managed Overlay
```

---

# 14. Profile 模型

例如：

```csharp
public sealed record ProxyProfile
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public string EngineId { get; init; } = "mihomo";

    public string ConfigFile { get; init; } = "";

    public bool Managed { get; init; }

    public Guid? SubscriptionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
```

RelaxKonOS 管理：

```text
profile metadata
subscription relation
active profile
runtime state
managed TUN overlay
managed controller configuration
```

Mihomo 自己继续管理完整 YAML。

---

# 15. 配置修改流程必须事务化

沿用 RelaxKonOS 对系统配置的标准策略：

```text
Read
 ↓
Validate
 ↓
Backup
 ↓
Write temporary file
 ↓
Validate new configuration
 ↓
Commit
 ↓
Reload / Restart
 ↓
Health check
```

如果失败：

```text
Rollback
 ↓
Restart
 ↓
Health check
```

禁止：

```text
直接覆盖 config.yaml
然后希望 Mihomo 能启动
```

---

# 16. Mihomo 配置验证

更新配置前调用 Mihomo 自身的配置验证能力。

抽象：

```csharp
public interface IProxyConfigurationValidator
{
    Task<ProxyConfigurationValidationResult> ValidateAsync(
        ProxyEngineRuntime runtime,
        string configuration,
        CancellationToken cancellationToken);
}
```

如果失败：

```text
do not commit
```

返回：

```csharp
public sealed record ProxyConfigurationValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<ProxyConfigurationError> Errors { get; init; }
}
```

---

# 17. 配置目录

## Windows

建议：

```text
C:\ProgramData\RelaxKonOS\Proxy\
```

结构：

```text
Proxy\
├── engines\
│   └── mihomo\
│       ├── current\
│       └── versions\
│
├── profiles\
│
├── subscriptions\
│
├── runtime\
│
├── backups\
│
├── logs\
│
└── state\
```

---

# 18. Linux 目录

推荐遵循 Linux 目录语义：

```text
/etc/relaxkonos/proxy/

/var/lib/relaxkonos/proxy/

/var/log/relaxkonos/proxy/

/opt/relaxkonos/proxy/
```

例如：

```text
/etc/relaxkonos/proxy/
    configuration metadata

/var/lib/relaxkonos/proxy/
    profiles
    subscriptions
    runtime state
    backups

/var/log/relaxkonos/proxy/
    logs

/opt/relaxkonos/proxy/engines/
    mihomo binary
```

具体目录应通过已有 RelaxKonOS PlatformPaths abstraction 实现。

不得在业务代码中散布绝对路径。

---

# 19. Engine 抽象

创建：

```csharp
public interface IProxyEngine
{
    string EngineId { get; }

    string DisplayName { get; }

    Task<ProxyEngineCapabilities> GetCapabilitiesAsync(
        CancellationToken cancellationToken);

    Task<ProxyRuntimeStatus> GetStatusAsync(
        CancellationToken cancellationToken);

    Task ValidateConfigurationAsync(
        string configPath,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProxyGroup>> GetGroupsAsync(
        CancellationToken cancellationToken);

    Task SelectProxyAsync(
        string groupName,
        string proxyName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProxyConnection>> GetConnectionsAsync(
        CancellationToken cancellationToken);

    Task CloseConnectionAsync(
        string connectionId,
        CancellationToken cancellationToken);
}
```

生命周期安装不要完全塞进 `IProxyEngine`。

建议单独：

```csharp
IProxyRuntimeManager
```

因为：

```text
Engine API
```

和：

```text
binary lifecycle
```

是两个领域。

---

# 20. Runtime Manager

```csharp
public interface IProxyRuntimeManager
{
    Task<ProxyRuntimeInfo> GetInstalledRuntimeAsync(
        string engineId,
        CancellationToken cancellationToken);

    Task InstallAsync(...);

    Task UpdateAsync(...);

    Task RollbackAsync(...);

    Task UninstallAsync(...);

    Task VerifyIntegrityAsync(...);
}
```

必须支持：

```text
Managed Runtime
External Runtime
```

---

# 21. Managed Runtime

Managed Runtime 表示：

```text
RelaxKonOS download
RelaxKonOS verify
RelaxKonOS install
RelaxKonOS update
RelaxKonOS rollback
RelaxKonOS remove
```

RelaxKonOS 必须保存：

```text
engine
version
architecture
download source
checksum
install time
previous version
```

---

# 22. External Runtime

允许管理员指定：

```text
mihomo
```

已由操作系统安装。

例如 Linux：

```text
/usr/bin/mihomo
```

RelaxKonOS 只进行：

```text
detect
validate
control
```

不得擅自：

```text
overwrite
upgrade
uninstall
```

External Runtime UI：

```text
Runtime

Type
External

Path
/usr/bin/mihomo

Version
1.x.x

Managed updates
Unavailable
```

---

# 23. Runtime 下载安全

禁止：

```text
download latest
execute immediately
```

Managed Runtime 必须：

```text
Download
 ↓
Verify expected artifact
 ↓
Verify checksum
 ↓
Write versioned directory
 ↓
Validate executable
 ↓
Atomic switch current runtime
```

失败：

```text
keep current runtime
```

更新必须支持 rollback。

---

# 24. 不修改 Windows Defender

RelaxKonOS 不得为了 Mihomo：

```text
disable Defender
add global Defender exclusion
disable SmartScreen
```

如果操作系统阻止某个二进制：

RelaxKonOS 应显示诊断信息。

不能偷偷修改安全策略。

---

# 25. Mihomo Controller

Controller 必须默认为 local-only。

建议：

```yaml
external-controller: 127.0.0.1:9090
secret: "<generated>"
```

secret：

```text
Server only
```

不得返回 Client。

REST 调用：

```text
Avalonia
    ↓
RelaxKonOS
    ↓
Mihomo
```

RelaxKonOS Server 提供自己的 DTO。

不得直接：

```text
return mihomo json
```

给 Client。

---

# 26. Controller Secret

Secret：

```text
randomly generated
minimum sufficient entropy
protected on disk
never logged
never returned through normal API
```

例如：

```csharp
ISecretStore
```

管理。

Client UI 永远不需要展示。

---

# 27. TUN 网络安全是最高优先级

远程服务器最大的风险不是代理失败。

而是：

```text
启用 TUN
    ↓
route changed
    ↓
RelaxKonOS connection enters proxy
    ↓
proxy unavailable
    ↓
RelaxKonOS disconnected
    ↓
server unreachable
```

因此必须实现：

```text
Management Traffic Protection
```

作为 TUN 的核心基础设施。

---

# 28. Management Traffic Protection

默认必须保护：

```text
Loopback

RelaxKonOS Server listening endpoints

active RelaxKonOS client source

default gateway

local LAN

configured management network

SSH management path

RDP management path
```

具体哪些流量可安全旁路需要由平台层计算。

不要仅靠 UI 写几个固定 CIDR。

---

# 29. Active Session Protection

Server 应知道当前 RelaxKonOS Session：

```text
RemoteAddress
LocalAddress
LocalPort
Protocol
```

启用 TUN 前生成：

```text
ManagementRouteSnapshot
```

至少保存：

```csharp
public sealed record ManagementRouteSnapshot
{
    public IPAddress ClientAddress { get; init; }

    public IPAddress ServerAddress { get; init; }

    public IPAddress? Gateway { get; init; }

    public string? InterfaceId { get; init; }

    public DateTimeOffset CapturedAt { get; init; }
}
```

TUN activation plan 应验证：

```text
current management route will remain reachable
```

---

# 30. 不可删除的 System Bypass

规则层可以展示：

```text
System Rules
```

例如：

```text
RelaxKonOS Management
DIRECT

Loopback
DIRECT

LAN
DIRECT
```

这些规则必须：

```text
non-editable by default
```

高级管理员可以关闭保护，但必须：

```text
explicit warning
explicit privilege
audit event
```

第一版甚至可以完全不允许关闭。

安全优先。

---

# 31. TUN 启动流程

禁止：

```text
change route
start proxy
```

正确顺序：

```text
Validate profile
 ↓
Validate runtime
 ↓
Check service
 ↓
Check controller availability
 ↓
Resolve outbound interface
 ↓
Capture network snapshot
 ↓
Generate protected TUN config
 ↓
Persist recovery marker
 ↓
Start / Reload Mihomo
 ↓
Wait for controller
 ↓
Wait for TUN
 ↓
Verify proxy outbound
 ↓
Verify RelaxKonOS management connectivity
 ↓
Mark TUN Active
```

---

# 32. Recovery Marker

在改变网络配置之前写：

```text
proxy-network-recovery.json
```

至少包含：

```text
previous runtime state
previous profile
previous route information
previous DNS information
activation timestamp
operation id
```

如果 RelaxKonOS.Server 在启动时检测：

```text
unfinished activation marker
```

必须进入：

```text
Recovery evaluation
```

不得直接忽略。

---

# 33. 自动恢复

以下情况考虑执行自动 rollback：

```text
Mihomo fails to start

Controller unavailable

TUN interface unavailable

default route invalid

health check fails

management route validation fails
```

流程：

```text
Stop proxy
 ↓
Restore previous config
 ↓
Restore network
 ↓
Start previous state
 ↓
Verify
```

---

# 34. Emergency Disable

必须提供独立操作：

```text
Emergency Disable TUN
```

作用：

```text
Disable Mihomo TUN
Restore networking
Restore DNS
Restore safe route
Keep profiles intact
```

该操作不等价于：

```text
Uninstall Mihomo
```

API：

```text
POST /api/proxy/recovery/disable-tun
```

需要：

```text
proxy.recovery.execute
```

权限。

---

# 35. Server 启动自检

RelaxKonOS.Server 启动时检查：

```text
Mihomo runtime running?
Controller reachable?
TUN expected?
TUN exists?
configuration state consistent?
unfinished operation?
recovery marker?
runtime version?
```

产生：

```csharp
ProxyHealthState
```

---

# 36. 状态模型

禁止只使用：

```text
Running
Stopped
```

使用：

```csharp
public enum ProxyRuntimeState
{
    NotInstalled,

    Installing,

    Stopped,

    Starting,

    Running,

    Reloading,

    Stopping,

    Updating,

    Recovering,

    Degraded,

    Failed
}
```

TUN 独立：

```csharp
public enum ProxyTunState
{
    Disabled,

    Enabling,

    Enabled,

    Disabling,

    Recovering,

    Failed
}
```

这样允许：

```text
Mihomo Running
TUN Failed
```

而不是错误地把整个 Engine 标记为 stopped。

---

# 37. Health 状态

定义：

```csharp
public sealed record ProxyHealth
{
    public ProxyRuntimeState RuntimeState { get; init; }

    public ProxyTunState TunState { get; init; }

    public bool ControllerReachable { get; init; }

    public bool NetworkReachable { get; init; }

    public bool ManagementRouteSafe { get; init; }

    public string? ErrorCode { get; init; }
}
```

---

# 38. 错误必须稳定编码

API 不应只返回：

```text
"Failed"
```

例如：

```text
PROXY_RUNTIME_NOT_INSTALLED

PROXY_CONFIG_INVALID

PROXY_SERVICE_START_FAILED

PROXY_CONTROLLER_UNAVAILABLE

PROXY_TUN_CREATE_FAILED

PROXY_TUN_PERMISSION_REQUIRED

PROXY_ROUTE_CONFLICT

PROXY_DNS_CONFIGURATION_FAILED

PROXY_MANAGEMENT_ROUTE_UNSAFE

PROXY_RUNTIME_INTEGRITY_FAILED

PROXY_RECOVERY_REQUIRED
```

Avalonia 根据 ErrorCode 国际化。

Server message 主要用于诊断。

---

# 39. API 设计

建议前缀：

```text
/api/proxy
```

---

# 40. Overview API

```http
GET /api/proxy/status
```

返回：

```json
{
  "engine": "mihomo",
  "runtimeState": "Running",
  "tunState": "Enabled",
  "profile": "Default",
  "mode": "Rule",
  "version": "...",
  "uploadBytesPerSecond": 0,
  "downloadBytesPerSecond": 0,
  "activeConnections": 0
}
```

---

# 41. Runtime API

```text
GET    /api/proxy/runtime

POST   /api/proxy/runtime/install

POST   /api/proxy/runtime/update

POST   /api/proxy/runtime/rollback

DELETE /api/proxy/runtime
```

Runtime mutations：

```text
proxy.runtime.manage
```

---

# 42. Lifecycle API

```text
POST /api/proxy/start

POST /api/proxy/stop

POST /api/proxy/restart
```

---

# 43. TUN API

```text
GET  /api/proxy/tun

POST /api/proxy/tun/enable

POST /api/proxy/tun/disable
```

Enable 请求建议：

```json
{
  "profileId": "...",
  "managementProtection": true
}
```

第一版：

```text
managementProtection
```

应始终强制为 true。

字段只是为未来扩展保留时，也不要允许客户端关闭。

---

# 44. Profiles API

```text
GET    /api/proxy/profiles

GET    /api/proxy/profiles/{id}

POST   /api/proxy/profiles

PUT    /api/proxy/profiles/{id}

DELETE /api/proxy/profiles/{id}

POST   /api/proxy/profiles/{id}/activate

POST   /api/proxy/profiles/{id}/validate
```

---

# 45. Groups / Nodes

```text
GET /api/proxy/groups
```

返回：

```csharp
ProxyGroupDto
{
    Name
    Type
    Selected
    Proxies[]
}
```

切换：

```text
POST /api/proxy/groups/{group}/select
```

Body：

```json
{
  "proxy": "..."
}
```

---

# 46. Connections

```text
GET    /api/proxy/connections

DELETE /api/proxy/connections/{id}

DELETE /api/proxy/connections
```

Avalonia 应支持实时刷新。

如果 RelaxKonOS 已有 WebSocket / streaming infrastructure：

复用。

不要专门建立另一套 socket framework。

---

# 47. Logs

```text
GET /api/proxy/logs
```

实时日志：

优先复用 RelaxKonOS 已存在的：

```text
event stream
websocket
streaming API
```

禁止建立：

```text
ProxyWebSocketManager
```

如果 RelaxKonOS 已有统一 event transport。

---

# 48. 日志脱敏

以下内容禁止进入普通日志：

```text
subscription URL token

Authorization

Mihomo controller secret

proxy password

UUID

private key

WireGuard private key

authentication header
```

定义统一：

```text
ProxyLogSanitizer
```

所有：

```text
Mihomo logs
HTTP logs
audit detail
exception detail
```

通过 sanitizer。

---

# 49. Subscription

Subscription 是敏感数据。

模型：

```csharp
public sealed record ProxySubscription
{
    public Guid Id { get; init; }

    public string Name { get; init; } = "";

    public SecretReference UrlSecret { get; init; }

    public DateTimeOffset? LastUpdated { get; init; }

    public TimeSpan? UpdateInterval { get; init; }

    public bool Enabled { get; init; }
}
```

API 不返回原 URL。

返回：

```text
https://example.com/********
```

或者：

```text
Configured
```

---

# 50. Subscription 更新流程

```text
Download
 ↓
Validate HTTP response
 ↓
Parse / identify config
 ↓
Validate Mihomo configuration
 ↓
Backup current profile
 ↓
Commit
 ↓
Reload
 ↓
Health check
 ↓
Rollback on failure
```

禁止下载后直接覆盖当前配置。

---

# 51. DNS

TUN 模式必须把 DNS 作为独立状态显示。

Avalonia：

```text
DNS

Status
Enabled

Mode
Fake IP / Redir Host / ...

Hijack
Enabled

Listen
...

Cache
...
```

第一版不需要完整实现所有 Mihomo DNS 参数编辑。

采用：

```text
Common Settings
+
Advanced Raw Config
```

---

# 52. Windows 特殊实现

创建：

```text
IProxyPlatformService
```

Windows：

```text
WindowsProxyPlatformService
```

负责：

```text
Windows Service integration

network adapter inspection

route snapshot

DNS snapshot

management route validation

TUN diagnostics

firewall diagnostics
```

不要把：

```text
sc.exe
netsh
PowerShell
```

散落在业务层。

---

# 53. Linux 特殊实现

Linux：

```text
LinuxProxyPlatformService
```

负责：

```text
systemd

ip route

ip rule

nftables / iptables diagnostics

DNS environment

network interface detection

TUN availability
```

Mihomo 当前 `auto-redirect` 仅 Linux 可用，并且依赖 `auto-route`。citeturn938106search3turn938106search4

UI 只有在 Server capability 返回：

```text
SupportsAutoRedirect = true
```

才显示。

---

# 54. Linux TUN 检测

检查：

```text
/dev/net/tun
```

存在性。

必要时验证系统是否允许创建 TUN。

容器环境应返回：

```text
Unsupported
```

或：

```text
PrivilegeMissing
```

而不是简单显示：

```text
Failed
```

---

# 55. Windows TUN 诊断

Server 至少能够区分：

```text
runtime missing

service permission failure

TUN adapter creation failure

firewall interference

route failure
```

Mihomo 官方当前文档对 Windows TUN 防火墙场景也明确要求确保核心程序允许通过防火墙。citeturn938106search3

RelaxKonOS 可以提供诊断提示。

不得未经明确设计自动：

```text
disable firewall
```

---

# 56. Firewall 集成边界

Proxy Manager 第一阶段：

```text
detect
diagnose
recommend
```

而不是：

```text
automatically rewrite firewall
```

未来如需要修改防火墙：

必须调用 RelaxKonOS 已存在/未来的 Firewall Manager。

不能：

```text
Proxy Manager
    ↓
直接写 nftables
```

形成另一套 firewall subsystem。

---

# 57. Route 操作边界

同样：

Proxy Manager 可以拥有：

```text
route safety abstraction
```

但不要演化成第二套 Network Manager。

公共网络能力应逐渐下沉：

```text
RelaxKonOS.Network
```

Proxy Manager 调用它。

---

# 58. Avalonia 页面结构

导航：

```text
Network
├── Interfaces
├── Firewall
├── DNS
└── Proxy
```

Proxy 内：

```text
Overview

Profiles

Proxies

Rules

Connections

DNS

Logs

Settings
```

---

# 59. Overview 页面

建议布局：

```text
┌───────────────────────────────────────────────┐
│ Proxy                                         │
│                                               │
│ ● Running                                     │
│                                               │
│ Engine      Mihomo                            │
│ Version     x.x.x                             │
│ Profile     Default                           │
│ Mode        Rule                              │
│                                               │
│ TUN         Enabled                           │
│ DNS         Healthy                           │
│ Route       Protected                         │
│                                               │
│ ↓ 42.8 MB/s        ↑ 5.2 MB/s                 │
│                                               │
│ Connections         128                       │
│                                               │
│ [Stop] [Restart]                              │
└───────────────────────────────────────────────┘
```

---

# 60. TUN 状态卡片

必须单独展示：

```text
TUN

Status
Enabled

Interface
Mihomo

Stack
Mixed

Auto Route
Enabled

Management Protection
Enabled

Outbound Interface
eth0

[Disable TUN]
```

失败时：

```text
TUN
Failed

Management route could not be protected.

[View Details]
[Emergency Restore]
```

---

# 61. 节点页

```text
Proxy Groups

GLOBAL
  Japan-01            42ms
  Japan-02            65ms
  Singapore-01        88ms

AUTO
  Selected: Japan-01
```

功能：

```text
Select
Latency test
Search
Filter
```

不要第一阶段重复实现 MetaCubeXD 所有高级功能。

---

# 62. Connections 页面

字段：

```text
Host

Source

Destination

Network

Rule

Chain

Upload

Download

Start Time
```

支持：

```text
Search
Filter
Close connection
Close all
```

---

# 63. Profile 页面

列表：

```text
Default
Subscription
Custom
```

编辑仍然使用 RelaxKonOS 已有的：

```text
custom desktop modal
```

不能突然使用系统原生对话框。

---

# 64. 创建 Profile Modal

字段：

```text
Name

Source
○ Local Configuration
○ Subscription

Engine
Mihomo
```

如果 local：

```text
configuration editor
```

如果 subscription：

```text
URL
update interval
```

---

# 65. 删除确认

使用 RelaxKonOS modal infrastructure：

```text
Delete Profile?

Default

This operation does not uninstall Mihomo.

[Cancel]
[Delete]
```

如果删除 active profile：

必须阻止或者要求先切换。

不要静默切换。

---

# 66. Settings 页面

建议：

```text
Runtime

Engine
Mihomo

Runtime type
Managed

Version
...

[Check Update]
[Rollback]
```

然后：

```text
Startup

Start proxy with system
ON

Enable TUN automatically
ON
```

然后：

```text
TUN

Stack
Mixed

Auto Route
ON

Auto Detect Interface
ON

Auto Redirect
ON      Linux only
```

然后：

```text
Safety

Management Traffic Protection
ON
Locked
```

---

# 67. Advanced Configuration

必须允许高级用户查看：

```text
Raw Mihomo YAML
```

但保存前：

```text
validate
backup
commit
reload
health check
```

UI：

```text
Advanced Configuration

[Editor]

[Validate]

[Save & Reload]
```

---

# 68. MVVM 结构

例如：

```text
Features/
└── Proxy/
    ├── Views/
    ├── ViewModels/
    ├── Models/
    ├── Services/
    ├── Repositories/
    └── Navigation/
```

不要：

```text
ProxyPage.axaml.cs
3000 lines
```

ViewModel 也不要变成 god object。

建议：

```text
ProxyOverviewViewModel

ProxyProfilesViewModel

ProxyGroupsViewModel

ProxyConnectionsViewModel

ProxyDnsViewModel

ProxyLogsViewModel

ProxySettingsViewModel
```

---

# 69. Client Repository

```csharp
public interface IProxyRepository
{
    Task<ProxyOverview> GetOverviewAsync(...);

    Task StartAsync(...);

    Task StopAsync(...);

    Task RestartAsync(...);

    Task EnableTunAsync(...);

    Task DisableTunAsync(...);

    Task<IReadOnlyList<ProxyProfile>> GetProfilesAsync(...);

    ...
}
```

HTTP 实现：

```text
RemoteProxyRepository
```

ViewModel 不直接：

```text
HttpClient.GetAsync(...)
```

---

# 70. Localization

不得在代码中增加：

```csharp
"Enable TUN"
"Proxy failed"
```

作为最终 UI 文本。

遵循 RelaxKonOS 现有资源体系。

例如 key：

```text
proxy.title

proxy.status.running

proxy.status.stopped

proxy.tun.title

proxy.tun.enable

proxy.tun.disable

proxy.tun.managementProtection

proxy.error.routeUnsafe

proxy.recovery.emergencyDisable

proxy.runtime.install
```

---

# 71. Theme

所有界面：

```text
use existing RelaxKonOS theme resources
```

禁止 Proxy Manager：

```text
hard-coded color
hard-coded dark theme
hard-coded Clash color scheme
```

控件使用 RelaxKonOS 已有：

```text
Card
Button
Toggle
StatusBadge
Modal
Tab
DataGrid
```

若不存在，再建立通用组件。

不要建立：

```text
ProxyButton
ProxyCard
```

除非组件确实只适用于代理领域。

---

# 72. 审计

以下操作必须产生 Audit Event：

```text
Install runtime

Update runtime

Rollback runtime

Uninstall runtime

Start proxy

Stop proxy

Enable TUN

Disable TUN

Emergency restore

Change active profile

Modify configuration

Update subscription

Change proxy node

Close connections
```

尤其：

```text
Enable TUN
Disable TUN
Recovery
Runtime install/update
```

属于高价值事件。

---

# 73. Audit 信息

记录：

```text
User

Session

Host

Operation

Engine

Profile

Result

Timestamp

CorrelationId
```

禁止记录：

```text
subscription URL token
controller secret
proxy password
```

---

# 74. Operation / Correlation

长任务：

```text
Install

Update

TUN enable

Recovery
```

应使用统一 operation id。

例如：

```text
OperationId
```

Client 可查询进度。

不要通过 HTTP request 一直挂着几十秒。

如果 RelaxKonOS 已经有 Job/Operation infrastructure：

直接复用。

---

# 75. 安装流程

Managed Mihomo：

```text
User Install
 ↓
Check RelaxKonOS permission
 ↓
Resolve platform / architecture
 ↓
Download artifact
 ↓
Verify
 ↓
Elevation request if required
 ↓
Install versioned runtime
 ↓
Install OS service
 ↓
Create config
 ↓
Start without TUN first
 ↓
Controller health check
 ↓
Mark Installed
```

推荐第一次安装时：

```text
TUN OFF
```

安装成功后再执行独立：

```text
Enable TUN
```

这样容易区分：

```text
runtime problem
```

和：

```text
network problem
```

---

# 76. 启用 TUN 流程

```text
EnableTunCommand

1 authorization

2 validate runtime

3 validate profile

4 validate service

5 collect current network state

6 determine outbound interface

7 calculate management protection

8 validate route plan

9 write recovery marker

10 generate managed config overlay

11 apply configuration

12 start/reload Mihomo

13 wait controller

14 detect TUN interface

15 verify route

16 verify DNS

17 verify outbound network

18 verify RelaxKonOS management path

19 clear activation transaction

20 report Enabled
```

任何步骤失败：

进入：

```text
rollback
```

---

# 77. 禁用 TUN 流程

```text
1 authorization

2 write recovery transaction

3 disable TUN

4 reload Mihomo

5 wait TUN disappearance

6 verify OS routes

7 verify DNS

8 verify management route

9 clear transaction
```

---

# 78. Stop Proxy 与 Disable TUN 区别

必须定义清楚。

```text
Disable TUN
```

可以：

```text
keep Mihomo running
```

用于 Listener 模式。

而：

```text
Stop Proxy
```

代表：

```text
stop the managed Mihomo runtime
```

UI 不要混为同一个 toggle。

---

# 79. Startup

服务器重启后：

Linux 由 systemd 负责 Mihomo 启动；Windows 由 Server 的既有启动策略启动
`RelaxKonOS.Server`，再由其进程宿主恢复 Mihomo。

RelaxKonOS Server 启动之后：

```text
discover current state
```

而不是假设：

```text
RelaxKonOS stopped
=> Mihomo stopped
```

这是重要状态恢复原则。

---

# 80. Fail-safe Startup

如果配置显示：

```text
TUN expected enabled
```

但是 RelaxKonOS 检测：

```text
recovery marker exists
```

则优先：

```text
Recover
```

不要直接再次启用。

"TUN expected enabled" 必须由引擎自述（控制器读取的 `tun.enable`）决定，不能由恢复标记推导：标记只表达事务是否完成。未完成的标记一律要求恢复；已完成的标记若与引擎自述矛盾，视为运行时被事务之外的方式重配过，此时先执行完整恢复流程（关闭运行时 TUN 并确认受保护的管理路由），成功后才允许丢弃该标记并重新启用，失败则继续拒绝。

---

# 81. Engine Capability

定义：

```csharp
public sealed record ProxyEngineCapabilities
{
    public bool SupportsTun { get; init; }

    public bool SupportsRules { get; init; }

    public bool SupportsProxyGroups { get; init; }

    public bool SupportsConnections { get; init; }

    public bool SupportsDns { get; init; }

    public bool SupportsSubscriptions { get; init; }

    public bool SupportsReload { get; init; }
}
```

以后 sing-box 接入不允许修改 Avalonia 页面业务判断：

```text
if engine == mihomo
```

应该：

```text
if capability.SupportsConnections
```

---

# 82. 禁止 Engine 泄漏

以下类型禁止进入 Client：

```text
MihomoProxyGroupResponse

ClashConnection

MihomoConfig
```

必须转换：

```text
ProxyGroupDto

ProxyConnectionDto

ProxyProfileDto
```

这样第二 Engine 才不会污染 UI。

---

# 83. 第一阶段范围

必须实现：

```text
Proxy built-in app

Mihomo runtime detection

Managed Mihomo runtime

External Mihomo runtime

Install

Uninstall

Start

Stop

Restart

OS service integration

Mihomo controller integration

Status

TUN enable

TUN disable

TUN safety protection

Profile list

Profile activation

Raw YAML editing

Config validation

Proxy groups

Proxy selection

Connections

Logs

Basic DNS status

Recovery

Audit

Permissions
```

---

# 84. 第一阶段非目标

第一阶段不要实现：

```text
sing-box

Xray

full YAML visual editor

rule provider visual designer

proxy provider visual designer

full firewall management

full routing table editor

full DNS server manager

traffic statistics database

multi-host centralized proxy orchestration

automatic proxy purchase

subscription marketplace

MetaCubeXD clone
```

保持范围。

---

# 85. 第二阶段

以后可以增加：

```text
Subscriptions

automatic subscription refresh

latency testing

rule visualization

per-application integration

RelaxKonOS-only proxy

Git proxy

Docker proxy

APT proxy

system proxy integration
```

---

# 86. 第三阶段

实现：

```text
SingBoxEngine
```

用于验证：

```text
IProxyEngine abstraction
```

是否真正成立。

如果接入 sing-box 时需要大规模修改：

```text
Avalonia ViewModels
RelaxKonOS API
domain model
```

说明第一阶段 Engine abstraction 失败。

---

# 87. 单元测试

至少：

```text
ProxyRuntimeManagerTests

ProxyProfileManagerTests

ProxyConfigurationTransactionTests

ProxyRecoveryManagerTests

ProxyPermissionTests

ProxySecretSanitizerTests

MihomoEngineTests

ProxyRoutingProtectionTests
```

---

# 88. 配置事务测试

覆盖：

```text
valid config

invalid config

write failure

reload failure

controller timeout

health check failure

rollback success

rollback failure
```

---

# 89. TUN 安全测试

必须重点测试：

```text
remote client IP preserved

LAN preserved

gateway preserved

management route conflict detected

invalid outbound interface rejected

Mihomo crash during activation

RelaxKonOS.Server crash during activation

machine reboot during activation
```

---

# 90. Windows Integration Tests

覆盖：

```text
Windows Server

install service

remove service

start

stop

restart

TUN creation

route health

restart OS

runtime update

runtime rollback
```

---

# 91. Ubuntu Integration Tests

覆盖：

```text
systemd

headless Ubuntu

/dev/net/tun

TUN startup

auto-route

auto-redirect capability

restart OS

runtime update

runtime rollback
```

---

# 92. 无 UI 环境测试

Proxy Manager Server 功能不得依赖：

```text
desktop session

DISPLAY

DBus desktop session

Windows interactive user session
```

Ubuntu Server 无 GUI：

```text
must work
```

Windows Server 无 GUI：

```text
must work
```

Client 在另一台机器运行即可。

---

# 93. 安全要求

必须做到：

```text
Mihomo controller not exposed publicly

controller secret not returned to client

subscription secret encrypted/protected

logs sanitized

runtime verified

dangerous operations authorized

privileged operations strongly typed

TUN recovery available

management route protected
```

---

# 94. 禁止设计

Codex 不得实现：

## 禁止 1

```csharp
RunAsRoot(string command)
```

## 禁止 2

```text
Client -> Mihomo controller directly
```

## 禁止 3

```text
Mihomo Controller 0.0.0.0
```

默认暴露。

## 禁止 4

```text
Disable Windows Defender
```

## 禁止 5

```text
Disable firewall
```

## 禁止 6

```text
Enable TUN without recovery state
```

## 禁止 7

```text
Enable TUN without management traffic protection
```

## 禁止 8

```text
Store subscription token in normal log
```

## 禁止 9

```text
Hard-code OS commands throughout business layer
```

## 禁止 10

```text
Avalonia directly knows Mihomo JSON
```

---

# 95. Codex 实现方式

Codex 不得一次性重写 RelaxKonOS。

实施必须：

```text
incremental
reviewable
buildable
testable
```

每一个阶段完成后：

```text
build
test
```

再继续。

---

# 96. Codex 开始前检查

Codex 首先必须检查现有：

```text
solution structure

Server service architecture

authorization system

elevation/helper implementation

platform abstraction

service manager

HTTP API conventions

error response conventions

operation/job framework

audit framework

secret storage

localization

theme system

modal system

navigation

repository/service patterns
```

不得假设不存在。

如果已有：

```text
IServiceManager
```

必须复用。

不要创建：

```text
ProxyServiceManagerForWindows
```

重复已有基础设施。

---

# 97. 实现 Phase 0

仅调查，不进行大规模修改。

输出：

```text
docs/applications/RelaxKonOS.ProxyManager.Discovery.md
```

内容：

```text
Relevant projects

Existing abstractions

Reusable services

Permission system

Elevation path

Service management

Platform layer

UI conventions

Required new components

Potential conflicts
```

完成之后开始 Phase 1。

---

# 98. Phase 1：Domain + Contracts

实现：

```text
Proxy domain models

DTO

capabilities

runtime state

TUN state

error codes

interfaces
```

暂时：

```text
no UI
no Mihomo download
```

要求：

```text
build passes
tests pass
```

---

# 99. Phase 2：Mihomo Adapter

实现：

```text
MihomoEngine

Controller client

status

groups

select proxy

connections

logs

config validation
```

Controller：

```text
local only
```

测试 Adapter。

---

# 100. Phase 3：Runtime

实现：

```text
detect external Mihomo

managed runtime directory

install

service install

start

stop

restart

uninstall
```

Windows/Linux 分平台。

---

# 101. Phase 4：Profile

实现：

```text
profile metadata

active profile

config read

config validate

transaction write

backup

rollback
```

---

# 102. Phase 5：TUN

实现：

```text
network snapshot

management protection

recovery marker

enable

disable

health check

rollback

emergency restore
```

这是整个功能风险最高的 Phase。

不要与 UI 同时实现。

先 Server test。

---

# 103. Phase 6：API

实现：

```text
status

runtime

lifecycle

profiles

groups

connections

tun

recovery
```

全部：

```text
authorization protected
```

---

# 104. Phase 7：Avalonia

加入导航：

```text
Network
    Proxy
```

实现：

```text
Overview

Profiles

Proxies

Connections

Logs

Settings
```

遵循已有：

```text
MVVM

Repository

Theme

Localization

Modal
```

---

# 105. Phase 8：Audit + Security

完成：

```text
audit

secret masking

controller security

permission tests

runtime verification
```

---

# 106. Phase 9：Integration Tests

在：

```text
Windows

Ubuntu
```

执行实际 TUN 测试。

特别验证：

```text
启用 TUN 后 RelaxKonOS 当前连接不会断开
```

这是发布的必要条件。

---

# 107. Phase 10：Documentation

增加：

```text
docs/proxy/

architecture.md

mihomo.md

tun.md

recovery.md

security.md
```

用户文档：

```text
安装

启用

配置

TUN

恢复

故障排查
```

---

# 108. 验收条件

只有同时满足以下条件才视为第一阶段完成。

## Runtime

```text
[ ] Windows 可以安装 Mihomo

[ ] Ubuntu 可以安装 Mihomo

[ ] 可以使用 External Runtime

[ ] 可以 Start

[ ] 可以 Stop

[ ] 可以 Restart

[ ] 可以 Update

[ ] Update 失败不会破坏现有版本
```

## TUN

```text
[ ] Windows TUN 可用

[ ] Ubuntu TUN 可用

[ ] Enable TUN 有权限检查

[ ] Disable TUN 正常

[ ] management protection 生效

[ ] TUN 失败能够 rollback

[ ] 有 Emergency Restore
```

## Remote Management

```text
[ ] 开启 TUN 后 RelaxKonOS 当前连接保持可用

[ ] Client 不直接访问 Mihomo

[ ] Controller 不暴露公网

[ ] Controller secret 不返回 Client
```

## Profiles

```text
[ ] 创建

[ ] 编辑

[ ] Validate

[ ] Activate

[ ] Delete

[ ] Rollback
```

## Proxy

```text
[ ] 查看 groups

[ ] 查看 nodes

[ ] 选择 node

[ ] 查看 connections

[ ] close connection

[ ] 查看 logs
```

## Security

```text
[ ] subscription secret protected

[ ] logs sanitized

[ ] Runtime verified

[ ] privileged API strongly typed

[ ] authorization tests pass
```

## UI

```text
[ ] Avalonia MVVM

[ ] Existing RelaxKonOS Theme

[ ] Existing localization

[ ] Existing modal system

[ ] No Mihomo-specific DTO leaks into UI
```

---

# 109. Definition of Done

Proxy Manager 的第一阶段最终体验应为：

```text
RelaxKonOS
    ↓
Network
    ↓
Proxy
```

管理员可以：

```text
Install Mihomo

Choose profile

Start proxy

Enable TUN

Select proxy node

Inspect connections

Inspect logs

Restart

Disable TUN

Recover network
```

在：

```text
Windows
Ubuntu
```

均可完成。

服务器本身不要求存在 GUI。

---

# 110. 最终架构目标

最终关系必须保持：

```text
RelaxKonOS Avalonia
        │
        │ RelaxKonOS API
        ▼
RelaxKonOS.Server
        │
        ├── Authorization
        ├── Audit
        ├── Proxy Domain
        ├── Runtime Management
        ├── Network Safety
        └── Platform Abstraction
                 │
                 ▼
            IProxyEngine
                 │
                 ▼
              Mihomo
                 │
                 ▼
                TUN
                 │
                 ▼
          Operating System
```

而权限提升关系是：

```text
RelaxKonOS.Server
       │
       │ explicit privileged operation
       ▼
RelaxKonOS privileged subsystem/helper
       │
       ├── install service
       ├── remove service
       ├── protected runtime update
       └── emergency network recovery
```

而不是：

```text
RelaxKonOS.Helper
       │
       └── everything requiring admin forever
```

Helper 应继续保持：

```text
small
auditable
strongly typed
least privilege
```

Proxy Manager 的业务逻辑、Mihomo Controller 操作、Profile 管理、节点选择、Connections 和普通配置逻辑全部留在：

```text
RelaxKonOS.Server
```

而不是 Helper。

---

# 111. Codex 最重要的执行规则

> 不要为了快速实现代理功能破坏 RelaxKonOS 已有架构。

优先：

```text
reuse
extend
abstract
test
```

而不是：

```text
duplicate
special-case
hard-code
```

任何需要：

```text
root
administrator
LocalSystem
```

权限的功能，都必须首先判断：

```text
是否真的需要 privilege？
```

如果不需要：

```text
keep it in RelaxKonOS.Server
```

如果需要：

```text
use existing elevation architecture
```

不得扩大 Helper 权限范围来换取开发便利。

最终设计应允许以后添加：

```text
SingBoxEngine
```

时仍保持：

```text
RelaxKonOS Avalonia
RelaxKonOS API
Proxy domain model
```

基本不变。

---

# 112. Codex 首条执行指令

将本文档放入仓库后，可以向 Codex 下达：

```text
Read this Proxy Manager implementation specification completely.

Then inspect the existing RelaxKonOS repository and its architecture before
making changes.

Pay particular attention to the existing permission/elevation system,
service management abstractions, platform abstractions, Avalonia MVVM
patterns, theme/localization infrastructure, modal infrastructure,
API conventions, audit infrastructure and operation/job infrastructure.

Do not create parallel implementations when equivalent RelaxKonOS
infrastructure already exists.

Start with Phase 0 only.

Create `docs/applications/RelaxKonOS.ProxyManager.Discovery.md` describing how this specification
maps onto the actual repository.

Do not begin the full implementation until the discovery document is
complete.

After discovery, implement the phases incrementally. Keep every phase
buildable and testable.

Mihomo is the first engine, but no UI or domain contract may depend directly
on Mihomo-specific response types.

TUN is a primary feature. Remote management connectivity protection and
network rollback are mandatory requirements, not optional enhancements.

Do not introduce a generic privileged command executor.
```

## 验证与发布边界

### 自动化测试

- **协议/纯函数**：DTO 无秘密序列化、problem code、状态迁移、路径与 Profile 校验、YAML/日志脱敏、版本选择、SHA-256、归档条目与大小限制、平台能力和 management-route plan。
- **Runtime/服务**：替换 HTTP、进程、文件系统、时间和平台适配器，覆盖 External 只读检测、下载失败、归档穿越、错误 RID、服务失败、超时、cancel、active/previous 回滚和 interrupted operation。
- **配置/恢复**：有效/无效配置、临时写入失败、reload/Controller/health timeout、回滚成功/失败、recovery marker、Server crash/reboot 与 Emergency Disable。
- **Endpoint/安全**：认证、read/manage/dangerous policy、Idempotency-Key、operation 状态、审计、secret refusal、敏感日志与 error detail。测试不得连接真实 Controller 或下载生产 Runtime。
- **Client**：repository 路由/认证、本地化动态更新、命令重入/取消、权限禁用和异常状态呈现；不模拟 Controller 协议。

### 手工验证矩阵

当前环境未执行的、需要特权和隔离网络的 平台测试，逐项记录在
[`docs/testing/RelaxKonOS.ProxyManager.SkippedTests.md`](../testing/RelaxKonOS.ProxyManager.SkippedTests.md)。
在该清单全部完成前，Proxy Manager 不得标为 V1 完成。

| 场景 | 需要确认的结果 |
|---|---|
| Windows / Windows Server Managed | 受验证 Runtime、SCM 服务、启动/停止、升级/回滚与无 TUN 首次健康检查均成功；Defender 不被修改。 |
| Ubuntu / Ubuntu Server Managed | systemd、受保护目录、`/dev/net/tun`、服务生命周期、升级/回滚和无 GUI 运行均成功。 |
| External Runtime | 仅检测用户已有 Runtime；显式受管时仅控制 RelaxKonOS 创建的服务/实例。 |
| TUN 成功路径 | 当前 Client、RelaxKonOS 监听端点、网关、LAN、SSH/RDP 仍可达；DNS 和出口状态真实。 |
| TUN 失败与恢复 | 无效接口、Controller 不可用、运行时崩溃、Server crash 和 reboot 后，marker 能驱动安全恢复或显式 recovery-required。 |
| 安全/权限 | 未授权用户无法获取 Controller/订阅凭据、raw YAML 或执行危险操作；日志、审计和诊断无秘密。 |

### 发布级完成定义

Proxy Manager 仅在以下条件全部满足后才可标为 V1 完成：

1. 范围内的功能、安全与平台验收均通过，且 V1 非目标没有被隐式实现或承诺。
2. Mihomo Controller 仅本机访问；Avalonia 从不直连 Controller，Controller secret 和其他凭据不进入 Client/API/日志/审计/异常。
3. Runtime 经过固定清单和 SHA-256 验证；更新不覆盖可用版本，失败可回滚；External Runtime 不被擅自接管。
4. 所有配置写入均验证、备份、原子提交、reload/restart 与健康检查；失败不破坏最后一个可用配置。
5. TUN 有管理流量保护、网络快照、recovery marker、全局锁、自动/紧急恢复，并已在 Windows 与 Ubuntu 上证明不切断 RelaxKonOS 管理连接。
6. Server 逐操作授权、幂等长操作和无秘密审计均落实；AppPermissions 不作为最终授权。
7. 没有通用 command executor、shell 拼接、公开 Controller、Defender/Firewall 绕过或业务层散布 OS 命令。
8. Avalonia 应用采用既有 workspace、MVVM、typed repository、主题、本地化和 modal 模式，且 UI 真实呈现失败、恢复与权限状态。

### mihomo 组件通用接口（2026-10-25）

mihomo 新增通用组件状态/计划适配，复用发布感知领域服务；与 sing-box 共用归档准入与运行时身份核对。直接组件执行保持拒绝，安装、卸载和配置仍由领域发布协调器拥有；尚未接入批量 TaskPlan。实现与验证范围见[组件实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md)。


### 桌面安装入口（2026-10-26）

本应用的宿主组件安装已移至组件中心，应用内改为“在组件中心管理依赖”跳转；保留业务配置与服务管理。各平台安装能力、冻结计划与台账路径见[组件中心实施进度](../platform/RelaxKonOS.ComponentCenter.Progress.md#桌面组件安装入口统一2026-10-26)。此项仅集中入口，不扩大运行时、信任来源、多版本更新或平台验收范围。
