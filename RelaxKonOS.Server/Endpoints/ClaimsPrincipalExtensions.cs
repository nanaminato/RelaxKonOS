using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// The authenticated caller identity in the form endpoints need. Authentication has already run when a
/// handler executes, so a missing subject is an authorization failure, not a format error worth a 500.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>User ID from <c>sub</c> (or the mapped name-identifier claim).</summary>
    internal static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                       ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
            ? userId
            : throw new UnauthorizedAccessException("The access token does not identify a user.");

    /// <summary>Session ID from the <c>sid</c> claim, or null when the token has none.</summary>
    internal static Guid? SessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("sid")?.Value, out var sessionId) ? sessionId : null;
}
