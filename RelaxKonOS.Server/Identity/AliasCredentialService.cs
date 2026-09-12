using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Identity;

public sealed class AliasCredentialService(IUserRepository users, IAliasCredentialRepository credentials,
    IIdentityProvider identities, CanonicalUserResolver resolver, AliasPasswordService passwords,
    AuthSessionStore sessions, LoginProtectionService protection, SessionValidityService validity)
{
    public User RequireUser(ClaimsPrincipal principal)
    {
        if (!validity.IsValid(principal)) throw new AliasAuthenticationException(401, "invalid-credential");
        return users.FindById(Guid.Parse(principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)!.Value))!;
    }
    public AliasConfigurationDto Read(User user)
    {
        var policy = credentials.Find(user.Id);
        var capability = new AliasEligibility(false, "identity-review-required");
        if (!user.IdentityReviewRequired)
        {
            try { capability = identities.CheckAliasEligibility(resolver.RequireBinding(user, false)); }
            catch (AliasAuthenticationException) { capability = new(false, "identity-unavailable"); }
        }
        if (!credentials.IsPersistent) capability = new(false, "persistent-storage-required");
        return new(user.Id, user.Username, policy?.Alias, policy?.SystemLoginEnabled ?? true, policy?.Revision ?? 0,
            policy?.UpdatedAt, policy?.PasswordChangedAt, capability.Available, capability.Reason);
    }

    public async Task<AliasConfigurationDto> ChangeAsync(User user, ClaimsPrincipal principal, object request, HttpContext http, CancellationToken ct)
    {
        if (!credentials.IsPersistent) throw new AliasAuthenticationException(409, "alias-unavailable");
        var policy = credentials.Find(user.Id) ?? new AliasCredential { UserId = user.Id, CreatedAt = DateTimeOffset.UtcNow };
        var revision = request switch { CreateAliasRequest r => r.ExpectedRevision, RenameAliasRequest r => r.ExpectedRevision,
            ChangeAliasPasswordRequest r => r.ExpectedRevision, DeleteAliasRequest r => r.ExpectedRevision,
            SetSystemLoginRequest r => r.ExpectedRevision, _ => -1 };
        if (revision < 0) throw new AliasAuthenticationException(400, "invalid-input");
        if (policy.Revision != revision) throw new AliasAuthenticationException(409, "alias-revision-conflict");
        var key = user.Id.ToString("D");
        if ((await protection.CheckAsync(key, http.Connection.RemoteIpAddress, ct, user.Id)).IsBlocked)
            throw new AliasAuthenticationException(429, "login-rate-limited");
        var identity = resolver.RequireBinding(user, false);
        var oldVersion = user.SecurityVersion;
        var revoke = false;
        string eventType;
        try
        {
            switch (request)
            {
                case CreateAliasRequest r:
                    if (policy.Alias is not null) throw new AliasAuthenticationException(409, "alias-already-configured");
                    if (!policy.SystemLoginEnabled || principal.FindFirst("amr")?.Value != "system") throw ReauthFailed();
                    VerifySystem(user, r.CurrentSystemPassword);
                    RequireEligible(identity);
                    RequireAvailable(r.Alias);
                    policy.Alias = r.Alias;
                    policy.PasswordHash = passwords.Hash(policy, r.AliasPassword);
                    policy.PasswordChangedAt = DateTimeOffset.UtcNow;
                    eventType = "AliasCreated";
                    break;
                case RenameAliasRequest r:
                    RequireAlias(policy); Verify(user, policy, r.Reauthentication);
                    RequireEligible(identity); RequireAvailable(r.Alias, user.Id);
                    policy.Alias = r.Alias; eventType = "AliasChanged";
                    break;
                case ChangeAliasPasswordRequest r:
                    RequireAlias(policy); Verify(user, policy, r.Reauthentication);
                    RequireEligible(identity);
                    policy.PasswordHash = passwords.Hash(policy, r.NewPassword);
                    policy.PasswordChangedAt = DateTimeOffset.UtcNow;
                    revoke = true; eventType = "AliasPasswordChanged";
                    break;
                case DeleteAliasRequest r:
                    RequireAlias(policy); VerifySystem(user, r.CurrentSystemPassword);
                    policy.Alias = null; policy.PasswordHash = null; policy.PasswordChangedAt = null;
                    policy.SystemLoginEnabled = true; revoke = true; eventType = "AliasDeleted";
                    break;
                case SetSystemLoginRequest r:
                    if (r.Enabled) VerifySystem(user, r.Password);
                    else { RequireAlias(policy); VerifyAlias(policy, r.Password); RequireEligible(identity); }
                    policy.SystemLoginEnabled = r.Enabled;
                    eventType = r.Enabled ? "SystemLoginEnabled" : "SystemLoginDisabled";
                    break;
                default: throw new AliasAuthenticationException(400, "invalid-input");
            }
        }
        catch (AliasAuthenticationException exception) when (exception.Status == 403)
        { await protection.RecordFailureAsync(key, http.Connection.RemoteIpAddress, ct, user.Id); throw; }
        if (!validity.IsValid(principal)) throw new AliasAuthenticationException(401, "invalid-credential");
        if (revoke) user.SecurityVersion = checked(oldVersion + 1);
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        var audit = new AuthenticationSecurityEvent { Id = Guid.NewGuid(), CanonicalUserId = user.Id,
            SessionId = Guid.TryParse(principal.FindFirst("sid")?.Value, out var sid) ? sid : null,
            AuthenticationMethod = principal.FindFirst("amr")?.Value, EventType = eventType, ReasonCode = "user-request",
            SourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown", CorrelationId = http.TraceIdentifier, CreatedAt = policy.UpdatedAt };
        if (!credentials.Commit(policy, revision, user, oldVersion, audit)) throw new AliasAuthenticationException(409, "alias-revision-conflict");
        if (revoke) sessions.RevokeUser(user.Id);
        return Read(user);
    }

    private void Verify(User user, AliasCredential policy, AliasReauthentication? reauth)
    {
        if (reauth?.Method == "alias") VerifyAlias(policy, reauth.Password);
        else if (reauth?.Method == "system" && policy.SystemLoginEnabled) VerifySystem(user, reauth.Password);
        else throw ReauthFailed();
    }
    private void VerifySystem(User user, string password)
    {
        if (!AliasPasswordService.ValidInput(password)) throw ReauthFailed();
        var result = identities.Verify(user.Username, password);
        if (!result.Success || result.Identity?.Uid != user.PlatformIdentity || result.Identity.Platform != user.Platform) throw ReauthFailed();
    }
    private void VerifyAlias(AliasCredential policy, string password)
    { if (passwords.Verify(policy, password) == PasswordVerificationResult.Failed) throw ReauthFailed(); }
    private static void RequireAlias(AliasCredential policy)
    { if (policy.Alias is null) throw new AliasAuthenticationException(409, "alias-not-configured"); }
    private void RequireEligible(PlatformUserInfo identity)
    { if (!identities.CheckAliasEligibility(identity).Available) throw new AliasAuthenticationException(409, "alias-unavailable"); }
    private void RequireAvailable(string alias, Guid? owner = null)
    {
        if (!AliasPasswordService.ValidAlias(alias)) throw new AliasAuthenticationException(400, "invalid-input");
        var lookup = identities.Lookup(alias);
        if (lookup.Status == IdentityLookupStatus.Unavailable) throw new AliasAuthenticationException(503, "authentication-unavailable");
        var existing = credentials.FindAlias(alias);
        if (lookup.Status != IdentityLookupStatus.NotFound || existing is not null && existing.UserId != owner)
            throw new AliasAuthenticationException(409, "alias-unavailable");
    }
    private static AliasAuthenticationException ReauthFailed() => new(403, "reauthentication-failed");
}
