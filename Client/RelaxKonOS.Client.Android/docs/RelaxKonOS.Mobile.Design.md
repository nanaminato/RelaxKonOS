# RelaxKonOS Android Mobile（手机与平板）设计

> **状态：产品与技术设计初稿；M0 架构准备已启动。**
> **首发范围：Android 手机与 Android 平板；iOS/iPadOS 为同一架构下的后续平台。**
>
> 本文定义移动客户端的产品边界、项目布局、模块依赖、协议演进、响应式交互、安全与验收要求；不代表当前已实现功能。本文与现有架构冲突时，遵守 [`RelaxKonOS.Architecture.md`](../architecture/RelaxKonOS.Architecture.md) 的“本地渲染、状态同步、Protocol 契约优先”原则。

---

## 1. 决策与目标

### 1.1 已确定的决策

| 项目 | 决策 |
| --- | --- |
| UI 技术 | Kotlin + Jetpack Compose + Android SDK；不引入 Avalonia Mobile、.NET for Android、XAML 或跨平台 UI 运行时。 |
| 首发平台 | 一个 Android 安装包，同时支持手机、8 英寸级平板与 11 英寸级平板；不按设备型号拆分 App。 |
| 后续平台 | iOS/iPadOS 如需支持，采用独立原生实现并遵循 Protocol wire contract；不以共享 UI 层为前提。iOS 构建、签名与发布另需 Mac/Xcode。 |
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
                    Kotlin Android data layer
        认证会话、Token 刷新、HTTP、Hub 连接、能力判断、远程服务代理
                                   ▲
                                   │
                         Kotlin/Compose Android 客户端
                    Compose 页面、响应式布局、平台适配
```

Android 不引用现有 `RelaxKonOS.Client`：它是桌面 Shell，已包含 WindowManager、Runtime、桌面内置应用、桌面凭据存储以及不支持 Android 的控件。Kotlin 数据层以 `RelaxKonOS.Protocol` 定义的 REST/JSON wire contract 为唯一事实来源；路由、字段名和枚举值变更必须同步更新 Kotlin 调用方与契约测试。

---

## 3. 规划中的源码与文档位置

### 3.1 解决方案目录

```text
RelaxKonOS/
├─ Client/
│  ├─ RelaxKonOS.Client/                    # 既有：Desktop Shell；不供 Mobile 引用
│  ├─ RelaxKonOS.Client.Desktop/            # 既有：桌面启动入口
│  └─ RelaxKonOS.Client.Android/            # Gradle Kotlin/Compose Android 应用
│     ├─ settings.gradle.kts
│     ├─ build.gradle.kts
│     ├─ AGENTS.md                           # Android 文档与实现约束
│     ├─ docs/                               # Android 文档的唯一详细来源
│     │  ├─ RelaxKonOS.Mobile.Design.md      # 本文：产品与技术设计初稿
│     │  ├─ RelaxKonOS.Mobile.Progress.md    # 阶段、验证与已知问题
│     │  └─ android-release.md               # 构建、签名与发布
│     └─ app/
│        └─ src/
│           ├─ main/java/app/relaxkonos/mobile/
│           │  ├─ MainActivity.kt
│           │  ├─ RelaxKonApi.kt
│           │  └─ LayoutState.kt
│           └─ test/java/app/relaxkonos/mobile/
├─ Shared/RelaxKonOS.Protocol/               # 既有：仅跨端 DTO/路由/Hub 契约
├─ Framework/RelaxKonOS.UI/                  # 既有：只复用经移动验证的颜色、字体、令牌
└─ docs/
   └─ mobile/
      └─ README.md                           # 仅作跨仓库入口，链接到 Android docs/
```

Android 工程由 Gradle 构建，不加入 `RelaxKonOS.sln`。Android UI 仅使用 Kotlin、Compose 与 Android SDK；`Directory.Packages.props` 不管理 Android 的 Gradle 依赖，也不保留 Avalonia Android 或 AndroidX 占位依赖。

### 3.2 依赖规则

```text
Protocol wire contract ──→ Kotlin Android data layer ──→ Compose UI
```

- `Protocol` 保持零 `PackageReference`，只定义 wire contract。
- Compose 页面只消费状态与意图；认证、HTTP、令牌与错误映射放在 Kotlin data layer，不散落在 Composable 中。
- Android 平台层负责 Activity、运行时权限、Keystore、文件选择/分享、Insets 和生命周期桥接；不得把服务端业务规则复制到页面中。
- Android 不复用 `Framework/RelaxKonOS.UI` 的 Avalonia 控件或资源。`RemoteWindow`、桌面模态机制、Taskbar 样式不进入 Android 客户端。

---

## 4. 先决协议调整

`LoginRequest.ClientPlatform` 曾使用同时描述 Server 宿主平台的 `PlatformKind`，会使 Android 客户端被错误归类。M0 已完成下列直接 breaking change。

实施前必须将两种语义拆开：

| 新契约 | 值 | 用途 |
| --- | --- | --- |
| `HostPlatformKind` | `Linux`、`Windows` | Server 描述、宿主权限与平台能力判断。 |
| `ClientPlatformKind` | `Windows`、`Linux`、`Android`、`iOS` | 登录请求、Device 注册、客户端兼容性与设备列表。 |

涉及位置：

- `Shared/RelaxKonOS.Protocol/Common/PlatformKind.cs`：已替换为语义明确的两个枚举。
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

### 5.2 首发应用目录

Mobile Shell 的导航项是能力类别，不是把桌面所有应用逐一缩小后塞进底栏。具体应用只有在 Server 同时声明能力、当前账号拥有权限且该布局状态具备可用操作路径时才出现。

| 类别 / 应用 | 目标工作流 | 首发优先级 | 手机与平板差异 |
| --- | --- | --- | --- |
| 主页（Home） | 连接健康、主机资源、近期操作和常用入口 | P0 | 手机显示可扫读的状态卡；平板将告警、趋势和近期操作并列。 |
| 文件（Files） | 浏览、上传、下载、新建、重命名、复制/移动、属性与删除确认 | P0 | 手机为列表→详情的导航栈；平板为位置/目录、列表、详情或预览多栏。 |
| 终端（Terminal） | 打开、恢复和关闭远端 PTY；输入命令与查看输出 | P0 PoC | 手机只聚焦一个会话并提供扩展键栏；平板可保留会话列表。 |
| 容器（Docker） | 容器、镜像、Stack、网络和卷的查看与受控操作 | P1 | 手机先呈现状态和单资源详情；平板增加日志/操作栏，不复制桌面表格。 |
| 进程与守护（Processes & Guardian） | 指标、受管工作负载、日志和重启等受控操作 | P1 | 手机用筛选列表；平板并列指标、资源和日志。 |
| 部署与 Web 服务（Deployments & Web） | 查看发布、任务状态、站点和服务操作 | P2 | 手机按向导分步完成；平板允许列表、详情和任务状态并列。 |
| 设置与诊断（More） | 账户、服务器连接、语言、主题、无障碍、日志导出、关于和登出 | P0（设置骨架） | 使用同一偏好模型；平板仅扩大内容列，不将设置拆成窗口。 |

Git、隧道/代理、防火墙、证书、注册表、浏览器和代码编辑等桌面应用不属于首批 Mobile Shell。将来只有在能定义移动端独立、可完成且安全的任务流后才加入管理目录；不得因为桌面端已有图标而添加只读或不可操作的占位入口。

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

### 6.2 国际化、主题与无障碍

#### 国际化

- 界面默认源语言为 `en`；语言包目标为 `en`、`zh` 与 `ja`。缺失翻译回退到 `en`，不回退到硬编码文本或其他语言。
- **语言包目录使用语言级限定符**（`values` / `values-zh` / `values-ja`），与 `app/build.gradle.kts` 的 `androidResources.localeFilters`（`en`、`zh`、`ja`）一致；`res/xml/locales_config.xml` 同样只声明语言级标签——写成 `zh-CN` 会让系统「应用语言」页声称本应用只支持这一种中文。只有一份中文译文，所以任何 `zh-*`（含繁体脚本与各区域变体）都必须落到它——写成 `values-zh-rCN` 会让 `zh-TW`/`zh-HK` 落回英文，这是一条回归。中文与日文以外的一切系统语言落到 `values`，即英文；这条规则由资源目录本身承担，Kotlin 侧不写「猜设备语言」的分支。
- 所有可见文案放入 Android resource；Compose 不得保留用户可见字符串字面量。使用 `stringResource`、复数资源和带命名参数的格式化文本，不通过字符串拼接构造句子。
- 日期、时间、数字、文件大小与排序使用用户 locale。Server 返回的稳定状态码、错误码和枚举由客户端映射为本地化文案；不得要求 Server 返回某一种自然语言的显示字符串。
- 默认跟随系统应用语言；“更多 → 语言”可显式覆写，并以 Android AppCompat locale API 持久化。**跟随系统时的取值规则**：设备语言为中文（任意变体）用中文、日文用日文、其余一切语言用英文。语言切换后允许 Activity 重建，但登录会话和当前安全操作不得丢失或被重复提交。
- 使用逻辑方向 `start`/`end`，不用 `left`/`right`；首发虽未承诺 RTL 语言，布局、图标镜像和导航顺序必须能够支持 RTL。测试至少覆盖 `en`、`zh`（含 `zh-TW` 这类繁体变体，它们必须落到同一份中文译文而不是英文）、`ja`、伪语言扩展长度与 RTL 预览。

#### 多主题与无障碍

设置提供 **跟随系统、浅色、深色** 三种颜色模式，并提供独立的“提高对比度”开关。模式优先级为用户显式选择 > 系统设置；未选择时跟随系统。主题偏好属于客户端体验设置，M1 先本地持久化，只有在跨设备偏好契约已定义后才能同步到 Workspace。

- 基于 Material 3 `ColorScheme` 和语义设计令牌（`primary`、`surface`、`error`、`outline` 等）实现；业务页面不得直接写入十六进制颜色、特定背景色或仅靠颜色表达状态。
- Android 12+ 的动态颜色只能作为“跟随系统”模式的可选输入，且必须通过对比度和状态色验证；浅色、深色和高对比度方案必须有确定的 RelaxKonOS 回退调色板。
- 正文、图标、焦点、禁用、错误、成功和危险操作在每个主题中都须满足 Android/Material 可读性要求；错误/告警同时使用文本、图标或形状，而不是单一颜色。
- 支持系统字号/Display size、TalkBack 的内容描述和合理焦点顺序；48dp 最小命中区域是下限而非视觉尺寸。不得通过关闭字体缩放来保持布局。

#### 视觉语言与界面构成

本节把「现代化」写成可验收的约束，而不是风格形容词。三套调色板在 `ui/theme/Palette.kt`，间距、圆角与排版尺度在 `ui/theme/Tokens.kt`。

- **背景**：整个窗口只有一层背景，由 `ui/common/AppBackdrop.kt` 绘制——一条纵向渐变加两处屏外柔光，全部取自主题令牌。高对比度下渐变收敛为接近纯色：可读性优先于层次。Shell 的 `Scaffold` 透明、底部导航与 rail 不透明，因此背景只画一次，登录页与 Shell 共用同一层。
- **表面分层**：卡片是 `surface` 底色 + 1dp `outlineVariant` 细边 + 零阴影。不使用 elevation 投影——在带色调的背景上它会形成灰色光晕，反而压低其上文字的可读性；深色主题下这条规则同样成立。只有真正悬浮在可滚动内容之上的长操作进度卡使用投影。
- **形状与间距**：圆角四档（10 / 14 / 20 / 28dp）+ 全圆角胶囊；按钮与输入框一律 14dp（`shapes.medium`），卡片 20dp，hero 面板与对话框 28dp。间距只用 4 / 8 / 12 / 16 / 24 / 32 一组尺度，页面不再各自写 `dp` 字面量。
- **语义色**：Material 3 没有 success / warning / info 角色，因此这三组颜色由 `MaterialTheme.relaxKon` 提供。业务页只选择 tone（`Neutral` / `Primary` / `Success` / `Warning` / `Danger` / `Info`），由调色板决定该 tone 在浅色、深色、高对比度下分别是什么；页面不得出现十六进制颜色。调色板必须显式填满 `surfaceContainer*` 阶梯——留空会沿用 Material 基线，把紫灰调带进蓝色体系。
- **状态不靠颜色单独表达**：`StatusChip` 始终携带文字，图标只是补充。主机指标的阈值只在一处定义（`loadTone`）：<70% 正常、70–90% 需要留意、≥90% 视为问题。
- **页面构成**：每个目的地以同一个 `ScreenHeader` 开头（标题 + 可选副标题 + 可选返回圆钮）；`onBack` 为 `null` 时是平板分栏形态，此时不渲染返回钮——分栏没有「返回」可退。设置类页面用「分组标题 + 分组卡」（`SectionLabel` + `SectionGroup`），页面标题不在卡内重复。列表项统一为「图标徽章 + 标题 + 两行细节 + 尾部动作」，不再把多个事实用分隔符拼成一行。
- **图标与桌面端同源**：Android 不自绘图标集。桌面端 `Client/RelaxKonOS.Client/Assets` 是唯一来源，`Tools/Mobile/sync-desktop-icons.py` 把它镜像到 `res/drawable-nodpi/`，`ui/icons/DesktopIcons.kt` 是按语义寻址的唯一映射点。改图标必须走这个脚本，不得在 `res/drawable/` 里另画一套。
  - 镜像分两组，职责与桌面端一致（桌面 Dock 用应用图标，Explorer 工具栏与文件类型用字形）：`ic_app_*` 是自带上色的圆角方形应用图标，只用于顶层目的地与产品标识（底部导航、rail 头部、登录页品牌标记、首页 hero）；`ic_sys_*` 是透明彩色字形，用于页面内的表头、列表行、按钮与文件类型。
  - 位图**不做主题染色**：素材自带配色与形状，套上主题色会被压成剪影。`IconBadge` 因此用中性底色而不是 `primaryContainer`——蓝底衬黄文件夹就是染色徽章的典型坏结果。
  - 素材为 128px 见方、放在 `drawable-nodpi`（不带密度，最大 32dp 槽位在 xxxhdpi 上仍 1:1 采样）；192px 原件会多出约两兆谁也用不到的像素。
  - 桌面端确实没有的两类：上传/下载（桌面把这两个动作放在无图标的菜单里）与密码显隐（桌面登录页没有该控件）。前者取集合中语义最近的箭头，后者保留 `res/drawable/ic_password_visible|hidden.xml` 自绘矢量。
  - 文件列表按扩展名选图标，规则逐条移植自桌面端 `ExplorerIconAssetResolver` / `ExplorerFileIconKindResolver`；`ui/icons/DesktopIconsTest.kt` 固化其判定顺序（先整名、再具体扩展名、最后归类），两个客户端对同一文件必须给出同一图形。
  - 不引入 `androidx.compose.material:material-icons-extended`：Compose BOM 2025.12.01 已不再解析该坐标（图标库在 Compose 1.7 冻结），且会显著增大包体。

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

- 凭据使用 Android Keystore；日志、诊断、崩溃报告不得写入密码、JWT、refresh token 或命令中的秘密。登录密码与宿主管理员密码分属两个独立保险箱，指纹只解封本机密文，不产生任何免验证凭据；管理员密码的保存条件是 [`RelaxKonOS.Security.md`](../../../docs/platform/RelaxKonOS.Security.md) §5.2 定义的受限例外，详细设计见 [`RelaxKonOS.Mobile.V1.Design.md`](./RelaxKonOS.Mobile.V1.Design.md) §5。
- 文件上传通过 Android 系统文件选择器取得内容流，不将用户文件路径假定为可访问的本地路径；下载直接落盘到本机下载目录（API 29+ 经 `MediaStore` 写入共享的 `Download/RelaxKonOS`，无需权限、无需对话框；API 23–28 没有这条写入路径，落入应用自身的外部 `Download` 目录），完成提示给出实际落盘位置，不以分享面板作为中转。
- 选中图片时在详情页直接显示图片，字节先落入应用私有的预览缓存：键为 `服务器 + 远端路径 + 文件大小 + 修改时间` 的摘要，容量上限 64 MB 并按最久未查看淘汰，随 `cacheDir` 被系统回收；长度与服务器报告的大小不符的副本一律不视为命中，因此崩溃或中断留下的半截文件不会被当成图片显示。呈现是分级的，且**传输与解码各自分级**：服务端 `GET /files/thumbnail`（最长边 320 px）与原图传输**并行**发起，小图先到就先上屏（画在进度条下方），因此慢链路与大图也能先看到能认出的画面；服务端给不出小图（415 `thumbnail-unsupported`，或旧服务端根本没有该路由）则回落到本地 96 px 解码。随后按当前显示框的像素尺寸解码详细图替换（像素总量上限 400 万，按显示框降采样，永不超过 heap 可承受的范围）。全屏查看器只对已缓存的文件重新解码到屏幕尺寸，不产生第二次传输。
- 预览不为自己申请提权：选中受保护路径时以「拒绝作答」的授权提供者发起请求，服务端拒绝后界面显示「读取该文件需要管理员密码」并提供「授权并预览」，只有用户点击该按钮才弹出管理员密码提示（§5.3.8 的「无显式作答不提权」同样适用于自动发起的读取）。缩略图预取同样如此：它是应用自己的主意而非用户的请求，因此**恒以「拒绝作答」发起且永不等待**，受保护路径在这次预取上一律得到 `elevation-required` 并被静默忽略——需要授权的是随后那张能看见按钮、需要用户按下的下载卡片。
- 仅按功能声明网络、通知等权限；不申请存储全盘访问、常驻后台或无关权限。
- 高风险动作（删除、停止/重启服务、部署、关闭终端）必须二次确认；确认文本必须包含具体目标。

---

## 8. 实施阶段与验收

| 阶段 | 交付 | 退出条件 |
| --- | --- | --- |
| M0：架构准备 | Kotlin/Compose 项目骨架、Protocol 平台语义拆分、Android 认证/HTTP、应用可启动 | Desktop 回归；Android 模拟器和真机均能显示登录页。 |
| M1：自适应 Shell | 登录、Keystore、能力读取、Compact/Medium/Expanded 导航和首页 | 手机/平板旋转、分屏、重启后布局正确；无凭据泄露。 |
| M2：核心操作 | 文件、状态、终端真机 PoC 与断线恢复 | Android 手机和两种平板尺寸完成登录、文件上传、终端 reconnect。 |
| M3：管理工作台 | Docker、守护/进程、日志与明确确认操作 | 仅显示受支持能力；失败、取消、超时均有可理解状态。 |
| M4：发布准备 | 图标/启动页、AAB 签名、崩溃诊断、Android 发布文档 | Release 包可安装；签名材料不入库；设备矩阵通过。 |

最小人工设备矩阵为：一台 Android 手机（竖/横屏）、一台约 8 英寸平板和一台约 11 英寸平板；每台验证登录、网络切换、软键盘、旋转、后台恢复、终端及危险操作确认。自动测试覆盖 ViewModel、布局状态计算、能力过滤、认证状态机、HTTP/Hub reconnect 和 Protocol 序列化。

---

## 9. 后续 iOS/iPadOS 接入

在 Android M2 之后才评估 `Client/RelaxKonOS.Client.iOS/`。它遵循 Protocol wire contract；页面、导航、网络和平台服务由 iOS 原生框架实现，不复制服务端业务逻辑。

---

## 10. 实施约束清单

- 新的移动功能先定义/修正 Protocol，再实现 Server（若需要），最后实现 Kotlin data layer 与 Compose 页面。
- Compose 页面不得直接执行 HTTP、拼接路由或访问桌面窗口管理器。
- 不以“能编译”为移动兼容性依据；终端、文件选择、软键盘、后台恢复和横竖屏必须在真实手机与平板验证。
- 不为保留当前错误的平台语义添加兼容 shim；直接更新仓库内所有调用者、测试和文档。
- 所有实施进展、已验证设备、已知限制和待决风险记录在同目录的 [`RelaxKonOS.Mobile.Progress.md`](./RelaxKonOS.Mobile.Progress.md)，而不是在本文中混写实现状态。
