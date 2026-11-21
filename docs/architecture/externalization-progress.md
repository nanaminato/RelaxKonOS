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
| appinstaller | AppInstaller | 否 / 否 | 是 | 文件读取与组件源；安装和恢复入口需保留 | 核心入口候选 | 待验 |
| welcome | RelaxKonOS.App.Welcome | 是 / 是 | 否 | 使用 SDK 激活记事本或安装器 | 基础 | Windows 已验 |
| settings | Settings | 否 / 否 | 是 | 工作区设置与宿主设置 | 基础 | 待验 |
| explorer | Explorer | 否 / 否 | 是 | 文件 API；调用存储提供者的聚合入口 | 基础 | 待验 |
| terminal | Terminal | 否 / 否 | 是 | Terminal capability | 基础 | 待验 |
| server-center | ServerCenter | 否 / 否 | 是 | 服务端连接、安装与管理 | 基础 | 待验 |
| component-center | ComponentCenter | 否 / 否 | 是 | 组件、可信来源、模块及任务 API | 基础 | 待验 |
| taskmanager | TaskManager | 否 / 否 | 是 | 服务端系统性能与进程 API | 基础 | 待验 |
| notepad | RelaxKonOS.App.Notepad | 是 / 是 | 否 | 文件能力通过 SDK | 基础 | 待验 |
| browser | Browser | 否 / 否 | 是 | 本地 WebView 与浏览器宿主服务 | 基础 | 待验 |
| imageviewer | RelaxKonOS.App.ImageViewer | 是 / 是 | 否 | 文件能力通过 SDK | 基础 | 待验 |
| registry | Registry | 否 / 否 | 是 | Registry API | 基础 | 待验 |
| event-alerts | EventAlerts | 否 / 否 | 是 | 可观测事件 API | 基础 | 待验 |
| codeeditor | CodeEditor | 否 / 否 | 是 | 远程文件与编辑器 API | 可选 | 待验 |
| docker | Docker | 否 / 否 | 是 | Docker 模块；组件中心管理 Engine | 可选 | 待验 |
| processguardian | ProcessGuardian | 否 / 否 | 是 | Guardian API 与独立 Agent | 可选 | 待验 |
| firewall | Firewall | 否 / 否 | 是 | Firewall 模块与宿主权限边界 | 可选 | 待验 |
| proxy | Proxy | 否 / 否 | 是 | Proxy 模块；组件中心管理 mihomo | 可选 | 待验 |
| proxy-server | ProxyServer | 否 / 否 | 是 | ProxyServer capability；组件中心管理 sing-box | 可选 | 待验 |
| squid | Squid | 否 / 否 | 是 | GatewayProxy capability；组件中心管理 Squid | 可选 | 待验 |
| file-services | FileServices | 否 / 否 | 是 | FileServices capability 与主机 SMB | 可选 | 待验 |
| port-forwarding | PortForwarding | 否 / 否 | 是 | 连接会话与隧道 | 可选 | 待验 |
| certificates | Certificates | 否 / 否 | 是 | Certificates 模块 | 可选 | 待验 |
| webservers | WebServers | 否 / 否 | 是 | WebServer 模块与宿主权限边界 | 可选 | 待验 |
| git | Git | 否 / 否 | 是 | Git 模块与远程文件 API | 可选 | 待验 |
| tunnels | Tunnels | 否 / 否 | 是 | Tunnels capability；组件中心管理 FRP | 可选 | 待验 |
| application-deployments | ApplicationDeployments | 否 / 否 | 是 | ApplicationDeployments capability | 可选 | 待验 |
| database | Databases | 否 / 否 | 是 | 数据库引擎与组件中心 | 可选 | 待验 |
| tailscale | Tailscale | 否 / 否 | 是 | Tailscale capability、Firewall 集成与宿主组件 | 可选 | 待验 |
| ssh-files | ServerCenter | 否 / 否 | 是 | Server Center 的 SSH/SFTP 会话 | 基础应用内部入口候选 | 待验 |

基线共有 29 个登记入口，2 个可独立打包安装，27 个由桌面宿主编译和登记。当前第三个独立包 Welcome 已从桌面宿主移出。桌面发行构建将 Welcome、Notepad 和 Image Viewer 放进 `OfficialApps`，并随包提供版本化的 installation-profiles.json。Standard 默认安装这三个应用，Minimal 跳过预装并保留应用安装器入口，Developer 暂与 Standard 同集，等开发者应用外置后扩充。首次可用 --installation-profile=minimal|standard|developer 与可重复的 --exclude-bundled-app=<id> 选择和排除。选择保存在本机 VSD。启动扫描后通过原安装器离线预装，逐项记录首次处理的版本。重启和客户端升级保留已记录应用的卸载决定。包含服务端要求或必需依赖的包须经交互部署。官方商店可使用与已安装版本相同的可信归档进行修复，继续使用原安装器的摘要、路径和版本检查。

## 复用边界

- Runtime 继续由 ApplicationManager 负责应用注册、激活和窗口策略。启动依赖观察应接入现有 IApplicationLaunchGuard。参见 [应用激活](RelaxKonOS.ApplicationActivation.md)。
- 官方桌面商店沿用 `OfficialDesktopPackages`、`AppPackageInstallerService` 和 `DeveloperPackageManager` 的来源、哈希、安装与注册边界。Direct Deploy 继续复用软件包管理器。参见 [包商店](../platform/RelaxKonOS.PackageStore.Design.md) 和 [开发 CLI](../../Tools/RelaxKonOS.DevCli/README.md)。
- 模块侧复用可信目录、包计划、应用操作回执及 ModuleHost 健康查询。主机准入和宿主级授权由服务端判定。参见 [组件中心](../platform/RelaxKonOS.ComponentCenter.Design.md) 和 [模块包格式](RelaxKonOS.ServerModulePackage.Format.md)。

## 实施与恢复

1. 代码基线和桌面登记盘点已记录。
2. `Shared/RelaxKonOS.Deployment` 已加入解决方案。解析器支持传递依赖、共享依赖、区间、能力与契约版本、回溯和循环检查。协调器有目标服务器、状态与目录版本、计划有效期和持久检查点。通用编排和文件日志已接入桌面安装器。
3. 桌面 .roapp 清单可读取和验证 dependencies.required 与 dependencies.optional。旧包可省略整个字段。应用安装器的联合部署路径在包安装提交前验证必需依赖。其余入口继续拒绝带必需依赖的包，直至接入同一协调器。模块目录适配器已复用现有模块管理客户端读取分页可信目录、安装记录和有效状态，并绑定服务器身份、目录版本和模块状态摘要。组件中心模块商店使用精确来源和发布 ID 创建签名包计划，并核对上传与计划的摘要、版本和发布者。桌面应用商店与共用安装器已接入协调器，默认勾选可选模块，展示已满足的依赖、版本变化及权限声明。用户确认后按签名计划执行，验证服务端依赖并通过现有应用安装器验证程序集与注册。模块执行适配器已实现按联合计划保存服务端计划 ID、到期时间和权限声明，按 ID 对账回执，并验证实际启用版本、能力和健康。首次安装在包回执确认后复用模块管理的 Enable 计划，保留已有的期望启用集合。需要重启时暂缓客户端安装。启动守卫复用现有应用就绪检查，按当前登录服务器的有效模块、版本和能力检查包依赖，并提供组件中心或应用安装器入口。客户端卸载锁内会阻止移除仍被另一已安装应用声明为必需的 .roapp。跨进程恢复可在同一账户与连接下重新选择同一摘要的应用包，恢复最近一份未完成计划。应用版本目录中记录联合计划回执，提交后中断时可核对现有安装。尚未支持自动恢复归档或刷新过期的未执行模块计划。组件中心的模块停用和卸载操作展示本桌面已安装应用的依赖，并在提交前重新检查。服务端原有模块互相依赖保护继续生效。不同设备和其他管理入口共享应用依赖声明仍待服务端持久化。服务端目录目前仅提供 capability 名称，显式要求契约版本时仍需可靠的版本证据。
4. 后续依次完成启动守卫、升级与卸载保护、基础应用外置及修复、其余模块迁移和 Minimal、Standard、Developer 配置集。
5. 恢复时从本分支运行 `dotnet build Shared/RelaxKonOS.Deployment -m:1`，查看当前差异与最新提交，再从第 3 项继续。部署存续期间须先观察现有服务端回执，再决定是否重试，保留共享依赖。

## 测试证据

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
- 服务端替换或回退模块活动版本时，从依赖方的已安装清单校验版本约束。Windows HTTP 签名包回归证实排他上界阻止升级并保留原版本，包完整性与热替换回归通过。手动回退会重新检查归档版本的前置依赖。缺少依赖时保留当前版本，自动启动恢复会隔离失败更新并留下回执，避免激活不兼容的旧包。两条路径均通过 Windows 测试。
- Windows 模块包回归验证了随宿主编译的 Git 模块也受依赖版本区间约束，兼容区间通过，要求更高版本的包被拒绝。
- Windows Release 使用实际 Welcome .roapp 验证配置集通过统一部署协调器离线预装并写入部署回执。回归覆盖 Standard 默认选择、Minimal、排除应用、避免重复安装、保留卸载决定，以及暂缓带服务端要求的包。
- 独立部署设备和 Linux 双包验收、Android 和全应用三语言界面验收均待执行。
