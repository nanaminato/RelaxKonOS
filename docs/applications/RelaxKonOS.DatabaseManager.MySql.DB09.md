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

密码先经提权接口存入现有实例绑定的加密凭据库，持久安装载荷只含凭据引用。Helper 通过子进程标准输入提交固定 SQL，并在专用 root 客户端进程中探测密码。改密前验证旧密码，提交凭据引用前用独立新连接验证新密码成功及旧密码明确被拒绝。已认证的连接保留原会话，应用需要更新后续连接使用的秘密。私有系统 socket 的目录访问范围沿用初始安装配置，仍需有相应宿主权限才能连接。远程账号和管理员身份恢复见对应章节。

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


## 指定来源 TLS 远程访问

首轮限定 Oracle MySQL 8.4.13、Ubuntu 24.04 amd64 和一个精确 IPv4 来源。实例在主机持有的非回环 IPv4 上监听 3306，关闭 X Plugin 和名称解析。操作者在 MySQL 桌面页选择受管本机账号及证书中心证书，填写监听地址、来源地址和证书 DNS 名，再确认开放。Helper 复用该本机账号的密码和单库权限创建独立的 `user@来源 IPv4`，保留原本机账号。远程账号使用 `REQUIRE SSL`，实例启用 `require_secure_transport`，TLS 最低版本为 1.2。`root@localhost` 保留 `auth_socket` 认证。连接端使用 `VERIFY_IDENTITY`、预期 DNS 名和可信 CA。[官方 TLS 说明](https://dev.mysql.com/doc/refman/8.4/en/using-encrypted-connections.html) · [官方账号匹配说明](https://dev.mysql.com/doc/refman/8.4/en/account-names.html)

Server 使用独立的远程访问提权能力提交安装队列，按证书 ID 读取证书中心材料，Helper 核验证书指纹、DNS 名和密钥。操作台账持久记录意图和远程账号阶段，nftables 表随规则原子写入原操作的归属标记。Helper 先安装拒绝规则，再重启并验证实例、root socket、精确监听、TLS 要求和账号权限，最后开放指定来源。其他到目标地址和端口的来源在实例规则中丢弃，允许来源仍受宿主其他防火墙规则约束。关闭先恢复拒绝规则，再重启至仅监听 socket 的单元，清理归属匹配的远程账号、TLS 文件和实例规则。外部规则或单元漂移会中止操作。证书或账号发生变更时，需要先关闭远程访问，再按新配置开放。

开启和关闭都会重启实例并中断已有连接。systemd 每次启动都先加载拒绝规则，重启后产品保留已配置的远程策略，但标记为未验证。需要重新开放时，先在桌面关闭该策略，再提交开放。本机 Unix socket 在实例启动完成后仍可认证。启停、账号修改和管理员恢复须先关闭远程策略。


隔离计划验收在安装 Oracle 8.4.13 固定核心包的一次性 Noble systemd 容器上通过 5 项。两个 veth 网络命名空间分别模拟允许与禁止来源。关闭防火墙规则时监听不可达，开放后允许来源使用 VERIFY_IDENTITY 校验证书并以准确 user@host 读取绑定数据库及确认 TLS 密码套件。禁止来源命中 nftables 丢弃计数。允许来源的明文请求收到原生拒绝。关闭后恢复原普通单元，root@localhost 通过 Unix socket 读回原数据。聚焦计划回归 50 项通过，容器已清理。

产品路径复现使用带 nftables、iproute2 和 openssl 的独立 Noble systemd 容器。设置 `/etc/relaxkonos-db09-product-disposable` 为 `DB-09-product`，用测试执行器 `mysql-product --normal` 通过安装队列安装并初始化实例。随后运行[产品验收脚本](../../Tests/MySql/native-remote-product-smoke.py)，传入 Linux x64 测试执行器路径。脚本使用 `10.77.0.1` 监听、`10.77.0.2` 允许、`10.78.0.2` 禁止，经证书中心保存测试证书并经实际安装服务提交 Helper 子进程。隔离夹具使用标准输入承载提权请求，桌面接线由桌面回归覆盖。

MySQL 8.4.13 的产品路径隔离验收通过。占用 `3306` 使开启失败时，防火墙仍拦截允许来源，原操作保持待恢复。释放端口后，新的 Server 和 Helper 测试进程从证书中心、持久凭据和原操作台账续做成功。允许来源以 `VERIFY_IDENTITY` 连接并读取原数据，禁止来源命中 nftables 丢弃规则，同一允许来源使用明文时被 MySQL 拒绝。systemd 重启后入站保持关闭，本机 root socket 仍可用。执行关闭后，远程账号被删除，MySQL 恢复仅 Unix socket，`root@localhost` 仍可读取原数据。

## 8.4 LTS 补丁维护

范围是 Ubuntu 24.04 amd64、Oracle Community 核心包和单一受管实例。更新方向固定为 `8.4.11-1ubuntu24.04` → `8.4.13-1ubuntu24.04`。8.4.12 仅发布 Docker 镜像安全补丁。8.4.11 的 Ubuntu amd64 DEB bundle 已通过 Oracle 发布密钥 `BCA43417C3B485DD128EC6D4B7B3B788A8D3785C` 的分离签名校验，池内三个核心包与签名归档提取结果的长度及 SHA-256 一致。首次桌面安装使用 8.4.13，安装 API 可显式选择固定 8.4.11 基线。应用重新核对本地缓存和下载制品摘要。 [8.4.11 发行说明](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/news-8-4-11.html) · [8.4.12 发布范围](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/news-8-4-12.html) · [Oracle 签名说明](https://dev.mysql.com/doc/refman/8.4/en/checking-gpg-signature.html) · [Oracle 核心包池](https://repo.mysql.com/apt/ubuntu/pool/mysql-8.4-lts/m/mysql-community/)

维护通过独立提权的安装队列提交，绑定实例 ID、操作 ID、修订和版本。远程策略须先关闭；Helper 核对受管单元、进程、包状态和数据目录，拒绝发现外部 MySQL、MariaDB 或 Percona 包、实例和常见默认路径后的共享包变更。仅接收当前机受管实例的单目录数据和可核对账号授权。外部表空间、额外系统账号或库、持久化全局配置、复制和组件超出当前备份门禁。更新会停机，软件包操作限定三个核心包及其现有依赖，保留 Ubuntu 依赖。

慢停机通过 `innodb_fast_shutdown=0` 和 systemd 退出状态确认。Helper 写入启动门禁后复制数据并核对逐文件摘要、单元和受管库及账号台账。备份必须在新数据目录、私有 socket 和禁用网络的临时实例中打开，检查原生版本、`root@localhost`、受管账号和授权，同时对受管表执行 `CHECK TABLE`。恢复限原实例、原版本和相同数据库及账号台账，阻止恢复已通过产品变更的旧权限和密码。更新同时验证旧版备份以及用目标二进制打开备份副本。独立修复要求实例已停止且存在同版本验证备份，使用精确版本重新安装核心包。 [Oracle 停机备份说明](https://dev.mysql.com/doc/refman/8.4/en/innodb-backup.html) · [Oracle 包升级说明](https://dev.mysql.com/doc/refman/8.4/en/upgrade-binary-package.html)

保留数据卸载清除受管服务和核心包，保留实例、备份和加密凭据。重新安装使用原版本并核实备份，复用原目录和身份。删除数据需再次确认目标实例 ID，清理该实例的数据、备份、专用系统身份和凭据引用。持久阶段允许恢复中断的停机、备份、换包、目录切换和删除；恢复时先核对原操作归属。目标副本验证失败且活动数据尚未由新版打开时，可补偿到旧包和旧实例。新版开始打开活动数据之后的失败保留备份和待恢复状态，旧二进制不会打开已可能变更的数据目录。

原生范围验收在一次性 Noble systemd 容器通过四项，覆盖 8.4.11 副本恢复、真实跨补丁更新、8.4.13 损坏二进制修复，以及精确移除核心包后保留数据和副本。复现使用带 `DB-09-maintenance` 标记的[原生维护脚本](../../Tests/MySql/native-maintenance-scope-smoke.py)。产品验证从空容器运行测试执行器 `mysql-product --maintenance`，使用既有 `DB-09-product` 标记，以实际安装队列启动 Helper 子进程。完整认证 HTTP/PAM 链路另由实机验证。

产品隔离验收从空容器安装并初始化 8.4.11，共通过 39 项。外部默认数据目录会阻止更新，另经真实 Helper 验证未知备份 ID 会在写入待办前被拒绝。慢停机后终止 Helper，新 Helper 以原操作 ID 完成备份并恢复服务。实际恢复将测试行从 `99` 还原到 `84`，随后升级到 8.4.13，原账号仍可登录读取。另在目标包校验后注入候选启动故障，隔离副本失败后恢复旧包和旧服务；再次更新成功。修复从损坏的 `mysqld` 恢复原版本。保留数据卸载后，原版本重装仍能使用原凭据。删除数据卸载清理了目标数据、备份与加密凭据引用。桌面数据库回归 109 项、数据库策略回归 52 项通过。
