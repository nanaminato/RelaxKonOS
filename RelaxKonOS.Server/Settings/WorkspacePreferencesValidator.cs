using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Server.Settings;

public static class WorkspacePreferencesValidator
{
    /// <summary>校验并归一化用户偏好。字段长度封顶防止滥用；枚举/格式白名单校验。
    /// DefaultApps 去重（按 scheme，后者覆盖前者）并剔除空值。
    /// DesktopDisplay 字段归一化校验 VisibleAppIds 列表。
    /// DesktopExperience 是颜色/系统风格/桌面 Shell 的唯一来源：三者互相独立，缺一不可。</summary>
    public static bool TryNormalize(WorkspacePreferencesDto request, out WorkspacePreferencesDto preferences)
    {
        preferences = WorkspacePreferencesDto.Default;

        var wallpaperKey = request.WallpaperKey?.Trim();
        if (string.IsNullOrWhiteSpace(wallpaperKey) || wallpaperKey.Length > 128)
            return false;
        if (!wallpaperKey.StartsWith(WorkspacePreferencesDto.BuiltInWallpaperPrefix, StringComparison.OrdinalIgnoreCase)
            && !TryGetCustomWallpaperId(wallpaperKey, out _))
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
            ShowWindowShadows = desktopDisplay.ShowWindowShadows,
            ShowWindowContentsWhileDragging = desktopDisplay.ShowWindowContentsWhileDragging,
            ShowTaskbarWindowPreviews = desktopDisplay.ShowTaskbarWindowPreviews,
        };

        if (!TryNormalizeDesktopExperience(request.DesktopExperience, out var desktopExperience))
            return false;

        preferences = new WorkspacePreferencesDto(
            wallpaperKey, timeFormat!, dateFormat!,
            string.IsNullOrEmpty(language) ? WorkspacePreferencesDto.Default.Language : language,
            string.IsNullOrEmpty(region) ? WorkspacePreferencesDto.Default.Region : region,
            deduped.Values.ToList(), notepadEncoding, codeEditorEncoding,
            normalizedDesktopDisplay, desktopExperience);
        return true;
    }

    /// <summary>
    /// The server validates <em>shape</em>, not availability: a style id is an intent that is
    /// resolved on the device that renders it, so an unknown-but-well-formed id is accepted and
    /// stored rather than silently replaced.
    /// </summary>
    private static bool TryNormalizeDesktopExperience(
        DesktopExperiencePreferencesDto? request,
        out DesktopExperiencePreferencesDto experience)
    {
        experience = DesktopExperiencePreferencesDto.Default;
        var source = request ?? DesktopExperiencePreferencesDto.Default;

        if (!TryNormalizeAppearance(source.Appearance, out var appearance))
            return false;

        var systemStyleId = source.SystemStyleId?.Trim();
        if (string.IsNullOrEmpty(systemStyleId) || !IsValidStyleId(systemStyleId))
            return false;

        var requestedShell = source.Shell ?? new ShellSelectionDto("relaxkonos.windows-like");
        var shellId = NormalizeShellId(requestedShell.ShellId);
        if (!IsValidShellId(shellId))
            return false;
        var packageId = requestedShell.PackageId?.Trim();
        var packageVersion = requestedShell.PackageVersion?.Trim();
        if (packageId is { Length: > 128 } || packageVersion is { Length: > 64 }) return false;
        if (shellId.StartsWith("relaxkonos.", StringComparison.Ordinal) &&
            (!string.IsNullOrEmpty(packageId) || !string.IsNullOrEmpty(packageVersion))) return false;

        experience = new DesktopExperiencePreferencesDto
        {
            Appearance = appearance,
            SystemStyleId = systemStyleId,
            Shell = new ShellSelectionDto(shellId, packageId, packageVersion),
        };
        return true;
    }

    private static string NormalizeShellId(string? id) => id?.Trim() switch
    {
        null or "" or "relaxkonos" or "relaxkonos.default" => "relaxkonos.windows-like",
        "windows-like" => "relaxkonos.windows-like",
        "macos-like" => "relaxkonos.macos-like",
        "ubuntu-like" => "relaxkonos.ubuntu-like",
        var value => value,
    };

    private static bool IsValidShellId(string id) => id is "relaxkonos.windows-like"
        or "relaxkonos.macos-like" or "relaxkonos.ubuntu-like"
        || Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{2,127}$");

    /// <summary>Same identifier grammar as a style manifest, so an id can never name an arbitrary object.</summary>
    private static bool IsValidStyleId(string id) => Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{2,127}$");

    public static bool TryGetCustomWallpaperId(string? key, out string id)
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

    private static bool TryNormalizeAppearance(AppearancePreferencesDto? request, out AppearancePreferencesDto appearance)
    {
        var source = request ?? AppearancePreferencesDto.Default;
        appearance = AppearancePreferencesDto.Default;
        if (!Enum.IsDefined(source.Mode))
            return false;
        if (string.IsNullOrWhiteSpace(source.PaletteId) || source.PaletteId.Length > 72)
            return false;
        var paletteId = source.PaletteId.Trim();
        if (paletteId is not "builtin:relaxkonos-blue" and not "builtin:nord" and not "builtin:catppuccin"
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
        appearance = new AppearancePreferencesDto
        {
            Mode = source.Mode,
            PaletteId = paletteId,
            AccentOverride = source.AccentOverride?.ToUpperInvariant(),
            CustomPalettes = normalized,
        };
        return HasValidResolvedAppearance(appearance);
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

    private static bool HasValidResolvedAppearance(AppearancePreferencesDto preferences)
    {
        if (!IsAccessible(preferences)) return false;
        foreach (var palette in preferences.CustomPalettes)
        {
            var candidate = new AppearancePreferencesDto
            {
                Mode = preferences.Mode,
                PaletteId = "custom:" + palette.Id,
                AccentOverride = preferences.AccentOverride,
                CustomPalettes = preferences.CustomPalettes,
            };
            if (!IsAccessible(candidate)) return false;
        }
        return true;
    }

    private static bool IsAccessible(AppearancePreferencesDto preferences) =>
        ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: false), out _)
        && ThemePaletteValidator.TryValidate(ThemePaletteDefaults.Resolve(preferences, dark: true), out _);

    private static bool IsOptionalColor(string? value) => string.IsNullOrEmpty(value) || IsHexColor8(value);
    private static bool IsHexColor8(string? value) => value is { Length: 7 or 9 } && value[0] == '#'
        && value[1..].All(Uri.IsHexDigit);
    private static bool IsPaletteId(string? value) => value is { Length: > 0 and <= 64 }
        && Regex.IsMatch(value, "^[a-z0-9-]+$");

}
