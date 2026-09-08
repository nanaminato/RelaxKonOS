using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.Common;
using RemoteOS.Protocol.Registry;
using Server.ConfigurationRegistry;
using Server.Storage;
using Server.Settings;
using RemoteOS.Protocol.Workspace;

namespace Server.Endpoints;

/// <summary>Read-only first-stage API for the schema-approved configuration registry.</summary>
public static partial class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(RegistryApiRoutes.Entries, (string? scope, string? path, ClaimsPrincipal principal, IRegistryRepository registry, IWorkspaceRepository workspaces) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            RegistryScope? parsed = null;
            if (!string.IsNullOrWhiteSpace(scope) && (!Enum.TryParse<RegistryScope>(scope, true, out var requestedScope) || !Enum.IsDefined(requestedScope)))
                return Results.BadRequest(new { message = "Scope must be user, workspace, or device." });
            if (!string.IsNullOrWhiteSpace(scope)) parsed = Enum.Parse<RegistryScope>(scope, true);
            if (parsed is null || string.IsNullOrWhiteSpace(path) || !PathPattern().IsMatch(path) || !TryScopeId(principal, userId, parsed.Value, out var scopeId))
                return Results.BadRequest(new { message = "Scope and a valid key path are required." });
            EnsureWorkspaceDefaults(parsed.Value, userId, scopeId, workspaces, registry);
            return Results.Ok(registry.List(userId, parsed).Where(x => x.ScopeId == scopeId && x.Path == path).Select(ToDto));
        }).RequireAuthorization().WithTags("Registry");

        app.MapGet(RegistryApiRoutes.Keys, (string? scope, string? path, ClaimsPrincipal principal, IRegistryRepository registry, IWorkspaceRepository workspaces) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (!Enum.TryParse<RegistryScope>(scope, true, out var parsed) || !Enum.IsDefined(parsed)
                || string.IsNullOrWhiteSpace(path) || !PathPattern().IsMatch(path)
                || !TryScopeId(principal, userId, parsed, out var scopeId))
                return Results.BadRequest(new { message = "Scope and a valid parent key path are required." });
            EnsureWorkspaceDefaults(parsed, userId, scopeId, workspaces, registry);
            return Results.Ok(registry.ListChildKeys(userId, parsed, scopeId, path).Select(x => new RegistryKeyDto(x.Scope, x.Path)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapPost(RegistryApiRoutes.Keys, (CreateRegistryKeyRequest request, ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (request.Scope != RegistryScope.Workspace || !TryScopeId(principal, userId, request.Scope, out var scopeId)
                || !IsWorkspaceChildPath(request.Path))
                return Results.BadRequest(new { message = "Only keys below Workspace can be created." });
            var saved = registry.CreateKey(new Server.Domain.RegistryKey
            {
                UserId = userId, Scope = request.Scope, ScopeId = scopeId, Path = request.Path.Trim(),
                CreatedAt = DateTimeOffset.UtcNow, CreatedBy = userId.ToString("D"),
            });
            return Results.Ok(new RegistryKeyDto(saved.Scope, saved.Path));
        }).RequireAuthorization().WithTags("Registry");

        app.MapGet(RegistryApiRoutes.Summary, (ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            var entries = registry.List(userId);
            return Results.Ok(new RegistrySummaryDto(
                entries.Count(x => x.State is RegistryEntryState.PendingSync or RegistryEntryState.Applying),
                entries.Count(x => x.State == RegistryEntryState.Failed),
                entries.Count(x => x.State == RegistryEntryState.RestartRequired)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapPut(RegistryApiRoutes.Entries, (PutRegistryEntryRequest request, ClaimsPrincipal principal, IRegistryRepository registry, IWorkspaceRepository workspaces) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (!TryScopeId(principal, userId, request.Scope, out var scopeId)
                || !PathPattern().IsMatch(request.Path) || !(request.Name == "(Default)" || NamePattern().IsMatch(request.Name))
                || !IsCompatible(request.ValueType, request.Value))
                return Results.BadRequest(new { message = "Invalid registry value." });
            var now = DateTimeOffset.UtcNow;
            if (!VerifyWorkspaceOwner(request.Scope, userId, scopeId, workspaces, out var ownershipError))
                return Results.BadRequest(new { message = ownershipError });
            if (request.ExpectedRevision is null) return Results.Problem(statusCode: 428, title: "settings.revision_required");
            if (request.ExpectedRevision < 0) return Results.BadRequest();
            var valueJson = request.Value.GetRawText();
            if (request.Scope == RegistryScope.Workspace && request.Path == WorkspaceConfigurationRegistry.DesktopPath
                && request.Name == WorkspaceConfigurationRegistry.DefaultValueName)
            {
                if (request.ValueType != RegistryValueType.Json) return Results.BadRequest();
                WorkspacePreferencesDto? preferences;
                try { preferences = request.Value.Deserialize<WorkspacePreferencesDto>(RemoteOsJsonOptions.Default); }
                catch (JsonException) { return Results.BadRequest(); }
                if (preferences is null || !WorkspacePreferencesValidator.TryNormalize(preferences, out var normalized))
                    return Results.BadRequest(new { message = "Invalid workspace preferences." });
                valueJson = JsonSerializer.Serialize(normalized with { Revision = null }, RemoteOsJsonOptions.Default);
            }
            var saved = registry.CompareExchange(new Server.Domain.RegistryEntry
            {
                UserId = userId, Scope = request.Scope, ScopeId = scopeId, Path = request.Path.Trim(), Name = request.Name.Trim(),
                ValueType = request.ValueType, ValueJson = valueJson, State = RegistryEntryState.Synced,
                DesiredUpdatedAt = now, DesiredUpdatedBy = userId.ToString("D"), AppliedRevision = 1, AppliedAt = now,
            }, request.ExpectedRevision.Value);
            if (saved is null) return Results.Problem(statusCode: 409, title: "settings.revision_conflict");
            return Results.Ok(ToDto(saved));
        }).RequireAuthorization().WithTags("Registry");

        app.MapDelete(RegistryApiRoutes.Entries, (RegistryScope scope, string path, string name, ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (!TryScopeId(principal, userId, scope, out var scopeId) || !PathPattern().IsMatch(path) || !(name == "(Default)" || NamePattern().IsMatch(name)))
                return Results.BadRequest(new { message = "Invalid registry value." });
            if (scope == RegistryScope.Workspace && path == WorkspaceConfigurationRegistry.DesktopPath
                && name == WorkspaceConfigurationRegistry.DefaultValueName)
                return Results.Problem(statusCode: 409, title: "settings.managed_value", detail: "Reset preferences through a versioned settings update.");
            return registry.Delete(userId, scope, scopeId, path, name) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithTags("Registry");

        app.MapDelete(RegistryApiRoutes.Keys, (RegistryScope scope, string path, ClaimsPrincipal principal, IRegistryRepository registry) =>
        {
            if (!TryUserId(principal, out var userId)) return Results.Unauthorized();
            if (scope != RegistryScope.Workspace || !TryScopeId(principal, userId, scope, out var scopeId) || !IsWorkspaceChildPath(path))
                return Results.BadRequest(new { message = "Only keys below Workspace can be deleted." });
            if (path == WorkspaceConfigurationRegistry.DesktopPath
                || WorkspaceConfigurationRegistry.DesktopPath.StartsWith(path + "\\", StringComparison.Ordinal))
                return Results.Problem(statusCode: 409, title: "settings.managed_value", detail: "This key contains managed workspace preferences.");
            return registry.DeleteKeyTree(userId, scope, scopeId, path) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithTags("Registry");
        return app;
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId) => Guid.TryParse(
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryScopeId(ClaimsPrincipal principal, Guid userId, RegistryScope scope, out Guid scopeId)
    {
        scopeId = scope switch
        {
            RegistryScope.User => userId,
            RegistryScope.Workspace when Guid.TryParse(principal.FindFirstValue("workspace_id"), out var workspaceId) => workspaceId,
            RegistryScope.Device when Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId) => deviceId,
            _ => Guid.Empty,
        };
        return scopeId != Guid.Empty;
    }

    private static bool IsCompatible(RegistryValueType type, JsonElement value) => type switch
    {
        RegistryValueType.String => value.ValueKind == JsonValueKind.String,
        RegistryValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        RegistryValueType.Number => value.ValueKind == JsonValueKind.Number,
        RegistryValueType.Json => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null,
        _ => false,
    };

    private static bool IsWorkspaceChildPath(string? path) => path is not null
        && PathPattern().IsMatch(path)
        && path.StartsWith("Workspace\\", StringComparison.Ordinal)
        && path.Length > "Workspace\\".Length;

    private static bool VerifyWorkspaceOwner(RegistryScope scope, Guid userId, Guid scopeId, IWorkspaceRepository workspaces, out string? error)
    {
        error = null;
        if (scope != RegistryScope.Workspace) return true;
        var workspace = workspaces.FindById(scopeId);
        if (workspace is null || workspace.UserId != userId) { error = "Workspace was not found."; return false; }
        return true;
    }

    private static void EnsureWorkspaceDefaults(RegistryScope scope, Guid userId, Guid scopeId, IWorkspaceRepository workspaces, IRegistryRepository registry)
    {
        if (scope == RegistryScope.Workspace && workspaces.FindById(scopeId) is { UserId: var owner } workspace && owner == userId)
            WorkspaceConfigurationRegistry.EnsureDefaults(registry, workspace, userId.ToString("D"));
    }

    private static RegistryEntryDto ToDto(Server.Domain.RegistryEntry entry)
    {
        using var document = JsonDocument.Parse(entry.ValueJson);
        var schema = RegistrySchema.Find(entry.Scope, entry.Path, entry.Name);
        return new RegistryEntryDto(entry.Scope, entry.Path, entry.Name, entry.ValueType, document.RootElement.Clone(), entry.Revision,
            entry.State, entry.DesiredUpdatedAt, entry.AppliedAt, schema?.ApplyMode ?? RegistryApplyMode.Immediate, schema?.RestartTarget, entry.LastErrorCode, entry.LastErrorMessage);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._\\\\-]{0,63}(\\\\[A-Za-z0-9][A-Za-z0-9 ._\\\\-]{0,63}){0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
