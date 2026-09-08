using RelaxKonOS.Protocol.Firewall;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Firewall;

/// <summary>Verifies a non-root caller's own host password for one firewall change only.</summary>
public interface IFirewallChangeAuthorizationService
{
    FirewallOperationResult Authorize(string requester, FirewallCredentialConfirmation? confirmation);
}

public sealed class FirewallChangeAuthorizationService(IIdentityProvider identities) : IFirewallChangeAuthorizationService
{
    public FirewallOperationResult Authorize(string requester, FirewallCredentialConfirmation? confirmation)
    {
        if (string.IsNullOrWhiteSpace(requester) || requester.IndexOf('\0') >= 0)
            return new(false, "firewall.invalid_requester");

        // Root's authenticated RelaxKonOS session is already the required confirmation.
        if (string.Equals(requester, "root", StringComparison.Ordinal))
            return new(true);

        if (confirmation is null || string.IsNullOrEmpty(confirmation.Password))
            return new(false, "firewall.password_required");

        // The secret is passed directly to PAM and intentionally not retained, logged, or sent
        // to UFW. A successful verification authorizes only this HTTP request.
        return identities.Verify(requester, confirmation.Password).Success
            ? new(true)
            : new(false, "firewall.password_invalid");
    }
}
