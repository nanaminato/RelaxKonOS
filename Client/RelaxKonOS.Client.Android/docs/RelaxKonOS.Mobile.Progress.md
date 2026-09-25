# RelaxKonOS Mobile 实施进展

## M0：Kotlin / Jetpack Compose 基线（已完成）

- 移除 `RelaxKonOS.Client.Mobile`、Avalonia Mobile、.NET for Android Host、其 XAML 页面、NuGet 包与 `.sln` 项目条目。
- `Client/RelaxKonOS.Client.Android` 现在是独立 Gradle 工程，使用 Kotlin、Jetpack Compose、Material 3 与 Android SDK。
- `AppCompatActivity` 承载 Compose 登录页与 Shell；按可用 Compose 宽度的 dp 切换 Compact、Medium、Expanded 导航布局。
- Kotlin `RelaxKonApi` 调用当前 `/api/v1.0` wire contract，并发送 `clientPlatform: "android"`。其字段名、路由与枚举值必须与 `RelaxKonOS.Protocol` 同步。
- 布局断点单元测试覆盖 599.99、600、839.99、840 dp。
- Android 构建与安装脚本已切换至 Gradle APK 输出；不再要求 .NET Android workload。

## V1：初版功能（对应 `RelaxKonOS.Mobile.V1.Design.md` §7）

| 阶段 | 状态 | 落点 |
| --- | --- | --- |
| V1-A 认证闭环 | 已实现 | 连接档案、登录、token 单次刷新重试、连接保险箱 + 指纹登录、登出 |
| V1-B 自适应 Shell | 已实现 | 顶级导航、断点布局、能力门控、首页、`more/*` 子页 |
| V1-C 核心操作 | 部分 | 文件浏览、上传/下载、创建、重命名、复制、移动与删除已完成；终端（SignalR + PTY）未实现 |
| V1-D 提权闭环 | 已实现 | 提权对话框、提权保险箱 + 指纹提权、危险操作确认、5 分钟窗口 |
| V1-E 管理工作台 | 部分 | 系统监控与进程管理已完成；Docker、守护、部署未实现 |

已实现细节：

- 两个独立保险箱（`VaultKind.Connection` / `VaultKind.Elevation`）：AES-256-GCM + Android Keystore，AAD 绑定
  `relaxkonos-vault|kind|serviceId|account`，永不合并、永不互相回填、不可移动密文。
- 生物识别四档（`Strong` / `WeakOnly` / `DeviceCredentialOnly` / `None`）与两种解锁模式（按次强生物识别
  `CryptoObject` 与 5 分钟设备解锁窗口），由 `unlockModeFor` 统一裁决。
- D1–D4 按设计落地：管理员密码仅强生物识别按次授权可保存；服务端拒绝提权即删除该条密码，不按错误码分支；
  网络错误、超时与 5xx 不删除。
- 提权严格保持 `capability + target + jti + 5 分钟`；客户端只做一次安全重试，token 变化即丢弃本地授权缓存。
- 文本全部走 Android resource（`values`、`values-zh`、`values-ja`），无用户可见字符串字面量；方向使用 `start`/`end`。
- 终端入口（`TopDestination.Terminal`）标记为未实现，因此不出现在导航中——设计 §8 禁止"不可用入口"。
- 文件上传使用 Android Storage Access Framework 的单个文档流，不申请宽泛的存储权限；上传和下载均显示
  已传输字节进度并可取消。**下载已改为直接落盘**，见下节；不再经过私有缓存与分享面板。
- 复制和移动会将提权范围限定为源与目标目录；Windows 驱动器根路径（例如 `C:\\`）在向上导航、创建目录
  与提权时保持正确的根分隔符。文件详情与列表共享操作对话框，手机详情页不再出现无响应的操作按钮。
- Expanded 首页现在显示本次进程内成功完成的文件与进程操作；记录最多 20 条，且不会落盘或包含密码、令牌、
  请求体和服务端诊断内容，因此不替代服务器审计日志。
- 连接密码的系统认证与保险箱写入现在先于认证态切换完成，避免登录页卸载后错失生物识别提示；成功登录后，
  主界面才会出现。
- 修复首次打开“更多”子页时路由栈未被 Compose 观察的问题；账户与安全、连接、外观、诊断和关于现在会在
  第一次点击后立即显示，无需先切换顶级导航。
- 密码输入（登录页与提权对话框）共用一个两态控件：默认掩码，点击尾部眼睛图标转为明文并保持在明文，
  再点一次回到掩码；可见性只存在于界面状态（`rememberSaveable`），不进会话、保险箱或任何文件。
- 首页与 `manage/monitor` 的主机指标走 `GET /system/performance/snapshot`。Android 没有 SignalR 客户端，
  服务端把这次读取当作一次有界 demand（覆盖建立差分基线所需的两个采样周期），因此没有实时订阅者时也能取到样本；
  只有在窗口内确实取不到样本（`503 performance-not-ready`）时才提示指标尚未就绪，而不再被误报为
  "无法连接到服务器"——`RelaxKonApi` 只在 5xx 响应体明确给出 RelaxKonOS 问题码时才把它当作 Problem。

## 登录与本地凭据（2026-09-23，对应 `RelaxKonOS.Mobile.LoginCredentials.Design.md`）

- 身份唯一键已演进为 `(serviceId, identifier)`：直连 `serviceId` 是规范化 URL，受管隧道使用安装 ID；`id` 与保险箱 `credentialKey`（`recordId`）由同一对值派生，临时 loopback 端口不参与身份。
- 四个概念分离为可单测的纯 Kotlin：`SelectedLogin` / `SavedCredentialState`（四态）/ `CredentialStatus`（状态行）/ `LoginDecision` + `decideLogin`（§5.1 决策表）。`core/auth/` 不依赖任何 Android 类型。
- 登录页改为单形态：密码框始终可见、`value` 只表示本次手动输入；「已保存密码」由密码框外的状态行（锁形图标 + 文案）表达；按钮文案随决策变化（`登录` / `连接` / `连接中…`）；缺字段或需要输入密码时把焦点移到对应输入框。原「简洁模式」与「指纹登录 / 改用密码」双按钮路径删除。
- 保存动作仍在认证成功之后（`AuthSession.login` 的 `afterLogin` 回调）；认证失败不触碰任何已存凭据，`401 invalid-credential` **不再删除**连接保险箱里的密码（§7.3，相对旧实现的行为变更）。保存失败提示为「已登录，但密码没有保存：<原因>」。
- 保存结果跨过登录页到 Shell 的界面切换：认证成功后发生的「未保存」提示由 `AppContainer.pendingNotice` 承载，在 Shell 顶部显示并可关闭；本地档案或凭据写入抛异常时，`AuthSession` 仍会发布已认证会话，不会永久停在「连接中…」。
- D5 落地：密钥永久失效由「自动删除」改为「标记作废、保留记录与密文、禁止读取」。`VaultRecord` 增加 `state`（`Sealed` / `Invalidated`）、`CredentialVault.markInvalidated` / `markAllInvalidated`、`VaultRecordInvalidatedException`；一个 `VaultKind` 共享一把 Keystore alias，因此 alias 失效会标记该保险箱的全部记录。用户手动登录成功并明确保存时，`VaultAccess.save` 会轮换一次失效 alias、重新请求授权并仅重新密封当前身份，避免每次登录都重复报「指纹已变更」。`beginOpen()` 与 `open()` 双双拒绝作废记录；保险箱文件格式升到 `RKV2`（每条记录多一个 state 字节），旧格式按版本不匹配降级为「无已保存凭据」，不写迁移。账户与安全页对作废记录标注原因；提权保险箱沿用同一规则，作废记录不再出现在「使用指纹确认」路径上。
- `mapKeyException` 现在把 `UserNotAuthenticatedException` 归为「当前不可用」而不是「已失效」。D5 之后误判会把一条好记录永久标死，所以「无法使用不是已失效的证据」必须在 Keystore 层也成立。
- 连接管理拆成两个互不替代的动作，且一律按 `(serviceId, identifier)` 成对生效：**忘记密码**（只删凭据、保留登录记录）与**删除登录记录**（删凭据 + 删该条登录）。按服务器全删的缺陷已修复。
- `SavedConnection` 更名为 `SavedLogin`，补 `id` / `displayName` / `hasSavedCredential` / `credentialKey`，档案文件格式升到 `RKC2`。`hasSavedCredential` 只是显示投影：启动时由 `AppContainer` 以保险箱记录复核修正，两者不一致时以保险箱为准；布尔值不构成安全边界。
- `CredentialUnlocked` 落地为「本进程内、窗口模式（D3）下已授权过的身份集合」：窗口内再次登录走 `VaultAccess.loadWithoutPrompt`，失败即回落为正常授权提示；按次强指纹模式恒为 false，不参与安全边界。
- 新增 `ProblemCodes.LOGIN_RATE_LIMITED`（`429 login-rate-limited`）与对应文案：登录被限流时说「登录尝试过于频繁」而不是通用拒绝；它不参与任何凭据删除判定。
- 三份 `strings.xml`（`values` / `values-zh` / `values-ja`）键集完全一致（现 279 键），并删除了不再使用的 `login_stored_credential_rejected`、`login_use_fingerprint`、`login_use_password`、`login_saved_credential`。
- **修复指纹保存与解封完全不可用**（真机报「设备的指纹已变更」）：`VaultKeyManager.generate` 只调用了 `KeyGenerator.init()` 配置策略，却从未调用 `generateKey()`，因此两个保险箱的 Keystore alias 从未被创建（该文件自 `58b4c64d` 起即如此）。随后 `beginSeal` / `beginOpen` 拿到的 `getKey()` 为 `null`，抛出的「密钥缺失」被界面统一呈现为「设备的指纹已变更」，与 debug / release 无关——两条构建路径是同一份代码。现在补上 `generateKey()`，`ensureKey` 在建钥后校验 alias 确实存在，并把**任何**建钥失败归类为 `VaultKeyUnavailableException`：「拿不到密钥」不是「指纹变了」，两者给用户的建议相反。
- 新增 debug-only 排障链路 `security/VaultDiagnostics.kt`（logcat tag `RelaxKonVault`，`adb logcat -s RelaxKonVault:D`）：记录 `canAuthenticate` 码（映射为 `SUCCESS` / `NONE_ENROLLED` / `NO_HARDWARE` 等名称——`BIOMETRIC_SUCCESS` 就是 `0`，原样打印会被读成失败）、`unlockMode` 裁决、密钥创建与 provider（StrongBox / TEE）选择、alias 存在性、`BiometricPrompt` 的结果码与返回文本，以及 Keystore 异常被分类前的原始类型与消息。能力探测与解锁模式只在结果**变化**时打印（探测本身仍每次调用都执行，不缓存结论），否则重组风暴会淹没关键行。日志只含保险箱种类、provider 决策、异常类与结果枚举，不含密码、账户、服务器地址、`Cipher` 或任何密钥材料；sink 由 `RelaxKonApplication` 仅在 debug 构建安装，release 构建保持未安装，`security` 包因此从不触碰 `android.util.Log`（`VaultDiagnosticsTest` 断言了这一点）。

## 服务器中心 G2 基础件（2026-09-25）

- 已有独立宿主资料、主机密钥固定、SSH 凭据域、JSch SSH/SFTP、loopback 隧道与连接解析规则；SSH 密码/私钥不复用登录或 API 提权保险箱。
- 新增无界面部署操作层：上传前验证签名 ZIP、RID、架构和逐文件摘要，经内置 SFTP 写入远端私有暂存目录，调用固定启动器，并按 `operationId` 查询权威回执。该层尚未接入 Compose 页面或应用级恢复协调器。
- 登录身份与传输地址已拆分。`SelectedLogin` / `SavedLogin` / `ConnectionProfileStore` / 连接与 debug 凭据都按 `(serviceId, identifier)`；`AuthSession` 另持有可重绑定的 `effectiveBaseUrl`，文件、指标、提权和上传等 API 调用读取当前地址。隧道换端口只更新传输地址，不改变登录记录、保险箱 AAD 或上传恢复归属。
- `RKC2` / `RKV2` 的二进制布局未改变：直连记录原先保存的 URL 本身就是 URL 型 `serviceId`；未增加旧端口键兼容分支。受管安装记录只允许保存安装 ID，不保存临时 loopback 地址。
- 已补 JVM 单元检查，覆盖受管登录档案持久化、隧道换端口后登录/凭据键不变、会话改用新端口，以及部署操作的上传、执行、回执与拒绝路径。本环境缺少 JDK、可执行 Gradle wrapper 及 Android SDK 接线，新增检查尚未在本轮执行；真机与真实 Windows/Linux 宿主验收仍未完成。

## 界面现代化（2026-09-23）

按 `RelaxKonOS.Mobile.Design.md` §6.2「视觉语言与界面构成」重做 Android 端的界面层。业务逻辑、导航、凭据与提权链路未改动。

- 新增设计令牌层：`ui/theme/Palette.kt`（四套调色板 + `RelaxKonColors` 语义色，经 `MaterialTheme.relaxKon` 读取）与 `ui/theme/Tokens.kt`（`Spacing` / `Radius` / `Layout`、`RelaxKonShapes`、`RelaxKonTypography`）。四套调色板都显式填满 `surfaceContainer*` 阶梯，避免 Material 基线紫灰调渗入；`RelaxKonOSTheme` 的对外 API（`ColorMode` / `AppLanguage` / `AppearanceState` / `applyAppLanguage` / `applyAppNightMode`）保持不变。
- 新增共享组件：`AppBackdrop`（整窗渐变 + 两处柔光，高对比度下近似纯色）、`ScreenHeader`、`StatusChip`（含 `loadTone` 阈值）、`MetricTile` / `MetricTrack` / `DiskRow`、`ListRow` / `IconBadge`、`EmptyState`、`SectionGroup` / `SectionLabel`；`SectionCard`、`ErrorBanner`、`ProgressSheet` 改为零阴影 + 细边表面。`ErrorBanner` 不再自带外边距，改由调用方决定（页面内与整窗浮层两种位置需要不同的内缩）。
- 界面改造：首页改为「身份 hero 卡 + 状态胶囊 + 指标磁贴 + 能力清单」（hero 替换原来的会话事实卡，服务器/账户/工作区/平台四个事实全部保留）；登录页加品牌标记与渐变；文件页改为位置栏 + 新建/上传双按钮 + 图标列表行；管理与更多改为分组图标行；性能页与进程页改用指标磁贴、胶囊与统一列表行；`more/*` 五个子页统一 `ScreenHeader` 与分组卡。`MainActivity` 只保留一处背景绘制，Shell 的 `Scaffold` 改为透明并补上 `safeDrawingPadding`。
- 新增 12 个自绘矢量图标（`ic_server` / `ic_folder` / `ic_file` / `ic_memory` / `ic_storage` / `ic_activity` / `ic_process` / `ic_upload` / `ic_download` / `ic_copy` / `ic_link`）。**未**引入 `material-icons-extended`：Compose BOM 2025.12.01 已不再解析该坐标（图标库在 Compose 1.7 起冻结），且会显著增大包体。**这套自绘矢量已在下一节被桌面端图标镜像整体取代。**
- `values/colors.xml` 与 `values-night/colors.xml` 的 `window_background` 改为对应渐变的首个色标，避免首帧 Compose 之前闪出不同底色。
- 未新增或删除任何字符串资源；三份 `strings.xml` 键集仍完全一致（279 键）。

校验：`:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，产物 `app/build/outputs/apk/debug/app-debug.apk`（13.3 MB）；单测 17 个测试类、158 个用例，0 失败 / 0 错误 / 0 跳过。**本次改动全部是界面层，尚未在真机上目视确认**：渐变与柔光在小屏上的观感、指标磁贴在窄屏（360dp）两列布局下的换行、以及深色与高对比度两套调色板的实际对比度，都需要按设备矩阵复核。

## 图标与语言（2026-09-23）

对应两条要求：「图标使用和桌面端相同的图标」与「语言跟随系统，中文、日文以外均使用英文」。业务逻辑、导航栈、凭据与提权链路未改动。

- **图标不再自绘**：来源改为桌面端 `Client/RelaxKonOS.Client/Assets`，由新增的 `Tools/Mobile/sync-desktop-icons.py` 镜像到
  `app/src/main/res/drawable-nodpi/`（128px 见方，113 个 PNG，1.71 MB）。分两组，职责与桌面端一致：`ic_app_*`（24 个，来自
  `Assets/AppIcons`，另有 `RelaxKonOS-client-icon.png` 作为品牌图）是自带上色的圆角方形应用图标，只用于品牌标记与顶层目的地；
  `ic_sys_*`（89 个，来自 `Assets/Icons/Explorer`）是透明彩色字形，用于表头、列表行、按钮与文件类型。桌面端的
  `Assets/Icons/Explorer/debug-grid.png` 是 2048×768 sprite sheet 而非图标，脚本按名跳过。脚本支持 `--check`（只报差异、
  不写文件，有差异即以非零退出码失败），用于在提交前或未来的 CI 中防止图标与桌面端漂移。
- **单一映射层**：新增 `ui/icons/DesktopIcons.kt`，按语义（导航、页面头、动作、文件系统）暴露资源 id，并提供 `DesktopIcon`
  组件。业务代码不再直接引用 `R.drawable.*`——现在只剩 `PasswordTextField.kt` 的两个自绘矢量（见下）。
  `TopDestination` 与 `ManageDomain` 的 `icon: ImageVector` 改为 `@param:DrawableRes iconRes: Int`（用 `@param:` 形式，
  否则 Kotlin 2.x 会报「注解目前只作用于值参数」警告）。
- **不做主题染色**：素材自带配色，`Icon` 的 tint 会把它们压成剪影，因此统一用 `Image`。`IconBadge` 容器相应从
  `primaryContainer` 改为中性 `surfaceContainerHigh`（蓝底衬黄文件夹是染色徽章的典型坏结果），文件列表行也不再按目录/文件分色。
  首页能力清单的勾选图标改为 6dp 成功色圆点——桌面图标集里没有对勾，凭空造一个会引入第二套图标语言。
- **桌面端确实没有的两类**：上传/下载（桌面把这两个动作放在无图标的菜单里，故取集合中语义最近的箭头）与密码显隐
  （桌面登录页没有该控件，保留 Android 自绘的 `ic_password_visible|hidden.xml`）。这是有意例外，不是遗漏。
- **文件类型判定移植**：`DesktopIcons.fileFor(name, isDirectory)` 的判定顺序（整名 → 具体扩展名 → 归类）逐条移植自桌面端
  `ExplorerIconAssetResolver` / `ExplorerFileIconKindResolver`，由 `ui/icons/DesktopIconsTest.kt`（7 个用例）固化，
  两个客户端对同一文件必须给出同一图形。
- **语言规则落到资源目录**：`values-zh-rCN` 重命名为 `values-zh`，`app/build.gradle.kts` 改用语言级
  `androidResources.localeFilters`（`en` / `zh` / `ja`，同时消掉了 `resourceConfigurations` 的 AGP 弃用警告），
  `res/xml/locales_config.xml` 同步由 `zh-CN` 改为 `zh`——写成区域级会让系统「应用语言」页声称本应用只支持这一种中文。
  只有一份中文译文，`zh-TW`/`zh-HK` 等全部变体因此落到同一份中文而不是英文。Kotlin 侧不写猜设备语言的分支：
  `AppLanguage.SimplifiedChinese` 更名为 `Chinese`，`AppLanguageTest`（6 个用例）固化各变体归属。
- 删除 11 个不再使用的自绘矢量（`ic_activity` / `ic_copy` / `ic_download` / `ic_file` / `ic_folder` / `ic_link` / `ic_memory` /
  `ic_process` / `ic_server` / `ic_storage` / `ic_upload`）；`appearance_language_note` 等三语文案同步更新，键集仍为 279 一致。

校验：`:app:assembleDebug` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，Kotlin 编译零警告；单测 19 个测试类、
171 个用例，0 失败 / 0 错误 / 0 跳过（本轮新增 `DesktopIconsTest` 7、`AppLanguageTest` 6）。图标资源共 113 个文件、1.71 MB，
APK 由 13.3 MB 增至 14.5 MB，`sync-desktop-icons.py --check` 复跑输出 "113 icons are up to date"（幂等）。**尚未做真机目视确认**：
底部导航 26dp 槽位上的彩色应用图标在深色与高对比度主题下的对比度、文件列表行改用彩色字形后的信息密度，都需要按设备矩阵复核。

## 登录链路修复与 debug 明文兜底（2026-09-23）

对应两条要求：「正确的用户名密码也必须卸载重装才能登录」与「debug 构建且机器没有强密码时能否保存密码（只存一条、可读取）」。

**根因：地址探测被当成了门禁。** `LoginViewModel.submit` 原先先做一次服务端地址探测（`ServerEndpointDiscovery`，
`OPTIONS /api/v1.0/auth/login`），只有探测通过才发登录请求；探测失败直接写一条提示并 `return`，请求根本不发出。
持久化的服务器地址一旦探测不过——改了端口或协议、中间有代理、探测路径被拒——正确凭据永远没有机会被发送；
卸载重装会清掉 `noBackupFilesDir` 下的地址，于是「重装就能登录」，与症状完全自洽。修法是把它降回**优化**：
`submit` 现在无条件进入 `submitResolved`，探测在后台并行跑，只用于润色「连不上」时的文案（`loginTransportMessage`
按探测结论分派 `login_server_unavailable` / `login_server_invalid` / `error_connectivity`），且结果只在地址未被
用户改过时才应用。这条原则已写进代码注释：**地址解析是优化，永远不是门禁。**

**顺带修掉两条「登录页点击后永久卡住」的路径**（与主 bug 同属「点击没反应」这一症状族）：
`signInWithSavedPassword` 原先在两个分支静默 `return`（既没有可用记录、也没有可用解锁方式时，点击等于什么都没发生），
且 `isLoggingIn` 不在 `finally` 里清除——一次异常就会让整张表单（三个输入框 + 按钮）永久 disabled。现在无凭据可用时
给出明确文案（`login_saved_password_unavailable`）并把焦点移回密码框，`isLoggingIn` 一律在 `finally` 复位。
`VaultAccess` 的三个入口（`save` / `load` / `loadWithoutPrompt`）原先不捕获未知平台异常，现在统一经
`unnameableVaultFailure(operation, error)` 归类为 `UnlockFailure.Unknown`，并在 `catch (Exception)` 之前先重抛
`CancellationException`（否则协程取消会被吞成「未知失败」）。未知异常映射为 `Unknown` 而不是 `KeyInvalidated` 至关重要：
前者提示「暂时不可用」，后者会把好记录 `markAllInvalidated` 永久标死，两者给用户的建议相反。

**debug 明文兜底（设计文档 §8.1，决策 D10）。** `AndroidKeyStore` 的 `setUserAuthenticationParameters` 只接受
`AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`，没有「弱生物识别」标志位；设备完全没有锁屏时
（`BiometricCapability.None`）连接保险箱在结构上无法建钥，`unlockMode()` 恒为 `null`，「保存密码」整体不可用。
为此新增 `security/DebugCredentialStore.kt`：仅 debug 构建、仅当设备无任何可用锁屏且用户已开启指纹保存总开关时，
把**一条**凭据明文写入 `noBackupFilesDir/debug-credential.bin`（格式 `RKD1`）。边界用代码结构锁死：release 下
`AppContainer.debugCredentials` 恒为 `null`，调用点全部走可空接收者；`save` 即替换，永远只有一条。界面每个出现处
都写明「未加密」（登录页勾选框与提示、状态行、连接列表、安全页），忘记密码 / 删除登录记录 / 关闭指纹总开关 /
清空全部四条删除路径全部接通。安全页会单独列出这条明文记录并标红，诊断导出含 `debugCredentialRecord=` 一行。

界面与文案：三份 `strings.xml` 各新增 5 键（现 285 键，键集一致）：`login_remember_hint_debug`、
`login_no_lock_screen_debug`、`login_saved_password_debug`、`login_credential_saved_debug`、
`connections_saved_password_debug`、`account_security_debug_record_note`。

调试诊断：登录决策链路新增 debug-only 跟踪点，走既有 `VaultDiagnostics` 通道（`adb logcat -s RelaxKonVault:D`，
release 下 sink 不安装即 no-op）：`login.decision`（走哪条路径、为什么）、`login.discovery`（探测结论）、
`login.result`（服务端结论）、`login.debug-store`（明文落盘）、`login.stored-path.failed`。

校验（2026-09-23）：`:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks`
均 BUILD SUCCESSFUL，Kotlin 编译零警告；单测 **21 个测试类、194 个用例，0 失败 / 0 错误 / 0 跳过**
（本轮新增 `DebugCredentialStoreTest` 13、`VaultAccessTest` 6，`SavedCredentialStateTest` 由 6 增至 10）；
产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB。**根因修复与兜底逻辑尚未真机复现**：
需要在「旧 APK 直接覆盖安装」的机器上确认登录不再被地址探测拦截，并在无锁屏设备上确认明文兜底可用。

## 文件传输落盘与回执归属（2026-09-24）

**用户报告**：小屏（Compact/Medium）上点「下载」不立即生效，按下返回键之后才生效；平板端（Expanded）能生效但弹的是
分享面板，文件并没有落到本机目录。

**根因有两层，缺一不可**：

1. **动作由界面托管，而不是由状态托管。** 下载完成后的投递写成 `FilesScreen` 内的
   `LaunchedEffect(viewModel.downloadReady)`，而 Compact/Medium 的文件详情是**压栈页**——点开文件后 `FilesScreen`
   已不在组合中，`LaunchedEffect` 自然不会跑；等用户返回、列表重新入栈时它才以当时的非空 `downloadReady` 触发，
   于是表现成「按返回键才生效」。**同一个缺陷还吞掉了另外两样东西**：`ProgressSheet`（进度卡）与 `ErrorBanner`
   （错误横幅）此前也只在 `FilesScreen` 里渲染，所以详情页上启动的传输**全程没有任何进度或失败提示**。
2. **投递目标本身就是错的。** 下载只写进私有 `cacheDir`，完成后再用 `FileProvider` 交给分享面板——文件从未进入
   本机存储，用户必须为每个文件在系统面板里挑一次目标。

**修复**：

- **落盘**：新增 `data/DownloadStore.kt`（`DownloadTarget` = `open` / `commit` / `discard`）。API 29+ 经 `MediaStore`
  写入共享的 `Download/RelaxKonOS`：无权限、无对话框，文件名冲突由 MediaStore 追加计数解决，写入前 `IS_PENDING=1`，
  `commit()` 清零后才对全设备可见；失败或取消则 `discard()` 删除，不留半截文件（旧实现会留下残包）。API 23–28
  没有无权限的公共下载目录写入路径，落入应用自身的外部 `Download` 目录——这不是降级为缓存，而是本机共享存储上
  用户可删的真实文件；应用内文案始终报出实际落盘位置。
- **传输层不再认识「文件」**：`RelaxKonGateway.download` 的目标参数由 `java.io.File` 改为 `core/net` 的
  `fun interface DownloadSink`，`RelaxKonApi` 只在响应码通过后才 `sink.open()`——被拒绝的下载不会创建任何文件。
- **回执归属移出页面**：新增 `FileMessageBanner` 与 `FileTransferCard`，由 `MobileNavHost.FilesDestination`（三种
  布局、两条 files 路由都在组合中）渲染；`ProgressSheet` 与 `ErrorBanner` 从 `FilesScreen` 移除。
- **上传两处修正**：目录在协程启动前取定，上传途中导航不会把文件投到别的目录；文档名、大小与流都在
  `Dispatchers.IO` 上取——云端文档的 `query` 要几百毫秒、`openFile` 可能数秒，旧实现是主线程同步调用。名称与大小
  取自一次 `OpenableColumns` 查询（`DISPLAY_NAME` + `SIZE`），失败才回落 `AssetFileDescriptor.length`。
- **回执语气**：`UiMessage` 增加 `tone`（默认 `Danger`），`ErrorBanner` 接受同一 tone 取色。下载/上传/新建目录的
  成功回执改用 `StatusTone.Success`——旧实现把「已上传 x」显示在红色错误横幅里。
- **删除**：`FileProvider` 声明、`res/xml/file_paths.xml` 与 `files_share_download` 文案随分享面板一并移除；
  应用不再需要任何文件分享 URI。三份 `strings.xml` 各删 1 键、增 4 键（`files_downloaded`、
  `files_download_failed`、`files_download_default_name`、`files_upload_unreadable`），现 288 键且键集一致。

**未做真机验证**：`MediaStore` 落盘、`Download/RelaxKonOS` 的可见性与重名追加计数需要在真机上确认一次。

## 图片预览与预览缓存（2026-09-24）

- **选中图片即显示**：详情页在属性卡片上方增加「预览」卡片。是否可预览由文件名扩展名判定，名字没有可用扩展名时才看服务端
  推断的 `mimeType`；`image/svg+xml`、`image/tiff`、`image/x-icon` 被显式排除——平台本来就解不出来，把它们算作图片
  只会让应用自己刚承诺可预览的文件立刻报「无法显示」。目录永不预览。
- **字节进缓存而非 Downloads**：新增 `data/ImagePreviewCache.kt`，图片流写进 `cacheDir/previews`。键是
  `服务器 + 远端路径 + 大小 + 修改时间` 的 SHA-1：远端路径本身不能当文件名（Windows 路径含 `\` 与 `:`），而改动过的
  文件必须换键，否则会把旧图当新图给用户看。复用前校验文件长度等于服务端报告的大小，因此崩溃或中断留下的半截文件不会
  成为命中（重命名做不到这件事——它分不清完整与截断）。容量上限 64 MB，`trim` 按最久未查看淘汰。
- **两次解码的阶梯**：新增 `data/ImageDecoder.kt`。第一趟只解码 96 px 见方并立即上屏（再大的照片也几乎不耗时），
  第二趟按当前显示框的像素尺寸重新解码并替换。降采样倍率由纯函数 `previewSampleSize` 决定，先保证「不小于显示框」
  （观感），再用 400 万像素上限兜底（内存：4000×3000 的 `ARGB_8888` 是 48 MB；50000×50000 的 25 亿像素还会让
  `Int` 乘积溢出成负数，所以这条比较在 `Long` 上做）。EXIF 方向在解码器里应用，竖拍照片不会横躺。
- **显示框由界面上报**：卡片与全屏查看器各自用 `BoxWithConstraints` 量出自己的像素尺寸交给 ViewModel。质量在一次
  选中内只升不降——打开查看器只对**已缓存**的文件重新解码到屏幕尺寸（不产生第二次传输），关闭不会把刚解出的图降回去。
- **代次守卫**：取消一个协程不等于停住它——取消下载不会中断阻塞读，旧任务仍会写完并走到收尾。因此每次新选中都递增
  `previewGeneration`，旧任务对 `preview`、缓存与磁盘的每一次写入都以「我的代次仍是当前的」为前提；过期的任务只留下
  一个长度不符的文件，而长度校验会把它当作未命中。
- **预览不自动提权**：`ElevationAnswerProvider.Declines`（加在 `ElevationRepository.kt`）给出的答案与「用户关掉对话框」
  完全相同——`null`，于是 `withPathElevation` 原样返回服务端的拒绝。选中受保护文件得到的是「读取该文件需要管理员密码」
  与「授权并预览」，只有点击才弹管理员密码提示。
- 详情页内容改为可滚动：预览加在原属性卡之上后，手机横屏会超出窗口；标题栏留在滚动区之外，返回入口不会滚走。
- 新增 7 个字符串键（`files_preview_title/open/loading/progress/failed/elevation/authorize`），三份 `strings.xml`
  现 295 键且键集一致。

## 服务端缩略图（2026-09-24）

- **为什么加它**：此前的「先缩略图」省的只是**解码时间**——图片仍是一次完整请求，第二次点同一张图才命中缓存。
  要让大图与慢链路上的**传输本身**渐进，只能让服务端先给一张小图。新增
  `GET /api/v1.0/files/thumbnail?path=&maxEdge=`（路由常量 `FileApiRoutes.Thumbnail`；`maxEdge` 缺省 256、允许
  16–1024、越界 400 `invalid-size`）。
- **渲染**：新增 `RelaxKonOS.Server/Files/ImageThumbnailRenderer.cs`。先 `Image.Identify` 读头，声明像素数超过 6400 万
  直接拒绝（解压炸弹在读第一个像素之前就被挡下）；解码只取一帧（`DecoderOptions.MaxFrames = 1`，否则一张动图会按
  帧数放大内存）；EXIF 方向在缩放前应用；缩放用 `ResizeMode.Max` + Lanczos3，小图不放大。输出格式跟着**像素**走
  而不是容器：`CloneAs<Rgba32>()` + `ProcessPixelRows` 逐像素扫描 alpha，有透明像素出 PNG，否则出 JPEG(q85)。
  **这一条踩过坑**：`image.PixelType.AlphaRepresentation` 对同一张透明 PNG 报 `bpp=32 alpha=[]`（编码侧不声明 alpha），
  据此判断会把透明图当 JPEG 输出、透明区域直接丢成黑底，因此改为按像素扫描。并发上限
  `MaxDegreeOfParallelism = min(4, CPU)`，几个缩略图请求占不满一个宿主。
- **不是错误的错误码**：内容不是本服务能解码的图像时返回 415 `thumbnail-unsupported`，客户端读作「没有缩略图」并
  照旧拉原图（`ProblemCodes.THUMBNAIL_UNSUPPORTED`）。受保护路径与 `download` 完全同构：`elevation-required` → 提权后重试。
- **客户端**：`RelaxKonGateway.thumbnail(...): ApiResult<ByteArray>`——唯一归宿是内存，因此不是 `DownloadSink`；
  `RelaxKonApi` 把它与 `download` 共用的传输逻辑抽成 `streamInto(route, query, sink, onProgress)`，
  `FilesRepository.thumbnail` 走同一个 `withPathElevation`，因此与下载**同权限、同范围**（文件自身路径，不扩大到目录）。
  `FilesViewModel` 在确认没有缓存命中后**并行**发起预取（`prefetchThumbnail`，`ElevationAnswerProvider.Declines`，
  永不等待、永不弹窗）：到货即画在进度条下方（`ImagePreview.Downloading.thumbnail`），原图落地后再充当详细图解码的
  占位，省掉本地那趟 96 px 解码。代次守卫沿用既有规则——过期、或已经进入 `Ready` 的缩略图一律丢弃，绝不把已解码的
  照片换回小图。
- **许可判定**：`SixLabors.ImageSharp` 3.1.12。Six Labors Split License v1.0 第 2 条把「用于 Open Source 或
  Source-Available 许可的软件」划入 **Apache-2.0** 分支，本项目即 Source-Available，故可用；3.1.x 是首个提供
  `DecoderOptions.MaxFrames` 的稳定线，纯托管、无原生依赖。判定依据已写进 `THIRD_PARTY_NOTICES.md` 与
  `Directory.Packages.props`。
- 客户端未新增字符串键（复用预览既有文案），三份 `strings.xml` 仍 295 键且键集一致。

## 进程属主显示为 null 的修复（2026-09-24）

- **现象**：手机端「管理 → 进程」每一行的细节都是 `CPU 0.7% · 2.91 GB · null`。
- **`userName` 确实是 null**：服务端只有 Linux 才解析 `/proc/<pid>/status` 的 `Uid:` 并映射 `/etc/passwd`；Windows 走的
  `ProcessSampler` 拿到的是 `LinuxProcessDetails(null, 0)`，所以线上的字段本身就是 null，这是既有设计
  （[`RelaxKonOS.TaskManager.md`](../../../docs/applications/RelaxKonOS.TaskManager.md) §319/§334）。
- **屏幕上那四个字母是客户端读出来的**：`RelaxKonApi` 原来写的是 `item.optString("userName").takeIf { it.isNotBlank() }`，
  而 Android 的 `org.json` 对 JSON null 返回的是**字面字符串** `"null"`（`JSON.toString(JSONObject.NULL)` →
  `String.valueOf(NULL)`；参考实现 `org.json:json` 在同一输入上返回空串），非空判断拦不住它，于是 `"null"` 进了
  `RemoteProcess.userName`，再被细节行的 `listOfNotNull` 原样拼上屏幕。
- **改法**：新增 `JSONObject.optNullableString(name)`（`if (isNull(name)) null else optString(name).takeIf { it.isNotBlank() }`），
  与既有 `optNullableLong` 同处、同形状；`isNull` 在 Android 与参考实现上语义一致（缺键与 JSON null 都算 null），
  因此这条修复不依赖运行时怪癖。5 处可空字符串读取全部换过去：`userName`、`mimeType`
  （`FileSystemEntryDto.MimeType` 可空，此前文件详情的 MIME 同样会显示 `null`），以及 RFC 7807 判定用的
  `type` / `problemCode` / `traceId`。
- **顺带修掉一个真缺陷**：`problemCode` 被判成 `"null"` 时 `ProblemCodes.namesContractCode` 会认为服务端**已经给出**
  契约码，于是 `readsAsProblem` 把没有问题的 5xx 读成 `Problem`——违反 §5.8.2（服务端未给出问题码时，5xx 不构成
  凭据判定，也不该被当作契约答复）。
- 界面不需要改：细节行是 `listOfNotNull(CPU, 内存, userName).joinToString(" · ")`，属主为空时这一段自然消失。
  Windows 服务端上属主恒为空，要让那里也显示属主要求服务端补 WMI / P/Invoke（TaskManager 文档 §334）。
- 未新增字符串键，三份 `strings.xml` 仍 295 键且键集一致。

## 已知限制

- 连接保险箱的密码被服务端拒绝时**不**删除（§7.3）。代价是：用户已在服务端改密后，本机那条旧密码会一直失败，直到手动输入新密码并在成功后保存覆盖它。这是有意选择——删除只在用户显式「忘记密码」或「删除登录记录」时发生。
- 终端、Docker、部署与守护进程管理尚未实现，对应入口不会出现。
- 连接保险箱在 `WEAK_ONLY` 设备上只能用**设备凭据**（锁屏 PIN/图案/密码）解封：Android Keystore 不存在
  "弱生物识别"标志位，`setUserAuthenticationParameters` 只接受 `AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`。
  仅有弱生物识别且未设置锁屏的设备无法创建窗口密钥，此时降级为输入密码，且**不删除**任何已存记录。
  详见 `RelaxKonOS.Mobile.V1.Design.md` §5.4。
- **完全没有锁屏的设备上，「保存密码」只由 debug 构建的明文兜底提供**（设计文档 §8.1 / 决策 D10）：该记录以**明文**
  存于 `noBackupFilesDir`，界面上每个出现处都标注「未加密」。release 构建与有锁屏的设备一律不具备这条路径。
- `androidx.lifecycle:lifecycle-viewmodel-compose` 固定在 `2.10.0`：`2.11.0` 起声明 `minCompileSdk 37`，而本模块编译于
  `compileSdk 36`。升 `compileSdk` 到 37 后应一并升回。
- 真机、横竖屏旋转、分屏、软键盘、后台恢复与 Insets 仍需设备矩阵验证。
- 若设备已由另一签名安装相同包名，`adb install -r` 会报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` 并保留旧包；
  需要先执行 `adb uninstall app.relaxkonos.mobile`。

## 构建与校验

- 使用 [`Tools/Mobile/Build-Android.ps1`](../../../Tools/Mobile/Build-Android.ps1) 运行 Gradle `:app:assembleDebug`；
  需要 Android SDK、JDK 21 与 Gradle 9.7.1。
- 使用 Gradle `:app:testDebugUnitTest` 执行单元测试：登录决策与身份键、凭据四态与显示投影、布局断点、
  认证状态机、保险箱加解密与 AAD 绑定、提权单次重试、生物识别能力映射、wire 时间戳解析、导航栈、能力门控、
  桌面端文件图标判定顺序、应用语言归属。
- 图标资源来自桌面端，不手工维护：改了 `Client/RelaxKonOS.Client/Assets` 下的图标后，运行
  [`Tools/Mobile/sync-desktop-icons.py`](../../../Tools/Mobile/sync-desktop-icons.py) 重新镜像到 `app/src/main/res/drawable-nodpi/`；
  提交前可用 `--check` 让它只报差异而不写文件（有差异时返回非零退出码）。

最近一次校验（2026-09-23）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL；单测 158 个、0 失败；
  产物为 `app/build/outputs/apk/debug/app-debug.apk`。
- 真机（SM-S9380）已安装该 APK；指纹链路排障用 `adb logcat -s RelaxKonVault:D`。
- 单元测试 12 个测试类、107 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 18、`AuthSessionTest` 14、
  `ElevationRepositoryTest` 13、`WireTest` 12、`BiometricCapabilityTest` 11、`ConnectionProfileStoreTest` 9、
  `MobileNavigatorTest` 10、`ProblemCodesTest` 8、`LayoutStateTest` 4、`TopDestinationTest` 4、`FilesRepositoryTest` 3、
  `RecentOperationJournalTest` 1。
- 密码可见性两态与主机指标按需采样落地后再次校验（同日）：`:app:assembleDebug` BUILD SUCCESSFUL，产物同上；
  `:app:testDebugUnitTest` 112 个用例，111 通过，其中 `ProblemCodesTest` 新增的 5 个用例（4xx/5xx 判定、命名问题码、
  凭据判定边界）全部通过。唯一失败的是 `MobileNavigatorTest` 的「首个子路由 push 触发路由观察者」用例——它来自工作树中
  尚未完成的导航改动，与本轮改动无关。
- 残留警告一处：`app/build.gradle.kts` 的 `resourceConfigurations` 在 AGP 9.4.1 已弃用，官方替代是
  `androidResources.localeFilters`。**该警告已在下一节「图标与语言」中随迁移消除**。
- 以上均为本机 JVM 单元测试与打包验证；设备矩阵验证仍未执行（见上）。

最近一次校验（2026-09-23，登录决策与本地凭据模型落地后）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告；
  产物为 `app/build/outputs/apk/debug/app-debug.apk`。
- 单元测试 15 个测试类、149 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 25、`AuthSessionTest` 16、
  `ConnectionProfileStoreTest` 16、`ProblemCodesTest` 15、`ElevationRepositoryTest` 13、`WireTest` 12、
  `BiometricCapabilityTest` 11、`MobileNavigatorTest` 10、`LoginDecisionTest` 7、`SavedCredentialStateTest` 6、
  `SelectedLoginTest` 6、`LayoutStateTest` 4、`TopDestinationTest` 4、`FilesRepositoryTest` 3、
  `RecentOperationJournalTest` 1。上一轮记录中失败的 `MobileNavigatorTest` 用例（工作树里未完成的导航改动）
  本轮已随该改动完成而通过。
- 本轮新增覆盖：§5.1 决策表逐行（含「字段不全优先于一切」「手动输入的密码覆盖已保存凭据」）；凭据四态与状态行映射；
  身份键归一化不折叠大小写、同服务器不同账号不碰撞；按对删除只影响一条登录且不触碰同服务器其他账号；
  `markInvalidated` 后记录与密文仍在、两个读取入口都被拒绝、重新保存可替换；保险箱格式升版后旧文件降级为空；
  认证失败不触发登录后的凭据步骤；限流既有专门文案也不被当成凭据拒绝。
- 仍未做设备矩阵验证：指纹授权、取消、失败、锁定、指纹变更后的「标记作废」提示都需要真机确认（见「后续步骤」）。

最近一次校验（2026-09-23，登录链路修复与 debug 明文兜底落地后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告（release 变体一并编译，确认 `BuildConfig.DEBUG` 为假时 `debugCredentials` 的空分支成立）。
- 单测 21 个测试类、194 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 26、`AuthSessionTest` 18、
  `ConnectionProfileStoreTest` 16、`ProblemCodesTest` 15、`DebugCredentialStoreTest` 13、`ElevationRepositoryTest` 13、
  `WireTest` 12、`BiometricCapabilityTest` 11、`MobileNavigatorTest` 10、`SavedCredentialStateTest` 10、
  `LoginDecisionTest` 7、`DesktopIconsTest` 7、`SelectedLoginTest` 6、`VaultAccessTest` 6、`AppLanguageTest` 6、
  `LayoutStateTest` 4、`TopDestinationTest` 4、`ServerEndpointDiscoveryTest` 3、`VaultDiagnosticsTest` 3、
  `FilesRepositoryTest` 3、`RecentOperationJournalTest` 1。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 285 键且键集一致。

最近一次校验（2026-09-24，文件传输落盘与回执归属修复后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest --rerun-tasks` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告。
- 单测 22 个测试类、201 个用例，0 失败 / 0 错误 / 0 跳过：本轮新增 `DownloadStoreTest` 6（重名追加计数、扩展名
  拆分、点文件、服务端名取末段路径）与 `FilesRepositoryTest` 的下载用例 1（sink 落字节 + 进度回调 + 不请求提权），
  `FilesRepositoryTest` 由 3 增至 4。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 288 键且键集一致。
- **尚未真机验证**：`MediaStore` 落盘后 `Download/RelaxKonOS` 在系统「文件 / 下载」中的可见性、重名追加计数的实际
  文案，以及大屏与手机两端「点下载立即出现进度卡、完成后横幅报出落盘路径」。

最近一次校验（2026-09-24，图片预览与预览缓存落地后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告。
- 单测 24 个测试类、230 个用例，0 失败 / 0 错误 / 0 跳过。本轮新增：
  - `ImagePreviewCacheTest` 15：键对同一修订稳定、大小或修改时间变化即换键、不同服务器不同键、远端路径里的 `\` 与 `:`
    不会进入文件名；长度不符、空文件不算命中，未报告长度但有字节仍算命中；超预算时按最久未用先淘汰且满足预算即停、
    单个超过整个预算的条目被淘汰、`trim` 只删自己命名的文件（同目录下的其他文件不动）。
  - `ImageDecoderTest` 14：比显示框小的图不放大、4000×3000 取 1/2、8000×6000 取 1/4、结果不小于显示框且再降一级就
    小于显示框、全景图与 50000×50000 扫描图受 400 万像素上限约束、显示框或图片尺寸为 0 时退回原尺寸；扩展名与
    `mimeType` 的判定优先级、宿主不可解码的图片类型被排除、目录永不预览。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.4 MB；三份 `strings.xml` 均为 295 键且键集一致。
- **尚未真机验证**：先缩略图后详细图的实际过渡观感与大图首次载入耗时、EXIF 方向（竖拍照片应正立）、
  受保护路径的「授权并预览」只在点击后弹窗、预览缓存在 `cacheDir` 中的实际占用与淘汰效果。

最近一次校验（2026-09-24，服务端缩略图落地后）：

- 服务端：`dotnet build RelaxKonOS.Server.Tests` 成功；`RelaxKonOS.Server.Tests.exe --thumbnails-only` 22 条
  `PASS THUMBNAILS:` 全绿，全量序列（已在 `FileServiceChecks` 之后插入 `ImageThumbnailChecks.Run`）以
  `RelaxKonOS.Server backend verification passed.` 结束。
- 客户端：`:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，
  Kotlin 编译零警告；单测 24 个测试类、232 个用例，0 失败 / 0 错误 / 0 跳过。本轮 `FilesRepositoryTest` 由 4 增至 6：
  透传 `maxEdge` 并回传字节而非字节数、受保护路径按**文件自身路径**以 `read` 提权一次后重试（范围不扩大到目录）。
- 产物 `app/build/outputs/apk/debug/app-debug.apk` 14.5 MB。
- **尚未真机验证**：320 px 小图放进 220 dp 卡片的实际观感（偏软就该调大）、`thumbnail-unsupported` 的回落路径、
  受保护路径上「预取不弹窗、下载卡片才弹窗」、以及「小图先到 → 详细图替换」的过渡。
- `ImageDecoder.decode(bytes)` 在 JVM 单测里无法覆盖（`BitmapFactory` 在测试运行时是 stub），只能真机验证或引入
  影子实现，本轮没有为它造这个轮子。

最近一次校验（2026-09-24，可空字符串读取修复后）：

- `:app:assembleDebug`、`:app:compileReleaseKotlin` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告。
- 单测 24 个测试类、232 个用例，0 失败 / 0 错误 / 0 跳过；产物 `app/build/outputs/apk/debug/app-debug.apk` 14.5 MB；
  三份 `strings.xml` 均为 295 键且键集一致。
- 本轮**没有**新增用例，这是有意的：这条缺陷只在 Android 运行时复现——参考实现的 `org.json` 对 JSON null 返回空串，
  只有 Android 的 `JSON.toString(JSONObject.NULL)` 才给出 `"null"`，因此 JVM 单测区分不了修复前后（写出来是一个恒过的
  断言）。防护放在唯一入口 `optNullableString` 与其注释上：可空字符串不再有第二种读法。
- **待真机确认**：进程行的属主段消失（Windows 服务端恒无属主）、文件详情的 MIME 不再显示 `null`。

## 后续步骤

- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证指纹登录
  （成功/取消/失败/锁定）、**新录入指纹后记录变为「已失效」而不是消失**、删除记录后重建的指纹、指纹提权、软键盘遮挡、
  旋转、后台恢复与危险操作确认文本。
- 真机验证时顺带确认窗口模式（`WEAK_ONLY` / `DEVICE_CREDENTIAL_ONLY`）下五分钟内免二次确认，以及窗口过期后回落为
  正常授权提示而不是「已失效」。
- V1-C 终端：SignalR 客户端与 PTY 渲染。
- V1-E 其余域：Docker、部署、守护（按服务端能力门控）。
