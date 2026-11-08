# Server 模块边界实施进度

截至 2026-11-08 已入仓的阶段证据。架构约束见[模块边界设计](RelaxKonOS.ServerModuleBoundaries.Design.md)，驻留状态机和工作租约见[驻留设计](RelaxKonOS.ServerModuleResidency.Design.md)。

表中“已验证”限证据页所列制品、环境和入口。“已实现，实机证据有缺口”包含已通过本地回归或部分实机路径的实现。对象释放、ALC 回收、进程退出、隔离负载的内存差值与生产收益分别记录。

## 当前里程碑

| 里程碑 | 已实现且已验证 | 已实现，实机证据有缺口 | 未实现或仍有阻塞 |
| --- | --- | --- | --- |
| MB-01 资源与基线 | 两平台启动采树、认证 Git 自然休眠与唤醒、Terminal 断线重连。Windows Firewall 三轮通过 | Linux System 模式的真实 Helper 联合矩阵缺失 | 需隔离 Helper 与宿主持久恢复，补齐 Linux 矩阵、共享服务归属和长时间负载 |
| MB-02 官方模块边界 | Git 独立实现与窄 Host 端口、Firewall 独立执行代码、Proxy 的部分独立执行图和构建守卫 | 正式安装包和私有依赖尚未完成统一 ABI 的实机验证 | 其余官方模块的根注册与实现引用待拆分 |
| MB-03 Git | 真实仓库、CLI、后台构建责任、原 ID 对账与独立执行代码回收；已验证专属 HTTPS 源仓库和真实 Engine 标签清理 | 构建入口、取消和清理已有 Release 回归，缺同一真实 BuildKit 构建的联合验证 | 现有隔离环境的 RootlessKit 创建用户命名空间失败。需安全可达的专属 builder 才能继续联合验收 |
| MB-04 Proxy | 隔离 Linux 验证原系统代理 guard、直连 TUN、授权到期、原发布恢复和工作租约。受管订阅激活覆盖 Helper 已提交与候选已转发但 Helper 尚无终态两种中断 | 受管发布下的 TUN 已验证拒绝门禁。独立读取、订阅和直连发布执行图已有实证，整模块安全交接和完整故障矩阵仍缺 | Proxy 保持 resident。启用整模块动态休眠前，须证明每项安全责任在原 owner 下连续执行，并测量生产收益 |
| MB-05 Docker、证书与 WebServer | 独立 Engine、Pebble、Nginx 中断路径已实测，三域使用原工作租约和原回执。Docker 已验证变更 CLI 在途取消后 Engine 观察中断、未知 CLI 清理跨 Server 重启的门禁。Nginx 自动部署覆盖未准入、投影未写且未派发、原生结果未知三类路径 | 有限执行程序集与 Linux 系统包 Nginx 已验证。其他 Nginx 形态、缺失身份的 CA 订单核实、整机掉电仍缺实机证据 | 三域保持 resident。Docker 未知清理仍缺原操作的可信确证入口。完整安全交接、动态休眠和生产收益继续开放 |
| MB-06 ModuleHost | 代次 owner、原生进程树、Windows LocalSystem/S4U 和部分崩溃恢复已验证 | 正式安装身份、Linux 固定启动器及跨代崩溃的联合矩阵未齐 | SDK 合作排空与后台申报仍开放。验收后才决定启用闲时退出 |
| MB-07 专项 | SystemPerformance S1、Firewall-F1、Tailscale TS-01、Terminal 阶段验收已交付 | Terminal 完整安装与崩溃压力矩阵、Tailscale 等域的完整外部故障与资源收益未验证 | S1 冻结，仅修阻塞性交付问题。各域独立代码边界及生产收益分别验收 |
| MB-08 发布与收益 | 当前诊断契约与局部包及 ModuleHost 验证 | 缺正式包的完整 ABI 及真实业务同构建跨平台矩阵 | 交付前验证包和各端调用一致，并达到预先记录的进程树内存、冷启动和错误率门槛 |

## 下一步与完成条件

1. **MB-04 逐个完成 Proxy 管理执行图。** 已覆盖[受管订阅激活期间 Helper 中断](server-module-evidence/Proxy-Subscriptions.md#mb-04-受管订阅激活期间-helper-中断)。按 [MB-01B 基线](RelaxKonOS.ServerModuleBaseline.20261106.md)及[隔离配对测量](server-module-evidence/Proxy-Observation.md)核对后续管理图。涉及安全 owner 或旧 worker 时，先验证持久索引、原台账恢复和真实租约，再验证重启、取消、禁用期间的真实网络和原副作用次数。受管 TUN 完整事务需要原生网络恢复能力和持久所有权证明。
2. **MB-05 按领域扩展实机矩阵。** 已覆盖[变更 CLI 在途取消](server-module-evidence/Docker.md#mb-05-docker-变更-cli-在途取消与-engine-观察中断)、[未知 CLI 清理跨重启](server-module-evidence/Docker.md#mb-05-docker-cli-清理未知的持久门禁)、[Linux 外部 Nginx 重载](server-module-evidence/WebServer.md#mb-05-linux-外部-nginx-重载执行代次)及[续期自动部署](server-module-evidence/WebServer-TLS.md#mb-05-nginx-续期自动部署)。外部实例的自动部署和启停需要能证明原生效果归属的实例接口。缺失身份的 CA 订单和 Docker 清理门禁须先确定可归因于原 ID 的证据来源。完成判据包括按原 ID 得到原回执、未知效果继续受保护、业务和清理完成前保留工作责任，以及外部服务按原所有权运行。
3. **MB-03 保留明确的实机启动条件。** BuildKit 必须在隔离环境里真实执行，Git CLI 与 builder 可达同一可信 HTTPS 源仓库。再覆盖在途取消、中断、清理、原回执恢复和实际执行代次回收。当前启动失败和探测退出详见 [Git 与 BuildKit 证据](server-module-evidence/Git.md#mb-03-独立-engine-的受限-builder-复核)。
4. **对外发布和生产收益独立验收。** 完成相应安全连续性后，按[基线测量协议](RelaxKonOS.ServerModuleBaseline.Protocol.md)记录相同构建的真实负载、全部相关进程、冷启动延迟和错误率。保持各域独立关闭条件。

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
