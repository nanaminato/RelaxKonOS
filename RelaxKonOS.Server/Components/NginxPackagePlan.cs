using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.Server.Components;

/// <summary>
/// The Linux package decision, kept free of host access so it can be checked without a Linux host.
/// It only ever describes a frozen candidate; whether a transaction can install it is a separate
/// fact that this build answers with "no".
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
            resolution.Candidate?.Sha256, managed ? "managed" : "unmanaged"),
            ownership, ComponentSourceMode.SystemPackage, target, resolution.InstalledVersion, RecoveryRequired: false);
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
        if (request.Change == RuntimeChange.Install && state.Ownership == RuntimeOwnership.Managed)
            problems.Add(ComponentProblemCodes.PackageAlreadyInstalled);
        if (request.Change != RuntimeChange.Install && state.Ownership == RuntimeOwnership.Absent)
            problems.Add(ComponentProblemCodes.PackageAbsent);
        var candidate = resolution.Candidate;
        if (candidate is null)
        {
            problems.Add(ComponentProblemCodes.PackageUnresolved);
            return new(request, state, null, problems, []);
        }
        if (!string.IsNullOrWhiteSpace(request.RequestedVersion)
            && !string.Equals(request.RequestedVersion, candidate.Version, StringComparison.Ordinal))
            problems.Add(ComponentProblemCodes.PackageVersionMismatch);
        // Everything is frozen, but only a first install has a transaction that honours the freeze.
        if (!executionAvailable || request.Change != RuntimeChange.Install)
            problems.Add(ComponentProblemCodes.ExecutionUnavailable);
        return new(request, state, ComponentFrozenTarget.FromPackage(candidate), problems, []);
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
        if (request.Expected is not { Mode: ComponentSourceMode.SystemPackage, Package: not null })
            return Refuse([ComponentProblemCodes.ExecutionNotRequested], null);
        // Only a first install has a transaction that honours a freeze; everything else stays refused.
        return request.Request.Change == RuntimeChange.Install ? null : Refuse([ComponentProblemCodes.ExecutionUnavailable], null);
    }

    /// <summary>The guards that need the host's own answer: a moved revision, plan or identity refuses.</summary>
    public static ComponentExecutionResult? PrecheckFrozen(ComponentExecuteRequest request, ComponentState state,
        ComponentPackageResolution resolution)
    {
        var plan = Evaluate(request.Request, state, resolution, executionAvailable: true);
        if (!plan.Allowed) return Refuse([.. plan.Problems], state);
        var expected = request.Expected!.Package;
        return AptPackageResolution.IsUsableExpectation(expected)
            && AptPackageResolution.MatchesExpectation(expected, plan.Target?.Package)
            ? null : Refuse([ComponentProblemCodes.PackageExpectationChanged], state);
    }

    private static ComponentExecutionResult Refuse(IReadOnlyList<string> problems, ComponentState? state) =>
        new(ComponentId, ComponentExecutionOutcome.Refused, problems, [], state, state?.ActiveVersion);
}
