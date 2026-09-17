# 代理故障排查

`proxy.privileged_operation_unavailable` 表示受约束的平台服务操作不可用；不要通过 RelaxKonOS 手工注入命令来绕过它。首次安装 Mihomo 时，先重新运行受支持的 RelaxKonOS 部署脚本：它会为 Server 服务账户配置固定的 Proxy 工作目录、特权 Helper 和服务控制权限。Linux 需要检查 `/var/lib/relaxkonos/proxy`、`/etc/relaxkonos/proxy`、`/var/log/relaxkonos/proxy` 的账户与权限；Windows 需要检查 `%ProgramData%\RelaxKonOS\Proxy` 对 RelaxKonOS Server 服务 SID 的“修改”权限，以及 `RelaxKonOSPrivilegedHelper` 服务、命名管道 ACL 和共享密钥。`proxy.recovery_required` 表示上一次 TUN 事务需要恢复后才能重试。

配置应用失败时，保留最后一份可用配置，并仅检查已脱敏的 Server 诊断信息。网络问题请使用“紧急禁用 TUN”，然后在重试前完成相应的一次性 VM 恢复测试。
