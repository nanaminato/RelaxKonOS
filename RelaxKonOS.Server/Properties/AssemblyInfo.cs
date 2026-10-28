using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RelaxKonOS.ServerModules.Tests")]
[assembly: InternalsVisibleTo("RelaxKonOS.Server.Tests")]
// The managed-runtime acceptance harness runs the real profile merge that produces a Mihomo
// configuration, so its native check is evidence about the generator rather than about a fixture.
[assembly: InternalsVisibleTo("RelaxKonOS.ManagedRuntimes.Tests")]
[assembly: InternalsVisibleTo("RelaxKonOS.Tailscale.Tests")]
