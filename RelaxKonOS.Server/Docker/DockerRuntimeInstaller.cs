using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerRuntimeInstaller(IDockerEngineService engine) : IDockerRuntimeInstaller
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
            ["Published Docker ports can bypass parts of host firewall policy. RelaxKonOS does not execute package-manager commands automatically."]);
    }

    public Task<DockerOperationResult> ExecuteAsync(DockerInstallationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (!request.Confirmed) return Task.FromResult(new DockerOperationResult(false, "docker.confirmation_required"));
        // Docker's repository setup and vendor installers are not yet a closed operation model.
        // Do not execute an administrator-configured command as a substitute for that model.
        return Task.FromResult(new DockerOperationResult(false, "docker.manual_host_action_required"));
    }
}
