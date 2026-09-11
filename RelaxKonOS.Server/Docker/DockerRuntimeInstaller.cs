using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerRuntimeInstaller(IDockerEngineService engine, IPrivilegedOperationTransport transport) : IDockerRuntimeInstaller
{
    public async Task<DockerInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default)
    {
        var status = await engine.GetStatusAsync(cancellationToken);
        if (status.IsAvailable)
            return new DockerInstallationPlanDto(false, "docker.already_available", [], []);
        if (OperatingSystem.IsWindows())
            return new DockerInstallationPlanDto(true, status.ProblemCode,
                ["Review Docker Desktop licensing and choose WSL 2 or Hyper-V.", "Run the vendor-signed installer through the host OS elevation flow.", "Restart Docker Desktop and verify hello-world."],
                ["RelaxKonOS does not install Docker Desktop automatically or collect administrator credentials."]);
        return new DockerInstallationPlanDto(true, status.ProblemCode,
            ["Review the official Docker Engine installation instructions for the host distribution.", "Use the host OS package manager through an administrator-approved elevation flow.", "Verify the local engine with hello-world before using RelaxKonOS management."],
            ["Published Docker ports can bypass parts of host firewall policy. The fixed Ubuntu installer requires a separate Docker installation grant."]);
    }

    public async Task<DockerOperationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return new(false, "docker.manual_host_action_required");
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.DockerEngineInstall), CancellationToken.None);
        if (!result.Success) return new(false, result.ProblemCode switch
        {
            PrivilegedProblemCode.UnsupportedOperation => "docker.install_not_supported",
            PrivilegedProblemCode.Conflict => "docker.install_conflict",
            PrivilegedProblemCode.InvalidProtocol => "installation.helper_protocol",
            _ => "docker.install_failed"
        });
        return (await engine.GetStatusAsync(CancellationToken.None)).IsAvailable
            ? new(true) : new(false, "docker.install_verification_failed");
    }
}
