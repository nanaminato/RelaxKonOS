# 项目编辑器实施与验证

2026-10-24。首版已实现；Ubuntu 实机与连接真实服务端的完整桌面验收仍为设计中。

## 已实现

- 原 `relaxkonos.git` 直接演进为“项目编辑器”，App ID 不变，Manifest 版本为 `0.3.0`，声明文件读写和 Git 权限。
- `ProjectEditorViewModel` 协调一个窗口的项目、文档会话与 Git；使用原 `CodeEditorViewModel`、可嵌入的 `CodeEditorView.UseProjectLayout()` 和共享 `CodeEditorSessionDialogs`。独立 Code Editor 仍使用同一编辑面板与文件对话框。
- 普通文件夹直接打开，不强制初始化 Git；仓库打开后可浏览完整文件树。项目树双击/Enter 打开文件；多标签保留独立文本缓冲区、撤销栈、光标和滚动位置。图片、SVG、二进制分类沿用原实现。
- 顶部改用文件、编辑、视图、Git 菜单及项目/分支入口，仅保留刷新快捷按钮；普通目录在 Git 菜单提供显式初始化。左侧活动条切换 Project/Commit，底部打开 Git；项目工具区与 Commit 工具区互斥。
- Commit 区分别显示 staged、unstaged、untracked、conflict；index 与 worktree 两项共享同一路径的提交勾选，点击打开各自 Diff，右键提供打开工作树文件、暂存、取消暂存和还原。底部日志、分支、远程和概览可切换与收起，不卸载编辑器。
- 工作树、index 和指定提交的 Diff 以不同身份打开只读标签；切回 Diff 标签重新查询，旧请求不会覆盖用户后来打开的文件或比较。
- 关闭脏标签/窗口与会修改工作树/index 的 Git 操作前，列出文件供保存/放弃/取消。保存失败阻止后续操作；操作期间锁定文本编辑。分支创建的默认 checkout、推送拒绝后的 pull、冲突写入/继续/中止也经过草稿检查。
- 项目编辑器经 `PUT /api/v1.0/files/conditional-content` 保存：已有文件提交读入字节的 SHA-256，新建/另存为使用排他创建；冲突返回 `409 text-file-changed`，保留草稿。正文按字节传输，保留编码与换行。条件编辑上限 256 KiB，超过上限的文本只读；UTF-8 BOM、UTF-16/32 的 BOM 保存规则已补齐。
- 显式刷新、保存和 Git 操作联动状态；干净文本可在刷新时更新，脏文件或外部删除保留缓冲区并提示重新加载/另存副本。服务端不把应用内锁当作对所有外部程序的事务隔离。
- 新界面资源同步中、英、日三语言，Diff 标题和历史筛选可随语言切换；复用现有浅色/深色主题资源。

快捷键：`Ctrl+S` 保存，`Ctrl+Shift+S` 全部保存，`Ctrl+N` 新建，`Ctrl+W` 关闭当前标签，`Ctrl+F` 查找，`Ctrl+Tab` / `Ctrl+Shift+Tab` 切换文档。`Alt+1` / `Alt+0` / `Alt+9` 切换 Project / Commit / Git，`Ctrl+K` 打开提交，`Ctrl+T` 拉取，`Ctrl+Shift+K` 推送，`Ctrl+O` 打开文件。另存为通过文件菜单进入；独立 Code Editor 原快捷键保持其自身语义。

## 协议和平台边界

新增契约为 `ConditionalFileWriteRequest(Path, Content, ExpectedVersion)` 和 `ConditionalFileWriteResult(Version)`；`Content` 是 JSON 中的字节数组（Base64）。版本由同一份原始字节计算，不用文本长度或时间戳代替。目标已存在时不能借新建请求覆盖；写入失败清理暂存文件。

新增条件写入能力没有替换原 Files content API 或改变 Android 已用契约，因此本次无需 Android 调用升级，也没有增加兼容路由、旧格式适配或 Android 文档副本。独立编辑器的现有 HTTP/SFTP 文件传输策略仍由其入口选择；项目编辑器使用 HTTP Files 的条件写入能力，不增加 SSH 执行或 Git 凭据存储。

Windows 上完成本地构建、有效文件服务的临时目录测试和 Avalonia Headless 行为/截图验证。Linux/Windows 的有效用户文件服务复用现有实现；Ubuntu 实机权限、符号链接及并发外部写入的验证仍未完成，不能由 Windows 自动化结果推定通过。

## 验证入口与结果

```powershell
dotnet build Tests/Client/RelaxKonOS.WindowPreviews.Tests --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests --no-build -- --project-editor-only
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests --no-build -- --fused-apps-only
dotnet build Tests/Server/RelaxKonOS.Editor.Tests --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project Tests/Server/RelaxKonOS.Editor.Tests --no-build
dotnet build Tests/Client/RelaxKonOS.GitDiff.Tests --no-restore -m:1 -p:UseSharedCompilation=false
dotnet run --project Tests/Client/RelaxKonOS.GitDiff.Tests --no-build -- --all
```

客户端专用入口覆盖项目打开、路径越界输入、草稿取消/保存失败、提交路径选择、外部修改/删除、独立 Diff 标签与请求竞态、BOM/CRLF、多标签撤销与光标、销毁后的迟到读取、普通目录不初始化 Git、三语言切换和 1180/860/640 DIP 布局；同入口执行原 `EditorDraftStateChecks`。生成截图在该工程输出的 `preview-qa/project-editor/` 下，包括三语言浅/深色预览。

`--fused-apps-only` 回归通过：14 个应用视图在三种标题栏方案、主题、语言和窗口宽度下验证；包含 Git 项目选择器/编辑器切换、分支状态绑定和刷新命令在标题栏重挂载后的保留。共享编辑器与文件浏览器的多标签、关闭保护和窄布局回归也通过。

服务端专用入口复用 `TextEditorChecks`，34 项通过，包含条件新建、旧版本拒绝、排他另存为、字节格式、大小/路径/版本校验和暂存清理。受当前运行沙箱约束，测试时将进程 `TEMP`/`TMP` 指向工程 `bin/test-temp`，避免系统临时目录的移动权限影响验证；不修改用户级环境设置。

Git Diff 原有解析 70 项与渲染 40 项通过。Client 与 Server 主项目构建通过；现有包漏洞/平台分析警告没有在本次变更中处理。`RelaxKonOS.Server.Tests` 总工程仍被已有 `NetworkProxyTunnelChecks` 对已移除 `MihomoRuntimeManager.InstallManagedAsync` 的引用阻塞，因此新增独立 Editor 测试入口，不以绕过总工程问题宣称全套服务端测试通过。

## 仍为计划中或设计中的部分

- 窗口激活时自动刷新、文件监听、跨重启草稿恢复、跨设备编辑同步。
- 历史版本的完整源码快照标签（目前历史审阅通过只读 Diff）；分栏编辑、项目索引、LSP、调试、终端、行级暂存和可编辑 Diff。
- 跨会话布局持久化及更多停靠交互。目前侧栏/底栏可折叠、缩放，尺寸在当前窗口记忆；窄窗口使用折叠菜单和覆盖侧栏，日志隐藏分支树并保留筛选入口，筛选栏横向滚动。
- 真正连接 Windows/Ubuntu 服务端的全流程手工验收，包括远程 push/pull 凭据、复杂冲突和高 DPI。既有 Git 操作入口被保留，当前本地测试不能替代这些环境验证。

目标与后续验收继续遵循 [改造设计](./RelaxKonOS.GitEditor.Design.md)。

## 截图布局调整（2026-10-24）

按用户提供的 Rider/IDEA 截图重排：移除顶部动作按钮堆叠，提交列表缩为紧凑行，消息框固定在左下，日志横跨下方并采用单行提交记录、右侧文件树与详情。项目树复用共享编辑器同一个控件；编辑器空态显示工具窗口快捷键。文件菜单新增返回项目选择器，沿用草稿保存/放弃/取消保护。

本轮重新通过客户端构建、项目编辑器行为/三语言与主题截图检查及 fused 应用回归；新增菜单折叠、底部跨栏和紧凑日志断言。未重新运行服务端测试，本轮未修改服务端或运行时生命周期。

### 工具窗口点击修复

修复从项目选择页进入仓库后 Commit/Git 活动条仍禁用的问题：工具入口直接绑定仓库选择状态，不受 Git 忙碌状态限制；`CanManage` 在仓库与忙碌状态变化时补发属性通知，实际 Git 操作仍遵循原有执行限制。此前布局测试直接设置面板状态，未覆盖此点击路径。回归改为先挂载选择页，再打开仓库，以真实指针点击验证 Commit/Git 展开和收起、忙碌期间导航；客户端构建及项目编辑器/共享草稿回归通过。

## 文件树与标签快捷操作（2026-10-24）

参考 [IDEA Project tool window](https://www.jetbrains.com/help/idea/project-tool-window.html)、[IDEA Editor basics](https://www.jetbrains.com/help/idea/using-code-editor.html) 与 [VS Code User interface](https://code.visualstudio.com/docs/editing/getting-started/userinterface)，在共享 Code Editor 面板中实现上下文菜单，项目编辑器和独立编辑器共用：

- 文件树：打开文件、新建文件/文件夹、重命名、删除、复制绝对/工作区相对路径；目录还提供刷新和折叠。工作区根不提供重命名/删除；独立编辑器可从工作区移除根目录。新建文件使用条件写入排他创建，连接不支持此能力时禁用该入口。
- 标签：保存、关闭当前、关闭其他、关闭左侧、关闭右侧、关闭已保存、全部关闭，及复制路径。操作以右键点击的标签为目标；批量关闭逐个复用草稿确认，取消或保存失败即停止后续关闭。
- 项目树快捷键：Enter 打开文件，F2 重命名，Delete 删除。删除先确认，涉及未保存文档时沿用保存/放弃/取消；远程失败不丢弃标签。文件夹重命名同步更新打开文档的路径并保留缓冲区。
- 路径变更期间阻止编辑、保存、项目切换和窗口关闭；成功后刷新文件树与 Git 状态。没有修改服务端 API 或增加兼容路由。

`EditorContextChecks` 纳入 `--project-editor-only`：覆盖批量关闭取消/保存失败、目标标签身份、排他新建冲突、非法名称、目录重命名保留草稿、删除取消/失败/成功及真实鼠标右键菜单。仍不包含拖放移动、文件剪贴板复制/粘贴、分栏编辑和远程实机验收。

本轮客户端构建、项目编辑器（含上下文菜单真实点击与草稿保护）、共享草稿检查、14 个应用的 fused 标题栏回归通过；中英日各 20 条菜单文案通过 JSON 与重复键检查。

## 图标与背景调整（2026-10-24）

- 新增主题着色的 `WorkspaceIcon` 线框控件，统一活动条、日志分支树、搜索、操作按钮、日志上下文菜单与项目选择器的符号；变更文件树使用 Explorer 的文件类型图标，避免字符图标受字体影响。
- 活动条使用柔和强调底色和强调色描边，替代实心强调块；项目树背景与选中行使用语义背景资源，中央编辑器保持清晰的阅读表面。状态标记改用彩色字母，减少饱和色块。
- 项目选择器使用居中的内容区域、分层底色、带边框项目卡片和独立的打开/注册操作；主操作使用强调色浅底，浅/深主题均从现有调色板动态取色。
- Framework 共享 `Button.header-refresh`：28 DIP 圆角轻描边、14 DIP 双箭头线框、悬停强调和按下/禁用状态。项目编辑器、文件浏览器、SSH 文件浏览器、代理、代理服务器、Squid、隧道、端口转发、任务管理器和文件服务采用该样式。保留原命令、提示和无障碍名称，不修改刷新逻辑。

客户端构建和项目编辑器行为测试通过；项目树、项目选择器与编辑器新增中/英/日浅深色渲染预览。14 个应用的 fused 标题栏及文件浏览器相关回归通过；本次不改服务端或运行时管理。
