using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.Installations;

public sealed class DockerInstallationService(IDockerRuntimeInstaller installer, IDockerEngineService engine,
    RelaxKonOS.Server.Components.ComponentLifecycleRegistry components, RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport helper)
    : InstallationService<DockerInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Docker;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Repair;
    protected override bool Confirmed(DockerInstallationRequest request) => request.Confirmed;
    protected override void ValidateRequest(InstallationOperationKind kind, DockerInstallationRequest request, string key)
    {
        if (request.Recovery is { } recovery && request.FrozenPackage is null) ComponentPackageInstallations.ValidateRecovery("docker", kind, recovery);
        else if (request.Recovery is null && request.FrozenPackage is { } frozen) ComponentPackageInstallations.Validate("docker", kind, frozen, key);
        else if (request.Recovery is not null || kind != InstallationOperationKind.Install) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, DockerInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (options.Recovery is { } recovery)
        {
            await ComponentPackageInstallations.ExecuteRecoveryAsync(recovery, progress, helper, ct);
            return;
        }
        if (options.FrozenPackage is { } frozen)
        {
            await ComponentPackageInstallations.ExecuteAsync(components, frozen, progress, ct);
            Check((await engine.GetStatusAsync(CancellationToken.None)).IsAvailable, "docker.access_restart_required");
            return;
        }
        await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
        var result = await installer.InstallAsync(CancellationToken.None);
        Check(result.Success, result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check((await engine.GetStatusAsync(CancellationToken.None)).IsAvailable, "docker.install_verification_failed");
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => await ComponentPackageInstallations.RecoverAsync("docker", operation.OperationId, helper, ct, operation.Kind);
}
