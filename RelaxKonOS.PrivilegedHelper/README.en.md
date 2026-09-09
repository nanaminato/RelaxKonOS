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
sudo deployment/linux/install-relaxkonos-privileged-helper-development.sh "$USER"
```

This copies the complete Debug output to
`/usr/local/lib/relaxkonos/privileged-helper-development/RelaxKonOS.PrivilegedHelper`, then grants
the development account permission to run only that exact apphost as root. Select the Server
`http-linux-privileged` profile, which sets `PrivilegedHelper__HelperPath` to this copy and
`PrivilegedHelper__SudoPath` to `/usr/bin/sudo`. Re-run the script after each Helper rebuild.

The Server itself remains unprivileged: sudo starts one Helper process for each structured request,
and the Helper permits only the closed operation set. Never point a sudoers rule at a development
account-writable `bin/Debug` executable; that would give the account root-equivalent control.

The development installer defaults to `/etc/relaxkonos` and `/var/lib/relaxkonos`. To test another
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
  "pipeName": "relaxkonos-privileged-helper-dev",
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
PrivilegedHelper__PipeName=relaxkonos-privileged-helper-dev
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
[`install-relaxkonos-services.sh`](../deployment/linux/install-relaxkonos-services.sh). The installer
copies the whole publish directory into a root-owned location and creates the narrow sudoers rule
for the Server service account.

## Settings timezone operations (2026-09-08; platform acceptance pending)

The closed `HostTimeRead` / `HostTimeApply` operations accept only an OS-listed `TimeZoneId` and `ExpectedRevision`. Mixed file/service fields are rejected. The Helper compares the observed OS content revision before changing the zone and reads it back afterward. Windows uses `tzutil.exe` in the system directory; Linux uses `/usr/bin/timedatectl` and requires systemd-timedated. An unavailable backend fails explicitly.

Server authorization uses `HostTimeChange` with the exact target `host/time`. Encrypted, synchronous SQLite records own HTTP idempotency and operation status. Lost results remain Unknown and must not be blindly replayed with another Helper id.

Linux Helper launch and privileged child launches clear inherited environment values and use trusted absolute executable paths. Linux file/service allowlists come only from the installed root-owned policy files, with no environment overrides. Installation must supply a trusted runtime without relying on a user's DOTNET_ROOT/PATH. Windows service runtime isolation before managed startup still requires installation review and target-host verification; cleaning child environments alone does not prove full launch isolation.

Real Windows/Ubuntu timezone changes, external changes, policy rejection and recovery remain unverified on designated test hosts.

### Settings environment operations (integration in progress)

Closed `HostEnvironmentRead` / `HostEnvironmentApply` operations accept only structured `environmentTarget` / `environmentChange` fields. They reject mixed file, service or time fields; other operations reject environment payloads. Windows uses fixed machine storage or `HKEY_USERS/<SID>/Environment`, never the Helper's HKCU. A canonical, resolvable account SID and a loaded user hive are required. The Server must map the authenticated user to the SID; clients must not choose arbitrary accounts.

Raw REG_SZ / REG_EXPAND_SZ values and types are preserved. Writes compare the full snapshot revision, mutate the requested values, flush, read back and broadcast the Environment change notification. Registry batches are not transactions: the Server must persist recovery material before dispatch and reconcile interrupted or partial writes as uncertain outcomes. Broadcast does not replace running process environments or guarantee delivery to other sessions/services.

Raw values are restricted to authenticated local IPC and must never be forwarded directly to HTTP, normal audit or diagnostics. Audit records only a resource hash. Environment HTTP authorization/coordinator integration is pending; real Windows registry/ACL and service runtime isolation validation require a designated remote test host. Linux currently has only the restricted document core; its provider remains required work, not a platform exemption.
