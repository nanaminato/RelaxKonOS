using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Process-wide grant state shared by request-scoped authorization services.</summary>
public sealed class HostElevationSessionState : IDisposable
{
    private readonly RelaxKonOS.Server.Settings.ServerRuntimeSettingsStore? _settings;
    private readonly RelaxKonOS.Server.Identity.SessionValidityService? _validity;
    private readonly object _gate = new();
    private readonly Dictionary<string, RememberedAdministrator> _administrators = new(StringComparer.Ordinal);
    private bool _enabled;
    public HostElevationSessionState(RelaxKonOS.Server.Settings.ServerRuntimeSettingsStore? settings = null,
        RelaxKonOS.Server.Identity.SessionValidityService? validity = null)
    {
        _settings = settings;
        _validity = validity;
        _enabled = settings?.Current.Settings.SessionValid ?? true;
        if (settings is not null) settings.Changed += PolicyChanged;
    }
    private void PolicyChanged()
    {
        lock (_gate)
        {
            var enabled = _settings!.Current.Settings.SessionValid;
            if (_enabled != enabled) _administrators.Clear();
            _enabled = enabled;
        }
    }
    public bool IsAdministratorVerified(ClaimsPrincipal principal)
    {
        var token = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return token is { Length: > 0 } && subject is { Length: > 0 } && IsSessionValid(principal)
            && IsRemembered(principal, token, subject);
    }
    internal bool IsSessionValid(ClaimsPrincipal principal) => _validity?.IsLiveSession(principal) ?? true;
    internal DateTimeOffset? Remember(ClaimsPrincipal principal, string tokenId, string subject, string authenticationMethod)
    {
        // Only successful platform administrator verification can create broad token authority.
        if (authenticationMethod is not ("windows-logonuser-administrators" or "linux-pam-administrator" or "windows-owner-device")
            || !TrySession(principal, out var session, out var version, out var expiry)) return null;
        lock (_gate)
        {
            if (!_enabled) return null;
            Prune();
            _administrators[tokenId] = new(subject, session, version, expiry);
            return expiry;
        }
    }
    internal bool IsRemembered(ClaimsPrincipal principal, string tokenId, string subject)
    {
        if (!TrySession(principal, out var session, out var version, out var expiry)) return false;
        lock (_gate)
        {
            Prune();
            return _enabled && _administrators.TryGetValue(tokenId, out var known)
                && known == new RememberedAdministrator(subject, session, version, expiry);
        }
    }
    internal void Forget(string tokenId) { lock (_gate) _administrators.Remove(tokenId); }
    private void Prune()
    {
        foreach (var key in _administrators.Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
            _administrators.Remove(key);
    }
    private static bool TrySession(ClaimsPrincipal principal, out string session, out string version, out DateTimeOffset expiry)
    {
        session = principal.FindFirstValue("sid") ?? "";
        version = principal.FindFirstValue("security_version") ?? "";
        expiry = default;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(session, out _)
            || !long.TryParse(version, out var securityVersion) || securityVersion < 0
            || principal.FindFirstValue("amr") is not ("system" or "alias" or "owner-device-key")
            || !long.TryParse(principal.FindFirstValue("exp"), out var seconds)
            || seconds < 0 || seconds > 253402300799) return false;
        expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return expiry > DateTimeOffset.UtcNow;
    }
    public void Dispose() { if (_settings is not null) _settings.Changed -= PolicyChanged; }
    private sealed record RememberedAdministrator(string Subject, string Session, string SecurityVersion, DateTimeOffset ExpiresAt);

    internal ConcurrentDictionary<string, ElevationGrant> Grants { get; } = new(StringComparer.Ordinal);

    internal sealed record ElevationGrant(string Subject, HostElevationCapability Capability, string Target, bool IncludeDescendants,
        DateTimeOffset ExpiresAt, string AuthenticationMethod, string? CorrelationId);
}

/// <summary>Revalidates automatic host authorization for each non-file operation, otherwise
/// checks verified administrator memory for this access token or five-minute capability/target grants.</summary>
public sealed class HostElevationSessionStore(IHostAccountPrivilegeService privileges,
    RelaxKonOS.Server.HostMode.IServerModeResolver mode, HostElevationSessionState state) : IHostElevationSessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, HostElevationSessionState.ElevationGrant> _grants = state.Grants;

    public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target)
    {
        if (!mode.Supports(RelaxKonOS.Server.HostMode.ServerHostFeature.PrivilegedOperations) || !Enum.IsDefined(capability)) return false;
        if (!state.IsSessionValid(principal))
        {
            Revoke(principal);
            return false;
        }
        PruneExpired();
        if (!TryIdentity(principal, out var tokenId, out var subject)) return false;
        var canonical = CanonicalTarget(capability, target);
        if (state.IsRemembered(principal, tokenId, subject)) return true;
        // Automatic authorization is recomputed for every operation and is never cached as a
        // five-minute grant. File routing keeps its separate authorization-source/root policy.
        if (!IsFileCapability(capability) && privileges.Classify(principal)
            is HostAccountPrivilege.HostAdministrator or HostAccountPrivilege.HostRoot) return true;
        return _grants.Any(pair => pair.Value.ExpiresAt > DateTimeOffset.UtcNow
            && pair.Value.Capability == capability
            && string.Equals(pair.Value.Subject, subject, StringComparison.Ordinal)
            && pair.Key.StartsWith(tokenId + "\n", StringComparison.Ordinal)
            && (string.Equals(canonical, pair.Value.Target, PathComparison)
                || pair.Value.IncludeDescendants && canonical.StartsWith(EnsureTrailingSeparator(pair.Value.Target), PathComparison)));
    }

    public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target,
        bool includeDescendants, string authenticationMethod, string? correlationId = null)
    {
        if (!mode.Supports(RelaxKonOS.Server.HostMode.ServerHostFeature.PrivilegedOperations))
            throw new InvalidOperationException("User Mode does not provide host elevation grants.");
        if (!state.IsSessionValid(principal) || !TryIdentity(principal, out var tokenId, out var subject))
            throw new InvalidOperationException("The access token or login session is no longer valid.");
        // Service, package, certificate and network capabilities are always exact-resource
        // grants.  Only explicitly file-scoped capabilities can cover descendants.
        if (!IsFileCapability(capability) && includeDescendants)
            throw new ArgumentException("Only file capabilities may include descendants.", nameof(includeDescendants));
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        var canonical = CanonicalTarget(capability, target);
        _grants[Key(tokenId, capability, canonical)] = new HostElevationSessionState.ElevationGrant(subject, capability, canonical, includeDescendants,
            expiresAt, authenticationMethod, correlationId);
        return state.Remember(principal, tokenId, subject, authenticationMethod) ?? expiresAt;
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        if (!TryIdentity(principal, out var tokenId, out _)) return;
        state.Forget(tokenId);
        var prefix = tokenId + "\n";
        foreach (var key in _grants.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            _grants.TryRemove(key, out _);
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _grants.Where(pair => pair.Value.ExpiresAt <= now))
            _grants.TryRemove(pair.Key, out _);
    }

    private static bool TryIdentity(ClaimsPrincipal principal, out string tokenId, out string subject)
    {
        tokenId = principal.FindFirstValue(JwtRegisteredClaimNames.Jti) ?? string.Empty;
        subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        return tokenId.Length > 0 && subject.Length > 0;
    }

    private static string CanonicalTarget(HostElevationCapability capability, string target)
    {
        if (!IsFileCapability(capability))
        {
            if (string.IsNullOrWhiteSpace(target) || target.Length > 256) throw new ArgumentException("Invalid privileged resource target.", nameof(target));
            return target.Trim();
        }
        var fullPath = Path.GetFullPath(target);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, PathComparison) ? fullPath : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsFileCapability(HostElevationCapability capability) => capability is >= HostElevationCapability.FileRead and <= HostElevationCapability.FileUpload;

    private static string Key(string tokenId, HostElevationCapability capability, string target) => $"{tokenId}\n{capability}\n{target}";
    private static string EnsureTrailingSeparator(string path) => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
