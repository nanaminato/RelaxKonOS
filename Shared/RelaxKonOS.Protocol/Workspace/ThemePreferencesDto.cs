using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Workspace-synchronised theme choice. Palettes are data only; no AXAML is accepted.</summary>
public sealed record ThemePreferencesDto
{
    public const string DefaultPaletteId = "builtin:remoteos-blue";

    [JsonPropertyName("styleId")] public string StyleId { get; set; } = "remoteos";
    [JsonPropertyName("paletteId")] public string PaletteId { get; set; } = DefaultPaletteId;
    [JsonPropertyName("accentOverride")] public string? AccentOverride { get; set; }
    [JsonPropertyName("customPalettes")] public List<ThemePaletteDto> CustomPalettes { get; set; } = [];

    public static ThemePreferencesDto Default => new();
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
