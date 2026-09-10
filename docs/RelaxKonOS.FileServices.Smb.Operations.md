# RelaxKonOS SMB 运维基线

本文件冻结首轮 SMB 控制面的宿主支持矩阵和安全边界。它不改变
`RelaxKonOS.FileServices.Smb.Goal.md` 的范围。

## 支持矩阵

| 宿主 | 最低要求 | 固定资源 | 允许共享根 |
| --- | --- | --- | --- |
| Debian 12 / Ubuntu 22.04、24.04 | systemd、Samba 4（`smbd`） | `samba` 包、`smbd`、`/etc/samba/smb.conf` 中唯一 RelaxKonOS marker、`/etc/samba/relaxkonos.conf` | `/srv/relaxkonos-shares` |
| Windows Server 2019+ | LanmanServer、SMB Server API | LanmanServer、由 HostGlobal ledger 标记的 share/ACL/security snapshot | `D:\\RelaxKonOSShares` |

Samba 安装只使用发行版的受信任默认仓库和固定的 `samba` 包；不会接受仓库、包名或版本。Linux 健康检查为 `testparm`、`smbd` active 与 TCP 445 listening；Windows 为 API 回读、LanmanServer 状态和 TCP 445。

每一个 SMB 写事务最多等待 30 秒，持有 SMB 单协议锁，并保留操作与审计记录 90 天。审计只保存 actor、JWT `jti` 的不可逆引用、操作 ID、受控资源 ID/路径哈希、结果、problem code、时间和 Helper 协议版本；绝不保存密码、原始配置、完整路径或 SID/display name。

## 威胁处理

输入校验拒绝控制字符、换行、`[global]`、`include`、`=`、路径分隔符、option 前缀和默认 Windows share。Helper 再次校验所有固定枚举、用户名、share、路径和 SID，且没有 command、shell、PowerShell、任意服务/包/路径/SID 或通用配置写入字段。

Linux 只在唯一 marker 和受管 include 都可验证时写入；候选配置先经 `testparm`，再原子替换、reload、健康检查，失败则原子还原。Windows 每次写前读取实际对象与 ACL snapshot；ledger 只表示所有权，绝不作为 desired state；检测到外部 drift 时拒绝覆盖。两者均将端口冲突、TOCTOU、Helper/pipe 认证失败、JWT jti/target/capability/TTL 不匹配和回滚失败映射为稳定 problem code。

## 暂缓的受控集成测试

CI 的无 root/Linux Samba、无 LocalSystem/Windows Server 环境不执行真实安装、TCP 445、`testparm`、Samba password backend、Windows SMB API/ACL 回滚或第三方 SMB 客户端传输测试。这些项目将在 Goal 7 于隔离 Debian/Ubuntu 与 Windows Server VM 中执行；自动化单元测试覆盖契约、授权、验证、锁、Helper allowlist 与 fake transport 的失败路径。

当前开发容器还禁止 Kestrel 绑定测试回环 socket，因此现有 `RelaxKonOS.Server.Tests` 的 HTTP settings smoke test 会在 socket bind 阶段失败；这不是 SMB 服务或协议测试结果。受影响项目的离线编译仍是通过的。
