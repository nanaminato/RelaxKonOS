# 项目编辑器实施与验证

2026-10-24。首版已实现；Ubuntu 实机与连接真实服务端的完整桌面验收仍为设计中。

## 已实现

- 原 `relaxkonos.git` 直接演进为“项目编辑器”，App ID 不变，Manifest 版本为 `0.3.0`，声明文件读写和 Git 权限。
- `ProjectEditorViewModel` 协调一个窗口的项目、文档会话与 Git；使用原 `CodeEditorViewModel`、可嵌入的 `CodeEditorView.UseProjectLayout()` 和共享 `CodeEditorSessionDialogs`。独立 Code Editor 仍使用同一编辑面板与文件对话框。
- 普通文件夹直接打开，不强制初始化 Git；仓库打开后可浏览完整文件树。项目树双击/Enter 打开文件；多标签保留独立文本缓冲区、撤销栈、光标和滚动位置。图片、SVG、二进制分类沿用原实现。
- 顶部提供项目/Commit 切换、新建、保存、全部保存、另存为、查找、暂存、拉取、推送；普通目录额外显示显式初始化命令。项目工具区与 Commit 工具区互斥。
- Commit 区分别显示 staged、unstaged、untracked、conflict；index 与 worktree 两项共享同一路径的提交勾选，点击打开各自 Diff，右键提供打开工作树文件、暂存、取消暂存和还原。底部日志、分支、远程和概览可切换与收起，不卸载编辑器。
- 工作树、index 和指定提交的 Diff 以不同身份打开只读标签；切回 Diff 标签重新查询，旧请求不会覆盖用户后来打开的文件或比较。
- 关闭脏标签/窗口与会修改工作树/index 的 Git 操作前，列出文件供保存/放弃/取消。保存失败阻止后续操作；操作期间锁定文本编辑。分支创建的默认 checkout、推送拒绝后的 pull、冲突写入/继续/中止也经过草稿检查。
- 项目编辑器经 `PUT /api/v1.0/files/conditional-content` 保存：已有文件提交读入字节的 SHA-256，新建/另存为使用排他创建；冲突返回 `409 text-file-changed`，保留草稿。正文按字节传输，保留编码与换行。条件编辑上限 256 KiB，超过上限的文本只读；UTF-8 BOM、UTF-16/32 的 BOM 保存规则已补齐。
- 显式刷新、保存和 Git 操作联动状态；干净文本可在刷新时更新，脏文件或外部删除保留缓冲区并提示重新加载/另存副本。服务端不把应用内锁当作对所有外部程序的事务隔离。
- 新界面资源同步中、英、日三语言，Diff 标题和历史筛选可随语言切换；复用现有浅色/深色主题资源。

快捷键：`Ctrl+S` 保存，`Ctrl+Shift+S` 全部保存，`Ctrl+N` 新建，`Ctrl+W` 关闭当前标签，`Ctrl+F` 查找，`Ctrl+Tab` / `Ctrl+Shift+Tab` 切换文档。另存为通过工具栏进入；独立 Code Editor 原快捷键保持其自身语义。

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
- 完整覆盖式窄屏工具窗口与跨会话布局持久化。目前侧栏/底栏可折叠、缩放，尺寸在当前窗口记忆，工具栏换行；这不等于设计中的所有自适应交互均已实现。
- 真正连接 Windows/Ubuntu 服务端的全流程手工验收，包括远程 push/pull 凭据、复杂冲突和高 DPI。既有 Git 操作入口被保留，当前本地测试不能替代这些环境验证。

目标与后续验收继续遵循 [改造设计](./RelaxKonOS.GitEditor.Design.md)。
