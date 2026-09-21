# RelaxKonOS Mobile 实施进展

## M0：架构准备（进行中）

已完成：

- 增加 `RelaxKonOS.Client.Foundation`，含首批 typed HTTP 登录客户端、内存 Token 会话、连接元数据存储抽象与能力集合。
- 增加平台无关的 `RelaxKonOS.Client.Mobile`，包含登录页、Mobile Shell 和按可用宽度计算的 Compact / Medium / Expanded 布局状态。
- 增加 `RelaxKonOS.Client.Android` Host；它只包含 Activity、Manifest 和生命周期/原生服务边界，没有桌面 WindowManager 或业务页面逻辑。
- 将 Protocol 中含混的 `PlatformKind` 直接替换为 `HostPlatformKind` 与 `ClientPlatformKind`，并更新 Server、Desktop 和验证调用方。Android 登录将发送 `Android` 客户端平台值。
- 增加边界值布局检查：599.99、600、839.99、840 dp。
- Android Host 在 `Resources/values/styles.xml` 中声明 `Theme.AppCompat` 后代的 `RelaxKonOSMobileTheme`，并挂到 `MainActivity` 的 `[Activity]` 特性上；Mobile 固定 `RequestedThemeVariant="Dark"`，与 Shell 的深色表面一致。

### Android 启动失败（已修复）

`Avalonia.Android.AvaloniaActivity` 继承 `AppCompatActivity`，而 AppCompat 要求 Activity 使用 `Theme.AppCompat` 后代主题；否则 `AppCompatActivity.setContentView` 立刻抛 `IllegalStateException: You need to use a Theme.AppCompat theme (or descendant) with this activity.`。此前 `AndroidManifest.xml` 与 `[Activity]` 都没有 `android:theme`，Avalonia.Android 包自身也不附带任何主题资源，所以进程在 `MainActivity.OnCreate` 阶段就退出（表现为点击图标即闪退，Avalonia 连第一帧都没起）。它与 `AvaloniaMainActivity<TApp>` / `AvaloniaMainActivity` 的选择无关，两种写法都会崩。

同一轮还发现第二个缺陷：Avalonia Fluent 主题默认跟随系统变体，模拟器为浅色时登录页用深色文字绘制在 Shell 固定的深色底（`#10151F` / `#1B2433`）上，标题与状态文字几乎不可见。现已固定为深色变体。

当前限制：

- M0 不保存密码或 refresh token。`AndroidSecureCredentialStore` 是刻意不可用的接口边界；M1 只能以 Android Keystore 加密实现它。
- 文件选择、分享、SignalR、终端、能力读取和管理页属于后续阶段，尚未宣称可用。
- 仅在模拟器验证启动与登录页渲染；真机、横竖屏旋转、分屏、软键盘、后台恢复、Insets 与系统返回键行为都未实现或未验证。
- 设备上若已存在由其他工具链（例如 IDE 部署）以不同 debug 签名密钥安装的同一包，`adb install -r` 会报 `INSTALL_FAILED_UPDATE_INCOMPATIBLE` 并**保留旧包**，此后启动的仍是旧代码。需先 `adb uninstall app.relaxkonos.mobile`。

## 已验证

编译与校验：

- `dotnet build Shared/RelaxKonOS.Protocol/RelaxKonOS.Protocol.csproj --no-restore`
- `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore`
- `dotnet build Client/RelaxKonOS.Client.Android/RelaxKonOS.Client.Android.csproj -c Debug -p:AndroidSdkDirectory=D:\environments\Android\Sdk -p:JavaSdkDirectory=D:\environments\JDK\jdk-21`（0 警告 0 错误）
- `dotnet build Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj`（0 错误；`Client/RelaxKonOS.Client/Apps/Docker/Views/DockerImageMirrorsView.axaml` 的 AVLN3001 警告为既有问题）
- `dotnet run --project Client/RelaxKonOS.Client.Mobile.Tests` → `Mobile layout checks passed.`

设备：

- 模拟器 `emulator-5554`（x86_64，Android 16 / API 36）：APK 安装并启动，`MainActivity` 取得焦点，logcat 无 `AndroidRuntime` / `MonoDroid` 致命异常，登录页正确渲染且文字可读。

## 后续步骤

- 本机 JDK 21 位于 `D:\environments\JDK\jdk-21`；`Tools/Mobile/Build-Android.ps1` 与 `Run-Android.ps1` 需要显式传入或通过 `JAVA_HOME` 指向它（工作负载不接受默认的 JDK 25）。
- 按设计文档 §8 的最小设备矩阵补齐真机验证：一台手机（竖/横屏）、约 8 英寸与约 11 英寸平板，逐台验证登录、网络切换、软键盘、旋转、后台恢复与危险操作确认。
