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
- 文本全部走 Android resource（`values`、`values-zh-rCN`、`values-ja`），无用户可见字符串字面量；方向使用 `start`/`end`。
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

## 已知限制

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
- 使用 Gradle `:app:testDebugUnitTest` 执行单元测试：布局断点、认证状态机、保险箱加解密与 AAD 绑定、
  提权单次重试、生物识别能力映射、wire 时间戳解析、导航栈、能力门控。

最近一次校验（2026-09-22）：

- `:app:assembleDebug` 与 `:app:testDebugUnitTest` 均 BUILD SUCCESSFUL，Kotlin 编译零警告；
  产物为 `app/build/outputs/apk/debug/app-debug.apk`。
- 单元测试 12 个测试类、107 个用例，0 失败 / 0 错误 / 0 跳过：`CredentialVaultTest` 18、`AuthSessionTest` 14、
  `ElevationRepositoryTest` 13、`WireTest` 12、`BiometricCapabilityTest` 11、`ConnectionProfileStoreTest` 9、
  `MobileNavigatorTest` 10、`ProblemCodesTest` 8、`LayoutStateTest` 4、`TopDestinationTest` 4、`FilesRepositoryTest` 3、
  `RecentOperationJournalTest` 1。
- 密码可见性两态与主机指标按需采样落地后再次校验（同日）：`:app:assembleDebug` BUILD SUCCESSFUL，产物同上；
  `:app:testDebugUnitTest` 112 个用例，111 通过，其中 `ProblemCodesTest` 新增的 5 个用例（4xx/5xx 判定、命名问题码、
  凭据判定边界）全部通过。唯一失败的是 `MobileNavigatorTest` 的「首个子路由 push 触发路由观察者」用例——它来自工作树中
  尚未完成的导航改动，与本轮改动无关。
- 残留警告一处：`app/build.gradle.kts` 的 `resourceConfigurations` 在 AGP 9.4.1 已弃用，官方替代是
  `androidResources.localeFilters`。本模块暂未迁移（AGP 9.4.1 仍支持该属性），待确认新 DSL 精确签名后再改。
- 以上均为本机 JVM 单元测试与打包验证；设备矩阵验证仍未执行（见上）。

## 后续步骤

- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证指纹登录
  （成功/取消/失败/锁定）、指纹提权、软键盘遮挡、旋转、后台恢复与危险操作确认文本。
- V1-C 终端：SignalR 客户端与 PTY 渲染。
- V1-E 其余域：Docker、部署、守护（按服务端能力门控）。
