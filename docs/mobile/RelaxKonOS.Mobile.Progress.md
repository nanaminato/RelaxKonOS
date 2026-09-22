# RelaxKonOS Mobile 实施进展

## M0：Kotlin / Jetpack Compose 基线（进行中）

已完成：

- 移除 `RelaxKonOS.Client.Mobile`、Avalonia Mobile、.NET for Android Host、其 XAML 页面、NuGet 包与 `.sln` 项目条目。
- `Client/RelaxKonOS.Client.Android` 现在是独立 Gradle 工程，使用 Kotlin、Jetpack Compose、Material 3 与 Android SDK。
- 原生 `ComponentActivity` 承载 Compose 登录页与 Shell；按可用 Compose 宽度的 dp 切换 Compact、Medium、Expanded 导航布局。
- Kotlin `RelaxKonApi` 调用当前 `/api/v1.0/auth/login` wire contract，并发送 `clientPlatform: "android"`。其字段名、路由与枚举值必须与 `RelaxKonOS.Protocol` 同步。
- 添加 Kotlin 的布局断点单元测试，覆盖 599.99、600、839.99、840 dp。
- Android 构建与安装脚本已切换至 Gradle APK 输出；不再要求 .NET Android workload。

当前限制：

- M0 不保存密码、access token 或 refresh token。M1 应使用 Android Keystore 加密凭据，并在 Kotlin data layer 中实现刷新和登出。
- 文件选择、分享、SignalR、终端、能力读取和管理页属于后续阶段，尚未宣称可用。
- 真机、横竖屏旋转、分屏、软键盘、后台恢复和 Insets 仍需设备矩阵验证。
- 若设备已由另一签名安装相同包名，`adb install -r` 会报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` 并保留旧包；需要先执行 `adb uninstall app.relaxkonos.mobile`。

## 构建与校验

- 使用 [`Tools/Mobile/Build-Android.ps1`](../../Tools/Mobile/Build-Android.ps1) 运行 Gradle `:app:assembleDebug`；需要 Android SDK、JDK 21 与 Gradle 9.7.1。
- 使用 Gradle `:app:testDebugUnitTest` 执行 Kotlin 布局边界测试。

## 后续步骤

- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证登录、网络切换、软键盘、旋转、后台恢复与危险操作确认。
- M1 以 Android Keystore、Activity Result 文件选择器和 Compose 导航页面扩展当前实现。
