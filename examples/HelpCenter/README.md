# Help Center 示例

此开发包注册 `help` URI 方案，并在一个可复用窗口中打开离线多语言 Markdown 指南。

示例：

```text
help://guide/docker/install?lang=en
help://guide/docker/uninstall?lang=zh-CN
```

在仓库根目录构建并安装：

在 Linux、macOS 或其他 POSIX shell 中：

```bash
export RELAXKONOS_DEV_TOKEN="<设置中的配对令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --install
```

在 Windows PowerShell 中：

```powershell
$env:RELAXKONOS_DEV_TOKEN = "<设置中的配对令牌>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\examples\HelpCenter --configuration Debug --install
```

如果已由 IDE 或 `dotnet build` 编译过同一 `Debug` 配置，可跳过重新编译，直接根据 `manifest.json` 打包已有输出：

```bash
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --no-build
```

PowerShell 中只需将项目路径写为 `.\examples\HelpCenter`。

安装后，在“设置 → 默认应用”中将 **Help Center** 设为 `help` 的默认程序。未安装其他 `help` 处理程序时，Shell 会自动选择它。
