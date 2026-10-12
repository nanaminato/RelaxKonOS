using Avalonia.Media;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Built-in terminal palettes. Saved values are the resolved colours, not just a preset name.</summary>
public static class TerminalAppearance
{
    public static readonly IReadOnlyList<string> FontFamilies =
        ["DejaVu Sans Mono", "Noto Mono", "Liberation Mono", "Cascadia Mono", "Consolas", "JetBrains Mono", "Courier New"];

    public static readonly IReadOnlyList<double> FontSizes = [8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 40];

    public static readonly IReadOnlyList<string> ColorSchemes =
        ["Campbell", "One Half Dark", "Solarized Dark", "Light"];

    public static TerminalSettingsDto ApplyScheme(TerminalSettingsDto current, string scheme) => scheme switch
    {
        "One Half Dark" => current with { ColorScheme = scheme, BackgroundColor = "#282C34", ForegroundColor = "#DCDFE4", CursorColor = "#FFFFFF" },
        "Solarized Dark" => current with { ColorScheme = scheme, BackgroundColor = "#002B36", ForegroundColor = "#839496", CursorColor = "#93A1A1" },
        "Light" => current with { ColorScheme = scheme, BackgroundColor = "#FFFFFF", ForegroundColor = "#1E1E1E", CursorColor = "#000000" },
        _ => current with { ColorScheme = "Campbell", BackgroundColor = "#0C0C0C", ForegroundColor = "#CCCCCC", CursorColor = "#FFFFFF" },
    };

    /// <summary>
    /// Picks an installed monospace face for the requested family. Cascadia/Consolas are normally
    /// absent on Linux, and passing a missing family to the Skia renderer produces poor metrics.
    /// </summary>
    public static string ResolveFontFamily(string requested)
    {
        var installed = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(requested) && installed.Contains(requested))
            return requested;

        foreach (var fallback in new[] { "DejaVu Sans Mono", "Noto Mono", "Liberation Mono", "JetBrains Mono", "Cascadia Mono", "Consolas" })
            if (installed.Contains(fallback))
                return fallback;

        return FontManager.Current.DefaultFontFamily.Name;
    }

    /// <summary>The CSS font stack for the same face, for engines that render in a WebView.</summary>
    public static string ToCssFontStack(string resolved) =>
        string.IsNullOrWhiteSpace(resolved)
            ? "monospace"
            : $"\"{resolved.Replace("\"", string.Empty)}\", monospace";
}
