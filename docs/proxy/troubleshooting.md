# 代理故障排查

`proxy.privileged_operation_unavailable` 表示受约束的平台服务操作不可用；不要通过 RelaxKonOS 手工注入命令来绕过它。`proxy.recovery_required` 表示上一次 TUN 事务需要恢复后才能重试。

配置应用失败时，保留最后一份可用配置，并仅检查已脱敏的 Server 诊断信息。网络问题请使用“紧急禁用 TUN”，然后在重试前完成相应的一次性 VM 恢复测试。
