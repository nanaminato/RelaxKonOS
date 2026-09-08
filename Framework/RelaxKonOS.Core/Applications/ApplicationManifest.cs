namespace RelaxKonOS.Core.Applications;

/// <summary>
/// Manifest describing a RelaxKonOS application package.
/// In a real RelaxKonOS deployment this would be loaded from a package on disk; currently it is
/// constructed in code by the application itself.
/// </summary>
public sealed record ApplicationManifest(
    AppId Id,
    string DisplayName,
    string Version = "1.0.0",
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null,
    IReadOnlyDictionary<string, ApplicationLocalizedMetadata>? LocalizedMetadata = null,
    IReadOnlyList<string>? ClientPlatforms = null,
    ApplicationServerRequirements? ServerRequirements = null,
    IReadOnlyList<string>? SupportedFileNames = null,
    bool SupportsExtensionlessFiles = false,
    bool SupportsTextFiles = false,
    ApplicationInstancePolicy InstancePolicy = ApplicationInstancePolicy.MultiWindow,
    IReadOnlyList<string>? SupportedUriSchemes = null,
    string? IconPath = null,
    int PermissionModelVersion = 2)
{
    /// <summary>Client OS platforms on which the package may run. An empty list means unrestricted.</summary>
    public IReadOnlyList<string> SupportedClientPlatforms => ApplicationPlatformNames.Normalize(ClientPlatforms);

    /// <summary>Image icon supplied by a package, or the convention-based built-in application icon.</summary>
    public string? EffectiveIconPath => !string.IsNullOrWhiteSpace(IconPath)
        ? IconPath
        : Id.Value.StartsWith("relaxkonos.", StringComparison.Ordinal)
            ? $"avares://RelaxKonOS.Client/Assets/AppIcons/{Id.Value["relaxkonos.".Length..]}.png"
            : null;

    /// <summary>Requirements imposed on the connected server. A null value means unrestricted.</summary>
    public ApplicationServerRequirements Server => ServerRequirements ?? new ApplicationServerRequirements();
    /// <summary>Normalised permission identifiers requested by this application package.</summary>
    public IReadOnlyList<string> Permissions => RequestedPermissions?
        .Where(AppPermissions.IsKnown)
        .Distinct(StringComparer.Ordinal)
        .ToArray()
        ?? Array.Empty<string>();

    /// <summary>
    /// Normalized file extensions that this application accepts from RemoteExplorer.
    /// </summary>
    public IReadOnlyList<string> FileExtensions => SupportedFileExtensions?
        .Where(extension => !string.IsNullOrWhiteSpace(extension))
        .Select(extension => extension.Trim())
        .Where(extension => extension.StartsWith(".", StringComparison.Ordinal) && extension.Length > 1)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
        ?? Array.Empty<string>();

    /// <summary>Exact file names that this application accepts from RemoteExplorer.</summary>
    public IReadOnlyList<string> FileNames => SupportedFileNames?
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name.Trim())
        .Where(name => !Path.IsPathRooted(name) && Path.GetFileName(name).Equals(name, StringComparison.Ordinal))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
        ?? Array.Empty<string>();

    /// <summary>Whether this application declares any rule for opening remote files.</summary>
    public bool SupportsFiles => FileExtensions.Count > 0 || FileNames.Count > 0 || SupportsExtensionlessFiles;

    /// <summary>
    /// Returns the priority of the matching file rule: exact name (3), extension (2),
    /// extensionless fallback (1), or no match (0).
    /// </summary>
    public int GetFileMatchPriority(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) return 0;
        if (FileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return 3;
        if (FileExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return 2;
        return SupportsExtensionlessFiles && Path.GetExtension(name).Length == 0 ? 1 : 0;
    }

    /// <summary>Whether this application explicitly accepts the supplied remote file path.</summary>
    public bool SupportsFile(string path) => GetFileMatchPriority(path) > 0;

    /// <summary>Normalized non-host URI schemes this application declares it can open.</summary>
    public IReadOnlyList<string> UriSchemes => SupportedUriSchemes?
        .Where(scheme => !string.IsNullOrWhiteSpace(scheme))
        .Select(scheme => scheme.Trim().ToLowerInvariant())
        .Where(scheme => System.Text.RegularExpressions.Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]{0,31}$"))
        .Where(scheme => !scheme.Equals("relaxkonos", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray()
        ?? Array.Empty<string>();

    public ApplicationInfo ToInfo() => new(Id, DisplayName, IconGlyph, Description, Permissions, FileExtensions, Version, LocalizedMetadata,
        FileNames, SupportsExtensionlessFiles, SupportsTextFiles, UriSchemes, EffectiveIconPath);
}
