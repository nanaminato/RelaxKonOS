# Android 构建、调试与发布

## 本地环境

本项目的 Android Host 是 `net10.0-android` + Avalonia，不依赖 Gradle 来编译 C# 项目。Gradle 只为将来的原生辅助工作保留，本仓库提供的初始化脚本只使用 `D:\environments\gradle-9.7.1-all.zip`，不会下载 distribution 或 SDK。

```powershell
pwsh Tools/Mobile/Initialize-AndroidEnvironment.ps1
pwsh Tools/Mobile/Build-Android.ps1 -Configuration Debug
pwsh Tools/Mobile/Run-Android.ps1
pwsh Tools/Mobile/Debug-Android.ps1
```

`Build-Android.ps1` 会检查 `D:\environments\Android\Sdk` 和 .NET Android workload。缺少 workload 时会停止并说明原因；请从组织批准的本地 workload/NuGet 源安装，而不是让脚本访问网络。

## 调试服务器地址

Android 模拟器访问开发机服务器使用 `http://10.0.2.2:5090`；真机必须填写开发机在局域网中可访问的 HTTP(S) 地址。M0 Manifest 暂时允许 cleartext，以便开发服务器联调。发布前改为只接受 HTTPS，并配置网络安全策略与证书验证。

## Release

M0 不产生可发布签名包。M4 前执行以下约束：

- 签名 keystore、别名、口令和 CI secret 均不入库。
- 使用 CI 注入 keystore 路径与密码，输出签名 AAB，再由独立设备矩阵验证安装。
- 发布前移除 cleartext 支持，验证 Android Keystore、文件分享 URI、后台恢复及危险操作确认。
- 至少覆盖一台手机、一台 8 英寸平板和一台 11 英寸平板的竖横屏、软键盘、网络切换与恢复。
