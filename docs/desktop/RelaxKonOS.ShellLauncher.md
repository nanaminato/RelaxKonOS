# RelaxKonOS 桌面 Shell 与启动器设计

> 当前实现：内置 Windows-like、macOS-like、Ubuntu-like 布局及外部桌面包。接口以 `Framework/RelaxKonOS.Shell/ShellContracts.cs` 为准，当前 Shell API 为 1.1。

## 1. 职责与边界

Shell 渲染桌面、启动器、任务栏/Dock、菜单栏及布局相关弹层，提供应用启动、窗口切换和桌面文件操作入口。应用生命周期归 `ApplicationManager`，窗口、焦点、层级和模态链归 `IWindowManager`，桌面状态由客户端领域服务投影。Shell 不替换宿主操作系统的桌面。

| 组件 | 职责 |
|---|---|
| `MainWindow` | 宿主窗口、连接栏、全屏、系统操作层与退出 |
| `ShellCatalog` | 内置与外置描述器、包发现、可用性和工厂 |
| `ShellRuntime` | 串行切换、生命周期、窗口重新挂载、故障回退 |
| `IDesktopShell` | 描述器、根视图、初始化/激活/停用/释放 |
| `ShellPresentationContext` | 状态、受控操作、覆盖层、承载面与本地化 |
| `ShellStateStore` | 客户端发布的只读状态投影与变化通知 |
| `IShellActions` | 通过稳定应用/窗口/桌面项 ID 请求操作 |

## 2. 窗口承载与切换

Shell 通过 `IShellSurfaceRegistry` 登记 `ShellSurfaces`：普通窗口 `WindowHost`、全屏窗口 `FullScreenWindowHost`、Shell 覆盖层 `ShellOverlayHost` 和桌面输入背景 `InputBackdrop`。工作区变化交给客户端协调窗口边界；任务栏和 Dock 不得遮挡最大化窗口。

切换流程由 `ShellRuntime` 串行执行：解析候选、初始化和验证承载面，保存原承载面，停用当前 Shell，挂载候选并激活。失败时恢复原承载与当前桌面；候选初始化、停用或释放的异常不得导致应用窗口丢失。切换迁移现有窗口视觉对象，不重新启动应用或重置 WindowManager。

桌面图标、运行应用、窗口组和本地化由同一状态适配层提供。桌面名称采用主题解析后的前景色；有图片图标时优先使用图片，没有图片时再使用 `IconGlyph`。窗口概览与系统对话框复用宿主协调器和 WindowManager。

## 3. 偏好与设备可用性

当前选择位于 `WorkspacePreferencesDto.DesktopExperience.Shell`，使用 `ShellSelectionDto`。内置选择仅保存 `shellId`；外置选择包含包 ID 和版本。设备本地解析由 `ShellPreferenceStore` 维护，内置实现版本不属于外置包元数据。

跨设备同步的是用户选择意图。外置包在当前设备不可用时，本机使用内置默认桌面并展示原因，保留服务端选择；只有用户主动选择才更新 Workspace 偏好。不保留旧顶层字段、旧 ID 映射或双格式解析。

系统风格与 Shell 布局分别存储，切换布局不隐式修改全局系统风格。见 [系统风格](RelaxKonOS.SystemStyle.md) 和 [设置](RelaxKonOS.Settings.md)。

## 4. 外部包

外部 Shell 使用 `.roapp`，通过应用安装器安装到版本化包目录。清单、路径、包类型、权限模型、Shell API 和能力组合由安装器与目录校验；仅在选择该 Shell 后加载程序集。Workspace 偏好不能指定任意 DLL/资源 URL，也不触发登录时自动下载和执行。

外部 Shell 仅使用公开 Shell 契约，不取得客户端服务容器、认证令牌、任意文件或网络能力。包制作、示例、运行时限制和卸载行为见 [外部桌面包](RelaxKonOS.ExternalShellPackages.md)。

## 5. 验证边界

| 场景 | 验收预期 |
|---|---|
| 切换三种内置布局 | 桌面、启动器和运行应用栏随布局改变 |
| 多窗口、最小化窗口和模态链 | 窗口数量、内容、焦点和遮罩关系保持 |
| 最大化、全屏、不同位置的常驻栏 | 受管窗口遵守工作区与系统层级 |
| 外部包缺失、初始化失败或被卸载 | 保留选择意图，当前桌面可用，现有窗口存活 |
| 语言、配色与系统风格变化 | 标签、图片图标和控件即时更新 |
| 高 DPI、窄窗口、拖放与自动隐藏 | 按目标平台实测，不由构建结果推定 |

窗口预览、真实像素与资源生命周期的自动检查及人工验收范围见 [桌面实现](RelaxKonOS.Desktop.md)。包验证见外部包文档。本文不把尚未执行的跨平台人工检查记为通过。
