# Proxy Manager architecture

`relaxkonos.proxy` is host-global. Avalonia calls only `/api/v1.0/proxy`; the Server owns the engine registry, runtime, protected configuration, operation ledger and audit trail. Mihomo remains a loopback-only implementation of the engine boundary. Controller schemas and secrets never cross the Server API.

High-risk mutations use an `Idempotency-Key`, return a durable operation ID and are recorded without configuration, credentials or controller secrets.
