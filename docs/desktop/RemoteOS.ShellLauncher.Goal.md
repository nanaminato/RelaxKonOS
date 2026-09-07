# RemoteOS 可插拔桌面 Shell（Launcher）扩展：Goal 执行文档

> **状态：已实施（2026-09）。** 本文是本次扩展的唯一 Goal 模式执行规范。目标是把当前仅改变外框的 Shell 切换，升级为类似 Android Launcher 的完整桌面呈现切换：Shell 自己渲染桌面、应用启动入口、任务栏/停靠栏和系统弹层，同时继续复用 RemoteOS 的应用运行时、窗口管理器、会话与权限边界。
>
> 相关现状：[桌面外壳](./RemoteOS.Desktop.md)、[设置](./RemoteOS.Settings.md)、[架构](../architecture/RemoteOS.Architecture.md)。
>
> 外部包作者指南与 Windows 11 风格可构建示例见 [RemoteOS.ExternalShellPackages.md](./RemoteOS.ExternalShellPackages.md) 和 `examples/Windows11DesktopShell`。

---

## Goal

将 Shell 从“`DesktopShellView` 外的一层装饰”演进为“RemoteOS 桌面的主渲染者”。切换 Windows-like、macOS-like、Ubuntu-like 时，用户应立即看见不同的：

- 桌面背景、图标布局、选择态及桌面右键菜单；
- 应用启动器（开始菜单、Dock 或应用概览）；
- 运行中应用的任务栏/停靠栏/顶部栏，以及最小化、恢复、聚焦、多窗口预览；
- 时钟、系统状态、通知与 Shell 级弹层的摆放和视觉样式；
- 应用窗口的可用工作区（不得被 Shell 自己的任务栏遮挡）。

Shell **不**取得应用进程、窗口 z-order、模态链、认证或远程文件系统的真源。它是呈现和意图入口；`ApplicationManager`、`IWindowManager`、`DesktopShellViewModel` 已拆出的领域服务仍是唯一真源。

“外部桌面”在本 Goal 中指第三方/独立发布的 **Shell 扩展包**，在 RemoteOS 窗口内运行并遵守同一接口；它不替换 Windows Explorer、macOS Finder 或宿主 OS 的实际桌面。后者需要系统级权限、平台特定安装和进程生命周期管理，不属于 RemoteOS 的模拟范围。

---

## 1. 已确认的现状与问题

当前实现路径如下：

```text
PersonalizationPageViewModel.SelectedShellId
  → ShellSettings.ShellSelectionChanged
  → ShellSession.SwitchToAsync
  → ShellDefinition.CreateView()
  → ShellHost.Content
```

`ShellSession.Definitions` 中 `windows-like`、`macos-like`、`ubuntu-like` 分别只构造一个 `Border`，其子控件始终是新的 `DesktopShellView`。因此桌面、开始菜单、任务栏、任务栏窗口分组、桌面文件操作及 `WindowManager.Attach(Canvas)` 都仍在同一份 `DesktopShellView` 中。现有注释也明确这些是“style-only”而不是 OS 仿真。

这个设计有三个根本问题：

1. Shell 没有桌面/任务栏的渲染责任，无法形成可辨认的桌面范式。
2. 每次切换都销毁并新建 `DesktopShellView`；窗口宿主与 Shell 生命周期耦合，无法安全支持不同布局的窗口工作区。
3. Shell 列表是硬编码白名单，`WorkspacePreferencesDto.ShellId`、服务端校验和本机 `ShellPreferenceStore` 都无法表达第三方 Shell。

---

## 2. 目标架构

### 2.1 职责边界

```text
MainWindow（宿主窗口控制、连接栏、退出）
  └─ ShellRuntime（选择、加载、切换、回退、生命周期）
       ├─ IDesktopShell（具体 Shell：RemoteOS / Windows-like / macOS-like / Ubuntu-like / 外部包）
       │    ├─ 自己的桌面层、启动器、任务栏/Dock、Shell 弹层
       │    └─ ShellWindowSurface（受管窗口和系统模态的唯一视觉挂载点）
       └─ ShellStateStore（只读状态投影） + IShellActions（受控命令）
                 │
                 ├─ ApplicationManager（启动/激活应用真源）
                 ├─ IWindowManager（窗口、焦点、z-order、模态真源）
                 ├─ DesktopShellViewModel 拆出的 DesktopWorkspaceService
                 └─ ShellSettings / IAuthSession / 文件与快捷方式服务
```

关键原则：

- `IDesktopShell` 只负责自己的视觉树和交互编排，不能直接修改 `ManagedWindow` 状态或访问窗口宿主 `Canvas.Children`。
- `IWindowManager` 只挂载到当前 Shell 暴露的 `WindowSurface`；Shell 改变时重新挂载同一窗口真源，已经打开的应用、窗口、模态关系及状态不丢失。
- Shell 的“工作区矩形”必须由其布局实时报告给窗口管理器。任务栏/Dock 处于底部、顶部或侧边时，最大化窗口只能占用剩余区域。
- Shell 弹出的“打开方式”“桌面显示设置”等桌面级对话框统一经 `IWindowManager.ShowShellDialogAsync`；Shell 只提供 overlay surface，不自行复制模态机制。
- Shell 命令均以 `IShellActions` 调用；外部 Shell 不取得服务端 Token、`IServiceProvider` 或任意文件路径访问权。

### 2.2 领域状态与 UI 状态拆分

将当前过大的 `DesktopShellViewModel` 拆为不带 Avalonia 控件引用的服务与可绑定状态。所有 Shell 使用同一份状态投影，区别只在如何呈现。

| 新类型 | 责任 | 来源/替代 |
|---|---|---|
| `DesktopWorkspaceState` | 壁纸、桌面条目、选择项、时钟、任务栏窗口分组、启动器搜索结果 | 从 `DesktopShellViewModel` 提取，`ObservableObject` |
| `IDesktopWorkspaceService` | 刷新桌面、桌面文件/快捷方式操作、应用目录、首次设置 | 迁移现有命令的领域逻辑 |
| `IShellActions` | 对状态进行受控操作：启动、打开、复制/粘贴、窗口聚焦/最小化/关闭、显示桌面、打开系统页 | `DesktopShellViewModel` 的命令入口 |
| `IShellOverlayService` | 打开 Shell 级确认、打开方式、属性、桌面显示设置和启动器外弹层 | 当前 `DesktopShellView.axaml.cs` 的回调装配 |
| `ShellStateStore` | 向 Shell 提供只读 `DesktopWorkspaceState`，并发布状态变化 | 新增，防止 Shell 反向拥有领域数据 |

保留 `DesktopShellViewModel` 仅作为过渡适配层；第二阶段后删除它对具体 AXAML 事件与 `Canvas` 的依赖。不得把一个 Shell 的控件、菜单项或 `ContextMenu` 引用塞回共享状态。

---

## 3. 必须新增/修改的接口

以下契约放入新的、无 Client UI 实现依赖的 `Framework/RemoteOS.Shell` 项目（可被 Client 和外部扩展引用）。其 DTO/标识只使用 `RemoteOS.Protocol` 与 `RemoteOS.Core`；Avalonia 控件接口留在 `Client` 的适配层，避免外部包得到应用服务定位入口。

### 3.1 Shell 描述与来源

```csharp
public enum ShellSourceKind { BuiltIn, ExternalPackage }

public sealed record ShellDescriptor(
    string Id,                 // 例：remoteos.windows-like；外部：com.example.shell
    string DisplayName,
    string Version,
    ShellSourceKind Source,
    ShellCapabilities Capabilities,
    string? PackageId = null);

[Flags]
public enum ShellCapabilities
{
    None = 0,
    Desktop = 1,
    AppLauncher = 2,
    RunningApps = 4,
    ShellOverlays = 8,
    All = Desktop | AppLauncher | RunningApps | ShellOverlays,
}

public interface IShellCatalog
{
    IReadOnlyList<ShellDescriptor> Available { get; }
    event EventHandler? Changed;
    bool TryGet(string id, out ShellDescriptor descriptor);
}
```

内置 ID 使用稳定命名空间：`remoteos.windows-like`、`remoteos.macos-like`、`remoteos.ubuntu-like`。Windows 风格为默认值；读取已移除的 `remoteos`、`remoteos.default` 以及旧值 `windows-like`、`macos-like`、`ubuntu-like` 时在客户端归一化到对应的新 ID；服务端在迁移期同时接受旧值，写回时只写新值。

### 3.2 Shell 运行时接口

```csharp
public interface IDesktopShell : IAsyncDisposable
{
    ShellDescriptor Descriptor { get; }
    Control View { get; }  // 仅 Client 侧适配接口；外部包返回自己的根 UserControl

    Task InitializeAsync(ShellPresentationContext context, CancellationToken cancellationToken);
    Task ActivateAsync(CancellationToken cancellationToken);
    Task DeactivateAsync(CancellationToken cancellationToken);
}

public sealed record ShellPresentationContext(
    ShellStateStore State,
    IShellActions Actions,
    IShellOverlayService Overlays,
    IShellSurfaceRegistry Surfaces,
    ILocalizationSnapshot Localization);

public interface ILocalizationSnapshot
{
    string Language { get; }
    string Get(string key, string fallback); // 仅宿主术语
    event EventHandler<ShellLanguageChangedEventArgs>? LanguageChanged;
}

public interface IShellSurfaceRegistry
{
    void Register(ShellSurfaces surfaces);
    void UpdateWorkArea(Rect workArea);
    void Clear();
}

public sealed record ShellSurfaces(
    Canvas WindowHost,
    Canvas FullScreenWindowHost,
    Panel ShellOverlayHost,
    Control InputBackdrop);
```

`Register` 只能在 `InitializeAsync` 结束前及 Shell 已激活时调用；每个 Shell 只可登记一次完整 surface 集合。`ShellRuntime` 验证非空、同一 visual tree、无重复登记后才允许 `IWindowManager.Attach`。`InputBackdrop` 用于桌面空白点击、选择框和右键菜单定位，不能遮住窗口宿主。

### 3.3 用户操作接口

外部与内置 Shell 都只获得此能力集。操作必须带有稳定 ID，不能传 Avalonia 控件或任意委托。

```csharp
public interface IShellActions
{
    Task LaunchAsync(AppId appId, CancellationToken cancellationToken = default);
    Task OpenDesktopEntryAsync(string entryId, CancellationToken cancellationToken = default);
    Task RefreshDesktopAsync(CancellationToken cancellationToken = default);
    void ClearDesktopSelection();
    void SelectDesktopEntry(string entryId);
    void ShowDesktop();
    void ToggleWindowGroup(AppId appId);
    void ActivateWindow(WindowId windowId);
    void MinimizeWindow(WindowId windowId);
    void CloseWindow(WindowId windowId);
    void OpenSettings(SettingsRoute route);
}
```

`DesktopWorkspaceState` 对 Shell 暴露的条目必须包含不可猜测的 `EntryId`、显示名、种类、图标引用、是否已选中及允许操作；实际远程路径仅保留在 workspace service 内部。对文件右键菜单，Shell 发送 `EntryId + DesktopEntryAction`，由服务统一鉴权和执行。

### 3.4 工作区与切换事务

`ShellRuntime.SwitchAsync(shellId)` 必须是串行的事务，禁止由 `ShellSettings.PropertyChanged` fire-and-forget 并发重入：

```text
校验 catalog 中存在且可用
  → 创建候选 Shell 并 Initialize
  → 候选登记 surfaces，验证成功
  → 暂停旧 Shell 输入；WindowManager 从旧 WindowHost 脱离
  → 将候选 View 置入 MainWindow.ShellHost
  → WindowManager 挂到候选 WindowHost，应用候选 WorkArea / fullscreen host
  → 激活候选 Shell，提交 activeShellId 与持久化
  → 停止并释放旧 Shell
```

在上述任一步失败时：移除候选视图、释放候选、重新挂回旧 surface；如果旧 surface 已不可用则启动 `remoteos.windows-like`。切换期间应显示宿主级不可交互遮罩，且不得关闭、最小化或重建现有 `ManagedWindow`。

`IShellSurfaceRegistry.UpdateWorkArea` 在布局变化（窗口 resize、任务栏自动隐藏、Dock 位置变动）后节流到 UI tick 调用；运行时把矩形转给 `IWindowManager.SetHostBounds`。全屏应用仍使用 `FullScreenWindowHost` 覆盖整个 Shell，不受普通工作区约束。

---

## 4. 内置 Shell 的最低可见行为

四个内置 Shell 必须是独立的 `IDesktopShell` 实现和独立 AXAML/样式资源，**禁止**以 `Border(Child = new DesktopShellView())` 实现。

| Shell | 桌面与启动器 | 运行中应用 | 工作区 |
|---|---|---|---|
| RemoteOS Default | 当前图标网格 + 开始菜单 | 底部任务栏，保留任务组预览 | 底部栏上方 |
| Windows-like | 左对齐图标、开始面板/搜索、右键菜单 | 底部任务栏：开始、已固定项、窗口组、托盘/时钟 | 底部栏上方 |
| macOS-like | 稀疏桌面图标、Launchpad 风格应用概览 | 底部居中 Dock：运行指示、多窗口弹出预览 | Dock 不自动隐藏时从底部扣除 |
| Ubuntu-like | GNOME 风格背景和应用概览 | 顶部状态栏 + 左侧 Dock（可自动隐藏） | 顶部栏和可见 Dock 之外 |

首期允许四个 Shell 复用 `DesktopWorkspaceState`、通用图标控件、文件上下文菜单的行动模型及统一的窗口缩略图数据；不得复用同一整棵 `DesktopShellView`。视觉“像某系统”只使用通用交互范式与自有资源，不复制厂商图标、壁纸、商标或系统资源。

Shell 必须处理的最小交互：

- 桌面条目单击选择、双击打开、空白单击清除选择、右键菜单；
- 启动内置/外部应用，打开设置的个性化页面；
- 显示每个非模态应用窗口组；单窗口最小化/还原/聚焦，多窗口预览并可关闭指定窗口；
- “显示桌面”仅最小化非最小化窗口；
- 时钟按 `ShellSettings` 的语言、日期和 12/24 小时偏好实时更新；
- 窗口最大化不覆盖非自动隐藏的 Shell 栏；模态和窗口全屏语义与当前 `WindowManager` 一致。

---

## 5. 外部 Shell 扩展包

### 5.1 支持模型

外部 Shell 是安装在本机、由用户显式启用的 .NET/Avalonia 扩展包。v1 使用受控的 `AssemblyLoadContext` 在独立目录加载；外部 Shell 与 Client 必须引用同一个版本化 `RemoteOS.Shell.Abstractions`，不允许反射访问 Client 私有类型。

包结构：

```text
<package-root>/
  manifest.json
  lib/<target-framework>/<publisher>.Shell.dll
  lib/<target-framework>/Localization/*.json
  assets/...
```

`manifest.json`：

```json
{
  "schemaVersion": 1,
  "permissionModelVersion": 2,
  "packageType": "desktopShell",
  "id": "com.example.windows11-desktop",
  "displayName": "Windows 11 Desktop (External)",
  "version": "1.0.0",
  "entryAssembly": "lib/net10.0/Example.Windows11DesktopShell.dll",
  "entryType": "Example.Windows11DesktopShell.Windows11ShellFactory",
  "shellApiVersion": "1.0",
  "capabilities": ["desktop", "shellOverlays"]
}
```

入口类型实现：

```csharp
public interface IDesktopShellFactory
{
    ShellDescriptor Descriptor { get; }
    IDesktopShell Create();
}
```

外部 Shell 与第三方应用统一封装为 `.roapp`，只能通过应用安装程序进入版本化软件包目录；个性化页面不提供目录安装入口。安装器验证 `permissionModelVersion: 2`、安全相对路径和清单必填字段，`ShellCatalog` 再验证 `packageType`、Shell API 版本与能力组合。不得根据 Workspace 偏好自动下载、加载网络 DLL 或执行脚本。

### 5.2 生命周期、故障隔离与卸载

- 应用安装程序先验证并登记软件包；`ShellCatalog` 再解析 manifest，检查桌面包类型、ID 格式、API 版本和能力组合，不执行程序集即可列出不可用原因。
- 只有用户选择该 Shell 时才加载程序集和创建实例。初始化超时、抛异常或未登记有效 surfaces 时，记录诊断，拒绝激活，并回退到当前 Shell。
- `DeactivateAsync` / `DisposeAsync` 失败不得阻塞回退。`AssemblyLoadContext` 在没有可达对象时卸载；若无法卸载，标记“需重启才能完成卸载”，不能破坏当前桌面。
- 切换中的外部 Shell 崩溃必须保留现有应用窗口；运行时立即回退到 `remoteos.windows-like` 并向用户显示可复制的诊断 ID。
- 外部 Shell 无权添加应用、读取 Token、调用网络或访问 VSD；任何将来要开放的能力必须以独立、可授权接口加入，不通过 `IServiceProvider` 旁路。

### 5.3 偏好同步规则

`WorkspacePreferencesDto` 增加结构化选择，替代只存 `ShellId`：

```csharp
public sealed record ShellSelectionDto(
    string ShellId,
    string? PackageId = null,
    string? PackageVersion = null);
```

`WorkspacePreferencesDto.Shell` 为跨设备的**意图**；本机 `ShellPreferenceStore` 保存最后一次可成功激活的选择和包解析结果。登录时：

1. 读取 Workspace 选择；本机有兼容包则激活它。
2. 包不存在、版本/API 不兼容或被禁用时，激活 `remoteos.windows-like`，但不覆盖服务端选择。
3. 在设置页显示“此设备未安装/不可用”，提供安装或切换到已安装 Shell 的入口。
4. 用户主动选择另一个 Shell 后才更新 Workspace 偏好；不要用设备回退结果覆盖其他设备的偏好。

兼容期保留 JSON `shellId` 的读取与写入映射一版；下一次主要协议版本才删除。服务端校验从固定字符串白名单改为：内置 ID 白名单或符合 `^[a-z0-9][a-z0-9.-]{2,127}$` 的外部 ID，字段长度、版本字符串和包 ID 均限长。服务端不验证扩展是否真实安装，因为它是设备本地事实。

---

## 6. 必改文件与新增文件

| 区域 | 变更 |
|---|---|
| `Framework/RemoteOS.Shell/`（新增） | `ShellDescriptor`、`IDesktopShell`、factory/catalog、state/command/overlay/surface 抽象及版本常量 |
| `Client/RemoteOS.Client/Services/ShellRuntime.cs`（替代 `ShellSession`） | 串行切换事务、回退、WindowManager 重新挂载、工作区更新、诊断 |
| `Client/.../Services/ShellCatalog.cs` | 内置注册、外部 manifest 发现、包校验和可用性状态 |
| `Client/.../Services/DesktopWorkspaceService.cs` | 从 `DesktopShellViewModel` 抽离桌面文件、快捷方式、应用目录、选择和任务栏组领域逻辑 |
| `Client/.../Views/Shell/` | 新建四套真正的 Shell 根视图及共享小部件；删除 style-only `WindowsLikeShellView`/`MacosLikeShellView`/`UbuntuLikeShellView` |
| `Client/.../Views/MainWindow.axaml(.cs)` | `ShellHost` 仍为唯一 Shell 根；保留宿主标题栏/连接栏；挂接 `ShellRuntime` 而非旧 `ShellSession` |
| `Client/.../ViewModels/Shell/DesktopShellViewModel.cs` | 分阶段缩小至状态适配器，最终删除视图专属回调/命令 |
| `Client/.../Services/ShellSettings.cs` 与 `VirtualSystemDrive/ShellPreferenceStore.cs` | 使用 `ShellSelectionDto`、迁移旧 ID、区分 Workspace 意图和本机可用回退 |
| `Shared/RemoteOS.Protocol/Workspace/WorkspacePreferencesDto.cs` | 增加 `ShellSelectionDto`，保留旧 `ShellId` 的兼容读取 |
| `RemoteOS.Server/Endpoints/WorkspaceEndpoints.cs` | 外部 ID 和选择 DTO 的归一化校验，移除固定四项白名单 |
| `Client/.../Apps/Settings/...Personalization...` | Shell 目录、来源/版本/不可用原因、安装/卸载/启用以及外部包风险提示 |
| `docs/desktop/RemoteOS.Desktop.md`、`RemoteOS.Settings.md` | 实施完成后改为链接本文并同步实际契约，不让旧的“style-only”说明继续有效 |

---

## 7. 分阶段执行计划

### Phase 0 — 契约与迁移护栏

1. 新建 `RemoteOS.Shell.Abstractions` / `RemoteOS.Shell` 项目，写入 §3 的最小接口和 API 版本常量。
2. 将旧四个 Shell ID 映射到新内置 ID；为 `ShellSelectionDto` 增加 JSON 兼容读取。
3. 改造服务端偏好校验，允许合规的外部 ID，但不改变旧用户的行为。
4. 为切换、旧偏好归一化、未知/不可用 Shell 回退写单元测试。

**完成条件：** 不改变当前视觉效果；旧偏好、现有 Workspace 和无扩展安装的设备均可无数据丢失启动。

### Phase 1 — 运行时与稳定的窗口重挂载

1. 以 `ShellRuntime` 替代 `ShellSession`，加入切换互斥锁、候选初始化、回滚和诊断。
2. 让 `IWindowManager` 显式支持 `Detach`/`Attach(ShellSurfaces)`，并在重新挂载后恢复已有 `RemoteWindow` 视觉对象或安全重建其视图绑定。
3. 拆出共享 `DesktopWorkspaceState` / `IShellActions`；把 `DesktopShellView.axaml.cs` 中的 Shell 级对话框装配迁入 overlay service。
4. 先将当前 RemoteOS 默认桌面实现为第一个 `IDesktopShell`。

**完成条件：** 开着多个应用窗口、一个最小化窗口及一个模态对话框时，切换到默认 Shell 的新运行时后，窗口数量、焦点规则、内容和布局均保持；没有孤儿 Canvas、重复事件订阅或不可点击窗口。

### Phase 2 — 内置 Launcher 呈现

1. 分别实现 Windows-like、macOS-like、Ubuntu-like 的桌面/启动器/运行应用栏，而非共享 `DesktopShellView`。
2. 每个实现依据布局实时更新工作区，并完成 §4 的最低交互。
3. 添加窗口组预览共享模型，保证不同 Shell 都能对多窗口进行激活和关闭。
4. 对高 DPI、窄窗口、全屏连接栏和自动隐藏 Dock/任务栏进行手工可视化验收。

**完成条件：** 切换四个 Shell 后，任务栏/Dock、启动入口和桌面布局均有可见且可交互的差异；最大化窗口不覆盖常驻系统栏。

### Phase 3 — 外部 Shell 包

1. 完成 manifest、安装目录、catalog、校验、独立 `AssemblyLoadContext` 和诊断。
2. 设置页实现外部 Shell 的安装、启用、卸载/禁用和不可用状态；任何文件安装都需要用户明确选择。
3. 提供一个独立示例 Shell 包及开发文档，验证零 Client 私有 API 依赖。
4. 完成异常、超时、卸载和跨设备缺包回退测试。

**完成条件：** 可安装一个示例外部 Shell，启动并渲染完整桌面；其初始化失败或卸载后，RemoteOS 自动回退且正在运行的应用不丢失。

---

## 8. 验收矩阵

| 场景 | 预期结果 |
|---|---|
| 从 Settings 选择四个内置 Shell | 无重启立即切换；桌面、启动器、运行中应用栏均改变，不只是边框 |
| 切换前开三个窗口（含最小化） | 切换后三个窗口仍存在；最小化、焦点和任务栏分组正确 |
| 切换前存在应用模态对话框 | 模态链、遮罩和 Esc/关闭语义正确；不出现在任务栏普通组 |
| 最大化窗口 + 不同位置的任务栏/Dock | 窗口仅填充 Shell 上报工作区，不压住常驻栏 |
| Shell 自动隐藏栏显示/隐藏 | WorkArea 随布局更新，窗口不会抖动、失焦或被裁剪 |
| 桌面文件/快捷方式操作 | 所有 Shell 都可选中、打开、刷新和打开右键菜单；权限仍由统一 service 控制 |
| 外部 Shell 未安装的另一台设备登录 | 保持服务端选择不变，本机回退默认，并在 Settings 显示可修复原因 |
| 外部 Shell 初始化抛异常 | 记录诊断、保留现有窗口、回退默认 Shell；无白屏或崩溃 |
| 旧 `shellId: "windows-like"` 偏好 | 自动迁移并落到新的 Windows-like Launcher |

---

## 9. 禁止项与风险控制

- 禁止继续以 `Border` 包裹 `DesktopShellView` 伪造新 Shell。
- 禁止让 Shell 直接操作 `Canvas.Children`、`ManagedWindow` 内部状态、应用服务容器或认证令牌。
- 禁止在切换中关闭/重新启动应用，或用“重置窗口管理器”规避重挂载问题。
- 禁止把外部 DLL 或资源 URL 写入 Workspace 偏好并在登录时自动下载/执行。
- 禁止把“支持外部桌面”误实现为替换宿主 OS Explorer/桌面；RemoteOS 始终只控制自己的主窗口内容。
- 首期不做跨 Shell 的独立图标布局持久化和每 Shell 独立的固定应用列表；这些应在基础架构稳定后，作为 `ShellSettings` 的按 Shell 命名空间偏好扩展。

---

## 10. Goal 模式执行提示

以下文本可直接作为 Goal 模式目标：

> 实施 `docs/desktop/RemoteOS.ShellLauncher.Goal.md`。把当前 style-only Shell 切换重构为可插拔 `IDesktopShell` Launcher 架构：每个内置 Shell 必须独立渲染桌面、启动器和任务栏/Dock，并通过受控 Shell 接口复用现有应用、窗口、模态和文件操作真源。实现可事务回滚的 ShellRuntime，使已打开窗口在切换中存活且最大化工作区避让系统栏。扩展 Workspace 偏好以保存可兼容的内置/外部 Shell 选择，并支持本机安装、发现、校验、故障回退的外部 Shell 扩展包。严格执行本文接口、边界、阶段和验收矩阵；每一阶段运行对应测试与构建，更新相关设计文档，并不要实现宿主 OS 的实际 shell replacement。
