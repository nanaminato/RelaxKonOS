# Server 模块边界实施进度

截至 2026-11-08 已入仓的阶段证据。架构约束见[模块边界设计](RelaxKonOS.ServerModuleBoundaries.Design.md)，驻留状态机和工作租约见[驻留设计](RelaxKonOS.ServerModuleResidency.Design.md)。

表中“已验证”限证据页所列制品、环境和入口。“已实现，实机证据有缺口”包含已通过本地回归或部分实机路径的实现。对象释放、ALC 回收、进程退出、隔离负载的内存差值与生产收益分别记录。

## 当前里程碑

| 里程碑 | 已实现且已验证 | 已实现，实机证据有缺口 | 未实现或仍有阻塞 |
| --- | --- | --- | --- |
| MB-01 资源与基线 | 两平台启动采树、认证 Git 自然休眠与唤醒、Terminal 断线重连。Windows Firewall 三轮通过 | Linux System 模式的真实 Helper 联合矩阵缺失 | 需隔离 Helper 与宿主持久恢复，补齐 Linux 矩阵、共享服务归属和长时间负载 |
| MB-02 官方模块边界 | Git 独立实现与窄 Host 端口、Firewall 独立执行代码、Proxy 的部分独立执行图和构建守卫 | 正式安装包和私有依赖尚未完成统一 ABI 的实机验证 | 其余官方模块的根注册与实现引用待拆分 |
| MB-03 Git | 专属 rootless BuildKit 已通过受信 HTTPS 构建、认证 HTTP 受理响应丢失、在途取消、CLI 与标签清理、两次重启的原回执及防重放。成功与取消均在保留原 DTO 时确认实际 ALC 收集 | 实测限专属 Linux，整机掉电与生产负载缺证据 | 未知派发保持原准入和代次释放门禁。可信的人工确证入口及生产收益继续开放 |
| MB-04 Proxy | 隔离 Linux 验证原系统代理 guard、直连 TUN、授权到期、受管订阅的两个中断点，以及受管 TUN 认证 HTTP 的原发布、补偿和具名启停回执恢复。完整 Server 已验证同次停机及原生归属未知时的管理停用门禁 | 已验证管理入口禁用后受管 TUN 持续转发。整模块安全交接仍缺 guard、授权到期和管理执行图的联合连续性证据 | Proxy 保持 resident。启用整模块动态休眠前，须证明每项安全责任在原 owner 下连续执行，并测量生产收益 |
| MB-05 Docker、证书与 WebServer | 独立 Engine、Pebble、Nginx 中断路径已实测，三域使用原工作租约和原回执。Docker 已验证在途取消及未知清理。Nginx 覆盖续期部署、外部重载取消和已确认阶段恢复 | 有限执行程序集、Linux 系统包及外部重载已验证。其余实例形态、缺失身份的 CA 订单核实、整机掉电仍缺实机证据 | 三域保持 resident。Docker 未知清理和外部 Nginx 未知结果缺原操作的可信确证入口。完整安全交接、动态休眠和生产收益继续开放 |
| MB-06 ModuleHost | 代次 owner、原生进程树、Windows LocalSystem/S4U 和部分崩溃恢复已验证 | 正式安装身份、Linux 固定启动器及跨代崩溃的联合矩阵未齐 | SDK 合作排空与后台申报仍开放。验收后才决定启用闲时退出 |
| MB-07 专项 | SystemPerformance S1、Firewall-F1、Tailscale TS-01、Terminal 阶段验收已交付 | Terminal 完整安装与崩溃压力矩阵、Tailscale 等域的完整外部故障与资源收益未验证 | S1 冻结，仅修阻塞性交付问题。各域独立代码边界及生产收益分别验收 |
| MB-08 发布与收益 | 当前诊断契约与局部包及 ModuleHost 验证 | 缺正式包的完整 ABI 及真实业务同构建跨平台矩阵 | 交付前验证包和各端调用一致，并达到预先记录的进程树内存、冷启动和错误率门槛 |

## 下一步与完成条件

1. **MB-04 逐个完成 Proxy 管理执行图。** 已覆盖[受管订阅激活期间 Helper 中断](server-module-evidence/Proxy-Subscriptions.md#mb-04-受管订阅激活期间-helper-中断)、[受管 TUN 原发布中断](server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http)、[原发布补偿](server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http-补偿)、[具名启停响应丢失](server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-认证-http-具名启停回执)、[同次停机](server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-与-proxy-安全责任同次停机)及[管理停用门禁](server-module-evidence/Proxy-Safety.md#mb-04-受管-tun-模块停用门禁)。下一项在管理入口禁用的完整 Server 中联验受管 TUN 持续转发、Core guard 纠偏及 sing-box 到期，按原归属核对恢复、租约和实际效果。按 [MB-01B 基线](RelaxKonOS.ServerModuleBaseline.20261106.md)及[隔离配对测量](server-module-evidence/Proxy-Observation.md)核对后续管理图。
2. **MB-05 按领域扩展实机矩阵。** 已覆盖[变更 CLI 在途取消](server-module-evidence/Docker.md#mb-05-docker-变更-cli-在途取消与-engine-观察中断)、[未知 CLI 清理跨重启](server-module-evidence/Docker.md#mb-05-docker-cli-清理未知的持久门禁)、[Linux 外部 Nginx 重载及待核验门禁](server-module-evidence/WebServer.md#mb-05-外部-nginx-重载结果待核验)及[续期自动部署](server-module-evidence/WebServer-TLS.md#mb-05-nginx-续期自动部署)。外部实例的自动部署和启停需要能证明原生效果归属的实例接口。缺失身份的 CA 订单和 Docker 清理门禁须先确定可归因于原 ID 的证据来源。完成判据包括按原 ID 得到原回执、未知效果继续受保护、业务和清理完成前保留工作责任，以及外部服务按原所有权运行。
3. **MB-03 真实 BuildKit 联合验收已通过。** 私有 `overlay2` Engine、现有 `rootlesskit` profile 与专属 CA 满足启动条件。[完整认证 Git HTTP](server-module-evidence/Git.md#mb-03-完整认证-git-http-的-buildkit-原回执)已续验。未知派发的人工解除须有原 builder 任务及操作标签的可信证据。
4. **对外发布和生产收益独立验收。** 完成相应安全连续性后，按[基线测量协议](RelaxKonOS.ServerModuleBaseline.Protocol.md)记录相同构建的真实负载、全部相关进程、冷启动延迟和错误率。保持各域独立关闭条件。

### 本轮剩余门槛

本轮在既有专属 Linux 设施覆盖受管订阅激活、Docker 与外部 Nginx 的独立故障窗口，以及真实 BuildKit 联合矩阵。以下工作分别依赖原操作证据、实例能力或独立环境。

| 范围 | 剩余门槛 | 条件满足后的第一项 |
| --- | --- | --- |
| MB-04 Proxy | 停用计划、应用及禁用启动已验证 Helper 不可用、原生漂移及独立待核验停止的阻断。保留数据移除见证与禁用启动尚缺联合实机证据 | 先验证管理入口禁用时受管 TUN、Core guard 与 sing-box 到期的联合连续性。未知结果继续待核验。Proxy 保持 resident，直至整模块安全交接通过 |
| MB-05 Docker | 未知清理缺与原 operationId 绑定的可信进程及输出排空证据入口 | 以原 CLI 清理门禁做只读确证，匹配时再结案原回执 |
| MB-05 Certificates | 丢失订单创建响应时缺与原请求匹配的订单身份。跨源 CA、补取部署和主机掉电需要独立证据 | 用可信原账户订单标识验证一次只读对账，保持现有部署。见 [原订单标识门槛](server-module-evidence/Certificates.md#缺失原订单标识的核验门槛) |
| MB-05 WebServer | 外部实例未知结果缺可信原命令证据。其他实例的启停与自动部署需要该实例原生回执和所有权接口，其他平台需要对应隔离主机 | 先在可核验的外部实例上用原 operationId 证明命令、配置身份和工作责任，再验证原门禁解除 |
| MB-03 Git | 未知派发缺原 builder 任务终态及原操作标签的可信确证入口 | 核对同一 builder 上原任务与精确标签，再验证原回执及准入门禁的关闭 |
| 跨范围 | 整机掉电需要可独立断电并检查持久状态的专属环境。生产内存收益需要真实代表性负载与完整进程树配对基线 | 先复跑目标领域原回执及恢复门禁，再分别测量对象释放、ALC、实际进程退出、隔离内存差值和生产收益 |

## 专项证据入口

| 范围 | 原始记录 |
| --- | --- |
| MB-01 / RES | [MB-01A](RelaxKonOS.ServerModuleBaseline.20261105.md)、[MB-01B](RelaxKonOS.ServerModuleBaseline.20261106.md)、[资源与恢复权威清单](RelaxKonOS.ServerModuleResources.Inventory.md)、[驻留历史](server-module-evidence/Residency-History.md) |
| MB-02 / MB-03 | [Git 代码边界、构建与恢复](server-module-evidence/Git.md) |
| MB-04 | [系统代理、TUN 与到期](server-module-evidence/Proxy-Safety.md)、[发布与恢复](server-module-evidence/Proxy-Publication.md)、[订阅队列和激活](server-module-evidence/Proxy-Subscriptions.md)、[读取与配对测量](server-module-evidence/Proxy-Observation.md) |
| MB-05 | [Docker](server-module-evidence/Docker.md)、[证书与 ACME](server-module-evidence/Certificates.md)、[WebServer 发布与服务操作](server-module-evidence/WebServer.md)、[WebRoot、TLS 与自动部署](server-module-evidence/WebServer-TLS.md) |
| MB-06 / MB-07 | [ModuleHost 与 Terminal](server-module-evidence/ModuleHost-Terminal.md)、[SystemPerformance S1](server-module-evidence/SystemPerformance.md)、[Firewall 与 Tailscale](server-module-evidence/Firewall-Tailscale.md)、[ModuleHost v2 实施记录](RelaxKonOS.ServerModuleV2.Progress.md) |
| 跨模块 | [阶段验收、历史进度与隔离设施](server-module-evidence/Milestones.md) |

后续阶段将范围、制品与环境、通过和失败路径、原回执及未验收项写入对应证据页，并更新本页的状态和下一步。应用文档在用户可见行为或应用约束变化时同步更新，已有证据可直接引用。
