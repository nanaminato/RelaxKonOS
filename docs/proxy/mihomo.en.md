# Mihomo runtime

Managed Mihomo installations are selected only from the fixed Server manifest and verified before activation. Releases use immutable version directories with active/previous rollback state. External runtimes are detection-only unless an administrator explicitly chooses a RelaxKonOS-managed instance.

The controller binds locally and its secret remains in the Proxy-scoped protected store.

The Server samples Mihomo NDJSON with `ResponseHeadersRead`: traffic and memory each return their first complete record; logs observe at most one second, with a 64 KiB line bound. Quiet logs return an empty list and caller cancellation propagates. Connection protocol comes from `metadata.network`; endpoints include ports and prefer the destination hostname. Mihomo `connections: null` means zero trackers. Failed controller reads return a problem-coded 503 from `GET /api/v1.0/proxy/connections`, rather than a successful empty list. The desktop connections page refreshes independently every three seconds, preserves a still-active selection, reports counts, empty state or read failure, and stops its timer when detached.
