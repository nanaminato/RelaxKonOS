using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Components;

/// <summary>Closed system-package adapter. It never accepts package names, native arguments or executable paths.</summary>
public sealed class HostPackageComponentAdapter(string componentId, IPrivilegedOperationTransport helper) : IComponentLifecycleAdapter
{
    private readonly HostPackageDefinition definition = HostPackageDefinitions.Find(componentId)
        ?? throw new ArgumentException("Unsupported host package", nameof(componentId));
    public string ComponentId => definition.ComponentId;
    public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.SystemPackage, "linux", OperatingSystem.IsLinux(),
        OperatingSystem.IsLinux() ? null : ComponentProblemCodes.PlatformUnsupported, "apt")];
    public Task<ComponentPackageResolution> ResolveAsync(CancellationToken ct) => ResolveAsync(null, ct);

    private async Task<ComponentPackageResolution> ResolveAsync(Guid? operationId, CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.ComponentPackageQuery, ComponentId: ComponentId,
            OperationId: operationId), ct);
        if (!result.Success && result.ProblemCode == PrivilegedProblemCode.UnsupportedOperation)
            throw new ComponentLifecycleException(ComponentProblemCodes.PlatformUnsupported);
        if (!result.Success || result.ComponentPackage is not { } resolution || resolution.PackageName != definition.PackageName)
            throw new ComponentLifecycleException(ComponentProblemCodes.PackageIndexUnavailable);
        return resolution;
    }
    public ComponentState Map(ComponentPackageResolution resolution) => new(ComponentId, Environment.MachineName,
        ComponentRevision.Of(resolution.InstalledVersion, resolution.Candidate?.Version, resolution.Candidate?.Sha256,
            resolution.InstalledPackage?.Architecture, resolution.InstalledPackage?.ExecutableSha256, resolution.Managed.ToString(), resolution.RecoveryRequired.ToString(), resolution.CommittedOperationId?.ToString()),
        resolution.Managed ? RuntimeOwnership.Managed : resolution.InstalledVersion is null ? RuntimeOwnership.Absent : RuntimeOwnership.External,
        ComponentSourceMode.SystemPackage, new("linux", NginxPackagePlan.HostArchitecture() ?? "unknown", null, null,
            RuntimeInstallMode.SystemPackage, definition.Service.Length == 0 ? "none" : "systemd"), resolution.InstalledVersion, resolution.RecoveryRequired);
    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return new(ComponentId, null, Modes, [ComponentProblemCodes.PlatformUnsupported]);
        try
        {
            var resolution = await ResolveAsync(ct);
            return new(ComponentId, Map(resolution), Modes, StateProblems(resolution), CommittedOperationId: resolution.CommittedOperationId,
                PendingOperationId: resolution.PendingOperationId, InstalledPackage: resolution.InstalledPackage,
                ExternalPackageChange: resolution.ExternalPackageChange, RecordedPackageVersion: resolution.RecordedPackageVersion);
        }
        catch (ComponentLifecycleException ex) { return new(ComponentId, null, Modes, [ex.ProblemCode]); }
    }

    /// <summary>
    /// Problems that explain an action the center cannot offer. A version without a verified package
    /// identity hides "纳入管理", update and uninstall by design, so the state says why instead of
    /// leaving those actions silently missing.
    /// </summary>
    public static IReadOnlyList<string> StateProblems(ComponentPackageResolution resolution)
    {
        var problems = new List<string>();
        if (resolution.BlockingProblem is { } blocked) problems.Add(blocked);
        if (resolution.InstalledVersion is not null && resolution.InstalledPackage is null)
            problems.Add(ComponentProblemCodes.PackageIdentityUnverified);
        return problems;
    }
    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (request.ComponentId != ComponentId) throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        if (!OperatingSystem.IsLinux()) return new(null, null, null, [ComponentProblemCodes.PlatformUnsupported], []);
        if (request.Mode != ComponentSourceMode.SystemPackage)
            return new(null, null, null, [ComponentProblemCodes.SourceModeUnsupported], []);
        if (!ValidRequest(request)) return new(null, null, null, [ComponentProblemCodes.RequestInvalid], []);
        var resolution = await ResolveAsync(ct);
        var state = Map(resolution);
        if (request.Change != RuntimeChange.Uninstall && resolution.Candidate is { } candidate
            && !HostPackageDefinitions.SupportsVersion(ComponentId, candidate.Version))
            return new(request, state, ComponentFrozenTarget.FromPackage(candidate), [ComponentProblemCodes.PackageVersionMismatch], []);
        if (request.Change != RuntimeChange.Uninstall && resolution.BlockingProblem is { } blocked) return new(request, state, null, [blocked], []);
        if (request.HostId != state.HostId) return new(null, state, null, [ComponentProblemCodes.RequestInvalid], []);
        return NginxPackagePlan.Evaluate(request, state, resolution, true);
    }
    public static bool ValidRequest(ComponentPlanRequest request) => request.Mode == ComponentSourceMode.SystemPackage
        && request.Change is RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Uninstall && request.SourceId is null && request.ReleaseId is null
        && request.ExpectedSourceRevision == 0 && request.ExpectedTrustRevision == 0 && request.KeepData
        && !string.IsNullOrWhiteSpace(request.IdempotencyKey) && request.IdempotencyKey.Length <= 128;
    public async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct)
    {
        if (request.ComponentId != ComponentId || request.Request.ComponentId != ComponentId)
            throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        var progress = InstallationExecutionContext.Progress.Value;
        if (progress is null || !ValidRequest(request.Request) || request.Expected is not { Mode: ComponentSourceMode.SystemPackage, Archive: null })
            return Refused(ComponentProblemCodes.ExecutionNotRequested);
        var plan = await PlanAsync(request.Request, ct);
        if (!plan.Allowed || request.Expected.InstalledPackage != plan.Target?.InstalledPackage
            || request.Request.Change != RuntimeChange.Uninstall && !AptPackageResolution.MatchesExpectation(request.Expected.Package, plan.Target?.Package))
            return Refused(plan.Problems.FirstOrDefault() ?? ComponentProblemCodes.PackageExpectationChanged);
        await progress.ReportAsync(new(RelaxKonOS.Protocol.Installations.InstallationStage.Installing, Cancellable: false), ct);
        var prior = await ResolveAsync(ct);
        var installed = await helper.ExecuteAsync(request.Request.Change == RuntimeChange.Install
            ? new(PrivilegedOperationKind.ComponentPackageInstall, ComponentId: ComponentId, PackageExpectation: request.Expected.Package, OperationId: progress.OperationId)
            : new(PrivilegedOperationKind.ComponentPackageChange, ComponentId: ComponentId, PackageExpectation: request.Expected.Package,
                PackageChange: new(request.Request.Change, request.Expected.InstalledPackage!), OperationId: progress.OperationId), CancellationToken.None);
        var replyUnconfirmed = !installed.Success && installed.ProblemCode == PrivilegedProblemCode.RecoveryRequired;
        if (!installed.Success && !replyUnconfirmed) return new(ComponentId, ComponentExecutionOutcome.Failed, [ComponentProblemCodes.InstallFailed],
            [ComponentProblemCodes.RollbackUnsupported], null, null, ComponentProblemCodes.InstallFailed);
        ComponentPackageResolution after;
        try
        {
            after = await ResolveAsync(progress.OperationId, CancellationToken.None);
        }
        catch (ComponentLifecycleException) when (replyUnconfirmed)
        {
            throw new InstallationInterruptedException(ComponentProblemCodes.RecoveryRequired);
        }
        if (after.RecoveryRequired || after.CommittedOperationId != progress.OperationId || (request.Request.Change == RuntimeChange.Uninstall
            ? after.InstalledVersion is not null : after.InstalledVersion != request.Expected.Package!.Version
                || after.InstalledPackage?.Version != after.InstalledVersion
                || after.Managed != prior.Managed && request.Request.Change != RuntimeChange.Install))
        {
            if (replyUnconfirmed)
                throw new InstallationInterruptedException(ComponentProblemCodes.RecoveryRequired);
            return Refused(ComponentProblemCodes.InstalledVersionMismatch);
        }
        return new(ComponentId, ComponentExecutionOutcome.Succeeded, [], [ComponentProblemCodes.RollbackUnsupported], Map(after), after.InstalledVersion);
    }
    private ComponentExecutionResult Refused(string code) => new(ComponentId, ComponentExecutionOutcome.Refused, [code], [], null, null);
}
