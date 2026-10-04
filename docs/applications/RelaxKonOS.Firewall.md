# RelaxKonOS Firewall

> 状态：**已实现（Linux UFW / Windows Defender Firewall）**。Windows 系统模式与 Windows 10/11 个人模式均支持。

## 定位与范围

Firewall 是 RelaxKonOS 的内置宿主防火墙编辑器。它读取并修改宿主防火墙的启用状态、默认入/出站策略和编号规则；Linux 使用 UFW，Windows 使用 Windows Defender Firewall。操作只接受结构化字段；Linux 不导入 iptables/nftables 配置，Windows 只编辑本应用管理的规则。

## 用户流程

1. 登录后，Shell 根据 `server.firewall` 能力和 Linux/Windows 平台显示应用。
2. 应用读取 UFW 状态和编号规则；UFW 未安装、命令不可用或缺少特权时显示稳定问题码。
3. 用户可启用/禁用 UFW、修改默认策略、新增、查看、修改或删除编号规则。启用和禁用按钮会随当前状态互斥；规则表显示编号、动作、方向、协议、IP 版本、来源、目标和端口，并使用 IPv4、IPv6 或 IPv4 + IPv6 明确标识地址族范围。UFW 为任意地址规则自动生成的相邻 IPv4/IPv6 项会合并显示为一条逻辑规则，更新或删除时会一起处理。新增和编辑均在模态弹窗内完成。页面始终提示这些变更可能中断当前会话。
4. 经系统账户认证的 root/宿主管理员无需重复输入密码；每次变更重新检查当前管理员策略。普通用户及 Alias 会话经统一授权入口验证所选管理员账户，取得 `firewallChange` + `host/firewall` 的五分钟、令牌绑定授权后重试一次。操作确认仍保留；变更请求不携带密码。

## 架构边界

| 层 | 职责 |
| --- | --- |
| Protocol | `Firewall*` DTO 与 `/api/v1.0/firewall/*` 路由，传输结构化策略和规则，不传 shell 命令。 |
| Client | Avalonia 本地窗口、状态和操作确认；`IRemoteFirewallClient` 经统一 `IHostElevationBroker` 授权并带 JWT 调用服务端。 |
| Server | `IHostElevationSessionStore` 统一检查动态管理员资格或精确临时授权；`IHostFirewallService` 为宿主防火墙边界。 |
| Linux Provider | `LinuxUfwFirewallService` 经 `IPrivilegedOperationTransport` 发送封闭的 `FirewallUfw*` 请求；Helper 使用固定 UFW 路径，规则的动作、方向、协议、端口和 IP/CIDR 均经过白名单/范围校验。 |

## 平台与权限

| 平台 | 状态 | 说明 |
| --- | --- | --- |
| Linux Server（UFW 已安装） | 已实现 | 通过 root-owned helper 管理。RelaxKonOS Server 仅获准以 `sudo -n` 调用该 helper。 |
| Linux Server（无 UFW） | 不支持 | 返回 `firewall.ufw_not_installed`，不会尝试安装或切换后端。 |
| Windows 系统／个人模式 | 已实现 | WindowsFirewallService 经 Helper 的 FirewallWindows* 固定操作调用 Windows COM API。 |

Linux 部署脚本会安装 root:root 的统一 `RelaxKonOS.PrivilegedHelper`，并创建仅允许 Server 服务账户无密码调用该固定 apphost 的 `sudoers` 规则。Helper 不是常驻进程，只接受版本化的封闭 `FirewallUfw*` operation，并再次校验参数后才执行 UFW。应用绝不把用户密码传给 `sudo`，也不接受任意命令。Helper 或其权限缺失时返回 `firewall.privileged_proxy_required`。

安装脚本默认创建 `relaxkonos-server` 系统账户并以其运行 Server；可用第五个参数指定已有账户（例如开发机上的 `nanami`）。脚本可重复执行：它会修复 helper、sudoers 规则、服务单元和运行数据目录权限，而不会自动启用 UFW。

## 安全与错误行为

- API 需要 JWT；变更端点检查当前宿主策略或当前令牌的 `firewallChange` / `host/firewall` grant。缺少授权返回 `firewall.elevation_required`；管理员认证问题码由统一提权入口返回。
- 已删除 `credentialConfirmation` 及 DELETE 请求体，密码只发送至统一管理员认证入口，不保留旧格式。管理员账户必须通过 PAM 与固定 Helper 的 root sudoers 资格检查；不能只凭用户自己的有效密码授权。
- 不接受 shell 字符串。协议仅允许 `allow`/`deny`/`reject`/`limit`、`in`/`out`、`tcp`/`udp`/`any`、合法端口（或范围）和 IP/CIDR（或 `any`）。来源、目标和端口留空时按 `any` 处理，界面会给出格式示例。
- 修改规则使用受限的 `replace` helper 子命令：先删除该编号，再以相同编号插入经过验证的新规则，从而保留其他规则的相对顺序；它不接受任意 UFW 参数。
- UFW 原始 stderr、密码及规则以外的敏感信息不回显给客户端；日志只记录退出状态。
- 编号规则读取失败返回 HTTP 503 与稳定 `problemCode`，不把 Helper 拒绝或不可用映射成成功的空规则集合。
- 本版本不持久化配置副本；UFW 是唯一真源。刷新、断线重连后重新读取主机状态。

## 验收

- Linux + root：状态、规则读取和全部变更不显示密码输入；已启用时“启用”不可用，已禁用时“禁用”不可用。
- Linux + 非 root 管理员：无需重复密码，撤权后停止自动授权。
- Linux + 普通用户 / Alias：通过有效管理员账户精确授权；非管理员的有效密码不能获得权限。
- Linux + 无 UFW / 无特权：应用稳定显示不可用/需要特权代理，且不执行替代命令。
- Windows：登录后的桌面与开始菜单显示 Firewall；缺少 Helper 时显示不可用；策略受组策略限制时拒绝写入。
- 三种语言切换后，应用名称、按钮、提示与错误文案均使用对应语言资源。

## Windows Defender Firewall

Windows 后端只管理 `RelaxKonOS Firewall` 分组及 `RelaxKonOS.Firewall.<编号>` 名称的规则，编号稳定，不随删除其他规则改变。系统、第三方与部署向导规则不在编辑列表中。新规则适用 Domain/Private/Public 三个配置；启停与默认策略也同时应用于三个配置。默认策略不一致时 Server 返回 null，UI 不把它当作统一策略。

允许／阻止、入／出站、TCP／UDP／任意协议、IP/CIDR、单端口与范围均在 Server 和 Helper 校验；Windows 不支持 reject/limit，任意协议只能使用任意端口。入站端口是本地端口，出站端口是远程端口。Helper 使用原生 COM 接口，不运行 PowerShell 或用户命令。变更通过统一 `FirewallChange` + `host/firewall` 精确授权。

Helper 串行执行操作；规则替换在添加失败时尝试恢复旧规则，配置变更失败时尝试恢复原值。此过程不是操作系统事务，失败后必须重新读取事实。组策略限制或 Helper 不可用时返回失败。原生读检查已在 Windows 执行；真实主机启停、默认策略和规则变更仍需在隔离测试主机验收。
