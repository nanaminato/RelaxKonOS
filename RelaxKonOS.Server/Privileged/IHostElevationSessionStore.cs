using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Current administrator policy or JWT-jti-scoped temporary authorization for a structured capability.</summary>
public interface IHostElevationSessionStore
{
    bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target);
    DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
        bool includeDescendants, string authenticationMethod, string? correlationId = null);
    void Revoke(ClaimsPrincipal principal);
}
