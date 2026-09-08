using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using static RelaxKonOS.Server.Settings.WorkspacePreferencesValidator;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Storage;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>Workspace-scoped presentation settings endpoints.</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(WorkspaceApiRoutes.TerminalSettings, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null) return Results.NotFound();
            return Results.Ok(ReadTerminalSettings(registry, workspace));
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.TerminalSettings, (Guid id, TerminalSettingsDto request, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();

            if (!TryNormalize(request, out var normalized))
                return Results.BadRequest(new { message = "Invalid terminal appearance settings." });

            WriteTerminalSettings(registry, workspace, normalized, workspace.UserId.ToString("D"));
            return Results.Ok(normalized);
        }).RequireAuthorization().WithTags("Workspace");

        // Workspace preference snapshots and conditional writes share the independent domain service.
        app.MapGet(WorkspaceApiRoutes.Preferences, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IWorkspaceSettingsService preferencesService) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            return workspace is null ? Results.NotFound() : Results.Ok(preferencesService.Read(workspace));
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.Preferences, async (Guid id, WorkspacePreferencesDto request, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IWorkspaceSettingsService preferencesService, WorkspaceWallpaperStore wallpapers) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();

            if (!WorkspacePreferencesValidator.TryNormalize(request, out var normalized))
                return Results.BadRequest(new { message = "Invalid workspace preferences." });

            if (request.Revision is null) return Results.Problem(statusCode: 428, title: "settings.revision_required");
            if (request.Revision <= 0) return Results.BadRequest(new { message = "Invalid preference revision." });
            normalized.Revision = request.Revision;
            var previousKey = preferencesService.Read(workspace).WallpaperKey;
            var saved = preferencesService.Save(workspace, normalized, workspace.UserId.ToString("D"));
            if (saved is null) return Results.Problem(statusCode: 409, title: "settings.revision_conflict");
            // Switching back to a preset must not leave the previously selected private image
            // on disk indefinitely. Cleanup is best-effort: the updated preference remains valid
            // even if a transient filesystem error delays removal.
            if (TryGetCustomWallpaperId(previousKey, out var previousId)
                && !string.Equals(previousKey, normalized.WallpaperKey, StringComparison.OrdinalIgnoreCase))
            {
                try { await wallpapers.DeleteAsync(id, previousId); }
                catch { /* best-effort orphan cleanup */ }
            }
            return Results.Ok(saved);
        }).RequireAuthorization().WithTags("Workspace");

        // 图片壁纸属于 Workspace 托管资源，不读取或修改宿主机桌面壁纸。上传成功后原子地更新
        // WallpaperKey，避免客户端在图片尚未同步时引用一个不存在的 blob。
        app.MapPost(WorkspaceApiRoutes.Wallpaper, async (Guid id, HttpContext context, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IWorkspaceSettingsService preferencesService, WorkspaceWallpaperStore wallpapers) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null) return Results.NotFound();
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { message = "Wallpaper upload must be multipart/form-data." });

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null)
                return Results.BadRequest(new { message = "No wallpaper image was provided." });
            try
            {
                var stored = await wallpapers.SaveAsync(id, file, context.RequestAborted);
                var currentPreferences = preferencesService.Read(workspace);
                var previousKey = currentPreferences.WallpaperKey;
                try
                {
                    var updated = currentPreferences with { WallpaperKey = WorkspacePreferencesDto.CustomWallpaperPrefix + stored.Id };
                    if (preferencesService.Save(workspace, updated, workspace.UserId.ToString("D")) is null)
                    {
                        await wallpapers.DeleteAsync(id, stored.Id);
                        return Results.Problem(statusCode: 409, title: "settings.revision_conflict");
                    }
                }
                catch
                {
                    // The blob has no registry reference until the cached write is accepted.
                    // Delete it on every persistence failure so retries do not leak files.
                    try { await wallpapers.DeleteAsync(id, stored.Id); }
                    catch { /* best-effort cleanup; preserve the database failure */ }
                    throw;
                }

                if (TryGetCustomWallpaperId(previousKey, out var previousId))
                {
                    try { await wallpapers.DeleteAsync(id, previousId); }
                    catch { /* best-effort orphan cleanup */ }
                }
                return Results.Ok(preferencesService.Read(workspace));
            }
            catch (InvalidWallpaperException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization().WithTags("Workspace");

        app.MapGet(WorkspaceApiRoutes.WallpaperContent, (Guid id, string blobId, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IWorkspaceSettingsService preferencesService, WorkspaceWallpaperStore wallpapers) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            // A blob is readable only while it is the Workspace's selected image. This prevents
            // stale/orphaned ids from acting as a file-access capability.
            if (workspace is null || !TryGetCustomWallpaperId(preferencesService.Read(workspace).WallpaperKey, out var currentId)
                || !string.Equals(currentId, blobId, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            var resource = wallpapers.OpenRead(id, blobId);
            return resource is null ? Results.NotFound() : Results.File(resource.Value.Stream, resource.Value.ContentType);
        }).RequireAuthorization().WithTags("Workspace");

        app.MapGet(WorkspaceApiRoutes.WindowLayouts, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            return workspace is null ? Results.NotFound() : Results.Ok(ReadWindowLayouts(registry, workspace));
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.WindowLayouts, (Guid id, WorkspaceWindowLayoutDto request, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();
            if (!TryNormalize(request, out var layouts))
                return Results.BadRequest(new { message = "Invalid workspace window layouts." });

            WriteWindowLayouts(registry, workspace, layouts, workspace.UserId.ToString("D"));
            return Results.Ok(layouts);
        }).RequireAuthorization().WithTags("Workspace");

        return app;
    }

    private static RelaxKonOS.Server.Domain.Workspace? FindAuthorizedWorkspace(
        Guid workspaceId, ClaimsPrincipal principal, IWorkspaceRepository workspaces)
    {
        var userText = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                       ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userText, out var userId)
               && workspaces.FindById(workspaceId) is { } workspace
               && workspace.UserId == userId
            ? workspace
            : null;
    }

    private static bool TryNormalize(TerminalSettingsDto request, out TerminalSettingsDto settings)
    {
        settings = TerminalSettingsDto.Default;
        var fontFamily = request.FontFamily?.Trim();
        var scheme = request.ColorScheme?.Trim();
        if (string.IsNullOrWhiteSpace(fontFamily) || fontFamily.Length > 128
            || string.IsNullOrWhiteSpace(scheme) || scheme.Length > 64
            || request.FontSize is < 8 or > 40
            || !IsHexColor(request.BackgroundColor) || !IsHexColor(request.ForegroundColor) || !IsHexColor(request.CursorColor))
            return false;

        settings = new TerminalSettingsDto(
            fontFamily, request.FontSize, scheme,
            request.BackgroundColor.ToUpperInvariant(),
            request.ForegroundColor.ToUpperInvariant(),
            request.CursorColor.ToUpperInvariant());
        return true;
    }

    private static TerminalSettingsDto ReadTerminalSettings(IRegistryRepository registry, RelaxKonOS.Server.Domain.Workspace workspace)
    {
        var value = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.TerminalPath, TerminalSettingsDto.Default);
        if (TryNormalize(value, out var normalized)) return normalized;
        WriteTerminalSettings(registry, workspace, TerminalSettingsDto.Default, "system");
        return TerminalSettingsDto.Default;
    }

    private static void WriteTerminalSettings(IRegistryRepository registry, RelaxKonOS.Server.Domain.Workspace workspace, TerminalSettingsDto settings, string updatedBy) =>
        WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.TerminalPath, settings, updatedBy);

    private static WorkspaceWindowLayoutDto ReadWindowLayouts(IRegistryRepository registry, RelaxKonOS.Server.Domain.Workspace workspace)
    {
        var value = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.WindowManagerPath, WorkspaceWindowLayoutDto.Default);
        if (TryNormalize(value, out var normalized)) return normalized;
        WriteWindowLayouts(registry, workspace, WorkspaceWindowLayoutDto.Default, "system");
        return WorkspaceWindowLayoutDto.Default;
    }

    private static void WriteWindowLayouts(IRegistryRepository registry, RelaxKonOS.Server.Domain.Workspace workspace, WorkspaceWindowLayoutDto layouts, string updatedBy) =>
        WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.WindowManagerPath, layouts, updatedBy);

    private static bool IsHexColor(string? color) => color is { Length: 7 }
        && color[0] == '#'
        && color[1..].All(Uri.IsHexDigit);

    private static bool TryNormalize(WorkspaceWindowLayoutDto request, out WorkspaceWindowLayoutDto layouts)
    {
        layouts = WorkspaceWindowLayoutDto.Default;
        var source = request.Windows ?? Array.Empty<WindowSizeDto>();
        if (source.Count > 128)
            return false;

        var normalized = new Dictionary<string, WindowSizeDto>(StringComparer.Ordinal);
        foreach (var entry in source)
        {
            var key = entry.Key?.Trim();
            if (string.IsNullOrWhiteSpace(key) || key.Length > 256
                || !double.IsFinite(entry.Width) || !double.IsFinite(entry.Height)
                || entry.Width is < 240 or > 3840 || entry.Height is < 160 or > 2160)
                return false;
            normalized[key] = new WindowSizeDto(key, Math.Round(entry.Width), Math.Round(entry.Height));
        }

        layouts = new WorkspaceWindowLayoutDto(normalized.Values.ToList());
        return true;
    }
}
