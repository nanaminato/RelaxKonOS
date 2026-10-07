# Android 文档

本目录是 Android 手机与平板的唯一详细文档源，描述产品规则、当前功能、支持范围、构建发布与验证要求。仓库级 [移动端入口](../../../docs/mobile/README.md) 仅链接到这里。

## 设计规范 `design/`

| 文档 | 内容 |
| --- | --- |
| [产品、架构与交互](design/Product.Design.md) | 原生架构、导航、自适应布局、多语言、主题与无障碍 |
| [Shell、认证与安全](design/Shell.Design.md) | 路由、返回栈、会话、提权与凭据安全 |
| [登录与本地凭据](design/LoginCredentials.Design.md) | 身份识别、凭据保存与失效、密码生命周期 |
| [移动端包方案](design/ApplicationPackages.Design.md) | 包类型、权限、版本、更新与移除，以及桌面包的支持范围 |
| [支持范围](design/SupportScope.md) | 应用组织、部署范围、平台限制与验证要求 |
| [Windows 设备密钥](design/OwnerDeviceKeys.Design.md) | 工作站配对、Keystore 签名登录与授权边界 |

## 当前功能 `features/`

| 文档 | 内容 |
| --- | --- |
| [应用内功能导航](features/ApplicationNavigation.md) | 管理应用的分类、返回行为、状态保留与操作范围 |
| [服务器中心](features/ServerCenter.md) | SSH 信任、文件与终端、隧道、安装及结果核实 |
| [登录页 SSH 隧道](features/LoginSshTunnel.md) | SSH 认证、登录、连接配置、会话与证书信任 |
| [文件与 Git 共用编辑器](features/TextEditor.md) | 编码与换行、查找替换、条件保存、冲突与离页保护 |
| [使用记忆](features/Settings.md#使用记忆) | 提权用户名、文件选择位置、本地隔离与清理 |
| [文件与图片](features/Files.md) | 浏览、多选、复制移动、属性与权限、图片预览 |
| [文件传输](features/FileTransfers.md) | 分块上传、源文件暂存、断点续传、后台传输与清理 |
| [应用部署与模板](features/ApplicationDeployments.md) | 部署向导、定义编辑、日志、版本、回滚与模板 |
| [宿主出站代理](features/OutboundProxy.md) | 宿主代理设置、作用范围、重启与状态核实 |
| [Docker 引擎与镜像源](features/DockerEngine.md) | 引擎安装与维护、镜像源管理、任务恢复与结果核实 |
| [Docker 资源](features/DockerResources.md) | 容器、镜像、网络和卷的管理、归属保护与操作限制 |
| [Docker 与 Compose](features/DockerCompose.md) | 资源归属、配置导入、部署预览、操作恢复与卷保护 |
| [Nginx 管理](features/Nginx.md) | 安装来源、实例发现与接管、维护、卸载与恢复 |
| [站点管理](features/WebSites.md) | 静态站点、SPA、反向代理、版本冲突与提交结果核实 |
| [证书管理](features/Certificates.md) | ACME 与自签名证书、续期、部署与任务恢复 |
| [Mihomo 代理管理器](features/Proxy.md) | 运行时安装、配置、订阅、节点与操作恢复 |
| [SMB 文件服务](features/Smb.md) | 安装、共享、权限、凭据与操作结果核实 |
| [宿主防火墙](features/Firewall.md) | 状态、默认策略、规则、提权与变更结果核实 |
| [FRP 隧道与运行时](features/Tunnels.md) | 运行时安装、隧道与服务端配置、维护、审计与恢复 |
| [网站发布](features/WebPublishing.md) | 诊断、HTTPS 确认、发布恢复与访问检查 |
| [Git 工作区与构建](features/Git.md) | 分支、提交、差异、冲突、Git 安装、隔离构建与发布 |
| [终端、脚本与守护](features/TerminalAutomation.md) | Server 与 SSH 会话、终端布局、脚本与远端任务 |
| [进程守护](features/Guardian.md) | 工作负载定义、运行身份、健康检查、状态与日志 |
| [任务管理与监控](features/TaskManager.md) | 实时指标、趋势、进程管理、排序、分页与降级显示 |
| [设置与应用管理](features/Settings.md) | 宿主设置、Android 应用管理与本地、远端操作范围 |
| [帮助与引导](features/Help.md) | 帮助入口、多语言说明、可用功能与现有指南 |
| [服务访问](features/ServiceAccess.md) | 站点、部署与 SSH 地址、外部应用、凭据与证书安全 |
| [公共运行时安装](features/Installations.md) | 包来源、上传、提权、任务观察、取消与恢复 |
| [任务、告警与恢复](features/OperationsRecovery.md) | 操作记录、诊断、通知、定义备份与恢复预检 |

## 开发与发布 `development/`

| 文档 | 内容 |
| --- | --- |
| [构建、调试与发布](development/android-release.md) | 本地环境、签名机、APK/AAB、导入发布与渠道证书 |
| [Ubuntu Git 构建环境](development/GitBuild.Ubuntu.md) | rootless BuildKit 的宿主准备、配置与受限执行 |
| [验证要求](development/Verification.md) | 设备、宿主、文件传输及故障场景的检查与预期行为 |

## 维护规则

设计文档描述当前规则，功能文档描述实际入口、交互、授权和错误恢复，支持范围明确当前边界，开发文档描述构建、发布及验证方法。不维护目标表、阶段编号、推进顺序、完成率或测试执行流水账。功能变化时同步修改对应说明，历史由 Git 保存。共享 Protocol/Server 语义链接仓库领域文档，不复制 Android 详细文档到仓库级目录。
