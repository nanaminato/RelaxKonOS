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
the development account permission to run only that apphost's no-argument protocol,
`--user-execution`, and `--user-terminal` entry points as root. Select the Server
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
  "allowConsoleDebug": true,
  "enableWindowsUserExecution": false,
  "userExecutionTimeoutSeconds": 25,
  "developerUserSids": ["S-1-5-21-1111111111-2222222222-3333333333-1005"]
}
```

`developerUserSids` lists the identities that may connect to the pipe in addition to the account
that started the Helper. Entries are the SID reported by `whoami /user` or an account name such as
`MACHINE\user`, and an unresolvable entry fails startup. It is optional and preserves the previous
behavior when omitted, but it is required whenever the Server runs under a different account than
the elevated Helper.

Start it from the IDE or a terminal:

```powershell
dotnet run --project RelaxKonOS.PrivilegedHelper -- --console --config C:\RelaxKonOS-dev\privileged-helper.debug.json
```

Configure the debug Server with the same pipe name and secret:

```text
PrivilegedHelper__PipeName=relaxkonos-privileged-helper-dev
PrivilegedHelper__SharedSecret=<same Base64 secret>
```

The console host grants pipe access to the account that started it plus every identity named in
`developerUserSids`, plus SYSTEM and Administrators, which lets a Server launched by that account use
the production IPC path. Because the Helper must run elevated it often runs as a different account
than the Server; name the Server's account in `developerUserSids` in that case. Otherwise the kernel
refuses the connection before any authentication happens (EPERM) and the client only reports an
unavailable Helper, which looks like a wrong secret. The startup line prints the effective client
SIDs, so compare it with `whoami /user` first when a connection fails. Run the IDE elevated only when
testing operations that genuinely require Administrator rights. The configuration requires
`allowConsoleDebug: true`; the production `helper.json` does not use this schema and cannot enable
console mode accidentally (conversely, service mode rejects a deployed configuration containing
`developerUserSids` instead of silently ignoring it). Before release, test once through the
LocalSystem service to cover Session 0, profile, DPAPI, network-credential and mapped-drive
differences.

Ordinary Windows file execution uses the derived `<pipeName>-user` endpoint and a fresh local-account S4U token. Domain accounts, Git, Terminal, and POSIX mode remain fail-closed. This path is enabled only when both the Server and the LocalSystem Helper explicitly set `EnableWindowsUserExecution=true`; the installer currently writes `false`. The elevated console host is not LocalSystem and therefore cannot acquire an S4U execution token even when its endpoint is enabled for protocol debugging. Do not enable the production path until the isolated Windows Server two-user SID/ACL, token-release, concurrency, and service-restart matrix has passed.

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

### Settings host name operations (integrated; target-host validation pending)

The closed `HostIdentityRead` / `HostIdentityApply` operations accept only `hostName` and `ExpectedRevision`; file, service, timezone and environment fields are rejected, and every other operation rejects a `hostName`. The name rules are shared with the Server and re-checked by the Helper: a single RFC 952/1123 label, no control characters, a leading and trailing letter or digit, never all digits, and no longer than the platform maximum (15 on Windows, 63 on Linux).

Windows reads only the fixed registry locations `...\Control\ComputerName\ActiveComputerName` and `...\ComputerName\ComputerName` (the effective and the pending name) and exposes no caller-supplied registry path. The write uses the fixed `SetComputerNameEx(ComputerNamePhysicalDnsHostname)` system API under a named mutex. That API only stages the name until the next restart, so the readback confirms the *pending* name and neither the Helper nor the Server ever presents a staged rename as live. A domain membership or security policy rejection returns the deterministic `ResourceNotAllowed` instead of an unknown outcome.

Linux reads the fixed `/etc/hostname` and writes with the fixed `/usr/bin/hostnamectl set-hostname`; if either is missing the operation fails explicitly as unsupported. It never appends to `.bashrc`/`.profile` and never overwrites a managed file directly. Linux has no staging step, so the pending name equals the effective name.

The Server uses the new short-lived `HostIdentityChange` grant with the exact target `host/identity`. Preview, idempotency, operation and recovery records live in the Server's separate encrypted SQLite journal; the Helper does not own HTTP idempotency.

Real Windows registry/restart behavior, domain-policy rejection and Linux `hostnamectl` write-then-readback remain unverified on a designated remote test target; the current evidence is controlled-provider behaviour tests and compilation only. When an operation ends Unknown or RecoveryRequired the administrator must set the host name on the host manually, which is the manual recovery path this operation declares.

### Settings environment operations (integrated; target-host validation pending)

Closed `HostEnvironmentRead` / `HostEnvironmentApply` operations accept only structured `environmentTarget` / `environmentChange` fields. They reject mixed file, service or time fields; other operations reject environment payloads. Windows uses fixed machine storage or `HKEY_USERS/<SID>/Environment`, never the Helper's HKCU. A canonical, resolvable account SID and a loaded user hive are required. The Server must map the authenticated user to the SID; clients must not choose arbitrary accounts.

Raw REG_SZ / REG_EXPAND_SZ values and types are preserved. Writes compare the full snapshot revision, mutate the requested values, flush, read back and broadcast the Environment change notification. Registry batches are not transactions: the Server must persist recovery material before dispatch and reconcile interrupted or partial writes as uncertain outcomes. Broadcast does not replace running process environments or guarantee delivery to other sessions/services.

Linux supports only the fixed `host/environment/machine` `/etc/environment` provider. The Helper first requires an installed PAM configuration containing a `pam_env.so` entry that has neither disabled `readenv` nor redirected `envfile`; otherwise it reports unsupported. It does not fabricate a Linux `HostUser` store. Reads and writes use the restricted non-shell, lossless parser, byte revision, Helper mutex, a second revision check before write, same-directory temporary file, flush, atomic replacement, and readback validation. The original mode is retained and links/directories are rejected. Its effective state is “new PAM login session”; it never claims to update running processes, shell profiles, or systemd services.

Raw values are restricted to authenticated local IPC and must never be forwarded directly to HTTP, normal audit or diagnostics. Audit records only a resource hash. Environment HTTP authorization/coordinator integration is complete; real Windows registry/ACL and service runtime isolation validation, plus Linux PAM login, external-edit, and rollback validation, still require designated remote test hosts.
