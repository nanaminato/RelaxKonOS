namespace RelaxKonOS.AppSDK;

/// <summary>Host-issued, short-lived credentials for directly calling the server file API.</summary>
public interface IExternalFileApiAccess
{
    /// <summary>
    /// Gets a capability token restricted to this application's granted file permissions.
    /// The token is valid only for the server file API and is never a user access or refresh token.
    /// </summary>
    Task<FileApiAccessResult> GetAccessAsync(CancellationToken cancellationToken = default);
}

public sealed record FileApiAccessResult(
    AppCapabilityResult Status,
    Uri? ServerUri,
    string? AccessToken,
    DateTimeOffset? ExpiresAt);

/// <summary>Creates host-renewed, single-file playback URLs for media engines that require HTTP.</summary>
public interface IExternalMediaService
{
    Task<ExternalMediaLeaseResult> OpenPlaybackAsync(string path, CancellationToken cancellationToken = default);
}

/// <param name="Detail">A safe host-provided explanation when the playback lease cannot be created.</param>
public sealed record ExternalMediaLeaseResult(
    AppCapabilityResult Status,
    IExternalMediaLease? Lease,
    string? Detail = null);

/// <summary>
/// A media URL valid only while its host-owned lease remains active. Dispose it when playback ends.
/// </summary>
public interface IExternalMediaLease : IAsyncDisposable
{
    Uri PlaybackUri { get; }
    DateTimeOffset ExpiresAt { get; }
}
