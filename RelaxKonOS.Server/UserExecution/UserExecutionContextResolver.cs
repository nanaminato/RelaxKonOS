using System.IdentityModel.Tokens.Jwt;
using System.Runtime.InteropServices;
using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.UserExecution;

/// <summary>Resolves an effective OS identity only from a validated RelaxKonOS subject.</summary>
public sealed class UserExecutionContextResolver(IUserRepository users, CanonicalUserResolver canonicalUsers,
    IServerModeResolver serverMode) : IUserExecutionContextResolver
{
    public UserExecutionContext Resolve(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId) || users.FindById(userId) is not { } user)
            throw new UserExecutionException(UserExecutionProblemCode.AuthenticationInvalid, "The authenticated user does not exist.");

        PlatformUserInfo identity;
        try { identity = canonicalUsers.RequireBinding(user, requireEligibility: false); }
        catch (AliasAuthenticationException exception)
        {
            var code = exception.Code == "authentication-unavailable"
                ? UserExecutionProblemCode.IdentityUnavailable : UserExecutionProblemCode.IdentityMismatch;
            throw new UserExecutionException(code, "The authenticated OS identity could not be verified.");
        }

        if (identity.Platform == PlatformKind.Linux)
        {
            if (!uint.TryParse(identity.Uid, out var uid) || uid is 0 or 65534
                || (serverMode.Mode == ServerMode.System && !UserExecutionProtocol.IsEligibleLinuxUserId(uid))
                || string.IsNullOrWhiteSpace(identity.HomeDirectory)
                || !Path.IsPathFullyQualified(identity.HomeDirectory))
                throw new UserExecutionException(UserExecutionProblemCode.IdentityNotExecutable, "The OS identity is not eligible for user execution.");
            if (serverMode.Mode == ServerMode.User && uid != geteuid())
                throw new UserExecutionException(UserExecutionProblemCode.IdentityNotExecutable, "User Mode can execute only as the Server's effective Unix user.");
        }
        else if (identity.Platform == PlatformKind.Windows)
        {
            var account = identity.Username.Split('\\', 2);
            if (serverMode.Mode != ServerMode.System || account.Length != 2
                || !account[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(identity.Uid) || !identity.Uid.StartsWith("S-1-5-", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(identity.HomeDirectory) || !Path.IsPathFullyQualified(identity.HomeDirectory))
                throw new UserExecutionException(UserExecutionProblemCode.IdentityNotExecutable,
                    "Only local Windows accounts with a verified profile are eligible for System Mode user execution.");
        }
        else
        {
            throw new UserExecutionException(UserExecutionProblemCode.UnsupportedPlatform, "The OS identity is not supported for user execution.");
        }

        return new UserExecutionContext(userId, new UserExecutionIdentity(identity.Platform, identity.Uid,
            identity.Username, identity.HomeDirectory!));
    }

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint geteuid();
}
