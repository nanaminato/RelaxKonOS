using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.WebServer;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Installations;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// Nginx is installed differently per platform and is not a ledgered runtime, so this adapter never
/// reuses the archive lifecycle. On Linux it freezes the distribution package the host's own package
/// manager resolves and can install exactly that identity through the Web Server application's own
/// managed-install flow. On Windows the declared mode is a managed archive, which this build does not
/// implement, and it says so instead of offering the Linux path.
/// </summary>
internal sealed class NginxComponentAdapter(IPrivilegedNginxOperations nginx, NginxWebServerManager manager,
    IPrivilegedOperationTransport helper, IRuntimeDownloadProxyProvider downloadProxy) : IComponentLifecycleAdapter
{
    private const string Linux = "linux";
    private const string Windows = "windows";

    public string ComponentId => ComponentIds.Nginx;

    public IReadOnlyList<ComponentModeSupport> Modes => DeclaredModes;

    /// <summary>What this build can do for nginx on this host, decided without touching the host.</summary>
    internal static IReadOnlyList<ComponentModeSupport> DeclaredModes { get; } = OperatingSystem.IsWindows()
        ? [new(ComponentSourceMode.ManagedArchive, Windows, true, null)]
        : [new(ComponentSourceMode.SystemPackage, Linux, true, null, "apt")];

    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.NginxArchiveQuery), ct);
            if (!result.Success || result.ComponentArchive is not { } archive)
                return new(ComponentId, null, Modes, [ComponentProblemCodes.StateUnavailable]);
            var observed = MapArchive(archive, Environment.MachineName,
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
            if (!archive.Installed && (await manager.ListIntegrationCandidatesAsync(ct)).Count != 0)
                observed = observed with { Ownership = RuntimeOwnership.External };
            var revision = ComponentRevision.Of(archive.Releases.Select(pin => pin.ReleaseId + "|" + pin.SourceUri).ToArray());
            return new(ComponentId, observed, Modes, [], archive.Releases, revision, archive.CommittedOperationId);
        }
        if (!OperatingSystem.IsLinux())
            return new(ComponentId, null, [], [ComponentProblemCodes.PlatformUnsupported]);
        try
        {
            var resolution = await ResolveAsync(ct);
            var state = new HostPackageComponentAdapter(ComponentId, helper).Map(resolution);
            return new(ComponentId, state, Modes, [], CommittedOperationId: resolution.CommittedOperationId,
                PendingOperationId: resolution.PendingOperationId);
        }
        catch (ComponentLifecycleException ex) { return new(ComponentId, null, Modes, [ex.ProblemCode]); }
        catch (RuntimeLifecycleException) { return new(ComponentId, null, Modes, [ComponentProblemCodes.StateUnavailable]); }
    }

    /// <summary>
    /// A package change is frozen against the host's current resolution. Only a fresh install can be
    /// carried out; update, repair, rollback and removal are refused by name, because a package
    /// transaction has no supported undo and this build has no verified transaction for them.
    /// </summary>
    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (!string.Equals(request.ComponentId, ComponentId, StringComparison.Ordinal))
            throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        if (OperatingSystem.IsWindows())
        {
            var view = await StateAsync(ct);
            if (view.State is not { } windowsState) return new(null, null, null, [ComponentProblemCodes.StateUnavailable], []);
            var problems = new List<string>();
            if (request.HostId != windowsState.HostId || string.IsNullOrWhiteSpace(request.IdempotencyKey)) problems.Add(ComponentProblemCodes.RequestInvalid);
            if (request.Mode != ComponentSourceMode.ManagedArchive) problems.Add(ComponentProblemCodes.SourceModeUnsupported);
            if (request.Change != RuntimeChange.Install) problems.Add(ComponentProblemCodes.ExecutionUnavailable);
            if (windowsState.Ownership != RuntimeOwnership.Absent) problems.Add(ComponentProblemCodes.PackageAlreadyInstalled);
            if (windowsState.RecoveryRequired) problems.Add(ComponentProblemCodes.RecoveryRequired);
            if (request.ExpectedRevision != windowsState.Revision || request.ExpectedSourceRevision != view.PinRevision)
                problems.Add(ComponentProblemCodes.StateRevisionConflict);
            if (request.ExpectedTrustRevision != 0 || !request.KeepData
                || request.ReleaseId is not null && request.SourceId != "host-pins") problems.Add(ComponentProblemCodes.RequestInvalid);
            var pin = view.PinnedArchives?.SingleOrDefault(pin => pin.ReleaseId == request.ReleaseId && pin.Version == request.RequestedVersion);
            if (pin is null) problems.Add(ComponentProblemCodes.PackageUnresolved);
            return new(request, windowsState, pin is null ? null : ComponentFrozenTarget.FromPin(pin), problems, []);
        }
        if (!OperatingSystem.IsLinux())
            return new(null, null, null, [OperatingSystem.IsWindows()
                ? ComponentProblemCodes.ExecutionUnavailable : ComponentProblemCodes.PlatformUnsupported], []);
        if (request.Mode != ComponentSourceMode.SystemPackage)
            return new(null, null, null, [ComponentProblemCodes.SourceModeUnsupported], []);
        if (request.Change == RuntimeChange.Configure || string.IsNullOrWhiteSpace(request.IdempotencyKey)
            || request.IdempotencyKey.Length > 128
            || request.SourceId is { } sourceId && !RuntimeValidation.Identifier(sourceId))
            return new(null, null, null, [ComponentProblemCodes.RequestInvalid], []);

        var state = await ReadStateAsync(ct);
        if (!string.IsNullOrWhiteSpace(request.HostId) && !string.Equals(request.HostId, state.HostId, StringComparison.Ordinal))
            return new(null, state, null, [ComponentProblemCodes.RequestInvalid], []);
        return NginxPackagePlan.Evaluate(request, state, await ResolveAsync(ct), executionAvailable: true);
    }

    /// <summary>
    /// Installs the identity that was frozen and shown. The host is measured again first: a revision,
    /// candidate, repository or digest that moved since the plan refuses the whole call, and the
    /// Helper re-resolves independently before it touches the package manager.
    /// </summary>
    public async Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var progress = InstallationExecutionContext.Progress.Value;
            if (progress is null || request.ComponentId != ComponentId || request.Expected?.Pin is not { } windowsExpected)
                return Failed(ComponentProblemCodes.ExecutionNotRequested, null);
            var plan = await PlanAsync(request.Request, ct);
            if (!plan.Allowed || plan.Target?.Pin is not { } current || current.ReleaseId != windowsExpected.ReleaseId
                || current.Sha256 != windowsExpected.Sha256 || current.Version != windowsExpected.Version || current.SourceUri != windowsExpected.SourceUri
                || current.Rid != windowsExpected.Rid || current.Format != windowsExpected.Format || current.MaximumBytes != windowsExpected.MaximumBytes
                || !current.AffectedComponents.SequenceEqual(windowsExpected.AffectedComponents))
                return Failed(ComponentProblemCodes.PackageExpectationChanged, null);
            await progress.ReportAsync(new(RelaxKonOS.Protocol.Installations.InstallationStage.Installing, Cancellable: false), ct);
            var installed = await helper.ExecuteAsync(new(PrivilegedOperationKind.NginxArchiveInstall,
                ArchiveExpectation: windowsExpected, OperationId: progress.OperationId, DownloadProxy: await downloadProxy.ResolveAsync(ct)), CancellationToken.None);
            var observed = await helper.ExecuteAsync(new(PrivilegedOperationKind.NginxArchiveQuery), CancellationToken.None);
            if (!installed.Success || observed.ComponentArchive is not { Managed: true, RecoveryRequired: false } windowsResult
                || windowsResult.CommittedOperationId != progress.OperationId || windowsResult.Version != windowsExpected.Version)
                return Failed(ComponentProblemCodes.InstallFailed, null);
            return new(ComponentId, ComponentExecutionOutcome.Succeeded, [], [],
                MapArchive(windowsResult, Environment.MachineName, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()), windowsResult.Version);
        }
        if (OperatingSystem.IsLinux() && request.Request.Change is RuntimeChange.Update or RuntimeChange.Uninstall)
            return await new HostPackageComponentAdapter(ComponentId, helper).ExecuteAsync(request, ct);
        if (NginxPackagePlan.PrecheckExecute(request, OperatingSystem.IsLinux()) is { } refused) return refused;
        var expected = request.Expected!.Package!;
        var state = await ReadStateAsync(ct);
        if (NginxPackagePlan.PrecheckFrozen(request, state, await ResolveAsync(ct)) is { } moved) return moved;

        WebServerOperationResult result;
        try { result = await manager.InstallComponentPackageAsync(expected, ct); }
        catch (RuntimeLifecycleException) { return Failed(ComponentProblemCodes.InstallFailed, null); }
        catch (UnauthorizedAccessException) { return Failed(ComponentProblemCodes.InstallFailed, "webserver.install_elevation_required"); }
        catch (IOException) { return Failed(ComponentProblemCodes.InstallFailed, "webserver.install_layout_invalid"); }

        var after = await ReadStateAsync(ct);
        if (result.ProblemCode.Length > 0)
            return new(ComponentId, ComponentExecutionOutcome.Failed, [ComponentProblemCodes.InstallFailed],
                [ComponentProblemCodes.RollbackUnsupported], after, after.ActiveVersion, result.ProblemCode);
        // The Helper already compared the installed version; this is the Server's own independent look.
        if (!string.Equals(after.ActiveVersion, expected.Version, StringComparison.Ordinal))
            return new(ComponentId, ComponentExecutionOutcome.Failed, [ComponentProblemCodes.InstalledVersionMismatch],
                [ComponentProblemCodes.RollbackUnsupported], after, after.ActiveVersion, null);
        return new(ComponentId, ComponentExecutionOutcome.Succeeded, [], [ComponentProblemCodes.RollbackUnsupported],
            after, after.ActiveVersion);
    }

    internal static ComponentState MapArchive(ComponentArchiveHostSnapshot archive, string hostId, string architecture) =>
        new(ComponentIds.Nginx, hostId, ComponentRevision.Of(archive.Installed.ToString(), archive.Managed.ToString(),
            archive.Version, archive.CommittedOperationId?.ToString(), archive.RecoveryRequired.ToString()),
            archive.Managed ? RuntimeOwnership.Managed : archive.Installed ? RuntimeOwnership.External : RuntimeOwnership.Absent,
            ComponentSourceMode.ManagedArchive, new("windows", architecture, null, null, RuntimeInstallMode.Archive, "scm"),
            archive.Version, archive.RecoveryRequired);

    private ComponentExecutionResult Failed(string problem, string? detail) =>
        new(ComponentId, ComponentExecutionOutcome.Failed, [problem], [ComponentProblemCodes.RollbackUnsupported],
            null, null, detail);

    private async Task<ComponentState> ReadStateAsync(CancellationToken ct)
    {
        var resolution = await ResolveAsync(ct);
        return new HostPackageComponentAdapter(ComponentId, helper).Map(resolution);
    }

    private async Task<ComponentPackageResolution> ResolveAsync(CancellationToken ct)
    {
        var result = await nginx.QueryPackageAsync(ct);
        if (!result.Success && result.ProblemCode == PrivilegedProblemCode.UnsupportedOperation)
            throw new ComponentLifecycleException(ComponentProblemCodes.PlatformUnsupported);
        if (!result.Success || result.ComponentPackage is not { } resolution)
            throw new ComponentLifecycleException(ComponentProblemCodes.PackageIndexUnavailable);
        if (!string.Equals(resolution.PackageName, AptPackageResolution.PackageName, StringComparison.Ordinal))
            throw new ComponentLifecycleException(ComponentProblemCodes.PackageUnresolved);
        return resolution;
    }
}
