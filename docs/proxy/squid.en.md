# Squid HTTP gateway

The first backend phase is implemented. This is not yet a complete client-facing MVP. The detailed design, API examples, recovery procedure and Linux acceptance checklist are maintained in [squid.md](squid.md).

Squid has a separate `GatewayProxy` contract and Server service. It does not implement the Mihomo-oriented outbound `IProxyEngine`. A manually configured HTTP parent can point at Mihomo's actual HTTP/mixed listener. Parent routing uses `never_direct` to prevent silent direct fallback; see the official [cache hierarchy documentation](https://wiki.squid-cache.org/Features/CacheHierarchy).

Implemented:

- Default loopback listener, explicit remote opt-in, required source CIDRs without `/0`, source-constrained domain allow/deny lists and final deny-all.
- HTTP forwarding and HTTPS CONNECT restricted to destination ports 80/443 and CONNECT 443 respectively. Manager, loopback, link-local and multicast destinations are denied first.
- Structured preview, independent native parse with bounded diagnostics, explicit existing-configuration adoption, backup, atomic publication, reload failure rollback and a pending recovery marker.
- Fixed Linux package paths and squid.service lifecycle through closed Helper operations. Root ownership, non-writable paths and symlink checks apply to managed configuration files.
- Status, existing authorization policies, durable operation ledger, idempotency keys and auditing. Linux User Mode blocks this API family. Windows mutation is unsupported.

API root: `/api/v1.0/gateway-proxy/squid`. GET `/status` requires `ProxyRead`. POST `/configuration/preview`, `/configuration/validate`, `/configuration/apply` and `/lifecycle` require `ProxyManage`. Apply and lifecycle require a unique `Idempotency-Key` of at most 160 characters and return 202; poll the returned Location under `/api/v1.0/proxy/operations/{id}`. Preview and validate accept `SquidConfiguration`; apply accepts `{ "configuration": { ... }, "adoptExistingConfiguration": false }`. Lifecycle accepts an action such as `{ "action": "Reload" }`.

The full `/etc/squid/squid.conf` is owned after explicit adoption. Existing includes, authentication and caching configuration are not merged. The previous version is stored as root-private `/etc/squid/relaxkonos.previous.conf`. A pending marker blocks further mutations except stop/disable; native validation remains available. Recovery is manual after an interrupted transaction: stop Squid, inspect the marker and backup, restore the previous configuration or remove a first publication, verify ownership/permissions and native parsing, then remove the marker and start as appropriate. Do not merely delete the marker.

Stopped services remain stopped after publication. Successful reload plus systemd active is only a service-level check, not proof of applied configuration or working HTTP/CONNECT traffic. The reported SHA-256 identifies the on-disk config. There is no Configuration Registry desired/applied integration, directory-fsync/power-loss guarantee, automatic crash recovery or configuration history yet.

Package installation/removal, Basic credentials, access-log ingestion/SignalR, firewall integration, automatic Mihomo endpoint discovery, Registry revisions and both client applications are subsequent work. Squid writes its own standard access.log, but RelaxKonOS does not expose it yet. Disk caching, transparent proxying, TLS interception and multiple instances are outside this phase. Android documentation belongs exclusively in the Android-owned docs directory.

Configuration syntax targets Squid 5–7 and every application is gated by the installed native parser. See [ACL](https://www.squid-cache.org/Doc/config/acl/) and [http_access](https://www.squid-cache.org/Doc/config/http_access/) for native semantics. The first phase accepts ASCII domain names; use a leading dot for the domain and its subdomains rather than `*.`.

Run `dotnet run --project RelaxKonOS.Server.Tests/RelaxKonOS.Server.Tests.csproj --no-restore -- --squid-only` for focused renderer, injection, ordering, wire and Helper boundary tests. Server and Helper builds pass on the Windows development host; actual Linux parse, permissions, service lifecycle, HTTP/CONNECT, rollback and interruption acceptance remain unverified and must pass before production rollout.
