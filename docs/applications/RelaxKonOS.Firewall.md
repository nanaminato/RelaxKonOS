# RelaxKonOS Firewall

> 状态：**已实现（Linux Server / UFW）**。Windows Server 不支持且不会在桌面或开始菜单中显示此应用。

## 定位与范围

Firewall 是 RelaxKonOS 的内置 Linux Server 防火墙编辑器。它读取并修改本机 UFW 的启用状态、默认入/出站策略和编号规则。它不是通用终端、iptables/nftables 编辑器，也不会导入或覆盖 UFW 以外的防火墙配置。

## 用户流程

1. 登录后，Shell 根据 `server.firewall` 能力和 Linux Server 平台显示应用。
2. 应用读取 UFW 状态和编号规则；UFW 未安装、命令不可用或缺少特权时显示稳定问题码。
3. 用户可启用/禁用 UFW、修改默认策略、新增、查看、修改或删除编号规则。启用和禁用按钮会随当前状态互斥；规则表显示编号、动作、方向、协议、IP 版本、来源、目标和端口，并使用 IPv4、IPv6 或 IPv4 + IPv6 明确标识地址族范围。UFW 为任意地址规则自动生成的相邻 IPv4/IPv6 项会合并显示为一条逻辑规则，更新或删除时会一起处理。新增和编辑均在模态弹窗内完成。页面始终提示这些变更可能中断当前会话。
4. root 用户的已认证会话可直接提交；其他用户每次提交都必须提供自己的 Linux 密码。密码只用于同一请求的 PAM 验证，提交后立即从 ViewModel 清空，绝不持久化、写入日志或传给 UFW。

## 架构边界

| 层 | 职责 |
| --- | --- |
| Protocol | `Firewall*` DTO 与 `/api/v1.0/firewall/*` 路由，传输结构化策略和规则，不传 shell 命令。 |
| Client | Avalonia 本地窗口、状态和一次性密码输入；`IRemoteFirewallClient` 带 JWT 调用服务端。 |
| Server | `IFirewallChangeAuthorizationService` 使用 PAM 验证非 root 的当前登录用户；`IHostFirewallService` 为宿主 UFW 边界。 |
| Linux Provider | `LinuxUfwFirewallService` 经 `IPrivilegedOperationTransport` 发送封闭的 `FirewallUfw*` 请求；Helper 使用固定 UFW 路径，规则的动作、方向、协议、端口和 IP/CIDR 均经过白名单/范围校验。 |

## 平台与权限

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Linux Server（UFW 已安装） | 已实现 | 通过 root-owned helper 管理。RelaxKonOS Server 仅获准以 `sudo -n` 调用该 helper。 |
| Linux Server（无 UFW） | 不支持 | 返回 `firewall.ufw_not_installed`，不会尝试安装或切换后端。 |
| Windows Server | 不支持 | Manifest 仅声明 Linux + `server.firewall`；图标不显示，防火墙 API 也不会注册。 |

Linux 部署脚本会安装 root:root 的统一 `RelaxKonOS.PrivilegedHelper`，并创建仅允许 Server 服务账户无密码调用该固定 apphost 的 `sudoers` 规则。Helper 不是常驻进程，只接受版本化的封闭 `FirewallUfw*` operation，并再次校验参数后才执行 UFW。应用绝不把用户密码传给 `sudo`，也不接受任意命令。Helper 或其权限缺失时返回 `firewall.privileged_proxy_required`。

安装脚本默认创建 `relaxkonos-server` 系统账户并以其运行 Server；可用第五个参数指定已有账户（例如开发机上的 `nanami`）。脚本可重复执行：它会修复 helper、sudoers 规则、服务单元和运行数据目录权限，而不会自动启用 UFW。

## 安全与错误行为

- API 需要 JWT；变更端点将 JWT 中的登录用户名与 PAM 验证绑定，不能验证其他用户或管理员密码。
- root 无需再次输入密码；其它用户缺少密码返回 `firewall.password_required`，失败返回 `firewall.password_invalid`。
- 不接受 shell 字符串。协议仅允许 `allow`/`deny`/`reject`/`limit`、`in`/`out`、`tcp`/`udp`/`any`、合法端口（或范围）和 IP/CIDR（或 `any`）。来源、目标和端口留空时按 `any` 处理，界面会给出格式示例。
- 修改规则使用受限的 `replace` helper 子命令：先删除该编号，再以相同编号插入经过验证的新规则，从而保留其他规则的相对顺序；它不接受任意 UFW 参数。
- UFW 原始 stderr、密码及规则以外的敏感信息不回显给客户端；日志只记录退出状态。
- 本版本不持久化配置副本；UFW 是唯一真源。刷新、断线重连后重新读取主机状态。

## 验收

- Linux + root：状态、规则读取和全部变更不显示密码输入；已启用时“启用”不可用，已禁用时“禁用”不可用。
- Linux + 非 root：每项变更都要求并只接受该账号的 PAM 密码；请求完成后密码为空。
- Linux + 无 UFW / 无特权：应用稳定显示不可用/需要特权代理，且不执行替代命令。
- Windows Server：登录后的桌面与开始菜单均不显示 Firewall。
- 三种语言切换后，应用名称、按钮、提示与错误文案均使用对应语言资源。
