# PostgreSQL DB-04：管理身份遗失恢复

更新：2026-11-16。状态：Ubuntu 24.04 x64 / PostgreSQL 16 托管单实例已实现，客户端已接线，真实 systemd/PostgreSQL 隔离验收通过。真实 Ubuntu 24.04 主机完整 Core/Helper/GUI 联调与物理掉电验收尚未完成。

## 实际身份模型与范围

DB-02 的固定管理员是数据库 `postgres`（内置 OID 10），由宿主 `postgres` 系统账号通过本机 Unix socket 的 peer 认证进入；没有产品保存的数据库管理员密码。DB-04 恢复的是该角色 LOGIN、SUPERUSER、CREATEDB、CREATEROLE、INHERIT、REPLICATION、BYPASSRLS 属性、连接限制和有效期，并将管理员密码清空。应用账号正常改密仍由 DB-03 负责。

仅接受当前实例 ID、修订、受管软件包/二进制/服务模板、数据路径及原配置身份。原 HBA 必须与固定正常模板一致，启动配置必须是 manual。管理员角色改名、角色 OID 不符、外部实例、配置漂移、数据损坏、宿主 postgres 系统账号遗失不在自动修复范围；拒绝猜测接管或新建替代管理员。合法宿主管理员仍可通过独立系统修复恢复前提后接续原事务。

操作明确进入维护窗口，断开全部既有数据库连接。成功后保持停机，用户通过原实例启动入口显式启动；不自动恢复业务流量。界面确认文本说明清除管理员密码、连接中断及停机结果。

## 授权与端到端接线

- 使用专属安装服务 `PostgreSqlAdminRecovery` 和提权能力 `DatabaseAdminRecovery`，复用安装操作审计、幂等键、进度、历史回执和重开观察。
- 实例、账号和管理员恢复共用 `service:PostgreSql` / APT 资源锁。普通实例维护或账号管理授权不能提交或接续管理员恢复；管理员恢复也不能接续其他领域的待定操作。
- 客户端维护页提供确认、宿主管理员提权、原请求断线重试、待恢复接续和操作日志。viewer 与失效会话禁用。状态健康失败时仍保留恢复入口，不把托管实例简单归类为外部安装。
- 请求只携带实例 ID 和预期修订，无 SQL、文件路径、任意角色或新秘密。Server 不向 Helper 传入管理员密码。

## 持久隔离、修复与清理

1. 预检通过后，Helper 先持久保存原操作与请求身份。建立该实例专属、持久的 systemd `/dev/null` mask，再停止原实例并核对 MainPID 为 0。只有停止已确认才记录 `AdminIsolated`；预检/隔离建立失败不冒称已完成隔离。
2. 单用户模式只在原数据目录、固定 postgres 二进制及宿主 postgres 身份下运行固定角色修复。该进程不提供客户端网络服务，也不生成临时密码。原生派发前写入 `AdminRepairDispatched`，修复属于固定目标状态的幂等操作，不创建新账号或重置应用密码。
3. 修复后，仍保留 mask，以 `listen_addresses=''` 和 `unix_socket_permissions=0700` 启动私有验证进程。通过原正常 HBA 的 peer 认证确认 postgres/OID/数据路径、超级用户及无密码状态；检查 HBA 无错误及 trust，并复核托管库、账号身份与权限。
4. 写入 `AdminAuthenticationVerified` 后停止私有进程，确认数据目录中已无 postmaster PID，再删除本事务的精确 mask、重载 systemd，写入原操作完成回执。普通网络服务始终未启动，manual 启动配置保持原样。

**不安装临时 trust 规则，也不创建临时认证账号或密码文件。** 因此清理对象是单用户/私有验证进程和专属启动 mask；原配置内容及摘要不改写。数据目录中的恢复启动日志保留为原生日志，不含密码。

发生失败，保持 mask 并停止已知私有进程；身份不明时拒绝强行删除 PID/锁文件。Helper 被强杀后，可能尚存无 TCP 的私有验证进程；下次显式恢复先核对并停止它，再继续原检查点。实例手动启动策略与持久 mask 跨重启保留：重启只读观察不会自动解除隔离或重做修复。认证验证成功后的清理中断也沿原检查点继续；清理完成到回执提交之间始终保持停机。

## 2026-11-16 隔离验收

一次性 Ubuntu 24.04 amd64 容器，PID 1 为真实 systemd，PostgreSQL `16.15-0ubuntu0.24.04.1`，无宿主挂载及公开端口。容器仅用于验收；没有修改登记的 Ubuntu 26.04 主机。

- DB-02 原生回归 29 项通过。
- DB-04 最终正常及故障检查 24 项通过（包含既有 DB-03 托管账号）：设置未知随机密码并取消 postgres LOGIN 后恢复，固定管理员无密码、正常 HBA 原样、无 trust、数据保留、显式启动、同操作幂等、异实例拒绝；在夹具中修改管理员角色名称，验证拒绝修复、持续 mask/无 IPv4 或 IPv6 监听、修正夹具身份后接续原操作；应用账号权限漂移导致最终验证失败时，私有进程被清理、原 HBA 不变、mask 保留，修正夹具后可继续。
- 真实终止 Helper 后检查通过；容器/systemd 实际重启后的 11 项通过：mask 和原操作保留，普通启动拒绝，原事务接续，清理后保持停机，显式启动正常认证和数据保留。
- DB-03 权限与改密原生回归 44 项通过。
- Server 专项 52 项通过，覆盖专属授权、共享锁、普通服务越权拒绝与原回执；桌面专项 52 项通过，覆盖确认取消、响应丢失后原请求和幂等键重试、独立恢复入口、viewer/会话失效以及中英日维护页渲染。

复现见 [测试说明](../../Tests/PostgreSql/README.md)。Helper 中断是实际进程终止，不是只修改台账；容器重启不是物理主机掉电。没有将这些证据扩大为生产 HTTP/IPC 全链路部署验收。

部署涉及 Shared Protocol、Core 注册/授权与 Helper，不能仅发布一个服务器模块；真实部署时需协调升级 Server、Helper 和客户端。当前协议直接演进，不增加旧请求适配或双格式解析。

## 官方依据

- [PostgreSQL 16 postgres](https://www.postgresql.org/docs/16/app-postgres.html)：单用户模式用于恢复，并具有隐式超级用户能力。
- [连接配置](https://www.postgresql.org/docs/16/runtime-config-connection.html)：空 listen_addresses 不监听任何 IP 接口；Unix socket 权限单独控制。
- [HBA 认证](https://www.postgresql.org/docs/16/auth-pg-hba-conf.html)：peer 与 trust 语义不同。本实现保持既有 peer 规则。
