using RelaxKonOS.Protocol.ImageMirrors;

namespace RelaxKonOS.AppSDK;

/// <summary>Image mirror preferences owned by the current server account.</summary>
public interface IServerDockerImageMirrors
{
    Task<IReadOnlyList<ImageMirrorDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<ImageMirrorDto> CreateAsync(CreateImageMirrorRequest request, CancellationToken cancellationToken = default);
    Task SelectAsync(Guid? mirrorId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
