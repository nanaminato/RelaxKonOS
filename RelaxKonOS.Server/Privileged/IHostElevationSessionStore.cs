using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Current administrator policy, verified access-token memory, or a temporary capability/resource grant.</summary>
public interface IHostElevationSessionStore
{
    bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target);
    DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
        bool includeDescendants, string authenticationMethod, string? correlationId = null);
    void Revoke(ClaimsPrincipal principal);
}
