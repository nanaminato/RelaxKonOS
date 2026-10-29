# RelaxKonOS 代码库优化与拆分计划

> 状态：审查完成，P0-1～P0-3 与 P1-1 试点为已采纳的实施方案（2026-10-28）。本文记录一次全仓静态审查的度量、优先级与实施记录；未列出的项不代表不存在问题。
>
> 范围：`RelaxKonOS.Server`、`RelaxKonOS.Server.Tests`、`Client/RelaxKonOS.Client`、`RelaxKonOS.PrivilegedHelper`、`Shared/*`、`Tests/*`。**Tailscale 相关代码与文档正在另一路开发中，本次审查与改动均排除**，避免与其并行修改冲突。
>
> 方法：按文件行数、类型/方法数量、跨域重复模式（`catch`、`Results.Problem`、claim 解析、路径包含检查、DI 注册、`<Compile Include>` 源码链接、测试宿主创建点）做静态扫描；不把“文件长”本身当作缺陷，只在能指出具体职责混合或重复时才列为候选。

## 1. 结论摘要

按“收益/风险”排序，先做四项：

| 编号 | 事项 | 解决的问题 | 风险 | 状态 |
| --- | --- | --- | --- | --- |
| P0-1 | 统一验证入口（`Tools/tests.json` + `Tools/verify.ps1`） | 测试靠文档里的 `--xxx-only` 手工调用，无套件清单与汇总 | 低（纯新增） | 已实施 |
| P0-2 | 拆分 `Startup/ServerModules.cs` | 一个类同时承担目录、配置解析、拓扑排序、只读清单与停用门禁 | 低（208 项模块测试兜底） | 已实施 |
| P0-3 | 统一路径包含校验（`PathSafety`） | 安全敏感逻辑至少两套私有实现，且分散在 Files/Docker/WebServer/Git | 中（需逐处核对语义） | 已实施 |
| P1-1 | 统一 problem 异常映射（Git + Installations 试点） | 13 个私有 `Handle` 包装、254 个端点 `catch`、131 处 `Results.Problem` | 中（错误契约不变） | 已实施 |

后续候选（本轮不做）：测试工程的源码链接（Server.Tests 22 个、Explorer.Tests 27 个、PrivilegedHelper 10 个）、`Protocol` 中的行为代码、客户端大文件与 UI 同步等待。详见 §7。

## 2. 度量快照（2026-10-28，排除 Tailscale）

### 2.1 服务端规模与端点

| 指标 | 数值 |
| --- | --- |
| `RelaxKonOS.Server` C# 文件 | 约 550 |
| 端点文件 / 行数 | 43 个 / 约 5600 行 |
| 内联 handler 映射（`MapGet/Post/Put/Delete/Patch`） | 477 |
| 端点内 `catch` / `Results.Problem` | 254 / 131 |
| 每文件私有 `Handle` 包装 | 13 个文件 |
| 领域异常类型（携带 `problemCode`/`statusCode`） | 20+ |
| 重复解析 `sub` claim（`Guid.TryParse(sub|principal…)`） | 32 处 |
| 路径包含检查（`GetFullPath`/`IsWithin`/`GetRelativePath`） | 约 70 处，至少 2 套私有实现 |

### 2.2 测试与工程结构

| 指标 | 数值 |
| --- | --- |
| 控制台测试项目 | 25 个（`Tests/*` 23 个 + `RelaxKonOS.Server.Tests` + `RelaxKonOS.ServerModules.Tests` 计入前者） |
| 测试入口风格 | 每个项目一个 `Program.cs`，靠 `--xxx-only` 开关选择套件；无统一清单、无 CI 配置 |
| `Tests/ServerModules/Program.cs` 内 `WebApplication.CreateBuilder` | 10 处 |
| 跨项目 `<Compile Include>` 源码链接 | Server.Tests 22、Explorer.Tests 27、PrivilegedHelper 10、Settings.Tests 7、Server 2 |

### 2.3 大文件（非 Tailscale，按行数）

| 文件 | 行数 | 主要症状 |
| --- | ---: | --- |
| `Client/RelaxKonOS.Client/Apps/Explorer/ViewModels/ExplorerViewModel.cs` | 1695 | 89 个方法、19 处 `catch (Exception)`；仅两个很小的 partial |
| `Client/RelaxKonOS.Client/Views/Shell/LauncherDesktopShells.cs` | 1556 | 4 个 shell 类 + 3 个 helper（含画笔转换器）同文件 |
| `Client/RelaxKonOS.Client/ViewModels/ServerCenter/ServerCenterViewModel.cs` | 1500 | 48 个方法、12 处 `catch`；已有一个 partial |
| `Client/RelaxKonOS.Client/Apps/ServerCenter/Views/SshFileBrowserView.axaml.cs` | 1488 | 128 个方法、22 个 `async void`，没有 ViewModel |
| `RelaxKonOS.Server.Tests/ServerCoreChecks.cs` | 1467 | 21 个互不相关的检查（用户执行、XDG、SMB、主题、性能采样、壁纸…） |
| `RelaxKonOS.PrivilegedHelper/LinuxUserFileOperations.cs` | 1325 | 10 个类型、88 个方法 |
| `RelaxKonOS.PrivilegedHelper/Program.cs` | 1055 | 命令分派与实现同文件 |
| `RelaxKonOS.Server.Tests/DockerChecks.cs` | 1017 | 容器/镜像/网络/卷/栈检查混在一起 |
| `Tests/ServerModules/Program.cs` | 约 950 | 10 个测试宿主；本轮新增的驻留检查也堆在这里 |
| `RelaxKonOS.Server/Startup/ServerModules.cs` | 320 | 目录、配置、排序、清单、门禁五种职责 |

### 2.4 跨域重复

- **错误响应**：13 个端点文件各自实现 `Handle`/`HandleAsync` + `Problem`，形状相同但异常类型、状态码与扩展字段分散；客户端另有 18 个 `*ApiException`、455 处 `problemCode` 引用、25 个文件同时做 HTTP 调用与错误解析。
- **用户身份解析**：32 处 `Guid.TryParse(...sub...)`，各端点各自决定 401/抛异常。
- **路径包含**：`ApplicationArchiveSafety.IsWithin`（相对路径法）与 `FileOperationService.ContainsPath`（前缀比较法）两套实现；Files/Docker/WebServer/Git 还有内联比较。
- **UI 线程同步等待**：`ClientPresentationPreferences` 11 处、`WindowManager` 6 处、`ServerCenterViewModel` 4 处 `.Result/.Wait()`。
- **测试源码链接**：同一类型在不同程序集各编译一份，产品与测试可能漂移（详见 §7.2）。

## 3. P0-1 统一验证入口

**问题**：测试入口是 25 个控制台项目，套件用 `--xxx-only` 选择；“这次改动要跑哪些”只写在设计文档的验证段落里，无法执行，也无法汇总。

**方案**：

- `Tools/tests.json`：套件清单，字段为 `id`、`name`、`project`、`args`、`default`、`timeoutSeconds`、`platforms`、`optional`。
- `Tools/verify.ps1`：读取清单，顺序执行（避免并行 MSBuild/端口冲突），逐个打印 `PASS/FAIL` 与耗时，失败时保留日志并在结尾汇总；支持 `-List`、`-Suite`、`-NoBuild`、`-KeepLogs`、`-TimeoutScale`。
- 默认套件（12 个）：模块框架与驻留、别名登录、会话有效性、部署定义、Git 冲突、Git 推送预览、服务端设置、Terminal 契约、路径包含规则、problem 形状、客户端模块归因、客户端设置页。
- 只登记“改动后必须绿”的套件；平台专属（Windows/Linux）、依赖真实外部服务（Docker/Mihomo/Squid/证书）的套件标为 `optional`，由人显式 `-Suite` 指定。
- 脚本保持 ASCII 与 Windows PowerShell 5.1 兼容：清单用 UTF-8 显式读取，避免控制台代码页改变套件名或让脚本本身失效。

**验收**：`powershell -NoProfile -File Tools\verify.ps1 -List` 列出套件；默认集合可执行并汇总；任一失败时退出码非零；`-Suite <id>` 可单独跑。

## 4. P0-2 `ServerModules` 拆分

**问题**：`ServerModules.cs` 同时是模块目录（编译期清单）、配置解析（含未知键/依赖/循环校验）、快照与拓扑排序、只读清单与停用门禁。文件 320 行，职责跨三层。

**方案**：保持 `internal` API 与 `ServerModules` 类型不变，用 partial 按职责拆文件（与仓库既有的 `GitClientViewModel.*.cs`、`LocalGitRepositoryService.*.cs` 一致）：

- `ServerModules.cs`：接口（`IServerModule`、`IServerModuleResidency`、`IServerModuleDeactivationGuard`）、常量、`ServerModuleContext`、快照状态与 `Create/Resolve`。
- `ServerModules.Catalog.cs`：`Catalog()`、模块配置名派生、`ResolveEnabledIds`。
- `ServerModules.Inventory.cs`：`DescribeAsync`、`DescribeResidency`、`ValidateDeactivationAsync`。

不改变配置语义、错误文本、启动顺序与调用方；测试无需改动。

**验收**：`Tests/ServerModules` 全部通过；三个文件的职责边界与本文一致；无公开/内部签名变化。

## 5. P0-3 路径安全统一

**问题**：`ApplicationArchiveSafety.IsWithin` 与 `FileOperationService.ContainsPath` 是两套语义接近但不完全相同的实现；Docker/WebServer/Git 还有内联比较。安全逻辑分散会导致“修一处漏两处”。

**方案**：

- 新增 `RelaxKonOS.Server/PathSafety.cs`（`namespace RelaxKonOS.Server`）：
  - `Comparison`：Windows `OrdinalIgnoreCase`，其它平台 `Ordinal`。
  - `IsWithin(root, candidate)`：两端 `GetFullPath` 归一，按“等于或 `root + 分隔符` 前缀”判断，处理根目录结尾分隔符。
  - `Normalize(path)`：`TrimEndingDirectorySeparator(GetFullPath(path))`。
- 替换 `ApplicationArchiveSafety.IsWithin`、`DockerCliEngineService.IsPathWithin`、`FileOperationService.ContainsPath`、`LocalGitRepositoryService.IsPathSafe` 四处私有实现；`GitTextEditor` 的“必须严格位于仓库内（等于根也拒绝）”额外判断保留在同处，不为了统一而放宽。
- 其余内联比较（WebServer 的路径解析、Nginx 配置路径）不是包含判断，保持原样；不为“统一”改变行为。
- 明确边界：`IsWithin` 不解析符号链接/重解析点；需要防链接逃逸的调用方必须先解析真实路径（与现状一致，避免假装解决了未解决的问题）。
- 测试覆盖：同路径、子路径、父路径、兄弟前缀（`/a/bc` vs `/a/b`）、`..` 越界与越界后返回、混合分隔符、根目录结尾分隔符、空值、大小写（分平台断言）。

**验收**：新测试通过；替换点行为不变；文档写明“不解析链接”的边界。

## 6. P1-1 problem 映射试点（Git + Installations）

**问题**：每个端点域复制 `Handle(() => …)` / `catch (DomainException)` / `Problem(code, status)`；异常类型各自携带 `ProblemCode`/`StatusCode`，HTTP 映射却分散在 13 个文件里。

**方案**：

- 新增 `Endpoints/IProblemException.cs`：`ProblemCode`、`StatusCode`、可选 `Detail`。
- 新增 `Endpoints/ProblemMapping.cs`：`ToResult(IProblemException)` 生成与现有一致的 `Results.Problem`（`title=code`、`type=problems/{code}`、`problemCode` 扩展），`Create(code,status)` 供没有异常可抛的校验分支使用，`WriteAsync` 供管线写同一形状。
- 在请求管线新增 `UseServerProblemMapping()`（由 `UseServerRequestObservation` 调用）：捕获 `IProblemException` 与 `ModuleUnavailableException`（后者保留 503 + `Retry-After`），未实现接口的异常继续是 500。
- 试点转换为 `IProblemException`：`GitBuildProblem`、`InstallationException`、`InstallationCancelledException`、`InstallationInterruptedException`（后两者在协调器内部收尾，HTTP 不可达，状态码取 409）。
- 试点删除包装：`GitBuildEndpoints`、`InstallationEndpoints` 不再自带 `Handle/HandleAsync/Problem`；handler 直接返回结果，业务校验仍显式返回 400/403/404（改走 `ProblemMapping.Create`）。
- 同时新增 `ClaimsPrincipal.UserId()`（缺 `sub`/name-identifier 时抛 `UnauthorizedAccessException`），试点文件改用它；其余端点留待下一轮，避免一次改动过大。

**明确不做**：不改变任何 `problemCode`、状态码、`type` URL 或扩展字段；不把 Git/Installations 之外的端点强行迁移；`ApplicationDeploymentEndpoints` 的 25 处包装留到下一轮验证后再动。

**验收**：`Tests/ServerModules` 的 Git 路由与 `--alias-only` 通过；新增“映射形状”单元检查（状态码、`problemCode`、`type`）；Git/Installation 的既有错误码回归通过。

## 7. 后续候选（本轮不做）

### 7.1 客户端大文件与 UI 线程

按现有 partial 惯例拆分 `ExplorerViewModel`（导航/选择/上传/操作/剪贴板）、`ServerCenterViewModel`（连接/服务器/安装/隧道）、`LauncherDesktopShells`（每个 shell 一文件）；`SshFileBrowserView` 抽 ViewModel 后 code-behind 只留事件转发。UI 线程上的 `.Result/.Wait()` 改为异步或 `Dispatcher.UIThread.InvokeAsync`，这需要在实机上验证交互，不能只靠构建。

### 7.2 测试工程的源码链接

`<Compile Include="../产品目录/...">` 让测试与产品各有一份类型。建议下沉为共享程序集（如 `Shared/RelaxKonOS.Platform`）或 `InternalsVisibleTo` + 项目引用；先拿 PrivilegedHelper 的 10 个链接试点。

### 7.3 `Protocol` 中的行为代码

`ServerReleaseArchiveVerifier` 等校验/IO 逻辑位于纯契约程序集。建议移到共享信任程序集（如 `Shared/RelaxKonOS.ReleaseTrust`），保持 Protocol 只含 DTO/路由/常量。

### 7.4 其余测试与帮助类

`ServerCoreChecks`、`DockerChecks`、`Tests/ServerModules/Program.cs` 可按域拆文件；`PrivilegedHelper/Program.cs` 可按 verb 拆分。这些只影响可读性，优先级低于 §7.1～7.3。

## 8. 实施记录

### 8.1 P0-1 统一验证入口（2026-10-28）

- 新增 `Tools/tests.json`（30 个套件）与 `Tools/verify.ps1`。
- 默认套件 12 个：`server-modules`、`server-alias`、`server-session-valid`、`server-deployment-definition`、`server-git-conflicts`、`server-push-preview`、`server-settings`、`server-terminal-contract`、`server-path-safety`、`server-problem-mapping`、`client-layout-modules`、`client-settings`。
- 可选套件：Docker/Proxy/WebServer/上传/文件/归档等依赖真实宿主或外部服务的检查，以及 Windows/Linux 专属检查；`optional` 只能显式 `-Suite` 选择。
- 验证：
  - `powershell -NoProfile -File Tools\verify.ps1 -List` 输出 30 个套件。
  - 默认集合一次运行约 126 s：**9 个通过**（alias、session-valid、deployment-definition、git-conflicts、push-preview、settings、terminal-contract、path-safety、problem-mapping）；**3 个失败**——`server-modules`、`client-layout-modules`、`client-settings` 都停在
    `Client/RelaxKonOS.Client/Apps/Tailscale/TailscaleApp.cs(90,40): CS7036`，这是另一路正在进行的 Tailscale 开发的中间态编译错误，与本轮改动无关（`server-modules` 在中断前单独运行通过 208 项）。
- 边界：脚本不安装/升级 SDK、不修改系统服务、不并行执行套件；平台不匹配的套件计为 SKIP；超时只终止 `dotnet` 进程，测试自己启动的子进程由套件自身的清理逻辑负责。
- Tailscale 编译恢复后，用 `powershell -NoProfile -File Tools\verify.ps1 -Suite server-modules,client-layout-modules,client-settings` 补跑这三个套件即可关闭本节。`server-modules` 在 Tailscale 中断前已单独通过 208 项。

### 8.2 P0-2 `ServerModules` 拆分（2026-10-28）

- `ServerModules` 改为 `partial`，按职责拆三个文件：
  - `ServerModules.cs` 187 行：接口、常量、`ServerModuleContext`、快照状态、`Create/Resolve`、`ConfigureServices/MapEndpoints`。
  - `ServerModules.Catalog.cs` 67 行：`Catalog()`、`ConfigurationName`、`ResolveEnabledIds`。
  - `ServerModules.Inventory.cs` 89 行：`DescribeAsync`、`DescribeResidency`、`ValidateDeactivationAsync`。
- API、配置语义、错误文本、启动顺序均未改变，测试无需改动；原来的 355 行单文件不再同时承担四种职责。
- 验证：`dotnet build RelaxKonOS.Server` 0 错误；`Tests/ServerModules` 208 项通过（拆分后、Tailscale 编译失败前）。

### 8.3 P0-3 路径安全统一（2026-10-28）

- 新增 `PathSafety`（43 行，`namespace RelaxKonOS.Server`）：平台相关比较、`Normalize`、`IsWithin`；明确不解析符号链接/重解析点。
- 替换四处私有实现：`ApplicationArchiveSafety.IsWithin`、`DockerCliEngineService.IsPathWithin`、`FileOperationService.ContainsPath`、`LocalGitRepositoryService.IsPathSafe`；`GitTextEditor` 改用 `PathSafety` 但仍保留“目标必须是仓库的子路径（等于根也拒绝）”的额外判断。
- 新增 `--path-safety-only`（16 项）：同根、子路径、`..` 越界与越界后返回、兄弟前缀、混合分隔符、根结尾分隔符、空值、Normalize、Windows/Unix 大小写差异。
- 验证：`--path-safety-only` 16 项通过；`--file-operations-only`、`--archives-only` 通过。
- 已知边界：`IsWithin` 以字符串前缀判断，不解析链接；依赖链接逃逸防护的调用方必须自己先解析真实路径（与替换前一致）。

### 8.4 P1-1 problem 映射试点（2026-10-28）

- 新增 `Endpoints/IProblemException.cs`、`Endpoints/ProblemMapping.cs`（`ToResult`/`Create`/`WriteAsync`）、`Startup/ServerPipeline.UseServerProblemMapping()`（由 `UseServerRequestObservation` 调用，同时保留模块不可用时的 503 + `Retry-After`）与 `Endpoints/ClaimsPrincipalExtensions.cs`（`UserId`/`SessionId`）。
- 试点转换为 `IProblemException`：`GitBuildProblem`、`InstallationException`、`InstallationCancelledException`、`InstallationInterruptedException`。后两者在协调器内部收尾、不经过 HTTP，`StatusCode` 取 409 只是给未来可达路径一个确定答案。
- 试点删除包装：`GitBuildEndpoints` 56→41 行（`Handle/HandleAsync/Problem` 全部删除），`InstallationEndpoints` 119→107 行（2 个 `Handle`、2 个显式 `catch (InstallationException)`、本地 `Problem` 删除；校验分支改用 `ProblemMapping.Create`）。
- 响应形状保持：`status`、`title=code`、`type=https://relaxkonos.app/problems/{code}`、`problemCode` 扩展；未实现接口的异常仍是 500。
- 新增 `--problem-mapping-only`（9 个用例）：6 个异常形状用例 + 真实回环 HTTP 的管线用例（Git 异常→404、安装异常→503、未映射异常→500），每个用例内部再断言状态码、`title`、`type` 与 `problemCode`。
- 验证：`--problem-mapping-only` 9 个用例通过；`--alias-only` 70 项通过；`Tests/ServerModules` 208 项通过（Tailscale 中断前）。
- 已知边界：`InstallationEndpoints`/`GitBuildEndpoints` 的异常映射现在依赖管线中间件；绕过 `UseServerRequestObservation` 的测试宿主会看到 500，这不是真实 Host 的路径。`ApplicationDeploymentEndpoints`（25 处包装）与其余 30 处 claim 解析留待下一轮，按域核对错误码后再迁移。
