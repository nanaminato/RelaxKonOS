using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// Appearance preferences shared by every terminal in one workspace.
/// The values are deliberately presentation-only: they never affect the PTY or shell process.
/// </summary>
public sealed record TerminalSettingsDto(
    [property: JsonPropertyName("fontFamily")] string FontFamily,
    [property: JsonPropertyName("fontSize")] double FontSize,
    [property: JsonPropertyName("colorScheme")] string ColorScheme,
    [property: JsonPropertyName("backgroundColor")] string BackgroundColor,
    [property: JsonPropertyName("foregroundColor")] string ForegroundColor,
    [property: JsonPropertyName("cursorColor")] string CursorColor)
{
    public static TerminalSettingsDto Default { get; } = new(
        FontFamily: "Cascadia Mono",
        FontSize: 14,
        ColorScheme: "Campbell",
        BackgroundColor: "#0C0C0C",
        ForegroundColor: "#CCCCCC",
        CursorColor: "#FFFFFF");
}
