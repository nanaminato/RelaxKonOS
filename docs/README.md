# RelaxKonOS 项目与文档索引

RelaxKonOS 在客户端本地渲染界面，通过共享协议与服务端同步状态并执行操作。模块边界、依赖方向和运行模型见[架构设计](architecture/RelaxKonOS.Architecture.md)。

## 从哪里开始

| 任务 | 入口 |
| --- | --- |
| 构建、调试和验证 .NET 项目 | [开发调试指南](development/RelaxKonOS.Develop.md) · [本地调试](development/RelaxKonOS.LocalDebugging.md) |
| 部署服务端或分发客户端 | [部署指南](../deployment/README.md) · [客户端分发](../deployment/ClientDistribution.md) |
| 修改桌面外壳、设置、主题或本地化 | [桌面端文档](desktop/README.md) |
| 修改 Android 客户端 | [Android 工程文档](../Client/RelaxKonOS.Client.Android/docs/README.md) |
| 开发桌面应用包 | [开发者模式](development/RelaxKonOS.DeveloperMode.md) · [内置应用规范](development/RelaxKonOS.BuiltInApplication.Conventions.md) · [示例](../examples/) |
| 查看功能验收或待办 | [专项验收](testing/) · [待办](../TODO.md) · [应用提案](application-ideas/README.md) |

## 代码地图

.NET 项目的解决方案入口为根目录的 [RelaxKonOS.sln](../RelaxKonOS.sln)。专项验证入口见开发调试指南，Android 构建入口见 Android 工程文档。

| 位置 | 职责 |
| --- | --- |
| [Client/](../Client/) | Avalonia 桌面外壳与内置应用、桌面启动项目、Android 工程 |
| [Framework/](../Framework/) | 基础类型、Shell 契约、窗口管理、UI、应用 SDK 与运行时 |
| [Shared/](../Shared/) | 客户端与服务端协议、受管运行时、服务端模块契约和打包 |
| [RelaxKonOS.Server/](../RelaxKonOS.Server/) | ASP.NET Core 宿主、认证、工作区及服务端能力 |
| [ServerModules/](../ServerModules/) · [RelaxKonOS.ServerModuleHost/](../RelaxKonOS.ServerModuleHost/) | 服务端功能模块及独立模块宿主 |
| [RelaxKonOS.PrivilegedHelper/](../RelaxKonOS.PrivilegedHelper/) · [RelaxKonOS.Guardian.Agent/](../RelaxKonOS.Guardian.Agent/) | 受限宿主特权操作与独立工作负载守护 |
| [Tests/](../Tests/) · [RelaxKonOS.Server.Tests/](../Tests/Server/RelaxKonOS.Server.Tests/) | 按领域划分的专项验证与服务端综合验证 |
| [Tools/](../Tools/) · [deployment/](../deployment/) · [examples/](../examples/) | 开发工具、构建发布与安装脚本、应用和部署示例 |

## 按领域查找

| 领域 | 主要入口 |
| --- | --- |
| [架构与协议](architecture/) | [REST 与 SignalR 契约](architecture/RelaxKonOS.Protocol.md) · [Workspace](architecture/RelaxKonOS.Workspace.md) · [应用激活](architecture/RelaxKonOS.ApplicationActivation.md) · [虚拟系统盘契约](architecture/RelaxKonOS.VirtualSystemDrive.Contracts.md) |
| 服务端模块 | [模块边界设计](architecture/RelaxKonOS.ServerModuleBoundaries.Design.md) · [进度与实机证据](architecture/RelaxKonOS.ServerModuleBoundaries.Progress.md) · [模块分发](architecture/RelaxKonOS.ServerModuleDistribution.Design.md) |
| [平台能力](platform/) | [认证](platform/RelaxKonOS.Authentication.md) · [登录](platform/RelaxKonOS.Login.md) · [登录别名设计](platform/RelaxKonOS.AliasLogin.Goal.md) · [安全](platform/RelaxKonOS.Security.md) · [存储](platform/RelaxKonOS.Storage.md) |
| 组件与安装 | [组件中心进度](platform/RelaxKonOS.ComponentCenter.Progress.md) · [受管运行时设计与补齐清单](platform/RelaxKonOS.ManagedRuntimes.Design.md) · [任务编排](platform/RelaxKonOS.TaskOrchestration.Design.md) · [容器应用进度](platform/RelaxKonOS.ContainerApplications.Progress.md) |
| [内置应用](applications/) | [文件管理器](applications/RelaxKonOS.Explorer.md) · [终端](applications/RelaxKonOS.Terminal.md) · [Git 客户端](applications/RelaxKonOS.GitClient.md) · [Docker 管理器](applications/RelaxKonOS.DockerManager.md) · [Tailscale 管理器](applications/RelaxKonOS.Tailscale.Design.md) · [代理运维](proxy/) |
| [宿主服务](services/) | [进程监督](services/ServerProcessSupervision.md) · [安装服务进度](services/RelaxKonOS.InstallationServices.Progress.md) · [用户模式进度](services/RelaxKonOS.UserModeServer.Progress.md) · [文件服务规格](services/file-services/RelaxKonOS.FileServices.Specification.md) |
| [开发规范与记录](development/) | [应用兼容性](development/RelaxKonOS.ApplicationCompatibility.md) · [代码库优化计划](architecture/RelaxKonOS.CodebaseOptimization.Design.md) · [阶段性优化与验收](development/RelaxKonOS.Optimization.Progress.md) |

功能范围、实现状态与实机验收结论由各领域文档维护。Android 专属内容由 [Android 文档目录](../Client/RelaxKonOS.Client.Android/docs/)维护，[移动端入口](mobile/README.md)提供跨仓库导航。
