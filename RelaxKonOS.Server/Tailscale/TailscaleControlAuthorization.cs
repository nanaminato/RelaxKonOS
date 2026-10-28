using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Tailscale;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Tailscale;

public interface ITailscaleControlAuthorization
{
    bool IsGranted(ClaimsPrincipal principal);
}

/// <summary>A resident module must not retain a request-scoped grant or account classifier.</summary>
public sealed class TailscaleControlAuthorization(IServiceScopeFactory scopes) : ITailscaleControlAuthorization
{
    public bool IsGranted(ClaimsPrincipal principal)
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IHostElevationSessionStore>()
            .IsGranted(principal, HostElevationCapability.HostNetworkChange, TailscaleAccess.Resource);
    }
}
