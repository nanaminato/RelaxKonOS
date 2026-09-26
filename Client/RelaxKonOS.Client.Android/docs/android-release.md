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

`Build-Android.ps1` 会检查 Android SDK、Gradle 9.7.1 与 JDK 21，然后运行 `:app:assembleDebug`。Release 必须显式提供未提交的签名配置文件；脚本不会生成未签名的 Release 包。若只允许使用已缓存的依赖，传入 `-Offline`。Gradle 依赖版本在 `Client/RelaxKonOS.Client.Android/build.gradle.kts` 与 `app/build.gradle.kts` 管理，而非 `Directory.Packages.props`。

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

## 本机 Release 签名与发布机

只指定一台受控 Android 发布机保存 release/upload keystore。日常开发机只用 debug keystore；桌面 PublisherServer 机器只导入已经签名的成品，绝不保存 Android 私钥或口令。

首次在发布机通过 Android Studio 的 **Build → Generate Signed Bundle / APK** 创建 keystore，或使用 `keytool` 创建。请把 keystore 放在加密磁盘的仓库外目录；使用密码管理器保存 keystore 密码、key 密码和 alias，并保存一份受控的加密离线备份。不要更换证书：Android 只允许使用同一签名证书覆盖安装更新。

创建仓库外的本机签名配置文件，例如 `E:\\RelaxKonSecrets\\android-release-signing.properties`：

```properties
storeFile=E:\\RelaxKonSecrets\\RelaxKonOS-upload.jks
storePassword=从密码管理器取得
keyAlias=relaxkonos-upload
keyPassword=从密码管理器取得
```

该文件和 keystore 都不得提交。项目 `.gitignore` 也会忽略项目目录内误放的 `release-signing.properties`、`.jks` 和 `.keystore`，但正确做法仍是让秘密留在仓库外。

在发布机生成供 Publisher 导入的已签名 APK、AAB 与校验清单：

```powershell
pwsh Tools/Mobile/Build-Android.ps1 `
  -Configuration Release `
  -ReleaseArtifacts `
  -SigningPropertiesPath E:\\RelaxKonSecrets\\android-release-signing.properties `
  -OutputDirectory E:\\RelaxKonReleaseImports\\Android
```

脚本会构建并验证 APK 与 AAB 的签名，检查两者使用相同证书，并在输出目录写入：

```text
RelaxKonOS-<version>-android-universal.apk
RelaxKonOS-<version>-android-universal.aab
RelaxKonOS-<version>-android-universal.release.json
```

其中 JSON 包含应用 ID、`versionName`、`versionCode`、签名证书 SHA-256 与两份产物 SHA-256，供 PublisherServer 再次核验。它不包含私钥或口令。

将这三个文件传输到 PublisherServer 所在机器的受控导入目录后，在其未提交的 `appsettings.Local.json` 配置 `AndroidImport`。配置只包含导入目录、包名、**公开的**证书 SHA-256 指纹以及 `apksigner`、`aapt2`、`keytool`、`jarsigner` 的本机路径；不得填写 keystore 或签名口令。Publisher 导入 APK 时会把它加入网站下载清单；AAB 仅归档，交给 Play Console 上传，不会作为网站安装包提供。

若要发布到 Google Play，建议使用 Play App Signing：发布机持有 upload key，Google 保管 app signing key。若同时提供官网 APK，必须在首次发布前确认各渠道更新所需的证书策略；证书不一致的 APK 不能互相覆盖安装。

发布前还必须：

- 移除 cleartext 支持，验证 Android Keystore、文件分享 URI、后台恢复及危险操作确认。
- 至少覆盖一台手机、一台 8 英寸平板和一台 11 英寸平板的竖横屏、软键盘、网络切换与恢复。
