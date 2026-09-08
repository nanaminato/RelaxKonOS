# Windows 11 Desktop Shell example

This project is a self-contained external RelaxKonOS desktop shell. It intentionally references only
`RelaxKonOS.Shell`; it does not depend on the Client project or its service container.

The example demonstrates:

- the host's active wallpaper (including synchronized custom images), theme-resolved desktop-label foreground, live desktop entries, and a desktop context menu;
- a centered taskbar, live All apps menu, clock, quick-settings flyout, and a short startup transition;
- shell actions for settings, display settings, desktop refresh, and Show Desktop;
- correct registration of normal-window, full-screen-window, overlay, and input-backdrop surfaces;
- work-area reporting that reserves the taskbar.
- host-provided application artwork on desktop entries, with `iconGlyph` used only as the fallback;
- detailed vector desktop icons for a computer, folder, document, and recycle bin.

Build from the repository root:

```powershell
dotnet build examples/Windows11DesktopShell/RelaxKonOS.Example.Windows11DesktopShell.csproj
```
如果已由 IDE 或 `dotnet build` 编译过同一 `Debug` 配置，可跳过重新编译，直接根据 `manifest.json` 打包已有输出：

```bash
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/Windows11DesktopShell --configuration Debug --no-build
```
The generated `.roapp` contains this layout:

```text
Windows11DesktopShell/
  manifest.json
  lib/net10.0/RelaxKonOS.Example.Windows11DesktopShell.dll
  lib/net10.0/Localization/
    en-US.json
    zh-CN.json
    ja-JP.json
```

Open the generated `.roapp` with the RelaxKonOS App Installer. After updating this sample, reinstall
the new package before selecting it from Personalization. The selection records the package ID and
version, so it remains available after restarting the client as long as the package is installed.
Personalization intentionally has no local-folder installation action. See
[`docs/desktop/RelaxKonOS.ExternalShellPackages.md`](../../docs/desktop/RelaxKonOS.ExternalShellPackages.md)
for validation, signing, and loading details.
