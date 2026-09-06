# Windows 11 Desktop Shell example

This project is a self-contained external RemoteOS desktop shell. It intentionally references only
`RemoteOS.Shell`; it does not depend on the Client project or its service container.

The example demonstrates:

- a Windows 11-inspired wallpaper and desktop shortcuts;
- a centered taskbar, Start menu, clock, and quick-settings flyout;
- shell actions for settings, display settings, desktop refresh, and Show Desktop;
- correct registration of normal-window, full-screen-window, overlay, and input-backdrop surfaces;
- work-area reporting that reserves the taskbar.

Build from the repository root:

```powershell
dotnet build examples/Windows11DesktopShell/RemoteOS.Example.Windows11DesktopShell.csproj
```
如果已由 IDE 或 `dotnet build` 编译过同一 `Debug` 配置，可跳过重新编译，直接根据 `manifest.json` 打包已有输出：

```bash
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/Windows11DesktopShell --configuration Debug --no-build
```
The generated `.roapp` contains this layout:

```text
Windows11DesktopShell/
  manifest.json
  lib/net10.0/Example.Windows11DesktopShell.dll
  lib/net10.0/Localization/
    en-US.json
    zh-CN.json
    ja-JP.json
```

Open the generated `.roapp` with the RemoteOS App Installer. After installation, select the desktop
from Personalization. Personalization intentionally has no local-folder installation action. See
[`docs/desktop/RemoteOS.ExternalShellPackages.md`](../../docs/desktop/RemoteOS.ExternalShellPackages.md)
for validation, signing, and loading details.
