# RelaxKonOS Mobile 实施进展

## M0：架构准备（进行中）

已完成：

- 增加 `RelaxKonOS.Client.Foundation`，含首批 typed HTTP 登录客户端、内存 Token 会话、连接元数据存储抽象与能力集合。
- 增加平台无关的 `RelaxKonOS.Client.Mobile`，包含登录页、Mobile Shell 和按可用宽度计算的 Compact / Medium / Expanded 布局状态。
- 增加 `RelaxKonOS.Client.Android` Host；它只包含 Activity、Manifest 和生命周期/原生服务边界，没有桌面 WindowManager 或业务页面逻辑。
- 将 Protocol 中含混的 `PlatformKind` 直接替换为 `HostPlatformKind` 与 `ClientPlatformKind`，并更新 Server、Desktop 和验证调用方。Android 登录将发送 `Android` 客户端平台值。
- 增加边界值布局检查：599.99、600、839.99、840 dp。

当前限制：

- M0 不保存密码或 refresh token。`AndroidSecureCredentialStore` 是刻意不可用的接口边界；M1 只能以 Android Keystore 加密实现它。
- 文件选择、分享、SignalR、终端、能力读取和管理页属于后续阶段，尚未宣称可用。
- 当前开发机已有 Android SDK API 37、Build Tools 36.0.0 和本地 Gradle 9.7.1 压缩包，但未安装 .NET Android workload。因此 Foundation/Mobile 与 Server 验证可运行；Android APK 构建等待从批准的本地 workload 源安装该 workload。

## 已验证

- `dotnet build Shared/RelaxKonOS.Protocol/RelaxKonOS.Protocol.csproj --no-restore`
- `dotnet build RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore`

待 `.NET Android` workload 准备完毕后，依次执行 `Tools/Mobile/Build-Android.ps1`、安装 APK、手机/平板旋转与网络切换验证。
