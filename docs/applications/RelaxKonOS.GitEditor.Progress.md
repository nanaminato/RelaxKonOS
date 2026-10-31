# 项目编辑器实施与验证

2026-10-24。首版已实现；Ubuntu 实机与连接真实服务端的完整桌面验收仍为设计中。

## 已实现

- 多 Git 主机与账号管理已接入项目选择页、Git 菜单和远程页；克隆、推送、拉取、逐远程抓取及非当前分支更新统一使用服务端受保护账号存储。协议、保存选择及当前支持边界见 [Git 客户端](RelaxKonOS.GitClient.md#多-git-主机与账号)。

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

新增条件写入能力没有替换原 Files content API 或改变 Android 已用契约，因此本次无需 Android 调用升级，也没有增加兼容路由、旧格式适配或 Android 文档副本。独立编辑器的现有 HTTP/SFTP 文件传输策略仍由其入口选择；项目编辑器使用 HTTP Files 的条件写入能力，不增加 SSH 执行；HTTPS Git 账号由独立服务端凭据存储管理。

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

## 国际化完整性复查（2026-10-24）

修复菜单显示原始键的问题：此前新增的 `code_editor.context.*` 共 20 条文案在三种语言中误放在 JSON 根节点；加载器仅合并 `Strings`。现已迁回 `Strings`，覆盖文件树/标签菜单、新建与重命名标题、删除确认和操作失败提示。此前 JSON 有效性及菜单与查询结果相等的检查没有验证实际加载结果，不能证明文案正确显示。

扩大扫描桌面端静态 `loc:Loc` 与 `LocalizedText.Get/Ref/RefWithFallback/Format` 引用，中/英/日均未发现缺失的完整字面量键；计算生成的键不计入该静态结论。额外清理三语言代理文案中的重复键 `proxy.system_proxy_mode_hint_enabled`，保留原先最终生效的版本；修正任务管理器四个作为标签直接使用的英文 `metric.*` 文案，移除不该出现的 `{0}`，保留需要格式化的 `memory_*` 文案。

新增 `LocalizationResourceChecks`，纳入 `--project-editor-only`，读取构建产物验证三语言资源层级、重复键、非空值与格式参数一致性，并通过真实 LocalizationService 检查全部 20 条菜单文案，明确断言三语言“新建文件”的翻译值，避免原始键与原始键比较的假通过。客户端构建、该检查、菜单实际点击/项目编辑器/草稿回归和 14 个应用 fused 标题栏回归通过。运行中的客户端需要使用本轮新构建并重启，以重新加载语言文件。

## 简要/详细菜单与标题栏分支操作（2026-10-24）

- 所有窗口宽度默认显示菜单按钮、项目名和分支；点击展开完整菜单，离开且弹出子菜单关闭后自动收起。删除原来按宽度维护的另一套简化菜单，统一文件/编辑/视图/Git 入口，窗口卸载停止收起计时器。
- 分支弹窗新增仓库拉取/推送/获取入口，以及所选分支的切换、切换并更新、基于分支新建、重命名、合并、比较、推送、跟踪设置、删除。这些入口复用已有业务命令和对话框；比较先打开日志面板展示文件结果。当前分支禁用重复切换和删除，远程分支禁用仅适用于本地的更新/重命名/推送/跟踪设置。
- 新增中英日操作菜单文案。设计文档记录后续分组/搜索、项目弹窗及尚未实现的 Git 能力，未把变基、标签、stash、reset、worktree 或补丁导入导出列为本轮完成。

验证：客户端及预览测试工程构建通过；`--project-editor-only` 通过，覆盖 1180/860/640 DIP 的真实鼠标展开、子菜单打开期间保持、关闭后恢复简要模式，以及双层弹窗的显式分支参数绑定和当前分支禁用。三语言/主题截图、草稿保护与文件菜单回归通过；`--fused-apps-only` 的 14 应用标题栏重挂载与布局回归通过。人工检查 640 DIP 渲染图。没有修改服务端协议；本轮不以本地 UI 回归替代真实远程 Git 操作验收。构建仍有既有包漏洞、平台分析与弃用属性警告。

## 按截图还原项目/分支弹窗与完整 Git 菜单（2026-10-24）

在上一阶段基础上进一步实现：

- 标题栏改为菜单、导航箭头、带图标/下拉箭头的项目按钮和分支按钮；详细模式显示文件/编辑/视图/导航/代码/重命名文件/工具/Git/窗口/帮助菜单。离开后恢复简要模式，Escape 可收起；窄窗口保留横向菜单滚动。320 DIP 时收起导航箭头和项目图标，避免与窗口控制按钮重叠，分支仍可由 Git 菜单打开。
- 项目弹窗包含新建空目录项目、打开目录、从版本控制获取、当前项目、会话内最近项目和其他已注册项目，显示名称、路径和分支。已有草稿可保存/放弃/取消，取消不清空当前项目；迟到的项目/日志/远程结果和操作输入不会覆盖新项目。
- 分支弹窗采用截图的搜索框、更新/提交/推送、新建/签出以及最近/本地/远程/标签树，按斜杠分目录、显示 upstream、支持会话收藏。点击分支叶节点打开以该分支为目标的动作，Enter 检出；远程和当前分支的受限操作禁用。异步引用加载在弹窗关闭后不再抢焦点。
- Git 菜单按同步、合并/变基、分支/标签/reset、工作树/贮藏、日志/补丁/文件、托管平台/远程/克隆分组。GitLab/GitHub/Azure DevOps 入口根据实际远程启用，打开去除凭据的仓库/合并请求网页。
- 新增显式 Protocol/Server/Client 契约，完成轻量标签列表/创建、普通 rebase、cherry-pick、stash 保存/应用/恢复/删除、关联工作树列表/创建/移除、文本补丁导出/应用和克隆。克隆复用 HTTPS askpass 与用户选择的加密凭据保存；重置仅 soft/mixed。引用检出和 HEAD reset 先验证提交，拒绝把文件名解释成覆盖/取消暂存命令。
- stash 按 SHA 核对，应用失败不删除记录；工作树不强制移除，Git 拒绝脏/锁定/主工作树。补丁导出按 UTF-8 字节计 200 KiB 上限，拒绝二进制/非 UTF-8 和截断；应用输入服务器上的补丁绝对路径，先 check 再 apply，拒绝 unsafe-paths。生成补丁使用新的独立文档入口，保留其他缓冲区并使旧文件读取失效，可另存为 `.patch`。

### 本轮验证

- Client 与 Server 及对应独立测试工程构建通过，保留既有包漏洞、平台/弃用属性警告。
- `dotnet run --project Tests/Server/RelaxKonOS.GitWorkspace.Tests --no-build`：48 项真实 Windows 临时仓库检查通过，覆盖 Unicode 标签/路径、重复与非法引用、用户隔离、补丁往返/失败不覆盖/大小与二进制限制、stash 成功/冲突数据保留、工作树脏/锁定/主目录保护、实际克隆、rebase/cherry-pick、冲突中止、检出/reset 的文件名歧义及共享 Helper 命令策略。
- `--project-editor-only`：真实菜单点击与三种宽度收起/展开、分支参数/分组/搜索、项目切换取消与迟到响应、延迟对话框目标保护、补丁新标签和克隆凭据重试通过；中/英/日浅深主题均渲染真实项目/分支/Git 菜单弹窗，并人工检查中文浅深预览。沿用共享草稿与文件菜单回归。
- `--fused-apps-only`：14 个应用、三种标题栏方案的布局/点击/标题栏重挂载回归通过，包含最窄 320 DIP 标题栏。
- Git Diff 的 `--all`：70 项解析和 40 项渲染通过。修正既有测试的 raw-string 固件会随源码 checkout 换行变化的问题：先显式归一到 LF，再独立构造 CRLF 用例；未改变解析器行为。

截图位于预览工程 `bin/Debug/net10.0/preview-qa/project-editor/` 的 `header-projects-*`、`header-branches-*`、`header-git-menu-*`。新增独立服务端工程与既有预览/Diff 工程均可直接运行，无需依赖总服务端测试工程。

### 仍存在的差异与验收边界

这不是完整 Rider：未提供构建/调试、语义代码分析/重构、交互式 rebase todo 编辑、标签删除/注释签名、远程分支删除、应用内 PR 工作流或跨窗口项目激活列表；“新建项目”创建普通目录，不生成 `.sln` 模板。最近记录/收藏仍限当前窗口会话，托管平台集成是浏览器链接。真实网络克隆/推送凭据、Ubuntu/Helper 有效身份、复杂并发外部 Git、高 DPI 与跨重启状态尚未验收；Linux Helper 已接受带显式凭据的 `GitExecute`（协议 `1.9`，凭据经一次性 askpass 脚本下发），Windows Helper 也已接入受限 primary token + `CreateProcessAsUserW` 的 `GitExecute`、私有 askpass、Job Object 进程树回收和有界输出；原生用户令牌测试及 Windows Server 2022 普通用户/Administrator 的真实 S4U Git 专项通过，详见 [Windows Helper Git 验收](RelaxKonOS.GitClient.WindowsAcceptance.md)，克隆保留现有 30 秒进程超时。本轮没有修改 Git 引擎安装或其他托管运行时生命周期。

## 底部 Git 多标签与日志布局（2026-10-24）

已实现独立日志/文件历史会话、控制台与概览/远程/分支/贮藏/工作树管理标签。标签可新建、复制、切换、关闭，隐藏恢复保留选择；支持最大化和打开文档时恢复编辑器。筛选、提交/文件选择、滚动与显示选项分别保存，写操作共享忙碌保护；异步读取在关闭或切换仓库后失效。

日志采用四区布局和 26 DIP 连续提交行，真实父关系绘制合并图，支持 HEAD/分支/标签引用、相对时间、文件状态/目录数量、路径历史和显式差异预览。控制台读取服务端完成命令的真实有界脱敏记录，支持增量读取、换行、清空和滚动到底部。新增 graph/console 契约与当前能力/限制见 [Git 客户端](./RelaxKonOS.GitClient.md#底部-git-多标签与日志)。

验证：Server 独立工程 60 项真实 Windows 仓库检查通过；底部独立回归覆盖不同标签内容、独立状态、父关系、引用、差异、共享忙碌、迟到请求、控制台清空/关闭、最大化/隐藏恢复，以及 320/640/860 DIP 按钮命中。项目编辑器、融合标题栏和 Git Diff（70 项解析、40 项渲染）回归通过。Client/Server 构建保留现有依赖漏洞及平台/弃用警告。

中/英/日浅深主题预览位于预览工程 `bin/Debug/net10.0/preview-qa/git-dock/`，包括 `git-toolwindow-*`、`git-log-*`、`git-console-*` 和窄窗口截图；人工复核中文日志浅色与控制台深色。预览使用确定的测试仓库数据；真实 Git 行为由上述临时仓库测试独立验证。尚未验收 Linux/Helper、远程网络与高 DPI，未提供跨重启标签恢复、无限历史索引或实时交互终端。

## 2026-10-24 Git commit controls and history presentation

- Fixed command availability notifications when the selected repository or shared busy state changes, including Commit and Commit and Push.
- Added explicit branch, author and date labels to the history filter toolbar in Chinese, English and Japanese.
- History graphs render real parent relationships with colored lanes and curved fork/merge connections; linear history remains a single lane. The graph API must be available on the connected server.
- Verified with the headless Git dock checks, including commit command notifications, merge parents, multiple lanes and narrow layouts.

## 顶部分支菜单刷新（2026-10-24）

分支弹出菜单使用独立的分支/标签刷新，不再加载暂存和工作树，避免无关接口失败弹出错误并中断分支刷新。刷新后保留所选分支，仓库切换后丢弃旧响应。新增无关维护接口返回 Not Found 时仍可刷新分支和标签的回归检查。

### 远程部署验收（2026-10-24）

实际问题为客户端已更新而远程服务端仍未包含 Git Workspace 路由；仅缩小菜单请求范围无法解决标签接口的 404。Debian 13 x64 的现有 systemd 部署已同步发布当前 Server、Guardian 和 Helper，保留配置、数据备份及旧程序目录。通过真实 nanami 身份读取原有仓库：branches/tags/graph/stashes/worktrees 全部 HTTP 200，分别返回 5/0/44/0/1 项。该结果验证服务端接口与身份执行链路，不替代客户端交互验收。

### Windows 保存凭据复用（2026-10-25）

fetch、pull 及非当前分支更新已接入与 push/clone 相同的应用加密凭据存储。按有效下载地址（包括 insteadOf）和用户隔离，不使用 pushurl，也不让宿主 credential helper 替换或另行保存应用下发的令牌。Windows Server 2022 的普通用户与 Administrator 均在不重新输入 Git 凭据的情况下通过真实 HTTPS fetch/pull、实际内容 fast-forward、非当前分支与 URL 重写检查，详见 [验收记录](RelaxKonOS.GitClient.WindowsAcceptance.md)。

### 日志筛选条的窄窗口折叠（2026-10-31）

提交列表上方的筛选条改为自适应折叠，不再横向滚动。日期、作者、分支、路径按顺序进入「更多筛选」浮层，搜索框优先保留并在必要时收窄；被折叠且已生效的条件在入口显示强调点。控件在工具条和浮层之间移动而非复制，筛选状态、下拉选项和路径弹层保持唯一。

`--git-dock-only` 新增 1475/1385/860/640/320 DIP 断言：控件只出现在工具条或浮层之一、内联内容不超宽、1385 DIP 折叠日期条件、320 DIP 全部折叠、隐藏的日期筛选点亮入口指示点、浮层控件具有实际可交互尺寸、路径输入弹层可从浮层内打开且父浮层保持打开，以及从 320 恢复到 1475 时所有条件重新展开。预览新增 `narrow-1385.png`、`filter-overflow-640.png` 与 `filter-overflow-640-dark.png`；构建保留现有平台与弃用警告。
