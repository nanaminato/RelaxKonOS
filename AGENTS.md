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

# Managed runtime lifecycle

Before adding or changing a built-in application's managed software installation,
uninstallation, version selection, update, repair, rollback, or release-source
management, read `docs/platform/RelaxKonOS.ManagedRuntimes.Design.md` and follow
its lifecycle, ownership, trust, and verification rules. Update the relevant
tracking item and application documentation in the same change. Distinguish
planned capabilities from implemented and verified behavior; a pinned single
version or same-version reinstall does not constitute multi-version updates.

# On-device server module testing

For changes confined to a server module, use the smallest supported deployment
on the test machine. Keep the existing Core and other modules running whenever
the installed host supports the module's current contracts.

- For an enabled, supervised ModuleHost, publish only the changed module for
  the target RID. Build a new version of its signed `.rkmodule` with the test
  host's accepted development or test source, then upload it and apply the
  existing package plan. Use the module management stop/restart actions when
  testing the same installed version. Keep version directories immutable.
- Check the package operation receipt, the ModuleHost's running version and
  health, and the changed feature on the device. For lifecycle changes, also
  verify that Core and an unaffected module stay available. If the update rolls
  back, verify that the previous version actually serves requests before
  recording the result.
- For a bundled module with a separately deployed execution assembly, update
  only its artifacts and restart Core when required by its loading model.
  Redeploy the affected Server or Helper components when the test host lacks
  the required lifecycle support or the change affects Core registration,
  shared contracts, Helper behavior, or the deployment itself. Record why a
  full Server deployment was needed when one is required.

See [ModuleHost online lifecycle](docs/architecture/RelaxKonOS.ServerModuleDistribution.Design.md#modulehost-在线生命周期)
and [module package build and management](docs/architecture/RelaxKonOS.ServerModulePackage.Format.md).

When working on authorized Ubuntu or WebDAV acceptance, look for local connection
details in `.codex-scratch/remote-acceptance/connections.local.md`. Git ignores
this file. Keep its credentials out of commits, logs, and replies.
