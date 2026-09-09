# Git 冲突可视化设计与实现

日期：2026-09-09

## 目标与现状

在现有 Avalonia + MVVM 客户端、JWT HTTP 协议和服务端 Git CLI 架构中，完成从 pull/merge 产生冲突，到查看、编辑、暂存、继续或中止的完整流程。交互参考 JetBrains 的三方合并布局：左侧 ours，中间结果，右侧 theirs，共同基础版本按需展开。

代码审查发现：

- `GitConflictResolutionView` 原本只有文件名列表，无法选择版本或保存。
- `GitConflictFileDto` 未接入文件版本读取；旧 resolve 请求仅支持 paths 与 continueMerge。
- pull/merge/revert 实现将有冲突的非零退出视为成功，与注释及实际完成状态不符。
- 拉取失败分支没有刷新列表，页面可能显示旧状态。
- 继续逻辑直接检查工作目录下 `.git/MERGE_HEAD`，忽略 worktree 的 `.git` 文件；没有 merge 时盲目调用 rebase。
- 冲突路径按空格分割，会破坏空格路径和 Git 引号转义路径。
- 冲突导航仅随未解决文件显示，最后一个文件暂存后无法重新进入继续操作界面。

## 用户流程

1. 拉取、合并、撤销、签出等操作遇到冲突后刷新状态，并以模态窗口打开冲突解决器；重新打开有进行中冲突的仓库也会恢复并打开该窗口。推送因远端已有新提交而被拒绝时，先在弹窗中选择 merge、rebase 或取消；同步产生冲突时继续进入同一解决器。
2. 文件列表选择一个冲突文件；左右显示索引阶段 2/3，中间显示工作区内容，基础版本来自阶段 1。
3. 下方下拉框按结果文本中的行号选择冲突块，使用带颜色的双方预览，采用 ours/theirs/双方。块操作只编辑草稿，不立即暂存；支持 merge、diff3、zdiff3 标记和自定义标记宽度。
4. 用户也可手动编辑结果；保存时拒绝残留的冲突标记。整文件采用一侧或删除均明确确认，并直接暂存。
5. 每个文件独立保存并标记解决。定时刷新不覆盖编辑内容，切换文件保留当前窗口中的草稿；重新载入会确认放弃未保存编辑。
6. 未解决列表清空后，继续按钮可用；服务端再次验证索引无冲突后执行对应操作。rebase 后续提交再次冲突时重新显示列表。
7. 中止操作必须确认；执行 Git 原生 abort，失败保持页面并展示错误。没有待继续操作（如 squash）的冲突仍可解决，但最终提交在工作区进行。

rebase 中 ours 是目标分支及已重放内容，theirs 是正在重放的提交。界面明确说明，避免把它们恒定称为“我的/远程的修改”。

## 接口与状态

统一前缀：`/api/v1.0/git/repositories/{id}`。预发布阶段直接升级协议，不保留旧请求适配。

| 方法与路径 | 请求 | 响应/行为 |
| --- | --- | --- |
| GET `/conflicts` | 无 | operation（merge/rebase/cherry-pick/revert/null）、paths |
| GET `/conflicts/file` | path 查询参数 | path、revision、baseVersion、oursVersion、theirsVersion、result、canEdit |
| POST `/resolve` | path、revision、choice（ours/theirs/edited/delete）、content | 仅保存并暂存该文件；不自动继续 |
| POST `/conflicts/operation` | operation、action（continue/abort） | 校验真实操作类型，执行 Git 子命令；返回新冲突 |

`Success` 表示 Git 操作成功退出；有冲突时为 false，另外返回 Conflicts。单文件不存在的索引阶段用 null 表示，区别于空文件。二进制/过大文件不提供可编辑文本。

操作状态通过 `git rev-parse --git-path` 查询 rebase-merge、rebase-apply、MERGE_HEAD、CHERRY_PICK_HEAD、REVERT_HEAD，适用于普通仓库和 worktree。只有 operation 非空且 paths 为空才允许继续；有 operation 时即使无冲突文件仍保留导航。

## 数据完整性与并发

- 每次请求沿用仓库所属用户验证。所有写操作沿用每仓库信号量。
- 使用 `git diff --name-only --diff-filter=U -z` 读取精确路径；单文件 `ls-files -u -z` 使用 literal pathspec，避免通配符误操作其他文件。
- 写入前验证相对路径、仓库边界、索引仍然冲突；拒绝符号链接路径、子模块和非普通文件模式。
- revision 包含未合并索引记录、工作区字节 SHA-256、HEAD。保存时重读验证，防止覆盖其他窗口或外部 Git 工具已修改的文件；过期时用户需重新载入。
- 手动编辑限制为 200 KiB UTF-8 文本，拒绝 NUL、非法 UTF-8 和未清除的冲突标记。保守标记检查可能拒绝本来就包含整行分隔标记的文本，这类文件可通过外部 Git 工具解决。
- 二进制/大文件用原生 Git checkout 采用整个版本，避免解码再编码；不存在的一侧必须明确选择删除。
- 保存文件后执行 git add -A；若暂存失败，保留写入结果并展示失败，不宣称解决成功。重新载入后可再次保存。
- Git 编辑器设为非交互，避免服务端 merge/rebase continue 等待不可见的编辑器。
- 客户端文件加载带请求代数和仓库校验，防止慢响应覆盖后来选中的文件；保存/继续/中止期间禁用操作按钮。

## 实现文件

- 协议：`Shared/RelaxKonOS.Protocol/Git/GitDtos.cs`、`GitApiRoutes.cs`。
- 服务端：`LocalGitRepositoryService.Conflicts.cs` 独立 partial 实现；原服务修正冲突路径和操作结果，`GitEndpoints.cs` 注册路由。
- 客户端：`GitClientViewModel.Conflicts.cs`、`GitConflictBlock.cs`、`GitConflictResolutionView.axaml`、`GitConflictResolutionDialog.axaml`；RemoteGitClient 连接新接口。冲突文件列表与三栏编辑器由同一个模态窗口承载，可逐文件保存、继续或中止。
- 本地化：zh-CN、en-US、ja-JP 同步新增文案。
- 集成检查：`RelaxKonOS.Server.Tests/GitConflictChecks.cs`，使用真实临时 Git 仓库与 SQLite，不操作开发仓库。

## 验证方式

```powershell
dotnet build Client/RelaxKonOS.Client/RelaxKonOS.Client.csproj --no-restore -p:UsedAvaloniaProducts= -v quiet
dotnet build RelaxKonOS.Server.Tests --no-restore -p:UseAppHost=false -v quiet
dotnet RelaxKonOS.Server.Tests/bin/Debug/net10.0/RelaxKonOS.Server.Tests.dll --git-conflicts-only
```

`UsedAvaloniaProducts=` 仅用于此受限构建环境跳过 Avalonia 构建遥测的用户目录写入，不改变产品构建配置。

实测结果：客户端与服务端测试项目构建均为 0 警告、0 错误；45 项 Git 冲突检查全部通过。

自动检查覆盖：三阶段内容、空格/中文路径、残留标记、过期修订、用户隔离、路径越界、merge/rebase 继续、worktree 中止、二进制原字节、删除冲突、pull 明确策略、squash 无操作状态、cherry-pick/revert 状态、连续 rebase 冲突、diff3 CRLF 与不完整标记。

## 明确边界

本次完成基础三方工作流和冲突块选择，不等同于 JetBrains 完整 diff 引擎：尚无全文件差异对齐、同步滚动、语法高亮或跨会话草稿持久化。未进行运行中的桌面视觉验收；XAML 编译与真实 Git 集成检查分别覆盖界面绑定和服务流程。

符号链接、子模块通过外部工具解决。重命名冲突按 Git 暴露的实际冲突路径处理，不提供专用重命名映射界面。外部 Git 进程不共享应用的信号量，revision 能检测保存前变化，但不能为外部工具提供跨进程事务；操作过程中应避免同时使用多个工具写同一仓库。
