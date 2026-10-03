# Mihomo 运行时

托管 Mihomo 安装仅从固定的 Server 清单中选择，并在激活前完成验证。发布版本使用不可变的版本目录以及当前/上一版本回滚状态。除非管理员明确选择 RelaxKonOS 托管的实例，否则外部运行时仅可检测。

Windows System Mode 中，Mihomo 由 LocalSystem 权限助手启动和停止；Server 本身继续以 LocalService 运行。这样只有固定的、受验证的 Mihomo 二进制与配置能够获得创建 Wintun 适配器所需的系统权限，Server 不会获得通用进程执行能力。启用 TUN 后还必须在 10 秒内观测到配置的适配器处于活动状态；仅收到 Mihomo 的热重载确认不足以判定成功，超时会自动还原配置与受保护路由。

控制器仅绑定本机回环地址，其密钥保留在代理作用域的受保护存储中。

Server 的 Data Protection 应用标识固定为 `RelaxKonOS.Server`，不随发布目录变化。升级与恢复备份时必须同时保留受保护数据和服务账户的 Data Protection 密钥环。无法读取控制器密钥时，首次安装会回滚，并在代理诊断日志中明确记录密钥读取失败；这不代表归档损坏或用户 YAML 校验失败。服务不会自动丢弃或重建无法解密的已有密钥。

系统代理按 `platformCapabilities.systemProxy` 的实际能力开放，包含 `supported`、`supportsPac`、`loginEnvironment`、`desktopSession`，不再按 Windows 名称推断。Windows 仍使用交互用户的 HKCU；服务账户不能代写登录用户的设置。Linux System Mode 通过协议 1.4 的固定 Helper 操作同时管理 `/etc/environment` 中大小写 HTTP_PROXY、HTTPS_PROXY、ALL_PROXY、NO_PROXY，以及当前已登录的本地 GNOME/KDE 用户代理。HTTP/HTTPS 使用 Mihomo HTTP CONNECT，ALL_PROXY 使用 SOCKS5；PAC 仅 Windows 提供。

Linux 环境变量在读取该文件的新的 PAM 登录会话生效，已运行的终端和独立 systemd 服务须使用各自的生效机制；Docker、受管下载继续使用独立宿主出站代理设置。GNOME 写入用户 dconf，KDE 写入用户 `kioslaverc` 并通知 KIO，均以桌面用户身份执行。仅发现 `loginctl` 中非远程、非 root、x11/wayland、GNOME/Ubuntu 或 KDE/Plasma 的普通用户会话；采用默认 `$HOME/.config`，不读取任意会话进程环境。新桌面用户在再次保存或开启代理守护后纳入。依赖与部署说明见 [Helper 运维指南](../platform/RelaxKonOS.PrivilegedOperations.Operations.md#linux-系统代理)。

启用前保存每个代理键的原值，先记录写入意图再修改；更新端口不覆盖最初原值。关闭只恢复仍与本应用所写值一致的键，保留之后的外部编辑。非守护更新发现冲突返回 `proxy.system_proxy_conflict`；开启代理守护表示明确持续覆盖这些代理键。部分失败会补偿，补偿失败保留 root 专用恢复记录；Server 启动按保存状态重试恢复，守护与保存串行，关闭后不会被旧守护重启。宿主应用失败不会发布新的 Mihomo 设置，保存失败则补偿宿主与 YAML。Linux 默认绕过 localhost、127.0.0.1、::1；自定义主机、域名和网段以逗号/分号分隔，具体匹配取决于消费者，不支持 Windows 通配符或 `<local>`。

当前证据为环境文件/GNOME/KDE 键的事务夹具、失败补偿、重启恢复、外部编辑保护、守护竞争及客户端能力门控验证；真实 Linux 桌面和终端联网仍待验收。

TUN 的启停属于安全事务，而不是某项设置或某个订阅配置的属性。因此任何一次配置重写——修改设置、切换订阅配置或是 TUN 事务本身——都必须显式写明 `tun.enable` 与 `route-exclude-address`：前两者从当前生效的 `active.yaml` 读回并原样保留，否则一次无关的设置保存就会静默拆除适配器；后者是管理路由的安全边界，若只保留 `enable: true` 而丢掉它，auto-route 会吞掉管理网络。

TUN 状态是**观测值而非记账值**：`MihomoEngine` 通过控制器读取运行时的 `tun.enable` 得到 `TunState`（并据此决定 `ProxyOperatingMode`），恢复标记只用于表达"事务是否完成"。已完成的事务标记若与引擎自述矛盾（引擎报告 TUN 未启用），说明运行时被事务之外的方式重配过，此时先走常规恢复流程确认管理路由，再丢弃该标记；无法观测时不得据此清除任何持久状态，未完成的标记则始终要求恢复。

Server 使用 `ResponseHeadersRead` 有界采样 Mihomo 的 NDJSON 接口：流量和内存各取第一条完整记录，日志最多观察 1 秒且单行不超过 64 KiB；安静日志流返回空列表，调用方取消仍传播。连接协议从 `metadata.network` 读取，端点包含端口，目标优先显示域名；Mihomo 的 `connections: null` 表示零连接。控制器读取失败时 `GET /api/v1.0/proxy/connections` 返回带问题码的 503，不能伪装成成功空列表。桌面连接页每 3 秒单独刷新，保持仍存在的选中项，显示连接数量、空状态或读取失败；离开页面停止定时器。
