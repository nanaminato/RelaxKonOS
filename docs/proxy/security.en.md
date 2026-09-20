# Proxy security

## File ownership and elevation boundary

The Server service account owns the fixed directories for Proxy runtimes, configuration, state, GEO data and diagnostics, and performs its atomic writes there directly rather than through a generic elevation endpoint. The Linux installer makes `/var/lib/relaxkonos/proxy`, `/etc/relaxkonos/proxy` and `/var/log/relaxkonos/proxy` private to that account. The Windows installer grants Modify on the ProgramData Proxy root only to the Server service SID. Proxy state, audit and configuration directories re-apply `0700`; state, audit, configuration and controller-secret files use `0600`.

Only systemd-unit installation/removal and fixed service verbs use the constrained root Helper. It accepts no caller-controlled file path, executable, arguments or command text. The Server records secret-free operation state around install, uninstall and rollback; inaccessible protected storage or Helper failures fail closed and never fall back to a sudo command.

Windows “system proxy” values are in the interactive user's `HKCU`. In System Mode the Server runs as a service account, so it refuses to write its own HKCU and claim that the desktop user's proxy changed. That operation remains fail-closed until an authenticated per-user companion can apply it in the target sign-in session.

Subscription payloads can contain proxy credentials and are never persisted to diagnostics, including in development. Diagnostics contain only sanitized events, result codes, byte counts and an irreversible source-host hash.

The public API exposes engine-neutral, safe DTOs only. It never returns raw YAML, controller addresses or secrets, subscription credentials, private keys or arbitrary command arguments. Controller logs are bounded and sanitized.

No Proxy workflow disables Defender or a firewall, opens a public controller port, asks for an OS password, or provides a generic privileged-command endpoint.
