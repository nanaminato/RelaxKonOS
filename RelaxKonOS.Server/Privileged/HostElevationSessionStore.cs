using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Privileged;

/// <summary>In-memory five-minute grants, bound to one access-token jti, subject, capability and canonical target.</summary>
public sealed class HostElevationSessionStore : IHostElevationSessionStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, ElevationGrant> _grants = new(StringComparer.Ordinal);

    public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target)
    {
        PruneExpired();
        if (!TryIdentity(principal, out var tokenId, out var subject)) return false;
        var canonical = CanonicalTarget(capability, target);
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
        if (!TryIdentity(principal, out var tokenId, out var subject))
            throw new InvalidOperationException("The access token has no id or subject.");
        // Service, package, certificate and network capabilities are always exact-resource
        // grants.  Only explicitly file-scoped capabilities can cover descendants.
        if (!IsFileCapability(capability) && includeDescendants)
            throw new ArgumentException("Only file capabilities may include descendants.", nameof(includeDescendants));
        var expiresAt = DateTimeOffset.UtcNow.Add(Lifetime);
        var canonical = CanonicalTarget(capability, target);
        _grants[Key(tokenId, capability, canonical)] = new ElevationGrant(subject, capability, canonical, includeDescendants,
            expiresAt, authenticationMethod, correlationId);
        return expiresAt;
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        if (!TryIdentity(principal, out var tokenId, out _)) return;
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
    private sealed record ElevationGrant(string Subject, HostElevationCapability Capability, string Target, bool IncludeDescendants,
        DateTimeOffset ExpiresAt, string AuthenticationMethod, string? CorrelationId);
}
