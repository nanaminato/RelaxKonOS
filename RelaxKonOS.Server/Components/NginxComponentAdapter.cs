using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// Nginx is installed differently per platform and is not a ledgered runtime, so this adapter never
/// reuses the archive lifecycle. On Linux it freezes the distribution package the host's own package
/// manager resolves and can install exactly that identity through the Web Server application's own
/// managed-install flow. On Windows the declared mode is a managed archive, which this build does not
/// implement, and it says so instead of offering the Linux path.
/// </summary>
internal sealed class NginxComponentAdapter(IPrivilegedNginxOperations nginx, NginxWebServerManager manager,
    NginxManagedOptions options) : IComponentLifecycleAdapter
{
    private const string Linux = "linux";
    private const string Windows = "windows";

    public string ComponentId => ComponentIds.Nginx;

    public IReadOnlyList<ComponentModeSupport> Modes => DeclaredModes;

    /// <summary>What this build can do for nginx on this host, decided without touching the host.</summary>
    internal static IReadOnlyList<ComponentModeSupport> DeclaredModes { get; } = OperatingSystem.IsWindows()
        ? [new(ComponentSourceMode.ManagedArchive, Windows, false, ComponentProblemCodes.ExecutionUnavailable)]
        : [new(ComponentSourceMode.SystemPackage, Linux, true, null)];

    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return new(ComponentId, null, Modes, [ComponentProblemCodes.PlatformUnsupported]);
        try
        {
            return new(ComponentId, await ReadStateAsync(ct), Modes, []);
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
        if (!OperatingSystem.IsLinux())
            return new(null, null, null, [ComponentProblemCodes.PlatformUnsupported], []);
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

    private ComponentExecutionResult Failed(string problem, string? detail) =>
        new(ComponentId, ComponentExecutionOutcome.Failed, [problem], [ComponentProblemCodes.RollbackUnsupported],
            null, null, detail);

    private async Task<ComponentState> ReadStateAsync(CancellationToken ct) =>
        NginxPackagePlan.BuildState(await ResolveAsync(ct), NginxManagedPaths.IsManaged(options), Environment.MachineName,
            NginxPackagePlan.HostArchitecture());

    private async Task<ComponentPackageResolution> ResolveAsync(CancellationToken ct)
    {
        var result = await nginx.QueryPackageAsync(ct);
        if (!result.Success || result.ComponentPackage is not { } resolution)
            throw new ComponentLifecycleException(ComponentProblemCodes.PackageIndexUnavailable);
        if (!string.Equals(resolution.PackageName, AptPackageResolution.PackageName, StringComparison.Ordinal))
            throw new ComponentLifecycleException(ComponentProblemCodes.PackageUnresolved);
        return resolution;
    }
}
