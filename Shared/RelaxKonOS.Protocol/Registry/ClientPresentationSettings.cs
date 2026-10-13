using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Registry;

public sealed record TerminalPaddingSettings(
    [property: JsonPropertyName("paddingTop")] int PaddingTop = 8,
    [property: JsonPropertyName("paddingBottom")] int PaddingBottom = 4,
    [property: JsonPropertyName("paddingHorizontal")] int PaddingHorizontal = 6)
{
    public static TerminalPaddingSettings Default { get; } = new();
    [JsonIgnore] public bool IsValid => PaddingTop is >= 0 and <= 32 && PaddingBottom is >= 0 and <= 32 && PaddingHorizontal is >= 0 and <= 32;
}

public sealed record RegistryAppearanceSettings(
    [property: JsonPropertyName("treeRowHeight")] int TreeRowHeight = 24,
    [property: JsonPropertyName("valueRowHeight")] int ValueRowHeight = 26,
    [property: JsonPropertyName("iconSize")] int IconSize = 16,
    [property: JsonPropertyName("scrollbarThickness")] int ScrollbarThickness = 6,
    [property: JsonPropertyName("sidebarWidth")] int SidebarWidth = 280)
{
    public static RegistryAppearanceSettings Default { get; } = new();
    [JsonIgnore] public bool IsValid => TreeRowHeight is >= 20 and <= 40 && ValueRowHeight is >= 22 and <= 44
        && IconSize is >= 12 and <= 24 && ScrollbarThickness is >= 4 and <= 12 && SidebarWidth is >= 180 and <= 600;
}

public static class ClientPresentationPaths
{
    public const string Terminal = "Device\\Client\\Terminal";
    public const string Registry = "Device\\Client\\Registry";
}
