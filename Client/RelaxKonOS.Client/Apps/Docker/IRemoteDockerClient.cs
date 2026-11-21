using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.Docker;

public interface IRemoteDockerClient : IServerDockerResources
{
    Task<DockerProxyStatusDto> SaveProxyAsync(SaveDockerProxySettingsRequest request, CancellationToken cancellationToken = default);
    Task<DockerProxyStatusDto> ClearProxyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A proxy write the server refused, carrying its stable problem code. The server rejects a
/// preference with <c>400</c> and leaves the previous one untouched, so the caller needs the code
/// to explain what was wrong instead of showing a transport error.
/// </summary>
public sealed class DockerProxyRequestException(string problemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
