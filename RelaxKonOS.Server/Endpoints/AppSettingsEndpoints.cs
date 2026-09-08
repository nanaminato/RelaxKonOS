using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Authenticated storage for versioned JSON configuration owned by an application and a caller scope.</summary>
public static partial class AppSettingsEndpoints
{
    private const int MaxDocumentBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapAppSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(AppSettingsApiRoutes.Document, (
            string appId, string scope, string key, HttpContext context, ClaimsPrincipal principal, IAppSettingsRepository settings) =>
        {
            if (!TryResolveRequest(appId, scope, key, principal, out var owner, out var resolvedScope, out var error))
                return Results.BadRequest(new { message = error });

            var found = settings.Find(owner.UserId, resolvedScope, owner.ScopeId, appId, key);
            if (found is null)
                return Results.NotFound();

            context.Response.Headers.ETag = ToETag(found.Revision);
            return Results.Ok(ToDto(found));
        }).RequireAuthorization().WithTags("Application settings");

        app.MapPut(AppSettingsApiRoutes.Document, (
            string appId, string scope, string key, PutAppSettingsRequest request, HttpContext context,
            ClaimsPrincipal principal, IAppSettingsRepository settings) =>
        {
            if (!TryResolveRequest(appId, scope, key, principal, out var owner, out var resolvedScope, out var error))
                return Results.BadRequest(new { message = error });
            if (request.SchemaVersion is < 1 or > 100_000 || request.Value.ValueKind is JsonValueKind.Undefined)
                return Results.BadRequest(new { message = "Invalid application settings document." });

            var json = request.Value.GetRawText();
            if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
                return Results.BadRequest(new { message = "Application settings document exceeds 64 KiB." });
            if (!TryGetExpectedRevision(context.Request, out var expectedRevision))
                return Results.BadRequest(new { message = "If-Match must be a non-negative numeric ETag." });

            var result = settings.Upsert(new AppSetting
            {
                UserId = owner.UserId,
                Scope = resolvedScope,
                ScopeId = owner.ScopeId,
                AppId = appId,
                Key = key,
                ValueJson = json,
                SchemaVersion = request.SchemaVersion,
            }, expectedRevision);
            if (result.IsConflict)
                return Results.Conflict(new { message = "The application settings document was changed by another client." });

            var saved = result.Setting!;
            context.Response.Headers.ETag = ToETag(saved.Revision);
            return Results.Ok(ToDto(saved));
        }).RequireAuthorization().WithTags("Application settings");

        app.MapDelete(AppSettingsApiRoutes.Application, (string appId, ClaimsPrincipal principal, IAppSettingsRepository settings) =>
        {
            if (!AppIdPattern().IsMatch(appId))
                return Results.BadRequest(new { message = "Invalid application id." });
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(subject, out var userId))
                return Results.Unauthorized();

            var deleted = settings.DeleteForApp(userId, appId);
            return Results.Ok(new { deleted });
        }).RequireAuthorization().WithTags("Application settings");

        return app;
    }

    private static bool TryResolveRequest(
        string appId, string scope, string key, ClaimsPrincipal principal,
        out AppSettingsOwner owner, out AppSettingsScope resolvedScope, out string error)
    {
        owner = default;
        resolvedScope = default;
        error = "Invalid application settings request.";
        if (!AppIdPattern().IsMatch(appId) || !KeyPattern().IsMatch(key))
        {
            error = "Invalid application id or settings key.";
            return false;
        }
        if (!TryParseScope(scope, out resolvedScope))
        {
            error = "Scope must be user, workspace, or device.";
            return false;
        }

        var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId))
            return false;

        var scopeId = resolvedScope switch
        {
            AppSettingsScope.User => userId,
            AppSettingsScope.Workspace when Guid.TryParse(principal.FindFirstValue("workspace_id"), out var workspaceId) => workspaceId,
            AppSettingsScope.Device when Guid.TryParse(principal.FindFirstValue("device_id"), out var deviceId) => deviceId,
            _ => Guid.Empty,
        };
        if (scopeId == Guid.Empty)
        {
            error = "The access token does not identify the requested settings scope.";
            return false;
        }

        owner = new AppSettingsOwner(userId, scopeId);
        return true;
    }

    private static bool TryParseScope(string value, out AppSettingsScope scope)
        => Enum.TryParse(value, ignoreCase: true, out scope) && Enum.IsDefined(scope);

    private static bool TryGetExpectedRevision(HttpRequest request, out long? expectedRevision)
    {
        expectedRevision = null;
        if (!request.Headers.TryGetValue("If-Match", out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;
        var text = raw.ToString().Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        if (!long.TryParse(text, out var revision) || revision < 0) return false;
        expectedRevision = revision;
        return true;
    }

    private static AppSettingsDocumentDto ToDto(AppSetting setting)
    {
        using var document = JsonDocument.Parse(setting.ValueJson);
        return new AppSettingsDocumentDto(setting.Scope, setting.Key, document.RootElement.Clone(),
            setting.SchemaVersion, setting.Revision, setting.UpdatedAt);
    }

    private static string ToETag(long revision) => $"\"{revision}\"";

    private readonly record struct AppSettingsOwner(Guid UserId, Guid ScopeId);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{2,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppIdPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
