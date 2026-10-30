using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Components;

public sealed class TailscaleComponentAdapter(IPrivilegedOperationTransport helper, IRuntimeDownloadProxyProvider proxy) : IComponentLifecycleAdapter
{
    private readonly HostPackageComponentAdapter linux = new(ComponentIds.Tailscale, helper);
    public string ComponentId => ComponentIds.Tailscale;
    public IReadOnlyList<ComponentModeSupport> Modes => OperatingSystem.IsWindows()
        ? [new(ComponentSourceMode.NativeInstaller, "windows", true, null, "msi")] : linux.Modes;

    public async Task<TailscaleInstallerSnapshot> ReadInstallerAsync(Guid? operation, CancellationToken ct)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.TailscaleInstallerQuery, OperationId: operation ?? Guid.NewGuid()), ct);
        return result.Success && result.TailscaleInstallerSnapshot is { } snapshot ? snapshot
            : throw new ComponentLifecycleException(ComponentProblemCodes.StateUnavailable);
    }
    public async Task<ComponentStateView> StateAsync(CancellationToken ct) => OperatingSystem.IsWindows()
        ? (await ReadInstallerAsync(null, ct)).View : await linux.StateAsync(ct);

    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return await linux.PlanAsync(request, ct);
        var snapshot = await ReadInstallerAsync(null, ct); var state = snapshot.View.State;
        if (state is null) return new(null, null, null, snapshot.View.Problems, []);
        var problems = new List<string>();
        if (request.ComponentId != ComponentId || request.HostId != state.HostId || request.Mode != ComponentSourceMode.NativeInstaller
            || request.Change is not (RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Uninstall)
            || !request.KeepData || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
            || request.SourceId is not null || request.ReleaseId is not null || request.ExpectedSourceRevision != 0 || request.ExpectedTrustRevision != 0)
            problems.Add(ComponentProblemCodes.RequestInvalid);
        if (request.ExpectedRevision != state.Revision) problems.Add(ComponentProblemCodes.StateRevisionConflict);
        if (state.RecoveryRequired) problems.Add(ComponentProblemCodes.RecoveryRequired);
        if (request.Change == RuntimeChange.Install && state.Ownership != RuntimeOwnership.Absent)
            problems.Add(ComponentProblemCodes.PackageAlreadyInstalled);
        if (request.Change != RuntimeChange.Install && state.Ownership != RuntimeOwnership.Managed)
            problems.Add(ComponentProblemCodes.PackageOwnershipRequired);
        if (problems.Count != 0) return new(request, state, null, problems, []);
        var release = request.Change == RuntimeChange.Uninstall ? snapshot.InstalledRelease
            : await new TailscaleOfficialReleases().ResolveAsync("win-" + (state.Target.Architecture == "arm64" ? "arm64" : "x64"),
                request.RequestedVersion, ct, await proxy.ResolveAsync(ct));
        if (release is null) return new(request, state, null, [ComponentProblemCodes.PackageUnresolved], []);
        if (request.Change == RuntimeChange.Update && (!Version.TryParse(release.Version, out var next)
            || !Version.TryParse(state.ActiveVersion, out var current) || next <= current))
            return new(request, state, null, [ComponentProblemCodes.PackageNoUpdate], []);
        return TailscaleInstallerPlan.Evaluate(request, state, release);
    }
    public async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return await linux.ExecuteAsync(request, ct);
        var progress = InstallationExecutionContext.Progress.Value;
        if (progress is null) return new(ComponentId, ComponentExecutionOutcome.Refused, [ComponentProblemCodes.ExecutionNotRequested], [], null, null);
        var plan = await PlanAsync(request.Request, ct);
        if (!plan.Allowed || request.Expected?.Installer != plan.Target?.Installer)
            return new(ComponentId, ComponentExecutionOutcome.Refused, [ComponentProblemCodes.PackageExpectationChanged], [], plan.State, null);
        await progress.ReportAsync(new(RelaxKonOS.Protocol.Installations.InstallationStage.Installing, Cancellable: false), ct);
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.TailscaleInstallerExecute, OperationId: progress.OperationId,
            TailscaleInstaller: request, DownloadProxy: await proxy.ResolveAsync(ct)), CancellationToken.None);
        var snapshot = result.TailscaleInstallerSnapshot;
        return result.Success && snapshot?.VerifiedOperationId == progress.OperationId
            ? new(ComponentId, ComponentExecutionOutcome.Succeeded, [], [], snapshot.View.State, snapshot.View.State?.ActiveVersion)
            : new(ComponentId, ComponentExecutionOutcome.Failed, [ComponentProblemCodes.RecoveryRequired], [], snapshot?.View.State, null);
    }
}
