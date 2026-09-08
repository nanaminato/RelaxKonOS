using System.Security.Claims;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Privileged;

public interface IFileElevationSessionStore
{
    bool IsElevated(ClaimsPrincipal principal, FileElevationCapability capability, params string[] paths);
    DateTimeOffset Grant(ClaimsPrincipal principal, FileElevationCapability capability, string path, bool includeDescendants = false,
        string authenticationMethod = "host-password", string? correlationId = null);
}
