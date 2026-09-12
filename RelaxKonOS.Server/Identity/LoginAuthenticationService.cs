using System.Net;
using Microsoft.AspNetCore.Identity;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

public sealed record AuthenticatedLogin(User User, string Method, long Revision, long SecurityVersion, string ProtectionKey);

public sealed class LoginAuthenticationService(IIdentityProvider identities, IUserRepository users,
    IAliasCredentialRepository credentials, AliasPasswordService passwords, CanonicalUserResolver resolver,
    LoginProtectionService protection)
{
    public async Task<AuthenticatedLogin> AuthenticateAsync(string identifier, string password, IPAddress? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 256 || identifier.Any(char.IsControl)
            || !AliasPasswordService.ValidInput(password)) throw new AliasAuthenticationException(400, "invalid-input");
        await CheckAsync(identifier, ip, ct);
        var alias = credentials.FindAlias(identifier);
        var system = identities.Lookup(identifier);
        if (system.Status == IdentityLookupStatus.Unavailable) throw new AliasAuthenticationException(503, "authentication-unavailable");
        var user = alias is not null ? users.FindById(alias.UserId)
            : system.Identity is { } identity ? users.FindByIdentity(identity.Uid, identity.Platform) : null;
        var key = user?.Id.ToString("D") ?? identifier;
        if (user is not null) await CheckAsync(key, ip, ct, user.Id);
        var version = user?.SecurityVersion ?? 0;
        var policy = user is null ? null : credentials.Find(user.Id);
        try
        {
            if (alias is not null)
            {
                if (system.Identity is not null || user is null) throw Invalid();
                if (passwords.Verify(alias, password) == PasswordVerificationResult.Failed) throw Invalid();
                resolver.RequireBinding(user, true);
                return new(user, "alias", alias.Revision, version, key);
            }
            if (system.Identity is null) { passwords.Dummy(password); throw Invalid(); }
            if (policy?.SystemLoginEnabled == false || user?.IdentityReviewRequired == true) { passwords.Dummy(password); throw Invalid(); }
            var verified = identities.Verify(identifier, password);
            if (verified.Error == CredentialError.Unknown) throw new AliasAuthenticationException(503, "authentication-unavailable");
            if (!verified.Success || verified.Identity is not { } trusted || trusted.Uid != system.Identity.Uid || trusted.Platform != system.Identity.Platform)
                throw Invalid();
            user = resolver.ResolveSystem(trusted);
            return new(user, "system", policy?.Revision ?? 0, version, user.Id.ToString("D"));
        }
        catch (AliasAuthenticationException exception) when (exception.Status == 401)
        {
            await protection.RecordFailureAsync(key, ip, ct, user?.Id);
            throw;
        }
    }

    public void RequireCurrent(AuthenticatedLogin login)
    {
        var current = users.FindById(login.User.Id);
        var policy = credentials.Find(login.User.Id);
        if (current is null || current.IdentityReviewRequired || current.SecurityVersion != login.SecurityVersion
            || (policy?.Revision ?? 0) != login.Revision || login.Method == "system" && policy?.SystemLoginEnabled == false)
            throw Invalid();
    }

    private async Task CheckAsync(string key, IPAddress? ip, CancellationToken ct, Guid? canonicalUserId = null)
    {
        if ((await protection.CheckAsync(key, ip, ct, canonicalUserId)).IsBlocked) throw new AliasAuthenticationException(429, "login-rate-limited");
    }
    private static AliasAuthenticationException Invalid() => new(401, "invalid-credential");
}
