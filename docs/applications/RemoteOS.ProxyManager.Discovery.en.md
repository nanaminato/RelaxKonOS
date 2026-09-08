# Proxy Manager implementation discovery

> Status: **implemented at code level; release-level platform validation remains**  
> Reviewed: 2026-09-04  
> Scope: the current `feature_privileged` tree, Proxy Manager commit history, and the skipped-test register. This document makes no product-code changes.

## Current conclusion

The former Phase 0 conclusion—“there is no Proxy Manager and implementation must start at Phase 1”—is obsolete. This branch now contains the `remoteos.proxy` built-in app, the `/api/v1.0/proxy` API, a Server-only Mihomo adapter, protected configuration and subscription storage, runtime lifecycle management, a constrained privileged-operation boundary, a TUN transaction framework, and audit/operation ledgers.

That does not make Proxy Manager V1 release-ready. Real Windows/Ubuntu privileged operations, Mihomo lifecycle, TUN route/DNS changes, and crash/reboot recovery have not yet been validated on isolated hosts; the current production network platform refuses a change when it cannot prove it is safe. The next phase is **controlled platform validation and release closure**, not a restart at Phase 1.

## Implemented capabilities

| Area | Current implementation |
| --- | --- |
| Contracts and authorization | `Shared/RemoteOS.Protocol/Proxy` supplies engine-neutral DTOs, routes, states, and stable `proxy.*` problem codes; `MapProxyEndpoints` maps `/api/v1.0/proxy`. `ProxyRead`, `ProxyManage`, and `ProxyDangerous` policies separate reading, management, and dangerous runtime/TUN work. Long operations use durable operation IDs and `Idempotency-Key`. |
| Server and Mihomo | `MihomoEngine`, a Server-only loopback Controller client, protected Controller-secret storage, runtime status, groups, selection, routing mode, latency tests, connection closure, traffic/memory, logs, and DNS status are wired up. The Controller address, secret, and raw Controller JSON do not cross the Client API. |
| Managed runtime | `MihomoRuntimeManager` uses a source-controlled trusted manifest. It installs from download or a Server file and applies size/archive-path/hash/architecture/version checks, staging, health checks, active/previous switching, rollback, and uninstall. Linux uses constrained `systemd` operations; Windows uses `WindowsMihomoProcessHost` for the Mihomo process tree, restart after unexpected exit, and cleanup on host shutdown. |
| Configuration and subscriptions | Host-global SQLite metadata, protected raw YAML, serialized configuration transactions, backup/atomic commit/reload/health-check/rollback, and subscription import/refresh/activation with encrypted URLs are implemented. Subscriptions default to public HTTPS only, disallow redirects, and bound response size; a verified system-proxy route can be selected explicitly. Base64/plain node lists can be converted to Mihomo YAML, and protected local `geoip.metadb` supports offline validation and runtime use. |
| Privilege and recovery | `IProxyPrivilegedOperations` permits only fixed Mihomo runtime, service, and network-recovery actions; it accepts no generic commands, arguments, or passwords. The unified privileged-helper path covers fixed Linux `remoteos-mihomo.service` actions. If the Helper, Windows service privilege, or pipe ACL/shared secret is unavailable, the current branch returns `proxy.privileged_operation_unavailable` with the common Chinese/English/Japanese remediation guidance. TUN has a global lock, management-route plan, recovery marker, recovery hosted service, disable, and emergency-disable flows. |
| Avalonia | `IProxyRepository` / `RemoteProxyRepository` and the single-window `remoteos.proxy` app are registered. The workspace has Overview, Subscriptions, Proxy Groups, Connections, Logs, and Settings; it supports runtime install/rollback/uninstall, lifecycle, subscriptions, node selection, routing mode, latency tests, system proxy, TUN settings, and emergency disable. Every request uses the typed RemoteOS API; Chinese, English, and Japanese resources are present. |
| Observability and tests | Runtime, lifecycle, TUN, subscription, configuration, group, and connection actions receive secret-free audit entries; diagnostic logs are bounded and sanitized. `RemoteOS.Server.Tests` covers contracts, host-global persistence, encrypted subscription storage/download limits, GEO data, configuration transactions, TUN fail-closed/recovery-marker behavior, Controller safety, and runtime archive/rollback behavior in process. |

## Recent implementation changes

- Since 2026-09-01, Windows no longer creates a second SCM service: `RemoteOS.Server` owns Mihomo through `WindowsMihomoProcessHost`; Linux continues to use `remoteos-mihomo.service`.
- From 2026-09-01 through 03, secure subscription import, offline GeoIP, proxy groups/routing mode/latency testing, traffic and memory, system proxy, and managed TUN configuration were completed. Ordinary refreshes no longer restart the runtime or pull subscriptions indirectly.
- On 2026-09-04, the unified privileged-helper path made Proxy preserve and present the structured “privileged helper unavailable” code and platform-specific remediation consistently with other privileged features.

## Remaining gaps and release gates

1. **Real platform validation is outstanding.** The Windows/Windows Server and Ubuntu/Ubuntu Server cases in [`RemoteOS.ProxyManager.SkippedTests.en.md`](../testing/RemoteOS.ProxyManager.SkippedTests.en.md) still need an isolated, disposable host: managed runtime install/update/rollback, service lifecycle, TUN enable/disable, emergency recovery, and recovery after Mihomo/Server/OS crash or reboot.
2. **TUN currently fails closed.** `HostProxyNetworkSafetyPlatform` only reads Linux's default route to create a management-path plan; Windows returns no plan, and apply, verify, and restore currently refuse every change. The API/UI and recovery model therefore exist, but the default platform implementation does not claim it can safely alter real routes or DNS yet.
3. **Privileged deployment is a prerequisite.** Linux needs the deployed root-owned Helper, fixed published directory, and sudoers configuration. Windows needs the service, named-pipe ACL, and shared secret. A missing prerequisite must make runtime/service work fail; it must never fall back to a shell or collect an OS password.
4. **API-host integration verification remains.** In-process Server tests cover most domain safety paths; the skipped-test register still calls for a real API-host fixture to exercise proxy authorization, idempotency, operation recovery, and audit output.
5. **The scope is still a one-engine V1.** Only Mihomo is implemented. sing-box/Xray, centralized multi-host orchestration, visual rule editing, traffic history, and automatic subscription refresh are outside the current scope. System proxy is supported only on Windows; startup-at-boot is deliberately unimplemented and displayed as disabled.

## Entry point for the next phase

First execute PM-G5-WIN-01 through 03 and PM-G5-UBU-01 through 03 from [`RemoteOS.ProxyManager.SkippedTests.en.md`](../testing/RemoteOS.ProxyManager.SkippedTests.en.md) on disposable VMs with a second management connection. Record the RemoteOS revision, Mihomo asset hash, environment, result, and problem code. Then add and run the PM-G6-G8-API-01 API-host fixture. Do not mark Proxy Manager V1 complete—or first enable TUN on a production host—until every case passes.

The intended scope and long-term safety constraints remain in [`RemoteOS.ProxyManager.Design.md`](./RemoteOS.ProxyManager.Design.md); the execution baseline is [`RemoteOS.ProxyManager.Goal.md`](./RemoteOS.ProxyManager.Goal.md); operator documentation is under [`docs/proxy/`](../proxy/). If those early planning documents conflict with this page's current-implementation account, this page, the code, and the skipped-test register take precedence and the planning documents should be corrected in follow-up maintenance.
