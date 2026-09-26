using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Privileged;

public enum HostAccountPrivilege { StandardUser, HostAdministrator, HostRoot }

/// <summary>Derives host privilege from the current canonical OS binding, not an HTTP field or JWT role.</summary>
public interface IHostAccountPrivilegeService
{
    bool IsRoot(ClaimsPrincipal principal);
    HostAccountPrivilege Classify(PlatformUserInfo identity);
    HostAccountPrivilege Classify(ClaimsPrincipal principal);
}

public sealed class HostAccountPrivilegeService(IPrivilegedOperationTransport helper,
    IUserRepository users, CanonicalUserResolver canonicalUsers, IServerModeResolver mode) : IHostAccountPrivilegeService
{
    public HostAccountPrivilege Classify(ClaimsPrincipal principal)
    {
        var identity = ResolveCurrentIdentity(principal);
        // An alias password is not PAM authentication of the host administrator. It can still
        // obtain an exact, short-lived grant by presenting administrator credentials explicitly.
        if (principal.FindFirst("amr")?.Value != "system") return HostAccountPrivilege.StandardUser;
        return Classify(identity);
    }

    public bool IsRoot(ClaimsPrincipal principal)
    {
        if (principal.FindFirst("amr")?.Value != "system" || mode.Mode != ServerMode.System)
            return false;
        var identity = ResolveCurrentIdentity(principal);
        return identity.Platform == HostPlatformKind.Linux && identity.Uid == "0" && identity.Username == "root";
    }

    private PlatformUserInfo ResolveCurrentIdentity(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subject, out var userId) || users.FindById(userId) is not { } user)
            throw new UnauthorizedAccessException("The authenticated user is unavailable.");
        return canonicalUsers.RequireBinding(user, requireEligibility: false);
    }

    public HostAccountPrivilege Classify(PlatformUserInfo identity)
    {
        if (mode.Mode != ServerMode.System || identity.Platform != HostPlatformKind.Linux)
            return HostAccountPrivilege.StandardUser;
        if (identity.Uid == "0" && identity.Username == "root") return HostAccountPrivilege.HostRoot;
        if (!uint.TryParse(identity.Uid, out var uid) || uid < 1000 || uid == 65534)
            return HostAccountPrivilege.StandardUser;
        var result = helper.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.CheckHostAdministrator,
            HostAdministratorUsername: identity.Username, HostAdministratorUid: identity.Uid)).GetAwaiter().GetResult();
        if (!result.Success || result.HostAdministratorEligible is null)
            throw new InvalidOperationException("Host administrator policy is unavailable.");
        return result.HostAdministratorEligible.Value ? HostAccountPrivilege.HostAdministrator : HostAccountPrivilege.StandardUser;
    }
}
