# Mihomo 运行时

托管 Mihomo 安装仅从固定的 Server 清单中选择，并在激活前完成验证。发布版本使用不可变的版本目录以及当前/上一版本回滚状态。除非管理员明确选择 RelaxKonOS 托管的实例，否则外部运行时仅可检测。

Windows System Mode 中，Mihomo 由 LocalSystem 权限助手启动和停止；Server 本身继续以 LocalService 运行。这样只有固定的、受验证的 Mihomo 二进制与配置能够获得创建 Wintun 适配器所需的系统权限，Server 不会获得通用进程执行能力。启用 TUN 后还必须在 10 秒内观测到配置的适配器处于活动状态；仅收到 Mihomo 的热重载确认不足以判定成功，超时会自动还原配置与受保护路由。

控制器仅绑定本机回环地址，其密钥保留在代理作用域的受保护存储中。
