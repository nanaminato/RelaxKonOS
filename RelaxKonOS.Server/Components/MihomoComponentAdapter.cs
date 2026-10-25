using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// Observes and plans through the mihomo publication-aware domain service. Execution stays with
/// its installation/removal/configuration coordinators, which own publication and credentials.
/// </summary>
public sealed class MihomoComponentAdapter(MihomoRuntimeService runtime)
    : ArchiveComponentAdapter(RelaxKonOS.Protocol.ManagedRuntimes.ComponentIds.Mihomo,
        runtime.QueryAsync, runtime.PlanAsync);
