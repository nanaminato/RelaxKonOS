using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// The ledgered archive runtime, exposed through the component-neutral shape. It adds no capability:
/// the plan still comes from the same domain service and the Helper still revalidates it.
/// </summary>
public sealed class SingBoxComponentAdapter(SingBoxRuntimeService runtime) : IComponentLifecycleAdapter
{
    public string ComponentId => ComponentIds.SingBox;

    public IReadOnlyList<ComponentModeSupport> Modes { get; } =
        [new(ComponentSourceMode.ManagedArchive, "linux", true, null), new(ComponentSourceMode.ManagedArchive, "windows", true, null)];

    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        try { return new(ComponentId, Map(await runtime.QueryAsync(ct)), Modes, []); }
        catch (RuntimeLifecycleException) { return new(ComponentId, null, Modes, [ComponentProblemCodes.StateUnavailable]); }
    }

    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.ComponentId, ComponentId, StringComparison.Ordinal))
            throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        if (request.Mode != ComponentSourceMode.ManagedArchive)
            return new(null, null, null, [ComponentProblemCodes.SourceModeUnsupported], []);
        if (request.Change == RuntimeChange.Configure || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 128)
            return new(null, null, null, [ComponentProblemCodes.RequestInvalid], []);

        var snapshot = await runtime.QueryAsync(ct);
        var state = Map(snapshot);
        if (!string.IsNullOrWhiteSpace(request.HostId) && !string.Equals(request.HostId, state.HostId, StringComparison.Ordinal))
            return new(null, state, null, [ComponentProblemCodes.RequestInvalid], []);
        if (request.ExpectedRevision != state.Revision)
            return new(null, state, null, [ComponentProblemCodes.StateRevisionConflict], []);
        var removal = request.Change == RuntimeChange.Uninstall;
        if (removal ? request.ReleaseId is not null : string.IsNullOrWhiteSpace(request.ReleaseId))
            return new(null, state, null, [ComponentProblemCodes.RequestInvalid], []);
        if (!removal && !RuntimeValidation.Identifier(request.SourceId))
            return new(null, state, null, [ComponentProblemCodes.RequestInvalid], []);

        var plan = await runtime.PlanAsync(request.SourceId, new RuntimePlanRequest(snapshot.Installation.InstallationId,
            snapshot.Installation.HostId, request.Change, request.ExpectedRevision, request.ExpectedSourceRevision,
            request.ExpectedTrustRevision, request.ReleaseId, request.IdempotencyKey, KeepData: request.KeepData), ct);
        return new(request, state, plan.Plan?.Release is { } release ? ComponentFrozenTarget.FromArchive(release) : null,
            [.. plan.Problems], []);
    }

    /// <summary>
    /// A ledgered runtime executes through the task plan (approval, journal, recovery), never through
    /// this route. Saying so is better than accepting a request this adapter cannot honour.
    /// </summary>
    public Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct) =>
        Task.FromResult(new ComponentExecutionResult(ComponentId, ComponentExecutionOutcome.Refused,
            [ComponentProblemCodes.ExecutionUnavailable], [], null, null));

    private ComponentState Map(RuntimeInstallationSnapshot snapshot) => new(ComponentId, snapshot.Installation.HostId,
        snapshot.Installation.StateRevision, snapshot.Installation.Ownership, ComponentSourceMode.ManagedArchive,
        snapshot.Installation.Target, snapshot.MeasuredVersion, snapshot.Installation.RecoveryRequired);
}
