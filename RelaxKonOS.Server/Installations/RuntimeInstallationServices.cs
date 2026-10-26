using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Server.Components;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Tunnels;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Installations;

internal sealed class NginxInstallationService(NginxWebServerManager manager, InstallationFileReferenceStore references, ComponentLifecycleRegistry components,
    RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport helper) : InstallationService<NginxInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Nginx;
    protected override bool Confirmed(NginxInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Repair or InstallationOperationKind.Uninstall;
    protected override void ValidateRequest(InstallationOperationKind kind, NginxInstallationRequest request, string key)
    {
        if (request.Recovery is { } maintenance)
        {
            Check(OperatingSystem.IsLinux() && request.FrozenPackage is null && request.Version is null && request.FileReferenceId is null, InstallationProblemCodes.InvalidRequest);
            ComponentPackageInstallations.ValidateRecovery(ComponentIds.Nginx, kind, maintenance);
            return;
        }
        if (request.FrozenPackage is not { } frozen) return;
        if (frozen.Expected?.Pin is not null)
        {
            Check(kind == InstallationOperationKind.Install && request.Version is null && request.FileReferenceId is null
                && frozen.ComponentId == ComponentIds.Nginx && frozen.Request is { ComponentId: ComponentIds.Nginx,
                    Change: RuntimeChange.Install, Mode: ComponentSourceMode.ManagedArchive }
                && frozen.Expected is { Archive: null, Package: null, Pin.AffectedComponents: [ComponentIds.Nginx] },
                InstallationProblemCodes.InvalidRequest);
            Check(frozen.Request!.IdempotencyKey == key, InstallationProblemCodes.IdempotencyConflict);
            return;
        }
        Check(request.Version is null && request.FileReferenceId is null, InstallationProblemCodes.InvalidRequest);
        ComponentPackageInstallations.Validate(ComponentIds.Nginx, kind, frozen, key);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, NginxInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (options.Recovery is { } maintenance)
        {
            await ComponentPackageInstallations.ExecuteRecoveryAsync(maintenance, progress, helper, ct);
            return;
        }
        if (options.FrozenPackage is { } frozen)
        {
            await progress.ReportAsync(new(InstallationStage.Preflight, Cancellable: false), ct);
            // The ledger holds service:Nginx and linux:apt-dpkg for the full worker lifetime.
            await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
            var result = await components.Require(ComponentIds.Nginx).ExecuteAsync(frozen, CancellationToken.None);
            Check(result.Outcome == ComponentExecutionOutcome.Succeeded,
                result.Detail ?? result.Problems.FirstOrDefault() ?? ComponentProblemCodes.InstallFailed);
            await progress.ReportAsync(new(InstallationStage.HealthChecking), CancellationToken.None);
            Check(await manager.CheckInstallationAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), "nginx.install_verification_failed");
            return;
        }
        Check(string.IsNullOrWhiteSpace(options.FileReferenceId) || kind == InstallationOperationKind.Install, InstallationProblemCodes.InvalidRequest);
        using var source = kind == InstallationOperationKind.Install && !string.IsNullOrWhiteSpace(options.FileReferenceId) ? references.Open(Id, actor, options.FileReferenceId) : null;
        var problem = await manager.ExecuteInstallationAsync(kind, options, source, progress, ct);
        Check(string.IsNullOrEmpty(problem), problem);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(await manager.CheckInstallationAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), "nginx.install_verification_failed");
    }
    // Current health is not proof that this interrupted operation committed. Never replay APT here.
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var view = await components.Require(ComponentIds.Nginx).StateAsync(ct);
            if (view.CommittedOperationId == operation.OperationId && view.State is { Ownership: RuntimeOwnership.Managed, RecoveryRequired: false }
                && await manager.CheckInstallationAsync(false, ct))
                return new(InstallationOperationState.Succeeded);
        }
        if (OperatingSystem.IsLinux())
        {
            var recovery = await ComponentPackageInstallations.RecoverAsync(ComponentIds.Nginx, operation.OperationId, helper, ct, operation.Kind);
            if (recovery.State == InstallationOperationState.Succeeded && operation.Kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade
                && !await manager.CheckInstallationAsync(false, ct)) return new(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
            return recovery;
        }
        return new(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
    }
}

public sealed class FrpInstallationService(IRuntimeManager runtime, ITunnelProvider provider, IManagedFrpsService frps,
    InstallationFileReferenceStore references, ComponentLifecycleRegistry components)
    : InstallationService<FrpInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Frp;
    public override IReadOnlyList<string> Resources => ["service:Frp", "runtime:frp"];
    protected override bool Confirmed(FrpInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Upgrade or InstallationOperationKind.Repair or InstallationOperationKind.Uninstall;
    protected override void ValidateRequest(InstallationOperationKind kind, FrpInstallationRequest request, string key)
    {
        if (request.FrozenArchive is not { } frozen) return;
        Check(kind == InstallationOperationKind.Install && request.Version is null && request.FileReferenceId is null && !request.Rollback
            && frozen.ComponentId is ComponentIds.Frpc or ComponentIds.Frps
            && frozen.Request is { Change: RuntimeChange.Install, Mode: ComponentSourceMode.ManagedArchive }
            && frozen.Request.ComponentId == frozen.ComponentId && frozen.Expected is { Archive: null, Package: null, Pin: not null },
            InstallationProblemCodes.InvalidRequest);
        Check(frozen.Request.IdempotencyKey == key, InstallationProblemCodes.IdempotencyConflict);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, FrpInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (options.FrozenArchive is { } frozen)
        {
            await progress.ReportAsync(new(InstallationStage.Preflight, Cancellable: false), ct);
            var planned = await components.Require(frozen.ComponentId).PlanAsync(frozen.Request, ct);
            var expected = frozen.Expected!.Pin!;
            Check(planned.Allowed && planned.Target?.Pin is { } current
                && current.ReleaseId == expected.ReleaseId && current.SourceUri == expected.SourceUri
                && current.Sha256 == expected.Sha256 && current.Rid == expected.Rid && current.Version == expected.Version
                && current.MaximumBytes == expected.MaximumBytes && current.Format == expected.Format
                && current.AffectedComponents.SequenceEqual(expected.AffectedComponents), ComponentProblemCodes.PackageExpectationChanged);
            var installed = await runtime.InstallManagedFrpcAsync(expected.Version, progress, ct);
            Check(installed.Succeeded, installed.ProblemCode);
            await progress.ReportAsync(new(InstallationStage.HealthChecking, Cancellable: false), CancellationToken.None);
            var client = await runtime.GetManagedFrpcStatusAsync(CancellationToken.None);
            var server = await runtime.GetManagedFrpsStatusAsync(CancellationToken.None);
            Check(client.IntegrityVerified && server.IntegrityVerified && client.Version == expected.Version && server.Version == expected.Version,
                "tunnel.runtime_health_check_failed");
            return;
        }
        Check(!options.Rollback || kind == InstallationOperationKind.Repair, InstallationProblemCodes.InvalidRequest);
        Check(string.IsNullOrWhiteSpace(options.FileReferenceId) || kind == InstallationOperationKind.Install && !options.Rollback, InstallationProblemCodes.InvalidRequest);
        if (kind != InstallationOperationKind.Uninstall && !options.Rollback)
            Check(!string.IsNullOrWhiteSpace(options.Version) && options.Version.Length <= 32, "tunnel.runtime_version_invalid");
        TunnelOperationResultDto result;
        if (kind == InstallationOperationKind.Uninstall || options.Rollback)
        {
            await progress.ReportAsync(new(options.Rollback ? InstallationStage.RollingBack : InstallationStage.Preparing), ct);
            await provider.StopManagedProcessesAsync(CancellationToken.None);
            await frps.StopAsync(actor, CancellationToken.None);
            result = options.Rollback ? await runtime.RollbackManagedFrpcAsync(CancellationToken.None)
                : await runtime.UninstallManagedFrpcAsync(CancellationToken.None);
        }
        else if (string.IsNullOrWhiteSpace(options.FileReferenceId)) result = await runtime.InstallManagedFrpcAsync(options.Version!, progress, ct);
        else
        {
            using var source = references.Open(Id, actor, options.FileReferenceId);
            result = await runtime.InstallManagedFrpcFromArchiveAsync(options.Version!, source.Stream, source.Length, progress, ct);
        }
        Check(result.Succeeded, result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(await HealthyAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), "tunnel.runtime_health_check_failed");
    }
    private async Task<bool> HealthyAsync(bool absent, CancellationToken ct)
    {
        var client = await runtime.GetManagedFrpcStatusAsync(ct);
        var server = await runtime.GetManagedFrpsStatusAsync(ct);
        return absent ? client.State == TunnelRuntimeState.NotInstalled && server.State == TunnelRuntimeState.NotInstalled
            : client.State == TunnelRuntimeState.Available && server.State == TunnelRuntimeState.Available;
    }
    public override Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct) =>
        Task.FromResult(new InstallationRecovery(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted));
}

