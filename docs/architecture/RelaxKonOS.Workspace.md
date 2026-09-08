# RelaxKonOS Workspace 模型设计文档

> 本文档定义 RelaxKonOS 的用户环境模型：User、Workspace、Device、Session、Controller / Observer、Workspace 生命周期、多设备连接模型。
>
> 本文档描述 RelaxKonOS 作为**云操作系统**时的运行模型。**落地状态**：Workspace 持久化已实现（EF Core + SQLite，User/Workspace(含 TerminalSettings)/Device 落库）；终端 PTY 会话由 `TerminalSessionManager` 持有，断开仅 detach 保留、再次登录 `Start(Attach)` 回放 1MB 缓冲恢复；多设备 Controller/Observer 协调、Workspace 生命周期（Idle/Sleep）等待后续实现。详见 [`RelaxKonOS.Storage.md`](../platform/RelaxKonOS.Storage.md) 与 [`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md)。
>
> - 模块架构见 [`RelaxKonOS.Architecture.md`](./RelaxKonOS.Architecture.md)
> - 当前代码实现见 [`RelaxKonOS.md`](../README.md)

---

## 1. 设计目标

RelaxKonOS 不采用传统远程桌面的用户模型。

**传统远程桌面**——每个 Session 是独立桌面：

```text
User
  |
  +-- Session A → Desktop Instance
  +-- Session B → Desktop Instance
```

**RelaxKonOS 采用**：

```text
User
  |
  Workspace
  |
  Session
  |
  Device
```

核心思想：

> 一个用户拥有一个持续存在的 RelaxKonOS Workspace，多个设备作为终端连接该 Workspace。

---

## 2. 核心对象关系

整体关系：

```text
User
  |
  Workspace
  |
  Session
  |
  Device
```

| 对象 | 含义 |
|------|------|
| User | 身份主体 |
| Workspace | 用户的 RelaxKonOS 环境 |
| Session | 设备连接 Workspace 的会话 |
| Device | 访问 RelaxKonOS 的终端设备 |

---

## 3. User

### 3.1 定义

User 是 RelaxKonOS 的身份抽象，与宿主 OS 用户（Linux User / Windows Account）建立映射关系。User 负责 RelaxKonOS Workspace、Session、Device 管理；宿主 OS 用户负责实际系统权限。

```text
User
  |
Platform Identity Mapping (Linux User / Windows Account)
  |
Workspace
```
- **负责**：登录认证、权限管理、数据归属、Workspace 所有权。
- **示例**：

  ```text
  User
    Id:       10001
    Username: alice
  ```

### 3.2 Workspace 关系

默认：One User → One Personal Workspace。

```text
Alice → Alice Workspace
```

未来可扩展：

```text
Alice
  +-- Personal Workspace
  +-- Work Workspace
  +-- Development Workspace
```

---

## 4. Workspace

### 4.1 定义

Workspace 是 RelaxKonOS 的核心运行实例。

> 一个持续存在的 RelaxKonOS 用户环境。

Workspace 不属于某个设备——设备只是连接 Workspace 的入口。

### 4.2 Workspace 包含内容

```text
Workspace
  ├── Desktop State
  ├── Application State
  ├── Runtime State
  ├── User Data
  ├── Platform Identity Context
  └── Remote Service State
```

---

## 5. Desktop State

Desktop State 表示桌面环境状态，包含：Wallpaper、Theme、Desktop Layout、Icon Position、Taskbar State。

当前 `WorkspacePreferencesDto`（作为 Workspace 导航属性 `OwnsOne + ToJson` 单列 JSON 持久化）已落地以下 Desktop State 字段（定义见 [`WorkspacePreferencesDto.cs`](../../Shared/RelaxKonOS.Protocol/Workspace/WorkspacePreferencesDto.cs) 与 [`DesktopDisplaySettingsDto.cs`](../../Shared/RelaxKonOS.Protocol/Desktop/DesktopDisplaySettingsDto.cs)）：

| 字段 | 类型 | 含义 | 默认值 |
|------|------|------|--------|
| `wallpaperKey` | `string` | 壁纸标识。前缀 `builtin:` 表示内置壁纸，`custom:` 表示 Workspace 托管图片 blob id。 | `builtin:bloom` |
| `theme` | `ThemeKind` enum | 明亮/暗黑主题。 | `Light` |
| `themePreferences.styleId` | `string` | 主题风格标识。 | `remoteos` |
| `themePreferences.paletteId` | `string` | 调色板标识。`builtin:remoteos-blue` 为默认；可选择自定义调色板（见下）。 | `builtin:remoteos-blue` |
| `themePreferences.accentOverride` | `string?` | 可选强调色覆盖（`#RRGGBB[AA]`）。若设置则覆盖当前调色板中的 Accent token。 | `null` |
| `themePreferences.customPalettes[]` | `ThemePaletteDto[]` | 工作区自定义调色板列表，每项包含 `id/name/lightColors/darkColors`。Light/Dark 同一调色板共用 `id`，颜色字典必须符合 `ThemePaletteContract.RequiredColorTokens` 且由 `ThemePaletteValidator` 完成对比度校验。 | `[]` |
| `desktopDisplay.showBuiltInApps` | `bool` | 是否在桌面显示内置应用图标。 | `true` |
| `desktopDisplay.visibleAppIds[]` | `List<string>` | 当 `showBuiltInApps=true` 且非空时，仅显示列表中的内置应用；空则显示全部。 | `[]` |
| `desktopDisplay.showServerDesktopFiles` | `bool` | 是否显示服务器桌面的一般文件（非快捷方式）。 | `true` |
| `desktopDisplay.showServerDesktopShortcuts` | `bool` | 是否显示服务器桌面的 `.lnk` / `.desktop` 快捷方式。 | `false` |
| `desktopDisplay.hasCompletedFirstTimeSetup` | `bool` | 用户是否完成首次桌面配置（跳过也算完成）。用于首次登录弹引导。 | `false` |

例如：

```text
Desktop
  Wallpaper: builtin:bloom
  Theme:     Dark  (palette = builtin:remoteos-blue)
  Icons:     Browser, Terminal, Explorer    (desktopDisplay.visibleAppIds 过滤)
```

除偏好 JSON 外，窗口位置与尺寸作为独立的 Workspace 状态持久化：`WorkspaceWindowLayoutDto.Windows: IReadOnlyList<WindowSizeDto>(key,width,height)`，每个 key 由应用自身声明（例如 `appId + ":" + windowInstanceId`）。路由 `GET/PUT /workspaces/{id}/window-layout` 读写该快照，登录后 `PreferencesSync` 按 key 恢复窗口尺寸（位置由窗口管理器的"打开偏好"策略再次决定）。

---

## 5.1 Locale & Text Encoding State

Locale（时间/日期/语言/区域）与文本编码默认值同样挂在 `WorkspacePreferencesDto`，跨设备共享：

| 字段 | 类型 | 含义 | 默认值 |
|------|------|------|--------|
| `timeFormat` | `string` | `24h` 或 `12h`。 | `24h` |
| `dateFormat` | `string` | `DateTime` 短日期格式串。 | `yyyy/M/d` |
| `language` | `string` | 语言标记（`en-US` / `zh-CN` / `ja-JP` 等）。 | `en-US` |
| `region` | `string` | 区域标记（影响默认星期起、数字/货币格式）。 | `en-US` |
| `notepadDefaultEncoding` | `string?` | Notepad 打开文件时的默认字符集。`TextEncodingPreferences.Default = UTF-8`。 | `UTF-8` |
| `codeEditorDefaultEncoding` | `string?` | Code Editor 打开文件时的默认字符集。 | `UTF-8` |
| `defaultApps[]` | `List<DefaultAppMappingDto>` | 扩展名 → 默认 AppId 映射（可变集合，EF Core 以 JSON 内部序号追踪项）。 | `[]` |

---

## 6. Application State

Application State 表示应用状态。

> 注意：Application State **不是** UI 图像。RelaxKonOS 保存的是应用配置、运行状态、用户数据。

### RemoteBrowser

- **保存**：Tabs、History、Bookmark、Cookie、Extension Config。
- **不保存**：Browser Screenshot。

### RemoteTerminal

- **保存**：Terminal Session Id、Working Directory、Environment、Process State。
- **示例**：

  ```text
Terminal
  Session: id=10001
  cwd:     /home/alice/project        (Linux)
           C:\Users\alice\project      (Windows)
  ```

---

## 7. Runtime State

Runtime State 表示持续运行的服务。

```text
RemoteTerminal
    |
  PTY
    |
  Shell Process
```

- **Client 断开**：`RelaxKonOS.Client` Offline。
- **Workspace**：Running。
- **Runtime**：Continue。
- **重新连接**：Restore Session。

---

## 8. Workspace 生命周期

Workspace 默认持续存在。

```text
Created → Running → Idle → Sleeping → Running → ...
```

### 9. Workspace Running

Running 状态表示：有连接设备、有活动 Runtime、有后台任务。

```text
Workspace
  Controller: Laptop
  Runtime:    Terminal, Browser Service
```

### 10. Workspace Idle

Idle 状态表示：无 Controller、无用户操作。但是 Workspace State 仍然存在。

### 11. Workspace Sleeping

为了降低资源消耗，Workspace 可以进入 Sleep。

- **条件**：长时间无连接、无重要 Runtime、无后台任务。
- **Sleep**：Memory State 保存，Runtime 暂停或迁移。
- **恢复**：

  ```text
  Device Login → Wake Workspace → Restore State
  ```

---

## 12. Device

### 12.1 定义

Device 表示访问 RelaxKonOS 的终端。

```text
Device
  Name:     Office-PC
  Platform: Windows 11
  Client:   RelaxKonOS.Client 1.0
```

### 12.2 Device 保存信息

DeviceId、Name、Platform、Client Version、Last Login Time、Trust Status。

---

## 13. Session

### 13.1 定义

Session 表示：

> 一个 Device 与 Workspace 的连接关系。

```text
Workspace
  |
  Session
  |
  Device
```

### 13.2 Session 与 Workspace 区别

Session 消失（Device Disconnect）**不代表** Workspace Destroy。

例如：

```text
Laptop Shutdown
  → Session:     Disconnected
  → Workspace:   Running
```

---

## 14. 多设备连接模型

RelaxKonOS 使用 **Active Controller + Observer** 模型。

- **目标**：支持多个设备访问同一个 Workspace。
- **但是**：同一时间只有一个设备拥有完整控制权。

### 15. Controller

Controller 是当前控制设备，拥有：Keyboard Input、Mouse Input、Window Operation、Application Control、System Command。

```text
Workspace
  Controller: Office-PC
```

### 16. Observer

Observer 是观察设备。

- **拥有**：View Workspace State、View Running Application、Receive Notification。
- **不拥有**：Window Control、Input Control、Application Modification。

### 17. Controller / Observer 状态

```text
Workspace
  Controller → Laptop
  Observers  → Phone, Tablet
```

---

## 18. Control Transfer

Observer 可以请求控制权。

```text
Observer → Request Control
    |
  Workspace
    |
  Controller Change
```

例如：

```text
Before:  Office-PC (Controller)
After:   Laptop (Controller), Office-PC (Observer)
```

---

## 19. Controller 断开处理

Controller 可能因为网络断开、Client 崩溃、设备睡眠而导致 Connection Lost。

RelaxKonOS 不立即释放 Controller。

### 19.1 Grace Period

```text
Controller Lost → Keep Controller Lease → Timeout → Release Controller
```

例如：5 minutes。

### 19.2 恢复连接

如果原设备在 Grace Period 内恢复：

```text
Reconnect → Restore Controller
```

### 19.3 超时后接管

超过时间：

```text
New Device → Request Control → Become Controller
```

---

## 20. 登录流程

### 20.1 首次登录

```text
RelaxKonOS.Client Start
    |
  Authentication
    |
  Create User Workspace
    |
  Become Controller
    |
  Load Workspace State
```

### 21. 已存在 Workspace 登录

例如，当前 `Office-PC` 是 Controller，新设备 `Laptop` 登录：

```text
RelaxKonOS:
  Workspace Active
  Current Controller: Office-PC

提供：
  [Join Observer]
  [Request Control]
  [Cancel]
```

---

## 22. 与传统 Windows Server 模型区别

**Windows Server**：

```text
User → Multiple Desktop Session
```

**RelaxKonOS**：

```text
User → One Persistent Workspace → Multiple Device Session
```

|  | Windows Server | RelaxKonOS |
|---|---|---|
| 用户环境 | Session | Workspace |
| 设备关系 | 独立登录 | 连接同一环境 |
| 桌面状态 | Session 内部 | Workspace 共享 |
| 应用状态 | Session 内部 | Workspace 持久 |
| 多设备 | 多个桌面 | 多个终端 |

---

## 23. AI Agent 理解规则

实现 RelaxKonOS 用户系统时必须遵守：

- **不要设计**：`User → Multiple Independent Desktop`。
- **不要创建**：Device Local Workspace。
- **正确模型**：

  ```text
  User → Persistent Workspace → Session → Device
  ```

必须保持：

- Workspace 持久化
- Runtime 状态独立于 Device
- Session 只是连接关系
- Controller 管理控制权

> 落地状态：Workspace 持久化已实现（EF Core + SQLite，User / Workspace(含 TerminalSettings/BrowserSettings/Preferences/WindowLayout) / Device 落库；Session / 刷新令牌 / PTY 进程维持内存，各有语义理由）。
>
> **Preferences JSON 已扩字段**（`OwnsOne + ToJson`，新增字段无需改 schema）：
> - **主题调色板**：`ThemePreferencesDto`（styleId/paletteId/accentOverride/customPalettes[]，每调色板包含 Light/Dark 双色变体，需通过 ThemePaletteValidator 对比度校验）。
> - **桌面显示**：`DesktopDisplaySettingsDto`（内置应用显隐、可见应用白名单、服务器桌面文件/快捷方式、首次配置完成位）。
> - **文本编码**：`notepadDefaultEncoding` / `codeEditorDefaultEncoding`，默认 `UTF-8`，由 Notepad/CodeEditor 打开对话框使用（见 `TextEditor/` 共用编码对话框）。
> - **窗口布局**：`WorkspaceWindowLayoutDto` 作为独立 Workspace 状态（GET/PUT `/workspaces/{id}/window-layout`），保存每个窗口 key 的 (width,height)，登录后恢复尺寸。
>
> 终端外观配置 TerminalSettings 随 Workspace 以 JSON 列持久，跨重启保留。终端 PTY 会话由 `TerminalSessionManager` 持有（与 Hub 连接解耦），断开仅 detach 保留、再次登录 `Start(Attach)` 回放 1MB 环形缓冲恢复历史输出——契合 §7 Runtime State "Client 断开 → Workspace Running → Runtime Continue → 重新连接 Restore" 模型。详见 [`RelaxKonOS.Storage.md`](../platform/RelaxKonOS.Storage.md)、[`RelaxKonOS.Terminal.md`](../applications/RelaxKonOS.Terminal.md) 与 [`RelaxKonOS.Settings.md`](../desktop/RelaxKonOS.Settings.md)。

RelaxKonOS 的目标：

- **不是**：多用户远程桌面服务器。
- **而是**：一个用户拥有持续运行的云操作系统环境，多个设备作为终端访问该环境。
