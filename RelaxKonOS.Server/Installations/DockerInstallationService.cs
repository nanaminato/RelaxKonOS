using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.Installations;

public sealed class DockerInstallationService(IDockerRuntimeInstaller installer, IDockerEngineService engine)
    : InstallationService<DockerInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Docker;
    protected override bool Confirmed(DockerInstallationRequest request) => request.Confirmed;
    protected override async Task ExecuteAsync(InstallationOperationKind kind, DockerInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        await progress.ReportAsync(new(InstallationStage.Installing), ct);
        var result = await installer.InstallAsync(CancellationToken.None);
        Check(result.Success, result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check((await engine.GetStatusAsync(CancellationToken.None)).IsAvailable, "docker.install_verification_failed");
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => Recovery((await engine.GetStatusAsync(ct)).IsAvailable);
}
