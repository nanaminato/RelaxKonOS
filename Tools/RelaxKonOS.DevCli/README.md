# RelaxKonOS 开发者 CLI

`relaxkonos-dev` 会发布 RelaxKonOS 应用项目并创建其 `.roapp` 包，无需为项目编写专用 Shell 脚本。

```bash
relaxkonos-dev pack ./MyApp --configuration Release
relaxkonos-dev pack ./MyApp --configuration Debug --no-build
relaxkonos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
relaxkonos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` 默认写入 `artifacts/<entry-assembly>.roapp`。它要求 `.csproj` 旁存在 `manifest.json`；可用 `--manifest` 和 `--output` 覆盖这些路径。默认会用 `dotnet publish` 重新编译指定的 `Debug` 或 `Release` 配置；传入 `--no-build` 时改为 `dotnet publish --no-build`，直接打包该配置已有的编译结果。它会打包 `manifest.json` 的 `entryAssembly` 所声明目标框架目录下的完整发布输出，包括私有依赖项和原生运行时资产；若清单声明 `iconPath`，也会安全地复制该相对路径的图标资源。

对会连接正在运行的 RelaxKonOS Shell 的命令设置 `RELAXKONOS_DEV_TOKEN`（或传入 `--token`）：`--install`、`watch`、`apps`、`install`、`update`、`launch` 和 `uninstall`。POSIX shell 使用 `export RELAXKONOS_DEV_TOKEN="<pairing-token>"`，Windows PowerShell 使用 `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"`。使用 `watch --no-install` 可在不连接 Shell 的情况下构建包。

不带参数运行 `relaxkonos-dev` 可查看完整命令参考。RelaxKonOS 仓库的“开发者模式”指南说明包格式和兼容性约定。
