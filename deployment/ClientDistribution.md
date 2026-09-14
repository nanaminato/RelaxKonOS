# RelaxKonOS Distribution

The client has three supported distribution forms. The portable ZIP remains available for users who do not want a system installation. The `.deb` package is the supported install-and-upgrade channel for Debian and Ubuntu. Windows uses a signed MSIX package.

## Portable ZIP

Download the client ZIP for the host architecture, verify its published SHA-256 checksum, then extract and run it. No .NET runtime or environment variables are required.

```bash
unzip RelaxKonOS-*-linux-x64-client.zip -d RelaxKonOS-client
chmod +x RelaxKonOS-client/payload/linux/client/RelaxKonOS
./RelaxKonOS-client/payload/linux/client/RelaxKonOS
```

## Debian and Ubuntu

Build a self-contained Debian package:

```bash
./deployment/packaging/package-relaxkonos-client-deb.sh 0.1.0 linux-x64 Release artifacts
```

The package installs the client beneath `/opt/relaxkonos/client`, adds a desktop launcher, and exposes the `relaxkonos-client` command. It does not install a server or require sudo at runtime.

Publish a package to the repository using an existing offline-protected GPG signing key:

```bash
./deployment/packaging/publish-relaxkonos-apt-repository.sh artifacts/relaxkonos-client_0.1.0_amd64.deb /srv/relaxkonos-release/apt YOUR_GPG_FINGERPRINT stable
```

Serve the resulting repository at `https://downloads.relaxkon.com/apt`. End users install the public key and add the source once:

```bash
curl -fsSL https://downloads.relaxkon.com/apt/relaxkonos-archive-keyring.asc | gpg --dearmor | sudo tee /etc/apt/keyrings/relaxkonos-archive-keyring.gpg >/dev/null
echo 'deb [signed-by=/etc/apt/keyrings/relaxkonos-archive-keyring.gpg] https://downloads.relaxkon.com/apt stable main' | sudo tee /etc/apt/sources.list.d/relaxkonos.list >/dev/null
sudo apt update
sudo apt install relaxkonos-client
```

Later client updates use the normal system upgrade flow:

```bash
sudo apt update
sudo apt upgrade
```

## Windows MSIX

On a Windows release machine with the Windows SDK packaging tools and a trusted code-signing certificate:

```powershell
$env:RELAXKONOS_MSIX_CERT_PASSWORD = '<certificate password>'
./deployment/packaging/New-RelaxKonOSClientMsix.ps1 -Version 0.1.0.0 -Runtime win-x64 -CertificatePath C:\secure\relaxkonos-client.pfx
```

MSIX upgrades in place only when `IdentityName` and `Publisher` stay unchanged and the package version increases. Do not commit a PFX file or its password; keep both in the release signing system.
