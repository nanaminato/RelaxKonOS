# RelaxKonOS 代理管理器——Goal 0 决策记录

> 状态：于 2026-08-31 接受。本记录冻结实现至 Goal 10 必须遵从的 V1 决策。代码依赖这些决策前，任何变更都必须同时更新本记录、设计规范和受影响测试。

## 评审结果

`RelaxKonOS.ProxyManager.Discovery.md` 对当前代码树仍然准确：尚无代理功能或通用提权工作流。可复用的只是模式：主机元数据的 `HostGlobalMigrationRunner`、持久幂等操作的 `WebServerOperationStore`、仅 Server 加密值的 `DataProtectionSecretStore`、验证/暂存/回滚经验的 `FrpRuntimeManager`，以及白名单生命周期控制的 `INativeServiceAdapter`。这些均不得扩展为监管 Mihomo 或通用命令运行器。

## V1 平台与运行时发布矩阵

| RelaxKonOS RID | 支持主机 | 固定 Mihomo 资产 | SHA-256 |
| --- | --- | --- | --- |
| `win-x64` | Windows 10/11、Windows Server，x64 | `mihomo-windows-amd64-v1.19.30.zip` | `22c09fd67673895ef7cd6b1820563918275c3d316f2462b306208675118db3c0` |
| `win-arm64` | Windows 11 / Windows Server，ARM64 | `mihomo-windows-arm64-v1.19.30.zip` | `b37c4b0259e85b020edc4215aa4c86052e21071cf520d4800364b21b4e2fc162` |
| `linux-x64` | Ubuntu 24.04+ / Ubuntu Server，x64 | `mihomo-linux-amd64-v1.19.30.gz` | `cf06ce2c7d1421bdbda14ee4a5b6046672dc35ebf8eecd8e77504ec3c0ed9a84` |
| `linux-arm64` | Ubuntu 24.04+ / Ubuntu Server，ARM64 | `mihomo-linux-arm64-v1.19.30.gz` | `58896873736d28628f66de3677c8654fa0f180662523148e136cff4f6e890069` |

V1 唯一的托管运行时版本是稳定版 `v1.19.30`。预发布/Alpha、“latest”URL、兼容性变体、发行版软件包、x86 及其他所有平台均拒绝，并返回 `proxy.runtime_unsupported_platform` 或 `proxy.runtime_version_unsupported`。Goal 3 必须在受源代码控制的 `MihomoRuntimeManifest` 中放入四个精确 HTTPS 发布 URL、版本、资产名、哈希、源发布 URL 和获取时间；网络响应不能新增或替换条目。权威来源为 2026-08-16 发布的官方 `MetaCubeX/mihomo` GitHub Release `v1.19.30`。

## 主机所有权、架构与受保护路径

代理状态由机器拥有，绝不存入 `RelaxKonOSDbContext`、工作区偏好、应用清单或用户拥有的行。Goal 4 在 `HostGlobalMigrationRunner` 新增迁移 **8**，用于 `proxy_profiles`、`proxy_runtime_state`、`proxy_operations`、`proxy_audit_entries` 和 `proxy_safety_state`。ID 使用 GUID、日期使用 UTC 文本，列中不存 YAML 或密钥；专用代理元数据仓库只使用此主机全局架构。

| 类型 | Windows | Linux | 保留/访问 |
| --- | --- | --- | --- |
| 托管二进制 | `%ProgramData%\\RelaxKonOS\\Proxy\\engines\\mihomo\\versions` | `/opt/relaxkonos/proxy/engines/mihomo/versions` | 仅机器管理员；`active` 与 `previous` 是原子指针 |
| 原始 YAML、覆盖层、备份、运行时状态、恢复标记 | `%ProgramData%\\RelaxKonOS\\Proxy\\state` | `/var/lib/relaxkonos/proxy` | 仅服务账户和管理员；备份轮换保留最近 5 个成功代际 |
| 服务配置 | `%ProgramData%\\RelaxKonOS\\Proxy\\config` | `/etc/relaxkonos/proxy` | 受保护；只能由结构化输入及已验证原始 YAML 生成 |
| 脱敏运维日志 | `%ProgramData%\\RelaxKonOS\\Proxy\\logs` | `/var/log/relaxkonos/proxy` | 每文件 10 MiB、5 文件；写入前删除控制器/凭据值 |
| 加密的控制器/订阅密钥 | 代理专用 Data Protection 存储 | 代理专用 Data Protection 存储 | 按用途隔离 `RelaxKonOS.Proxy.SecretStore.v1`；无列表/导出/读取 API |

配置文件元数据仅可通过不透明标识符引用受保护 YAML，不含文本。活动配置文件、恢复标记引用、运行时选择、操作/审计引用与安全状态均为主机范围。所有路径由 `IProxyPlatformPaths` 提供；业务服务不得接收或创建绝对路径。

## 授权与能力映射

应用权限仅是桌面端提示，Server 才是权威：使用 JWT 角色策略，绝不使用客户端 app ID。`ProxyRead` 允许 `controller` 或 `observer`；`ProxyManage`、`ProxyRuntimeManage`、`ProxyTunManage`、`ProxyRecoveryExecute` 需要 `controller`。后三者还要求已安装平台特定的特权操作部署，缺失时返回 `proxy.privileged_operation_unavailable`。每个危险变更需要 `Idempotency-Key`，创建持久操作并审计。

| 稳定应用能力 | Server 策略 | 范围 |
| --- | --- | --- |
| `server.proxy.read`、`server.proxy.profile.read`、`server.proxy.connection.read`、`server.proxy.tun.read`、`server.proxy.runtime.read` | `ProxyRead` | 安全状态与脱敏诊断 |
| `server.proxy.manage`、`server.proxy.profile.manage`、`server.proxy.connection.close` | `ProxyManage` | 配置文件/生命周期/节点/连接操作 |
| `server.proxy.runtime.manage` | `ProxyRuntimeManage` | 仅经验证的托管运行时 |
| `server.proxy.tun.manage` | `ProxyTunManage` | Goal 5 验证后的 TUN 启用/禁用 |
| `server.proxy.recovery.execute` | `ProxyRecoveryExecute` | 紧急安全网络恢复 |

## 问题代码与特权边界

公共代码为 Protocol 中唯一声明的小写点分 ASCII，包括运行时未安装/平台或版本不支持/归档不可用/完整性或健康检查失败、外部运行时无效、服务或特权操作不可用、配置无效或应用失败、控制器不可用/响应无效/超时、管理路由不安全、平台能力不可用、TUN 权限或激活失败、需要或恢复失败、操作中断、缺少幂等键、权限拒绝和不支持（对应 `proxy.*` 代码）。未实现能力返回 `proxy.not_supported`；不支持主机返回 `proxy.platform_capability_unavailable`；控制器失败不得转发其正文。客户端本地化代码，且不显示原始控制器或 OS 输出。

Goal 3 引入 `IProxyPrivilegedOperations`，只包含 `InstallRuntime`、`RemoveRuntime`、`ReplaceRuntime`、`InstallService`、`RemoveService`、`SetServiceStartup`、`StartService`、`StopService`、`RestartService`、`WriteProtectedConfiguration`、`RestoreNetworkConfiguration` 和 `RepairService`。方法只接收经验证的类型化请求（ID、哈希、固定路径），绝不接收可执行文件、命令行、参数列表、Shell 文本、环境、密码或客户端路径。Windows 服务控制及 Linux systemd/路由/DNS 代码留在平台实现内；领域服务没有 `Process.Start` 回退。

Proxy Manager 未获授权修改 Defender、SmartScreen、UFW、nftables、iptables 或 Windows 防火墙；只可诊断防火墙。外部运行时检测只读；即使管理员明确选择其供 RelaxKonOS 私有配置使用，也不会覆盖、升级、卸载或停止用户拥有的进程/二进制。

## 审计与威胁模型

每次安装、更新、回滚、卸载、生命周期变更、配置文件/配置变更、节点选择、连接关闭、TUN 转换、恢复操作和拒绝都生成不含密钥的审计事件，包含操作人、会话、主机、引擎、适用的配置文件 ID、操作/关联 ID、结果、问题代码和 UTC 时间戳。审计、日志、异常和 DTO 字段绝不包含控制器密钥、含 Token 的 URL、认证头、代理凭据、UUID、私有/WireGuard 密钥、完整 YAML 或命令输出。

实现和测试必须拒绝或安全处理恶意归档与路径穿越、哈希/架构/二进制验证失败、YAML 与命令注入、密钥/日志/异常泄漏、公共控制器绑定、操作中断、路由/DNS 损坏、活动管理路由丢失、Defender 或组织策略拒绝、特权部署缺失，以及 TUN 转换期间的 Server/Mihomo 崩溃或重启。每次网络变更前都写入恢复标记和网络快照。在 Goal 5 于 Server 和平台适配器验证完整恢复路径之前，不公开 TUN API 或 UI。
