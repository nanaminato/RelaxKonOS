using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>JWT-jti-scoped, short-lived authorization for one structured host capability.</summary>
public interface IHostElevationSessionStore
{
    bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target);
    DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
        bool includeDescendants, string authenticationMethod, string? correlationId = null);
    void Revoke(ClaimsPrincipal principal);
}
