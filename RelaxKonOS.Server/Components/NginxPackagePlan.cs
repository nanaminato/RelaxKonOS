using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// The Linux package decision, kept free of host access so it can be checked without a Linux host.
/// It describes a frozen candidate; execution availability is supplied by the host adapter.
/// </summary>
public static class NginxPackagePlan
{
    public const string ComponentId = ComponentIds.Nginx;

    public static ComponentState BuildState(ComponentPackageResolution resolution, bool managed, string hostId, string? hostArchitecture)
    {
        var ownership = managed ? RuntimeOwnership.Managed
            : resolution.InstalledVersion is null ? RuntimeOwnership.Absent : RuntimeOwnership.External;
        var target = new RuntimeTarget("linux", hostArchitecture ?? "unknown", resolution.Candidate?.Suite, null,
            RuntimeInstallMode.SystemPackage, "systemd");
        return new(ComponentId, hostId, ComponentRevision.Of(resolution.InstalledVersion, resolution.Candidate?.Version,
            resolution.Candidate?.Sha256, managed ? "managed" : "unmanaged",
                resolution.RecoveryRequired.ToString(), resolution.CommittedOperationId?.ToString()),
            ownership, ComponentSourceMode.SystemPackage, target, resolution.InstalledVersion, RecoveryRequired: resolution.RecoveryRequired);
    }

    /// <summary>
    /// <paramref name="executionAvailable"/> says whether this build can carry out the change. Only a
    /// fresh install of the frozen package can: update, repair, rollback and removal stay in the Web
    /// Server application until they have their own verified transaction.
    /// </summary>
    public static ComponentPlanResult Evaluate(ComponentPlanRequest request, ComponentState state,
        ComponentPackageResolution resolution, bool executionAvailable)
    {
        var problems = new List<string>();
        if (request.ExpectedRevision != state.Revision) problems.Add(ComponentProblemCodes.StateRevisionConflict);
        if (state.RecoveryRequired) problems.Add(ComponentProblemCodes.RecoveryRequired);
        if (request.Change == RuntimeChange.Install && state.Ownership != RuntimeOwnership.Absent)
            problems.Add(ComponentProblemCodes.PackageAlreadyInstalled);
        if (request.Change != RuntimeChange.Install && state.Ownership == RuntimeOwnership.Absent)
            problems.Add(ComponentProblemCodes.PackageAbsent);
        if (request.Change is RuntimeChange.Update or RuntimeChange.Uninstall && !PackageIdentityPolicy.Valid(resolution.InstalledPackage))
            problems.Add(ComponentProblemCodes.PackageIdentityUnverified);
        if (request.Change == RuntimeChange.Uninstall)
        {
            if (!executionAvailable) problems.Add(ComponentProblemCodes.ExecutionUnavailable);
            return new(request, state, new(ComponentSourceMode.SystemPackage, null, null, InstalledPackage: resolution.InstalledPackage), problems, []);
        }
        if (request.Change == RuntimeChange.Update && !resolution.UpdateAvailable)
        {
            problems.Add(resolution.CandidateVersion is null ? ComponentProblemCodes.PackageUnresolved : ComponentProblemCodes.PackageNoUpdate);
            return new(request, state, null, problems, []);
        }
        var candidate = resolution.Candidate;
        if (candidate is null)
        {
            problems.Add(ComponentProblemCodes.PackageUnresolved);
            return new(request, state, null, problems, []);
        }
        if (!string.IsNullOrWhiteSpace(request.RequestedVersion)
            && !string.Equals(request.RequestedVersion, candidate.Version, StringComparison.Ordinal))
            problems.Add(ComponentProblemCodes.PackageVersionMismatch);
        // A first install and a verified update both carry a frozen candidate; removal carries only
        // the installed identity.
        if (!executionAvailable || request.Change is not (RuntimeChange.Install or RuntimeChange.Update))
            problems.Add(ComponentProblemCodes.ExecutionUnavailable);
        return new(request, state, ComponentFrozenTarget.FromPackage(candidate) with { InstalledPackage = resolution.InstalledPackage }, problems, []);
    }

    public static string? HostArchitecture() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "amd64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "i386",
        System.Runtime.InteropServices.Architecture.Arm => "armhf",
        _ => null
    };

    /// <summary>
    /// The guards that need no host access. A null answer means the call may proceed to measure the
    /// host; anything else is the refusal the caller gets.
    /// </summary>
    public static ComponentExecutionResult? PrecheckExecute(ComponentExecuteRequest request, bool linux)
    {
        if (!string.Equals(request.ComponentId, ComponentId, StringComparison.Ordinal)
            || !string.Equals(request.Request.ComponentId, ComponentId, StringComparison.Ordinal))
            throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        if (!linux) return Refuse([ComponentProblemCodes.PlatformUnsupported], null);
        if (request.Request.Mode != ComponentSourceMode.SystemPackage)
            return Refuse([ComponentProblemCodes.SourceModeUnsupported], null);
        if (request.Expected is not { Mode: ComponentSourceMode.SystemPackage, Archive: null } expected)
            return Refuse([ComponentProblemCodes.ExecutionNotRequested], null);
        // An uninstall freezes the verified installed identity only; install and update additionally
        // freeze the resolved candidate and its dependencies.
        var complete = request.Request.Change switch
        {
            RuntimeChange.Install => expected.Package is not null && expected.InstalledPackage is null,
            RuntimeChange.Update => expected.Package is not null && PackageIdentityPolicy.Valid(expected.InstalledPackage),
            RuntimeChange.Uninstall => expected.Package is null && PackageIdentityPolicy.Valid(expected.InstalledPackage),
            _ => false
        };
        return complete ? null : Refuse([ComponentProblemCodes.ExecutionNotRequested], null);
    }

    /// <summary>The guards that need the host's own answer: a moved revision, plan or identity refuses.</summary>
    public static ComponentExecutionResult? PrecheckFrozen(ComponentExecuteRequest request, ComponentState state,
        ComponentPackageResolution resolution)
    {
        if (!string.Equals(request.Request.HostId, state.HostId, StringComparison.Ordinal))
            return Refuse([ComponentProblemCodes.RequestInvalid], state);
        var plan = Evaluate(request.Request, state, resolution, executionAvailable: true);
        if (!plan.Allowed) return Refuse([.. plan.Problems], state);
        if (request.Expected?.InstalledPackage != plan.Target?.InstalledPackage)
            return Refuse([ComponentProblemCodes.PackageExpectationChanged], state);
        if (request.Request.Change == RuntimeChange.Uninstall)
            return request.Expected is { Package: null } ? null : Refuse([ComponentProblemCodes.PackageExpectationChanged], state);
        var expected = request.Expected?.Package;
        return AptPackageResolution.IsUsableExpectation(expected)
            && AptPackageResolution.MatchesExpectation(expected, plan.Target?.Package)
            ? null : Refuse([ComponentProblemCodes.PackageExpectationChanged], state);
    }

    private static ComponentExecutionResult Refuse(IReadOnlyList<string> problems, ComponentState? state) =>
        new(ComponentId, ComponentExecutionOutcome.Refused, problems, [], state, state?.ActiveVersion);
}
