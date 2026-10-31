using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

internal static class ComponentPackageInstallations
{
    public static void Validate(string componentId, InstallationOperationKind kind, ComponentExecuteRequest frozen, string key)
    {
        if (kind is not (InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Uninstall) || frozen is null || frozen.ComponentId != componentId
            || frozen.Request is not { } request || request.ComponentId != componentId || !HostPackageComponentAdapter.ValidRequest(request)
            || string.IsNullOrWhiteSpace(request.HostId) || request.ExpectedRevision <= 0
            || frozen.Expected is not { Mode: ComponentSourceMode.SystemPackage, Archive: null, Pin: null, Installer: null }
            || kind == InstallationOperationKind.Install && frozen.Expected.InstalledPackage is not null
            || (kind == InstallationOperationKind.Uninstall
                ? !PackageIdentityPolicy.Valid(frozen.Expected.InstalledPackage) || frozen.Expected.Package is not null
                : frozen.Expected.Package?.PackageName != HostPackageDefinitions.Find(componentId)?.PackageName || !AptPackageResolution.IsUsableExpectation(frozen.Expected.Package))
            || kind != InstallationOperationKind.Install && (frozen.Expected.InstalledPackage?.PackageName != HostPackageDefinitions.Find(componentId)?.PackageName
                || !PackageIdentityPolicy.Valid(frozen.Expected.InstalledPackage)))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        if (request.Change != (kind == InstallationOperationKind.Install ? RuntimeChange.Install : kind == InstallationOperationKind.Upgrade ? RuntimeChange.Update : RuntimeChange.Uninstall))
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        if (request.IdempotencyKey != key) throw new InstallationException(InstallationProblemCodes.IdempotencyConflict);
    }
    public static void ValidateRecovery(string componentId, InstallationOperationKind kind, ComponentRecoveryRequest request)
    {
        if (kind != InstallationOperationKind.Repair || request.ComponentId != componentId || (request.Action is ComponentRecoveryAction.RefreshSources or ComponentRecoveryAction.ConfigureOfficialSource or ComponentRecoveryAction.RegisterPackage ? request.OperationId != Guid.Empty : request.OperationId == Guid.Empty)
            || request.Action == ComponentRecoveryAction.ConfigureOfficialSource && componentId is not (ComponentIds.Tailscale or ComponentIds.SingBox)
            || (request.Action == ComponentRecoveryAction.RegisterPackage
                ? !PackageIdentityPolicy.Valid(request.ExpectedInstalledPackage) || request.ExpectedInstalledPackage!.PackageName != HostPackageDefinitions.Find(componentId)?.PackageName
                : request.ExpectedInstalledPackage is not null)
            || !Enum.IsDefined(request.Action)) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    public static async Task ExecuteRecoveryAsync(ComponentRecoveryRequest request, IInstallationProgress progress,
        IPrivilegedOperationTransport helper, CancellationToken ct)
    {
        await progress.ReportAsync(new(request.Action == ComponentRecoveryAction.RefreshSources ? InstallationStage.UpdatingPackageLists
            : InstallationStage.Preflight, Cancellable: false), ct);
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.ComponentPackageRecover,
            PackageRecovery: request, OperationId: progress.OperationId), CancellationToken.None);
        if (!result.Success) throw new InstallationException(ComponentProblemCodes.RecoveryRequired);
        if (request.Action is ComponentRecoveryAction.RefreshSources or ComponentRecoveryAction.ConfigureOfficialSource) return; // An uncertain refresh is Interrupted; never inferred from cached metadata.
        var observed = await helper.ExecuteAsync(new(PrivilegedOperationKind.ComponentPackageQuery,
            ComponentId: request.ComponentId, OperationId: progress.OperationId), CancellationToken.None);
        if (observed.ComponentPackage?.RecoveryReceipt is not { } receipt || receipt.OperationId != progress.OperationId
            || receipt.TargetOperationId != request.OperationId || receipt.Action != request.Action)
            throw new InstallationException(ComponentProblemCodes.RecoveryRequired);
    }
    public static async Task ExecuteAsync(ComponentLifecycleRegistry registry, ComponentExecuteRequest frozen,
        IInstallationProgress progress, CancellationToken ct)
    {
        await progress.ReportAsync(new(InstallationStage.Preflight, Cancellable: false), ct);
        var result = await registry.Require(frozen.ComponentId).ExecuteAsync(frozen, CancellationToken.None);
        if (result.Outcome != ComponentExecutionOutcome.Succeeded)
            throw new InstallationException(result.Detail ?? result.Problems.FirstOrDefault() ?? ComponentProblemCodes.InstallFailed);
        await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
    }
    public static async Task<InstallationRecovery> RecoverAsync(string componentId, Guid operationId,
        IPrivilegedOperationTransport helper, CancellationToken ct, InstallationOperationKind kind)
    {
        var result = await helper.ExecuteAsync(new(PrivilegedOperationKind.ComponentPackageQuery, ComponentId: componentId, OperationId: operationId), ct);
        if (kind == InstallationOperationKind.Repair && result.Success && result.ComponentPackage?.RecoveryReceipt is { } recovery && recovery.OperationId == operationId)
            return new(InstallationOperationState.Succeeded);
        return kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Uninstall && result.Success && result.ComponentPackage is { RecoveryRequired: false } state
            && (kind == InstallationOperationKind.Uninstall ? state.InstalledVersion is null : state.InstalledPackage is not null) && state.CommittedOperationId == operationId
            ? new(InstallationOperationState.Succeeded)
            : new(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
    }
}

public sealed class SquidInstallationService(ComponentLifecycleRegistry registry, IPrivilegedOperationTransport helper)
    : InstallationService<SquidInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Squid;
    protected override bool Confirmed(SquidInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Uninstall or InstallationOperationKind.Repair;
    protected override void ValidateRequest(InstallationOperationKind kind, SquidInstallationRequest request, string key)
    {
        if (request.Recovery is { } recovery && request.FrozenPackage is null) ComponentPackageInstallations.ValidateRecovery(ComponentIds.Squid, kind, recovery);
        else if (request.Recovery is null && request.FrozenPackage is { } frozen) ComponentPackageInstallations.Validate(ComponentIds.Squid, kind, frozen, key);
        else throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override Task ExecuteAsync(InstallationOperationKind kind, SquidInstallationRequest request, string actor,
        IInstallationProgress progress, CancellationToken ct) => request.Recovery is { } recovery
            ? ComponentPackageInstallations.ExecuteRecoveryAsync(recovery, progress, helper, ct)
            : ComponentPackageInstallations.ExecuteAsync(registry, request.FrozenPackage!, progress, ct);
    public override Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct) =>
        ComponentPackageInstallations.RecoverAsync(ComponentIds.Squid, operation.OperationId, helper, ct, operation.Kind);
}
