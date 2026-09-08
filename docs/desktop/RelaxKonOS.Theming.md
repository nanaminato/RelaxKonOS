# RelaxKonOS 主题与配色系统设计

> **状态：实施前设计 / 后续主题化工作的唯一执行规范。** 本文把现有零散的浅色样式与仅影响 Shell 局部的 `Light / Dark / System` 偏好，升级为可运行时切换、可同步、可扩展的全局主题系统。
>
> - 设置中心和 Workspace 偏好模型见 [`RelaxKonOS.Settings.md`](./RelaxKonOS.Settings.md)。
> - 桌面外壳和窗口系统见 [`RelaxKonOS.Desktop.md`](./RelaxKonOS.Desktop.md)。
> - 当前共享样式入口是 [`Styles.axaml`](../../Framework/RelaxKonOS.UI/Themes/Styles.axaml)，应用入口是 [`App.axaml`](../../Client/RelaxKonOS.Client/App.axaml)。

---

## 1. 目标与边界

RelaxKonOS 需要让用户在不重启应用的情况下改变整个桌面、窗口、内置应用和对话框的外观；登录同一 Workspace 的设备应得到同一偏好。实现必须以 Avalonia 的资源系统为基础，而不是为每个页面分别维护浅色和深色版本。

本系统将外观拆成三个互不混淆的概念：

| 层 | 决定什么 | v1 决策 |
|---|---|---|
| **外观模式**（Appearance mode） | 浅色、深色，或跟随本机系统 | `Light` / `Dark` / `System`，沿用并扩展现有 `ThemeKind` |
| **视觉样式**（Visual style） | 控件形状、圆角、间距、字体、阴影、控件模板与动画 | v1 固定为 `relaxkonos`；预留 `StyleId`，不在首期实现 Fluent / Compact 等第二套控件模板 |
| **调色板**（Palette） | 语义颜色及其派生状态色 | 内置 RelaxKonOS Blue、Nord、Catppuccin；支持用户导入的 JSON 调色板与单独强调色覆盖 |

换言之：**视觉样式决定“长什么样”，调色板决定“用什么颜色”，外观模式决定选取浅色或深色变体。**

### 1.1 v1 必须完成

- 所有 RelaxKonOS 自有 Avalonia UI（Shell、窗口管理器、登录、设置、内置应用、对话框、示例应用）使用语义资源，不再在 AXAML 或 C# UI 构造代码中硬编码产品颜色。
- `Light`、`Dark`、`System` 真正切换整个应用，而不只是任务栏和开始菜单。
- 内置调色板可立即应用；用户可以以安全的 JSON 数据格式导入、导出、创建、编辑、删除自己的调色板。
- 选择同步到当前 Workspace；无效、旧版或缺失的调色板永远回退到可渲染的内置 RelaxKonOS Blue。
- 主题切换无需重启、不产生未处理异常，也不丢失当前窗口与应用状态。

### 1.2 明确不属于 v1

- 不允许主题包提供或动态加载 `.axaml`、C#、程序集、字体或任意资源 URI。
- 不将终端 ANSI 配色、代码编辑器语法高亮、网页内容配色强行改为桌面调色板；它们是应用特定设置。后续可让它们提供“跟随桌面”的可选模式。
- `NativeWebView` 内网页及操作系统原生标题栏不保证可主题化；RelaxKonOS 自己绘制的宿主区域必须主题化。
- 不在首期提供第二个完整的控件形状体系。增加 `StyleId` 只是避免未来破坏存储模型。

---

## 2. 当前基线与迁移结论

当前项目已具备以下基础：

- `ThemeKind` 已定义 `Light`、`Dark`、`System`，并存储于 `WorkspacePreferencesDto.Theme`。
- `ShellSettings` 会同步该字段，但现在仅用它计算任务栏和开始菜单的局部颜色；`App.axaml` 仍固定 `RequestedThemeVariant="Light"`。
- `RelaxKonOS.UI/Themes/Styles.axaml` 已包含一组共享资源，但主要为浅色且用 `StaticResource`；`RemoteWindowTheme.axaml`、Shell 和应用页面仍有大量直接十六进制颜色。
- 现有 AXAML 中约有 **603** 个十六进制色值（统计命令：`rg -o '#[0-9a-fA-F]{3,8}\\b' --glob '*.axaml'`）。这意味着本工作是全量迁移，不应通过为现有页面再复制一套 `Dark.axaml` 来完成。

因此，迁移的准则是：**先建立完整的令牌与运行时切换链路，再按范围将所有调用方换成令牌。** 不新增 `Dark` 页面副本，不保留“仅 Shell 主题化”的中间终态。

---

## 3. 总体架构

```text
Settings / PreferencesSync
            │ ThemePreferences
            ▼
     ThemeService (Client singleton)
            │  设置 RequestedThemeVariant
            │  替换运行时 Palette ResourceDictionary
            ▼
 Application.Resources + ThemeDictionaries
            │
            ▼ DynamicResource
  RelaxKonOS.UI styles / WindowManager / Shell / Apps
            │
            ▼
        Avalonia controls
```

### 3.1 Avalonia 使用规则

1. 只用 Avalonia 的 `ThemeVariant.Light`、`ThemeVariant.Dark`、`ThemeVariant.Default` 表示模式。`Default` 交给 Avalonia 跟随系统。
2. 不为每个调色板创建自定义 `ThemeVariant`。调色板不是系统外观变体；把 Nord、Catppuccin 等注册为 ThemeVariant 会使模式、回退与第三方 Fluent 资源的组合复杂化。
3. 会在运行时改变的颜色、画刷、尺寸、圆角、阴影等，引用一律使用 `{DynamicResource TokenName}`。`StaticResource` 只可用于真正的常量（例如字体列表）或不会随着主题改变的内部引用。
4. 控件与业务页面只能引用**语义令牌**，不能引用品牌原色（如 `Blue500`）或 `#0078D4`。页面表达用途而不是调色板的实现细节。
5. `ThemeService.ApplyAsync` 必须在 Avalonia UI 线程上原子地更新资源，并先验证完整候选调色板。无论失败位置在哪里，都保留上一次有效资源或默认调色板。

### 3.2 资源所有权与目录布局

实施时将现有单一 `Styles.axaml` 拆分为以下布局；实际文件名可以小幅调整，但职责和引用方向不得改变。

```text
Framework/RelaxKonOS.UI/
└── Themes/
    ├── RelaxKonOSTheme.axaml              # 汇总入口：基础样式、令牌定义与控件样式
    ├── Tokens/
    │   ├── TokenContract.axaml          # 令牌键与安全默认值
    │   ├── LightDefaults.axaml          # RelaxKonOS Blue 的浅色默认值
    │   └── DarkDefaults.axaml           # RelaxKonOS Blue 的深色默认值
    ├── Styles/
    │   ├── Foundations.axaml            # 字体、间距、圆角、阴影、动画
    │   ├── Controls.axaml               # Button、TextBox、ListBox、菜单等
    │   └── Helpers.axaml                # card / surface / title 等语义类
    └── Palettes/
        ├── relaxkonos-blue.json           # 内置数据，不是 AXAML
        ├── nord.json
        └── catppuccin-mocha.json

Client/RelaxKonOS.Client/
├── Services/Theming/
│   ├── ThemeService.cs
│   ├── ThemePreferences.cs
│   ├── ThemePalette.cs
│   ├── ThemePaletteValidator.cs
│   ├── ThemePaletteRepository.cs
│   └── AccentColorGenerator.cs
└── Apps/Settings/Views/Pages/
    └── PersonalizationPageView.axaml    # 模式、调色板、强调色和导入/导出 UI
```

`RelaxKonOS.UI` 只拥有资源契约和共享控件样式；它不得依赖 Client 的存储或服务。`RelaxKonOS.WindowManager` 只消费令牌，不自建窗口颜色。`Client` 负责选择、加载、验证、持久化和注入当前调色板。

---

## 4. 语义令牌契约

令牌名称是跨项目 API。新增令牌需要先说明语义、浅色值、深色值和对比度要求；重命名或删除必须同时迁移所有消费者。

### 4.1 核心颜色与画刷

每个 `*Color` 必须有同名的 `*Brush`。控件一般引用 Brush，控件模板、派生画刷或 C# 计算才引用 Color。

| 类别 | 必需令牌 |
|---|---|
| 应用底色 | `AppBackground`、`ShellBackground`、`Surface`、`SurfaceRaised`、`SurfaceSunken`、`SurfaceHover`、`SurfacePressed` |
| 文本与图标 | `TextPrimary`、`TextSecondary`、`TextTertiary`、`TextDisabled`、`TextOnAccent`、`TextOnDanger` |
| 分隔与焦点 | `BorderSubtle`、`BorderDefault`、`BorderStrong`、`FocusBorder`、`FocusRing` |
| 强调与选择 | `Accent`、`AccentHover`、`AccentPressed`、`AccentMuted`、`SelectionBackground`、`SelectionForeground` |
| 状态 | `Success`、`SuccessMuted`、`Warning`、`WarningMuted`、`Danger`、`DangerHover`、`DangerPressed`、`Info` |
| 桌面与窗口 | `TaskbarBackground`、`TaskbarForeground`、`StartMenuBackground`、`WindowFrameBackground`、`WindowTitleBarBackground`、`WindowTitleForeground`、`WindowInactiveTitleForeground` |
| 透明层 | `OverlayScrim`、`DialogScrim`、`ShadowColor`、`DesktopIconHover`、`DesktopIconSelected` |

还应提供这些非颜色令牌：`ControlCornerRadius`、`OverlayCornerRadius`、`WindowCornerRadius`、`ControlHeight`、`ControlPadding`、`ContentFont`、`ContentFontSize`、`ElevationLow`、`ElevationMedium`、`TransitionFast`。v1 可以让所有调色板共享这些数值，但必须通过动态资源引用，以便未来 `StyleId` 能接管它们。

### 4.2 使用示例

```xml
<!-- 正确：语义明确，运行时可更新。 -->
<Border Background="{DynamicResource SurfaceBrush}"
        BorderBrush="{DynamicResource BorderDefaultBrush}">
    <TextBlock Foreground="{DynamicResource TextPrimaryBrush}" />
</Border>

<!-- 正确：危险操作使用状态语义。 -->
<Button Classes="danger"
        Background="{DynamicResource DangerBrush}"
        Foreground="{DynamicResource TextOnDangerBrush}" />

<!-- 禁止：在业务视图中直接写产品色或旧资源。 -->
<Button Background="#0078D4" />
<TextBlock Foreground="{StaticResource TextBrush}" />
```

对于带透明度的状态（例如桌面图标悬停），提供完整的预合成令牌，不在页面中使用 `#22...` 这类 alpha 十六进制字面量。对于数据可视化，使用 `ChartSeries1` 至 `ChartSeries8` 和 `ChartGridLine`，不能滥用 Accent。

### 4.3 可访问性基线

- `TextPrimary` 对其正常承载背景、普通文字尺寸下的对比度不低于 **4.5:1**；大文字不低于 **3:1**。
- `TextOnAccent`、`TextOnDanger` 与相应填充不低于 **4.5:1**。
- `FocusRing` 在相邻背景上的可见对比度不低于 **3:1**，且不能只靠颜色区别选中、错误或焦点状态。
- 调色板导入时，如果必需文字/背景组合不达标，拒绝应用并显示具体令牌；强调色不足时可以自动选择黑/白 `TextOnAccent`，但必须仍通过对比度验证。

---

## 5. 模式、调色板与强调色

### 5.1 内置调色板

首期内置以下稳定 ID；显示名称由本地化资源提供，ID 不得被翻译或重命名。

| ID | 浅色变体 | 深色变体 | 备注 |
|---|---|---|---|
| `builtin:relaxkonos-blue` | `relaxkonos-blue-light` | `relaxkonos-blue-dark` | 默认与缺失回退 |
| `builtin:nord` | `nord-light` | `nord-dark` | 两种模式都必须完整定义 |
| `builtin:catppuccin` | `catppuccin-latte` | `catppuccin-mocha` | 两种模式都必须完整定义 |

选择 `System` 时，只依据运行 Client 的设备选择浅/深变体；调色板 ID 本身不变。系统模式不可同步为某台设备的实际深色结果，否则跨设备会失去“跟随本机”的语义。

### 5.2 用户自定义调色板

用户主题是**受限 JSON 数据**，绝不加载 AXAML。JSON 可以通过设置页面导入和导出；导入后被规范化并保存为当前 Workspace 的自定义调色板数据，因此不同设备可使用同一套颜色。原始导入文件不是运行时依赖，也不扫描任意目录。

```json
{
  "formatVersion": 2,
  "id": "violet-night",
  "name": "Violet Night",
  "lightColors": {
    "accent": "#6D28D9",
    "surface": "#FFFFFF"
  },
  "darkColors": {
    "accent": "#A78BFA",
    "appBackground": "#0F0F14",
    "surface": "#18181F",
    "surfaceRaised": "#202028",
    "textPrimary": "#F4F4F5",
    "textSecondary": "#A1A1AA",
    "borderDefault": "#30303A",
    "success": "#22C55E",
    "warning": "#F59E0B",
    "danger": "#EF4444"
  }
}
```

导入格式只允许 6 位或 8 位 sRGB 十六进制色（统一转为大写 `#RRGGBB` 或 `#AARRGGBB`）；`id` 为 `[a-z0-9-]`、长度 1–64，`name` 长度 1–80。一份自定义调色板必须同时提供 `lightColors` 与 `darkColors`，从而在模式切换时仍保持同一主题身份；每个变体可只覆盖需要变更的语义令牌。服务端会对令牌白名单、两种模式的完整解析结果及 WCAG 对比度进行校验；旧版 `formatVersion: 1` 的 `mode/colors` 载荷会在下次保存时规范化为 v2。服务端限制调色板数量（建议 20）、单个 JSON 体积（建议 16 KiB）和总偏好大小。

“仅改强调色”不是一份特殊主题：它是对所选调色板的 `AccentOverride`。服务会生成 `AccentHover`、`AccentPressed`、`AccentMuted`、选中背景和焦点环，并为 Accent、Danger、选中态自动选择满足对比度的黑/白前景。清除覆盖应恢复调色板原始 Accent。

### 5.3 存储与协议演进

现有 `WorkspacePreferencesDto.Theme` 继续表示外观模式，避免破坏已有客户端。新增一个可选 `ThemePreferences` 对象，旧服务端或旧 JSON 缺失它时使用默认值：

```text
WorkspacePreferencesDto
├── Theme: ThemeKind                         # 现有：Light / Dark / System
└── ThemePreferences: ThemePreferencesDto?   # 新增
    ├── StyleId: "relaxkonos"
    ├── PaletteId: "builtin:relaxkonos-blue" | "custom:<id>"
    ├── AccentOverride: "#RRGGBB"?
    └── CustomPalettes: List<ThemePaletteDto>
```

`ThemePreferencesDto`、`ThemePaletteDto` 与 API 端校验放入 `Shared/RelaxKonOS.Protocol` / `RelaxKonOS.Server`，同现有 Workspace Preferences 的 `OwnsOne + ToJson` 模式。迁移时应读取旧偏好并以默认 `ThemePreferences` 补全；写回时保留所有既有偏好字段。主题选择是 Workspace 级数据，和壁纸相同随账号工作区同步。

---

## 6. 实施计划（供后续 Goal 使用）

以下阶段按顺序实施。每一阶段应保持可编译，并在完成后进行本阶段验证；不要先大范围替换颜色而没有运行时资源基础。

### Phase 0 — 基线与防护

1. 记录硬编码颜色清单，分别统计 AXAML 与 C#（C# 的 `Color.Parse`、`Brushes.*`、`new SolidColorBrush` 也在范围内）。
2. 在贡献规范或 CI 中加入检查：除 `Themes/`、测试资源、示例调色板和图片数据外，禁止新出现 `#[0-9A-Fa-f]` 的产品 UI 颜色。
3. 标出第三方控件和原生控件的不可控区域，避免把它们误报为迁移遗漏。

### Phase 1 — 契约、默认资源与服务

1. 建立第 3.2 节的资源布局，提供完整浅/深 RelaxKonOS Blue 令牌；将 `App.axaml` 引用切换到新汇总入口。
2. 实现 `ThemeService`：读取 `ShellSettings`，把 `ThemeKind.Light/Dark/System` 映射到 Avalonia `RequestedThemeVariant`，并将当前调色板的语义令牌写入专用、可替换的 `ResourceDictionary`。
3. 在 `Bootstrapper` 注册 singleton，并保证 PreferencesSync 在初始偏好加载和后续保存后调用服务。订阅设置变化时防抖持久化，但 UI 立即更新。
4. 令牌缺失、JSON 解析异常、资源注入异常均回退 `builtin:relaxkonos-blue`；记录可诊断日志，不把用户输入显示为异常堆栈。

### Phase 2 — 框架、桌面与窗口

1. 迁移 `RelaxKonOS.UI/Themes/Styles.axaml` 的 Button、TextBox、ListBox、card、surface、文本辅助样式。
2. 迁移 `RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`，包括活动/非活动边框、标题栏、关闭按钮与阴影。
3. 迁移 `App.axaml`、`MainWindow.axaml`、登录窗口、各 `IDesktopShell` launcher、连接栏、任务栏/Dock、启动器、菜单和桌面图标状态。Shell 运行时边界见 [`RelaxKonOS.ShellLauncher.Goal.md`](./RelaxKonOS.ShellLauncher.Goal.md)。
4. 删除 `ShellSettings.TaskbarBackground` / `TaskbarForeground` 中固定的浅深颜色逻辑；Shell 改为资源绑定或由令牌驱动的可通知画刷。

### Phase 3 — 所有内置应用与代码生成 UI

按目录批量迁移 `Client/RelaxKonOS.Client/Apps/**`、`Services/Diagnostics`、`Views/**`、`examples/**`。每个 AXAML 中的 `Background`、`Foreground`、`BorderBrush`、`CaretBrush`、选中、悬停、危险状态、图表色和阴影都必须映射到本文件的令牌。

同时迁移 C# 创建的 `Button`、`Border`、`TextBlock`、`DataGrid` 等：使用 `DynamicResourceExtension`、样式类或集中式控件样式，不能以 `Brushes.White`、`Color.Parse` 等形式绕过主题。专用业务色应先增加语义令牌再使用。

### Phase 4 — 设置、存储和用户调色板

1. 扩展 Protocol、Server 归一化校验、`ShellSettings`、`PreferencesSync` 和 `PersonalizationPageViewModel`。
2. 将个性化页重构为四块：外观模式、调色板、强调色、导入/导出与自定义调色板管理。所有文案添加到 `en-US`、`zh-CN`、`ja-JP` 的 `settings.json`。
3. 支持预览后应用；导入失败时说明是格式、令牌、对比度还是容量限制，不改变当前生效主题。
4. 更新设置中心文档、协议文档和本文件所列实现状态。

### Phase 5 — 验证与收尾

1. 在 Light、Dark、System（分别以系统浅色和深色启动）下验证全部内置应用、模态对话框、登录/退出/重新登录和多窗口场景。
2. 逐一切换三套内置调色板、强调色覆盖、导入/导出/删除自定义调色板、无效 JSON、网络保存失败和旧偏好迁移。
3. 使用截图或人工视觉检查，重点检查文字、输入焦点、选中行、禁用态、危险按钮、窗口非活动态、任务栏、菜单和 DataGrid。
4. `dotnet build RelaxKonOS.sln` 必须通过；完成后重新运行 Phase 0 的扫描，确认剩余硬编码色全部位于允许的例外目录，或已有逐项说明。

---

## 7. 验收标准

主题化工作只有同时满足以下条件才算完成：

- 从设置页变更模式、调色板或 Accent 后，当前已打开的 Shell、窗口和内置应用立即变化，无需重启。
- `System` 在不同宿主系统外观下正确解析；保存的值仍为 `System`，不会被覆写成 `Light` 或 `Dark`。
- 所有三个内置调色板都在 Light 与 Dark 变体下完整渲染；不存在白底白字、黑底黑字、不可见焦点框或无法辨认的禁用控件。
- 新建、导入、导出、编辑、删除自定义调色板在同一 Workspace 的另一客户端也能得到一致结果；无效数据没有副作用。
- 主题 JSON 不执行代码或 AXAML，不能引用本地路径、网络 URI 或程序集。
- 现有壁纸、语言、时间格式、默认应用、终端私有配色和桌面显示配置不因主题迁移而丢失或改变含义。
- UI 目录不再新增未经批准的硬编码产品色，现存颜色迁移清单无未解释项。

---

## 8. 后续执行提示

后续使用 Goal 模式实施时，将本文件作为任务规范，并使用以下目标：

> 依据 `docs/desktop/RelaxKonOS.Theming.md` 完成 RelaxKonOS 的全局主题与配色系统。严格按 Phase 0–5 执行：建立 Avalonia 动态语义资源和 ThemeService，迁移所有自有 UI 的硬编码颜色，扩展 Workspace 偏好与个性化设置以支持模式、内置调色板、Accent 覆盖和安全 JSON 自定义调色板；完成构建、扫描与 Light/Dark/System 验证。不要动态加载用户 AXAML，不要改变终端或网页内容的独立配色语义。

实施前应先重新检查本文所引用文件和硬编码色统计，因为项目可能已发生变化；本文中的路径、令牌契约、边界和验收标准则为约束性要求。
