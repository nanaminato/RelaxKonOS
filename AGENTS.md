# API evolution policy

Until the first formal RelaxKonOS release, do not retain backward-compatibility shims for built-in APIs, wire contracts, routes, or package manifests. Adopt the current interface directly and make breaking upgrades when an interface changes. Update all in-repository callers, tests, examples, and documentation in the same change; do not add legacy aliases, fallback routes, migration adapters, or dual-format parsing.

# Android documentation ownership

Android-specific product and implementation documentation is owned by
`Client/RelaxKonOS.Client.Android/docs/`. This includes the mobile application
catalog, Compose interaction and adaptive-layout specifications, Android
internationalization and theme rules, release procedures, implementation
progress, and Android-only decisions. Do not add or maintain a second canonical
copy under the repository-level `docs/` tree.

`docs/mobile/` is reserved for a short, cross-repository introduction that links
to the Android-owned documentation. Repository-level architecture or protocol
documents may link to Android documents, but must not duplicate their detailed
mobile design.
