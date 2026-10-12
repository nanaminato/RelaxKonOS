using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Privileged;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Editable, owner-aware API for the schema-approved configuration registry.</summary>
public static partial class RegistryEndpoints
{
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(RegistryApiRoutes.Values, (Guid userId, string? scope, string? path, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            RegistryScope? parsed = null;
            if (!string.IsNullOrWhiteSpace(scope) && (!Enum.TryParse<RegistryScope>(scope, true, out var requestedScope) || !Enum.IsDefined(requestedScope)))
                return Results.BadRequest(new { message = "Scope must be user, workspace, or device." });
            if (!string.IsNullOrWhiteSpace(scope)) parsed = Enum.Parse<RegistryScope>(scope, true);
            if (parsed is null || string.IsNullOrWhiteSpace(path) || !PathPattern().IsMatch(path) || !TryScopeId(principal, userId, workspaces, parsed.Value, out var scopeId))
                return Results.BadRequest(new { message = "Scope and a valid key path are required." });
            EnsureWorkspaceDefaults(parsed.Value, userId, scopeId, workspaces, registry);
            var direct = registry.List(userId, parsed).Where(x => x.ScopeId == scopeId && x.Path == path && RegistryConfigurationFields.Root(x.Path) is null).Select(ToDto);
            var root = parsed == RegistryScope.Workspace ? RegistryConfigurationFields.Root(path) : null;
            var backing = root is null ? null : registry.Find(userId, parsed.Value, scopeId, root, WorkspaceConfigurationRegistry.DefaultValueName);
            return Results.Ok((backing is null ? direct : direct.Concat(RegistryConfigurationFields.Values(backing, path))).Select(value => Describe(http, users, userId, scopeId, value)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapGet(RegistryApiRoutes.Keys, (Guid userId, string? scope, string? path, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            if (!Enum.TryParse<RegistryScope>(scope, true, out var parsed) || !Enum.IsDefined(parsed)
                || string.IsNullOrWhiteSpace(path) || !PathPattern().IsMatch(path)
                || !TryScopeId(principal, userId, workspaces, parsed, out var scopeId))
                return Results.BadRequest(new { message = "Scope and a valid parent key path are required." });
            EnsureWorkspaceDefaults(parsed, userId, scopeId, workspaces, registry);
            var direct = registry.ListChildKeys(userId, parsed, scopeId, path).Select(x => new RegistryKeyDto(x.Scope, x.Path));
            var root = parsed == RegistryScope.Workspace ? RegistryConfigurationFields.Root(path) : null;
            var backing = root is null ? null : registry.Find(userId, parsed, scopeId, root, WorkspaceConfigurationRegistry.DefaultValueName);
            return Results.Ok((backing is null ? direct : direct.Concat(RegistryConfigurationFields.Children(backing, path))).DistinctBy(key => key.Path).Select(key => Describe(http, users, userId, scopeId, key)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapPost(RegistryApiRoutes.Keys, (Guid userId, CreateRegistryKeyRequest request, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            if (request.Scope != RegistryScope.Workspace || !TryScopeId(principal, userId, workspaces, request.Scope, out var scopeId)
                || !IsWorkspaceChildPath(request.Path) || RegistryConfigurationFields.Root(request.Path) is not null)
                return Results.BadRequest(new { message = "Only keys below Workspace can be created." });
            var saved = registry.CreateKey(new RelaxKonOS.Server.Domain.RegistryKey
            {
                UserId = userId, Scope = request.Scope, ScopeId = scopeId, Path = request.Path.Trim(),
                CreatedAt = DateTimeOffset.UtcNow, CreatedBy = actorId.ToString("D"),
            });
            return Results.Ok(Describe(http, users, userId, scopeId, new RegistryKeyDto(saved.Scope, saved.Path)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapGet(RegistryApiRoutes.Summary, (Guid userId, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            var entries = registry.List(userId);
            return Results.Ok(new RegistrySummaryDto(
                entries.Count(x => x.State is RegistryEntryState.PendingSync or RegistryEntryState.Applying),
                entries.Count(x => x.State == RegistryEntryState.Failed),
                entries.Count(x => x.State == RegistryEntryState.RestartRequired)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapPut(RegistryApiRoutes.Values, (Guid userId, PutRegistryEntryRequest request, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            if (!TryScopeId(principal, userId, workspaces, request.Scope, out var scopeId)
                || !PathPattern().IsMatch(request.Path) || !(request.Name == "(Default)" || NamePattern().IsMatch(request.Name))
                || !IsCompatible(request.ValueType, request.Value))
                return Results.BadRequest(new { message = "Invalid registry value." });
            var now = DateTimeOffset.UtcNow;
            if (!VerifyWorkspaceOwner(request.Scope, userId, scopeId, workspaces, out var ownershipError))
                return Results.BadRequest(new { message = ownershipError });
            if (request.ExpectedRevision is null) return Results.Problem(statusCode: 428, title: "settings.revision_required");
            if (request.ExpectedRevision < 0) return Results.BadRequest();
            var root = request.Scope == RegistryScope.Workspace ? RegistryConfigurationFields.Root(request.Path) : null;
            if (root is not null && (request.Path != root || request.Name != WorkspaceConfigurationRegistry.DefaultValueName))
            {
                EnsureWorkspaceDefaults(request.Scope, userId, scopeId, workspaces, registry);
                var backing = registry.Find(userId, request.Scope, scopeId, root, WorkspaceConfigurationRegistry.DefaultValueName)!;
                var field = RegistryConfigurationFields.Values(backing, request.Path).SingleOrDefault(value => value.Name == request.Name);
                if (field is null || field.ValueType != request.ValueType) return Results.BadRequest();
                var metadata = RegistryConfigurationFields.Describe(field, null, true);
                if (metadata.Choices.Count > 0 && !metadata.Choices.Any(choice => choice.Value.GetRawText() == request.Value.GetRawText())) return Results.BadRequest();
                var patched = RegistryConfigurationFields.Patch(backing, request.Path, request.Name, request.Value);
                if (patched is null) return Results.BadRequest(new { message = "Unknown or invalid configuration field." });
                backing.ValueJson = patched; backing.DesiredUpdatedAt = now; backing.DesiredUpdatedBy = actorId.ToString("D");
                var updated = registry.CompareExchange(backing, request.ExpectedRevision.Value);
                return updated is null ? Results.Problem(statusCode: 409, title: "settings.revision_conflict")
                    : Results.Ok(Describe(http, users, userId, scopeId, RegistryConfigurationFields.Values(updated, request.Path).Single(entry => entry.Name == request.Name)));
            }
            if (root is not null) return Results.Problem(statusCode: 409, title: "settings.managed_value");
            if (request.Scope != RegistryScope.Workspace || !IsWorkspaceChildPath(request.Path)) return Results.BadRequest();
            var valueJson = request.Value.GetRawText();
            var saved = registry.CompareExchange(new RelaxKonOS.Server.Domain.RegistryEntry
            {
                UserId = userId, Scope = request.Scope, ScopeId = scopeId, Path = request.Path.Trim(), Name = request.Name.Trim(),
                ValueType = request.ValueType, ValueJson = valueJson, State = RegistryEntryState.Synced,
                DesiredUpdatedAt = now, DesiredUpdatedBy = actorId.ToString("D"), AppliedRevision = 1, AppliedAt = now,
            }, request.ExpectedRevision.Value);
            if (saved is null) return Results.Problem(statusCode: 409, title: "settings.revision_conflict");
            return Results.Ok(Describe(http, users, userId, scopeId, ToDto(saved)));
        }).RequireAuthorization().WithTags("Registry");

        app.MapDelete(RegistryApiRoutes.Values, (Guid userId, RegistryScope scope, string path, string name, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            if (!TryScopeId(principal, userId, workspaces, scope, out var scopeId) || !PathPattern().IsMatch(path) || !(name == "(Default)" || NamePattern().IsMatch(name)))
                return Results.BadRequest(new { message = "Invalid registry value." });
            if (scope == RegistryScope.Workspace && RegistryConfigurationFields.Root(path) is not null)
                return Results.Problem(statusCode: 409, title: "settings.managed_value", detail: "Reset preferences through a versioned settings update.");
            return registry.Delete(userId, scope, scopeId, path, name) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithTags("Registry");

        app.MapDelete(RegistryApiRoutes.Keys, (Guid userId, RegistryScope scope, string path, HttpContext http, IHostElevationSessionStore elevations, IRegistryRepository registry, IWorkspaceRepository workspaces, IUserRepository users) =>
        {
            var principal = http.User;
            if (!TryUserId(principal, out var actorId)) return Results.Unauthorized();
            if (userId != actorId && !elevations.IsGranted(principal, HostElevationCapability.RegistryManage, RegistryTarget(userId)))
                return ElevationRequired();
            if (users.FindById(userId) is null) return Results.NotFound();
            if (scope != RegistryScope.Workspace || !TryScopeId(principal, userId, workspaces, scope, out var scopeId) || !IsWorkspaceChildPath(path))
                return Results.BadRequest(new { message = "Only keys below Workspace can be deleted." });
            if (RegistryConfigurationFields.Root(path) is not null
                || RegistrySchema.Definitions.Any(definition => definition.Path.StartsWith(path + "\\", StringComparison.Ordinal)))
                return Results.Problem(statusCode: 409, title: "settings.managed_value", detail: "This key contains managed workspace preferences.");
            return registry.DeleteKeyTree(userId, scope, scopeId, path) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization().WithTags("Registry");
        app.MapGet(RegistryApiRoutes.Users, (HttpContext http, IUserRepository users, IWorkspaceRepository workspaces, IHostElevationSessionStore elevations) =>
        {
            if (!TryUserId(http.User, out var actorId)) return Results.Unauthorized();
            if (!elevations.IsGranted(http.User, HostElevationCapability.RegistryManage, "registry/users")) return ElevationRequired();
            return Results.Ok(users.List().Select(user => new RegistryUserDto(user.Id, user.Username, user.Id == actorId, workspaces.FindByUserId(user.Id)?.Id)));
        }).RequireAuthorization().WithTags("Registry");
        app.MapGet(RegistryApiRoutes.Context, (HttpContext http, IUserRepository users, IWorkspaceRepository workspaces, IHostElevationSessionStore elevations) =>
        {
            if (!TryUserId(http.User, out var actorId)) return Results.Unauthorized();
            var user = users.FindById(actorId);
            return user is null ? Results.NotFound() : Results.Ok(new RegistryContextDto(ServerId(http), actorId, user.Username,
                workspaces.FindByUserId(actorId)?.Id, elevations.IsGranted(http.User, HostElevationCapability.RegistryManage, "registry/users")));
        }).RequireAuthorization().WithTags("Registry");
        return app;
    }

    private static bool TryUserId(ClaimsPrincipal principal, out Guid userId) => Guid.TryParse(
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out userId);

    private static bool TryScopeId(ClaimsPrincipal principal, Guid userId, IWorkspaceRepository workspaces, RegistryScope scope, out Guid scopeId)
    {
        TryUserId(principal, out var caller);
        scopeId = scope switch
        {
            RegistryScope.User => userId,
            RegistryScope.Workspace => workspaces.FindByUserId(userId)?.Id ?? Guid.Empty,
            RegistryScope.Device when caller == userId && Guid.TryParse(principal.FindFirstValue("device_id"), out var device) => device,
            _ => Guid.Empty,
        };
        return scopeId != Guid.Empty;
    }

    // The server-issued origin identifies this connection's registry namespace.
    private static string ServerId(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";
    private static RegistryKeyDto Describe(HttpContext http, IUserRepository users, Guid owner, Guid scopeId, RegistryKeyDto key) => key with
    {
        Address = new(ServerId(http), owner, key.Scope, scopeId, key.Path),
        CanCreateChild = key.Scope == RegistryScope.Workspace && RegistryConfigurationFields.Root(key.Path) is null,
        CanDelete = key.Scope == RegistryScope.Workspace && IsWorkspaceChildPath(key.Path)
            && RegistryConfigurationFields.Root(key.Path) is null
            && !RegistrySchema.Definitions.Any(definition => definition.Path.StartsWith(key.Path + "\\", StringComparison.Ordinal)),
    };
    private static RegistryEntryDto Describe(HttpContext http, IUserRepository users, Guid owner, Guid scopeId, RegistryEntryDto value)
    {
        var root = value.Scope == RegistryScope.Workspace ? RegistryConfigurationFields.Root(value.Path) : null;
        var defaultValue = root is null ? null : RegistryConfigurationFields.DefaultValue(root, value.Path, value.Name);
        return value with { Address = new(ServerId(http), owner, value.Scope, scopeId, value.Path),
            OwnerUsername = users.FindById(owner)!.Username,
            Schema = RegistryConfigurationFields.Describe(value, defaultValue, root is not null) };
    }
    private static string RegistryTarget(Guid user) => "registry/users/" + user.ToString("D");
    private static IResult ElevationRequired() => Results.Problem(statusCode: 403, title: "elevation-required",
        type: "https://relaxkonos.app/problems/elevation-required", detail: "Administrator authorization is required for another user's registry.");

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

    private static RegistryEntryDto ToDto(RelaxKonOS.Server.Domain.RegistryEntry entry)
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
