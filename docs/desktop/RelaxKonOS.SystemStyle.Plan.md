# RelaxKonOS 系统级桌面风格扩展计划

> **状态：规划，尚未实施。**
>
> 本文把当前的“桌面样式”从仅决定 Launcher / 任务栏 / Dock 布局的能力，扩展为一个可运行时切换、覆盖桌面与受管应用的**系统级风格（System Style）**能力。本文不授权实现；后续工作应以本文为实施基线，并在开始前复核当前代码。
>
> 相关现有文档：[`RelaxKonOS.Desktop.md`](./RelaxKonOS.Desktop.md)、[`RelaxKonOS.Theming.md`](./RelaxKonOS.Theming.md)、[`RelaxKonOS.ExternalShellPackages.md`](./RelaxKonOS.ExternalShellPackages.md)。

## 1. 目标与范围

用户可从 Windows-like、macOS-like、Ubuntu-like 及未来的 Windows 11-like 等系统风格中选择一项。切换后，无需重新启动客户端，风格应一致地影响以下**由 RelaxKonOS 控制**的界面，而不仅是桌面布局：

- 桌面工作区、面板 / 菜单栏、任务栏 / Dock、启动器、系统托盘、桌面图标及其状态；
- 桌面空白处和桌面图标的右键菜单；
- 受管窗口的外壳：活动/非活动边框、标题栏、高度、圆角、阴影、窗口控制按钮、最大化及全屏形态；
- 所有内置应用及 SDK 应用采用标准 Avalonia `ContextMenu` / `MenuItem` 时的应用内右键菜单；
- 系统级弹出层：开始菜单、快速设置、通知、文件选择/确认类系统对话框、模态遮罩；
- 桌面操作层，包括窗口概览 / 任务切换器（例如 `Win+Tab`）、应用切换、显示桌面及全屏窗口上的相应呈现。

体验上，风格决定“**部件如何排布、怎样有形、怎样运动**”；配色板与浅色/深色模式仍决定“**使用哪组颜色**”。二者不得再被混为一个下拉框或通过页面硬编码耦合。

### 1.1 非目标

- 不模仿或复制 Windows、macOS、Ubuntu 的商标、专有图标、系统字体、原生私有 API 或受版权保护的完整界面资产；名称仅表示交互与视觉方向。
- 不改变宿主操作系统的真实桌面、任务栏、Alt-Tab 或窗口装饰；RelaxKonOS 仅控制自身主窗口内的桌面。
- 不强行改变 `NativeWebView` 中网页、终端 ANSI 调色板、编辑器语法高亮或第三方原生对话框；这些可在后续提供“跟随系统风格”的独立选项。
- 不允许主题或风格包注入任意 AXAML、C#、程序集或资源 URI 到宿主全局样式树。外置 Shell 可以渲染它自己的界面，但不能获得修改内置应用/窗口管理器的任意代码执行入口。
- 本计划不实现虚拟桌面、窗口平铺/吸附、跨设备窗口迁移或真实系统通知服务；设计接口时为它们保留位置即可。

## 2. 现状调研

### 2.1 当前“桌面样式”是 Shell 替换，而非系统风格

当前设置中的 Windows-like、macOS-like、Ubuntu-like 是 `ShellSelectionDto.ShellId` 选择。`ShellCatalog` 注册三个内置 `IDesktopShell`，`ShellRuntime` 在切换时将同一批窗口、全屏窗口和覆盖层 surface 重新挂接到新 Shell；因此应用生命周期和 `WindowManager` 真相不会重建。实现位置为：

| 项目/位置 | 现有职责 | 结论 |
|---|---|---|
| `Framework/RelaxKonOS.Shell/ShellContracts.cs` | `IDesktopShell`、`ShellPresentationContext`、`ShellSurfaces`、窄 `IShellActions` 契约 | 提供了替换桌面布局的安全边界，但不含系统风格、菜单或窗口外壳契约。 |
| `Client/RelaxKonOS.Client/Services/ShellCatalog.cs` | 注册 `relaxkonos.windows-like`、`relaxkonos.macos-like`、`relaxkonos.ubuntu-like`；发现外置 `.roapp` Shell | 外置扩展是完整 Shell 包，并非可复用的全局视觉样式包。 |
| `Client/RelaxKonOS.Client/Services/ShellRuntime.cs` | 切换 Shell 并重挂 `WindowHost`、`FullScreenWindowHost`、`ShellOverlayHost`、`InputBackdrop` | 当前能迁移 surface，是新系统操作层的可用基础。 |
| `Client/RelaxKonOS.Client/Views/Shell/LauncherDesktopShells.cs` 与三个 `*ShellLayoutView.axaml` | 以 C# 构造 Windows / macOS / Ubuntu 的启动器、任务栏/Dock、菜单栏与桌面右键菜单，并仅在布局 AXAML 中定义几何 | 三套风格主要影响 Shell chrome；其内部又有大量独立颜色、尺寸、圆角。 |
| `Shared/RelaxKonOS.Protocol/Workspace/ShellSelectionDto.cs` | 保存 Shell ID 与本机解析所需的包标识/版本 | 当前选择意图可同步，但语义仅为“选哪个 Shell”。 |

`ShellCapabilities` 虽含 `ShellOverlays`，但它只描述 Shell 可以注册覆盖层 surface，不描述覆盖层的统一行为或样式。`DesktopShellOverlayService` 目前仅将若干桌面流程接到 `WindowManager.ShowShellDialogAsync`；尚未存在统一的窗口概览/任务切换器服务。

### 2.2 颜色主题已开始全局化，但 `StyleId` 尚未成为风格实现

现有 `ThemeService` 将 `ThemeKind` 和调色板解析到 `Application.Resources`，`TokenContract.axaml` 也已定义颜色、少量圆角、字体和窗口令牌。`ThemePreferencesDto` 有 `StyleId`，但客户端目前固定写入 `"relaxkonos"`，解析调色板时也没有读取该字段来选择控件模板或布局。因此它是未兑现的预留字段，不是当前桌面样式机制。

共享 `Styles.axaml` 与 `RemoteWindowTheme.axaml` 已能使用 `DynamicResource` 覆盖一部分通用控件和受管窗口，但仍有以下缺口：

- 共享样式没有定义 `ContextMenu`、`MenuItem`、子菜单、分隔线、菜单图标/快捷键栏等完整模板；桌面和应用内右键菜单多为直接构造的标准 Avalonia 菜单。
- `RemoteWindowTheme.axaml` 使用单一 42px 标题栏和固定 caption 按钮规则；窗口阴影仍直接写十六进制值，不能按风格切换。
- `MainWindow.axaml` 的宿主标题栏、连接栏和加载遮罩仍拥有局部样式与硬编码颜色；它不属于选定 Shell，因此切换桌面布局不会影响它。
- 内置 Shell 和 Windows 11 示例中仍可见硬编码颜色、圆角、阴影与图标处理。以当前工作区检索，客户端、窗口管理器和 Windows 11 示例 AXAML 中仍有 58 处十六进制颜色匹配；这说明仅替换布局不能形成完整系统风格。

### 2.3 右键菜单、窗口与任务切换的现状

- 内置 Shell 会为桌面空白处及桌面项创建 `ContextMenu`，macOS/Ubuntu 仅改变菜单命令的排列或文字；所有菜单仍沿用同一未风格化的通用控件呈现。
- 应用内至少有文件管理器、注册表、Git、终端等使用 `ContextMenu`（当前 AXAML 命中 7 个应用目录），无统一的产品菜单封装或风格参数。
- `WindowManager` 维护窗口、焦点、Z 顺序、最小化/最大化/全屏与模态链；`RemoteWindow` 提供拖动/八向 resize，`RemoteWindowTheme.axaml` 负责唯一窗口框架。这是应承载系统窗口外壳的权威层，不能让每个 Shell 私自重绘应用窗口。
- 已有 `FullScreenWindowHost` 与 `ShellOverlayHost`，但未有窗口概览模型、缩略图生命周期、`Win+Tab` 命令或任务切换覆盖层。Windows 11 示例把标作 Task View 的按钮绑定到 `ShowDesktopCommand`，说明它只是外观占位，而不是任务视图实现。

## 3. 目标架构：分离体验选择、系统风格与 Shell 布局

### 3.1 三个彼此独立的概念

| 层 | 决定什么 | 选择来源 | 示例 |
|---|---|---|---|
| 外观与调色板 | 浅/深/跟随系统、语义颜色、强调色 | `AppearancePreferences` | Dark + Nord |
| 系统风格 | 系统部件的形状、尺寸、材料、控件模板、动效、菜单和窗口 chrome | `SystemStyleId` | `relaxkonos.windows-like` |
| 桌面 Shell | 桌面布局及启动器/Dock/菜单栏的具体实现 | `ShellSelection` | 内置 Ubuntu Shell 或外置 Windows 11 Shell |

默认映射可令内置 Windows Shell 配套 Windows-like 系统风格、macOS Shell 配套 macOS-like 系统风格、Ubuntu Shell 配套 Ubuntu-like 系统风格；但映射必须只是设置页面的便利操作，不能使两者成为同一个数据字段。用户可明确选择“Ubuntu 布局 + Windows-like 窗口与菜单”，外置 Shell 也不能未经确认修改用户的全局系统风格。

### 3.2 建议的运行时图

```text
WorkspacePreferences
  ├─ AppearancePreferences ──┐
  ├─ SystemStyleId ──────────┼─> AppearanceService
  └─ ShellSelection ─────────┘       │ 原子替换：调色板 + 风格资源
                                      v
                 Application resources / global control templates
                 ┌──────────────┼─────────────────────┐
                 v              v                     v
        MainWindow + menus  RemoteWindow        内置/SDK 应用控件
                 │              │                     │
                 └────── SystemUiCoordinator ────────┘
                                      │
                         ShellOverlayHost / InputBackdrop
                                      │
                     Window overview, app switcher, dialogs,
                       quick settings, notification surfaces
```

`AppearanceService` 是现有 `ThemeService` 的替代者：它在 UI 线程上验证并一次性替换完整的资源提供者，先应用 `ThemeVariant`，再组合调色板令牌与系统风格令牌/样式。若候选风格不可用，保留上一次已验证的风格，不清空现有资源。

`SystemUiCoordinator` 是 Client 端受信任单例，消费 `IWindowManager` 的窗口/焦点事件及当前 `ShellSurfaces`，控制唯一的系统操作层。Shell 只提供 surface 和可选布局锚点，不拥有窗口切换、系统菜单或窗口外壳的业务真相。

### 3.3 推荐模块边界

```text
Shared/RelaxKonOS.Protocol
  AppearancePreferencesDto, SystemStyleSelectionDto, SystemStyleManifestDto
Framework/RelaxKonOS.UI
  SystemStyle token contract、global control/menu styles、内置风格 recipes
Framework/RelaxKonOS.WindowManager
  Window presentation state、overview-safe window projection（不依赖 Avalonia Shell）
Framework/RelaxKonOS.Shell
  Shell layout contract、system-overlay anchor / capability contract
Client/RelaxKonOS.Client
  AppearanceService、SystemStyleRegistry、SystemUiCoordinator、设置页面和内置 recipes
examples/Windows11DesktopShell
  外置 Shell + data-only Windows 11 风格描述与兼容性演示
```

`Core` 保持无 Avalonia 依赖；只有可序列化选择与窗口状态可以进入 Core/Protocol。窗口视觉模板、Avalonia `Control`、资源字典、截图/缩略图与快捷键路由不得进入 Core 或外置 Shell 的写入权限范围。

## 4. 风格令牌、组件覆盖与应用约定

### 4.1 两层令牌契约

现有语义颜色令牌继续作为第一层，例如 `Surface`、`TextPrimary`、`Accent`、`DialogScrim`、`WindowFrameBackground`。新增的系统风格不得让业务页面引用 `WindowsBlue`、`MacTrafficLightRed` 等品牌/实现名；应增加下列第二层**语义化形态令牌**：

| 类别 | 建议令牌（示例） | 消费者 |
|---|---|---|
| 窗口 | `WindowTitleBarHeight`、`WindowFrameThickness`、`WindowCornerRadius`、`WindowShadow`、`WindowControlWidth`、`WindowControlOrder`、`WindowInactiveOpacity` | `RemoteWindowTheme`、宿主标题栏 |
| 菜单 | `MenuBackground`、`MenuBorderBrush`、`MenuCornerRadius`、`MenuPadding`、`MenuItemHeight`、`MenuItemHover`、`MenuSeparatorBrush`、`MenuSubmenuDelay` | `ContextMenu`、`MenuItem`、桌面/应用右键菜单 |
| 浮层与对话框 | `FlyoutCornerRadius`、`FlyoutElevation`、`OverlayScrim`、`DialogCornerRadius`、`DialogMotion` | Start、快速设置、模态、通知 |
| 桌面 chrome | `TaskbarHeight`、`TaskbarAlignment`、`DockMagnification`、`TopBarHeight`、`LauncherCornerRadius`、`DesktopIconGrid` | 内置/外置 Shell，以及 host 提供的锚点 |
| 窗口概览 | `OverviewScrim`、`OverviewCardRadius`、`OverviewCardBorder`、`OverviewThumbnailScale`、`OverviewEnterMotion`、`OverviewSelectionRing` | `SystemUiCoordinator` |
| 输入与可访问性 | `FocusRingThickness`、`FocusRingBrush`、`ReducedMotionDuration`、`MinimumHitTarget` | 所有共享控件 |

每个可以在运行时切换的令牌必须使用 `{DynamicResource ...}`。阴影、圆角、厚度、动画持续时间也应作为资源，不得在样式外散落常量。`StyleId` 不覆盖调色板的颜色来源；二者组合后才能输出最终 `Brush`、尺寸及模板参数。

### 4.2 固定宿主模板 + 有限 recipe，而不是任意样式注入

每一个全局组件在 `RelaxKonOS.UI` 保持由宿主审查的基础模板，风格通过有限 `SystemStyleRecipe` 选择器调整：

- `WindowChromeRecipe`: `caption-buttons-right`、`traffic-lights-left`、`headerbar-right`；
- `ContextMenuRecipe`: `compact-command-menu`、`rounded-command-menu`、`gnome-popover-menu`；
- `TaskSwitcherRecipe`: `windows-grid`、`macos-strip`、`gnome-overview`；
- `ShellChromeRecipe`: `bottom-taskbar`、`top-menu-plus-dock`、`top-bar-plus-left-dock`。

每个 recipe 只能从预定义枚举和 token 键选择，不能携带 XAML、程序集类型名、任意资源 URI 或事件处理器。`SystemStyleRegistry` 对内置 recipe 进行完整性校验：所有全局组件必须有覆盖策略，所有引用 token 必须存在，所有最小命中区、焦点可见性、对比度与减少动态效果规则必须通过。

该模式既能让 Windows、macOS、Ubuntu 产生真正不同的标题栏/菜单/概览体验，又保持内置应用和外置应用的视觉一致与安全边界。

### 4.3 应用与 SDK 的消费规则

1. 内置应用和 SDK 应用继续创建标准 `ContextMenu` / `MenuItem`；共享全局样式负责基础外观，因而现有应用内右键菜单自动获得风格，但前提是其页面没有局部模板或硬编码画刷覆盖。
2. 产品代码不得为某种系统风格判断 `SystemStyleId` 后拼装颜色/圆角。需要特殊组件时，使用 `SystemUi` 提供的受限工厂或语义 class，例如 `menu-command`、`menu-danger`、`window-toolbar`，由 recipe 决定外观。
3. 应用可声明语义需求（例如“这是一组 destructive commands”），不能声明“我要 macOS 菜单”或修改窗口控制按钮位置。窗口 chrome 始终由 `WindowManager` / `RemoteWindow` 决定。
4. 迁移所有 AXAML 和 C# 构造 UI：`Background`、`Foreground`、边框、阴影、圆角、选中/悬停/禁用状态，均替换为 token 或共享 class；业务视图禁止新增十六进制颜色。
5. 原生控件和 WebView 只主题化 RelaxKonOS 宿主区域；无法适配的内容需要明确降级说明，不能伪装为已覆盖。

## 5. 接口、配置与扩展包演进

### 5.1 目标配置模型

建议将现有分散的 `Theme`、`ThemePreferences.StyleId`、`ShellSelection` 收敛为下列现行协议模型（字段名可在实施前小幅调整，但职责不应重叠）：

```text
WorkspacePreferencesDto
└── DesktopExperience: DesktopExperiencePreferencesDto
    ├── Appearance: AppearancePreferencesDto
    │   ├── Mode: Light | Dark | System
    │   ├── PaletteId: builtin:* | custom:*
    │   ├── AccentOverride: #RRGGBB?
    │   └── CustomPalettes: ThemePaletteDto[]
    ├── SystemStyleId: relaxkonos.windows-like
    └── Shell: ShellSelectionDto
        ├── ShellId
        ├── PackageId?
        └── PackageVersion?
```

`SystemStyleId` 是 Workspace 同步的用户意图；可解析性则是设备本地事实。启动时，`SystemStyleRegistry` 检查本机是否有此受信任风格定义。内置风格不可用属于产品安装损坏，应使用明确的恢复/诊断流程，而不是悄悄改写用户偏好。外置风格包缺失时，保留选择意图，并让设置页显示“此设备未安装”；仅在当前会话使用最近有效的已加载风格以保证可渲染。

### 5.2 不保留兼容层的迁移要求

仓库的 API 演进政策要求首个正式发布前直接采用新接口。因此实施此计划时必须：

- 删除旧的顶层 `Theme`、`ThemePreferences`、`Shell` 读取/写入路径和 `ThemePreferencesDto.StyleId`，以 `DesktopExperience` 作为唯一来源；不得保留别名、可选旧字段、双读双写、旧 JSON 回退或路由兼容层。
- 同一次变更更新 Client、Server、Protocol、偏好持久化、设置 UI、测试、示例和所有文档；既有工作区/开发数据库可按开发阶段约定重置或由明确的一次性部署迁移处理，但运行时协议不解析两种格式。
- 让当前 `ThemeService` 由新的 `AppearanceService` 取代，而不是保留两个服务互相转发；所有调用方只调用新服务。
- 版本化 `ShellApi` 与风格清单的当前契约，并让 `ShellCatalog` / 包验证一次只接受该契约版本。不要为旧 Windows 11 示例或旧桌面包保留适配器。

### 5.3 风格描述与外置扩展

新增 data-only `SystemStyleManifestDto`，由已安装包的清单或宿主内置资源提供，建议包含：

```json
{
  "schemaVersion": 1,
  "id": "com.example.windows11-style",
  "displayName": "Windows 11-like",
  "supportedRecipes": {
    "windowChrome": "caption-buttons-right",
    "contextMenu": "rounded-command-menu",
    "taskSwitcher": "windows-grid",
    "shellChrome": "bottom-taskbar"
  },
  "tokens": {
    "WindowTitleBarHeight": 40,
    "WindowCornerRadius": 8,
    "MenuCornerRadius": 8,
    "TaskbarHeight": 52
  },
  "minimumHostApiVersion": "2.0"
}
```

此 JSON 仅是示意，最终清单应同时定义 light/dark 可用 token、可访问性元数据、包签名/来源和允许的 recipe 枚举。不能通过它提供颜色以外的任意代码或全局 XAML。需要自定义桌面布局时，外置包仍实现 `IDesktopShell`；需要影响全局窗口/菜单/概览时，只能提供并通过校验的 style manifest。选择 Shell 与选择其 companion style 必须是两个可撤销操作，设置页可提供“采用此 Shell 推荐的系统风格”按钮，但不自动隐式修改。

## 6. Windows 11-like 扩展示例

现有 `examples/Windows11DesktopShell` 应从“独立的视觉演示”升级为首个端到端兼容样例，而不成为 host 风格的特例。

| 区域 | 样例应演示的目标行为 |
|---|---|
| Shell | 居中任务栏、开始菜单、快速设置、桌面快捷方式与推荐的 `bottom-taskbar` recipe；Shell 仍负责自身布局。 |
| 右键菜单 | 紧凑圆角命令菜单、标准命令顺序、图标/快捷键列、危险操作语义；同一 `ContextMenu` recipe 同时作用于 Explorer、Registry、Terminal 等应用菜单。 |
| 窗口 chrome | 圆角、细边框、透明/材料感（受平台能力和减少动态效果设置限制）、右侧最小化/最大化/关闭按钮，以及活动窗口强调。 |
| 任务视图 | `Win+Tab` 打开 `windows-grid` 概览；卡片展示窗口标题、图标与可获得的缩略图，点击激活，`Esc` 关闭，关闭按钮走 `IWindowManager.Close`。 |
| 全屏 | 全屏应用位于 `FullScreenWindowHost`；任务视图和安全系统对话框位于更高的 `ShellOverlayHost`，且不会因任务栏而裁切。 |
| 可访问性 | 键盘焦点顺序、可见 focus ring、屏幕阅读器名称、最小 40px 目标、减少动态效果时没有缩放/淡入依赖。 |

应把示例现有的硬编码白色/蓝色、圆角和阴影替换为 style token，保留其自有图标几何与本地化资源。这样它验证的不是“另画一套桌面”，而是“一个外置 Shell 能同宿主全局系统风格一起工作”。

## 7. 系统操作层与窗口概览设计

### 7.1 新的只读窗口投影与命令

`WindowManager` 是窗口状态唯一权威。它应提供不暴露可变 `ManagedWindow` 的 `WindowOverviewItem` 投影：`WindowId`、应用 ID、标题、图标、是否活动、窗口状态、是否可关闭、缩略图可用性。`SystemUiCoordinator` 订阅窗口打开/关闭/焦点/状态变更，建立概览排序与选择状态。

命令入口应为 host 受控的 `IWindowOverviewController`：

- `ShowOverview()` / `HideOverview()` / `ToggleOverview()`；
- `MoveSelection(direction)`、`ActivateSelection()`、`CloseSelection()`；
- `CycleApplication(direction)` 供更轻量的应用切换；
- 宿主 `MainWindow` 在应用未处理后捕获 `Win+Tab`、`Alt+Tab`、`Esc`，并通过统一命令路由处理，不让 Shell 或某个应用自行截获全局快捷键。

v1 可使用窗口内容的受控视觉快照或占位缩略图；不能为获得缩略图而重建应用、泄漏隐藏窗口、绕过 WebView 安全限制或阻塞 UI 线程。截图无法取得时，使用图标 + 标题卡片。

### 7.2 层级与输入规则

```text
MainWindow
  ├─ 选定 Shell 的普通 chrome / WindowHost
  ├─ FullScreenWindowHost                 # 全屏应用
  ├─ ShellOverlayHost                     # 任务概览、系统 flyout、系统对话框
  └─ InputBackdrop                        # 仅在模态/概览期间拦截下层输入
```

概览打开时：隐藏或停用会抢输入的 Shell flyout；`InputBackdrop` 拦截原桌面和窗口输入；概览本身按 `SystemStyleRecipe` 渲染；选择/关闭后恢复之前活动窗口及焦点。普通应用模态、Shell 模态和安全系统模态的现有优先级须被保留：安全系统模态高于概览，概览高于普通 desktop chrome，应用模态仅阻塞其 owner。

## 8. 分阶段实施计划

每个阶段结束均应保持可编译、可运行、可切换；不要先大规模复制风格页面或只完成某一个 Shell。

### Phase 0 — 契约冻结与基线

1. 盘点所有硬编码颜色、形态、窗口外壳与菜单模板；分别列出 Client、WindowManager、示例和内置应用。
2. 确认最终 `DesktopExperiencePreferencesDto`、style manifest、`ShellApi` 版本及可访问性基线；删除旧接口，而不是并行支持。
3. 为资源 token 完整性、禁止产品 UI 新增十六进制颜色、菜单/窗口 recipe 覆盖率建立测试与 CI 规则。

### Phase 1 — 资源与选择链路

1. 建立 `SystemStyleRegistry`、data-only manifest 验证器、`AppearanceService` 和原子资源替换逻辑。
2. 将现有 `ThemeService`、`ThemePreferencesDto.StyleId` 和顶层偏好字段迁移到新唯一模型；更新 Server/Client/测试/文档/示例。
3. 实现三个内置 profile：Windows-like、macOS-like、Ubuntu-like；先确保它们完整提供 token 和合法 recipe。
4. 设置中心拆成“颜色与模式”“系统风格”“桌面布局/Shell”三项，可预览、保存、失败提示和恢复。

### Phase 2 — 通用系统组件

1. 在 `RelaxKonOS.UI` 提供 `ContextMenu`、`MenuItem`、Separator、子菜单、ToolTip、Flyout、Dialog、基础控件的统一模板与 recipe selectors。
2. 迁移 `MainWindow`、连接栏、加载层、`RemoteWindowTheme`、`ModalBlocker`、桌面 display dialogs；使活动/非活动/全屏/无阴影均由 token 驱动。
3. 将 C# 动态创建的 Shell/应用控件改为动态资源或共享 semantic class，并删除本地颜色/圆角/阴影常量。

### Phase 3 — 桌面与应用覆盖

1. 为 Windows/macOS/Ubuntu 内置 Shell 接入其配套 `ShellChromeRecipe`；保持命令模型和 window surface 不变。
2. 迁移桌面空白处、图标、任务栏组、启动器、快速设置等右键菜单/弹出层。
3. 批量迁移所有内置应用的 `ContextMenu` 与局部覆盖；对不遵循共享控件的自定义菜单列出明确改造项。
4. 将 Windows 11 示例转为使用 manifest、token 和标准系统菜单，作为外置 Shell 兼容测试资产。

### Phase 4 — 系统操作层

1. 在 `WindowManager` 增加窗口概览投影和稳定事件；实现 `SystemUiCoordinator`、概览命令和快捷键路由。
2. 为三个内置 recipe 实现 Windows grid、macOS strip、GNOME overview；先支持窗口选择、关闭、Esc 退出与焦点恢复。
3. 支持全屏、模态和窗口切换期间的层级/输入规则；在缩略图不可用时验证卡片降级。

### Phase 5 — 质量、外部扩展与发布门槛

1. 完成 manifest 签名/来源、兼容性及失败诊断；外置包只接受当前 API 版本。
2. 在高 DPI、窄窗口、触摸、键盘、三平台、浅/深/System、低性能和减少动态效果条件下进行视觉回归。
3. 仅在 Windows 11 示例与三个内置风格均覆盖菜单、窗口、概览与全屏场景后，开放第三方 style manifest。

## 9. 验收标准

### 功能一致性

- 在设置页切换任一内置系统风格，Shell、宿主标题栏、受管窗口、桌面/应用右键菜单、模态和系统操作层均在当前会话更新；窗口、应用状态与 Workspace 不丢失。
- 选择颜色/深浅模式时只改变调色板输出，不意外改变菜单布局或窗口控制按钮位置；选择系统风格时可改变形态/布局，但不篡改用户的调色板。
- Windows-like、macOS-like、Ubuntu-like 至少在 Shell chrome、窗口 chrome、ContextMenu 和任务概览上具有可测试的 recipe 差异；差异不只体现在任务栏位置。
- 任意内置应用使用标准 `ContextMenu` 时获得当前风格；桌面菜单与应用菜单共享相同的命令项状态、键盘导航、焦点和禁用/危险语义。
- `Win+Tab` 概览可在普通窗口、多个最小化窗口、全屏窗口和 Shell 切换后稳定打开/退出；激活、关闭、Esc、焦点恢复及模态拦截均正确。

### 质量与安全

- 每个已登记 style profile 的 token/recipe 完整性在加载前校验；失败不会产生半套资源或透明/无文本 UI。
- 所有可键盘到达的菜单、窗口控制和概览项目具有可见焦点、可读名称和 4.5:1 正常文本对比度；关键目标满足设定的最小命中尺寸。
- 系统风格切换、Shell 切换与概览打开/关闭不泄漏 Control、事件订阅、窗口 host 或 collectable `AssemblyLoadContext`。
- 外置 style manifest 不能携带代码/XAML；版本不匹配、签名/校验失败、recipe 非法或 token 缺失时被拒绝并给出可诊断提示。
- 仓库中不再有旧偏好字段、旧 API 别名、双格式 JSON 解析或仅为兼容而存在的 adapter；所有 callers、测试、示例、文档使用新接口。

## 10. 风险与待决策项

| 项目 | 风险/问题 | 实施前需作出的决定 |
|---|---|---|
| 风格与 Shell 的关系 | 两者合并会限制“混搭”，完全分离又增加设置复杂度 | 采用分离存储；确认设置页面是否默认联动并如何向用户解释。 |
| 外置全局风格 | 允许任意 AXAML 会破坏安全、性能与一致性 | v1 仅 data-only manifest + host recipe；确定签名与包信任策略。 |
| 任务概览缩略图 | WebView、原生子窗口和高频抓取可能失败或昂贵 | 决定 v1 使用实时快照、延迟快照还是统一图标卡片回退；定义隐私遮挡规则。 |
| macOS-like 窗口控制 | 左侧 traffic-light 布局与现有 `RemoteWindow` 模板结构不同 | 以 recipe 改变宿主模板，不暴露给应用；确认关闭/最小化/最大化的可访问名称。 |
| 透明/模糊材料 | Avalonia 与 Linux 图形栈对 blur 支持不一致 | 定义功能检测与不透明 surface 回退，不能因材料不可用改变信息层级。 |
| 快捷键冲突 | 宿主、远程应用、浏览器和系统会竞争 `Win+Tab` / `Alt+Tab` | 明确 RelaxKonOS 主窗口内的优先级和平台无法截获时的可发现 UI 入口。 |
| 配置破坏性升级 | 本仓库政策禁止保留兼容层 | 在开发期一次性升级协议与测试数据；确认部署/开发数据库重置或单次离线迁移方案。 |
| 视觉回归成本 | 多 profile × 浅深模式 × Shell × 应用菜单组合数量大 | 选择基准截图矩阵与自动化 UI 覆盖范围，先保证共享模板，再迁移局部例外。 |

## 11. 结论

应把现有 Shell 选择保留为“桌面布局扩展点”，同时引入受信任、数据驱动、全局生效的 `SystemStyle` 层。以统一 token、受限 recipe、共享菜单/窗口模板和 host 控制的系统操作层为核心，才能让 Windows-like、macOS-like、Ubuntu-like 及 Windows 11-like 的差异穿透到右键菜单、窗口外壳、全屏与任务切换，而不让每个应用或外置 Shell 各自复制一套不可维护的样式代码。
