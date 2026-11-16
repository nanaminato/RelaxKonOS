# PostgreSQL DB-03：建库、账号、授权与正常改密

更新：2026-11-16。状态：产品路径及 Avalonia 接线已实现，隔离 Ubuntu 24.04 x64 原生验收通过；未完成真实验收机上的完整 Core/Helper/桌面端联调。范围继承 [数据库设计](RelaxKonOS.DatabaseManager.Design.md) 和 [DB-02](RelaxKonOS.DatabaseManager.PostgreSql.DB02.md)。

## 已实现行为

- 仅操作本应用创建、实例身份及 OID 均匹配的数据库和账号；拒绝接管同名外部资源。名称限制为 1–40 位 ASCII 小写字母、数字、下划线，字母开头，拒绝系统名称及 `pg_`、`rk_` 前缀。上限为 32 个数据库、64 个账号。
- 每库创建独立 NOLOGIN 所有者 `rk_owner_<数据库名>` 和 `app` schema。应用账号无超级用户、建库、建角色、复制或绕过 RLS 权限，也不继承所有者身份。
- 只读：app schema USAGE 和现有、未来表 SELECT。读写：额外 INSERT/UPDATE/DELETE，以及现有、未来序列 USAGE/SELECT；不授予 DDL、TRUNCATE、REFERENCES、TRIGGER 或临时表权限。
- 迁移账号需要显式 `SET ROLE rk_owner_<数据库名>` 才能执行所有者 DDL。默认权限跟随实际创建对象的角色，因此迁移必须使用该身份；不支持任意外部角色创建对象的自动授权。
- 回收托管库、postgres、template1 的 PUBLIC 连接权限；其他可连接数据库仍有 PUBLIC CONNECT 时拒绝创建账号。普通账号只允许连接其指定数据库。此策略适用于独占托管实例，不适用于接管共享实例。
- 正常改密通过现有管理通道执行，验证新密码可建立连接、旧密码不能建立新连接，再记录成功。已有认证会话继续有效，不强制断开。状态明确保留“依赖应用待更新”，不会自动修改应用凭据，也没有伪造的同步完成按钮。

## 凭据与授权

账号动作使用独立 `PostgreSqlAccounts` 安装服务及 `DatabaseAccountsManage` 提权范围；与实例生命周期共享资源锁。双方恢复入口拒绝越权恢复另一类待定事务。客户端只允许 controller 提交，服务端独立检查权限及提权。

密码通过专属凭据暂存接口提交，返回不可变随机引用；安装请求、普通状态、操作台账及客户端任务日志只携带引用。引用绑定实例和暂存操作者；同引用同密码重试幂等，不允许重新绑定。服务端使用宿主既有 ASP.NET Data Protection 密钥体系加密，Linux 文件权限为 0600，目录为 0700，不提供密码回显接口。密钥与加密文件必须共同保管；本轮没有凭据清理/轮换策略，历史及未使用引用仍保留。

密码输入当前限制 16–128 位可打印 ASCII 且不含空格，以避免 SCRAM 规范化产生隐式差异。Helper 经受控 IPC 获得瞬时凭据，生成 SCRAM verifier，固定 SQL 通过 stdin 执行。认证使用 `/run` 中 0600 临时 passfile，正常及异常返回时删除；进程被强杀可能留存至 tmpfs 清理或重启，尚未声称具备崩溃后逐文件清扫。密码不进入命令行。没有任意 SQL、shell 或凭据读取产品接口。

## 事务与恢复

账号及授权变更与 SQL 操作见证在同一 PostgreSQL 事务内提交；Helper 持久检查点跟踪派发、提交和验证。丢失响应后读取原操作见证和资源身份，不能仅凭角色存在推断成功。已提交但凭据验证失败保持待恢复，不直接重放改密。

CREATE DATABASE 不能放入普通事务，因此使用专属所有者标记、先禁止连接的数据库及 OID 对账分阶段完成。无法证明结果的派发保持待恢复，不猜测重试。客户端保存同一请求体和幂等键，断线后继续原请求；确认前取消不会暂存密码或提交安装事务。

本次直接升级内置协议和 Helper 台账，数据库/账号列表为必填，不解析旧 DB-02 开发台账。已有开发夹具需明确重建；不提供迁移适配或自动删除数据。部署涉及共享协议、Core 注册和 Helper，不能仅替换一个 ModuleHost 包。

## 2026-11-16 验收证据

隔离 Docker Ubuntu 24.04 amd64，真实 systemd、PostgreSQL `16.15-0ubuntu0.24.04.1`。容器无宿主挂载及公开端口。注册的 Ubuntu 26.04 主机仍不在支持矩阵，本轮未对它部署或修改。

- DB-02 原生产品路径回归：29 项通过，安装、初始化、启停、身份/配置漂移和事务恢复。
- `Tests/PostgreSql/native-accounts-smoke.py`：44 项通过。覆盖建库、三类账号、跨库隔离、读写与 DDL 拒绝、未来对象授权、降权、真实新旧密码认证、既有会话、验证失败保留待恢复、SQL 见证恢复、外部 PUBLIC-connect 数据库拒绝及临时 passfile/明文日志检查。
- Server 专项：40 项通过；凭据加密、引用绑定与冲突、专属安装协议、权限映射、生命周期恢复边界、回执及共享队列。
- 桌面专项：39 项通过（含实例回归）；取消、提权后原引用重试、响应丢失后原请求恢复、密码掩码与清空、viewer 禁用、应用凭据待更新，以及中/英/日三语言渲染。

恢复用例是针对已真实提交 SQL 的检查点注入，不是实际断电实验。上述分层测试不能替代真实主机完整 HTTP/IPC/GUI 联调。重现命令见 [测试说明](../../Tests/PostgreSql/README.md)。

## 未交付范围

DB-03 本轮未包含管理身份遗失恢复（后续进展见 [DB-04](RelaxKonOS.DatabaseManager.PostgreSql.DB04.md)）。远程/TLS 后续进展见 [DB-05](RelaxKonOS.DatabaseManager.PostgreSql.DB05.md)。应用凭据自动同步、删除数据库/账号、任意 schema 权限编辑、跨版本更新及卸载仍未实现。DB-03 的正常改密不代表 DB-04 已通过。

## 官方语义依据

- [角色属性](https://www.postgresql.org/docs/16/sql-createrole.html)：LOGIN、INHERIT 与角色能力。
- [默认权限](https://www.postgresql.org/docs/16/sql-alterdefaultprivileges.html)：默认权限取决于对象创建角色，不改变已有对象。
- [权限](https://www.postgresql.org/docs/16/ddl-priv.html)：PUBLIC 默认权限与数据库/schema/对象权限相互独立。
- [CREATE DATABASE](https://www.postgresql.org/docs/16/sql-createdatabase.html)：不能在事务块中执行。
- [密码认证](https://www.postgresql.org/docs/16/auth-password.html)与 [ALTER ROLE](https://www.postgresql.org/docs/16/sql-alterrole.html)：SCRAM 和账号密码修改。
