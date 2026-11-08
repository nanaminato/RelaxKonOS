# MB-04 Proxy 系统代理、TUN 与授权到期

系统代理 guard、TUN、sing-box 授权到期及共同停机排空的原始证据。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [13. sing-box 到期调度与执行实例分离（2026-11-03，MB-04 部分）](#13-sing-box-到期调度与执行实例分离2026-11-03mb-04-部分)
- [14. 系统代理 guard 窄执行边界与安全责任审计（2026-11-03，MB-04 部分）](#14-系统代理-guard-窄执行边界与安全责任审计2026-11-03mb-04-部分)
- [15. TUN 运行控制与设置写入执行边界（2026-11-03，MB-04 部分）](#15-tun-运行控制与设置写入执行边界2026-11-03mb-04-部分)
- [51. MB-04 sing-box 授权到期执行边界](#51-mb-04-sing-box-授权到期执行边界)
- [MB-04 sing-box 授权到期的真实网络续核](#mb-04-sing-box-授权到期的真实网络续核)
- [MB-04 sing-box 持久到期索引](#mb-04-sing-box-持久到期索引)
- [MB-04 TUN 私有网络命名空间故障恢复](#mb-04-tun-私有网络命名空间故障恢复)
- [MB-04 TUN 未知 marker 的紧急停用边界](#mb-04-tun-未知-marker-的紧急停用边界)
- [MB-04 真实 Mihomo 控制器与 TUN 恢复](#mb-04-真实-mihomo-控制器与-tun-恢复)
- [MB-04 TUN 路由重试去重与设备清理证明](#mb-04-tun-路由重试去重与设备清理证明)
- [MB-04 真实 Mihomo 自动路由恢复](#mb-04-真实-mihomo-自动路由恢复)
- [MB-04 真实 Mihomo TUN DNS 劫持](#mb-04-真实-mihomo-tun-dns-劫持)
- [MB-04 Linux 系统代理后端跨进程恢复](#mb-04-linux-系统代理后端跨进程恢复)
- [MB-04 Linux 系统代理 guard 与真实文件后端](#mb-04-linux-系统代理-guard-与真实文件后端)
- [MB-04 独立 Linux Helper 的系统代理 IPC](#mb-04-独立-linux-helper-的系统代理-ipc)
- [MB-04 系统代理 IPC 非 root 身份](#mb-04-系统代理-ipc-非-root-身份)
- [MB-04 非 root Core guard 与恢复](#mb-04-非-root-core-guard-与恢复)
- [MB-04 Proxy Server 授权到期回归](#mb-04-proxy-server-授权到期回归)
- [MB-04 到期索引从原台账重建](#mb-04-到期索引从原台账重建)
- [MB-04 sing-box 到期发布跨进程恢复](#mb-04-sing-box-到期发布跨进程恢复)
- [MB-04 完整 Server 接续在途 TUN 激活](#mb-04-完整-server-接续在途-tun-激活)
- [MB-04 TUN 发布准入与恢复标记](#mb-04-tun-发布准入与恢复标记)
- [MB-04 已确认受管发布的真实 TUN 门禁](#mb-04-已确认受管发布的真实-tun-门禁)
- [MB-04 Linux TUN 标记持久化及受管门禁复核](#mb-04-linux-tun-标记持久化及受管门禁复核)
- [MB-04 受限 Mihomo 的 TUN 描述符交付](#mb-04-受限-mihomo-的-tun-描述符交付)
- [MB-04 TUN 原生效果的双中断核对](#mb-04-tun-原生效果的双中断核对)
- [MB-04 Linux 管理端点实际路由与原操作](#mb-04-linux-管理端点实际路由与原操作)
- [MB-04 Helper 原生 TUN 描述符交付](#mb-04-helper-原生-tun-描述符交付)
- [MB-04 Helper TUN 设备和精确路由归属](#mb-04-helper-tun-设备和精确路由归属)
- [MB-04 Helper TUN 原归档操作检查点](#mb-04-helper-tun-原归档操作检查点)
- [MB-04 受管 Mihomo 固定 TUN 单元](#mb-04-受管-mihomo-固定-tun-单元)
- [MB-04 Helper 接管 TUN 交付与具名停止](#mb-04-helper-接管-tun-交付与具名停止)
- [MB-04 TUN 归档事务同键回执](#mb-04-tun-归档事务同键回执)
- [MB-04 冻结 TUN 意图授权](#mb-04-冻结-tun-意图授权)
- [MB-04 生产归档配置适配器的 TUN 恢复](#mb-04-生产归档配置适配器的-tun-恢复)
- [MB-04 Server 生成 TUN 意图及管理 UID 复核](#mb-04-server-生成-tun-意图及管理-uid-复核)
- [MB-04 TUN 策略规则归属及删除中断](#mb-04-tun-策略规则归属及删除中断)
- [MB-04 TUN 分流 DNS 归属](#mb-04-tun-分流-dns-归属)
- [MB-04 TUN DNS 应答中断与 resolver 换代](#mb-04-tun-dns-应答中断与-resolver-换代)
- [MB-04 受管 TUN 宿主范围](#mb-04-受管-tun-宿主范围)
- [MB-04 受管 TUN 提交态归属](#mb-04-受管-tun-提交态归属)
- [MB-04 受管 TUN 提交后具名停止](#mb-04-受管-tun-提交后具名停止)
- [MB-04 受管 TUN 提交后具名启动与重启](#mb-04-受管-tun-提交后具名启动与重启)
- [MB-04 受管 TUN 提交后移除](#mb-04-受管-tun-提交后移除)
- [MB-04 受管 TUN Server 发布协调](#mb-04-受管-tun-server-发布协调)
- [MB-04 受管 TUN AF_UNIX 发布与恢复](#mb-04-受管-tun-af_unix-发布与恢复)
- [MB-04 受管 TUN 认证 HTTP](#mb-04-受管-tun-认证-http)
- [MB-04 受管 TUN 认证 HTTP 补偿](#mb-04-受管-tun-认证-http-补偿)
- [MB-04 受管 TUN 认证 HTTP 具名启停回执](#mb-04-受管-tun-认证-http-具名启停回执)
- [MB-04 完整 Server 的受管 TUN 停机排空](#mb-04-完整-server-的受管-tun-停机排空)
- [MB-04 受管 TUN 与 Proxy 安全责任同次停机](#mb-04-受管-tun-与-proxy-安全责任同次停机)
- [MB-04 受管 TUN 模块停用门禁](#mb-04-受管-tun-模块停用门禁)
- [MB-04 禁用 Proxy 管理入口后的安全交接](#mb-04-禁用-proxy-管理入口后的安全交接)
- [MB-04 Core 接续运行期 TUN 恢复](#mb-04-core-接续运行期-tun-恢复)
- [MB-04 实际 TUN 激活期间取消与同进程补偿](#mb-04-实际-tun-激活期间取消与同进程补偿)
- [MB-04 入站授权到期的停机排空](#mb-04-入站授权到期的停机排空)
- [MB-04 Core TUN 恢复的停机排空](#mb-04-core-tun-恢复的停机排空)
- [MB-04 Core 系统代理 guard 的停机排空](#mb-04-core-系统代理-guard-的停机排空)
- [MB-04 完整 Server 的系统代理 guard 停机排空](#mb-04-完整-server-的系统代理-guard-停机排空)
- [MB-04 完整 Server 的真实 TUN 停机排空](#mb-04-完整-server-的真实-tun-停机排空)
- [MB-04 完整 Server 的入站授权到期停机排空](#mb-04-完整-server-的入站授权到期停机排空)
- [MB-04 同次停机的 Proxy 安全责任与恢复](#mb-04-同次停机的-proxy-安全责任与恢复)
- [MB-04 Linux 系统代理原生回执](#mb-04-linux-系统代理原生回执)
- [MB-04 Linux 直连宿主策略变更恢复](#mb-04-linux-直连宿主策略变更恢复)
- [MB-04 Linux 宿主代理切换验收矩阵](#mb-04-linux-宿主代理切换验收矩阵)
- [MB-04 系统代理 guard 监听切换](#mb-04-系统代理-guard-监听切换)

## 13. sing-box 到期调度与执行实例分离（2026-11-03，MB-04 部分）

`ProxyServerExpiryService` 不再在构造时持有 `ProxyServerService`，只持有作用域工厂与日志。启动立即检查、此后每 30 秒检查；每轮在独立异步作用域解析 `IProxyServerExpiryExecutor`，等待完成并释放作用域后才等待下一轮。作用域局部变量位于独立异步方法，不留在长期计时器状态机上。领域失败记录后继续下一轮；停机同时覆盖正在执行和等待计时器两种取消路径。

`ProxyServerService` 与到期执行接口改为 transient：HTTP 请求或已排队的领域操作持有自己的实例直至完成，后台计时器不再给管理实例增加单例根引用。`ProxyServerStore` 仍是 Core 单例，所有实例共用原状态锁；到期继续读取受保护材料、仅剔除已发布授权中的失效凭据，不发布未应用草稿，保留原发布/对账/补偿实现。没有新增第二个到期循环，也没有转移 Helper 权限或改变运行时安装生命周期。

这只是对象持有边界：引擎注册表等依赖仍有独立常驻引用，Proxy 仍为 resident；调度器仍由 `ProxyModule` 注册，尚无跨禁用的持久调度索引、租约交接或 collectible Proxy 实现。不能据此宣称程序集卸载或生产 RSS 降低，MB-04 继续开放。

Windows / Linux 验证使用生产 30 秒周期，覆盖懒构造、失败后释放、下一轮新实例、成功后释放、等待期间弱引用回收、执行中取消并等待异步释放；同时运行原 Proxy Server 配置、加密、凭据、订阅、发布、回滚、到期、导出与幂等回归。Windows 默认注册专项由 8 增至 **10** 项，新增非单例执行器与单一到期调度器检查。证据保存在 `.artifacts/proxy-expiry-boundary-20261103/` 的 `windows-final.log`、`linux-final.log` 和 `registration-final.log`，本轮未部署生产。

一次使用错误参数的 Windows 注册运行实际进入 ManagedRuntimes 全套，并在 `LifecycleChecks` 的伪造制品布局授权断言失败（`registration-windows.log`）；未诊断或修复此全套失败，不将它作为通过证据。改用当前无前导横线参数后，独立注册专项 10 项通过；本节只宣称上述专项及 Proxy Server 两平台回归通过。

续核（2026-11-03）：确认上述实现、测试与日志均已落地，保留全部未提交实现。补强执行中取消测试：执行器的 `DisposeAsync` 在测试闸门上真正异步等待，断言 `StopAsync` 在闸门释放前不能完成，释放后才确认作用域销毁和停机完成。当前 Windows Release Proxy Server 全回归及注册专项 10 项再次通过，记录为 `windows-disposal-recheck-final.log`、`registration-recheck.log`；新增闸门检查尚未重跑 Linux，前述 Linux 记录对应补强前测试。默认并行构建无诊断失败，改用 `--no-restore -m:1` 构建成功；沙箱内回归在临时状态文件原子替换时被拒绝，批准沙箱外运行后通过，失败记录 `windows-disposal-recheck.log` 不作成功证据。

下一切片先审计系统代理 guard、TUN 恢复与发布补偿的根引用、共享锁和持久责任，再确定窄执行端口；在持久调度交接及故障验收完成前继续保留现有 worker。此次不扩大为 Proxy 休眠、不移交安全责任，也不关闭 MB-04。

## 14. 系统代理 guard 窄执行边界与安全责任审计（2026-11-03，MB-04 部分）

### 根引用、锁与持久责任

本表记录 guard 切片完成时的引用图；TUN 与 writer 的后续分离见 §15。

| 职责 | 根引用及当前锁 | 持久责任与本轮处理 |
| --- | --- | --- |
| 系统代理 guard | 根 HostedService → reader + `IProxySystemProxyReconciler`；原接口映射到设置单例，从而带入控制器客户端和配置发布图 | 拆为 Core 单例 `MihomoSystemProxyReconciler`，仍取得唯一 `MihomoPublicationStore.Gate` 后读取设置及执行。启动失败继续保留 startup 标志，原间隔与重试规则不变 |
| TUN 启停及启动恢复 | `ProxyRecoveryHostedService`、生命周期 recovery 接口及管理调用 → 同一 `ProxyTunSafetyService` → `IProxyTunRuntimeController` → 设置单例；安全服务私有 `_gate`，随后取得发布 gate | `proxy-tun-recovery.json` 先于网络变更落盘，区分未完成转换与已激活会话；先停引擎 TUN 再恢复/核实管理路径，失败保留 marker。未移动、改锁或停止此责任，仍会构造管理图 |
| 配置、安装、移除发布补偿 | Core `MihomoPublicationRecoveryService` 长期持有三个发布协调器；各协调器共用 publication gate，授权回调在持锁期间不重入 | `mihomo-publication.protected` 保存已确认身份、Pending 冻结计划、设置/档案意图与完成回执；按原 operationId 观察 Helper、确认或补偿。busy/helper 不可用继续原操作重试，不能重新准备或以当前健康推导成功。此次不改注册时机、执行器或恢复循环 |
| OS 系统代理 | Core `HostSystemProxyService` → 既有特权传输；Linux Helper 使用环境 provider 的同一命名 mutex | Linux `/var/lib/relaxkonos-system-proxy/recovery.json` 与所有权快照仍由 Helper 事务拥有；Windows 仍为交互用户 HKCU 原语，不宣称具备同等持久 journal。无新增 OS 权限或真实系统代理写入验收 |

TUN 的安全锁不能简单改成每请求对象锁；发布 gate 也不能按新执行实例复制。恢复 worker 根引用不是全部可删除的缓存：在持久责任、执行租约和禁用交接得到验证前必须保留。当前锁顺序和责任单一性保持原实现，本次不宣称全部跨域锁竞态已验收。

### 已实现的窄边界

系统代理对账从 `MihomoSettingsService` 直接迁至 `MihomoSystemProxyReconciler`，旧类不再实现 reconciler 或保留转发方法。writer 与 TUN 控制仍是同一设置单例；guard 不再引用该单例、直接控制器客户端或配置发布执行器。所有调用方与注册测试同步使用当前接口，没有兼容别名。

已发布状态的检查提取为无缓存的 `MihomoManagedPublicationVerification.VerifyAsync`，供 writer 与 guard 在原发布 gate 内复用：只读取现有控制器秘密，核对 active YAML 身份，查询真实 Helper 的受管归属、活动修订、pending/recovery 和配置身份。撤销不生成凭据；不明或漂移时返回原 recovery-required，不能写 OS。guard 仍依赖运行时只读观察、授权和传输图，并未使整个 Proxy 脱离根 DI。

### 验证与范围

新增注册构造陷阱：替换设置管理单例和控制器客户端为抛异常工厂后，生产 guard 与 reconciler 仍能构造。系统代理夹具用独立 reconciler 与 writer 验证共锁、禁用后不复活、startup 清理、pending 拒绝，并增加等待锁取消及后续可用检查。发布夹具增加凭据撤销、YAML 漂移、设置损坏、pending、Helper recovery 与外部所有权六种 guard 拒绝场景，各自核对无宿主写入、无原生配置执行且锁可再获取。

Linux 在用户指定的 `192.168.1.9` 测试机独立目录 `/home/nanami/component-catalog-20261102/phase23/proxy-guard-boundary-20261103/expiry/` 已补跑 §13 新增的真实异步释放闸门检查与完整 Proxy Server 回归，通过；首次旧地址超时和 SSH 历史密钥不匹配均未作为成功。用户确认密钥变更后固定核对观察到的测试机密钥，未修改本机 known_hosts。产物和日志仅用于测试，没有替换运行中的 Server/Helper。

Windows/Linux 默认注册各 **11 项**、配置发布事务分别 **96/97 项**、系统代理共锁/取消与 TUN 专项均通过。Release Server.Tests 构建及 Linux framework-dependent 发布通过；runtimes Linux 发布首次缺少 RID 还原目标，完成正常 restore 后成功。证据统一保存于 `.artifacts/proxy-guard-boundary-20261103/`：`expiry-linux-final.log`、`system-proxy-{windows-final,linux}.log`、`registration-{windows-final,linux}.log`、`publication-{windows-final,linux}.log`、`tun-{windows,linux}.log` 以及构建/发布日志；复跑命令见同目录 `verification.txt`。测试使用隔离状态、伪造 Helper/系统代理后端和受控网络，不证明真实 OS 代理、TUN 或安装故障的端到端验收。

Proxy 继续 **resident**，MB-04/RES-05 保持开放。下一边界需先为 TUN 运行控制与配置写入明确当前意图核验端口，再验证完整启停/恢复/补偿和锁竞争；不能先把安全服务改为 transient 或移除恢复 worker。Core 持久调度、跨禁用安全责任交接、collectible Proxy 与生产内存测量仍未实现。

## 15. TUN 运行控制与设置写入执行边界（2026-11-03，MB-04 部分）

### 本轮实现

`MihomoSettingsService` 只实现 `IProxySettingsWriter`，不再实现或转发 `IProxyTunRuntimeController`。TUN 的设置读取、实时配置改写、路由排除、controller 可达性/重载、回滚及 live flag 观察直接迁到 `MihomoTunRuntimeController`。没有保留旧方法或双入口，公开 HTTP/配置契约不变。

Core 保留单例 TUN 控制器，依赖 paths、reader、controller、publication store 与设备观察端口；它不依赖 settings writer、配置发布执行器、系统代理 writer 或安装器。原 Windows 适配器确认移到 `IProxyTunDeviceObserver` / `HostProxyTunDeviceObserver`：同名活动网卡、10 秒等待与取消规则保持原实现，Linux 仍无该 Windows 专项等待；测试注入受控观察器，不创建真实 TUN。TUN 重载成功不等于 Windows 适配器已激活的门禁仍然存在。

设置 writer 的具体类与接口改为 transient。当前内仓产品调用者只有设置 PUT 入口；每次调用持有自己的 writer，完成后根 DI 不再保留该管理实例。writer 本身没有计时器、私有锁或需迁移的实例缓存，controller/运行时/发布依赖仍按原所有者驻留，不宣称这一步释放这些依赖或已测得堆/RSS 收益。

### 锁、恢复与补偿

`ProxyTunSafetyService`、`IProxyRecoveryService` 映射及启动恢复 worker 保持原单例/注册，不把安全锁改为每调用私有锁。顺序仍为 safety gate → publication gate；writer、guard、TUN 控制与发布事务共用同一个 `MihomoPublicationStore.Gate`。writer 不在持有 publication gate 时反向获取 safety gate。新 DI 构造陷阱验证 TUN startup recovery 在 settings writer 构造即抛异常时仍可解析。

TUN marker 仍在接触引擎前落盘。reload、设备确认或管理路由核验失败继续由原安全服务驱动停 TUN 与管理路径恢复；恢复失败保留同一 marker。reload 中取消可能已替换 YAML，不推断已经回滚；marker 保留，随后启动恢复重新取得两把锁完成 teardown。controller 原先不可达时不发 reload，恢复未核实不清 marker。设置写入继续保留 live TUN flag 与精确 exclusions，不把普通设置更新当成 TUN 转换。

**没有新增受管发布下的 TUN 支持**：存在 confirmed publication 或 pending 时，直接 TUN 启用和停用仍走原 `UncoordinatedWriteProblemAsync` 拒绝，不能绕过授权、发布或网络事务门禁。若因此不能完成安全恢复，仍保留恢复阻止与 marker，不能伪报已清理。Core 三个发布协调器及恢复 worker、冻结计划、Helper 回执、settings/profile 补偿意图均未迁移或削减。

### 验证与未完成边界

新增 `ProxyTunBoundaryChecks`，接入 `--proxy-tun-boundary-only`、`--proxy-tun-only` 与默认 Server.Tests 入口。24 项覆盖真实 TUN 执行器与安全服务组合：启停、marker-before-engine、路由排除、writer 保留 live TUN、reload/设备/管理路径失败补偿、不可达恢复、未知观察、pending/confirmed 拒绝、平台补偿失败重试、reload 中取消及恢复、writer/TUN/安全停机竞争和等待锁取消。这里的“真实执行器”指生产 C# 实现；controller、设备与网络平台是受控夹具，不代表实机 TUN 或 Windows Wintun 验收。

Windows/Linux Release 新边界各 **24 项**、完整 TUN 与系统代理专项、默认注册各 **13 项**、配置发布事务分别 **96/97 项**通过。Server.Tests/ManagedRuntimes.Tests 构建和 framework-dependent Linux 发布成功；存在既有 nullable/未使用参数/平台分析警告，Helper 构建另有无法取得 NuGet 漏洞数据的 NU1900 警告，不宣称完成漏洞检查。Linux 在已确认测试机的 `/home/nanami/component-catalog-20261102/phase23/proxy-tun-boundary-20261103/` 独立目录执行。证据目录 `.artifacts/proxy-tun-boundary-20261103/`：`tun-windows-final.log`、`tun-linux.log`、`system-proxy-{windows,linux}.log`、`registration-{windows,linux}.log`、`publication-{windows,linux}.log` 以及构建/发布日志；复跑命令见 `verification.txt`，源码与 Linux 产物摘要见 `provenance.json`。本轮保留全部未提交实现，未提交、推送或部署，也未更改生产配置/组件数据。

最终结果：完整 solution Release 构建通过（0 错误、2 个既有 Server 告警）。Windows 模块默认全量及最终 ModuleHost 定向检查通过；Linux 模块默认全量在已补齐 Git 的隔离镜像通过，包含真实子进程和异架构包拒绝。最小 ASP.NET 镜像首轮缺少 Git 的失败保留，不作为产品回归失败或通过证据。新增工作租约/独立代码专项 Windows/Linux 各 **23** 项；原 Core 安全所有权各 **17**、lifecycle/TUN scope 各 **31**、责任 **45/46**、订阅各 **37**、TUN 执行各 **30**、settings **31/32**、Mihomo 注册各 **15**、配置发布 **107/108**、安装发布各 **88**、移除发布各 **92**、profile 各 **19**、runtime HTTP 各 **46** 项通过，Squid、出站代理、入站原回归与到期/HTTP 队列专项均通过。本轮不重复原生归档/固定系统服务验收；保留 §19 已有证据的原范围。

Proxy 仍为 **resident**，MB-04/RES-05 保持开放。配置档案事务、引擎注册表、controller、TUN 安全 owner 与 Core 发布恢复仍有独立根引用；还没有按需执行租约、持久调度和跨禁用安全责任交接，也没有 collectible Proxy 或生产内存基线。下个切片须先审计发布协调器与 profile 执行图的持久意图和请求/后台所有权，不能因为 writer 已 transient 就移除补偿 worker 或给整个 Proxy 添加可释放标记。

## 51. MB-04 sing-box 授权到期执行边界

Core 唯一到期 worker 先在原加密领域台账和原 gate 下读取最早到期时间、失效凭据及 pending publication 信号。扫描只向调度循环返回截止时间和待恢复标志，未到期时不构造发布执行器。已到期、已禁用或已经替换的发布凭据，以及 pending publication，仍进入原单一发布执行器。执行器重新读取领域状态并按原规则核对 Helper。共享服务及原事务恢复语义不变。

这是原持久台账的调度投影，仍在每轮反序列化台账，完整持久调度索引、运行时安全责任交接、真实网络故障和动态休眠继续开放。未停用既有 worker，未改变 30 秒扫描节奏，也未测量生产内存。

Windows 与既有 Linux 测试机的 Release Proxy Core 安全 25 项及关联驻留和作用域专项通过。验证了截止边界、空闲不构造执行器、待确认发布和禁用凭据仍需要执行，以及受控 Helper 回执丢失后只有一次发布。日志见 `.artifacts/mb04-expiry-win.log`、`.artifacts/mb04-expiry-linux.log`。同机只读检查发现 `/usr/bin/sing-box` 和 `/dev/net/tun`，未启动实例或变更网络。

### MB-04 sing-box 授权到期的真实网络续核

既有 Linux 测试机用普通账户运行已安装的 `/usr/bin/sing-box`。专属实例和源站仅监听随机回环端口，TLS 验证核对专属证书指纹和主机名。真实 sing-box 使用生产渲染的 VLESS/TLS 入站和用户配置。夹具仅替换自身路由，放行一个回环源端口并拒绝其他目的地址。共享配置、服务和网络保持原状。

原凭据通过实际 VLESS/TLS 转发。写入未来到期时间后，等系统时钟达到该截止点，再调用生产到期扫描与原发布执行器。专属 Helper 传输完成真实 sing-box 配置切换后注入回执丢失。通过真实连接确认过期 UUID 无法到达源站，未到期 UUID 仍可双向传输。重开原加密台账后，发布执行器核对已应用材料，清除 PendingPublication，实际应用调用总计一次。

本项验证真实网络授权行为及生产台账的回执丢失恢复。运行时应用和状态证明由夹具 Helper 传输提供，正式 Helper、系统代理/TUN、既有连接撤销及动态休眠仍开放。

Linux x64 Release 日志见 `.artifacts/mb04-expiry-live-final.log`，制品归档 SHA-256 为 `799158891686780343c27b08dc2c085e917db963f0445588fb5d1b7629ce6d8e`。专属进程退出后临时目录已清理。

既有连接续核通过。到期前分别为即将到期和无到期时间的凭据建立真实 VLESS/TLS 双向流，并保持客户端和回环源站两端打开。专属实例切换后，两条旧流的客户端和源站均实际观察到 EOF 或断开。新连接中，过期 UUID 不能到达源站，未到期 UUID 能重新转发。故障回执恢复后仍只应用一次。此结果量化了夹具停旧启新的连接影响，正式 Helper/systemd 的运行时切换仍待验收。日志见 `.artifacts/mb04-streams-live.log`，归档 SHA-256 为 `9080c25e5cab568d85d2e4e48e1588861e90014f13ff7fd03722aa9d06f0b0ad`。

### MB-04 sing-box 持久到期索引

原 ProxyServerStore 在写入加密领域台账时同步生成独立受保护的到期索引。索引只包含原密文的 SHA-256、最早到期时间及待确认发布标志，不含凭据。两份文件分别原子替换，索引先写。启动或扫描只信任能通过数据保护校验、且摘要与所打开原台账一致的索引。索引失配、损坏、缺失或写入失败时，原台账继续提供到期和恢复责任。领域台账格式不变。

匹配的扫描按流计算密文摘要，跳过完整台账解密和对象反序列化。单一 Core worker、原 gate、原发布执行器和 30 秒扫描节奏继续承担授权及未知结果恢复。本项只交付 sing-box 到期索引，尚未将 Proxy 安全职责整体交接到持久调度，也未验证动态休眠或生产内存收益。

Windows 与既有 Linux 测试机的 Release Core Proxy 安全各 31 项、关联驻留与作用域专项通过。确认匹配索引不解密原状态，旧索引、损坏、缺失和写入失败均不遗漏待恢复或已禁用凭据。Linux 专属 sing-box 实机回归再次通过，原应用调用一次。日志见 `.artifacts/mb04-index-win.log`、`.artifacts/mb04-index-linux.log`、`.artifacts/mb04-index-live.log`。同机网络命名空间预检确认普通 user namespace 映射被拒绝，既有提权路径可创建进程私有网络命名空间。尚未在其中创建 TUN 或注入故障。

### MB-04 TUN 私有网络命名空间故障恢复

既有 Linux 测试机使用提权后的进程私有网络命名空间，启动前核对其与宿主命名空间不同且无既有接口。生产 ProxyTunSafetyService 与 HostProxyNetworkSafetyPlatform 观察真实 veth 默认网关。专属 runtime 夹具在原 marker 持久化后创建真实 TUN，再停在进程终止屏障。强制终止该工作进程后，原 TUN 和未完成 marker 仍存在。

移除私有默认路由后恢复原操作 `b99267b4-e215-49ac-9086-e6abb6d5e3c2`，实际删除 TUN 一次，但因原网关不可核实而保留 marker，新启用被拒绝。恢复默认路由后复查原操作并清除 marker，未再删除设备。新代启用后，重试原恢复引用不会移除新 TUN。专属接口和目录已清理，宿主路由与共享服务未修改。日志见 `.artifacts/mb04-tun-live.log`，Linux Release 制品 SHA-256 为 `21a96189d17ca64b3c8608a50911be7f047483b55997c7f7f20a13bd5a74ae64`。

本项验证生产 marker 和真实内核设备、路由状态。设备切换由夹具 runtime 提供，Mihomo 控制器、DNS、系统代理和动态休眠联合故障仍待验收。

### MB-04 TUN 未知 marker 的紧急停用边界

自动恢复和紧急停用均拒绝无法核实的 marker。损坏内容或缺少可信管理网关时，原文件保留，Mihomo 切换和路由恢复均不调用。新 marker 在原子替换前刷新到磁盘，停机取消保留先前已提交的证据。

Windows Release TUN 专项通过，覆盖损坏 JSON 和不可核实网关在自动恢复及紧急停用入口的拒绝。同机只读确认正式 Mihomo 1.19.33 二进制存在且共享服务 active，后续可使用独立配置和控制端口验证实际 Mihomo 切换，无需修改共享服务。

Linux Release 私有命名空间的真实 TUN 崩溃恢复复测通过，原操作为 `f3fcb878-dbe5-4eed-adb1-48a6e9bc1816`。Windows 与 Linux 日志见 `.artifacts/mb04-tun-marker-win.log`、`.artifacts/mb04-tun-marker-live.log`。

### MB-04 真实 Mihomo 控制器与 TUN 恢复

既有 Linux 测试机在全新私有网络命名空间复用已安装的 Mihomo 1.19.33 二进制。专属配置、控制端口和目录均独立。生产 MihomoTunRuntimeController、MihomoControllerClient、ProxyTunSafetyService 和路由验证平台共同执行。真实重载创建内核 TUN 后终止管理工作进程，保留未完成 marker。默认路由不可核实时，原恢复删除真实设备但保留 marker；恢复私有默认路由后清除原 marker。迟到的原恢复引用不会移除新会话。专属 Mihomo 在整个管理崩溃及恢复期间保持可达，最后随夹具清理退出。

原操作为 `380b5c87-bf26-45b2-ad4a-7d1024f3453a`，日志见 `.artifacts/mb04-mihomo-tun-live.log`，制品归档 SHA-256 为 `a95640fef4bc7bb30800a6868dab36eb22e5d90abdb2cdf101b2e2321b70968b`。本轮核对实际设备只删除一次，控制器重载次数尚未核对。夹具关闭自动路由，真实自动路由、DNS、系统代理、动态休眠及生产内存收益仍开放。共享 Mihomo、宿主网络和安装状态持续运行。

### MB-04 TUN 路由重试去重与设备清理证明

恢复发现磁盘配置已关闭 TUN 时，先查询真实控制器。控制器也确认关闭且原设备已不活动时，只继续管理路由核对，无需再写配置或发重载。控制器不可观测或设备仍活动时保留 marker 和恢复责任。首次关闭重载确认后同样等待设备清理证据。Linux 要求设备消失，Windows 接受设备消失或明确停止状态。

私有命名空间实机夹具记录生产控制器的 PUT 次数，将首次关闭和仅恢复管理路由的后续重试分开计数。自动路由、DNS、系统代理和动态休眠仍待联合验收。

Windows Release TUN 边界 32 项及关联专项通过。真实 Linux Mihomo 复测操作 `9c7336bf-4f89-4741-95e6-ff1d532e8156`，首次恢复 PUT 为一次，恢复原网关后的重试没有新增 PUT。无效控制器响应保持未知。日志见 `.artifacts/mb04-tun-reload-win-final.log` 与 `.artifacts/mb04-tun-reload-live-final.log`。

### MB-04 真实 Mihomo 自动路由恢复

同机 Linux 私有网络命名空间开启 Mihomo 自动路由。配置应用后实际内核路由查询对保留测试地址选择 TUN，对管理网关仍选择原 veth。故障前确认策略规则偏离基线，随后终止管理工作进程。删除私有默认路由后原恢复保留 marker，恢复原网关后同一操作完成。IPv4/IPv6 策略规则与初始快照相同，测试地址再次通过原默认网关。原关闭重载仍为一次。

原 operationId 为 `86899a7b-b6d0-425d-869e-5f40da657a40`，日志见 `.artifacts/mb04-auto-route-live.log`，归档 SHA-256 为 `ab4961e556d7654ffaec89eee67b67abe74c5db3b7440ea63d66b0f74e81aa19`。本轮验证真实内核选路和规则清理；没有向外网发送流量，DNS 和系统代理行为另行验收。

### MB-04 真实 Mihomo TUN DNS 劫持

既有 Linux 测试机在私有网络命名空间以实际 UDP 查询向保留地址 `198.51.100.53:53` 发出 DNS 请求。内核选路指向 TUN，Mihomo 对夹具记录 `mb04.test` 返回预设 A 记录 `203.0.113.42`。检查核对事务 ID、成功状态和应答地址。管理进程崩溃恢复后，策略规则回到基线，同一 DNS 目的地址回到原 veth 默认路由，原恢复仍只发送一次关闭重载。使用独立配置及无外网出口的命名空间，宿主解析器和共享 Mihomo 持续运行。

原 operationId 为 `505d8b43-bdb3-427a-bb52-65a2a527e7c7`，日志见 `.artifacts/mb04-tun-dns-live.log`，Linux Release 制品 SHA-256 为 `c39749a1db2f4069208db7f0836f281f62fe7b7ad41fc2b3fabf05f187907286`。本轮恢复后验证内核选路及规则清理，未向原上游发查询。系统代理和动态休眠仍待联合验收。

### MB-04 Linux 系统代理后端跨进程恢复

既有 Linux 测试机在私有挂载及网络命名空间使用生产 LinuxSystemProxyOperations、原事务和环境文件后端。独立挂载的 `/etc` 和 `/var/lib` 提供专属环境文件和恢复台账，独立 `/run` 隔离宿主桌面会话。测试在原进程确认写入代理键且恢复台账存在后强制终止它。在恢复前，用生产环境接口写入一个竞争代理值和无关键。恢复只撤销原操作仍拥有的值，保留竞争编辑；重复停用后环境文件字节一致，台账已清理。

Windows Release 事务专项及 Linux 实机均通过。实机前后核对宿主环境文件和恢复台账摘要一致。日志见 `.artifacts/mb04-system-proxy-live-final.log`，制品归档 SHA-256 为 `8d5413332ae0441ece4279fd092b7958571a1f66e209952a48eab0b33b4c5f03`。本轮使用真实文件后端，未连接共享 Helper 服务或桌面会话；guard 与动态休眠的联合验收仍开放。

### MB-04 Linux 系统代理 guard 与真实文件后端

生产 Core guard、MihomoSystemProxyReconciler、HostSystemProxyService 和 Linux Helper 文件后端在私有挂载中联合执行。测试未加载 Proxy 管理门面。启用 guard 后，已确认且未变更的环境键不再改写环境或所有权台账。仅对环境文件优化，桌面会话通知路径仍保持原行为。竞争编辑触发 guard 回到配置值；停止 guard 后，启动恢复只还原仍由原台账拥有的键，保留更新的竞争值。

系统代理文件后端和 Core guard 使用生产实现。Helper 传输由同进程专属适配器转接，正式独立 Helper、桌面会话和动态休眠联合验收仍开放。

Windows Release 事务专项通过，Linux 实机核对宿主环境文件和台账摘要前后一致。日志见 `.artifacts/mb04-guard-final-win.log` 与 `.artifacts/mb04-guard-final-live.log`。Linux 制品归档 SHA-256 为 `34e49e11f7f8c85c8319aafe857f92451811d174900a333ca5f3357fbd1c9664`。

### MB-04 独立 Linux Helper 的系统代理 IPC

既有 Linux 测试机在私有挂载和网络命名空间启动当前 Release 的真实 Linux Helper 服务入口。专属 root-owned 配置、独立 socket 及授权 UID 0 仅用于测试。生产 UnixSocketPrivilegedOperationTransport 经实际握手和帧传输执行原启动、崩溃恢复、周期 guard 与停止路径。记录七次成功的 LinuxSystemProxyApply 请求。原操作进程异常退出后恢复只撤销仍拥有的键，重复恢复无改写，竞争写入保留，稳定 guard 扫描不重写台账。

专属 Helper 收到 TERM 后正常退出且 socket 移除。宿主环境和恢复台账前后摘要一致。日志见 `.artifacts/mb04-guard-ipc-live.log`，归档 SHA-256 为 `0e57085a42b85a484ea2c6678c9ae514ed13e09c3e1abf69cb38e9711b87070e`。本轮没有测试正式非 root Server 身份、共享 systemd 服务重启、桌面会话或动态休眠。

### MB-04 系统代理 IPC 非 root 身份

同机私有命名空间复用现有 Server UID 999 及 GID 983。专属 Helper 仅授权该 UID 和恢复观察者 UID 0。未授权 UID 65534 使用同一 socket 组，Helper 在 peer 校验中拒绝，环境文件和台账无变化。已授权 Server UID 通过实际 socket 写入系统代理，Helper 回执操作 ID 为 `276c7916-634f-48a4-95ac-ea8a724cba42`。强制终止操作进程后，专属 root 观察者走同一 socket 完成原台账恢复和 guard 检查。

首轮拒绝事件正确，但客户端收到 socket 提前关闭映射的 HelperUnavailable。最终夹具以 Helper 拒绝日志和无副作用联合判定，仍校验非 root 成功事件。专属 Helper 正常退出，宿主环境和台账摘要一致。日志见 `.artifacts/mb04-guard-peer-final.log`，归档 SHA-256 为 `af8e8054e237c3946bff93a13691baaed06ecb3e8645be7c2e07b9652ae018e7`。正式 systemd 服务、非 root 常驻 guard 与桌面会话未纳入本轮。

### MB-04 非 root Core guard 与恢复

在同一隔离 Linux Helper 夹具中，周期 guard、生产对账和停用恢复改由现有 Server UID 999 的独立非 root 进程经真实 IPC 执行。root 观察者仅在 guard 阶段负责核对文件和注入竞争编辑。首轮写入后，稳定扫描保持环境值和台账；竞争写入被 guard 纠正。停止 guard 后写入新的竞争值，非 root 启动恢复撤销受管值并保留该编辑。夹具要求 Helper 日志恰有五次成功的 UID 999 操作，包括先前的崩溃组初始写入，以及 guard 的三轮和停用恢复。崩溃组本身的首轮台账恢复仍由 root 测试观察者执行。

共享 systemd 服务、桌面会话和 Proxy 管理模块动态休眠继续开放。

Linux Release 验收通过，Helper 日志确认五次 UID 999 成功请求并拒绝未授权 UID。专属 Helper 正常退出，宿主环境和台账摘要不变。日志见 `.artifacts/mb04-guard-nonroot-live.log`，归档 SHA-256 为 `7e47efcf9290814db088268c43b1ffd501b80792eaee596d78cbf2c622565f58`。

## MB-04 Proxy Server 授权到期回归

Windows Release 的独立 Proxy Server 测试已接入现行持久调度索引。使用原台账中的待处理发布触发生产 30 秒调度器，验证失败释放作用域、下一轮新建执行器、取消时等待异步清理，以及 HTTP 队列原回执。日志为 .artifacts/mb04-proxy-server-release-final.log。此轮为本地回归，Proxy 安全责任交接和动态休眠仍开放。

## MB-04 到期索引从原台账重建

到期扫描在原状态 gate 内从权威密文重建失配、损坏或缺失的索引，后续匹配扫描跳过台账解密。索引不可写时继续按原台账判断，未引入新的发布执行者。Windows Release Proxy Core 安全与生命周期作用域专项通过，日志为 .artifacts/mb04-index-repair-win.log。Linux 实机复测、安全责任交接和动态休眠仍开放。

Linux x64 Release 复测通过。Proxy Core 安全专项确认失配索引重建后不再解密原台账。同一制品的专属 sing-box 实机验收确认到期前流量可达、切换后旧连接关闭、过期凭据拒绝、保留凭据可重新连接，丢失发布响应后只执行一次 apply。两项分别覆盖索引恢复和实际网络切换。日志为 .artifacts/mb04-index-repair-linux.log，归档 SHA-256 为 18ea160a65fb22fd1de1aa4575775ac8f07c68619801b743f808053c8829ed97。实机仍使用专属运行时切换夹具，跨进程发布恢复和调度责任交接仍开放。

## MB-04 sing-box 到期发布跨进程恢复

Linux x64 Release 隔离夹具复用持久保护密钥。发布进程通过生产执行器落盘候选配置并停在 Helper 响应前。父进程用原候选配置切换真实 sing-box，随后强制终止发布进程。原 PendingPublication 保留。实际网络观察到旧流关闭、过期凭据拒绝和保留凭据重连。移除派生索引后，恢复进程重新打开原加密台账，重建索引并按实际应用配置摘要确认原候选，apply 共一次。

日志为 .artifacts/mb04-expiry-crash-live.log，归档 SHA-256 为 2b2143ce124c35224c72502ad569334be77c22bb7bd0371ded73adec28f18e9d。运行时切换及状态响应由专属 transport 夹具承载。此处调度依据原 PendingPublication，授权到期 worker 没有用户操作回执。生产 Helper 事务联合验收及动态休眠仍开放。

## MB-04 完整 Server 接续在途 TUN 激活

Linux x64 Release 在原私有 systemd 夹具中使用生产 TUN 安全服务和实际 Mihomo。子进程已完成真实控制器重载、设备创建和策略规则变更，在写入激活完成标记前强制终止。原操作 `23ff134e-a01c-4970-9268-63ca3b352731` 在同一私有持久状态目录经过独立 Helper 退出及完整 Server 拒绝启动后仍保持未完成。Helper 恢复后，非 root 完整 Server 的 Core 日志确认原 ID，禁用 TUN、恢复管理路由及 IPv4/IPv6 规则，Mihomo 继续提供实际 HTTP 转发。系统代理漂移纠正和 sing-box 后续授权到期同时通过，两代 Helper 各收到一次入站 apply。专属 cgroup 清理完成，宿主 sing-box、Mihomo 及代理文件指纹一致。证据为 .artifacts/mb04-tun-activation-crash-live.log，归档 SHA-256 为 d0a258b8484b3e48cf32517cdb5ca98b5c34a756ad93bd379bf91ce57e128e65。Windows Release 受管服务 15 项及模块边界回归通过，Docker 跨进程夹具返回原 Interrupted 回执，副作用一次。Mihomo 使用专属直连配置。确认受管发布后的 TUN 事务、Proxy 动态休眠和生产内存测量仍开放。

## MB-04 TUN 发布准入与恢复标记

TUN 激活在既有发布锁内通过待处理和确认发布门禁，并完成配置读取和计算后才提交恢复标记。恢复标记落盘后才允许首次文件和网络变更。拒绝准入或等待发布锁时取消，不产生虚假的 TUN 恢复责任。已有原标记遇到发布竞争仍保留原责任，不追加 reload。

Windows Release TUN 边界 33 项及模块边界回归通过，覆盖待处理和已确认发布、准入前取消、写入后取消、补偿及锁竞争。Linux x64 Release 私有网络专项首轮夹具使用旧发布修订号，修正为读取原台账后通过。原待处理发布 `b1bd32c1-b530-4990-918a-37cac406a530` 拒绝新的 TUN 激活，新增标记、reload、配置和规则变更均为零。另一条真实 TUN 恢复 `1a6ac90c-aeec-484b-a9ad-113e44ff17b3` 保留原标记直到路由可核实，原禁用 reload 一次。日志为 .artifacts/mb04-tun-admission-linux.log，归档 SHA-256 为 9d4bee3b1861cf0d42f01266c36b79f6d199c999eb15a79f920a425aa293bc18。完整非 root Server、独立 Helper 和私有 systemd 同批复测恢复原激活操作 `0719d031-b697-42db-82ed-d59526826165`，系统代理和授权到期持续，宿主服务状态及代理文件指纹一致。日志为 .artifacts/mb04-tun-admission-systemd-live.log，归档 SHA-256 为 d8e8a862bdefee52516f8ff640c02c9b2e44c881e499ff561a2327aea1ecdf74。此切片验证发布准入和原补偿，不启用已确认受管发布下的 TUN 网络事务。Proxy 保持 resident。

## MB-04 已确认受管发布的真实 TUN 门禁

Linux x64 Release 在私有 systemd、D-Bus 和网络内复测独立 Helper 及非 root 完整 Server。原受管配置中断后只执行一次具名补偿，原 `c130d33d-de2b-493a-b0f4-ce0c41019b5a` 回执为 Interrupted，原端口恢复实际转发。随后生产 TUN 安全服务从已确认的 Server 台账拒绝直接激活。无控制器请求及新恢复标记，受管状态修订、原回执、配置字节、运行 PID、管理路由、IPv4/IPv6 规则和真实转发均通过前后核验。专属 cgroup 已清理，宿主服务状态与代理文件指纹一致。Linux 原生套件通过 171 项及完整 IPC 路径，Windows Release 受管服务 15 项通过。日志为 .artifacts/mb04-confirmed-tun-live.log，归档 SHA-256 为 97ca5ce3b2e788e7ac7d39434f052f42d032c0bb5ff2caf4d048d60bb0ff61fe。此项覆盖受管发布后的拒绝与责任保留，受管 TUN 网络事务与 Proxy 动态休眠仍开放。

## MB-04 Core 接续运行期 TUN 恢复

原 Core TUN worker 在启动后持续扫描原持久标记。同一安全服务在原 gate 内识别当前进程已完成的会话，扫描不会拆除该会话。主动关闭在首次运行时或路由变更前将原标记置为未完成，保留原标记 ID 和路由快照。旧进程遗留或当前未完成的标记由原安全服务恢复。控制器或路由结果不可核实时继续持有原标记，只有控制器、设备和管理路由都核实后才清理。空闲扫描不解析控制器或管理执行图。

Windows Release TUN 边界 34 项、Core 安全所有权 37 项、工作作用域 31 项及模块边界回归通过。Linux x64 Release 在私有网络、挂载和 PID 命名空间用实际 Mihomo 验证。原崩溃操作 `5c9f30ec-7db1-4cee-a8ee-0777fc08b82a` 恢复时经历真实路由缺失、控制器响应丢失与不安全标记路径，原禁用 reload 一次。随后同一 Core worker 保留已完成的新会话。删除专属默认路由后显式关闭会话，原 `bb1ba90e-4e66-4237-ac4b-bf527f374067` 仍以未完成标记存在。路由恢复后，同一 worker 关闭原标记，设备和策略规则回到基线，未追加禁用 reload。宿主 Mihomo、sing-box PID 与代理文件指纹一致。日志为 .artifacts/mb04-tun-rescan-live-final.log，归档 SHA-256 为 befc69710c42c3a2d6844a2037396bb8bae39ab1a59dd646faf78d6caad3eeed。首轮本地重启夹具误用原 owner，已用两个 owner 复测。首轮远端包装器引号错误，在连接前退出，修正后通过。受管发布仍保留网络事务门禁，Proxy 保持 resident。

Linux 完整非 root Server、独立 Helper 与私有 systemd 联合回归也通过。完整 Server 恢复在途激活 `b5a3586c-3afb-4f92-9e67-2e86a5e75518` 的原标记和路由，Mihomo 继续运行，系统代理纠偏、sing-box 授权到期及真实转发接续，专属 cgroup 清理且宿主指纹一致。日志为 .artifacts/mb04-tun-rescan-systemd-live.log，归档 SHA-256 为 1e837ca6a8a4fa02c1b967d37906ee05e183f5cac44a47dfcb102f714e091a19。

## MB-04 Linux TUN 标记持久化及受管门禁复核

Core 在 Linux 按原格式写入 TUN 标记，文件内容同步并原子替换后同步状态目录及其父目录，删除标记后同步状态目录。所有权仍在原 Core 服务。受管 Mihomo 在现有 systemd 单元中隔离 `/dev/net/tun`，配置适配器拒绝需要宿主网络变更的配置。私有 systemd 复核使用实际 `ip -4 route get` 查询管理网关，并对 Mihomo 进程命名空间的 `active.yaml` 启动凭据取摘要。两次门禁请求维持原配置修订、PID、管理路由、规则、设备列表与真实 HTTP 转发。原补偿 `9b9e212b-68a3-41d5-b3d7-ce10a8d031ff` 仍可读取，新增标记与控制器请求均为零。

Windows Release ManagedRuntimes、TUN 边界 38 项、Core 安全 40 项、驻留及独立程序集 31 项、工作作用域 31 项及模块边界回归通过。可信下载探针因缺少进程级测试 CA 覆盖而跳过。Linux x64 Release 私有网络验收中，原直连操作 `700f289a-cd49-432c-be8a-63cde830bb28` 在启动方强制退出后恢复，实际设备撤除一次，恢复管理路由且保持其他操作的门禁。原关闭 `c179090c-61cd-4b42-8ec5-5947a25e7e72` 在路由恢复后结案，禁用未重复。宿主服务 PID 与代理文件指纹一致。日志目录 `.artifacts/mb04-tun-marker-sync/`，`tun-linux.log` 归档 SHA-256 为 `387fad8bafb1ec613e792b3d2c31a1c610a47e79aabc7b28c8d7c5026c7d6c4f`。

受管 Mihomo 私有 systemd 回归 178 项通过。独立 Helper 和 Server 的原回执恢复继续通过，专属 cgroup 已移除，宿主服务及代理文件指纹一致。`managed-linux.log` 归档 SHA-256 为 `33a2b4757bc236c53de8d33e3c5fb2881bb836b0e9718d47a18bd3e7302f51a6`。

已实现但实机未验证的部分为整机掉电后的标记目录持久性。受管 TUN 设备交付、规则与 DNS 效果归属、原生网络回执及其与 Core 标记的绑定均未实现。未核实的效果维持准入门禁，Proxy 保持 resident。本轮分别观察 Core 工作作用域释放和恢复夹具进程退出。未测量 ALC 回收、隔离负载内存或生产内存收益。

## MB-04 受限 Mihomo 的 TUN 描述符交付

初轮私有 systemd 实机中，测试进程创建非持久 TUN 并只添加一个专属目标的路由，通过私有 Unix 套接字把已打开的描述符传给 Mihomo v1.19.33 服务。测得有效与边界 capability 只有 `CAP_NET_BIND_SERVICE`，服务看不到 `/dev/net/tun`。进程 PID 45 的 FD 7 指向 `rk-fd-proof`，systemd 启动凭据 SHA-256 为 `53e0a0721db664590feebe1202d6667bd6c652c3ad6d15d8632261b526f780c1`，与冻结文件一致。测试进程关闭自身描述符后，内核把目标流量交给 TUN，上游收到一次 CONNECT 和一次真实 HTTP 请求。`SIGKILL` 后分别等待原进程回收和最后一个描述符关闭导致的设备消失，专属路由也移除。管理网关的内核查询及 IPv4/IPv6 规则在激活前、实际转发时和清理后相同。专属 cgroup 移除，宿主服务及代理文件指纹一致。

原型失败路径包括私有夹具中继承的 sysfs 视图与当前网络命名空间不一致、上游未实现 CONNECT，以及设备消失早于 systemd 回收进程。修正为使用内核链路查询、验证完整 CONNECT 转发并分别等待设备和进程后通过。原始日志为 `.artifacts/mb04-tun-marker-sync/tun-fd-linux-repo.log`，SHA-256 为 `eb67aee9e5d94efaccd68056944d2affc6d3815f9ae1f00ded4bba97a465541c`。

Windows ManagedRuntimes Release 回归通过，可信下载探针因缺少进程级测试 CA 覆盖而跳过。日志为 `.artifacts/mb04-tun-marker-sync/tun-fd-managed-win.log`。

此夹具覆盖已打开描述符的真实交付、转发和退出。生产 Helper 的持久网络事务、规则和 DNS 的归属、原回执绑定及中断恢复仍未实现。下一项先在 Helper 权威中把专属网络资源和原操作绑定，验证效果已生效但回执未确认的强制中断，仅按原 ID 核对与恢复。当前受管门禁及 Proxy resident 状态继续生效。本轮没有对象释放、ALC、隔离内存或生产收益测量。描述符语义见[Mihomo v1.19.33 配置源码](https://github.com/MetaCubeX/mihomo/blob/v1.19.33/listener/config/tun.go)和[sing-tun v0.4.28 Linux 源码](https://github.com/MetaCubeX/sing-tun/blob/v0.4.28/tun_linux.go)。

## MB-04 TUN 原生效果的双中断核对

私有 systemd 的 `--tun-fd` 夹具在首次网络效果前同步原操作意图，在交付描述符前同步运行服务身份、设备 ifindex 及专属路由。记录绑定操作 ID、网络命名空间、管理路由和规则、systemd InvocationID、PID 与进程启动时间、运行配置摘要和设备归属。原 `1e7b9adc846d476daed300a7b6089785` 在真实上游已收到请求后强制终止执行进程，未写入成功回执。新进程读回原记录并验证 PID、FD、内核路由及配置摘要 `33ff18368c8b7a65ccb2ddc12ed88a7e06d900b0699f3e8e33702b4fdbba31ce`，同一服务继续转发。

故意替换设备归属后，恢复以冲突退出，原记录逐字节一致，运行进程和转发继续。恢复原归属后，停止命令已派发时强制终止恢复进程，留下原停止意图。下一进程核对原进程、设备和目标路由均消失且管理路径未变，完成同一记录。重复恢复与查询返回原回执。三次独立 HTTP 请求均到达真实上游，专属 cgroup 移除，宿主服务及代理文件指纹一致。原始日志 `.artifacts/mb04-tun-marker-sync/tun-fd-owner-linux-current.log`，SHA-256 为 `fff5e85473b3525a2b57e476d9292429dd52db4a4eefd37ab47430fdc1fef4dc`。

已实现且已验证的范围为夹具原操作归属、拒绝冲突、停止结果核对和进程退出。夹具使用专属目标路由与自己的日志，恢复基线为测试单元停止。生产 Helper 尚未把受管 TUN 网络所有权和 Core 标记写入原事务，也未验收生产策略规则、DNS、实际工作租约或整机掉电。Proxy 保持 resident。未测量对象释放、ALC 回收、隔离内存或生产收益。

## MB-04 Linux 管理端点实际路由与原操作

Core 在 Linux 直连 TUN 的原保护标记中记录内核为网关和管理端点选择的路径。路由验证比较接口、下一跳、源地址、路由类型和外部路径的路由表。IPv4 映射地址先按原 IPv4 目的地查询。较早的直连标记仅有网关和出口身份，恢复仍要求管理目的地解析为本地路径或符合已记录的出口。所有激活、补偿和恢复调用携带同一标记 ID。命令只读取内核路由。[Linux `ip route get` 定义](https://man7.org/linux/man-pages/man8/ip-route.8.html)

私有 Linux 网络中，实际 Mihomo 设备已经由原操作 `19a20b2c-42d0-47ae-ae4e-df3e18de19d7` 禁用一次。保留默认网关及其出口，仅将管理端点的精确路由改到另一 veth，恢复保留同一操作且未重发 reload。解除专属路由偏移后原 Core 恢复收尾，设备、路由和规则恢复基线。真实自动路由及 DNS 劫持同轮通过，宿主共享服务和代理文件指纹一致。原始日志 `.artifacts/mb04-tun-marker-sync/tun-route-native-final.log`，归档 SHA-256 `6e9966d0760d1b783b95fabdd5fe45e496f6e2fabbbae083b2a842a4e0a5ee31`。

完整非 root Server 首轮在本机管理路径上保留原标记。诊断确认 `dev lo`、源地址及本地交付类型一致，唯一差异是 `ip` 输出的 `local` 表标签由省略变为显式。仅规范化 loopback 本地交付的表标签，外部管理路径的路由表和下一跳仍参与比较。失败日志 `.artifacts/mb04-tun-marker-sync/tun-route-managed-linux.log` 和 `tun-route-core-diag.log`。

最终非 root Server 私有 systemd 阶段按原 `3adf3e45-53d1-45fa-8d3c-08b187cd96cf` 恢复，保留实际 Mihomo 进程及转发。原网络设备和规则清理完成，宿主指纹一致。Windows Release TUN 边界 38 项、Core 安全及工作作用域、配置回滚和模块边界回归通过。日志前缀 `.artifacts/mb04-tun-marker-sync/tun-route-final-`。

另一私有 systemd 单元完成受管回归 185 项。两次受管 TUN 请求均被拒绝，原补偿 `e6b43569-3245-442f-9663-8980ac89ee16`、原生配置、选路及转发保持，新增标记和控制器请求为零。订阅 Helper 中断 `19878e1d-74ba-44d8-ad78-0893b5ef1e5f` 按原回执恢复。两组专属 cgroup 均移除，宿主服务和代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-route-managed-final.log`，归档 SHA-256 `87ccd164e8714682808e2368e20afad0575408c70d5546a802e6ff8ae347112f`。

该证据覆盖 Core 操作关联和直连内核路由。路由查找验证本次查询上下文的内核选路，实际管理 TCP 会话的标记和端口策略仍需受管事务联合验收。生产 Helper 的 TUN 描述符交付、设备、规则及 DNS 的持久归属仍缺，受管 TUN 门禁继续生效。Proxy 保持 resident。本轮未测量对象释放、ALC、隔离内存或生产收益。

## MB-04 Helper 原生 TUN 描述符交付

私有 systemd 夹具调用 Helper 程序集的原生描述符实现。原操作 `3c036388e3834fcfb985c09a59988e5f` 独占打开非持久 TUN，内核返回接口 `rk-fd-proof` 与 ifindex 4。原记录已持久保存接口、接收进程 PID 68 和路由后，Helper 代码核对 Unix 对端的 PID、UID 及原操作 ID，再尝试一次 `SCM_RIGHTS` 交付。重复打开、错误操作 ID、错误 PID 和重复交付均被拒绝。[Linux TUN/TAP 接口](https://docs.kernel.org/networking/tuntap.html) · [Unix 描述符传递](https://man7.org/linux/man-pages/man7/unix.7.html)

真实上游跨执行进程强制退出及设备归属冲突收到三次请求。Mihomo 的原 PID、FD、管理路由及配置摘要 `c11e74de76ca6deeb16d876f818098ee2ff77c22f175b22f3127fedddee2bc09` 一致。停止已派发时再次强制退出，下一进程仅核对进程、设备和路由清理，原回执可重复查询。专属 cgroup 移除，共享服务及代理文件指纹一致。实机 x64 日志 `.artifacts/mb04-tun-marker-sync/tun-helper-fd-linux.log`，归档 SHA-256 `e7c6dbb6e1abc35fd5a669c6bea211feb3b475ac960807f4433f7d3119b78992`。

第二轮用 Helper 程序集的原生接收替换夹具的 Python 接收。接收端拒绝错误发送进程，核对 Unix 对端 PID、UID 和消息内的原操作 ID，只接受一个带 `CLOEXEC` 的描述符，并通过 `TUNGETIFF` 及接口索引核对设备。私有 systemd 单元以 `PrivateDevices`、`NoNewPrivileges` 及仅含 `CAP_NET_BIND_SERVICE` 的能力集启动测试启动器。启动器将描述符放入空闲的固定槽位 198，并以同一 PID 执行固定 Mihomo 二进制，读取 systemd 加载的配置凭据。

原操作 `fd4feb62257f408c9bb7000c8265a4f4` 的接收进程 PID 68、`rk-fd-proof` ifindex 4、管理选路及 IPv4/IPv6 规则、配置摘要 `05d93814d2db6a9c35b74226a106ad5818d473d7d5d21ecfe7cc37f8906ec89e` 在交付中断和归属冲突检查中保持一致。真实上游收到三次请求。停止中断后只核对进程、设备及路由退出，重查取得原回执。专属 cgroup 清理，共享服务及代理文件指纹一致。首轮因受限启动器无法遍历专属夹具目录而在交付前失败，见 `.artifacts/mb04-tun-marker-sync/tun-helper-launch-linux.log`。赋予该目录遍历权限后，实机 x64 复验通过，日志 `.artifacts/mb04-tun-marker-sync/tun-helper-launch-linux-retry.log`，归档 SHA-256 `89d39d8e60a1e5e652295b370ae4373ba6a56e094eb8cf9078bb4ee38bc0a44a`。

Windows ManagedRuntimes Release 回归通过，配置事务 99 项，可信下载探针因缺进程级测试 CA 覆盖而跳过。Linux arm64 描述符 ABI 分支未实机验证。启动器位于测试程序集，网络策略、持久归属记录及恢复仍由夹具驱动。生产固定单元接入、设备及规则和 DNS 归属、Helper 与 Core 原回执的联合证明均待完成。受管门禁及 Proxy 常驻继续生效。本轮验证夹具进程退出，对象释放、ALC 回收、隔离内存及生产内存收益未测量。

## MB-04 Helper TUN 设备和精确路由归属

Helper 的 Linux TUN 路由实现读取原意图中的 IPv4 地址和单个目标。它在独占打开的非持久设备上写入原操作 ID 标签、带 `noprefixroute` 的 /32 源地址和精确目标路由，使用固定的 `/usr/sbin/ip` 参数及独占添加，拒绝覆盖已有目标路由或重复应用。只读核对检查设备标签、名称、ifindex、启用状态、地址和精确路由。归属证据不一致时返回待核验，专属夹具保留原记录和进程，不重建路由。 [Linux TUN/TAP](https://docs.kernel.org/networking/tuntap.html) · [ip-route](https://man7.org/linux/man-pages/man8/ip-route.8.html)

Linux x64 私有 systemd 复验的原操作为 `1d64716220184883a948ce12ccd8114a`，设备 `rk-fd-proof` ifindex 4，接收和运行 PID 75，配置摘要 `1554be492de6eb12461ea2ab39d1c5f5c55f57d37443924127bb95188824dfed`。Helper 的描述符发送进程强制退出后，原记录逐字节一致，管理选路及 IPv4/IPv6 规则保持，真实上游共收到三次请求。归属标签漂移及专属精确路由丢失分别返回 `runtime.network_device_identity_unknown` 和 `runtime.network_route_conflict`，两次均保留原记录及 Mihomo 进程。夹具显式还原测试漂移后继续停止验收。停止派发后强制退出，下一进程只读确认原进程、设备和路由退出，并重复取得同一夹具回执。专属 cgroup 已移除，共享服务和代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-owned-route-linux-final.log`，归档 SHA-256 `1401ae0987e23b95e31531ce8f8e01a7f5d44d4aff69909c7f7dd19fa5eabdb4`。

Windows ManagedRuntimes Release 在宿主权限下通过，含配置事务 99 项。沙箱内运行先后受临时文件替换及原生监听表限制，可信下载探针因宿主缺进程级测试 CA 覆盖而跳过。最终回归日志为 `.artifacts/mb04-tun-marker-sync/tun-owned-route-win-final.log`。

本轮证明单个 IPv4 目标的设备和路由归属。原意图持久化、管理选路和规则的比对、systemd 启动、恢复及回执由夹具驱动。生产固定单元、原归档状态的持久检查点、策略路由和 DNS 归属、真实工作租约与同键生产入口仍待接入。生产受管 TUN 门禁继续生效，Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 Helper TUN 原归档操作检查点

Helper 在 `operations/<operationId>/tun-0.json` 至 `tun-4.json` 追加意图、网络写入派发、描述符派发、停止派发及已核对恢复五个检查点。持有归档实例锁的执行者以独占创建写入 root 专用文件，依次同步文件、操作目录及其父目录。记录绑定原操作、活动制品修订、配置摘要、启动 ID、网络命名空间、管理选路和规则基线摘要、设备名称与 ifindex，以及服务 PID、systemd InvocationID 和进程启动时刻。读取会核对阶段顺序与不可变身份。半写或缺阶段记录保留待核验。生产普通配置补偿在发现该记录时保留原状态，进入网络事务门禁。

Linux x64 私有 systemd 实测原操作 `311e16d584e04c10aa822f4e7ba1380c`，`rk-fd-proof` ifindex 4，接收与运行 PID 76，配置摘要 `923fc3d8dc0f8d93ccdd7cab4b904309eea521dee9de07f5f9cda43927277bcb`。写入侧使用实际归档 `instance.lock`，夹具的跨进程恢复也使用同一锁。发送进程强制退出后，三条已派发检查点及原启动记录逐字节一致，管理选路、IPv4/IPv6 规则和真实上游三次请求均已核对。半写的下一阶段、设备标签漂移、路由丢失和重复停止派发均被拒绝。停止派发后强制退出，恢复先只读核对服务、设备及路由退出，再追加原操作终态。同一原操作反复读到相同夹具回执。专属 cgroup 清理，宿主共享服务及代理文件指纹一致。实机日志 `.artifacts/mb04-tun-marker-sync/tun-checkpoint-linux.log`，归档 SHA-256 `de8105e549761556960b2dd81647a07e39d183cdb604196a0270a080ca3dcfe6`。

Windows ManagedRuntimes Release 通过，含配置事务 99 项。可信下载探针因宿主缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-checkpoint-win.log`。

检查点由 Helper 程序集写入，固定单元启动和停止、运行配置与管理选路的实时比对、清理观察及终态投影仍由夹具编排。生产写入入口、规则和 DNS 归属、同键 HTTP 回执和实际工作租约待联合验收。生产受管 TUN 门禁继续生效，Proxy 保持 resident。本轮核验专属进程退出，对象释放、ALC 回收、隔离负载内存及生产内存收益未测量。

## MB-04 受管 Mihomo 固定 TUN 单元

`sudo bash Tests/ManagedRuntimes/run-proxy-systemd-linux.sh --tun-fd <Release-tests.dll> <Linux-Helper-apphost>` 在私有挂载、PID 和网络命名空间内运行真实 `relaxkonos-mihomo.service`。Helper 的归档宿主生成固定单元和注册，保存原 broker PID，并在调用 systemd 前追加启动派发检查点。TUN 单元禁止自动重启和普通托管启动，保持 `PrivateDevices` 和仅含 `CAP_NET_BIND_SERVICE` 的能力集。固定 Helper 启动器核对 systemd MainPID、InvocationID、程序和凭据摘要，从原归档操作目录的 Unix socket 接收描述符，并核对已落盘的接收进程身份。同一 PID 执行已验证的 Mihomo，继承 FD 198。普通受管配置沿用原单元启动。

Linux x64 原操作 `891316628d634f4f982fc1261c86f76e`，`rk-fd-proof` ifindex 4，接收和运行 PID 95，配置摘要 `80b9b24b79b9025e8d7f23a91e49cf9a45fd10519c1648cc42c204547c54dbb3`。重复启动派发、单元外直接执行启动器和终态后的单元重启均被拒绝。真实上游收到三次请求，发送进程和停止执行进程先后被强制终止，原检查点、管理选路、配置身份及原回执均按相应阶段核对。路由丢失、设备标签漂移和半写检查点保留待核验。专属进程、设备、路由及 cgroup 清理，宿主共享服务及代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-fixed-linux.log`，归档 SHA-256 `0a1c5e9992e2740a6e5be84b882718279cf42e706f032e5c1d617f36c4f3ad3e`。

Windows ManagedRuntimes Release 通过，含配置事务 99 项。可信下载探针因宿主缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-fixed-win.log`。

实机使用生产固定单元与 Helper apphost，仍由夹具组装原配置、创建 broker、编排具名停止及终态回执。生产归档事务尚未调用 TUN 写入和启动，策略规则与 DNS 的归属和清理、同键生产回执、实际工作租约待联合验收。生产受管 TUN 准入门禁继续生效，Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 Helper 接管 TUN 交付与具名停止

归档宿主在实例锁内持久记录原意图，创建专属设备、路由和 broker，依据 systemd MainPID、InvocationID、进程启动时刻及 Unix 对端凭据确定描述符接收者。它在发出描述符前写入原派发记录，之后核对实际 Mihomo 可执行程序、配置凭据、FD、设备和精确目标路由。Helper 直接读取单个 IPv4 管理端点的选路及 IPv4/IPv6 规则，激活和停止均复核冻结摘要。

具名停止以 pidfd 固定原进程，在重新核对实时身份和路由后落盘停止派发，发送一次 SIGTERM。重启后的完成路径只核对固定单元、原进程、设备、源地址、精确目标路由和管理基线；确认清理才追加原终态。同一已派发停止请求无重发入口。缺少 pidfd 支持时在网络变更前拒绝激活。[pidfd_open](https://man7.org/linux/man-pages/man2/pidfd_open.2.html) · [pidfd_send_signal](https://man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)

Linux x64 私有 systemd 的原操作 `8a9005b2656e49679400ca5edbd1eb90`，设备 `rk-fd-proof` ifindex 4，接收与运行 PID 100，凭据摘要 `94454d6d3dcedf92c068203e61ab14ae5c0bae88b179417be9a18e7c49e89112`。真实上游收到三次请求。原执行进程在效果产生后强制退出，管理选路、配置和原记录一致。专属管理路由下一跳漂移时，Helper 返回 `runtime.management_route_changed`，保持原进程与待核验记录。设备标签漂移、精确路由丢失、半写记录、重复激活及重复停止也被拒绝。停止派发后再次强制退出，下一进程从同一检查点只读确认清理，原 ID 重复查询得到相同夹具回执。专属进程及 cgroup 清理，宿主共享服务与代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-broker-linux.log`，归档 SHA-256 `b60713aff5e3455854659931ca6e995ba0b2f24661c97d75a1f6720a99209244`。

Windows ManagedRuntimes Release 通过，含配置事务 99 项。可信下载探针因宿主缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-broker-win.log`。

实机通过夹具调用生产归档宿主，夹具提供冻结意图、实例锁和中断控制，并投影回执。生产配置事务的授权及调用尚未接入。当前核对单个 IPv4 管理目的地与现有策略规则，策略规则写入、DNS 所有权、同键 HTTP 及实际工作租约仍待验证。受管 TUN 门禁继续生效，Proxy 保持 resident。已核验专属进程退出，对象释放、ALC 回收、隔离负载内存及生产收益未测量。

## MB-04 TUN 归档事务同键回执

`RuntimeArchiveConfiguration` 在归档实例锁内按原操作 ID 或同一幂等键查找待处理配置。冻结请求摘要和键均匹配时直接返回原 ID 和 `RecoveryRequired`，请求或键冲突仍拒绝。终态沿用原归档回执。等待实例锁期间的并发行为沿用原门禁。

私有 systemd 夹具改用真实归档配置事务持有实例锁，并由测试适配器把原计划绑定到候选配置摘要、活动修订和 Helper 归档宿主。Linux x64 复验的原操作 `089af3acd05747c0a6ec2f3f382da847`，接口 `rk-fd-proof` ifindex 4、Mihomo PID 115、配置凭据摘要 `406d1075c5aaff0cc7035c103620087d4a1cfa041ea354285a8bca6a9518c275`。候选已真实转发且归档事务尚未确认时终止执行进程。同键新请求 ID 返回原 `RecoveryRequired`，归档状态文件逐字节一致。管理选路和配置身份维持，真实上游收到三次请求；管理路由漂移、设备标签冲突、精确路由丢失及半写记录保持待核验。

具名归档恢复在原 Helper 停止已派发后再次被强制终止。下一进程确认网络清理、恢复原配置和停止状态，归档事务持久返回原 `Interrupted`。再次同键调用读到相同回执，状态文件仍逐字节一致。专属进程、设备、路由和 cgroup 已清理，宿主共享服务与代理文件指纹一致。首轮因夹具候选摘要大小写与 Helper 规范不同而在准备阶段退出，日志 `.artifacts/mb04-tun-marker-sync/tun-receipt-linux.log`。统一摘要格式后复验通过，日志 `.artifacts/mb04-tun-marker-sync/tun-receipt-linux-retry.log`，归档 SHA-256 `922da1b5f5691035fedf813c3c31df1a8dc6cdb439569956b827b3c9c4ab807a`。

Windows ManagedRuntimes Release 通过，配置事务 104 项，可信下载探针因宿主缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-receipt-win.log`。本次使用真实归档事务和 Helper 归档宿主，冻结意图与配置授权仍由测试适配器提供。生产配置事务的网络授权与宿主调用、策略规则及 DNS 归属、同键 HTTP 和真实工作租约待验证。受管 TUN 准入门禁继续生效，Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 冻结 TUN 意图授权

配置计划仅保存网络意图摘要。摘要覆盖原操作 ID、安装、主机、活动制品修订、候选配置身份及规范化网络意图。完整意图保存在 Server 加密发布台账，只随同一原操作的新鲜候选授权回读。旧配置授权仍独立。Helper 的配置适配器重新校验意图与原计划的绑定。描述符模式核对设备、FD 198 和允许的 TUN 字段，拒绝 Mihomo 自行添加路由、重定向和配置 DNS 劫持。生产发布入口继续拒绝该模式。

Linux x64 私有 systemd 夹具使用生产加密台账、真实授权提供器和认证授权通道。篡改目标地址及重新计算摘要后启用自动路由均在归档派发前被拒绝。原操作 `c19d90cc545b4b71866a4a26a5339bfc` 使用 `rk-fd-proof` ifindex 4、Mihomo PID 115 和配置摘要 `0cb1d0d4c49b7884603fc8c91941b83121970a285189c19c3e88076de35b8f4b`。原进程强制中断后，同键返回原 `RecoveryRequired`，选路及配置身份维持，真实上游收到三次请求。停止派发后再次中断，新进程从同一加密台账回读原配置授权、只读确认网络清理并恢复旧配置，同键读到原 `Interrupted`。专属进程及 cgroup 清理，宿主共享服务与代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-auth-linux.log`，归档 SHA-256 `175dd3334e55c834a33fb2b400c95c61e388c216ed9c3dc62d501e5a5f43939b`。

Windows ManagedRuntimes Release 通过，含 104 项配置事务检查。可信下载探针因缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-auth-win.log`。

夹具仍提供最初网络意图，通过测试适配器调用生产归档事务及 Helper 宿主，并在同进程内建立认证回读通道。生产 Server 生成意图、生产配置适配器调用归档宿主、策略规则及 DNS 归属、生产同键 HTTP 和真实工作租约待验收。Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 生产归档配置适配器的 TUN 恢复

生产配置适配器在原归档实例锁内复核制品、签名目录、冻结网络意图及原候选授权，使用现有配置检查、停止、快照及配置替换流程调用固定 TUN 宿主。试运行要求原普通服务正在运行且禁用开机启动。普通停止、移除及覆盖仍在途 TUN 注册被拒绝。具名恢复只对可证明的原描述符进程派发停止，已派发停止时只读确认退出，再恢复旧配置、数据与运行状态。早期派发身份未知时继续待核验。TUN 生命周期尚未联合验证，适配器的成功提交门禁和 Server 的公开发布准入继续生效。

Linux x64 私有 systemd 最终轮使用真实生产配置适配器、Helper 固定单元、签名夹具目录和 Server 加密授权。原操作 `148c9c7356f448d5b3e53de2261a2c47`，`rk-fd-proof` ifindex 4、候选 PID 197、配置摘要 `e83aaa1ca47944f1e0efe03a19be97f07d8a4d662de4368ee0709b2d3c1e6d51`。候选已真实转发但事务尚未确认时强制终止原执行进程。提交门禁和普通停止拒绝均实测，管理选路及配置身份一致，真实上游收到三次请求，同键返回原 `RecoveryRequired`。第二次中断命中已派发的原停止。后续恢复确认 TUN 设备及路由退出，恢复旧配置、可变数据哨兵与已认证监听，返回原 `Interrupted`。同键复查未改写台账或重启已恢复进程。专属 cgroup 清理，共享服务及代理文件指纹一致。最终日志 `.artifacts/mb04-tun-marker-sync/tun-adapter-linux-final.log`，归档 SHA-256 `5c2363bf63e6bca9358da5bcefa09de3e22fc529f1b172a87056197719c8a975`。

首轮夹具签名密钥标识错误，在目录验签前置检查被拒绝，见 `.artifacts/mb04-tun-marker-sync/tun-adapter-linux.log`。修正后发现隔离环境缺少 `systemd-run --pipe` 所需的系统总线，见 `.artifacts/mb04-tun-marker-sync/tun-adapter-linux-native-diag.log`。夹具在私有 `/etc` 和 `/run` 内加载专属 D-Bus 单元后通过，宿主共享服务继续按原指纹核对。Windows ManagedRuntimes Release 通过，含 104 项配置事务。可信下载探针因缺进程级测试 CA 覆盖跳过，见 `.artifacts/mb04-tun-marker-sync/tun-adapter-win.log`。

夹具仍提供初始意图、测试信任源和两个中断时点。生产 Server 意图生成、策略规则与 DNS 归属、正常成功提交后启停与移除、生产同键 HTTP 和实际工作租约待验收。Proxy 保持 resident。已确认专属进程退出，对象释放、ALC 回收、隔离内存与生产收益未测量。

## MB-04 Server 生成 TUN 意图及管理 UID 复核

Server 规划器从受控设置、可信 IPv4 源和目标范围、Core 观察到的管理路由及候选配置生成固定描述符模式。拒绝默认自动路由、DNS 劫持和把受保护网关纳入 TUN。使用同一读取逻辑，Server 冻结其有效 UID 的 IPv4 管理端点选路及 IPv4/IPv6 规则摘要，Helper 以原 UID 独立复核。规划只产生材料及摘要，网络写入仍由原 Helper 执行。 [iproute2 的 UID 选路参数](https://raw.githubusercontent.com/iproute2/iproute2/main/man/man8/ip-route.8.in)

Linux x64 私有 systemd 让非特权 Server 进程以 nobody UID 生成候选和意图，root Helper 使用原计划派发。原操作 `d13ee83cd0b94eeaa367adfa63474541`，`rk-fd-proof` ifindex 4、候选 PID 223、配置摘要 `2599973f214f2bb41dd3b5c0cab7f21d7f6c25695b124f52e1a3a0c4b92a109d`。拒绝默认自动路由设置及网关目标，授权通道也拒绝换目标和开启自动路由。效果已产生后强制中断，管理选路和配置身份一致，真实上游共收到三次请求。专属策略规则仅改变 Server UID 的下一跳，root 路由未变，Helper 仍拒绝停止并保留原进程及台账。移除夹具的故障注入后继续原具名恢复，原停止已派发时再次强制中断。后续恢复旧配置、数据及监听，同键重读原 `Interrupted`。专属 cgroup 清理，共享服务与代理文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-plan-linux.log`，归档 SHA-256 `98c720d4999a2f961090e30a94bd5d4a89f87491ea2c9066e064e95e0e600f41`。

Windows ManagedRuntimes Release 通过，含 104 项配置事务，可信下载探针因缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-plan-win.log`。

本轮只覆盖单个 IPv4 管理端点、一个精确 TUN 目标和 UID 选路。策略规则由夹具注入以验证漂移拒绝。生产受控范围来源及发布入口、规则和 DNS 写入所有权、成功提交后的生命周期、同键 HTTP 和实际工作租约待验收。公开准入与成功提交门禁继续生效，Proxy 保持 resident。已确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 TUN 策略规则归属及删除中断

Helper 在原操作记录中绑定派生路由表，独占添加精确 IPv4 目标及策略规则。归属核对要求优先级、目标、表、协议和其余选择器全部符合原规则。同优先级或同表存在外来规则时拒绝继续。管理基线使用数字化规则输出，只有唯一且完整匹配的自有规则可在基线比对时排除。原停止后确认设备、地址及专用表路由退出，再持久化规则删除派发并删除一次。派发结果不明时只读确认规则缺席。[iproute2 策略规则](https://raw.githubusercontent.com/iproute2/iproute2/main/man/man8/ip-rule.8.in)

Linux x64 私有 systemd 原操作 `f10ec9a945e74c4dadbd277e3bc18689`，专用表 1823052713、规则优先级 11000、协议 186、`rk-fd-proof` ifindex 4、候选 PID 226、配置摘要 `d0e98f9961a9abed8e3b72d710300e03647beba4609359c827cdd1965e9a4c63`。三次真实上游请求覆盖候选派发中断及归属冲突。夹具插入同优先级外来规则时，Helper 返回 `runtime.network_rule_conflict`，保留原记录与进程。也复验了仅影响 Server UID 的管理路由漂移拒绝。

原停止派发后强制中断。再恢复时，由私有挂载内的诊断边界在真实内核删除规则成功后、Helper 写终态前第三次强制中断。重启只读核对规则缺席，实际删除调用计数为一，旧配置、数据、监听和原 `Interrupted` 回执恢复，同键读取未重发。专属进程及 cgroup 清理，宿主共享服务与代理文件指纹一致。首轮数字路由输出的链路范围导致只读校验拒绝，保持待核验，见 `.artifacts/mb04-tun-marker-sync/tun-rule-linux.log`。按内核数字链路范围校验后通过，日志 `.artifacts/mb04-tun-marker-sync/tun-rule-linux-final.log`，归档 SHA-256 `b0b7260a2f71d1d775cbc125ae1da2df8e2615d841dce0bc139e167bb9297529`。

Windows ManagedRuntimes Release 通过，含 104 项配置事务。可信下载探针因缺进程级测试 CA 覆盖跳过，日志 `.artifacts/mb04-tun-marker-sync/tun-rule-win-final.log`。

当前证据覆盖一个精确 IPv4 目标和一条自有策略规则。生产范围来源、DNS 写入归属、提交后生命周期、生产同键 HTTP 和真实工作租约待验收。公开准入及成功提交门禁继续生效，Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 TUN 分流 DNS 归属

可选分流域进入原冻结意图及授权摘要。Helper 检查 systemd-resolved 可用后，仅向原 TUN ifindex 写入 DNS 服务器、分流域及禁用默认 DNS 路由。DNS 服务器使用同一受控 IPv4 目标。写入前持久记录总线 ID、resolver 唯一 owner、其他接口和全局 DNS 设置摘要。重复或重叠域、配置漂移、resolver 身份变化及未知结果保留待核验。具名恢复确认接口退出并核对本接口 DNS 缺席和原外部摘要，无全局重置动作。描述符配置显式关闭 Mihomo 原生 DNS 设置与默认劫持。 [systemd-resolved 接口](https://raw.githubusercontent.com/systemd/systemd/main/man/org.freedesktop.resolve1.xml) · [Mihomo v1.19.33 默认配置](https://raw.githubusercontent.com/MetaCubeX/mihomo/v1.19.33/config/config.go)

Linux x64 私有 systemd 和独立 resolver 上，原操作 `bd4d8609c84848c788532547f6600240`，`rk-fd-proof` ifindex 4，候选 PID 239，配置摘要 `79f0baa39bac37dea487e8fbe2de576b62705147043d081a6e3a00b1d02e46c6`。确认前中断及冲突检查期间，真实 HTTP 和分流 DNS 上游分别收到三次请求，原管理选路及配置一致。变更原接口分流域时，Helper 返回 `runtime.network_dns_conflict` 并保留原进程与记录。撤销夹具漂移后，停止派发和规则删除阶段分别再次中断。恢复确认本接口 DNS 缺席、外来链路 DNS 及域和默认路由保持原样，原 `Interrupted` 同键回执可重复读取。专属进程和 cgroup 已清理，宿主共享服务及代理、resolver 文件指纹一致。最终日志 `.artifacts/mb04-tun-marker-sync/tun-dns-linux-final.log`，归档 SHA-256 `ed06390ef5a5898724d98ef8f59424b594f79da4707dac8d865cdeff07649d28`。

首轮 resolver 分流已生效，但 Mihomo 省略 `dns-hijack` 时启用默认 53 端口拦截，实际查询返回 `SERVFAIL` 且上游零请求。诊断日志 `.artifacts/mb04-tun-marker-sync/tun-dns-linux-diag.log`。显式设置 `dns-mode: disabled` 与 `dns-hijack: []` 后实机通过。

Windows ManagedRuntimes Release 通过，含 104 项配置事务。可信下载探针因缺进程级测试 CA 覆盖跳过，日志 `.artifacts/mb04-tun-marker-sync/tun-dns-win-final.log`。

实机覆盖 DNS 归属确认后的原归档中断、外部漂移和清理。DNS 写入派发后、写入归属尚未确认的只读恢复路径已实现，专用断点及 resolver 换代未实机验证。生产网络范围、发布入口、成功提交后生命周期、同键 HTTP 和真实工作租约待验收。公开准入及成功提交门禁继续生效，Proxy 保持 resident。本轮确认专属进程退出，对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。

## MB-04 TUN DNS 应答中断与 resolver 换代

`run-proxy-systemd-linux.sh --tun-fd <Release-tests.dll> <Linux-Helper-apphost> [full|dns-receipt|dns-owner]` 在三个独立私有 systemd 环境运行。专属挂载中的故障边界仅在真实 `SetLinkDNS` 成功后暂扣子进程退出，随后强制终止原 Helper。断点命中 `DnsDispatched`，原 `DnsApplied` 尚未落盘。每轮记录 `SetLinkDefaultRoute`、`SetLinkDomains` 和 `SetLinkDNS` 各一次。原记录和配置摘要跨中断核对，真实 HTTP 和 DNS 上游各三次。

`dns-receipt` 原操作 `9d8819c1874c4b84aabc2cc4475a0b9e`，ifindex 4，PID 240，配置摘要 `c5aaa2b24b16f2858b9e2f118070a546b7a725436832d6aa2f231e5ca0ac5ecc`。同键读取原 `RecoveryRequired` 时归档状态未变。修改分流域后具名恢复保留原 `DnsDispatched`，记录 `runtime.network_dns_conflict`，停止未派发。恢复原外部设置后，生产适配器只读确认原 DNS，接续具名停止与规则删除，恢复旧配置和可变数据，同键取得原 `Interrupted`。本接口 DNS、设备和路由缺席，规则删除一次。

`dns-owner` 原操作 `de2eef3fed2f4ce4935c903d655cdc22`，ifindex 4，PID 243，配置摘要 `2e826250f2a6a55b186e4cd2ba9b602a506e876ea7593aaef9c3da0e57f9bfe6`。专属 resolver 由 `:1.0` 换代为 `:1.33`，夹具重建与原观察相等的 DNS、域及回退设置，真实查询仍通过。Helper 返回原 `RecoveryRequired`，归档记录 `runtime.network_dns_owner_unknown`。原生检查点、原进程及管理选路保持，未派发停止或规则删除，未向新 resolver 重发。原事务继续待核验，夹具通过退出私有命名空间清理专属资源。

`full` 原操作 `6bdcae6f539240418968af3b76813574` 同批通过。三轮均核对专属 cgroup 清理及宿主共享服务、代理和 resolver 文件指纹一致。日志 `.artifacts/mb04-tun-marker-sync/tun-dns-ack-linux.log`，归档 SHA-256 `071c02f28dce63e6260643f98f0659ff680642884625cce0fec9333f96fdfdd7`。Windows ManagedRuntimes Release 通过，含 104 项配置事务检查，可信下载探针因缺进程级测试 CA 覆盖跳过。日志 `.artifacts/mb04-tun-marker-sync/tun-dns-ack-win.log`。

生产范围、发布入口、成功提交后生命周期、同键 HTTP 与实际工作租约仍待联合验收。公开准入和成功提交门禁继续生效，Proxy 保持 resident。已确认专属进程退出，对象释放、ALC 回收、隔离负载内存与生产内存收益未测量。

## MB-04 受管 TUN 宿主范围

Server 规划器从固定的 `/etc/relaxkonos/mihomo-tun-network.json` 冻结设备、源和目标 IPv4、管理 UID 及可选分流域。Server 以实际 UID 核对策略及管理选路，Helper 依据独立读取的同一宿主策略复核原授权。文件严格解析且限 4 KiB，读取依次固定 root 所有且不可由组或其他用户写入的目录描述符，拒绝符号链接和非普通文件。原恢复路径仍由原归档检查点、旧配置授权和原网络效果归属裁决。

**已实现且已验证。** Linux x64 三个独立私有 systemd 环境均拒绝缺失、组可写及符号链接策略。非特权规划器拒绝设备名不符和管理网关重叠。冻结后替换目标地址，真实 Helper 的前置检查及激活均返回 `runtime.network_scope_changed`，旧 PID、配置字节和原操作目录内容保持。恢复合法策略后实际运行候选，原 Helper 强制中断后撤掉策略文件，核对管理路由、原配置和真实上游。

| 路径 | 原 operationId | 原候选身份与结果 |
| --- | --- | --- |
| 已应用 | `c69aab810cc34b4e8b90d488b6b7f4de` | `de91273a5cc05c8eb38deaccfd20d9768a47d54dfacf492797bae2f8a14f37de`，PID 249，ifindex 4。实际 HTTP 和分流 DNS 各三次，跨中断的原回执先待核验、后 `Interrupted`，具名停止与规则删除无重放 |
| DNS 写入后应答丢失 | `16fbf99ab9fc4b29a431a0f6044fc7d3` | `b3c137058f21aba31d5d158f8befa7f5b1269e846747803459b542a92e268b86`，PID 253，ifindex 4。分流域漂移保持原待核验，原 resolver 设置恢复后只读确认并返回原 `Interrupted` |
| resolver 换代 | `e889655d1f3640828d847fe7648d55dc` | `9d140ab7f718d9645ed87477971b9af04f686f9f7dcbeb010127de75f92c791b`，PID 250，ifindex 4。设置与流量一致仍返回原 `RecoveryRequired`，未重发 DNS、停止或规则删除 |

首轮夹具把预建的空原操作目录误判为 Helper 写入，旧服务仍在运行且宿主清理通过。调整断言后最终三轮通过。三轮专属 cgroup 均清理，宿主共享服务、代理和 resolver 文件指纹一致。最终归档 SHA-256 为 `ab8cd5c581cb0b2e3d23cb6d3ddeab688ac79904b2b445ee40217b88be762a06`。日志在 `.artifacts/mb04-tun-marker-sync/`，首轮 `host-scope-linux.log`，最终 `host-scope-linux-final.log` 和 `host-scope-windows-final.log`。Windows Release 包含 104 项配置事务检查，可信下载探针因缺进程级测试 CA 覆盖跳过。

**已实现但实机未验证。** Linux arm64 的策略读取分支已实现，本轮实机为 x64。

**未实现或仍受门禁保护。** 成功提交后的持久归属定位、生命周期、公开发布、同键 HTTP 和该入口的实际工作租约仍待验收。已确认专属进程退出。对象释放、ALC 回收、隔离负载内存及生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN 提交态归属

**已实现且已验证。** 原归档配置事务在同一次状态替换中保存成功回执和活动 TUN 归属。归属记录含原 operationId、活动修订、配置和冻结网络意图摘要。归档台账核对原事务阶段及同一成功回执，Helper 查询另外复核固定单元、进程、原检查点、设备、路由、规则和分流 DNS。写入新配置、切换制品或移除均拒绝绕过已提交归属。原同键读取优先返回原回执。

Linux x64 私有 systemd 的 `committed` 场景使用真实候选验证器和归档提交，夹具限定允许写入成功态。原 `cfed8f56146b41ccac49657efb4cb5aa`，PID 250，ifindex 4，配置摘要 `75b27c64370890674d6212f1a156ae1d2ae5e6e8080ab3ddb11d15270131e9d2`。归属及成功回执落盘后、应答前强制终止原事务进程，随后移除宿主范围文件。管理选路、Mihomo 进程及原配置跨中断一致，两侧均完成真实 HTTP 和分流 DNS。重启查询按原 ID 返回原成功回执和提交见证，同键读回相同结果。普通停止被拒绝。外部改写设备标签后，当前归属变为未知，原成功回执及见证仍可读。台账和原生检查点字节保持，未重派启动、停止或规则删除。

首轮夹具以自造宿主身份初始化，生产查询拒绝身份不符。改为生产生命周期初始化后，复跑发现 resolver 换代会调换不同接口的枚举顺序。夹具按接口编号对齐，同时保留每个接口内的地址与域顺序，继续要求外部设置逐项一致及 resolver 身份确实变化。

同批 `full` 原 `f764797a7c8a4afe9764ccdc9e5693e5`、`dns-receipt` 原 `6b74a13fd86248e1802f21c7b144f3c7`、`dns-owner` 原 `f97b3bcc2efa472795ab9b0410d8fdb9` 全部通过。四个专属 cgroup 已清理，宿主服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `f254bd84561931ba7ccfd129bf811a40fbd82b3a4c185d92ff3fae7694c4543a`。最终日志在 `.artifacts/mb04-tun-marker-sync/tun-ownership-linux-accepted.log` 和 `.artifacts/mb04-tun-marker-sync/tun-ownership-windows-final.log`。

**已实现但实机未验证。** 本轮实机为 Linux x64，其他架构未验证。Windows Release 通过，包括 104 项配置事务检查，可信下载探针因缺进程级测试 CA 跳过。

**未实现或仍受门禁保护。** 提交后的具名启停、重启、移除，公开发布、生产成功提交及该入口的实际工作租约仍待验收。进程退出仅指专属环境清理和原事务进程的强制中断，另有候选补偿路径验证具名停止。对象释放、ALC 回收、隔离负载和生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN 提交后具名停止

此批 Helper 本地协议的 `MihomoRuntimeTunStop` 使用原发布 operationId 和标准关联字段。后续协议接收独立停止 ID 和原发布 ID，见[具名启停回执](#mb-04-受管-tun-认证-http-具名启停回执)。归档实例锁覆盖原归属核对、停止派发、清理及最终观察。重试由原网络检查点裁决。

**已实现且已验证。** Linux x64 私有 systemd 场景的原操作 `1127100c8f0f4f31a31121aea4f83552`，Mihomo PID 252，ifindex 4，候选配置摘要 `1e266605502cd646c8da3a4b7ce827a73906cc6affff26296805b98e304b1325`。夹具限定允许写入成功态，跨原发布应答丢失仍有真实 HTTP、分流 DNS 及一致的管理选路。错误 ID 在真实 Helper 入口拒绝。夹具暂停原进程，Helper 落盘 `StopDispatched` 并通过 pidfd 发送真实 SIGTERM，随后在进度边界强制终止停止 worker。原 SIGTERM 已在内核排队。原进程仍暂停时，新的真实 Helper 返回 `runtime.network_stop_unconfirmed`，没有派发规则删除，原检查点和历史回执保持。

夹具恢复原进程后，原 SIGTERM 使其退出。Helper 先落盘 `RuleRemovalDispatched`，真实规则删除后再次强制中断。同 ID 重试遇到夹具重建的同值规则时返回 `runtime.network_rule_conflict`，未重删。撤掉夹具规则后，同 ID 只读确认原规则缺席并完成停止，重复请求返回相同结果。真实规则删除派发一次。原 PID、设备、路由和本链路 DNS 均缺席，管理选路及外来链路 DNS 保持，原配置字节、历史发布成功回执和提交见证保持。

首轮及诊断轮因夹具把无连字符的日志 ID 当作协议 GUID 字符串而被入口拒绝，修正线上 GUID 格式后重跑。Windows Release 通过，包括 104 项配置事务检查。可信下载探针因缺少进程级测试 CA 覆盖跳过。

同批提交态归属原 `adb2e34709be4b68a1e38f125abdd513`、完整补偿原 `f5e456b6ce324dfd9d2bc9ad1b16e81f`、DNS 应答中断原 `0d557befe81b4d31ae4e03b6b44ed8af`、resolver 换代原 `3e142ca1935f498ba5d826cd8aae56cf` 全部通过。五个专属 cgroup 已清理，宿主服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `99d00c0ce8d1841fc39b22c8a05f5ffbe9268a7f7504d867921f549ac89bebd2`。最终日志在 `.artifacts/mb04-tun-marker-sync/committed-stop-linux-final.log` 和 `.artifacts/mb04-tun-marker-sync/committed-stop-windows.log`。

**已实现但实机未验证。** 其他 Linux 架构和持久 AF_UNIX 通道。本轮停止重试经真实 Helper 单次进程入口。

**未实现或仍受门禁保护。** 具名启动、重启、移除、公开发布、生产成功提交及公开入口工作租约仍待验收。已确认真实 Mihomo 退出和专属 cgroup 清理。对象释放、ALC 回收、隔离负载内存及生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN 提交后具名启动与重启

Helper 本地协议 `MihomoRuntimeTunStart` 与 `MihomoRuntimeTunRestart` 接收原发布 ID、新执行 ID 和当前短效授权回调。归档实例锁覆盖当前执行核对、重启停止、启动派发和回执。启动前再次核对候选配置摘要、描述符配置、宿主范围、管理基线和已安装可信制品。新执行使用自己的原生检查点及专用路由表，发布回执和提交见证继续绑定原 ID。具名停止通过原发布 ID 定位当前已确认执行。

**已实现且已验证。** 私有 systemd 场景限定夹具可提交初始发布，并通过生产生命周期处理器和真实授权回调启动与重启。宿主范围缺失时，启动在登记前拒绝。启动 worker 在真实候选就绪后、归档回执前强制中断，查询按该执行 ID 报待核验。跨中断核对管理选路、配置摘要和真实 HTTP、分流 DNS。标签漂移时同键拒绝确认，不重新启动；撤销漂移后按原执行只读确认，PID 与原生检查点保持。同键历史成功回执可重复读取。

重启 worker 在旧执行的 SIGTERM 派发后中断。原重启执行保留旧停止阶段，错误动词及其他执行 ID 被拒绝。续接原重启只对账旧停止，确认清理后派发一次新启动。新 PID 实际转发，旧 PID 已退出。原发布回执和见证保持，具名停止定位新执行。最后在新启动记录落盘后、原生证据出现前中断，后续同键保留待核验，不创建原生操作目录、不补派，新键仍受门禁约束。

最终实机原发布 ID `e5448792833442698fc3749ffd6a1da8`，配置摘要 `db5f4a33b356b2836cc918a4604950b80961780b7faca113b580efb72888617c`。启动 `d4b6b977c53f4a62bb9f27f183d19e15` 使用 PID 1030，重启 `f55562eb49d843a5a078fd97783ccf49` 使用 PID 1395，缺少原生证据的 `6f7779051ae7417cbbfb2d0304ef1012` 保持待核验。Windows Release 通过，包括 104 项配置事务检查。可信下载探针因缺少进程级测试 CA 覆盖跳过。

同批具名停止 `fbf10b2df6fc4c33a6ac120f03d5e19e`、提交态归属 `ee096044948e4de29209d34426e182d3`、完整补偿 `4be344a572a64c1aa458f3863027402c`、DNS 应答中断 `24bbd47a1977463db462e7b2c28615cc`、resolver 换代 `d90f74933bff4febac777d66465ac7e5` 均通过。六个专属 cgroup 已清理，宿主服务、代理和 resolver 文件指纹一致。最终归档 SHA-256 为 `a3c729b6e4b49b6a5629126dd39eef9d187818335a473e3f2d3772da6e3b0657`。日志为 `.artifacts/mb04-tun-marker-sync/committed-start-linux-final.log` 和 `.artifacts/mb04-tun-marker-sync/committed-start-windows-final.log`。

**已实现但实机未验证。** 其他 Linux 架构、持久 AF_UNIX 通道，以及新执行单独发生 DNS 写入应答丢失时的联合启动对账。原配置事务的 DNS 应答中断路径单独回归。

**未实现或仍受门禁保护。** 提交后移除、公开发布、生产成功提交及公开入口工作租约待验收。已确认原进程退出和专属 cgroup 清理。对象释放、ALC 回收、隔离负载内存与生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN 提交后移除

复用现有 Helper 归档移除事务及同一实例锁。只允许当前执行已证明停止和网络清理的安装进入移除。固定 systemd 单元及归档注册撤销前记录原移除派发，清理前独立记录派发。已派发阶段恢复只核对效果，不执行第二次撤销或删除。终态清除活动归属，并在原移除回执保留其发布归属。发布和启动历史继续可查，保留的原生检查点不含配置正文。

**已实现且已验证。** 两组 Linux x64 私有 systemd 均先运行真实已提交候选、执行具名启动和重启，核对真实 HTTP、分流 DNS、管理选路和原配置身份。当前执行仍在运行时，移除计划拒绝。具名停止确认后，生产归档移除处理器接续同一实例。

- 保留数据的原发布 `0ab6f17206ec4e0c9d77aa3037b1482d`、当前执行 `128196c6719d4c02a2283beaa783323c`、移除 `3147cfc37831455c8553b0715789967f`。固定注册撤销后、清理派发前中断。另一个请求 ID 用同键只读取得原待核验。夹具重建部分注册，恢复保留待核验，注册字节及制品仍在。撤掉夹具文件后，原移除确认注册缺席、派发首次清理，保留配置原字节、运行数据及其来源记录。
- 显式清除数据的原发布 `bf78314175b94b4dbf39263b0e87ac33`、当前执行 `a2f44b77f8154696bb72a87bd565b686`、移除 `95b46195d9534cc7bb1de8de64c94c07`。在清理完成、终态落盘前中断。夹具添加迟到文件，原移除保持待核验，文件未被再次删除。撤掉夹具文件后，原移除只读确认清理终态，配置及运行数据均缺席。

两组最终均按原移除 ID 返回成功及提交见证。原发布回执和提交见证、三个执行目录各九份原生检查点保持，归档活动归属释放。设备、分流 DNS 和策略路由缺席，管理选路保持。保留数据首轮的业务断言通过，退出夹具时对已移除单元多发停止导致整轮失败。修正为先核对单元存在后重跑。Windows Release 通过，包括 17 项归档移除和 104 项配置事务检查。可信下载探针因缺少进程级测试 CA 覆盖跳过。

同批具名启动与重启、具名停止、提交态归属、完整补偿、DNS 应答中断及 resolver 换代均通过。八组专属 cgroup 已清理，宿主共享服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `bdacd92a02ceaa754a2f24ab07648c90a62c571cd8ed488dfecc4b286915ff24`。最终日志为 `.artifacts/mb04-tun-marker-sync/committed-remove-linux-final.log` 和 `.artifacts/mb04-tun-marker-sync/committed-remove-windows.log`。

**已实现但实机未验证。** 持久 AF_UNIX 通道及其他 Linux 架构。初始配置成功写入限夹具，实机移除通过生产归档处理器。

**未实现或仍受门禁保护。** 生产成功提交、公开发布、同键 HTTP 和该入口实际工作租约待联合验收。已确认实际进程退出及专属 cgroup 清理。对象释放、ALC 回收、隔离负载内存及生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN Server 发布协调

**已实现且已验证。** 内部 Server 发布只接受冻结的 TUN 意图，独立核对 root 宿主策略的 `allowManagedPublication`，默认关闭。Helper 通过真实授权回调读取加密台账，并在原生成功提交前重核宿主许可。旧通用发布入口即使宿主显式许可也拒绝 TUN。Server 确认成功需要原提交见证和当前 TUN 归属均匹配。两组私有 systemd 均以真实 Proxy 常驻工作租约核对发布调用、原回执恢复及作用域结束后的租约释放。

- Helper 已提交、Server 未收应答的原操作 `68367d19d39949c6bc881936e2983637`，PID 245，配置摘要 `05301bed48eed0037e3c0e14bfea4e70e83bbcc3f50d538ddf0124062dfe477f`。真实 HTTP 和分流 DNS 跨进程中断保持，管理选路和原生检查点保持。重启前撤回宿主范围，改写设备标签时恢复拒绝确认，Helper 台账及 Server 旧配置未变。恢复标签后，Server 按原见证确认并同步候选配置。同键返回原成功，配置派发和补偿均为零。
- 候选已转发、Helper 未提交的原操作 `1489692594ae4810898ddd7287b7f3c1`，PID 248，配置摘要 `e22b2c832179755f2688ca537e73d5c35d337a18ae2136c5616a3a1484de71ef`。真实 HTTP 和分流 DNS 在重启前后仍经过原进程。撤回宿主范围后，Server 按原操作派发一次恢复，Helper 具名补偿并恢复旧配置和控制器。恢复的原管理选路保持，候选进程及设备退出，规则删除一次。同键返回原 `Interrupted`，配置派发为零。

同批保留及清除数据移除、具名启动与重启、具名停止、提交态归属、完整补偿、DNS 应答中断及 resolver 换代均通过。十组专属 cgroup 已清理，宿主共享服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `38d1f765a989f628a0c9fc3ccea100bf5b99b81f5e8944fdb10b1c26347618ba`。日志为 `.artifacts/mb04-tun-marker-sync/server-publication-linux.log` 和 `.artifacts/mb04-tun-marker-sync/server-publication-windows.log`。

**已实现但实机未验证。** 其他 Linux 架构。夹具在一个 worker 内执行生产 Server 协调器和 Helper 处理器，授权回调、归档、systemd 和网络效果均为真实。Server 与 Helper 分进程的持久 AF_UNIX 路径仍待验收。Windows Release 通过，包括 104 项配置事务检查。可信下载探针因缺少进程级测试 CA 覆盖跳过。

**未实现或仍受门禁保护。** 客户端公开发布、同键 HTTP、公开入口工作租约和提交后生命周期联合验收待完成。已确认专属进程退出和 cgroup 清理。对象释放、ALC 回收、隔离负载内存和生产内存收益未测量。Proxy 保持 resident。

## MB-04 受管 TUN AF_UNIX 发布与恢复

**已实现且已验证。** 专属 Linux x64 systemd 夹具分别启动非 root Server worker 和 root Helper。Server 通过生产 AF_UNIX 传输及真实短效授权回调调用 Helper；Socket 权限和 Helper 同行 UID 审计均核对实际非 root 调用。Server 发布协调、恢复及提交后具名启停和重启使用实际 Proxy 工作租约，确认及清理结束后租约归零。

已提交路径在 Server 收到真实 Helper 帧、交还发布协调器前中断。两个进程分别退出并重启。撤回宿主范围后，设备归属漂移阻止确认，原见证和 Server 旧配置保持。恢复标签后，按原回执确认发布，Helper 只读核对，同键不重派配置。随后使用同一通道对当前发布具名停止，持新执行 ID 启动和重启。原发布及启动回执保持，旧进程退出，新进程继续实际转发。

未提交路径在真实 DNS 写入后、Helper 记录原生确认前中断，分别替换 Server 和 Helper。真实 HTTP、分流 DNS、管理选路和配置身份跨中断核对。原操作以原 resolver 身份只读确认 DNS，继而按原 ID 补偿并恢复旧配置。三项 DNS 写入各一次，规则删除一次，新 Helper 的日志没有再次派发配置。

最终两组 Server UID 均为 65534。已提交原操作 `d966f49bbfc64fd39595a5209a2baabd` 的 Server PID 150，Helper PID 137→391，候选 PID 276，配置摘要 `92bfb3f59f09c6177abefa596d1cf94b021959940691ab1d99937afda169c0ed`。具名启动 `dbf37b36504a4600b87f82cebc7227bf`，重启 `b096224c4aeb440b96c423ed16f1e625`，新 PID 830 实际完成 HTTP 与分流 DNS。新 Helper 记录具名停止一次、启动调用两次（其中一次仅返回历史回执）和重启一次。

未提交原操作 `77888f131a28464f8fab96509f43b376` 的 Server PID 152，Helper PID 139→356，候选 PID 277，配置摘要 `3527b0deb13b1886d07906d7a73c55be438f45a6c6bcacee620c94405e4e939f`。新 Helper 记录一次原恢复和零次配置派发。

同批进程内发布与恢复、保留及清除数据移除、具名启动重启、具名停止、提交态归属、完整补偿、DNS 应答中断和 resolver 换代均通过。十二组专属 cgroup 已清理，宿主共享服务、代理和 resolver 文件指纹一致。Windows Release 回归通过，包括 104 项配置事务检查。最终归档 SHA-256 为 `47751547ae2476267b90adec4ea4a53338d7584061e0554322eb6038effba4e7`，日志为 `.artifacts/mb04-tun-marker-sync/ipc-server-linux-final.log` 和 `.artifacts/mb04-tun-marker-sync/ipc-server-windows-final.log`。

**已实现但实机未验证。** 其他 Linux 架构。Windows 可信下载探针因缺少进程级测试 CA 覆盖跳过。

该批确认专属进程退出和 cgroup 清理。客户端入口由后续[认证 HTTP 验收](#mb-04-受管-tun-认证-http)覆盖。

## MB-04 受管 TUN 认证 HTTP

**已实现且已验证。** 专属 Linux x64 私有 systemd 中，非 root Kestrel 映射生产 Proxy TUN 路由和原操作查询，使用实际 Proxy 队列、工作租约和独立 root Helper 的 AF_UNIX 通道。匿名请求返回 401，观察者返回 403。关闭宿主 `allowManagedPublication` 后，获准的控制请求终止于 `proxy.not_supported`，Helper 配置派发为零。

独立客户端网络命名空间使用实际 TCP 连接触发管理端选路。测试延迟 HTTP 受理响应使客户端断连，同键重试得到 202 和原 ID，改换档案得到 409。Helper 成功帧到达而 Server 尚未确认时观察到至少两个工作租约，随后终止并重启 Server 和 Helper。按原 ID 查询确认原发布，核对原配置身份、真实上游 HTTP、分流 DNS 和管理连接。原 Helper 配置派发仅一次，新 Helper 未再次派发。随后从同一认证入口具名停止及重新启动，验证真实进程、设备、路由及流量变化。

13 组实机通过，专属 cgroup 均已清理，宿主共享服务、代理及 resolver 文件指纹一致。该批原发布 `20df6fcf82cc4a5cab7be65154c33740`，停止队列 ID `8b9efb5144904e17bb6fad84f5d9b5ad`，重新启动 `034fe9e73ac348e8906849735157f548`，凭据摘要 `ebb03015cd9c31449360312acc01375efd69e538c00b51d31e1ab730bb5869a1`，重新启动 PID 689。归档 SHA-256 为 `020852500b4a584b8571dcb2984cc24046eafef833f17f748b6de18acc547717`，日志为 `.artifacts/mb04-tun-marker-sync/tun-http-linux-final.log`。Windows Release 通过，包括 104 项配置事务检查，日志为 `.artifacts/mb04-tun-marker-sync/tun-http-windows.log`。

首轮实机认证和提交通过，但上游探针失败。探针规则只写入了草稿，而入口加载的是已确认档案。夹具改为以同一套规则种下已确认档案，再次核对实际转发，结果通过。原失败及复测分别记录于 `.artifacts/mb04-tun-marker-sync/tun-http-linux-first.log` 和 `.artifacts/mb04-tun-marker-sync/tun-http-linux-second.log`。

最终 HTTP 单项复跑通过。实际队列终态和清理后，租约观察值归零。原发布 `d30d3451a5b24af99a323872b68c4f2a`，停止队列 ID `efd2ff92570946de87152f9dcd4a4e20`，重新启动 `fa8ffea281864bdcb2a75cac684e8a06`，重新启动 PID 686。归档 SHA-256 为 `3cd588d832a7d07674023c0446ed4796ca2b0e7b731a5be249b72d7382f16509`，日志为 `.artifacts/mb04-tun-marker-sync/tun-http-linux-lease.log`。

**已实现但实机未验证。** 其他 Linux 架构。HTTP 实机使用等效角色策略和夹具 bearer，完整安装态 JWT 登录和 Android 客户端未参与。Windows 可信下载探针因缺少进程级测试 CA 覆盖跳过。

具名启停响应丢失的原执行对账仍受门禁保护。Helper 无终态由后续[认证 HTTP 补偿](#mb-04-受管-tun-认证-http-补偿)覆盖。此批证据确认专属进程退出及清理，未测量对象释放、ALC 回收、隔离负载内存或生产收益。

## MB-04 受管 TUN 认证 HTTP 补偿

**已实现且已验证。** 在同一 Linux x64 专属 systemd 和实际认证 HTTP 队列中，客户端丢失 202 后同键得到原 ID。真实分流 DNS 写入后、Helper 尚无终态时，核对至少两个在途工作租约，随后杀掉非 root Server 和独立 root Helper。重启 Helper 前后的归档台账字节一致，候选进程和配置凭据身份、真实上游 HTTP、分流 DNS 与管理选路相符。

重启 Server 的原发布协调器调用 Helper 按原发布 ID 补偿。夹具扣留真实 AF_UNIX 恢复响应时，候选进程已退出，设备和路由回到基线，Helper 配置及 Server 镜像均为旧配置。HTTP 同键仍返回原 ID，原状态为 `Interrupted`、阶段为 `managed_tun`、完成时间为空，原恢复仍持有工作租约。新键以 `proxy.operation_interrupted` 拒绝。响应交付后，原 Helper 回执为 `Interrupted`，原队列终态为 `proxy.config_apply_failed`，同键和查询均返回原记录，控制器就绪且租约归零。DNS 三项原写入各一次、原路由规则删除一次、原生已存在检查点字节保持。重启 Helper 收到一次原恢复和零次配置派发。

原发布 `572d253dbbc643e78493bc0a3906e039`，候选 PID 317，配置身份 `cb12ff5806f05aa589c560fff201c69129965a0ef6e89e3805a0769c88543079`，恢复后为 `465677193942d700b887e28887778aadc31bb406f9a82dceb23694c86b13572b`。恢复 IPC 关联 ID 为 `a910fa82-6594-42ca-8a48-d5780ab78eb0`。已提交对照路径同时复跑通过，两组专属 cgroup 均清理，宿主共享服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `62d443705a6cc4e11ab1b8ec5713e2e375bde4e910fae20b8ca64b227dd5de8f`，日志为 `.artifacts/mb04-tun-marker-sync/tun-http-pending-linux-second.log`。首轮夹具把 IPC 关联 ID 当成原恢复目标，导致断言失败，见 `.artifacts/mb04-tun-marker-sync/tun-http-pending-linux.log`。复跑分开核对原目标、传输审计与原终态回执。

**已实现但实机未验证。** Windows Release 回归通过，包含 112 项 Server 发布与 104 项配置事务检查。可信下载探针因缺少进程级测试 CA 跳过。其他 Linux 架构、完整安装态 JWT 和 Android 未参与。回归日志为 `.artifacts/mb04-tun-marker-sync/tun-http-pending-windows.log`。

具名启停丢失响应由后续[具名启停回执](#mb-04-受管-tun-认证-http-具名启停回执)覆盖。本批确认候选进程退出及专属环境清理。对象释放、ALC 回收、隔离负载内存和生产收益未测量。

## MB-04 受管 TUN 认证 HTTP 具名启停回执

**已实现且已验证。** Linux x64 专属 systemd 中，以实际认证 Proxy HTTP 队列、非 root Server、独立 root Helper 与 AF_UNIX 验证原执行对账。停用先持久绑定停止操作、发布与目标原生执行。Helper 确认真实进程退出、设备和规则清理后写入停止回执，配置身份及历史发布回执保持。启动沿用既有持久台账，回执绑定原发布与前一执行。

两次分别在 Helper 成功响应到达、Server 尚未确认队列终态时观察在途租约并终止两个进程。重启后，同键或查询只读核对原操作同动作回执，停止、启动的归档字节及回执保持，Helper 各收到一次原动作。停止后路由恢复基线，启动前后验证真实上游 HTTP、分流 DNS、管理选路及相同配置凭据。终态和清理后租约归零。另一次在已持久写入队列阶段、尚无 Helper 回执时终止进程。重启后原操作保持 `managed_tun_action` 待核验且完成时间为空，新键被拒绝，原进程和流量保持，停止未派发。

原发布 `2673d842eb804e2bb815e43d0dc5e79f`，停止 `82e12b58a9844aafac340bcc982d2097`，启动 `4f41da4114cc434ea2accc8c9e8864df`，无回执 `8469a7e0d3ed4d7eb0f365253efe4222`。重启后的活动 PID 829，配置身份 `8735deabb9cd3aae3aafbd8b28745b66ee2812adfa3b9f8a265df09eb3ebd84c`。首轮 HTTP 实测通过，直接调用 Helper 的 Python 夹具把嵌套字段大小写写错，触发请求校验。修正后具名停止的信号与规则删除断点复测通过。首轮日志为 `.artifacts/mb04-tun-marker-sync/tun-http-action-linux.log`，复测日志为 `.artifacts/mb04-tun-marker-sync/tun-http-action-linux-second.log`。

最终七组私有 systemd 场景通过，包含具名启停、发布补偿、原生停止中断、原生启动及重启、保留和清除数据移除，以及独立 IPC 发布与生命周期。七组 cgroup 均已清理，宿主服务、代理和 resolver 文件指纹一致。归档 SHA-256 为 `971047080de6e39efad8a9dae2d2e4f4f78c913e4d28bc013faad879822bcec3`。

**已实现但实机未验证。** 该批未覆盖其他 Linux 架构、完整 Server JWT、同次停机和 Android。完整 Server JWT 由后续[受管 TUN 停机排空](#mb-04-完整-server-的受管-tun-停机排空)覆盖。Windows Release 回归通过，可信下载探针因缺进程级测试 CA 跳过。日志为 `.artifacts/mb04-tun-marker-sync/tun-http-action-windows-final.log`。

**未实现或仍受门禁保护。** 缺失或未确认的原生启停回执继续阻止新键。整模块交接前 Proxy 保持 resident。本批核对专属进程退出及环境清理。对象释放、ALC 回收、隔离负载内存和生产收益未测量。

## MB-04 完整 Server 的受管 TUN 停机排空

**已实现且已验证。** Linux x64 私有 systemd 中运行生产 Server 程序、非 root Server 进程和独立 root Helper。登录账户只存在于私有 `/etc` overlay。独立客户端网络命名空间走生产 PAM 登录获取 JWT，匿名和无效 JWT 被拒绝。认证入口读取生产 SQLite 中的活动档案和保护后的控制器凭据，完成真实受管 TUN 发布。核对管理选路、配置身份、上游 HTTP 和分流 DNS。

专属 AF_UNIX relay 仅扣留原停止成功响应。Helper 已持久写入原停止回执，候选进程退出、设备移除、路由回到基线，配置身份保持。HTTP 队列仍为 Running，生产模块诊断报告至少一个工作租约。发送 SIGTERM 后，Server 已进入停机且原队列字节保持。释放响应后，原终态和一次成功审计持久化，Server 退出码为零。生产队列的执行路径在写入终态前等待作用域清理。此用例没有单独阻塞清理回调。

重启后再次用 PAM 登录，同键返回原停止 ID 和原终态，队列台账字节及 Helper 停止回执一致，租约归零。再次启用沿用原发布和配置，新进程真实转发 HTTP 与分流 DNS，管理选路一致。Helper 审计中原配置和停止各派发一次，原规则删除一次。

首次完整用例通过前有两处夹具问题。制品缺 Server 的 `.deps.json` 导致加载失败，补齐清单并加入运行前检查。远端客户端查询仅对本机开放的 `/healthz` 返回 404，改为以受保护 TUN 路由返回 401 作为就绪条件。失败日志为 `.artifacts/mb04-tun-marker-sync/tun-full-linux-first.log` 和 `.artifacts/mb04-tun-marker-sync/tun-full-linux-second.log`。

最终原发布 `2d19f0aca80343a88f407dc34b9cd455`，停止 `5e57b294c4c6452e8a4944a86d5d2425`，再次启用 `af18c5dbbbda4265961b7d8a9c69a34a`。Server PID 180→690，重新启用后的 Mihomo PID 888，配置身份 `48688ff3698c68f4a70a47370fc18938a0f5dba8b76e4d9da371a9a53e41837f`。同批具名启停响应丢失和发布补偿两组通过。三组 cgroup 已清理，宿主运行时、代理、resolver 指纹一致，完整 Server 用例另核对宿主账户文件指纹一致。归档 SHA-256 为 `2c1338dffda26c7284cd79ac6bf353dee8ee5e6be0c9e3d46ebc68efee223224`，日志为 `.artifacts/mb04-tun-marker-sync/tun-full-linux-final.log`。

**已实现但实机未验证。** 其他 Linux 架构、Android 和正式安装服务包。认证与停机均走生产代码，试验由私有 systemd 夹具启动 Server。Windows Release 回归通过，包含 104 项配置事务检查。可信下载探针因缺进程级测试 CA 跳过，日志为 `.artifacts/mb04-tun-marker-sync/tun-full-windows-final.log`。

受管 TUN 与系统代理 guard、授权到期由后续[同次停机](#mb-04-受管-tun-与-proxy-安全责任同次停机)覆盖。本批确认专属 Server、原 Mihomo 进程退出及环境清理。对象释放、ALC 回收、隔离内存和生产收益未测量。

## MB-04 受管 TUN 与 Proxy 安全责任同次停机

**已实现且已验证。** Linux x64 私有 systemd 的完整非 root Server 使用生产 PAM/JWT、独立 root Helper 和三个可分别释放响应的 AF_UNIX relay。使用两条真实 TLS VLESS 授权，先验证即将撤回的授权可转发，再以已到期状态启动生产到期 worker。原 Helper 提交后扣留到期响应，核对原回执和 Server 待确认状态。旧连接关闭、到期授权拒绝、保留授权继续转发。

专属系统代理文件被竞争写入后，真实 guard 恢复受管代理值并写入原归属台账，响应被扣留。认证 TUN 停用在同一发布锁前等待，原队列为 Running 且至少持有一个工作租约，原 Mihomo 仍通过管理选路、实际 HTTP 和分流 DNS 检查。发送 SIGTERM 后先释放独立到期响应，确认原发布台账前进，Server 仍在排空，guard 和 TUN 责任继续保留。释放 guard 响应后，Helper 才收到具名停用并持久写入停止回执。确认进程退出、设备移除、路由回到基线和配置身份一致后，才释放停止响应，原队列及成功审计落盘，Server 以零退出。

重启后，同键返回原停止回执，租约归零。guard 环境文件、归属台账字节及修改时间一致。sing-box PID、配置身份和原到期回执一致，撤回授权继续拒绝且保留授权转发。再次启用恢复原配置、管理选路和实际流量。Helper 审计分别记录一次原到期、一次原 guard 纠偏、一次原 TUN 配置和一次原具名停用，原路由删除一次。

最终 Mihomo 原发布 `f16b3741b8da4cf8b23f388b9013f375`、停用 `b35f6711388e412fbdbc75890dbfbd53`、再次启用 `1ad2b42eaee246e39edff30bc0835253`，配置身份 `c2586a9389770fde2c37cb31b5176cacfccbe46000bf511fc2f8e49308c95286`。guard 关联 ID `73fbed86284944f4b8959e717ed29e67`，到期原 ID `9e20acb2da2d4a90ba97ef745ba1df1d`，sing-box PID 463，配置摘要 `a32f81f9822fa6f54804f6f1b249b393810ca8b5d2cc587bfd6df0b8b579a4b5`。首轮和最终复跑均通过，最终同批还通过单独 SIGTERM 和具名启停丢失响应。归档 SHA-256 为 `a573e3cd62e036a31d3e42d2a8afbaf503da8967e4588f96a948782fd7976878`，日志为 `.artifacts/mb04-tun-marker-sync/tun-joint-linux-first.log` 和 `.artifacts/mb04-tun-marker-sync/tun-joint-linux-final.log`。三组专属 cgroup 清理通过，宿主服务、系统代理、resolver 及完整 Server 场景的账户文件指纹一致。

**已实现但实机未验证。** 其他 Linux 架构及桌面会话代理后端未参与。Windows Release 回归通过，包括 104 项配置事务检查。进程级测试 CA 缺失，可信下载探针跳过。日志为 `.artifacts/mb04-tun-marker-sync/tun-joint-windows.log`。

管理模块停用及禁用启动由后续[模块停用门禁](#mb-04-受管-tun-模块停用门禁)覆盖。本批核对原 Mihomo 与 Server 的实际退出及 cgroup 清理。对象释放、ALC 回收、隔离内存和生产收益未测量。

## MB-04 受管 TUN 模块停用门禁

**已实现且已验证。** 在 Linux x64 私有 systemd 中，完整非 root Server 使用生产 PAM/JWT 和独立 root Helper，发布实际受管 TUN。生产模块停用计划在可核验状态通过。Helper 停止后，新计划包含 `module-recovery-blocked`，应用先前可用的计划也返回 409。Helper 恢复后再次得到可用计划。改变原 TUN 设备归属标签后，停用计划再次阻断。失联和漂移期间均核对同一 Mihomo 进程、配置身份、管理选路、真实上游 HTTP 和分流 DNS。

归属漂移及 Helper 失联分别使禁用启动在监听前拒绝，诊断包含原发布 ID。恢复 Helper 和原标签后，禁用启动成功，管理 TUN 路由返回 404，原进程及实际流量继续，Server 队列、Helper 台账及配置镜像字节一致。随后独立 root 操作在具名停用信号已派发时中断，Server 队列没有这笔操作。停用计划及禁用启动均报告原停止 ID，读取后原台账字节不变。恢复该进程后，原 Helper 路径用同一停止 ID 确认清理，规则删除一次，Server 未派发停止。已确认的停止态再次通过禁用启动。

最终发布 `f526f4997539432988b1027afa23cc44`，仅 Helper 持有的原停止 `4a449026-99c4-48b0-8c7d-e8cecc92c247`，原 Mihomo PID 393，配置身份 `cee974ee2eab04b0a9528aefc51e2eb97a6dd1ddad049f065e4cb3ce0d52ebdd`。首轮已产生门禁记录，模块清单未将其标为待核验，修正后同时核对计划、应用和清单。日志为 `.artifacts/mb04-tun-marker-sync/tun-admission-linux-first.log`、`.artifacts/mb04-tun-marker-sync/tun-admission-linux-second.log` 和 `.artifacts/mb04-tun-marker-sync/tun-admission-linux-final.log`。

最终四组 Linux 通过，覆盖停用门禁、同次停机、独立停机及原生保留数据移除。归档 SHA-256 为 `40d54216383cfe23175ea47aff7d316ecb4cdae195cce339799bacf4a9859fd4`。四组专属 cgroup 清理通过，宿主运行时、代理和 resolver 指纹一致，完整 Server 场景的账户文件指纹一致。

**已实现但实机未验证。** 保留数据移除的禁用启动分支要求原移除见证和当前确证缺席。原生保留数据移除单项已覆盖，未和禁用启动联验。其他 Linux 架构未参与。Windows Release 回归通过，含 104 项配置事务，可信下载探针因缺进程级测试 CA 跳过。日志为 `.artifacts/mb04-tun-marker-sync/tun-admission-windows.log`。

管理入口禁用时的联合验收见[禁用态安全交接](#mb-04-禁用-proxy-管理入口后的安全交接)。本批已核对专属 Server 和原 Mihomo 进程退出及 cgroup 清理。对象释放、ALC 回收、隔离负载内存及生产收益未测量。

## MB-04 禁用 Proxy 管理入口后的安全交接

**已实现且已验证。** Linux x64 私有 systemd 中，完整非 root Server 通过生产 PAM/JWT 发布受管 TUN，然后通过认证模块计划及应用接口提交 Disable。原管理操作为 `AwaitingRestart`，重启后为 `Succeeded`。后续 Server 两次启动均以管理台账为准，Proxy 有效状态和期望状态均为禁用，清单无 Proxy 工作代次，管理 TUN 路由返回 404。原 Mihomo PID、配置身份、管理选路、上游 HTTP 与分流 DNS 在整个期间一致。

在禁用态 Server 已监听后，先验证真实 TLS VLESS 到期凭据仍能转发。专属代理环境文件被竞争修改，Core guard 按原归属纠偏，真实 Helper 成功响应暂扣。随后生产定时器在授权到期后派发 sing-box 的原发布，扣留该响应时核对 Helper 已提交回执及 Server 待确认台账。到期授权的旧连接关闭，新连接拒绝，保留授权继续转发。

两笔响应均在途时向禁用态 Server 发送 SIGTERM。先放行到期响应，Server 原台账确认，进程仍等待 guard。其间再次核对原 TUN 路由和流量。释放 guard 后正常退出，再次按相同台账禁用启动，并按原停用 ID 查询成功回执。sing-box 到期回执和 PID、guard 文件及归属台账字节和修改时间、Mihomo 台账、原生检查点、配置镜像及队列字节均保持。Helper 审计各有一次原发布、原 guard 纠偏和原到期，未派发 TUN 停止或补偿。

最终原 TUN 发布 `522ac35ee4d145f195e4b1ec38f07e75`，原模块停用 `7530ef4d-6e1e-426e-8cb9-808dfea0cc18`，guard 关联 ID `ffb8bf0b68d340f9bfa2b7a4907857b1`，原到期 `49f1e994e0e549288760fba8926331cc`。原 Mihomo PID 393，配置身份 `d318627d69eeb32385ddc69895b8b95a55174af706063f0d9ab793b9c399e263`。sing-box PID 1095，配置身份 `2b861e16230399be4aee0844821771e244c5da3dfb6770de3c94f8bf0d523784`。禁用态 Server PID 810→1252。

首轮和最终禁用态用例通过，同批再验收同次停机及停用门禁。归档 SHA-256 为 `5086a82094e52b8afe9987bb8eb29599b9415d47647a0c530c58964fb68bca2a`。日志为 `.artifacts/mb04-tun-marker-sync/tun-handoff-linux-first.log` 和 `.artifacts/mb04-tun-marker-sync/tun-handoff-linux-final.log`。三组专属 cgroup 清理通过，宿主服务、系统代理、resolver 及账户文件指纹一致。Windows Release 回归通过，含 104 项配置事务检查，可信下载探针因缺进程级测试 CA 跳过。日志为 `.artifacts/mb04-tun-marker-sync/tun-handoff-windows.log`。

**已实现但实机未验证。** 该验收覆盖配置及重启下的管理停用。其他 Linux 架构及桌面代理后端未参与。

**未实现或仍受门禁保护。** 保留数据移除的原见证与禁用启动仍需联验。动态驻留仍受整模块安全交接与资源收益验收门槛保护，Proxy 继续 resident。已验证专属 Server 退出和 cgroup 清理。对象释放、ALC 回收、隔离内存与生产收益未测量。

## MB-04 实际 TUN 激活期间取消与同进程补偿

Linux x64 Release 在私有网络、挂载和 PID 命名空间中等待实际 Mihomo 接受 reload。设备及策略规则已经创建，生产 TUN 安全任务仍等待响应。测试驱动在响应交付处触发取消，并暂时移除专属默认路由，原任务以取消退出，保留标记 `777a7afa-2958-421d-81cf-c98dadc34745`。管理路由恢复后，原 Core worker 在 Proxy 管理图缺席时接手，同一 Mihomo 的激活和禁用各 reload 一次，设备和规则回到基线。测试屏障仅用于控制取消时点。Windows Release 模块里程碑回归通过，Docker 跨进程原回执保持 Interrupted，副作用一次。Linux 宿主共享服务 PID 和代理文件指纹一致。日志为 .artifacts/mb04-tun-cancel-live.log，归档 SHA-256 为 15ab51cafa0b3fdd7ce9efc1e24118636dc5dc2e350a1aba3417477ded3c5589。

此项验证现有直接 TUN 安全任务取消及 Core 接续。完整 HTTP 已受理任务仍由原队列持有，不随 HTTP 断开取消。受管 TUN 继续开放。当前 Linux Mihomo 受管单元隔离设备且能力集仅含 CAP_NET_BIND_SERVICE，控制器配置入口本身不提供受管的设备、路由和补偿权威。

## MB-04 入站授权到期的停机排空

Core 继续使用原受保护台账和摘要绑定的到期索引，只有到期或存在待确认候选才创建执行作用域。入站发布写入候选后，原 Helper 调用及终态提交继续执行，调用方取消不截断响应。到期 worker 停止接收新扫描并等待实际任务与异步作用域释放，停机调用者的等待期限不充当清理完成证明。管理队列继续使用原操作租约，Core 到期执行不依赖管理模块启用。

Linux x64 Release 在私有 systemd、生产 Helper 和真实 sing-box 上通过。原 Helper apply `d14b9cfa-b26d-42ed-ac99-01a91ff64514` 生效后，夹具暂缓向 Core 交付实际响应。用已取消的停机令牌调用 worker Stop，原发布仍持有领域锁和持久候选。交付响应后确认原终态已落盘，Stop 继续等待异步作用域清理。此次实际配置修订仅增加一次，保留授权完成真实转发。同批用另一次到期 `207e2563-c2ed-422c-9b04-77fc03bd4f80` 复验跨进程恢复。Helper 不可达时完整 Server 保留原候选，重启 Helper 后按原摘要确认，后续授权自然到期并拒绝真实认证。系统代理、TUN 和保留授权接续通过，宿主状态与文件指纹一致，专属 cgroup 已清理。

Linux 日志为 `.artifacts/mb04-expiry-drain-live.log`，归档 SHA-256 为 `9aaa2a7a57f4dc4fb1d7ab013d28ff5a5f44da0923f8486fa1b9c30858e37751`。Windows Release 独立 Proxy Server 与模块边界回归通过，日志为 `.artifacts/mb04-expiry-drain-proxy-win.log`、`.artifacts/mb04-expiry-drain-milestones-win.log`。本次停机直接驱动生产 worker，响应交付与作用域清理屏障由夹具注入，完整 Server 的 SIGTERM 同时命中这两个边界仍待实测。

原入站台账仍是恢复权威。此阶段没有为 Core 到期发布新增独立 Helper 终态回执。候选摘要可用于确认已提交的配置，尚缺原配置摘要仍存在时区分未派发和已补偿的原操作证据。Proxy 保持 resident。

## MB-04 Core TUN 恢复的停机排空

Core TUN worker 停止调度后仍等待已接受的恢复任务。接受前尊重取消。原标记写入未完成状态并发出实际禁用后，控制器响应、管理路由验证和原标记清理在同一次执行内完成。等待方的停机期限不会归还责任。无法确认禁用或路由时，原标记仍是恢复权威。

Linux x64 Release 私有网络命名空间使用真实 Mihomo。夹具暂扣实际禁用响应，并用已取消的等待令牌调用生产 worker 的 Stop。原 Task 和标记均保持活跃，交付响应后恢复基线路由与规则、移除设备并结清标记 `af65c4e5-5881-470f-a6af-6739756adec5`。实际禁用 reload 一次，Mihomo 继续运行。同批复验发布门禁、响应丢失、DNS 劫持、路由故障、旧恢复引用与激活取消。宿主服务 PID 和代理文件指纹一致。日志为 `.artifacts/mb04-tun-drain-final-live.log`，归档 SHA-256 为 `d714219fad46c5866967d4d2a577af5380ef7990fc4a1474ca00c3a85f2fefe3`。首轮新增用例通过，旧累计计数断言因统计范围扩大失败，调整断言位置后全组通过。

Windows Release TUN 34 项、Core 安全 37 项、驻留 28 项、工作作用域 31 项和模块边界回归通过。停机专项直接驱动生产 worker。完整 Server 的 SIGTERM 联验、其他 Core 安全 worker 的停机交接和 Proxy 动态休眠仍开放。

## MB-04 Core 系统代理 guard 的停机排空

Core guard 停止调度后等待实际对账任务与执行作用域。对账取得原发布锁并重新核验设置后，派发前仍可取消。已派发的系统代理写入使用原发布锁等待 Helper 返回。Helper 的环境所有权日志继续负责中断恢复和外部修改保护。

Linux x64 Release 在私有挂载和网络命名空间使用非 root Server 身份与独立生产 Helper Unix socket。先核验未变化的 guard 扫描不改写所有权日志，再注入专属环境值冲突。Helper 实际纠正冲突后，测试传输暂扣交付给 guard 的结果。用已取消的等待令牌调用生产 guard 的 Stop，任务与原发布锁保持活跃，私有环境和原恢复日志可核验。交付结果后同一任务释放锁，未追加 apply。随后停用恢复保留了更新的外部设置。真实 Helper 纠正请求为 `72564240-2c8f-468c-9a99-b17447eabfbc`，日志为 `.artifacts/mb04-system-proxy-drain-live.log`，归档 SHA-256 为 `b78e7956222ade99b316dd85aab1f067cf2f52b149b542a5c4a58ffa9467a83b`。宿主环境和恢复日志指纹一致。

Windows Release 系统代理、Core Proxy 安全与模块边界回归均通过。此次故障屏障在 guard 的传输边界，覆盖停机等待取消。完整 Server 的 SIGTERM、真实 IPC 断流时的联合排空、Windows 原生代理环境和 Proxy 动态休眠仍待验收。

## MB-04 完整 Server 的系统代理 guard 停机排空

验收使用私有 Linux systemd、网络和可写 `/etc` 覆盖层。将专属代理环境改为竞争值，再启动停用 Proxy 管理面的非 root Server，由 Core guard 调用原 Helper 修正漂移。响应中继截留真实成功回执。夹具同时保持一条已到源站的真实代理连接，并以代理文件与原归属日志的内容、写入时间区分重启核对与再次写入。

Windows Release 系统代理和模块边界回归通过。MB-05 跨进程夹具按原 `06a4b7dc-2bdd-43bd-9dc8-74a766317f17` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-guard-sigterm-proxy-win.log` 和 `.artifacts/mb04-guard-sigterm-boundaries-win.log`。

真实 Helper 原操作 `8b4ae9fc-604f-45c0-b75a-9c8241e5bcd5` 在回复前已修正漂移，保留原归属。SIGTERM 后 Server 关闭监听并继续等待；放行原回执后正常退出。重启的启动核对使用新 ID `8af1379b-3f50-4508-a4b5-abb9f7192626`，原代理文件和归属日志内容、写入时间一致。Mihomo PID、原入站修订均保持，源站响应通过最初的连接交付。测试退出显式恢复私有环境，专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-guard-sigterm-auth-live.log`，归档 SHA-256 为 `cce50caac87aae2084f223915f708fa8ff7b003d65d97fcd5b8c382eace6bce1`。

共用停机测试工具的受管 Mihomo 套件复验通过 177 项，原发布排空回执为 `1fabc0ea-f689-452f-885e-97b6534241fd`。日志为 `.artifacts/mb04-guard-sigterm-native-live.log`，归档 SHA-256 为 `027cb2e33dd22efb95086a6e6a69874a836f40a24594e23ad4ba56deb966318e`。本轮证据限 Linux 原 guard 与发布恢复。Windows 原生停机及整模块休眠仍开放。下一项验证真实 TUN 已撤销但原控制器响应尚未返回时，完整 Server 是否保留原恢复标记直至排空。

## MB-04 完整 Server 的真实 TUN 停机排空

私有 Linux systemd 和网络内，测试工作进程在真实激活后、完成标记提交前终止。停用 Proxy 管理面的完整非 root Server 使用原 Core worker 读取同一标记。回环中继将真实 reload 和后续读取转发至原 Mihomo，截留显示 TUN 已禁用的真实控制器响应。夹具在发送 SIGTERM 前核验设备、IPv4/IPv6 规则、管理路由和原标记。

Windows Release TUN 边界 34 项及模块边界通过。MB-05 跨进程夹具按原 `41612b18-54ab-412e-a743-3f7c0ba8ef09` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-tun-sigterm-win.log` 与 `.artifacts/mb04-tun-sigterm-boundaries-win.log`。

原 TUN 操作 `44a0b476-2e7f-4dd5-98f8-3a55504a7ab9` 在设备已撤销、管理路由及策略规则恢复后仍保留标记。SIGTERM 关闭监听，Server 持续等待原禁用确认。放行响应后，Core 记录原操作，完成标记清理，Server 正常退出。重启后确认没有第二次 reload，Mihomo PID、原入站修订及已到达源站的代理连接保持，原连接收到响应。

同批系统代理 guard 排空原 `06219350-801c-44e7-a8ba-c0d5f084b691`、入站发布恢复 `9f5c1791-a665-448f-9c74-b76ef2fc8cad` 及补偿 `a3adbc1e-2a5e-47d1-a08d-a7a6c56cc4b9` 通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。日志为 `.artifacts/mb04-tun-sigterm-live.log`，归档 SHA-256 为 `7735b1b31fdda665e473272707268afc25eee1fdbae60c59869eee79da9d48aa`。本次验收使用独立直连 Mihomo 配置。已确认受管发布的 TUN 事务、Windows 原生停机及整模块休眠仍开放。下一项用完整 Server 联验入站授权到期在真实 Helper 提交后、原回执仍在途时的 SIGTERM。

## MB-04 完整 Server 的入站授权到期停机排空

私有 Linux systemd 中，先使用已发布授权建立真实 VLESS TLS 连接，再为该授权设置临近的到期时间。停用 Proxy 管理面的完整非 root Server 通过原 Core 持久索引发现到期，真实 Helper 提交撤销。回环中继截留原成功响应，夹具从 Helper 另行读取同一 operationId 的原生回执，同时核对原应用候选和修订。撤销后检查原连接关闭、到期授权被拒绝、保留授权可转发。

Windows Release Core 安全与模块边界回归通过。MB-05 跨进程夹具按原 `7f407d28-1991-4966-89de-df88ce2ea2e3` 读回 Interrupted，副作用计数为一。日志为 `.artifacts/mb04-expiry-sigterm-core-win.log` 与 `.artifacts/mb04-expiry-sigterm-boundaries-win.log`。

真实原回执 `b2e22e1a-55e5-45cc-b859-5a38168a0644` 为 Committed。SIGTERM 后 Server 停止监听，仍持有同一待处理操作；保留授权继续转发，原生 PID 仍是撤销提交后的进程。放行原响应后，Server 持久化已发布结果并正常退出。重启核对索引无待处理或到期工作，仍从原 Helper 按 ID 读到完全一致的回执。到期授权继续被拒绝，保留授权可重连，原生修订仅增加一次，Helper 日志确认这一阶段仅一次 apply。

同批系统代理 guard 和 TUN 停机、入站发布原恢复及补偿路径通过。专属 cgroup 清理完成，宿主服务和代理文件指纹一致。首轮认证 runner 在 150 秒上限到达后未产生终态；延长认证窗口至 210 秒后全套通过，仍要求实际结果与全部标记。失败日志为 `.artifacts/mb04-expiry-sigterm-live.log`，通过日志为 `.artifacts/mb04-expiry-sigterm-final-live.log`，归档 SHA-256 为 `ede13aa743ab9f232db9f10d08c3bb4c005b499a6509d4cfd1b97ca0e6637a67`。

四条 Core 安全路径已有 Linux 完整 Server 停机证据。Proxy 仍 resident，Windows 原生与动态休眠保持开放。下一项核对管理队列的准入、真实收尾和停机期限之间的所有权，保证已受理工作能在原台账完成终态。

## MB-04 同次停机的 Proxy 安全责任与恢复

完整非 root Server 停用 Proxy 管理面，在私有 Linux systemd、网络及宿主代理文件覆盖层中运行。原 Core TUN worker 恢复真实未完成标记，控制器已禁用 TUN 并恢复管理路由和策略规则，真实确认响应暂扣。原到期 worker 同时经 Helper 提交 sing-box 撤销，原回执另按 operationId 读取，主响应暂扣。撤销关闭到期连接并拒绝该凭据，保留授权仍可通过真实 TLS 转发。

在 TUN 持有共享发布锁时写入专属系统代理的竞争值，再发送 SIGTERM。Server 停止监听。先放行到期响应并核对原应用终态、原生回执和调度索引，Server 仍存活并保留原 TUN 标记，guard 未覆盖竞争值。随后放行真实 TUN 确认，原 worker 完成清理。重启使用原系统代理归属记录。若原 guard 已在释放发布锁后提交修复，重启仅核对文件和日志指纹；若竞争值仍在，由启动 guard 修复。到期撤销和 TUN reload 各一次，原 Mihomo 连接贯穿停机与重启。

最终实机复验的到期原回执为 `06a3af0b-905c-4976-a586-881df5da8272`，TUN 原标记为 `5712993f-0771-459e-8eb6-92dc4b41e35f`。guard 在重启后以 `714b8016-a916-4562-a85f-f93957e09900` 修正测试漂移，原归属保留。最终制品未观测到 guard 在重启前提交的交错顺序。同批原入站恢复 `d4f8e6b3-01a1-4146-910e-2941865f5424`、原补偿 `60285cab-6dc6-4aa9-90f3-884fe5a68612` 与管理队列停机 `b359ee89-caf5-4b80-a565-28b77504e09c` 通过，均核对原回执与派发次数。专属 runtime 注册及 cgroup 已清理，宿主 runtime 服务和代理文件指纹一致。

首轮联合夹具在准备非 root Server 后又写入私有台账，Server 启动退出。调整为先写台账再赋予专属状态目录和文件的非 root 所有权，随后两轮实机通过。日志在 `.artifacts/mb04-joint-safety/`，最终 Linux 归档 SHA-256 为 `b81cedb11c2a4ca891da7a80b9c175572954d44fe2ae1d511b6d95c46598c4fc`。Windows Release Core 安全 37 项、TUN 边界 34 项、Proxy Server 和模块边界回归通过，MB-05 跨进程原 `b091593e-78b6-4316-a626-632defedf9e0` 返回 Interrupted，受控副作用计数为一。

联合实测使用独立直连 Mihomo 的 TUN，受管发布下的 TUN 网络事务、整个 Proxy 的动态休眠和生产内存继续开放。下一项核对设置写入的工作租约、真实响应和清理边界，以及现有原发布回执在 HTTP 断线后的恢复。

## MB-04 Linux 系统代理原生回执

Linux Helper 的具名代理写入仍在原环境互斥锁内使用已有归属事务和后端。在写入前以原 ID 持久记录请求摘要。完成原生写入和回读后，再保存目标范围、代理键和归属台账的效果摘要。启用核对当前代理键及其归属，禁用核对归属已清理并保留实际恢复结果，包括原事务保留的外部值。回执目录由 Helper 持有，查询只返回原 ID、历史提交和当前匹配两个独立状态。

查询未知 ID 返回空。未确认的已受理 ID 继续待核对。同 ID 改变请求返回冲突。同 ID 重复写入只核对回执及现状，已出现独立修改时返回历史已提交与当前不匹配。查询和重复请求均不写代理键。连续系统代理 guard 仍使用原安全事务和所有权记录。Server 直连设置尚待将策略变化接入具名 Helper 操作和本地提交门禁，端到端恢复暂未交付。

Windows Release 的系统代理专项、ManagedRuntimes 和模块边界回归通过。受控 GNOME 和 KDE 后端核对原 ID 去重、独立修改和禁用恢复。MB-05 跨进程原 `96dae2e0-c3cb-497d-aca1-dc6b72854590` 返回 Interrupted，受控副作用计数为一。设置广跑在未改动的 UI 硬编码颜色检查处停止，可信下载探针缺少进程级测试 CA 覆盖。日志在 `.artifacts/mb04-linux-host-receipt/`。

同一 Linux 测试机以私有挂载和网络启动真实 Helper。非 root 客户端派发原 `630a7065-c07e-4f8b-97c3-464c047397c8` 后断开套接字，真实环境代理改到专属端口。强制终止并重启独立 Helper 后，原回执可读。同 ID 重复派发后，环境文件字节、修改时间及归属台账字节保持。改变请求被拒绝，未知 ID 为空。独立修改后，原回执仍确认历史提交，当前效果报告不匹配。专属禁用 `056b5f58-2379-4d46-a635-1394dbde2945` 保留外部代理值并留下回执。共享环境文件和归属台账指纹一致。实机覆盖 Linux 环境后端。首轮脚本启动参数错误，随后实机发现通用进程内缓存拦截原 ID，修正由持久台账承担这两个动词的防重放并通过 AF_UNIX 回归。归档 SHA-256 为 `4aa881369dc15b60976ecbab55b78cdad7a40516421067903bb7ab4a3d38a5bf`。

## MB-04 Linux 直连宿主策略变更恢复

Linux 直连设置校验代理策略后，持有原发布锁执行控制器 reload。收到控制器确认后，先持久化冻结设置与原 ID，再以该 ID 派发一次 Helper 变更。宿主应答未知时保留候选文件和待处理意图。具名恢复、Core 启动和 guard 共用原发布锁，核对候选文件、原 Helper 回执及当前效果。匹配才提交本地设置并保存原 ID 回执。恢复只查询 Helper，控制器结果缺失和外部文件改写继续待处理。

guard 在自己需要执行安全策略时先尝试原回执核对。原效果仍无法确认时，guard 在接管前持久记录该事实，随后独立执行已保存的安全策略。后续即使宿主值再次与候选相同，原操作也不会据此结案。环境后端继续使用原有的同值跳过规则，GNOME 和 KDE 仍按原流程维护桌面通知。TUN 安全恢复仍使用原标记。

Windows Release 的 Linux 代理协议夹具、系统代理与 guard、TUN 边界、Core 安全、ManagedRuntimes 和模块边界通过。非法 Linux 旁路值在写入检查点前拒绝。受控原效果未知时，独立 guard 写入只执行一次，之后的同 ID 重试仍待恢复。MB-05 跨进程原 `deefdc2e-b7a3-41fc-8571-00728d93a5f7` 返回 Interrupted，受控副作用计数为一。缺少进程级测试 CA 覆盖，可信下载探针跳过。

同一 Linux 测试机私有 systemd 环境两轮通过。最终制品以真实 Mihomo 和 Helper 执行旁路变化，Helper 完成后在本地提交前暂扣应答并强制终止写入进程。原 `36d5198c-1fc7-4931-9813-42939eb0cb47` 在 guard 接续时只查询一次并提交设置，环境文件字节与时间戳保持，额外 guard 写入为零。独立修改 `no_proxy` 的原 `5a0d6688-929d-4575-9e39-e84971906890` 持续返回 `RecoveryRequired`，guard 以独立调用执行一次已保存策略。两条路径各一次候选 Mihomo reload、一次原 Helper 变更，原运行时 PID 和跨中断转发保持。专属 cgroup 已清理，共享服务和代理文件指纹一致。最终归档 SHA-256 为 `970fe49854efaa7488f76a99c0f0af5b835d62707eaf72a74f297c73711cfedc`，日志在 `.artifacts/mb04-tracked-settings-final/`。

本阶段覆盖 Linux 环境代理旁路策略变化。监听端口变化时，guard 独立执行旧策略之后的转发连续性与显式补偿仍待验收。Windows 仍使用现有宿主路径。Proxy 保持 resident，动态休眠与生产内存未验收。

## MB-04 Linux 宿主代理切换验收矩阵

在同一私有 Mihomo 与 Helper 环境中，将原回执恢复矩阵扩展为旁路、启用、禁用和监听端口变化。每个场景先收到真实 Helper 成功并暂扣响应，再终止写入进程。重启路径按原 ID 核对真实宿主效果和本地文件。持续的旧连接与新建连接分别检查，端口漂移回退还检查 guard 写入的目标和实际监听状态。夹具恢复原策略及运行配置后再结束。

Windows Release ManagedRuntimes 广跑和模块边界回归通过。MB-05 跨进程原 `aee02c9f-e638-45ac-8000-245e576d1090` 返回 Interrupted，受控副作用计数为一。进程级测试 CA 覆盖缺失，可信下载探针跳过。

隔离 Linux 两轮通过六场景矩阵。最终轮的禁用 `58ea48ff-30a0-49d2-bf07-ecfe0186f314`、启用 `75c22ff5-ebee-469b-bb61-55052acdcf79`、监听端口切换 `c10ab973-0a85-4252-96bf-d5d54ed61a8e` 均按原 ID 返回成功。每次只有一次原 Helper 变更和一次候选 reload，恢复中无额外宿主写入。禁用后实际归属台账移除且 Mihomo 仍接收新连接，启用后宿主指向真实代理端口。端口切换后，新端口完成真实 HTTP 转发，旧端口拒绝新 TCP 连接。

最终轮旁路漂移 `d711ec92-94df-4d09-9136-e11686e1f90e` 和端口漂移 `a24374ec-2bf1-438e-a5f2-6da9ed256a03` 都保留 `RecoveryRequired`，各有一次独立 guard 操作和零原变更重放。跨场景既有连接完成转发，专属资源已清理，宿主服务和代理文件指纹一致。最终归档 SHA-256 为 `4de199b81f2859dbc20d45620f6192f799b63bb1da32d5c8890e32926b2ce127`。日志在 `.artifacts/mb04-host-policy-matrix-final/`。

监听端口漂移路径已在 Linux 复现。guard 按保存策略把宿主环境指回旧端口后，真实 Mihomo 仍在候选端口转发，新连旧端口被明确拒绝。原 ID 保持 `RecoveryRequired`。既有连接是否继续传输单独核对，不能作为新连接可用的证据。显式补偿仍需单独操作 ID，核对原持久策略、监听和宿主效果后才能结清。Proxy 保持 resident，动态休眠与生产内存未验收。

## MB-04 系统代理 guard 监听切换

原宿主策略变更已由 Helper 持久确认，候选只改变端口及可选日志级别，且认证控制器和 SOCKS 协商都确认候选端口在服务时，guard 可以在旧端口未就绪期间沿用候选端口。绕过规则、代理地址和其他策略来自已保存设置。控制器读取只在有在途端口变更时解析，仍使用既有 guard、作用域、发布锁和租约。

候选宿主效果仍匹配原回执时，guard 持久记录延期并保留原对账资格。需要纠正外部漂移且 guard 已启用时，先在原台账记录 guard 接管，再以独立调用按已保存规则维护可用端口。旧监听恢复后，guard 可以继续应用旧策略，独立动作不结清原操作。未取得原 Helper 应用证据、候选策略差异扩大或候选监听不可核实时，仍走原宿主安全策略。显式补偿用既有补偿 ID 和真实监听证据完成原操作。

Windows Release 系统代理、TUN 边界、Core 安全、ManagedRuntimes 和模块边界回归通过。控制器证据缺失时原安全策略照常执行。MB-05 跨进程夹具按原 `0bd19b9c-1884-4b47-abea-275c656a58da` 返回 Interrupted，受控副作用计数为一。可信下载探针因缺少进程级测试 CA 覆盖跳过。

Linux 私有 Mihomo 与生产 Helper 验证真实监听与宿主环境。原 `3af4c2e5-d919-4dba-b7ec-c6d501ff5205` 的旧端口拒绝新连接时，guard 使用一次独立写入纠正外部绕过规则，宿主保留可真实转发的候选端口。重开台账再次检查未增加写入。具名补偿 `31201734-ce2f-4c4e-98c0-4613d37d23f6` 一次重载并恢复旧策略，原回执为 Interrupted。

原 `ce95abd9-eb23-4bbf-97dc-d43484aa3d44` 的旧监听重新就绪后，guard 再用一次独立写入应用已保存地址，原回执保持待核验。原 `700cc500-a317-4024-871f-ef7b5ed8e799` 的候选宿主效果一直与原 Helper 回执一致。候选文件漂移期间持久延期，复查零写入，精确候选恢复后原 ID 直接成功。应答丢失、未应用及差异不可完整观察的补偿路径同批通过，后两条均未调用宿主补偿。

专属 cgroup 已清理，宿主共享服务和代理文件指纹一致。日志为 `.artifacts/mb04-guard-listener/linux.log`，归档 SHA-256 为 `8995c884fa4204dcaa01015f8ea12722c83a114f689d44b9f649d2aa02ebc881`。

Proxy 保持 resident。无法认证任何监听时，安全策略可能仍指向暂不可用的旧端口。跨休眠交接与生产内存继续开放。
