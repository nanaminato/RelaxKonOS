using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.ManagedRuntimes;
namespace RelaxKonOS.Server.Components;

/// <summary>Linux component-center packages use the host's configured APT sources.</summary>
public sealed class SingBoxComponentAdapter(SingBoxRuntimeService runtime, IPrivilegedOperationTransport helper)
    : ArchiveComponentAdapter(ComponentIds.SingBox, ct => runtime.QueryAsync(ct), runtime.PlanAsync)
{
    private readonly HostPackageComponentAdapter packages = new(ComponentIds.SingBox, helper);
    public override IReadOnlyList<ComponentModeSupport> Modes => OperatingSystem.IsLinux() ? packages.Modes : base.Modes;
    public override async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return await base.StateAsync(ct);
        var archive = await base.StateAsync(ct);
        if (archive.State is { Ownership: RuntimeOwnership.Managed } || archive.State?.RecoveryRequired == true)
            return archive with { Problems = [ComponentProblemCodes.ExecutionUnavailable] };
        return await packages.StateAsync(ct);
    }
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
}
