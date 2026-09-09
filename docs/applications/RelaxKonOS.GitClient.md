# RelaxKonOS GitClient 模块设计

> 内置 Git 客户端：参考 TortoiseGit / Git Extensions，工作区式单窗口应用。面向 RelaxKonOS Server 上已存在的 Git 仓库（服务端文件系统中的工作树），提供仓库选择、分支切换、提交、拉取（含冲突解决）、新建/删除分支、提交历史、Revert 等日常版本控制操作。所有 Git 操作经 Server 端 REST API 执行，服务端以宿主 OS 进程身份调用 `git` CLI，复用宿主用户/权限（不另建 ACL，不存储任何凭据/SSH 私钥）。
>
> - 架构原则见 [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md)
> - 项目当前状态见 [`RelaxKonOS.md`](../README.md)（§6 内置应用）
> - 内置应用开发约束见 [`RelaxKonOS.BuiltInApplication.Conventions.md`](../development/RelaxKonOS.BuiltInApplication.Conventions.md)
> - 桌面外壳与窗口管理见 [`RelaxKonOS.Desktop.md`](../desktop/RelaxKonOS.Desktop.md)
> - 登录与身份认证见 [`RelaxKonOS.Authentication.md`](../platform/RelaxKonOS.Authentication.md)（GitClient 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RelaxKonOS.Protocol.md`](../architecture/RelaxKonOS.Protocol.md)（§Git DTO 与路由）
> - 安全设计见 [`RelaxKonOS.Security.md`](../platform/RelaxKonOS.Security.md)（§不存储凭据 / 危险操作确认）
> - 服务端持久化见 [`RelaxKonOS.Storage.md`](../platform/RelaxKonOS.Storage.md)（GitClient 仅注册仓库元数据落库，提交/分支/历史均为 `git` 实时结果）

---

## 1. 定位

GitClient 是 RelaxKonOS 的内置版本控制客户端，参考 TortoiseGit / Git Extensions。

- **架构归属**：§6.2 Remote Service Application —— UI 完全在 Client 本地渲染；仓库状态、提交历史、分支列表与变更结果**真源在 Server 端通过 `git` 实时采集**（不持久化运行时状态，每次请求都是当下快照）。
- **复用宿主 OS 权限**（硬约束）：Server 端 `IGitRepositoryService` 以宿主 OS 进程身份调用 `git` CLI，复用宿主用户/权限，不另建 ACL。push/pull 需要 SSH 凭据或 HTTPS 凭据时，**RelaxKonOS 不存储、不代理、不收集**——直接由宿主 OS 的 `git` 凭据助手（credential helper / SSH agent）处理；若凭据缺失则 `git` 返回失败，客户端展示本地化提示，引导用户在宿主 OS 配置。
- **不存储任何凭据/私钥**：认证委托宿主 OS 的 git 凭据体系，GitClient 仅消费 `IAuthSession.Tokens.AccessToken`（RelaxKonOS 自身登录），不接触 Git 远程仓库的凭据。
- **跨平台抽象**：与 `IIdentityProvider` / `ISystemMetricsProvider` 同模式——`IGitRepositoryService` 接口 + 平台无关的 `LocalGitRepositoryService`（`git` CLI 在 Windows/Linux 行为一致），仅在探测 `git` 可执行文件路径与 shell 启动环境时按宿主 OS 处理。平台差异封装在服务之后，Server 端单一代码库跨 Ubuntu + Windows Server。

**核心功能（MVP 范围）**：

| 功能 | 能力 | 数据源 |
|------|------|--------|
| 仓库选择 | 已注册仓库列表 + 当前选中仓库 + 切换 | `GET /api/v1.0/git/repositories` |
| 工作区状态 | 已暂存/未暂存/未跟踪/冲突文件清单 + 当前分支 + upstream 落后/领先计数 | `GET /api/v1.0/git/repositories/{id}/status` |
| 分支管理 | 本地+远程分支列表、切换(checkout)、新建、删除 | `GET/POST/DELETE /api/v1.0/git/repositories/{id}/branches` |
| 提交 | 暂存选择文件 + 提交消息 + 提交 | `POST /api/v1.0/git/repositories/{id}/commit` |
| 拉取 | 当前分支 fetch+merge/fetch+rebase；未检出分支可仅安全快进更新 | `POST /api/v1.0/git/repositories/{id}/pull` |
| 推送 | 推送当前或指定本地分支到指定 remote/分支，无需 checkout | `POST /api/v1.0/git/repositories/{id}/push` |
| 提交历史 | log 列表（hash/作者/时间/消息）+ 单提交详情 | `GET /api/v1.0/git/repositories/{id}/log` |
| Revert | 反向提交指定 commit | `POST /api/v1.0/git/repositories/{id}/revert` |
| Diff | 单文件 diff（工作区/已暂存/某提交） | `GET /api/v1.0/git/repositories/{id}/diff` |
| 冲突解决 | 三栏编辑、逐块选择、逐文件暂存与独立继续/中止 | `POST /api/v1.0/git/repositories/{id}/resolve` |

**非目标（MVP 不含）**：cherry-pick、rebase 交互式编辑、submodule 深度管理、stash、tag 管理、内置 diff/merge 三方编辑器（冲突解决调用宿主 CodeEditor 或外联）、PR 工作流、多远程管理。这些列入 §8 后续演进。

---

## 2. 嵌入方式

`GitClientWorkspace` 作为 `UserControl` 嵌入 `RemoteWindow`，与 DockerManager / TaskManager 同构：

```text
GitClientApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("Git 客户端", view, bounds=1180x760)
    |
    WindowManager.Create → RemoteWindow
    |
    GitClientWorkspace (UserControl)
        ├── 顶部标题栏（图标 + 仓库选择器 + 当前分支 + upstream 状态 + 刷新）
        └── 左侧导航 + 右侧内容区（按 ActivePage 切换：概览/工作区/日志/冲突解决/远程）
```

`GitClientApp.Activate` 注入 `IAuthSession` + `IRemoteGitClient`（从 `context.Services`）。未登录时弹 `TextBlock` 提示窗（470x180，不可缩放），不崩溃；登录则构造 `GitClientViewModel` + `GitClientWorkspace`，`context.ShowWindow`（bounds 1180x760）后 `_ = viewModel.StartAsync()` 异步拉取仓库列表与首仓状态。

---

## 3. 协议契约（`Shared/RelaxKonOS.Protocol/Git/`）

沿用 Protocol 约定（`sealed record` + `[property: JsonPropertyName]`，零 PackageReference）。

### 3.1 路由（`GitApiRoutes.cs`）

路径含 `/api/v1.0` 前缀，Server 注册路由与 Client 拼接 URL 共用：

```text
Repositories       = /api/v1.0/git/repositories                       (GET, POST)   # 列表 / 注册
RepositoryById     = /api/v1.0/git/repositories/{id}                  (GET, DELETE) # 详情 / 注销
Status             = /api/v1.0/git/repositories/{id}/status            (GET)         # 工作区状态
Branches           = /api/v1.0/git/repositories/{id}/branches          (GET, POST)   # 列表 / 新建
BranchByName       = /api/v1.0/git/repositories/{id}/branches/{name}  (DELETE)      # 删除
Checkout           = /api/v1.0/git/repositories/{id}/checkout          (POST)        # 切换分支
Commit             = /api/v1.0/git/repositories/{id}/commit            (POST)        # 提交
Pull               = /api/v1.0/git/repositories/{id}/pull              (POST)        # 拉取
Push               = /api/v1.0/git/repositories/{id}/push              (POST)        # 推送
Log                = /api/v1.0/git/repositories/{id}/log              (GET)         # 历史
Diff               = /api/v1.0/git/repositories/{id}/diff             (GET)         # 文件 diff
Revert             = /api/v1.0/git/repositories/{id}/revert            (POST)        # 反向提交
Conflicts          = /api/v1.0/git/repositories/{id}/conflicts         (GET)         # 操作状态和冲突路径
Conflict           = /api/v1.0/git/repositories/{id}/conflicts/file    (GET)         # ?path= 单文件版本
ConflictOperation  = /api/v1.0/git/repositories/{id}/conflicts/operation (POST)       # continue / abort
Resolve            = /api/v1.0/git/repositories/{id}/resolve           (POST)        # 标记冲突已解决
Fetch              = /api/v1.0/git/repositories/{id}/fetch             (POST)        # 仅抓取
```

### 3.2 DTO

| DTO | 字段 | 说明 |
|-----|------|------|
| `GitRepositoryDto` | Id / Name / Path / CurrentBranch? / DefaultBranch? / HeadAhead / HeadBehind / HasUpstream / UncommittedCount | 注册仓库元数据 + 仓库级摘要（列表项） |
| `GitRepositoryDetailDto` | Id / Name / Path / CurrentBranch / UpstreamBranch? / RemoteUrl? / IsDetached / IsClean / AheadCount / BehindCount | 仓库详情 |
| `GitStatusDto` | Branch / Upstream? / Ahead / Behind / Staged / Unstaged / Untracked / Conflicts | 工作区状态聚合 |
| `GitFileChangeDto` | Path / OldPath? / Status (Modified/Added/Deleted/Renamed/Copied/Untracked/Conflicted) / Staged (bool) | 单文件变更项 |
| `GitBranchDto` | Name / IsRemote (bool) / IsCurrent (bool) / IsDefault (bool) / Tracking? / Ahead / Behind | 分支项 |
| `GitCommitDto` | Sha / ShortSha / Author / AuthorEmail / AuthorDate / Committer? / Subject / Body? | 提交项 |
| `GitCommitDetailDto` | Sha / Author / Date / Subject / Body / Parents / ChangedFiles | 单提交详情 |
| `GitDiffDto` | Path / OldPath? / Patch / Additions / Deletions / Binary (bool) | 单文件 diff 文本与统计 |
| `GitOperationResult` | Success / Operation / Conflicts (list of path) / Message? / RequiresCredentials (bool) | 操作结果（pull/push/merge/revert 通用） |
| `GitConflictFileDto` | Path / Status / OursVersion? / TheirsVersion? | 冲突文件项 |
| `GitRepositoryRegistration` | Name / Path | 注册仓库请求体 |
| `GitCommitRequest` | Message / Paths (要暂存的文件) / Amend (bool) | 提交请求 |
| `GitBranchCreateRequest` | Name / StartPoint? / Track (bool) | 新建分支 |
| `GitPullRequest` | Strategy (Merge/Rebase) / Remote? / Refspec? / Branch? | 拉取策略；`Branch` 指定未检出的本地分支时，服务端 fetch 后仅允许快进该 ref |
| `GitPushRequest` | Credentials? / LocalBranch? / Remote? / RemoteBranch? | 推送选项；显式 refspec 支持在 HEAD 位于 a 时推送 b |
| `GitCheckoutRequest` | Branch / CreateIfMissing (bool) | 切换请求 |
| `GitRevertRequest` | Sha / NoCommit (bool) | 反向提交请求 |
| `GitResolveRequest` | Paths / ContinueMerge (bool) | 标记冲突已解决 |

`Status` 枚举用稳定字符串（与 Firewall/Process 的 `Status` 同风格），不传本地化文案。

---

## 4. 服务端

### 4.1 跨平台抽象（`RelaxKonOS.Server.Git/`）

与 `IIdentityProvider` / `ISystemMetricsProvider` 同模式——接口 + 单一 CLI 实现（`git` 在 Windows/Linux 行为一致，平台差异仅在可执行文件探测与启动环境）：

```text
IGitRepositoryService (接口)
    │
    └── LocalGitRepositoryService (Singleton, 依赖 IHostGitCli)
          ├── 仓库注册表：从 RelaxKonOSDbContext 读写 GitRepository(Name, Path) 记录
          ├── 所有 git 操作：捕获 stdout/stderr + exit code → GitOperationResult
          ├── 路径校验：Path.IsPathRooted + 白名单根（注册时记的 Path，禁止越权到仓库外）
          └── 凭据：完全不介入——由宿主 git 凭据助手处理
```

`IHostGitCli` 仅负责解析 `git` 可执行路径（Linux 用 `which git` / 常见路径 `/usr/bin/git`；Windows 用 `where git` / PATH 探测），**不**硬编码盘符或注册表。命令参数以**结构化数组**传递（`ArgumentBuilder`），不拼接 shell 字符串（符合约束 §4 路径与命令规则）。

**注册**（`Program.cs`）：

```csharp
builder.Services.AddSingleton<IHostGitCli, HostGitCli>();
builder.Services.AddSingleton<IGitRepositoryService, LocalGitRepositoryService>();
```

**Singleton 的理由**：Provider 不持有差分状态（与 SystemMetricsProvider 不同），但持有 `git` 进程并发协调信号量（同一仓库的写操作串行化，避免 index.lock 冲突），Singleton 保证跨请求的信号量一致性。

### 4.2 `git` 调用与 porcelain 输出

所有读取用 `git` 的 machine-readable porcelain 格式，不解析人类文案：

| 操作 | git 命令（结构化参数） | 解析 |
|------|------------------------|------|
| status | `git status --porcelain=v2 --branch` | 解析 `# branch.head` / `# branch.ab` / `1/2 <xy> ... <path>` |
| 分支列表 | `git for-each-ref --format='%(refname:short)\t%(upstream:short)\t%(upstream:track)' refs/heads refs/remotes` | 按制表符切分 |
| log | `git log --pretty=format:'%H%x00%h%x00%an%x00%ae%x00%aI%x00%s%x00%b%x00' --date=iso-strict -n 200` | 按 NUL 切分 |
| diff | `git diff --no-color [--cached] [<ref>] -- <path>` | 原始 patch 文本 + `--stat` 计数 |
| 当前分支 | `git rev-parse --abbrev-ref HEAD` / detached 用 `git rev-parse --short HEAD` | |
| ahead/behind | `git rev-list --left-right --count <upstream>...HEAD` | `left\tRight` |

**写操作**（commit/checkout/pull/push/revert/merge）：捕获 exit code + stderr + 是否进入冲突状态（`git status --porcelain=v2` 含 `u` 行 = 冲突）。

**并发与 index.lock**：同一仓库的写操作用 `SemaphoreSlim(1,1)`（按 repoId 缓存）串行化；读操作不串行（git 读不持锁）。3 秒 timeout 获取信号量失败返回 `Success=false, Message="仓库繁忙"`。

**凭据**：push/pull 调用 `git push` / `git fetch`，凭据由宿主 git credential helper（如 `manager` / `store` / `cache`）或 SSH agent 处理。若 `git` 因凭据缺失返回非零，`GitOperationResult.RequiresCredentials=true`，客户端展示本地化提示「需在宿主 OS 配置 Git 凭据」，**不**重试、**不**收集密码。

### 4.3 危险操作预检

| 操作 | 预检 | 确认 |
|------|------|------|
| 删除分支 | 检查是否当前分支（拒绝）/ 是否未合并（`git branch --merged` 返回不含该分支则告警） | 未合并分支删除前客户端二次确认 |
| 强制 push | MVP 不暴露 `--force` | 不支持 |
| checkout | 工作区有未提交变更且会覆盖时，`git checkout` 失败 → 返回冲突文件列表 | 客户端提示先提交或 stash（stash 见 §8） |
| revert | 若会产生冲突，返回冲突文件，不自动提交 | 客户端提示冲突需手动解决 |
| 删除仓库注册 | 仅删除注册记录，**不**删除磁盘仓库目录 | 客户端二次确认 |

**审计**：所有写操作记录操作者、时间、仓库、动作、结果（不记录 commit message 之外的文件内容、不记录凭据）。

### 4.4 REST 端点（`RelaxKonOS.Server.Endpoints/GitEndpoints.cs`）

全 `RequireAuthorization()` + `server.git.read` / `server.git.manage` 权限校验，错误统一 RFC 7807（与 Docker/Firewall 端点同风格）：

| Method | Route | 用途 |
|--------|-------|------|
| GET | `/api/v1.0/git/repositories` | 已注册仓库列表 |
| POST | `/api/v1.0/git/repositories` | 注册新仓库（校验路径存在 + 是 git 仓库） |
| GET | `/api/v1.0/git/repositories/{id}` | 仓库详情（含当前分支/upstream/ahead-behind） |
| DELETE | `/api/v1.0/git/repositories/{id}` | 注销仓库注册（不删目录） |
| GET | `/api/v1.0/git/repositories/{id}/status` | 工作区状态 |
| GET | `/api/v1.0/git/repositories/{id}/branches` | 分支列表（local+remote） |
| POST | `/api/v1.0/git/repositories/{id}/branches` | 新建分支 |
| DELETE | `/api/v1.0/git/repositories/{id}/branches/{name}` | 删除分支 |
| POST | `/api/v1.0/git/repositories/{id}/checkout` | 切换分支 |
| POST | `/api/v1.0/git/repositories/{id}/commit` | 提交 |
| POST | `/api/v1.0/git/repositories/{id}/fetch` | 仅抓取 |
| POST | `/api/v1.0/git/repositories/{id}/pull` | 拉取（merge/rebase） |
| POST | `/api/v1.0/git/repositories/{id}/push` | 推送 |
| GET | `/api/v1.0/git/repositories/{id}/log` | 提交历史（query: limit, skip） |
| GET | `/api/v1.0/git/repositories/{id}/diff` | 文件 diff（query: path, staged, ref） |
| POST | `/api/v1.0/git/repositories/{id}/revert` | 反向提交 |
| POST | `/api/v1.0/git/repositories/{id}/resolve` | 保存选定版本/编辑结果并暂存 |
| POST | `/api/v1.0/git/repositories/{id}/stage` | 仅暂存指定文件 |
| POST | `/api/v1.0/git/repositories/{id}/unstage` | 仅取消暂存指定文件 |
| POST | `/api/v1.0/git/repositories/{id}/restore` | 从 HEAD（或指定提交）还原指定文件 |
| POST | `/api/v1.0/git/repositories/{id}/reset` | 安全重置到提交（仅 `--soft` / `--mixed`） |

`Program.cs` 注册：`app.MapGitEndpoints()`。

> **持久化范围**：仅 `GitRepository` 注册记录落 SQLite（Id/Name/Path + 所属 Workspace），提交/分支/历史/diff 均为 `git` 实时结果，不落库、不缓存。

---

## 5. 客户端

### 5.1 客户端 HTTP（`IRemoteGitClient` / `RemoteGitClient`）

- typed HttpClient（`Bootstrapper` 注册 `services.AddHttpClient<IRemoteGitClient, RemoteGitClient>()` + NetworkDiagnosticsHandler + AcceptLanguageHandler）。
- **不 mutate `HttpClient.BaseAddress`**（避免共享实例并发竞态），每请求用 `IAuthSession.ServerUrl` 构造绝对 URI。
- `Authorization: Bearer {AccessToken}` 从 `IAuthSession.Tokens` 取；未登录抛 `InvalidOperationException`。
- 路由常量共用 `GitApiRoutes`，`{id}`/`{name}` 用 `Uri.EscapeDataString` 替换，禁止硬编码字符串。
- 失败读 `ProblemDetails` 抛 `RelaxKonOSAuthException`（与 `TaskManagerClient` / `DockerClient` 同源模式）。
- JSON 用 `RelaxKonOSJsonOptions.Default`。

### 5.2 应用入口（`GitClientApp`）

`RemoteApplicationBase`：`Manifest`（Id=`relaxkonos.git`，Icon=`🌿`）+ `Activate(AppContext)`。

```csharp
public sealed class GitClientApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.git"), "Git Client", "0.1.0", "🌿",
        "Manage Git repositories on the RelaxKonOS Server",
        [AppPermissions.ServerGitRead, AppPermissions.ServerGitManage],
        ServerRequirements: new ApplicationServerRequirements(
            Capabilities: [ServerCapabilities.Git]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    // ...
}
```

- 未登录：弹 `TextBlock` 提示窗（470x180，`canResize/canMinimize/canMaximize=false`），不崩溃。
- 登录：构造 `GitClientViewModel(client, session, context.Permissions)` + `GitClientWorkspace`，`context.ShowWindow`（bounds 1180x760），注入对话框回调（提交对话框/新建分支对话框/冲突解决对话框/确认对话框），`_ = viewModel.StartAsync()` 异步加载仓库列表。

### 5.3 ViewModel（`GitClientViewModel`）

`CommunityToolkit.Mvvm`（`[ObservableProperty]` + `[RelayCommand]`）。

**刷新机制**：

- `DispatcherTimer`（10s 间隔，低于 TaskManager 的 2s——Git 操作不频繁且写操作串行）触发 `RefreshStatusAsync`。
- `StartAsync()`：加载仓库列表 → 选首个仓库 → `RefreshAllAsync`（status + branches + log）+ 启动定时器（若 `IsAutoRefresh`）。
- `Stop()`：停止定时器（View `Unloaded` 时调用）。
- **重入保护**：`Interlocked.CompareExchange(ref _refreshing, 1, 0)`——上一次刷新未完成时跳过本次 tick。

**状态结构**：

```text
GitClientViewModel
    ├── Repositories: ObservableCollection<GitRepositoryDto>   # 仓库列表（左上选择器）
    ├── SelectedRepository: GitRepositoryDto?                  # 当前仓库
    ├── ActivePage: GitClientPage (Overview/Workspace/Log/ConflictResolution/Remotes)
    ├── Status: GitStatusDto?                                  # 工作区状态
    │     ├── StagedFiles / UnstagedFiles / UntrackedFiles / ConflictFiles
    │     └── Branch / Upstream / Ahead / Behind
    ├── Branches: ObservableCollection<GitBranchDto>            # 分支列表
    ├── Commits: ObservableCollection<GitCommitDto>           # 历史列表
    ├── SelectedCommit: GitCommitDto?                          # 选中提交
    ├── CommitDetail: GitCommitDetailDto?                      # 选中提交详情（变更文件列表+parents+作者等）
    ├── CommitChangedFiles: ObservableCollection<GitFileChangeDto>  # 选中提交的变更文件树
    ├── SelectedFile: GitFileChangeDto?                        # 选中文件
    ├── FileDiff: GitDiffDto?                                  # 当前文件 diff
    ├── StatusText: string                                     # 状态栏文案
    └── IsBusy: bool                                           # 操作进行中
```

**命令映射**：

| 命令 | 动作 | CanExecute |
|------|------|-----------|
| `RefreshCommand` | `RefreshAllAsync` | SelectedRepository != null && !IsBusy |
| `SwitchRepositoryCommand` | 切换仓库 + 重新加载全部 | Repositories 非空 |
| `CheckoutCommand` | `POST checkout` (branch) | SelectedRepository != null && !IsBusy |
| `CreateBranchCommand` | 弹新建分支对话框 → `POST branches` | SelectedRepository != null && !IsBusy |
| `DeleteBranchCommand` | 二次确认 → `DELETE branches/{name}` | SelectedBranch != null && !IsCurrent |
| `CommitCommand` | 弹提交对话框 → `POST commit` | SelectedRepository != null && StagedFiles 非空 && !IsBusy |
| `PullCommand` | `POST pull` (Merge/Rebase 选择) → 冲突则进冲突视图 | HasUpstream && !IsBusy |
| `PushCommand` | `POST push` → RequiresCredentials 则提示 | HasUpstream && !IsBusy |
| `FetchCommand` | `POST fetch` | SelectedRepository != null && !IsBusy |
| `RevertCommand` | 二次确认 → `POST revert` → 冲突则进冲突视图 | SelectedCommit != null && !IsBusy |
| `StageCommand`/`UnstageCommand` | `POST stage` / `POST unstage` 暂存调整 | SelectedFile != null |
| `ViewDiffCommand` | `GET diff` | SelectedFile != null |
| `ResolveConflictCommand` | 采用整个 ours/theirs 或删除文件 → `POST resolve` | 已加载冲突且空闲 |
| `RegisterRepositoryCommand` | 弹注册对话框 → `POST repositories` | server.git.manage 权限 |

**冲突解决流程**：`pull`/`merge`/`revert` 返回 `Conflicts` 非空时刷新并进入冲突页面。通过 `GET conflicts` 读取真实操作类型和精确路径，`GET conflicts/file?path=…` 读取 base/ours/theirs/工作区结果。三栏编辑器支持逐冲突块采用 ours/theirs/双方、整文件采用、删除以及手动编辑。`POST resolve` 使用 `{ path, revision, choice, content }` 保存并暂存单文件；全部解决后独立调用 `POST conflicts/operation`，请求 `{ operation, action: "continue" }`。中止使用 `action: "abort"` 并要求界面确认。无操作状态（如 squash）时解决后在工作区提交，不执行 rebase。详见 [冲突可视化设计](RelaxKonOS.GitClient.ConflictResolution.md)。

### 5.4 视图（`GitClientWorkspace.axaml`）

模拟 Windows 风格（用户偏好），与 DockerManagerWorkspace 同构的三段式布局：

- **顶部标题栏**（`#122344` 深色）：左侧 🌿 图标 + 应用名 + 副标题；右侧仓库选择 ComboBox + 当前分支 Badge + upstream 落后/领先指示 + 「⟳ 刷新」按钮。
- **左侧导航**（190px，`#EEF3FA` 浅色）：概览 / 工作区 / 日志 / 冲突解决 / 远程 五个导航按钮（`NavigationButton_Click` 切换 `ActivePage`，高亮激活页）。
- **右侧内容区**（`ScrollViewer` + `ContentControl`，按 `ActivePage` 切换子视图）：
  - **概览页**：仓库卡片（名称/路径/当前分支/upstream/远程 URL）+ 状态摘要卡片（已暂存/未暂存/未跟踪/冲突计数 + ahead/behind）+ 快捷操作按钮（拉取/推送/提交/新建分支）。
  - **工作区页**：双栏文件列表（左：未暂存/未跟踪；右：已暂存），中间「暂存»/«取消暂存」按钮，底部提交消息输入框 + 提交按钮；选中文件显示 diff（只读 patch）。
  - **日志页**（原分支+历史合并，三栏统一视图）：
    - 左栏（220px）：分支/标签树（HEAD、本地分支分组、远程分支分组），支持右键菜单：新建分支、切换、重命名、删除、推送、拉取、更新、设置跟踪分支、显示与工作树差异、复制分支名。
    - 中栏：提交列表（带时间图元、hash、作者、时间、消息、分支标签），支持搜索过滤（文本/哈希、用户、日期、路径），右键菜单：复制修订号（SHA）、创建补丁、签出修订、在修订版中显示仓库、与本地比较、将当前分支重置到此处、还原提交、撤销提交、编辑提交消息、Fixup、压缩到、删除提交、压缩提交、交互式变基、推送此前所有提交、新建分支、新建标记。
    - 右栏（360px）：上半部分为提交变更文件树（按文件夹分组，显示状态图标），右键菜单：显示差异、在新标签页中显示差异、与本地比较、将之前版本与本地版本进行比较、编辑源、打开仓库版本、还原所选更改、优选所选更改、将所选更改提取到单独的提交、删除所选更改、创建补丁、从修订中获取、迄今为止的历史记录、显示对父项的更改；下半部分为提交详情（SHA、作者、日期、消息正文、所在分支标签）。
  - **冲突解决页**（冲突时显示）：冲突文件列表 + 每文件 ours/theirs 选择 + 继续/中止合并按钮。
  - **远程页**：多远程仓库管理（添加、修改、删除 remote，配置 push/pull URL）。

`Border.card` 样式选择器统一卡片外观（与 DockerManagerWorkspace 同风格）。

### 5.5 对话框（`GitClientDialogs.cs`）

复用 `AppContext.ShowDialogAsync` 模式（与 `DockerManagerDialogs` 同构）：

| 对话框 | 用途 | 返回 |
|--------|------|------|
| `ShowCommitDialogAsync` | 提交消息输入 + 暂存文件勾选 + amend 选项 | `GitCommitRequest?` |
| `ShowCreateBranchDialogAsync` | 分支名 + 起点 + 是否 track | `GitBranchCreateRequest?` |
| `ShowPullDialogAsync` | merge/rebase 策略选择 | `GitPullRequest?` |
| `ShowResolveConflictDialogAsync` | 冲突文件 + ours/theirs 选择 | `GitResolveRequest?` |
| `ShowRegisterRepositoryDialogAsync` | 仓库名 + 路径 | `GitRepositoryRegistration?` |
| `ShowConfirmAsync` | 危险操作二次确认 | `bool` |

---

## 6. 数据流

### 6.1 启动与刷新流

```text
StartAsync
    ├── GET /api/v1.0/git/repositories (JWT) → Repositories
    ├── SelectedRepository = Repositories.FirstOrDefault()
    └── RefreshAllAsync (Interlocked 重入保护)
          ├── GET status      → Status (StagedFiles/UnstagedFiles/UntrackedFiles/ConflictFiles)
          ├── GET branches    → Branches
          └── GET log         → Commits

DispatcherTimer (10s tick) → RefreshStatusAsync (仅 status，轻量)
```

### 6.2 提交流

```text
用户点「提交」
    ↓
ShowCommitDialogAsync → GitCommitRequest(message, paths, amend)
    ↓
POST /api/v1.0/git/repositories/{id}/commit (JWT)
    ↓
GitOperationResult
    ├── Success=true      → StatusText="已提交"；RefreshStatusAsync
    └── Success=false     → StatusText="提交失败：{Message}"
```

### 6.3 拉取与冲突解决流

```text
用户点「拉取」
    ↓
ShowPullDialogAsync → GitPullRequest(strategy)
    ↓
POST /api/v1.0/git/repositories/{id}/pull (JWT)
    ↓
GitOperationResult
    ├── Success=true                              → StatusText="已拉取"；RefreshAllAsync
    ├── Success=false && Conflicts 非空            → ActivePage=ConflictResolution
    │     ├── 列出冲突文件（每个 ours/theirs）
    │     ├── 用户逐文件选择 → ShowResolveConflictDialogAsync
    │     └── POST resolve(path, revision, choice, content) → POST conflicts/operation(operation, action)
    │           ├── Success → RefreshAllAsync
    │           └── 仍有冲突 → 继续显示冲突视图
    └── RequiresCredentials=true → StatusText="需在宿主 OS 配置 Git 凭据"
```

### 6.4 分支切换流

```text
用户在分支页选中分支 → 点「切换」
    ↓
POST /api/v1.0/git/repositories/{id}/checkout (JWT, branch)
    ↓
GitOperationResult
    ├── Success=true            → StatusText="已切换到 {branch}"；RefreshAllAsync
    └── Success=false && Conflicts → 刷新并进入冲突解决页面
```

### 6.5 Revert 流

```text
用户在历史页选中提交 → 点「Revert」
    ↓
ShowConfirmAsync("将创建反向提交，撤销 {shortSha} 的变更？")
    ↓
POST /api/v1.0/git/repositories/{id}/revert (JWT, sha)
    ↓
GitOperationResult
    ├── Success=true                → StatusText="已 Revert"；RefreshAllAsync
    └── Conflicts 非空              → ActivePage=ConflictResolution（同 §6.3）
```

---

## 7. 关键技术坑

1. **不存储 Git 凭据**：push/pull 的 SSH 私钥 / HTTPS 用户名密码完全由宿主 OS 的 git 凭据体系处理。RelaxKonOS 不代理、不收集、不存储。凭据缺失时 `RequiresCredentials=true`，引导用户在宿主 OS 配置（如 `git config --global credential.helper store` 或 SSH key）。符合约束 §5.2「不存储宿主 OS 密码/sudo 密码/私钥/明文 secret」。
2. **index.lock 并发**：同一仓库的写操作（commit/checkout/pull/push/revert/merge）用按 repoId 缓存的 `SemaphoreSlim(1,1)` 串行化，避免 `git` 因 `.git/index.lock` 失败。读操作（status/branches/log/diff）不串行。3 秒获取信号量超时返回「仓库繁忙」。
3. **porcelain 解析而非人类文案**：所有 `git` 输出用 `--porcelain=v2` / `--pretty=format` / `--for-each-ref --format` 等机器可读格式，按 NUL/制表符切分，不解析人类文案（避免本地化 git 输出导致解析失败）。
4. **路径越权防护**：所有 `git` 命令的 `cwd` 设为注册时记录的仓库 `Path`；diff/commit 的 `<path>` 参数必须 `Path.IsPathRooted` 后判断是否在仓库根下（`Path.GetRelativePath` 不抛异常即合法），禁止 `../` 越权到仓库外。
5. **危险操作确认**：删除未合并分支、revert、checkout 覆盖未提交变更、删除仓库注册均需二次确认（`ShowConfirmAsync`）；MVP 不暴露 `git push --force` / `reset --hard`。
6. **冲突状态机**：`pull`/`revert` 可能进入冲突状态（`git status --porcelain=v2` 含 `u` 行）。冲突时强制切到冲突解决页，未解决完不允许其他写操作（`IsBusy` 或 `HasConflicts` 门禁）。
7. **DispatcherTimer 生命周期**：View `Unloaded`（窗口关闭/卸载）时必须调 `viewModel.Stop()` 停止定时器（`DispatcherTimer` 不会随视图卸载自动停止）。`RefreshStatusAsync` 用 `Interlocked` 重入保护防止请求堆积。
8. **Singleton Provider 持信号量**：`IGitRepositoryService` 为 Singleton——持有按 repoId 缓存的 `SemaphoreSlim` 字典，跨请求的写操作串行化靠单例保证。禁止 Scoped/Transient。
9. **detached HEAD**：`git status --porcelain=v2 --branch` 的 `# branch.head` 为 `(detached)` 时，`IsDetached=true`，客户端禁用分支操作并提示「当前处于 detached HEAD，请先 checkout 分支」。
10. **注册仓库校验**：`POST repositories` 时服务端校验 `Path` 存在 + `.git` 存在（`git -C {path} rev-parse --is-inside-work-tree`），失败返回 RFC 7807 `problem`。注册记录落 SQLite（Workspace 隔离）。
11. **不持久化运行时状态**：提交/分支/历史/diff 均为 `git` 实时结果，不落库、不缓存。仅 `GitRepository` 注册记录持久化。Server 重启后下次请求重新从 `git` 读取。
12. **diff 安全边界**：diff 文本可能很大，`GitDiffDto.Patch` 服务端截断（默认 200KB，超出则 `Truncated=true` + 仅返回 `--stat`），客户端只读展示，不执行 patch。二进制文件 `Binary=true`，不返回 patch。
13. **编译验证**：`dotnet build RelaxKonOS.sln -c Debug` 必须 0 错误。

---

## 8. 后续演进

> 标注 [右键菜单已占位] 的项：客户端日志页三栏（分支树/提交列表/变更文件树）已绑定对应命令，但其中高级 Git 子系统尚未具备服务端 API；点击会在状态栏说明当前不可用。

### 8.1 分支树右键菜单（左栏）

- **[右键菜单已占位] 重命名分支**：`git branch -m <old> <new>`。需加 `POST /repositories/{id}/branches/{name}/rename`，校验新分支名不冲突 + 当前分支允许重命名。
- **[右键菜单已占位] 合并分支**：`git merge <source>`。需加 `POST /repositories/{id}/merge`（源分支名 + fast-forward/--no-ff/--squash 策略）。冲突时沿用 `GitOperationResult.Conflicts` 通道切到冲突解决页（与 pull/revert 同构）。
- **[右键菜单已占位] 变基分支**：`git rebase <upstream>`。需加 `POST /repositories/{id}/rebase`（交互式/非交互式，abort/continue/skip 子动作）。交互式 rebase 后续补编辑器。
- **[右键菜单已占位] 设置跟踪分支**：`git branch -u <upstream> <branch>`。需加 `PUT /repositories/{id}/branches/{name}/tracking`，允许解绑（track=null）。
- **[右键菜单已占位] 显示与工作树差异**：基于现有 diff 接口 `GET /diff?ref=<branch>`，新增「工作树 vs 指定分支」比较，当前仅支持工作区 vs HEAD / staged。

### 8.2 提交列表右键菜单（中栏）

- **[右键菜单已占位] 创建补丁**：`git format-patch -1 <sha>`。需加 `GET /repositories/{id}/patch?sha=` 返回 `.patch` 文件文本或 zip。
- **[右键菜单已占位] 签出修订（detached）**：`git checkout <sha>`。复用现有 checkout 接口，传入分支名=SHA 且 `CreateIfMissing=false`，进入 detached HEAD 状态；UI 需同步禁用分支写操作。
- **重置 / 撤销提交**：已支持 `POST /repositories/{id}/reset` 的 `--soft` 与 `--mixed`；客户端「重置到此处」使用 mixed，「撤销提交」仅允许当前 HEAD 并使用 soft，保留暂存内容。`--hard` 仍不暴露。
- **[右键菜单已占位] 编辑提交消息**：`git commit --amend -m <newMsg>`，当前 `POST /commit` 已有 `Amend` 参数，需校验是 HEAD 提交（无未暂存变更或允许 amend+暂存）。
- **[右键菜单已占位] Fixup / 压缩到 / 删除提交 / 压缩提交**：均基于交互式 rebase（`git rebase -i <sha>^`），后续与 8.1 的变基一起补齐；动作分别对应 fixup/squash/drop/squash 行。
- **[右键菜单已占位] 交互式变基**：`git rebase -i <startPoint>`，需返回 todo 列表供客户端编辑，再 `POST /rebase/continue` 提交 todo 文本。
- **[右键菜单已占位] 推送此前所有提交**：`git push <remote> <sha>:<branch>`，当前 push 接口仅支持当前分支 → upstream，需扩展指定源/目标 refspec。
- **[右键菜单已占位] 新建标记（tag）**：`git tag <name> <sha>`。需加 `GET/POST/DELETE /repositories/{id}/tags`（含 lightweight/annotated 两种，后者支持 message）。与 §8.3 同项。
- **cherry-pick / rebase -i**：当前 revert 之外的反向操作。后续加交互式 rebase 编辑器（与 Fixup/压缩同属 rebase 子系统）。
- **tag 管理**：当前未含。后续加 tag 列表/新建/删除，同时在日志页左栏分支树增加「标签」分组展示。
- **历史搜索 / 责怪 (blame)**：当前仅 log + 搜索过滤。后续加 `git log -S`（按 diff 内容搜索）/ `git blame <path>`（逐行最后修改人+SHA），右栏文件右键菜单补充「显示 Blame」。

### 8.3 变更文件树右键菜单（右栏）

- **[右键菜单已占位] 在新标签页中显示差异**：纯 UI 增强（与主窗口同窗口内开新 Tab），当前 diff 弹窗/侧栏模式；需扩展 Shell Tab 容器或复用 CodeEditor 打开 patch。
- **[右键菜单已占位] 将之前版本与本地版本进行比较（三元 diff）**：当前 diff 仅支持两两比较；三元 diff（父提交 vs HEAD vs 工作区）后续调用 CodeEditor 做三方合并视图。
- **[右键菜单已占位] 编辑源 / 打开仓库版本**：复用 CodeEditor / 已存文件管理器，仓库版本（旧内容）需先写入临时文件再打开，临时文件生命周期由 Workspace 缓存。
- **还原所选更改**：已支持 `POST /repositories/{id}/restore`；默认从 HEAD 还原工作树，也可通过 `source` 指定提交。所有路径均在服务端校验必须位于仓库根目录。
- **[右键菜单已占位] 优选所选更改 / 将所选更改提取到单独的提交**：交互性提交重排，基于 `git add -p` / interactive add，后续与 reset --soft + recommit 组合实现；当前提交接口 `Paths` 字段仅支持全文件暂存。
- **[右键菜单已占位] 删除所选更改**：等价于还原 + 工作区删除，需二次确认（工作区文件将被覆盖/删除）。
- **[右键菜单已占位] 从修订中获取**：`git show <sha>:<path>` 将指定提交版本的文件内容提取到工作区（可选另存路径），与「打开仓库版本」同用 show 命令。
- **[右键菜单已占位] 迄今为止的历史记录**：`git log -- <path>`，当前中栏仅全仓库 log，需增加按路径过滤（复用现有 `Log` 接口新增 `path` query）。
- **[右键菜单已占位] 显示对父项的更改**：当前 diff 默认相对 HEAD；对于多父提交（merge commit），需支持选择相对第 1 父 / 第 2 父，`GET /diff` 接口新增 `parent` 参数（1-based）。

### 8.4 其它后续演进

- **stash**：当前未含。后续加 `POST /stash` + 工作区快照列表（push/pop/apply/drop），工作区页与分支 checkout 前预检走 stash。
- **submodule**：当前未含。后续按需新增（`.gitmodules` 探测 + submodule add/init/update/sync）。
- **内置冲突编辑器**：当前冲突解决仅 ours/theirs 二选一。后续调用 CodeEditor 打开冲突文件做三方合并（与 8.3 三元 diff 同视图）。
- **多远程管理**：当前远程页 UI 已占位；`GET/POST/DELETE/PUT /repositories/{id}/remotes`（name/url/pushUrl/fetch），与现有的多 remote 路由映射对齐。
- **强制 push / reset --hard**：MVP 不暴露（危险操作）。后续在「高级」对话框二次确认暴露，写入审计日志。
- **LFS 指针文件**：当前未特殊处理。后续探测 `.gitattributes` 的 LFS 配置，diff 时对 LFS 指针文件跳过内容展示（显示「LFS 对象」占位）。

---

## 9. AI Agent Rules

> 实现与维护本模块时必须遵守的规则。

1. **真源在 Server 实时采集**：仓库状态/分支/历史/diff 均由 `IGitRepositoryService` 以宿主 OS 进程身份调用 `git` 实时读取，**不持久化运行时状态**（仅 `GitRepository` 注册记录落 SQLite）。每次请求返回当下快照。禁止为提交/分支/历史新建数据库表。
2. **跨平台抽象**：与 `IIdentityProvider` 同模式——`IGitRepositoryService` 接口 + `LocalGitRepositoryService` 实现，平台差异（`git` 可执行路径探测）封装在 `IHostGitCli`。Server 端单一 CLI 实现跨 Ubuntu + Windows Server（`git` 行为一致）。
3. **Provider 必须 Singleton**：`IGitRepositoryService` 持有按 repoId 缓存的 `SemaphoreSlim` 字典（写操作串行化，避免 index.lock 冲突），必须 Singleton。禁止 Scoped/Transient。
4. **复用 `IAuthSession` JWT**：`IRemoteGitClient` 不持有独立凭据；未登录时 `GitClientApp.Activate` 弹提示窗，不崩溃。`RemoteGitClient` 每请求检查 `State == Authenticated`。
5. **不存储 Git 凭据**：push/pull 的 SSH 私钥 / HTTPS 密码完全由宿主 OS git 凭据体系处理。RelaxKonOS 不代理、不收集、不存储。凭据缺失时 `RequiresCredentials=true`，引导用户在宿主 OS 配置（硬约束 §5.2）。
6. **不 mutate `HttpClient.BaseAddress`**：每请求用绝对 URI，与 `TaskManagerClient` / `DockerClient` 同模式。
7. **路由常量共用 `GitApiRoutes`**：Server 注册路由与 Client 拼接 URL 必须用同一常量，`{id}`/`{name}` 用 `Uri.EscapeDataString` 替换，禁止硬编码字符串。
8. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），JSON 用 `RelaxKonOSJsonOptions.Default`。
9. **porcelain 而非人类文案**：所有 `git` 输出用 `--porcelain=v2` / `--pretty=format` / `--for-each-ref --format` 机器可读格式，不解析本地化人类文案。
10. **路径越权防护**：所有 `git` 命令 `cwd` = 注册仓库 `Path`；`<path>` 参数校验在仓库根下，禁止 `../` 越权。
11. **危险操作确认**：删除未合并分支、revert、checkout 覆盖、删除仓库注册需二次确认；MVP 不暴露 `--force` / `reset --hard`。
12. **错误统一 RFC 7807**：Server `Results.Problem(..., type: "https://relaxkonos.app/problems/git-" + suffix)`；Client 解析 `ProblemDetails` 抛 `RelaxKonOSAuthException`，VM catch 后写 `StatusText`。
13. **DispatcherTimer 生命周期**：View `Unloaded` 时必须调 `viewModel.Stop()` 停止定时器。`RefreshStatusAsync` 用 `Interlocked` 重入保护。
14. **国际化三语言**：所有可见文案用 `loc:Loc` 绑定 key，`en-US`/`zh-CN`/`ja-JP` 同步新增，key 层级 `git.*` 隔离。
15. **编译验证**：`dotnet build RelaxKonOS.sln -c Debug` 必须 0 错误。

---

## 10. 验收矩阵

| 场景 | 验收点 |
|------|--------|
| 仓库列表 | 登录后显示已注册仓库；切换仓库刷新全部状态 |
| 工作区状态 | 修改文件后 status 显示未暂存；暂存后移到已暂存栏 |
| 提交 | 输入消息 + 暂存文件 → 提交成功 → 历史出现新提交 |
| 分支新建/切换/删除 | 新建分支 → 切换 → 历史正确；删除未合并分支有确认；删除当前分支被拒 |
| 拉取(无冲突) | upstream 落后时 pull → 成功 → ahead/behind 归零 |
| 拉取(冲突) | 制造冲突 → pull → 进入冲突页 → 选 ours/theirs → 继续 → 成功 |
| 推送 | 提交后 push → 成功 → upstream 领先归零；凭据缺失时提示而非崩溃 |
| 历史 | log 显示 hash/作者/时间/消息；选中提交显示变更文件 |
| Revert | 选中提交 → 确认 → 成功 → 历史出现反向提交；冲突时进冲突页 |
| 权限 | 仅有 read 权限的用户：写操作按钮禁用 + 提示 |
| 未登录 | 未登录弹提示窗，不崩溃 |
| 跨平台 | Ubuntu + Windows Server 均能列仓库/提交/拉取（git 行为一致） |
| 三语言 | 切换 en-US/zh-CN/ja-JP 所有文案更新 |
| 断线/取消 | 操作中窗口关闭不崩溃；服务端信号量正确释放 |
