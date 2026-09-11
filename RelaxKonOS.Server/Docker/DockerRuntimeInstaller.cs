using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

public sealed class DockerRuntimeInstaller(IDockerEngineService engine, IPrivilegedOperationTransport transport) : IDockerRuntimeInstaller
{
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
            ? new(true, string.Empty) : new(false, "docker.install_verification_failed");
    }
}
