# RelaxKonOS Developer CLI

`relaxkonos-dev` publishes a RelaxKonOS application project and creates its `.roapp` package without a project-specific shell script.

```bash
relaxkonos-dev pack ./MyApp --configuration Release
relaxkonos-dev pack ./MyApp --configuration Debug --no-build
relaxkonos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
relaxkonos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` writes `artifacts/<entry-assembly>.roapp` by default. It requires a `manifest.json` beside the `.csproj`; use `--manifest` and `--output` to override those paths. By default it recompiles the selected `Debug` or `Release` configuration with `dotnet publish`; pass `--no-build` to use `dotnet publish --no-build` and package that configuration's existing build output. It packages the complete publish output beneath the target framework directory declared by `manifest.json`'s `entryAssembly`, including private dependencies and native runtime assets. When the manifest declares `iconPath`, the CLI also safely copies that relative icon asset.

Set `RELAXKONOS_DEV_TOKEN` (or pass `--token`) for commands that contact a running RelaxKonOS Shell: `--install`, `watch`, `apps`, `install`, `update`, `launch`, and `uninstall`. Use `export RELAXKONOS_DEV_TOKEN="<pairing-token>"` in a POSIX shell or `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"` in Windows PowerShell. Use `watch --no-install` to build packages without a Shell.

Run `relaxkonos-dev` with no arguments to see the complete command reference. The RelaxKonOS repository's Developer Mode guide describes the package format and compatibility contract.
