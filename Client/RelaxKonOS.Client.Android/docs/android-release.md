# Android 构建、调试与发布

## 本地环境

Android 客户端是独立的 Kotlin + Jetpack Compose Gradle 工程，位于 `Client/RelaxKonOS.Client.Android`，不使用 .NET Android workload 或 Avalonia。

工程的 [`gradle/wrapper/gradle-wrapper.properties`](../gradle/wrapper/gradle-wrapper.properties) 将发行包固定为本机 `D:\environments\gradle-9.7.1-all.zip`（`file:///D:/environments/gradle-9.7.1-all.zip`）。首次执行环境初始化时，`Initialize-AndroidEnvironment.ps1` 从该本地包解压 Gradle；构建过程不应改回在线 Gradle 分发地址。

```powershell
pwsh Tools/Mobile/Initialize-AndroidEnvironment.ps1
pwsh Tools/Mobile/Build-Android.ps1 -Configuration Debug
pwsh Tools/Mobile/Run-Android.ps1
pwsh Tools/Mobile/Debug-Android.ps1
```

`Build-Android.ps1` 会检查 Android SDK、Gradle 9.7.1 与 JDK 21，然后运行 `:app:assembleDebug` 或 `:app:assembleRelease`。若只允许使用已缓存的依赖，传入 `-Offline`。Gradle 依赖版本在 `Client/RelaxKonOS.Client.Android/build.gradle.kts` 与 `app/build.gradle.kts` 管理，而非 `Directory.Packages.props`。

APK 输出位置为：

```text
Client/RelaxKonOS.Client.Android/app/build/outputs/apk/debug/app-debug.apk
```

运行 Kotlin 单元测试：

```powershell
Push-Location Client/RelaxKonOS.Client.Android
D:\environments\Android\gradle-9.7.1\bin\gradle.bat :app:testDebugUnitTest --no-daemon
Pop-Location
```

## 调试服务器地址

Android 模拟器访问开发机服务器使用 `http://10.0.2.2:5090`；真机必须填写开发机在局域网中可访问的 HTTP(S) 地址。M0 Manifest 暂时允许 cleartext，以便开发服务器联调。发布前改为只接受 HTTPS，并配置网络安全策略与证书验证。

## Release

M0 不产生可发布签名包。M4 前执行以下约束：

- 签名 keystore、别名、口令和 CI secret 均不入库。
- 使用 CI 注入 keystore 路径与密码，输出签名 AAB，再由独立设备矩阵验证安装。
- 发布前移除 cleartext 支持，验证 Android Keystore、文件分享 URI、后台恢复及危险操作确认。
- 至少覆盖一台手机、一台 8 英寸平板和一台 11 英寸平板的竖横屏、软键盘、网络切换与恢复。
