using System.Net;
using Microsoft.AspNetCore.Identity;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Identity;

public sealed record AuthenticatedLogin(User User, string Method, long Revision, long SecurityVersion, string ProtectionKey,
    /// <summary>Whether this identity may execute ordinary operations on this Server. Login itself never
    /// depends on it: the session is issued either way, and the client is told the answer up front.</summary>
    UserExecutionEligibility ExecutionEligibility);

public sealed class LoginAuthenticationService(IIdentityProvider identities, IUserRepository users,
    IAliasCredentialRepository credentials, AliasPasswordService passwords, CanonicalUserResolver resolver,
    LoginProtectionService protection, IServerModeResolver serverMode, ILogger<LoginAuthenticationService> logger)
{
    public async Task<AuthenticatedLogin> AuthenticateAsync(string identifier, string password, IPAddress? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 256 || identifier.Any(char.IsControl)
            || !AliasPasswordService.ValidInput(password)) throw new AliasAuthenticationException(400, "invalid-input");
        if (serverMode.Mode == ServerMode.User)
            return await AuthenticateUserModeAsync(identifier, password, ip, ct);
        await CheckAsync(identifier, ip, ct);
        var alias = credentials.FindAlias(identifier);
        var system = identities.Lookup(identifier);
        if (system.Status == IdentityLookupStatus.Unavailable) throw Unavailable("system-identity-lookup");
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
                var bound = resolver.RequireBinding(user, true);
                return new(user, "alias", alias.Revision, version, key,
                    UserExecutionEligibilityRules.Evaluate(bound, serverMode.Mode));
            }
            if (system.Identity is null) { passwords.Dummy(password); throw Invalid(); }
            if (policy?.SystemLoginEnabled == false || user?.IdentityReviewRequired == true) { passwords.Dummy(password); throw Invalid(); }
            var verified = identities.Verify(identifier, password);
            if (verified.Error == CredentialError.Unknown) throw Unavailable("system-credential-verification");
            if (!verified.Success || verified.Identity is not { } trusted || trusted.Uid != system.Identity.Uid || trusted.Platform != system.Identity.Platform)
                throw Invalid();
            user = resolver.ResolveSystem(trusted);
            return new(user, "system", policy?.Revision ?? 0, version, user.Id.ToString("D"),
                UserExecutionEligibilityRules.Evaluate(trusted, serverMode.Mode));
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
            || (policy?.Revision ?? 0) != login.Revision
            || serverMode.Mode != ServerMode.User && login.Method == "system" && policy?.SystemLoginEnabled == false)
            throw Invalid();
    }

    /// <summary>User Mode has exactly one login identity: the effective Unix account running
    /// this Server.  Do not inspect aliases or the per-user system-login toggle here: either
    /// could make a second local credential authority part of this deployment.</summary>
    private async Task<AuthenticatedLogin> AuthenticateUserModeAsync(string identifier, string password, IPAddress? ip, CancellationToken ct)
    {
        await CheckAsync(identifier, ip, ct);
        var system = identities.Lookup(identifier);
        if (system.Status == IdentityLookupStatus.Unavailable)
            throw Unavailable("user-mode-identity-lookup");
        if (system.Identity is null)
        {
            passwords.Dummy(password);
            throw Invalid();
        }

        var existing = users.FindByIdentity(system.Identity.Uid, system.Identity.Platform);
        var key = existing?.Id.ToString("D") ?? identifier;
        if (existing is not null) await CheckAsync(key, ip, ct, existing.Id);
        try
        {
            var verified = identities.Verify(identifier, password);
            if (verified.Error == CredentialError.Unknown)
                throw Unavailable("user-mode-credential-verification");
            if (!verified.Success || verified.Identity is not { } trusted
                || trusted.Uid != system.Identity.Uid || trusted.Platform != system.Identity.Platform)
                throw Invalid();
            var user = resolver.ResolveSystem(trusted);
            var policy = credentials.Find(user.Id);
            return new(user, "system", policy?.Revision ?? 0, user.SecurityVersion, user.Id.ToString("D"),
                UserExecutionEligibilityRules.Evaluate(trusted, serverMode.Mode));
        }
        catch (AliasAuthenticationException exception) when (exception.Status == 401)
        {
            await protection.RecordFailureAsync(key, ip, ct, existing?.Id);
            throw;
        }
    }

    private async Task CheckAsync(string key, IPAddress? ip, CancellationToken ct, Guid? canonicalUserId = null)
    {
        if ((await protection.CheckAsync(key, ip, ct, canonicalUserId)).IsBlocked) throw new AliasAuthenticationException(429, "login-rate-limited");
    }
    private AliasAuthenticationException Unavailable(string stage)
    {
        logger.LogError("Authentication backend is unavailable. Stage={Stage} ServerMode={ServerMode}", stage, serverMode.Mode);
        return new AliasAuthenticationException(503, "authentication-unavailable");
    }
    private static AliasAuthenticationException Invalid() => new(401, "invalid-credential");
}
