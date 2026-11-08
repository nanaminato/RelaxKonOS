# MB-05 Docker Engine 与 Compose 验收

隔离 Engine、Compose、真实工作租约及原操作回执。跨域响应机制另见联合阶段验收。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [45. MB-05 Docker 应用层在途中断与回执恢复（2026-11-06）](#45-mb-05-docker-应用层在途中断与回执恢复2026-11-06)
- [MB-05 Docker 本地跨进程回执续核](#mb-05-docker-本地跨进程回执续核)
- [MB-05 真实 Engine 与工作进程崩溃联合验收](#mb-05-真实-engine-与工作进程崩溃联合验收)
- [54. MB-05 独立 Engine 重启与持久卷](#54-mb-05-独立-engine-重启与持久卷)
- [MB-05 完整 Server 认证回执](#mb-05-完整-server-认证回执)
- [MB-05 Docker Compose 准入与执行作用域](#mb-05-docker-compose-准入与执行作用域)
- [MB-05 Docker Engine 离线时的原启动对账](#mb-05-docker-engine-离线时的原启动对账)
- [MB-05 Docker 变更返回后的 Engine 观察中断](#mb-05-docker-变更返回后的-engine-观察中断)
- [MB-05 Docker 用户取消与观察断线交叠](#mb-05-docker-用户取消与观察断线交叠)
- [MB-05 Docker 变更 CLI 在途取消与 Engine 观察中断](#mb-05-docker-变更-cli-在途取消与-engine-观察中断)
- [MB-05 Docker CLI 清理未知的持久门禁](#mb-05-docker-cli-清理未知的持久门禁)
- [MB-05 Docker Compose 只读 HTTP 工作责任](#mb-05-docker-compose-只读-http-工作责任)
- [MB-05 Docker Compose 独立执行程序集](#mb-05-docker-compose-独立执行程序集)

## 45. MB-05 Docker 应用层在途中断与回执恢复（2026-11-06）

Compose 命令取消或超时时，生产执行器终止尚在运行的 CLI 进程树，并等待 CLI 退出和输出流排空后返回。无法核实清理时，栈协调器把异常交给工作 owner 隔离，保留原活动台账。启动对账的只读观察若无法核实 CLI 清理，同样保留责任。关闭时已取消且本地清理完成的操作保留原 Running 回执，供下次启动观察 Engine 后记录 Interrupted。

Ubuntu 测试机使用固定 SSH 主机密钥、现有 sudo 凭据、缓存 Alpine 镜像 ID 和随机专属 Compose 项目。Linux Release 的 --stack-live-only 通过真实 DockerStackOperationCoordinator、DockerComposeService、持久台账和 resident 工作租约执行。容器的 TERM 日志证明 Engine 已接受停止请求，且当时实查 Docker CLI 仍在运行、回执为 Running、代次有工作租约。调用协调器 StopAsync 后，实查原 CLI 已退出，工作租约已归还，原操作仍留待对账。新协调器重新打开同一台账，在工作租约保护下观察真实服务并以原 operationId 记录 Interrupted / docker.stack_interrupted。最终项目为 ad04live93e97f1b，恢复操作为 de667356-ceb6-4b4b-a3f6-7bae6bc610b9。

测试等待 Engine 已接受的停止动作自行结束，再用独立新操作显式启动原容器。以原停止请求的幂等键重试，返回同一 Interrupted 回执，容器继续运行且 TERM 记录只有一次。按项目标签确认容器、网络和卷已清理。共享 Engine 未重启，宿主 AppArmor 未变更。本机 --stack-operations-only 台账专项通过。

此切片验收了真实 CLI 在途保护、协调器停机排空与启动恢复。协调器在同一测试进程内重建，未注入整个 Server 进程崩溃或 Engine daemon 重启。Engine 已接受的动作可在 CLI 断开后继续完成，恢复快照不保证 Engine 已稳定。CLI 清理无法核实的保护路径已实现，未做真实进程清理失败注入。HTTP 响应排空、真实 CA/Nginx 应用层联合矩阵及生产内存仍开放。实机原始日志与制品哈希见 .artifacts/mb05-docker-live.log 和 .artifacts/mb05-docker-provenance.json。

## MB-05 Docker 本地跨进程回执续核

从 `d255af3e` 继续。Windows Release 聚合入口 `--module-boundary-milestones-only` 通过，包含新增 `--docker-process-recovery-only` 夹具。夹具在独立进程使用生产栈协调器与持久台账，替身 Compose 在工作租约内向临时文件记录一次部署后阻塞。父进程确认原回执为 Running 且副作用见证已落盘，再强制终止原进程并确认异常退出。新进程读取同一台账，执行一次只读观察，以原 operationId 记录 Interrupted。相同请求键返回与恢复查询一致的回执，部署执行次数为零。父进程再次打开台账，确认终态与观察结果持久化，副作用见证仍只有一次。

本轮日志 `.artifacts/mb05-release-final.log` 中的 operationId 为 `ac1cda96-23e8-453a-8b5c-b69cf8d919d1`。沙箱令牌下的既有聚合回归在证书恢复断言处失败，普通 Windows 令牌下完整 Release 重新构建和回归通过。该夹具覆盖跨进程持久责任与幂等回执，外部动作由文件见证替身提供。真实 Engine daemon/Server 联合故障、HTTP 响应排空、CA 与 Nginx 应用层中断仍开放。

## MB-05 真实 Engine 与工作进程崩溃联合验收

Linux x64 Release 在既有测试机的专属项目 `ad04live4722012f` 通过。独立测试进程使用生产 Docker 栈协调器、Compose 服务及常驻工作租约，提交真实停止请求。父测试进程观察容器收到 TERM，确认对应 Docker CLI 仍在运行、原回执已持久化为 Running，再仅终止该测试进程。确认工作进程异常退出、原 CLI PID 消失且 Engine 报告容器 exited 后，新协调器才读取原台账并观察 Engine。原 operationId `a73b18b4-f9c3-40cf-a18e-c017fc08efb5` 持久化为 Interrupted。显式启动原容器后，同键请求返回与持久回执一致的结果，容器继续运行，崩溃组只产生一次停止见证。

同一轮通过原正常停机排空、受控跨进程台账回归和专属项目资源清理。Windows Release 聚合回归通过。Linux 制品归档 SHA-256 为 `c48dc98798a2f998469c59946887b0d15408f2e23fa2b45d521b0bf21dd70323`，原始证据见 `.artifacts/mb05-linux-crash-run.log` 和 `.artifacts/mb05-crash-windows.log`。共享服务持续运行。测试进程承载生产 Docker 协调器，完整 Server HTTP 传输、Engine daemon 重启、CLI 无法核实退出、CA 与 Nginx 应用层中断仍开放。

## 54. MB-05 独立 Engine 重启与持久卷

既有 Linux 测试机的私有挂载和网络命名空间运行 Docker Engine 29.8.2。私有挂载隐藏宿主 containerd socket，日志确认 daemon 连接专属 exec-root 下的 containerd。Docker socket、客户端配置和 vfs 数据目录均专属。镜像从共享 Engine 已缓存的 Alpine 标签只读导出，并导入专属 Engine。首轮日志发现 daemon 默认尝试使用共享 containerd，独立验收改为隐藏宿主 socket。离线导入后使用导出的标签处理两种镜像存储返回的 ID 差异。

专属 daemon 正常停止后通过原数据目录重启。同一容器 27703da4ed8b0466ea8952768a22b1ace3ea9673df0ef256d8ceace762925a48 按 restart policy 重新运行，启动时间更新，命名卷中的原内容仍可读取。容器与卷随后清理。证据为 .artifacts/mb05-private-engine-workload-verified.log。此轮验证了外部运行设施，原操作回执与真实 daemon 中断的联合验收仍开放。

### 执行中 Engine 与操作进程中断

独立 Engine 上真实停止请求进入容器 TERM handler 后，终止专属 daemon 和管理进程。新进程在原 ledger 读取 Running，daemon 重启且 witness 恢复运行后，通过生产协调器观察原项目，按原 operationId 1f3aa469-0012-4b43-86e6-f51e2ef68bf1 持久化 Interrupted。同键重试返回原回执。停止日志在恢复前后均为两条，容器持续运行。首轮按一次停止信号断言失败，最终以 daemon 中断完成后的实际计数为恢复基线。专属项目容器和卷已清理。

Linux x64 Release 日志为 .artifacts/mb05-daemon-operation-final.log，归档 SHA-256 为 e044cb4bcfc2563a67ce2eac59bc413a8a2c4eb253b4789326286a074e973746。此次联合验收使用 daemon 正常终止信号和管理进程强制终止，完整 Server HTTP 传输仍待验证。

### dockerd 强制终止

同一实机增加私有 PID 命名空间和 cgroupfs。真实停止请求进入容器后，专属 dockerd 收到 SIGKILL 并以 137 退出，管理工作进程也被终止。新 daemon 和协调器从原数据目录及台账恢复，原操作 f053458d-6456-421a-8deb-978119d2894e 持久化 Interrupted。同键重试返回原回执，容器持续运行，恢复前后的停止日志均为两条。测试移除专属容器和卷，卸载残留数据挂载并删除临时目录。

Linux x64 Release 实机日志为 .artifacts/mb05-daemon-kill-final.log，归档 SHA-256 为 273f8a3dde8229c9a1884fa5965032b0db1b07fc2d26e5760c889eba714f9d87。首次私有 PID 运行遇到 systemd cgroup 的 PID 映射问题。切换 cgroupfs 后通过业务断言，修复残留挂载清理后整套通过，早期遗留目录已清除。完整 Server HTTP 响应排空仍开放。

### Docker HTTP 客户端断开

专属 Engine 与回环 Kestrel 使用生产 Docker 路由、协调器和 CLI。夹具身份及响应头屏障用于在 HTTP 202 写出前触发真实客户端断开。容器已收到 TERM，原回执为 Running，工作租约仍在。Kestrel 观察到 RequestAborted 后，真实停止操作继续执行，终态落盘后释放工作租约。原操作 e563f5a7-2316-4269-84e5-6bf8741c5b68 持久化为 Succeeded，HTTP 同键重试返回同一回执。相对恢复后的停止计数仅增加一次。

Linux x64 Release 日志为 .artifacts/mb05-docker-http-live.log，归档 SHA-256 为 dfe0b1fa60f8a4d3db103639cb5a784c5f490ed17501dd9e9db10000f76f3233。本轮由 Kestrel 测试宿主承载生产路由，使用观测租约和夹具身份。完整 Server 启动、认证和模块响应排空仍开放。

HTTP 断开使用生产 Docker resident 工作租约复测通过。客户端断开时 ModuleResidencyCoordinator 仍报告活动工作，终态事件发生在租约保护下，持久回执落盘后排空。原操作 f7503653-b5ce-4cbc-b07e-da09b4874237 同键重试成功，停止次数未增加。Linux x64 Release 日志为 .artifacts/mb05-docker-http-lease-live.log，归档 SHA-256 为 d271d7731579b3c16fea1c546b8c4b14306a932e21f2860d28d8a50df7e1d87e。Kestrel 测试宿主仍提供身份和响应屏障，尚未验收完整 Server 启动及认证链。

## MB-05 完整 Server 认证回执

Linux x64 Release 在私有 Engine 和回环网络中启动完整 System Mode Server，先确认匿名 Docker 请求被拒绝，再通过真实 Unix socket peer 校验及固定 PAM 认证操作登录。专属 Helper 只派发认证，其他操作拒绝，避免触及共享服务。真实 Docker 停止完成后重启 Server，再次登录并同键取回原 Succeeded 操作 fb400c0c-210b-42ed-84b9-b7d2ba085362。停止日志仅增加一次。日志为 .artifacts/mb05-auth-only-helper-live.log，归档 SHA-256 为 d779ce3adafcd0ee22836d2df45668afcd5ebcbfaf744c990e0adf47deac1c06。先前 in-process PAM 的 login 和 sshd 路径返回 401，生产 Helper 固定 PAM 路径通过。本次完整认证回执与响应屏障实测为独立路径。

Linux 完整认证 Server 复测中，独立 TCP 连接发送真实停止请求，另一连接按原幂等键确认受理后，客户端关闭原连接且不读取响应。原操作 751cc66c-faad-4626-8c6e-e0f974db740d 完成并落盘，Server 重启和重新认证后返回原 Succeeded 回执。专属容器停止日志仅增加一次。日志为 .artifacts/mb05-auth-response-loss-live.log，归档 SHA-256 为 c518fb25902d0060a3a196c9d694da8ee1e97c8656d785a15fa9b7a7253bdec4。本项验证客户端丢失响应，不提供完整 Server 写出任务阻塞期间的租约观测。

Linux 完整认证 Server 的运行中崩溃路径通过。客户端未读取受理响应，通过原幂等键取到 operationId，确认专属容器的 TERM handler 已执行且原回执仍为 Running 后强制终止 Server。重启并重新认证后，同键返回原操作 39baf3ad-13d6-4333-9649-0310e61778b4 的 Interrupted 回执，停止日志只增加一次。日志为 .artifacts/mb05-auth-server-crash-live.log，归档 SHA-256 为 40c4adf9834c82b3d30cb6e3f50e4530b2ec5ac5b53430b23fa619e075e88416。

完整 Server 去重续核在原 Engine 停止完成后显式重新启动同一专属容器，再启动 Server。登录后同键提交及按 ID 查询均返回原 Interrupted 操作 495e7459-98db-4e69-9d34-cf534fb9cace。容器仍在运行，停止日志只增加一次。Linux 实机日志为 .artifacts/mb05-auth-restarted-witness-live.log，归档 SHA-256 为 930f32b0ab2a0ebeae16a0a272a9b6b7cb45863693a3770e9d42314d4aaa7572。

## MB-05 Docker Compose 准入与执行作用域

原常驻协调器独占准入、项目互斥、回执和恢复。新操作持久化 queued 后才解析 Compose 执行图，异步清理结束后才写终态。清理失败保留原活跃回执和故障责任。启动仅为原 Running 项创建只读观察作用域，已知键重试不创建执行作用域。Core Engine、外部容器及卷由原设施持有。

Windows Release 模块边界与本地跨进程回归通过，覆盖原工作租约与回执跨异步清理、原 operationId 恢复和副作用计数。Linux x64 Release 在独立挂载、网络、PID 命名空间和私有 Docker socket 上通过真实 Engine 与完整 Server 验收。停止期间强制终止 daemon 和工作进程后，原操作 `ac82fe2d-9996-4012-a68d-5381c630801b` 恢复为 Interrupted，观察作用域一次、重放作用域零，见证容器的停止计数前后同为 2。完整 Server 经认证提交停止请求并丢弃原响应，在真实停止过程中强制终止 Server；重启后同键返回原 `2408d31c-3dfd-4ee8-9650-a08246796f28` Interrupted，停止计数只增加一次。私有 Engine 重启也保留原容器和命名卷，夹具清理专属资源，宿主共享服务 PID、容器和环境指纹保持一致。首轮包装器因 CRLF 在启动 Engine 前退出，转为 LF 后通过。证据为 .artifacts/mb05-docker-scope-live.log，归档 SHA-256 为 e0666d40f74d1058d6ca6d940a33903c9eff280cdef33b06a72c6bb9ecfba6b8。Docker 模块保持 resident。独立执行程序集、完整故障矩阵和生产内存测量仍开放。

## MB-05 Docker Engine 离线时的原启动对账

`docker ps` 非零退出时，栈服务读取返回 `503 / docker.unavailable`。启动对账仅在实际观察完成后结束原 Running 操作。Engine 不可达时，由原协调器持有恢复租约和项目互斥，保持原 operationId 与原键可查。原 owner 在独立观察作用域内重试只读查询，异步清理和终态持久化完成后才归还租约。停机等待实际观察结束，仍待核验的操作由原台账交给下一次启动。原 Queued 操作按未派发中断处理。

Linux 私有 Engine 中，完整 Server 经 PAM 登录提交专属容器的真实停止请求。在原操作为 Running 且容器已接到停止信号时终止 Server。原停止结束后关闭专属 daemon，再启动 Server。Engine 离线时服务查询返回 HTTP 503，同键返回原 `3810f922-ce3f-499a-a42e-1e6f54dbf3e8` Running，新键对同项目返回 HTTP 409。启动原 daemon 后，同一 Server 取得实际容器观察并将原操作持久化为 Interrupted。同键与按 ID 查询一致，专属容器保持运行，停止计数未增加。原 daemon 重启和命名卷、专属容器清理同批通过，宿主服务 PID、容器清单及代理文件指纹一致。

首轮夹具缺少私有持久状态目录，完整 Server 在 Proxy 安全启动门禁处终止。复验使用私有 `/var/lib`、`/etc` 覆盖层和独立发布的完整 Server。Windows Release 模块边界回归通过，受控用例验证恢复 worker 在 Engine 离线时停止，下一任 owner 接续同一记录。原 `5be1e27c-7be8-4cf8-a990-c5ea69065f90` 跨进程返回 Interrupted，受控副作用计数为一。日志在 `.artifacts/mb05-engine-offline-recovery/`，归档 SHA-256 为 `c38ed10257a4d99545b0b0292d61686872c0e1c4e03547aa848d8e5782610257`。Docker 保持 resident。存活 Server 在原变更完成后丢失 Engine 观察的连续性、整机掉电与独立执行程序集继续开放。

## MB-05 Docker 变更返回后的 Engine 观察中断

变更 CLI 返回后，首次 Engine 观察失败会在原台账保留 Running / Observing 和原项目互斥。协调器结束原变更作用域，继续持有同一个工作租约，并在独立作用域内重试只读观察。原操作进入只读恢复时关闭取消入口。观察作用域清理并写入原 Interrupted 终态后才归还租约。Host 停机时等待在途观察清理，仍待核验的原记录由下一次启动接续。

Windows Release 模块边界回归通过。用例验证原回执和工作租约持续，同一项目的不同键拒绝准入，Engine 恢复后只读终结，原变更调用一次。MB-05 跨进程原 `75eb7cdf-99a6-4437-8fd0-286d669b7080` 返回 Interrupted，受控副作用计数为一。

Linux Release 使用完整 PAM/JWT Server、独立 Docker socket、私有 Engine 和专属 Compose 项目。原容器收到真实停止后，测试屏障暂扣首次 `docker ps` 观察调用。关闭专属 daemon 后放行真实 CLI，命令非零退出。Server 保持同一进程，原 `a8c0b873-c124-42c6-a4a6-d190409873c5` 仍为 Running / Observing，同键返回原回执，列表返回 503，同项目新键返回 409。Engine 重新启动后，同一 Server 以真实观察提交原 Interrupted 回执，原 ID 与同键查询一致。容器显式重新启动后保持运行，停止计数未增加。

同一制品复验 Engine 离线时的完整 Server 启动，原 `917dcfc6-9308-4fe6-88b2-d4c0a8a6a765` 恢复为 Interrupted。专属容器及卷清理完成，宿主服务 PID、运行容器清单和代理文件指纹一致。日志在 `.artifacts/mb05-live-observation-outage/`，归档 SHA-256 为 `3a578140199fe33ee8bda9b2d0c46fe8585e903aceb175b3a7290a45a0356f3d`。本轮覆盖正常返回后的首次观察丢失。用户取消与首次观察失联同时发生、整机掉电及独立执行程序集仍开放。Docker 保持 resident。

## MB-05 Docker 用户取消与观察断线交叠

变更可能已派发时，用户取消先让原 CLI 任务及输出清理结束，再使用 Host 停机信号约束只读观察。Engine 可观测时沿用原取消终态。观察失败时保留原 Running / Observing 回执、诊断、项目互斥及工作租约。Engine 恢复后，同一个 owner 只读观察、清理作用域并以原 operationId 写入 Interrupted。Host 停机可交接仍待观察的原记录。

Windows Release 模块边界回归通过，含在首个观察阻塞时取消、原项目继续受保护、恢复后原变更仍只有一次。MB-05 跨进程原 `5f7f3cc4-878f-4df4-8813-91ca210b750e` 返回 Interrupted，受控副作用计数为一。

Linux Release 使用完整认证 Server 和专属 Engine。原容器真实停止后，夹具挂起第一次观察进程，停止专属 daemon，再通过生产 HTTP 路由取消。Server 返回原 `d754692b-b1c2-4d88-9c9c-18633f8e6728` Running 回执，首次观察进程已回收，随后真实 `docker ps` 非零退出。原回执保持 Running / Observing，诊断显示观察失败，列表返回 503，同项目新键返回 409。专属 Engine 恢复后，同一 Server 完成只读观察并提交原 Interrupted 回执，按 ID 和同键查询一致，容器保持运行，停止计数未增加。

同制品复验了未取消的首次观察断线 `a5aafdfa-7349-413b-993a-809c08247a3d` 和完整 Server 离线启动 `38b69a9a-5cb6-4712-8325-798951830581`。专属项目和卷已清理，宿主服务 PID、容器清单及代理文件指纹一致。日志位于 `.artifacts/mb05-cancelled-observation/`，归档 SHA-256 为 `d016b37fadfcfc9996cd527288cfaedc6b5113f9a1a6e108b029b1a50acaa5b7`。验收限变更返回后的取消窗口。变更 CLI 自身无法证实清理、整机掉电、生产内存和独立执行程序集仍开放。Docker 保持 resident。

## MB-05 Docker Compose 只读 HTTP 工作责任

Compose 列表、预览、定义和服务读取在认证后先取得 Docker 工作租约，再解析独立请求作用域。实际 CLI 及输出排空、HTTP 响应和异步作用域清理均完成后才归还租约。CLI 进程清理无法核实时，原驻留协调器保留故障责任。Proxy 读取复用同一响应作用域机制。Compose 变更与恢复继续由原持久台账和协调器执行，共享 Engine 传输由 Core 持有。

Windows Release Kestrel 夹具验证匿名请求无准入、四条路由的实际任务与响应、异步清理和未确认 CLI 清理的故障租约。模块边界回归通过，MB-05 跨进程以原 `d665dd05-3d97-4cc5-a29c-a44afda94652` 返回 Interrupted，受控副作用计数为一。

Linux 私有 Engine 以完整 PAM/JWT Server 验证服务读取。夹具收到真实 `docker ps` 输出后暂扣交付，模块接口显示一个工作租约。断开 HTTP 后，CLI 包装进程已退出、工作租约归零，新请求取得原容器服务。停止见证次数保持。随后复验原用户取消 `86c05a1a-a0cf-46b6-a5cd-85aa4064b61f`、存活 Server 观察断线 `93e69d33-cb16-44ad-9bdc-926578edca1b` 和重启后 Engine 离线 `72f0b280-d9a7-4435-9168-e2fcd8a98010`，按各自原回执完成只读对账，停止副作用均未重放。三个专属 Engine 场景均清理容器和卷，宿主服务、容器清单与代理文件指纹一致。

首轮混用旧协议与测试 Helper 制品，PAM 登录返回 HTTP 503。统一制品版本后通过，日志在 `.artifacts/mb05-compose-read/`，验收归档 SHA-256 为 `f5753c5aaea7ae1c014a5a57b17097078a8c27f8e6d6c799a82de628ec967cb9`。真实断线验收覆盖服务读取，其他三条路由的任务与响应边界使用本地夹具验证。Docker 保持 resident，Compose 独立执行程序集、ALC 回收与生产资源测量继续开放。

## MB-05 Docker Compose 独立执行程序集

固定 CLI 调用、文件操作及输出解析进入 bundled collectible 程序集。原 HTTP 路由和栈协调器通过同一个作用域入口使用该程序集。Core 继续持有共享 Engine 传输、唯一原操作台账和终态确认。共享契约保留 DTO、领域错误与准入校验规则。执行项目及契约项目的构建守卫分别约束依赖，Server 发布目录携带独立执行 DLL。

请求作用域在实际 CLI、HTTP 响应与清理结束后释放执行对象。后台作用域完成执行清理后由原 Core 租约负责终态落盘。最后一个作用域离开 30 秒后卸载代码，新工作按需唤醒。代次预算保护回收滞后时的可用性。预览文件或 CLI 清理无法核实时，执行代次和原工作责任继续保留，拒绝新执行。跨边界失败只传递共享错误数据或 Host 生成的异常。

Windows Release 模块边界回归通过。独立检查在保留成功与失败 Task 的情况下收集五个代次，活动作用域跨闲时窗口保持原代次，受控清理未知检查保留故障代次。MB-05 跨进程按原 `e392d5da-42c1-48ae-b491-a92435198b9d` 返回 Interrupted，受控副作用计数为一。

Linux Release 的五代收集检查同样通过。完整认证 Server 在专属 Engine 中暂扣真实 `docker ps` 输出，跨 30 秒观察窗口保持原代次与一个工作租约。客户端断线后 CLI 包装进程已退出，工作租约归零，原代次报告闲时卸载，新请求加载下一代并读到原服务。新代次也通过真实列表、原定义读取及预览，预览目录确认清空。Core 的原操作 `e24ef921-2592-4b39-a596-df1f772d0c91`、`20168d97-2f95-43b1-99b5-d6c430e43845` 和 `3f21603a-a081-4e11-acc4-bb1f1af2b6ed` 分别在取消、存活 Server 失联和重启后 Engine 离线场景按原 ID 对账，停止未重放。专属资源清理完成，宿主服务、容器和代理文件指纹一致。

首轮外层握手在空闲卸载窗口结束前停止专属 Engine，修正等待条件的期限后通过。构建守卫反向拒绝额外程序集引用。最终日志在 `.artifacts/mb05-compose-execution/`，Linux 归档 SHA-256 为 `7190796694ca3ffd9f27e3c898d7258b25f9e0d2be50976c25f421dd09020816`。未知清理保留代次在本地检查验证。Docker 管理模块继续 resident，生产资源测量及其余管理执行图继续开放。

## MB-05 Docker 变更 CLI 在途取消与 Engine 观察中断

Linux Release 完整认证 Server 使用专属 Engine 和 Compose 项目。容器已记录一次真实停止信号、原 `docker stop` 进程仍存活且回执为 Running / Applying 时，通过原 HTTP 接口取消 `fc83c587-fb62-4494-8826-5d84ed842886`。取消响应保留原 ID。首次观察屏障验证变更 CLI 已退出，再等待容器自行结束原停止，随后关闭专属 Engine 并放行真实观察命令。Engine 离线时原回执保持 Running / Observing，诊断记录观察失败，原工作租约仍为一。同键返回原回执，服务读取返回 503，同项目新键返回 409。

重启专属 Engine 后，同一 Server 按原 ID 只读对账并持久化 Interrupted。按 ID 与同键读取的终态一致，工作租约归零。显式启动的见证容器保持运行，测试在原 CLI 的执行入口与容器日志分别核对停止派发、停止信号均只增加一次。同批独立 Engine 与命名卷重启验证通过，原 daemon 中断操作 `abce395c-1f22-4feb-ae1f-d848ee2e3dbf` 的恢复作用域为一次只读观察，执行作用域为零。专属容器和卷已清理，宿主共享服务、运行容器清单和代理文件指纹一致。

首轮将观察失败文件视为台账已经更新，复测改为等待原诊断和阶段。第二轮 daemon 停机与见证容器退出重叠，停止计数断言失败。最终轮等待原容器退出才让 daemon 停机，观察一直扣留在屏障内。复现模式为 `RELAXKONOS_TEST_DOCKER_OBSERVATION_OUTAGE=cancel-mutation`。日志在 `.artifacts/mb05-cancelled-mutation/`，Linux 归档 SHA-256 为 `6293af77038d7b5fc5ce6ab5581ee402f850f316553bff00878627b232187f02`。Windows Release 模块边界通过，跨进程原 `fc729a51-d953-46ca-8190-4cde784c24dc` 读回 Interrupted，受控副作用计数为一。

本轮实测真实 CLI 退出、原租约和专属资源清理。独立 Linux 回归另验证五个闲时代次的 ALC 收集，覆盖保留 Task 和未知清理时钉住代次。本次实机故障路径未单独测量执行对象回收、ALC 收集或隔离负载内存。变更 CLI 清理不可核实仍仅有本地故障证据，整机掉电和生产内存收益仍待验收。Docker 保持 resident。

## MB-05 Docker CLI 清理未知的持久门禁

原 Compose 台账在发送用户取消前保存清理检查点。实际 CLI、输出与执行作用域确认排空后清除此检查点。无法确认清理时保留原活动回执、诊断和项目互斥。原进程的工作租约保留故障责任，重启由原协调器重建工作租约并等待清理证据。此类原记录跳过 Engine 观察对账。认证同键 POST 可直接读取原台账，原执行仍由故障租约或重建的租约负责。尚无正式的证据提交及解除门禁入口。

Linux Release 完整 Server 在专属 PID 命名空间执行真实 `docker stop`。夹具子进程持有原输出管道，真实 CLI 及容器停止已结束，原任务仍为 Running。取消后生产执行器等待输出排空超时，原操作 `1486b548-301a-4e7d-ad5b-5c2f88e51385` 保留无完成时间的回执及一个租约。杀死 Server 时持管道的进程仍活着。重启原 Server 后，同键 POST 和按 ID 查询返回同一回执，新键返回 409，一个恢复租约接续。夹具释放并确认子进程退出，原回执仍等待可信清理证据。CLI 派发和容器停止信号各一次。专属 Engine 清理容器及卷，宿主服务、容器清单和代理文件指纹一致。

首轮发现同键 POST 的 202 响应还会申请新模块租约，故障隔离使它返回 503。调整为读取持久回执后复验通过。日志在 `.artifacts/mb05-cli-cleanup/`，Linux 归档 SHA-256 为 `559749b0833df463b71f68917d1af9506b425290865952a372cff68df399dcfe`。Windows Release 模块边界通过，跨进程原 `2cef56de-0335-4f98-86bb-1f93b035e48a` 返回 Interrupted，受控副作用计数为一。Linux 独立 ALC 回归验证五代收集及未知清理保留代次。实机路径只确认两个实际 CLI 相关进程退出及责任保留，执行对象释放、故障代次的 GC、隔离负载和生产内存收益未单独测量。Docker 保持 resident，整机掉电未验收。
