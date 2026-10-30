using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// Observes and plans through the mihomo publication-aware domain service. Execution stays with
/// its installation/removal/configuration coordinators, which own publication and credentials.
/// </summary>
public sealed class MihomoComponentAdapter(MihomoRuntimeService runtime, RelaxKonOS.Server.Proxy.Mihomo.MihomoRuntimeManager native, RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport helper)
    : ArchiveComponentAdapter(RelaxKonOS.Protocol.ManagedRuntimes.ComponentIds.Mihomo,
        runtime.QueryAsync, runtime.PlanAsync)
{
    private readonly HostPackageComponentAdapter packages = new("mihomo", helper);
    public override IReadOnlyList<RelaxKonOS.Protocol.ManagedRuntimes.ComponentModeSupport> Modes => OperatingSystem.IsLinux() ? packages.Modes : base.Modes;
    public override async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return await base.PlanAsync(request, ct);
        var view = await StateAsync(ct);
        return view.Problems.Count > 0 ? new(request, view.State, null, view.Problems, []) : await packages.PlanAsync(request, ct);
    }
    public override async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return await base.ExecuteAsync(request, ct);
        var plan = await PlanAsync(request.Request, ct);
        return plan.Allowed ? await packages.ExecuteAsync(request, ct)
            : new(ComponentId, ComponentExecutionOutcome.Refused, plan.Problems, [], plan.State, null);
    }
    public override async Task<RelaxKonOS.Protocol.ManagedRuntimes.ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsLinux())
        {
            var archive = await base.StateAsync(ct);
            if (archive.State is { Ownership: RelaxKonOS.Protocol.ManagedRuntimes.RuntimeOwnership.Managed } || archive.State?.RecoveryRequired == true)
                return archive with { Problems = [RelaxKonOS.Protocol.ManagedRuntimes.ComponentProblemCodes.ExecutionUnavailable] };
            return await packages.StateAsync(ct);
        }
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
