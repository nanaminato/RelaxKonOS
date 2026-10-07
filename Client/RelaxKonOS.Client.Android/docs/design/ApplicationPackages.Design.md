# 移动端应用包方案

BP21 的交付是平台方案与协议边界，不承诺手机已经安装或运行第三方扩展。Android 内置管理功能随签名 APK/AAB 发布；Compose 没有桌面 .NET/Avalonia 加载器。应用部署、运行时安装、Android 系统应用与桌面扩展包分别管理。

## 已核对的桌面契约

当前 `DeveloperPackageManifest` 位于 `Client/RelaxKonOS.Client/Services/Developer/DeveloperPackageManager.cs`。`.roapp` 是 ZIP，根 `manifest.json` 声明 id/displayName/version、lib 下 DLL entryAssembly、entryType、permissionModelVersion（必须 2）、requestedPermissions、clientPlatforms、serverRequirements、文件/URI 关联与实例策略。

application 与 desktopShell 使用不同激活机制。普通扩展经 AssemblyLoadContext、IExternalRemoteApplication 与 Avalonia UI 激活；桌面 Shell 还需要 shellApiVersion 等字段。

`clientPlatforms` 是兼容性声明，空列表不限制桌面平台；它不是可执行运行时的证明。即便包声明 android，当前 DLL/Avalonia/窗口 SDK 仍不兼容手机。requestedPermissions 只是请求，不是授权；manifest 不能声明 BuiltIn、Host Elevation 或注入服务。桌面更新清除原权限决策、版本独立目录/指针激活，卸载经本地 package manager；这些不是 Server 安装端点。

## 每类包的执行与生命周期

| 类别 | 检查与执行平台 | 权限、版本、更新与移除 |
| --- | --- | --- |
| 内置 Android 客户端 | Android 系统核对签名与平台；Compose 原生路由随客户端构建 | 通过签名 APK/AAB 更新；系统应用设置管理 OS 权限/存储与卸载；不在客户端动态装入代码 |
| 普通 .roapp / desktopShell | 当前 Android 一律拒绝执行，明确说明所需桌面运行时；在对应桌面客户端检查/安装 | Android 不生成 installedVersion、成功回执或桌面权限授予；桌面 package manager 负责更新/卸载。手机上传文件仅是文件传输，不算安装 |
| 服务端应用部署 | 现有 applications 定义、固定来源、目录版本与 operation 契约，在远端 Docker 执行 | 部署权限/秘密/端口/卷独立确认；升级/回滚/删除沿用 BP16，不授予手机 OS 或桌面扩展权限 |
| Nginx/FRP/Mihomo/Git/Docker 等宿主运行时 | 现有 installation capabilities、固定清单或核验包来源，在明确远端宿主执行 | preview/来源/hash/提权/持久 operation 与对应领域生命周期；不复用 .roapp 安装器 |
| Web 服务 | BP20 使用实际 HTTP(S) 地址交给外部应用 | 无代码安装、客户端凭据或原生桥接；站点版本和删除由服务端领域管理 |
| APK 或其他系统安装包 | 当前不提供 APK 旁加载器、任意 OS 包管理或通用 shell 安装 | 交给 Android/宿主原有安装渠道；文件扩展名不能绕过平台或签名检查 |

## 包检查器边界

如增加手机 `.roapp` 检查页面，只允许 SAF 选取后有界复制至应用私有临时目录，ZIP 最多 64 MiB/1000 条目，manifest 最多 256 KiB；拒绝重复 manifest、绝对/穿越/链接路径、错误类型和无效 JSON。不解压 DLL、不实例化入口、不授予权限、不把 manifest 文案当受信 UI。显示来源、id、版本、声明的平台/权限及“仅可在桌面运行”；已装版本仅能来自对应真实包目录，不能根据服务器文件存在推断。取消/离页删除暂存，换身份不保留原候选。

检查不能声称验证发布者、签名、哈希来源或包内代码安全。目前没有手机检查页面；此段定义其实施规则，不列作已实现功能。

## 远端桌面包管理协议决策

当前 Server 没有桌面客户端 package manager 的读写代理。手机 API 登录不能取得桌面程序目录、开发桥接令牌或用户 UI 会话；loopback 开发桥接不能直接暴露至局域网。**Android 没有远端 .roapp 安装端点**，手机明确拒绝该流程，并使用上表中的已实现替代入口。

若未来确需远端桌面包管理，先实现独立、用户确认配对的桌面代理与 shared Protocol：设备/平台/运行时能力、真实包目录（appId/version/digest/revision）、有界候选来源检查、精确版本及权限差异预览（planId/target/expiry）、expectedRevision + idempotencyKey 的 apply、可重读的 operation，以及显式更新/移除/数据保留。

Server 仅路由到已配对设备；目标代理重复验证包来源、兼容性、权限、版本和授权，断线不能自动换目标或重放。包管理权限不得隐含 OS 管理员或应用执行权限。没有上述执行者时 UI 不显示可安装按钮。

未来改变 manifest 或协议时直接升级并同步 shared/server/desktop/Android/tests/docs，不加入旧包别名、双格式解析或迁移适配。现有桌面运行时继续由其当前契约管理，本方案未新增兼容路径。

## 验收与范围

Android 不提供第三方 .roapp 安装、通用 APK 旁加载或远端桌面代理。本文中的代理约束描述平台边界，不是生产端点；当前可用入口见 [应用部署](../features/ApplicationDeployments.md)、[服务访问](../features/ServiceAccess.md) 和 [公共运行时安装](../features/Installations.md)。
