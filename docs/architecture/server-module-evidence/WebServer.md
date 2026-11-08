# MB-05 WebServer 发布与 Nginx 服务操作

隔离 Nginx、站点发布、直接服务操作与原生回执。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [MB-05 真实 Nginx 重载与管理工作进程崩溃](#mb-05-真实-nginx-重载与管理工作进程崩溃)
- [MB-05 直接 WebServer 操作取消与排空](#mb-05-直接-webserver-操作取消与排空)
- [MB-05 WebServer 启动恢复准入](#mb-05-webserver-启动恢复准入)
- [MB-05 Nginx provider 和门面的原回执恢复](#mb-05-nginx-provider-和门面的原回执恢复)
- [MB-05 网站发布站点应用后的停机责任](#mb-05-网站发布站点应用后的停机责任)
- [MB-05 应用记录消失后的发布回执](#mb-05-应用记录消失后的发布回执)
- [MB-05 网站发布真实 HTTPS 验证响应中断](#mb-05-网站发布真实-https-验证响应中断)
- [MB-05 网站发布补偿结果未知](#mb-05-网站发布补偿结果未知)
- [MB-05 站点配置阶段中断的持久责任](#mb-05-站点配置阶段中断的持久责任)
- [MB-05 网站发布准入与执行作用域](#mb-05-网站发布准入与执行作用域)
- [MB-05 直接 WebServer 操作执行作用域](#mb-05-直接-webserver-操作执行作用域)
- [MB-05 完整认证 Server 的 Nginx 重载与重启](#mb-05-完整认证-server-的-nginx-重载与重启)
- [MB-05 系统包 Nginx 经生产 Helper 中断与恢复](#mb-05-系统包-nginx-经生产-helper-中断与恢复)
- [MB-05 已提交 Nginx systemd 作业的取消与停机排空](#mb-05-已提交-nginx-systemd-作业的取消与停机排空)
- [MB-05 Nginx 原生重载结果待核验](#mb-05-nginx-原生重载结果待核验)
- [MB-05 Nginx 原 Helper 回执对账](#mb-05-nginx-原-helper-回执对账)
- [MB-05 Nginx 存活进程响应丢失和失败回执](#mb-05-nginx-存活进程响应丢失和失败回执)
- [MB-05 Nginx 终态回执保留与防重放](#mb-05-nginx-终态回执保留与防重放)
- [MB-05 完整认证网站发布中断与恢复](#mb-05-完整认证网站发布中断与恢复)
- [MB-05 Nginx 原回执清理确认与租约](#mb-05-nginx-原回执清理确认与租约)
- [MB-05 网站发布独立执行程序集](#mb-05-网站发布独立执行程序集)
- [MB-05 直接 Nginx 重载独立执行程序集](#mb-05-直接-nginx-重载独立执行程序集)
- [MB-05 Linux 外部 Nginx 重载执行代次](#mb-05-linux-外部-nginx-重载执行代次)
- [MB-05 外部 Nginx 重载结果待核验](#mb-05-外部-nginx-重载结果待核验)
- [MB-05 Nginx 集成检查点与执行代次](#mb-05-nginx-集成检查点与执行代次)
- [MB-05 已知 Nginx 集成失败的具名补偿](#mb-05-已知-nginx-集成失败的具名补偿)
- [MB-05 Linux Nginx 启动、停止与重启的原回执](#mb-05-linux-nginx-启动停止与重启的原回执)

## MB-05 真实 Nginx 重载与管理工作进程崩溃

既有 Linux 测试机以普通账户运行 Linux x64 Release 的 `--nginx-live-recovery-only /usr/sbin/nginx`。专属 Nginx 仅绑定随机回环端口，配置、PID、日志及临时目录独立。独立工作进程使用生产 WebServerOperationStore 接收并持久化重载请求，向专属 Nginx 发送真实重载信号，然后在提交终态前停在受控屏障。测试观察到新配置 HTTP 内容、一次实际重载事件及原 Running 回执后才终止工作进程。

恢复进程读取原台账，将操作 `94c83391-b03f-4dad-870f-3a627f5a0705` 持久化为 Failed / interrupted / webserver.operation_interrupted。同键重试与重新打开台账均返回同一回执，原 Nginx 继续提供新内容，重载事件仍为一次。专属实例正常退出，临时目录清理完毕。制品 SHA-256 为 `1a83a7e8f646edbab6bb568bf763c2b2dc2f6ef82646d84ef1cf8b6c3df7ba42`，日志见 `.artifacts/mb05-nginx-final.log`。首轮日志事件计数断言失败，修正后复测通过。

本项覆盖生产 WebServer 操作台账和真实 Nginx 动作，重载命令由专属夹具调用。完整 Nginx provider、网站发布、TLS/ACME 及 HTTP 响应排空仍待联合验收。共享 Nginx 持续运行。

## MB-05 直接 WebServer 操作取消与排空

直接 Nginx 操作沿用原 WebServer 操作台账，并在写入 Queued 前取得 WebServer 常驻工作租约。取消请求返回当前回执并发出取消信号，实际动作结束后才写入 Cancelled。桌面端继续轮询已提交的操作。停机等待实际后台工作，停机取消保留原活动回执供启动恢复；终态保存或 CLI 清理无法确认时，工作 owner 保留故障责任。停用检查同时读取直接操作和网站发布的未终结台账。Linux 本地 Nginx 命令取消后等待 CLI 退出及输出排空。

Windows Release 聚合回归通过。新增受控取消测试确认实际动作仍在运行时，原回执保持 Running，租约仍在，台账仍列为未终结；放行动作后才持久化 Cancelled 并释放租约。证据见 `.artifacts/mb05-web-drain.log`。实际 provider 取消、真实 CLI 清理无法核实的故障路径及网站发布联合验收继续开放。

## MB-05 WebServer 启动恢复准入

直接操作台账从构造时读取和改写迁到显式 hosted 启动恢复，复用同一 WebServer 常驻工作 owner。恢复完成前拒绝直接操作准入和回执读取。原 Queued/Running 按原 operationId 写成 interrupted；解析、读取或持久化失败时保持恢复责任并拒绝新操作。停用检查使用同一只读解析路径，损坏文件、目录占位及权限错误按不可核实处理。

Windows Release 聚合回归通过。新增损坏台账路径确认保留原文件、持有一份待恢复租约且拒绝新动作。此路径未清除台账或重放副作用。生产 provider 及网站发布的完整实机故障矩阵继续开放。

既有 Linux 测试机的真实 Nginx 重载与崩溃夹具以显式启动恢复复测通过。原 operationId `fa2bbf90-cb26-4280-819e-0cefc9635442` 持久化 interrupted，重载仍为一次。归档 SHA-256 为 `958d06c78edddaa53751c6a28de1924c1e6363522e282f145bb0e618d46f6378`，日志见 `.artifacts/mb05-web-startup-nginx.log`。

## MB-05 Nginx provider 和门面的原回执恢复

WebServer 门面将生命周期及重载请求交给生产 provider 核对原回执。Nginx provider 按原 actor、操作类型、实例 ID 和幂等键查询台账，再执行实例发现。实例不再被发现时，两条重载入口仍可返回原回执。

既有 Linux 测试机通过生产 WebServerManager、NginxWebServerManager、PrivilegedNginxOperations 和 WebServerOperationStore 的联合测试。夹具替换 Helper 传输，只接受封闭的 Reload 请求并重载独立回环 Nginx；没有连接共享 Helper 或调用 systemd。真实重载生效且原操作持久化为 Running 后终止工作进程。恢复后移除专属发现 marker，确认实例不在发现结果中。生命周期及独立重载入口以原键取得 `bb3cc3d1-e9dd-40e0-9c75-83b85eb50593` 的同一 interrupted 回执，传输调用为零，实际重载仍为一次。专属 Nginx 正常退出，临时目录已清理。Windows Release `--webserver-only` 通过。

日志见 `.artifacts/mb05-provider-final.log`，归档 SHA-256 为 `d38b7b36458e78889f24a64a5aa5298ec41d36383c3e73ab8a97b9732e810a1d`。真实 Helper 响应中断、完整 HTTP、网站发布及 TLS/ACME 联合验收继续开放。

## MB-05 网站发布站点应用后的停机责任

生产 WebsitePublicationCoordinator 在站点应用已返回成功后，先完成应用目录关联，或等待原有的关联失败补偿，再响应停机取消。目录关联先于关联阶段日志和后续外部验证。停机取消仍保留原活动回执供恢复，恢复只记录 Interrupted。同键请求继续取原回执。

Windows Release 聚合新增验证覆盖停机等待尚未返回的站点动作、原工作租约、原台账跨实例重开，以及只执行一次站点应用。站点 provider 与证书为受控边界。完整生产 provider 的网站发布、真实 HTTPS 响应中断和补偿失败仍待实机联合验证。

既有 Linux 测试机也通过 Release 聚合回归，新增检查为后台 owner 第 33 至 35 项。首次 Linux 执行通过新增项，随后因 Windows 打包未保留测试 apphost 执行位而在 Terminal 停止。补专属临时目录中的执行位后，整组复测通过。证据见 `.artifacts/mb05-publication.log` 和 `.artifacts/mb05-publication-linux-final.log`，Linux 制品归档 SHA-256 为 `cd32c94a1e4fb6a69f95d0230c367902f6771213de873d8be9f19a6fae68382f`。

## MB-05 应用记录消失后的发布回执

网站发布在同一台账准入区间先按原 actor、幂等键和请求身份查找回执。只有新操作需要核对应用仍存在。原应用记录已删除时，原请求仍返回 Interrupted 回执，新请求返回 404 且不创建台账记录或再次应用站点。验证使用生产协调器、应用目录和发布台账，站点动作由受控 provider 记录。

Windows 与既有 Linux 测试机的 Release 聚合回归均通过，新增及更新的断言为后台 owner 第 35、36 项。日志见 `.artifacts/mb05-publication-replay-win.log` 与 `.artifacts/mb05-publication-replay-linux.log`。

## MB-05 网站发布真实 HTTPS 验证响应中断

生产发布协调器使用真实回环 HTTPS 请求执行网站验证。夹具在随机端口使用专属证书，客户端核对指纹和主机名，系统信任库保持原状。先通过 TLS 探针，再让服务收到验证请求后挂起响应头。原操作进入 Verifying 并持有 WebServer 工作租约。触发停机后，测试等待协调器排空，并由服务端独立观察 RequestAborted。原台账仍活动，重新打开后以原 operationId 记录 Interrupted，同键重试既不发起第二次验证，也不再次应用站点。

站点应用、证书目录和 Docker 工作负载观察仍由受控边界提供。本项覆盖发布协调器的真实出站验证请求，完整管理 API 的响应写入、生产 Nginx 网站发布、真实 CA 和未知补偿仍待验收。首轮证书未完成握手，修正夹具证书加载并通过独立 TLS 探针后复测。

Windows 与既有 Linux 测试机的 Release 聚合回归通过，新增为后台 owner 第 37 至 39 项。日志见 `.artifacts/mb05-https-win-final.log` 与 `.artifacts/mb05-https-linux.log`。Linux 制品归档 SHA-256 为 `92a435a95330e215516aeae6e696377249929c34fdd656e017dbda0539fafec5`。

## MB-05 网站发布补偿结果未知

站点已应用且应用目录关联失败时，原发布工作租约一直持有到补偿返回。补偿结果无法确认时，原回执持久化为 Failed / website.application_association_recovery_failed，并带 website.recheck_required。原台账保留这份责任，常规历史裁剪不会移除它。停用检查可见该责任，同一应用或同一 WebServer 与域名组合的新发布被拒绝。同键仍返回原回执，启动及重试均不重放站点应用或补偿。

本项使用生产协调器、目录和台账，站点补偿使用受控 provider。操作员确认并解除门禁的正式入口、补偿执行过程中被强制终止的故障矩阵及实际 Nginx 补偿仍待验证。

Windows 与既有 Linux 测试机的 Release 聚合回归通过，新增为后台 owner 第 40 至 42 项。首轮并发删除触发目录通用异常并绕过补偿，目录改用明确的应用不存在错误后复测通过。日志见 `.artifacts/mb05-compensation-win-final.log` 与 `.artifacts/mb05-compensation-linux.log`。

## MB-05 站点配置阶段中断的持久责任

恢复读取原活动回执时，若最后持久阶段为 Configuring，仍以原 operationId 写入 Interrupted，并记录 website.site_application_unverified。此阶段可能已经改变 Nginx，恢复保留原站点 ID 和证书引用，禁止同一应用或同一 WebServer 与域名的新发布，停用检查和历史保留规则继续承担责任。同键请求读取原回执，恢复不执行站点写入或补偿。关联已提交后的阶段和单纯验证阶段沿用各自的恢复语义。

测试使用生产发布协调器和持久台账，受控 provider 在 Configuring 阶段接收停机信号并返回取消。真实 Nginx 写入中被强制终止及人工核实后的解除入口仍开放。

Windows 与既有 Linux 测试机 Release 聚合回归通过，新增为后台 owner 第 43 至 45 项。日志见 `.artifacts/mb05-configure-win.log` 与 `.artifacts/mb05-configure-linux.log`。

## MB-05 网站发布准入与执行作用域

原网站发布协调器继续独占准入、台账恢复与停机排空。只有持久化 queued 的新发布创建执行作用域，执行器在原工作租约内处理上游观察、证书选择、Nginx 配置、应用关联、HTTPS 验证及原补偿。工作 owner 等待终态提交和异步作用域释放。启动恢复、已知键回执和准入拒绝不解析执行器。Core Nginx 管理、TLS 材料与应用关联仍由原服务持有。

同一台账锁同时为活跃发布保留应用及 Nginx 实例内的规范域名。同键先查原回执。不同键命中活跃发布返回 `website.operation_conflict`，命中已有未核验配置继续返回 `website.recheck_required`。域名占用在应用观察、CA 签发及配置执行前生效。

Linux Release 在私有 systemd、Docker、Nginx、Pebble 和 DNS 上启动两个独立应用。原发布 `69f61255-1f9c-407f-b946-25e24e7297fc` 的实际 CA 证书响应被暂扣时，另一运行应用提交带大写和末尾点的同域名请求，收到认证 HTTP 409，历史为空。原键返回原回执，CA 一张订单、一次签发提交。随后终止 Server，重启仍以原发布和原证书操作 `553bf539-e7e7-4321-bcb1-9e44e65047f2` 查询原有效订单。挑战已清理，旧 TLS 和原上游继续，零新增 Nginx 重载。专属 cgroup 清理，宿主服务与配置指纹一致。日志为 `.artifacts/mb05-publication-domain-live.log`，归档 SHA-256 为 `b76242814db9faa538ee0f8281ee5963ba612da11be18458509d294a02eb23b6`。首站自动建路由的后续交付见下文。

同制品正常发布 `fef71822-3ea7-4f02-b996-fc99a7870c7b` 通过真实 HTTPS 和一次重载，日志为 `.artifacts/mb05-publication-domain-success.log`。Windows Release 模块里程碑回归通过，验证并发拒绝没有创建执行作用域或新回执，Docker 跨进程原 `e8d5d6b4-bf59-41b3-bea3-8a7f2252860d` 返回 Interrupted，副作用计数为一。Windows 首轮夹具的应用创建与观察共用屏障，第二轮站点关联检查沿用单应用假设。修正后日志为 `.artifacts/mb05-publication-domain-win-confirmed.log`。

Windows Release 模块边界回归通过。测试确认原协调器启动不构造执行图，停止时等待真实观察任务和异步作用域清理，工作租约持续到清理结束。Linux x64 Release 在两套专属网络、挂载和 PID 命名空间用实际 Nginx 验证。配置提交崩溃 `8caf607a-1400-4dfe-96e8-ad67dbb518a9` 返回原 Interrupted / website.site_application_unverified，HTTPS 验证崩溃 `2d7f8936-648f-474f-8101-5ffe131a3d27` 返回原 Interrupted / website.recheck_required。两条路径的同键 HTTP 响应断线后均完成排空，恢复执行作用域为零，各只重载一次发布配置，真实 HTTPS 转发保持。宿主 Nginx PID 和配置指纹前后一致。首轮远端包装器在进入命名空间前失败，修正后通过。证据为 .artifacts/mb05-website-scope-live.log，归档 SHA-256 为 30c32dd3867bccc7b16ca16664b4d54c179df48e0563584a2afc510ae049968c。该联合验收使用生产执行器与真实 Nginx，HTTP 认证和响应屏障由隔离夹具提供。完整 Server 认证与直接 WebServer 后续验收见文末，网站发布模块保持 resident。

## MB-05 直接 WebServer 操作执行作用域

直接 Nginx 集成、重载、HTTP-01 启用及受管实例启动、停止、重启使用同一 WebServer 操作台账。只有新准入的操作在原实例锁和原工作租约内解析执行器。执行器在既有 Core Nginx 提供者上调用封闭命令，执行任务和异步作用域清理完成后才提交终态。清理不可确认时保持原活跃回执及故障租约。原子准入同时核验原操作者，其他操作者的同键碰撞返回冲突且不暴露原回执。共享提供者及同步站点接口仍归原 Core 服务。

Windows Release WebServer 和模块边界专项通过，覆盖取消期间的真实任务、异步作用域清理、清理失败保留原责任和操作者同键冲突。Linux x64 Release 在私有网络、挂载和 PID 命名空间用实际 Nginx 验证直接重载。专属重载命令退出成功后，传输返回前终止原工作进程，原 `eb74e92c-f9de-4351-9322-85f424d51a0d` 恢复为 Failed / interrupted。原实例不再可发现时，生命周期入口和直接重载 HTTP 同键入口仍返回原回执。客户端断线后响应排空，恢复作用域为零，原 Nginx 继续提供 HTTPS，实际重载一次。宿主 Nginx PID 和配置指纹一致。日志为 .artifacts/mb05-web-direct-scope-live.log，归档 SHA-256 为 b72228bb83eb26db12eda0aecbc5b93fa313c4942db3be2e297eb6740ea47b8e。本轮 Linux 使用生产提供者、原台账和实际 Nginx，封闭重载命令经隔离传输发往专属进程。完整 Server 认证重载见下文，其他直接动作的实机中断仍开放。WebServer 保持 resident。

## MB-05 完整认证 Server 的 Nginx 重载与重启

仓内 `RelaxKonOS.Server.Tests/run-authenticated-nginx-live.sh` 和同名 Python 驱动在独立挂载、网络和 PID 命名空间运行。私有状态、发现入口、配置和证书只供专属 Nginx。完整 Linux x64 Release Server 使用生产 PAM 登录和管理员授权路径，未认证重载被拒绝。专属命令实际向 Nginx 提交一次 reload，客户端验证原证书和新站点响应。命令返回前保留测试屏障，通过生产 API 确认操作仍 Running 后终止整个 Server。重启重新认证，同键和原 ID 查询均返回 `3eb76997-4b04-4849-ad72-a34423de592a` Failed / webserver.operation_interrupted。相同 Nginx master 持续提供 HTTPS，reload 计数保持一次。宿主 Docker 和 Nginx PID 及 Nginx 配置指纹一致。

日志为 .artifacts/mb05-auth-nginx-admin-live.log，Release 归档 SHA-256 为 e0666d40f74d1058d6ca6d940a33903c9eff280cdef33b06a72c6bb9ecfba6b8。前几轮暴露私有状态挂载、专属 Nginx 初始化和测试身份选择问题，修正后通过。测试命令包装器仅限专属集成实例，生产提供者、授权、台账及重启入口均参与。系统包 Nginx 的 Helper/systemd 故障见下文，其他直接动作仍待验收，完整认证的网站发布见文末。WebServer 保持 resident。

## MB-05 系统包 Nginx 经生产 Helper 中断与恢复

仓内 `RelaxKonOS.Server.Tests/run-authenticated-nginx-systemd-live.sh` 启动专属 systemd 委派 cgroup，隔离挂载、PID、网络、systemd 单元、PAM 配置和状态。完整 Linux x64 Release Server 走真实 PAM 和生产 Helper Unix socket，调用固定 `nginx.service` 重载。专属单元执行实际 Nginx reload，并在重载后暂缓结束。客户端核验更新后的 HTTPS，生产 API 同时报告原操作 Running。强制终止 Server 和独立生产 Helper 后，原 Helper socket 不可达，原 Nginx master 仍以同一证书提供 HTTPS。释放专属 systemd 重载并重启两进程后，同键与原 ID 均返回 `53b6405f-7cda-41c0-9c46-0e9994f65df6` Failed / webserver.operation_interrupted。原 master PID 保持，实际 reload 一次，专属 cgroup 清理完成。宿主 Nginx、Docker 的服务 PID 和配置指纹一致。

系统包路径日志为 .artifacts/mb05-auth-nginx-systemd-final.log，归档 SHA-256 为 1e837ca6a8a4fa02c1b967d37906ee05e183f5cac44a47dfcb102f714e091a19。原集成路径独立复测保留 `0ddd5092-68a4-476a-aad1-d6345f7c150d` 和一次重载，日志为 .artifacts/mb05-auth-nginx-integrated-final.log。系统包的自然停机和取消联合排空见下文。Helper 自身等待超时后的原 systemd 作业对账仍开放。

## MB-05 已提交 Nginx systemd 作业的取消与停机排空

Linux Unix socket 在 Nginx 固定服务动作提交前仍接受取消。帧提交后等待生产 Helper 的终态响应，原 WebServer 工作任务、实例锁与执行作用域继续持有责任。其余 Helper 操作沿用各自响应等待策略。原版在专属 systemd reload 尚未结束时已把原回执从 Running 推到终态，夹具明确失败。

修复后，真实 reload 更新 HTTPS 后，客户端取消原 `45e7671c-86c1-4146-904c-11bca1078c13`，直到原 systemd 重载结束，回执仍 Running。释放原重载后，同键返回原 Cancelled。该回执记录管理操作取消，已生效的 Nginx 配置继续提供服务。第二次真实 reload `b6c2d4fd-6f01-45b0-97a5-caab66fecd0e` 期间发送 SIGTERM，监听关闭后 Server 仍等原作业结束才正常退出。重启同键返回原中断回执。两条路径合计两次实际 reload，原 master 和 HTTPS 持续，宿主服务指纹一致。Windows Release 模块里程碑通过。

失败和修复日志分别为 .artifacts/mb05-auth-nginx-drain-before.log 和 .artifacts/mb05-auth-nginx-drain-final.log，修复归档 SHA-256 为 39b27952467315dc201df33627780b3ddf79bc9a316ae2ef52be5b9ba497251e。Helper 自身命令超时及传输丢失后的原回执保留见下文，原生终态查询和关账仍开放。

## MB-05 Nginx 原生重载结果待核验

直接系统包 Nginx 重载在派发前持久化原操作检查点。生产 Unix socket 在派发后发现连接丢失、帧不完整或 Helper 自身超时，报告原生结果未核实。存活 Server 保持原 Running / native-recovery-required 回执、工作租约和实例锁。启动遇到未获终态的派发检查点也保留原 ID 和待核验回执，已知键和原 ID 可读，同实例新的后台操作返回 409。收到明确的 Helper 响应时沿原终态与停机规则处理。此阶段尚无 Helper 原作业终态的持久查询入口，后续补齐见下文。待核验回执不会因系统服务仍健康、看不到作业或等待超时而自动关账。桌面和 Android 显示待核验提示。

最终 Linux x64 Release 私有 systemd 实测三条路径。仅终止 Helper 时，存活 Server 保留 `5560ad28-8aef-4374-9b19-1e392586351b`。Helper 自身 30 秒超时且真实 reload 作业仍在时，保留 `fc4e4ac4-993f-474c-b426-f655af7fcf69`。双进程强制终止时，重启从派发检查点保留 `7c6f143f-8e6f-4b61-b45c-5c890f108af8`。每条路径均核对原 ID、同键、不同键拒绝、HTTPS 持续、同一 master 和仅一次实际重载。Windows Release 模块边界通过。Linux 同批取消与 SIGTERM 排空复测通过，收到原 Helper 终态后仍按原规则持久化。专属 cgroup 清理完成，宿主共享服务和配置指纹一致。

日志为 .artifacts/mb05-auth-nginx-checkpoint-unknown.log、.artifacts/mb05-auth-nginx-checkpoint-timeout.log、.artifacts/mb05-auth-nginx-checkpoint-crash.log 及 .artifacts/mb05-auth-nginx-checkpoint-drain.log，归档 SHA-256 为 c663b4d80d62f9d41e313064a74be4105cd7ed3de0e951bc3e88a22a9e1b456b。原 operationId 的 Helper 成功证据和门禁解除见下文。WebServer 保持 resident。

## MB-05 Nginx 原 Helper 回执对账

直接系统包 reload 将原 WebServer operationId 传给生产 Helper 的专属固定操作。Helper 在 root 保护目录内独占写入原意图，再调用固定的 `systemctl reload nginx.service`。原命令明确成功后原子持久化成功证据，再发送响应。同 ID 执行请求若已存在原意图，只读取原回执。只读查询按同一 ID 返回成功证据，超时、非零但无法核实原生终态、文件缺失或身份不符均保留待核验。Server 原后台工作 owner 在运行期与启动时按原 ID 查询，只在匹配证据后持久化成功并释放原责任。未完成查询不构造 Nginx 执行作用域，不再次派发。

Linux x64 Release 私有 systemd 实测。原重载 `c8021da0-0f86-4b20-81b5-093455edc5b4` 已更新真实 HTTPS 时终止 Server，随后生产 Helper 收到实际 systemctl 成功并落盘。重启 Helper 与 Server 后，原 ID 和同键返回 Succeeded，原重载仍为一次。确认原回执后，新键才准入并完成下一次重载。Helper 提前终止 `281f0065-49b7-40ff-bfa1-5b010e1679ce` 和真实 30 秒超时 `ea6aaf34-479e-4192-a0f5-557a42762336` 继续保留同 ID 待核验，实际各一次。正常取消与 SIGTERM 排空复测通过。原 Nginx master、HTTPS 和宿主指纹核验通过，专属 cgroup 清理。Windows Release 模块里程碑通过。Linux 原工作进程崩溃和 HTTP 响应断线夹具也复测通过，原待核验回执和单次重载保持。

联合日志为 .artifacts/mb05-nginx-receipt-receipt-live.log、.artifacts/mb05-nginx-receipt-unknown-live.log、.artifacts/mb05-nginx-receipt-timeout-live.log 和 .artifacts/mb05-nginx-receipt-drain-live.log，归档 SHA-256 为 fa12570bc4d2f4b970b79326639576140bf4f8b94011e83855808d5a448fa790。HTTP 断线夹具日志为 .artifacts/mb05-nginx-receipt-fixture-live.log。该阶段范围为明确成功证据，失败证据见下文。若 Helper 未收到或未写入原终态，系统健康及时间流逝无法解除门禁。此前未向 Helper 传递原 ID 的回执也保持待核验。Helper 回执保留策略和系统重启后的联合验证仍开放。

## MB-05 Nginx 存活进程响应丢失和失败回执

Helper 原 ID 回执支持实际成功及可确认的失败。仅接受固定 `systemctl reload nginx.service` 对所等待原作业的明确失败诊断，读取失败、通用非零、断连和本地超时仍保留原意图。只读查询返回带原 ID 的终态证据，Server 持久化 Succeeded 或 Failed / webserver.reload_failed 后才解除原实例后台准入门禁。原生失败诊断来自 [systemd 作业终态等待实现](https://github.com/systemd/systemd/blob/main/src/shared/bus-wait-for-jobs.c)。

Linux 私有 systemd 上，Unix socket 中继仅截断实际生产 Helper 的首个终态响应，并短暂阻止只读查询，以核对原操作进入待核验、新键被拒绝。释放查询后，完整 Server 和原 Helper 保持存活，成功操作 `1a3516f4-6709-486f-99ef-76f9b478a2bd` 按原 ID 落盘，实际 reload 一次。真实 Nginx 配置检查拒绝 `bdd59a66-2a3a-453c-9488-8f4f0d348ba0`，作业尝试一次，未发重载信号，原 HTTPS 和 master 持续。按原 ID 只读确认失败后，新键才通过并完成下一次真实 reload。两次均核验同键和共享服务指纹，专属 cgroup 清理完成。首轮中继把终态字段名大小写读错，修正后通过。

日志为 .artifacts/mb05-nginx-live-loss-attempts-response-loss.log 和 .artifacts/mb05-nginx-live-loss-attempts-rejected.log，归档 SHA-256 为 7587cfe65e52aa7bf83feaee98548d2eafad09831c9f0dbba1a75fd0effabff5。Windows Release 模块里程碑通过。Linux 本批取消和 SIGTERM 排空复测通过，真实 30 秒超时操作仍保持原待核验回执。日志为 .artifacts/mb05-nginx-live-loss-final-drain.log 和 .artifacts/mb05-nginx-live-loss-final-timeout.log。Helper 未观察到明确原作业终态时保留待核验。Helper 回执保留策略、主机重启及其他 WebServer 直接动作仍待验收。

## MB-05 Nginx 终态回执保留与防重放

Server 在原后台 owner 内同步持久化终态及清理待确认标记后，才以原 ID 通知 Helper。Helper 仅处理有明确终态的原记录，用原子替换将详情压缩为同路径的零字节防重放标记，继续拒绝派发该 ID。未确认记录保持原样。标记已存在时重复确认成功，丢失清理响应时由 Server 启动按原 ID 重试清理。Linux 同步回执文件和目录，Server 的整账替换也同步，避免后续写入削弱已通知 Helper 的原终态持久性。自然停机时若已收到确切原生响应，重启可按 Helper 证据完成原终态。显式取消仍按原取消语义结案。

Linux 私有 systemd 实测原 `04f1f463-a0cb-4197-a271-8ad1afb93c9d` 在 Server 退出时由 Helper 保留完整证据，Server 重启按原 ID 完成后才压缩。Helper IPC 重发该 ID 在本代及 Helper 重启后均被拒绝，原 HTTPS 持续，同键读取 Server 原成功回执。另截断原 `469bb500-0257-4560-a8dd-cb27ccced484` 的真实清理确认。Server 已有原成功回执和待确认标记，Helper 已压缩。两进程重启后只重试原清理，完成原标记，无额外 reload。真实 30 秒超时的原待核验回执拒绝清理。自然停机与取消排空复测通过，自然停机重启后核实原成功。日志为 .artifacts/mb05-nginx-retire-timeout-live.log 和 .artifacts/mb05-nginx-retire-drain-live.log。Windows Release 模块里程碑通过，专属 cgroup 清理与共享服务指纹通过。

日志为 .artifacts/mb05-nginx-retire-receipt-live.log 和 .artifacts/mb05-nginx-retire-ack-retry-live.log，归档 SHA-256 为 fb3aeac412206f5e0771a5b11795534f3345974871ee00907bf2587fc9857ef8。每个已清理 ID 仍占一个防重放目录项，目录项总量、旧终态详情的迁移清理及整机掉电恢复未验收。WebServer 保持 resident，本阶段未测量生产内存。

## MB-05 完整认证网站发布中断与恢复

在既有 Linux 测试机的私有 systemd、挂载、网络和 PID 命名空间运行完整 Release Server、生产 Helper、系统包 Nginx 和独立 Docker Engine。仅从共享 Engine 读取已缓存的 Alpine 镜像。专属应用通过认证路由创建，夹具在私有 Engine 启动带应用所有权标签的 HTTP 上游。生产证书路由生成专属自签证书，Nginx 使用证书仓库绑定，HTTPS 客户端校验证书和实际转发正文。

配置阶段在原生 reload 已生效、systemd 尚未返回结果时终止 Server。重启从原 operationId、应用历史和同键 POST 读回同一 `Interrupted / website.site_application_unverified` 回执。应用未关联，实际 HTTPS 转发持续。验证阶段在应用关联已提交、真实 HTTPS 探测到达容器后由上游暂缓响应。此时终止 Server，重启以同样三个入口读回 `Interrupted / website.recheck_required`，保留原关联，未重发探测。两条路径分别核验原 Nginx master、原上游容器和一次原生 reload。

首轮缺少专属 Nginx 密钥目录，第二轮上游容器提前退出，修正后通过。原系统包 Nginx Helper 回执路径和 Windows Release 模块边界回归也复验。Windows Docker 跨进程专项恢复原 `Interrupted` 回执且副作用一次。夹具清理专属容器和 cgroup，核对宿主 Docker 与 Nginx 进程、运行容器清单和 Nginx 配置。此阶段覆盖生产发布与自签证书绑定，公共 CA 与网站发布的同一事务、整机中断、独立执行程序集和动态休眠仍待验收。WebServer 保持 resident。

最终复验的原操作分别为 `3e073972-f898-42ab-ad2c-ff362435be20`（配置）、`4f891ce5-b66d-4475-8ca3-b4f35e94413f`（验证）和 `2ac65837-94cc-4069-82e5-1deac7f48dea`（直接重载回执）。Linux 日志为 `.artifacts/mb05-auth-website-final-website.log`、`.artifacts/mb05-auth-website-final-website-verifying.log` 和 `.artifacts/mb05-auth-website-final-receipt.log`，归档 SHA-256 为 `fb3aeac412206f5e0771a5b11795534f3345974871ee00907bf2587fc9857ef8`。Windows 日志为 `.artifacts/mb05-auth-website-win.log`，Docker 原操作 `9d25c4d8-aa4e-4f28-92c2-548bae0dfd9c`。

## MB-05 Nginx 原回执清理确认与租约

原 Server 终态及清理待确认标记先持久化，再按同一 ID 通知 Helper。清理确认丢失时，原后台 owner 在存活 Server 内重试清理，保留工作租约直至确认写盘。停机时将尚未确认的清理交给同一持久标记，重启只重试原清理。停用检查也会读取带清理标记的终态。恢复原生重载结果时，实例执行锁保护到原终态持久化；之后只保留清理工作租约，已核实的同实例操作可继续准入。

Linux 私有 systemd 的最终 Release 验证三个原 ID。原 `41069cb8-6c70-4a8c-b72d-0e7ad7c94721` 的 Helper 已压缩详情但首次清理确认被截断，Server 与 Helper 均存活。暂扣第二次清理时，原终态仍可查且管理端报告活跃租约；放行后待确认标记先落盘，租约随后归零。原 `a1759fe6-af9f-4c97-afda-e112fa5bc382` 在首次清理确认丢失后停止 Server 和 Helper，重启只重试原清理。两条路径均保持原 Succeeded、同键和 HTTPS，各一次真实重载，Helper 拒绝旧 ID 再次重载。另复跑原重载响应丢失 `9053e79e-6151-45c5-b362-870034e38af9`，两进程存活，原回执确认成功。三轮专属 cgroup 清理，宿主 Nginx、Docker PID 和配置指纹一致。

Windows Release 验证实际工作租约覆盖终态后的清理重试，并用受控后续工作确认原生对账完成后释放实例执行锁。MB-05 跨进程按原 `bc26ad61-03d3-4d55-a360-32a69f4d01cf` 读回 Interrupted，受控副作用计数为一。首轮 Linux 因夹具重试事件作用域错误失败，修正后通过。日志为 `.artifacts/mb05-nginx-retirement-final/`，归档 SHA-256 为 `4ea113c4a83357709b27078b65313e704363cddfbee97b3194f70162254b93a9`。整机掉电及防重放目录长期容量仍开放。WebServer 继续 resident，生产内存未测。

## MB-05 网站发布独立执行程序集

已准入的网站发布、具名首站续接及清理由同一个 bundled collectible 执行器运行，包含流程决策、补偿和 DNS/TLS/HTTP 验证。Host 端口只传入发布所需的应用投影、协议值和原服务调用。原协调器独占准入、停机及恢复，原台账负责检查点和终态。Nginx 提供者、Helper 原生回执、证书 owner 和应用目录继续持有各自的写入及安全权威。

执行作用域涵盖实际任务、补偿和终态提交。最后一个作用域释放 30 秒后可卸载，新操作按需加载，恢复读取原键时不会派发原副作用。跨边界失败使用共享错误字段或 Host 重建的异常。Windows Release 模块边界回归通过，56 项后台所有权检查包含真实 HTTPS 响应等待期间代次保活、停机排空、GC 收集和原回执恢复。Docker 跨进程夹具原 `6a6b82a2-c9bd-4155-bb1b-92a5b4157ed1` 为 Interrupted，受控副作用计数为一。

Linux 完整 PAM/JWT Server 使用专属 Nginx、Docker 上游和 Pebble。在原发布 `2e1ac707-f033-438c-96c6-f5704a3e0b85` 的已签发证据读取在途时，模块保持一个工作租约与原执行代次。进程中断后，启动恢复和未认证请求均未加载执行器。授权续接使用原证书 `589aca3d-fbbe-4ab5-a701-a58c7c6b1bb4`，闲时卸载期间原 Nginx PID、HTTPS 转发和上游保持。下一次具名请求唤醒新代次并返回原回执。Pebble 保持一张订单、一次签发提交，Nginx 保持一次引导和一次绑定重载。

同制品复验绑定回执应答中断 `cf5e7123-2a98-49a9-8710-b94c4aab5880`、已知 CA 拒绝与清理 `c7ff6404-3e1a-4b70-b462-da65c3dcdd46`、配置中断 `d5136a5e-d136-4c4b-bca7-d567beb32559` 和 HTTPS 验证中断 `b91afdca-2d08-4064-b1a6-60772ec104f9`。各自保留原回执，恢复未重放原签发或站点变更。五个专属 cgroup 已清理，宿主服务、Nginx 配置和宿主文件指纹一致。首轮夹具读错启动日志名，修正后通过。构建守卫反向拒绝未批准引用。最终证据在 `.artifacts/mb05-website-execution/`，归档 SHA-256 为 `794ba3419f3a1f861af46ba7d9a02ae6eba2218a460868f59c7a6c52a86e7340`。

Linux 证据包含真实工作中的保活、闲时卸载事件及按需唤醒。真实 HTTPS 任务后的 GC 收集由 Windows Release 检查覆盖。直接重载的后续边界见下节。WebServer 整体动态休眠和生产资源测量继续开放。

## MB-05 直接 Nginx 重载独立执行程序集

直接重载与网站发布复用 WebServer 的可回收执行代次和 30 秒闲时窗口。仅获准并进入原实例锁的新重载创建执行器。Helper 调用、原 operationId 和回执由 Host 提供，原 WebServer 台账独占检查点、对账和终态写入。执行代次由原后台工作持有，覆盖实际命令、异步作用域清理、终态持久化和 Helper 回执压缩确认。启动恢复只对账原 Helper 回执，同键查询读取原台账。

Windows Release 模块边界和 WebServer 专项通过。跨进程夹具以原 `13d1f234-ff24-4c45-9740-3af503e58687` 读回 Interrupted，受控副作用计数为一。

Linux 完整认证 Server 使用专属 Nginx、生产 Helper 和私有 systemd。原重载 `82644a5b-5077-4e17-8dfb-d0a3aa1cd398` 终态已落盘时，故障注入扣住原回执清理确认超过 30 秒，原工作租约和执行代次仍在。确认后观测到旧代次卸载并经测试探针确认 GC 收集。同键请求未加载执行代码，另一次明确授权的新重载才加载下一代。整个过程保持原 Nginx master、TLS 和已应用配置。首轮夹具误将新重载的清理回执纳入原 ID 阻断，修正后复验确认两项各自收尾。

同制品另通过取消 `11d74e23-072b-435d-a4d5-9628c1ee3c0e`、SIGTERM `daef5fa8-b23b-4499-aa15-1eb047ae78b3`、进程中断后匹配原 Helper 证据 `70e4fb54-bcde-45f3-9b85-60917a196dc7`，以及应答丢失后的成功和已知拒绝。Server 与 Helper 同时中断且缺少终态证据时，`52b9a55e-d960-41da-8bfb-4493ccdb3c7f` 仍待核验并拦截同实例新工作，实际重载一次。网站配置中断 `e1639586-961e-43fc-9519-75e314a0d5e1` 继续保留原回执与 HTTPS 上游，实际重载一次。七个隔离场景的专属 cgroup 均已清理，共享服务与宿主文件指纹一致。日志见 `.artifacts/mb05-nginx-execution/linux-final.log`，归档 SHA-256 为 `eefbed73be0be3a1f30805a342bba36bfd0f6ada04630bd41b865de07d7ace3b`。

WebServer 继续 resident。Nginx 安装图由 Host 持有。Linux 启停执行图见后续章节。直接 WebRoot 启用与集成见后续章节。非系统包重载的可回收边界已实现，仍待原生实机复验。整机掉电及生产内存继续开放。

## MB-05 Nginx 集成检查点与执行代次

Linux 系统包目录的集成事务复用 WebServer 可回收程序集。Host 继续持有原实例锁、配置快照、Helper 固定路径授权、站点元数据和操作台账。写入暂存锚点前持久化原操作 ID、实例身份和主配置摘要。提交锚点前记录原派发边界，Helper 的跟踪重载使用原 operationId。真实执行、暂存清理、原终态和 Helper 回执清理都在原工作租约内完成。

重启时，派发前的原操作只清理文件名和内容都匹配的暂存锚点。进入派发边界后只观察原 Helper 回执，核对原锚点与主配置快照。成功证据匹配时幂等提交实例元数据、完成原回执，再清理 Helper 回执。缺少证据、回执记录重载失败或锚点漂移时保留待核验状态和同实例派发门禁，恢复期间不删除已提交锚点，不再次重载。已知原生命令失败也保持待核验，直到有能确认现场状态的独立恢复流程。

Windows Release WebServer 及模块边界回归通过。MB-05 跨进程夹具按原 `4df37d23-6bf2-407d-819f-0a5028ba4013` 返回 Interrupted，受控副作用计数为一。执行与契约程序集的禁止依赖检查通过。

Linux 私有 systemd 使用生产 Helper 与实际 Nginx。正常集成 `b4519242-c492-4e69-9d1d-09207a6f05ae` 只重载一次，原回执清理后旧代次被 GC 收集。已派发后中断的 `cbfc73b1-c9fd-42d4-99d2-4e2a0b1c11ac` 有原 Helper 成功证据，锚点漂移期间仍拒绝新请求。复原精确锚点后，原回执完成，恢复期间加载代次和重载次数均为零。

派发前中断的 `785a57a4-659d-454a-a2da-4c612ba63562` 在暂存内容漂移时保留文件和门禁，内容恢复后只清理原暂存并完成原中断回执，实际重载为零。已知拒绝 `da83770c-2feb-4276-9d48-76e7d40205c8` 保留原 Helper 失败回执与锚点，重启后未派发补偿。各场景维持原 Nginx master 和 HTTPS，专属 cgroup 已清理，宿主服务及文件指纹一致。首次夹具用了普通私有配置目录，漂移复验另修正了实例发现入口。最终日志见 `.artifacts/mb05-nginx-integration-execution/linux-release.log`，归档 SHA-256 为 `e640ce1504c6db6e4d774c4d40b579d1e97d09f3a93f1b347f5fa9dcbf97c38e`。

WebServer 整体保持 resident。已知失败的具名补偿见下节。缺失 Helper 回执、其他平台的集成、整机掉电与生产内存继续开放。

## MB-05 已知 Nginx 集成失败的具名补偿

具备原实例 Nginx 配置写入授权的操作者，可确认后对原集成 ID 发起具名补偿。受理要求原 Helper 持久失败回执、原主配置快照、原锚点内容匹配，且原生作业空闲、无原暂存文件和受管站点。授权、确认或证据不足时拒绝，原锚点与回执保留。

原 WebServer 台账在变更文件前持久化唯一补偿 ID 与操作者。原工作 owner 排空实际删除、配置验证与跟踪 reload。重启和重复请求只查询同一补偿 ID。只有匹配的原 Helper 补偿成功回执、原配置摘要及锚点确已移除才能完成原操作。原操作保持 Failed 并报告 `webserver.integration_compensated`，关联 `compensationOperationId`。终态落盘后压缩两条 Helper 回执，保留各自的防重放标记。

Windows Release WebServer 与模块边界通过，跨进程夹具按原 `c65c9631-8b7e-4973-b188-31676ed11258` 返回 Interrupted，受控副作用计数为一。Android Nginx 管理与原回执相关 26 项通过，桌面 Release 编译通过。

Linux 私有 systemd 中，Server 在实际补偿 reload 执行时被终止。重启核对原集成 `5c5d7612-32c0-42d4-874e-2725a290da3e` 和原补偿 `cf2dbcb0-9647-4dca-844f-0dd328cbe1ab`，并完成原回执。存活 Server 的应答丢失以原集成 `390e4099-019a-4580-8485-8faed857894e` 和原补偿 `2fd91c11-84bb-4d21-bbd6-728d0d665dcb` 复验通过。各场景实际补偿一次，旧 ID 重试均拒绝再次 reload。匿名、未确认、有独立站点文件及原成功回执均未进入补偿。

同制品复验原集成成功证据、暂存清理及直接 reload 取消和 SIGTERM 排空。原 Nginx master 和 TLS 继续服务，专属 cgroup 已清理，宿主服务和配置指纹一致。日志为 `.artifacts/mb05-nginx-integration-compensation/linux.log`，归档 SHA-256 为 `40781d62a63ac1deb2445f84a9b6f42a8559caa39684194982a88ae1f5f9f811`。

缺失原 Helper 终态，或补偿 ID 已落盘但无匹配的成功回执时，原操作继续待核验。WebServer 整体保持 resident，外部 Nginx 和 TLS 由原服务负责。

## MB-05 Linux Nginx 启动、停止与重启的原回执

Linux 系统包受管实例的启动、停止与重启接入 WebServer 可回收执行代次。原 Server 台账在派发前保存检查点，Helper 以同一个操作 ID 持久记录动作和作业结果。启动与重启沿用先启用服务再派发作业的行为，两步都处于原 Helper 责任边界。Helper 只允许具名受跟踪的启停和重启。原生任务、应答、终态持久化与回执退休继续由原工作租约负责。

派发后的取消等待原生结果，已经确认的效果按实际成功或失败收尾。重启只按原 ID 与动作读取证据。原结果缺失时保留待核验和配置写入门禁，同键返回原回执。终态写入后 Helper 保留防重放标记。Nginx 服务及 TLS 由 systemd 管理，执行代次的释放独立于服务状态。

Windows Release 编译、WebServer 与模块边界专项通过。跨进程夹具按原 `2ede093a-2069-43e2-aa87-b2eca666f302` 返回 Interrupted，受控副作用计数为一。

Linux 私有 systemd 与实际 Nginx 通过四组回归。停止 `68ca6118-fddb-418a-857b-277fee1d8db1` 在真实作业挂起时收到取消，原回执保持运行，HTTPS 继续服务，作业释放后确认原停止成功。启动拒绝 `e2330e0f-5e4c-42e0-a4e5-b68b0ea7ebdb` 记录真实失败，随后启动 `f4955b18-da95-42d9-84c0-3f5667f39dde` 恢复 HTTPS。终态及退休确认后，测试观察到旧执行代次被 GC 收集，Nginx 继续运行。

实际重启 `ca728552-416a-41e1-9c89-016fb2e62223` 期间终止 Server，Helper 完成原作业并持久化回执。后继 Server 核对原 ID 和动作、保存原成功终态并退休回执，未加载执行代次。同键返回原回执，旧 ID 或错误动作无法重复执行。另一次重启 `d9721973-0f96-4f3f-9dc8-895665404404` 在实际作业中终止 Helper，随后重启两进程。私有 Nginx 已恢复 HTTPS，但原 Helper 无终态，Server 保持原待核验回执。新请求返回 409，重复原 ID、换动作和无跟踪的 Helper 调用均未增加 systemd 作业。

同批复验直接重载的取消与 SIGTERM 排空、Nginx 集成具名补偿。前两轮未知场景夹具的 HTTP 409 断言有误，修正后四组通过。专属 cgroup 均已清理，宿主共享服务与配置指纹一致。日志为 `.artifacts/mb05-nginx-lifecycle/linux-verified.log`，归档 SHA-256 为 `8985d9fa6593f158990f399136d9e56bf6b2cc1522e542829305a16411975360`。

WebServer 整体继续 resident。其他 Nginx 形态的原生启停对账、整机掉电、无 Helper 终态时的人工处置和生产内存仍待验收。

## MB-05 Linux 外部 Nginx 重载执行代次

Linux Release 使用私有挂载、PID 和网络命名空间。完整认证 Server 通过 `/usr/local/sbin/nginx` 的专属命令包装器发现外部实例，包装器向真实 Nginx 派发 reload 后暂扣命令完成。原 `768496bb-8cfc-4928-a993-e7ba4d25ad12` 已提供新的受信 HTTPS 响应，原 master PID 保持。跨过 30 秒闲时窗口，原操作仍为 Running，一个工作租约及原执行代次仍在。释放命令后原终态落盘、租约归零，观察旧 ALC 卸载并由测试 GC 确认收集。同键返回原回执，执行次数和加载次数保持。新键才加载下一代并进行第二次实际 reload。

另一轮在真实 HTTPS 已更新、命令仍在途时终止 Server。重启不加载执行代码，按原 ID `2ffccb46-536d-4ad9-b131-d3646bbfa0b7` 及同键返回 `Failed / webserver.operation_interrupted`，真实 reload 一次，原 Nginx 进程及 HTTPS 继续。外部实例只有命令完成证据，此路径的原回执记录了中断。两轮专属进程由夹具及命名空间清理，宿主服务 PID、配置与代理环境文件指纹一致。

日志在 `.artifacts/mb05-external-nginx/`，归档 SHA-256 为 `b384275ecfe7121b860fe09cf3b8b3513e0d1333a2d485a5b082ae15fbac9dfa`。Windows Release WebServer 和模块边界回归通过。此次确认外部 Linux 重载命令收尾及 ALC 收集，执行对象释放、隔离负载内存和生产收益未单独测量。外部实例的原生启停、自动续期部署及整机掉电仍开放。WebServer 保持 resident。

## MB-05 外部 Nginx 重载结果待核验

外部命令派发前在原 WebServer 台账持久化原 ID 与直接重载检查点。命令成功返回并通过原响应检查点后，原操作可持久化成功。重启遇到已派发但尚无命令确认的记录，原协调器保留 `Running / webserver.native_recovery_required`、无完成时间、同实例后台变更和配置写入门禁，以及原恢复租约。外部命令结果未知时没有 Helper 回执可查询，协调器等待同一操作的可信证据，重启只重建责任。直接命令派发后的取消或不明确失败也进入待核验。已持久化的直接命令成功阶段可在重启时补写原终态。

Linux 完整认证 Server 复验真实外部 Nginx。原 `541ff47d-b3b9-43a4-b53e-abef28cdb68b` 在 HTTPS 已更新且命令应答仍扣留时中断。重启不加载执行器，同键与按 ID 查询返回同一待核验回执。原恢复租约为一，同实例新键返回 409，真实 reload 一次，Nginx master 和 HTTPS 均保持。正常路径 `4fd70602-5fe1-4939-a139-bcd2a0fe652c` 同批复验工作租约、真实命令和响应收尾、旧执行 ALC 收集及新键加载下一代。宿主服务 PID、配置和环境文件指纹一致。

日志在 `.artifacts/mb05-external-nginx-gate/`，归档 SHA-256 为 `6f3fa944f10d83571d4db2c6eacab9ad04174e4093d435f142eeb2df15f299aa`。Windows Release WebServer 和模块边界回归通过。本轮实机覆盖派发后丢失结果的进程中断与正常重载收尾。派发后取消及已确认阶段的进程中断见下文。原操作可信确证入口、整机掉电和生产内存收益仍开放。WebServer 保持 resident。

### 外部重载的取消与已确认阶段

Linux 完整认证 Server 在真实外部 Nginx 已更新 HTTPS、命令包装器仍在运行时取消 `0c9ecbbb-ed67-4caf-9220-2ebaa1c2a0c2`。原命令进程退出后，原回执为 Running / `webserver.native_recovery_required`，工作租约仍为一。SIGTERM 后原 Server 正常退出。新进程恢复同一原回执和租约，同键与原 ID 结果相同，新键返回 409。执行代码没有重新加载，原 master 和 HTTPS 保持，实际重载一次。

第二条原 `7dd8688c-8fa9-417e-bff2-ce0b8efe22bd` 在真实 HTTPS 已更新后用私有 SQLite 写锁暂停投影。命令退出且原文件台账已写 `direct-reload-confirmed`，终止 Server。释放锁并重启后，按原 ID 与同键读回同一成功终态，SQLite 投影补齐，执行器未加载，恢复租约归零。真实重载仍为一次，原 master 持续服务。

两条路径分别记录命令进程退出、原回执与租约，未额外测量对象释放、ALC 收集、隔离负载或生产内存。专属资源清理，宿主指纹一致。日志为 `cancel-linux.log` 和 `confirm-linux.log`，位于 `.artifacts/mb05-external-nginx-gate/`。归档 SHA-256 分别为 `c21eb9389a87789c966805ae5ce23b5210472359db04069eb6bfb95615da43c3` 与 `6f9b02c32d9eef2d8024b3d9b53050946ce4ebbea201a47cdc23c4410ba3ae4d`。
