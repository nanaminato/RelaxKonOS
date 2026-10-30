using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.ManagedRuntimes.ComponentPackages;

public static class TailscaleInstallerPlan
{
    public static ComponentPlanResult Evaluate(ComponentPlanRequest request, ComponentState state, ComponentDownloadRelease? candidate)
    {
        var problems = new List<string>();
        if (request.ComponentId != ComponentIds.Tailscale || state.ComponentId != ComponentIds.Tailscale || request.HostId != state.HostId
            || request.Mode != ComponentSourceMode.NativeInstaller || state.Mode != ComponentSourceMode.NativeInstaller
            || state.Target.Os != "windows" || request.Change is not (RuntimeChange.Install or RuntimeChange.Update or RuntimeChange.Uninstall)
            || !request.KeepData || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128
            || request.SourceId is not null || request.ReleaseId is not null || request.ExpectedSourceRevision != 0 || request.ExpectedTrustRevision != 0)
            problems.Add(ComponentProblemCodes.RequestInvalid);
        if (request.ExpectedRevision != state.Revision) problems.Add(ComponentProblemCodes.StateRevisionConflict);
        if (state.RecoveryRequired) problems.Add(ComponentProblemCodes.RecoveryRequired);
        if (request.Change == RuntimeChange.Install && state.Ownership != RuntimeOwnership.Absent) problems.Add(ComponentProblemCodes.PackageAlreadyInstalled);
        if (request.Change != RuntimeChange.Install && state.Ownership != RuntimeOwnership.Managed) problems.Add(ComponentProblemCodes.PackageOwnershipRequired);
        if (candidate is null) problems.Add(ComponentProblemCodes.PackageUnresolved);
        else
        {
            try { TailscaleMsiArtifactTransfer.Validate(candidate); }
            catch (RuntimePolicyException) { problems.Add(ComponentProblemCodes.PackageUnresolved); }
            if (candidate.Rid != "win-" + state.Target.Architecture) problems.Add(ComponentProblemCodes.PackageVersionMismatch);
            if (request.RequestedVersion is { } selected && selected != candidate.Version) problems.Add(ComponentProblemCodes.PackageVersionMismatch);
            if (request.Change == RuntimeChange.Update && (!Version.TryParse(candidate.Version, out var next)
                || !Version.TryParse(state.ActiveVersion, out var current) || next <= current)) problems.Add(ComponentProblemCodes.PackageNoUpdate);
            if (request.Change == RuntimeChange.Uninstall && candidate.Version != state.ActiveVersion) problems.Add(ComponentProblemCodes.PackageVersionMismatch);
        }
        return new(request, state, candidate is null ? null : new(ComponentSourceMode.NativeInstaller, null, null, Installer: candidate),
            problems.Distinct().ToArray(), [ComponentProblemCodes.RollbackUnsupported]);
    }
}
