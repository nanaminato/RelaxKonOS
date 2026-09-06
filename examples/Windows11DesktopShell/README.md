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

To assemble a development package, create this layout and copy in `shell.json` plus the built DLL:

```text
Windows11DesktopShell/
  shell.json
  lib/net10.0/Example.Windows11DesktopShell.dll
```

Select that package directory from RemoteOS Personalization while Developer Mode is enabled. See
[`docs/desktop/RemoteOS.ExternalShellPackages.md`](../../docs/desktop/RemoteOS.ExternalShellPackages.md)
for validation, signing, and loading details.
