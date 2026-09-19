using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>Stable identifiers of the styles RelaxKonOS ships itself.</summary>
public static class SystemStyleIds
{
    public const string WindowsLike = "relaxkonos.windows-like";
    public const string MacOsLike = "relaxkonos.macos-like";
    public const string UbuntuLike = "relaxkonos.ubuntu-like";

    public static IReadOnlyList<string> All { get; } = [WindowsLike, MacOsLike, UbuntuLike];

    /// <summary>Maps a shell id to the style RelaxKonOS recommends for it. Never applied implicitly.</summary>
    public static string RecommendedForShell(string? shellId) => shellId switch
    {
        "relaxkonos.macos-like" => MacOsLike,
        "relaxkonos.ubuntu-like" => UbuntuLike,
        _ => WindowsLike,
    };
}

/// <summary>
/// Provenance a manifest may claim. The host recognises exactly these strings and refuses anything
/// else - there is no legacy alias and no "unknown" bucket, because an unrecognised origin is
/// precisely the case in which a style must not be installed.
/// </summary>
public static class SystemStyleSources
{
    /// <summary>Shipped with the host and verified by the build.</summary>
    public const string BuiltIn = "builtin";

    /// <summary>Delivered by an installed, attributed package.</summary>
    public const string SignedPackage = "signed-package";

    public static IReadOnlyList<string> All { get; } = [BuiltIn, SignedPackage];

    public static bool IsRecognized(string? source) => source is not null && All.Contains(source, StringComparer.Ordinal);

    public static bool IsExternal(string? source) => IsRecognized(source) && !string.Equals(source, BuiltIn, StringComparison.Ordinal);
}

/// <summary>
/// A data-only description of a system style. This is the whole external extension surface: it can
/// select host recipes and set numeric tokens, and nothing else. There is deliberately no field for
/// XAML, code, resource URIs, event handlers or colour values.
/// </summary>
public sealed record SystemStyleManifestDto
{
    /// <summary>The only manifest schema this build understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The host API this build implements; a manifest may require no more than this.</summary>
    public const string CurrentHostApiVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("supportedRecipes")]
    public SystemStyleRecipeSelectionDto SupportedRecipes { get; set; } = new();

    /// <summary>Shape tokens shared by light and dark mode.</summary>
    [JsonPropertyName("tokens")]
    public Dictionary<string, double> Tokens { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Optional light-mode overrides of <see cref="Tokens"/>.</summary>
    [JsonPropertyName("lightTokens")]
    public Dictionary<string, double>? LightTokens { get; set; }

    /// <summary>Optional dark-mode overrides of <see cref="Tokens"/>.</summary>
    [JsonPropertyName("darkTokens")]
    public Dictionary<string, double>? DarkTokens { get; set; }

    [JsonPropertyName("accessibility")]
    public SystemStyleAccessibilityDto Accessibility { get; set; } = new();

    [JsonPropertyName("minimumHostApiVersion")]
    public string MinimumHostApiVersion { get; set; } = CurrentHostApiVersion;

    /// <summary>Set when the style came from an installed package; null for a built-in style.</summary>
    [JsonPropertyName("packageId")]
    public string? PackageId { get; set; }

    /// <summary>Set when the style came from an installed package; null for a built-in style.</summary>
    [JsonPropertyName("packageVersion")]
    public string? PackageVersion { get; set; }

    /// <summary>Provenance recorded by the host (for example <c>builtin</c> or <c>signed-package</c>).</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = SystemStyleSources.BuiltIn;

    /// <summary>Resolves the effective token map for the requested colour mode, filling defaults.</summary>
    public IReadOnlyDictionary<string, double> ResolveTokens(bool dark)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var definition in SystemStyleTokenContract.All)
            result[definition.Key] = definition.Default;
        foreach (var (key, value) in Tokens)
            if (SystemStyleTokenContract.IsKnown(key)) result[key] = value;
        if (!dark && LightTokens is not null)
            foreach (var (key, value) in LightTokens)
                if (SystemStyleTokenContract.IsKnown(key)) result[key] = value;
        if (dark && DarkTokens is not null)
            foreach (var (key, value) in DarkTokens)
                if (SystemStyleTokenContract.IsKnown(key)) result[key] = value;
        return result;
    }
}

/// <summary>Which host recipe variant each global component uses.</summary>
public sealed record SystemStyleRecipeSelectionDto
{
    [JsonPropertyName("windowChrome")]
    public string WindowChrome { get; set; } = WindowChromeRecipes.CaptionButtonsRight;

    [JsonPropertyName("contextMenu")]
    public string ContextMenu { get; set; } = ContextMenuRecipes.CompactCommandMenu;

    [JsonPropertyName("taskSwitcher")]
    public string TaskSwitcher { get; set; } = TaskSwitcherRecipes.WindowsGrid;

    [JsonPropertyName("shellChrome")]
    public string ShellChrome { get; set; } = ShellChromeRecipes.BottomTaskbar;

    public IReadOnlyDictionary<string, string> AsMap() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SystemStyleRecipeKinds.WindowChrome] = WindowChrome,
        [SystemStyleRecipeKinds.ContextMenu] = ContextMenu,
        [SystemStyleRecipeKinds.TaskSwitcher] = TaskSwitcher,
        [SystemStyleRecipeKinds.ShellChrome] = ShellChrome,
    };
}

/// <summary>Accessibility commitments a style declares and the host can then verify.</summary>
public sealed record SystemStyleAccessibilityDto
{
    /// <summary>Smallest interactive system target the style is designed against, in device-independent pixels.</summary>
    [JsonPropertyName("minimumHitTarget")]
    public double MinimumHitTarget { get; set; } = SystemStyleTokenContract.AbsoluteMinimumHitTarget;

    /// <summary>When false the style depends on animation to convey state and is refused.</summary>
    [JsonPropertyName("supportsReducedMotion")]
    public bool SupportsReducedMotion { get; set; } = true;
}
