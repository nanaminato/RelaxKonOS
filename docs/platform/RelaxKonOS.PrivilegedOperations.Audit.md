# RelaxKonOS 特权操作审计（2026-09-04）

本清单是 `RelaxKonOS.PrivilegedOperations.Goal.md` 的 Goal 0 实现记录。审计基于
`ProcessStartInfo`、宿主文件写入、服务控制、包安装、证书与网络代码路径的静态检索；它
只把实际需要 root/LocalSystem/Administrator 的动作列为特权路径。

| 范围 | 位置 | 分类 | 当前处置 |
| --- | --- | --- | --- |
| Explorer 受保护文件 | `Files`、`PrivilegedFileService` | 必须 Helper | 已迁移：capability、目标范围、Linux sudo Helper / Windows pipe Helper |
| Native service start/stop/restart | `ProcessGuardian/NativeServiceAdapter` | 必须 Helper | 已迁移：allowlisted service ID + 枚举 action；读取状态保持非特权 |
| UFW | `Firewall/LinuxUfwFirewallService` | 必须 Helper | 统一 `IPrivilegedOperationTransport` 的封闭 UFW operation；不接受任意 firewall 命令 |
| Nginx 系统服务生命周期、APT 安装/卸载 | `WebServer/NginxWebServerManager` | 必须 Helper | 已迁移：固定 nginx.service 枚举动作与固定 APT nginx 包操作 |
| Nginx 集成、站点与 ACME 配置写入 | `WebServer/NginxWebServerManager` | 必须 Helper | 待迁移；当前明确 `configuration_helper_unavailable`，不以 Server 身份写入 |
| Mihomo systemd unit 与服务生命周期 | `Proxy/Platform/NativeMihomoPrivilegedOperations` | 必须 Helper | 已迁移：固定 unit、daemon-reload 与枚举 service action 均经 Helper |
| Mihomo 受保护配置与网络恢复 | `Proxy` | 必须 Helper | 待迁移；暂保持 fail-closed，不允许 Server 以特权直写 |
| ACME / 证书部署与 80/443 绑定 | `Certificate` | 必须 Helper | 待迁移且已 fail-closed；Server 进程即使为 root/Admin 也不能直接绕过 Helper |
| Docker runtime 安装 | `Docker` | 必须 Helper 或手工操作 | 已降级为 `manual_host_action_required`；移除了管理员配置的任意安装器命令 |
| Git engine 安装 | `Git/LocalGitRepositoryService` | 必须 Helper 或手工操作 | 已迁移 Linux APT 固定安装；Windows/非 APT 主机返回手工操作提示；普通 Git 仓库操作是非特权 |
| Guardian Agent 安装器 | `ProcessGuardian/GuardianAgentInstaller` | 手工宿主操作 | 已降级为 `guardian.manual_host_action_required`；仅签名部署包可安装服务 |
| Guardian workload、FRP、普通 Docker CLI | `Guardian`、`Tunnels`、`Docker` | 无特权 | 仅对 Server 私有目录或用户权限范围运行，不进入 Helper |
| 指标、Nginx/服务发现、配置测试 | `SystemMonitor`、`WebServer` | 无特权 | 固定只读子进程；不得借用 Helper |

## 冻结的安全默认值

- Windows Helper 运行账户：`LocalSystem`；Server：`NT AUTHORITY\\LocalService` 加服务 SID。
- Windows 管理员认证允许输入不同的宿主管理员用户名；通过 `LogonUser` 获取临时 token 后检查 Administrators 成员资格。
- Linux 仅验证当前 RelaxKonOS 宿主用户的 PAM 密码，不接受隐式的另一 sudo 管理员身份。
- Linux Firewall 已迁入统一 Helper transport；安装脚本不再部署或授予独立 firewall helper 的 sudo 权限。
- 不能建模为封闭结构化能力的安装器操作必须安全拒绝，不能退回到通用命令执行。

## 尚未完成的迁移门槛

Nginx 配置事务、Mihomo 受保护配置/网络恢复与证书部署尚未迁入对应 Helper capability。
它们不能被视为已完成，后续 Goal 4/5 必须以 operation-specific 接口替换这些路径，并在隔离
Ubuntu 与 Windows Server VM 中执行实机验证。Docker runtime 和 Guardian 安装器已明确降级为
手工宿主操作；Git 的 Linux APT 安装已迁移至 Helper。
