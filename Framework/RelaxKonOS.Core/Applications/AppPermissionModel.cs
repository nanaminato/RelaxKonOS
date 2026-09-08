namespace RelaxKonOS.Core.Applications;

/// <summary>Host-assigned origin label. It is product metadata, not a security boundary.</summary>
public enum AppTrustLevel
{
    BuiltIn,
    Development,
    ThirdParty,
    Trusted,
}

/// <summary>Stable application identity plus host-owned, user-visible installation metadata.</summary>
public sealed record AppIdentity(AppId AppId, AppTrustLevel TrustLevel, string InstallSource);

/// <summary>A capability target. Version 2 currently standardizes no scope and normalized paths.</summary>
public sealed record PermissionScope(string Kind, string? Value = null)
{
    public static PermissionScope None { get; } = new("none");

    public static PermissionScope Path(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A path scope requires a path.", nameof(path));
        return new PermissionScope("path", System.IO.Path.GetFullPath(path));
    }

    public bool Matches(PermissionScope requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!string.Equals(Kind, requested.Kind, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(Kind, "none", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(Kind, "path", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Value) || string.IsNullOrWhiteSpace(requested.Value)) return false;

        var root = System.IO.Path.GetFullPath(Value);
        var target = System.IO.Path.GetFullPath(requested.Value);
        if (!string.Equals(root, System.IO.Path.GetPathRoot(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            root = System.IO.Path.TrimEndingDirectorySeparator(root);
        if (!string.Equals(target, System.IO.Path.GetPathRoot(target), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            target = System.IO.Path.TrimEndingDirectorySeparator(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var boundary = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            || root.EndsWith(System.IO.Path.AltDirectorySeparatorChar)
            ? root : root + System.IO.Path.DirectorySeparatorChar;
        return target.Equals(root, comparison)
            || target.StartsWith(boundary, comparison);
    }
}

public enum GrantSource
{
    SystemDefault,
    User,
    Temporary,
    ExplicitDeny,
}

/// <summary>A host-owned local grant. Expired temporary grants are never effective.</summary>
public sealed record PermissionGrant(
    AppId AppId,
    string Capability,
    PermissionScope Scope,
    GrantSource Source,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsActive(DateTimeOffset now) => Source != GrantSource.Temporary || ExpiresAt is null || ExpiresAt > now;
}

public enum PermissionDecision
{
    Deny,
    Prompt,
    Allow,
}

/// <summary>Host policy defaults. Policies never come from a package manifest.</summary>
public interface IAppPolicyProvider
{
    PermissionDecision GetDefaultDecision(AppIdentity identity, string capability, PermissionScope scope);
}

/// <summary>Local v2 grant persistence abstraction, deliberately independent of package metadata.</summary>
public interface IAppPermissionStore
{
    IReadOnlyList<PermissionGrant> Get(AppId appId, string capability);
    void Replace(AppId appId, string capability, IReadOnlyList<PermissionGrant> grants);
    void Clear(AppId appId);
}

public interface IPermissionEvaluator
{
    PermissionDecision Evaluate(AppIdentity identity, ApplicationManifest manifest, string capability, PermissionScope? scope = null);
}

/// <summary>Pure, deterministic v2 capability precedence evaluator.</summary>
public sealed class AppPermissionEvaluator(IAppPolicyProvider policies, IAppPermissionStore store, TimeProvider? clock = null) : IPermissionEvaluator
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public PermissionDecision Evaluate(AppIdentity identity, ApplicationManifest manifest, string capability, PermissionScope? scope = null)
    {
        var effectiveScope = scope ?? PermissionScope.None;
        if (!AppPermissions.IsKnown(capability)
            || identity.AppId != manifest.Id
            || !manifest.Permissions.Contains(capability, StringComparer.Ordinal))
            return PermissionDecision.Deny;

        var now = _clock.GetUtcNow();
        var grants = store.Get(identity.AppId, capability).Where(grant => grant.IsActive(now)).ToArray();
        if (grants.Any(grant => grant.Source == GrantSource.ExplicitDeny && grant.Scope.Matches(effectiveScope)))
            return PermissionDecision.Deny;
        if (grants.Any(grant => (grant.Source == GrantSource.User || grant.Source == GrantSource.Temporary)
                                && grant.Scope.Matches(effectiveScope)))
            return PermissionDecision.Allow;

        return policies.GetDefaultDecision(identity, capability, effectiveScope);
    }
}
