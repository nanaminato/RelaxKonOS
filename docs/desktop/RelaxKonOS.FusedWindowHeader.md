# 桌面端融合窗体头部（TitleBarContent）接入计划

建立日期：2026-10-10。分支：`windows_enhance`。

本文是这项工作的**唯一进度与恢复入口**。中断后请先读第 1 节的「状态总览」和第 8 节的「进度日志」，再按第 7 节「恢复工作指引」继续，不要凭记忆重排批次。

## 1. 状态总览

| 批次 | 范围 | 窗口数 | 状态 |
|---|---|---|---|
| 第一批 | 与设置同构的 `Border.app-header` 工作区 | 9 | 未开始 |
| 第二批 | `Border.app-toolbar` 菜单 / 地址栏型视图 | 5 | 未开始 |
| 顺带 | `FileServicesWorkspace`（本就没有头部） | 1 | 未开始 |
| 不做 | 模态对话框、登录窗口、远端桌面 Shell、资源管理器选择器模式 | — | 已定（见 3.3） |

**目标（一句话）**：把内置应用窗口内容区里那条与窗口标题栏重复的身份条搬进宿主标题栏，使每个窗口只有一层头部。

**量化收益**：`Border.app-header`（`Framework/RelaxKonOS.UI/Themes/ApplicationStyles.axaml:14`）实高 78px（`Padding=24,16` + 46px 图标），叠在 42px 窗口标题栏（`Framework/RelaxKonOS.UI/Themes/Tokens/SystemStyleTokens.axaml:17` 的 `WindowTitleBarHeight`）上共 120px；融合后由模板的 56px 单层替代，每个窗口省约 64px。默认窗口 820×560（`Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs:80`）下约占窗口高度 11%。

## 2. 机制（开工前必读，改模板前再读一遍）

- 应用窗口的 `ManagedWindow.View` 是 `RemoteWindow`；把头部控件赋给 `RemoteWindow.TitleBarContent`（`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs:28`）。
- `TitleBarContent` 非空时置上 `:custom-title-content` 伪类（`RemoteWindow.cs:113`）。
- 模板中的 `PART_CustomTitleContent`（`Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml:41`）铺满标题栏第 0–1 列；伪类规则（同文件 179–192）**隐藏窗口图标与标题、把标题栏抬到 56、背景改为 `SurfaceBrush`**。
- 窗口按钮（`PART_WindowControls`）始终由宿主排在右侧，应用不得改动它的位置。
- `:chrome-traffic-lights-left`（macOS / Ubuntu Shell）另有一条规则把 `PART_CustomTitleContent` 从第 0 列挪到第 1 列（同文件 189–192），此时窗口按钮在左侧。
- 接入入口沿用设置已跑通的模式：视图暴露 `Header` 属性并提供 `AttachWindowHeader(ManagedWindow)`，由 `*App.Activate` 在 `context.ShowWindow(...)` 之后调用。参考实现：`Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml.cs:50`、`Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs:82`。

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

### 3.2 第二批：`Border.app-toolbar` 视图（5 个，需先让出宽度）

这些不是标题重复，而是菜单条 / 地址栏。融合后窗口按钮、拖拽区、菜单、地址栏要挤同一行，收益更高但正确性风险也更高，故排在第一批之后。

| # | 窗口 | 视图文件 | 现头部 | 状态 |
|---|---|---|---|---|
| 10 | 资源管理器 | `Apps/Explorer/Views/ExplorerMainView.axaml` | `DockPanel.Dock="Top"` 的导航 + 地址栏（`app-toolbar`） | 未开始 |
| 11 | 代码编辑器 | `Apps/CodeEditor/CodeEditorView.axaml` | 菜单栏 + 文档标签 | 未开始 |
| 12 | 记事本 | `Apps/Notepad/NotepadView.axaml` | 菜单栏 + 文档名 | 未开始 |
| 13 | 任务管理器 | `Apps/TaskManager/Views/TaskManagerMainView.axaml` | 工具条 | 未开始 |
| 14 | 图片查看器 | `Apps/ImageViewer/Views/ImageViewerView.axaml` | 查看工具条 | 未开始 |

必读注意：

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

1. `TitleBarContent` 指向本窗口自己的头部控件；窗口图标与标题被模板隐藏，窗口按钮仍在宿主右侧。
2. 头部空白区可拖动窗口，**头部内的交互控件不吃拖动**：点击按钮 / 在输入框打字时窗口位置不变（参考断言见第 7 节）。
3. 内容区不再有第二条 `app-header` / `app-toolbar`，`Grid RowDefinitions` 同步删行，不留空行。
4. 三语言（`zh-CN` / `en-US` / `ja-JP`）下标题与操作不溢出、不换行错位。
5. 浅色与深色主题下文字对比度达标；头部与侧栏（`Border.app-sidebar`，`SurfaceRaisedBrush`）、状态栏的层级过渡自然。**注意融合后标题栏背景由模板强制改成 `SurfaceBrush`**（原先 `app-header` 是 `SurfaceRaisedBrush` + 下边框），这是最容易看起来「头部凹进去」的地方。
6. 窄窗口（建议 640×480，以及极窄 320×240）下头部不裁切、操作仍可达。
7. 三套窗口样式都复验：`caption-buttons-right`、`traffic-lights-left`（macOS / Ubuntu Shell，内容列会右移、按钮在左）、`headerbar-right`。
8. 任务栏与窗口总览里的窗口标题不变（融合只影响标题栏内部，`Title` 仍由 `ShowWindow` 设置）。
9. 在 `Tests/Client/RelaxKonOS.WindowPreviews.Tests` 里为该窗口补一条断言（模板见第 7 节）。
10. 更新本文第 3 节状态列与第 8 节进度日志；若该窗口在 `docs/desktop/UiReview.md` 有对应条目，同步其结论。

## 5. 开放问题（开工前定一个，别边做边改）

1. **头部高度**：模板把 `:custom-title-content` 的标题栏高度硬编码为 56（`RemoteWindowTheme.axaml:186`），是为了容纳设置的搜索框。纯标题的工作区是否需要 48？若要按应用区分，属于 `WindowManager` 契约改动，需评估——模板是 host-reviewed 的单一结构，改它比改应用影响面大得多。**倾向：先统一 56，不做分化。**
2. **图标怎么处理**：融合会隐藏窗口图标（`PART_WindowIcon`），所以 `app-header` 里的 46px 图片要么删除、要么缩到标题旁的 16–20px。逐个窗口决定并记录，别一半删一半留。
3. **共享头部控件**：9 个同构窗口不要各写一份。倾向做一个可参数化的头部（图标 / 标题 / 副标题 / 右侧操作槽），参数差异用属性表达。**在第一、二个窗口做完后再抽，避免过早抽象。**
4. **第二批菜单融合的降级策略**：窄窗口下菜单是折叠成汉堡菜单，还是退回内容区一行？先看 #11 的实际宽度再定。

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

**断言模板**（复制它，替换成新窗口）：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/SettingsWindowChecks.cs:215-228`——在 `Canvas` 上 `windows.Attach` 后 `Create` 一个 `ManagedWindow`，调用 `view.AttachWindowHeader(managed)`，然后断言：

- `ReferenceEquals(managed.View.TitleBarContent, view.Header)`
- `view.Header.Bounds.Height >= 48 && view.Header.IsEffectivelyVisible`
- 在头部输入框打字后 `managed.Info.Bounds` 不变（证明交互控件没吃拖动）
- 截图落盘（`RenderTargetBitmap` → `preview-qa`），供浅深色与三语人工比对

常看的文件：`Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`、`Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`、`Framework/RelaxKonOS.UI/Themes/ApplicationStyles.axaml`、`Client/RelaxKonOS.Client/Apps/Settings/Views/SettingsView.axaml.cs`（参考实现）。

## 8. 进度日志（只追加，倒序在上）

### 2026-10-10

- 建立本文。完成候选盘点：`Classes="app-header"` 9 处、`Classes="app-toolbar"` 5 处，逐个核对头部内容与特殊点后确定批次（见第 3 节）。
- 未改动任何产品代码；工作区干净，分支 `windows_enhance`。
- **验证回路已跑通并记录基线**：构建 + 运行 `RelaxKonOS.WindowPreviews.Tests.exe` 成功，**195 条 PASS、无异常、退出码 0**，整套约 30 秒。改动前后都用同一命令比对，命令见第 7 节。
