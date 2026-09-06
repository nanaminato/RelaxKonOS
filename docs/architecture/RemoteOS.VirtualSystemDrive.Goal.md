# RemoteOS 虚拟系统盘、应用目录与可切换 Shell（Goal 执行版）

> 状态：**可实施，按阶段推进**  
> 建立日期：2026-09-06  
> 适用范围：`RemoteOS.Client`、`RemoteOS.Runtime`、`RemoteOS.App.SDK`、现有 `.roapp` 开发包  
> 架构依据：[整体架构](./RemoteOS.Architecture.md)、[应用激活](./RemoteOS.ApplicationActivation.md)、[开发者模式](../development/RemoteOS.DeveloperMode.md)、[应用权限模型](../platform/RemoteOS.AppPermissionRefactor.Goal.md)

本文是后续 Goal 模式的执行基线。它将“客户端虚拟系统盘、目录发现的应用注册、受限自动化脚本和多桌面 Shell”拆分为可独立构建、验证及回滚的目标。本文中的边界、信任规则、目录约定和验收标准具约束性；实现开始前仍须检查当前 Solution 与关联 Goal 的实际状态。

## 1. 目标与产品边界

RemoteOS 在客户端本地应用数据目录维护一个**虚拟系统盘**（Virtual System Drive，以下简称 VSD）。它是 RemoteOS 的应用安装、快捷方式、Shell 资源和用户本地文件的组织模型，不是宿主操作系统的真实磁盘、注册表或用户目录，也不映射为真实 `C:`。

V1 必须形成以下闭环：

- 客户端首次启动时创建 VSD 基础目录，并为 Host 随附的内置应用补齐受控的应用描述文件。
- 启动时先发现和校验应用描述文件，再向 `ApplicationManager` 注册可启动应用；桌面、开始菜单、文件关联和 URI 激活继续只读取运行时注册表。
- 内置应用与外置 `.roapp` 安装包共享 Application Descriptor / Catalog 流程，但二者的信任与加载机制严格区分。
- 用户快捷方式可指向应用、RemoteOS 文件、目录或受限自动化脚本；桌面仅展示快捷方式和当前 Workspace 的远端桌面文件，不直接把安装目录当桌面。
- Shell 与运行时解耦，用户在设置中切换 Shell 后立即看到新桌面；已运行应用和 `WindowManager` 不被重建。
- 建立脚本自动化的最小受限模型和执行审计，但 V1 不提供任意本机命令执行。

V1 明确不包括：

- 把 VSD 映射为宿主真实 `C:`、劫持文件系统、模拟 Windows 注册表或向宿主 OS 写入应用快捷方式。
- 通过可编辑 JSON 把外置应用提升为内置应用，或允许描述文件任意指定宿主程序集/类型。
- 为第三方包提供进程隔离、OS 沙箱、包签名或发布者信任根；这些仍遵循现有 App 权限 Goal 的风险说明。
- 执行 PowerShell、`cmd.exe`、`sh -c`、任意可执行文件、任意 .NET 代码片段、任意 HTTP 请求或任意宿主自动化。
- 将 macOS、Windows、Ubuntu/GNOME 的视觉资产、布局和每一个交互细节在首版完全复刻；首版提供同一运行时之上的 Shell 风格实现。
- 将现有 Workspace 远端文件系统替换成本地 VSD。远端文件仍只能经 Explorer / Server Files API 访问。

## 2. 已有基线与改造原则

| 已有基础 | 当前行为 | 本 Goal 的处理 |
| --- | --- | --- |
| `ApplicationManifest` | 内置应用在代码构造；包应用来自包 `manifest.json` | 保持其作为标准运行时元数据；新增磁盘描述文件 DTO 与转换器。 |
| `ApplicationManager` | 保存注册表、启动、文件关联和 URI 激活 | 保持唯一运行时注册表；不让 Shell 或扫描器直接启动程序集。 |
| `Bootstrapper` | 直接枚举 `IRemoteApplication` 并 `RegisterBuiltIn` | 改为提供可信的 Built-in Factory Registry，再由 Catalog 注册。 |
| `DeveloperPackageManager` | `.roapp` 解压至 `developer-apps`，使用 `catalog.json` 记录并按需加载 | 迁移为 VSD 的外置应用安装提供者；安装包格式可继续使用 `.roapp`。 |
| `DesktopShellViewModel` | 同时承载 Shell 呈现、应用列表和桌面文件 | 拆出 Shell 无关状态与可替换 Shell 呈现层。 |
| `WindowManager` | 客户端唯一窗口状态管理者 | 不因 Shell 切换重建；Shell 只能请求布局和展示窗口。 |
| App 权限模型 | Host 门控正常 SDK 调用，非恶意代码沙箱 | 脚本、快捷方式和应用启动均必须沿用该事实与用户可见风险提示。 |

不可改变的架构规则：

1. `ApplicationManager` 是唯一的 launch registry；VSD Catalog 是持久化的发现与安装状态，不是第二套运行时。
2. 目录扫描、描述文件读取与 schema 校验不得执行外置程序集代码。
3. 只有 Host 编译进的映射能证明一个应用是 `BuiltIn`；任何 VSD 中的字段均不能改变来源身份。
4. `WindowManager` 仍是窗口位置、层级、焦点和生命周期的唯一真源；Shell 仅拥有桌面视觉与用户交互。
5. VSD 的路径必须经固定根目录和完整路径规范化校验；不得接受 `..`、符号链接逃逸、绝对包内路径或来自 UI 的任意安装目标。
6. 应用权限、Server 用户授权、Host Elevation 三层仍保持分离；脚本或快捷方式不绕过其中任意一层。

## 3. VSD 目录与数据归属

### 3.1 根目录

默认根目录为：

```text
{LocalApplicationData}/RemoteOS/SystemDrive
```

该目录为当前设备的本地数据，不参与 Server Workspace 同步。需要跨设备同步的用户偏好（默认 Shell、Shell 主题偏好、桌面快捷方式清单等）仍按现有 Workspace / Device 配置模型存储，VSD 只承载其在本机的物化与安装状态。

建议目录：

```text
SystemDrive/
  System/
    catalog.json                 # Host 写入的索引缓存；可由扫描重建
    associations.json            # 应用/文件/URI 的本地缓存，不替代 ApplicationManager
    automation-audit/            # 有上限且脱敏的本地执行摘要
  Programs/
    BuiltIn/
      remoteos.terminal/
        app.remoteos.json
      remoteos.explorer/
        app.remoteos.json
    External/
      com.example.hello/
        versions/<immutable-id>/
          app.remoteos.json
          lib/
          assets/
        current.json
  Shells/
    remoteos/
      shell.remoteos.json
    windows-like/
      shell.remoteos.json
  Users/
    <local-profile-id>/
      Desktop/
        Terminal.remoteos-link.json
        Project.remoteos-link.json
      Documents/
      Downloads/
      Scripts/
        Open Development Environment.remoteos-script.yaml
      AppData/
        <app-id>/
```

`System/catalog.json` 是可删除、可重建的加速索引，不得成为唯一事实来源；启动时发现结果与索引不一致时以经过验证的描述文件和目录状态为准。应用私有本地数据的清除继续走 `IAppDataManager`，不得让设置页面递归删除整个 VSD。

### 3.2 路径与更新约束

- VSD Root 由 Host 计算，外部包、脚本和 UI 都不能指定 Root。
- 所有持久化相对路径必须以 `/` 表示，落盘前再转换为平台分隔符。
- 外置应用每次安装写入一个新版本目录；不得覆盖当前正在加载的程序集目录。
- `current.json` 仅含 Host 生成的版本标识。它的目标必须是同一 AppId 下的现有版本目录，且没有路径穿越。
- 内置应用目录只有 descriptor，不能出现可由 descriptor 选择的本地 DLL 入口。
- 删除外置包是逻辑卸载（从 Catalog 注销、关闭其窗口、撤销可加载状态）；文件被锁定时允许下次启动清理，不报告为应用仍已安装。

## 4. 应用描述文件与可信来源

### 4.1 统一描述文件

文件名固定为 `app.remoteos.json`。它是应用被发现、显示和注册前所读取的描述，不等同于已加载应用对象。建议的 v1 schema：

```json
{
  "schemaVersion": 1,
  "id": "com.example.hello",
  "kind": "package",
  "displayName": "Hello",
  "version": "0.2.0",
  "description": "Example package",
  "icon": { "path": "assets/icon.png", "glyph": "👋" },
  "activation": {
    "entryAssembly": "lib/net10.0/Hello.dll",
    "entryType": "Example.HelloApp"
  },
  "requestedPermissions": ["server.files.read"],
  "supportedFileExtensions": [".hello"],
  "supportedUriSchemes": ["example-hello"],
  "instancePolicy": "SingleWindow",
  "clientPlatforms": ["windows", "linux"],
  "permissionModelVersion": 2
}
```

描述文件只表达应用请求，不能声明最终授权、`TrustLevel`、BuiltIn 身份、默认允许权限、Host Elevation 或任意服务接口。`ApplicationManifest` 继续承载当前运行时已经支持的字段；新的 descriptor 字段只有在明确映射并被校验后才可进入该模型。

### 4.2 内置应用 descriptor

内置应用 descriptor 使用 `kind: "builtin"` 和不可伪造的 `activation.builtinKey`，示例：

```json
{
  "schemaVersion": 1,
  "id": "remoteos.terminal",
  "kind": "builtin",
  "displayName": "Terminal",
  "version": "1.0.0",
  "activation": { "builtinKey": "terminal" },
  "requestedPermissions": [],
  "instancePolicy": "MultiWindow",
  "permissionModelVersion": 2
}
```

Host 维护 `IBuiltInApplicationFactoryRegistry`，它是 `builtinKey → IRemoteApplication factory + 固定 AppId` 的编译期映射。发现器必须同时验证：

- descriptor 位于 `Programs/BuiltIn/<app-id>/`；
- `id`、`builtinKey` 与 Host 映射完全一致；
- descriptor 没有包程序集入口；
- 由 Host 生成的受控字段与当前客户端版本匹配。

任一条件不满足时不得注册该 descriptor；Host 使用编译期元数据重建它并记录不含用户数据的诊断。内置 descriptor 允许作为可观察、可恢复的安装清单，但不是安全边界，也不是用户自定义配置文件。

### 4.3 外置包 descriptor 与 `.roapp`

`.roapp` 继续作为传输/安装归档。安装器在受控 staging 目录中读取旧包根目录 `manifest.json`，完成当前已有的路径遍历、ID、URI 和权限模型校验后：

1. 解压到 `Programs/External/<app-id>/versions/<immutable-id>/`；
2. 生成或规范化该版本目录的 `app.remoteos.json`；
3. 校验图标、程序集和 entry type 路径都在该版本目录中；
4. 原子写入 `current.json`；
5. 更新可重建 catalog，再向运行时注册 manifest metadata；
6. 仅在首次启动该应用时才加载其程序集。

首版可以把包 `manifest.json` 和 VSD descriptor 的公共字段保持一一映射；不得引入两套互相漂移的字段。安装失败、应用加载失败或新版本不兼容时必须保留上一个 `current` 版本，并提供明确的未启动状态。

### 4.4 Catalog 发现与注册管道

```text
VSD Bootstrap
  → 确保目录和内置 descriptor
  → 扫描 Programs/BuiltIn 与 Programs/External 的固定深度
  → 规范化路径、反序列化、schema 校验
  → 校验来源（BuiltIn Factory / External package layout）
  → 转换为 ApplicationManifest + AppIdentity
  → ApplicationManager.RegisterBuiltIn / Register
  → RegistryChanged
  → 当前 Shell 刷新应用入口
```

扫描器必须收集所有错误，而不是第一个损坏包就终止 Shell；每项错误至少包含 AppId（若可安全提取）、来源目录和稳定问题码。扫描结果不应包含 token、文件内容、完整用户目录或异常堆栈。

## 5. 快捷方式、文件与激活

用户桌面目录只存 `*.remoteos-link.json`，而不是应用 descriptor 的副本。建议模型：

```json
{
  "schemaVersion": 1,
  "id": "4b7e9a2a-1e4d-4b7b-9b88-1c8c3a2b0c12",
  "displayName": "Open development environment",
  "kind": "script",
  "target": "Scripts/Open Development Environment.remoteos-script.yaml",
  "icon": { "glyph": "⚡" }
}
```

允许的 `kind` 固定为：

| 类型 | target 语义 | 激活方式 |
| --- | --- | --- |
| `application` | 已注册的 AppId | `ApplicationManager.Launch` |
| `remote-file` | 受现有 Explorer 校验的远端路径 | `remoteos://file/open` 或打开方式流程 |
| `remote-folder` | 受现有 Explorer 校验的远端目录 | Shell-owned Explorer activation |
| `script` | 当前用户 `Scripts/` 下的相对文件 | `IAutomationRunner.RunAsync` |
| `uri` | 已验证的 `remoteos://` 或 manifest 声明 URI | `IAppActivationService.Activate` |

快捷方式不可保存任意本地绝对路径、程序集类型、命令行、HTTP URL 凭据或权限 grant。应用卸载后，指向该 AppId 的快捷方式保留为“目标不可用”，用户可删除、重定向或在应用重新安装后恢复；不得静默把它重定向给名称相似的应用。

## 6. Shell 抽象与即时切换

### 6.1 分层

将当前 `DesktopShellViewModel` 分解为：

```text
ShellSession（共享、长期存在）
  ├─ ApplicationManager / Catalog 观察
  ├─ WindowManager
  ├─ Desktop shortcut 与远端 Desktop 文件数据
  ├─ 默认应用、搜索、通知、全局快捷键
  └─ 当前 Workspace / 设备偏好

IShellDefinition（被发现的 Shell 元数据）
  ├─ id、显示名、预览资源、支持的功能
  └─ 创建 IShellController / View 的受控工厂

IShellController（可替换、短生命周期）
  ├─ ShellView
  ├─ Dock / Taskbar / Start / Menu Bar 呈现
  ├─ 桌面右键菜单和快捷方式排列
  └─ 请求布局策略，不直接持有窗口真相
```

`remoteos` 是首个内置 Shell。`windows-like`、`macos-like`、`ubuntu-like` 在首版可只作为内置 Shell 风格，不能让外置包获得替换整个 Shell 或截获全局输入的权限。

### 6.2 切换事务

```text
用户在设置中选择 Shell
  → 校验 Shell 已安装且兼容
  → 保存“期望 Shell”偏好
  → 当前 controller 导出纯 UI 视图状态
  → 从宿主视觉树移除旧 ShellView
  → 创建并挂载新 ShellView
  → 新 controller 绑定既有 ShellSession / WindowManager
  → 恢复允许的视图状态并显示新桌面
  → 标记偏好为已应用；失败则恢复旧 controller
```

切换过程中不得重新创建 `ApplicationManager`、`WindowManager`、认证会话、网络客户端或已运行应用窗口。单个 Shell 挂载失败必须回退到 `remoteos` Shell，并写入不含敏感内容的本地诊断。设置中的默认 Shell 需要明确 Scope：默认建议为 Workspace 偏好，允许 Device 覆盖；实现前必须与现有 `WorkspacePreferencesDto` schema 对齐。

### 6.3 统一与差异化的边界

首版所有 Shell 共享：应用安装/卸载、桌面快捷方式数据、文件关联、搜索索引、窗口模型、应用权限、壁纸选择和系统对话框。Shell 可差异化：任务栏/Dock、应用启动器、菜单栏、任务预览、图标网格、工作区入口、窗口控制视觉和右键菜单。

平铺窗口、Spaces、虚拟桌面、全局菜单代理等会改变窗口行为，应在 `IWindowLayoutPolicy` / `IWorkspaceLayoutService` 的后续 Goal 中独立设计，不能由某个 Shell 直接修改 `WindowManager` 私有状态。

## 7. 受限自动化脚本

### 7.1 模型与允许操作

脚本是 RemoteOS 用户自动化，不是本机 shell。首版选择声明式 YAML 或 JSON 工作流，不引入可执行脚本引擎；所有动作通过 Host 受控 API 执行。

```yaml
schemaVersion: 1
name: Open development environment
requestedPermissions: []
steps:
  - action: app.launch
    appId: remoteos.terminal
  - action: uri.activate
    uri: remoteos://settings/apps
  - action: shell.notify
    title: RemoteOS
    message: Development environment is ready.
```

首版仅允许：

- `app.launch`：启动已注册且兼容的应用；
- `uri.activate`：激活 Host-owned 或已登记且已校验的 URI；
- `remote-file.open` / `remote-folder.open`：走已有的文件访问与打开方式流；
- `window.focus` / `window.close`：仅操作当前用户已启动的受管窗口；
- `shell.notify`：显示本地 Shell 通知；
- `delay`、条件分支和失败策略：只使用确定、有限的 schema。

明确禁止任何本机命令、环境变量读写、任意 HTTP、原始 socket、反射、程序集加载、剪贴板秘密读取、任意 VSD 写入、Server 高风险操作和 Host Elevation。以后若需要可编程脚本语言，必须先单独设计隔离、API 边界、资源限额和审查模型。

### 7.2 授权、资源限制与审计

脚本拥有稳定 `ScriptId`，但不伪装成 AppId。每个动作以实际目标应用/服务的原有授权链执行；脚本本身仅请求未来定义的自动化权限，例如“后台启动应用”或“运行登录后自动化”。不继承任何应用的 grant。

`IAutomationRunner` 至少限制：最大步骤数、最大递归深度、最长总运行时间、单一脚本并发数、取消、循环检测和用户可见的执行来源。记录开始、结束、每步结果、稳定问题码和用户发起/触发来源；日志不记录文件内容、URI query 中可能存在的秘密、认证信息或应用私有参数。

V1 只允许用户从快捷方式或脚本库显式启动。登录后自动运行、计划任务、文件变更触发器、网络触发器和 AI 自动执行都留待后续独立 Goal，并必须新增确认、禁用和审计机制。

## 8. Goal 执行计划

每个 Goal 完成后均须保持 `dotnet build RemoteOS.sln -c Debug` 通过，并添加与该目标匹配的测试。除明确写为迁移的内容外，不得删除仍在使用的数据或让一个失败的外置包阻止用户进入桌面。

### Goal 0：冻结契约、目录模型与迁移策略

**工作**

- 确认 VSD Root、目录名称、文件扩展名、schemaVersion、问题码、Windows/Linux 路径规则及本地存储清理边界。
- 建立应用、快捷方式、Shell 与脚本的 DTO/schema 草案；列出从现有 `DeveloperPackageManager` 目录和 `catalog.json` 的一次性迁移规则。
- 确认内置 App 的 `builtinKey` 映射和所有当前内置 AppId；不得以显示名匹配。
- 更新开发者文档，说明 descriptor 不是权限或安全身份，第三方包依然不受进程隔离保护。

**验收**

- 有一份字段归属表，能区分 Host 固定字段、包声明字段、用户偏好和可重建缓存。
- 迁移不会删除现有开发包；不兼容/损坏的包保持原目录并在 UI 显示可操作状态。
- 文档没有把 VSD 说成真实 `C:`、宿主文件系统沙箱或第三方代码安全边界。

### Goal 1：VSD Bootstrap、路径安全与 descriptor 领域模型

**工作**

- 在 Client 新建 `VirtualSystemDrive` 服务，负责计算 Root、建立固定目录、完整路径校验和原子 JSON 读写。
- 在 Core 或 Runtime 建立纯 DTO/validator：`ApplicationDescriptor`、`ApplicationDescriptorKind`、`ShellDescriptor`、`RemoteOsShortcut`、稳定问题码。
- 编写 `IBuiltInApplicationFactoryRegistry` 并把当前内置 App 的工厂映射从 Bootstrapper 的直接枚举中抽离。
- 实现内置 descriptor seeder：缺失时创建，损坏/陈旧时根据 Host 固定映射恢复，且保留独立用户配置。

**验收**

- 新装客户端在没有 VSD 的情况下可成功启动并创建固定目录与全部内置 descriptor。
- 路径穿越、绝对路径、无效 AppId、符号链接/重解析点逃逸、未知 schema 与过大 JSON 被安全拒绝。
- descriptor validator 不引用 Avalonia、DI、文件系统或外置程序集；有覆盖上述拒绝条件的单元测试。

### Goal 2：Catalog discovery 与内置应用注册迁移

**工作**

- 实现 `ApplicationCatalogScanner`：扫描固定深度、聚合错误、验证内置来源、转换 `ApplicationManifest` / `AppIdentity`。
- 在 Bootstrapper 中以 scanner 替换内置 `IRemoteApplication` 直接注册循环；`ApplicationManager` API 保持为唯一注册入口。
- 实现可重建 `System/catalog.json`，并让运行时 registry 更新继续触发桌面应用列表刷新。
- 为 descriptor 缺失、重复 AppId、损坏 JSON、陈旧内置 descriptor 和单应用失败建立诊断 UI/日志约定。

**验收**

- 现有内置应用的启动、文件关联、URI 激活、兼容性检查、权限提示及桌面展示不回归。
- 修改磁盘 descriptor 中的 `kind`、AppId、builtinKey、程序集字段或权限字段均不能让其冒充/提升内置 App。
- 一个损坏 descriptor 不会阻断其他应用注册，也不阻断 Shell；运行时不会出现重复 AppId 的不确定覆盖。

### Goal 3：外置包安装目录与版本切换迁移

**工作**

- 将 `DeveloperPackageManager` 的包目录、catalog 与 deferred cleanup 迁移到 `Programs/External` 结构；保留 `.roapp` 安装 CLI/开发者桥接接口。
- 让 `.roapp` 根 manifest 经既有校验后生成 VSD descriptor，并在未加载 DLL 前注册 metadata adapter。
- 实现 `current.json` 原子更新、上一个有效版本回退、逻辑卸载、锁定程序集延迟清理和包状态查询。
- 在设置/安装 UI 显示版本、安装来源、目录状态、未验证来源警告与 descriptor/加载错误摘要。

**验收**

- 包更新不会覆盖已加载版本目录；新版本注册或首次加载失败时旧版本仍可启动。
- 包内路径遍历、根外图标、根外程序集、伪造 `kind: builtin`、旧权限模型和无效入口类型都在执行包代码前被拒绝。
- 安装、更新、启动、卸载后 ApplicationManager、桌面、开始菜单和旧快捷方式的状态一致。

### Goal 4：快捷方式、桌面数据与统一激活

**工作**

- 实现 `ShortcutStore`、固定目标类型、相对路径校验、创建/重命名/删除和失效目标状态。
- 在现有 Desktop UI 显示 VSD 快捷方式，并与当前远端 Desktop 文件列表并列但类型明确。
- 所有快捷方式激活必须委派给 `ApplicationManager`、`IAppActivationService`、Explorer 或 Automation Runner；不在 ViewModel 分支执行目标。
- 定义桌面图标位置的 Workspace/Device 合并策略，确保不同 Shell 可以共享同一快捷方式集合。

**验收**

- App、远端文件、远端目录、URI 和脚本快捷方式均走正确的 Host API；失效目标不会自动替代成其他应用。
- 桌面读写不会允许用户从快捷方式访问任意本地文件；无效/未知 JSON 只显示为损坏条目或被安全忽略。
- 文件关联、默认应用、现有远端 Desktop 文件操作及登录/登出清理不回归。

### Goal 5：ShellSession、RemoteOS Shell 迁移与即时切换

**工作**

- 抽取 `ShellSession` 和 `IShellDefinition` / `IShellController`；将当前桌面作为 `remoteos` Shell 的首个实现。
- 将 Shell 设置持久化到现有 Workspace/Device 偏好模型，并在认证状态和偏好变化时安全应用。
- 实现切换事务、旧 Shell 视图状态导出、新 Shell 视图挂载、失败回退和 UI 测试入口。
- 构建第二个最小 `windows-like` Shell，验证同一应用、任务栏窗口和快捷方式在不同 Shell 中即时呈现。

**验收**

- 用户切换默认 Shell 后无需退出应用，当前已打开窗口、焦点、窗口布局和认证会话保持。
- 新 Shell 发生构造或挂载异常时自动回退 `remoteos`，不出现空白桌面或无法交互的窗口层。
- Shell 不直接注册/卸载应用、不拥有 WindowManager 真相，且不通过反射访问 App 私有对象。

### Goal 6：受限自动化工作流与脚本快捷方式

**工作**

- 实现声明式脚本 schema、严格 parser、`IAutomationRunner`、取消、步骤/时间/并发限制和脱敏审计。
- 只实现本文件 §7.1 列出的允许动作；所有激活复用既有应用、URI、文件和窗口 API。
- 实现脚本列表、语法/校验错误、执行进度、取消和最后一次结果的最小 Shell UI。
- 将 script shortcut 接入 Goal 4 的激活路由。

**验收**

- 脚本不能执行本机 shell、启动任意 EXE、访问任意本地路径、发起任意网络或取得认证秘密。
- 无限循环、过多步骤、超时、取消、未知动作、无效 AppId 与被卸载目标都有稳定结果且不会令 Shell 无响应。
- 每一步的实际授权和兼容性检查不因脚本调用而被绕过；审计不含敏感数据。

### Goal 7：更多内置 Shell 风格与布局策略前置接口

**工作**

- 在已验证的 ShellSession 上实现最小 `macos-like` 与 `ubuntu-like` Shell 风格：Dock/菜单栏或应用概览等视觉与交互差异。
- 提取只读 `IWindowLayoutPolicy` 请求接口，为未来平铺、虚拟桌面和 Spaces 留出边界，但不在本 Goal 改写窗口管理算法。
- 增加 Shell 可访问性、键盘导航、响应式尺寸和多语言本地化验证。

**验收**

- 每种 Shell 均可打开应用、查看/操作任务、激活快捷方式、使用基础系统对话框并切回其他 Shell。
- 任一 Shell 不会改变其他 Shell 的应用注册、权限、VSD 安装数据或窗口持久化语义。
- 提供的风格名称和品牌资源不冒充真实操作系统，也不要求拥有其原始视觉资产。

### Goal 8：回归、迁移发布与后续自动化扩展评审

**工作**

- 覆盖 Windows/Linux 首次启动、旧开发包迁移、损坏 VSD 恢复、外置包更新/回退、快捷方式、Shell 切换与脚本限制的测试矩阵。
- 更新架构、开发者模式、应用兼容性、安装器、设置和桌面文档；更新 `docs/README.md` 索引。
- 明确后续独立 Goal 的前置决策：包签名/发行渠道、Shell 插件化、脚本语言沙箱、计划/登录触发器、AI 生成并执行自动化、平铺/虚拟桌面。

**验收**

- VSD 被部分删除或单项损坏后，客户端能恢复到可用 `remoteos` Shell，内置应用仍可重新播种。
- 旧开发包用户得到明确迁移/重装结果，迁移不静默删除其包或授权数据。
- 构建、现有测试及新增安全/迁移测试稳定；文档不承诺当前版本未实现的沙箱、任意脚本或真实 OS 仿真能力。

## 9. 测试与发布完成定义

### 9.1 自动化测试

- **纯单元测试**：AppId、descriptor、shortcut、Shell descriptor、脚本 schema、路径规范化、重复 ID、版本选择、URI 白名单、限制计数和审计脱敏。
- **Catalog 测试**：内置播种、空目录、单个损坏应用、重复 ID、伪造 BuiltIn、外置包回退、逻辑卸载和 catalog 重建。
- **运行时测试**：注册顺序、`RegistryChanged`、文件关联、URI 激活、单窗口实例策略、包首次加载失败和权限提示保持现有语义。
- **Shell 测试**：切换事务、旧视图销毁、新视图挂载、失败回退、WindowManager/应用窗口身份保持、设置 Scope 合并。
- **Automation 测试**：允许动作、未知动作、超时、取消、最大步骤数、被卸载应用、权限拒绝和“不得调用本机进程/网络”的 adapter 测试。

### 9.2 手工验证矩阵

| 场景 | 应确认的结果 |
| --- | --- |
| 首次启动 | VSD 创建、内置 descriptor 播种、RemoteOS Shell 和全部内置应用可用。 |
| VSD 部分损坏 | 一个 descriptor/shortcut 损坏时其余桌面可用，内置项目可恢复。 |
| 开发包更新 | 新目录安装、旧程序集不被覆盖、失败可回退、桌面条目正确刷新。 |
| Shell 切换 | 运行中应用窗口不关闭；切换立即可见；失败退回 RemoteOS Shell。 |
| 快捷方式 | App、远端文件/目录、URI、脚本和失效目标均显示正确且不会越权。 |
| 脚本 | 正常工作流可运行；未知/超限/取消不会冻结 Shell 或执行宿主命令。 |
| Windows 与 Linux | 路径、LocalApplicationData 等价目录、锁定文件与图标资源行为符合约定。 |

### 9.3 发布级完成定义

只有同时满足以下条件，VSD 与多 Shell 基础设施才可标为完成：

1. VSD 是本地应用数据下受控目录，而非真实系统盘或权限边界；目录、descriptor 和包更新均有路径安全验证。
2. 内置与外置应用都经 Catalog 发现后注册，`ApplicationManager` 仍是唯一运行时注册与启动入口。
3. 外置包无法靠修改 descriptor 冒充内置应用；包 DLL 在验证、兼容性判断和首次启动前不会执行。
4. 包更新不覆盖在用版本，失败能回退；一个坏包、描述文件或快捷方式不阻断 Shell。
5. 至少 `remoteos` 和 `windows-like` 两种 Shell 能即时切换，运行中应用和 WindowManager 状态保持。
6. 快捷方式与自动化只经受控 Host API 激活；首版不存在任意 shell、进程、网络或本地文件执行入口。
7. 权限文档持续明确：当前第三方包模型不是恶意代码沙箱，VSD 和 AppId 也不构成安全隔离。
8. Windows 和 Linux 的首次启动、迁移、更新、回退和失败路径均经过测试或明确标为发布前阻塞项。

## 10. 后续 Goal 提示

后续 Goal 模式应使用以下提示，并以本文为约束：

> 依据 `docs/architecture/RemoteOS.VirtualSystemDrive.Goal.md` 实现 RemoteOS 虚拟系统盘、目录发现的应用 Catalog、快捷方式、多 Shell 和受限自动化。严格按 Goal 0–8 顺序推进：先冻结目录/schema/迁移与路径安全，再实现内置应用 descriptor 和 Catalog，随后迁移 `.roapp`、快捷方式、Shell 即时切换和声明式脚本。`ApplicationManager` 与 `WindowManager` 分别保持唯一的应用和窗口真源；磁盘 descriptor 不能授予 BuiltIn 身份、权限或任意代码执行。不得实现真实 C 盘映射、任意宿主命令、任意 EXE/网络执行、静默自动化或将第三方包误称为隔离/可信。每个 Goal 只有在构建、测试和本文件验收通过后才能进入下一项。
