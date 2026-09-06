using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using RemoteOS.Protocol.Desktop;
using RemoteOS.Protocol.Workspace;
using Server.ConfigurationRegistry;
using Server.Storage;

namespace Server.Endpoints;

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

        // ── 用户偏好（壁纸/主题/时间格式/语言/区域/默认程序）── 与 TerminalSettings 同模式：GET 直读，PUT 校验后整列覆盖。
        app.MapGet(WorkspaceApiRoutes.Preferences, (Guid id, ClaimsPrincipal principal, IWorkspaceRepository workspaces, IRegistryRepository registry) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            return workspace is null ? Results.NotFound() : Results.Ok(ReadPreferences(registry, workspace));
        }).RequireAuthorization().WithTags("Workspace");

        app.MapPut(WorkspaceApiRoutes.Preferences, async (Guid id, WorkspacePreferencesDto request, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IRegistryRepository registry, WorkspaceWallpaperStore wallpapers) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            if (workspace is null)
                return Results.NotFound();

            if (!TryNormalize(request, out var normalized))
                return Results.BadRequest(new { message = "Invalid workspace preferences." });

            var previousKey = ReadPreferences(registry, workspace).WallpaperKey;
            WritePreferences(registry, workspace, normalized, workspace.UserId.ToString("D"));
            // Switching back to a preset must not leave the previously selected private image
            // on disk indefinitely. Cleanup is best-effort: the updated preference remains valid
            // even if a transient filesystem error delays removal.
            if (TryGetCustomWallpaperId(previousKey, out var previousId)
                && !string.Equals(previousKey, normalized.WallpaperKey, StringComparison.OrdinalIgnoreCase))
            {
                try { await wallpapers.DeleteAsync(id, previousId); }
                catch { /* best-effort orphan cleanup */ }
            }
            return Results.Ok(normalized);
        }).RequireAuthorization().WithTags("Workspace");

        // 图片壁纸属于 Workspace 托管资源，不读取或修改宿主机桌面壁纸。上传成功后原子地更新
        // WallpaperKey，避免客户端在图片尚未同步时引用一个不存在的 blob。
        app.MapPost(WorkspaceApiRoutes.Wallpaper, async (Guid id, HttpContext context, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IRegistryRepository registry, WorkspaceWallpaperStore wallpapers) =>
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
                var currentPreferences = ReadPreferences(registry, workspace);
                var previousKey = currentPreferences.WallpaperKey;
                try
                {
                    var updated = currentPreferences with { WallpaperKey = WorkspacePreferencesDto.CustomWallpaperPrefix + stored.Id };
                    WritePreferences(registry, workspace, updated, workspace.UserId.ToString("D"));
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
                return Results.Ok(ReadPreferences(registry, workspace));
            }
            catch (InvalidWallpaperException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireAuthorization().WithTags("Workspace");

        app.MapGet(WorkspaceApiRoutes.WallpaperContent, (Guid id, string blobId, ClaimsPrincipal principal,
            IWorkspaceRepository workspaces, IRegistryRepository registry, WorkspaceWallpaperStore wallpapers) =>
        {
            var workspace = FindAuthorizedWorkspace(id, principal, workspaces);
            // A blob is readable only while it is the Workspace's selected image. This prevents
            // stale/orphaned ids from acting as a file-access capability.
            if (workspace is null || !TryGetCustomWallpaperId(ReadPreferences(registry, workspace).WallpaperKey, out var currentId)
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

    private static Server.Domain.Workspace? FindAuthorizedWorkspace(
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

    private static TerminalSettingsDto ReadTerminalSettings(IRegistryRepository registry, Server.Domain.Workspace workspace)
    {
        var value = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.TerminalPath, TerminalSettingsDto.Default);
        if (TryNormalize(value, out var normalized)) return normalized;
        WriteTerminalSettings(registry, workspace, TerminalSettingsDto.Default, "system");
        return TerminalSettingsDto.Default;
    }

    private static void WriteTerminalSettings(IRegistryRepository registry, Server.Domain.Workspace workspace, TerminalSettingsDto settings, string updatedBy) =>
        WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.TerminalPath, settings, updatedBy);

    private static WorkspacePreferencesDto ReadPreferences(IRegistryRepository registry, Server.Domain.Workspace workspace)
    {
        var value = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, WorkspacePreferencesDto.Default);
        if (TryNormalize(value, out var normalized)) return normalized;
        WritePreferences(registry, workspace, WorkspacePreferencesDto.Default, "system");
        return WorkspacePreferencesDto.Default;
    }

    private static void WritePreferences(IRegistryRepository registry, Server.Domain.Workspace workspace, WorkspacePreferencesDto preferences, string updatedBy) =>
        WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, preferences, updatedBy);

    private static WorkspaceWindowLayoutDto ReadWindowLayouts(IRegistryRepository registry, Server.Domain.Workspace workspace)
    {
        var value = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.WindowManagerPath, WorkspaceWindowLayoutDto.Default);
        if (TryNormalize(value, out var normalized)) return normalized;
        WriteWindowLayouts(registry, workspace, WorkspaceWindowLayoutDto.Default, "system");
        return WorkspaceWindowLayoutDto.Default;
    }

    private static void WriteWindowLayouts(IRegistryRepository registry, Server.Domain.Workspace workspace, WorkspaceWindowLayoutDto layouts, string updatedBy) =>
        WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.WindowManagerPath, layouts, updatedBy);

    private static bool IsHexColor(string? color) => color is { Length: 7 }
        && color[0] == '#'
        && color[1..].All(Uri.IsHexDigit);

    /// <summary>校验并归一化用户偏好。字段长度封顶防止滥用；枚举/格式白名单校验。
    /// DefaultApps 去重（按 scheme，后者覆盖前者）并剔除空值。
    /// DesktopDisplay 字段归一化校验 VisibleAppIds 列表。</summary>
    private static bool TryNormalize(WorkspacePreferencesDto request, out WorkspacePreferencesDto preferences)
    {
        preferences = WorkspacePreferencesDto.Default;

        var wallpaperKey = request.WallpaperKey?.Trim();
        if (string.IsNullOrWhiteSpace(wallpaperKey) || wallpaperKey.Length > 128)
            return false;
        if (!wallpaperKey.StartsWith(WorkspacePreferencesDto.BuiltInWallpaperPrefix, StringComparison.OrdinalIgnoreCase)
            && !TryGetCustomWallpaperId(wallpaperKey, out _))
            return false;
        if (!Enum.IsDefined(request.Theme))
            return false;
        var timeFormat = request.TimeFormat?.Trim();
        if (timeFormat != WorkspacePreferencesDto.TimeFormat24H
            && timeFormat != WorkspacePreferencesDto.TimeFormat12H)
            return false;
        var dateFormat = request.DateFormat?.Trim();
        if (string.IsNullOrWhiteSpace(dateFormat) || dateFormat.Length > 32)
            return false;
        var language = request.Language?.Trim();
        if (language is { Length: > 16 })
            return false;
        var region = request.Region?.Trim();
        if (region is { Length: > 16 })
            return false;
        var notepadEncoding = request.NotepadDefaultEncoding?.Trim();
        if (string.IsNullOrEmpty(notepadEncoding)) notepadEncoding = TextEncodingPreferences.Default;
        if (!TextEncodingPreferences.IsSupported(notepadEncoding))
            return false;
        var codeEditorEncoding = request.CodeEditorDefaultEncoding?.Trim();
        if (string.IsNullOrEmpty(codeEditorEncoding)) codeEditorEncoding = TextEncodingPreferences.Default;
        if (!TextEncodingPreferences.IsSupported(codeEditorEncoding))
            return false;

        var sourceApps = request.DefaultApps ?? new List<DefaultAppMappingDto>();
        if (sourceApps.Count > 64)
            return false;

        // 按 scheme 去重（大小写不敏感），保留最后一条；剔除空 scheme/appId。
        var deduped = new Dictionary<string, DefaultAppMappingDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in sourceApps)
        {
            var scheme = mapping.Scheme?.Trim();
            var appId = mapping.AppId?.Trim();
            if (string.IsNullOrWhiteSpace(scheme) || scheme.Length > 32
                || string.IsNullOrWhiteSpace(appId) || appId.Length > 128)
                return false;
            deduped[scheme] = new DefaultAppMappingDto(scheme, appId);
        }

        // ── DesktopDisplaySettings 归一化 ──
        var desktopDisplay = request.DesktopDisplay ?? DesktopDisplaySettingsDto.Default;
        var visibleAppIdsSource = desktopDisplay.VisibleAppIds ?? new List<string>();
        if (visibleAppIdsSource.Count > 256)
            return false;

        var normalizedVisibleAppIds = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawId in visibleAppIdsSource)
        {
            var id = rawId?.Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
                return false;
            if (seenIds.Add(id))
                normalizedVisibleAppIds.Add(id);
        }

        var normalizedDesktopDisplay = new DesktopDisplaySettingsDto
        {
            ShowBuiltInApps = desktopDisplay.ShowBuiltInApps,
            VisibleAppIds = normalizedVisibleAppIds,
            ShowServerDesktopFiles = desktopDisplay.ShowServerDesktopFiles,
            ShowServerDesktopShortcuts = desktopDisplay.ShowServerDesktopShortcuts,
            HasCompletedFirstTimeSetup = desktopDisplay.HasCompletedFirstTimeSetup,
        };

        if (!TryNormalizeThemePreferences(request.ThemePreferences, out var themePreferences))
            return false;
        var shellId = request.ShellId?.Trim() ?? "remoteos";
        if (shellId is not "remoteos" and not "windows-like" and not "macos-like" and not "ubuntu-like")
            return false;

        preferences = new WorkspacePreferencesDto(
            wallpaperKey, request.Theme, timeFormat!, dateFormat!,
            string.IsNullOrEmpty(language) ? WorkspacePreferencesDto.Default.Language : language,
            string.IsNullOrEmpty(region) ? WorkspacePreferencesDto.Default.Region : region,
            deduped.Values.ToList(), notepadEncoding, codeEditorEncoding,
            normalizedDesktopDisplay, themePreferences, shellId);
        return true;
    }

    private static bool TryGetCustomWallpaperId(string? key, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrWhiteSpace(key)
            || !key.StartsWith(WorkspacePreferencesDto.CustomWallpaperPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var value = key[WorkspacePreferencesDto.CustomWallpaperPrefix.Length..];
        if (!Guid.TryParseExact(value, "N", out _)) return false;
        id = value;
        return true;
    }

    private static bool TryNormalizeThemePreferences(ThemePreferencesDto? request, out ThemePreferencesDto preferences)
    {
        var source = request ?? ThemePreferencesDto.Default;
        preferences = ThemePreferencesDto.Default;
        if (!string.Equals(source.StyleId?.Trim(), "remoteos", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(source.PaletteId) || source.PaletteId.Length > 72)
            return false;
        var paletteId = source.PaletteId.Trim();
        if (paletteId is not "builtin:remoteos-blue" and not "builtin:nord" and not "builtin:catppuccin"
            && !paletteId.StartsWith("custom:", StringComparison.Ordinal))
            return false;
        if (!IsOptionalColor(source.AccentOverride)) return false;
        var palettes = source.CustomPalettes ?? [];
        if (palettes.Count > 20) return false;
        var normalized = new List<ThemePaletteDto>(palettes.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var palette in palettes)
        {
            if (palette.FormatVersion != 2 || !IsPaletteId(palette.Id) || !ids.Add(palette.Id)
                || string.IsNullOrWhiteSpace(palette.Name) || palette.Name.Trim().Length > 80)
                return false;
            if (!TryNormalizeThemeColors(palette.LightColors, out var light)
                || !TryNormalizeThemeColors(palette.DarkColors, out var dark)) return false;
            normalized.Add(new ThemePaletteDto { FormatVersion = 2, Id = palette.Id, Name = palette.Name.Trim(), LightColors = light, DarkColors = dark });
        }
        if (paletteId.StartsWith("custom:", StringComparison.Ordinal) && !ids.Contains(paletteId[7..])) return false;
        preferences = new ThemePreferencesDto { StyleId = "remoteos", PaletteId = paletteId,
            AccentOverride = source.AccentOverride?.ToUpperInvariant(), CustomPalettes = normalized };
        return HasValidResolvedTheme(preferences);
    }

    private static bool TryNormalizeThemeColors(Dictionary<string, string>? source, out Dictionary<string, string> colors)
    {
        colors = new(StringComparer.OrdinalIgnoreCase);
        if (source is null || source.Count is 0 or > 56) return false;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key) || !ThemePaletteContract.ColorTokens.Contains(key) || !IsHexColor8(value)) return false;
            colors[key] = value.ToUpperInvariant();
        }
        return true;
    }

    private static bool HasValidResolvedTheme(ThemePreferencesDto preferences)
    {
        if (!IsAccessible(preferences)) return false;
        foreach (var palette in preferences.CustomPalettes)
        {
            var candidate = new ThemePreferencesDto
            {
                PaletteId = "custom:" + palette.Id,
                AccentOverride = preferences.AccentOverride,
                CustomPalettes = preferences.CustomPalettes,
            };
            if (!IsAccessible(candidate)) return false;
        }
        return true;
    }

    private static bool IsAccessible(ThemePreferencesDto preferences) =>
        ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: false), out _)
        && ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: true), out _);

    private static bool IsOptionalColor(string? value) => string.IsNullOrEmpty(value) || IsHexColor8(value);
    private static bool IsHexColor8(string? value) => value is { Length: 7 or 9 } && value[0] == '#'
        && value[1..].All(Uri.IsHexDigit);
    private static bool IsPaletteId(string? value) => value is { Length: > 0 and <= 64 }
        && Regex.IsMatch(value, "^[a-z0-9-]+$");

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
