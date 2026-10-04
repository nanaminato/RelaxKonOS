# RelaxKonOS 桌面外壳与模态对话框设计文档

> 本文档定义 RelaxKonOS 登录成功后的桌面外壳交互层：宿主窗口（`MainWindow`）的窗口控制与 mstsc 风格连接栏，以及 Framework 层的可复用模态对话框机制（`ModalDialog` / `ShowDialogAsync`）。
>
> - 登录流程与认证会话见 [`RelaxKonOS.Login.md`](../platform/RelaxKonOS.Login.md)
> - 架构原则见 [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - 项目结构与当前进度见 [`文档总览`](../README.md)

---

## 1. 模块定位

登录成功后，`App.axaml.cs` 把桌面 `MainWindow`（顶层 Avalonia `Window`，`WindowDecorations=None`）显示给用户。`MainWindow` 内部承载由 `ShellRuntime` 激活的 `IDesktopShell`（每个 Shell 提供自己的桌面、启动器、任务栏/Dock 与 `WindowManager` surface）。完整的 launcher 契约、可回滚切换和扩展包边界见 [`RelaxKonOS.ShellLauncher.Goal.md`](./RelaxKonOS.ShellLauncher.Goal.md)；本文档覆盖宿主窗口控制与模态机制。

| 能力 | 层 | 说明 |
|---|---|---|
| 宿主窗口控制 | `Client`（`MainWindow`） | 标题栏拖动、8 向 resize、最小化/最大化/关闭、全屏切换 |
| mstsc 连接栏 | `Client`（`MainWindow` + `DesktopShellViewModel`） | 全屏/固定/自动隐藏、连接信息、关闭连接 = 登出 |
| Launcher runtime | `Client` + `RelaxKonOS.Shell` | `IDesktopShell` 切换、窗口重挂载、受控外部 Shell 包；不替换宿主 OS shell |
| 模态对话框 | `Framework`（`WindowManager` + `App.SDK`） | `ModalDialog<TResult>` / `ShowDialogAsync` / `ModalBlocker`，可复用、可嵌套 |

设计目标：

- **宿主窗口**模拟原生 OS 窗口（mstsc 全屏体验）：自绘标题栏 + 系统控制按钮，进入全屏隐藏标题栏，连接栏仿 mstsc 顶部条。
- **模态对话框是真正的受管窗口**（`ManagedWindow`），可移动、可 resize，只屏蔽其直接 owner，其它窗口保持可交互；支持嵌套与任意结果类型。

---

## 2. 宿主窗口控制（MainWindow）

### 宿主界面现代化（2026-10-04）

宿主标题栏采用抬升表面与底部分隔线，连接栏沿用自动隐藏、固定与拖动行为，增加系统风格阴影。启动遮罩保持不透明，以遮住尚未恢复的桌面；中央卡片使用品牌图标、状态标题、辅助说明和进度条建立信息层次。所有颜色、圆角与阴影复用现有动态资源，随配色和系统风格切换。

连接信息卡片以次级颜色显示字段名，服务器、用户与工作区值支持换行。卡片顶部偏移使用 `HostTitleBarMargin`，跟随系统风格的标题栏高度，不再固定为 42px。连接栏中的状态标记使用成功语义色。

验收时检查：最小宿主尺寸 800×520、长服务器/用户/工作区名称、三种内置系统风格、浅色/深色、100%/150%/200% 缩放，以及加载、全屏、固定和断开连接状态。构建成功仅确认编译，不代表这些人工视觉检查已完成。


桌面默认壁纸为客户端内置的山间湖泊照片（`builtin:alpine-lake`）；另提供海岸波浪（`builtin:ocean-waves`）和沙丘光影（`builtin:desert-dunes`）。三张 JPEG 随客户端作为 Avalonia 资源打包，离线解析且使用等比填充。服务端偏好只保存 `wallpaperKey`，选择内置照片或原有渐变背景都不上传或下载壁纸图片。空设置或无法识别的内置标识回到默认照片，已明确选择的渐变背景保持原样；自选图片仍使用 Workspace 图片上传与同步流程，下载期间使用默认照片作为回退背景。图片作者、来源与许可见 [`Assets/Wallpapers/README.md`](../../Client/RelaxKonOS.Client/Assets/Wallpapers/README.md)。

设置的壁纸区域只显示标题、浏览图片按钮和可随可用宽度自动换行的缩略图网格，不显示逐项文字名称或内置资源、默认图片、上传同步方式的说明；选中项以强调色边框和高亮背景标示，名称保留在悬停提示与无障碍标签中。

### 2.1 标题栏与系统控制

进入桌面时，宿主先显示不透明的主题背景、居中的 RelaxKonOS Logo、产品名、动态进度条及本地化加载提示。加载画面完成首轮布局和渲染后才开始初始化 Shell；工作区偏好、窗口宿主和桌面状态恢复完成后隐藏加载层，再显示首次设置对话框。加载层覆盖整个桌面内容区，避免空宿主或尚未恢复完成的桌面透出灰色背景。

`MainWindow.axaml` 设 `WindowDecorations="None"` + `WindowState="Maximized"` + `MinWidth=800 MinHeight=520`，自绘：

- **标题栏**（`WindowTitleBar`，高 34）：`PointerPressed` → `BeginMoveDrag`（仅 `WindowState == Normal` 时）。
- **系统按钮**（Segoe MDL2 Assets 字形）：
  - 最小化（`&#xE921;`）→ `WindowState = Minimized`
  - 最大化/还原（`&#xE922;` / `&#xE923;`）→ 切换 `Normal`/`Maximized`，按钮字形与 Tooltip 同步
  - 关闭（`&#xE8BB;`，红底）→ 执行登出并关闭（见 §2.3）

### 2.2 8 向 resize

`MainWindow` 周围放 8 个透明命中区（4 边 + 4 角），`Tag` 标记方向：

```text
[NW][──North──][NE]
[│                 │]
[W     桌面内容    E]
[│                 │]
[SW][──South──][SE]
```

`Resize_OnPointerPressed` 把 `Tag`（`West`/`East`/`North`/`South`/`NorthWest`/`NorthEast`/`SouthWest`/`SouthEast`）解析为 Avalonia `WindowEdge`，仅 `WindowState == Normal` 时 `BeginResizeDrag(edge, e)`。`MinWidth/MinHeight` 防止桌面内容过度压缩。

> 这是**宿主 Avalonia Window** 的 resize，与 `WindowManager` 内部 `RemoteWindow` 的 8 向 resize（`ComputeResize`）是两套独立机制：前者管整个桌面窗口的边框，后者管桌面内每个应用窗口。

### 2.3 mstsc 风格连接栏

连接栏（`ConnectionBar`）与标题栏同处顶层，居中悬浮顶部（520×34，下圆角 7，阴影）：

```text
[ 服务器信息 ][│][ 固定/已固定 ][   预留    ][ 全屏/退出全屏 ][│][ 关闭连接 ]
```

| 控件 | 行为 |
|---|---|
| 服务器信息按钮 | 点击切换 `ConnectionInfo` 面板（服务器/用户/工作区，绑定 `IAuthSession`） |
| 固定 / 已固定 | `_isPinned` 切换；固定时停止自动隐藏计时器 |
| 全屏 / 退出全屏 | `_isFullScreen` 切换 `WindowState.FullScreen`；进全屏隐藏标题栏；未固定时 2s 后自动隐藏连接栏 |
| 关闭连接 | `DisconnectAsync()` → `IAuthSession.LogoutAsync()` → `Close()` |

中间 `StackPanel` 预留未来动作位（剪贴板、显示设置、会话操作等）。

桌面空白处的“粘贴”（或先点击空白处再按 `Ctrl+V`）根据最近一次文件复制来源操作：RelaxKonOS 内部复制的远端条目使用远端复制/移动接口，宿主机系统剪贴板里的文件或文件夹上传到当前远端桌面目录。文件夹递归上传与文件管理器共用上传规划规则。

**自动隐藏行为**（mstsc 仿生）：

- 进全屏且未固定 → `DispatcherTimer`（2s）到点隐藏 `ConnectionBar` + `ConnectionInfo`
- 鼠标移到顶部（`Y <= 6`）→ 停止计时器并重新显示连接栏（`Root_OnPointerMoved`）
- 鼠标进入连接栏 → 停止计时器；离开 → 重新排程隐藏
- `DispatcherTimer` 单次触发：首次 tick 内 `Stop()`（Avalonia 无 `AutoReset`）

**连接信息绑定**（`DesktopShellViewModel`）：

```text
ConnectionServer    ← IAuthSession.ServerUrl                （未连接时 "未连接"）
ConnectionUser      ← IAuthSession.CurrentUser.Username
ConnectionWorkspace ← IAuthSession.CurrentWorkspace.Name
```

### 2.4 关闭连接 = 登出

```text
用户点"关闭连接"或标题栏"关闭"
    → DisconnectAsync()
        → IAuthSession.LogoutAsync()   // 吊销 RefreshToken，状态回 Unauthenticated
        → MainWindow.Close()
        → MainWindow.Closed → desktop.Shutdown()   // 进程退出
```

> 这是登录会话与桌面外壳的衔接点：连接栏把"断开远程连接"映射为 `IAuthSession.LogoutAsync`，与 [`RelaxKonOS.Login.md`](../platform/RelaxKonOS.Login.md) §7 的登出路径一致。当前阶段断开即退出进程（不回 `LoginWindow`），未来可改为回登录窗。

---

## 3. 模态对话框系统（Framework）

### 3.1 设计目标

应用需要弹"输入框""选择文件""确认"等模态对话。RelaxKonOS 的模态对话框设计为**真正的受管窗口**：

- 对话框是一个 `ManagedWindow`（由 `WindowManager.Create` 创建），可移动、可 resize，与普通应用窗口共用同一套 z-order / 焦点逻辑。
- **只屏蔽其直接 owner**：通过一个跟随 owner 的半透明遮罩（`ModalBlocker`）盖住 owner，owner 之外其它窗口仍可交互。
- **桌面级模态**：没有应用 owner 的桌面外壳流程使用 `ShowShellDialogAsync`；遮罩覆盖整个桌面窗口宿主，且对话框仍是受管窗口。
- **系统级模态**：安全敏感流程使用 `ShowSystemDialogAsync`；它位于全屏宿主层，遮罩包含任务栏在内的整个桌面，阻止所有其他桌面交互。
- **可嵌套**：对话框可以再弹自己的子对话框（owner = 该对话框窗口）。
- **任意结果类型**：`ModalDialog<TResult>`，`await` 返回 `TResult?`；取消/关闭返回 `default`。
- **自动清理**：owner 关闭/最小化、对话框关闭、Esc/取消按钮，都触发 session 取消并移除遮罩。

### 3.2 核心类型（`Framework/RelaxKonOS.WindowManager/`）

```text
ModalDialog<TResult>        对话框句柄：Owner / Result(Task<TResult?>) / Close(result) / Cancel()
                            ShowDialogAsync<TChild>(...) 打开子模态（owner = 本对话框窗口）
ModalBlocker : Border       半透明遮罩（#3D000000），ApplyBounds 跟随 owner
ModalSession<TResult>      owner + dialogWindow + blocker + dialog，实现 IModalSession
IWindowManager.ShowDialogAsync<TResult>(owner, title, contentFactory)
AppContext.ShowDialogAsync<TResult>(owner, title, contentFactory)   // 应用入口
IWindowManager.ShowShellDialogAsync<TResult>(title, contentFactory) // 桌面外壳入口
IWindowManager.ShowSystemDialogAsync<TResult>(title, contentFactory) // 安全敏感的全桌面入口
```

### 3.3 ShowDialogAsync 流程

```text
WindowManager.ShowDialogAsync<TResult>(owner, title, contentFactory)
    │
    Create WindowCreateOptions(ownerAppId = owner.OwnerAppId, CanResize = true,
                              CanMinimize = false, CanMaximize = false)   → 受管对话框窗口
    │
    new ModalBlocker(owner); blocker.ApplyBounds(owner.Bounds)
    blocker.ZIndex = dialogWindow.View.ZIndex - 1   // 遮罩在 owner 之上、对话框之下
    blocker.PointerPressed → Focus(owner)            // 点遮罩 = 点 owner → 激活最顶层模态（见 §3.9）
    _host.Children.Add(blocker)
    │
    new ModalSession(owner, dialogWindow, blocker, dialog) → _modalSessions
    │
    dialog.Result.ContinueWith → Dispatcher.UIThread.Post(CloseModalSession)
    │
    return dialog.Result   // 调用方 await
```

`CloseModalSession`：从 `_modalSessions` 移除 → 从 host 移除 blocker → 若对话框窗口仍开则 `Close(dialogWindow)`。

对话框窗口尺寸由 owner 推算：居中于 owner，宽 320–460、高 220–320（不超出 owner）。

### 3.4 遮罩跟随 owner

owner 拖动/resize 时，`WindowManager` 调 `UpdateDialogs(owner)` 对该 owner 的每个 session 重新 `blocker.ApplyBounds(owner.Info.Bounds)`，遮罩始终贴合 owner。`SetHostBounds`（宿主区域变化）也会同步更新所有 session 的 blocker 边界。

```text
owner 拖动/Resize ──→ OnDrag/OnResize ──→ UpdateDialogs(owner) ──→ blocker.ApplyBounds(owner.Bounds)
宿主区域变化     ──→ SetHostBounds     ──→ 遍历 _modalSessions ──→ blocker.ApplyBounds(owner.Bounds)
```

### 3.5 自动取消场景

| 触发 | 行为 |
|---|---|
| 对话框调 `dialog.Close(result)` | `TaskCompletionSource.TrySetResult(result)` → session 关闭 |
| 对话框调 `dialog.Cancel()` / Esc / 取消按钮 | `TrySetResult(default)` → 返回 null |
| owner 被 `Close()` | `Close` 遍历 `_modalSessions` 取消相关 session |
| owner 被 `Minimize()` | 最小化前取消该 owner 的 session（避免遮罩悬空） |
| 对话框窗口被关闭 | `dialog.Result` 已完成 → `CloseModalSession` 移除遮罩 |

### 3.6 嵌套模态

`ModalDialog<TResult>.ShowDialogAsync<TChild>(title, contentFactory)` 把**本对话框窗口**作为子对话框的 owner 调 `WindowManager.ShowDialogAsync`，形成栈式嵌套。每层各自有 owner + blocker + session，互不干扰。

### 3.7 应用接入

应用通过 `AppContext.ShowDialogAsync<TResult>(owner, title, contentFactory)` 打开对话框，`contentFactory` 收到一个 `ModalDialog<TResult>`，用其 `Close`/`Cancel` 构造 ViewModel 的回调：

```csharp
var result = await context.ShowDialogAsync<string>(window, "选择要打开的文件", dialog =>
    new FilePickerView { DataContext = new FilePickerViewModel(dialog.Close, dialog.Cancel) });
```

### 3.8 内置示例（Notepad）

| 入口 | 对话框 | 结果 |
|---|---|---|
| Notepad → Insert text... | `NotepadInsertDialogView` | string（追加到正文） |
|   └─ 从子对话框添加... | `NotepadInsertDialogView`（嵌套，owner = 父对话框窗口） | string（拼到父输入框） |
| Notepad → Open... | `FilePickerView`（"选择文件"模式） | 文件路径 string → `File.ReadAllTextAsync` |

`FilePickerViewModel` 用本地 `Directory.Enumerate*` 列目录，选中文件后 `dialog.Close(fullPath)`；返回路径后 Notepad 读取文件内容。这同时验证了"模态对话框返回任意类型结果"与"应用通过对话框获取输入"两条路径。

### 3.9 模态链激活与层级（Z-order）

激活逻辑保证：**点击模态链上的任意窗口（或盖在 owner 上的遮罩）都激活该链最顶层的模态对话框，并把整条链一起抬到最上层。** 焦点/激活态永远交给链的叶子对话框，根 owner 与中间对话框保持被遮罩、不可交互。

- **链的构成**（`BuildModalChain`）：从被点击窗口出发，先向上经 `GetModalOwner` 找到根 owner（没有任何 modal session 把它当作 `DialogWindow` 的窗口），再向下经 `GetTopmostModal` 走到最顶层模态对话框。例如 `A → B(modal) → C(modal)`，点击 A/B/C 任一，链都是 `[A, B, C]`；点击叶子 C 也是 `[A, B, C]`。
- **激活目标**：链的叶子（最顶层模态）。`Focus` 把 `IsFocused`/`IsActive`/`SetActive` 只设给叶子，根 owner 与中间对话框不被激活。
- **层级抬升**：沿链自底向上依次递增 `_zCounter`，每个窗口之后紧跟其遮罩 `GetBlockerFor(w)`，保证 `owner < blocker < dialog` 的相对顺序，且整条链高于桌面其它窗口。抬升 `[A,B,C]` 后 Z 顺序为 `A < blocker₁ < B < blocker₂ < C`，其余窗口（含并存的其它模态链，如另一激活的模态 D）全部落在 A 之下。
- **遮罩点击转发**：`ModalBlocker.PointerPressed` 调 `Focus(owner)`——点击被屏蔽的 owner 区域等价于点击其模态链，从而激活最顶层对话框。遮罩事件标记 `Handled`，不冒泡到宿主。

> **仅模态子窗口才传递激活**：普通（非模态）子窗口不创建 modal session、不遮罩 owner、不阻塞 owner。此时点击 owner 激活 owner、点击子窗口激活子窗口，二者互不传递（`BuildModalChain` 对非模态窗口返回单元素链）。

**两场景对照**：

| 场景 | 窗口关系 | 点击 A/B/C 的结果 | 激活后层级 |
|---|---|---|---|
| 1 | A→B(modal)→C(modal)，另有激活的模态 D | 都激活 C | C 最高；B 次之（高于 A）；A 高于除 C 外所有窗口（含 D）；D 落到 A 之下 |
| 2 | A→B(modal)，另有激活的模态 C | 都激活 B | B 最高；A 高于除 B 外所有窗口（含 C）；C 落到 A 之下 |

---

## 4. 关键文件清单

| 文件 | 职责 |
|---|---|
| `Client/RelaxKonOS.Client/Views/MainWindow.axaml`(+`.cs`) | 宿主窗口：标题栏、8 向 resize 命中区、连接栏、连接信息面板、全屏/固定/自动隐藏、关闭 = 登出 |
| `Client/RelaxKonOS.Client/ViewModels/Shell/DesktopShellViewModel.cs` | `ConnectionServer/User/Workspace` 绑定 `IAuthSession` |
| `Framework/RelaxKonOS.WindowManager/ModalDialog.cs` | `ModalDialog<TResult>` / `ModalBlocker` / `ModalSession` / `IModalSession` |
| `Framework/RelaxKonOS.WindowManager/WindowManager.cs` | `ShowDialogAsync` / `CloseModalSession` / `UpdateDialogs` / `Focus`（模态链激活与层级抬升）/ `BuildModalChain` / `GetBlockerFor` / `GetModalOwner` |
| `Framework/RelaxKonOS.WindowManager/IWindowManager.cs` | `ShowDialogAsync` 接口契约 |
| `Framework/RelaxKonOS.App.SDK/AppContext.cs` | `ShowDialogAsync` 应用入口 |
| `Client/RelaxKonOS.Client/Apps/NotepadApp.cs` | 模态对话框 + 嵌套 + 文件选择示例装配 |
| `Client/RelaxKonOS.Client/Apps/NotepadInsertDialogView.axaml`(+`.cs`) + `NotepadInsertDialogViewModel.cs` | 文本输入对话框 |
| `Client/RelaxKonOS.Client/Apps/FilePickerView.axaml`(+`.cs`) + `FilePickerViewModel.cs` + `FilePickerEntry.cs` | 文件选择对话框（"选择文件"模式） |

---

## 5. AI Agent 理解规则

实现/修改桌面外壳与模态对话框时必须遵守：

**必须**：

- 模态对话框必须是**真正的受管窗口**（`WindowManager.Create`），不要用 Avalonia 顶层 `Window.ShowDialog` 绕开 WindowManager。
- `ShowDialogAsync` 的遮罩只盖 owner；owner 拖动/resize/宿主区域变化时必须 `UpdateDialogs` / `SetHostBounds` 同步 blocker 边界。
- owner 关闭/最小化前必须取消其 modal session（避免遮罩悬空或对话框孤儿）。
- 模态链激活不变量：点击模态链上任一窗口或其遮罩必须激活该链最顶层模态对话框，并把整条链（根 owner → … → 顶层模态，含各自 `ModalBlocker`）一起抬到最上层，保持 `owner < blocker < dialog` 的相对顺序（见 §3.9）。仅模态子窗口传递激活，非模态子窗口不创建 session、各自独立激活。
- 应用层一律经 `AppContext.ShowDialogAsync` 打开对话框，不直接调 `WindowManager.ShowDialogAsync`。
- 宿主 `MainWindow` 的 resize/拖动用 Avalonia `BeginMoveDrag` / `BeginResizeDrag(WindowEdge)`；`WindowDecorations=None` + 自绘标题栏。
- "关闭连接"/标题栏关闭 = `IAuthSession.LogoutAsync()` 后 `MainWindow.Close()`，与登录模块登出路径一致。
- `DispatcherTimer` 单次触发：首次 tick 内 `Stop()`（Avalonia 无 `AutoReset`）。

**禁止**：

- 用 `RemoteWindow` 承载模态对话框以外的"屏蔽全桌面"遮罩（遮罩只跟随 owner，不屏蔽其它窗口）。
- 在 `WindowManager.ShowDialogAsync` 之外另起模态实现。
- 让模态遮罩脱离 owner 边界（必须 `ApplyBounds(owner.Info.Bounds)` 跟随）。
- 只抬升最顶层模态而不抬升其 owner 链（owner 会被其它窗口压住，模态链被割裂）。
- 让 `ModalBlocker` 点击无响应（遮罩必须 `Focus(owner)` 转发激活到最顶层模态）。
- 把宿主 `MainWindow` 的窗口控制与桌面内 `RemoteWindow` 的 resize 混为一谈（两套独立机制）。
- 在连接栏"关闭连接"里跳过 `LogoutAsync` 直接 `Close()`（会留下未吊销的 RefreshToken）。

---

## 6. 本地键盘路由

键盘输入是客户端本地 UI 事件，不经 Workspace Hub 或任何同步协议。`RemoteWindow` 将
Avalonia 的键盘事件转换成 `RelaxKonOS.Core.Input.RemoteKeyEventArgs`，并只在事件从当前
焦点控件冒泡到该受管窗口时通知 `ManagedWindow.KeyDown` / `KeyUp`。

```text
焦点控件 → 应用内容 → RemoteWindow / ManagedWindow → DesktopShell → MainWindow
```

应用可在其 `AppContext.ShowWindow` 返回的 `ManagedWindow` 上订阅 `KeyDown`。将
`RemoteKeyEventArgs.Handled` 设为 `true` 会同步处理原 Avalonia 事件，因而阻止它继续
冒泡到 Shell 和宿主窗口。后台窗口不在键盘事件路由中，不能接收活动窗口的输入。

`WindowManager` 的默认处理在应用处理器之后执行：`Esc` 先取消最上层模态窗口；否则，
若活动受管窗口处于 RelaxKonOS 全屏，则退出**该窗口**的全屏并处理事件。若两者都未处理，
事件才会抵达 `MainWindow`；宿主窗口在自身全屏时以 `Esc` 退出客户端全屏。

普通文本与 IME composition 仍由 Avalonia 焦点控件处理，不能从 `KeyDown` 推导或通过
`RemoteKeyEventArgs` 传递文本。

---

## Windows 任务栏预览与任务视图（2026-10）

内置 Windows-like 桌面的运行中应用图标在悬停 400ms 后显示窗口预览；单窗口显示一张卡片，多窗口横向排列。鼠标移入面板保持显示，图标与面板之间的空隙也属于悬停区域；移出完整悬停区域 250ms 后才关闭，计时结束时再次检查鼠标位置，避免过期的离开事件误关预览。切换到另一应用图标时立即换组。点击缩略图恢复并激活窗口，标题栏的关闭按钮关闭该窗口；点击外部、按 `Esc`、宿主失去焦点或切换 Shell 都会关闭面板并取消等待中的悬停。面板锚定图标上方并限制在宿主范围内，多窗口超宽时横向滚动。

任务栏与宿主任务视图共用每个 `ManagedWindow` 的 `WindowThumbnail`。普通 Avalonia 内容通过受控 `VisualBrush` 快照缩放到不超过 480×300 像素，面板打开期间约每 350ms 更新，多个消费者共享帧并节流。最小化前保存最后一帧，最小化期间不截图、不恢复窗口；关闭窗口释放缓存。包含 `NativeControlHost`（例如原生 WebView）的窗口使用图标与标题卡片，不重建应用或截取宿主屏幕。

点击缩略图时，先关闭预览、停止全部计时器并清除待打开图标，再恢复或激活应用窗口；隐藏控件的延迟离开事件不参与下一次悬停。窗口分组按现有窗口逐项协调，焦点改变时不清空列表、不重建未变化的卡片，避免鼠标捕获和焦点在激活过程中失效。

Windows 实测追踪发现，点击后重新打开预览时，鼠标仍在缩略图内部，面板可能收到离开事件且 `IsPointerOver=false`。关闭判定因此使用宿主输入持续更新的鼠标坐标，对照面板、连接区域和图标的实际边界；进入区域取消关闭计时，计时到期再次检查边界。原生宿主离开事件清除旧坐标，避免最后一个区域内坐标导致预览一直打开。Headless 回归测试注入这一悬停标志与坐标不一致的场景，同时验证真正移出时即使标志仍为 true 也能关闭。

交互追踪自动写入 `%LOCALAPPDATA%\RelaxKonOS\logs\taskbar-preview-<启动时间>-<进程号>.log`，每行一个 JSON 事件。启动记录包含程序路径、客户端程序集路径和构建标识；交互记录包含图标/卡片的控件身份、鼠标位置和捕获对象、事件来源的控件祖先、面板与连接区域的悬停状态、实际边界命中结果、计时器状态、布局和 DPI、宿主焦点、窗口激活，以及关闭原因。鼠标移动日志限为每 250ms 一条（交互判定处理每次输入），面板打开时每 2 秒记录状态；关闭后继续观察鼠标输入 10 秒，以追踪下一次悬停。日志不包含窗口标题、文档内容或凭据。

复现时启动带日志的客户端，先悬停图标并点击缩略图，再次悬停并移入缩略图，出现问题后收集该次启动对应的最新日志。单文件上限约 2MB，超出后旧段保存为同名 `.previous`；收集时一并提供。保留当前与最近五次启动的日志，写入失败不影响应用交互。Headless 测试使用测试输出下的 `preview-qa/logs/`，验证构建标识、事件顺序、鼠标捕获记录和六轮真实 Shell 的激活记录。

任务视图继续由宿主 `SystemUiCoordinator` 管理，窗口选择、关闭、键盘导航和入场动效复用现有实现。预览面板位于 Shell 内部，层级低于全屏窗口和系统模态覆盖层。设置中的 `ShowTaskbarWindowPreviews` 控制任务栏预览；关闭此设置时，多窗口图标点击直接切换到选定窗口。

本版范围为 RelaxKonOS 内部窗口，不包含虚拟桌面管理、悬停缩略图时临时透视桌面的 Peek 效果或 Windows 宿主全局快捷键接管。Windows 系统可能优先处理 `Win+Tab`，客户端任务栏的任务视图按钮是可靠入口。

验证命令：`dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests/RelaxKonOS.WindowPreviews.Tests.csproj -p:UsedAvaloniaProducts=`。测试使用 Headless + Skia 验证真实像素、最小化缓存、恢复刷新、原生内容降级、关闭清理、悬停延迟、移入保持、换组、屏幕边缘定位与 `Esc`，并使用真实 `WindowsLikeDesktopShell` 和 `DesktopShellViewModel` 验证六轮连续点击后再悬停、跨应用切换、多窗口变为单窗口、预览控件身份保持和焦点移交顺序；截图输出到测试项目的 `bin/Debug/net10.0/preview-qa/`。`UsedAvaloniaProducts` 仅在验证命令中置空以跳过构建遥测。真实 Windows、高 DPI、深色模式及大量窗口的人工验收仍需执行。

---

## 内存与资源生命周期

设置同步的相同 `VisibleAppIds` 内容不触发桌面重建；手动刷新复用 ID、显示元数据和图标路径均未变化的应用条目，保留选择状态。替换或移除条目时，在桌面与开始菜单绑定更新后释放旧图标。受管窗口切换状态复用已解码图标，窗口关闭释放图标及缩略图；设置应用的列表、详情图片和 Shell 固定 Dock 图片也由各自所有者释放。`AppIconImageLoader.Load` 每次返回独立拥有的图片，调用方必须在解除显示引用后释放它，不依赖 GC 回收原生像素内存。

本地化 ViewModel 使用弱事件订阅，使全局语言服务不保留废弃窗口与对话框对象。设置页统一实现可重复调用的 `Dispose`，退出时退订设置、语言和各页的目录/会话事件。宿主窗口关闭时停止连接栏计时器，退订全局事件、清除宿主回调并解除 Shell 与旧宿主的连接；选定 Shell 可在下次登录时重新挂载。

`RelaxKonOS.WindowPreviews.Tests` 同时验证 200 次相同偏好应用和桌面刷新、窗口状态变化的图标复用、废弃本地化对象的可回收性、设置页退订，以及 1,200 次图标加载/释放期间的私有内存增长。内存检查不在加载循环后强制 GC，以覆盖原生图片释放延迟的问题；真实客户端长期空闲仍需在目标机器上验收。
