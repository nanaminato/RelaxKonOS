using System.Text.Json;
using RelaxKonOS.Protocol.Databases;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Components;

/// <summary>Thin Core transport only. Database processes and durable native ownership remain in Helper.</summary>
public sealed class PostgreSqlComponentAdapter(IPrivilegedOperationTransport helper) : IComponentLifecycleAdapter
{
    public string ComponentId => ComponentIds.PostgreSql;
    public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.SystemPackage, "linux", OperatingSystem.IsLinux(),
        OperatingSystem.IsLinux() ? null : ComponentProblemCodes.PlatformUnsupported, "apt")];

    public async Task<PostgreSqlState> ReadAsync(CancellationToken ct, Guid? operation = null, Guid? instance = null)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.PostgreSqlQuery, OperationId: operation, PostgreSqlInstance: instance), ct);
        return Decode(result);
    }
    internal static PostgreSqlState Decode(PrivilegedOperationResult result)
    {
        if (!result.Success || result.OutputBase64 is null) throw new ComponentLifecycleException(result.Error is { } code && code.StartsWith("database.", StringComparison.Ordinal)
            ? code : "database.state_unavailable");
        try { return JsonSerializer.Deserialize<PostgreSqlState>(Convert.FromBase64String(result.OutputBase64))
            ?? throw new ComponentLifecycleException("database.state_unavailable"); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw new ComponentLifecycleException("database.state_unavailable"); }
    }
    private ComponentState Map(PostgreSqlState state) => new(ComponentId, Environment.MachineName, state.Revision,
        state.Managed ? RuntimeOwnership.Managed : state.Installed || state.ProblemCode == "database.external_installation" ? RuntimeOwnership.External : RuntimeOwnership.Absent,
        ComponentSourceMode.SystemPackage, new("linux", "amd64", "noble", null, RuntimeInstallMode.SystemPackage, "systemd"),
        state.Version, state.PendingOperationId is not null || state.InstanceId is not null && state.ProblemCode is not null);
    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        return new(ComponentId, state.Supported ? Map(state) : null,
            [new(ComponentSourceMode.SystemPackage, "linux", state.Supported, state.Supported ? null : ComponentProblemCodes.PlatformUnsupported, "apt")],
            state.ProblemCode is null ? [] : [state.ProblemCode], PendingOperationId: state.PendingOperationId);
    }
    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        var state = await ReadAsync(ct);
        var problems = new List<string>();
        if (!state.Supported) problems.Add(ComponentProblemCodes.PlatformUnsupported);
        if (state.ProblemCode is { } problem) problems.Add(problem);
        if (state.Installed || state.PendingOperationId is not null) problems.Add("database.installation_exists");
        if (!Valid(request) || request.HostId != Environment.MachineName || request.ExpectedRevision != state.Revision)
            problems.Add(ComponentProblemCodes.RequestInvalid);
        if (state.Candidate is null) problems.Add(ComponentProblemCodes.PackageUnresolved);
        if (request.RequestedVersion is { Length: > 0 } version && version != state.Candidate?.Version)
            problems.Add(ComponentProblemCodes.PackageVersionMismatch);
        return new(request, Map(state), state.Candidate is null ? null : ComponentFrozenTarget.FromPackage(state.Candidate), problems,
            ["database.package_install_only", "database.package_system_dependencies", "database.rollback_unsupported"]);
    }
    public static bool Valid(ComponentPlanRequest request) => request.ComponentId == ComponentIds.PostgreSql
        && request.Change == RuntimeChange.Install && request.Mode == ComponentSourceMode.SystemPackage && request.KeepData
        && request.SourceId is null && request.ReleaseId is null && request.ExpectedSourceRevision == 0 && request.ExpectedTrustRevision == 0
        && request.IdempotencyKey is { Length: > 0 and <= 128 };
    public async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct)
    {
        if (InstallationExecutionContext.Progress.Value is not { } progress) throw new ComponentLifecycleException(ComponentProblemCodes.ExecutionNotRequested);
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.PostgreSqlExecute, OperationId: progress.OperationId,
            PostgreSql: new(true, request)), CancellationToken.None);
        var state = Decode(result);
        return new(ComponentId, state.CommittedOperationId == progress.OperationId ? ComponentExecutionOutcome.Succeeded : ComponentExecutionOutcome.Failed,
            state.CommittedOperationId == progress.OperationId ? [] : ["database.recovery_required"], [], Map(state), state.Version);
    }
}
