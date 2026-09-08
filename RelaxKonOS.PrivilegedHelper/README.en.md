# RelaxKonOS.PrivilegedHelper

This is the narrow local privileged boundary, not a network service and not the Guardian Agent.
Linux uses a short-lived root worker over standard input/output. On Windows, both the LocalSystem
service and the developer console host use the same authenticated named-pipe protocol and the
same closed-set operation dispatcher. It never accepts an arbitrary command or executable.

## Development

Build the helper normally:

```bash
dotnet build RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj
```

For real Server → sudo → Helper integration testing, install the built output into a root-owned
development directory and create a narrow sudoers rule:

```bash
sudo deployment/linux/install-remoteos-privileged-helper-development.sh "$USER"
```

This copies the complete Debug output to
`/usr/local/lib/remoteos/privileged-helper-development/RelaxKonOS.PrivilegedHelper`, then grants
the development account permission to run only that exact apphost as root. Select the Server
`http-linux-privileged` profile, which sets `PrivilegedHelper__HelperPath` to this copy and
`PrivilegedHelper__SudoPath` to `/usr/bin/sudo`. Re-run the script after each Helper rebuild.

The Server itself remains unprivileged: sudo starts one Helper process for each structured request,
and the Helper permits only the closed operation set. Never point a sudoers rule at a development
account-writable `bin/Debug` executable; that would give the account root-equivalent control.

The development installer defaults to `/etc/remoteos` and `/var/lib/remoteos`. To test another
protected directory, use `--file-access whitelist --file-roots deployment/linux/privileged-helper-roots.example`
and retain only the absolute test roots you need. `--file-access full` permits every path below `/`;
use it only on an isolated test host whose RelaxKonOS users are all trusted. Never use it to expose
private keys below `/etc/ssh` through the file explorer.

## Windows development

Run the Helper directly from the IDE with `--console`; do not install a Windows service for daily
development. Create a development-only configuration outside the deployment directory, using a
new random Base64 secret (at least 32 bytes) and only disposable file roots:

```json
{
  "pipeName": "remoteos-privileged-helper-dev",
  "sharedSecret": "replace-with-a-random-base64-secret-of-at-least-32-bytes",
  "fileAllowedRoots": ["C:\\RelaxKonOS-dev"],
  "allowedServiceIds": ["RelaxKonOSServer-dev"],
  "allowConsoleDebug": true
}
```

Start it from the IDE or a terminal:

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

Configure the debug Server with the same pipe name and secret:

```text
PrivilegedHelper__PipeName=remoteos-privileged-helper-dev
PrivilegedHelper__SharedSecret=<same Base64 secret>
```

The console host grants pipe access only to the interactive developer account (plus SYSTEM and
Administrators), which lets a Server launched by that account use the production IPC path. Run
the IDE elevated only when testing operations that genuinely require Administrator rights. The
configuration requires `allowConsoleDebug: true`; the production `helper.json` does not use this
schema and cannot enable console mode accidentally. Before release, test once through the
LocalSystem service to cover Session 0, profile, DPAPI, network-credential and mapped-drive
differences.

## Linux release installation

Publish the project first (the helper needs its `.runtimeconfig.json`, `.deps.json`, and any
managed assemblies next to the executable):

```bash
dotnet publish RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj -c Release -r linux-x64 --self-contained false
```

Pass the published apphost as the fourth argument of
[`install-remoteos-services.sh`](../deployment/linux/install-remoteos-services.sh). The installer
copies the whole publish directory into a root-owned location and creates the narrow sudoers rule
for the Server service account.
