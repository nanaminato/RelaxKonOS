# Unified Privileged Helper Architecture

Status: Proposed

This document defines the target architecture for privileged host operations in RelaxKonOS. It is intentionally a design document only; implementation is left to a follow-up change.

## 1. Goals

RelaxKonOS needs privileged host operations for features such as firewall management, certificates, networking/TUN, services, mounts, package management and other system administration features. The Server process itself should not need to run as root/LocalSystem.

The design must:

- provide one cross-platform abstraction to Server business modules;
- keep privileged execution behind a narrow local privilege boundary;
- use a persistent operating-system service on Windows and Linux;
- avoid spawning `sudo` for every Linux operation;
- support reliable request/response semantics, auditing and version negotiation;
- allow future transports/hosts, including macOS, without changing business modules;
- provide a migration path from the existing Linux sudo/helper approach;
- leave room for systemd socket activation as a later Linux lifecycle optimization.

## 2. Non-goals

This design does not implement the helper, IPC transports, installers or privileged operations.

The helper is not a generic remote shell. It MUST NOT expose APIs such as `ExecuteCommand(string)`, arbitrary process execution, arbitrary shell scripts, arbitrary file writes, or an unrestricted root command proxy.

The helper is local-only. It MUST NOT expose TCP/HTTP/WebSocket listeners to the network.

## 3. Architecture

```text
Server business modules
        |
        v
IPrivilegedHelperClient
        |
        +-------------------+
        |                   |
        v                   v
Windows transport       Linux transport
Named Pipe              Unix Domain Socket
        |                   |
        v                   v
Privileged Helper       Privileged Helper
Windows Service         systemd system service
LocalSystem             root
        |                   |
        +---------+---------+
                  |
                  v
       allow-listed privileged operations
```

Business modules depend only on `IPrivilegedHelperClient`. Operating-system selection belongs in the composition root / dependency injection registration, not in feature code.

Conceptually:

```csharp
public interface IPrivilegedHelperClient
{
    Task<PrivilegedResponse<TResponse>> InvokeAsync<TRequest, TResponse>(
        PrivilegedOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken = default);
}
```

The exact C# API may differ, but it SHOULD preserve typed operations and MUST NOT degrade into a string command API.

Feature code should look like:

```csharp
public sealed class FirewallService(IPrivilegedHelperClient privilegedHelper)
{
    // Calls a typed firewall operation.
}
```

Feature code should not contain transport selection such as `OperatingSystem.IsWindows()` followed by pipe/sudo/socket logic.

## 4. Components

### 4.1 Privileged protocol

The IPC contract SHOULD live in a small shared assembly/module independent from Server business logic and helper host implementation, for example `RelaxKonOS.PrivilegedProtocol` if a new project is appropriate when implementation begins.

The protocol defines operation identifiers, request/response DTOs, protocol version information, stable error codes and framing limits. Both Server and Helper consume this contract.

Each privileged capability MUST be explicitly modeled. Examples include service start/stop/restart, firewall rule mutation, certificate installation/removal, selected network configuration, mount/unmount and narrowly scoped package operations.

Operations SHOULD be idempotent where practical. Where they cannot be idempotent, the operation contract MUST document retry behavior.

### 4.2 Server client

`IPrivilegedHelperClient` is the only privileged-helper dependency exposed to normal Server modules.

Platform implementations are responsible for connection establishment, framing, protocol negotiation, timeouts, cancellation, transport errors and response validation. They are not responsible for feature policy.

Expected implementations:

- Windows: Named Pipe client.
- Linux v1: Unix Domain Socket client.
- Future macOS: transport/host appropriate to macOS without changing callers.

### 4.3 Helper host

The helper runs with elevated operating-system identity and accepts requests only from the local authorized Server identity.

It owns transport hosting, caller authentication, protocol validation, operation dispatch, privilege-sensitive validation, execution, audit logging and stable error conversion.

The dispatcher MUST reject unknown operations and unsupported protocol versions safely.

## 5. Windows model

Windows uses a persistent Windows Service running as LocalSystem (or a future more restricted service identity if capabilities permit) and a local Named Pipe.

The pipe ACL MUST restrict connection to the intended RelaxKonOS Server service identity/service SID and required administrative identities. The helper MUST NOT rely only on a pipe name being difficult to guess.

The existing architectural direction of a persistent Windows helper should be retained. The unified client abstraction is intended to make Linux match this lifecycle model rather than make Windows behave like per-call elevation.

## 6. Linux v1: persistent systemd service

Linux v1 SHOULD use a persistent system-level systemd service running the helper as root. Server communicates through an AF_UNIX Unix Domain Socket.

Recommended socket path:

```text
/run/relaxkonos/privileged.sock
```

`/run` is appropriate for runtime state and is cleared at boot. The runtime directory SHOULD be managed by systemd rather than manually creating persistent directories under `/run`.

A conceptual service configuration is:

```ini
[Unit]
Description=RelaxKonOS Privileged Helper
Before=relaxkonos-server.service

[Service]
Type=simple
ExecStart=/opt/relaxkonos/RelaxKonOS.PrivilegedHelper
User=root
Group=root
RuntimeDirectory=relaxkonos
RuntimeDirectoryMode=0750
Restart=on-failure
RestartSec=2s

[Install]
WantedBy=multi-user.target
```

The final unit may differ based on packaging and hardening. The helper does not need `network.target` merely to provide local IPC.

Server ordering SHOULD ensure the helper is available before privileged calls are expected. Whether `Requires=` is desirable depends on whether Server should remain useful when the helper fails. Prefer graceful degradation: ordinary non-privileged Server functionality should generally continue, while privileged features report helper unavailability.

### 6.1 Socket ownership and authorization

The Unix socket MUST not be world-accessible. A dedicated RelaxKonOS service identity/group SHOULD be used where packaging permits. A typical target is owner/group equivalent to `root:relaxkonos` and mode `0660`, with the Server identity in the authorized group.

Filesystem permissions are the first authorization boundary. Linux peer credentials (`SO_PEERCRED`) SHOULD additionally be inspected where practical to validate the connecting UID/GID/PID. Peer credential checks are defense in depth, not a replacement for socket ACLs.

Do not authenticate a caller solely by resolving `/proc/<pid>/exe`; PID reuse and process lifetime races make that unsuitable as the primary trust mechanism.

### 6.2 Stale socket handling

When the helper owns socket creation, startup code MUST handle an existing path defensively. It MUST NOT blindly delete an arbitrary filesystem object at the configured path. Before removal, verify that the path is the expected socket type and apply appropriate ownership/location checks.

Systemd-managed runtime directories reduce stale-state complexity across reboot.

## 7. Linux v2+: systemd socket activation

Socket activation is a desirable later enhancement, but it SHOULD NOT block the first persistent-service implementation.

With socket activation, systemd owns `/run/relaxkonos/privileged.sock` and starts the helper when the Server connects. This provides deterministic socket ownership/permissions, removes helper-managed stale-socket cleanup, allows the helper to consume no process memory while idle, and lets systemd queue initial connections while activating the service.

Conceptual socket unit:

```ini
[Unit]
Description=RelaxKonOS Privileged Helper Socket

[Socket]
ListenStream=/run/relaxkonos/privileged.sock
SocketUser=root
SocketGroup=relaxkonos
SocketMode=0660
RemoveOnStop=true

[Install]
WantedBy=sockets.target
```

Conceptual service relationship:

```ini
[Unit]
Description=RelaxKonOS Privileged Helper
Requires=relaxkonos-privileged-helper.socket
After=relaxkonos-privileged-helper.socket

[Service]
Type=simple
ExecStart=/opt/relaxkonos/RelaxKonOS.PrivilegedHelper
User=root
Group=root
```

The exact unit relationship MUST be tested against the chosen activation behavior; implementation should follow systemd semantics rather than copy these examples blindly.

### 7.1 Important .NET implementation requirement

Socket activation passes already-open listening file descriptors to the service (`LISTEN_PID`, `LISTEN_FDS`, starting at file descriptor 3). A custom .NET Unix socket server MUST explicitly support the systemd activation contract or use a library that does.

`Microsoft.Extensions.Hosting.Systemd` should not be assumed to adopt arbitrary `LISTEN_FDS` for a custom Unix socket listener automatically. The implementation Agent MUST verify this behavior and implement descriptor adoption when necessary.

The activated helper MUST use the inherited listening socket instead of unlinking/rebinding the configured path.

### 7.2 Lifecycle policy

Socket activation does not require the helper to exit after each request. A useful later policy is activation on first connection followed by an idle timeout, allowing the helper to remain alive during bursts and exit after a configurable quiet period. systemd can activate it again on the next connection.

This is an optimization only. Correctness and security must not depend on whether the helper is persistent forever or activation/idle-timeout based.

## 8. Protocol framing

The transport MUST use explicit message framing; do not assume one socket/pipe read equals one message.

A simple implementation may use a fixed-size length prefix followed by a bounded serialized payload (for example JSON using existing RelaxKonOS serialization conventions). The maximum frame size MUST be enforced before allocation.

A request should carry at least:

```text
ProtocolVersion
RequestId
Operation
Payload
```

A response should carry at least:

```text
ProtocolVersion
RequestId
Success
ErrorCode (when failed)
ErrorMessage (safe for caller)
Payload (when successful)
```

Request IDs allow responses/logs to be correlated and leave room for multiplexing. Unknown fields should follow a documented compatibility policy.

The protocol SHOULD include an explicit handshake/capabilities exchange so Server can distinguish "helper unavailable" from "helper installed but incompatible" and can discover optional operations introduced by newer versions.

## 9. Versioning and compatibility

Protocol versioning is independent from the RelaxKonOS product version.

Prefer major/minor semantics:

- incompatible framing/semantic changes increment major;
- backward-compatible operations or fields increment minor;
- capability negotiation determines whether a particular operation is available.

During upgrade, Server and Helper may temporarily be different package versions. The client MUST fail gracefully with a stable `HelperUpgradeRequired` / `ProtocolIncompatible` style error instead of producing obscure IPC failures.

A rolling package upgrade SHOULD avoid a state where an old Server can accidentally invoke a newly changed operation with different semantics.

## 10. Request execution semantics

Every request MUST have bounded execution behavior. Server client calls SHOULD support cancellation and a configured timeout. Helper-side operations SHOULD also enforce operation-specific limits where practical so a disconnected caller cannot leave unlimited work queued.

Transport disconnection does not automatically mean an already-started privileged operation can safely be rolled back. Each operation contract SHOULD state whether cancellation is supported and what happens after dispatch.

The helper SHOULD apply a global connection limit, per-connection outstanding-request limit and/or bounded work queue. A local authenticated client must not be able to exhaust root helper memory through unlimited parallel requests.

Long-running privileged jobs should not keep an IPC request open indefinitely if a durable job model is more appropriate. Such operations can return a job identifier with separately modeled status/cancel operations.

## 11. Security boundary

Treat the helper as a local privilege boundary, not as a convenience utility.

Required principles:

1. No arbitrary command/shell execution RPC.
2. Explicit allow-listed typed operations.
3. Validate every privileged parameter again inside the helper; Server-side validation is insufficient.
4. Canonicalize and constrain paths for file-affecting operations; defend against traversal and symlink/reparse-point attacks where relevant.
5. Never concatenate untrusted values into shell command strings. Prefer native APIs or direct process invocation with an argument list when an external system tool is unavoidable.
6. Apply least privilege to IPC access.
7. Bound message sizes, connection counts, queues and execution time.
8. Do not deserialize arbitrary runtime types supplied by the caller.
9. Do not log secrets, passwords, private keys, access tokens or sensitive payloads.
10. Audit security-relevant mutations with request ID, operation, result and appropriate caller identity.

The fact that RelaxKonOS Server is trusted does not remove the need for helper-side validation. A compromised Server process should receive only the privileged capabilities intentionally exposed by the helper API, not an automatic arbitrary-root primitive.

## 12. Operation design guidelines

Operations should express intent rather than implementation commands. Prefer:

```text
RestartService(serviceId)
AddFirewallRule(rule)
RemoveFirewallRule(ruleId)
InstallCertificate(certificate, targetStore)
SetTunConfiguration(configuration)
MountVolume(request)
```

Avoid:

```text
Run("systemctl restart " + name)
Run("nft ...")
RunShell(script)
WriteRootFile(anyPath, bytes)
```

Where possible, operation inputs should use constrained enums/IDs rather than arbitrary strings. For example, service management may initially be restricted to RelaxKonOS-managed services rather than every service on the machine.

## 13. Error model

Transport exceptions should not leak directly into business modules. Define stable categories such as:

```text
HelperUnavailable
ProtocolIncompatible
OperationUnsupported
UnauthorizedCaller
InvalidRequest
PermissionDenied
Conflict
NotFound
Timeout
Cancelled
Busy
OperationFailed
InternalHelperError
```

Platform-specific details may be retained for diagnostics but business logic should receive stable semantic errors.

## 14. Availability and health

`IPrivilegedHelperClient` SHOULD expose or internally maintain a lightweight way to determine helper/protocol availability without requiring every feature to invent its own probe.

Do not make general Server startup fail solely because the helper is unavailable unless the deployment mode explicitly requires privileged functionality. Surface degraded state to diagnostics/health APIs and disable only features that require the helper.

Repeated failures should use bounded retry/backoff rather than tight reconnect loops.

## 15. Auditing and observability

Helper logs should include protocol/version startup information, connection authorization failures, request ID, operation name, duration, result/error category and lifecycle events.

Sensitive operation payloads should be redacted or summarized. Audit logs must never become a second storage location for credentials or private key material.

Server and Helper should use the same request ID for correlated logs.

Metrics may later include active connections, request counts/duration by operation, failures by stable error category and queue saturation. Avoid high-frequency sampling when nobody consumes it.

## 16. systemd hardening

The Linux helper starts as root because its operation set may span networking, firewall, mounts, packages and certificates. Hardening SHOULD be added incrementally and verified against actual required operations.

Candidate directives include `NoNewPrivileges`, `PrivateTmp`, `ProtectHome`, `ProtectSystem`, `RestrictAddressFamilies`, `SystemCallFilter`, capability bounding and namespace restrictions. Do not enable a hardening directive merely because it looks secure if it silently breaks required administration operations.

As the operation set matures, split capabilities or separate helpers may be considered if a broad root helper becomes unnecessarily powerful. This is not required for the first implementation.

## 17. Packaging and installation

Linux packaging should eventually install the helper binary, service unit and (for v2) socket unit as first-class product components.

For v1 persistent service, installer responsibilities include creating/ensuring the intended service user/group model, installing units, `daemon-reload`, enabling/starting the helper as appropriate, and ensuring Server can connect without sudo.

For v2 socket activation, enable the socket unit rather than relying on the helper service to be permanently started. Upgrade/uninstall must stop/disable the correct units and remove only RelaxKonOS-owned runtime/configuration artifacts.

Windows installation similarly owns service creation, service identity/ACL setup and helper upgrade/restart.

Helper binary and Server should normally be upgraded from the same signed/trusted RelaxKonOS package even though protocol compatibility tolerates a temporary version skew.

## 18. Migration from Linux per-call sudo helper

Migration SHOULD be staged so upgrades do not abruptly remove privileged functionality.

Suggested sequence:

### Phase A - abstraction

Introduce `IPrivilegedHelperClient` and route existing privileged call sites through it while retaining existing behavior where needed.

### Phase B - persistent Linux helper

Install/start the systemd helper and Unix socket transport. Prefer the socket transport when available. During a bounded migration release, an explicitly configured legacy fallback may remain if necessary.

### Phase C - remove legacy sudo path

Once packaging/upgrades reliably install the service, remove automatic per-operation sudo launching and its sudoers configuration. Uninstall/upgrade scripts clean obsolete RelaxKonOS sudoers entries only when ownership/content is verified.

### Phase D - optional socket activation

Move socket ownership to systemd and add activation/idle lifecycle support. The `IPrivilegedHelperClient` and operation protocol should remain unchanged.

Do not make business modules aware of these migration phases.

## 19. Failure scenarios to test

Implementation must explicitly test at least:

- helper not installed;
- helper stopped/crashed;
- helper restarts between requests;
- Server starts before helper;
- incompatible protocol major version;
- supported protocol but unsupported operation;
- malformed/oversized/truncated frame;
- unauthorized local process attempts to connect;
- timeout/cancellation during an operation;
- Server disconnects during execution;
- helper reaches concurrency/queue limit;
- stale Unix socket path in v1;
- socket activation cold start in v2;
- helper crashes after systemd accepts/queues an activated connection;
- product upgrade with Server/Helper temporary version skew;
- product uninstall/reinstall;
- reboot and `/run` recreation.

Security tests should also attempt path traversal, symlink substitution, argument injection and malformed serialized payloads against applicable operations.

## 20. Suggested implementation structure

Exact paths should follow repository conventions at implementation time, but the conceptual split is:

```text
RelaxKonOS.Server
  Privileged/
    IPrivilegedHelperClient
    WindowsNamedPipePrivilegedHelperClient
    LinuxUnixSocketPrivilegedHelperClient

RelaxKonOS.PrivilegedProtocol
  Protocol version/capabilities
  Request/response envelope
  Stable errors
  Typed operation contracts

RelaxKonOS.PrivilegedHelper
  Host
  IPC listeners
  Caller authorization
  Dispatcher
  Operation handlers
```

Platform-specific operation handlers may live behind internal abstractions where behavior genuinely differs. Do not force identical Windows/Linux implementation details merely to achieve superficial symmetry; unify the Server-facing contract and semantics.

## 21. Implementation acceptance criteria

A future implementation PR should be considered complete for Linux v1 only when all of the following are true:

- normal Server modules depend on `IPrivilegedHelperClient`, not sudo/pipe/socket details;
- Windows continues through a persistent Named Pipe helper;
- Linux Server can perform supported privileged operations through a persistent root systemd helper over UDS without per-call sudo;
- the socket is inaccessible to unauthorized users;
- caller identity is validated using OS-supported mechanisms in addition to path secrecy;
- arbitrary shell/command execution is not part of the public privileged protocol;
- framing is bounded and handles partial reads/writes;
- protocol compatibility/capabilities are negotiated;
- errors are mapped to stable semantic categories;
- timeouts, cancellation and concurrency limits exist;
- privileged mutations are safely audited without secret leakage;
- install, upgrade, uninstall and reboot scenarios are tested;
- legacy sudo fallback, if temporarily retained, is explicitly marked transitional and removable.

Socket activation is a separate v2 acceptance target. It is complete only when systemd owns the socket, the .NET helper correctly adopts the inherited descriptor, cold activation/restart behavior is tested, and the Server-facing API/protocol requires no change.

## 22. Design decisions

For the initial implementation, the recommended decisions are:

- **Cross-platform boundary:** `IPrivilegedHelperClient`.
- **Windows IPC:** Named Pipe.
- **Linux v1 IPC:** Unix Domain Socket.
- **Windows lifecycle:** persistent Windows Service.
- **Linux v1 lifecycle:** persistent systemd system service.
- **Linux v2 lifecycle:** systemd socket activation, optionally with helper idle shutdown.
- **Privilege:** LocalSystem on Windows and root on Linux initially, narrowed later where practical.
- **API philosophy:** typed, allow-listed privileged capabilities; never a generic root shell.
- **Availability philosophy:** privileged features degrade independently; ordinary Server functionality should remain available when possible.
- **Compatibility:** explicit protocol version + capabilities, tolerant of temporary Server/Helper package version skew.

These decisions keep the privilege boundary stable while allowing lifecycle and hardening to evolve independently.