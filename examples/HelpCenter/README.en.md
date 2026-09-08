# Help Center example

This development package registers the `help` URI scheme and opens offline multilingual Markdown guides in one reusable window.

Examples:

```text
help://guide/docker/install?lang=en
help://guide/docker/uninstall?lang=zh-CN
```

Build and install it from the repository root:

On Linux, macOS, or another POSIX shell:

```bash
export RELAXKONOS_DEV_TOKEN="<pairing-token>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --install
```

On Windows PowerShell:

```powershell
$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"
dotnet run --project Tools/RelaxKonOS.DevCli -- pack .\examples\HelpCenter --configuration Debug --install
```

If the same `Debug` configuration was already built by the IDE or `dotnet build`, package its existing output directly from `manifest.json` without recompiling:

```bash
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --no-build
```

In PowerShell, use `.\examples\HelpCenter` for the project path instead.

Once installed, choose **Help Center** as the default program for `help` in Settings → Default apps. With no competing `help` handler installed, the Shell selects it automatically.
