# Tailscale desktop checks

Build with `dotnet build Tests/Client/RelaxKonOS.Tailscale.Client.Tests/RelaxKonOS.Tailscale.Client.Tests.csproj -m:1`.
Run with `dotnet run --project Tests/Client/RelaxKonOS.Tailscale.Client.Tests/RelaxKonOS.Tailscale.Client.Tests.csproj --no-build`.
The registered repository suite is `tailscale-client` in `Tools/tests.json`.

Uses test clients and temporary reference files; does not execute Tailscale or mutate a host network.
Covers confirmation, connection identity changes, lost replies, original-operation reopening, bounded reference persistence,
HTTP authorization and routing, manual closure confirmation, lost closure replies and identity checks, three languages, narrow/wide pages, dark theme and fixed dialog actions.
Rendered PNGs are written to `bin/Debug/net10.0/layout-qa/`.

The process needs ordinary local filesystem atomic rename and native Skia rendering. The Codex sandbox may deny temporary
file publication; running the same harness with that restriction lifted is not real daemon or OS privilege acceptance.
