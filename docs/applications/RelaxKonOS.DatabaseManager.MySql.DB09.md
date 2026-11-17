# DB-09 MySQL 8.4 LTS 首轮核实

核实日期为 2026-11-17。首轮候选为 Ubuntu 24.04 amd64 原生 systemd 单实例。上游 8.4.13 于 2026-10-20 发布，8.4.13.1 在版本说明中仍未发布。版本支持和来源见 [MySQL 8.4 发布记录](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/)及[平台矩阵](https://www.mysql.com/support/supportedplatforms/database.html)。

| 平台 | RelaxKonOS 范围 |
| --- | --- |
| Ubuntu 24.04 amd64 / systemd | 首轮目标。Oracle 官方 Noble 源的核心包与原生启动夹具已核实 |
| Ubuntu 22.04/26.04 amd64、Debian amd64 | 上游列出支持，需分别核实包、共享依赖和服务行为 |
| Windows x64 | 上游列出支持，需另验安装格式、服务、管理员认证 |
| ARM64、其他 Linux、容器产品模式 | 另立来源及验收矩阵 |

Oracle APT 仓库 `https://repo.mysql.com/apt/ubuntu`，发行版 `noble`，组件 `mysql-8.4-lts`。2026-10-23 的 `InRelease` 实际通过 OpenPGP 验证，签名指纹 `BCA43417C3B485DD128EC6D4B7B3B788A8D3785C` 与 Oracle 手册一致。amd64 Packages SHA-256 为 `4dac794cc9dbc54c90bb1b5c0b4439a5d9cc082c157dcb88b6a75c9ec1ce84e4`，与已签名记录一致。实际下载并核对了三个 `8.4.13-1ubuntu24.04` 核心包的 SHA-256，精确制品记录见固定策略。 [Oracle 仓库元数据](https://repo.mysql.com/apt/ubuntu/dists/noble/InRelease) · [Oracle 签名说明](https://dev.mysql.com/doc/refman/26.10/en/checking-gpg-signature.html)

核心包为 `mysql-community-server-core`、`mysql-community-client-core` 和 `mysql-community-client-plugins`，底层库由 Ubuntu 签名仓库解析。候选路径用专用 systemd 单元和低权限账户运行，数据、socket、服务身份从实例 UUID 派生。只开放私有 Unix socket，显式关闭 TCP、X Plugin 和服务器文件导入导出。首次启动的无密码阶段仅在此 socket 内，完成本机身份绑定后使用 `root@localhost` 的 `auth_socket`。正式 Helper 必须持久记录初始化阶段，验证空目录、软件包归属、专用系统身份以及配置，拒绝接管任何外部 MySQL/MariaDB 实例。 [MySQL 初始化说明](https://dev.mysql.com/doc/refman/8.4/en/data-directory-initialization.html)

现阶段已实现固定目标策略、实例运行计划与原生验收脚本。产品路径现已接入持久安装队列、Helper 台账、Server 状态及桌面 MySQL 页。


## 隔离证据

Ubuntu 24.04 amd64 一次性 systemd 容器 `relaxkonos-db09` 中，安装三个 Oracle 核心包后没有发行版默认实例。生成的固定计划通过 6 项原生检查，包括版本、数据目录、管理员身份、同 socket 的错误系统身份拒绝、IPv4/IPv6 上无 3306/33060 监听，以及停止和重新启动后的持久数据读取。首轮发现核心包默认引用 `/var/lib/mysql-files`，配置显式使用 `--secure-file-priv=NULL` 后通过。编译通过。

复现时先核对 Oracle 签名和制品摘要，在一次性 Noble systemd 容器中从本地三个已核实包安装，依赖使用 Ubuntu 签名仓库。使用 `Tests/PostgreSql` 测试执行器的 `mysql-bootstrap-plan` 模式生成 JSON，复制到容器，显式设置 `/etc/relaxkonos-db09-disposable` 内容为 `DB-09`，通过 [原生脚本](../../Tests/MySql/native-bootstrap-smoke.py) 运行计划。验收容器的直接系统命令结果只覆盖原生行为，此段为产品接线前的独立原生证据。


## 产品入口与事务

桌面 MySQL 页读取独立状态接口，并通过需提权的安装队列提交固定动作及状态修订。Helper 持久保存原操作、阶段和回执。Oracle 核心包按冻结长度和 SHA-256 校验，Ubuntu 依赖来自独立的签名 APT 索引。包派发后按精确包集合、dpkg 审计、文件完整性和归属对账。原生初始化结果未知时保持待恢复，已有初始化完成证据时可继续发布服务和收紧认证。启动验证检查 systemd PID、版本、数据目录、网络和本机认证插件。

## 产品路径复现

准备全新的 Ubuntu 24.04 amd64 systemd 容器，标签 db09-product，无宿主挂载和公开端口。安装系统 CA 证书，将 /etc/relaxkonos-db09-product-disposable 设为 DB-09-product。发布 Tests/PostgreSql 的 linux-x64 自包含执行器，运行 mysql-product --normal。用[中断脚本](../../Tests/MySql/native-product-interrupt.py)终止实际 Helper，再重启容器并运行 mysql-product --after-restart。

夹具通过生产 InstallationCoordinator、MySQL 安装服务及独立真实 Helper 进程执行。传输替身通过 stdin 调用真实 Helper 调度器。桌面单独运行窗口回归。完整 HTTP/PAM 与真实桌面到主机的联调留待后续。首轮安装验收范围为本机单实例。账号及授权的后续切片见下文。


## 产品隔离结果

真实 Ubuntu 24.04 amd64 systemd 容器走生产队列的正常流程 13 项通过。软件包由 Helper 下载、对照固定摘要安装，原生初始化后运行私有实例。模拟安装提交后的应答与首次回执查询丢失，原操作可对账，安装派发仅发生一次。实际终止启动事务的 Helper 后，systemd 重启恢复 4 项通过，原启动操作结案，私有 socket 重新通过身份验证，随后正常停止。桌面数据库回归 97 项通过，包含 MySQL 新装、初始化、运行状态、待恢复和会话失效。

测试范围涵盖实际系统包、真实数据库进程与服务重启。初始化未完成且原生结果未知的分支会保留原意图，由操作员进一步核对。本轮真实进程终止覆盖启动事务。


## 建库、账号、授权与正常改密

桌面 MySQL 页面使用独立的 MySQL 账号安装服务和数据库账号提权能力。事务绑定实例、状态修订与完整的 (user, host)，当前仅接受字面 localhost。数据库及用户名使用小写字母和数字，避免 MySQL 库级授权中的通配符语义。账号只绑定一个受管库。只读授予 SELECT，读写授予 SELECT、INSERT、UPDATE、DELETE。授予与降权前核对已有权限，提交前复核真实 SHOW GRANTS 和认证插件。账号创建时通过原生属性保存归属标识，后续按完整身份核对，防止接管换名或重建的对象。

密码先经提权接口存入现有实例绑定的加密凭据库，持久安装载荷只含凭据引用。Helper 通过子进程标准输入提交固定 SQL，并在专用 root 客户端进程中探测密码。改密前验证旧密码，提交凭据引用前用独立新连接验证新密码成功及旧密码明确被拒绝。已认证的连接保留原会话，应用需要更新后续连接使用的秘密。私有系统 socket 的目录访问范围沿用初始安装配置，仍需有相应宿主权限才能连接。远程账号留待后续。管理员身份恢复另见下文。

恢复按原操作处理。账号创建可核对原生归属标识。改密返回不明时先探测已暂存的新旧密码，核对成功时收尾。若建库原生执行结果未知且同名库已经存在，保留待核对状态，避免自动接管同名库。

复现使用前述一次性系统容器，完成 mysql-product --normal 后运行 mysql-product --accounts。通过生产账号安装队列、加密凭据存储和独立 Helper 进程执行。验收 SQL 仅用于夹具和连接核对。


账号切片在 Ubuntu 24.04 amd64 / systemd 和真实 Oracle 8.4.13 核心包上通过 23 项检查。两库分别验证了跨库读写拒绝、只读、读写和降权行为。原连接跨改密保持可用，新连接接受新密码并对旧密码返回 MySQL 1045。同名的 127.0.0.1 账号仍锁定且认证字段未变。桌面窗口回归 99 项，PostgreSQL 服务端回归 103 项。验收容器无宿主挂载和公开端口，已清理。生产 HTTP/PAM 到桌面的整链路在本轮未部署。


## 管理员身份恢复

恢复仅针对已登记 Ubuntu 24.04 amd64 的 Oracle MySQL 8.4.13 实例，以及既有的 root@localhost。Helper 核对固定包、服务单元、实例数据路径、权限和操作修订。操作使用独立管理员恢复提权能力。遗失的本机认证恢复为 auth_socket，解除账号锁定及密码过期。已有账号和授权保持原样。缺失的 root 行、丢失的管理授权和无法加载的 auth_socket 插件超出本轮范围。

Helper 先写持久隔离阶段，再发布阻止普通启动的 systemd 条件并停止原服务。私有恢复进程使用独立 Unix socket、私有网络命名空间和 AF_UNIX 限制。仅用固定 init-file 修改既有 root@localhost，不绕过授予表。临时 SQL 无秘密。恢复先核对真实进程、数据目录、版本、socket 认证和错误系统身份的拒绝，再在无 init-file 的独立进程中重做验证。清除临时单元、socket 和 SQL 后才解除普通启动隔离，成功后保持停止。[^initfile]

原操作按已持久化阶段接续。重启后临时目录由 systemd 和 Helper 重建，普通服务继续被持久配置阻止。遇到外部临时文件内容、归属异常或验证失败时保留待恢复状态。桌面 MySQL 页提供管理员恢复及原操作接续入口。

[^initfile]: [MySQL 8.4 官方启动文件恢复说明](https://dev.mysql.com/doc/refman/8.4/en/resetting-permissions.html)


### 恢复隔离验收

Ubuntu 24.04 amd64、真实 systemd 与 Oracle MySQL 8.4.13。先执行 mysql-product --normal，使用 --admin-loss 构造认证插件改变、密码未知和账号锁定，再执行 --admin-recover。该路径验证普通生命周期权限不能派发恢复，root@localhost 恢复 auth_socket，root@127.0.0.1 保持锁定，见证数据可读。

第二轮在 AdminIsolated 终止真实 Helper，确认普通单元不能启动且 IPv4/IPv6 无 MySQL 监听。实际容器重启后，原操作 ID 与持久隔离仍在。注入非本事务的启动 SQL 时拒绝覆盖并保持隔离。移除夹具注入的文件后，经生产安装队列接续原操作并清理全部临时资源。显式启动正常服务后验证本机认证、其他 host 及数据。

复现使用同一个一次性容器，运行 --admin-loss 后调用 [Helper 中断脚本](../../Tests/MySql/native-admin-recovery-interrupt.py)，重启 systemd 容器后运行 mysql-product --admin-after-restart。桌面数据库回归 101 项、PostgreSQL 服务端回归 103 项通过。隔离测试通过生产安装队列和独立实际 Helper 进程，桌面渲染单独测试。完整 HTTP/PAM 真实主机联调和物理断电尚未验收。

本轮一次性容器标签为 db09-admin，无宿主挂载与公开端口，验收后已清理。
