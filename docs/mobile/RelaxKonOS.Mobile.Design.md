# RelaxKonOS Android Mobile（手机与平板）设计

> **状态：提案 / 实施前设计基线**  
> **首发范围：Android 手机与 Android 平板；iOS/iPadOS 为同一架构下的后续平台。**
>
> 本文定义移动客户端的产品边界、项目布局、模块依赖、协议演进、响应式交互、安全与验收要求；不代表当前已实现功能。本文与现有架构冲突时，遵守 [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md) 的“本地渲染、状态同步、Protocol 契约优先”原则。

---

## 1. 决策与目标

### 1.1 已确定的决策

| 项目 | 决策 |
| --- | --- |
| UI 技术 | Avalonia 12 + .NET 10；沿用既有 C#、XAML、MVVM 与 `RelaxKonOS.Protocol`。 |
| 首发平台 | 一个 Android 安装包，同时支持手机、8 英寸级平板与 11 英寸级平板；不按设备型号拆分 App。 |
| 后续平台 | 预留 iOS/iPadOS Host，业务、ViewModel、移动页面与布局规则复用；iOS 构建、签名与发布另需 Mac/Xcode。 |
| 产品形态 | 独立的 **Mobile Shell**，不是桌面 Shell 的缩小版，也不是仅展示数据的仪表盘。 |
| 通信模型 | 复用 REST + SignalR + Protocol DTO；客户端本地渲染，禁止像素流、RDP/VNC 或让服务端生成 UI。 |
| 平板定位 | 一等支持：平板是持续轻量管理工作台，使用双栏/三栏；手机是快速查看与应急操作。 |

### 1.2 目标

- 在手机上可靠完成登录、查看状态、文件操作、终端连接及常见服务操作。
- 在平板横屏上同时查看列表、详情、日志或终端，不退化为放大的手机界面。
- 遵守现有认证、Workspace、设备控制权、权限提升和能力声明模型。
- 把可跨平台复用的网络/业务代码从桌面 Shell 中逐步提炼，避免为移动端复制 REST、SignalR 与认证逻辑。

### 1.3 非目标

- 不将 Desktop、Taskbar、Start Menu、WindowManager、桌面内 `RemoteWindow` 迁移到移动端。
- 首版不实现本地 Browser、IDE、图片/视频编辑、注册表完整编辑、证书全生命周期等高密度桌面工作流。
- 不把移动端后台连接当作服务端任务的存活条件；Android 随时可能暂停或终止 App。

---

## 2. 总体架构

```text
                          RelaxKonOS.Server
                     REST / SignalR / Protocol DTO
                                   ▲
                                   │
              ┌────────────────────┴────────────────────┐
              │       RelaxKonOS.Protocol（既有）         │
              │ DTO、路由、Hub 方法/事件、序列化契约       │
              └────────────────────┬────────────────────┘
                                   │
                    RelaxKonOS.Client.Foundation（新增）
       认证会话、Token 刷新、HTTP 处理链、Hub 连接、能力判断、远程服务代理
                         ▲                         ▲
                         │                         │
       既有 Desktop Shell │                         │ 新增 Mobile Shell
 RelaxKonOS.Client + Client.Desktop          Client.Mobile + Client.Android
  桌面/窗口/内置桌面应用                      页面导航/响应式布局/Android 平台适配
```

`RelaxKonOS.Client.Foundation` 是共享的客户端基础设施，而非新的“万能 Core”。它只能依赖 `RelaxKonOS.Protocol` 和通用 .NET / Microsoft 扩展包；不得引用 Avalonia、WindowManager、App SDK、Runtime、桌面 `Window`、文件路径或 Android API。

移动端不引用现有 `RelaxKonOS.Client`：该项目是桌面 Shell，已包含 WindowManager、Runtime、桌面内置应用、桌面凭据存储以及可能不支持 Android 的控件。共享代码必须以小步、按功能提取的方式迁移到 Foundation，不能先进行大规模重写。

---

## 3. 规划中的源码与文档位置

### 3.1 解决方案目录

```text
RelaxKonOS/
├─ Client/
│  ├─ RelaxKonOS.Client/                    # 既有：Desktop Shell；不供 Mobile 引用
│  ├─ RelaxKonOS.Client.Desktop/            # 既有：桌面启动入口
│  ├─ RelaxKonOS.Client.Foundation/         # 新增：纯客户端通信与业务基础设施
│  │  ├─ Auth/
│  │  ├─ Http/
│  │  ├─ Hubs/
│  │  ├─ Capabilities/
│  │  ├─ RemoteServices/
│  │  └─ DependencyInjection/
│  ├─ RelaxKonOS.Client.Mobile/             # 新增：平台无关的 Avalonia 移动 UI
│  │  ├─ Navigation/
│  │  ├─ Shell/
│  │  ├─ Features/
│  │  │  ├─ Authentication/
│  │  │  ├─ Dashboard/
│  │  │  ├─ Files/
│  │  │  ├─ Terminal/
│  │  │  ├─ Workloads/
│  │  │  └─ Settings/
│  │  ├─ Controls/
│  │  ├─ Resources/
│  │  ├─ Services/
│  │  └─ ViewModels/
│  ├─ RelaxKonOS.Client.Android/            # 新增：net10.0-android Host
│  │  ├─ MainActivity.cs
│  │  ├─ AndroidManifest.xml
│  │  ├─ PlatformServices/
│  │  │  ├─ AndroidSecureCredentialStore.cs
│  │  │  ├─ AndroidFilePicker.cs
│  │  │  ├─ AndroidShareService.cs
│  │  │  └─ AndroidLifecycleService.cs
│  │  └─ Resources/
│  └─ RelaxKonOS.Client.Mobile.Tests/       # 新增：导航、布局状态、ViewModel、服务代理测试
├─ Shared/RelaxKonOS.Protocol/               # 既有：仅跨端 DTO/路由/Hub 契约
├─ Framework/RelaxKonOS.UI/                  # 既有：只复用经移动验证的颜色、字体、令牌
└─ docs/
   └─ mobile/
      ├─ RelaxKonOS.Mobile.Design.md         # 本文：架构与设计基线
      ├─ RelaxKonOS.Mobile.Progress.md       # 实施后新增：阶段、验证与已知问题
      └─ android-release.md                  # 实施后新增：签名、AAB、商店/侧载发布
```

项目均应加入 `RelaxKonOS.sln`。`Directory.Packages.props` 统一管理 Avalonia Android、AndroidX 及未来 iOS 依赖版本；不在各项目内写浮动版本号。

### 3.2 依赖规则

```text
Protocol  ← Foundation ← Mobile ← Android Host
                         ↑
Desktop Client ────────┘（仅按需引用 Foundation；不反向依赖 Mobile）
```

- `Protocol` 保持零 `PackageReference`，只定义 wire contract。
- Foundation 的远程服务接口和实现应沿用现有 typed `HttpClient`、认证 handler、SignalR 重连策略；ViewModel 不拼 URL、不直接 `new HttpClient`。
- Mobile 只负责 View、ViewModel、导航与触摸交互；不调用 Android API。
- Android Host 只负责 Activity、运行时权限、Keystore、文件选择/分享、Insets 和生命周期桥接；不得放业务规则或页面逻辑。
- 通用 UI 只在确认手机/平板可用后从 `Framework/RelaxKonOS.UI` 复用。`RemoteWindow`、桌面模态机制、Taskbar 样式不进入 Mobile。

---

## 4. 先决协议调整

现有 `LoginRequest.ClientPlatform` 使用 `PlatformKind`，而 `PlatformKind` 当前只有 `Linux` 与 `Windows`，且同时描述 Server 宿主平台。这会使 Android 客户端被错误归类，不能直接沿用。

实施前必须将两种语义拆开：

| 新契约 | 值 | 用途 |
| --- | --- | --- |
| `HostPlatformKind` | `Linux`、`Windows` | Server 描述、宿主权限与平台能力判断。 |
| `ClientPlatformKind` | `Windows`、`Linux`、`Android`、`iOS` | 登录请求、Device 注册、客户端兼容性与设备列表。 |

涉及位置：

- `Shared/RelaxKonOS.Protocol/Common/PlatformKind.cs`：替换为语义明确的枚举。
- `Shared/RelaxKonOS.Protocol/Identity/LoginRequest.cs`：`ClientPlatform` 改为 `ClientPlatformKind`。
- `Shared/RelaxKonOS.Protocol/Workspace/RegisterDeviceRequest.cs`、设备 DTO/仓储/测试：使用客户端平台。
- Server 的 `ServerDescriptorDto`、身份 Provider、宿主能力判断：使用 `HostPlatformKind`。
- Desktop 登录 ViewModel：显式返回 `Windows` 或 `Linux`；Android Host 返回 `Android`。

项目处于首个正式版本前，按仓库 API 演进规则直接完成 breaking change，更新所有调用方、测试和文档；不保留旧枚举、双格式解析或兼容路由。

---

## 5. Mobile Shell 与导航

### 5.1 登录至主界面

```text
启动 → 已保存服务器/登录 → 认证成功 → 读取 Server Capabilities
     → MobileShell（首页） → 页面导航 / 操作确认 / 网络恢复
```

首次登录要求服务器地址、用户名和密码。用户勾选“记住连接”时，服务器地址与用户名可保存；密码只能保存到 Android Keystore，刷新令牌始终仅在内存中。移动端不复用当前 Windows DPAPI、macOS Keychain 或 Linux Secret Service 的桌面实现。

主导航固定为：**主页、文件、终端、管理、更多**。

- **主页**：服务器名与连接状态、CPU/内存/磁盘、近期状态和高频入口；不是独立的“监控产品”。
- **文件**：远程目录浏览、上传、下载、重命名、新建、复制/移动和属性；删除属于显式确认操作。
- **终端**：远端 PTY 会话、会话切换、断线重连与移动扩展键栏。
- **管理**：Docker、进程守护、应用部署、Web Server 等服务控制面；由 Server Capabilities 和应用权限决定可见性。
- **更多**：账户、连接、设置、诊断、关于与登出。

不支持的服务不显示入口，不以灰色“伪功能”占位。影响运行环境的操作必须显示目标、后果和状态；现有的控制权与受限提权流程仍由 Server 决定，Mobile 只负责清晰确认与结果展示。

---

## 6. 手机与平板响应式规范

布局依据是扣除系统 Insets 后的**可用宽度 dp**，不是物理尺寸、品牌或屏幕方向。方向变化和分屏变化都会重新计算布局状态。

| 布局状态 | 可用宽度 | 典型设备 | Shell | 页面行为 |
| --- | ---: | --- | --- | --- |
| Compact | `< 600dp` | 手机竖屏 | 底部五项导航 | 单栏，详情压入导航栈。 |
| Medium | `600–839dp` | 横屏手机、小平板 | 窄导航 rail | 列表优先，可按页显示详情。 |
| Expanded | `≥ 840dp` | 常见 Android 平板横屏 | 完整左侧导航 | 双栏或三栏，列表、详情、日志/操作可并列。 |

所有交互控件最小命中区域为 48dp；列表行优先 56dp；文字、图标、间距随状态分级而不是等比例放大。软键盘出现时不得遮挡终端输入或关键确认按钮；系统返回键遵循“关闭弹层 → 返回详情/列表 → 退出当前导航层”的顺序。

### 6.1 重点页面

| 功能 | Compact（手机） | Expanded（平板） |
| --- | --- | --- |
| 文件 | 单目录列表；属性/预览为独立页 | 目录树或地点栏 + 列表 + 详情/预览三栏。 |
| 终端 | 单会话全屏；底部扩展键栏 | 会话列表 + 当前终端双栏；横屏优先。 |
| Docker/守护 | 卡片或列表进入详情页 | 资源列表 + 详情 + 日志/操作面板。 |
| 性能/进程 | 指标卡 + 可筛选列表 | 指标、趋势和进程/详情并列；不因屏幕变大增加无意义图表。 |

---

## 7. 远程能力、生命周期与安全

### 7.1 服务映射

| Mobile 功能 | 复用的既有契约 | 首个实现优先级 |
| --- | --- | --- |
| 首页状态 | SystemMonitor REST + Performance Hub | P0 |
| 文件 | `FileApiRoutes` | P0 |
| 终端 | Terminal Hub / PTY attach、resize、input | P0（先做 Android 真机 PoC） |
| Docker | `DockerApiRoutes` | P1 |
| 进程/守护 | SystemMonitor、Guardian API/Hub | P1 |
| Web Server/部署 | 既有 REST 操作模型 | P2 |

终端包及其依赖必须先在真实 Android 手机和平板验证触摸选择、IME、横屏、软键盘、Ctrl/Alt/Esc/Tab/方向键扩展栏和旋转后的 resize；验证失败前，不把桌面终端控件直接承诺为移动端正式方案。

### 7.2 后台与断网

- App 转后台、旋转、进程被系统回收均可能中断 HTTP 和 SignalR；Mobile Shell 需能重建视图与连接，不假设连接永久存在。
- Server 端操作不得依赖客户端常驻。提交型操作使用既有幂等键和服务端操作状态，恢复前台后查询结果。
- 终端断开只 detach；Server PTY 按既有模型继续运行并保留有限环形缓冲。恢复前台后以 session ID attach 并恢复尺寸；用户显式关闭才请求关闭 PTY。
- 网络错误不能自动登出；只有 refresh token 被 Server 明确拒绝时才清除会话并返回登录。

### 7.3 Android 平台边界

- 凭据使用 Android Keystore；日志、诊断、崩溃报告不得写入密码、JWT、refresh token 或命令中的秘密。
- 文件上传通过 Android 系统文件选择器取得内容流，不将用户文件路径假定为可访问的本地路径；下载完成后通过系统分享/打开机制交给用户。
- 仅按功能声明网络、通知等权限；不申请存储全盘访问、常驻后台或无关权限。
- 高风险动作（删除、停止/重启服务、部署、关闭终端）必须二次确认；确认文本必须包含具体目标。

---

## 8. 实施阶段与验收

| 阶段 | 交付 | 退出条件 |
| --- | --- | --- |
| M0：架构准备 | 新项目骨架、Protocol 平台语义拆分、Foundation 首批认证/HTTP、Android Host 可启动 | Solution 构建；Desktop 回归；Android 模拟器和真机均能显示登录页。 |
| M1：自适应 Shell | 登录、Keystore、能力读取、Compact/Medium/Expanded 导航和首页 | 手机/平板旋转、分屏、重启后布局正确；无凭据泄露。 |
| M2：核心操作 | 文件、状态、终端真机 PoC 与断线恢复 | Android 手机和两种平板尺寸完成登录、文件上传、终端 reconnect。 |
| M3：管理工作台 | Docker、守护/进程、日志与明确确认操作 | 仅显示受支持能力；失败、取消、超时均有可理解状态。 |
| M4：发布准备 | 图标/启动页、AAB 签名、崩溃诊断、Android 发布文档 | Release 包可安装；签名材料不入库；设备矩阵通过。 |

最小人工设备矩阵为：一台 Android 手机（竖/横屏）、一台约 8 英寸平板和一台约 11 英寸平板；每台验证登录、网络切换、软键盘、旋转、后台恢复、终端及危险操作确认。自动测试覆盖 ViewModel、布局状态计算、能力过滤、认证状态机、HTTP/Hub reconnect 和 Protocol 序列化。

---

## 9. 后续 iOS/iPadOS 接入

在 Android M2 之后才新建 `Client/RelaxKonOS.Client.iOS/`。它引用 Foundation 与 Mobile，不复制页面或业务逻辑；仅提供 iOS `AppDelegate` / Scene、Keychain、安全区、文件选择和分享服务实现。iPad 直接采用本设计的 Medium/Expanded 布局，不另建“iPad UI”。

---

## 10. 实施约束清单

- 新的移动功能先定义/修正 Protocol，再实现 Server（若需要），最后实现 Foundation 与 Mobile UI。
- 任何 ViewModel 都不得直接创建 `HttpClient`、拼接路由、使用 Android 原生 API 或访问桌面窗口管理器。
- 不以“能编译”为移动兼容性依据；终端、文件选择、软键盘、后台恢复和横竖屏必须在真实手机与平板验证。
- 不为保留当前错误的平台语义添加兼容 shim；直接更新仓库内所有调用者、测试和文档。
- 所有实施进展、已验证设备、已知限制和待决风险记录在 `docs/mobile/RelaxKonOS.Mobile.Progress.md`，而不是在本文中混写实现状态。
