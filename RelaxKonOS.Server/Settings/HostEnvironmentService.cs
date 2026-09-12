using System.Security.Claims;
using System.Security.Principal;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Settings;

public interface IHostEnvironmentService
{
    SettingsTarget ResolveTarget(ClaimsPrincipal principal, SettingsScope scope);
    void RequireGrant(ClaimsPrincipal principal, SettingsTarget target, HostElevationCapability capability);
    Task<HostEnvironmentSnapshot> ReadAsync(ClaimsPrincipal principal, SettingsScope scope, bool reveal, CancellationToken ct);
    Task<PrivilegedEnvironmentState> ReadRawAsync(ClaimsPrincipal principal, SettingsTarget target, CancellationToken ct);
    Task<PrivilegedOperationResult> ApplyAsync(ClaimsPrincipal principal, SettingsTarget target, EnvironmentChangeSet change,
        string revision, Guid operationId, CancellationToken ct);
}

/// <summary>Host account mapping and field-safe projection; raw IPC data never becomes an HTTP response.</summary>
public sealed class HostEnvironmentService(IUserRepository users, IHostElevationSessionStore grants,
    IPrivilegedOperationTransport transport, RelaxKonOS.Server.Identity.IIdentityProvider identities) : IHostEnvironmentService
{
    public SettingsTarget ResolveTarget(ClaimsPrincipal principal, SettingsScope scope)
    {
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(subject, out var id))
            throw new SettingsException(401, "settings.unauthenticated");
        var user = users.FindById(id) ?? throw new SettingsException(401, "settings.user_mapping_missing");
        if (scope is not (SettingsScope.HostMachine or SettingsScope.HostUser)) throw new SettingsException(400, "settings.environment.invalid_scope");
        var platformIdentity = user.PlatformIdentity;
        if (OperatingSystem.IsWindows())
        {
            if (user.Platform != PlatformKind.Windows) throw new SettingsException(403, "settings.environment.identity_mismatch");
            try
            {
                var mappedSid = (SecurityIdentifier)new NTAccount(user.Username).Translate(typeof(SecurityIdentifier));
                var storedSid = (SecurityIdentifier)new NTAccount(user.PlatformIdentity).Translate(typeof(SecurityIdentifier));
                if (!mappedSid.IsAccountSid() || mappedSid.Value != storedSid.Value)
                    throw new SettingsException(403, "settings.environment.identity_mismatch");
                platformIdentity = mappedSid.Value;
            }
            catch (IdentityNotMappedException) { throw new SettingsException(403, "settings.environment.identity_unmapped"); }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (user.Platform != PlatformKind.Linux || !uint.TryParse(user.PlatformIdentity, out _)
                || identities.GetUserInfo(user.Username).Uid != user.PlatformIdentity)
                throw new SettingsException(403, "settings.environment.identity_mismatch");
        }
        else throw new SettingsException(503, "settings.environment.platform_unsupported");
        return scope == SettingsScope.HostMachine ? new("host/environment/machine", scope)
            : new("host/environment/user/" + platformIdentity, scope, platformIdentity);
    }

    public void RequireGrant(ClaimsPrincipal principal, SettingsTarget target, HostElevationCapability capability)
    {
        if (ResolveTarget(principal, target.Scope) != target) throw new SettingsException(403, "settings.environment.identity_mismatch");
        if (!grants.IsGranted(principal, capability, target.ResourceId)) throw new SettingsException(428, "settings.environment.authorization_required");
    }

    public async Task<PrivilegedEnvironmentState> ReadRawAsync(ClaimsPrincipal principal, SettingsTarget target, CancellationToken ct)
    {
        RequireGrant(principal, target, HostElevationCapability.HostEnvironmentRead);
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.HostEnvironmentRead, EnvironmentTarget: target, OperationId: Guid.NewGuid()), ct);
        if (!result.Success || result.HostEnvironment is not { } state)
            throw new SettingsException(503, "settings.environment.helper_" + result.ProblemCode.ToString().ToLowerInvariant());
        if (state.Target != target) throw new SettingsException(502, "settings.environment.target_mismatch");
        return state;
    }

    public async Task<HostEnvironmentSnapshot> ReadAsync(ClaimsPrincipal principal, SettingsScope scope, bool reveal, CancellationToken ct)
    {
        var target = ResolveTarget(principal, scope);
        if (reveal) RequireGrant(principal, target, HostElevationCapability.HostEnvironmentReveal);
        var state = await ReadRawAsync(principal, target, ct);
        var windows = OperatingSystem.IsWindows();
        var values = state.Values.ToDictionary(value => value.Name, value => value.Value,
            windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var projected = state.Values.Select(value =>
        {
            var expanded = EnvironmentExpansion.Expand(value.Value, values, windows);
            var sensitive = EnvironmentValidation.IsPotentiallySensitive(value.Name)
                || expanded.ReferencedNames.Any(EnvironmentValidation.IsPotentiallySensitive);
            // Default reads mask every value; name heuristics alone cannot identify all secrets.
            var masked = !reveal;
            var warnings = expanded.Warnings.Concat(value.Name.Equals("PATH", windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? EnvironmentExpansion.PathWarnings(value.Value, windows) : Array.Empty<string>()).Distinct().ToArray();
            return new EnvironmentVariable(value.Name, masked ? null : value.Value, masked ? null : expanded.Value,
                value.Kind, scope, sensitive, masked, warnings);
        }).ToArray();
        return new(target, state.Revision, DateTimeOffset.UtcNow, new(SettingsCapabilityState.Available),
            SettingsEffectiveState.NewProcess, state.Provider, !windows, windows ? ";" : ":", projected);
    }

    public Task<PrivilegedOperationResult> ApplyAsync(ClaimsPrincipal principal, SettingsTarget target, EnvironmentChangeSet change,
        string revision, Guid operationId, CancellationToken ct)
    {
        RequireGrant(principal, target, HostElevationCapability.HostEnvironmentChange);
        if (EnvironmentValidation.Validate(change, OperatingSystem.IsWindows()) is { } problem) throw new SettingsException(400, problem);
        return transport.ExecuteAsync(new(PrivilegedOperationKind.HostEnvironmentApply, EnvironmentTarget: target,
            EnvironmentChange: change, ExpectedRevision: revision, OperationId: operationId), ct);
    }
}
