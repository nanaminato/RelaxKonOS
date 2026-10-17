# 代理管理器架构

Squid 的 HTTP 代理网关使用独立的 GatewayProxy 边界，设计、实现状态与 API 见 [Squid 网关](squid.md)。

面向手机、电脑提供 VLESS 等代理服务的独立内置应用见 [代理服务端应用设计](../applications/RelaxKonOS.ProxyServer.Design.md)。该应用首选 sing-box，目前仅有设计，使用独立 ProxyServer 边界；不改变现有 Mihomo 出站代理和 TUN 的职责。

`relaxkonos.proxy` 是主机全局功能。Avalonia 只调用 `/api/v1.0/proxy`；Server 负责引擎注册表、运行时、受保护配置、操作台账和审计追踪。Mihomo 始终只是引擎边界的仅回环实现。控制器架构和密钥绝不会跨越 Server API。

高风险变更使用 `Idempotency-Key`，返回持久化操作 ID，并记录时不包含配置、凭据或控制器密钥。
