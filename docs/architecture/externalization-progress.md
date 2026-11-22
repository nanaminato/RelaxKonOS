# 基础应用外置化与联合部署进度

## 基线与验收口径

- 本地起点与 GitHub master 同为 `76415cee377d46d6c5a8362c561b07e9c455f6a0`。在 `codex/externalization` 实施。
- 下表的“内置”指桌面 Client 内的编译期注册。Shell 项目仅引用 Core 和 Avalonia，桌面宿主的 Bootstrapper 和 BuiltInApplicationRegistry 才是主要拆分点。
- “服务端归属”描述应用当前消费的 API。只有已存在的独立模块标为包候选，其他归属须按实际提供者和主机契约确认。宿主运行时由组件中心管理。
- 验收列指本轮完整验收。既有模块测试和历史设备证据见 [模块边界进度](RelaxKonOS.ServerModuleBoundaries.Progress.md)。

## 桌面应用矩阵

内置行的目录以 `Client/RelaxKonOS.Client/Apps/` 为起点。独立行的项目位于 `Applications/`。应用 ID 均具有 `relaxkonos.` 前缀。

| ID 后缀 | 目录或项目 | 独立项目 / .roapp 安装 | 桌面宿主内置 | 服务端 API / 模块归属 | 目标层 | 本轮验收 |
|---|---|---|---|---|---|---|
| appinstaller | AppInstaller | 否 / 否 | 是 | 文件读取与组件源；安装和恢复入口需保留 | 核心入口 | 三档内置恢复入口、隔离 HTTPS 商店及归档恢复已验 |
| welcome | RelaxKonOS.App.Welcome | 是 / 是 | 否 | 使用 SDK 激活记事本或安装器 | 基础 | Windows 已验 |
| settings | Settings | 否 / 否 | 是 | 工作区设置与宿主设置 | 基础 | 待验 |
| explorer | Explorer | 否 / 否 | 是 | 文件 API；调用存储提供者的聚合入口 | 基础 | 待验 |
| terminal | RelaxKonOS.App.Terminal | 是 / 是 | 否 | SDK 终端会话和宿主原生渲染 | 基础 | Windows 与 WSL 官方包已验，服务端恢复待验 |
| server-center | ServerCenter | 否 / 否 | 是 | 服务端连接、安装与管理 | 基础 | 待验 |
| component-center | ComponentCenter | 否 / 否 | 是 | 组件、可信来源、模块及任务 API | 基础 | 待验 |
| taskmanager | RelaxKonOS.App.TaskManager | 是 / 是 | 否 | SDK 性能与进程 API | 基础 | Release 包验收；真实服务器待验 |
| notepad | RelaxKonOS.App.Notepad | 是 / 是 | 否 | 文件能力通过 SDK | 基础 | Windows 包生命周期、缺失资源修复及用户文档保留已验 |
| browser | Browser 独立应用项目 | 是 / 是 | 否 | 本地 WebView 与 SDK 浏览器数据服务 | 基础 | Windows 发行包已验，原生 WebView 待验 |
| imageviewer | RelaxKonOS.App.ImageViewer | 是 / 是 | 否 | 文件能力通过 SDK | 基础 | Windows 包生命周期及 Linux Standard 原生首次预装已验 |
| registry | RelaxKonOS.App.Registry | 是 / 是 | 否 | SDK Registry 与设备外观偏好 | 基础 | Release 包验收；真实服务器待验 |
| event-alerts | RelaxKonOS.App.EventAlerts | 是 / 是 | 否 | SDK 告警读取能力，复用可观测事件 API | 基础 | Windows 包生命周期已验，真实服务端告警读取待验 |
| codeeditor | RelaxKonOS.App.CodeEditor | 是 / 是 | 否 | SDK 文件 API 和 SSH 工作区 | 可选 | Windows 包生命周期已验，真实 SSH 工作区待验 |
| docker | RelaxKonOS.App.Docker | 是 / 是 | 否 | Docker 模块；组件中心管理 Engine | 官方可选包 | 已验证 Release 包生命周期及隔离宿主联合部署；真实 Engine 待验 |
| processguardian | RelaxKonOS.App.ProcessGuardian | 是 / 是 | 否 | Guardian API 与独立 Agent | 官方可选包 | Windows Release 包生命周期已验，真实 Agent 待验 |
| firewall | RelaxKonOS.App.Firewall | 是 / 是 | 否 | Firewall 模块与宿主权限边界 | 官方可选包 | Windows Release 包生命周期和隔离宿主联合部署已验，真实防火墙操作待验 |
| proxy | RelaxKonOS.App.ProxyManager | 是 / 是 | 否 | Proxy 模块与 Proxy capability；组件中心管理 mihomo | 官方可选包 | Windows Release 包生命周期已验，真实 mihomo 待验 |
| proxy-server | RelaxKonOS.App.ProxyServer | 是 / 是 | 否 | Proxy 模块与 ProxyServer capability；组件中心管理 sing-box | 官方可选包 | Windows Release 包生命周期及启动守卫已验，真实 sing-box 待验 |
| squid | RelaxKonOS.App.Squid | 是 / 是 | 否 | Proxy 模块与 GatewayProxy capability；组件中心管理 Squid | 官方可选包 | Windows Release 包生命周期已验，真实 Squid 待验 |
| file-services | RelaxKonOS.App.FileServices | 是 / 是 | 否 | SDK 与主机 SMB 能力 | 官方可选包 | Windows 隔离包生命周期已验，真实 SMB 服务待验 |
| port-forwarding | RelaxKonOS.App.PortForwarding | 是 / 是 | 否 | 宿主 SSH 连接与环回转发 | 官方可选包 | Windows Release 生命周期已验，真实 SSH 待验 |
| certificates | RelaxKonOS.App.Certificates | 是 / 是 | 否 | SDK 证书接口与 Certificates 模块 | 官方可选包 | Windows 包生命周期及隔离宿主模块复用已验，真实证书服务待验 |
| webservers | RelaxKonOS.App.WebServers | 是 / 是 | 否 | WebServer 模块与宿主权限边界 | 官方可选包 | Windows、Linux 隔离模块启用、重启恢复和证书依赖保护已验，Nginx 业务联调待验 |
| git | RelaxKonOS.App.Git | 是 / 是 | 否 | SDK Git、远程文件与 Git 模块 | 官方可选包 | Windows 包生命周期及隔离联合部署已验，真实设备业务联调待验 |
| tunnels | RelaxKonOS.App.Tunnels | 是 / 是 | 否 | SDK Tunnels；组件中心管理 FRP | 官方可选包 | Windows Release 包生命周期已验，真实 FRP 待验 |
| application-deployments | RelaxKonOS.App.ApplicationDeployments | 是 / 是 | 否 | ApplicationDeployments capability | 官方可选包 | Windows Release 包生命周期已验，真实部署待验 |
| database | RelaxKonOS.App.Databases | 是 / 是 | 否 | 数据库 SDK、Database 模块与宿主安装任务 | 官方可选包 | Windows 包生命周期、Ubuntu 签名独立包联合安装与重启续跑及四条真实 Helper 只读查询已验 |
| tailscale | Tailscale | 是 / 是 | 否 | Tailscale 模块、Tailscale SDK | 可选 | Windows Release 包生命周期及授权验证通过，实机待验 |
| ssh-files | RelaxKonOS.App.SshFiles | 是 / 是 | 否 | SDK SSH/SFTP 会话，由宿主验证主机密钥 | 基础 | Windows 官方包及环回 SFTP 已验，远端桌面待验 |

基线共有 29 个登记入口，2 个可独立打包安装，27 个由桌面宿主编译和登记。当前桌面发行清单包含 25 个官方 `.roapp`，宿主仍注册应用安装器、设置、Explorer、服务器中心和组件中心。Standard 离线安装 Browser、Event & Alert Center、Image Viewer、Notepad、Registry、SSH Files、Task Manager、Terminal 和 Welcome。Minimal 跳过应用包预装并保留应用安装器入口。Developer 额外离线预装 Code Editor，其余 15 个应用包列为交互式安装项。

首次可用 `--installation-profile=minimal|standard|developer` 与可重复的 `--exclude-bundled-app=<id>` 选择和排除。选择保存在本机 VSD。启动扫描后通过原安装器离线预装，逐项记录首次处理的版本。重启和客户端升级保留已记录应用的卸载决定。带有必需依赖的包须经交互部署。随包提供的 Task Manager 和 Terminal 在启动时检查所需服务端能力。官方商店可使用与已安装版本相同的可信归档进行修复，继续使用原安装器的摘要、路径和版本检查。

`python Tests/Client/verify-desktop-distribution.py` 检查官方目录、桌面发布项和包清单的应用 ID 是否一致，核对配置集版本下限，并拒绝把声明必需依赖的包列为离线预装。
完整 Windows Release 发布通过。`verify-desktop-distribution.py --publish-dir` 核对了实际生成的 25 个归档、摘要、入口程序集和配置文件。窗口预览测试的 `--desktop-distribution-only` 在独立 VSD 上使用真实发布配置验证 Minimal、默认 Standard 和 Developer 分别登记 0、9、10 个包；Developer 的 15 个交互项未被自动安装。三个配置均通过生产扫描器登记内置应用安装器及其 `.roapp` 文件入口。发行输出中的 SSH Files 归档另通过官方包生命周期专项。

Windows 图形启动冒烟测试使用发行版 `RelaxKonOS.exe --installation-profile=minimal` 打开了可响应窗口，窗口关闭后进程以状态 0 退出。可在隔离的 Windows 应用数据沙箱内运行 `Tests/Client/verify-windows-desktop-startup.ps1`。窗口测试未能从调用进程读取子进程的 VSD 配置文件，预装与恢复入口由独立无头宿主验证。

## 复用边界

- Runtime 继续由 ApplicationManager 负责应用注册、激活和窗口策略。启动依赖观察应接入现有 IApplicationLaunchGuard。参见 [应用激活](RelaxKonOS.ApplicationActivation.md)。
- 官方桌面商店沿用 `OfficialDesktopPackages`、`AppPackageInstallerService` 和 `DeveloperPackageManager` 的来源、哈希、安装与注册边界。Direct Deploy 继续复用软件包管理器。参见 [包商店](../platform/RelaxKonOS.PackageStore.Design.md) 和 [开发 CLI](../../Tools/RelaxKonOS.DevCli/README.md)。
- 模块侧复用可信目录、包计划、应用操作回执及 ModuleHost 健康查询。主机准入和宿主级授权由服务端判定。参见 [组件中心](../platform/RelaxKonOS.ComponentCenter.Design.md) 和 [模块包格式](RelaxKonOS.ServerModulePackage.Format.md)。

## 实施与恢复

1. 代码基线和桌面登记盘点已记录。
2. `Shared/RelaxKonOS.Deployment` 已加入解决方案。解析器支持传递依赖、共享依赖、区间、能力与契约版本、回溯和循环检查。协调器有目标服务器、状态与目录版本、计划有效期和持久检查点。通用编排和文件日志已接入桌面安装器。
3. 桌面 .roapp 清单可读取和验证 dependencies.required 与 dependencies.optional。旧包可省略整个字段。应用安装器的联合部署路径在包安装提交前验证必需依赖。其余入口继续拒绝带必需依赖的包，直至接入同一协调器。模块目录适配器已复用现有模块管理客户端读取分页可信目录、安装记录和有效状态，并绑定服务器身份、目录版本和模块状态摘要。组件中心模块商店使用精确来源和发布 ID 创建签名包计划，并核对上传与计划的摘要、版本和发布者。桌面应用商店与共用安装器已接入协调器，默认勾选可选模块，展示已满足的依赖、版本变化及权限声明。用户确认后按签名计划执行，验证服务端依赖并通过现有应用安装器验证程序集与注册。模块执行适配器已实现按联合计划保存服务端计划 ID、到期时间和权限声明，按 ID 对账回执，并验证实际启用版本、能力和健康。首次安装在包回执确认后复用模块管理的 Enable 计划，保留已有的期望启用集合。需要重启时暂缓客户端安装。启动守卫复用现有应用就绪检查，按当前登录服务器的有效模块、版本和能力检查包依赖，并提供组件中心或应用安装器入口。客户端卸载锁内会阻止移除仍被另一已安装应用声明为必需的 .roapp。安装器可在重开后找回原本地路径，或从当前可信目录、原服务器文件连接重新取得同一摘要的应用包，再请求确认。应用版本目录中记录联合计划回执，提交后中断时可核对现有安装。已确认未派发的过期模块计划需要重新审阅，协调器保留已完成步骤。组件中心的模块停用和卸载操作展示本桌面已安装应用的依赖，并在提交前重新检查。服务端原有模块互相依赖保护继续生效。不同设备和其他管理入口共享应用依赖声明仍待服务端持久化。服务端目录目前仅提供 capability 名称，显式要求契约版本时仍需可靠的版本证据。
4. 启动守卫、本机共享依赖保护、基础包预装与同版本修复已有验收。Windows 与 Linux 通过三档配置安装及切换，Linux 原生入口逐档核对首次预装。Ubuntu 通过桌面发起的 Databases 独立模块 HTTPS 联合安装及重启恢复。后续仍包括其余业务模块迁移、跨设备依赖持久化和矩阵中待验的真实业务链路。
5. 恢复时从本分支运行 `dotnet build Shared/RelaxKonOS.Deployment -m:1`，查看当前差异与最新提交，再从第 3 项继续。部署存续期间须先观察现有服务端回执，再决定是否重试，保留共享依赖。

## 测试证据

- Windows 完整桌面 Release 发布通过。核对 `OfficialApps` 的全部 12 个归档及其 SHA-256，确认 Developer 保留 Web Server Manager 的交互安装条目。使用完整发行产出的 Web Server Manager 归档再次通过官方来源、安装、启动、更新、失败保留、重启发现与卸载。

- Linux x64 的 .NET 10 无头容器使用实际 Web Server Manager 归档通过来源校验、安装、启动、更新、失败保留、重启发现和卸载回归。网站专项也通过，覆盖三语资源、站点草稿、授权范围和连接切换。真实 Nginx 业务联调和 Linux 图形会话仍待验收。

- Windows Release 和 Linux x64 隔离联合部署验收使用实际 Web Server Manager 归档。安装计划依次复用已启用的 Certificates 与 WebServer 模块，再安装并注册应用。测试确认无新增模块包操作。Nginx 业务联调和 Linux 桌面包生命周期仍待执行。

- Web Server Manager 已移除桌面内置登记，作为官方归档进入桌面发行和 Developer 交互配置。清单要求 `relaxkonos.webserver` 模块及 `server.web-server` 能力。Windows Release 使用实际 DevCli 归档验证官方来源、拒绝绕过联合部署、交互审阅、安装后启动、更新、失败保留、重启发现和卸载。客户端生命周期测试使用模块验证替身，实际模块联动与完整桌面发布待验收。

- Web Server Manager 已具备独立 SDK 入口、三语包清单和文件选择权限。入口复用宿主文件选择、组件设置页、窗口确认与 Web Server 能力。Windows Release 网站专项使用 SDK 上下文验证打开工作区并读取当前 Nginx。DevCli 生成包并核对清单、入口程序集和图标。内置登记、官方发行接线及实际归档安装验收仍待完成。

- Web Server Manager 的站点保存向宿主提权和 HTTP 客户端传递同一取消令牌。更换连接、实例或站点以及结束编辑时取消待处理保存，派发前和收到回执后重新检查。Windows Release 网站专项验证身份与实例替换会取消等待中的授权，并保留原有迟到回执和草稿保护。

- Web Server Manager 的实例页、主工作区和图标已迁入应用程序集。设置页仍由宿主提供并沿用组件中心的状态刷新。Windows Release 的网站专项及融合标题栏回归通过，覆盖窄窗口和三种桌面样式。独立启动入口及发行包仍待完成。

- Web Server Manager 的站点列表和站点编辑对话框已迁入应用程序集，并直接加载应用文案。Windows Release 网站专项验证两种宿主平台映射的列表表头、嵌套路由草稿确认及提交保护。主工作区、实例页、独立入口和发行包仍待迁移。

- Web Server Manager 和组件中心共享的主视图模型已迁入 `RelaxKonOS.App.WebServers`。模型通过应用语言资源解析状态文案，宿主提供组件设置页和特权错误文案。Windows Release 网站专项覆盖现有站点编辑、授权、连接失效和组件中心卸载派发。应用窗口及发行包仍待迁移。

- Web Server Manager 的视图模型通过应用项目定义的实例授权接口调用宿主提权代理。Windows Release 网站专项验证提权后重新核对站点目标，以及连接变化时阻止保存。主视图模型仍由桌面宿主编译。

- Web Server Manager 的共享视图模型已移除对安装任务视图模型的直接依赖。组件中心继续连接原 Nginx 卸载台账。Windows Release 网站专项验证确认期间更换实例时不派发，以及同一实例确认后调用组件中心。实机卸载和应用包迁移仍待完成。

- Web Server SDK 的实例集成、站点写入、重载与运行控制通过原宿主提权代理，授权目标沿用候选或实例 ID。代理执行前重新检查授权和连接。Windows Release 网站专项验证集成提权范围及等待授权期间切换服务器时阻止写入。独立入口仍待迁移。

- Web Server Manager 的视图模型通过 SDK 连接接口接收失效通知，由宿主继续核对原服务器、登录和用户身份。Windows Release 网站专项覆盖切换连接后清理旧站点状态，以及阻止尚在授权阶段的提交。视图模型仍在桌面宿主。

- Web Server Manager 的站点域名、端口、代理路由和 HTTPS 来源草稿类型已迁入应用程序集，现有桌面编辑器直接绑定这些类型。Windows Release 网站专项验证草稿保留、提交快照和连接切换保护。连接适配、主视图模型和发行包仍待迁移。

- Web Server Manager 的中、英、日资源原样迁入 `RelaxKonOS.App.WebServers` 项目。桌面宿主从应用程序集载入同一资源供 Web Server Manager 和组件中心使用。Windows Release 网站专项验证三语解析、编辑、读取恢复和当前 Nginx 选择。视图模型、独立入口及发行包仍待迁移。

- Web Server Manager 的 SDK 宿主适配器按应用检查读取和管理权限，并绑定创建窗口时的服务器连接。当前 Nginx 选择继续由组件中心处理。Windows Release 网站专项覆盖读取授权、拒绝只读应用集成实例、已有管理权限时保留组件中心选择权，以及拦截旧连接。独立应用入口尚未接入。

- Web Server Manager 和组件中心共用 SDK 中的 `IServerWebServers` 契约与结构化 HTTP 错误。Windows Release 网站专项通过，覆盖网站编辑、读取恢复、作用域及当前 Nginx 选择。SDK 权限适配、应用界面和发行包仍待迁移。

- Windows Release 的 `--certificate-binding-only` 将生产桌面证书 HTTP 客户端接到隔离宿主的实际证书端点和存储。匿名读取返回 401，使用测试 Bearer 凭据成功读取预置证书及服务端生成的自签名证书。已部署设备上的完整桌面到服务端验收仍待执行。

- Windows Release 证书专项通过桌面 SDK 和生产证书 HTTP 客户端接入测试传输层，验证证书列表使用正确的 GET 路由与 Bearer 凭据，并保留集合请求返回的 HTTP 404 和结构化错误码。真实证书服务联调仍待完成。

- Linux x64 无头桌面验收通过。一次性 .NET 10 容器补齐 `libfontconfig1` 和 DejaVu 字体后，使用完整桌面发行生成的证书归档通过官方来源、安装、启动、更新、失败保留、重启发现和卸载回归。证书权限、工作区入口、草稿和取消回归也通过。实际 Linux 图形会话与真实证书服务仍待验收。

- Linux x64 的隔离 .NET 10.0.14 容器使用 Windows Release 的可移植测试程序集与完整桌面发布中的证书归档，通过双归档联合部署验收。证书归档复用已启用的同名服务端模块，完成桌面注册且未新增模块操作。桌面预览在该镜像因缺少 `libfontconfig.so.1` 无法启动，Linux 图形与包生命周期测试仍待执行。

- Windows 桌面 Release 完整发布通过。核对 `OfficialApps` 下全部 11 个归档的 SHA-256 及 Developer 配置中的证书管理器交互条目。使用发布目录中的证书管理器归档再次通过官方来源、安装、启动、更新、失败保留、重启发现和卸载回归。

- Windows Release 双归档联合部署验收使用实际 Certificate Manager 官方归档。协调器通过隔离服务端的模块管理 HTTP 接口确认已启用的同名 Certificates 模块满足依赖，未创建模块安装计划或增加包操作回执，客户端完成安装与注册。隔离宿主未挂载证书业务 HTTP 端点，真实证书服务联调仍待执行。

- 证书管理器已移除内置登记，桌面发行将 `relaxkonos.certificates.roapp` 纳入官方来源，Developer 配置将它列为交互安装项。清单要求 Certificates 模块及其能力。Windows Release 使用 DevCli 生成的实际归档验证可信来源、直接安装拒绝、交互审阅、安装后启动、更新、失败保留、重启发现和卸载。客户端生命周期测试使用模块验证替身，真实服务端模块联动和完整桌面发行发布尚待验收。

- 证书管理器已具备 SDK 启动入口、三语包元数据和权限声明。DevCli 在 Windows 生成 `relaxkonos.certificates.roapp`，归档包含入口程序集和图标。Windows Release 证书专项使用桌面 SDK 上下文和测试证书服务验证独立入口打开主工作区并读取证书列表。桌面内置登记尚在，官方来源、发行配置和实际归档安装仍待完成。

- 证书主工作区、登录提示和图标已迁入应用程序集。Windows Release 的融合标题栏回归覆盖证书工作区的窄窗口与三套窗口样式，证书布局专项确认三语登录提示和六项布局。当前仍由内置入口启动，独立包入口和发行集成待完成。

- 证书申请、自签名和续期历史视图已迁入应用程序集。宿主通过现有草稿确认策略接入两个编辑器。Windows Release 证书专项验证了草稿确认、运行中的关闭保护、停止按钮文案和服务端取消回执。主工作区、独立启动入口和发行包仍待迁移。

- 证书列表及证书枚举文案转换已迁入应用程序集。Windows Release 的六项证书布局回归覆盖三种语言与两种主题，验证表头、状态单元格和续期历史入口。主工作区、对话框及发行包仍待迁移。

- 证书概览和续期运行记录视图已迁入应用程序集，由现有桌面工作区加载。两页直接使用应用语言资源。Windows Release 证书布局专项在三种语言和两种主题通过六项检查，覆盖概览标签和续期表格列标题。证书列表、编辑对话框、启动入口和发行包仍待迁移。

- 证书管理器和 HTTPS 设置共用的视图模型已迁入 `RelaxKonOS.App.Certificates`，通过 SDK 获取证书接口、应用权限和当前语言。Windows Release 验证同一实例的状态文案随中、英、日切换，证书编辑及取消回归通过。完整布局套件在安装器控件匹配处中断，证书专项通过三语和两套主题共六项。应用视图和独立发行包仍待迁移。

- 证书管理器中、英、日文案原样迁入 `RelaxKonOS.App.Certificates` 项目，桌面宿主从应用程序集读取同一份资源，供证书管理器和 HTTPS 设置继续使用。Windows Release 回归验证三种语言分别解析，并通过证书权限和编辑检查。视图模型、界面和独立发行包仍待迁移。

- 证书管理器视图模型已移除未使用的宿主会话依赖，证书 API 的结构化错误由 SDK 提供。Windows Release 的证书编辑和权限回归通过，应用布局测试项目编译通过。视图模型仍依赖宿主的动态语言资源。

- 证书管理器的服务器契约已迁入 SDK，现有桌面调用方共用 `IServerCertificates`。外置应用通过宿主适配器访问证书元数据和操作，宿主检查读取、管理授权和原服务器连接。Windows Release 的 `--certificates-only` 回归验证授权、切换服务器后丢弃旧响应，以及原有证书编辑和取消行为。证书管理器的界面、独立入口和发行包仍待迁移。

- 桌面联合部署在处理需要变更的服务端模块前重新核验计划中先于它完成的依赖步骤。Windows Release 隔离联合部署回归通过。多模块恢复时前一步变为不健康的场景尚无专项验收。

- 联合部署在读取客户端安装回执前核对计划绑定的应用 ID、版本及归档摘要，并确认回执所指版本仍已注册。Windows Release 隔离验收验证已完成计划拒绝不同的候选摘要，并允许以原候选重放。

- 协调器在处理取消前保存已完成的步骤。Windows Release 回归在首个步骤成功返回时触发取消，验证恢复从第二步开始，最终校验只运行一次。

- 部署日志在读取时核对文件名中的计划 ID 与记录内容。Windows Release 协调器回归验证改名后的有效检查点会被拒绝，恢复原文件名后同一计划仍可继续完成。

- Windows Release 回归确认单条模块回执接口对匿名请求返回 401，对无模块管理权限的已认证用户返回 403。联合部署验收替换回执的发布 ID 后，客户端在模块启用和桌面安装之前拒绝该回执。移除测试替换后，同一计划恢复完成。

- 联合部署按服务端操作 ID 读取单条模块安装回执，即使历史超过原客户端的 128 条限制也可查询。Windows Release 的模块包 HTTP 测试验证单条回执与历史一致、未知 ID 返回 404。隔离联合部署回归使用实际 Welcome 归档与签名模块包，验证丢失响应和重启后的恢复。

- Browser 的书签、历史记录和工作区设置已通过 SDK 接入，仅官方 Browser 应用上下文提供此能力。请求绑定启动时的服务器会话。
- Browser 的窗口和原生 WebView 已由独立 SDK 入口承载。Standard 与 Developer 使用同一官方 .roapp 离线预装。Windows Release 使用 DevCli 生成的实际归档验证来源校验、预装、未登录启动窗口跟随中英日语言、关闭后解除语言订阅、更新、失败保留版本、重启发现与卸载。已登录的原生 WebView 实机验收待完成。
- 桌面发行项目的 `PublishOfficialDesktopApps` 目标在 Windows 上生成九份归档和校验文件。逐包复核 SHA-256 及配置集引用均通过。Browser 的签入无头回归通过，测试数据经 SDK 进入已安装包的工作区。窗口关闭时停止 UI 心跳和后台诊断计时器。

- Registry SDK 分开检查读写权限，用户与实例子客户端沿用同一会话和授权。宿主提权在授权重试和令牌刷新时核对原会话。
- Registry 桌面工作区和三个编辑对话框已使用 SDK 的注册表和窗口接口。在刷新、编辑及删除时按需请求应用权限，确认后的回复仍使用原有所有者、实例和版本核对。独立项目和应用包仍待迁移。
- Registry 主模型使用 SDK 提供的外观偏好接口读取设备外观并保存侧栏宽度。该接口绑定到当前连接和设备，宿主继续维护偏好同步。
- Registry 值编辑器从应用上下文取得外观偏好，并通过 SDK 异常保留服务端的访问拒绝与版本冲突信息。
- Registry 的键树、延迟 JSON 树、文档投影和中英日文案已迁入独立项目。嵌套编辑仍提交原文档的所有者范围、路径和版本。
- Registry 主模型和值、新建键、重命名对话框模型已迁入独立项目，直接从应用资源解析文案。
- Registry 主视图、编辑对话框、图标和包启动入口已迁入独立项目，DevCli 已生成本地 Release `.roapp`。官方包随 Standard 和 Developer 档位离线预装，Minimal 跳过。桌面宿主已移除内置注册。

- Task Manager 所需的性能静态信息、实时快照与短期历史已接入只读监控 SDK。读取校验应用权限和捕获的服务器会话，历史采样窗口限定为 1–60 秒。
- Task Manager 性能页通过 SDK 读取 HTTP 快照、历史并订阅 SignalR。两条路径使用同一指标读取授权，断线重连后回补历史。关闭窗口或切换会话会取消性能读取并释放订阅。
- Task Manager 进程列表、图标和结束进程均通过 SDK 调用，使用进程管理授权。管理员模式仍由原宿主授权代理通过只读探测进入。结束进程沿用 PID 和启动时间核对，传输失败不重放。独立界面和应用包仍待迁移。
- Task Manager 的实时性能图、进程树行模型、窗口图标和中英日文案已迁入独立应用项目。当前工作区仍由宿主打开并引用这些资源。
- Task Manager 主模型通过 SDK 检查及监听启动时的服务器会话。会话更换时停止订阅和轮询，清空旧数据。性能与进程 API 复用同一会话边界。
- Task Manager 主模型、性能指标模型和字节格式化器已迁入独立项目。主模型直接读取应用内嵌文案，不再依赖宿主本地化服务。
- Task Manager 主视图、导航样式、表头和界面转换器已迁入独立项目。窗口外壳仍使用原有标题栏、快捷键和进程表交互。
- Task Manager 独立启动入口使用 SDK 创建窗口并监听应用生命周期。已移除桌面内置注册。官方包随 Standard 和 Developer 档位离线预装，Minimal 跳过。启动时继续检查服务端指标和进程能力。

- Docker Manager 的状态、资源详情、Compose 回执、引擎控制和账户镜像源使用 SDK，沿用现有 HTTP 路由。读取要求读授权，变更要求管理授权。SDK 在操作前后检查服务器会话，停止或重启引擎还要求用户确认。
- Docker Manager 主工作区模型位于 `Applications/RelaxKonOS.App.Docker`。组件依赖页由宿主 SDK 提供。
- Docker 账户镜像源页面、模型和三种语言资源已迁入应用项目。页面通过 SDK 读取和保存当前账户的镜像源选择。
- Docker 概览、容器、镜像、网络、卷、Compose 栈、操作日志和工作区外壳已迁入应用项目。外壳使用共享的窄窗口导航，组件依赖页仍由宿主提供。
- Docker 对话框已迁入应用项目，由 SDK 创建和确认。Compose 提交期间禁止取消，放弃修改需经当前对话框的子窗口确认。
- Docker 独立入口使用 SDK 打开窗口、显示组件依赖和跳转文件浏览器，自带中英日界面文案。Docker 已从桌面内置注册移除，作为官方商店可选 `.roapp` 随桌面发行物发布。Developer 声明交互式安装，启动时不安装。真实 Docker 主机验收仍待执行。
- Docker Manager 0.3.0 在清单中要求 `relaxkonos.docker` 服务端模块及 `server.docker` 能力。Windows Release 使用实际归档验证官方商店审查、拒绝绕过联合部署、Developer 交互条目跳过启动安装，以及隔离的客户端包生命周期。发行配置中的最低版本与归档一致。客户端生命周期测试使用模拟部署回执，Docker 模块与 Engine 实机验收待完成。
- Windows Debug 使用实际 Docker 包内容构造 Direct Deploy 负载。回归验证候选启动时读取新清单的依赖，启动守卫拒绝后恢复注册状态，且 `--no-launch` 路径可以登记保留依赖的候选。该测试直接调用桌面部署处理程序。
- Docker 代理状态页已迁入应用项目，通过 SDK 读取引擎、构建与 Docker Desktop 的代理状态。系统设置负责主机级代理配置的写入和清除。

- 本地 SDK 10.0.300。部署库独立构建通过，零警告、零错误。桌面 Client 构建通过，NuGet 漏洞索引不可达，项目还报告了原有编译警告。
- Git CLI 通过本地代理访问 GitHub 失败，基线已通过 GitHub 连接器独立确认。
- 运行 `dotnet run --project Tests/Deployment/RelaxKonOS.Deployment.Tests` 可复现。模拟后端通过共享依赖版本回溯、循环检测、过期计划、服务器切换、中断后恢复和重复提交检查。模拟后端未调用桌面或服务端安装器。
- Windows 上的桌面构建、协调器可选依赖与恢复测试，以及既有本地和远程 .roapp 目录安装测试通过。Release 官方商店回归也通过，涵盖本机 HTTPS 的浏览、下载、审核、安装、同版本修复、升级、失败回退、随包来源和卸载。沙箱内拒绝本机套接字，授权运行通过。联合部署界面新增字符串覆盖 zh-CN、en-US、ja-JP，三个 JSON 文件均通过语法检查。
- 现有应用启动守卫回归通过，覆盖服务器切换、旧窗口重用和文件打开。解析器测试验证缺失依赖定位和明确契约版本。外置应用的启动守卫还需真实双包验收。
- 模块管理桌面 `--modules-only` 回归通过，覆盖本机应用依赖阻止停用及卸载，以及预览后新增依赖阻止确认。
- Notepad 和 Image Viewer 的 Windows Release 包测试通过，覆盖损坏包启动降级、首次离线预装、重复启动、更新和卸载保留。MSBuild 元数据探针生成了这两个默认应用 ID。
- Welcome 已独立构建并使用 DevCli 生成 Release 归档。Windows 包测试通过首次预装、更新、坏包拒绝、重启和卸载。窗口测试覆盖中英日、关闭后解除订阅、记事本入口与安装器回退。
- Welcome 的 Windows Release 回归验证了 Standard 默认读取、Minimal 选择跨启动保留、排除项生效、Developer 选择以及卸载决定保留。尚未验证完整 Developer 工具集和图形化选择。
- Windows 本机 HTTP 签名包测试通过。两份签名 .rkmodule 中，依赖方可提前生成计划，缺少前置模块时提交被拒绝，安装前置模块后原计划成功写入安装目录。此测试覆盖服务端包操作，桌面 .roapp 尚未参加该场景。
- Windows 隔离宿主双包验收通过。使用 DevCli 打包的 Welcome 和实际签名的 fixture-module，经桌面协调器创建计划、安装模块、恢复并启用、再次重启、查证 1.0.0 健康后注册应用。模块包操作只有一条，模块 ping 与测试 Core 就绪端点均可用。模拟启用提交前断线后，重试根据最新期望状态生成新的启用预览。随后模拟服务端已接受启用而响应丢失，重试从服务端待重启状态恢复，整个过程只发出一次实际启用提交。运行 ServerModules 测试的 `--joint-deployment-acceptance <Welcome.roapp>` 可复现。fixture 使用本机 HTTP 和测试鉴权，归档经真实上传端点暂存。
- 同一 Windows 验收还验证了有纯客户端依赖的 .roapp 在退出登录后通过协调器安装，并且没有发送模块 API 请求。服务端依赖仍走原签名包计划。验收也确认，已装应用的版本上界会阻止其客户端依赖被替换为不兼容版本，卸载使用方后同一升级可完成。
- 联合部署取消信号已传入模块目录、包计划、启用提交和健康检查的 HTTP 请求。Windows 双包回归在启用请求等待传输时取消，验证应用保持待部署且启用尚未提交。重试生成新预览，随后模拟提交已被服务器接受但响应丢失。再次恢复无需重复提交，重启后验证模块、Core 和应用登记。
- 联合部署每次执行都绑定当次登录和独立执行上下文。Windows 双包回归在模块客户端创建期间替换登录，确认当次执行被拒绝且未发送模块请求。同一账号在服务器上重新登录后，可另行提交计划并按原流程恢复。
- 联合部署规划失败时保留未能解析的软件包 ID。应用安装器使用中、英、日文分别提示服务端模块不可用与已安装应用版本不兼容。协调器回归验证缺少依赖时返回对应模块 ID。
- 联合部署可以选择仅启用兼容的服务器随附模块。计划绑定模块版本和摘要，确认前以中、英、日文显示启用操作。Windows 隔离宿主验收通过现有模块管理 API 停用 Firewall，再由桌面应用计划重新启用。全程未新增模块包安装记录，重启并通过健康检查后才登记应用。真实 Docker Engine 和 Linux 宿主仍待验收。
- 已安装的独立签名模块也可仅启用。规划核对当前安装记录、摘要及可信目录中的兼容制品，启用前重新检查目录状态。Windows 隔离双包回归先用签名归档安装模块，再停用并重启。联合部署复用该版本，未生成新安装计划或安装记录。再次重启并通过健康检查后完成应用登记。
- 取消所有可选模块后，应用安装器仍生成可审阅的部署计划。Windows Release 用加入可选模块声明的 Welcome 归档验证，未登录时取消唯一可选模块会生成纯本地计划。拒绝计划保留未安装状态，确认计划则登记应用并写入联合部署回执。
- 登录或服务器变更使安装器放弃当前确认。再次安装会按当前会话查找待部署计划或重新生成计划，并请求确认。Windows 隔离双包测试通过安装器命令验证，登录在确认后被替换时没有发出模块请求，重试需要重新确认。
- 查找待恢复部署时会校验检查点的进度范围和持久计划摘要，跳过无效记录。协调器回归修改了已保存计划的启用标记，验证查找跳过该记录、按 ID 读取拒绝该记录，以及恢复原记录后仍可续跑。
- 离线配置集的最低版本适用于仍处于安装状态的应用。提高最低版本后，启动时可通过协调器采用符合要求的可信随包版本。归档未达到要求时保留当前应用。已卸载且有预装回执的应用保持卸载。
- 应用安装器是宿主恢复入口，在 SSH 桌面和仅显示就绪应用模式中仍可从启动菜单打开。Windows 桌面回归覆盖无外置应用的 SSH 启动菜单，以及三个 Shell 的中英日就绪状态布局。
- 启动列表采用宿主就绪检查的应用范围，包括外置包清单的必需依赖。Windows 回归使用模拟状态验证，模块不可用时仅显示就绪应用筛选会隐藏外置应用，模块就绪后应用重新出现。
- 离线桌面根据已登记的本地包判断纯客户端依赖是否就绪。Windows 隔离双包测试安装了实际依赖包，退出登录后验证状态为就绪，且没有发送模块 API 请求。
- 外置应用就绪检查排除模块清单中已隔离、报告恢复状态或驻留故障的模块。Windows 双包回归使用真实模块清单，注入已启用模块的驻留故障，验证应用状态从就绪转为待恢复。
- 联合部署规划、执行验证和启动检查使用同一模块状态判定。Windows 双包回归确认已启用模块报告驻留故障后，状态快照发生变化，解析器无法将该模块标为已满足。
- 外置应用的启动恢复提示采用已登记的本地化应用名。中英日提示分别指向应用安装器和组件中心。Windows Release 启动门禁回归通过。
- Terminal 的 RoyalTerminal 渲染、连接期间的 VT 会话生命周期和外观配置已迁入独立的应用项目。Windows Release 工作区回归覆盖标签、托管与原生 VT、光标和历史记录。窗口入口及 SignalR/SSH 传输仍由宿主承载。
- Terminal 的会话恢复占用表已迁入应用项目。宿主通过一个适配器构造 SSH、SignalR 或本地 PTY 的连接选项，视图模型调用适配器后启动表面。
- Terminal 字体、字号、配色的编辑和状态同步已迁入应用项目。桌面宿主收到编辑后的外观对象后，应用到当前终端表面并保存至工作区。
- Terminal 设置视图、VT 选择模型和中英日文案已迁入应用程序集。桌面宿主加载同一份资源，并继续持久化设备 VT 首选项。
- Terminal 主工作区和标签栏已迁入应用程序集。工作区通过会话视图契约管理标签切换、焦点和可见性。
- Terminal 单会话视图、命令菜单和本地化上下文菜单已迁入应用程序集。宿主视图模型通过会话契约创建传输、提供状态与设置，SSH 凭据解析留在宿主。
- Terminal 应用程序集使用 SDK 编排工作区、创建窗口、打开设置并确认关闭。Windows 测试通过 SDK 窗口服务验证窗口归属、恢复会话、新标签页目录和标签关闭。启动发现和会话创建仍由宿主提供。
- Terminal 恢复编排已迁入应用程序集，SignalR 会话发现通过仅官方 Terminal 可访问的 SDK 能力提供。查询绑定当前登录，连接失效时取消。Windows 回归通过模拟发现接口验证恢复顺序、去重、跳过已退出会话和拒绝过期连接的结果。
- Terminal 恢复占用会在连接启动失败、关闭标签或关闭窗口时释放。Windows 回归验证恢复标签的视图尚未加载时关闭，也允许之后重新发现该会话。
- Terminal VT 首选项通过仅官方 Terminal 可用的 SDK 接口读取和保存。宿主选择桌面或 SSH 设备存储，并校验 VT 取值。Windows 回归使用实际设备首选项文件验证保存与重新加载。
- Terminal 工作区配色和设备内边距通过 SDK 读取。宿主复用现有的外观订阅和设置写入，能力绑定创建时的登录。Windows 终端回归用模拟设置传输验证当前连接可保存、替换服务器后原能力停止写入。
- Terminal 会话视图模型和 SDK 入口已迁入应用程序集。宿主提供会话控制、原生渲染、连接绑定和凭据解析。Terminal 1.0.0 官方 `.roapp` 约 7.1 MiB，由 Standard 和 Developer 离线预装。Windows Release 使用实际 DevCli 归档验证了预装、窗口启动、从目录打开、更新、失败保留版本、重启和卸载。桌面恢复经已安装的包入口转发，真实服务器恢复待验。
- 同一 Terminal 官方归档在 WSL Linux x64 无头环境通过预装、窗口启动、目录打开、升级、失败保留版本、重启与卸载。终端工作区回归在 Linux 识别并运行 Ghostty 原生 VT，托管 VT 回归也通过。
- Windows Release 桌面回归覆盖未登记 Terminal 时普通桌面和 SSH 桌面的「打开终端」入口，以及未登记 Task Manager 时普通桌面的对应入口。这些入口会打开应用安装器。SSH 桌面应用列表及其启动命令使用相同的应用范围，回归验证可直接启动列表中的应用安装器。
- 设置主题的颜色扫描已覆盖 Terminal 新项目。全局扫描仍报告 Proxy、Shell、Git、UI 样式和示例项目中的既有硬编码颜色。
- 服务端替换或回退模块活动版本时，从依赖方的已安装清单校验版本约束。Windows HTTP 签名包回归证实排他上界阻止升级并保留原版本，包完整性与热替换回归通过。手动回退会重新检查归档版本的前置依赖。缺少依赖时保留当前版本，自动启动恢复会隔离失败更新并留下回执，避免激活不兼容的旧包。两条路径均通过 Windows 测试。
- Windows 模块包回归验证了随宿主编译的 Git 模块也受依赖版本区间约束，兼容区间通过，要求更高版本的包被拒绝。
- Windows Release 使用实际 Welcome .roapp 验证配置集通过统一部署协调器离线预装并写入部署回执。回归覆盖 Standard 默认选择、Minimal、排除应用、避免重复安装、保留卸载决定，以及暂缓带服务端要求的包。
- Windows Release 使用 DevCli 打包 Event & Alert Center，验证可信来源、只读权限声明、安装与登记、更新失败保留活动版本、重启发现及卸载。真实服务端告警读取和 Linux 桌面验证待完成。
- Windows 桌面 `--event-alerts-capability-only` 使用 SDK 上下文和实际 HTTP 客户端连接测试传输层，验证告警摘要和列表的认证请求、未授权时阻止请求、撤销权限后停止后续读取，以及切换服务器时丢弃旧响应并阻止旧窗口向新服务器发送请求。服务端 `--event-alerts-only` 的投影和恢复回归通过。
- 配置集 schemaVersion 2 显式声明离线或交互安装策略。Windows Release 使用实际 Event & Alert Center .roapp 验证交互条目在启动时不安装，官方商店可以审阅同一摘要的随包制品。当前发行配置的四个应用都声明离线安装。
- SSH 文件管理器通过应用注册表和激活路由调用已登记的图像查看器或代码编辑器。Git 项目编辑器的共享文件选择器使用独立于 Code Editor 启动类的文件类型表。Code Editor 与 Git 的界面和 SDK 迁移仍待完成。
- 桌面 SDK 的 `IServerFiles` 支持显式选择文件、目录或保存路径，支持多组过滤器，并提供远程路径大小写规则。Windows Release 的记事本回归通过真实宿主选择器验证目录结果和过滤器顺序，也覆盖原有打开与另存为操作。
- 桌面 SDK 可以通过现有服务器文件 API 列出目录的直接子项。Windows Release 回归验证了未授权时拦截请求，以及获授权后返回文件和子目录。Code Editor 工作区仍需接入该能力。
- 桌面 SDK 提供新建目录、就地重命名和删除。Windows Release 通过桌面 SDK 和已有文件客户端接测试 HTTP 传输，验证写授权、非法新名称拦截和三个认证请求。Code Editor 仍需接入这些能力及条件保存。
- 桌面 SDK 的文件读取提供内容版本，条件保存复用当前服务端端点。Windows Release 回归验证匹配版本、冲突、仅创建和应用写权限。服务端条件写入回归另行验证冲突会保留文件内容。
- Code Editor 和 Git 共用的编辑器工作区通过 `IEditorWorkspaceFiles` 声明六项文件操作。Code Editor 的服务器工作区通过 SDK 创建主窗口、读写文件、管理目录树和打开宿主选择器。Windows Release 回归验证源文件与图片过滤器、目录选择和另存为建议名称。SSH 工作区和 Git 项目编辑器通过现有 Explorer 客户端适配。
- Code Editor 的服务器工作区通过 SDK 打开编码、设置、文件树操作和放弃草稿对话框，并使用 SDK 窗口确认和关闭通知保护所有标签页及释放工作区。应用界面和独立发行包仍待迁移。
- Code Editor 使用 `RelaxKonOS.UI` 中的文件内容检测、图标分类、图标控件和 PNG 资源。原有 93 个资源逐个验证内容一致。Android 图标同步脚本指向共享资源目录，现有 Android 资源字节保持原样。本机缺少 Pillow，尚未完成同步脚本的 `--check` 验证。编辑器视图仍在桌面宿主。
- Code Editor 和 Git 的差异视图、补丁解析和渲染器统一由 `RelaxKonOS.UI` 提供。两个应用沿用同一套主题资源和显示规则。
- `Applications/RelaxKonOS.App.CodeEditor` 独立项目保存文件关联规则和中、英、日三语资源。桌面宿主从该程序集加载同一份文案，供当前 Code Editor 和 Git 共用。应用入口、视图及发行包仍待迁移。
- Code Editor 项目还提供文档、标签页状态、目录树、工作区文件契约和视图模型。Code Editor 与 Git 项目编辑器共用这些类型。应用项目只依赖 SDK、共享 UI 和协议类型。桌面宿主提供文案解析和文件适配，并把版本冲突转换为统一的编辑器异常。独立应用入口、视图和发行包仍待迁移。
- Code Editor 主视图和设置视图已由应用程序集编译。文字从视图模型绑定，窗口标题栏移到宿主后仍可刷新文案。Git 项目编辑器引用应用程序集中的同一视图。
- Code Editor 具备独立 SDK 入口、文件关联清单、权限声明与中英日文案，DevCli 在 Windows 生成了 `relaxkonos.codeeditor.roapp`。归档包含程序入口和图标。Windows 桌面回归使用该入口打开本地化 SDK 窗口并经测试文件 API 读取源文件。桌面发行版通过 Developer 配置安装该应用包。Windows Release 已验证实际包的来源、安装、文件关联、更新、失败保留版本、重启发现与卸载。SSH 文件能力沿用经主机密钥校验的 SFTP 会话，真实远端的工作区验收仍待完成。
- WSL x86_64 上使用 Linux .NET 10.0.14 运行协调器及隔离双包验收。用实际 Welcome 归档与在 Linux 测试进程签名的 `.rkmodule` 验证模块安装、取消、响应丢失、重启恢复及随附和独立模块复用。测试进程使用桌面和服务器 Release 的可移植程序集。
- 桌面宿主在 Windows 成功交叉发布到 `linux-x64`，此次交叉发布仅验证宿主产物。在 WSL 单独使用实际 Welcome 与 Browser 官方归档验证无头包生命周期，包括 Welcome 配置集升级、Browser 签入工作区和卸载。修复了包目录已移除时 Linux 卸载清理仍试图删除活动指针的问题。Windows Welcome 回归通过。
- Android Debug 离线构建与 1,904 项 JVM 单元测试通过。只读模拟器使用生产应用宿主冷启动 Debug APK，并通过包来源选择与工作区导航的 10 项设备测试。
- 独立部署设备、Linux 图形桌面、Android 真实服务端联调和全应用三语言界面验收仍待执行。
- Git 的服务器操作由 SDK 中的 `IServerGit` 声明，桌面宿主仍负责提供远程实现。Windows Release 编译和 Git Dock 回归通过。项目编辑器回归在资源预检中因现有 `registry.error.empty_response` 翻译为空而中断。Git 界面和独立发行包待迁移。
- Git 的中英日资源已迁入独立的 RelaxKonOS.App.Git 程序集，由现有桌面语言服务加载。移除三种语言中与有效注册表错误文案重复的空条目，恢复项目编辑器回归。
- Git 的冲突标记解析、提交文件选择和推送预览树模型已迁入应用程序集。桌面 XAML 使用迁移后的类型，服务器侧测试直接引用应用程序集验证冲突标记解析。Windows Release 客户端与服务器测试项目构建通过，Git Dock、项目编辑器和冲突标记解析检查通过。完整 Git 冲突集成回归停在继续合并的断言，后续集成检查暂跳过。
- Git 的差异窗格及其展示模型已在应用程序集编译，桌面日志页与对话框共用该视图。窗口关闭按钮保留宿主语言绑定。Windows Release 的 70 项补丁解析检查和 47 项无头渲染检查通过。
- Git 提交图的分支轨道计算、标签与渲染控件，以及控制台视图，已迁入 RelaxKonOS.App.Git。桌面宿主提供控制台文案并继续调度日志页和命令记录。Windows Release 构建和 Git Dock 回归通过，覆盖合并父节点、分支和标签、命令记录及清空控制台。
- Git 工作区视图模型及各日志会话已编译到应用程序集，通过 SDK 的 IServerGit 和宿主注入的文案函数运行。状态消息保存资源键和参数，沿用现有的切换语言刷新。桌面继续负责窗口、对话框和组件设置。Windows Release 的 Git Dock、项目编辑器、组件安装入口、补丁解析及差异渲染回归通过。
- Git 分支、远程、概览和历史页面已由应用程序集编译。页面通过应用视图模型获取宿主文案，已验证表头随语言切换刷新，远程行模板能显示操作文案。Windows Release 构建与 Git Dock 回归通过。
- Git 提交工具、冲突处理和凭据账户视图已由应用程序集编译，主工作区和宿主对话框使用迁移后的视图。提交工具的已选文件数通过应用视图模型格式化。Windows Release 项目编辑器与 Git 差异渲染回归通过。
- Git 日志主视图、分支树、变更文件树和响应式筛选栏已迁入 Git 应用程序集。工作区图标控件由共享 UI 提供。日志文案和筛选栏重排跟随应用视图模型的语言刷新。Windows Release Git Dock 回归、70 项补丁解析和 47 项差异渲染检查通过。
- Git 项目选择器、标题栏项目切换器和分支弹窗已由应用程序集编译。项目切换器通过回调读取当前项目路径和名称。Windows Release 项目编辑器回归通过，覆盖弹窗树与本地化搜索文案、分支操作、三语言布局和项目切换。
- 项目编辑协调器已迁入 Git 应用程序集。共享编辑器通过 IEditorWorkspaceFiles 访问文件，桌面组合入口提供现有 Explorer 适配和路径存在性检查。Windows Release 构建、项目编辑器和 Git Dock 回归通过。
- Git 主工作区、停靠标签、工作树与暂存视图和托管站点链接解析已迁入应用程序集。主视图直接使用共享编辑器与窗口管理器，宿主继续提供应用激活、对话框和设置入口。Windows Release 构建、项目编辑器及 Git Dock 回归通过。
- Git 工作区统一绑定托管窗口的标题栏、关闭保护和键盘快捷键，桌面激活入口调用同一绑定方法。Windows Release 的融合标题栏及项目编辑器回归通过，验证了忙碌状态下的关闭保护。
- 外置应用上下文新增 Git 能力。宿主在每次调用前后验证当前服务端会话及权限，写操作和凭据账户操作要求管理授权，并将应用生命周期取消传入请求。Windows Release 构建和 Git 能力测试通过，覆盖授权拒绝、认证传输、会话切换与已取消上下文。
- 应用文件 SDK 新增受读权限和会话约束的文件条目查询，供共享编辑器适配器检查 Git 新建项目目标。已验证拒绝未授权查询，并区分存在的目录与缺失路径。Windows Release 构建及项目编辑器回归通过。
- Git 对话框工厂与推送、冲突、远程分支选择和差异对话框已迁入应用程序集，通过 IGitDialogPresenter 接入窗口管理器。Windows Release 融合窗口与项目编辑器回归通过，并在真实托管模态窗口验证推送提交数文案和关闭后的任务完成。
- Git 已具备 IExternalRemoteApplication 激活入口，通过受权限约束的 SDK 获取 Git、文件和窗口能力。入口复用项目编辑器、文件选择和 Git 对话框，窗口关闭后释放会话。Windows Release 使用测试服务验证了打开工作区、探测 Git 及加载项目列表，Git Dock 和项目编辑器回归通过。发行归档、目录接入与完整外置端到端验收待完成。
- Git 0.3.0 已加入桌面发行打包及官方目录。归档声明 Server Git 模块及 server.git 能力，包含独立入口、共享编辑器程序集和原图标。Windows Release 使用实际归档验证来源、完整性、联合安装入口、隔离启动、更新、失败保留版本、重启发现和卸载。测试使用 Git SDK 服务替身，真实服务器联合部署和移除内置注册仍待完成。
- 桌面宿主已移除内置 Git 应用注册。Developer 配置提供 Git 0.3.0 交互式联合安装，正式入口来自官方归档。Windows Release 客户端和服务器模块测试项目构建通过，实际归档生命周期及 Git SDK 激活回归通过。真实设备上的联合部署待验收。
- Windows Release 使用实际 Git 与 Welcome 官方归档完成本机隔离 HTTP 联合部署验收。已验证 Git 模块禁用后仅提交启用、重启前保持待完成、重启后恢复并注册 Git 应用，以及复用已启用模块时不产生模块包操作。真实设备与 Git 仓库业务联调仍待执行。
- File Services 的 SMB 客户端契约已迁入 SDK。外置上下文使用现有传输，检查读取和管理授权、当前服务端会话与应用生命周期取消。Windows Release 的 SMB 功能及共享对话框回归通过，并验证未授权管理与提权、会话切换和取消。界面、激活入口和发行包待迁移。
- File Services 新增独立应用项目，已迁入 SMB 共享权限编辑模型及原有中英日资源。宿主继续使用同一份文案，并通过现有共享对话框使用迁移后的模型。Windows Release 构建、共享对话框和三语言资源解析及运行时绑定检查通过。
- File Services 主视图模型已迁入应用程序集，依赖受限的 File Services SDK 与注入的文案函数。桌面工作区通过回调创建组件设置页。Windows Release 构建、SMB 状态与共享操作、共享对话框及组件安装入口检查通过。
- File Services 主界面及概览、共享、用户三页已迁入应用程序集，标题栏使用包内图标和应用文案。Windows Release 共享页面与对话框回归通过，验证了权限列及行文案；融合窗口验收通过三种窗口样式、语言及宽度。宿主对话框接线、外置入口和发行包待完成。
- File Services 的共享编辑、密码、删除及路径警告对话框已迁入应用程序集。宿主提供草稿关闭保护策略，应用保留字段和权限变更检测。Windows Release 构建与共享页面、草稿关闭和权限回归通过。
- File Services 已具备外置激活入口，使用 SMB SDK、会话失效观察和应用窗口。组件设置及管理员授权提示由宿主提供，目录选择可挂在共享编辑模态窗口下。Windows Release 构建、外置入口加载 SMB 状态、只读授权禁止打开提权提示及共享对话框回归通过。官方包及内置入口切换待完成。
- File Services 1.0.0 已接入官方桌面发行、目录与 Developer 交互安装，桌面宿主内置入口已移除。归档要求服务端 host capability server.file-services。Windows Release 通过实际归档验证来源、完整性、安装、隔离启动、升级、失败保留版本、重启发现和卸载。真实 SMB 主机业务联调待验。
- Firewall 的服务端契约已迁入应用 SDK。外置上下文对读取和管理分别验证权限，并在请求前后检查会话和应用生命周期。Windows Release 构建与防火墙提交和界面回归通过，覆盖只读拒绝变更、授权请求、会话切换和取消。
- Firewall 新增独立应用项目，迁入规则表行、策略选项、问题文案解析及中英日资源。宿主使用注入的文案函数与既有 Helper 错误说明。Windows Release 构建、三语言资源与防火墙回归通过，打开的规则和选项可在切换语言时刷新而保留选中项。
- Firewall 主视图模型现由独立应用项目提供。桌面宿主注入本地化与 Helper 诊断，继续承载窗口和对话框。Windows Release 构建与防火墙回归通过，覆盖提交确认、主机状态回读、权限控制及三语言界面。
- Firewall 工作区和规则编辑器已迁入独立应用项目，使用应用内的图标和本地化绑定，以及 WindowManager 的窄窗口导航。Windows Release 构建与防火墙回归通过，包括打开的工作区标题随语言切换刷新。
- Firewall 新增外置入口和包清单，声明 Linux、Windows 与 Firewall 服务端能力，并通过 SDK 请求独立的读取和管理权限。Windows Release 构建、防火墙既有回归和 DevCLI 打包通过。官方目录切换和外置包安装、启动与权限验收待完成。
- Firewall 已切换为官方可选包，并在开发者安装配置中列为交互安装。清单声明 Firewall 模块硬依赖，旧内置注册和桌面端编译引用已移除。Windows Release 防火墙回归与官方包生命周期验收通过，覆盖单独安装拒绝、包启动、只读权限、更新、重启和卸载。包生命周期测试用测试联合部署回执与模拟服务端，真实 Windows/Linux 模块与防火墙变更待验。
- Windows 环回隔离宿主使用未改写的 Firewall Release 包通过联合部署验收。已停用的随附模块只生成启用步骤，应用等待服务端重启及模块检查后注册。模块已启用时直接复用，两种路径均未新增模块包操作。测试使用隔离模块状态与测试鉴权，未操作操作系统防火墙。可将 Firewall 包作为 --joint-deployment-acceptance 的第二个归档参数复现。

- Process Guardian 的工作负载接口已迁入 SDK。外置上下文分别验证读取和管理权限，并在调用前后检查连接与应用生命周期。Windows Release 构建与权限回归通过。桌面入口与视图尚待外置。
- Process Guardian 的主视图模型和工作负载定义编辑逻辑已迁入独立项目。桌面入口提供当前用户、平台、本地化及 Helper 诊断。Windows Release 构建、SDK 权限测试及 24 项 Guardian 读取和定义回归通过，覆盖参数、健康检查及账户标识的保留。
- Process Guardian 实时日志窗口模型已迁入应用项目，SignalR、证书信任和认证令牌由桌面宿主管理。SDK 日志流使用读取授权，在会话失效、应用结束或查看器关闭时终止。Windows Release 构建和 SDK 模拟日志流回归通过，真实 Agent 的断线重连待验。
- Process Guardian 的工作区、工作负载编辑器及日志视图已迁入应用程序集。桌面入口仍承载文件选取和管理员批准。Windows Release 构建与无头回归通过，验证独立视图绑定工作负载表格，并将可执行文件选取交给宿主。
- Process Guardian 的中、英、日资源已迁入应用程序集，现有桌面入口从该程序集读取界面文案。Windows Release 构建、三语键与占位符校验及 Guardian 界面和权限回归通过。
- Process Guardian 外置上下文已提供受限的当前账户读取和宿主原生文件选择。选取可执行文件或工作目录需读取及管理授权、有效会话与当前应用的所有者窗口。Windows Release 构建和外置上下文授权测试通过，原生选择器实机交互待验。
- Process Guardian 的跨账户工作负载保存已移至宿主批准流程。应用提交工作负载定义，宿主显示目标账户和可执行文件，并在批准后使用凭据提交。Windows Release 构建与取消批准回归通过，真实管理员验证待验。
- Process Guardian 外置入口与包清单已实现，沿用 Guardian 服务端 capability、宿主路径选择、管理员批准和实时日志。Windows Release 构建、只读命令回归及 DevCLI 打包通过。官方目录切换和安装启动验收待完成。
- Process Guardian 已切换为官方可选包，开发者配置采用交互安装。桌面宿主的内置入口与应用程序集引用已移除，管理员批准文案由宿主管理。Windows Release 构建、三语资源、Guardian SDK 和官方包生命周期验收通过，覆盖模拟 Agent 上的已安装启动、只读授权、更新、重启及卸载。真实 Agent 和宿主批准尚待联调。
- 端口转发的请求、状态和设备连接偏好模型已迁入应用 SDK。SSH 进程与凭据处理仍由现有宿主服务拥有。Windows Release 构建和本机环回提交回归通过，桌面视图与启动入口尚待外置。
- 端口转发视图模型及其服务契约已迁入独立应用项目，桌面宿主继续提供服务实现。本地界面与宿主提交共用 SDK 的请求校验。Windows Release 构建和环回提交回归通过。
- 端口转发工作区、连接页及编辑对话框已迁入应用程序集，使用应用图标、绑定本地化和 WindowManager 窄屏导航。外部入口仍待接入。Windows Release 构建、环回提交和组合工作区回归通过，覆盖草稿关闭确认。
- 端口转发三语资源已迁入应用程序集。工作区通过应用资源解析文案，现有桌面宿主从同一资源读取操作错误。Windows Release 构建、三语资源校验及环回提交回归通过。
- 端口转发外置入口与包清单已实现。SDK 宿主服务仅向声明并获授本地转发权限的端口转发应用提供操作。Windows Release 构建、权限与环回回归以及 DevCLI 打包通过。官方分发切换和安装启动验收待完成。
- 端口转发已切换为官方可选包，开发者配置采用交互安装。桌面宿主移除了内置入口和应用程序集引用，宿主与发行包共享操作错误的翻译源。Windows Release 官方包生命周期、本机环回、权限和三语校验通过，真实 SSH 联调待验。
- 应用部署管理器的服务端契约与上传进度类型已迁入 SDK。Windows Release 客户端工作区、诊断、环回 HTTP 上传和 SignalR 回归均通过。应用授权适配器与外置入口尚待接入。
- 应用部署外置上下文已接入读取和管理授权。创建、部署、上传、引用文件及取消等变更要求管理权限，HTTP 与实时日志绑定调用、应用和连接生命周期。Windows Release 构建、应用作用域与工作区回归、环回上传和日志回归通过。
- 应用部署管理器的应用、修订和操作列表模型与状态显示规则已迁入独立项目。宿主继续提供翻译，服务端失败类型由 SDK 提供。Windows Release 构建和应用部署工作区回归通过。
- 应用部署主工作区和部署向导的视图模型已迁入独立应用程序集。宿主注入本地化、窗口、文件选择及剪贴板交互。Windows Release 构建和 SDK 授权与部署工作区回归通过。
- 应用部署工作区、部署向导和状态转换器已迁入应用程序集，沿用共享主题资源。Windows Release 构建与部署回归通过，覆盖窄窗口工作区、向导本地化、草稿绑定和关闭。
- 应用目录安装和升级对话框已迁入应用程序集，由宿主注入本地化与服务器能力信息。Windows Release 构建和模拟服务器工作区回归通过，覆盖拓扑预览、修改端口后撤销确认及升级目标筛选。
- 应用部署的中、英、日界面资源与未登录提示已迁入应用程序集。宿主保留两条 HTTP 传输错误文案。Windows Release 构建、工作区和三语资源回归通过。
- 应用部署外置入口与包清单已实现。入口使用部署 SDK、服务端文件选择授权、宿主窗口以及 Avalonia 原生文件选择和剪贴板，目录安装使用当前连接的能力描述。Windows Release 构建、部署工作区回归和 DevCLI 打包通过。官方分发切换及包启动验收待完成。
- 应用部署管理器已切换为官方可选包，开发者配置采用交互安装。桌面内置注册与应用程序集引用均已移除。Windows Release 工作区和发行包生命周期验收通过，覆盖安装后启动、只读授权、更新、重启及卸载。测试使用模拟部署服务，真实容器部署待验。
- 隧道管理器与组件中心共用的服务端契约已迁入应用 SDK。现有宿主传输与授权流程未改动。Windows Release 构建和组合工作区回归通过，外置应用的授权适配器与入口尚待接入。
- 隧道外置上下文已接入读取、管理与连接生命周期。档案编辑详情和 frps 编辑配置需要管理授权。Windows Release 构建与 SDK 回归通过，覆盖只读拒绝、会话切换取消在途请求及应用结束。受管运行时的安装仍由既有组件中心流程负责。
- 隧道档案与定义编辑器、日志窗口的视图模型已迁入独立应用项目，宿主提供本地化。服务端隧道错误类型由 SDK 提供。Windows Release 构建、SDK 授权与组合工作区回归通过。
- 隧道主视图模型已迁入独立项目，依赖设置页由宿主提供控件工厂。Windows Release 构建与组合工作区回归通过。独立布局测试构建成功，执行在进入隧道用例前因应用安装器主按钮匹配到多个元素而中止。
- 隧道工作区、档案和定义编辑器、日志和诊断视图均已迁入独立应用程序集。组件中心仍通过宿主工厂提供依赖页。Windows Release 构建和组合工作区回归通过，验证了状态列绑定、日志展开和本地化。
- 隧道管理器的中、英、日资源已迁入应用程序集，现有启动器从应用资源解析页面、对话框和服务端故障文案。Windows Release 构建、三语资源与组合工作区回归通过。
- 隧道管理器已实现外置入口和独立包清单。入口使用 SDK 的会话、权限、窗口和组件依赖能力，保留档案与定义编辑、frps 配置与诊断以及独立日志窗口。桌面移除了内置入口与项目引用，将包纳入官方目录和 Developer 交互安装项。Windows Release 使用实际 DevCli 归档验证官方来源、只读启动、升级、拒绝无效升级、重启发现和卸载。隧道 SDK 权限与会话生命周期回归通过。Windows x64 完整桌面发行已生成 19 个官方包，全部通过 SHA-256 核对。发行中的隧道管理器和 Welcome 均通过官方包生命周期回归。真实 FRP 服务待验。

- Squid 管理器已改用应用 SDK 的类型化服务端契约。提交配置、账号和服务动作仍使用既有路由和幂等键，组件中心继续复用状态读取。Windows Release Squid 专项验证了权限、草稿、确认、重试、生命周期和布局。下一步接入外置上下文的会话与授权边界。
- Squid 外置上下文已接入独立读取和管理授权、按连接获取的传输以及应用结束取消。只读授权无法提交变更，操作回执仅接受 Squid 类型。Windows Release 的 Squid 工作区和 SDK 权限回归通过。下步拆分视图模型、工作区与应用资源。
- Squid 工作区通过宿主提供的设置页工厂和组件管理命令接入组件中心。视图及视图模型已移除对组件中心具体类型的依赖。Windows Release Squid 工作区和 SDK 回归通过。
- Squid 工作区视图模型已迁入独立应用项目。本地化通过可注入的查询函数在显示时解析，原桌面入口继续提供当前语言。Windows Release 构建、Squid 工作区和 SDK 回归通过。
- Squid 工作区视图、图标与中英日本地化资源已迁入应用程序集。弹窗内容搬移复用窗口管理框架，宿主入口提供标题、确认和组件依赖页。Windows Release Squid 工作区和 SDK 回归通过。
- Squid Gateway 已改为官方可选包，桌面宿主移除了内置注册与应用项目引用。清单通过联合协调器要求 Proxy 模块及网关能力。Windows Release 使用实际 DevCli 归档验证了来源、拒绝绕过联合部署、只读激活、升级、重启发现与卸载。模块执行使用隔离夹具。Windows x64 完整发行的 20 个官方包均已核对 SHA-256，发行中的 Squid Gateway 通过官方包生命周期回归。真实 Squid 待验。
- Proxy Server 已改用独立 SDK 的类型化配置、账号、凭据、订阅与运行控制契约。界面不再拼接 HTTP 方法和路径，传输绑定打开窗口时的服务器地址。运行控制只接受工作区公开的启动、停止、重启和开机设置。Windows Release 代理工作区、类型化路由、幂等键和端点切换回归通过。下一步接入外置上下文的授权与会话边界。
- Proxy Server 外置上下文已接入读取、管理、凭据导出三个权限。管理和导出分别核对控制设备角色，切换连接会取消在途请求。Windows Release 专项验证仅管理授权不能导出、仅导出授权可读凭据、Observer 无法导出以及连接切换与应用结束。
- Proxy Server 工作区视图模型已迁入独立应用项目，通过注入的本地化查询和组件依赖页工厂接入宿主。组件中心的 sing-box 安装工作区仍由宿主负责。Windows Release 构建、代理专项和 Squid 共享编辑器回归通过。
- Proxy Server 工作区、图标及中英日资源已进入独立程序集。组件中心保留安装工作区和运行时资源，双方复用共同的状态文案。Windows Release 工作区实时切换中英日、代理授权和 Squid 回归通过。组件中心测试夹具已显式选择 sing-box 后再预览只读计划。Windows Release 应用目录和组件中心窗口回归通过，覆盖三种语言与两种主题。
- Proxy Server 已改为官方可选包，移除桌面内置注册和应用项目引用。与 Squid 共用 Proxy 模块依赖，各自核对对应能力。Windows Release 使用实际 DevCli 归档验证拒绝绕过联合部署、只读激活、权限撤销后的窗口反馈、升级、重启发现和卸载。组件启动守卫专项通过。Windows x64 完整发行的 21 个官方包已核对 SHA-256，发行中的 Proxy Server 通过官方包生命周期回归。真实 sing-box 待验。
- Proxy Manager 的服务器契约和结构化故障类型已迁入 SDK，现有传输绑定创建时的登录和服务器地址。路由及原宿主授权代理仍沿用现有实现。Windows Release 代理工作区和流量轮询专项通过，覆盖卸载取消、重挂及迟到结果。
- Proxy Manager 外置上下文已验证读取、常规管理、TUN 管理与控制设备角色。仅管理权限可读完整订阅内容，TUN 要求单独授权。操作回执排除另两个代理工作区的操作，切换服务器取消旧请求。Windows Release 代理工作区与 SDK 专项通过。
- Proxy Manager 工作区通过控件工厂显示组件依赖，通过宿主回调读取网络地址并处理 Helper 故障。视图模型移除了组件中心、任务管理器客户端和提权故障格式化的具体类型引用。Windows Release 代理专项通过。
- Proxy Manager 视图模型和代理组投影已进入独立项目。视图模型通过注入的查询解析本地化，现有桌面窗口在语言变更时刷新绑定。Windows Release 代理工作区与流量轮询专项通过。
- Proxy Manager 的工作区、设置和订阅对话框、样式、图标及中英日资源已进入应用程序集。Windows Release 代理工作区、组合工作区和流量轮询通过，独立入口与官方发行接线待完成。
- Proxy Manager 已改为官方可选包。联合部署要求与 Squid、Proxy Server 共用的 Proxy 模块及对应管理能力。Windows Release 使用实际 DevCli 归档验证拒绝绕过联合部署、只读激活、权限隔离、升级、重启发现和卸载。SDK 专项验证关闭或切换连接时取消请求并清除订阅内容。系统代理地址候选在打开相应设置时请求指标读取权限。Windows x64 完整发行生成 22 个官方包，均通过 SHA-256 核对。发行中的 Proxy Manager、Proxy Server 和 Squid Gateway 均通过包生命周期回归。真实 mihomo 待验。
- Tailscale 控制与登录契约及其共享主机连接标识已迁入应用 SDK。桌面传输继续区分稳定登录身份与当前地址，事件桥接隐藏宿主认证实现。Windows Release 246 项 Tailscale 行为、传输和布局检查通过，设置三语交互检查通过。
- Tailscale 外置上下文已接入读取、管理及按连接绑定的登录。只读状态会隐藏账号和节点详情，原生授权限于 HostNetworkChange 的 Tailscale 目标。Windows Release 验证了只读遮蔽、登录挑战和授权期间切换服务器。现有桌面入口仍在迁移。
- Tailscale 外置 SDK 区分被动刷新与用户主动打开控制设置。被动读取不会触发宿主授权，显式读取仍通过原生授权代理。Windows Release 授权专项覆盖这两条路径。
- Tailscale 控制、登录及对等节点显示通过 SDK 的语言事件和注入的文案查询更新。主机没有授权代理时返回明确状态。Windows Release 的 246 项 Tailscale 行为、传输和布局检查通过。
- Windows 隔离 HTTP 联合部署使用三个实际代理归档验证了对已启用 Proxy 模块的复用。三应用同时安装时全部出现在模块依赖清单，逐个卸载只移除被卸载应用的依赖，模块包操作回执数保持不变。
- Tailscale 外置应用现在收到 SDK 的错误码和 HTTP 状态。宿主先处理原生授权，再将失败转成应用契约。Windows Release 验证被动读取保留授权所需的 403 与错误码。
- Tailscale 连接与登录的操作引用通过 SDK 提供，沿用设备上独立的现有存储文件。读取和保存分别验证当前连接及相应应用权限。Windows Release 专项验证两个存储独立、只读拒绝保存以及服务器切换后拒绝读取旧连接；既有 246 项桌面检查通过。
- Tailscale 的页面标签、确认和偏好对话框改用 SDK 语言事件和应用自己的文案绑定。Windows Release 的 246 项桌面回归通过。独立应用项目和官方入口仍待切换。
- Tailscale 已建立独立应用项目，五类对话框、出口节点显示模型和中英日资源由该项目提供。现有桌面入口和工作区已接入独立项目，Windows Release 的 246 项桌面行为、传输和布局检查通过。主工作区与包入口仍待迁移。

- Tailscale 桌面工作区已随可选官方 .roapp 发布，包清单声明 Tailscale 服务端模块依赖。Windows Release 验证了 246 项桌面检查、SDK 授权，以及官方包的来源、完整性、更新、安装失败、重启和卸载。真实 Tailscale daemon 和设备验收仍待完成。
- Databases 的 PostgreSQL 状态、计划与凭据暂存契约已迁入应用 SDK。后续接入外置应用的授权与连接作用域，以及 MySQL 和受管安装任务。
- Windows Release Databases 桌面专项通过，覆盖 PostgreSQL 操作恢复、凭据暂存和 MySQL 页面回归。
- PostgreSQL SDK 已接入外置上下文。Windows Release 专项验证读取授权、凭据暂存的应用授权和控制端角色，以及切换主机时取消请求并拒绝迟到结果。Databases 桌面回归通过。
- MySQL 已通过 SDK 查询状态和暂存凭据，桌面视图模型改用类型化宿主传输。MySQL 和 PostgreSQL 共用应用授权、控制端角色和连接失效门禁。Windows Release 数据库作用域专项和桌面回归通过；受管安装任务与官方包仍待迁移。
- 数据库外置上下文已提供安装任务提交、查询和取消，限定 PostgreSQL 和 MySQL 服务，并沿用原幂等键和宿主安装台账。Windows Release 验证了拒绝跨服务提交、读取和取消、Databases 桌面回归，以及共享安装任务的授权取消与同键重试。桌面入口仍需接入作用域设置和提权。
- 数据库安装 SDK 已接入宿主提权代理，按固定安装服务选择授权能力和目标。Windows Release 作用域专项验证观察者及非数据库服务无法请求授权，切换服务器会取消在途请求，迟到的授权结果不会交给应用。桌面安装任务仍需接入应用设置。
- 安装任务通过应用 SDK 设置保存和恢复工作区操作 ID，Databases 与组件中心复用同一宿主适配器。Windows Release 的 Databases 桌面与安装事务专项通过，覆盖原工作区、MySQL 设置键、JSON 结构及修订号。
- Databases 维护页改为接收组件依赖设置视图，MySQL 视图模型改用 SDK 连接作用域。Windows Release 数据库作用域专项及 109 项桌面检查通过，覆盖切换服务器后停止旧工作区操作。现有入口仍组装宿主视图，独立包入口将通过应用上下文获得该视图。
- Databases 独立项目已承接中英日各 205 个资源键。现有桌面入口通过 SDK 显示语言解析应用标题和确认文案，工作区暂时通过宿主本地化绑定读取同源资源。Windows Release 独立项目构建和 112 项 Databases 桌面回归通过。
- Databases 初始化和确认对话框已迁入独立应用项目，打开期间通过 SDK 语言事件更新标签和确认正文。Windows Release 构建与 116 项桌面检查通过，包括三语热切换、默认端口返回及取消。
- 共享安装任务视图模型已迁入应用 SDK，由调用方提供文案、权限错误映射和失败回执显示策略。Windows Release Databases 桌面、安装提权和安装事务专项通过，覆盖恢复失败回执的调用方差异。
- Databases 主工作区、各页面及视图模型已迁入独立项目。程序集引用检查确认未引用桌面客户端。Windows Release 119 项桌面检查通过，覆盖同一窗口中静态标签和状态的三语切换。桌面登记和官方包入口仍待切换。
- Databases 已切换为官方可选包，使用应用 SDK 提供的数据库、安装、证书和组件设置能力。Windows Release 制品通过官方来源与摘要、升级、安装失败、重启、卸载及只读启动验证，另有 119 项桌面检查和数据库授权专项通过。真实数据库主机与服务端模块切换尚待验收。
- 数据库服务端的端点和领域服务各自归入单一注册入口。Windows Release 验证 6 条 PostgreSQL/MySQL 路由的方法和授权策略，以及 11 个安装适配器、凭据存储和备份调度注册。Core 仍调用这些入口，模块开关、停用保护和实机调度待验。
- 数据库服务由内置模块 relaxkonos.database 注册并公布 server.database 能力。Windows Release 专项核对启停能力、六条路由和停用保护，使用受控 Helper 覆盖排队安装、MySQL 待恢复操作及次要 PostgreSQL 实例上的备份计划。ServerModules 默认套件通过模块目录及恢复清单检查后，在现有 Proxy 重复设置写入器断言处停止。数据库尚未以独立 .rkmodule 发布或部署到真实 Linux 主机。
- Databases 0.2.0 声明需要 server.database。Windows Release 官方包验收使用桌面兼容性检查验证服务器撤下能力时拒绝启动，恢复公告后正常只读启动。官方包生命周期、数据库授权专项及 119 项桌面检查通过。
- PostgreSQL 备份调度器已迁入数据库执行程序集。Core 通过专用接口保留固定调度身份、原生查询和安装台账。Windows Release PostgreSQL 服务端与自动备份回归以及数据库端点专项通过，验证同一时段幂等、台账重开和失败回执；真实 Linux Helper 和独立 .rkmodule 仍待验。
- 数据库停用检查已迁入执行程序集，Core 仅桥接共享安装台账和原生状态查询。Windows Release 专项通过，使用受控 Helper 验证待恢复操作、次要实例备份计划、缺失的原生证据和损坏台账都会阻止停用。独立 .rkmodule 和实机验收仍待完成。
- PostgreSQL 实例状态、维护计划、迁移计划和 MySQL 状态采用数据库执行程序集的查询入口。Core 保留 HTTP 授权和闭集 Helper 调用。Windows Release 数据库端点专项核对了四种 Helper 查询及实例选择，原备份回执投影继续沿用。
- 数据库密文存储已迁入同进程执行程序集，原路径、数据保护用途和密文格式可继续读取，Core 保留钥匙和身份引用。Windows Release PostgreSQL 专项验证旧格式密文、实例和身份绑定、幂等暂存与清理。未部署独立模块或连接真实数据库。
- Core 的 ModuleHost 私有代理已登记四条数据库只读路由。请求需通过可信数据库模块的进程令牌、同代次用户上下文、有效会话和现有数据库读取策略。Windows Release 专项验证四条路由全部拒绝无令牌访问，原数据库公开端点和停用检查仍通过。安装包调用与可信代次端到端验收待完成。
- ModuleHost ABI 2.4 新增数据库只读客户端。Windows Release 专项使用受控传输核对四个固定 GET、实例参数和两种授权头，拒绝无用户上下文、错误模块身份和非回环地址。数据库入口接入、独立包和真实代次验收待完成。
- Databases 已实现独立 ModuleHost 只读入口，候选发行元数据指定 Linux x64 和 ABI 2.4。Windows Release 数据库专项验证仅映射四条 GET、传递 PostgreSQL 实例参数并保留代理的 HTTP 状态、内容类型和问题正文。内置模块仍占用同一 ID，签名归档、生产接线和真实 Linux 验收待完成。
- 使用 Windows Release 生成的托管模块文件和一次性本地 P-256 密钥，离线打包命令已生成 Databases 0.1.0 测试归档和签名目录。保留 ID relaxkonos.database 要求目录用途为 official，development 用途被打包器拒绝。本地测试签名未接纳到任何设备。Linux RID publish 未成功，归档仅用于打包验证，设备安装与健康检查仍待验。
- Databases Linux x64 framework-dependent Release 发布已通过。使用该发布目录和一次性 P-256 密钥生成 0.1.0 本地候选，独立核对目录签名与归档 SHA-256。尚未接纳该目录或部署候选包。
- Core 已将 Databases 的四条查询和两条凭据暂存拆为独立路由注册入口，为后续将查询映射到 ModuleHost 提供切换点。当前内置模块调用两个入口，公开路径及授权策略保持原样。安装器仍以 module-package-module-id-conflict 拒绝与内置 ID 冲突的候选。
- Databases 路由拆分通过 Windows 专项，使用新编译的 Server 程序集和既有测试程序集，核对六条公开路由及授权、停用保护和私有代理拒绝路径。
- Databases 私有只读代理沿用 RuntimeRead 对控制端和观察者的授权，并继续核对有效会话、可信进程代次及显式配置的用户名单。通用模块管理代理继续使用其管理授权。生产路由切换与实机观察者请求仍待验。
- 安装器已允许同 ID 的官方 Databases ModuleHost 候选。已安装包描述在模块目录中接替内置描述，沿用数据库启用开关、能力公告、Core 服务和停用检查，并由监督器跟踪包版本。Core 公开查询转发和实际安装待验。
- 已安装的 Databases ModuleHost 通过四条固定公开 GET 接收查询。Core 保留读策略和凭据暂存，转发绑定受信包代次及用户请求上下文。进程不可用时返回 503，通用代理拒绝该模块。真实设备安装和端到端读取待验。
- 官方模块来源策略修订升至 5，并授权 relaxkonos.database。默认仍需部署方显式启用来源并配置可信公钥。WSL x86_64 使用自包含 ModuleHost 加载签名候选，核对无令牌请求返回 401、PostgreSQL 实例参数与两种上下文头送达受控回环代理，并通过 ready、health 与 shutdown。真实 Core、目录接纳、在线安装和 Helper 验收待完成。
- WSL x86_64 运行当前 Linux x64 自包含 Core，在临时服务根以本地测试公钥接纳 Databases 签名目录，并通过离线安装写入 0.1.0 ModuleHost 安装事实。同一操作 ID 的持久回执为 AwaitingRestart，effective=false。在线计划、运行信任确认、监督器激活及真实 Helper 查询待验。
- 冻结计划可为离线安装且没有代码信任记录的同一 ModuleHost 归档首次确认信任；保留 AwaitingRestart。Windows Release 的模块包 HTTP 专项验证离线包初始无信任、直接安装无法确认信任、冻结计划写入精确信任，并核对回执与幂等重试。Linux 上的运行激活和观察者查询待验。
- 已安装且启用的 ModuleHost 在缺少精确代码信任时，健康报告为 blocked 与 module-host-code-trust-required。禁用和隔离状态继续独立显示，无法读取信任台账时报告 module-package-trust-invalid。
- Windows Release 模块包专项通过。受监督的测试模块在坏版本回滚和健康版本热更新后，均使用当前进程代次的令牌实际读取 HTTP 端点。Linux Databases 的隔离激活与真实 Helper 查询待验。
- Databases 桌面包 0.3.0 声明同 ID 的服务端模块和 server.database 能力。Windows Release 使用实际 .roapp 验证清单、拒绝绕过联合部署、只读启动、升级、重启和卸载。客户端生命周期测试使用受控的联合部署回执，真实 Linux 联合安装仍待验。
- Windows 隔离 HTTP 联合部署使用实际 Databases 0.3.0 归档验证复用已生效的内置数据库模块。安装登记模块依赖，卸载移除该依赖，两步均不新增模块包操作。Linux 独立包的联合安装与真实 Helper 查询待验。
- Windows 隔离 HTTP 联合部署使用实际 Databases 0.3.0 归档验证内置数据库模块停用后的启用计划。启用既有模块无需下载，Core 重启确认前桌面安装保持待处理，重启后可继续完成安装。
- 模块商店为已接纳的官方 Databases ModuleHost 发行投影 Core 提供的 server.database 能力，使联合部署能以该能力匹配签名候选。独立包清单继续不声明 Core 能力。
- 联合部署确认独立模块时，健康报告的运行版本必须与模块清单的生效版本一致。Windows 本机 HTTP 验收拒绝了版本不一致的健康报告，并通过恢复报告后的验证及 Databases 打包应用联合部署回归。
- 应用启动检查区分服务端依赖缺失、等待模块操作或重启、以及已启用独立模块尚无运行版本。Windows 联合部署 HTTP 验收确认尚无运行版本时拒绝启动且不误报缺失。
- 两个完整发行入口统一以 MSBuild 全局属性传递 RuntimeIdentifier。此前 Linux x64 ModuleHost 使用 dotnet publish --runtime 时，Packaging 项目的资产缺少目标 RID；使用全局属性后 ModuleHost 和 Core 自包含发布成功。PowerShell 和 Bash 发行脚本语法已核对。本地 WSL 预览实例未配置 ModuleHost 隔离启动所需的系统文件，暂用于临时目录及进程验收。
- 当前 PowerShell 发行入口完成完整 Linux x64 服务端 ZIP，包含 Core、Helper、Guardian 和 ModuleHost。归档 SHA-256 与随包摘要一致，解包后通过 server/linux-x64 发行文件清单逐项校验。制品保留在本地验收目录。
- 使用完整 Linux x64 发行 ZIP 解包后的 Core 和 ModuleHost 完成 WSL 验收。Core 接纳新签名的测试目录并持久记录离线安装回执 AwaitingRestart。ModuleHost 直接启动候选，四条查询携带预期参数和头部到达受控回环代理，健康检查及退出通过。系统隔离激活和真实 Helper 仍待验。
- Databases 私有查询代理显式使用共享 RelaxKonOS JSON 协议，使独立包转发的响应沿用公开 API 的枚举字符串格式。Windows Release Server 编译与数据库边界专项通过，真实观察者查询仍待验。
- ModuleHost 监督器的状态快照在停止或精确代码信任失效时立即撤下 running；健康响应不再报告生效版本。Windows 模块包专项验证热更新后撤销代码信任立即撤下健康和连接，无需等下一次探测。
- 一次性、无外部网络的 Ubuntu 容器使用发行 ZIP 中的 ModuleHost 通过 Linux 隔离启动验收。采用现有 direct-root 测试模式，确认 root 持有不可变包快照、非 root 用户持有模块数据目录，并验证四条查询均到达受控回环代理及健康、退出。镜像缺 ICU，验收副本启用 invariant globalization。生产 sudo 接线、Core 监督激活和真实 Helper 查询仍待验。
- 含 ICU 的一次性 Ubuntu 容器使用未经修改的发行运行时配置通过 Databases 隔离验收。正式 Linux 安装脚本配置固定 sudo 启动器后，非 root Server 测试账户可以启动包，专用模块账户持有数据目录，root 持有包快照，四条查询、健康和退出均通过。查询使用受控回环代理，Core 监督下的联合安装和真实 Helper 查询待验。
- 一次性 Ubuntu 容器中，发行 Core 接纳签名目录并离线安装 Databases。验收夹具为精确归档预置代码信任后，非 root Core 使用正式 sudo 启动器监督模块，记录 running，Core /healthz 返回 200 并正常退出。安装回执由 AwaitingRestart 转为 Succeeded。真实在线联合安装及观察者经 Helper 查询待验。
- 一次性 Ubuntu 容器运行发行 Core、隔离 Databases ModuleHost 及真实 Linux Helper。使用临时 PAM 账户登录两台客户端，刷新首台设备令牌并核对 observer 角色，四条数据库查询均从公开 HTTP 入口返回 200 和 JSON。安装信任仍由夹具为精确归档预置，在线联合安装及真实数据库实例业务验收待补。
- 一次性 Ubuntu 容器移除预置代码信任。真实 PAM 控制端上传已离线安装的 Databases 归档、创建并应用冻结计划，验证持久信任绑定归档摘要且首次确认前无运行进程。Core 重启后监督器启动模块，同一在线操作回执为 Succeeded，观察者四条查询经真实 Helper 均返回 200。桌面协调器跨设备安装及在已部署数据库实例上的业务验收仍待完成。
- 一次性 Ubuntu 容器从无 installed.json 和代码信任的状态验证 Databases 首次在线安装。控制端上传签名归档、冻结并应用计划，包记录与代码信任均绑定同一 SHA-256。Core 重启后由固定 sudo 启动器监督模块，在线操作回执为 Succeeded，真实观察者经 Helper 的四个 GET 均返回 200。尚未从实际桌面协调器跨设备调用该流程。
- 桌面模块管理客户端接受 Core 已发布的 ModuleHost 生命周期健康状态，包括 blocked、starting、draining、stopping、stopped、faulted 与 disabled。Windows 本机 HTTP 回归验证包含缺少代码信任条目的健康报告仍可读取。
- Ubuntu 临时容器安装并启动实际 PostgreSQL 16 系统包。通过在线安装后的 Databases 模块与真实 Helper，观察者状态查询返回 database.external_installation 且 managed=false，维护与迁移计划返回 503。验收前后均能通过 psql 查询外部实例。容器使用 pg_ctlcluster 启动并提供 systemd 目录标记，受管实例及真正 systemd 服务生命周期仍待验。
- 联合部署只在独立模块的运行版本与活跃安装记录一致，且 SHA-256 与模块清单一致时复用该模块。Windows HTTP 回归验证摘要不一致时不会生成已满足的候选。
- 联合部署在最终验证独立模块时重读活跃安装记录，并核对运行版本、SHA-256 及适用的来源和发行 ID。Windows HTTP 回归篡改单独的安装记录响应，确认即使模块清单和健康报告仍正常也会拒绝验证。
- 联合部署目标修订纳入服务端活跃安装记录的归档身份、隔离状态和生效选择。准备计划时复核安装记录，独立观察目标沿用同一计算。Windows HTTP 回归验证仅安装记录摘要改变也会使旧目标失效。
- Ubuntu 临时容器从签名目录指定的本地 HTTPS 来源下载 Databases 候选归档，Core 使用夹具 CA 校验 TLS，并核对归档长度和 SHA-256 后暂存。控制端冻结并应用安装计划，重启核对同一回执成功，观察者经真实 Helper 的四条读取通过。本次由管理员 API 驱动，桌面协调器跨设备安装待验。
- 组件中心以中英日文案显示 ModuleHost 启动、等待请求、停止与阻塞状态，并解释代码信任缺失、撤销和台账读取失败。Windows Release 客户端构建及三语资源键核对通过。组件中心宽范围测试在 zh-CN Host facts 布局断言处中断。
- 补齐数据库分类的中英日资源，修复组件中心 Host facts 页显示原始键的问题。Windows Release 组件中心专项通过，覆盖三语和两套主题，以及本地、远程 .roapp 商店回归。
- Databases 观察入口及发行元数据迁入专用项目。Linux x64 发布目录只含观察入口及共享契约，不携带 Core 数据库执行程序集。
- 独立 Databases 观察程序集生成 0.2.0 Linux x64 签名候选。一次性 Ubuntu 容器以隔离启动器加载候选，四条查询到达受控回环代理，健康和退出通过。
- 拆分后的 Databases 0.2.0 签名候选通过一次性 Ubuntu 的完整在线安装与读取验收。既有 Linux x64 发行 Core 从无安装记录开始，应用冻结计划、重启监督新入口、核对回执成功。观察者经真实 Helper 的四条公开查询均返回 200。
- 一次性 Ubuntu 使用同一签名目录内的 0.1.0 与 0.2.0 Databases 归档完成在线热更新。更新回执为 Succeeded，当前代次报告 0.2.0/running，Core 进程未重启，Core 健康和 Git 仓库枚举继续提供服务。更新后观察者经真实 Helper 的四条查询通过。
- Ubuntu 在线回退验收在 Databases 0.2.0 运行时应用签名但入口缺失的 0.3.0 测试归档。更新回执为 Failed/module-host-update-rolled-back，监督器恢复 0.2.0/running。旧版本实际处理 PostgreSQL 请求，Core 健康和 Git 仓库枚举继续可用，观察者四条查询通过。
- Ubuntu 在线生命周期验收通过冻结管理计划停止和重启 Databases 0.2.0。两次操作回执均为 Succeeded，停止后 PostgreSQL 查询返回 503，重启恢复原版本及观察者四条读取。Core 未重启，Core 健康和 Git 仓库枚举继续可用。
- SSH 文件浏览和 Server Center 的安装、修复文件选择入口显式传入各自应用上下文。视图从传入上下文取得语言服务，并在打开选择器时捕获用量记忆作用域。Windows Release fused-apps 专项通过，覆盖 SSH 文件布局、会话隔离和传输保护。独立应用包仍待迁移。
- SSH 文件浏览器与 Server Center 选择器改用 SDK ISystemLanguage 和按需捕获的选择器记忆回调，视图无需访问宿主服务容器。Windows Release fused-apps 专项通过。SSH 传输和界面仍在桌面宿主项目。
- SSH 文件浏览器和编辑器文件适配器共用宿主 SFTP 建连，视图不再读取密码或指纹。建连前后核对同一会话端点，继续校验已接受的主机密钥。Windows Release fused-apps 专项通过；真实 SFTP 切换和独立包待验。
- SSH 文件浏览器三条目录读取路径（导航、缓存刷新、目录树）经宿主会话读取目录项，视图只处理显示、过滤和缓存。目录导航和树加载拒绝连接切换后的旧结果。Windows Release fused-apps 专项通过，真实 SFTP 和独立包待验。
- SSH 文件浏览器的重命名、新建目录和删除由宿主会话执行。新建及删除确认绑定打开对话框时的远端会话，不会把操作转送到后来连接的主机。Windows Release fused-apps 专项通过，复制和上传下载仍使用视图内的 SFTP 操作。
- SSH 文件浏览器的单文件上传和下载经宿主会话执行，使用打开系统选择器前捕获的端点，批量文件上传沿用同一端点，远端目标存在时拒绝覆盖。Windows Release fused-apps 专项通过。递归目录传输和复制仍待迁移。
- SSH 文件浏览器的递归目录上传和下载迁入宿主会话。目标绑定文件选择前的端点，保留进度、取消、覆盖拒绝、符号链接及本地路径名校验。Windows Release fused-apps 专项通过；复制和真实 SFTP 联调仍待完成。
- SSH 文件浏览器的远端复制和移动迁入宿主会话。保留批量操作的部分成功与剪贴板更新，普通文件复制使用独占创建防止覆盖。视图不再引用 SSH.NET，目录访问拒绝由宿主转换成可识别的异常。Windows Release fused-apps 专项通过，真实 SFTP 联调及独立打包待完成。
- SSH 文件浏览器通过应用 SDK 的 `ISshFileSession` 调用宿主文件操作。视图使用不透明的连接引用绑定剪贴板、缓存和异步文件操作，宿主继续按连接身份校验请求。Windows Release fused-apps 专项通过，外部应用上下文接线、独立打包及真实 SFTP 联调待完成。
- 外部应用上下文为官方 SSH 文件浏览器提供启动时捕获的 SSH 文件会话。连接引用按上下文隔离，重连或应用生命周期取消后，该上下文无法发起新的文件请求。Windows Release fused-apps 专项覆盖这些边界并通过。应用入口仍由宿主注册，独立打包及真实 SFTP 联调待完成。
- SSH 文件浏览器通过 SDK 的 `ISshTransferPicker` 选择本地上传源和下载目标。宿主继续使用原有的四项目录记忆，并向官方外部应用上下文提供同一能力。Windows Release 构建和 fused-apps 专项通过。真实系统选择器和 SFTP 联调待验。
- SSH 文件浏览器实现 `IExternalRemoteApplication` 启动接口，通过外部上下文创建窗口、选择本地路径并调用 SFTP。打开远端文件继续优先交给图像查看器，其次交给代码编辑器，由宿主校验当前 SSH 连接和应用关联。Windows Release 构建与 fused-apps 专项通过。外部入口仍编译在桌面宿主中，独立加载和真实 SFTP 待验。
- Explorer 与 SSH 文件浏览器共用的目录树节点、列表布局、框选和工具栏样式移至 `RelaxKonOS.UI`。Explorer 通过列表描述回调保留驱动器容量文本。Windows Release 构建、467 项 Explorer 回归检查和 fused-apps 专项通过。SSH 文件浏览器仍有本地化和路径排序依赖待迁移。
- 文件名自然排序和扩展名提取移至 `RelaxKonOS.UI`，Explorer 和 SSH 文件浏览器使用同一实现。Windows Release 构建、467 项 Explorer 回归检查和 fused-apps 专项通过，覆盖长数字文件名及 SSH 列表排序。SSH 文件浏览器仍需迁移本地化资源并验证独立打包。
- SSH 文件浏览器的外部入口、视图、对话框和中英日资源移至独立的 `RelaxKonOS.App.SshFiles` 项目。桌面宿主与服务器中心复用该项目的视图，目录缓存由 `RelaxKonOS.UI` 提供。Windows Release 构建、467 项 Explorer 回归检查及 fused-apps 专项通过，覆盖语言切换、工具栏提示和安装包选择器。全仓本地化检查仍报告迁移前已有的 60 项缺失。正式应用包接入和真实 SFTP 联调待完成。
- `relaxkonos.ssh-files` 加入官方目录及桌面发布打包清单。DevCli 生成 Release `.roapp`，官方包专项通过来源与完整性校验、安装和更新、失败回滚、重启及卸载。测试以模拟 SSH 会话启动已安装的包，并确认窗口来自独立加载上下文。默认安装配置切换、移除内置启动注册及真实 SFTP 联调待完成。
- SSH 文件浏览器已移除内置启动注册，标准与开发者配置通过离线官方包安装。SSH 桌面缺少该包时，文件入口打开应用安装器。同一包生命周期内、连接到同一 SSH 会话的窗口共享剪贴板和拖放状态。Windows Release 构建、官方包专项和 fused-apps 专项通过。真实 SFTP 复验需要配置 SSH 测试会话。
- Windows Release 已安装的 SSH Files 官方包通过真实环回 SFTP 读取根目录，宿主使用测试服务器的已知主机密钥指纹。可用 `Tests/Client/verify-ssh-files-sftp.py` 搭配 Paramiko、窗口预览测试 DLL 与已生成的官方包及其校验文件复验。真实远端桌面与写操作未在本次验收执行。
- 环回 SFTP 验收补充逐字节下载校验。测试修改宿主保存的主机密钥指纹后，确认下一次下载被拒绝且没有收到文件字节。Windows Release 官方包专项通过。
- Windows Release 环回 SFTP 验收通过单文件上传、拒绝覆盖及下载回读。测试服务只将新文件保存在内存中。远端桌面及目录传输、重命名、删除等操作仍待验收。
- Windows Release 环回 SFTP 验收通过单文件重命名、同名拒绝和删除。测试核对旧目录项消失、重命名后内容保持一致，以及删除后原始测试文件仍存在。目录传输与远端桌面待验收。
- Windows Release 环回 SFTP 验收通过嵌套目录上传和下载，核对两级目录中的文件字节及空目录，并验证同名远端目录拒绝合并。此次验证由已安装官方包的文件能力发起，远端桌面交互和远端复制移动待验收。
- Windows Release 环回 SFTP 验证复制保留源文件、移动移除旧目录项并报告成功，下载确认内容一致。修复上传进度回调在文件流关闭后读取长度导致的崩溃。界面忽略取消或关闭后的迟到回调，批量及递归操作在文件边界检查取消。重新打包后环回验收连续两次通过。远端桌面交互待验收。
- 联合部署恢复会重新计算所选归档的摘要。没有目录预置摘要的本地候选包也可按内容匹配未完成计划，恢复后锁定同一摘要。Windows Release 官方包专项验证同一归档可恢复、变更字节后无法匹配。退出后自动恢复归档仍待实现。
- 联合部署把暂存归档的清理推迟到最终验证回执写入之后，安装已提交但验证尚未结束时仍可使用原归档重试。Windows Release 官方包专项通过，检查了更新完成后已写入验证回执且暂存归档已清理。
- 已开始的联合部署将持久本地归档路径记入回执。应用安装器重开时按本地或当前账户与服务器查找未完成回执，重新检查归档来源、应用身份和摘要后加入待确认队列。Windows Release 测试通过重开台账发现原归档与篡改后跳过的检查。临时下载归档和需要恢复目录授权的候选包仍需重新取得。
- 官方应用目录候选包可按应用 ID、版本和 SHA-256 从当前可信目录重新取得，再加入待确认队列。Windows Release 官方包专项使用随附目录验证重开后匹配发行版可恢复、摘要不同时跳过。在线下载恢复路径仍待联调，组件中心的独立目录授权仍需重新取得。
- Windows Release 官方包专项使用受控 HTTP 传输验证在线目录恢复。删除首次暂存的归档并重开部署台账后，安装器拒绝同长度但摘要错误的下载，重新取得正确归档后将其留在待确认状态。生产 HTTPS 目录与组件中心授权仍待验收。
- 组件中心签名目录包的联合部署回执记录来源、组件、发布者和发行身份。恢复时重新取得当前目录授权并校验摘要，执行前再验证授权。Windows Release 官方包专项确认来源不可用时跳过恢复，且同摘要的随附包无法代替原授权。使用有效签名目录完成恢复的端到端验收仍待执行。
- Windows Release 签名目录测试从待处理回执恢复实际可加载的组件包。删除原暂存包并刷新签名目录序列后，重新授权和下载匹配发行版，沿用原计划安装，验证后清理归档。修复了恢复时应先加载目录快照再授权的问题。验收使用受控传输，未访问生产目录。
- 服务端确认某一模块计划已过期且从未派发时，协调器持久记录需要重新审阅，保留已完成步骤，并在重开后仍可找回归档。安装器为该归档重新规划并要求确认。同一归档的新计划验证完成后，旧计划不再进入恢复队列。部署检查点、官方包和签名组件目录 Release 回归通过，过期分支使用模拟后端验证。
- 同一版本及摘要的组件归档来自多个签名来源时，恢复按原来源、组件、发布者和发行身份匹配，候选队列也按该身份去重。Windows Release 检查点回归验证两个相同摘要的签名来源分别恢复各自的计划，无来源授权的候选包无法匹配这两份计划。签名组件目录端到端回归通过。
- 从服务器文件选择器导入的联合部署归档可按原账户、服务标识和服务器路径重新读取。重新获取时复用文件下载授权，核实会话和摘要后才加入待确认队列。Windows Release 测试传输验证切换服务器时不发下载请求、切回后得到原计划，以及远端内容变化后不入队。真实服务器文件下载恢复待验收。
- 服务器归档恢复专项使用实际 Explorer HTTP 客户端及认证处理器验证下载路由、文件路径和 Bearer 授权。Explorer 在构建请求时绑定登录，切换发生在请求构造后或令牌读取期间时都会阻止请求发往旧连接。验收使用受控 HTTP 传输。
- 服务器归档下载将暂存操作捕获的登录约束传给 Explorer 和认证处理器。测试验证调用方捕获的登录失效时，即使 HTTP 客户端可见的当前登录仍有效，请求也不会发出。Release 组件目录专项和 467 项 Explorer 回归通过。
- 中英日资源校验通过。检查器纳入宿主已加载的应用资源和链接资源，验证 4,362 个宿主键及 37 组应用资源的键与格式参数。Browser 未登录文案移到宿主资源，数据库恢复文案由组件中心和独立数据库包共享，两者无需增加程序集引用。Windows Release Browser 能力、数据库工作区和账号专项通过，覆盖三语数据库恢复提示。
- Android 图标同步恢复了十个已迁出桌面宿主的图标来源，保留原 Android 资源名和文件内容。补齐组件中心衍生图标并同步品牌图标。162 项图标检查通过，Android Debug 离线构建及 1,904 项 JVM 单元测试通过。本轮没有可用模拟器，未重跑设备测试。
- 使用只读 Medium_Phone（API 37）模拟器安装重新构建的 Debug APK 和测试 APK。ADB 确认 MainActivity 冷启动成功。`WorkspaceNavigationTest` 的 7 项和 `InstallationPackagePickerTest` 的 3 项均通过，覆盖宽窄布局、导航恢复、草稿归属及安装包来源选择。本次仅执行这两组设备测试。
- Windows x64 Release 重新构建 ServerModules 测试，与当前桌面发行归档运行两轮 `--joint-deployment-acceptance` 并通过。两轮均经环回模块管理 HTTP API 验证签名测试模块上传、安装、启用、响应丢失后的对账、重启和桌面登记。真实 Firewall、Databases 和 Git 归档验证仅启用和重启恢复，Certificates 复用现有模块。Proxy Manager、Proxy Server 和 Squid Gateway 同时安装后逐个卸载，仍在使用同一模块的应用保留依赖记录。离线客户端依赖阻止不兼容升级，移除依赖者后同一升级成功。测试使用隔离授权和模块状态，未执行生产业务或跨设备依赖保护验收。
- Windows Release 将实际 Docker Manager 官方归档加入 `--joint-deployment-acceptance`。隔离环回宿主使用实际 Compose 操作台账，验证随附 Docker 模块停用后仅生成启用计划，服务端重启后才登记应用。直接复用已启用模块时未产生新的模块包操作，卸载应用只移除依赖记录。测试未调用 Docker Engine。
- Windows Release 使用实际 Web Server Manager 官方归档验证传递依赖的启用和恢复。计划先复用 Certificates，再启用 Web Servers，服务端重启后才登记桌面应用。服务端停用预览在 Web Servers 仍启用时阻止停用 Certificates。卸载桌面应用只移除直接依赖记录，不更改服务端模块。Docker Manager 同轮回归通过，未调用 Nginx 或 Docker Engine。
- 联合部署只接受包清单声明的可选依赖及原版本、能力条件，无效选择在任何服务端请求之前被拒绝。Windows Release 隔离双包回归使用声明了可选 Firewall 和必需签名模块的测试桌面归档，验证选中时保留 Firewall，取消后仍规划并安装必需签名模块。安装器对话框继续默认勾选清单的可选项，本次通过安装器视图模型和环回模块管理 API 验收。
- 从未开始的过期联合部署计划在联系服务器前被拒绝，避免离线错误掩盖必须重新审阅的状态。读取当前快照后再次校验时限，已写入检查点的计划继续沿用原恢复流程。Release 协调器测试通过，覆盖服务器不可达时返回过期、未生成执行检查点以及现有中断恢复。
- 恢复服务端联合部署计划时，安装器在校验归档及读取台账前后核对同一次已认证登录，读取后再核对账户和服务标识。Windows Release 环回回归验证断线但客户端仍保留缓存身份时不会恢复服务端计划，重新建立认证状态后可继续同一计划；离线本地依赖安装仍通过。
- 自动恢复归档从查找台账开始绑定同一次已认证登录。服务端来源和服务端联合计划在暂存前及入队前复核登录，本地离线归档仍可恢复。Windows Release 目录专项通过，受控 HTTP 传输验证同账户同服务重新登录会使旧恢复尝试在下载前失效，重新开始恢复后可取得原归档。
- 启动守卫的模块依赖查询绑定检查开始时的登录、账户和服务标识，创建 HTTP 客户端后及收到查询结果时重新核对。Windows Release 隔离双包回归验证同账户同服务重新登录发生在客户端创建期间时不会发出模块查询；下一次检查可正常识别健康模块，故障模块仍阻止启动。
- 依赖解析回溯时成对保留失败原因与相关包，避免后续版本冲突覆盖先前循环或深度耗尽对应的包。Release 协调器测试通过，覆盖循环与缺失混合分支、超深候选被拒绝，以及可选的较低版本仍成功求解。
- 联合部署在每个会更改服务端模块的步骤前重新核对全部已安装客户端依赖的路径、版本和应用登记，也覆盖排序位于该模块之后的依赖。Windows Release 隔离双包和离线依赖回归通过，本轮未注入执行中卸载客户端依赖的并发场景。
- Windows Release 隔离双包回归补齐客户端依赖并发卸载场景。混合计划中服务端模块排在本地依赖前，在读取执行快照时卸载该依赖，协调器返回依赖已变更，服务端模块包操作仍为零。后续签名模块完整安装与恢复回归通过。
- 安装器以中文、英文和日文提示过期、快照变更及客户端依赖变更后的重新审阅操作。客户端依赖变更会让当前安装器实例跳过旧计划，后续提交重新规划和确认。Windows Release 应用目录回归与 4,363 个宿主语言键、37 组应用语言资源校验通过。
- 已开始的联合部署在步骤执行时发现客户端依赖变更，会持久记录需要重新审阅，保留当前进度和归档恢复入口。重开台账后拒绝直接恢复旧计划。Release 协调器及隔离双包回归均通过，后者验证读取服务端快照期间卸载本地依赖后重开安装器的计划查找。
- Windows Release 隔离双包验收覆盖本地依赖变更后的完整重新审阅。补装本地依赖后为同一归档创建新计划，复用已健康的签名模块并完成桌面登记。模块包回执总数仍为一，重开台账时旧计划不再进入恢复队列。
- Linux x64 在无外网容器运行当前 Release 可移植测试程序集，使用 Welcome 官方归档与运行时签名的 Linux `.rkmodule` 完成联合安装与恢复。Databases、Web Server Manager 和 Docker Manager 官方归档通过仅启用、重启、模块复用及卸载检查。数据库停用保护使用空闲状态 Helper 夹具。独立 Databases 发行包与客户端的联合安装、图形运行和实际业务引擎另待验收。
- Linux x64 WSL 使用 .NET 10.0.14 和当前 Release 可移植桌面测试程序集，执行发行包三配置无头验收。Minimal、默认 Standard、Developer 分别登记 0、9、10 个离线官方包，均保留内置恢复入口。SSH Files 发行包通过来源、完整性、更新、安装失败保留、重启发现和卸载检查。该轮复用桌面发行中的归档，未运行 Linux 原生窗口或真实 SFTP。
- Linux x64 原生图形冒烟通过。专用 WSL 使用临时解包的 Ubuntu X11 依赖和隔离的用户目录，经 Linux .NET 10.0.14 启动当前 Release Desktop 可移植入口 `RelaxKonOS.dll --installation-profile=minimal`。`xwininfo` 识别到新增的 RelaxKonOS 1920×1007 窗口，并确认 `Map State: IsViewable`。测试在观察后终止所启动进程，未检查桌面交互或正常关闭。
- Linux x64 WSL 原生 Minimal 窗口正常退出验收通过。X11 确认新窗口已映射后，仅向该测试窗口发送 WM_DELETE_WINDOW，桌面进程自行退出并返回 0。测试使用临时用户目录，未执行其他桌面交互。
- Linux 原生桌面启动与退出复验可使用 `Tests/Client/verify-linux-desktop-startup.py <publish-directory>`。脚本需要 Python、Linux .NET、X11 图形会话以及 xwininfo 和 xprop，使用临时 HOME，确认可见窗口 PID 属于测试进程后发送关闭请求。专用 WSL 上复验通过，退出码为 0。
- Windows x64 复跑与 Linux 相同的 Release 双包验收输入。Welcome 与签名测试模块通过安装和恢复，Databases、Web Server Manager、Docker Manager 官方包通过仅启用、重启、复用和卸载，且证书传递依赖保护通过。测试使用环回宿主及空闲数据库 Helper 夹具。
- 全发行配置切换验收在 Windows x64 与 Linux x64 WSL 均通过。Developer 切至 Minimal 保留已登记的十个包及安装记录，用户随后卸载 Welcome，再切回 Developer 不会重新安装 Welcome，也不会替换其余包。回归使用发布归档、实际安装器和隔离 VSD。
- 当前 Release 桌面可移植发行完整构建成功。25 个官方 `.roapp` 的 SHA-256、ZIP 完整性及清单 ID 全部通过，发布的安装配置与源码逐字节一致。Linux x64 WSL 直接使用这套发行产物复验 X11 Minimal 启动和退出、三种配置分别安装 0/9/10 个离线包，以及配置切换保留主动卸载。该发行由 Linux .NET 运行时启动，未构建独立 Linux RID 应用宿主。
- Linux x64 RID 发行入口首次启动问题已修复。用户应用数据目录尚未创建时，桌面在初始化前创建并验证绝对路径，防止虚拟系统盘回退到发行目录。专用 WSL 使用临时且不存在的 XDG 数据目录，通过独立 `RelaxKonOS` 应用宿主和托管入口两次复验，系统盘均创建在隔离用户数据目录内，X11 窗口正常打开并以 0 退出。RID 宿主采用框架依赖发布并复用已校验的 25 个官方归档。
- Linux x64 发行的独立应用宿主在专用 WSL 中通过三种配置的原生首次启动。每个测试使用全新的用户数据目录，启动脚本核对系统盘的实际已安装包记录。Minimal、未提供配置参数的默认 Standard 和 Developer 分别登记 0、9、10 个离线包，X11 窗口均可见并正常退出。脚本使用 `--apphost --profile minimal|standard|developer` 复现。
- 用户数据目录初始化改动在 Windows x64 Release 应用宿主通过原生回归。使用已校验的 25 个归档组装测试发行目录，运行 `verify-windows-desktop-startup.ps1` 在应用数据沙箱启动 Minimal，确认主窗口响应并正常关闭，退出码为 0。本次 Windows 图形测试覆盖 Minimal，三档预装和切换由独立发行测试验证。
- Windows Release 官方商店 HTTPS 环回复验使用 Notepad 发行归档。删除已安装版本的图标资源后，通过原官方来源审阅并执行同版本修复。新安装目录恢复原图标字节并保留应用登记，虚拟系统盘中的用户文档内容保持一致。目录端点和证书由隔离夹具提供。
- Windows Release 和 Linux x64 WSL 协调器专项验证同一计划的并发执行。首个协调器暂停在首步骤时，另一协调器无法取得同一日志锁，也没有执行额外步骤。首个执行完成后重放只读取已验证的检查点，不会重复执行或验证。
- 同一部署计划的日志锁冲突在 Windows/Linux 上有明确的正在执行状态。安装器以中英日提示等待后重试，并保留已确认计划；其他 I/O 错误仍按原异常报告。Release 客户端构建、跨平台协调器并发回归及 4,364 个宿主语言键校验通过。
- 当前 Databases 独立观察模块的 Linux x64 Release 发布通过。通过 WSL 上的 Server 包构建入口，以一次性测试密钥生成 0.2.0 签名候选。归档 ZIP、入口程序集逐字节比对及签名目录内归档摘要核对通过。测试签名短期有效，本轮未接纳目录或部署模块。
- 当前 Databases 0.2.0 签名候选在一次性 Ubuntu 通过两层验收。独立 ModuleHost 在无外网容器通过四条回环代理查询、健康和退出。发行 Core 另通过在线冻结计划安装该候选、按归档摘要授予代码信任、经正式 sudo 启动器监督运行并核对安装回执。观察者登录经真实 Helper 的四条 HTTP 只读查询均返回 200，Core 健康和正常退出通过；桌面协调器发起该安装仍待联调。
- Ubuntu 隔离验收使用当前 Databases 观察程序集和短期测试 HTTPS 目录。真实 Core 商店搜索投影 `server.database` 并报告 Linux RID 兼容，Core 从 HTTPS 下载确切归档，冻结计划验证摘要并安装、记录精确代码信任。随后独立 ModuleHost 在隔离启动时报 IOException，健康验收未通过。前一轮直接上传的 0.2.0 归档能启动，此轮 HTTPS 夹具使用 0.1.0 测试版本，差异原因待排查，尚未执行桌面协调器端到端。
- 已定位并修正 Ubuntu HTTPS 夹具的隔离启动失败。旧入口类型不属于当前观察程序集，重新用同一 0.1.0 测试版本和正确入口签发后，Core HTTPS 下载、冻结安装、代码信任、正式 sudo 监督、健康、卸载前的四条观察者只读查询和正常退出全部通过。该测试从服务端商店 API 发起，桌面协调器仍待端到端验收。
- `.rkmodule` 离线打包增加入口类型元数据校验，无需加载或执行模块。旧类型名与新程序集不匹配会在签名前被拒绝。Linux WSL 模块包专项全通过。Windows Release 打包断言通过，随后安装回归在临时目录移动时被拒绝，Windows 整组未通过。
- 使用同一 Windows Release 测试程序集复跑模块包专项，所有断言通过，临时目录移动失败未复现，原因尚未确认。模块商店与安装 HTTP 专项另行通过，覆盖已消费上传的幂等执行、丢失响应后的原回执、已执行计划过期后的回执保留。
- 新增 `--live-databases-joint-deployment` 验收入口，使用真实账户登录、桌面协调器和隔离 VSD。Ubuntu 一次性环境用原版 Databases `.roapp` 验证复用经 HTTPS 安装、由正式 sudo 启动器监督的签名独立 `.rkmodule`，桌面在核对运行模块后完成登记，四条真实 Helper 查询仍通过。原版 `.roapp` 遇到随附模块已满足能力时会优先复用，未覆盖由桌面发起独立模块安装的 start/resume 路径。
- 一次性 Ubuntu 通过由桌面发起的 Databases 双包端到端验收。用实际 `.roapp` 复制生成测试归档，只把最低模块版本设为 9.0.0，使随附版本不能满足条件，并以同一真实观察程序集签发 9.0.0 `.rkmodule`。桌面协调器以真实账号登录，进行 HTTPS 下载和冻结计划安装，重启前不登记桌面应用。Core 通过正式 sudo 启动器监督模块后，第二个桌面进程恢复原计划并完成登记。四条经真实 Helper 的观察者查询通过，服务端唯一安装回执为 Succeeded 且摘要吻合，Core 正常退出。生产归档和最低版本要求均未改动。
- Ubuntu Databases 双包端到端再次通过，并补验桌面启动守卫。登记应用后，守卫接受实际运行的独立签名模块，随后进入单独的组件读取权限检查。验收夹具未授权该权限，因此验证范围止于此检查，未打开业务窗口。
- 当前 Windows Debug 桌面、DevCli 和测试宿主重新构建后，全量 Direct Deploy 验收通过。真实桌面登录界面通过 --no-launch 安装并重启后回退；两个隔离配置验证项目与包直传、监视更新、失败保留窗口、串行操作及配置隔离。Notepad 和 Image Viewer 通过直传生命周期，Release 安装指针保持不变。
- Windows Debug 使用真实 Docker `.roapp` 内容复跑 Direct Deploy 必需依赖专项。候选首次启动被拒绝时不登记，--no-launch 登记并保留依赖。再次部署并启动被拒绝时，先前安装路径、应用登记和依赖仍保持一致。测试使用启动守卫夹具，未启动真实 Docker Engine。
- Windows Release 组件中心专项验证模块移除预览后新增本机应用依赖。直接调用提交命令时仍会报告依赖应用，服务端未收到提交，旧预览保留供取消。验收覆盖本机安装状态，跨设备依赖声明仍待实现。
- Windows Release 隔离双包回归模拟同一连接地址和登录下服务端实例标识变化。旧计划返回 `deployment-server-changed`，未创建执行检查点，未增加模块操作，也未登记桌面应用。恢复原标识后，原计划通过正常安装和恢复流程。
- Settings 外置拆分开始。Workspace 壁纸传输接口及 HTTP 实现迁至宿主 WorkspaceSettings 服务，Shell 壁纸服务不再引用 Settings 应用命名空间。Windows Release 壁纸与工作区设置专项通过。Settings 界面、独立包和恢复入口迁移仍待完成。
- Settings 搜索栏已迁入独立 `RelaxKonOS.App.Settings` 程序集，只依赖 Avalonia。宿主工作区通过双向查询属性和本地化提示属性接线。Windows Release `--settings-window-only` 通过，覆盖键盘焦点、搜索、导航、缩放和三语界面。完整 Settings 应用仍由宿主登记，独立 `.roapp` 待完成。
- Settings 的搜索目标附加属性和高亮焦点逻辑已迁入独立程序集。14 个设置页直接使用新程序集的附加属性。Windows Release 设置交互和完整窗口专项均通过，覆盖隐藏控件、只读控件、任意远程目标拒绝、导航与三语。
- Settings 中英日资源已原样迁入独立程序集。宿主合并同一份嵌入文案，发布输出不再单独复制 Settings 语言文件。Windows Release 设置交互及完整窗口专项在无旧文案输出的环境通过，资源校验通过 4,364 个宿主键及 38 组应用资源。
- Settings 标题区已迁入独立程序集，现有工作区通过属性提供返回命令、无障碍标签和本地化标题。Windows Release 完整设置窗口和融合标题栏专项通过。完整 Settings 工作区和独立安装包尚待迁移。
- Settings 导航历史和搜索索引迁入独立程序集，单测改为引用实际项目，设置窗口的搜索结果模板也引用该程序集。Windows Release 导航、搜索及完整设置窗口断言通过。Settings 整组在之后的既有全局颜色检查失败，涉及 Shell、主题及示例中的硬编码颜色。
- Settings 搜索结果列表、结果模板和键盘/双击激活逻辑迁入独立程序集。现有工作区传入结果集合、打开命令和本地化文字。Windows Release 完整窗口专项通过，测试通过列表选中和 Enter 触发跨程序集导航、定位、返回并恢复查询。
- Settings 的 Shell 快捷入口及 SDK 应用/环境导航具备缺失时打开内置安装器的恢复行为。Windows Release 设置交互专项使用激活夹具核对路由不存在时进入安装器、已受理的设置请求不会另开安装器。Settings 仍注册为内置应用，本轮未验完整卸载后的独立包修复。
- Settings 已接管法律文档资源、离线读取和只读对话框。宿主只提供窗口、标题和关闭操作。Windows Release 完整设置窗口专项通过，并核对独立程序集可离线读取项目许可与第三方声明。关于页主视图及完整独立 .roapp 仍待迁移。
- Settings 关于页的视图、产品信息、链接命令和法律文档入口迁入独立程序集。现有工作区负责页面导航与语言事件，宿主提供链接打开、剪贴板和对话框能力。Windows Release 完整设置窗口专项通过，核对迁移后的页面渲染当前语种，且可离线读取两份法律文档。
- Settings 页面路由、图标、标题和本地化查找迁入独立页面模型，宿主子类仍处理 ShellSettings 的订阅与保存。页面通过构造时提供的本地化函数解析文字。Windows Release 完整窗口和交互回归通过。
- Settings 无障碍页面与交互模型迁入独立程序集，通过窄接口读写缩放、减少动效和高对比度。宿主保留原设备文件和应用样式通知。Windows Release 设备设置及完整窗口专项通过，验证持久化、即时资源变化、分组恢复默认与搜索目标。
- Settings 通知与启动页面和交互模型迁入独立程序集。宿主继续使用原文件分别保存本机和 SSH 桌面偏好。Windows Release 设备偏好及完整设置窗口专项通过，覆盖 SSH 自动打开终端的独立保存、通知抑制、分组重置及搜索定位。
- 独立 Settings 视图增加基于当前页面数据上下文的文案绑定。关于、无障碍及通知与启动页面复用该绑定，删除逐条标签转发属性。Windows Release 设置窗口与设备偏好专项通过，覆盖关于页打开后实时切换中文与日文。资源校验纳入独立 Settings XAML 引用并通过。
- Settings 首页视图、快捷入口筛选和固定项排序迁入独立程序集。宿主继续提供设备偏好、语言通知和导航请求处理。Windows Release 完整设置窗口专项及本地化资源校验通过。
- Settings 性能选项对话框和预设编辑模型迁入独立程序集。应用通过窗口效果值读取和提交设置，宿主继续负责保存工作区偏好。Windows Release 设置窗口回归与本地化校验通过。
- Settings 默认应用页面及关联编辑迁入独立程序集。宿主负责应用注册事件和工作区偏好 DTO 转换。Windows Release 设置窗口回归与本地化校验通过，专项检查验证已保存的自定义扩展名能显示并进入自动补全。
- Settings 应用数据清理对话框及选项模型迁入独立程序集。宿主接收确认后的选项并调用原数据服务。Windows Release 设置窗口回归和本地化校验通过，本轮未执行真实数据清理。
- Settings 应用权限编辑界面、草稿和保存命令迁入独立程序集。宿主仍根据应用声明和权限注册表构建选项，并通过现有权限管理器写入用户决定。Windows Release 设置窗口回归覆盖草稿、保存和取消，本地化校验通过。
- Settings 应用入口、清单和 URI 路由分发迁入独立程序集。内置注册使用新的入口，宿主通过 ISettingsWindow 提供窗口构建和工作区导航。本阶段仍使用内置注册，尚未接入外部包加载。Windows Release 设置窗口和工作区偏好测试通过，覆盖支持及拒绝的 Settings URI。
- Settings 深链接校验和分发共用独立程序集中的路由解析，普通页面输出规范路由，环境变量和应用权限保留各自的窗口操作。工作区偏好回归通过，覆盖实际分发、混合大小写以及非法深链接拒绝。
- Settings 页面滚动记忆、搜索焦点恢复与结果定位迁入独立程序集。宿主视图通过导航模型契约及控件接线，Windows Release 设置窗口回归通过。
- Settings 主工作区视图、窄屏导航和融合标题栏接线迁入应用程序集。宿主向应用视图注入仍依赖客户端服务的页面模板。Windows Release 设置窗口、宿主标题栏回归和本地化资源校验通过。
- Settings 主工作区启用编译绑定，通过应用程序集定义的页面模型、命令及页面状态契约接收宿主数据。Windows Release 设置窗口回归与本地化校验通过。
