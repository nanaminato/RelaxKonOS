# RelaxKonOS SMB 运维基线

本文件冻结首轮 SMB 控制面的宿主支持矩阵和安全边界。它不改变
`RelaxKonOS.FileServices.Smb.Goal.md` 的范围。

部署、回滚、诊断和卸载步骤见 [SMB 管理员指南](./RelaxKonOS.FileServices.Smb.Administrator.md)。

## 本轮验收记录（2026-09-10）

- Goal 0：已审计 Protocol、Host elevation、Linux one-shot Helper、Windows LocalSystem pipe、HostGlobal ledger、审计、应用注册和本地化；本文件冻结支持矩阵、固定资源、超时与保留期，并记录了威胁处理。
- Goal 1：SMB-only Client↔Server DTO、路由和 `SmbLifecycleAction` 均在 `RelaxKonOS.Protocol`；Server/Client 只引用该路由常量。`SmbManage` 授权精确绑定 `smb:managed` 与 JWT `jti`，有效期五分钟。
- Goal 2–5：所有 SMB 特权请求均经封闭的 Helper operation；检测、固定包/服务动作、Linux 受管 include 事务、Windows API/ledger/snapshot、Samba credential 及无秘密审计均已接通。Helper 不提供 shell、PowerShell、通用命令、任意 service/package/path/SID 或通用配置写入。
- Goal 6：内置 App 使用 typed client；Linux 凭据 UI 仅在 capability 启用时显示，Windows 不暴露密码/账户操作。SFTP、FTP/FTPS、WebDAV、NFS 未注册任何 DTO、Provider 或 UI。
- 自动化验收：`dotnet build RelaxKonOS.sln -c Debug -m:1 -p:UseSharedCompilation=false` 与 `RelaxKonOS.Server.Tests --file-services-only` 均通过；测试替身覆盖 resolver 的 fail-closed 行为、协议锁派发和 Windows security snapshot/drift。

Goal 7 的真实宿主 mutation / 第三方客户端测试**尚未在此开发机执行**：必须在隔离 Debian/Ubuntu 与 Windows Server 2019+ VM 按下文测试矩阵执行，不能用当前 Windows 开发环境、root 或 LocalSystem 权限替代。这是受控集成验证的部署前置条件，不影响 CI 中不需 root/LocalSystem 的自动化检查。

## 支持矩阵

| 宿主 | 最低要求 | 固定资源 | 允许共享根 |
| --- | --- | --- | --- |
| Debian 12 / Ubuntu 22.04、24.04、26.04 | systemd、Samba 4（`smbd`） | `samba` 包、`smbd`、`/etc/samba/smb.conf` 中唯一 RelaxKonOS marker、`/etc/samba/relaxkonos.conf` | 任意已存在的真实目录 |
| Windows Server 2019+ | LanmanServer、SMB Server API | LanmanServer、由 HostGlobal ledger 标记的 share/ACL/security snapshot | 任意已存在的本地目录 |

Samba 安装只使用发行版的受信任默认仓库和固定的 `samba` 包；不会接受仓库、包名或版本。Linux 健康检查为 `testparm`、`smbd` active 与 TCP 445 listening；Windows 为 API 回读、LanmanServer 状态和 TCP 445。

Windows share 管理通过 LocalSystem Helper 内的 `NetShareEnum`、`NetShareGetInfo`、`NetShareAdd`、`NetShareSetInfo` 和 `NetShareDel` 编译绑定完成。它只接受受管 share 的固定字段与 SID principal，拒绝 `IPC$`、名称以 `$` 结束的默认/管理 share、reparse-point 路径；Helper 从 API 读取 security descriptor，生成回读 snapshot，并在 apply/delete 后健康失败时恢复 snapshot。没有 PowerShell、registry 或任意系统 API/命令输入。`NetShareSetInfo` 接受 `SHARE_INFO_502` 但**静默忽略其中的 path**（Windows 返回成功，共享却仍指向原目录），因此修改共享目录必须走 `NetShareDel` + `NetShareAdd` 并附带还原回滚；只有 remark/ACL 变化才使用原位更新。该宿主行为可用 `Tools/verify-windows-share-path.py` 在目标 Windows 主机上复验。

Windows SMB Server 全局安全状态通过 Helper 内固定绑定的 `ROOT\\Microsoft\\Windows\\Smb:MSFT_SmbServerConfiguration` 读取和设置，不提供通用 WMI/CIM 入口。它仅可强制 V1 基线：禁用 SMB1、启用 SMB2、启用 authenticated-user sharing，并清空 null-session share/pipe 列表。原始配置不会离开 LocalSystem Helper；Server 只在 HostGlobal 中保存不可逆 snapshot hash，之后的外部变更会进入 `reconciliation-required`，不会静默覆盖。SMB3 encryption 仅报告为 Windows 后端能力，V1 不宣称已配置全局加密。

Samba 用户列表、启用/禁用和密码更新路由只在 Linux 进程映射。Windows Server 不映射这些路由，也不显示相关 UI；Windows 只将既有 local/domain SID 用于 share ACL，绝不读取、设置或保存 Windows 帐户密码。

每一个 SMB 写事务最多等待 30 秒，持有 SMB 单协议锁，并保留操作与审计记录 90 天。审计只保存 actor、JWT `jti` 的不可逆引用、操作 ID、受控资源 ID/路径哈希、结果、problem code、时间和 Helper 协议版本；绝不保存密码、原始配置、完整路径或 SID/display name。

实现中的 `smb_audit_entries` 是 HostGlobal 表；Endpoint 在 install、生命周期、share CRUD 和 Samba credential 操作完成后写入该表。密码请求仅被映射到一次 Helper 调用，审计资源为 username 哈希而非密码或 principal。

提交到本地 Helper 的已签名 SMB mutation 不继承 HTTP request 的取消 token；它只受 Helper transport 的固定超时约束。这避免浏览器/Client 取消或连接中断时把正在写入、验证或回滚的事务显示为已取消。

## 威胁处理

输入校验拒绝控制字符、换行、`[global]`、`include`、`=`、路径分隔符、option 前缀和默认 Windows share。Helper 再次校验所有固定枚举、用户名、share、路径和 SID，且没有 command、shell、PowerShell、任意服务/包/路径/SID 或通用配置写入字段。

Linux 只在唯一 marker 和受管 include 都可验证时写入；候选配置先经 `testparm`，再原子替换、reload、健康检查，失败则原子还原。Windows 每次写前读取实际对象与 ACL snapshot；ledger 只表示所有权，绝不作为 desired state；检测到外部 drift 时拒绝覆盖。两者均将端口冲突、TOCTOU、Helper/pipe 认证失败、JWT jti/target/capability/TTL 不匹配和回滚失败映射为稳定 problem code。

## 暂缓的受控集成测试

CI 的无 root/Linux Samba、无 LocalSystem/Windows Server 环境不执行真实安装、TCP 445、`testparm`、Samba password backend、Windows SMB API/ACL 回滚或第三方 SMB 客户端传输测试。这些项目将在 Goal 7 于隔离 Debian/Ubuntu 与 Windows Server VM 中执行；自动化单元测试覆盖契约、授权、验证、锁、Helper allowlist 与 fake transport 的失败路径。

`RelaxKonOS.Server.Tests/FileServiceChecks.cs` 不要求 Samba、root、LocalSystem 或 TCP 445，验证 SMB-only provider resolver、未注册 provider 的 fail-closed 状态、连接信息以及 Manager→Provider 生命周期派发。
可单独执行它和 SMB 契约验证：`dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj -c Debug --no-build --no-restore /p:UsePrebuiltServerAssembly=true -- --file-services-only`。

当前开发容器还禁止 Kestrel 绑定测试回环 socket，因此现有 `RelaxKonOS.Server.Tests` 的 HTTP settings smoke test 会在 socket bind 阶段失败；这不是 SMB 服务或协议测试结果。上面的 File Services 专用测试可正常编译和执行。该容器的 .NET SDK 10.0.400 还缺少 `Microsoft.NET.SDK.WorkloadAutoImportPropsLocator` / `Microsoft.NET.SDK.WorkloadManifestTargetsLocator` 的 SDK 目录；这会使 Avalonia Client 的 MSBuild 以零诊断失败，需在完整桌面 SDK 环境重新构建 Client。
