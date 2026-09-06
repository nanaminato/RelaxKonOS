# Help Center example

This development package registers the `help` URI scheme and opens offline multilingual Markdown guides in one reusable window.

Examples:

```text
help://guide/docker/install?lang=en
help://guide/docker/uninstall?lang=zh-CN
```

Build and install it from the repository root:

```powershell
$env:REMOTEOS_DEV_TOKEN = "<pairing-token>"
dotnet run --project Tools/RemoteOS.DevCli -- pack .\examples\HelpCenter --configuration Debug --install
```

If the same `Debug` configuration was already built by the IDE or `dotnet build`, package its existing output directly from `manifest.json` without recompiling:

```powershell
dotnet run --project Tools/RemoteOS.DevCli -- pack .\examples\HelpCenter --configuration Debug --no-build
```

Once installed, choose **Help Center** as the default program for `help` in Settings → Default apps. With no competing `help` handler installed, the Shell selects it automatically.
