# RelaxKonOS SMB 管理员指南（V1）

RelaxKonOS 管理 SMB 的控制面，不转发、不代理、不检查 SMB 文件数据。客户端必须直接连接 Samba 或 Windows SMB Server，例如 `\\host\\share` 或 `smb://host/share`。

## 部署前置条件

| 平台 | 受支持宿主 | 服务账户与 Helper |
| --- | --- | --- |
| Linux | Debian 12、Ubuntu 22.04/24.04，systemd，Samba 4 | Server 以普通账户运行；root-owned one-shot Helper 通过最小 sudoers 规则运行。 |
| Windows | Windows Server 2019+，LanmanServer | Server 以普通服务账户运行；只允许该服务 SID 访问的 LocalSystem named-pipe Helper 运行系统 API。 |

Server 本身不应以 root、Administrator 或 LocalSystem 运行。Helper 不监听网络端口，不接受 shell、PowerShell、可执行文件、参数、任意 service/package/config path 或 SID 输入。

首次 Linux 安装由 Helper 从受信任默认 APT 源安装固定 `samba` 包；Windows Server 安装由 Helper 通过 Windows Server Manager WMI 部署 API 启用固定 `FS-FileServer` role。该操作不接受 role 名、源、参数或命令文本；客户端 Windows 不支持此安装。防火墙不由本模块更改：TCP 445 冲突和防火墙状态只会显示为诊断。

## 所有权与共享根

Linux 可共享任意现有的真实目录；共享根目录或系统目录可能暴露敏感信息。它只拥有：

- `/etc/samba/smb.conf` `[global]` 中唯一的 RelaxKonOS include marker；
- `/etc/samba/relaxkonos.conf` 及其原子暂存/备份文件；
- 带稳定 ID 的 share sections。

若 marker 重复、目标 include 被改写、`[global]` 无法安全定位或 `testparm` 失败，操作返回 `file-services.smb.configuration_unmanaged` 或 `file-services.smb.configuration_invalid`；绝不接管、重写或删除管理员的 share/configuration。

Windows 可共享任意已存在的本地目录，仍拒绝 reparse-point 路径。共享磁盘根目录或系统目录可能暴露敏感信息。HostGlobal ownership ledger 保存由 RelaxKonOS 创建的 **share name**、路径哈希和实际 API readback snapshot。`ADMIN$`、`C$`、`IPC$` 和任意 `$` share 永远不可管理。资源被重命名、删除、替换、路径或 ACL 改变时标记 drift，返回 `file-services.smb.reconciliation_required`，而不会覆盖外部改动。

允许 share ACL 并不等同于 Unix/NTFS 文件系统 ACL 允许。V1 只检测并显示控制面规则，不修改 chown/chmod/Unix ACL/NTFS ACL。

## 事务与恢复

Linux 每次写入：读取并验证 marker/include → 生成确定性配置 → 私有暂存 → `testparm` → 原子替换 → `smbd reload` → service/TCP 445/`testparm` 健康检查。失败时 Helper 原子恢复旧 include/marker、重新验证并 reload；若恢复失败，停止继续配置并保留不含秘密的 problem code。

Windows 每次写入：读取 NetShare 实际对象/security descriptor → 验证 ledger/snapshot 与 SID → 通过受限 `NetShare*` API apply → readback/LanmanServer/TCP 445 health check。失败时恢复 API snapshot；若无法恢复，标记 reconciliation-required。不要手工编辑 ledger 来“接管”外部 share。

一旦 typed Helper 请求提交，HTTP 取消不会中断事务；它受 Helper 固定超时限制。管理者应刷新状态和审计，而不是假设客户端关闭意味着操作撤销。

## 身份、密码与审计

Linux 只列出已有、合格的本地用户；启用、禁用或设置 Samba password 不创建/删除宿主用户。密码仅穿过一次 TLS 请求与一次 Helper stdin/API 调用，不出现在 GET、日志、异常、数据库、审计或重放 payload。

Windows principal 必须是现有 SID，且只能用于 share ACL；V1 不管理 Windows 账户、组或密码。

成功或失败的管理操作写入 HostGlobal `smb_audit_entries`：actor、哈希化 JWT `jti`、operation ID、action、资源哈希、结果、problem code、Helper protocol version 与时间。审计保留 90 天；不保存明文路径、SID、显示名、密码或 Samba 配置。

## 故障诊断与卸载

- `file-services.smb.helper_unavailable`：确认 Helper 已按平台安装、Linux sudoers 规则/Windows service SID 和 pipe shared secret 完整；不要通过放宽 sudoers 或 pipe ACL 解决。
- `file-services.smb.port_in_use`：使用现有宿主诊断识别占用 TCP 445 的服务；本模块不终止其他服务。
- `file-services.smb.configuration_unmanaged`：恢复唯一 include marker 或将管理员配置保持为外部配置；不要手动粘贴 RelaxKonOS fragments 到多个位置。
- `file-services.smb.reconciliation_required`：检查 Windows share API 实际状态与 ACL，选择保持外部修改或在 UI 中重新确认新的受管资源；不要编辑数据库账本。
- `file-services.smb.system_account_not_found`：先在宿主操作系统中准备用户；V1 不提供账户创建。

卸载时先删除所有 RelaxKonOS 管理 share；Linux 再删除唯一 marker 与 `/etc/samba/relaxkonos.conf`，但绝不删除 Samba 包、宿主用户、共享目录或管理员 share。Windows 仅删除 ledger 仍拥有且 API snapshot 未 drift 的 share；drift 资源必须由管理员手工处置，且不会移除 File Server role。SFTP、FTP/FTPS、WebDAV 和 NFS 均不属于 V1。


## Desktop file service pages

The desktop selects controls from the connected server's SMB capabilities, not the desktop operating system. Overview shows the provider, service version, runtime status and selectable connection prefixes. Append the share name to connect. Installation is available only for a server reporting installation support and a not-installed runtime. Start is enabled when stopped; stop/restart when running.

Shares uses localized columns, including permissions, ownership and configuration drift. Only managed shares can be edited or removed. Removing a share requires confirmation and retains directory contents. Use any existing directory with SID principals on Windows, or system usernames on Linux. Windows offers common groups in a dropdown and accepts custom SIDs. Sharing drive roots or system directories may expose sensitive data. Symbolic links and reparse points remain rejected. Guest access is independently read-only; authenticated principals retain their configured write access unless read-only for all users is enabled. Share names support Unicode letters, numbers, and spaces.

The Samba users page exists only when the server advertises Samba credential support. Select an eligible system account to change credentials. Windows uses its native security principals and has no Samba password page. Mutations remain locked throughout administrator authorization and refresh the server state after completion. Chinese, English and Japanese resources cover columns, runtime states and SMB problem codes.

Run desktop control-plane regression checks with `dotnet run --project Client/RelaxKonOS.FileServices.Tests`.

### Guest and authenticated write access

“Allow guest read-only access” is independent of “Read-only for all users”. On Windows, explicit read/write rules remain writable while anonymous and Builtin Guests receive read access and a deny rule for mutation rights, including when Everyone has write access. The global read-only option also applies to Administrators; no implicit administrative write ACE is added.

Samba uses a read-only default plus an explicit write list for authenticated usernames when guests are enabled. The fixed guest account is `nobody`; it cannot be granted write access, and guest-enabled write rules accept individual system usernames. With global read-only enabled the write list is explicitly empty. The managed format stores the requested global read-only setting separately from Samba's effective default so reloading preserves the distinction. Existing host and client authentication policies and filesystem ACLs still determine whether a guest can connect and read files.
