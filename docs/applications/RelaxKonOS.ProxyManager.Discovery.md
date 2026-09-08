# 代理管理器实现调研

> 状态：**代码级实现已完成，尚未完成发布级平台验证**  
> 核对日期：2026-09-04  
> 核对范围：`feature_privileged` 当前代码、Proxy Manager 提交历史和跳过测试登记表。本文不改变产品代码。

## 当前结论

旧的阶段 0 结论（“尚无 Proxy Manager，必须从阶段 1 开始”）已不再适用。当前分支已有可运行的 `remoteos.proxy` 内置应用、`/api/v1.0/proxy` API、Server-only Mihomo 适配器、受保护的配置/订阅存储、运行时生命周期、受限特权操作边界、TUN 事务框架和审计/操作台账。

这不等于 Proxy Manager 已达到 V1 发布条件：真实 Windows/Ubuntu 特权、Mihomo 生命周期、TUN 路由/DNS 变更、崩溃/重启恢复仍未在隔离主机上验证；当前生产网络平台实现会在不能证明安全时拒绝变更。下一阶段是**受控平台验证与发布收尾**，不是重新实现阶段 1。

## 已实现能力

| 层面 | 当前实现 |
| --- | --- |
| 协议与授权 | `Shared/RelaxKonOS.Protocol/Proxy` 提供 engine-neutral DTO、路由、状态和稳定 `proxy.*` 问题码；`MapProxyEndpoints` 已映射 `/api/v1.0/proxy`。`ProxyRead`、`ProxyManage` 和 `ProxyDangerous` 策略将读取、管理及运行时/TUN 等危险操作分开；长操作使用持久化 operation ID 和 `Idempotency-Key`。 |
| Server 与 Mihomo | `MihomoEngine`、仅 Server 使用的 loopback Controller client、控制器密钥保护存储、运行状态/代理组/节点选择、路由模式、延迟测试、连接关闭、流量/内存、日志和 DNS 状态均已接入。Controller 地址、密钥和原始 Controller JSON 不会进入 Client API。 |
| 托管运行时 | `MihomoRuntimeManager` 使用源代码固定的受信任清单；支持下载或从 Server 文件安装，执行大小/归档路径/哈希/架构/版本检查、暂存、健康检查、active/previous 切换、回滚和卸载。Linux 使用受限的 `systemd` 操作；Windows 由 `WindowsMihomoProcessHost` 管理 Mihomo 进程树及异常重启/宿主停止清理。 |
| 配置与订阅 | 主机全局 SQLite 元数据、受保护 raw YAML、串行配置事务、备份/原子提交/reload/健康检查/回滚、订阅导入/刷新/激活和加密 URL 存储均已实现。订阅默认仅接受公网 HTTPS、禁止重定向并限制响应；可显式选择经过验证的系统代理路径。Base64/明文节点列表可转换为 Mihomo YAML；受保护的本地 `geoip.metadb` 支持离线校验与运行。 |
| 特权与恢复 | `IProxyPrivilegedOperations` 只允许固定的 Mihomo 运行时、服务和网络恢复操作，不接受通用命令、参数或密码。统一特权助手已覆盖 Linux `remoteos-mihomo.service` 的固定操作；缺少可用 Helper/Windows 服务权限或管道 ACL 时，当前分支以 `proxy.privileged_operation_unavailable` 返回统一的中/英/日修复指引。TUN 已有全局锁、管理路由方案、恢复标记、恢复 hosted service、禁用和紧急禁用路径。 |
| Avalonia | 已注册 `IProxyRepository` / `RemoteProxyRepository` 和单窗口 `remoteos.proxy` 应用。工作区包含概览、订阅、代理组、连接、日志和设置；支持运行时安装/回滚/卸载、启停、订阅、节点、路由模式、测速、系统代理、TUN 设置及紧急禁用。所有请求经类型化 RelaxKonOS API；中、英、日资源已接入。 |
| 可观测性与测试 | 安装、生命周期、TUN、订阅、配置、节点和连接操作有无秘密审计；诊断日志有界且经脱敏。`RelaxKonOS.Server.Tests` 已覆盖协议、主机级持久化、订阅加密与下载限制、GEO 数据、配置事务、TUN 故障关闭/恢复标记、Controller 安全、运行时归档与回滚等进程内场景。 |

## 近期实现变化

- 2026-09-01 起，Windows 不再创建额外 SCM 服务：`RelaxKonOS.Server` 通过 `WindowsMihomoProcessHost` 直接拥有 Mihomo 子进程；Linux 仍使用 `remoteos-mihomo.service`。
- 2026-09-01 至 03，补齐了订阅安全导入、离线 GeoIP、代理组/路由模式/测速、流量与内存、系统代理和受管 TUN 配置；配置刷新不再隐式重启或拉取订阅。
- 2026-09-04，统一特权助手链路已让 Proxy 与其他特权功能一致地保留并呈现“特权助手不可用”的结构化问题码和平台对应修复指引。

## 剩余缺口与发布门槛

1. **真实平台验证尚未完成。** `docs/testing/RelaxKonOS.ProxyManager.SkippedTests.md` 所列 Windows/Windows Server 与 Ubuntu/Ubuntu Server 用例仍待在隔离、可丢弃的主机上执行：托管运行时安装/更新/回滚、服务生命周期、TUN 启停、紧急恢复，以及 Mihomo/Server/OS 崩溃和重启后的恢复。
2. **TUN 目前按安全失败。** `HostProxyNetworkSafetyPlatform` 仅在 Linux 读取默认路由以生成管理路径方案；Windows 返回无方案，而 apply、verify 和 restore 目前一律拒绝。因此 API/UI 和恢复模型已在，但当前默认平台实现不会宣称已经能安全地修改真实路由或 DNS。
3. **特权部署是前置条件。** Linux 需要已部署 root-owned Helper、固定发布目录和 sudoers 配置；Windows 需要服务、命名管道 ACL 与共享密钥。缺失任一条件时，运行时/服务所需的操作应失败，不得绕过到 shell 或收集操作系统密码。
4. **API 宿主集成验证待补。** 进程内 Server 测试覆盖了大部分领域安全路径；跳过测试登记表仍要求增加并运行真实 API 宿主夹具，以验证代理授权、幂等、操作恢复和审计输出。
5. **范围仍是单引擎 V1。** 仅 Mihomo 已实现；sing-box/Xray、集中式多主机编排、规则可视化编辑器、流量历史库和自动订阅刷新不在当前范围。系统代理目前仅 Windows 支持；“开机自启”没有实现，UI 明确显示未启用。

## 下一阶段入口

先在带第二条管理连接的临时 VM 上，依照 [`RelaxKonOS.ProxyManager.SkippedTests.md`](../testing/RelaxKonOS.ProxyManager.SkippedTests.md) 执行 PM-G5-WIN-01 至 03 和 PM-G5-UBU-01 至 03；记录 RelaxKonOS 修订、Mihomo 资产哈希、环境、结果与问题码。随后补齐并运行 PM-G6-G8-API-01 的 API 宿主夹具。所有用例通过前，不得将 Proxy Manager 标记为 V1 已完成，尤其不得在生产主机首次启用 TUN。

设计范围与长期安全约束仍见 [`RelaxKonOS.ProxyManager.Design.md`](./RelaxKonOS.ProxyManager.Design.md)，执行基线见 [`RelaxKonOS.ProxyManager.Goal.md`](./RelaxKonOS.ProxyManager.Goal.md)，操作员文档见 [`docs/proxy/`](../proxy/)。若这些早期规划文档与本页的“当前实现”叙述冲突，以本页、代码和跳过测试登记表为准，并应在后续文档维护中同步修正。
