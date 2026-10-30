using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

public sealed class TailscaleInstallationService(RelaxKonOS.Server.Components.ComponentLifecycleRegistry components, IPrivilegedOperationTransport helper)
    : InstallationService<TailscaleInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Tailscale;
    public override IReadOnlyList<string> Resources => OperatingSystem.IsWindows()
        ? ["service:Tailscale", "windows:msi", "host:tailscale"] : ["service:Tailscale", "linux:apt-dpkg", "host:tailscale"];
    protected override bool Confirmed(TailscaleInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Uninstall or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, TailscaleInstallationRequest request, string key)
    {
        if (OperatingSystem.IsWindows())
        {
            if (request.Recovery is { } maintenance && request.FrozenPackage is null)
            {
                Check(kind == InstallationOperationKind.Repair && maintenance.ComponentId == "tailscale"
                    && maintenance.OperationId != Guid.Empty && maintenance.Action is RelaxKonOS.Protocol.ManagedRuntimes.ComponentRecoveryAction.Stop
                        or RelaxKonOS.Protocol.ManagedRuntimes.ComponentRecoveryAction.Close, InstallationProblemCodes.InvalidRequest);
                return;
            }
            var frozen = request.FrozenPackage;
            Check(request.Recovery is null && frozen is { ComponentId: "tailscale", Expected.Mode: RelaxKonOS.Protocol.ManagedRuntimes.ComponentSourceMode.NativeInstaller,
                Expected.Archive: null, Expected.Package: null, Expected.Pin: null, Expected.Installer: not null }
                && frozen.Request.ComponentId == "tailscale" && frozen.Request.Mode == frozen.Expected.Mode
                && frozen.Request.Change == (kind == InstallationOperationKind.Install ? RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Install
                    : kind == InstallationOperationKind.Upgrade ? RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Update
                    : kind == InstallationOperationKind.Uninstall ? RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange.Uninstall
                    : (RelaxKonOS.Protocol.ManagedRuntimes.RuntimeChange)(-1)), InstallationProblemCodes.InvalidRequest);
            Check(frozen!.Request.IdempotencyKey == key, InstallationProblemCodes.IdempotencyConflict);
            return;
        }
        if (request.Recovery is { } recovery && request.FrozenPackage is null) ComponentPackageInstallations.ValidateRecovery("tailscale", kind, recovery);
        else if (request.Recovery is null && request.FrozenPackage is { } frozen) ComponentPackageInstallations.Validate("tailscale", kind, frozen, key);
        else throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, TailscaleInstallationRequest request, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (request.Recovery is { } recovery)
        {
            if (OperatingSystem.IsWindows())
            {
                var result = await helper.ExecuteAsync(new(RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.TailscaleInstallerRecover,
                    PackageRecovery: recovery, OperationId: progress.OperationId), CancellationToken.None);
                Check(result.Success, "component.recovery_required");
            }
            else await ComponentPackageInstallations.ExecuteRecoveryAsync(recovery, progress, helper, ct);
        }
        else await ComponentPackageInstallations.ExecuteAsync(components, request.FrozenPackage!, progress, ct);
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return await ComponentPackageInstallations.RecoverAsync("tailscale", operation.OperationId, helper, ct, operation.Kind);
        var result = await helper.ExecuteAsync(new(RelaxKonOS.Protocol.Privileged.PrivilegedOperationKind.TailscaleInstallerQuery,
            OperationId: operation.OperationId), ct);
        return result.Success && result.TailscaleInstallerSnapshot?.VerifiedOperationId == operation.OperationId
            ? new(InstallationOperationState.Succeeded) : new(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
    }
}

