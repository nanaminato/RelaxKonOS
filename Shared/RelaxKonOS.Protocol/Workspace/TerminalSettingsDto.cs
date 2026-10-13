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
    [property: JsonPropertyName("cursorColor")] string CursorColor,
    [property: JsonPropertyName("backend")] string? Backend = null,
    [property: JsonPropertyName("scrollbackLines")] int ScrollbackLines = 10000,
    [property: JsonPropertyName("cursorStyle")] string CursorStyle = "block",
    [property: JsonPropertyName("cursorBlink")] bool CursorBlink = true)
{
    /// <summary>The backend the workspace asks for, defaulting to the built-in control.</summary>
    [JsonIgnore] public string RequestedBackend =>
        TerminalBackends.TryNormalize(Backend, out var backend) ? backend : TerminalBackends.BuiltIn;

    public static TerminalSettingsDto Default { get; } = new(
        FontFamily: "Cascadia Mono",
        FontSize: 14,
        ColorScheme: "Campbell",
        BackgroundColor: "#0C0C0C",
        ForegroundColor: "#CCCCCC",
        CursorColor: "#FFFFFF",
        Backend: TerminalBackends.BuiltIn);
}
