using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// Observes and plans through the mihomo publication-aware domain service. Execution stays with
/// its installation/removal/configuration coordinators, which own publication and credentials.
/// </summary>
public sealed class MihomoComponentAdapter(MihomoRuntimeService runtime, RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager native)
    : ArchiveComponentAdapter(RelaxKonOS.Protocol.ManagedRuntimes.ComponentIds.Mihomo,
        runtime.QueryAsync, runtime.PlanAsync)
{
    public override async Task<RelaxKonOS.Protocol.ManagedRuntimes.ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return await base.StateAsync(ct);
        var status = await native.GetAsync("mihomo", ct);
        var ownership = status.IntegrityVerified ? RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Managed
            : status.State == RelaxKonOS.Protocol.Proxy.ProxyRuntimeState.NotInstalled ? RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Absent
            : RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Unknown;
        return new(ComponentId, new(ComponentId, Environment.MachineName, ComponentRevision.Of(status.Version, status.State.ToString()), ownership,
            RelaxKonOS.Protocol.ManagedRuntimes.ComponentSourceMode.ManagedArchive,
            new("windows", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), null, null,
                RelaxKonOS.Protocol.ManagedRuntimes.RuntimeInstallMode.Archive, "scm"), status.Version, false), Modes, []);
    }
}
