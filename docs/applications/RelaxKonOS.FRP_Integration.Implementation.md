# RelaxKonOS FRP 集成——当前实现边界

本文记录根据 FRP 集成 Goal 落地的代码级 V1 边界；它不改变 Goal 文档中的架构或安全约束。

## 已实现的控制平面

- `Shared/RelaxKonOS.Protocol/Tunnels` 负责 JSON 协议和路由常量。Profile 读取返回已保存 Token；FRPS 读取与保存响应返回 Token 和 Dashboard 密码，编辑器直接显示并回填。生成 TOML 和受保护密钥载荷不公开。
- Server 将 FRP 服务端配置文件和隧道期望状态按 JWT 主体范围持久化至 SQLite，并使用乐观修订检查和唯一远端端口约束。运行时/进程状态保持主机本地，不属于 Workspace 偏好。
- Token 通过专用写入接口或 FRPS 配置更新保存，以 ASP.NET Core Data Protection 加密；授权读取直接返回配置值。FRPS 编辑读取继续保留审计，日志和审计不记录凭据正文。
- `TunnelsRead` 允许 Controller 和 Observer 会话读取安全状态；`TunnelsManage` 需要 Controller 会话。策略同时识别原始 JWT `role` 与框架映射的角色声明，且从不信任客户端 app id。配置文件、隧道和 Token 变更写入不含请求正文或 TOML 的脱敏审计记录。
- 外部运行时检测只接受规范绝对文件路径，检查存在性和可执行状态，并且只通过 `ProcessStartInfo.ArgumentList` 调用 `<固定路径> --version`。检测期间不会修改、启动、升级或终止外部可执行文件。
- 应用配置文件会按配置文件串行化工作，写入私有临时 TOML，调用 `<固定路径> verify -c <固定临时路径>`，然后替换托管配置并以参数列表启动 RelaxKonOS 拥有的 `frpc` 子进程。验证或启动失败会返回稳定问题代码并保留/恢复上一配置。停止操作使用已保存的进程对象及 PID/启动时间检查，绝不按名称查找或终止进程。

## 支持的配置范围

仅接受 `tcp`、`udp`、`http` 和 `https` 期望状态。生成器使用封闭架构：服务端主机/端口、Token 认证、TLS 启用、本地主机/端口、远端端口/域名以及每个代理的传输压缩/加密。它不会输出 `includes`、插件、环境替换、任意 TOML、任意命令参数、OIDC、STCP、XTCP、visitor 或 `frps` 设置。

Avalonia 隧道管理器是包含“概览、隧道、FRP 服务器、运行时”页面的单窗口工作区。配置文件和隧道编辑器在独立窗口打开；每个服务器行可打开独立的自动刷新日志窗口，因此可同时查看多个服务器日志。它会为每个请求使用已认证会话的绝对 Server URL（从不依赖未设置的 `HttpClient.BaseAddress`），支持配置文件/隧道期望状态 CRUD 和显式外部运行时探测，profile 编辑器使用空的替换 Token 字段，不读取已有 Token。运行时安装除了 Server 端确认检查外还需 UI 中明确确认。

## 运行时信任与发布操作

托管运行时变更使用统一 `POST /api/v1.0/installations/Frp/{Install|Upgrade|Repair|Uninstall}`，需要 Controller、宿主授权、显式确认和稳定幂等键。安装/升级/非回滚修复指定固定版本；回滚为 Repair + rollback。响应、活动查询、按 ID 恢复与取消遵循公共 Installations 契约。安装支持宿主下载、经 `/installations/Frp/file-reference` 暂存的服务器归档、经 `/installations/Frp/package` 上传的手机归档；请求只带 FileReferenceId，包引用只支持 Install。运行时安全状态读 `/tunnels/runtime`，下载信息读 `/tunnels/runtime/download?version=...`，指定外部文件探测为 POST `/tunnels/runtime/external/detect`。

任何安装来源都必须匹配宿主配置提供的当前 RID、HTTPS 官方 GitHub 发布 URL、固定 64 字符 SHA-256 与受支持归档格式。归档先进入私有临时文件，解压前必须与信任清单 SHA-256 匹配；没有 latest 或旧 runtime/managed 安装路由。

安装管线以有界流下载到私有临时文件，解压前验证 SHA-256，拒绝路径穿越、符号链接/设备条目、过大条目及意外归档内容，只解压 `frpc` / `frps`，检查 `frpc --version`，再以原子替换私有 `state.json` 指针激活新版本。旧版本保留在独立版本目录中；回滚会在切换指针前再次验证旧 `frpc`。下载、校验和、解压或健康检查失败都不能替换当前版本。

随附的 `appsettings.json` 固定了 FRP `v0.71.0` 的 Linux x64/arm64 与 Windows x64/arm64 资产及 GitHub 发布 SHA-256。主机没有匹配 RID 条目时返回 `tunnel.runtime_release_not_configured`，而不是下载未验证二进制；绝不可将发布版本选为“latest”。

Server 验证套件使用本地 `tar.gz` 夹具和可替换 HTTP 客户端，覆盖成功安装、当前/上一版本切换、回滚、错误校验和拒绝、意外归档内容拒绝及“期望状态 → `frpc verify` → 进程启停”、修改后尚未应用、重新应用、脱敏日志和 Token 生命周期。它不需要网络下载或本地安装的 FRP 二进制。

FRP 官方配置参考将 `frpc verify -c <config>` 作为此处使用的验证协议。官方发布页会公布逐项 SHA-256；公开托管安装器前，发布清单必须复制这些值。实际工作时必须重新验证上游兼容性和生命周期信息，不能从本文推断版本。

## 运维

私有生成文件位于 Server 内容根目录下的 `data/tunnels/frp/<profile-id>`；Unix 上目录权限收紧为 `0700`，TOML 和备份文件为 `0600`。运行时版本和状态指针同样是私有的。Windows 使用服务账户的数据目录，部署时必须以 ACL 只允许该账户访问。绝不修改 Defender。被隔离或缺失的运行时会报告为不可用；RelaxKonOS 认证和 LAN API 不依赖 FRP。运行时 stdout/stderr 会被读取并限制为每配置文件 200 行脱敏日志，专用读取端点绝不返回生成配置或凭据。只有 FRP 报告成功登录服务器后状态才变为 `Connected`；可识别的认证失败显示为断开，而不会伪造健康状态。


应用时记录 profile revision、排序后的隧道 ID/revision 和受保护 Token 版本的内存 SHA-256 指纹；它不公开、不持久化，不包含明文 Token。运行中修改任何关联期望状态后，列表投影为 `SavedNotApplied`，缺少已应用指纹时为 `Unknown`，禁用隧道不显示 `Connected`。重启后 Helper 的进程状态不能单独证明当前期望状态已应用。子进程日志/退出回调只更新仍属于当前 profile 的原进程实例，旧实例迟到事件不得覆盖替代实例。`Connected` 不证明每个代理注册或公网可达。

Profile/隧道 CRUD、Token、应用、停止为同步 API，没有 operation ID 或幂等键；丢失响应须核实当前事实，不能宣称自动安全重放。运行时安装的长任务和稳定键是不同契约。专项检查 `--frpc-state-only` 与 `--frpc-lifecycle-only` 使用本地夹具，真实 frps、公网与 Windows Helper 仍需对应环境验收。Android 交互与实现范围由 [Android FRP 文档](../../Client/RelaxKonOS.Client.Android/docs/features/Tunnels.md) 维护。


## 托管 frps 配置、版本与进程事实

frps 为宿主级资源，配置包含 bind 地址/端口、允许端口/范围、可选 HTTP/HTTPS vhost、强制 TLS、Token 和可选 Dashboard 地址/端口/账号/密码。配置保存要求 confirmed 和原 expectedRevision（首次 0），锁内 CAS 成功后推进保存 revision；冲突 409 并记录失败审计。配置文件直接要求当前 revision 字段，不解析旧格式。Token/dashboard 密码限制为有界单行值，由 Data Protection 保护；空白替换保留旧秘密。PUT 和普通 GET 返回保存的 Token 和 Dashboard 密码，编辑器直接显示；Controller 编辑 GET 继续记录读取审计。

DTO 同时返回保存 revision 和活跃进程的 appliedRevision；保存不重启，旧进程保持旧 appliedRevision。已运行的 Start 若版本不匹配不会隐式应用，要求显式 Stop/Start；成功 Stop 返回 Disconnected。各监听使用其实际绑定 IP 探测占用，包括独立 Dashboard 地址；失败返回稳定问题码。进程生命周期由系统服务管理器持有，只有确认服务停止后才清理运行时和管理记录；外部 FRP 程序仍以普通 Server 身份运行并随 Server 停止。

Windows FRPS/FRPC 使用独立 SCM 服务，Linux 使用独立 systemd unit，每个 FRPC profile 对应一个实例。Helper 仅执行受限服务管理，Server/Helper 退出不停止独立实例。服务持久化保存 appliedIdentity：FRPC 使用配置指纹，FRPS 使用保存 revision；Server 重启后按服务实际状态读取应用证明，不再凭内存推断 Unknown。缺失/外部修改的服务配置拒绝覆盖，Running 仍不证明公网连通。详见 [独立服务进度](../services/RelaxKonOS.IndependentComponentServices.Progress.md)。

`--frps-only` 使用隔离 SQLite/Data Protection 和独立服务传输替身，覆盖保存/应用版本、Server 退出后实例保留、重新创建管理器后的状态/版本恢复、显式停止、秘密审计及占用拒绝。`--independent-component-services-only` 检查实例命名隔离、路径边界和结构化应用证明。真实 SCM/systemd、FRP 协议与重启验收仍需要隔离主机。
