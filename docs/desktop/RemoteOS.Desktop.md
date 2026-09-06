# RemoteOS 桌面外壳与模态对话框设计文档

> 本文档定义 RemoteOS 登录成功后的桌面外壳交互层：宿主窗口（`MainWindow`）的窗口控制与 mstsc 风格连接栏，以及 Framework 层的可复用模态对话框机制（`ModalDialog` / `ShowDialogAsync`）。
>
> - 登录流程与认证会话见 [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md)
> - 架构原则见 [`RemoteOS.Architecture.md`](../architecture/RemoteOS.Architecture.md)
> - 项目结构与当前进度见 [`RemoteOS.md`](../README.md)

---

## 1. 模块定位

登录成功后，`App.axaml.cs` 把桌面 `MainWindow`（顶层 Avalonia `Window`，`WindowDecorations=None`）显示给用户。`MainWindow` 内部承载由 `ShellRuntime` 激活的 `IDesktopShell`（每个 Shell 提供自己的桌面、启动器、任务栏/Dock 与 `WindowManager` surface）。完整的 launcher 契约、可回滚切换和扩展包边界见 [`RemoteOS.ShellLauncher.Goal.md`](./RemoteOS.ShellLauncher.Goal.md)；本文档覆盖宿主窗口控制与模态机制。

| 能力 | 层 | 说明 |
|---|---|---|
| 宿主窗口控制 | `Client`（`MainWindow`） | 标题栏拖动、8 向 resize、最小化/最大化/关闭、全屏切换 |
| mstsc 连接栏 | `Client`（`MainWindow` + `DesktopShellViewModel`） | 全屏/固定/自动隐藏、连接信息、关闭连接 = 登出 |
| Launcher runtime | `Client` + `RemoteOS.Shell` | `IDesktopShell` 切换、窗口重挂载、受控外部 Shell 包；不替换宿主 OS shell |
| 模态对话框 | `Framework`（`WindowManager` + `App.SDK`） | `ModalDialog<TResult>` / `ShowDialogAsync` / `ModalBlocker`，可复用、可嵌套 |

设计目标：

- **宿主窗口**模拟原生 OS 窗口（mstsc 全屏体验）：自绘标题栏 + 系统控制按钮，进入全屏隐藏标题栏，连接栏仿 mstsc 顶部条。
- **模态对话框是真正的受管窗口**（`ManagedWindow`），可移动、可 resize，只屏蔽其直接 owner，其它窗口保持可交互；支持嵌套与任意结果类型。

---

## 2. 宿主窗口控制（MainWindow）

### 2.1 标题栏与系统控制

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

> 这是登录会话与桌面外壳的衔接点：连接栏把"断开远程连接"映射为 `IAuthSession.LogoutAsync`，与 [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md) §7 的登出路径一致。当前阶段断开即退出进程（不回 `LoginWindow`），未来可改为回登录窗。

---

## 3. 模态对话框系统（Framework）

### 3.1 设计目标

应用需要弹"输入框""选择文件""确认"等模态对话。RemoteOS 的模态对话框设计为**真正的受管窗口**：

- 对话框是一个 `ManagedWindow`（由 `WindowManager.Create` 创建），可移动、可 resize，与普通应用窗口共用同一套 z-order / 焦点逻辑。
- **只屏蔽其直接 owner**：通过一个跟随 owner 的半透明遮罩（`ModalBlocker`）盖住 owner，owner 之外其它窗口仍可交互。
- **桌面级模态**：没有应用 owner 的桌面外壳流程使用 `ShowShellDialogAsync`；遮罩覆盖整个桌面窗口宿主，且对话框仍是受管窗口。
- **系统级模态**：安全敏感流程使用 `ShowSystemDialogAsync`；它位于全屏宿主层，遮罩包含任务栏在内的整个桌面，阻止所有其他桌面交互。
- **可嵌套**：对话框可以再弹自己的子对话框（owner = 该对话框窗口）。
- **任意结果类型**：`ModalDialog<TResult>`，`await` 返回 `TResult?`；取消/关闭返回 `default`。
- **自动清理**：owner 关闭/最小化、对话框关闭、Esc/取消按钮，都触发 session 取消并移除遮罩。

### 3.2 核心类型（`Framework/RemoteOS.WindowManager/`）

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
| `Client/RemoteOS.Client/Views/MainWindow.axaml`(+`.cs`) | 宿主窗口：标题栏、8 向 resize 命中区、连接栏、连接信息面板、全屏/固定/自动隐藏、关闭 = 登出 |
| `Client/RemoteOS.Client/ViewModels/Shell/DesktopShellViewModel.cs` | `ConnectionServer/User/Workspace` 绑定 `IAuthSession` |
| `Framework/RemoteOS.WindowManager/ModalDialog.cs` | `ModalDialog<TResult>` / `ModalBlocker` / `ModalSession` / `IModalSession` |
| `Framework/RemoteOS.WindowManager/WindowManager.cs` | `ShowDialogAsync` / `CloseModalSession` / `UpdateDialogs` / `Focus`（模态链激活与层级抬升）/ `BuildModalChain` / `GetBlockerFor` / `GetModalOwner` |
| `Framework/RemoteOS.WindowManager/IWindowManager.cs` | `ShowDialogAsync` 接口契约 |
| `Framework/RemoteOS.App.SDK/AppContext.cs` | `ShowDialogAsync` 应用入口 |
| `Client/RemoteOS.Client/Apps/NotepadApp.cs` | 模态对话框 + 嵌套 + 文件选择示例装配 |
| `Client/RemoteOS.Client/Apps/NotepadInsertDialogView.axaml`(+`.cs`) + `NotepadInsertDialogViewModel.cs` | 文本输入对话框 |
| `Client/RemoteOS.Client/Apps/FilePickerView.axaml`(+`.cs`) + `FilePickerViewModel.cs` + `FilePickerEntry.cs` | 文件选择对话框（"选择文件"模式） |

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
Avalonia 的键盘事件转换成 `RemoteOS.Core.Input.RemoteKeyEventArgs`，并只在事件从当前
焦点控件冒泡到该受管窗口时通知 `ManagedWindow.KeyDown` / `KeyUp`。

```text
焦点控件 → 应用内容 → RemoteWindow / ManagedWindow → DesktopShell → MainWindow
```

应用可在其 `AppContext.ShowWindow` 返回的 `ManagedWindow` 上订阅 `KeyDown`。将
`RemoteKeyEventArgs.Handled` 设为 `true` 会同步处理原 Avalonia 事件，因而阻止它继续
冒泡到 Shell 和宿主窗口。后台窗口不在键盘事件路由中，不能接收活动窗口的输入。

`WindowManager` 的默认处理在应用处理器之后执行：`Esc` 先取消最上层模态窗口；否则，
若活动受管窗口处于 RemoteOS 全屏，则退出**该窗口**的全屏并处理事件。若两者都未处理，
事件才会抵达 `MainWindow`；宿主窗口在自身全屏时以 `Esc` 退出客户端全屏。

普通文本与 IME composition 仍由 Avalonia 焦点控件处理，不能从 `KeyDown` 推导或通过
`RemoteKeyEventArgs` 传递文本。
