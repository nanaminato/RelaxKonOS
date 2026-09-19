using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Desktop;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// Workspace-synchronised appearance choice: light/dark mode, palette and accent only.
/// Palettes are data only; no AXAML is accepted. Shape, sizing, motion and control templates are
/// deliberately absent here and belong to <see cref="SystemStyles.SystemStyleManifestDto"/>, so
/// choosing a colour can never change a menu layout and choosing a style can never change a colour.
/// </summary>
public sealed record AppearancePreferencesDto
{
    public const string DefaultPaletteId = "builtin:relaxkonos-blue";

    [JsonPropertyName("mode")] public ThemeKind Mode { get; set; } = ThemeKind.Light;
    [JsonPropertyName("paletteId")] public string PaletteId { get; set; } = DefaultPaletteId;
    [JsonPropertyName("accentOverride")] public string? AccentOverride { get; set; }
    [JsonPropertyName("customPalettes")] public List<ThemePaletteDto> CustomPalettes { get; set; } = [];

    public static AppearancePreferencesDto Default => new();
}

/// <summary>Safe, serialisable palette payload. It intentionally contains only named sRGB values.</summary>
public sealed record ThemePaletteDto
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 2;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;

    /// <summary>Light and dark variants are one user-facing palette and must share an id.</summary>
    [JsonPropertyName("lightColors")] public Dictionary<string, string>? LightColors { get; set; }
    [JsonPropertyName("darkColors")] public Dictionary<string, string>? DarkColors { get; set; }

}
