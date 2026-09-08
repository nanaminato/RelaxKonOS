using System.Security.Claims;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Maps Explorer elevation capabilities to the capability-scoped host elevation store.</summary>
public sealed class FileElevationSessionStore(IHostElevationSessionStore elevations) : IFileElevationSessionStore
{
    public bool IsElevated(ClaimsPrincipal principal, FileElevationCapability capability, params string[] paths)
        => paths.All(path => elevations.IsGranted(principal, ToHostCapability(capability), path));

    public DateTimeOffset Grant(ClaimsPrincipal principal, FileElevationCapability capability, string path, bool includeDescendants = false,
        string authenticationMethod = "host-password", string? correlationId = null)
        => elevations.Grant(principal, ToHostCapability(capability), path, includeDescendants, authenticationMethod, correlationId);

    private static HostElevationCapability ToHostCapability(FileElevationCapability capability) => capability switch
    {
        FileElevationCapability.Read => HostElevationCapability.FileRead,
        FileElevationCapability.Write => HostElevationCapability.FileWrite,
        FileElevationCapability.CreateDirectory => HostElevationCapability.FileCreateDirectory,
        FileElevationCapability.Delete => HostElevationCapability.FileDelete,
        FileElevationCapability.Rename => HostElevationCapability.FileRename,
        FileElevationCapability.Move => HostElevationCapability.FileMove,
        FileElevationCapability.Copy => HostElevationCapability.FileCopy,
        FileElevationCapability.Upload => HostElevationCapability.FileUpload,
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };
}
