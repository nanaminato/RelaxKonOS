# 代理管理器架构

`relaxkonos.proxy` 是主机全局功能。Avalonia 只调用 `/api/v1.0/proxy`；Server 负责引擎注册表、运行时、受保护配置、操作台账和审计追踪。Mihomo 始终只是引擎边界的仅回环实现。控制器架构和密钥绝不会跨越 Server API。

高风险变更使用 `Idempotency-Key`，返回持久化操作 ID，并记录时不包含配置、凭据或控制器密钥。
