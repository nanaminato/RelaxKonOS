# PostgreSQL DB-07B：主版本迁移

更新：2026-11-17。状态：限定路径的实现、桌面接线与隔离原生验收已完成；真实主机完整联调和物理断电未验收。

## 首轮范围

Ubuntu 24.04 amd64 / systemd，既有受管 PostgreSQL 16 单实例 → PostgreSQL 18。来源核实得到 PGDG `18.7-1.pgdg24.04+2`，共享 `postgresql-common` / `postgresql-client-common` 为 `295.pgdg24.04+1`，`libpq5` 为同版 18.7。未来候选不自动成为已验证迁移目标。

官方来源为 `https://apt.postgresql.org/pub/repos/apt`、`noble-pgdg/main`，仓库公钥指纹 `B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8`。来源身份、密钥、精确目标和依赖须在提交前展示确认。宿主须已有可信系统 CA 证书；缺少时返回 `database.migration_ca_required`，不在只读计划中安装依赖。计划使用私有 APT 索引，不写全局软件源；Helper 复核签名索引、完整依赖、长度与 SHA-256。不得因版本发现自动安装，不接管外部 PostgreSQL。

## 迁移与恢复门禁

采用新版本 `pg_upgrade` 的复制方式；禁止 link/swap，不共享旧数据文件。迁移前先完成 DB-06 停机备份及隔离恢复演练，匹配校验和、locale/encoding、角色及权限。独立新集群执行原生 `--check` 后才迁移。普通服务在持久 mask 下停机，私有阶段只接受 postgres peer/socket，不开放 TCP 或 trust。

迁移操作绑定源实例、状态修订、精确源包和目标冻结计划。持久保存原操作、来源确认及每个有副作用的阶段；未知包操作不盲重放。新集群验证版本、数据、账号身份、既有与未来权限，完成统计/索引处理后才提交。成功后保持停止，由用户显式启动。自动备份策略停用，必须重新确认新版策略。

初始边界沿用 DB-06：拒绝外部集群、未托管数据库、除 plpgsql 外的扩展、复制槽/订阅、外部表空间、符号链接及数据树挂载点；须关闭远程访问，空间和备份容量充足。新版本开始服务后不提供自动降级；旧版恢复材料不等于新版可直接恢复的备份。APP-001 可移植备份、多实例、Windows 及其他引擎不在本轮。

## 持久阶段与恢复边界

- `MajorPackageDispatched` 先核对冻结集合中每个包的精确版本/架构、引擎及客户端完整性、共享服务单元归属，才能接续；不重新派发未知 APT 操作。
- `MajorUpgraded` 表示原 `pg_upgrade` 已成功返回且目标目录已同步。恢复会停止遗留私有目标进程，重新做校验和、账号/权限及索引/统计验证，再切换目录。
- `MajorSwitchDispatched` 保留旧目录与新暂存目录，按原实例的固定路径核对并接续目录切换；切换后的验证和旧目录清理由原事务持有。验证完成后移除旧引擎及旧配置，保留已验证的迁移前备份；共享客户端/common/libpq5 不做全局移除。
- `Verified` 已完成普通服务 mask 清理，回执提交前中断时只接续回执，不能重新加 mask。本轮修复并通过检查点窗口测试。
- `MajorInitializeDispatched` 或 `MajorUpgradeDispatched` 中断且缺少原完成检查点时，保持隔离及原操作，返回 `database.migration_uncertain`，不凭目标目录存在推断成功、不盲目重跑初始化或复制。此类未知原生结果的自动重建/回退未实现；本轮真实进程中断验收覆盖已完成复制、尚未激活的检查点，不代表所有原生指令内部的故障都可自动恢复。

成功后保持停止，正常服务没有临时 trust HBA。恢复普通启动后继续核对新主版本、配置、真实进程及认证连接。迁移后的修复、管理员恢复、自动备份、保留数据卸载、重装和删除数据使用包身份决定主版本，不再调用固定 PostgreSQL 16 路径。

## 官方依据

- [Ubuntu 安装来源与 PGDG 公钥](https://www.postgresql.org/download/linux/ubuntu/)
- [PostgreSQL 18 pg_upgrade](https://www.postgresql.org/docs/18/pgupgrade.html)
- [PostgreSQL 17 迁移兼容变化](https://www.postgresql.org/docs/17/release-17.html)
- [PostgreSQL 18 迁移兼容变化](https://www.postgresql.org/docs/18/release-18.html)

## 验收记录（2026-11-17）

环境为 Ubuntu 24.04 amd64、真实 systemd、Ubuntu `16.15-0ubuntu0.24.04.1` → PGDG `18.7-1.pgdg24.04+2`。使用 `relaxkonos-db07b`、`relaxkonos-db07b-restart`、`relaxkonos-db07b-interrupt` 三个一次性容器，均带 `relaxkonos.task=db07b` 标签，无宿主绑定挂载、无公开端口。原生执行走实际 Helper 事务；没有修改登记的 Ubuntu 26.04 主机。验收完成后已核实标签、挂载及端口范围，并删除这三个一次性容器。

| 验收项 | 结果与范围 |
| --- | --- |
| 构建与纯策略 | Linux 自包含发布成功；PostgreSQL 聚焦策略 42 项通过，含支持主版本路径及拒绝未知主版本 |
| Server | `--postgresql-only` 103 项通过，含迁移目标/公钥确认、普通及自动备份授权不能越权提交迁移 |
| 桌面 | `--database-only` 87 项通过，含独立迁移计划、来源确认、取消、原请求/幂等键重试、会话失效、HTTP 前置条件错误和中英日渲染 |
| 真实迁移源 | 三个新实例分别通过 DB-02 的 29 项基线；含账号的源通过 DB-03 44 项建库/权限/改密检查 |
| 16→18 与新版验证 | 真实复制、版本/数据库及角色 OID、授权/凭据引用保持，旧自动策略停用；11 项迁移后检查通过，含正常启动、代表性行、旧版恢复拒绝、无 trust、新版物理备份验证 |
| Helper 真正中断 | 14 项通过；在 `MajorUpgraded` 终止实际 Helper，原操作保留，16/18 普通服务均持久 mask，普通启动拒绝，IPv4/IPv6 无监听 |
| systemd 重启后恢复 | 19 项通过；原操作 `e03680b6-87b1-4a3a-b722-2e46a574e876` 接续完成，身份与数据保持，正常认证及新版备份通过 |
| 回执提交窗口 | 4 项通过；向隔离台账注入已验证但未提交回执的检查点，证明恢复不重新创建 mask、责任正常清除。该项是检查点注入，不是真实断电 |
| 迁移后生命周期 | 18 项通过；18 同版本修复、管理员恢复、授权停机自动备份、保留数据卸载/重装、数据保持、删除数据及新旧备份和策略清理 |
| CA 前置条件 | 2 项通过；隔离夹具临时移走系统 CA 后，明确拒绝来源核实且不创建迁移事务；测试后恢复原 CA |

首轮包验证发现精简 Ubuntu 镜像省略 `.mo` 翻译文件；仅允许缺失的非执行翻译文件，实际引擎/客户端/服务归属和完整性校验保持。原操作随后按已安装的冻结包集合恢复成功，没有重发安装。中断脚本的初始空检查点竞态已修复；受其影响自然完成的迁移只记录成功路径，真实中断证据来自另建的 `relaxkonos-db07b-interrupt`。

可复现入口：[原生迁移脚本](../../Tests/PostgreSql/native-major-migration-smoke.py)、[运行说明](../../Tests/PostgreSql/README.md)。本轮修改共享契约、Core 路由和 Helper，不能仅替换数据库模块；验收部署独立 Helper 执行器，并分别运行 Server 与桌面检查。未部署完整 HTTP/PAM/桌面到真实主机链路，未验证 Core/其他模块在线升级可用性，也不把容器/systemd 重启视作物理掉电验收。其他主版本组合、跨平台、任意扩展、自动降级和未知原生复制结果的自动恢复不在完成范围内。
