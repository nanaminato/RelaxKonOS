# PostgreSQL DB-01：版本、来源与平台核实

核实日期：2026-11-16。关联[领域设计与跟踪](RelaxKonOS.DatabaseManager.Design.md)。

结论：**DB-01 的首个实现目标仍为 Ubuntu 24.04 / amd64 / PostgreSQL 16 / 原生 APT。版本与来源核实已完成，可以开始 DB-02；这不是 DB-02～06 安装、服务或更新验收通过的声明。**

## 证据与版本决定

在本任务独立的 Ubuntu 24.04.5 amd64 容器中刷新原生软件源，APT 成功验证发布索引；另用 `gpgv` 分别核验 noble、noble-updates、noble-security 的 InRelease，均为 Ubuntu Archive Automatic Signing Key (2018)，指纹 `F6ECB3762474EDA9D21B7022871920D1991BC93C`。镜像身份为 `ubuntu@sha256:fe81560990fae86dc4743202b2cb4d3df98d0bcf949173cecef15ce1127ef852`。容器没有宿主挂载、端口发布；它是原生包验证夹具，不是产品的 Docker 部署实现。

| 用途 | 准确包版本 | 来源 | 结论 |
| --- | --- | --- | --- |
| 首次安装候选 | `postgresql-16=16.15-0ubuntu0.24.04.1` | noble-updates/main、noble-security/main | APT 索引、真实下载及 SHA-256 已核实 |
| 更新验收起点 | `postgresql-16=16.2-1ubuntu4` | noble/main | 真实下载及 SHA-256 已核实；只作隔离升级夹具，不向正常新安装推荐旧版 |
| 客户端工具 | `postgresql-client-16`，与服务端分别相同的上述两版 | 同系列官方仓库 | 两版真实下载已核实 |
| 集群与服务管理 | `postgresql-common=257build1.1` | noble-updates/main | 已下载检查内容 |
| 包安装辅助脚本 | `postgresql-client-common=257build1.1` | noble-updates/main | 已下载检查内容 |

首次安装采用当前已核实候选，不自动跳到 PGDG、其他主版本或其他发行版。上游版本政策页面列出的 16.16 不能替代 Ubuntu 已发布候选 16.15；发行版补丁和包修订必须保留。16 系列上游支持截止日期为 2028-11-09，立项核实见[官方政策](https://www.postgresql.org/support/versioning/)。生产选择仍需查询当时签名索引，不能把本次快照硬编码为永久“最新版”。

下载字节实测 SHA-256：

| 制品 | SHA-256 |
| --- | --- |
| postgresql-16 16.15 amd64 | `d60ae76fb862cd4cca7bfdcdcdcc271c5e1d75f6b7915305ce2d962735a4b666` |
| postgresql-16 16.2 amd64 | `7f25383e8f39ca99f4886b7b2cbb31c62534ac924e3a61da3f0e8e6456091e69` |
| postgresql-client-16 16.15 amd64 | `4796f72e1a7270ad8c17b1ecb025cd453f692b42c10aaf89f6b476da61761eac` |
| postgresql-client-16 16.2 amd64 | `d9f308ba17d9ac027d3a44fd07e5464e3d69407fb609a7d3ecd6e81937e5282d` |
| postgresql-common 257build1.1 all | `5363ba39f18ede7f54776d41356d7dd6e859d089ee0092096050500c9d4413f5` |

核实命令为 `apt-get update`、`apt-cache policy`、`apt-get --print-uris`（强制 SHA-256）、精确版本 `apt-get download`、`sha256sum`、`dpkg-deb -e/-x/-c`。未关闭 APT 认证、未使用 trusted=yes 或任意上传包授信。公开解析证据保存在 [policy](../../Tests/PostgreSql/Fixtures/noble-policy.txt) 和 [制品及依赖计划](../../Tests/PostgreSql/Fixtures/noble-install-uris.txt)。

本次干净镜像上的模拟事务为 34 个新包、3 个升级包，其中升级包含 libc6、libc-bin、perl-base。依赖不是固定不变的列表，必须按目标主机完整冻结版本、大小、摘要与来源并显示系统包升级影响；不能将“只装数据库”当成实际计划。已有包状态变化必须重新预检。

## 安装来源边界

首个实现只接受已配置的 Ubuntu 官方 archive/security 来源及 noble、noble-updates、noble-security 的 main 区域。初始支持精确主机 `archive.ubuntu.com`、`security.ubuntu.com` 及 `cn.archive.ubuntu.com` 的 `/ubuntu` 根；其他镜像需后续登记，不能因为域名带 ubuntu 就信任。制品路径限 `/ubuntu/pool/`，来源身份、签名索引及依赖闭包均须由宿主独立核验，URL 允许列表本身不是签名验证。

不自动加源；PGDG、PPA、跨发行版包、backports、预发布版及未登记镜像均不属于首个组合。离线缓存可保存当前已验证制品，但缓存文件和文件名本身不赋予安装权。旧版本未来从当前索引消失时，需按可信快照流程重新核实，不能关闭有效期校验；[Ubuntu Snapshot Service](https://snapshot.ubuntu.com/)只是后续可选来源，本轮没有依赖它。

## 包脚本、所有权与实现门禁

检查两类包内容后确认：

1. `postgresql-16` 的 postinst 调用 `configure_version`；该函数实际由 `postgresql-client-common` 提供。
2. 首次配置默认创建 `16/main`。默认创建可由 `create_main_cluster` 控制，但它是宿主共享配置，不能任意覆盖。
3. 配置脚本最终调用 `invoke-rc.d postgresql start`；systemd 路径可能启动所有版本。安装计划必须显式包含该副作用；第一切片拒绝已有 PostgreSQL 包、集群、配置和未知目录，避免影响外部实例。
4. `/usr/lib/postgresql/16/bin/postgres` 属于引擎包；`postgresql@.service` 和 umbrella `postgresql.service` 属于 `postgresql-common`。现有单包可执行文件/服务同属一个包的核验不适用。必须分别证明两种归属，且用具体实例服务与真实认证检查，不用 umbrella active 作为健康。
5. 初始化须同时明确 local peer 与 host SCRAM，固定回环监听、端口、UTF-8/locale 以及受管数据、配置和 socket 路径；任一归属未知即拒绝。不得采用未指定另一种认证时可能出现的 trust 默认值。

DB-01 阶段先提供专属纯预检及初始化规划。上述执行门禁现由 [DB-02 专属事务](RelaxKonOS.DatabaseManager.PostgreSql.DB02.md)承接；组件中心已登记 PostgreSQL，但不加入通用包执行映射，防止绕过集群抑制、归属和恢复检查。

## 平台矩阵

| 平台 / 模式 | 引擎 | 本轮核实结论 | 允许宣称的范围 |
| --- | --- | --- | --- |
| Ubuntu 24.04 amd64 / native apt | 16 | 签名索引、两版制品、依赖解析及脚本已核实 | DB-02 已完成隔离真实 systemd/Helper 验收；真实主机完整部署验收待补 |
| Ubuntu 26.04 amd64 / native apt | 18 | 已登记验收机实测候选 `18.6-0ubuntu0.26.04.1`，基础包 `18.3-1`；无 16 候选 | 不作为 16 的验收替代；18 单独适配后才能声明支持 |
| Windows x64 / native | 待定 | 未下载/核实安装制品 | DB-08；不支持当前切片 |
| Linux / Docker 产品模式 | 待定 | 本轮测试容器不等于部署能力 | DB-10；不支持当前切片 |
| ARM64、其他发行版 | 待定 | 未核实 | 不支持当前切片 |

已登记验收机启动后通过 SSH 核实为 Ubuntu 26.04 amd64，Docker 存在；当时 `127.0.0.1:5000/healthz` 无监听。未变更该主机软件源、未安装 PostgreSQL、未重启 Core 或其他模块。24.04 的隔离包验证不能扩大为该主机原生生命周期验收。DB-06 的 16.2→16.15 真实数据更新仍须单独执行，下载两版不等于更新通过。

官方交叉资料：[Ubuntu 包](https://packages.ubuntu.com/noble/postgresql-16)、[amd64 制品](https://packages.ubuntu.com/noble/amd64/postgresql-16/download)、[集群创建与认证默认值](https://manpages.ubuntu.com/manpages/noble/man1/pg_createcluster.1.html)。
