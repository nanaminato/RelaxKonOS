namespace RelaxKonOS.AppSDK;

/// <summary>Permission-gated, read-only server file access for package applications.</summary>
public interface IServerFiles
{
    /// <summary>
    /// Opens a server file for reading. The caller owns and must dispose <see cref="ServerFileReadResult.Content"/>
    /// when the status is <see cref="AppCapabilityResult.Succeeded"/>.
    /// </summary>
    Task<ServerFileReadResult> OpenReadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record ServerFileReadResult(AppCapabilityResult Status, Stream? Content, string? FileName);
