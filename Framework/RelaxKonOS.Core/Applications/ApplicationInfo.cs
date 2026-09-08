namespace RelaxKonOS.Core.Applications;

/// <summary>Public metadata for a registered application (shown in desktop / start menu / taskbar).</summary>
public sealed record ApplicationInfo(
    AppId Id,
    string DisplayName,
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null,
    string Version = "1.0.0",
    IReadOnlyDictionary<string, ApplicationLocalizedMetadata>? LocalizedMetadata = null,
    IReadOnlyList<string>? SupportedFileNames = null,
    bool SupportsExtensionlessFiles = false,
    bool SupportsTextFiles = false,
    IReadOnlyList<string>? SupportedUriSchemes = null,
    string? IconPath = null)
{
    public IReadOnlyList<string> Permissions => RequestedPermissions ?? Array.Empty<string>();

    /// <summary>File extensions this application explicitly accepts from RemoteExplorer.</summary>
    public IReadOnlyList<string> FileExtensions => SupportedFileExtensions ?? Array.Empty<string>();

    /// <summary>Exact file names this application explicitly accepts from RemoteExplorer.</summary>
    public IReadOnlyList<string> FileNames => SupportedFileNames ?? Array.Empty<string>();

    /// <summary>URI schemes this application explicitly accepts from the Shell.</summary>
    public IReadOnlyList<string> UriSchemes => SupportedUriSchemes ?? Array.Empty<string>();

    /// <summary>Returns package-owned metadata in the requested UI language, with stable fallbacks.</summary>
    public ApplicationLocalizedMetadata GetLocalizedMetadata(string culture)
    {
        if (LocalizedMetadata is not null)
        {
            if (LocalizedMetadata.TryGetValue(culture, out var exact)) return exact;

            var neutral = culture.Split('-', 2)[0];
            var neutralMatch = LocalizedMetadata.FirstOrDefault(pair => pair.Key.StartsWith(neutral + "-", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(neutralMatch.Key)) return neutralMatch.Value;

            if (LocalizedMetadata.TryGetValue("en-US", out var english)) return english;
        }

        return new ApplicationLocalizedMetadata(DisplayName, Description);
    }

    public static readonly ApplicationInfo None = new(default, string.Empty);
}

/// <summary>Package-owned display metadata for one BCP-47 UI culture.</summary>
public sealed record ApplicationLocalizedMetadata(string DisplayName, string? Description = null);
