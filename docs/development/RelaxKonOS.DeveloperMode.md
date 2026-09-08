# RelaxKonOS 开发者模式

开发者模式提供了一个仅限本地主机的桥接通道，用于安装和刷新开发应用，而无需将它们发布到应用商店。默认情况下已禁用。

## 启用与配对

打开 **设置 → 应用 → 开发者模式**，启用它，然后复制配对令牌。该桥接仅在 `http://127.0.0.1:45321/api/developer/v1.0/` 上监听，并在每个 `X-RemoteOS-Dev-Token` 请求头中要求提供令牌。

从仓库根目录使用内置 CLI。`pack` 命令只需要 .NET SDK；只有当命令安装、更新、监视或以其他方式与运行中的 Shell 通信时才需要令牌：

```powershell
# 构建包。默认输出为 <project>/artifacts/<entry-assembly>.roapp。
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\MyApp --configuration Release

# 不重新编译，直接将已编译的 Debug 输出打包。
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\MyApp --configuration Debug --no-build

# 一条命令完成构建、打包、安装和启动。
$env:REMOTEOS_DEV_TOKEN = "<设置中的令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\MyApp --configuration Debug --install

# 当项目源码更改时重新构建、打包和重新安装。
dotnet run --project Tools/RelaxKonOS.DevCli -- watch .\MyApp --configuration Debug
```

在 Linux、macOS 或其他 POSIX shell 中，使用 `export REMOTEOS_DEV_TOKEN="<设置中的令牌>"` 设置令牌，并将项目路径写为 `./MyApp`。PowerShell、bash、zsh 和 cmd 都可以运行相同的 CLI 命令；只有环境变量和路径语法不同。

`watch <project>` 从源码重新构建，创建新包，然后重新安装并重新启动。`watch <package.roapp>` 仍可用于外部生成的归档。更新应用会关闭其窗口，卸载其可收集的程序集加载上下文，注册新版本，然后再次启动。

## 打包第三方应用

在应用的 `.csproj` 旁边放置 `manifest.json`，然后使用以下 shell 命令：

```bash
dotnet run --project /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli -- pack ./MyApp/MyApp.csproj --configuration Release
```

要获得可复用的 shell 命令，请构建内置的 .NET 工具一次，然后从其本地包目录安装：

```bash
dotnet pack /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli --output /tmp/remoteos-dev-tool
dotnet tool install --global --add-source /tmp/remoteos-dev-tool RelaxKonOS.DevCli
remoteos-dev pack ./MyApp/MyApp.csproj --configuration Release
```

当 RelaxKonOS 将工具发布到包源时，用该源替换 `--add-source`。`remoteos-dev` 命令接受与 `dotnet run --project … --` 相同的参数。

CLI 默认运行 `dotnet publish`，重新编译指定的 `Debug` 或 `Release` 配置，并将 ZIP 格式的 `.roapp` 写入 `artifacts/<entry-assembly>.roapp`。如已用相同配置（及适用时相同 RID）编译项目，可传入 `--no-build`：CLI 会改用 `dotnet publish --no-build`，不重新编译而直接打包现有输出。它将完整的发布输出复制到 `entryAssembly` 声明的 `lib/<TFM>/` 目录下；私有托管依赖、`.deps.json` 和原生运行时资产因此被一致地打包，无需应用特定脚本。若 manifest 声明 `iconPath`，CLI 还会复制这个安全的相对图标路径，确保安装器能找到图标文件。

对于需要原生平台资产的项目，请显式添加目标运行时：

```bash
dotnet run --project /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli -- pack ./MyApp --runtime win-x64 --configuration Release
```

对原生应用每个运行时标识符使用一个包，例如 `--runtime win-x64` 和 `--runtime linux-x64`，并在清单中声明兼容的客户端平台。纯托管应用通常省略 `--runtime` 并生成一个跨平台包。当清单不在项目旁边时使用 `--manifest <path>`，使用 `--output <file.roapp>` 覆盖制品位置，并在 `watch` 命令中使用 `--no-install` 在不联系 Shell 的情况下重建包。

## 开发包格式

`.roapp` 文件是一个 ZIP 归档，具有以下最小结构：

```text
manifest.json
lib/net10.0/MyApp.dll
lib/net10.0/<私有依赖>.dll
```

`manifest.json`：

```json
{
  "id": "com.example.hello",
  "displayName": "Hello Developer",
  "version": "0.1.0-dev",
  "entryAssembly": "lib/net10.0/MyApp.dll",
  "entryType": "RelaxKonOS.Example.HelloApp",
  "iconGlyph": "🧪",
  "description": "一个开发包",
  "requestedPermissions": ["desktop.wallpaper.write"],
  "supportedFileExtensions": [".hello"],
  "supportedFileNames": [".hellorc"],
  "supportsExtensionlessFiles": false,
  "instancePolicy": "SingleWindow",
  "supportedUriSchemes": ["example-help"],
  "clientPlatforms": ["windows", "linux"],
  "serverRequirements": {
    "platforms": ["windows", "linux"],
    "capabilities": ["server.files"]
  }
}
```

`clientPlatforms` 和 `serverRequirements` 是可选的。省略或空列表意味着该包在该维度上没有限制。Shell 在加载包程序集之前评估它们，包括桌面启动和 **打开方式** 文件启动。完整的契约和服务器能力目录请参阅 [`RelaxKonOS.ApplicationCompatibility.md`](./RelaxKonOS.ApplicationCompatibility.md)。

入口类型必须实现 `RelaxKonOS.AppSDK.IExternalRemoteApplication`。它接收 `IExternalAppContext`，该上下文仅暴露已批准的 RelaxKonOS 能力，包括自有窗口创建和权限门控的桌面外观服务。

要出现在 RemoteExplorer 的 **打开方式** 菜单中，入口类型还必须实现 `IExternalFileOpenApplication` 并声明至少一个接受的路径规则。`supportedFileExtensions` 不区分大小写，每个扩展名必须以点开头。`supportedFileNames` 接受精确文件名，例如 `.gitignore`；这些优先于扩展名匹配。`supportsExtensionlessFiles` 仅对没有扩展名的文件启用低优先级回退。省略这三个字段的包仍可启动，但永远不会获得文件路径。

`supportedUriSchemes` 可选地声明包拥有的自定义 URI 方案。每个方案必须匹配 `^[a-z][a-z0-9+.-]{0,31}$`；`remoteos` 保留给 Shell 使用。入口类型必须实现 `IExternalAppActivationHandler` 以接收其声明的 URI 之一。Shell 为该方案选择用户有效的默认应用，或者在没有设置默认值时选择唯一兼容的处理程序。对导航器（如帮助中心）使用 `instancePolicy: "SingleWindow"`，这样重复的链接会导航到同一窗口而不是打开重复的窗口。

## 服务器监控能力

`IExternalAppContext.ServerMonitor` 提供稳定的只读聚合服务器指标 API。它需要 `server.metrics.read` 清单权限和用户在 **应用权限** 下的批准。`GetSnapshotAsync` 返回一个能力结果；`WatchAsync` 返回主机轮询序列（最小一秒间隔）用于实时仪表板。它故意不暴露进程枚举、进程终止、原始服务器凭据或任务管理器客户端。

完整示例位于 [`examples/ServerMonitor`](../../examples/ServerMonitor)。构建、打包并安装：

```powershell
$env:REMOTEOS_DEV_TOKEN = "<设置中的令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\examples\ServerMonitor --configuration Debug --install
```

安装后，在设置中向 **Server Monitor** 授予 **读取服务器性能指标**，然后启动桌面图标。

## 网络检查器

`网络检查器` 是一个 RelaxKonOS 系统诊断窗口，不是开发包。通过 **设置 → 开发者 → 网络检查器** 或 `Ctrl+Shift+I` 打开。默认为关闭状态；关闭开发者模式或退出登录会立即停止录制并清除其内存日志。它仅显示经过脱敏处理的 RelaxKonOS REST 和 SignalR 摘要，从不显示令牌、cookie、媒体正文、终端数据或任意设备流量。

## 安全模型

开发包使用保留的外部应用 ID，不能使用 `remoteos.*` 内置命名空间。它们安装在当前用户的本地应用数据下方，不会覆盖商店包。开发者模式不会自动授予清单权限；请在 **应用权限** 下授予或撤销每个请求的能力。

应用安装状态会逐步迁移到 Virtual System Drive（VSD）；详见 [VSD 契约](../architecture/RelaxKonOS.VirtualSystemDrive.Contracts.md)。VSD 只是 RelaxKonOS 本地数据目录，不是宿主真实磁盘、文件系统沙箱、包签名或第三方代码信任边界。包中的 `manifest.json` 或派生的 descriptor 只能声明请求，不能把包提升为 BuiltIn、授予权限、请求 Host Elevation 或获得任意代码/命令执行。

桥接没有局域网监听器。不要暴露其配对令牌。重新生成令牌会使现有的开发者工具会话失效。

## 权限目录

包清单请求稳定的、主机定义的权限，而不是定义自己的权限类型。设置按类别分组请求的权限，并为每个应用打开单独的编辑器。

| 类别 | 权限 ID |
| --- | --- |
| 服务器文件 | `server.files.read`, `server.files.write` |
| 服务器监控 | `server.metrics.read` |
| 服务器管理 | `server.processes.manage`, `server.services.manage`, `server.power.manage` |
| 服务器网络 | `server.network.read`, `server.network.configure` |
| 桌面与工作区 | `desktop.wallpaper.write` |

管理权限仅包括安全执行该操作所需的最小检查（例如，进程管理包括列出进程）；它们不授予其他类别。仅声明未来的目录权限永远不会授予对主机服务的访问，直到权限门控的 SDK 能力暴露它。

授予在桌面客户端是本地的。在 Windows 上，它们使用当前用户的 DPAPI 保护存储；其他平台使用兼容的本地 JSON 回退。

## 清除应用数据

**设置 → 应用 → [应用] → 清除数据** 始终会删除当前设备上该应用的标准本地数据目录。它故意不卸载包。用户还可以选择以下任一或两者：

- **权限决策**：删除该应用 ID 的本地 `Granted`/`Denied` 决策。其声明的权限因此变为 `Undecided`，并将在下次启动时再次请求。
- **服务器应用数据**：删除该应用和当前用户的每个 `IExternalAppContext.SettingsStore` 文档，跨其用户、工作区和设备范围。

第二个选项是全账户私有配置删除，不仅仅是当前设备的重置。因此包必须在任何启动后容忍缺失的设置文档。

## 运行时权限批准

当应用打开时，Shell 为每个仍未决定的声明权限打开一个应用拥有的提示。应用在这些提示显示时已经在运行：选择 **拒绝** 不会关闭它，选择 **稍后** 会在剩余提示继续时保持该权限未决定。授予和拒绝的决策会被记住；只有未决定的权限会在下次启动时自动提示。

内置的 `AppContext` 和包的 `IExternalAppContext` 都暴露相同的作用域 `Permissions` 表面。它只能检查或请求当前应用的声明 ID：

```csharp
var status = context.Permissions.GetStatus("server.metrics.read");
if (status != AppPermissionStatus.Granted)
    status = await context.Permissions.RequestAsync("server.metrics.read");

if (status == AppPermissionStatus.Granted)
    await LoadMetricsAsync();

// 打开 remoteos://settings/apps/{this-app}/permissions。
await context.Permissions.OpenSettingsAsync();
```

`RequestAsync` 故意在先前拒绝后再次显示单权限提示；**稍后** 保留当前决策。应用必须将 `Undecided` 和 `Denied` 视为未授予，并保持其非特权功能可用。

## 导航到应用设置

外部应用可以在不获得任何额外权限的情况下将用户发送到主机拥有的应用页面：

```csharp
await context.Settings.OpenApplicationsAsync();
```

主机在可能时重用并聚焦现有的设置窗口；否则它打开设置并选择 **应用**。此 API 仅执行导航，不能读取或更改设置。

## 系统语言

包应用可以读取工作区显示语言，并在其更改时刷新其 UI。这是只读的，不需要权限：

```csharp
context.SystemLanguage.LanguageChanged += (_, change) =>
    RefreshUi(change.CurrentLanguage);

RefreshUi(context.SystemLanguage.CurrentLanguage); // BCP-47 名称，如 "en-US"
```

内置语言选择器从客户端 `Localization` 目录发现文件。语言文件声明其 BCP-47 `Culture`、显示名称、排序顺序和源字符串翻译；添加格式正确的 `.json` 文件即可选择，无需更改客户端代码。
