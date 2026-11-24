# RelaxKonOS Distribution

The client has two supported distribution forms: a portable ZIP for Linux and Windows, and a signed MSIX package for Windows. Debian/Ubuntu APT repositories and `.deb` packages are not supported.

## Portable ZIP

Download the client ZIP for the host architecture, verify its published SHA-256 checksum, then extract and run it. No .NET runtime or environment variables are required.

On Linux, the desktop client requires a graphical session with X11 access and the system X11 libraries. If startup reports that `libX11.so.6` could not be loaded, make the X11 client library available on the host before launching again.

```bash
unzip RelaxKonOS-*-linux-x64-client.zip -d RelaxKonOS-client
chmod +x RelaxKonOS-client/payload/linux/client/RelaxKonOS
./RelaxKonOS-client/payload/linux/client/RelaxKonOS
```

## Windows MSIX

Build on Windows with Python 3, the Windows SDK packaging tools, and a trusted code-signing certificate. The build checks the desktop executable architecture, official application archives, installation profiles, and the applications' compatibility with the bundled server module contracts before creating the MSIX.

```powershell
$env:RELAXKONOS_MSIX_CERT_PASSWORD = '<certificate password>'
./deployment/packaging/New-RelaxKonOSClientMsix.ps1 -Version 0.1.0.0 -Runtime win-x64 -CertificatePath C:\secure\relaxkonos-client.pfx
```

MSIX upgrades in place only when `IdentityName` and `Publisher` stay unchanged and the package version increases. Do not commit a PFX file or its password; keep both in the release signing system.

## Official desktop app catalog

Pass the target runtime's HTTPS `catalog.json` URL as `-OfficialDesktopCatalogUri` when building a ZIP with `New-RelaxKonOSRelease.ps1` or an MSIX with `New-RelaxKonOSClientMsix.ps1`. For `package-relaxkonos.sh`, pass the URL as the seventh argument and use an empty sixth argument when no server modules are externalized.

The packaging scripts verify the catalog address in `OfficialApps/catalog-source.json` before assembling the archive. Publish the catalog and the official application artifacts built for that runtime together. See the [package store design](../docs/platform/RelaxKonOS.PackageStore.Design.md).

## Desktop Settings

Standard and Developer install the bundled Settings package on first startup.
Minimal starts with the App Installer recovery entry. To install or repair Settings
offline, open the distribution's `OfficialApps/relaxkonos.settings.roapp` in App Installer.

## Component Center

Standard and Developer also preinstall Component Center. In Minimal, the same
App Installer recovery entry can install or repair it from
`OfficialApps/relaxkonos.component-center.roapp`.
