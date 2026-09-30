# Android 文档

本目录是 Android 手机与平板的唯一详细文档源。先查 [实现进度](status/Progress.md) 了解已实现范围与下一项；测试进度、执行证据及未关闭检查看 [测试与验收](status/Verification.md)，新增功能看后续计划。实现和测试独立追踪，即使未执行测试，也可按实现依赖继续下一步。仓库级 [移动端入口](../../../docs/mobile/README.md) 仅链接到这里。

## 设计规范 `design/`

| 文档 | 内容 |
| --- | --- |
| [产品、架构与交互](design/Product.Design.md) | 原生平台边界、导航、手机/平板、自适应、三语、多主题与无障碍 |
| [Shell、认证与安全](design/Shell.Design.md) | 实际路由、导航栈、会话、提权与保险箱安全规则 |
| [登录与本地凭据](design/LoginCredentials.Design.md) | 身份键、决策表、保存/失效/删除、密码明文生命周期 |
| [Windows 设备密钥](design/OwnerDeviceKeys.Design.md) | 工作站配对、Keystore 签名登录与授权边界 |

## 当前功能 `features/`

| 文档 | 内容 |
| --- | --- |
| [服务器中心](features/ServerCenter.md) | SSH 信任、文件/终端、稳定身份与隧道、安装回执；首次安装仍缺执行链路 |
| [文件传输](features/FileTransfers.md) | 分块上传、源暂存、续传、前台通知与清理 |
| [应用部署与模板](features/ApplicationDeployments.md) | 四来源七步向导、日志、版本/回滚、可信动态模板 |
| [Docker 与 Compose](features/DockerCompose.md) | 资源归属、导入/预览、持久操作、部分失败和卷保护 |
| [网站发布](features/WebPublishing.md) | 诊断、确认式 HTTPS、发布恢复和访问观测 |
| [Git 编辑与构建](features/Git.md) | 受限文本编辑、固定 SHA、隔离构建与产物发布 |
| [终端、脚本与守护](features/TerminalAutomation.md) | Server/SSH 会话、输入/恢复、远端任务与工作负载 |
| [公共运行时安装](features/Installations.md) | 当前安装契约、包引用/上传、幂等提权、任务观察/取消与恢复边界 |
| [任务、告警与恢复](features/OperationsRecovery.md) | 领域操作索引、诊断、前台通知、定义备份与预检 |

## 后续计划 `plans/`

| 文档 | 内容 |
| --- | --- |
| [部署剩余工作](plans/Deployment.md) | 首次安装、模板更新、构建回收、数据恢复与后台通知的代码缺口 |
| [内置应用补齐](plans/BuiltInParity.md) | 桌面 25 个应用差异与 BP00–BP24；公共安装链路见功能说明；后续网络/Nginx/证书/FRP/Mihomo |

## 开发与发布 `development/`

| 文档 | 内容 |
| --- | --- |
| [构建、调试与发布](development/android-release.md) | 本地环境、签名机、APK/AAB、导入发布与渠道证书 |
| [Ubuntu Git 构建环境](development/GitBuild.Ubuntu.md) | rootless BuildKit 的宿主准备、配置与受限执行 |

## 状态与验收 `status/`

| 文档 | 内容 |
| --- | --- |
| [当前实现与进度](status/Progress.md) | 当前功能、代码缺口、BP 实现状态与下一项 |
| [测试进度与验收](status/Verification.md) | 独立 BP 测试进度、已有执行证据与设备/宿主/故障矩阵；保留 AD 验收 ID 与 Compose 部分通过事实 |

## 维护规则

规范写行为，功能说明写当前接入，计划只写未实现交付，Progress 记录实现状态与代码证据，Verification 独立记录测试进度、执行证据和未关闭检查。未测不阻止继续实现，不将已实现自动写成已验收。目标实现后移出计划；仅缺真机/宿主测试时将检查归入验收，不保留整份旧目标。历史流水账由 Git 保存，不建存档副本或旧路径跳转文件。共享 Protocol/Server 执行语义只链接仓库领域文档，不复制到 Android 文档。
