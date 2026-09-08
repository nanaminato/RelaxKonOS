# 代理安装

1. 为代理应用授予与操作人员相符的权限。
2. 安装经清单验证的托管运行时，或验证已有的外部运行时。
3. 创建并激活配置文件，然后应用已验证的配置。
4. 在禁用 TUN 的情况下启动托管运行时并确认控制器健康状态。在 Windows 上，`RelaxKonOS.Server` 拥有 Mihomo 子进程；在 Linux 上，systemd 拥有 `mihomo.service`。
5. 只有通过 `RelaxKonOS.ProxyManager.SkippedTests.md` 中的平台检查清单后，才启用 TUN。

不可用的特权边界或平台能力是安全失败：RelaxKonOS 不会请求密码，也不会执行替代的 Shell 文本。
