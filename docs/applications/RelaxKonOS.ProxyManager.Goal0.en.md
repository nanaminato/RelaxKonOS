# RelaxKonOS Proxy Manager — Goal 0 decision record

> Status: accepted on 2026-08-31. This record freezes the V1 decisions that
> implementation must use through Goal 10. A change requires updating this
> record, the design specification, and the affected tests before code relies
> on it.

## Review result

`RelaxKonOS.ProxyManager.Discovery.en.md` remains accurate for the current tree:
there is no proxy feature or general elevation workflow. The reusable pieces
are patterns only: `HostGlobalMigrationRunner` for host metadata,
`WebServerOperationStore` for durable idempotent operations,
`DataProtectionSecretStore` for server-only encrypted values,
`FrpRuntimeManager` for verification/staging/rollback lessons, and
`INativeServiceAdapter` for allowlisted lifecycle controls. None is extended
to supervise Mihomo or to become a generic command runner.

## V1 platform and runtime release matrix

| RelaxKonOS RID | Supported host | Pinned Mihomo artifact | SHA-256 |
| --- | --- | --- | --- |
| `win-x64` | Windows 10/11 and Windows Server, x64 | `mihomo-windows-amd64-v1.19.30.zip` | `22c09fd67673895ef7cd6b1820563918275c3d316f2462b306208675118db3c0` |
| `win-arm64` | Windows 11 / Windows Server on ARM64 | `mihomo-windows-arm64-v1.19.30.zip` | `b37c4b0259e85b020edc4215aa4c86052e21071cf520d4800364b21b4e2fc162` |
| `linux-x64` | Ubuntu 24.04+ / Ubuntu Server, x64 | `mihomo-linux-amd64-v1.19.30.gz` | `cf06ce2c7d1421bdbda14ee4a5b6046672dc35ebf8eecd8e77504ec3c0ed9a84` |
| `linux-arm64` | Ubuntu 24.04+ / Ubuntu Server, ARM64 | `mihomo-linux-arm64-v1.19.30.gz` | `58896873736d28628f66de3677c8654fa0f180662523148e136cff4f6e890069` |

The only V1 managed Runtime version is stable `v1.19.30`; prerelease/Alpha,
"latest" URLs, compatibility variants, distribution packages, x86, and every
other platform are refused with `proxy.runtime_unsupported_platform` or
`proxy.runtime_version_unsupported`. Goal 3 must place the four exact HTTPS
release URLs, version, asset name, hash, source release URL, and retrieval
timestamp in a source-controlled `MihomoRuntimeManifest`; no network response
can add or replace an entry. The source of record is the official
`MetaCubeX/mihomo` GitHub Release `v1.19.30`, published 2026-08-16.

## Host ownership, schema, and protected paths

Proxy state is machine-owned and never stored in `RemoteOsDbContext`, workspace
preferences, an app manifest, or a user-owned row. Goal 4 adds schema migration
**8** to `HostGlobalMigrationRunner` for `proxy_profiles`, `proxy_runtime_state`,
`proxy_operations`, `proxy_audit_entries`, and `proxy_safety_state`. IDs are
GUIDs, dates are UTC text, and no column contains YAML or a secret. A dedicated
Proxy metadata repository uses this host-global schema exclusively.

| Kind | Windows | Linux | Retention / access |
| --- | --- | --- | --- |
| managed binaries | `%ProgramData%\\RelaxKonOS\\Proxy\\engines\\mihomo\\versions` | `/opt/remoteos/proxy/engines/mihomo/versions` | machine administrators only; `active` and `previous` are atomic pointers |
| raw YAML, overlay, backup, runtime state, recovery marker | `%ProgramData%\\RelaxKonOS\\Proxy\\state` | `/var/lib/remoteos/proxy` | service account and administrators only; backups rotate to the most recent 5 successful generations |
| service configuration | `%ProgramData%\\RelaxKonOS\\Proxy\\config` | `/etc/remoteos/proxy` | protected, generated only from structured inputs plus validated raw YAML |
| sanitized operational log | `%ProgramData%\\RelaxKonOS\\Proxy\\logs` | `/var/log/remoteos/proxy` | 10 MiB per file, 5 files; controller/credential values are redacted before write |
| encrypted controller/subscription secrets | Proxy-specific Data Protection storage | Proxy-specific Data Protection storage | purpose-separated `RelaxKonOS.Proxy.SecretStore.v1`; no list/export/read API |

Profile metadata may reference a protected YAML file by opaque identifier, but
does not contain its text. The active profile, recovery marker reference,
runtime selection, operation/audit references, and safety state are host-wide.
All paths are supplied by `IProxyPlatformPaths`; business services receive no
absolute path and must not create one.

## Authorization and capability mapping

App permissions are desktop affordances only. The Server is authoritative and
uses JWT role policies, never a client app ID: `ProxyRead` permits `controller`
or `observer`; `ProxyManage`, `ProxyRuntimeManage`, `ProxyTunManage`, and
`ProxyRecoveryExecute` require `controller`. The latter three additionally
require an installed, platform-specific privileged-operation deployment and
return `proxy.privileged_operation_unavailable` when it is unavailable. Every
dangerous mutation requires an `Idempotency-Key`, creates a durable operation,
and is audited.

| Stable app capability | Server policy | Scope |
| --- | --- | --- |
| `server.proxy.read`, `server.proxy.profile.read`, `server.proxy.connection.read`, `server.proxy.tun.read`, `server.proxy.runtime.read` | `ProxyRead` | safe state and sanitized diagnostics |
| `server.proxy.manage`, `server.proxy.profile.manage`, `server.proxy.connection.close` | `ProxyManage` | profile/lifecycle/node/connection actions |
| `server.proxy.runtime.manage` | `ProxyRuntimeManage` | verified managed runtime only |
| `server.proxy.tun.manage` | `ProxyTunManage` | TUN activation/disable after Goal 5 validation |
| `server.proxy.recovery.execute` | `ProxyRecoveryExecute` | emergency safe-network recovery |

## Problem-code contract and unsupported behavior

Public codes are lowercase dotted ASCII and are declared once by the Protocol.
The initial closed set is: `proxy.runtime_not_installed`,
`proxy.runtime_unsupported_platform`, `proxy.runtime_version_unsupported`,
`proxy.runtime_archive_unavailable`, `proxy.runtime_integrity_failed`,
`proxy.runtime_health_check_failed`,
`proxy.external_runtime_invalid`, `proxy.service_unavailable`,
`proxy.privileged_operation_unavailable`, `proxy.config_invalid`,
`proxy.config_apply_failed`, `proxy.controller_unavailable`,
`proxy.controller_response_invalid`, `proxy.controller_timeout`,
`proxy.management_route_unsafe`, `proxy.platform_capability_unavailable`,
`proxy.tun_permission_required`, `proxy.tun_activation_failed`,
`proxy.recovery_required`, `proxy.recovery_failed`,
`proxy.operation_interrupted`, `proxy.idempotency_key_required`,
`proxy.permission_denied`, and `proxy.not_supported`.

Unimplemented engine capabilities return `proxy.not_supported`; unsupported
hosts return `proxy.platform_capability_unavailable`; a controller failure never
forwards its body. The Client localizes codes and does not display raw controller
or OS output.

## Restricted privileged-operation boundary

Goal 3 introduces `IProxyPrivilegedOperations`, whose methods are only
`InstallRuntime`, `RemoveRuntime`, `ReplaceRuntime`, `InstallService`,
`RemoveService`, `SetServiceStartup`, `StartService`, `StopService`,
`RestartService`, `WriteProtectedConfiguration`, `RestoreNetworkConfiguration`,
and `RepairService`. Each accepts validated typed requests with IDs, hashes,
and fixed paths; it accepts no executable, command line, argument list, shell
text, environment, password, or client-provided path. Windows service control
and Linux systemd/route/DNS code remain inside their platform implementations.
There is deliberately no fallback to `Process.Start` in domain services.

No Defender, SmartScreen, UFW, nftables, iptables, or Windows Firewall change
is authorized by Proxy Manager. Firewall may be diagnosed only. External
runtime detection is read-only; even when explicitly selected for a RelaxKonOS
private configuration, RelaxKonOS does not overwrite, upgrade, uninstall, or
stop a user-owned process or binary.

## Audit and threat model

Every install/update/rollback/uninstall, lifecycle, profile/configuration
change, node selection, connection close, TUN transition, recovery action, and
rejection produces a secret-free audit event containing actor, session, host,
engine, profile ID when applicable, operation/correlation ID, result,
problem-code, and UTC timestamp. Audit/log/exception/DTO fields never contain
controller secrets, URLs with tokens, auth headers, proxy credentials, UUIDs,
private/WireGuard keys, full YAML, or command output.

The implementation and tests must reject or safely handle malicious archives
and path traversal; hash/architecture/binary validation failures; YAML and
command injection; secret/log/exception disclosure; public controller binding;
interrupted operations; route/DNS damage; a lost active management route;
Defender or organization-policy rejection; absent privilege deployment; and
Server/Mihomo crash or reboot during a TUN transition. A recovery marker and
network snapshot are written before every network-changing action. No TUN API
or UI is exposed until Goal 5 verifies the complete recovery path on the Server
and platform adapters.
