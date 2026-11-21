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
| welcome | Welcome | 否 / 否 | 是 | 桌面向导与宿主动作 | 基础 | 待验 |
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

基线共有 29 个登记入口，2 个可独立打包安装，27 个由桌面宿主编译和登记。基础应用尚无默认预装机制。桌面发行构建已把 Notepad 和 Image Viewer 放进 `OfficialApps`，商店可浏览和安装这些包。

## 复用边界

- Runtime 继续由 ApplicationManager 负责应用注册、激活和窗口策略。启动依赖观察应接入现有 IApplicationLaunchGuard。参见 [应用激活](RelaxKonOS.ApplicationActivation.md)。
- 官方桌面商店沿用 `OfficialDesktopPackages`、`AppPackageInstallerService` 和 `DeveloperPackageManager` 的来源、哈希、安装与注册边界。Direct Deploy 继续复用软件包管理器。参见 [包商店](../platform/RelaxKonOS.PackageStore.Design.md) 和 [开发 CLI](../../Tools/RelaxKonOS.DevCli/README.md)。
- 模块侧复用可信目录、包计划、应用操作回执及 ModuleHost 健康查询。主机准入和宿主级授权由服务端判定。参见 [组件中心](../platform/RelaxKonOS.ComponentCenter.Design.md) 和 [模块包格式](RelaxKonOS.ServerModulePackage.Format.md)。

## 实施与恢复

1. 代码基线和桌面登记盘点已记录。
2. `Shared/RelaxKonOS.Deployment` 已加入解决方案。解析器支持传递依赖、共享依赖、区间、能力与契约版本、回溯和循环检查。协调器有目标服务器、状态与目录版本、计划有效期和持久检查点。当前仅有通用编排和文件日志，生产执行适配器尚未接入。
3. 下一项是扩展客户端包清单和现有安装候选模型，使用现有服务器目录、包计划、执行回执和健康接口实现适配器，接入商店预览与执行。
4. 后续依次完成启动守卫、升级与卸载保护、基础应用外置及修复、其余模块迁移和 Minimal、Standard、Developer 配置集。
5. 恢复时从本分支运行 `dotnet build Shared/RelaxKonOS.Deployment -m:1`，查看当前差异与最新提交，再从第 3 项继续。部署存续期间须先观察现有服务端回执，再决定是否重试，保留共享依赖。

## 测试证据

- 本地 SDK 10.0.300。部署库独立构建通过，零警告、零错误。
- Git CLI 通过本地代理访问 GitHub 失败，基线已通过 GitHub 连接器独立确认。
- Windows/Linux 设备联合部署、Android 和中英日验收均待执行。
