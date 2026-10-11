# 桌面端融合窗体头部接入规范与进度

建立日期：2026-10-10。现行规范核对日期：2026-10-11。开工前以实际 Git 分支与工作区状态为准。

本文是这项工作的**唯一进度与恢复入口**。中断后请先读第 1 节的「状态总览」和第 9 节的「进度日志」，再按第 7 节「恢复工作指引」继续，不要凭记忆重排批次。

**当前方案：宿主多段式角色槽，已经实现。**应用提交 `HeaderLeading` / `HeaderCenter` / `HeaderTrailing` / `HeaderTabs` / `HeaderToolbar`，宿主按 recipe 与角色组合安排标题、标签和导航行。旧 `TitleBarContent` 已删除，不保留兼容形态。

**实施依据只有 §2–§7 的现行规范。**§8 保存历史方案与取舍，§9 保存各次执行记录；其中已经被替代的布局、测试断言和环境命令不作为新接入的指令。日志不能覆盖正文中的现行契约，发现实现变化应先更新正文再追加日志。

设置及本文列出的 17 个应用窗口已经接入；本轮完成原先剩余的 14 个窗口。五槽契约不变，接入与验证证据见 §9 最新记录。

## 1. 状态总览

| 批次 | 范围 | 窗口数 | 状态 |
|---|---|---|---|
| 前置 | 宿主角色槽 + 各 recipe 的融合条与交通灯（方案 A，见 §8） | — | **已完成** |
| 前置 | `HeaderTabs` + `HeaderToolbar`：标签 / 导航共存与多行布局 | — | **已完成** |
| 第一批 | 与设置同构的 `Border.app-header` 工作区 | 9 | **已接入；离线回归通过，真实会话证据见 §9** |
| 第二批 | 菜单 / 地址栏型视图（其中 4 个带标签条） | 7 | **全部已接入；验收范围见 §9** |
| 顺带 | `FileServicesWorkspace` | 1 | **已接入；平台信息保留在状态栏** |
| 不做 | 模态对话框、登录窗口、远端桌面 Shell、Server / SSH 选择器模式 | — | 已定（见 3.4） |

**目标（一句话）**：把内置应用窗口内容区里那条与窗口标题栏重复的身份条搬进宿主标题栏，使每个窗口只有一层头部。

**单层身份头部的量化收益**：`Border.app-header`（`Framework/RelaxKonOS.UI/Themes/ApplicationStyles.axaml:14`）实高 78px（`Padding=24,16` + 46px 图标），叠在 32px 应用窗口标题栏（`Shared/RelaxKonOS.Protocol/Workspace/SystemStyles/BuiltInSystemStyles.cs` 的 `WindowCaptionHeight`）上共 110px；融合后由单层 `WindowFusedTitleBarHeight`（`max(48, WindowCaptionHeight + 14)`：Windows-like 48 / macOS-like 48 / Ubuntu-like 60）替代，Windows-like 下每个窗口省约 62px。此估算只适用于移除重复身份条的单行窗口；菜单、标签、导航共存时应按实际多行头部测量，不能套用 62px。默认窗口 820×560（`Client/RelaxKonOS.Client/Apps/Settings/SettingsApp.cs:80`）下约占窗口高度 11%。

## 2. 现行契约（开工前必读）

### 2.1 角色与所有权

`ManagedWindow.View` 是 `RemoteWindow`，五个角色槽的声明位于 `Framework/RelaxKonOS.WindowManager/RemoteWindow.cs`，模板位于 `Framework/RelaxKonOS.WindowManager/Themes/RemoteWindowTheme.axaml`。

| 角色 | 用途 | 应用提交内容 |
|---|---|---|
| `HeaderLeading` | 应用身份与前置操作 | 16–20px 图标、标题、返回、菜单 |
| `HeaderCenter` | 可伸缩的中心内容 | 搜索框、文档入口；中心基准为可用弹性列 |
| `HeaderTrailing` | 尾部操作 | 刷新、状态或其他少量操作 |
| `HeaderTabs` | 窗口内视图或文档标签 | 标签列表、新建与全部标签入口 |
| `HeaderToolbar` | 导航工具栏 | 导航、地址、筛选及该行必要操作 |

任一槽非空会触发 `:custom-title-content`，宿主图标与可见标题让位，但 `RemoteWindow.Title` 仍用于任务栏、窗口总览与窗口身份。只声明标签也可触发融合。标签是窗口内视图状态，不包含拖出成窗口、合并窗口等窗口级标签能力。

**宿主拥有**：槽的行列位置、槽外留白、与窗口按钮的避让、标题行高度、头部底色、拖拽规则以及 recipe 差异。窗口按钮永远由宿主渲染。

**应用拥有**：槽内控件的行列、间距、内容优先级、折叠菜单、标签缩短、搜索弹出入口及自身命令语义。应用可以布局槽内部，但不能指定宿主部件的列、通过猜测按钮宽度补空白、或写死整个融合头部高度。自适应依据槽内容实际得到的宽度，不依据操作系统名分支。

### 2.2 当前布局组合

表中的“标题行”是 `PART_TitleRow` 所在的第 0 行；“标签独立行”为第 1 行；“导航独立行”为第 2 行。未使用的行自动收缩。Windows-like 对应 `caption-buttons-right`，macOS-like 对应 `traffic-lights-left`，Ubuntu-like 对应 `headerbar-right`。

| 声明组合 | Windows-like / Ubuntu-like | macOS-like |
|---|---|---|
| leading / center / trailing，无标签、无导航 | 角色与窗口按钮共用标题行 | 角色在交通灯右侧的标题行 |
| tabs，无 center、无 toolbar | 标签在标题行弹性列；leading / trailing 可共存 | 同左，交通灯在左 |
| center + tabs，无 toolbar | 中心内容在标题行，标签独立一行 | 同左，交通灯在左 |
| toolbar，无 tabs | 标题行保留宿主按钮及已声明的其他角色，导航独立一行 | 导航进入交通灯右侧标题行；leading / center / trailing 被隐藏 |
| toolbar + tabs，无 center | 标签在标题行，导航独立一行 | 导航在标题行，标签独立一行 |
| center + tabs + toolbar | 中心内容在标题行，标签与导航各占一行 | 导航在标题行，标签独立一行；center 与 leading / trailing 被隐藏 |

实现判据：`toolbarInCaption = traffic-lights-left && HeaderToolbar != null`；`stackedTabs = HeaderTabs != null && (HeaderCenter != null || toolbarInCaption)`。`HeaderCenter` 和 `HeaderTabs` 可以同时声明，不能再按早期“互斥”方案接入。

**操作可达性约束**：应用使用 `HeaderToolbar` 时，不得把必须在所有 recipe 下可达的操作仅放在 leading / center / trailing。应优先将完整导航操作放入 toolbar，再交 tabs；若某窗口确实需要同时保留独立身份或中心操作，接入前先明确 macOS 下的操作入口与信息归属。不能将同一个 Control 同时挂到两个槽，也不能以复制命令控件制造多个主入口。单独 toolbar 会隐藏宿主标题，身份表达需要在应用接入清单中明确。

### 2.3 尺寸、样式与紧凑布局

- `WindowFusedTitleBarHeight` 只定义融合**标题行**高度，目前 Windows/macOS/Ubuntu 为 48 / 48 / 60；`PART_TitleBar` 整个头部可能包含标签与导航行，不能与这个令牌直接比较。
- 标签条读 `WindowTabHeight`，控件形状读 `ControlCornerRadius`。宿主按钮的尺寸和位置仍按 recipe 的窗口控制令牌确定。
- Windows-like / macOS-like 头部底色为 `SurfaceBrush`，Ubuntu-like 为 `SurfaceRaisedBrush`。应用不能将自己的整行底色覆盖宿主头部。
- 窄窗口先缩短可省略的文字，再折叠菜单或搜索；保留窗口按钮、当前标签关闭入口与主要任务入口。全部标签菜单与快捷键提供溢出访问，不能将关闭入口完全藏在被裁切的标签尾部。
- 允许保留文件操作、图片预览等内容区工具栏；迁移对象是重复身份条或已经交给宿主的导航头部，不是所有 `app-toolbar`。

### 2.4 `AttachWindowHeader` 接入与生命周期

由 `*App` 在 `context.ShowWindow(...)` 后调用 `view.AttachWindowHeader(ManagedWindow)`。先判断普通应用 / 选择器 / 对话框模式；选择器和模态任务不移入应用头部。参考设置、代码编辑器、Explorer 与 SSH 浏览器的现行接入实现。

控件移出原视图前后必须检查：

1. **父级与模式**：从旧容器移除后再赋给槽；检查旧行是否归零、剩余 `Grid.Row` 是否匹配新的 `RowDefinitions`。再次挂接时不重复注册事件；若视图支持更换窗口，明确解除旧宿主槽的责任。
2. **数据绑定**：显式确认头部控件的 `DataContext`、祖先绑定、名称作用域和弹出内容的数据源；不依赖原 UserControl 自动继承。检查后台状态、标题、命令参数与忙碌状态仍会更新。
3. **样式与资源**：原视图局部样式可能在重挂后失效。将必要样式放在随控件移动的容器或共享资源中，验证图标、输入框、焦点和禁用态。检查主题及语言切换后仍有效。
4. **输入与焦点**：键盘事件不再必然经过内容视图。覆盖内容区、融合头部和弹出菜单的快捷键路由，防止漏处理或重复执行；输入框中的编辑快捷键不应触发文件操作。弹出搜索关闭后恢复合理焦点。
5. **命中与拖动**：按钮、输入框、标签关闭和菜单必须接收鼠标事件；空白区域应仍可拖动窗口。直接执行命令不能替代命中检查。
6. **关闭与释放**：保留窗口及草稿关闭守卫；明确关闭最后一个标签的语义。窗口关闭时解除订阅、结束窗口持有的工作并释放图片等资源，避免控件移出内容树后遗漏清理。

接口演进按 `AGENTS.md`：不保留旧契约别名或双形态，同一改动更新全部仓库调用者、测试与文档。选择器模式是产品语义分支，不是旧 API 的兼容路径。

## 3. 范围

### 3.1 第一批：`Border.app-header` 工作区（9 个，与设置同构）

接入前共同形态：窗口内容区第一行是一条 `Border.app-header`，内含 46px 应用图标 + `TextBlock.app-title` + `TextBlock.app-description`，部分窗口右侧还挂一个刷新按钮或状态胶囊。多数窗口使用 `Grid RowDefinitions="Auto,*,Auto"`；移除身份块后必须同步调整行编号与星号内容行，不能只删控件或只改 `Grid.Row`。

| # | 窗口 | 视图文件（`Client/RelaxKonOS.Client/`） | 头部右侧内容 | 状态 |
|---|---|---|---|---|
| 1 | 服务器中心 | `Apps/ServerCenter/Views/ServerCenterWorkspace.axaml` | 无（仅 `Title`/`Subtitle` 绑定） | **已接入** |
| 2 | Git | `Apps/Git/Views/GitClientWorkspace.axaml` | 分支胶囊 + 刷新（`IsPickerMode` 时隐藏） | **已接入** |
| 3 | Docker | `Apps/Docker/Views/DockerManagerWorkspace.axaml` | 状态胶囊按钮（可打开安装向导） | **已接入** |
| 4 | 代理 | `Apps/Proxy/Views/ProxyManagerWorkspace.axaml` | 刷新按钮 | **已接入** |
| 5 | 隧道 | `Apps/Tunnels/Views/TunnelManagerView.axaml` | 刷新按钮 | **已接入** |
| 6 | 端口转发 | `Apps/PortForwarding/Views/PortForwardingMainView.axaml` | 刷新按钮 | **已接入** |
| 7 | 网站 | `Apps/WebServers/Views/WebServerManagerWorkspace.axaml` | 无 | **已接入** |
| 8 | 证书 | `Apps/Certificates/Views/CertificateManagerWorkspace.axaml` | 无 | **已接入** |
| 9 | 应用部署 | `Apps/ApplicationDeployments/Views/ApplicationDeploymentsWorkspace.axaml` | 无 | **已接入** |

排序理由：先做 #1（头部主要为两个文本，先验证信息重新归属与接入模式）与 #2（同时验证「标题栏内放交互控件」，且分支胶囊是这套里最有表现力的元素），跑通后 #3–#9 可复用模式，但仍逐个验收状态胶囊、刷新、选择器模式和紧凑布局。

### 3.2 第二批：菜单 / 导航型视图（7 个，其中 4 个带标签条）

这些不是标题重复，而是菜单条 / 地址栏。融合后窗口按钮、拖拽区、菜单、地址栏按 §2.2 分配到宿主头部的相应行，收益更高但正确性风险也更高，故排在第一批之后。

| # | 窗口 | 视图文件 | 现头部 | 标签条 | 状态 |
|---|---|---|---|---|---|
| 10 | 资源管理器 | `Apps/Explorer/Views/ExplorerMainView.axaml` | 导航 / 地址 / 筛选交 `HeaderToolbar`；文件操作栏留在正文 | **已有**窗口内标签，独立历史 / 搜索 / 视图状态 | **已接入** |
| 11 | 代码编辑器 | `Apps/CodeEditor/CodeEditorView.axaml` | 菜单栏 + 文档标签 | **已有** `ListBox.title-tabs` | **已接入** |
| 12 | 记事本 | `Apps/Notepad/NotepadView.axaml` | 菜单栏 + 文档名 | 无 | **已接入** |
| 13 | 任务管理器 | `Apps/TaskManager/Views/TaskManagerMainView.axaml` | 工具条 | 无 | **已接入** |
| 14 | 图片查看器 | `Apps/ImageViewer/Views/ImageViewerView.axaml` | 查看工具条 | 无 | **已接入** |
| 15 | 浏览器 | `Apps/Browser/Views/BrowserMainView.axaml` | `DockPanel.Dock="Top"` 的标签条 + 导航工具条与地址栏 | **已有** `ListBox.browser-tabs` | **已接入** |
| 16 | SSH 文件浏览器 | `Apps/ServerCenter/Views/SshFileBrowserView.axaml` | SFTP 导航 / 地址 / 筛选 | **已有**独立目录快照、历史 / 搜索 / 视图状态 | **已接入** |

必读注意：

- **带标签条的窗口（#10 / #11 / #15 / #16）一律交 `HeaderTabs`**，由宿主决定内联或独立行，不在正文重复排列标签。标签条高度读 `WindowTabHeight`，选中形状读 `ControlCornerRadius`；尚未接入的浏览器应移除自己的固定标签形状，已接入窗口以共享 `title-tabs` 样式回归。
- **#10 同时是文件选择器宿主**。选择器模式（`ExplorerPickerOptions`）必须保留任务对话框布局——标题栏要说明「选择文件夹」这个任务，而不是应用身份。现行实现只在普通主窗口模式接入；后续改造保持这一边界。
- **#16 也用于 SSH 安装包选择**：选择器隐藏应用标签，保留任务标题与独立导航布局；不能因主窗口增加标签改变 ZIP 包筛选和确认语义。
- **#11 / #12 带草稿守卫**（`DraftWindowCloseGuard` / `DraftDialogGuard`，见 `docs/desktop/UiReview.md` D-009）。迁移头部时不要改关闭语义，也不要让新头部吃掉草稿提示。
- #11 / #12 的融合形态等价于「菜单栏搬上标题栏」（VS Code、Win11 记事本的做法），是这套改动里视觉变化最大的一组，需要浅深色 + 三语单独截图确认。

### 3.3 顺带：`FileServicesWorkspace`

`FileServicesWorkspace` 已使用共享身份控件：leading 为文件服务图标，center 为标题，trailing 为刷新；平台信息在状态栏及身份提示中保留。原正文刷新 / 平台标题块已经移除，原有紧凑导航布局保留。

### 3.4 明确不做

| 对象 | 原因 |
|---|---|
| 模态对话框（`ConfirmDialogView`、`TextInputDialogView` 及各 `*DialogView`） | 窗口本来就小，没有多余的条带可省；融合会丢掉模态层级与任务标题 |
| `LoginWindow` | 自己绘制窗口、没有系统装饰 |
| 远端桌面 `MainWindow` | 标题栏是 `WindowTitleBar` 与 mstsc 连接条的共享顶层，承担全屏切换（`_isFullScreen` 时整条隐藏）与断开操作，语义完全不同 |
| Server 文件选择器 / SSH 安装包选择器 | 见 3.2 #10 / #16；保留任务标题与独立对话框布局 |

## 4. 验收标准

每个窗口接入后逐项确认，缺一项不算完成。测试结果分别记录为“代码 / 离线布局通过”与“真实会话交互通过”，离线截图不能替代真实 Server 或 SSH 会话验收。

1. 声明的五个角色与 §2.2 一致，宿主窗口按钮没有被遮挡或由应用复制；窗口身份在任务栏与窗口总览中保持正确。
2. 仅删除重复身份条或迁入宿主的导航头部，保留必要的内容区工具栏；旧容器为空时收缩，`Grid.Row` 与星号内容行正确，内容区不被挤压。
3. 空白处可拖动；点击、输入、标签切换、关闭按钮与弹出菜单不会拖动窗口。验证鼠标命中和实际操作，不能只执行命令。
4. 三种语言下文字正确、省略合理；切换主题或语言后绑定、图标、输入框、禁用态与焦点样式仍生效。
5. 三种 recipe × 明暗主题 × 三种语言复验，覆盖角色组合的行列、按钮位置、头部与正文层次。
6. 覆盖常规 960px、紧凑 640px、极窄 320px 宽度及各应用折叠断点附近；高度按宿主允许的最小值测试。窗口按钮、当前标签关闭、全部标签入口和主要操作可达，不以裁切隐藏代替降级。
7. 单行融合断言 `PART_TitleRow.Height` 消费 `WindowFusedTitleBarHeight`；多行融合另外验证实际行分配、各行边界与整个头部高度，不能断言整个 `PART_TitleBar` 高度等于标题行令牌。
8. 快捷键在正文、头部输入框与弹出内容中按语义执行一次；初始焦点、菜单关闭后的焦点、Tab 导航及控件无障碍名称正确。
9. 标签切换保留各自状态，关闭选中项正确激活相邻项；忙碌、失败和延迟操作不能误改其他标签。缓存切换与重新读取的策略由应用明确记录，宿主不承担数据读取。
10. 草稿守卫、关闭最后一个标签、选择器确认 / 取消与窗口关闭资源释放维持原有语义。
11. 在 `Tests/Client/RelaxKonOS.WindowPreviews.Tests` 增加有意义的行为与布局断言；渲染截图并人工检查。未接入真实后端的项目标记“待真实会话验证”，不写成完全验收。
12. 更新 §1 / §3 状态和 §9 日志；在 `docs/desktop/UiReview.md` 有对应条目时同步结论。记录实际命令、范围、结果和尚未验证项。

## 5. 接入决策与后续扩展顺序

五槽契约、标题行令牌、弹性列居中与视图级标签已定，不重新开放。以下信息须在相应窗口动工前写入接入清单：

- **身份与信息归属**：图标保留为 16–20px，还是由标签表达身份；原副标题中的连接状态、版本或帮助文字移往正文、状态栏或提示，不能因搬迁直接丢弃。
- **槽与操作入口**：列出需要的槽、操作优先级、最窄模式下的菜单入口；使用 toolbar 的窗口说明 macOS 隐藏其他槽后的信息和操作去向。
- **模式与关闭语义**：确认应用主窗口、选择器、模态任务的接入边界，以及草稿、忙碌和最后标签关闭策略。
- **共享控件**：先接服务器中心和 Git，验证身份、分支与操作三类差异，再抽取小型共享身份控件；窗口按钮和槽布局不放进应用共享控件。不先做能包含所有窗口功能的通用头部框架。

本轮已按服务器中心 / Git 的形态验证共享身份方案，再完成其余工作区和菜单 / 导航型视图。共享 `WorkspaceWindowHeader` 仅提供图标、弹性标题和信息提示；应用操作仍由调用视图提交到 trailing。后续新增窗口先核对 §2 的组合与已有样例，再增加对应回归，不重新设计槽契约。复杂窗口的菜单折叠以实际槽宽度和操作可达性验收，不再泛泛保留“以后再定”的降级问题。

## 6. 与既有文档的关系

- `docs/desktop/RelaxKonOS.Settings.md`（第 26 行）与 `docs/desktop/RelaxKonOS.Settings.Windows11.Progress.md` 记录了设置的顶栏融合，是本模式的先例与验收参考。
- `docs/desktop/RelaxKonOS.BuiltInApps.UI.md`、`docs/desktop/RelaxKonOS.Desktop.md` 是设计规则来源；本文提供接入规范与进度，冲突时以设计规则为准，并同步修正本文。
- `docs/desktop/UiReview.md` 是本专项的审查清单与 D-xxx 决定记录（D-009 顶层窗口关闭守卫与本工作有交集）。

## 7. 恢复工作与验证指引

先读 §1 / §3 的当前状态与 §9 最近日志，核对实际分支、用户未提交改动和接入视图。不要从历史日志复制已经过期的模板断言、环境路径或窗口清单。

在仓库根目录使用当前可用的 .NET SDK；以下 PowerShell 命令不依赖个人 Python、Git Bash 或临时目录路径：

```powershell
git status --short
git branch --show-current
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -c Release -p:BuildInParallel=false -- --fused-apps-only
```

`--fused-apps-only` 运行宿主角色槽、已融合应用、SSH 文件浏览器和编辑器草稿 / 预览检查，适合本专项的局部回归。更多测试入口以 `Tests/Client/RelaxKonOS.WindowPreviews.Tests/Program.cs` 为准；收尾按变更范围运行相应设置 / recipe 检查和完整窗口预览套件，不因文档中的历史 PASS 数固定预期。

若运行中的客户端锁定构建产物，可在仓库内使用独立输出目录：

```powershell
dotnet build Tests/Client/RelaxKonOS.WindowPreviews.Tests -c Release -m:1 -o .artifacts/fused-header-review
& ./.artifacts/fused-header-review/RelaxKonOS.WindowPreviews.Tests.exe --fused-apps-only
```

输出目录保持在仓库内，确保需要查找仓库根的扫描没有被静默跳过。检查退出码和异常，不仅统计 PASS。截图位于实际测试输出目录的 `preview-qa/` 下；本机未运行的命令不得记为已经验证。

参考检查：

- `SettingsWindowChecks.cs`：身份 / 搜索与设置交互。
- `FusedWindowHeaderChecks.cs`：三种 recipe、角色组合、窗口按钮几何和拖拽。
- `FusedApplicationChecks.cs`：实际代码编辑器与 Server Explorer、标签状态和选择器隔离。
- `SshFileBrowserLayoutChecks.cs`：SSH 标签、筛选、忙碌限制与紧凑布局。
- `FusedWorkspaceChecks.cs`：本轮 14 个视图 × 三种 recipe × 明暗主题 × 三语 × 960/640/320；身份绑定、实际按钮命中、紧凑导航和 Git 模式切换。

新增断言应验证已声明角色与实例身份、未声明槽为空、对应部件实际可见和布局合理。重挂后的数据与输入必须另外验证。单行与多行高度按 §4 第 7 项分别处理；直接执行命令不能证明按钮能被鼠标命中。

真实应用验收入口为 `--fused-all-apps-review`（Server 登录）和 `--fused-ssh-review`（SSH 桌面）。连接目标和凭据仅通过测试进程环境的 `RK_REVIEW_SERVER` / `RK_REVIEW_USER` / `RK_REVIEW_PASSWORD` 提供；不把实际值写入命令示例、文档或仓库。两种入口只读取远端状态、目录并检查窗口，Git 只打开已有注册项目，不创建测试仓库。SSH 使用生产宿主密钥判定；测试覆盖指纹选项只有用户明确核对指纹后才可使用，并且只在进程内保留。

构建或检查失败时先解决失败，再更新状态。真实后端测试记录会话类型、交互范围与结果；凭据只通过既有安全输入方式提供，不写入文档、仓库或截图。

## 8. 历史附录：系统风格与早期方案（非实施规范）

2026-10-10 追加。起因：设置的融合顶栏在 `macOS 风格` 下明显不对（见下 D1–D2），而风格被认为可以随桌面程序扩展，因此不能只针对 macOS 打补丁。

以下记录保留当时的论证；“三个 / 四个槽”“center 与 tabs 互斥”“暂不做独立行”等描述均不代表当前契约。当前行为、范围和验收只按 §2–§7 执行。历史中的旧章节编号指当时版本，不作为当前章节导航。

### 8.0 早期问题与决定快照（已被现行规范替代）

1. ~~**头部高度**~~ **已定**：不再硬编码。融合条高度取派生键 `WindowFusedTitleBarHeight = max(48, WindowCaptionHeight + 14)`，随风格得到 48 / 48 / 60；应用读同一个键，不自己写数字。
   融合条长高不会把窗口按钮一起拉长：按钮恒为 `WindowControlWidth × WindowCaptionHeight`，`caption-buttons-right` 下贴顶（见
   [系统风格 §4.3](RelaxKonOS.SystemStyle.md)）。三枚记号同样不再由字形决定——它们是宿主几何
   （`WindowCaptionMarks`），尺寸只取 `WindowCaptionGlyphSize` 这个记号盒，所以融合条或字体怎么变都改不动记号的粗细与位置。
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

1. **✔** 融合条的 `Height` / `Background` 不再一刀切：高度取派生键 `WindowFusedTitleBarHeight`（`max(48, WindowCaptionHeight + 14)`），底色每条 recipe 显式给出（`headerbar-right` 用 `SurfaceRaisedBrush`，因为 headerbar 是应用自己的工具条）。
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

- **不新增「有标签时的融合条高度」派生键**。三条内置风格的 `WindowFusedTitleBarHeight`（48 / 48 / 60）都大于
  `WindowTabHeight + 2×内边距`（默认 28 + 16 = 44），标签条放得下。将来某条风格真需要更高的栏，再加派生键——契约不变。
- **不做窗口级标签**（见 §5.7）。
- **不做「栏外另起一行」的模板分支**：目前没有 recipe 需要，先不加结构；接口已为此留好。

**验证**：`Tests/Client/RelaxKonOS.WindowPreviews.Tests/FusedWindowHeaderChecks.cs` 增加标签条用例，逐 recipe 断言
`PART_HeaderTabs` 落在弹性列（`Grid.Column == 2`）、居中槽让位、宿主标题让位、与按钮组 / leading / trailing 均不重叠、
标签条确实**吃满**弹性列（左界在 leading 之后、右界不越过按钮组或 trailing）、`WindowTabHeight` 可解析；
另断言「清空标签条后居中槽恢复」与「只有标签条、其余三槽为空时仍然融合」。
截图 `preview-qa/fused-header/fused-header-tabs-{recipe}.png`。

## 9. 进度日志（只追加，倒序在上）

### 2026-10-11（剩余十四个窗口接入与验收）

- 第一批服务器中心、Git、Docker、代理、隧道、端口转发、网站、证书和应用部署全部接入；共享 `Controls/WorkspaceWindowHeader` 将 20px 身份图标交 leading、弹性标题交 center，原副标题保留为提示或正文信息。Git 分支 / 刷新、Docker 安装状态操作与其余刷新命令仍有各自绑定；Git 项目选择模式不显示应用融合头部，进入工作区时重新挂接。
- 记事本保留 File / Settings 命令与保存锁定，紧凑宽度提供菜单入口；任务管理器的页面切换 / 刷新进入头部，连接与状态文字留在状态栏；图片查看器保留缩放、适应与原始尺寸，信息留在预览区，窄窗口不再由提示文字撑出横向滚动。
- 浏览器交 `HeaderTabs` + `HeaderToolbar`，采用共享标签形状和动态缩短，提供全部标签入口；导航样式随控件移动，窄窗口将前进 / 主页 / 收藏操作收进菜单。原生 WebView 生命周期与菜单遮挡处理保留。
- 文件服务身份 / 刷新进入宿主，平台信息保留。应用部署的替换策略及空闲状态说明继续显示在正文。八个侧栏型工作区增加紧凑页面菜单，复用原导航动作；保持完整内容宽度，避免 320px 下侧栏挤压正文。
- `FusedWorkspaceChecks` 覆盖全部新接入视图的三 recipe、明暗、三语和 960/640/320 宽度；额外验证实际按钮命中、Git 选择器往返、重挂后的标题更新、紧凑导航及正文行几何。命中检查先提交 headless 渲染帧，避免用过期场景进行判断。
- 已通过完整 WindowPreviews 套件及 Settings 套件；recipe coverage 和产品颜色扫描均实际执行，未 SKIP。对应最终记录为 `.artifacts/fused-window-final.log` 和 `.artifacts/fused-window-matrix-final.log`；本轮已有的客户端警告不属于头部改动。
- 真实 Server 会话：正常证书验证成功，十四个融合窗口完成三 recipe、960/320 的实际客户端检查；代理、隧道、文件服务与任务管理器刷新针对已认证服务执行。Git 测试账号未提供已注册项目，真实会话验证项目选择器不融合；工作区的分支 / 刷新、模式往返与绑定由离线检查覆盖，没有创建或注册测试仓库。结果见 `.artifacts/live-fused-final.out.log`。
- 真实 SSH 会话：正常宿主密钥校验成功，服务器中心与 SSH 文件浏览器完成三 recipe、960/320 检查；SFTP 主目录 / 根目录读取、独立筛选、标签切换与关闭相邻标签通过。未执行远端安装、删除、上传或其他写操作；记录为 `.artifacts/live-ssh-fused-review.out.log`。
- 截图目录（相对预览测试输出目录）：`preview-qa/fused-workspaces/`、`preview-qa/live-fused-workspaces/`、`preview-qa/live-ssh-fused/`。实际截图已检查，凭据不写入这些产物。
- 本轮完成融合接入与对应读取 / UI 回归，不将其扩大为各管理应用全部安装、修改、断线恢复或草稿未知结果的验收；D-009 尚未完成的后台写入场景仍保持原结论。


### 2026-10-11（接入规范整理；后续批次实施前优化）

- 将现行五槽规则、角色组合与早期方案分开；旧开放问题归入 §8 历史附录，历史日志不再作为恢复执行指令。
- 补齐三种 recipe 的组合布局表，说明 macOS toolbar 占据标题行时其他槽隐藏的可达性约束。
- 明确宿主槽外布局与应用槽内布局的边界；保留必要的正文工具栏，分别验收标题行和整个多行头部高度。
- 加入重挂后的数据绑定、样式资源、输入路由、焦点、命中及关闭清理检查，修正正文星号行和窄标签关闭按钮的验收要求。
- 状态表加入已接入的 SSH 多标签浏览器；整理剩余窗口的实施前清单与共享控件提取顺序。
- 恢复命令改用仓库内输出与当前 .NET 测试入口；区分离线验证和真实会话验收。
- 本次仅优化文档，对照现有 `RemoteWindow`、模板及测试入口核对；未运行构建或真实会话测试，未宣布剩余窗口完成。


### 2026-10-11（代码编辑器 / 资源管理器优先接入）

- 宿主增加 `HeaderToolbar`，让导航和标签共存；中心控件和标签同时存在时不再隐藏中心控件。
  模板为标题、标签、导航提供宿主行；macOS-like 导航进入交通灯右侧的标题行，Windows-like 标签在顶行、导航在下一行。
- 代码编辑器：菜单和文档入口融入标题行，文档标签交宿主独立行；紧凑菜单、全部文档菜单、Ctrl+Tab / Ctrl+Shift+Tab、
  文档内查找（Ctrl+F）。关闭选中标签前保存激活身份，修正 ListBox 回写 null 后无法激活相邻文档的问题；草稿守卫保持有效。
- 资源管理器：加入窗口内多标签，每个标签保存路径、历史、过滤词和视图设置；目标目录读取成功后才提交切换，
  失败保留当前目录，导航 / 文件操作忙碌或重命名时禁止切换。Ctrl+T / Ctrl+W / Ctrl+Tab / Ctrl+Shift+Tab；
  全部标签菜单保证溢出时可达。导航、地址、当前目录名称过滤交给工具栏角色；窄窗口搜索收进可展开按钮，地址面包屑滚到当前目录。
  文件夹右键菜单支持在新标签中打开。
  视图 / 排序 / 隐藏项选项收进弹出面板。文件选择器保留任务标题与内容区导航，不接入应用标签。
- 新检查 `FusedApplicationChecks.cs`：两款实际视图 × Windows/macOS × 浅深色 × 三语 × 960/640/320 宽度；
  另验证历史 / 搜索隔离、失败与延迟目录读取、融合后关闭命令、未保存文档与选择器隔离。
  预览测试补齐真实客户端使用的 DataGrid / AvaloniaEdit 样式。
- 离线截图：`.artifacts/header-review/preview-qa/fused-apps/`。
- 参考：[VS Code UI](https://code.visualstudio.com/docs/editing/getting-started/userinterface)、
  [VS Code 布局](https://code.visualstudio.com/docs/configure/custom-layout)、
  [Finder 文件组织](https://support.apple.com/guide/mac-help/organize-your-files-in-the-finder-mchle9f0a1b2/mac)、
  [Finder 设置](https://support.apple.com/en-ie/guide/mac-help/mchlp2803/27/mac/27)。
- 验证状态：窗口预览完整套件通过；资源管理器 253 项回归通过；设置 / recipe coverage / 产品颜色扫描通过；三语资源一致。
  登录后实际 UI 验证被测试服务器的 TLS 证书校验阻止，等待本次进程临时信任的确认；不能以离线截图替代真实服务验收。
  可选实际客户端入口：预览测试 exe 的 `--fused-live-review`，凭据仅通过进程环境变量
  `RK_REVIEW_SERVER` / `RK_REVIEW_USER` / `RK_REVIEW_PASSWORD` 提供，不保存账号或密码。


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
