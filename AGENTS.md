# API evolution policy

Until the first formal RemoteOS release, do not retain backward-compatibility shims for built-in APIs, wire contracts, routes, or package manifests. Adopt the current interface directly and make breaking upgrades when an interface changes. Update all in-repository callers, tests, examples, and documentation in the same change; do not add legacy aliases, fallback routes, migration adapters, or dual-format parsing.
