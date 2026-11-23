# RelaxKonOS 开发者模式

开发者模式提供了一个仅限本地主机的桥接通道，用于安装和刷新开发应用，而无需将它们发布到应用商店。默认情况下已禁用。

组件中心现支持可选的签名 HTTPS `.roapp` 目录，发布步骤见[组件目录发布](../platform/RelaxKonOS.ComponentCatalog.Publishing.md)。来源公钥必须由当前桌面用户显式确认，安装仍使用原 AppInstaller 的身份/版本/权限审查；来源签名不会授予 BuiltIn 身份、沙箱或额外权限。开发者桥接与来源信任彼此独立，相同版本重装属于修复。

## 启用与配对

打开 **设置 → 开发者**，启用开发者模式并复制配对令牌。桥接只监听 `127.0.0.1`，每个请求都需要 `X-RelaxKonOS-Dev-Token`。Debug 默认使用 `default` profile 和端口 `45322`；Release 的 `.roapp` 桥接使用端口 `45321`。

## Direct Deploy

先构建并运行 Debug 桌面客户端一次，之后可重复构建和部署应用。以下 PowerShell 命令可以在不同终端运行。客户端和 CLI 应选择相同的 profile。

```powershell
$env:RELAXKONOS_DEV_PROFILE = 'image-pilot'
dotnet run --project Client/RelaxKonOS.Client.Desktop -c Debug
```

```powershell
$env:RELAXKONOS_DEV_PROFILE = 'image-pilot'
$env:RELAXKONOS_DEV_TOKEN = '<该 profile 的配对令牌>'
dotnet build Tools/RelaxKonOS.DevCli
$cli = 'Tools/RelaxKonOS.DevCli/bin/Debug/net10.0/RelaxKonOS.DevCli.dll'
dotnet $cli status
dotnet $cli deploy ./examples/ImageViewerPilot
dotnet $cli watch --direct ./examples/ImageViewerPilot
dotnet $cli diagnostics com.relaxkonos.example.image-viewer
dotnet $cli reload com.relaxkonos.example.image-viewer
dotnet $cli rollback com.relaxkonos.example.image-viewer --no-launch
```

开发官方图片查看器时，把项目路径换成 `./Applications/RelaxKonOS.App.ImageViewer`，应用 ID 换成 `relaxkonos.imageviewer`。仅已外置并由宿主列入名单的官方应用可以通过配对的 Debug 桥接部署或替换。卸载仍用 `dotnet $cli uninstall relaxkonos.imageviewer`。

记事本使用 `./Applications/RelaxKonOS.App.Notepad` 和 `relaxkonos.notepad`。候选加载成功后，若当前编辑窗口拒绝关闭或正在保存，更新或回退会返回 `close_blocked` 并保留原窗口与版本。

`deploy` 接受 `.csproj`、含一个 `.csproj` 的项目目录或已编译的输出目录。项目输入会执行 `dotnet publish`。对平铺的编译产物，传入 `deploy <输出目录> --manifest <manifest.json>`。输出也可以自带根级 `manifest.json` 和完整的 `lib/<TFM>/` 目录。包含平台私有依赖的应用可传入完整的发布目录，例如 `dotnet publish -r win-x64 --no-self-contained` 的输出。使用 `--no-launch` 可以仅验证、安装并注册。`watch --direct` 监听项目目录，忽略 `bin`、`obj`、`artifacts`、`.tmp` 和所选中转目录，编译失败后仍继续监听。

CLI 通过回环 HTTP 传输文件，无需生成 `.roapp`。Debug 客户端验证清单、入口程序集和可解析的运行时依赖，并为每次部署分配独立的版本目录。候选版本通过加载与可选的激活后，客户端原子更新 `current.json`，关闭旧窗口并卸载旧版的程序集加载上下文。若候选加载或激活失败，当前版本和其窗口保留。`reload` 用新加载上下文重载当前版本，`rollback` 切换到持久化的上一版本。原生库能否完成初始化取决于应用实际调用相关功能时的运行结果。自动启动需要已打开的桌面工作区。在登录页可用 `--no-launch` 安装。

### Profile 与结果

桌面端会先验证数据目录可写，再初始化依赖该目录的服务。目录不可用（包括 Debug profile 被占用）时显示“选择数据位置”窗口，不再因目录异常直接退出。用户可以浏览或输入数据父目录，验证失败会留在窗口中重试；选择器取消后也可继续输入，只有点击“退出”或关闭启动窗口才取消启动。选择新的位置不会移动或删除原有数据。

验证成功后将位置保存到程序旁的 `data-location.txt`，下次启动自动使用；显式设置的 `RELAXKONOS_DATA_HOME` 优先于此文件。如果程序目录不可写，会明确提示无法记住位置，用户可选择“仅本次使用并继续”。清理构建输出会移除该位置记录，但不会删除放在输出目录之外的实际数据。此机制不自动选择临时目录，避免系统清理导致数据丢失。

启动恢复窗口在依赖注入和数据目录初始化之前使用内存中的默认外观，确保输入框与按钮正常显示。2026-11-23 已在 Windows Debug 真实界面验证：将数据父目录设为普通文件以触发不可用错误，通过原生目录选择器选择可写目录，点击“使用此目录”后进入登录页；关闭客户端并清除 `RELAXKONOS_DATA_HOME` 后重启，从 `data-location.txt` 读取先前选择并直接进入登录页。该验收没有提交登录请求，也不代表已完成服务端连接验证。

也可以在 Rider 中打开 **Run → Edit Configurations → 桌面客户端配置 → Environment variables**，添加 `RELAXKONOS_DATA_HOME`，值填写实测可写的绝对路径。该路径是父目录，默认 Debug profile 会在其中创建 `RelaxKonOS\Debug\default`。用户目录同样可能受到运行环境限制，不能仅根据盘符或目录名称认定其可写。

命令行使用相同配置：

```powershell
$env:RELAXKONOS_DATA_HOME = Join-Path $env:USERPROFILE 'RelaxKonOSData'
dotnet run --project Client/RelaxKonOS.Client.Desktop -c Debug
```

默认使用操作系统提供的当前用户本地应用数据目录（Windows 通常为 `C:\Users\<用户名>\AppData\Local`），无需配置额外盘符，通常不需要管理员权限。自定义位置时建议使用构建输出目录之外的固定目录，避免清理 `bin` 时删除调试数据。切换目录不会自动迁移旧数据；如需保留旧配对和应用数据，请关闭客户端后将原 profile 目录完整复制到新位置。不同构建配置或 RID 的 Debug 构建默认复用同一 profile。

- Debug 默认路径为 `%LOCALAPPDATA%/RelaxKonOS/Debug/<profile>/SystemDrive`。每个 profile 独立保存配对令牌和本地应用授权。同一个 profile 的数据目录只能由一个桌面进程打开。若此目录因 ACL 或运行环境限制无法写入，应检查权限或显式配置数据父目录，不能假定任意固定 C 盘目录都可写。
- Debug 和 Release 均使用 `RELAXKONOS_DATA_HOME` 指定数据父目录（取代 `RELAXKONOS_DEV_DATA_HOME`）。Debug 在其中创建 `RelaxKonOS/Debug/<profile>`，Release 创建 `RelaxKonOS`；相对路径以程序所在目录为基准。未显式配置时使用记住的位置，否则使用默认目录。启动验证失败时由用户选择新位置，不静默切换目录。
- 命名 profile 使用稳定的派生端口。端口被占用时，通过客户端的 `RELAXKONOS_DEV_PORT` 和 CLI 的 `--port` 指定同一可用端口。CLI 支持 `--profile`，桥接会校验请求目标。自动化可以在启动 Debug 进程时显式设 `RELAXKONOS_DEV_ENABLE=1`，配对仍使用该 profile 的 `developer-mode.json` 生成的令牌。
- Release 未指定数据父目录时使用 `%LOCALAPPDATA%/RelaxKonOS/SystemDrive` 及既有配对数据，拒绝 Direct Deploy 路由。远端工作区及服务端应用数据仍由所登录的服务器管理。
- `deploy`、`reload`、`rollback`、`status` 和 `diagnostics` 各写一行 JSON 到 stdout。`watch --direct` 每次尝试写一行 JSON，编译日志和监听信息写到 stderr。独立命令失败退出码为 `1`。Agent 可直接调用已构建的 CLI，以获得纯 JSON 输出。

```json
{"ok":true,"code":"ok","appId":"com.relaxkonos.example.image-viewer","attemptedVersionId":"…","currentVersionId":"…","installation":"passed","assemblyLoad":"passed","windowLaunch":"passed","uiTest":"notRun","message":null}
```

`windowLaunch: passed` 表示激活已返回且窗口管理器观测到候选应用的非模态窗口。后台应用可返回 `notOpened`。`uiTest: notRun` 表示这条通道未执行 UI 交互测试。`diagnostics <app-id>` 返回当前版本、加载状态、窗口标题和本进程最后一次版本操作。常见失败码包括 `build_failed`、`unauthorized`、`profile_mismatch`、`debug_required`、`invalid_payload`、`invalid_manifest`、`assembly_load_failed`、`registration_failed`、`launch_blocked`、`activation_failed`、`activation_timeout`、`rollback_unavailable`。`currentVersionId` 可用于确认失败后仍在运行的版本。

桥接路由位于 `/api/developer/v1.0/` 下。Debug 请求还需 `X-RelaxKonOS-Dev-Profile`。`GET status` 查询进程和已安装应用，`GET apps/{id}/diagnostics` 查询单个应用，`POST deploy` 上传 `application/x-tar` 常规文件流，`POST apps/{id}/reload` 与 `POST apps/{id}/rollback` 使用相同的操作结果格式。三个写入路由均接受 `?launch=true|false`。

Windows 端到端验收可在桌面客户端、DevCli 和 `RelaxKonOS.WindowPreviews.Tests` 的 Debug 构建完成后运行 `python Tests/Client/verify-direct-deploy.py`。测试使用隔离 profile 启动标准桌面客户端及使用同一套客户端服务的本地桌面验收宿主。日志保存在 `.codex-scratch` 下。

## `.roapp` 安装与监听

从仓库根目录使用内置 CLI。`pack` 命令只需要 .NET SDK；只有当命令安装、更新、监视或以其他方式与运行中的 Shell 通信时才需要令牌：

```powershell
# 构建包。默认输出为 <project>/artifacts/<entry-assembly>.roapp。
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\MyApp --configuration Release

# 不重新编译，直接将已编译的 Debug 输出打包。
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\MyApp --configuration Debug --no-build

# 一条命令完成构建、打包、安装和启动。
$env:RELAXKONOS_DEV_TOKEN = "<设置中的令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- --profile default pack .\MyApp --configuration Debug --install

# 当项目源码更改时重新构建、打包和重新安装。
dotnet run --project Tools/RelaxKonOS.DevCli -- --profile default watch .\MyApp --configuration Debug
```

在 Linux、macOS 或其他 POSIX shell 中，使用 `export RELAXKONOS_DEV_TOKEN="<设置中的令牌>"` 设置令牌，并将项目路径写为 `./MyApp`。PowerShell、bash、zsh 和 cmd 都可以运行相同的 CLI 命令；只有环境变量和路径语法不同。

`watch <project>` 从源码重新构建，创建新包，然后重新安装并重新启动。`watch <package.roapp>` 仍可用于外部生成的归档。更新应用会关闭其窗口，卸载其可收集的程序集加载上下文，注册新版本，然后再次启动。

## 打包第三方应用

在应用的 `.csproj` 旁边放置 `manifest.json`，然后使用以下 shell 命令：

```bash
dotnet run --project /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli -- pack ./MyApp/MyApp.csproj --configuration Release
```

要获得可复用的 shell 命令，请构建内置的 .NET 工具一次，然后从其本地包目录安装：

```bash
dotnet pack /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli --output /tmp/relaxkonos-dev-tool
dotnet tool install --global --add-source /tmp/relaxkonos-dev-tool RelaxKonOS.DevCli
relaxkonos-dev pack ./MyApp/MyApp.csproj --configuration Release
```

当 RelaxKonOS 将工具发布到包源时，用该源替换 `--add-source`。`relaxkonos-dev` 命令接受与 `dotnet run --project … --` 相同的参数。

CLI 默认运行 `dotnet publish`，重新编译指定的 `Debug` 或 `Release` 配置，并将 ZIP 格式的 `.roapp` 写入 `artifacts/<entry-assembly>.roapp`。如已用相同配置（及适用时相同 RID）编译项目，可传入 `--no-build`：CLI 会改用 `dotnet publish --no-build`，不重新编译而直接打包现有输出。

它将完整的发布输出复制到 `entryAssembly` 声明的 `lib/<TFM>/` 目录下；私有托管依赖、`.deps.json` 和原生运行时资产因此被一致地打包，无需应用特定脚本。若 manifest 声明 `iconPath`，CLI 还会复制这个安全的相对图标路径，确保安装器能找到图标文件。

对于需要原生平台资产的项目，请显式添加目标运行时：

```bash
dotnet run --project /path/to/RelaxKonOS/Tools/RelaxKonOS.DevCli -- pack ./MyApp --runtime win-x64 --configuration Release
```

对原生应用每个运行时标识符使用一个包，例如 `--runtime win-x64` 和 `--runtime linux-x64`，并在清单中声明兼容的客户端平台。纯托管应用通常省略 `--runtime` 并生成一个跨平台包。当清单不在项目旁边时使用 `--manifest <path>`，使用 `--output <file.roapp>` 覆盖制品位置，并在 `watch` 命令中使用 `--no-install` 在不联系 Shell 的情况下重建包。

`pack` 需要一个当前进程能写的中转目录，按 `--work-dir` → `RELAXKONOS_DEVCLI_TMP` → 系统临时目录 → `%LOCALAPPDATA%\RelaxKonOS\DevCli` → `<项目>/.tmp/devcli` 的顺序取第一个通过写入探测的位置，并在使用非默认项时打印 `Scratch:`。受限宿主（应用容器、收紧的终端、杀软策略）会把系统临时目录和用户级 NuGet 缓存对 .NET 进程关闭；此时 CLI 会把子进程的 `TMP`/`TEMP` 指向同一中转根，并在默认包目录不可写时把 `NUGET_PACKAGES`、`NUGET_HTTP_CACHE_PATH` 改到 `<中转根>/nuget` 下并打印 `NuGet cache:`，因此打包会在工作区内完成而不是以权限错误终止。中转根可随时删除，代价是下次重新下载包。

客户端必须有权写入当前 VSD 的 `Programs/External`。`vsd.package.storage-unavailable` 会给出受限目录。若沙箱限制应用数据目录，可用 `RELAXKONOS_DATA_HOME` 选择一个允许写入的父目录。

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

要出现在 RemoteExplorer 的 **打开方式** 菜单中，入口类型还必须实现 `IExternalFileOpenApplication` 并声明接受的文件类型。`supportedFileExtensions` 不区分大小写，每个扩展名必须以点开头。`supportedFileNames` 接受精确文件名，例如 `.gitignore`；这些优先于扩展名匹配。`supportsExtensionlessFiles` 仅对没有扩展名的文件启用低优先级回退。

文本编辑器可在清单中声明 `supportsTextFiles: true`。Explorer 识别出文件内容为文本时，可把它交给此类应用，包括未声明的扩展名。入口仍需实现 `IExternalFileOpenApplication`。

`supportedUriSchemes` 可选地声明包拥有的自定义 URI 方案。每个方案必须匹配 `^[a-z][a-z0-9+.-]{0,31}$`；`relaxkonos` 保留给 Shell 使用。入口类型必须实现 `IExternalAppActivationHandler` 以接收其声明的 URI 之一。Shell 为该方案选择用户有效的默认应用，或者在没有设置默认值时选择唯一兼容的处理程序。对导航器（如帮助中心）使用 `instancePolicy: "SingleWindow"`，这样重复的链接会导航到同一窗口而不是打开重复的窗口。

## 服务器监控能力

`IExternalAppContext.ServerMonitor` 提供稳定的只读聚合服务器指标 API。它需要 `server.metrics.read` 清单权限和用户在 **应用权限** 下的批准。`GetSnapshotAsync` 返回一个能力结果；`WatchAsync` 返回主机轮询序列（最小一秒间隔）用于实时仪表板。它故意不暴露进程枚举、进程终止、原始服务器凭据或任务管理器客户端。

完整示例位于 [`examples/ServerMonitor`](../../examples/ServerMonitor)。构建、打包并安装：

```powershell
$env:RELAXKONOS_DEV_TOKEN = "<设置中的令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- --profile default pack .\examples\ServerMonitor --configuration Debug --install
```

安装后，在设置中向 **Server Monitor** 授予 **读取服务器性能指标**，然后启动桌面图标。

## 网络检查器

`网络检查器` 是一个 RelaxKonOS 系统诊断窗口，不是开发包。通过 **设置 → 开发者 → 网络检查器** 或 `Ctrl+Shift+I` 打开。默认为关闭状态；关闭开发者模式或退出登录会立即停止录制并清除其内存日志。它仅显示经过脱敏处理的 RelaxKonOS REST 和 SignalR 摘要，从不显示令牌、cookie、媒体正文、终端数据或任意设备流量。

## 安全模型

第三方开发包使用自己的命名空间。`relaxkonos.*` 由宿主保留。Release 的外置官方应用由发行目录中的 `OfficialApps/<app-id>.roapp` 及配套 SHA-256 文件提供，经过原安装器审查后写入当前 VSD。普通本地、服务器、组件目录和开发桥接上传继续拒绝保留 ID。Debug 的配对 Direct Deploy 仅为宿主名单中的外置官方 ID 开放，记录到独立 profile。清单权限继续由用户审查和授权。完整流程见[官方图片查看器](../applications/RelaxKonOS.ImageViewer.md)。

商店来源、目录、搜索、下载和来源信任的统一设计见[包商店来源设计](../platform/RelaxKonOS.PackageStore.Design.md)；本文仍拥有 `.roapp` 清单、本地安装器和开发者桥接语义。

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

`IExternalAppContext.ServerFiles` 在普通工作区使用当前服务器文件服务，在 SSH 桌面经当前已核验主机密钥的 SFTP 会话读取宿主交给应用的单个文件。两种入口都要求 `server.files.read`，并沿用连接本身的身份和文件权限。`IsSshSession` 可用于界面标记来源。

在普通服务器工作区，编辑器可通过 `ReadAsync` 读取内容、`WriteAsync` 写回，以及 `PickFileAsync` 在应用所属窗口打开宿主选择器。打开和浏览需要读权限，写入需要写权限，另存为选择器需要两者。文件写入使用原服务端文件授权。记事本通过宿主提供的文本编码设置接口继续使用工作区默认编码，详见[记事本](../applications/RelaxKonOS.Notepad.md)。

授予在桌面客户端是本地的。在 Windows 上，它们使用当前用户的 DPAPI 保护存储；其他平台使用兼容的本地 JSON 回退。

## 清除应用数据

**设置 → 应用 → [应用] → 清除数据** 始终会删除当前设备上该应用的标准本地数据目录。它故意不卸载包。用户还可以选择以下任一或两者：

- **权限决策**：删除该应用 ID 的本地 `Granted`/`Denied` 决策。其声明的权限因此变为 `Undecided`，并将在下次启动时再次请求。
- **服务器应用数据**：删除该应用和当前用户的每个 `IExternalAppContext.SettingsStore` 文档，跨其用户、工作区和设备范围。

第二个选项是全账户私有配置删除，不仅仅是当前设备的重置。因此包必须在任何启动后容忍缺失的设置文档。

## 运行时权限批准

权限由应用在使用相应能力时请求。Shell 在应用窗口内显示授权提示。选择 **稍后** 保持原权限状态，授予和拒绝的决策会被记住。

内置的 `AppContext` 和包的 `IExternalAppContext` 都暴露相同的作用域 `Permissions` 表面。它只能检查或请求当前应用的声明 ID：

```csharp
var status = context.Permissions.GetStatus("server.metrics.read");
if (status != AppPermissionStatus.Granted)
    status = await context.Permissions.RequestAsync("server.metrics.read");

if (status == AppPermissionStatus.Granted)
    await LoadMetricsAsync();

// 打开 relaxkonos://settings/apps/{this-app}/permissions。
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
