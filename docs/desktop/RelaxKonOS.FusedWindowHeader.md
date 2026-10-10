# 桌面端融合窗体头部（TitleBarContent）接入计划

建立日期：2026-10-10。分支：`windows_enhance`。

本文是这项工作的**唯一进度与恢复入口**。中断后请先读第 1 节的「状态总览」和第 9 节的「进度日志」，再按第 7 节「恢复工作指引」继续，不要凭记忆重排批次。

**方案已定：A（宿主多段式角色槽）**，第 8.3 节的选择、第 8.4 节的落地清单与第 8.5 节的第 4 槽扩展均已执行完毕（见 §9 的 2026-10-10 日志）。
宿主契约现在是**四个角色槽**：应用只交 `HeaderLeading` / `HeaderCenter` / `HeaderTrailing` / `HeaderTabs`，由 recipe 决定它们与宿主按钮组的列分配。
因此第一批 9 个窗口接入时**不要再写任何列、留白或融合条高度**——那些都属于宿主。旧的单一 `TitleBarContent` 契约已删除，没有并存形态。

## 1. 状态总览

| 批次 | 范围 | 窗口数 | 状态 |
|---|---|---|---|
| 前置 | 宿主角色槽 + 各 recipe 的融合条与交通灯（方案 A，见 §8） | — | **已完成** |
| 前置 | 第 4 槽 `HeaderTabs`：多标签窗口的形状（见 §8.5） | — | **已完成** |
| 第一批 | 与设置同构的 `Border.app-header` 工作区 | 9 | 未开始 |
| 第二批 | `Border.app-toolbar` 菜单 / 地址栏型视图（其中 2 个带标签条） | 5 | 未开始 |
| 顺带 | `FileServicesWorkspace`（本就没有头部） | 1 | 未开始 |
| 不做 | 模态对话框、登录窗口、远端桌面 Shell、资源管理器选择器模式 | — | 已定（见 3.4） |

**目标（一句话）**：把内置应用窗口内容区里那条与窗口标题栏重复的身份条搬进宿主标题栏，使每个窗口只有一层头部。

**量化收益**：`Border.app-header`（`Framework/RelaxKonOS.UI/Themes/ApplicationStyles.axaml:14`）实高 78px（`Padding=24,16` + 46px 图标），叠在 42px 窗口标题栏（`Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml` 的 `WindowTitleBarHeight`）上共 120px；融合后由单层 `WindowFusedTitleBarHeight`（`max(48, WindowTitleBarHeight + 14)`：Windows-like 56 / macOS-like 52 / Ubuntu-like 60）替代，Windows-like 下每个窗口省约 64px。默认窗口 820×560（`Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs:80`）下约占窗口高度 11%。

## 2. 机制（开工前必读，改模板前再读一遍）

应用窗口的 `ManagedWindow.View` 是 `RemoteWindow`。它与应用之间只有一个契约：**四个角色槽**。

- `RemoteWindow.HeaderLeading` / `HeaderCenter` / `HeaderTrailing` / `HeaderTabs`（`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`）。
  任一非空 → 置上 `:custom-title-content` 伪类，宿主自己的窗口图标与标题让位；单独声明 `HeaderTabs` 也足以触发融合
  （多标签窗口里，标签就是标题）。
- 标题栏网格是 `Auto,Auto,*,Auto,Auto`（`Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`）：
  `[按钮组或图标][leading][center * 与 tabs][trailing][按钮组或图标]`。**每条 recipe 都把七个部件显式写出**
  （图标 / 标题文本 / 四个角色槽 / 按钮组），因此不存在「落到默认恰好正确」的隐式分支。
- **应用永远不写列、不写留白、不写融合条高度**：槽的列分配与 `Margin`、融合条高度
  （`WindowFusedTitleBarHeight`）与底色全部由 recipe 给出。新增一条 recipe = 宿主加一组规则，应用零改动。
- **`HeaderTabs` 与 `HeaderCenter` 争同一个弹性列**，规则是宿主的：声明了标签条就置上 `:header-tabs`，
  并让居中槽 `IsVisible=False`。应用不该同时交两者；宿主兜住这个组合，避免两块叠在同一格。
  标签条自身高度读可选令牌 `WindowTabHeight`（默认 28），不写像素字面量。
  目前的实现把标签条画在栏内（内联），但**「内联还是另起一行」由 recipe 决定**——将来某条 recipe 要把标签条
  压到栏外一行，只需加宿主规则，应用零改动。见 §8.5。
- 窗口按钮（`PART_WindowControls`）始终由宿主拥有：`caption-buttons-right` / `headerbar-right` 排在最右列，
  `traffic-lights-left` 排在最左列，并画成红（关）/ 黄（小）/ 绿（放大）三枚圆点，字形仅在指针或键盘进入按钮组时显现。
- 接入入口沿用设置已跑通的模式：视图提供 `AttachWindowHeader(ManagedWindow)`，由 `*App.Activate` 在
  `context.ShowWindow(...)` 之后调用，实现里只有几行赋值。参考实现：
  `Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml.cs`（槽内容拆成 `SettingsHeaderView`＝leading「返回 + 标题」
  与 `SettingsSearchBarView`＝center「搜索框」）、`Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs:82`。

约束（`AGENTS.md`）：接口变化不保留兼容别名或双形态，同一次改动里改完仓库内所有调用者、测试与文档。

## 3. 范围

### 3.1 第一批：`Border.app-header` 工作区（9 个，与设置同构）

共同现状：窗口内容区第一行是一条 `Border.app-header`，内含 46px 应用图标 + `TextBlock.app-title` + `TextBlock.app-description`，部分窗口右侧还挂一个刷新按钮或状态胶囊。且多数窗口内容区第一行就是 `Grid RowDefinitions="Auto,*,Auto"`，删掉第 0 行即可。

| # | 窗口 | 视图文件（`Client/RelaxKonOS.Client/`） | 头部右侧内容 | 状态 |
|---|---|---|---|---|
| 1 | 服务器中心 | `Apps/ServerCenter/Views/ServerCenterWorkspace.axaml` | 无（仅 `Title`/`Subtitle` 绑定） | 未开始 |
| 2 | Git | `Apps/Git/Views/GitClientWorkspace.axaml` | 分支胶囊 + 刷新（`IsPickerMode` 时隐藏） | 未开始 |
| 3 | Docker | `Apps/Docker/Views/DockerManagerWorkspace.axaml` | 状态胶囊按钮（可打开安装向导） | 未开始 |
| 4 | 代理 | `Apps/Proxy/Views/ProxyManagerWorkspace.axaml` | 刷新按钮 | 未开始 |
| 5 | 隧道 | `Apps/Tunnels/Views/TunnelManagerView.axaml` | 刷新按钮 | 未开始 |
| 6 | 端口转发 | `Apps/PortForwarding/Views/PortForwardingMainView.axaml` | 刷新按钮 | 未开始 |
| 7 | 网站 | `Apps/WebServers/Views/WebServerManagerWorkspace.axaml` | 无 | 未开始 |
| 8 | 证书 | `Apps/Certificates/Views/CertificateManagerWorkspace.axaml` | 无 | 未开始 |
| 9 | 应用部署 | `Apps/ApplicationDeployments/Views/ApplicationDeploymentsWorkspace.axaml` | 无 | 未开始 |

排序理由：先做 #1（头部只有两个文本，零信息损失，验证模式）与 #2（同时验证「标题栏内放交互控件」，且分支胶囊是这套里最有表现力的元素），跑通后 #3–#9 基本是机械替换。

### 3.2 第二批：`Border.app-toolbar` 视图（6 个，其中 3 个带标签条）

这些不是标题重复，而是菜单条 / 地址栏。融合后窗口按钮、拖拽区、菜单、地址栏要挤同一行，收益更高但正确性风险也更高，故排在第一批之后。

| # | 窗口 | 视图文件 | 现头部 | 标签条 | 状态 |
|---|---|---|---|---|---|
| 10 | 资源管理器 | `Apps/Explorer/Views/ExplorerMainView.axaml` | `DockPanel.Dock="Top"` 的导航 + 地址栏（`app-toolbar`） | 暂无（计划加多标签） | 未开始 |
| 11 | 代码编辑器 | `Apps/CodeEditor/CodeEditorView.axaml` | 菜单栏 + 文档标签 | **已有** `ListBox.editor-tabs` | 未开始 |
| 12 | 记事本 | `Apps/Notepad/NotepadView.axaml` | 菜单栏 + 文档名 | 无 | 未开始 |
| 13 | 任务管理器 | `Apps/TaskManager/Views/TaskManagerMainView.axaml` | 工具条 | 无 | 未开始 |
| 14 | 图片查看器 | `Apps/ImageViewer/Views/ImageViewerView.axaml` | 查看工具条 | 无 | 未开始 |
| 15 | 浏览器 | `Apps/Browser/Views/BrowserMainView.axaml` | `DockPanel.Dock="Top"` 的标签条 + 导航工具条与地址栏 | **已有** `ListBox.browser-tabs` | 未开始 |

必读注意：

- **带标签条的窗口（#11 / #15，将来 #10）一律走 `HeaderTabs` 角色槽**，不要各自 `DockPanel.Dock="Top"` 排一行：
  那在 macOS 风格窗口里会是一条 Windows 形状的带子（详见 §8.5）。标签条高度读 `WindowTabHeight`，
  选中态形状（现在是应用内写死的 `CornerRadius="12,12,0,0"`，Chrome 式上圆下方）要改成 token 驱动，否则外观扫描会拦。
- **#10 同时是文件选择器宿主**。选择器模式（`ExplorerPickerOptions`）必须继续走旧路径——标题栏要说明「选择文件夹」这个任务，而不是应用身份。先确认调用方如何区分两种模式，再决定是同一个视图内分支，还是只在主窗口模式接入。
- **#11 / #12 带草稿守卫**（`DraftWindowCloseGuard` / `DraftDialogGuard`，见 `docs/desktop/UiReview.md` D-009）。迁移头部时不要改关闭语义，也不要让新头部吃掉草稿提示。
- #11 / #12 的融合形态等价于「菜单栏搬上标题栏」（VS Code、Win11 记事本的做法），是这套改动里视觉变化最大的一组，需要浅深色 + 三语单独截图确认。

### 3.3 顺带：`FileServicesWorkspace`

`Apps/FileServices/Views/FileServicesWorkspace.axaml` 根本没有 `app-header`，只用 `Margin="20"` 加一个 `PlatformText` 当标题。它不是「该不该融合」，而是这批里唯一没有头部的，观感最脱节，借这次统一。

### 3.4 明确不做

| 对象 | 原因 |
|---|---|
| 模态对话框（`ConfirmDialogView`、`TextInputDialogView` 及各 `*DialogView`） | 窗口本来就小，没有多余的条带可省；融合会丢掉模态层级与任务标题 |
| `LoginWindow` | 自己绘制窗口、没有系统装饰 |
| 远端桌面 `MainWindow` | 标题栏是 `WindowTitleBar` 与 mstsc 连接条的共享顶层，承担全屏切换（`_isFullScreen` 时整条隐藏）与断开操作，语义完全不同 |
| 资源管理器的选择器模式 | 见 3.2 #10 |

## 4. 验收标准

每个窗口接入后逐条确认，缺一条不算完成：

1. 头部块交给对应的**角色槽**（leading / center / trailing / tabs）；窗口图标与宿主的标题被模板隐藏，**窗口按钮仍在宿主那一侧**——由 recipe 决定在左还是在右，应用侧不需要也不应该知道。
   带标签条的窗口（见 §3.2）交 `HeaderTabs`，**不要**自己 `DockPanel.Dock="Top"` 排一行；声明标签条后宿主会让 `HeaderCenter` 让位，所以同一窗口不要同时交这两者。
2. 头部空白区可拖动窗口，**头部内的交互控件不吃拖动**：点击按钮 / 在输入框打字时窗口位置不变（参考断言见第 7 节）。
3. 内容区不再有第二条 `app-header` / `app-toolbar`，`Grid RowDefinitions` 同步删行，不留空行。
4. 三语言（`zh-CN` / `en-US` / `ja-JP`）下标题与操作不溢出、不换行错位。
5. 浅色与深色主题下文字对比度达标；头部与侧栏（`Border.app-sidebar`，`SurfaceRaisedBrush`）、状态栏的层级过渡自然。**注意融合后标题栏背景由模板强制改成 `SurfaceBrush`**（原先 `app-header` 是 `SurfaceRaisedBrush` + 下边框），这是最容易看起来「头部凹进去」的地方。
6. 窄窗口（建议 640×480，以及极窄 320×240）下头部不裁切、操作仍可达。
7. 三套窗口样式都复验：`caption-buttons-right`、`traffic-lights-left`、`headerbar-right`。**用 `settings.SystemStyleId = …` 直接切换**（`ShellSettings` 会立刻重装资源栈）；融合条会随风格变高（56 / 52 / 60），按钮组在左时 leading 自动落在按钮组右侧，`headerbar-right` 的融合条底色是 `SurfaceRaisedBrush`。
8. 任务栏与窗口总览里的窗口标题不变（融合只影响标题栏内部，`Title` 仍由 `ShowWindow` 设置）。
9. 在 `Tests/Client/RelaxKonOS.WindowPreviews.Tests` 里为该窗口补一条断言（模板见第 7 节）；形态跨 recipe 都要成立的窗口，照 `FusedWindowHeaderChecks.cs` 的写法补「三条 recipe」的断言与截图。
10. 更新本文第 3 节状态列与第 8 节进度日志；若该窗口在 `docs/desktop/UiReview.md` 有对应条目，同步其结论。

## 5. 开放问题

1. ~~**头部高度**~~ **已定**：不再硬编码。融合条高度取派生键 `WindowFusedTitleBarHeight = max(48, WindowTitleBarHeight + 14)`，随风格得到 56 / 52 / 60；应用读同一个键，不自己写数字。
2. ~~**居中基准**~~ **已定**：center 槽占标题栏的弹性列，即「leading 与 trailing 之间、宿主按钮之外的可用中段」。
   **不**把 center 锚到整条标题栏的几何中心——那样窄窗口下搜索框会压到宿主按钮上（640 宽时 520 的搜索框居中后与右侧按钮组重叠约 24px），
   而弹性列会随可用宽度收缩，永不重叠。副作用是 leading 与 trailing 不等宽时 center 会偏移（macOS-like 下约 +65px），这是可接受的取舍。
3. **图标怎么处理**：融合会隐藏宿主窗口图标（`PART_WindowIcon`），所以 `app-header` 里的 46px 图片要么删除、要么缩到标题旁的 16–20px。逐个窗口决定并记录，别一半删一半留。
4. **共享头部控件**：9 个同构窗口不要各写一份。倾向做一个可参数化的头部（图标 / 标题 / 副标题 / 右侧操作槽），参数差异用属性表达。**在第一、二个窗口做完后再抽，避免过早抽象。**
5. **第二批菜单融合的降级策略**：窄窗口下菜单是折叠成汉堡菜单，还是退回内容区一行？先看 #11 的实际宽度再定。
6. ~~**多标签窗口的头部**~~ **已定**：加**第 4 个角色槽 `HeaderTabs`**，由宿主放在标题栏的弹性列；声明标签条时宿主同时让
   `HeaderCenter` 与自己的标题文本让位（`:header-tabs`）。标签条高度读可选令牌 `WindowTabHeight`（默认 28）。
   **「内联在栏里」还是「栏外另起一行」由 recipe 决定**，目前三条 recipe 都是内联。契约见 §2，决策与平台依据见 §8.5。
   已施工完成（2026-10-10），**赶在第一批接入之前**，因此 9 个窗口不必回改。
7. **标签条的窗口级 / 视图级语义**（新，未定，且**不影响布局契约**）：macOS 的原生窗口标签是**窗口级**——一个标签等于一个窗口，
   拖出成新窗口、`窗口 > 合并所有窗口` 可合并；Windows 资源管理器是**视图级**——标签是窗口内的一次视图切换。
   本实现取**视图级**（与 VS Code / Chrome / Edge 一致，业界通用做法）。若将来要做真正的窗口级标签，
   那是窗口管理器的职责（给 `RemoteWindow` 加标签组），**不属于本计划**，也不会改动 `HeaderTabs` 契约。

## 6. 与既有文档的关系

- `docs/desktop/RelaxKonOS.Settings.md`（第 26 行）与 `docs/desktop/RelaxKonOS.Settings.Windows11.Progress.md` 记录了设置的顶栏融合，是本模式的先例与验收参考。
- `docs/desktop/RelaxKonOS.BuiltInApps.UI.md`、`docs/desktop/RelaxKonOS.Desktop.md` 是设计规则来源；本文只做接入计划，冲突时以设计规则为准。
- `docs/desktop/UiReview.md` 是本专项的审查清单与 D-xxx 决定记录（D-009 顶层窗口关闭守卫与本工作有交集）。

## 7. 恢复工作指引

环境与构建（本机 Windows / Git Bash；PATH 被 shim 破坏，命令开头先补）：

```bash
export PATH="/usr/bin:/bin:/c/Windows/System32:/c/Program Files/Git/cmd"
cd /d/RelaxKon/RelaxKonOS
git status --short          # 用户可能并行改同一仓库，动别人的文件前先看
git rev-parse --abbrev-ref HEAD   # 期望 windows_enhance
```

编译校验与运行窗口预览检查（该工程是控制台 exe，运行即整套断言；`-o` 用独立目录避开正在运行的客户端锁 bin）：

```bash
"/c/Program Files/dotnet/dotnet.exe" build \
  Tests/Client/RelaxKonOS.WindowPreviews.Tests/RelaxKonOS.WindowPreviews.Tests.csproj \
  -c Debug -o D:/tmp/rk-preview
/d/tmp/rk-preview/RelaxKonOS.WindowPreviews.Tests.exe
```

可用 flag（`Tests/Client/RelaxKonOS.WindowPreviews.Tests/Program.cs:94,103`）：`--ui-review-only` 在设置交互检查前返回；`--settings-interaction-only` 在设置类检查后返回。改单个窗口时用 flag 缩短回路，收尾再跑完整套件。

输出是 GBK，判定中文结论不要用 grep/tail，用受管 Python（本机 `python` 不在 PATH，直接用绝对路径）：

```bash
"/c/Users/betha/.workbuddy/binaries/python/versions/3.13.12/python.exe" -c \
  "d=open(r'D:/tmp/rk-preview.txt','rb').read().decode('gbk','replace'); print(d.count('PASS:'), 'Exception' in d)"
```

断言失败时进程以非 0 退出且后续断言不再执行，要看最后一行的异常消息，别以为只是「报告没找到」。

**断言模板**（复制它，替换成新窗口）：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/SettingsWindowChecks.cs`——在 `Canvas` 上 `windows.Attach` 后 `Create` 一个 `ManagedWindow`，调用 `view.AttachWindowHeader(managed)`，然后断言：

- `ReferenceEquals(managed.View.HeaderLeading, view.Header)`（center / trailing 同理；用不到的槽必须显式写成 `null`）
- `managed.View.HasTitleBarContent`，且 `PART_TitleBar.Bounds.Height == managed.View.FindResource("WindowFusedTitleBarHeight")`
  ——**别写死 56**，融合条高度随风格变（56 / 52 / 60）
- 在头部输入框打字后 `managed.Info.Bounds` 不变（证明交互控件没吃拖动）
- 截图落盘（`RenderTargetBitmap` → `preview-qa`），供浅深色与三语人工比对

跨 recipe 的形态另有一份现成模板：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/FusedWindowHeaderChecks.cs`——
切 `settings.SystemStyleId`，断言槽列（1/2/3）、按钮组所在列（0 或 4）、按钮顺序与配色、融合条几何、以及「拖拽不被槽吃掉」，并按 recipe 落盘截图。

常看的文件：`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`、`Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`、`Framework/RelaxKonOS.UI/Themes/ApplicationStyles.axaml`、`Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml.cs`（参考实现）。

## 8. 系统风格 × 融合头部：查证结论与优化方向

2026-10-10 追加。起因：设置的融合顶栏在 `macOS 风格` 下明显不对（见下 D1–D2），而风格被认为可以随桌面程序扩展，因此不能只针对 macOS 打补丁。

### 8.1 查证：安装桌面程序会不会加系统风格 / 系统布局

**结论：不会加系统风格，只会加桌面布局（shell）。**

- `.roapp` 桌面包的 `manifest.json` 只有 `packageType: "desktopShell"`、`entryAssembly`、`entryType`、`shellApiVersion`、`capabilities`、`requestedPermissions`、`localizedMetadata` 等字段（见 `examples/Windows11DesktopShell/manifest.json`），**没有任何风格 / 窗口 chrome 字段**。桌面布局经 `ShellCatalog` 从统一软件包目录发现（`Client/RelaxKonOS.Client/Services/ShellCatalog.cs:25`）。
- 系统风格是另一条独立的轴：`DesktopExperiencePreferencesDto` 里 `appearance` / `systemStyleId` / `shell` 三者独立存储、自由组合。
- `SystemStyleRegistry` 的构造函数**只**登记 `BuiltInSystemStyles.All`（3 个：windows-like / macos-like / ubuntu-like，`Client/RelaxKonOS.Client/Services/Theming/SystemStyleRegistry.cs:49`）。`Register(manifest, isBuiltIn: false)` 的外置路径在本构建里**没有任何调用方**，代码注释写明「Nothing in this build feeds the external path yet」。
- 文档同口径：`docs/desktop/RelaxKonOS.SystemStyle.md:246`「**本构建尚无包加载器**……外置路径是已实现、已测试、但生产上未接通」；第 541、571 行重复确认。开放第三方 manifest 是 Phase 5 的门槛，前置条件是先做完 §11.3 回归矩阵。

**更关键的一点（决定了优化方向）**：即使将来接通外置风格，**风格清单也不能带来新的窗口 chrome 布局**。

- 风格清单是纯数据、只读的封闭契约：`SystemStyleManifestDto` 只有 `supportedRecipes`（四个槽位各选一个常量）+ 数值 `tokens`（`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleManifestDto.cs:48`）。没有任何 XAML / 程序集 / 资源 URI / 事件处理器的口子。
- `WindowChromeRecipes` 是**封闭三值**：`caption-buttons-right` / `traffic-lights-left` / `headerbar-right`（`SystemStyleRecipes.cs:10`），校验器按 `Allowed(kind)` 拒绝表外取值。
- 逻辑推论：**「未知风格」不存在，只有「当前三个 recipe」和「宿主将来新增一个 recipe」两种情况**。所以不需要为任意布局做准备，需要的是让「按钮在哪一侧」成为宿主发布的语义，新增 recipe 时只改宿主、不改应用。

### 8.2 现状缺陷（macOS 风格下的融合顶栏）

| 编号 | 现象 | 根因 |
|---|---|---|
| D1 | `− ▢ ×` 与「← 设置」挤成一串，左侧没有留白 | 头部自己的 `Margin="12,4"` 是应用区与按钮之间唯一的间隔 |
| D2 | 右半条完全空白；搜索框无法相对窗口居中 | `SettingsHeaderView` 的 `Grid ColumnDefinitions="Auto,*,Auto"` 第三列是空的——它是为「默认 recipe 把尾侧留给宿主按钮」而写的 |
| D3 | `:custom-title-content` 假设了默认 recipe 的列分配，再单独写一条 `:custom-title-content:chrome-traffic-lights-left` 挪列；`headerbar-right` 没有任何显式规则，靠落到默认布局「恰好正确」 | 模板把「recipe × 是否融合」写成了笛卡尔积，6 种组合只有 2 条真实规则（`RemoteWindowTheme.axaml:179-192`） |
| D4 | 标题栏高度 56 与底色 `SurfaceBrush` 对所有 recipe 一刀切 | 同上，融合态的两个属性没有随 recipe 变化的表达 |

D4 顺带牵出 `traffic-lights-left` 自身的观感问题（按钮仍是 `− ▢ ×` 的 Windows 字形与顺序、无红黄绿），那属于 recipe 而不属于融合头部，但建议同批处理，否则「没考虑 macOS」只修一半。

### 8.3 优化方向（已选 A，2026-10-10 落地）

> **2026-10-10 第三轮更新**：本节与 §8.4 是第二轮（三段式槽）的记录。§8.5 把它扩展为**第四个角色槽 `HeaderTabs`**，
> 因此下面出现的「三个角色槽」「六个部件」在**当前**代码里分别是「四个角色槽」「七个部件」。保留原文以留改动轨迹，不复写。

**✔ 方案 A · 宿主提供三段式标题栏内容槽**（已实现）

`RelaxKonOS.WindowManager` 暴露三个角色槽，**不暴露布局**：

```text
RemoteWindow.HeaderLeading / HeaderCenter / HeaderTrailing   （均为 Control?）
```

模板按 recipe 决定这三块与 `PART_WindowControls` 的先后与列分配，和 `PART_WindowIcon` / `PART_TitleText` / `PART_WindowControls` 被 recipe 重新部署的方式完全一致。应用只交三块内容，永远不写「我在第几列」。

落地形态与最初设想有一处偏差，如实记录：**没有新增「头部控件」类型，而是把三个槽做成 `RemoteWindow` 自己的三个 `StyledProperty` + 模板里的三个 `ContentPresenter`。**
原因是按钮组必须与槽位处在**同一个网格**才能被 recipe 交错排布；若把槽装进一个子控件，宿主那唯一一份已审查的标题栏结构就得拆成两份（按钮组要么搬进子控件，要么与子控件重叠）。
契约效果与设想完全相同（应用只认角色），而需要审查的模板仍是一份——这与「`PART_WindowIcon` / `PART_TitleText` / `PART_WindowControls` 本来就是 `RemoteWindow` 的属性」是同一条道理。

- 3 条内置 recipe 各自把六个部件全部显式写出；新增 recipe 只加一组宿主规则，**9 个工作区零改动**。
- 代价（已付）：`TitleBarContent` 这条公开契约被删除（按仓库约定不留并存形态）；`SettingsHeaderView` 拆成两个槽内容视图。

**方案 B · 宿主只发布「标题栏留白侧」语义**（未采用）
保留应用自绘头部，宿主把 recipe 折算成伪类 `:caption-leading` / `:caption-trailing` 加两个厚度令牌，头部把 `Margin` 绑上去。
好处是不动模板结构；代价是应用仍自绘、仍可能忘记消费令牌，且出现「按钮两侧都有」的 recipe 时还要再加令牌。

**方案 C · 给融合头另开 recipe 变体**（否决）
例如再加 `traffic-lights-left-compact`。这会把封闭集合从 3 乘成 3×N，且外置风格一旦选中某个变体就可能渲染不了，与「风格只选宿主配方」的既有边界冲突。

### 8.4 落地清单（全部完成，2026-10-10）

1. **✔** 融合条的 `Height` / `Background` 不再一刀切：高度取派生键 `WindowFusedTitleBarHeight`（`max(48, WindowTitleBarHeight + 14)`），底色每条 recipe 显式给出（`headerbar-right` 用 `SurfaceRaisedBrush`，因为 headerbar 是应用自己的工具条）。
2. **✔** `headerbar-right` 有了完整的显式规则（图标、标题、三个槽、按钮组、融合条），不再依赖「落到默认恰好正确」；`SystemStyleChecks.VerifyRecipeCoverage` 通过。
3. **✔（结论有修正）** 居中基准改为**标题栏的弹性列**，而不是原先写的内容列。原计划是「相对标题栏整体居中」，实测在 640 宽窗口下会与宿主按钮组重叠约 24px，故改为弹性列（随可用宽度收缩、永不重叠）。取舍与副作用见 §5.2。
4. **✔** `traffic-lights-left` 的按钮组重做：红（关）/ 黄（小）/ 绿（放大）三枚圆形按钮，顺序按平台惯例重排（模板里按钮的 `Grid.Column` 由 recipe 赋值），字形默认透明、指针或键盘进入按钮组时显现。
   配色取自调色板语义角色 `Danger` / `Warning` / `Success`，**不写十六进制字面量**（产品 UI 的硬编码颜色扫描会拦下后者，而这条规则有意不为 chrome 开豁免）；直径取新可选令牌 `WindowTrafficLightSize`（8–24，默认 12）。
5. **✔ 补验证**：新增 `FusedWindowHeaderChecks.cs`（三条 recipe 的槽列、按钮列、顺序、配色、圆点几何、融合条几何、拖拽不被槽吃掉，并按 recipe 落盘截图），
   并在 `SettingsWindowChecks` 里补「设置窗口 × 三条 recipe」的断言与截图。这一格此前从未被渲染过，现在有了基线。

### 8.5 第 4 槽 `HeaderTabs`：多标签窗口（已定并落地，2026-10-10）

**为什么单记一节**：这条扩展是「文件浏览器 / 代码编辑器也要融合头部」逼出来的，而它恰好是「应用要不要按风格分支」这个问题的**判据案例**，
所以把平台依据与取舍一起留档，避免以后重新论证。

**平台事实（查证于 2026-10-10）**：

- macOS 有**两套**多标签，Apple 在 API 上就分开，不互通：
  ①**窗口标签**（`NSWindowTabGroup` / `NSWindow.tabbingMode`）属于**窗口边框**——官方措辞是
  「Tabs in the window frame group separate document windows; **they are not an NSTabView inside the content area**」，
  且 Apple 的窗口结构说明把 *document tabs* 与标题栏、工具栏、accessory 并列计入 **top chrome**；
  macOS 10.12 起全系统免费提供（`显示 > 显示标签栏` 开关、`窗口 > 合并所有窗口` 合并、`系统设置 > 桌面与程序坞 >
  打开文稿时首选标签页` 给「从不 / 仅全屏 / 始终」），**语义是「一个标签 = 一个窗口」**。
  ②**内容区标签**（`NSTabView`）只在窗口内切换内容。Finder / 终端 / 文本编辑走①，浏览器走②。
- AppKit 给「把应用控件放进标题栏」的正规入口是 `NSTitlebarAccessoryViewController`，其位置用
  **方向性的 `layoutAttribute`**（leading / trailing 那一族）表达——即**平台自己也是「按方向给槽」，
  而不是「按风格各备一份」**，与本计划的角色槽是同一个模型。
- **Windows 11 资源管理器**（22H2，Build 22621.675+）：标签条是**最顶行**的一条 tab strip（在命令栏与文件夹内容之上），
  最小化 / 最大化 / 关闭在同一行右端，`+` 在标签行上。

→ 两个平台都把标签条放在「顶栏这一带、跟在 leading 之后」，**差别只在窗口按钮在左还是在右**——而那是 recipe 已经管的。

**决策**：加 `HeaderTabs`（**角色槽，不是布局**）。宿主必须能「分辨」标签条，因为它要改的是**自己那部分**：

1. 标签条占据标题栏的**弹性列**（与 `HeaderCenter` 同列，两者互斥）；
2. 声明标签条时宿主置 `:header-tabs`，让 `HeaderCenter` 让位；宿主自己的标题文本本来就在 `:custom-title-content` 下隐藏
   （与 AppKit 的 `titleVisibility` 让标题文字可隐藏是同一个语义）；
3. 标签条尺寸读可选令牌 `WindowTabHeight`（20–40，默认 28），选中态形状同样交给 token。

**同一份输入、宿主按 recipe 渲染**：三条 recipe 目前都把标签条画在**栏内**（内联）。将来某条 recipe 要把标签条压到
**栏外另起一行**（macOS 的独立标签条形态），只需加一组宿主规则——**应用零改动**。这正是角色模型相对「每种布局一个适应层」
的胜处：差异被消化在宿主那份已审查的模板里，而不是散进每个应用。

**顺带发现（原文档漏了）**：`Apps/Browser/Views/BrowserMainView.axaml` 是一个**已在运行的受管窗口**
（`BrowserApp.cs:59` `context.ShowWindow(...)`），它今天就有一条应用自排的标签条（`ListBox.browser-tabs`，
`DockPanel.Dock="Top"`，选中态写死 `CornerRadius="12,12,0,0"` 的 Chrome 式上圆下方），
但此前**不在任何批次表里**。已补进 §3.2 作为 #15。同理 #11 代码编辑器已有 `ListBox.editor-tabs`。

**明确不做（延后）**：

- **不新增「有标签时的融合条高度」派生键**。三条内置风格的 `WindowFusedTitleBarHeight`（56 / 52 / 60）都大于
  `WindowTabHeight + 2×内边距`（默认 28 + 16 = 44），标签条放得下。将来某条风格真需要更高的栏，再加派生键——契约不变。
- **不做窗口级标签**（见 §5.7）。
- **不做「栏外另起一行」的模板分支**：目前没有 recipe 需要，先不加结构；接口已为此留好。

**验证**：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/FusedWindowHeaderChecks.cs` 增加标签条用例，逐 recipe 断言
`PART_HeaderTabs` 落在弹性列（`Grid.Column == 2`）、居中槽让位、宿主标题让位、与按钮组 / leading / trailing 均不重叠、
标签条确实**吃满**弹性列（左界在 leading 之后、右界不越过按钮组或 trailing）、`WindowTabHeight` 可解析；
另断言「清空标签条后居中槽恢复」与「只有标签条、其余三槽为空时仍然融合」。
截图 `preview-qa/fused-header/fused-header-tabs-{recipe}.png`。

## 9. 进度日志（只追加，倒序在上）

### 2026-10-10（第三轮：第 4 槽 `HeaderTabs`，赶在第一批接入之前）

动机：用户提出「以后给代码编辑器 / 文件浏览器加融合头部，文件浏览器还要支持多标签（参考 Windows），表现效果不就和 macOS 不一致吗」。
结论是**不一致与否取决于标签条由谁落位，而不是用几个槽**；为了让差异留在宿主，第 4 槽必须**在第一批开工前**加完。依据与取舍见 §8.5。

改动文件：

- **宿主契约**：`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`
  （新增 `HeaderTabsProperty` / `HeaderTabs`，纳入 `HasTitleBarContent`，新增 `:header-tabs` 伪类，
  `HeaderCenter` 的文档注释写明它让位于 `HeaderTabs`）。
- **模板与规则**：`Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`
  （模板加 `PART_HeaderTabs`（弹性列、Stretch）；**三条 recipe 各自把 `PART_HeaderTabs` 显式写出**；
  `:header-tabs` 让 `PART_HeaderCenter` `IsVisible=False`；融合态三条 recipe 各补一条；
  「六个部件」的注释改为「七个部件 = 图标 / 标题文本 / 四个角色槽 / 按钮组」）。
- **令牌**：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleTokenContract.cs`
  （新增**可选**令牌 `WindowTabHeight`，Number，20–40，默认 28；未加入 `Required`，因此三条内置风格不必声明）、
  `Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml`（同名兜底 28）。
- **校验**：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/FusedWindowHeaderChecks.cs`
  （标签条用例 + 追加 `fused-header-tabs-{recipe}.png` 截图；类注释由「三个角色块」改为四个）。
- **文档**：本文（§1 状态表、§2 机制、§3.2 补第 15 项浏览器并标注三个带标签条的窗口、§4 验收、§5.6 / §5.7、本节与 §8.5）、
  `docs/desktop/RelaxKonOS.SystemStyle.md`（令牌表与消费方表）。

验证：

- `Tests/Client/RelaxKonOS.WindowPreviews.Tests` → **196 PASS / 0 异常 / exit 0**（新断言并入既有那条融合头部 PASS 行）。
- `Tests/Client/RelaxKonOS.Settings.Tests` → 通过，且 `recipe coverage` 与 `no hardcoded colours` **未被跳过**（真跑）。
  注意 `-o` 必须落在仓库内（本次用 `.artifacts/style-check`），否则 `TryFindRepositoryRoot` 找不到 sln 会静默跳过这两项。
- 截图：`preview-qa/fused-header/fused-header-tabs-{caption-buttons-right,traffic-lights-left,headerbar-right}.png`
  已人工核对——macOS-like 下标签条紧接交通灯与 leading、吃满弹性列、居中槽让位、不越过 trailing。

遗留：第一批 9 个窗口与第二批 6 个窗口仍未开始。

### 2026-10-10（第二轮：方案 A 落地 + macOS 交通灯）

改动文件：

- **宿主契约**：`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`（删除 `TitleBarContent`，新增 `HeaderLeading` / `HeaderCenter` / `HeaderTrailing` 与 `HasTitleBarContent`）、
  `Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`（标题栏改 5 列网格；三条 recipe 各自把六个部件显式写出；`traffic-lights-left` 的按钮组重做）。
- **令牌**：`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/SystemStyleTokenContract.cs`（新增可选令牌 `WindowTrafficLightSize`，8–24，默认 12）、
  `Framework/RelaxKonOS.UI/Themes/SystemStyle/SystemStyleResourceBuilder.cs`（新增派生键 `WindowFusedTitleBarHeight`）、
  `Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml`（两个兜底值）。
- **设置**：`Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsHeaderView.axaml(.cs)`（收敛为 leading 块：返回 + 标题）、
  新增 `SettingsSearchBarView.axaml(.cs)`（center 块：搜索框）、`SettingsView.axaml(.cs)`（`AttachWindowHeader` 只做三行赋值；
  Ctrl+F / Escape 改为在两个块上各挂一份，因为融合后标题栏不在内容视图的子树里，内容视图上的键盘处理收不到）。
- **测试**：新增 `Tests/Client/RelaxKonOS.WindowPreviews.Tests/FusedWindowHeaderChecks.cs`；`SettingsWindowChecks.cs` 改槽断言并补「设置窗口 × 三条 recipe」循环；`Program.cs` 挂载新检查。
- **文档**：本文 §1 / §2 / §4 / §5 / §7 / §8；`docs/desktop/RelaxKonOS.SystemStyle.md`（§3 令牌表、§4.1 消费方、新增 §4.2、§7.3 / §7.4）。

验证：

- `RelaxKonOS.WindowPreviews.Tests` 全套 **196 条 PASS、无异常、退出码 0**（基线 195；新增 1 条汇总断言 + 设置里的三 recipe 循环）。
- `RelaxKonOS.Settings.Tests` 全套通过，其中 `SystemStyleChecks` 的**配方覆盖**与**硬编码颜色扫描**都是真跑而不是 SKIP
  ——注意输出目录必须在仓库内，否则 `TryFindRepositoryRoot` 找不到 sln 会跳过这两项。

已知副作用（有意保留）：center 槽在 leading / trailing 不等宽时会偏移（macOS-like 下约 +65px），取舍见 §5.2。
产品代码已改；第一批 9 个窗口仍未开始。

### 2026-10-10

- 建立本文。完成候选盘点：`Classes="app-header"` 9 处、`Classes="app-toolbar"` 5 处，逐个核对头部内容与特殊点后确定批次（见第 3 节）。
- 未改动任何产品代码；工作区干净，分支 `windows_enhance`。
- **验证回路已跑通并记录基线**：构建 + 运行 `RelaxKonOS.WindowPreviews.Tests.exe` 成功，**195 条 PASS、无异常、退出码 0**，整套约 30 秒。改动前后都用同一命令比对，命令见第 7 节。
- 查证「安装桌面程序是否添加系统风格 / 布局」：**只加桌面布局，不加系统风格**；系统风格严格 3 个内置，外置 manifest 加载器在生产上未接通；即使接通也只能在 3 个封闭 recipe 里选、不能带来新布局。详见第 8.1 节。
- 记下 macOS 风格下融合顶栏的 4 个缺陷（D1–D4）与三个候选方案（A/B/C，C 否决），**方案未定，暂停第一批编码**。详见第 8.2–8.4 节。
