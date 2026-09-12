using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

public sealed class CanonicalUserResolver(IIdentityProvider identities, IUserRepository users,
    IAliasCredentialRepository credentials, AuthSessionStore sessions)
{
    public User ResolveSystem(PlatformUserInfo identity)
    {
        var user = users.FindByIdentity(identity.Uid, identity.Platform);
        if (user is null)
            return users.Add(new User { Id = Guid.NewGuid(), Username = identity.Username, Platform = identity.Platform,
                PlatformIdentity = identity.Uid, CreatedAt = DateTimeOffset.UtcNow });
        RequireBinding(user, false);
        return users.FindById(user.Id)!;
    }

    public PlatformUserInfo RequireBinding(User user, bool requireEligibility)
    {
        if (user.IdentityReviewRequired) throw new AliasAuthenticationException(401, "invalid-credential");
        var byName = identities.Lookup(user.Username);
        var byId = identities.LookupIdentity(user.PlatformIdentity);
        if (byName.Status == IdentityLookupStatus.Unavailable || byId.Status == IdentityLookupStatus.Unavailable)
            throw new AliasAuthenticationException(503, "authentication-unavailable");
        if (byId.Identity is not { } identity || identity.Uid != user.PlatformIdentity || identity.Platform != user.Platform
            || byName.Identity is { } named && named.Uid != user.PlatformIdentity)
        {
            var expectedVersion = user.SecurityVersion;
            user.SecurityVersion = checked(expectedVersion + 1);
            user.IdentityReviewRequired = true;
            var credential = credentials.Find(user.Id) ?? new AliasCredential { UserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
            credential.UpdatedAt = DateTimeOffset.UtcNow;
            if (credentials.IsPersistent)
                credentials.Commit(credential, credential.Revision, user, expectedVersion,
                    new AuthenticationSecurityEvent { Id = Guid.NewGuid(), CanonicalUserId = user.Id,
                        EventType = "CanonicalIdentityMismatch", ReasonCode = "binding-mismatch", CreatedAt = DateTimeOffset.UtcNow });
            else users.Update(user);
            sessions.RevokeUser(user.Id);
            throw new AliasAuthenticationException(401, "invalid-credential");
        }
        if (requireEligibility && !identities.CheckAliasEligibility(identity).Available)
            throw new AliasAuthenticationException(401, "invalid-credential");
        if (user.Username != identity.Username)
        {
            user.Username = identity.Username;
            users.Update(user);
        }
        return identity;
    }
}
