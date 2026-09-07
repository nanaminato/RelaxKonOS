# RemoteOS Settings 模块设计

> 演进基线（2026-09-07）：后续升级遵循 [`SettingsSystem.Goal`](./RemoteOS.SettingsSystem.Goal.md)。本文下述五页结构等内容是早期实现说明，当前代码已有八页；新 Goal 将设置提升为独立平台服务，并允许通过受限特权助手修改远程环境变量、时区、主机名与 DNS。新增能力尚未实现，不应将本文旧范围视为禁止实现的约束。

> 内置设置中心（Windows 11 / GNOME 风格）：5 个分类页（系统 / 个性化 / 时间和语言 / 网络 / 应用），用户偏好（壁纸 / 主题 / 时间格式 / 日期格式 / 语言 / 区域 / 默认程序）持久化到 Server 端 Workspace（`/workspaces/{id}/preferences`），多设备登录同一 Workspace 共享。
>
> Shell 选择使用 `WorkspacePreferencesDto.Shell` 保存跨设备意图；本机缺少或禁用扩展包时只回退本机默认 Shell，不覆盖该意图。完整的外部包安装/校验模型见 [`RemoteOS.ShellLauncher.Goal.md`](./RemoteOS.ShellLauncher.Goal.md)。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](../architecture/RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](../README.md)（§6 内置应用）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](../platform/RemoteOS.Login.md)（Settings 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](../architecture/RemoteOS.Protocol.md)（§Workspace Preferences DTO 与路由）
> - 服务端持久化见 [`RemoteOS.Storage.md`](../platform/RemoteOS.Storage.md)（Preferences JSON 列）

---

## 1. 定位

Settings 是 RemoteOS 的内置系统设置应用，参考 Windows 11 设置 / GNOME Settings 的分类导航结构。

- **架构归属**：§6.2 Remote Service Application —— UI 完全在 Client 本地渲染；偏好**真源在 Server 端 Workspace**（与 `TerminalSettings` / `BrowserSettings` 同模式：`OwnsOne + ToJson` 单列 JSON 持久化）。
- **多设备共享**：同一用户的持久 Workspace 持有一份偏好，多设备登录同一 Workspace 自动拉取同一份设置（符合 project_memory 硬约束「User → Workspace → Session → Device」模型）。
- **复用宿主 OS 权限**：早期 Settings 主要管理 RemoteOS 偏好，宿主配置以只读展示为主。后续按 [`SettingsSystem.Goal`](./RemoteOS.SettingsSystem.Goal.md) 增加真实宿主写入；需要特权的接口经封闭 Helper 操作执行，Server 保持非特权。此前“Settings 不触及宿主设置”的限制已被此决策替换。
- **不存储密码**：认证委托宿主 OS，Settings 仅消费 `IAuthSession.Tokens.AccessToken`。

**5 个分类页**：

| 页 | 图标 | 能力 | 持久化 |
|----|------|------|--------|
| 系统 | 💻 | 只读展示版本 / Server URL / 用户 / Workspace / 设备 / 连接状态 | — |
| 个性化 | 🎨 | 内置预设或上传图片壁纸 + 主题（Light / Dark / System） | Workspace.Preferences + Server Storage |
| 时间和语言 | 🕐 | 12/24 小时制 + 日期格式 + 语言 + 区域（时区只读） | Workspace.Preferences |
| 网络 | 🌐 | 只读连接状态 + 「测试连接」测往返延迟 | — |
| 应用 | 📦 | 已注册应用清单、第三方应用权限、内置远程浏览器的链接打开位置、开发者模式、默认程序映射编辑器（scheme/ext → appId） | 默认程序映射与浏览器链接打开位置 → Workspace；应用授权与开发者模式 → 本机 |

---

## 2. 嵌入方式

`SettingsView` 作为 `UserControl` 塞进 `RemoteWindow`，与 Notepad / Explorer / Browser / Terminal 同构：

```text
SettingsApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("Settings", view, bounds=820x560)
    |
    WindowManager.Create → RemoteWindow
    |
    SettingsView (UserControl)
        ├── 左侧导航 ListBox（5 个 SettingsPageViewModel，glyph + 显示名）
        └── 右侧 ContentControl（绑定 SelectedPage，DataTemplate 分发到 5 个 PageView）
```

`SettingsApp.Activate` 注入 `ShellSettings` / `IAuthSession` / `ISettingsClient` / `ApplicationManager` / `IRemoteOsClient` / `DefaultAppRegistry` / `IAppPermissionManager` / `DeveloperModeService`，构造 `SettingsViewModel` 后 `_ = viewModel.InitializeAsync()` 异步从服务端加载偏好。未登录时仍可打开（沿用 `ShellSettings` 默认值，不持久化）。

---

## 3. 协议契约（`Shared/RemoteOS.Protocol/Workspace/`）

新增 2 个 DTO（沿用 Protocol 约定：`sealed record` + `[property: JsonPropertyName]`，零 PackageReference）：

### 3.1 `WorkspacePreferencesDto.cs`

```text
WorkspacePreferencesDto
  WallpaperKey   string   壁纸 key（"builtin:bloom" 等，前缀见 BuiltInWallpaperPrefix）
  Theme          ThemeKind 主题（Light / Dark / System，来自 RemoteOS.Protocol.Desktop）
  TimeFormat     string   "24h" | "12h"（常量 TimeFormat24H / TimeFormat12H）
  DateFormat     string   .NET 日期格式串（"yyyy/M/d" 等）
  Language       string   BCP-47（"zh-CN" / "en-US" …）
  Region         string   BCP-47
  DefaultApps    IReadOnlyList<DefaultAppMappingDto>

  const BuiltInWallpaperPrefix = "builtin:"
  static Default { get; } = (builtin:bloom, Light, 24h, yyyy/M/d, zh-CN, zh-CN, [])
```

### 3.2 `DefaultAppMappingDto.cs`

```text
DefaultAppMappingDto
  Scheme   string   URI scheme（"http"/"mailto"）或文件扩展名（".txt"/".md"）
  AppId    string   目标应用 Id（"remoteos.browser" 等）
```

### 3.3 路由（`WorkspaceApiRoutes.cs`）

新增常量（路径含 `/api/v1.0` 前缀）：

```text
Preferences = /api/v1.0/workspaces/{id}/preferences   (GET / PUT)
```

---

## 4. 服务端

### 4.1 领域模型（`Server.Domain/Workspace.cs`）

`Workspace` 增加 `Preferences` 属性，与 `TerminalSettings` / `BrowserSettings` 并列：

```csharp
public WorkspacePreferencesDto Preferences { get; set; } = WorkspacePreferencesDto.Default;
```

默认值 `WorkspacePreferencesDto.Default`——旧 Workspace（建库时无此列）读取时回退默认偏好。

### 4.2 SQLite Schema（`RemoteOsDbContext.OnModelCreating`）

`Preferences` 作为 `OwnsOne + ToJson("preferences")` 挂在 `workspaces` 表，序列化为单列 JSON 文本（EF Core 9+ JSON 列映射，与 `terminal_settings` / `browser_settings` 同模式）：

```text
workspaces
  ...（既有列）
  terminal_settings   TEXT   (既有，TerminalSettingsDto JSON)
  browser_settings    TEXT   (既有，BrowserSettingsDto JSON)
  preferences         TEXT   (新增，WorkspacePreferencesDto JSON)
```

**配置可演进**——新增偏好字段无需改 schema（JSON 列内增字段）。

**建库兼容**：`EnsureCreated` 为新库建表时含 `preferences` 列；既有库（建库时无此列）由 `Program.cs` 启动时检测 `pragma_table_info('workspaces')` 并追加 `ALTER TABLE "workspaces" ADD COLUMN "preferences" TEXT NULL;` 增量补齐（与 `browser_settings` 列同模式）。列允许 NULL——EF Core `OwnsOne+ToJson` 读取 NULL 时回退领域模型默认值 `WorkspacePreferencesDto.Default`。

### 4.3 REST 端点（`Server.Endpoints/WorkspaceEndpoints.cs`）

2 个端点，全 `RequireAuthorization()`，复用既有 `FindAuthorizedWorkspace`（按 JWT `sub` claim 校验 workspace 归属）：

| Method | Route | 用途 |
|--------|-------|------|
| GET | `/api/v1.0/workspaces/{id}/preferences` | 读取偏好（直接返回 `workspace.Preferences`） |
| PUT | `/api/v1.0/workspaces/{id}/preferences` | 校验 + 整列覆盖（返回归一化后 DTO） |

**校验（`TryNormalize`）**：

- `WallpaperKey`：trim 后非空，长度 ≤ 128。
- `Theme`：`Enum.IsDefined<ThemeKind>()`。
- `TimeFormat`：必须等于 `24h` 或 `12h`。
- `DateFormat`：trim 后非空，长度 ≤ 32。
- `Language` / `Region`：长度 ≤ 16（空则回退默认）。
- `DefaultApps`：数量 ≤ 64；按 scheme（大小写不敏感）去重，后者覆盖前者；剔除空 scheme/appId；scheme 长度 ≤ 32，appId 长度 ≤ 128。

校验失败 `Results.BadRequest(new { message = "Invalid workspace preferences." })`。

> 注：错误体沿用 Workspace 端点既有风格（`{ message }` 匿名对象），与 Browser/Files 端点的 RFC 7807 `Results.Problem` 略有差异——这是 Workspace 端点历史约定（`TerminalSettings` 端点同样用 `{ message }`），保持一致。

---

## 5. 客户端

### 5.1 客户端 HTTP（`ISettingsClient` / `SettingsClient`）

- typed HttpClient（`Bootstrapper` 注册 `services.AddHttpClient<ISettingsClient, SettingsClient>()`）。
- **不 mutate `HttpClient.BaseAddress`**（避免共享实例并发竞态），每请求用 `IAuthSession.ServerUrl` 构造绝对 URI。
- `Authorization: Bearer {AccessToken}` 从 `IAuthSession.Tokens` 取。
- 路由常量共用 `WorkspaceApiRoutes.Preferences`，`{id}` 用 `workspaceId.ToString("D")` 替换，禁止硬编码字符串。
- 失败读 `ProblemDetails` 抛 `RemoteOsAuthException`（与 `BrowserClient` / `ExplorerClient` 同源）。
- JSON 用 `RemoteOsJsonOptions.Default`（与线协议一致）。

### 5.2 ViewModel 层（`Apps/Settings/ViewModels/`）

#### 5.2.1 `SettingsViewModel`（根 VM）

- 持有 5 个 `SettingsPageViewModel`（构造时注入 `ShellSettings` + 各自依赖 + `save` 回调）。
- `SelectedPage`（绑定左侧 ListBox + 右侧 ContentControl）。
- `InitializeAsync()`：窗口打开后调用一次——从服务端 GET 偏好 → `_settings.Apply(prefs)` + `AppsPage.SetMappings(prefs.DefaultApps)`；失败沿用默认值（设置仍可用，仅本地不持久化）。
- `Save()`（internal）：页 VM 编辑后调用——① 即时同步默认程序映射到 `DefaultAppRegistry`；② 防抖 300ms（`CancellationTokenSource` 取消上次）后 `SaveAsync` PUT 服务端。
- `SaveAsync`：构造 `prefs = _settings.ToPreferences(mappings)` PUT；失败保留本地值，后续改动可重试。

#### 5.2.2 `SettingsPageViewModel`（抽象基类）

- 持有 `ShellSettings`（实时外壳绑定源）+ `save` 回调。
- **透传通知**：订阅 `Settings.PropertyChanged`，在本 VM 上重发同名通知——外部加载（`PreferencesSync` 改变 Settings）时视图即刻刷新，避免脏数据。
- 抽象 `Glyph`（emoji）+ `DisplayName`（导航 + 标题）。

#### 5.2.3 5 个页 VM

| VM | 透传属性 | 特殊逻辑 |
|----|----------|----------|
| `SystemPageViewModel` | — | 只读展示 `IAuthSession` 当前状态（ServerUrl / User / Workspace / Device / ConnectionState / CreatedAt / LastLoginAt） |
| `PersonalizationPageViewModel` | `WallpaperIndex` / `Theme` | 主题 RadioButton 辅助属性 `IsLightTheme`/`IsDarkTheme`/`IsSystemTheme`（Theme 变化时刷新） |
| `TimeLanguagePageViewModel` | `TimeFormat` / `DateFormat` / `Language` / `Region` | `TimeSample` / `DateSample` 实时预览（`FormatTime`/`FormatDate` 供桌面外壳时钟复用）；`TimeZone` 只读展示宿主时区 |
| `NetworkPageViewModel` | — | 只读连接状态 + `TestConnectionCommand`（`IRemoteOsClient.GetMeAsync` + `Stopwatch` 测延迟） |
| `AppsPageViewModel` | — | `RegisteredApps`（只读）+ 应用权限入口；选择内置 `remoteos.browser` 时读取/保存 `BrowserSettings.LinkOpenTarget`；开发者模式开关/配对令牌 + `Mappings`（`ObservableCollection<DefaultAppMappingViewModel>`，可增删改）+ `SetMappings`/`ToMappings` 与 DTO 互转 |

**`DefaultAppMappingViewModel`**：单条映射（`Scheme` + `AppId` + `SelectedApp` ComboBox 双向同步），`OnSchemeChanged`/`OnAppIdChanged` 触发 `_save`。

### 5.3 `ShellSettings`（`Client.Services/`）

桌面外壳的**实时 UI 绑定源**（单例，当前 `IDesktopShell` 绑定 `Settings.CurrentWallpaper` 等）。是服务端 `WorkspacePreferencesDto` 在客户端的活副本：

```text
ShellSettings (ObservableObject, 单例)
  Wallpapers          IReadOnlyList<WallpaperOption>  9 个内置预设渐变壁纸（含 4 个暗色系）
  WallpaperIndex      int                              当前内置壁纸索引；自定义图片时为 -1
  Theme               ThemeKind                        Light/Dark/System
  TimeFormat          string                           "24h"(默认) / "12h"
  DateFormat          string                           "yyyy/M/d"(默认)
  Language            string                           "zh-CN"(默认)
  Region              string                           "zh-CN"(默认)

  CurrentWallpaper      → 内置 Brush 或已下载的 ImageBrush (绑桌面背景)
  CurrentWallpaperKey   → "builtin:{key}" 或 "custom:{blobId}"（与服务端 DTO 对齐）
  TaskbarBackground     → 暗/亮主题对应任务栏底色
  TaskbarForeground     → 暗/亮主题对应前景
  IsDarkTheme           → Theme == Dark

  Apply(WorkspacePreferencesDto)   服务端偏好 → 本地活状态
  ToPreferences(defaultApps?)      本地活状态 → 服务端 DTO
  TrySetWallpaperKey(key)          按内置 key 设置壁纸
  SetCustomWallpaper(key, bitmap)  将下载的 Workspace 图片应用为壁纸
```

**主题最小可见效果**：`TaskbarBackground` / `TaskbarForeground` 随主题切换（亮=#F7F7F7 / 暗=#1F1F1F）。完整主题切换（控件样式）为后续演进项。

### 5.4 偏好同步（`PreferencesSync`，单例）

监听 `IAuthSession.StateChanged`：

```text
Authenticated   → LoadIfAuthenticatedAsync: GET 偏好 → ShellSettings.Apply + DefaultAppRegistry.SetMappings
Unauthenticated → ShellSettings.Apply(Default) + DefaultAppRegistry.SetMappings(Default)
```

构造时若已认证（桌面外壳可能在登录后才构造本服务）立即加载。`Bootstrapper` 末尾 `provider.GetRequiredService<PreferencesSync>()` 急切实例化，确保捕获登录事件。

### 5.5 默认程序注册表（`DefaultAppRegistry`，单例）

持有当前 Workspace 的 `scheme/ext → appId` 映射（`ConcurrentDictionary`，scheme 不区分大小写）：

- `SetMappings(IEnumerable<DefaultAppMappingDto>?)`：用服务端 DTO 覆盖（`PreferencesSync` 登录加载 + `SettingsViewModel.Save` 编辑保存时调用）。
- `Resolve(string schemeOrExt)`：查询某 scheme/扩展名对应的应用 Id（供启动路由用，未配置返回 null）。
- `Snapshot`：当前映射只读快照。

> **已接入文件扩展名与第三方 URI scheme 路由**：RemoteExplorer 双击文件时调用 `Resolve(extension)`，但只接受仍实现 `IFileOpenApplication` 且声明该扩展名的应用；无有效关联时从兼容应用中回退选择。“打开方式”可修改此关联并保存到 Workspace。对于如 `help://guide/docker/install?lang=en` 的 URI，Runtime 只会选择在 manifest 中显式声明该 scheme 且接受该 URI 的处理程序；默认映射只能指向此候选集。没有默认映射时，唯一候选会直接打开，多个候选由 Shell 显示选择对话框，且可保存为 Workspace 默认程序。

### 5.6 桌面外壳时钟集成（`DesktopShellViewModel.StartClock`）

任务栏时钟每秒 tick，按 `ShellSettings` 当前偏好格式化：

```csharp
var culture = SafeCulture(_settings.Language);                    // 语言 culture
var timeFmt = _settings.TimeFormat == "12h" ? "h:mm tt" : "HH:mm"; // 12/24h
Clock = now.ToString(timeFmt, culture);
var dateFmt = string.IsNullOrWhiteSpace(_settings.DateFormat) ? "M/d ddd" : _settings.DateFormat;
DateText = now.ToString(dateFmt, culture);
```

`ShellSettings` 属性变化时（设置应用编辑或 `PreferencesSync` 加载）即时生效——`DispatcherTimer` 每 tick 读最新值。

### 5.7 视图（`Apps/Settings/Views/`）

- `SettingsView.axaml`：左 220px 导航 ListBox（glyph + 显示名）+ 右 `ScrollViewer` 包 `ContentControl`（绑 `SelectedPage`）。5 个 `DataTemplate` 在 `UserControl.DataTemplates`（非 `UserControl.Resources`——Avalonia 12.1 DataTemplate 需放 `DataTemplates` 集合，放 Resources 报 AVLN3000）。
- `Views/Pages/`：5 个 `UserControl`，各页根声明 `x:DataType="vm:XxxPageViewModel"`（编译绑定 AVLN2100 要求）。

---

## 6. 数据流

### 6.1 登录加载流

```text
IAuthSession.StateChanged(Authenticated)
    ↓
PreferencesSync.LoadIfAuthenticatedAsync
    ├── GET /api/v1.0/workspaces/{id}/preferences (JWT)
    ├── ShellSettings.Apply(prefs)           → 桌面外壳即时生效（壁纸/主题/时钟格式）
    └── DefaultAppRegistry.SetMappings(prefs.DefaultApps)
```

### 6.2 编辑保存流（防抖）

```text
用户在 Settings 页改某项（如选 Dark 主题）
    ↓
PageViewModel.Setter → ShellSettings.Theme = Dark   → 桌面外壳即时生效
    ↓
PageViewModel.Save() → SettingsViewModel.Save()
    ├── DefaultAppRegistry.SetMappings(当前 Mappings)  ← Explorer 文件打开立即读到最新关联
    └── 防抖 300ms（取消上次未发请求）
          ↓
          SettingsViewModel.SaveAsync
            ├── prefs = ShellSettings.ToPreferences(mappings)
            └── PUT /api/v1.0/workspaces/{id}/preferences (JWT)
                  └── 失败保留本地值，后续改动可重试
```

### 6.3 登出重置流

```text
IAuthSession.StateChanged(Unauthenticated)
    ↓
PreferencesSync.OnStateChanged
    ├── ShellSettings.Apply(WorkspacePreferencesDto.Default)  → 桌面回默认外观
    └── DefaultAppRegistry.SetMappings(Default.DefaultApps)
```

---

## 7. 关键技术坑

1. **DataTemplate 放 `UserControl.DataTemplates` 非 `Resources`**：Avalonia 12.1 的 `DataTemplate` 必须放在 `UserControl.DataTemplates` 集合（XAML 元素 `<UserControl.DataTemplates>`）。放在 `UserControl.Resources` 报 AVLN3000（XAML 解析器找不到匹配 setter）。详见 `SettingsView.axaml`。
2. **页 UserControl 根声明 `x:DataType`**：Avalonia 12.1 编译绑定要求每个 `UserControl` 根声明 `x:DataType="vm:XxxPageViewModel"`，否则 `{Binding}` 报 AVLN2100。
3. **`ShellSettings` 透传通知**：页 VM 基类 `SettingsPageViewModel` 订阅 `Settings.PropertyChanged` 重发同名通知。外部加载（`PreferencesSync` 改 Settings）时页 VM 视图即刻刷新，避免编辑后服务端加载覆盖显示脏数据。
4. **防抖保存**：`SettingsViewModel.Save` 用 `CancellationTokenSource` 取消上次未发请求，避免连续编辑触发多次 PUT。`Save` 内 `await Task.Delay(300, ct)` 被 catch `OperationCanceledException` 静默退出。
5. **`InitializeAsync` 防重入**：`_initialized` 标志位防止窗口多次 Activate 重复加载。初始化 `Apply` 期间 `Save` 因 `_initialized==false` 不保存（避免 Apply 触发的 PropertyChanged 回写服务端）。
6. **`DefaultAppMappingViewModel.SelectedApp` 双向同步**：ComboBox `SelectedItem` 绑 `SelectedApp`（`AppOption?`），setter 写回 `AppId`；`OnAppIdChanged` 时 `OnPropertyChanged(nameof(SelectedApp))` 让 ComboBox 跟随外部 `AppId` 变化（如 `SetMappings` 填充）。
7. **路由常量 `{id}` 替换**：`WorkspaceApiRoutes.Preferences` 含 `{id}` 占位符，客户端 `SettingsClient.SendAsync` 用 `workspaceId.ToString("D")` 替换。禁止硬编码 URL 字符串。
8. **时区 / 网络只读**：宿主 OS 时区切换（Linux `timedatectl` / Windows 时区设置）与网卡配置需 sudo/UAC 提权（硬约束「权限提升委托宿主 OS」），Settings 仅只读展示宿主时区 + Client→Server 连接状态，不触及宿主 OS 配置。
9. **扩展名关联必须受 manifest 约束**：可选扩展名从所有已注册应用的 `SupportedFileExtensions` 聚合；扩展名映射的下拉项只显示声明支持该扩展名的应用。Explorer 对精确文件名和无扩展名文件也会依据应用的路径规则再次验证，历史遗留的错误关联不会把文件交给不兼容应用。
10. **`AvailableApps.FirstOrDefault()?.Id`**：`AppsPageViewModel.AddMapping` 用 `?.` 防 `FirstOrDefault()` 返回 null 时 NRE（`AvailableApps` 为 `IReadOnlyList<AppOption>` 引用类型，可能空）。

---

## 8. 后续演进

- **完整主题切换**：当前仅任务栏底色随主题切换。后续接入 `RemoteOS.UI` 的 Light/Dark 样式切换（控件级主题）。
- **自定义壁纸**：支持从本机选择 PNG/JPEG/WebP/GIF（最大 10 MB）。服务端以 Workspace 私有 blob 保存，偏好仅保存 `custom:{blobId}`；同 Workspace 的其他设备登录时按需下载并渲染。默认不读取、不同步或修改宿主 OS 的壁纸。
- **URI scheme 自动路由**：第三方 manifest 已声明的 scheme 通过 `DefaultAppRegistry.Resolve` 选择默认程序。`http`/`https`/`mailto` 等内置链接入口仍需在各自控件接入此 activation 流程。
- **更多语言资源**：当前语言切换仅影响时钟格式化 culture。后续接入 i18n 资源文件，UI 文案随语言切换。
- **区域格式化**：当前区域仅存储未深度应用。后续按区域格式化数字 / 货币 / 首日星期。
- **通知中心 / 声音 / 显示**：当前未含。后续按需新增分类页。
- **偏好变更广播**：当前多设备偏好同步靠重新登录拉取。后续经 SignalR Hub 广播偏好变更，多设备实时同步。

---

## 9. AI Agent Rules

> 实现与维护本模块时必须遵守的规则。

1. **偏好真源在 Server Workspace**：用户偏好（壁纸/主题/时间格式/日期格式/语言/区域/默认程序）持久化到 `Workspace.Preferences`（`OwnsOne + ToJson("preferences")` 单列 JSON），多设备登录同一 Workspace 共享。禁止在客户端本地独立持久化偏好（`ShellSettings` 是活副本，非真源）。
2. **复用 `TerminalSettings` / `BrowserSettings` 同模式**：新增 Workspace 级偏好字段时，扩 `WorkspacePreferencesDto`（JSON 列内增字段，无需改 schema）；领域模型 `Workspace` 加属性 + DbContext `OwnsOne+ToJson`。禁止为偏好新建独立表。
3. **复用 `IAuthSession` JWT**：`ISettingsClient` 不持有独立凭据；未登录时设置仍可打开（沿用 `ShellSettings` 默认值，不持久化）。`SettingsClient` 从 `IAuthSession.ServerUrl` + `Tokens.AccessToken` 构造请求。
4. **不 mutate `HttpClient.BaseAddress`**：每请求用绝对 URI（避免共享 typed HttpClient 实例并发竞态），与 `BrowserClient` / `ExplorerClient` 同模式。
5. **路由常量共用 `WorkspaceApiRoutes.Preferences`**：Server 注册路由与 Client 拼接 URL 必须用同一常量，`{id}` 用 `workspaceId.ToString("D")` 替换，禁止硬编码字符串。
6. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），JSON 用 `RemoteOsJsonOptions.Default`。
7. **按 Workspace 归属校验**：Server 端复用 `FindAuthorizedWorkspace`（按 JWT `sub` claim 校验 workspace.UserId == userId），禁止跨用户读写偏好。
8. **`ShellSettings` 是实时 UI 绑定源**：页 VM 透传读写 `ShellSettings`（即时反映到桌面外壳），`Apply(WorkspacePreferencesDto)` 服务端→本地，`ToPreferences(defaultApps)` 本地→服务端。外部加载改 Settings 时通过 `SettingsPageViewModel` 透传通知刷新视图。
9. **防抖保存**：`SettingsViewModel.Save` 用 `CancellationTokenSource` 取消上次未发请求（300ms 防抖），连续编辑只触发一次 PUT。初始化 `Apply` 期间 `_initialized==false` 不保存。
10. **宿主 OS 级设置只读**：时区切换 / 网卡配置 / DNS 等需 sudo/UAC 提权（硬约束「权限提升委托宿主 OS」），Settings 仅只读展示，不触及宿主 OS 配置。
11. **DataTemplate 放 `UserControl.DataTemplates`**：Avalonia 12.1 的 `DataTemplate` 必须放 `UserControl.DataTemplates` 集合（非 `UserControl.Resources`，否则 AVLN3000）。页 UserControl 根必须声明 `x:DataType="vm:XxxPageViewModel"`（编译绑定 AVLN2100）。
12. **既有库增量补齐**：新增 `preferences` 列时，`Program.cs` 启动时检测 `pragma_table_info('workspaces')`，缺失则 `ALTER TABLE "workspaces" ADD COLUMN "preferences" TEXT NULL;`（与 `browser_settings` 列同模式）。列允许 NULL——EF Core `OwnsOne+ToJson` 读取 NULL 时回退领域模型默认值。`EnsureCreated` 不为已存在 db 追加列。
13. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi / SQLitePCLRaw.lib.e_sqlite3 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
