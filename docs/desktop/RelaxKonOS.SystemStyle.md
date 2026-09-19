# RelaxKonOS 系统风格（System Style）

> **状态：Phase 0–5 已实施（2026-09-19）。** 本文是系统风格层的实施规范与现状记录。
> 五个阶段的功能性交付物均已落地并通过构建与契约校验；
> **视觉回归尚未执行**（见 §11.2），因此「已实施」指代码与契约层面，不等于已通过观感验收。
>
> 规划来源：[`RelaxKonOS.SystemStyle.Plan.md`](./RelaxKonOS.SystemStyle.Plan.md)。
> 配色与调色板见 [`RelaxKonOS.Theming.md`](./RelaxKonOS.Theming.md)；
> 设置中心见 [`RelaxKonOS.Settings.md`](./RelaxKonOS.Settings.md)；
> 偏好协议见 [`RelaxKonOS.Protocol.md`](../architecture/RelaxKonOS.Protocol.md)、[`RelaxKonOS.Workspace.md`](../architecture/RelaxKonOS.Workspace.md)。

---

## 1. 三个彼此独立的概念

系统风格的核心是**不再把“颜色”和“形状”混为一个下拉框**。三者各自独立选择、独立存储：

| 层 | 决定什么 | 真源字段 | 取值 |
|---|---|---|---|
| 外观与调色板 | 浅/深/跟随系统、语义颜色、强调色 | `DesktopExperience.Appearance` | `ThemeKind` + `builtin:*` / `custom:*` |
| **系统风格** | 系统部件的形状、尺寸、动效、菜单与窗口 chrome 的部件选择 | `DesktopExperience.SystemStyleId` | `relaxkonos.windows-like` / `.macos-like` / `.ubuntu-like` |
| 桌面 Shell | 桌面布局及启动器/Dock/菜单栏的具体实现 | `DesktopExperience.Shell` | 内置或外置 `IDesktopShell` |

约束：

- **系统风格永不带颜色。** `SystemStyleManifestDto` 没有任何颜色字段；窗口阴影的**形状**（深度/不透明度）来自风格，**颜色**来自当前调色板的 `Shadow`。
- **调色板永不带形状。** `AppearancePreferencesDto` 只有模式/调色板/强调色/自定义调色板，没有 `StyleId`。
- **推荐映射不是合并。** `SystemStyleIds.RecommendedForShell(shellId)` 只给设置页提供“采用此 Shell 推荐的系统风格”按钮，不写入、不隐式修改；用户可任意组合（例如 Ubuntu 布局 + Windows-like 窗口与菜单）。

---

## 2. Phase 0 基线审计（硬编码颜色与形态）

统计命令（在仓库根执行，排除 `obj/`、`bin/`）：

```bash
grep -rEo '#[0-9a-fA-F]{3,8}\b' --include='*.axaml' --include='*.cs' Client Framework examples \
  | grep -v '/obj/' | grep -v '/bin/' | wc -l
```

2026-09-19 基线结果：**154** 处（AXAML 70 + C# 84）。

| 文件 | 命中数 | 说明 |
|---|---|---|
| `examples/Windows11DesktopShell/Views/Windows11ShellView.axaml` | 49 | 外置示例自绘桌面，Phase 3 目标 |
| `Client/.../Views/Shell/LauncherDesktopShells.cs` | 35 | 三个内置 Shell 的 C# 构造 UI |
| `Client/.../Services/ShellSettings.cs` | 27 | 任务栏/开始菜单局部颜色计算 |
| `Client/.../Apps/Terminal/TerminalAppearance.cs` | 12 | 终端 ANSI 配色，**允许例外** |
| `Client/.../Views/MainWindow.axaml` | 5 | 宿主标题栏/连接栏 |
| `Client/.../Apps/TaskManager/ViewModels/TaskManagerViewModel.cs` | 4 | 图表系列色，**允许例外**（数据可视化） |
| 其余（Git/TaskManager/Explorer/Environment 等 AXAML，以及 `SettingsApp.cs` 等） | 22 | 逐项迁移 |
| `Framework/RelaxKonOS.UI/Themes/**` | 2 | 令牌字典内的合法默认值 |

> Phase 0 阶段**不清空**这 154 处。Plan 明确要求先建立完整令牌与运行时切换链路，再按范围迁移；
> 一次性替换颜色会留下无法回归的半成品（见 Theming 计划 §6 的同一条准则）。
> 命中数不含 `obj/`、`bin/`，也不含终端/图表等应用私有配色语义。

### 2.1 Phase 3 迁移结果（2026-09-19）

Phase 2/3 完成迁移后，按 §10.2 的**精确口径**（`#[0-9A-Fa-f]{6,8}\b`；比 Phase 0 的 `{3,8}` 更严，
不再把 `#RGB` 缩写计入）复扫，结果为 **46** 处，且全部落在下表的允许例外与令牌兜底里：

| 结果 | 文件 |
|---|---|
| 已清零 | `Windows11ShellView.axaml`（49 → 0）、`DesktopShortcutsView.axaml`（4 → 0）、`LauncherDesktopShells.cs`（35 → 0）、`MainWindow.axaml`（5 → 0）、`ShellSettings.cs` 中的**界面色**部分、Git/Explorer/TaskManager/Environment 等 AXAML、`SettingsApp.cs` |
| 保留的 46 处 | `ShellSettings.cs` 27（壁纸渐变）、`TerminalAppearance.cs` 12（终端配色）、`TaskManagerViewModel.cs` 4 + `PerformanceLineChart.cs` 1（图表系列色）、`SystemStyleTokens.axaml` 2（首帧兜底） |

`SystemStyleChecks.VerifyNoHardcodedColours` 把这张「保留清单」固化为可执行规则：
新建/新增十六进制颜色会让校验失败，只有减少是自由的（见 §10.2）。

---

## 3. 形状令牌契约（闭集）

真源：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleTokenContract.cs`。

令牌键是跨项目 API。清单只能设置**这里声明过**的键，且值必须落在显式区间内——
这是清单无法夹带破坏布局的数值的原因。

`SystemStyleTokenKind` 决定宿主如何把数值物化成 Avalonia 资源：

| Kind | 物化结果 |
|---|---|
| `Number` | `double`（`x:Double`） |
| `CornerRadius` | `CornerRadius` |
| `Thickness` | `Thickness`（统一四边） |
| `Duration` | `TimeSpan`（输入单位毫秒） |
| `Opacity` | `double`（0..1） |

### 3.1 令牌全表（33 项，`*` 为必需）

| 分组 | 令牌 | Kind | 区间 | 默认 |
|---|---|---|---|---|
| 密度与反馈 | `ControlHeight` * | Number | 20–64 | 32 |
| | `ControlCornerRadius` * | CornerRadius | 0–24 | 4 |
| | `OverlayCornerRadius` * | CornerRadius | 0–32 | 8 |
| | `FocusRingThickness` * | Thickness | 0–6 | 2 |
| | `MinimumHitTarget` * | Number | 28–64 | 40 |
| | `TransitionFast` * | Duration | 0–1000 | 120 |
| | `ReducedMotionDuration` * | Duration | 0–1000 | 0 |
| 受管窗口外壳 | `WindowTitleBarHeight` * | Number | 24–96 | 42 |
| | `WindowFrameThickness` * | Thickness | 0–8 | 1 |
| | `WindowCornerRadius` * | CornerRadius | 0–32 | 8 |
| | `WindowControlWidth` * | Number | 28–96 | 52 |
| | `WindowInactiveOpacity` * | Opacity | 0.2–1 | 0.55 |
| | `WindowShadowDepth` * | Number | 0–64 | 28 |
| | `WindowShadowOpacity` * | Opacity | 0–1 | 0.4 |
| 命令菜单 | `MenuCornerRadius` * | CornerRadius | 0–32 | 6 |
| | `MenuBorderThickness` * | Thickness | 0–4 | 1 |
| | `MenuItemHeight` * | Number | 20–64 | 30 |
| | `MenuPadding` * | Thickness | 0–24 | 6 |
| | `MenuSubmenuDelay` | Duration | 0–1000 | 250 |
| 浮层与对话框 | `FlyoutCornerRadius` * | CornerRadius | 0–32 | 8 |
| | `FlyoutElevation` * | Number | 0–64 | 12 |
| | `DialogCornerRadius` * | CornerRadius | 0–32 | 8 |
| | `DialogMotionDuration` * | Duration | 0–1000 | 160 |
| 桌面 chrome | `TaskbarHeight` * | Number | 32–96 | 48 |
| | `TopBarHeight` * | Number | 20–64 | 30 |
| | `LauncherCornerRadius` * | CornerRadius | 0–40 | 12 |
| | `DockMagnification` | Number | 1–2 | 1.25 |
| | `TaskbarIconSize` | Number | 24–64 | 40 |
| 窗口概览 | `OverviewCardRadius` * | CornerRadius | 0–32 | 10 |
| | `OverviewCardBorderThickness` * | Thickness | 0–6 | 2 |
| | `OverviewThumbnailScale` | Number | 0.4–1.5 | 1 |
| | `OverviewEnterDuration` * | Duration | 0–1000 | 180 |
| | `OverviewSelectionRingThickness` * | Thickness | 0–8 | 3 |

必需令牌共 **29** 项（上表 `*`）。非必需令牌缺失时回退到定义中的默认值。

`SystemStyleTokenContract.AbsoluteMinimumHitTarget = 40`：宿主接受的最低交互命中尺寸。

**明确不在契约内**：`WindowControlOrder`、`TaskbarAlignment`、颜色类令牌。
控件按钮顺序由 `WindowChromeRecipe` 决定（见 §4），不是可自由填写的数值；
对齐/颜色属于 recipe 与调色板的职责。这是有意的收敛，避免出现“数值看似合理但布局不可实现”的档位。

---

## 4. Recipe 选择器（闭集）

真源：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleRecipes.cs`。

每个全局组件在 `RelaxKonOS.UI` 保留由宿主审查的基础模板，风格只能通过 recipe **选择**其中一种已审查的变体。
recipe 的合法值全部是同一文件里的字符串常量——清单无法提供 AXAML 片段、CLR 类型名、程序集引用、资源 URI 或事件处理器。

| 槽位（`SystemStyleRecipeKinds`） | 允许值 |
|---|---|
| `windowChrome` | `caption-buttons-right` / `traffic-lights-left` / `headerbar-right` |
| `contextMenu` | `compact-command-menu` / `rounded-command-menu` / `gnome-popover-menu` |
| `taskSwitcher` | `windows-grid` / `macos-strip` / `gnome-overview` |
| `shellChrome` | `bottom-taskbar` / `top-menu-plus-dock` / `top-bar-plus-left-dock` |

四个槽位**必须全部填写**；缺任何一项 → `systemstyle.incomplete_recipes`。

### 4.1 各槽位的消费方与实现落点

recipe 只是「选择」，真正产生差异的是宿主审查过的模板。下表是每个槽位的**唯一**消费点，
`SystemStyleChecks.VerifyRecipeCoverage` 会逐一核对：任何允许值都必须在该文件里留下实现痕迹，
否则测试失败——避免「清单选了某个变体而宿主悄悄回退」。

| 槽位 | 消费方 | 实现机制 |
|---|---|---|
| `windowChrome` | `Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml` | `RemoteWindow` 把 `SystemStyle.WindowChrome` 资源镜像为 `:chrome-<值>` 伪类，模板只重新部署 `PART_WindowIcon` / `PART_TitleText` / `PART_WindowControls` 三个部件 |
| `contextMenu` | `Framework/RelaxKonOS.UI/Themes/SystemStyle/SystemStyleResourceBuilder.cs` | 在令牌派生的命令面（菜单/子菜单/ToolTip/Flyout）密度之上再乘一组系数：边框权重、内边距、条目横向内缩、分隔线间距 |
| `taskSwitcher` | `Client/RelaxKonOS.Client/Views/Shell/WindowOverviewView.axaml` | 概览视图把 `SystemStyle.TaskSwitcher` 镜像为根节点上的 `recipe-<值>` class，由样式决定卡片几何与遮罩 |
| `shellChrome` | `Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/BuiltInSystemStyles.cs` | 结构性槽位：三个内置 Shell 各自对应一种桌面 chrome；校验保证「Shell ↔ 推荐风格 ↔ shellChrome」三者一致 |

**`contextMenu` 的边界（如实记录）**：它能改的是**密度、内缩与边框权重**，
不能改圆角——Fluent 的 `MenuFlyoutPresenter` 从单一共享 overlay 键取圆角，
要在每个 recipe 下换圆角就必须替换该模板，而替换模板会牺牲子菜单、键盘导航与点击外部关闭，
因此不做。菜单圆角仍由风格的 `MenuCornerRadius` / `FlyoutCornerRadius` 令牌全局驱动。

**`shellChrome` 的边界（如实记录）**：它是一个**结构性**槽位，描述桌面 chrome 属于哪一类；
具体的任务栏/顶栏/Dock 布局由 Shell 包自己拥有（Plan §4.2：「需要自定义桌面布局时，外置包仍实现 `IDesktopShell`」）。
宿主不做任何视觉后处理，因此它的「消费方」是内置 profile 本身与一致性校验，而不是某个宿主模板。

已实现、可驱动的 recipe 值：

| 槽位 | 已实现值 |
|---|---|
| `windowChrome` | `caption-buttons-right`（默认：图标在前、标题居中偏左、按钮在右）、`traffic-lights-left`（按钮在左、图标在右、标题居中、按钮胶囊化）、`headerbar-right`（无图标、左对齐加粗标题） |
| `contextMenu` | `compact-command-menu`（紧凑基准）、`rounded-command-menu`（内边距 ×1.35、条目再内缩 +4）、`gnome-popover-menu`（去掉边框、内边距 ×1.6、条目再内缩 +8） |
| `taskSwitcher` | `windows-grid`（288×200 居中换行网格 + 标题）、`macos-strip`（372×252 大卡片、更重遮罩、隐藏标题）、`gnome-overview`（236×172 密集网格、网格顶端对齐） |
| `shellChrome` | 三套内置 Shell 的自身布局 |

---

## 5. 清单契约与校验

### 5.1 `SystemStyleManifestDto`

真源：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleManifestDto.cs`。

| 字段 | 说明 |
|---|---|
| `schemaVersion` | 本构建仅接受 `1`（`CurrentSchemaVersion`） |
| `id` | `^[a-z0-9][a-z0-9.-]{2,127}$` |
| `displayName` | 1–80 字符 |
| `supportedRecipes` | 四个槽位的 recipe 选择 |
| `tokens` | 浅深共用形状令牌 |
| `lightTokens` / `darkTokens` | 可选，按模式覆盖 `tokens` |
| `accessibility` | `minimumHitTarget`（≥40）、`supportsReducedMotion`（必须为 `true`） |
| `minimumHostApiVersion` | 不得高于宿主 `CurrentHostApiVersion`（`"1.0"`） |
| `packageId` / `packageVersion` | 外置包来源；内置为 `null`。外置门禁要求两者都非空 |
| `source` | 来源标注，只接受 `SystemStyleSources` 的两个常量：`builtin` / `signed-package`。**没有「unknown」兜底**——无法说明来源的清单会被拒绝 |

`ResolveTokens(bool dark)`：先铺全部令牌默认值 → 覆盖 `tokens` → 再覆盖对应模式变体。
**清单没有颜色字段**，这是可外部扩展与不可外部扩展之间的分界线。

### 5.2 `SystemStyleManifestValidator`

`TryValidate(manifest, hostApiVersion, out problemCode, out normalized)`。
拒绝即整体拒绝，并返回一个键（不是句子，由客户端本地化）：

| 问题码 | 触发条件 |
|---|---|
| `systemstyle.invalid_manifest` | `manifest` 为 `null` |
| `systemstyle.schema_unsupported` | `schemaVersion` 不是 `1` |
| `systemstyle.host_api_too_old` | `minimumHostApiVersion` 无法解析或高于宿主 |
| `systemstyle.invalid_id` | id 缺失或不符合正则 |
| `systemstyle.invalid_display_name` | 空或超过 80 字符 |
| `systemstyle.incomplete_recipes` | `supportedRecipes` 为 `null` |
| `systemstyle.unknown_recipe` | 某槽位为空或不在该槽位允许集合内 |
| `systemstyle.unknown_token` | 出现未声明的令牌键 |
| `systemstyle.token_out_of_range` | 值为 NaN/Infinity 或超出区间 |
| `systemstyle.missing_token` | 缺少必需令牌（既未在 `tokens` 给出，也未在 `lightTokens`+`darkTokens` 同时给出） |
| `systemstyle.hit_target_too_small` | `accessibility.minimumHitTarget` < 40，或 `tokens.MinimumHitTarget` 小于它 |
| `systemstyle.reduced_motion_unsupported` | `supportsReducedMotion == false` |
| `systemstyle.invalid_source` | `source` 不是 `SystemStyleSources` 的已知常量（含已废弃的 `unknown`） |
| `systemstyle.style_unavailable` | 运行时：本设备没有该风格（见 §7.2） |

校验通过时输出 `normalized`（裁剪空白、丢弃未知键、规范化 `source`）。
**校验在令牌进入实时资源图之前完成**，调用方原子应用结果，因此拒绝永远不会产生半套或透明的 UI。

### 5.3 外置包门禁 `TryValidateExternal`

`TryValidateExternal(manifest, hostApiVersion, out problemCode, out normalized)` 是**包附带风格**必须通过的更严一档：

| 问题码 | 触发条件 |
|---|---|
| `systemstyle.external_source_required` | 校验通过但 `source` 是 `builtin`——包不能冒充宿主自带风格 |
| `systemstyle.package_attribution_missing` | `packageId` 或 `packageVersion` 为空 |

「只接受当前契约版本」由 `TryValidate` 保证：`schemaVersion` 必须严格等于 `1`，
且 `minimumHostApiVersion` 不得高于宿主 API。**没有旧 schema 读取路径、没有迁移层。**

调用点是 `SystemStyleRegistry.Register(manifest, isBuiltIn)`：`isBuiltIn: false` 走外置门禁。
**本构建尚无包加载器**（Plan Phase 5 第 3 条要求「仅在示例与三个内置风格全覆盖后再开放第三方 manifest」），
因此外置路径是**已实现、已测试、但生产上未接通**的边界——这是刻意的，不是遗漏。

---

## 6. 三套内置 profile

真源：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/BuiltInSystemStyles.cs`，纯数据，与外部清单走同一 schema/validator/令牌词表。

| | Windows-like | macOS-like | Ubuntu-like |
|---|---|---|---|
| id | `relaxkonos.windows-like` | `relaxkonos.macos-like` | `relaxkonos.ubuntu-like` |
| `windowChrome` | `caption-buttons-right` | `traffic-lights-left` | `headerbar-right` |
| `contextMenu` | `compact-command-menu` | `rounded-command-menu` | `gnome-popover-menu` |
| `taskSwitcher` | `windows-grid` | `macos-strip` | `gnome-overview` |
| `shellChrome` | `bottom-taskbar` | `top-menu-plus-dock` | `top-bar-plus-left-dock` |
| 命中目标 | 40 | 40 | 44 |

差异**不只在任务栏位置**，关键令牌逐项不同（摘录）：

| 令牌 | Windows-like | macOS-like | Ubuntu-like |
|---|---|---|---|
| `WindowTitleBarHeight` | 42 | 38 | 46 |
| `WindowCornerRadius` | 8 | 12 | 10 |
| `WindowControlWidth` | 52 | 28 | 44 |
| `WindowInactiveOpacity` | 0.55 | 0.60 | 0.50 |
| `WindowShadowDepth` / `Opacity` | 28 / 0.40 | 34 / 0.32 | 24 / 0.30 |
| `MenuCornerRadius` | 6 | 10 | 12 |
| `MenuBorderThickness` | 1 | 1 | 0 |
| `MenuItemHeight` | 30 | 28 | 34 |
| `ControlCornerRadius` | 4 | 6 | 6 |
| `FocusRingThickness` | 2 | 3 | 2 |
| `TaskbarHeight` | 48 | 64 | 72 |
| `TopBarHeight` | 30 | 28 | 32 |
| `LauncherCornerRadius` | 12 | 20 | 18 |
| `OverviewCardRadius` | 10 | 14 | 12 |
| `TransitionFast` | 120 | 200 | 150 |

`BuiltInSystemStyles.Default` = Windows-like，用于风格不可解析时的兜底渲染。

---

## 7. 运行时链路

```text
WorkspacePreferencesDto.DesktopExperience
   ├─ Appearance ──────┐
   ├─ SystemStyleId ───┼─> ShellSettings ──> AppearanceService.Apply(...)
   └─ Shell ───────────┘                          │
                                    ┌─────────────┴──────────────┐
                                    v                            v
                         palette ResourceDictionary    style ResourceDictionary
                         （AppearancePreferencesDto）  （SystemStyleManifestDto）
                                    └─────────────┬──────────────┘
                                    Application.Resources.MergedDictionaries
                                                  │ DynamicResource
                                    RelaxKonOS.UI / WindowManager / Shell / Apps
```

### 7.1 `AppearanceService`（`Client/Services/Theming/AppearanceService.cs`）

`ThemeService` 的替代者，不再保留两个服务互相转发。

- 构造时把 `_paletteResources` / `_styleResources` 两个空字典挂进 `Application.Resources.MergedDictionaries`，并立即应用默认外观。
- `Apply(ThemeKind mode, AppearancePreferencesDto?, string? systemStyleId)` 与便捷重载 `Apply(DesktopExperiencePreferencesDto?)`。
  一律在 UI 线程执行（`Dispatcher.UIThread.CheckAccess()`，否则 `Post`）。
- 每次应用：先设 `RequestedThemeVariant`（`Dark`/`Default`/`Light`），再解析深色与否，
  用 `ThemePaletteDefaults.Resolve` + `ThemePaletteValidator` 得到完整调色板（不合格回退默认），
  再把颜色写成 `<name>Color` 键；随后向 registry 取样式清单。
- **原子交换**：先 `Add` 新字典、再 `Remove` 旧字典，中间没有空窗，因此不会出现无资源渲染。
- 样式不可解析时 **保留上一次已验证的形状资源**，只把 `StyleProblem` 暴露给 UI；
  形状资源本身用 `BuiltInSystemStyles.Default` 兜底，保证令牌集始终完整。
- `AppliedStyleId` 是当前**实际渲染**的风格（可能不等于请求的 id）；`Changed` 事件在交换后触发。
- `ReducedMotion` 为显式开关：为 `true` 时所有 `Duration` 令牌折叠为风格的 `ReducedMotionDuration`。
  （Avalonia 未提供跨平台“减少动态效果”标志，接入平台设置另立任务。）

### 7.2 `SystemStyleRegistry`（`Client/Services/Theming/SystemStyleRegistry.cs`）

- 设备本地解析 Workspace 的 `SystemStyleId` **意图**。`HostApiVersion = SystemStyleManifestDto.CurrentHostApiVersion`。
- 构造时注册三套内置 profile；`Available` 内置在前、其余按显示名排序。
- `TryResolve` 只返回可用风格；`TryGetManifest` 给出校验后的清单或问题码。
- `Register(manifest, isBuiltIn)` 先校验：校验失败但 id 可用时，仍登记该 id 并附带 `UnavailableReason`，
  这样设置页能显示“此设备未安装”，而不是静默丢弃用户的选择，也**不静默改写偏好**。

### 7.3 `SystemStyleResourceBuilder`（`Framework/RelaxKonOS.UI/Themes/SystemStyle/`）

`Build(manifest, dark, paletteShadow, reducedMotion)` → 完整的 `ResourceDictionary`：

- 逐令牌物化为 Avalonia 类型（见 §3 的 Kind 表）；`reducedMotion` 时折叠所有 `Duration`。
- `WindowShadow` 由**调色板的 `Shadow` 颜色** + 风格的 `WindowShadowDepth`/`WindowShadowOpacity` 合成：
  `OffsetY = round(depth/2.3)`、`Blur = depth`、alpha 按 `opacity/0.4` 归一。
  这样风格能改变阴影的轻重，但从不携带颜色。
- 写入四个 recipe 选择，键为 `SystemStyleRecipeKeys`：
  `SystemStyle.WindowChrome` / `SystemStyle.ContextMenu` / `SystemStyle.TaskSwitcher` / `SystemStyle.ShellChrome`。
- 派生复合值（`SystemStyleDerivedKeys`）：`OverlayTopCornerRadius`、`OverlayBottomCornerRadius`、
  `ElevationShadow`（由 `FlyoutElevation` + 调色板 `Shadow` 合成）、`WindowCaptionCornerRadius`
  （`min(WindowControlWidth, WindowTitleBarHeight)/2`）、`HostTitleBarMargin`。
- 命令面厚度键（`SystemStyleCommandSurfaceKeys`）：Fluent 的菜单/子菜单/ToolTip/Flyout 模板只认
  **具名 thickness 键**，而 thickness 无法在 AXAML 里写 `Color="{DynamicResource ...}"`，
  所以由本类在代码中物化，并按 `contextMenu` recipe 缩放（见 §4.1）。
  这些键是 Fluent 命名键在本仓库中「桥接字典之外」的唯一书写点。

### 7.3.1 `ThemeResources.Bind`（`Framework/RelaxKonOS.UI/Themes/ThemeResources.cs`）

C# 构造的 UI 必须消费**会跟随主题变化**的令牌，因此：

- `Bind(control, property, key)` —— `{DynamicResource}` 的 C# 等价物，基于
  `control.GetResourceObservable(key)`，是整个仓库中代码构造 UI 读取令牌的**唯一**受支持方式；
- `BindSurface(control, backgroundKey, borderKey?, borderThicknessKey?, cornerRadiusKey?)` —— 一次绑定表面四要素；
- `Brush(key)` / `Color(key)` 只是**调用时刻的快照**，不跟随主题变化，仅用于必须把画刷交给无法接受绑定的 API 的场合。

### 7.4 默认令牌字典与合并入口

- `Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml`：形状令牌的默认值字典
  （含 `sys:TimeSpan` 的动效时长与 `WindowShadow` 的 `BoxShadows`、recipe 字符串键）。
- `Themes/Tokens/TokenContract.axaml` 只保留颜色无关的 `ControlPadding` / `ContentFont` / `ContentFontSize`；
  形状值已全部移入 `SystemStyleTokens.axaml`。
- `Themes/RelaxKonOSTheme.axaml` 同时合并两者，作为唯一入口。
- `Themes/Styles.axaml` 的 caption/icon 按钮与 focus-visible 状态改用 `DynamicResource` 引用
  `Width` / `MinWidth` / `MinHeight` / `BorderThickness`。
- `Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`：
  标题栏高度 → `{DynamicResource WindowTitleBarHeight}`、外框 `BorderThickness` → `{DynamicResource WindowFrameThickness}`、
  非活动标题 `Opacity` → `{DynamicResource WindowInactiveOpacity}`、`:shadow` → `{DynamicResource WindowShadow}`。
- `Framework/RelaxKonOS.UI/Themes/Controls/ControlThemeOverrides.axaml`：Fluent 主题键桥接。
  菜单/子菜单/chevron/ToolTip/Flyout 的画刷键在这里重指向语义调色板；
  厚度键在此只放 Windows-like 的静态兜底，运行时由 `SystemStyleResourceBuilder` 覆盖。
  选择桥接而非替换模板，是为了保留下拉子菜单、键盘导航与点击外部关闭——替换模板会静默破坏它们。

### 7.5 Phase 2–4 引入的组件层

| 组件 | 文件 | 说明 |
|---|---|---|
| 命令面桥接 | `Framework/RelaxKonOS.UI/Themes/Controls/ControlThemeOverrides.axaml` | 唯一的 Fluent 键桥接点（见 §7.4） |
| 窗口 chrome recipe | `Framework/RelaxKonOS.WindowManager/RemoteWindow.cs` + `Themes/RemoteWindowTheme.axaml` | `ChromeRecipeProperty` 通过 `GetResourceObservable` 镜像 `SystemStyle.WindowChrome` → `:chrome-*` 伪类；**已在屏幕上的窗口切换风格后会即时换装** |
| 窗口概览投影 | `Framework/RelaxKonOS.WindowManager/WindowOverview.cs` | 只读 `WindowOverviewItem` + `IWindowOverviewController`；不含 `ManagedWindow`，概览无法改 z 序或访问应用 |
| 系统 UI 协调器 | `Client/RelaxKonOS.Client/Services/SystemUi/SystemUiCoordinator.cs` | 概览可见性与全局快捷键的唯一权威；`Win+Tab`/`Alt+Tab`/`Esc`/方向键/Enter/Delete 在此归一处理 |
| 概览视图 | `Client/RelaxKonOS.Client/Views/Shell/WindowOverviewView.axaml(.cs)` | 三种 task switcher recipe；卡片降级为「图标 + 标题」（无缩略图）；入场动效使用 `OverviewEnterDuration` |
| 宿主叠加层 | `Client/RelaxKonOS.Client/Views/MainWindow.axaml(.cs)` | `WindowOverview` 以 `ZIndex=100` 覆盖全部 Shell 表面，并**自身充当 `InputBackdrop`**：可见时下层不可点击，隐藏时不参与命中测试 |

**层级与输入规则（Plan §7.2 的四层）**：选定 Shell chrome / `WindowHost` → `FullScreenWindowHost` →
`ShellOverlayHost` → `InputBackdrop`。本实现的概览位于宿主窗口层，因此对内置与**外置** Shell 一视同仁；
安全系统模态（`ShowSystemDialogAsync`，`coversFullDesktop: true`）优先于概览：
`IWindowManager.IsSystemModalOpen` 为真时 `SystemUiCoordinator.ShowOverview()` 直接返回 `false`。
应用模态只阻塞其 owner，因此概览仍可打开（`Focus` 会把激活重定向到该模态链顶端）。

> 注：`ShellSurfaces.InputBackdrop` 由 Shell 注册后目前无人消费；概览使用的是宿主级叠加层，
> 两者职责不重叠（前者留给 Shell 自己的弹出层）。此处如实记录，不做重复实现。

---

## 8. 偏好模型（`DesktopExperience`）

`WorkspacePreferencesDto` 删除顶层 `theme` 与 `shell`，新增唯一对象：

```text
WorkspacePreferencesDto
└── desktopExperience: DesktopExperiencePreferencesDto
    ├── appearance: AppearancePreferencesDto
    │   ├── mode: Light | Dark | System
    │   ├── paletteId: builtin:* | custom:*
    │   ├── accentOverride: #RRGGBB?
    │   └── customPalettes: ThemePaletteDto[]
    ├── systemStyleId: relaxkonos.windows-like
    └── shell: ShellSelectionDto { shellId, packageId?, packageVersion? }
```

`ThemePreferencesDto` 已删除；`ThemePaletteDefaults.Resolve/ResolveCustom` 与 `ThemePaletteImport`
改用 `AppearancePreferencesDto`。按仓库 `AGENTS.md` 的“首个正式发布前不保留兼容层”：

- 不保留别名、可选旧字段、双读双写、旧 JSON 回退或路由兼容层。
- 同一变更已更新 Client、Server、Protocol、设置 UI、测试与文档。
- JSON 契约测试断言线格式**不含** `styleId`、不含顶层 `"theme":`，且保留了
  `systemStyleId` / `appearance` / `shell`（见 §9）。

服务端 `WorkspacePreferencesValidator`：`TryNormalizeDesktopExperience` 校验 **`systemStyleId` 的格式**
（`^[a-z0-9][a-z0-9.-]{2,127}$`），**不校验可用性**——可用性是设备本地事实，不能因某台机器未安装而拒绝保存；
`shell` 校验格式。`TryNormalizeAppearance` 校验模式/调色板/强调色。

---

## 9. 设置中心拆分

个性化页由单一“主题”下拉拆成三张卡片（`PersonalizationPageView.axaml` + `PersonalizationPageViewModel.cs`）：

1. **颜色与模式**：模式（浅/深/跟随系统）、调色板、强调色、自定义调色板导入导出。
2. **系统风格**：风格下拉（`SystemStyleChoices`）、当前风格摘要（`SystemStyleSummary`）、
   不可用提示（`SystemStyleProblem` / `HasSystemStyleProblem`）、
   “采用此 Shell 推荐的系统风格”按钮（`ApplyRecommendedStyle`，由 `IsUsingRecommendedStyle` 控制可用性）。
3. **桌面布局**：Shell 选择，附“与系统风格相互独立”的说明。

`SettingsViewModel.Navigation.cs` 的本地搜索条目由 `workspace.theme` / `workspace.shell`
改为 `workspace.colors` / `workspace.systemStyle` / `workspace.desktopLayout`（含中英日同义词）。

本地化 `Localization/{zh-CN,en-US,ja-JP}/settings.json` 新增：
`settings.colors_and_mode`(+description)、`settings.palette_scope_hint`、`settings.system_style`(+description、
`windows_like`/`macos_like`/`ubuntu_like`、`unavailable_format`、`apply_recommended`、`independent_hint`、`token.*`)、
`settings.desktop_layout`、`settings.shell.separate_hint`，以及全部 `systemstyle.*` 问题码文案。

---

## 10. 已实施落点（Phase 1）

| 层 | 文件 |
|---|---|
| 协议 · 新 | `Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/`（`SystemStyleRecipes` / `SystemStyleTokenContract` / `SystemStyleManifestDto` / `SystemStyleManifestValidator` / `BuiltInSystemStyles`） |
| 协议 · 新 | `Workspace/AppearancePreferencesDto.cs`、`Workspace/DesktopExperiencePreferencesDto.cs` |
| 协议 · 改 | `Workspace/WorkspacePreferencesDto.cs`（去掉顶层 `Theme`/`Shell`，加 `DesktopExperience`）、`ThemePaletteDefaults.cs`、`ThemePaletteImport.cs` |
| 协议 · 删 | `Workspace/ThemePreferencesDto.cs` |
| UI · 新 | `Themes/Tokens/SystemStyleTokens.axaml`、`Themes/SystemStyle/SystemStyleResourceBuilder.cs` |
| UI · 改 | `Themes/Tokens/TokenContract.axaml`、`Themes/RelaxKonOSTheme.axaml`、`Themes/Styles.axaml`、`RelaxKonOS.UI.csproj`（引用 Protocol） |
| WindowManager · 改 | `Themes/RemoteWindowTheme.axaml` |
| 客户端 · 新 | `Services/Theming/AppearanceService.cs`、`Services/Theming/SystemStyleRegistry.cs` |
| 客户端 · 删 | `Services/Theming/ThemeService.cs` |
| 客户端 · 改 | `Services/ShellSettings.cs`（`Appearance` + `SystemStyleId`，`IsDarkTheme` 取自 `Appearance.Mode`，`HasUnavailableSystemStyle` / `AppliedSystemStyleId`）、`Services/Bootstrapper.cs`、`App.axaml.cs`、`Apps/Settings/**` |
| Server | `Settings/WorkspacePreferencesValidator.cs` |
| 测试 | `Client/RelaxKonOS.Settings.Tests/SystemStyleChecks.cs`（新增）、`RelaxKonOS.Server.Tests/Program.cs`、`RelaxKonOS.Server.Tests/SettingsSystemVerification.cs` |

### 10.1 测试覆盖（`SystemStyleChecks`）

纯契约校验（不依赖 Avalonia/UI、不打开显示器、不访问宿主），覆盖：

- recipe 闭集与槽位映射；
- 三套内置 profile 完整（令牌齐全、recipe 合法）且彼此存在**可测差异**；
- 校验器拒绝：非法 schema、id 违规、未知 recipe、未知令牌、越界值、缺必需令牌、命中目标过小、不支持减少动态效果；
- 令牌解析：默认补齐、浅深变体覆盖优先级；
- 外观仅含颜色（无 `StyleId`，令牌键不得以 `Color`/`Brush` 结尾，清单不得出现颜色形字段）；
- `RecommendedForShell` 映射；
- 线格式往返：JSON 不含 `styleId` 与顶层 `"theme":`，保留 `systemStyleId`/`appearance`/`shell`；
- **外置包门禁**（Phase 5）：来源必须属于已知集合、包归属必填、冒充 `builtin` 被拒、
  更高 schema / 更高 `minimumHostApiVersion` 被拒；
- **recipe 覆盖率**（Phase 5）：遍历四个槽位的全部允许值，
  在消费文件中查找对应伪类/class/常量，任一缺失即失败；
- **产品 UI 硬编码颜色扫描**（Phase 5）：扫描 `Client/RelaxKonOS.Client`、`Framework`、`examples`
  的 `.axaml`/`.cs`（排除 `obj`/`bin`/`*.g.cs`），命中不得超过豁免表；

### 10.2 硬编码颜色豁免表（Phase 5 扫描的事实基线）

豁免必须逐条说明为什么它**不是主题颜色**。表中数字是当前命中数，**只能减少不能增加**：

| 文件 | 命中 | 为什么不是主题颜色 |
|---|---|---|
| `Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml` | 2 | 两处 `WindowShadow` 的**首帧兜底**值；运行时由 `AppearanceService` 用调色板 `Shadow` 合成覆盖 |
| `Client/RelaxKonOS.Client/Services/ShellSettings.cs` | 27 | 9 套壁纸渐变（壁纸是内容，不是 chrome） |
| `Client/RelaxKonOS.Client/Apps/Terminal/TerminalAppearance.cs` | 12 | 终端配色方案（ANSI/终端内容本身） |
| `Client/RelaxKonOS.Client/Apps/TaskManager/ViewModels/TaskManagerViewModel.cs` | 4 | 性能图表的逐资源系列色（数据可视化系列） |
| `Client/RelaxKonOS.Client/Apps/TaskManager/Controls/PerformanceLineChart.cs` | 1 | 图表线条默认色（同上） |

`Shared/RelaxKonOS.Protocol/Workspace/ThemePaletteDefaults.cs` 与 `TerminalSettingsDto.cs` 是颜色的**真源**与终端协议，
不在扫描范围内（规则约束的是「可主题化的 UI」，不是「颜色的定义处」）。

运行：

```bash
dotnet run --project Client/RelaxKonOS.Settings.Tests/RelaxKonOS.Settings.Tests.csproj --no-restore -c Release -p:UseSharedCompilation=false
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --settings-only
```

> 注意：`Client/RelaxKonOS.Settings.Tests` **不在** `RelaxKonOS.sln` 中，
> 因此 `dotnet build RelaxKonOS.sln` 不会编译它，上述命令必须显式执行。
> 仓库目前没有 `.github/` 工作流；这套校验是「可执行的 CI 规则」，需要由流水线显式调用。

---

## 11. 验收与未验证项

### 11.1 已验证

- **构建**：Protocol / RelaxKonOS.UI / RelaxKonOS.WindowManager / Client / Server /
  PrivilegedHelper / DevCli / 三个示例；`dotnet build RelaxKonOS.sln -c Debug -m:1` → 0 warning · 0 error
  （Server `-t:Rebuild` 的 2 个 CA1416 为起点既有，非本次引入）。
- `RelaxKonOS.Settings.Tests`（Release）全通过，输出：
  `System style verification passed: closed recipe vocabulary, three complete built-in profiles, manifest rejection, token resolution, colour-only appearance, wire round-trip, external-package gate, recipe coverage, no hardcoded colours in product UI.`
- `RelaxKonOS.Server.Tests --settings-only` 全通过（设置域全部子项）。
- `SystemStyleResourceBuilder` 产出的字典包含全部 33 令牌 + 5 派生复合键 + 11 命令面厚度键 + 4 recipe 键。
- 无 `StyleId` / `ThemePreferences` / `ThemeService` 残留引用（`grep` 全仓为零，文档已同步）。

### 11.2 尚未验证 / 未实施（如实记录）

- **未做任何视觉验收。** 本仓库的验证只到编译与契约层：没有启动客户端截图，
  没有在浅/深/System 三种模式、三套风格、三个内置 Shell 下逐一核对观感。
  **「Phase 0–5 已实施」不等于「视觉已验收」。** 待执行的回归矩阵见 §11.3。
- **概览的层叠与输入规则只经过代码审查**：`Win+Tab`/`Alt+Tab` 是否会先被宿主窗口收到
  取决于平台与焦点位置（Windows 上 `Win+Tab` 由 OS 的任务视图优先捕获），
  因此「快捷键一定能触发概览」**未在真实平台上验证**；任务栏「任务视图」按钮与
  `IShellActions.ShowWindowOverview()` 是可靠的入口。
- **`Alt+Tab` 未做「按住 Alt 连续切换、松开 Alt 才激活」的会话语义**：
  当前每次 `Alt+Tab` 立即切换一个窗口（等价于快速切换），没有按住不放的预览态。
- **macOS-like / Ubuntu-like 桌面没有可视化的任务切换入口**：这两种桌面本来就不放按钮
  （Mission Control 用 F3/手势，GNOME 用 Super），当前只由键盘快捷键触发，且 **F3/Super 未接线**。
- **概览卡片始终使用图标 + 标题降级形态**：`IsThumbnailAvailable` 恒为 `false`，
  没有实现任何受控缩略图，也没有验证「窗口很多时」的网格观感。
- **`ReducedMotion` 仍是显式开关**，未接入平台「减少动态效果」设置；
  不过 `SystemStyleResourceBuilder` 已把**所有** `Duration` 令牌折叠为 `ReducedMotionDuration`，
  因此一旦该开关打开，概览入场动效等会自然退化为瞬时。
- **外置 style manifest 在生产上未接通**：门禁已实现且已测试，但没有包加载器，
  按 Plan Phase 5 第 3 条，第三方 manifest 在有完整回归数据前保持关闭。
- **未做**高 DPI、窄窗口、触摸、低性能、三平台（Windows/macOS/Linux）实机回归。
- 跨平台宿主配置与远程实机验收未执行。

### 11.3 待执行的视觉回归矩阵（Phase 5 第 2 条的落地清单）

| 维度 | 取值 |
|---|---|
| 系统风格 | Windows-like / macOS-like / Ubuntu-like |
| 颜色模式 | Light / Dark / System（跟随 OS） |
| 组件 | 窗口 chrome（活动/非活动/全屏/无阴影）× 命令菜单 × 概览 × 全屏 × 模态 |
| 桌面 | 三个内置 Shell + Windows 11 示例 Shell |
| 环境 | 100% / 150% / 200% DPI、窄窗口（≤ 800px）、触摸、纯键盘 |
| 可访问性 | 焦点环可见、最小命中区 ≥ 40px、`ReducedMotion` 打开时无缩放入场依赖 |
| 平台 | Windows / macOS / Linux |

---

## 12. 后续工作要求

Plan §8 的五个阶段均已落地。仍未完成的是**验收性**工作，不是功能性工作：

1. **执行 §11.3 的视觉回归矩阵**，并把结果（含截图）补进本文 §11.1；
   在此之前不要把「已实施」对外表述为「已验证」。
2. **接通平台「减少动态效果」设置**，使 `AppearanceService` 不必依赖显式开关。
3. **为 macOS-like / Ubuntu-like 补上键盘入口**（F3 等），或在文档中明确只支持键盘快捷键的现状。
4. **概览缩略图**：若要实现，必须走受控快照路径——不得为取缩略图重建应用、泄漏隐藏窗口、
   绕过 WebView 安全限制或阻塞 UI 线程（Plan §7.1 的原文约束）。
5. **第三方 style manifest 的开放门槛**：先完成 §11.3 回归，再接入包加载器
   （`SystemStyleRegistry.Register(manifest, isBuiltIn: false)` 已就绪）。


每阶段结束应保持可编译、可运行、可切换；不得先大规模复制风格页面或只完成某一个 Shell。
