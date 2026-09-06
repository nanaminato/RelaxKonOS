# Server Monitor 示例

该开发包展示 `server.metrics.read` 权限以及 `IExternalAppContext.ServerMonitor` 提供的只读服务器性能指标。

在仓库根目录构建、打包并安装 Debug 包：

在 Linux、macOS 或其他 POSIX shell 中：

```bash
export REMOTEOS_DEV_TOKEN="<设置中的配对令牌>"
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/ServerMonitor --configuration Debug --install
```

在 Windows PowerShell 中：

```powershell
$env:REMOTEOS_DEV_TOKEN = "<设置中的配对令牌>"
dotnet run --project Tools/RemoteOS.DevCli -- pack .\examples\ServerMonitor --configuration Debug --install
```

如果已用相同配置编译该项目（例如直接在 IDE 中构建），可跳过重新编译，直接依据 `manifest.json` 打包已有输出：

```bash
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/ServerMonitor --configuration Debug --no-build
```

PowerShell 中只需将项目路径写为 `.\examples\ServerMonitor`。

安装后，在 **设置 → 应用 → Server Monitor → 权限** 中批准 **读取服务器性能指标**，然后从桌面图标启动应用。
