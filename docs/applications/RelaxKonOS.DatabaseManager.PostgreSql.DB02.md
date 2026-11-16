# PostgreSQL DB-02：首个单实例闭环

日期：2026-11-16。设计及唯一进度入口：[数据库引擎管理](RelaxKonOS.DatabaseManager.Design.md#9-实施拆分与唯一跟踪表)。来源依据：[DB-01](RelaxKonOS.DatabaseManager.PostgreSql.DB01.md)。

## 已实现范围

面向 Ubuntu 24.04 amd64、systemd、官方 Noble APT 来源和 PostgreSQL 16，提供单个受管实例。桌面用户在组件中心选择 PostgreSQL，解析并确认精确版本、SHA-256 和完整依赖计划，完成管理员授权后安装。随后打开“数据库引擎管理”，选择空闲端口初始化，再执行启动、停止与连接验证。

默认端口 5432，允许 1024–65535。数据固定在 `/var/lib/relaxkonos/databases/<实例 ID>/data`，配置位于 `/etc/postgresql/16/rk<实例 ID>`，socket 位于 `/run/postgresql/rk<实例 ID>`。系统服务为具体的 `postgresql@16-rk<实例 ID>.service`，使用 manual 启动策略，不随管理应用窗口关闭而停止。

仅监听 IPv4/IPv6 回环；本机管理使用 postgres peer，TCP HBA 使用 SCRAM 并拒绝非回环来源。连接验证执行真实 psql 查询，核对管理身份、数据目录、端口、16 主版本、HBA 解析和同一 systemd 主进程。尚未提供数据库用户/密码配置，因此这里的“连接已验证”指本机管理连接，不表示已配置可供应用使用的 TCP 凭据。

## 安装事务与边界

- PostgreSQL 登记为组件中心的 apt 组件，但没有加入通用 `HostPackageDefinitions.Find` 可执行映射。安装必须经过专属 `PostgreSqlComponentAdapter` 和 `PostgreSqlInstallationService`。
- 所有变更复用 `InstallationCoordinator` 的认证、controller 权限、幂等键、资源互斥、进度与持久操作记录；提升权限限定为 `NativeServiceAction` / `postgresql`。只读状态使用组件读取策略。
- Helper 只接受结构化 Install / Initialize / Start / Stop / Verify / Recover，不接受 shell、任意路径或程序参数。混入其他特权请求字段会被拒绝。
- Helper 重新检查主机、版本、来源和冻结目标。在私有 APT 索引与缓存中使用 Ubuntu archive keyring 和固定签名指纹；不修改宿主全局源。软件包与依赖冻结到精确版本和 SHA-256，下载后校验。
- 首装拒绝既有 PostgreSQL 包、集群目录、共享配置、未知状态和 dpkg 待恢复状态。仅在空白共享范围中暂存 `create_main_cluster=false`，以防包脚本建立默认 `main`。安装后实际检查没有集群，只删除本事务原样写入的临时配置。
- 独立核对 server 二进制与 postgresql-common 服务模板的包归属、精确安装版本、文件完整性及 dpkg audit。精简系统允许缺少文档/man 文件；不放宽执行文件检查。
- 初始化使用固定 pg_createcluster 参数，启用数据校验和、C.UTF-8、peer/SCRAM；替换 HBA 后才启动。后续核对配置摘要、postgresql.auto.conf、空 conf.d、实际服务模板、drop-in、MainPID 和可执行文件。配置漂移在新事务建立前拒绝。

## 持久状态与恢复

Helper 台账位于 root 私有目录 `/var/lib/relaxkonos/postgresql-control/`。写入使用临时文件、文件 flush、原子替换与目录 fsync；域锁、文件锁及共享包锁串行化原生工作。

| 路径 | 关键检查点 |
| --- | --- |
| 安装 | Prepared → Downloaded → Suppressed → PackageDispatched → PackagesVerified → Verified → 提交回执 |
| 初始化 | Prepared → InitializeDispatched → Initialized → Configured → StartDispatched → Started → Verified → 提交回执 |
| 启停/验证 | Prepared → 对应派发检查点 → Started/Stopped → Verified → 提交回执 |

每个操作绑定原 ID 与规范化请求摘要。原生响应丢失后，Core 查询原 ID 的提交回执；当前实例健康不能替代原操作成功证据。重复相同请求返回原回执，替换正文或绕过未完成事务会被拒绝。恢复动作自身也持有独立回执，便于 Core 重启后对账。

组件中心和应用维护页均提供“恢复事务”。恢复只续接有证据支持的安全检查点。以 `Dispatched` 结尾的结果未知阶段保持阻断，不重放命令、不把当前健康当成完成；本切片没有自动清理未知副作用或强制结案按钮。完整故障处置仍需管理员检查，不能宣称任意安装崩溃均可自动恢复。

客户端断线后保留原请求正文和幂等键，显式重试不会重新生成实例 ID。会话变化使旧窗口失效。组件安装状态、活动事务和恢复状态共同限制操作；读者不能提交变更。

## 验收证据

本轮使用无宿主目录挂载、无端口映射的临时 Ubuntu 24.04 amd64 容器，以真正的 systemd 作为 PID 1，调用发布的实际 Helper 执行代码。此处容器是隔离测试设施，不是 PostgreSQL 的 Docker 产品部署实现。

- PostgreSQL：`16.15-0ubuntu0.24.04.1`；首装完整脚本通过 26 项检查；最终生命周期/恢复回归通过 23 项（包含结果未知派发检查点注入）。
- 首装事务：`f24c9462-64e7-4369-acc7-9205569ff7bf`；初始化事务：`dd3dd17b-651b-46f8-8915-1ccac905e293`。
- 实例：`0c748559-571a-452b-bbf2-306d1471f0a3`；验收端口：55432。
- 安装提交后 `pg_lsclusters --no-header` 为空，临时抑制配置已清理；同一安装 ID 重试未增加修订号。
- 初始化、启停、真实管理连接、回环监听、SCRAM 设置和表中数据跨重启保留通过。
- 同键替换正文、旧修订、混合 Helper 字段、配置漂移均拒绝。将实例停止后进行 Verify，实际留下待恢复验证检查点；恢复原生运行条件后，新的 Helper 进程沿原 ID 完成恢复。
- 服务端专项检查通过 17 项，包含健康不能冒充回执、独立恢复回执、无效请求、权限/提升映射、响应丢失对账和实际共享安装队列/台账重新打开。
- 组件中心安装入口检查通过 37 项，新增 PostgreSQL 的冻结计划、取消和实际 HTTP 请求正文/操作键接线。
- 桌面专项覆盖状态动作、取消、只读、会话失效、丢失响应后原正文/键重试；中英日三语、980×700 和 760×580 概览渲染通过。
- 平台/来源/初始化纯检查通过 40 项。Server、Helper 和桌面构建通过；构建中仍有仓库既有的可空、平台分析与 NuGet 漏洞源不可达警告。

测试入口与可复现步骤见 [专项测试](../../Tests/PostgreSql/README.md)。对结果未知派发的测试通过在隔离台账注入检查点验证拒绝重放，不能据此声称已经测试真实断电。

## 保留的限制

尚未在真实 Ubuntu 24.04 验收机上部署完整 Core/Helper 并执行桌面到远端的整机联调，也未做真实断电测试。已登记主机实际为 Ubuntu 26.04，未修改其软件源、安装 PostgreSQL 或重启现有服务。本轮不宣称该主机、PG18、Windows、ARM64 或其他发行版已受支持。

若部署本变更，需要一起更新 Core、Shared 契约、Helper 和客户端；它不是仅替换某个独立 ServerModule 的变更。之后仍须按仓库要求验证 Core 与无关模块可用。

DB-03 账号/改密、DB-04 管理身份遗失恢复、DB-05 远程访问、DB-06 更新/修复/卸载及备份恢复、多实例和开机策略编辑均未完成。组件中心隐藏本切片的更新/卸载入口；不能把首次安装或重放原请求称为版本更新。
