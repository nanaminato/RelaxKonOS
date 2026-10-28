# Tailscale Server/Helper checks

Build: `dotnet build Tests/Tailscale/RelaxKonOS.Tailscale.Tests.csproj -m:1`.
Run: `dotnet run --project Tests/Tailscale/RelaxKonOS.Tailscale.Tests.csproj --no-build`.
Registered suite: `tailscale-server` in `Tools/tests.json`.

Uses temporary files, real SQLite/protected receipts, the production module lease proxy and a loopback HTTP server.
Native Tailscale actions use test replacements. Includes conditional mutations, operation ownership, immutable original
receipts, lost replies, manual recovery preview/closure, quiescence and revision conflicts, and closure evidence validation.

The process-quiescence check creates a hidden test-owned process named `tailscale` by copying an OS utility into its own
temporary directory (Windows cmd waiting on redirected stdin, Linux sleep). It only terminates its own child, then removes
its own directory. It does not invoke a real Tailscale executable, stop tailscaled, or change host network configuration.
The OS probe only establishes CLI name detection on the execution platform, not real daemon or interruption acceptance.

Tests require ordinary loopback sockets and temporary filesystem atomic publication. The Codex sandbox may block these;
run the same harness with those restrictions lifted rather than treating the blocked run as a pass.
