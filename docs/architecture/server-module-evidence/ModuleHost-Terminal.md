# MB-06/07 ModuleHost 与 Terminal 验收

第三方 ModuleHost 与 Terminal 进程树、安装身份、崩溃及跨代隔离证据。 [当前进度与验收入口](../RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 场景目录

- [40. MB-06B 与 Terminal-T2：原生进程树验收（2026-11-05）](#40-mb-06b-与-terminal-t2原生进程树验收2026-11-05)
- [46. MB-06 与 Terminal 安装、崩溃和恢复续核（2026-11-06）](#46-mb-06-与-terminal-安装崩溃和恢复续核2026-11-06)
- [47. 固定启动器与跨代故障验收预检（2026-11-06）](#47-固定启动器与跨代故障验收预检2026-11-06)
- [48. Windows LocalSystem/S4U 与正式 Terminal Helper 续核（2026-11-06）](#48-windows-localsystems4u-与正式-terminal-helper-续核2026-11-06)
- [49. 三层 Terminal 树、正式制品与 Linux 固定启动器（2026-11-06）](#49-三层-terminal-树正式制品与-linux-固定启动器2026-11-06)

## 40. MB-06B 与 Terminal-T2：原生进程树验收（2026-11-05）

本轮继续 §39 的进程树缺口，不扩大 SystemPerformance S1 范围。MB-06B 交付专用 ModuleHost 树退出和同源原生证明；Terminal-T2 交付会话独占树 owner，普通关闭与根先退出均必须结束后代、原生输出和工作租约后才成功。此处记录树生命周期门槛，不等于 SDK 合作闲时退出、完整安装矩阵或内存收益通过。

### ModuleHost 原生树

Linux 专用 launcher 在加载模块前启用 subreaper。原来的专用 UID 清理会把已退出僵尸仍算责任，却未负责领养/reap，因而依赖宿主 PID 1 的行为；现在在独占 launcher 内按既有专用 UID 终止，领养该 UID 的直接僵尸并真实 reap，要求该 UID 在 `/proc` 中彻底为空后才发送 `tree-stopped`。不把此 UID 清理机制应用于共享 Server 或 Terminal 用户身份。根退出码与树清理分别核验，成功清树不能把根 exit 7 改写成成功。

Windows ModuleHost 与 Terminal 使用同源 `WindowsProcessTreeJob`。根进程挂起创建，加入不允许 breakaway 的 Job 后才 resume。真实验收发现 `ActiveProcesses=0` 可能早于已知成员进程对象 signaled；新证明在终止前捕获有界成员列表、打开原生句柄并以 IsProcessInJob 再绑定，随后等待 Job 归零及已绑定句柄终态。无效句柄、权限不足和超时不伪报成功，未按裸 PID 终止复用进程。

已通过的真实验收：Windows 当前账号 Job **11 项**；Linux 独立数字 UID 原生树 **4 场景**（正常、根先被杀、根 exit 7、控制输入断开），每场景有真实 root/child/setsid/嵌套后代，忽略 TERM 的子孙结束并 reap，无关进程存活。数字 UID 仅用于该次子进程，未新增系统账号。

Linux 同时复用既有固定 `/etc/relaxkonos/module-host.json`、Server UID 999 与已有 `fixture-host` 专用账号，使用本轮 root-owned 测试产物执行真实 Prepare/包校验/永久降权，再验收 shutdown/disconnect × plain/detached **4 路径**：受保护宿主文件、不可变包、真实 Helper socket/环境隔离、私有存储、健康检查及显式树退出/reap 均通过。此夹具由 root 显式设置配置所要求的 SUDO_UID，**没有**更换生产 launcher 或 sudoers；它验证当前 Prepare 与树实现，不声称现有 sudo 固定路径已部署当前二进制。原 fixture 数据恢复，临时包和其隔离副本移除，固定配置未修改。

### Terminal 独占树与失败责任

Linux 本地终端改用同 UID 的单会话 Server `terminal-worker` 入口；Helper 仍先永久切换到既有目标 UID/GID，再进入同源 worker。每个 worker 独占 subreaper，按直接子进程前沿取得绑定实例的 pidfd、终止并 reap，重复直到内核 ECHILD；覆盖脱离 session/process group 的后代，不扫描或清场共享用户 UID，也不在共享 Server 安装全局 subreaper。Windows ConPTY 与 ModuleHost 共用上述 Job 证明。

协议直接更新至 **1.11**，新增 `TreeFaulted` 控制帧；清理未知只通知一次，父端保留同一 worker/会话故障和工作租约，worker 保持 subreaper/残存子孙所有权，不自动退出、重试或提权。正常确认仍需 Exited、worker 成功退出和实际读写排空。Server/Helper 必须同步构建更新，不提供旧版本双解析。

Windows 当前账号 ConPTY、Linux 同 UID 本地 worker、Linux 当前 Helper 永久降权路径各 **17 项**通过：显式关闭、根先退出 0/7、detach 不结束树、真实 root/child/setsid grandchild、无关同身份进程存活、完整树结束/reap 后真实协调器租约归零及重复关闭不重执行。Windows 使用真实已绑定进程句柄；Linux 在启动时绑定真实 pidfd，结束时核验退出信号与原实例已从 `/proc` 回收。首轮 `.NET Process.HasExited` 缓存与 `/proc` 消失不一致是夹具观察缺陷，替换为内核证明，未用 Refresh 或增加等待掩盖。

Linux 自然退出场景另发现生产阻塞：内核快照已显示根为 Z、pidfd signaled，但 worker 线程仍停在 `anon_pipe_read`，标准输入 ReadAsync 的取消没有排空实际同步读取。现使用独占 worker 的 O_NONBLOCK stdin + native poll/read，循环检查取消；调用方等待原生读取实际结束后才归还清零的池缓冲并恢复标志，不用 WhenAny 遗弃读 Task。真实打开管道的空头部与部分载荷取消 **2 项**通过，均保持管道未 EOF，核验实际 ReadTask 完成和 ActiveNativeReads=0。运行期独立 pidfd 只读观察根状态，WNOWAIT 保留真实退出 0/7，最终清理阶段才 reap。

真实权限故障专项通过：独占 root 夹具创建 root 后代后永久降权，实际 pidfd 信号收到 EPERM；同源生产失败保留方法发出 `TreeFaulted`，原 worker 仍活且保持树责任。随后由独立特权测试 owner 显式通过绑定句柄结束该 worker，领养并精确终止/reap 自有 root 后代，`/proc` 消失。此人工测试收尾不是生产自动恢复，也不表示普通 UID 可以清理 sudo 升权后的所有后代。

原里程碑聚合两平台通过：resident 目录8、HTTP-01 真实 listener16、三域后台 owner32、终端帧9、终端驻留15及 Docker/证书/身份回归；Linux 加上述输入2项。默认模块全量两平台、Linux ModuleHost 实际端点/生命周期通过。Helper 与 Server 同源构建、linux-x64 两套发布通过；未新增 SystemPerformance 实现，未借本轮关闭 MB-01/08。

### 保留的后续边界

当前 Windows 机器为普通 Medium token，未置备 LocalSystem/S4U 的专用账号验收服务；当前账号 Job/ConPTY 实测不代替该安装矩阵。完整信任撤销/宿主崩溃/多代重新核验、SDK 后台责任申报与合作 drain、闲时退出/唤醒仍开放。Linux 遇到由 sudo 等改变身份、当前 owner 无权信号的后代时必须保留未知责任，不能为成功清场扩大权限。

本地同 UID Terminal 新增独占 .NET worker 有额外进程/内存成本；本轮优先确立可确认树边界，不据此宣称内存节省。最终 Linux 三个本地点样中，该 worker RSS 54356～54508 KiB、PSS 30245～30407 KiB（约53 MiB RSS/29.5～29.7 MiB PSS），不含父 Server 和整棵树；它不是峰值、稳定态、前后差值或净新增成本测量。Helper 运输进程与其子进程另行逐个记录，不能把 sudo 的较小 RSS 当作 Helper 成本或重复相加成树峰值。Terminal 会话数量/进程预算、长期压力、轻量 worker 打包及真实同构建内存/冷启动收益继续由 MB-01/07/08 跟踪。

### 证据及部署边界

证据位于本地 Git 忽略目录 `.artifacts/process-tree-20261105/`：最终 `remote-run-complete.log`、`remote-provenance.json` 的7条远端命令均0；三条 Terminal 树/权限故障、`milestones-linux.log`、模块全量、`modulehost-root-provenance.json` 与4条实际隔离路径日志保留。Windows `windows-job-tree-final.log`、`terminal-tree-windows-frozen.log`、`milestones-windows-frozen.log`、`modules-windows.log` 及构建日志拥有相应最终结果。首次失败、内核现场 `terminal-live-kernel-diagnostic.json` 和修复前 Linux 发布归档保留，不把它们算作通过。

SSH 固定核验主机密钥，归档上传校验 SHA256。用户授权测试机继续复用 `/home/nanami/relaxkonos-tests`；root-owned 当前测试产物仅放在 `/opt/relaxkonos/acceptance/process-tree-20261105`，未替换运行中的 Server/Helper、sudoers 或固定配置，未新增系统账号/服务。应用候选等既有成果保留；凭据仍在 Git 追踪之外。

## 46. MB-06 与 Terminal 安装、崩溃和恢复续核（2026-11-06）

当前阶段从 `b41f91ef` 继续，保留 §40 的已有实机进程树证据。安装 ModuleHost 在每次启动重新读取信任同意和验证包，并将同意绑定的版本及摘要交给隔离启动器。在途和后续准入按当前同意修订核对，撤销后重新授权也不能恢复旧令牌。Linux 由有权限的监控进程在真实树清理后发退出证明；Windows Helper 只在 Job 原生确认后发送认证树退出回执。控制断开、超时或单个父 PID 退出均不能替代该证明。

**已实现且已验证。** Windows 当前账户 Job 11 项通过，覆盖根先退、脱离后代、禁止 Job breakaway、原生活动数归零、Job 句柄关闭与同账户无关进程存续。Windows、Ubuntu 本地 Terminal 父端终止测试先执行真实 PTY 输入输出，只终止拥有 PTY 的父进程，再核对根、子、孙退出；Ubuntu 还核对 worker 退出、后代已 reap。Ubuntu 的本地树 17 项、真实控制输入取消 2 项通过。测试机沿用原有 SSH 主机密钥核验。原始日志见 `.artifacts/terminal-recovery-20261106`。

Windows 本地 Terminal 树 17 项、生命周期帧 11 项、驻留 15 项通过。帧测试覆盖实际阻塞的命名管道输出写入取消。沙箱内跨模块套件停在 MB-05 Certificate 恢复断言，普通 Windows 令牌下重跑完整 `--module-boundary-milestones-only` 通过。

Windows ModuleHost 专项 36 项通过。Ubuntu ModuleHost 专项 40 项和原生树专项 4 项通过。使用独立 root-owned 当前启动器、现有机器策略和专用模块 UID，完成合作关闭、控制断开和 SIGKILL 模块根进程各两组（普通后代及独立 session 后代）。注入前观察后代读写，注入后检查监控器树证明、根与后代 reap 以及该 UID 无存活进程。崩溃组保留实际非零监控器退出。旧代回调和信任修订检查由真实 Broker 端点及受控代次测试覆盖。正式固定 sudo 启动器未替换，临时源包已移除，原数据已还原。证据见 `.artifacts/mb06-linux-crash-20261106`。

**已实现、安装环境未实测。** Windows 安装夹具更新到 schema 3，覆盖正式运行时快照置备、已签名包启动、LocalSystem/S4U、已观测的实际后代输出、Core 崩溃、Helper 崩溃与信任撤销后树核对，以及原 Broker 回执和副作用不重放。Windows Terminal Helper 增加启动应答和输出/控制通道异常的清理保护，安装入口补启动拒绝后的复测。当前 Windows 执行令牌未提升，未置备 SCM 测试服务、S4U 账户或正式 Terminal Helper。管理员执行条件、独立制品和清理恢复见[模块安装验收](../RelaxKonOS.ServerModuleV2.Progress.md)和[Terminal 应用文档](../../applications/RelaxKonOS.Terminal.md)。

**仍开放。** Windows 安装后的全树崩溃和恢复矩阵、Linux 完整安装和监督器本身的故障恢复、未知责任的正式运维恢复以及跨代应用副作用矩阵。Linux Terminal 的 EPERM 实机证据沿用 §40。自动审批拒绝本轮特权复测，理由是清理失败可能留下 root 所有的测试后代。需管理员在隔离宿主运行 `--terminal-tree-privilege-checks` 并接管该夹具的清理。保留无权终止后代的未知责任，不扩大权限。SDK 后台责任申报和合作 drain 尚未验收，ModuleHost 保持 resident，未启用闲时 kill、退出或重启。Terminal 保持当前驻留策略，SystemPerformance S1 保持冻结。进程树证明、对象释放与 ALC 回收均不计为生产内存下降。

## 47. 固定启动器与跨代故障验收预检（2026-11-06）

**已验证。** 在 Ubuntu 测试机使用既有固定 SSH 主机密钥，确认正式 `relaxkonos-server` 用户的无密码 sudo 授权绑定已安装的 ModuleHost 路径和 `--isolated` 参数。以该用户通过原规则启动 0.2.7 的固定启动器，空请求返回 `module-host-isolation-failed`，退出码 77。只验证了真实安装入口与拒绝路径。共享服务和机器级策略未变更。另以普通用户创建、观察并回收实际子进程，验证新夹具在进程处于僵尸状态时保留未知责任，待原 PID/starttime 消失后才接受回收。

**夹具已实现，完整故障矩阵待执行。** `Tests/ServerModules/verify-linux-v2-response-loss.py --core-tree-crash` 在原真实 Broker 回执矩阵上加入故障前的实际后代输出、仅终止 Core 后的原 ModuleHost 树回收，以及新代次的真实输出。以原计划核对 RecoveryRequired 和禁止重放，最后用新明确批准产生一次新动作。预检要求机器级源包根和 sudo 路径都指向专用测试安装。当前 Ubuntu 的独立服务处于停止状态，机器级启动器绑定共享安装。需要已用当前制品和签名夹具置备的隔离 Linux 主机，不能在共享主机上重跑会改写固定配置的置备脚本。清理无法确认时保留证据并停止重启夹具 Core。

**Windows 待执行。** 新的按需测试机未能连接 SSH，本地 Windows 令牌未提升。LocalSystem/S4U 与正式 Terminal Helper 矩阵仍需启动测试机、核验主机密钥，并取得管理员执行条件。沿用 §46 产物和清理入口。SDK 后台申报与合作 drain 仍开放。

## 48. Windows LocalSystem/S4U 与正式 Terminal Helper 续核（2026-11-06）

使用经核验并固定 ED25519 主机密钥的 Windows 测试机。SSH Administrator 可使用已提升令牌，普通测试账户未提升。测试机无共享 .NET 10 运行时，隔离 ModuleHost 夹具使用当前源码的 Windows x64 自包含制品。验收脚本支持直接复用 SSH 的提升令牌，仍使用唯一服务、临时账户和隔离根。

正式 Terminal 使用测试机已有的 `1.0.0+c58de8e9` 安装，未替换 Server/Helper。LocalService Server 和 LocalSystem Helper 分别注入单进程终止。两轮均先经真实 Hub 与 PTY 读写取得根 shell 和实际子进程，核对父子关系及 PID/启动时刻，再分别观测旧实例消失和传输结束。恢复后通过无效目录拒绝、普通及管理员身份、真实读写、并发、退出与重连检查。最终确认 Server 停止、Helper 运行，临时 Terminal 用户无残留。验证范围为已有安装及一层后代，当前制品的正式安装与更复杂的安装后代拓扑继续开放。[Terminal 应用文档](../../applications/RelaxKonOS.Terminal.md)

ModuleHost 夹具改用当前安装计划的资源声明和审计台账格式，并按系统支持选择原生管理员或工作站 owner-device 确认。Windows Server 上没有启用工作站专用能力。未改变 SystemPerformance S1 或 ModuleHost 闲时策略，未测量生产内存收益。

当前 ModuleHost Windows 安装夹具最终 **78 项**通过。生产运行时安装函数复用不可变快照并拒绝额外文件及损坏制品。实际 LocalSystem Helper 通过 S4U 启动不同的普通模块 SID，验证私有读写、跨模块和 Helper 管道拒绝。通过已签名包、实际 Core 及真实 Broker 执行，覆盖仅终止 Core、仅终止 LocalSystem Helper、信任撤销三组。每组先读到真实后代 stdout，再观察原根及后代离场。持久回执在中断后保留 RecoveryRequired，重复查询及旧确认未重放 SCM 动作，恢复动作需新明确确认。清理报告为 true，独立查询确认临时 SCM 服务、临时管理员和 Terminal 账号无残留。原服务状态已还原。Windows 当前账户原生 Job 另通过 11 项。

本轮 Core 由提升的测试调用者运行，ModuleHost 使用隔离测试服务和生产运行时安装函数。整套正式发行安装服务、Windows 工作站 owner-device 分支和 ModuleHost 根单独崩溃的 Windows 带内回执未在本机验收。Linux §47 的跨代矩阵仍待独立安装环境。实机证据保存在 `.artifacts/mb06-windows-native-remote/remote-rerun3.log`、`.artifacts/windows-terminal-Server-crash.log` 和 `.artifacts/windows-terminal-helper-crash-retry.log`。

## 49. 三层 Terminal 树、正式制品与 Linux 固定启动器（2026-11-06）

**Windows Terminal 实机通过。** 在已有正式安装 `1.0.0+c58de8e9` 上分别只终止 Server 和 Helper。实际孙进程写入 PTY 标记，故障前核对两级父子关系及根、子、孙三个进程的启动时刻，故障后确认三个原实例均消失，并分别观察传输结束。Helper 组完整通过恢复。Server 组恢复检查在最终关闭会话时遇到 SSH 转发断开，独立复测完整通过。恢复包括无效目录拒绝、普通和管理员执行、真实读写、并发及重连。独立复查确认原 Server 停止、Helper 运行，临时账号为零。当前制品的正式 Terminal 安装待验收。[应用文档](../../applications/RelaxKonOS.Terminal.md)

**Windows 正式制品已核验，正式发行服务链待执行。** 生产打包生成不同源码的 Windows x64 自包含 0.2.7 和 0.2.8，两个包均通过生产清单核验，相关本地回归 44 项通过。测试机已有正式服务名，验收入口按设计拒绝接管。该 VMware 客体没有可用的嵌套虚拟机工具。需在正式服务名未占用的隔离 Windows 实例运行安装、升级、恢复与清理。[正式制品及执行条件](../RelaxKonOS.ServerModuleV2.Progress.md)

**Linux 固定启动器两代实机通过。** 在私有挂载命名空间中使用生产安装脚本生成策略和精确 sudo 规则，以正式 Server 账号通过实际 `sudo -n --` 启动当前 launcher。两个代次都完成实际后代读写，分别以合作关闭和控制断开结束。独立 session 的后代忽略 SIGTERM，仍获得明确树证明并核实专用 UID 无残留进程。宿主正式策略哈希保持一致。原始证据见 `.artifacts/mb06-linux-fixed-namespace.log`。

**Linux 固定启动器跨代回执实机通过。** 私有命名空间中使用当前 Core 与实际 Helper、PAM、签名包和固定 sudo 启动。待原回执为 Executing、独立 systemd 测试服务正在停止后，仅 SIGKILL 原 Core，确认原根和独立 session 后代已回收且专用 UID 无残留。重启 Core 后读到同一 RecoveryRequired 回执，旧批准与重复读取没有重复服务动作。新代次完成真实读写，新明确批准只执行一次新动作。最后确认新代树回收、宿主正式策略哈希保持一致。夹具运行记录见 `.artifacts/mb06-linux-fixed-core-recovery.log`，现场标识 `core-BNBKVP3J`。此结论覆盖本次隔离固定启动器及真实持久回执路径，宿主共享发行服务未参与故障注入。

缺少实际退出证明时保留未知清理责任。ModuleHost SDK 后台申报及合作 drain 仍开放，未启用闲时 kill、退出或重启。SystemPerformance S1 保持冻结。本轮未测量生产内存变化。
