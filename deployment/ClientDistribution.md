# RelaxKonOS Distribution

The client has two supported distribution forms: a portable ZIP for Linux and Windows, and a signed MSIX package for Windows. Debian/Ubuntu APT repositories and `.deb` packages are not supported.

## Portable ZIP

Download the client ZIP for the host architecture, verify its published SHA-256 checksum, then extract and run it. No .NET runtime or environment variables are required.

```bash
unzip RelaxKonOS-*-linux-x64-client.zip -d RelaxKonOS-client
chmod +x RelaxKonOS-client/payload/linux/client/RelaxKonOS
./RelaxKonOS-client/payload/linux/client/RelaxKonOS
```

## Windows MSIX

On a Windows release machine with the Windows SDK packaging tools and a trusted code-signing certificate:

```powershell
$env:RELAXKONOS_MSIX_CERT_PASSWORD = '<certificate password>'
./deployment/packaging/New-RelaxKonOSClientMsix.ps1 -Version 0.1.0.0 -Runtime win-x64 -CertificatePath C:\secure\relaxkonos-client.pfx
```

MSIX upgrades in place only when `IdentityName` and `Publisher` stay unchanged and the package version increases. Do not commit a PFX file or its password; keep both in the release signing system.
