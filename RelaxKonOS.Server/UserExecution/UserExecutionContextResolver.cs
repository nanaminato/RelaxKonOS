using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Observability;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Resolves an effective OS identity only from a validated RelaxKonOS subject.</summary>
public sealed class UserExecutionContextResolver(IUserRepository users, CanonicalUserResolver canonicalUsers,
    IServerModeResolver serverMode, IEventLogger? eventLogger = null) : IUserExecutionContextResolver
{
    public UserExecutionContext Resolve(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId) || users.FindById(userId) is not { } user)
            throw Reject(UserExecutionProblemCode.AuthenticationInvalid, "The authenticated user does not exist.");

        PlatformUserInfo identity;
        try { identity = canonicalUsers.RequireBinding(user, requireEligibility: false); }
        catch (AliasAuthenticationException exception)
        {
            var code = exception.Code == "authentication-unavailable"
                ? UserExecutionProblemCode.IdentityUnavailable : UserExecutionProblemCode.IdentityMismatch;
            throw Reject(code, "The authenticated OS identity could not be verified.");
        }

        // Eligibility is one shared rule, and login reports the same answer: a refused identity is
        // known to the client before it opens anything instead of surfacing at the first folder.
        var eligibility = UserExecutionEligibilityRules.Evaluate(identity, serverMode.Mode);
        if (!eligibility.Available)
        {
            var unsupported = eligibility.Reason == UserExecutionIneligibleReason.UnsupportedPlatform;
            throw Reject(unsupported ? UserExecutionProblemCode.UnsupportedPlatform
                    : UserExecutionProblemCode.IdentityNotEligible,
                eligibility.Describe(identity),
                unsupported ? "The OS identity is not supported for user execution."
                    : "The OS identity is not eligible for user execution.");
        }

        return new UserExecutionContext(userId, new UserExecutionIdentity(identity.Platform, identity.Uid,
            identity.Username, identity.HomeDirectory!));
    }

    /// <summary>
    /// A refused identity resolution is an authorization denial: it must leave a correlatable
    /// record even though no OS operation runs. The record carries <paramref name="auditMessage"/>,
    /// never the client-facing <paramref name="message"/>: the latter names the account so the signed-in
    /// user knows which identity was refused, and the audit record never may. The stable problem code
    /// is what identifies the refusal there.
    /// </summary>
    private UserExecutionException Reject(UserExecutionProblemCode code, string message, string? auditMessage = null)
    {
        eventLogger?.Write(new ObservabilityEvent(ObservabilityEventCatalog.AuthorizationDenied,
            ObservabilitySeverity.Information, ObservabilityOutcome.Denied, "server", auditMessage ?? message,
            ProblemCode: code.ToString(), Action: "authorization.check"));
        return new UserExecutionException(code, message);
    }
}
