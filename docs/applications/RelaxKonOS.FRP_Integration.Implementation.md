# RelaxKonOS FRP 集成——当前实现边界

本文记录根据 FRP 集成 Goal 落地的代码级 V1 边界；它不改变 Goal 文档中的架构或安全约束。

## 已实现的控制平面

- `Shared/RelaxKonOS.Protocol/Tunnels` 负责 JSON 协议和路由常量。配置文件列表及普通 FRPS 状态响应仅公开 `tokenConfigured`；经 Controller 授权的配置文件和 FRPS 编辑端点会额外返回 Token，供编辑器显示当前值。生成的 TOML 和受保护密钥载荷绝不公开。
- Server 将 FRP 服务端配置文件和隧道期望状态按 JWT 主体范围持久化至 SQLite，并使用乐观修订检查和唯一远端端口约束。运行时/进程状态保持主机本地，不属于 Workspace 偏好。
- Token 通过 `PUT /api/v1.0/tunnels/profiles/{id}/secret` 或经 Controller 授权的 FRPS 配置更新进入，以 ASP.NET Core Data Protection 保护；仅会通过 `GET /api/v1.0/tunnels/profiles/{id}` 或 `GET /api/v1.0/tunnels/frps/editor` 返回给经 Controller 授权的编辑器。列表、普通 FRPS 状态读取、导出、配置下载、生成 TOML 和受保护密钥载荷永不公开密钥；每次成功读取 Token 都会审计。
- `TunnelsRead` 允许 Controller 和 Observer 会话读取安全状态；`TunnelsManage` 需要 Controller 会话。策略同时识别原始 JWT `role` 与框架映射的角色声明，且从不信任客户端 app id。配置文件、隧道和 Token 变更写入不含请求正文或 TOML 的脱敏审计记录。
- 外部运行时检测只接受规范绝对文件路径，检查存在性和可执行状态，并且只通过 `ProcessStartInfo.ArgumentList` 调用 `<固定路径> --version`。检测期间不会修改、启动、升级或终止外部可执行文件。
- 应用配置文件会按配置文件串行化工作，写入私有临时 TOML，调用 `<固定路径> verify -c <固定临时路径>`，然后替换托管配置并以参数列表启动 RelaxKonOS 拥有的 `frpc` 子进程。验证或启动失败会返回稳定问题代码并保留/恢复上一配置。停止操作使用已保存的进程对象及 PID/启动时间检查，绝不按名称查找或终止进程。

## 支持的配置范围

仅接受 `tcp`、`udp`、`http` 和 `https` 期望状态。生成器使用封闭架构：服务端主机/端口、Token 认证、TLS 启用、本地主机/端口、远端端口/域名以及每个代理的传输压缩/加密。它不会输出 `includes`、插件、环境替换、任意 TOML、任意命令参数、OIDC、STCP、XTCP、visitor 或 `frps` 设置。

Avalonia 隧道管理器是包含“概览、隧道、FRP 服务器、运行时”页面的单窗口工作区。配置文件和隧道编辑器在独立窗口打开；每个服务器行可打开独立的自动刷新日志窗口，因此可同时查看多个服务器日志。它会为每个请求使用已认证会话的绝对 Server URL（从不依赖未设置的 `HttpClient.BaseAddress`），支持配置文件/隧道期望状态 CRUD 和显式外部运行时探测，并在 Controller 打开配置文件编辑器时显示已有 Token。运行时安装除了 Server 端确认检查外还需 UI 中明确确认。

## 运行时信任与发布操作

托管运行时安装是仅 Controller 可执行的显式操作（`POST /api/v1.0/tunnels/runtime/managed/install`），需要确认和指定的**固定**版本。UI 还公开官方 FRP 发布页，并可选择已存在于 RelaxKonOS Server 上的归档文件（`POST /api/v1.0/tunnels/runtime/managed/install/from-file`）。Server 仅在主机管理员配置提供当前 RID、HTTPS 官方 GitHub 发布 URL、固定 64 字符 SHA-256 及受支持归档格式时接受任何来源。Server 选择的归档会先复制到私有临时文件，解压前必须与配置的 SHA-256 匹配；不存在“latest”路由。

安装管线以有界流下载到私有临时文件，解压前验证 SHA-256，拒绝路径穿越、符号链接/设备条目、过大条目及意外归档内容，只解压 `frpc` / `frps`，检查 `frpc --version`，再以原子替换私有 `state.json` 指针激活新版本。旧版本保留在独立版本目录中；回滚会在切换指针前再次验证旧 `frpc`。下载、校验和、解压或健康检查失败都不能替换当前版本。

随附的 `appsettings.json` 固定了 FRP `v0.71.0` 的 Linux x64/arm64 与 Windows x64/arm64 资产及 GitHub 发布 SHA-256。主机没有匹配 RID 条目时返回 `tunnel.runtime_release_not_configured`，而不是下载未验证二进制；绝不可将发布版本选为“latest”。

Server 验证套件使用本地 `tar.gz` 夹具和可替换 HTTP 客户端，覆盖成功安装、当前/上一版本切换、回滚、错误校验和拒绝、意外归档内容拒绝及“期望状态 → `frpc verify` → 进程启停”。它不需要网络下载或本地安装的 FRP 二进制。

FRP 官方配置参考将 `frpc verify -c <config>` 作为此处使用的验证协议。官方发布页会公布逐项 SHA-256；公开托管安装器前，发布清单必须复制这些值。实际工作时必须重新验证上游兼容性和生命周期信息，不能从本文推断版本。

## 运维

私有生成文件位于 Server 内容根目录下的 `data/tunnels/frp/<profile-id>`；Unix 上目录权限收紧为 `0700`，TOML 和备份文件为 `0600`。运行时版本和状态指针同样是私有的。Windows 使用服务账户的数据目录，部署时必须以 ACL 只允许该账户访问。绝不修改 Defender。被隔离或缺失的运行时会报告为不可用；RelaxKonOS 认证和 LAN API 不依赖 FRP。运行时 stdout/stderr 会被读取并限制为每配置文件 200 行脱敏日志，专用读取端点绝不返回生成配置或凭据。只有 FRP 报告成功登录服务器后状态才变为 `Connected`；可识别的认证失败显示为断开，而不会伪造健康状态。
