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
  `relaxkonos-vault|kind|serverUrl|account`，永不合并、永不互相回填、不可移动密文。
- 生物识别四档（`Strong` / `WeakOnly` / `DeviceCredentialOnly` / `None`）与两种解锁模式（按次强生物识别
  `CryptoObject` 与 5 分钟设备解锁窗口），由 `unlockModeFor` 统一裁决。
- D1–D4 按设计落地：管理员密码仅强生物识别按次授权可保存；服务端拒绝提权即删除该条密码，不按错误码分支；
  网络错误、超时与 5xx 不删除。
- 提权严格保持 `capability + target + jti + 5 分钟`；客户端只做一次安全重试，token 变化即丢弃本地授权缓存。
- 文本全部走 Android resource（`values`、`values-zh`、`values-ja`），无用户可见字符串字面量；方向使用 `start`/`end`。
- 终端入口（`TopDestination.Terminal`）标记为未实现，因此不出现在导航中——设计 §8 禁止"不可用入口"。
- 文件上传使用 Android Storage Access Framework 的单个文档流，不申请宽泛的存储权限；上传和下载均显示
  已传输字节进度并可取消。下载保留于私有缓存，完成后只通过短时 `FileProvider` URI 交给系统打开/分享面板。
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

- 身份唯一键落到 `(serverUrl, identifier)`：新增 `core/auth/SelectedLogin.kt`，归一化只做「去首尾空白 + 去地址结尾斜杠」，**不折叠大小写**；`id` 与保险箱 `credentialKey`（`recordId`）由同一对值派生，不会出现两套键。
- 四个概念分离为可单测的纯 Kotlin：`SelectedLogin` / `SavedCredentialState`（四态）/ `CredentialStatus`（状态行）/ `LoginDecision` + `decideLogin`（§5.1 决策表）。`core/auth/` 不依赖任何 Android 类型。
- 登录页改为单形态：密码框始终可见、`value` 只表示本次手动输入；「已保存密码」由密码框外的状态行（锁形图标 + 文案）表达；按钮文案随决策变化（`登录` / `连接` / `连接中…`）；缺字段或需要输入密码时把焦点移到对应输入框。原「简洁模式」与「指纹登录 / 改用密码」双按钮路径删除。
- 保存动作仍在认证成功之后（`AuthSession.login` 的 `afterLogin` 回调）；认证失败不触碰任何已存凭据，`401 invalid-credential` **不再删除**连接保险箱里的密码（§7.3，相对旧实现的行为变更）。保存失败提示为「已登录，但密码没有保存：<原因>」。
- 保存结果跨过登录页到 Shell 的界面切换：认证成功后发生的「未保存」提示由 `AppContainer.pendingNotice` 承载，在 Shell 顶部显示并可关闭；本地档案或凭据写入抛异常时，`AuthSession` 仍会发布已认证会话，不会永久停在「连接中…」。
- D5 落地：密钥永久失效由「自动删除」改为「标记作废、保留记录与密文、禁止读取」。`VaultRecord` 增加 `state`（`Sealed` / `Invalidated`）、`CredentialVault.markInvalidated` / `markAllInvalidated`、`VaultRecordInvalidatedException`；一个 `VaultKind` 共享一把 Keystore alias，因此 alias 失效会标记该保险箱的全部记录。用户手动登录成功并明确保存时，`VaultAccess.save` 会轮换一次失效 alias、重新请求授权并仅重新密封当前身份，避免每次登录都重复报「指纹已变更」。`beginOpen()` 与 `open()` 双双拒绝作废记录；保险箱文件格式升到 `RKV2`（每条记录多一个 state 字节），旧格式按版本不匹配降级为「无已保存凭据」，不写迁移。账户与安全页对作废记录标注原因；提权保险箱沿用同一规则，作废记录不再出现在「使用指纹确认」路径上。
- `mapKeyException` 现在把 `UserNotAuthenticatedException` 归为「当前不可用」而不是「已失效」。D5 之后误判会把一条好记录永久标死，所以「无法使用不是已失效的证据」必须在 Keystore 层也成立。
- 连接管理拆成两个互不替代的动作，且一律按 `(serverUrl, identifier)` 成对生效：**忘记密码**（只删凭据、保留登录记录）与**删除登录记录**（删凭据 + 删该条登录）。`ConnectionProfileStore.remove(serverUrl)` 的「按服务器全删」缺陷修复。
- `SavedConnection` 更名为 `SavedLogin`，补 `id` / `displayName` / `hasSavedCredential` / `credentialKey`，档案文件格式升到 `RKC2`。`hasSavedCredential` 只是显示投影：启动时由 `AppContainer` 以保险箱记录复核修正，两者不一致时以保险箱为准；布尔值不构成安全边界。
- `CredentialUnlocked` 落地为「本进程内、窗口模式（D3）下已授权过的身份集合」：窗口内再次登录走 `VaultAccess.loadWithoutPrompt`，失败即回落为正常授权提示；按次强指纹模式恒为 false，不参与安全边界。
- 新增 `ProblemCodes.LOGIN_RATE_LIMITED`（`429 login-rate-limited`）与对应文案：登录被限流时说「登录尝试过于频繁」而不是通用拒绝；它不参与任何凭据删除判定。
- 三份 `strings.xml`（`values` / `values-zh` / `values-ja`）键集完全一致（现 279 键），并删除了不再使用的 `login_stored_credential_rejected`、`login_use_fingerprint`、`login_use_password`、`login_saved_credential`。
- **修复指纹保存与解封完全不可用**（真机报「设备的指纹已变更」）：`VaultKeyManager.generate` 只调用了 `KeyGenerator.init()` 配置策略，却从未调用 `generateKey()`，因此两个保险箱的 Keystore alias 从未被创建（该文件自 `58b4c64d` 起即如此）。随后 `beginSeal` / `beginOpen` 拿到的 `getKey()` 为 `null`，抛出的「密钥缺失」被界面统一呈现为「设备的指纹已变更」，与 debug / release 无关——两条构建路径是同一份代码。现在补上 `generateKey()`，`ensureKey` 在建钥后校验 alias 确实存在，并把**任何**建钥失败归类为 `VaultKeyUnavailableException`：「拿不到密钥」不是「指纹变了」，两者给用户的建议相反。
- 新增 debug-only 排障链路 `security/VaultDiagnostics.kt`（logcat tag `RelaxKonVault`，`adb logcat -s RelaxKonVault:D`）：记录 `canAuthenticate` 码（映射为 `SUCCESS` / `NONE_ENROLLED` / `NO_HARDWARE` 等名称——`BIOMETRIC_SUCCESS` 就是 `0`，原样打印会被读成失败）、`unlockMode` 裁决、密钥创建与 provider（StrongBox / TEE）选择、alias 存在性、`BiometricPrompt` 的结果码与返回文本，以及 Keystore 异常被分类前的原始类型与消息。能力探测与解锁模式只在结果**变化**时打印（探测本身仍每次调用都执行，不缓存结论），否则重组风暴会淹没关键行。日志只含保险箱种类、provider 决策、异常类与结果枚举，不含密码、账户、服务器地址、`Cipher` 或任何密钥材料；sink 由 `RelaxKonApplication` 仅在 debug 构建安装，release 构建保持未安装，`security` 包因此从不触碰 `android.util.Log`（`VaultDiagnosticsTest` 断言了这一点）。

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

## 已知限制

- 连接保险箱的密码被服务端拒绝时**不**删除（§7.3）。代价是：用户已在服务端改密后，本机那条旧密码会一直失败，直到手动输入新密码并在成功后保存覆盖它。这是有意选择——删除只在用户显式「忘记密码」或「删除登录记录」时发生。
- 终端、Docker、部署与守护进程管理尚未实现，对应入口不会出现。
- 连接保险箱在 `WEAK_ONLY` 设备上只能用**设备凭据**（锁屏 PIN/图案/密码）解封：Android Keystore 不存在
  "弱生物识别"标志位，`setUserAuthenticationParameters` 只接受 `AUTH_BIOMETRIC_STRONG` 与 `AUTH_DEVICE_CREDENTIAL`。
  仅有弱生物识别且未设置锁屏的设备无法创建窗口密钥，此时降级为输入密码，且**不删除**任何已存记录。
  详见 `RelaxKonOS.Mobile.V1.Design.md` §5.4。
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

## 后续步骤

- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证指纹登录
  （成功/取消/失败/锁定）、**新录入指纹后记录变为「已失效」而不是消失**、删除记录后重建的指纹、指纹提权、软键盘遮挡、
  旋转、后台恢复与危险操作确认文本。
- 真机验证时顺带确认窗口模式（`WEAK_ONLY` / `DEVICE_CREDENTIAL_ONLY`）下五分钟内免二次确认，以及窗口过期后回落为
  正常授权提示而不是「已失效」。
- V1-C 终端：SignalR 客户端与 PTY 渲染。
- V1-E 其余域：Docker、部署、守护（按服务端能力门控）。
