# RemoteOS Explorer 模块设计

> 内置文件管理器：UI 移植自 [Jaya File Manager](https://github.com/waliarubal/Jaya)（BSD-3-Clause），所有文件操作经 Server 端 REST API 执行，复用宿主 OS 用户/权限体系。
>
> - Windows 11 体验优化的当前完成项、验证与后续 API 计划见 [`RemoteOS.Explorer.Progress.md`](./RemoteOS.Explorer.Progress.md)（2026-09-06）
> - 架构原则见 [`RemoteOS.Architecture.md`](../architecture/RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](../README.md)（§6 内置应用 / §7 RemoteExplorer）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](../desktop/RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md)（Explorer 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](../architecture/RemoteOS.Protocol.md)（§Files DTO 与路由）

---

## 1. 定位

RemoteExplorer 是 RemoteOS 的内置文件管理器，用于浏览与操作**服务端宿主 OS** 的文件系统。

- **架构归属**：§6.2 Remote Service Application —— UI 在 Client 本地渲染，文件 IO 在 Server 端执行。
- **复用宿主 OS 权限**（project_memory 硬约束）：Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限，**不另建** RemoteOS ACL 系统。权限提升（sudo/UAC）后续接入（见 §7 后续）。
- **不存储密码**：认证委托宿主 OS（已完成于登录模块），Explorer 仅消费 `IAuthSession.Tokens.AccessToken`。
- **UI 来源**：移植自 Jaya File Manager（BSD-3）。保留 Jaya 的导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏布局；**去除**插件系统（仅保留 FileSystem 单 provider）、Ribbon（后续延后）、About/ManagePlugins/Update 等非核心视图。原始版权头保留于移植文件。

**实现范围**：浏览（驱动器/目录/文件）+ 基本操作（新建文件夹/删除/重命名/复制/剪切/粘贴/上传/下载）+ 文件打开（默认程序或“打开方式”）+ 文件/目录属性查看（Linux 可编辑 POSIX 权限）。上传支持多文件与递归文件夹；客户端宿主机剪贴板中的文件/文件夹可直接粘贴到当前远端目录。

---

## 2. 包与集成方式

### 2.1 NuGet 包

| 包 | 版本 | 用途 |
|----|------|------|
| `Xaml.Behaviors.Avalonia` | 12.0.0.1 | Avalonia 12 兼容的 behaviors（双击导航等交互；wieslawsolotes fork） |
| `Newtonsoft.Json` | 13.0.3 | Jaya 配置模型序列化（保留 Jaya 原始依赖；线协议 DTO 仍用 System.Text.Json） |

- 中心化包管理：版本声明在 [`Directory.Packages.props`](../../Directory.Packages.props)。
- **DataGrid**：实际使用独立的 `Avalonia.Controls.DataGrid` 包；当前为可调整列宽的详细信息列表，并由 DataGrid 自身管理滚动。
- **不引入** Jaya 原依赖：`AvaloniaUIRibbon`（不兼容 12.1）、`RestSharp`（去 UpdateService）、`Avalonia.ReactiveUI`、`Avalonia.Xaml.Behaviors`（旧 0.10 线，已弃用）。

### 2.2 嵌入而非替换 Shell

`ExplorerMainView` 作为 `UserControl` 塞进 `RemoteWindow`，与 Notepad / Terminal 同构：

```text
ExplorerApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("RemoteExplorer", view)
    |
    WindowManager.Create → RemoteWindow
    |
    ExplorerMainView (UserControl)
    ├── NavigationBar（后退/前进/向上/刷新 + 面包屑/可编辑地址 + 当前目录筛选）
    ├── CommandBar（新建/剪切/复制/粘贴/重命名/删除 + 更多操作 + 视图开关）
    ├── StatusbarView（底部：状态文本 + 加载指示）
    └── Grid（主体）
        ├── NavigationView（左：TreeView 懒加载）
        ├── GridSplitter
        └── ExplorerView（右：DataGrid + 列标题 + 空视图提示）
```

### 2.3 去插件化

Jaya 原架构通过 `ServiceLocator` 反射扫描 `Jaya.Provider.*.dll` 加载多 provider（FileSystem/Dropbox/Ftp/GoogleDrive/S3）。本次移植**去除**整个插件系统：

- 删除 4 个云 provider 项目（Dropbox/Ftp/GoogleDrive/S3）。
- `ServiceLocator` 反射加载 + `AppDomain.GetAssemblies` 扫描 → **删除**。
- `IProviderService` 接口边界 → **替换为** `IExplorerClient`（typed HttpClient，直接调 Server REST API）。
- 多账户模型（`AccountModelBase`）→ **删除**（单 provider 单宿主机，无需账户配置）。
- `ViewModelLocator.AutoWireViewModel` 反射装配 → **替换为** `ExplorerApp.Activate` 中显式 `new ExplorerViewModel(client)` + DI。

---

## 3. 架构：REST（非 SignalR）

### 3.1 数据流

```text
┌───────────── Client (RemoteOS.Client/Apps/Explorer) ─────────────┐    ┌──────── Server (RemoteOS.Server/Files) ────────┐
│                                                                  │    │                                                │
│  ExplorerMainView (UserControl)                                  │    │  FileEndpoints (minimal API, [Authorize], JWT) │
│    └─ DataContext = ExplorerViewModel                            │    │    └─ GET drives/list/info/download            │
│                                                                  │    │       POST directory/rename/move/copy/upload   │
│  ExplorerViewModel                                               │    │       DELETE files                              │
│    ├─ Nodes: ObservableCollection<TreeNodeModel>（导航树）       │    │                                                │
│    ├─ Entries: ObservableCollection<FileSystemEntryDto>（网格）  │    │  IFileService                                  │
│    ├─ AddressbarPath / StatusText / IsBusy                       │    │    └─ LocalFileService (System.IO, 平台感知)    │
│    ├─ 历史栈（Back/Forward/Up）                                  │    │         = 移植自 Jaya FileSystemService         │
│    └─ 文件操作命令（NewFolder/Delete/Rename/Copy/Cut/Paste/U/D） │    │         Windows 盘符 / Linux "/" 根特殊处理     │
│                                                                  │    │         复用宿主 OS 进程身份与权限              │
│  IExplorerClient (typed HttpClient, JWT via IAuthSession)        │    │                                                │
│    └─ ExplorerClient                                             │    │                                                │
└──────────────────────────────────────────────────────────────────┘    └────────────────────────────────────────────────┘
              ↕  REST /api/v1.0/files/*  +  Shared/RemoteOS.Protocol/Files (DTO + FileApiRoutes)
```

### 3.2 传输选型：REST HTTP（非 SignalR）

与终端模块（SignalR 流式字节）不同，Explorer 用 **REST HTTP**：

1. **请求/响应天然契合**目录列举（一次请求返回完整 `DirectoryDto`）。
2. 与 Auth 端点同构（`Results.Ok` / `Results.Problem`），错误处理复用 `RemoteOsAuthException`。
3. 文件下载用 `Results.File(stream, ...)` 流式返回；上传用 `multipart/form-data`。
4. SignalR 仅未来 watch（目录变化推送）/大文件分块流式才需要，当前不引入。

### 3.3 认证复用 IAuthSession

`IExplorerClient` 从 `IAuthSession` 取 `ServerUrl` + `Tokens.AccessToken`：

- 不 mutate `HttpClient.BaseAddress`（每个请求用 `serverUrl` 构造绝对 URI，避免共享实例并发竞态——与 `IRemoteOsClient` 同模式）。
- 未登录（`State != Authenticated`）调用抛 `InvalidOperationException`；`ExplorerApp.Activate` 在未登录时弹提示窗。
- 所有端点 `[Authorize]`，错误统一 RFC 7807 `ProblemDetails`（错误码在 `type` URI，无 `Errors` 字典——见 Protocol.md）。

---

## 4. Server 端

### 4.1 IFileService / LocalFileService

[`RemoteOS.Server/Files/IFileService.cs`](../../RemoteOS.Server/Files/IFileService.cs) 定义接口；[`LocalFileService.cs`](../../RemoteOS.Server/Files/LocalFileService.cs) 实现。

- **移植自** Jaya `FileSystemService.GetDirectoryAsync` 的目录枚举逻辑（`DirectoryInfo.EnumerateDirectories` / `EnumerateFiles`）。
- **平台感知**：Windows 的 `GetDrives()` 返回盘符；Linux 仅返回 `/` 作为导航根，避免将 `/dev/shm`、`/dev/pts` 等嵌套挂载点显示为并列磁盘。`GetDirectory(null)` 在 Windows 返回盘符聚合视图，在 Linux 返回 `/` 根列举。
- **特殊位置枚举**（`GetSpecialLocations`）：跨平台枚举家目录/桌面/文档/下载/图片/音乐/视频，供 Explorer 导航窗格"主目录"组节点填充快捷入口。
  - 用 `Environment.GetFolderPath(Environment.SpecialFolder.UserProfile / Desktop / MyDocuments / MyPictures / MyMusic / MyVideos)` 跨平台获取。
  - Downloads 不在 `SpecialFolder` 枚举中 → 手动 `Path.Combine(home, "Downloads")` 拼接。
  - Linux `UserProfile` 为空时回退 `Environment.GetEnvironmentVariable("HOME")`（headless 服务进程兜底）。
  - **全部经 `Directory.Exists` 过滤**——headless Linux 上 Downloads/Pictures 等可能缺失，过滤后不返回失效快捷入口。
- **UnauthorizedAccessException 吞并**：列举时部分子目录不可访问不应导致整列失败（与 Jaya 一致）。
- **新增操作**（Jaya 原本 NotImplemented）：`CreateDirectory` / `Delete`（递归）/ `Rename` / `Move` / `Copy` / `Upload` / `OpenRead`（下载）。
- **以宿主 OS 进程身份运行**：Server 进程的权限即文件操作权限（复用宿主用户/权限，不另建 ACL——project_memory 硬约束）。复制、移动、删除、重命名和上传若被宿主 OS 拒绝，客户端才调用既有系统管理员认证窗口；认证成功后，对当前 JWT 的受影响目录授予 5 分钟（含子目录）权限并通过本地特权 helper 重试。取消或认证失败不会执行写操作。

### 4.2 FileEndpoints

[`RemoteOS.Server/Endpoints/FileEndpoints.cs`](../../RemoteOS.Server/Endpoints/FileEndpoints.cs) — 静态 `MapFileEndpoints(this IEndpointRouteBuilder)`，minimal API，全部 `RequireAuthorization()`。错误用 `Results.Problem(detail, statusCode, title, type: "https://remoteos.app/problems/" + suffix)`（仿 `AuthEndpoints.cs`）。

### 4.3 REST 端点签名

路由常量见 [`FileApiRoutes`](../../Shared/RemoteOS.Protocol/Files/FileApiRoutes.cs)，均 `$"/api/v1.0/files/..."`。

| 方法 | 入参 | 返回 | 错误码（type suffix） |
|------|------|------|----------------------|
| `GET /files/drives` | — | `IReadOnlyList<DriveDto>` | — |
| `GET /files/special` | — | `IReadOnlyList<SpecialLocationDto>` | —（缺失项服务端 `Directory.Exists` 过滤） |
| `GET /files/list?path=` | `string? path`（空=盘符根） | `DirectoryDto` | `not-found` / `access-denied` / `invalid-path` |
| `GET /files/info?path=` | `string path` | `FileSystemEntryDto`（404 if 缺） | `not-found` / `access-denied` |
| `GET /files/download?path=` | `string path` | `Results.File(stream, contentType, fileName)` | `not-found` / `access-denied` |
| `GET /files/content?path=` | `string path` | 原始文件字节流 | `not-found` / `access-denied` / `invalid-path` |
| `PUT /files/content?path=` | `string path` + 请求体字节流 | `FileEntryDto` | `not-found` / `access-denied` / `io-error` / `invalid-path` |
| `GET /files/properties?path=` | `string path` | `FilePropertiesDto`（404 if 缺） | `not-found` / `access-denied` / `invalid-path` |
| `PUT /files/permissions` | body `UpdateUnixPermissionsRequest` | `FilePropertiesDto` | `not-found` / `access-denied` / `invalid-path` / `invalid-mode` / `unsupported-operation` |
| `POST /files/directory?path=` | `string path` | `Results.Created(path, FileSystemEntryDto)` | `already-exists` / `access-denied` |
| `DELETE /files?path=` | `string path` | `Results.NoContent()` | `not-found` / `access-denied` / `io-error` |
| `POST /files/rename` | body `RenameRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/move` | body `MoveRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/copy` | body `CopyRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/upload?path=` | `string path` + `IFormFile` | `Results.Created(path, FileEntryDto)` | `not-found` / `access-denied` / `io-error` |

### 4.4 Program.cs 注册

```csharp
builder.Services.AddSingleton<Server.Files.IFileService, Server.Files.LocalFileService>();
// ...
app.MapFileEndpoints();   // 紧随 app.MapAuthEndpoints();
```

---

## 5. Client 端

### 5.1 文件清单

```
Client/RemoteOS.Client/Apps/Explorer/
├── ExplorerPickerOptions.cs       可复用远端文件选择器配置（打开文件 / 选择文件夹、多选、通配符过滤）
├── ExplorerApp.cs                 RemoteApplicationBase，Activate 创建 VM+View+Window，注入对话框回调
├── IExplorerClient.cs             typed HttpClient 抽象
├── ExplorerClient.cs              实现：JWT from IAuthSession，绝对 URI，ProblemDetails → RemoteOsAuthException
├── Models/
│   ├── TreeNodeModel.cs           导航树节点（懒加载 + dummy child 模式，移植自 Jaya TreeNodeModel；加 IconKind 驱动 emoji）
│   └── TreeNodeIconKind.cs        导航树节点图标种类枚举（Computer/Drive/Folder/Home/Desktop/Documents/Downloads/Pictures/Music/Videos/Network）
├── ViewModels/
│   └── ExplorerViewModel.cs       主 VM：合并 Jaya Explorer/Navigation/Addressbar/Toolbar/Statusbar VM；多根树 + 选中同步
├── Views/
│   ├── ExplorerMainView.axaml     布局：Menu/Toolbar/Addressbar/Statusbar + Navigation Tree + Explorer Grid
│   └── ExplorerMainView.axaml.cs  code-behind：地址栏回车/转到/双击进入
├── Dialogs/
│   ├── TextInputDialogView.axaml(.cs)   通用文本输入对话框（新建文件夹/重命名/复制/移动目标）
│   ├── TextInputDialogViewModel.cs
│   ├── ConfirmDialogView.axaml(.cs)     通用确认对话框（删除确认/About 消息）
│   ├── ConfirmDialogViewModel.cs
│   ├── OpenWithDialogView.axaml(.cs)     按扩展名筛选的“打开方式”选择器
│   ├── OpenWithDialogViewModel.cs
│   ├── FilePropertiesDialogView.axaml(.cs) 文件/目录属性与 Linux POSIX 权限编辑器
│   └── FilePropertiesDialogViewModel.cs
└── Converters/
    └── EntryConverters.cs         EntryType→图标可见性/类型名/大小友好字符串（SizeSuffix 移植自 Jaya）+ TreeNodeIconKind→emoji
```

### 5.2 ExplorerViewModel 设计

合并 Jaya 的 5 个 VM（`ExplorerViewModel` / `NavigationViewModel` / `AddressbarViewModel` / `ToolbarViewModel` / `StatusbarViewModel`）为单一 `ExplorerViewModel`，适配 RemoteOS DI 约定（避免引入 Jaya 的 `ServiceLocator` / `EventAggregator` / `ViewModelLocator` 反射基础设施）。

- **导航树懒加载**：`TreeNodeModel.AddDummyChild()` 保证展开箭头显示；首次展开触发 `ExpandRequested` 回调 → `IExplorerClient.GetDirectoryAsync` → 填充子目录（与 Jaya `OnNodeExpanded` 同模式）。
- **历史栈**：`_history` + `_historyIndex` 支持 `GoBack` / `GoForward` / `GoUp`（`CanGoBack`/`CanGoForward`/`CanGoUp` 驱动命令可用性）。
- **双击行为**：目录/驱动器 → `NavigateToAsync`；文件 → `OpenEntryAsync`。先使用 `DefaultAppRegistry` 中有效的扩展名关联；未关联或关联的应用不再声明该扩展名时，回退到第一个兼容的已注册应用；没有兼容应用时给出提示。
- **打开方式与默认关联**：右键菜单提供 `Open` / `Open with...` / `Properties`。“打开方式”仅列出同时实现 `IFileOpenApplication` 且在 manifest 声明当前扩展名的应用；可将所选应用设为该扩展名的默认程序，映射即时写入注册表并持久化到当前 Workspace。
- **属性与权限**：属性对话框展示类型、大小、时间、属性和宿主 OS 权限摘要；Linux 返回 `UnixMode` 时可编辑并保存 POSIX 权限位。Windows 等不支持的平台仅展示只读属性。
- **文件操作命令**：`Open` / `OpenWithSelected` / `Properties` / `NewFolder` / `Delete` / `Rename` / `Copy` / `Cut` / `Paste` / `Move` / `Upload` / `UploadFolder` / `Download` / `About` / `Close`，均通过 `[RelayCommand]` 生成；会改变目录内容的操作后调 `RefreshAsync` 刷新视图。
- **Windows 式剪贴板与进度**：远端条目复制/剪切仅保存短暂客户端会话引用，粘贴时才调用 Server 的 `Copy`/`Move`；当没有远端剪贴板内容时，“粘贴”读取宿主机系统剪贴板的文件/文件夹并上传。多文件、文件夹和剪贴板导入按项目顺序执行，状态栏显示项目数进度；上传还显示已发送字节百分比。宿主机路径只在 Client 读取，绝不发送给 Server。
- **可复用远端文件选择器**：`ExplorerPickerOptions` 将同一导航和条目视图嵌入应用的模态对话框；支持单/多文件选择、扩展名通配符过滤及目录选择。Notebook 与 Code Editor 用它选择远端文件，不会绕过 `IExplorerClient` 直接访问服务端文件系统。

#### 多根导航树（参考 Windows File Explorer Navigation Pane）

`LoadRootAsync` 并发 `Task.WhenAll(GetSpecialLocationsAsync, GetDrivesAsync)` 后构造三段树：

```
Nodes
├── 🏠 主目录 (Home)              ← path=家目录，点击组节点本身=导航到家目录（右侧网格列全部子项）
│   ├── 🖥️ 桌面                   ← 静态填充（GetSpecialLocationsAsync），叶子节点不挂 ExpandRequested
│   ├── 📄 文档
│   ├── 📥 下载
│   ├── 🖼️ 图片
│   ├── 🎵 音乐
│   └── 🎬 视频
├── 💻 此电脑 (This PC)            ← isComputer=true，挂 ExpandRequested
│   ├── 💽 C:                     ← dummy child 懒加载（与原 Jaya 逻辑一致）
│   └── 💽 D:
└── 🌐 网络 (Network)             ← IsNetwork=true 占位，点击仅显示状态栏文本不导航
```

- **主目录组节点用静态填充**（不含 dummy child、不挂 `ExpandRequested`）：与 Windows 11 Home 节点行为一致——展开=精选快捷入口，点击组节点本身=导航到家目录（右侧网格列出家目录全部子项）。两套来源不重复：快捷入口是协议层枚举的固定 6 项（桌面/文档/下载/图片/音乐/视频），家目录右侧网格列出的是真实目录的全部子项。
- **此电脑节点保留盘符列表 + dummy child 懒加载**（与原 Jaya 逻辑一致）。
- **网络节点占位**：当前不实现浏览；`OnSelectedNodeChanged` 中 `value.IsNetwork` 早退仅设状态栏文本"网络浏览暂未实现"。

#### 选中同步（防循环）

地址栏/前进后退/双击改变路径时，树自动展开并选中对应节点（与 Windows File Explorer 行为一致）：

- **同步点在 `NavigateToAsyncCore` 末尾**（不在 `OnAddressbarPathChanged`）——`TextBox.Text` TwoWay 绑定默认 `PropertyChanged`，每按键都触发 `OnAddressbarPathChanged`，路径还是半成品时去查找节点无意义且打断输入；同步必须在路径被服务端确认后做。
- **防循环标志 `_isSyncingTreeSelection`**：仅抑制 `SyncTreeSelectionAsync` 设 `SelectedNode` → `OnSelectedNodeChanged` → `NavigateToAsync` 这条反向边。不需要双向抑制（`OnAddressbarPathChanged` 不做同步，不会反向写 `SelectedNode`）。
- **`SyncTreeSelectionAsync` 早退**：当前 `SelectedNode.Path` 已等于目标 path 时直接返回（树点击触发导航场景的冗余查找）。
- **`FindAndExpandNodeAsync` 下钻**：先在顶层根节点与快捷入口叶子精确匹配（O(1) 命中家目录/桌面/文档等）；否则从"此电脑"下匹配的盘符节点下钻，逐级懒加载祖先——VM 内直接调 `await OnNodeExpandRequested(node)` 绕过 `IsExpanded` setter 的 fire-and-forget（setter 用 `_ = ExpandRequested?.Invoke(this)` 无法 await），加载完成后再设 `node.IsExpanded = true`（setter 检测 `_hasLoadedChildren` 已 true 不再 Invoke）。
- **失败不抛**：找不到对应节点时保持原选中，不阻塞右侧网格已展示内容（如路径存在但树未展开且懒加载失败）。
- **路径规范化辅助**（`NormalizePath` / `PathEquals` / `IsAncestorOrEqual`）：Linux 区分大小写（`Ordinal`），Windows 不区分（`OrdinalIgnoreCase`）；Linux "/" 根特殊处理（不能 trim 成空串）；非法字符 try/catch 兜底返回原值。

### 5.3 对话框回调注入

`ExplorerApp.WireDialogs` 将宿主能力注入 VM（仿 `NotepadApp` 的 `RequestTextAsync` 模式）：

| VM 属性 | 用途 | 实现 |
|---------|------|------|
| `RequestTextInputAsync` | 新建/重命名/复制/移动目标输入 | `AppContext.ShowDialogAsync<string?>` + `TextInputDialogView` |
| `RequestConfirmAsync` | 删除确认 | `AppContext.ShowDialogAsync<bool?>` + `ConfirmDialogView` |
| `ShowMessageAsync` | About 消息 | `ConfirmDialogView`（单按钮） |
| `RequestLocalUploadFilesAsync` / `RequestLocalUploadFoldersAsync` | 上传本地文件（多选）/ 文件夹（多选） | `StorageProvider.OpenFilePickerAsync` / `OpenFolderPickerAsync`（TopLevel = MainWindow） |
| `RequestClipboardUploadSourcesAsync` | 导入宿主机剪贴板文件/文件夹 | Avalonia 跨平台 `IClipboard.TryGetDataAsync` + `TryGetFilesAsync` |
| `RequestLocalSaveFileAsync` | 下载本地保存路径 | `StorageProvider.SaveFilePickerAsync` |
| `OpenFileAsync` | 根据默认关联或兼容应用打开远端文件 | `ApplicationManager.OpenFile` |
| `RequestOpenWithAsync` | 显式选择兼容应用并可保存默认关联 | `OpenWithDialogView` + `DefaultAppRegistry` |
| `ShowPropertiesAsync` | 显示文件/目录属性与可用的 Linux 权限编辑器 | `FilePropertiesDialogView` |
| `CloseAction` | 关闭 Explorer 窗口 | `WindowManager.Close(window)` |

**TopLevel 获取**：`ManagedWindow` / `RemoteWindow` 不是 `TopLevel`（桌面外壳 Canvas 内的 `TemplatedControl`），故 `StorageProvider` 用 `Application.Current.ApplicationLifetime.MainWindow`（实际 Avalonia `Window`）作为根。

### 5.4 Bootstrapper 注册

```csharp
services.AddHttpClient<Client.Apps.Explorer.IExplorerClient, Client.Apps.Explorer.ExplorerClient>();
services.AddSingleton<IRemoteApplication, Client.Apps.Explorer.ExplorerApp>();
```

`DesktopShellViewModel.PopulateDesktop()` 自动遍历所有 `IRemoteApplication`，Explorer 图标（📁）出现在桌面与开始菜单。

---

## 6. Protocol 层

[`Shared/RemoteOS.Protocol/Files/`](../../Shared/RemoteOS.Protocol/Files) — 零 Newtonsoft，纯 `System.Text.Json`，`sealed record` + `[property: JsonPropertyName("...")]`（camelCase，对齐 `RemoteOsJsonOptions.Default`）。

| 文件 | 职责 |
|------|------|
| `FileSystemEntryType.cs` | enum `File/Directory/Drive`（`JsonStringEnumConverter` camelCase） |
| `FileSystemEntryDto.cs` | 通用条目元数据：path/name/size/type/created/modified/accessed/isHidden/isSystem |
| `FileEntryDto.cs` | 文件条目（含 extension，非空 size）——用于 `DirectoryDto.Files` 列表 |
| `FilePropertiesDto.cs` / `UpdateUnixPermissionsRequest.cs` | 文件/目录属性、宿主权限摘要与 Linux POSIX 权限更新契约 |
| `DirectoryDto.cs` | 目录列举结果：目录自身元数据 + `Directories[]` + `Files[]` |
| `DriveDto.cs` | 驱动器/根挂载点：name/path/totalSize/isReady |
| `SpecialFolderKind.cs` | enum `Home/Desktop/Documents/Downloads/Pictures/Music/Videos`（camelCase 序列化，由 `RemoteOsJsonOptions.Default` 全局生效，无需显式 `[JsonStringEnumConverter]`） |
| `SpecialLocationDto.cs` | 特殊文件夹位置：kind/name/path（Server `GetSpecialLocations` 返回，已 `Directory.Exists` 过滤） |
| `RenameRequest.cs` / `MoveRequest.cs` / `CopyRequest.cs` | 操作请求 body |
| `FileApiRoutes.cs` | 路由常量（路径含 `/api/v1.0` 前缀，Server 注册与 Client 拼接共用；含 `Content` / `Properties` / `Permissions` 等文件读写与属性路由） |

**设计说明**：`FileEntryDto` 与 `FileSystemEntryDto` 是独立 record（非继承），因为 `FileEntryDto` 多 `Extension` 字段且 `Size` 为非空 `long`。Client 网格绑定 `FileSystemEntryDto`，`ExplorerViewModel` 在填充 `Entries` 时将 `FileEntryDto` 转为 `FileSystemEntryDto`（`Type=File`），丢弃 `Extension`（网格不展示）。

---

## 7. 后续

- **Ribbon**（Phase 6）：引 `AvaloniaControls.Ribbon.Flowery`（Avalonia 11.3+/net8+ 兼容 fork），移植 `RibbonView.axaml`。
- **网络节点浏览**：当前"网络"节点为占位，点击仅显示状态栏文本。后续接入 SMB/SSH 网络共享浏览（Server 端需扩展 `IFileService` 支持非本地路径）。
- **统一图标库**：当前导航树与条目网格均用 emoji，跨平台渲染差异（Windows Segoe UI Emoji / Linux Noto Color Emoji）。后续引 `Material.Icons.Avalonia`（或同类矢量图标库）统一替换全部图标，届时与 Ribbon 一起做（避免半 emoji 半矢量的中间态）。
- **预览/详情面板**：移植 Jaya `PreviewView` / `DetailsView`（图片/文本预览、文件属性详情）。
- **视图模式切换**：Details/Icons/List/Tiles/Content（Jaya `PaneConfigModel.ViewMode`）。
- **权限提升**：危险操作（如删除系统目录）委托宿主 OS（Linux: sudo / Windows: UAC、RunAs）——project_memory 硬约束。
- **目录 watch**：SignalR Hub 推送目录变化（`FileSystemWatcher` → Hub → Client 刷新）。
- **大文件流式**：分块上传/断点续传（替代当前一次性 `multipart/form-data`）。
- **配置持久化**：Jaya `PaneConfigModel` / `ToolbarConfigModel` / `ApplicationConfigModel` 本地保存（保留 Newtonsoft 序列化）。
- **快速访问**：Windows File Explorer 的"快速访问"（Quick Access）需要持久化最近访问记录 + 用户固定项，当前"主目录"组节点仅枚举标准特殊位置，不含最近访问；后续接入。

---

## 8. AI Agent 规则

实现/修改 Explorer 时必须遵守：

1. **所有文件 IO 必须经 `IExplorerClient` → REST API**。客户端不得直接访问本地文件系统（上传/下载的本地源/目标除外，走 `StorageProvider`）。Server 端 IO 只在 `LocalFileService` 内。
2. **复用宿主 OS 权限，不另建 ACL**（project_memory 硬约束）。Server 以进程身份执行 `System.IO`，权限不足返回 `access-denied`（403）。当前不做 sudo/UAC 提升。
3. **JWT 复用 `IAuthSession`**：`IExplorerClient` 不持有独立凭据；未登录调 `ExplorerApp.Activate` 弹提示窗，不崩溃。
4. **错误统一 RFC 7807**：Server `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`；Client `ExplorerClient` 解析 `ProblemDetails` 抛 `RemoteOsAuthException`，VM catch 后写 `StatusText`。
5. **路由常量共用 `FileApiRoutes`**：Server 注册与 Client 拼接 URL 必须用同一常量，禁止硬编码字符串。
6. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），线协议用 `System.Text.Json`（`RemoteOsJsonOptions.Default`）。Jaya 配置模型（如未来引入 `PaneConfigModel`）保留 Newtonsoft，但不进入线协议。
7. **移植 Jaya 文件保留原始版权头**（`// Copyright (c) Rubal Walia...`），不删改；新增文件用 RemoteOS 自己的版权头。Jaya 归属见 [`THIRD_PARTY_NOTICES.md`](../../THIRD_PARTY_NOTICES.md)。
8. **不引入 Jaya 的 `ServiceLocator` / `ViewModelLocator` / `EventAggregator` 反射基础设施**。新代码用 RemoteOS DI（`Microsoft.Extensions.DependencyInjection`）+ `CommunityToolkit.Mvvm`（`[ObservableProperty]` / `[RelayCommand]`）。
9. **对话框走 `AppContext.ShowDialogAsync`**（与 Notepad 同模式），不直接创建 Avalonia `Window`。本地文件选择走 `StorageProvider`（TopLevel = `MainWindow`）。
10. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
11. **特殊位置枚举必须经 `Directory.Exists` 过滤**，禁止返回失效快捷入口；`GetSpecialLocations` 在 Linux 下必须 `HOME` 环境变量兜底（`Environment.SpecialFolder.UserProfile` 在 headless 服务进程可能为空）。Downloads 不在 `SpecialFolder` 枚举中需手动 `Path.Combine(home, "Downloads")` 拼接。
12. **导航树选中同步必须防循环**：用 `_isSyncingTreeSelection` 标志仅抑制 `SyncTreeSelectionAsync → OnSelectedNodeChanged → NavigateToAsync` 反向边；同步点在 `NavigateToAsyncCore` 末尾（路径服务端确认后），不在 `OnAddressbarPathChanged`（每按键触发，路径半成品无意义）。`FindAndExpandNodeAsync` 下钻时直接 `await OnNodeExpandRequested(node)` 绕过 `IsExpanded` setter 的 fire-and-forget。
13. **路径比较跨平台**：用 `OperatingSystem.IsLinux()` 区分大小写敏感性（Linux `Ordinal` / Windows `OrdinalIgnoreCase`）；`NormalizePath` 对 Linux "/" 根特殊处理（不能 trim 成空串），对非法字符 try/catch 兜底返回原值。
