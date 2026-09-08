namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Single source of truth for RelaxKonOS built-in palette values and derived accent roles.</summary>
public static class ThemePaletteDefaults
{
    private static readonly IReadOnlyDictionary<string, string> LightBase = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AppBackground"]="#F3F3F3", ["ShellBackground"]="#F7F7F7", ["Surface"]="#FFFFFF", ["SurfaceRaised"]="#FFFFFF", ["SurfaceSunken"]="#EEF3FA", ["SurfaceHover"]="#F0F0F0", ["SurfacePressed"]="#E5E5E5",
        ["TextPrimary"]="#1F1F1F", ["TextSecondary"]="#616161", ["TextTertiary"]="#72819A", ["TextDisabled"]="#A0A0A0", ["TextOnAccent"]="#FFFFFF", ["TextOnDanger"]="#FFFFFF",
        ["BorderSubtle"]="#E5EAF2", ["BorderDefault"]="#D6DCE5", ["BorderStrong"]="#8AB9E5", ["FocusBorder"]="#0078D4", ["FocusRing"]="#0078D4",
        ["Accent"]="#0078D4", ["AccentHover"]="#1A86D9", ["AccentPressed"]="#005A9E", ["AccentMuted"]="#E6F0FA", ["SelectionBackground"]="#E6F0FA", ["SelectionForeground"]="#1F1F1F",
        ["Success"]="#107C10", ["SuccessMuted"]="#DFF6DD", ["Warning"]="#C77A00", ["WarningMuted"]="#FFF4CE", ["Danger"]="#C42B1C", ["DangerHover"]="#B3271D", ["DangerPressed"]="#8F2117", ["Info"]="#2369A7",
        ["TaskbarBackground"]="#F7F7F7", ["TaskbarForeground"]="#1F1F1F", ["StartMenuBackground"]="#FFFFFF", ["WindowFrameBackground"]="#FFFFFF", ["WindowTitleBarBackground"]="#F5F5F5", ["WindowTitleForeground"]="#202020", ["WindowInactiveTitleForeground"]="#4F4F4F",
        ["OverlayScrim"]="#66000000", ["Shadow"]="#22000000", ["DesktopIconHover"]="#220078D4", ["DesktopIconSelected"]="#330078D4",
        ["CardShadow"]="#22000000", ["FlyoutShadow"]="#22000000", ["DialogScrim"]="#3D000000", ["ChartGridLine"]="#E5EBF5",
        ["ChartSeries1"]="#0078D4", ["ChartSeries2"]="#107C10", ["ChartSeries3"]="#C77A00", ["ChartSeries4"]="#C42B1C", ["ChartSeries5"]="#7B61FF", ["ChartSeries6"]="#008272", ["ChartSeries7"]="#B146C2", ["ChartSeries8"]="#2369A7",
    };

    private static readonly IReadOnlyDictionary<string, string> DarkBase = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AppBackground"]="#202020", ["ShellBackground"]="#202020", ["Surface"]="#2B2B2B", ["SurfaceRaised"]="#333333", ["SurfaceSunken"]="#252525", ["SurfaceHover"]="#3A3A3A", ["SurfacePressed"]="#454545",
        ["TextPrimary"]="#F5F5F5", ["TextSecondary"]="#C8C8C8", ["TextTertiary"]="#A8A8A8", ["TextDisabled"]="#777777", ["TextOnAccent"]="#FFFFFF", ["TextOnDanger"]="#FFFFFF",
        ["BorderSubtle"]="#3A3A3A", ["BorderDefault"]="#515151", ["BorderStrong"]="#6FAEE8", ["FocusBorder"]="#4CC2FF", ["FocusRing"]="#4CC2FF",
        ["Accent"]="#4CC2FF", ["AccentHover"]="#70D0FF", ["AccentPressed"]="#249DDB", ["AccentMuted"]="#174665", ["SelectionBackground"]="#174665", ["SelectionForeground"]="#FFFFFF",
        ["Success"]="#6CCB5F", ["SuccessMuted"]="#183C1B", ["Warning"]="#FFD166", ["WarningMuted"]="#4A3B14", ["Danger"]="#FF7262", ["DangerHover"]="#FF8C80", ["DangerPressed"]="#D94D40", ["Info"]="#6AB8FF",
        ["TaskbarBackground"]="#242424", ["TaskbarForeground"]="#F5F5F5", ["StartMenuBackground"]="#2B2B2B", ["WindowFrameBackground"]="#2B2B2B", ["WindowTitleBarBackground"]="#333333", ["WindowTitleForeground"]="#F5F5F5", ["WindowInactiveTitleForeground"]="#B0B0B0",
        ["OverlayScrim"]="#99000000", ["Shadow"]="#66000000", ["DesktopIconHover"]="#334CC2FF", ["DesktopIconSelected"]="#554CC2FF",
        ["CardShadow"]="#66000000", ["FlyoutShadow"]="#66000000", ["DialogScrim"]="#66000000", ["ChartGridLine"]="#515151",
        ["ChartSeries1"]="#4CC2FF", ["ChartSeries2"]="#6CCB5F", ["ChartSeries3"]="#FFD166", ["ChartSeries4"]="#FF7262", ["ChartSeries5"]="#B9A7FF", ["ChartSeries6"]="#4FD1C5", ["ChartSeries7"]="#E9A8F2", ["ChartSeries8"]="#6AB8FF",
    };

    private static readonly IReadOnlyDictionary<string, string> NordLight = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["AppBackground"]="#ECEFF4", ["ShellBackground"]="#E5E9F0", ["Surface"]="#FFFFFF", ["SurfaceRaised"]="#F8FAFC", ["SurfaceSunken"]="#E5E9F0", ["TextPrimary"]="#2E3440", ["TextSecondary"]="#4C566A", ["TextTertiary"]="#5E6A7D", ["BorderDefault"]="#D8DEE9", ["Accent"]="#5E81AC", ["Info"]="#88C0D0", ["Success"]="#A3BE8C", ["Warning"]="#EBCB8B", ["Danger"]="#BF616A" };
    private static readonly IReadOnlyDictionary<string, string> NordDark = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["AppBackground"]="#2E3440", ["ShellBackground"]="#2E3440", ["Surface"]="#3B4252", ["SurfaceRaised"]="#434C5E", ["SurfaceSunken"]="#2E3440", ["TextPrimary"]="#ECEFF4", ["TextSecondary"]="#D8DEE9", ["TextTertiary"]="#B8C2D2", ["BorderDefault"]="#4C566A", ["Accent"]="#5E81AC", ["Info"]="#88C0D0", ["Success"]="#A3BE8C", ["Warning"]="#EBCB8B", ["Danger"]="#BF616A" };
    private static readonly IReadOnlyDictionary<string, string> CatppuccinLight = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["AppBackground"]="#EFF1F5", ["ShellBackground"]="#E6E9EF", ["Surface"]="#FFFFFF", ["SurfaceRaised"]="#F7F8FB", ["SurfaceSunken"]="#E6E9EF", ["TextPrimary"]="#4C4F69", ["TextSecondary"]="#6C6F85", ["TextTertiary"]="#7C7F93", ["BorderDefault"]="#CCD0DA", ["Accent"]="#1E66F5", ["Info"]="#04A5E5", ["Success"]="#40A02B", ["Warning"]="#DF8E1D", ["Danger"]="#D20F39" };
    private static readonly IReadOnlyDictionary<string, string> CatppuccinDark = new Dictionary<string, string>(StringComparer.Ordinal)
    { ["AppBackground"]="#1E1E2E", ["ShellBackground"]="#1E1E2E", ["Surface"]="#313244", ["SurfaceRaised"]="#45475A", ["SurfaceSunken"]="#181825", ["TextPrimary"]="#CDD6F4", ["TextSecondary"]="#BAC2DE", ["TextTertiary"]="#A6ADC8", ["BorderDefault"]="#585B70", ["Accent"]="#89B4FA", ["Info"]="#89DCEB", ["Success"]="#A6E3A1", ["Warning"]="#F9E2AF", ["Danger"]="#F38BA8" };

    public static Dictionary<string, string> Resolve(ThemePreferencesDto? preferences, bool dark)
    {
        var result = new Dictionary<string, string>(dark ? DarkBase : LightBase, StringComparer.Ordinal);
        var source = preferences ?? ThemePreferencesDto.Default;
        var palette = source.PaletteId switch
        {
            "builtin:nord" => dark ? NordDark : NordLight,
            "builtin:catppuccin" => dark ? CatppuccinDark : CatppuccinLight,
            _ when source.PaletteId.StartsWith("custom:", StringComparison.Ordinal) => ResolveCustom(source, dark),
            _ => null,
        };
        if (palette is not null)
            Overlay(result, palette);
        else
            ApplyDerivedRoles(result);
        if (!string.IsNullOrWhiteSpace(source.AccentOverride) && IsColor(source.AccentOverride))
        {
            result["Accent"] = Normalize(source.AccentOverride);
            ApplyDerivedRoles(result);
        }
        return result;
    }

    public static bool IsComplete(IReadOnlyDictionary<string, string> colors) => ThemePaletteContract.RequiredColorTokens.All(key => colors.TryGetValue(key, out var value) && IsColor(value));
    public static bool IsColor(string? value) => value is { Length: 7 or 9 } && value[0] == '#' && value[1..].All(Uri.IsHexDigit);
    public static string Normalize(string value) => value.ToUpperInvariant();

    private static IReadOnlyDictionary<string, string>? ResolveCustom(ThemePreferencesDto preferences, bool dark)
    {
        var id = preferences.PaletteId["custom:".Length..];
        var palette = preferences.CustomPalettes?.FirstOrDefault(x => x.Id == id);
        if (palette is null) return null;
        return dark ? palette.DarkColors : palette.LightColors;
    }

    private static void Overlay(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var (key, value) in source)
            if (ThemePaletteContract.ColorTokens.Contains(key) && IsColor(value)) target[key] = Normalize(value);
        ApplyDerivedRoles(target);
    }

    private static void ApplyDerivedRoles(Dictionary<string, string> target)
    {
        var accent = target["Accent"];
        target["AccentHover"] = Adjust(accent, 0.12);
        target["AccentPressed"] = Adjust(accent, -0.16);
        target["AccentMuted"] = Blend(accent, target["SurfaceRaised"], 0.15);
        target["SelectionBackground"] = Blend(accent, target["SurfaceRaised"], 0.20);
        target["SelectionForeground"] = BestForeground(target["SelectionBackground"]);
        var focus = EnsureContrast(accent, target["Surface"], 3.0);
        target["FocusBorder"] = focus;
        target["FocusRing"] = focus;
        target["TextOnAccent"] = BestForeground(accent);
        target["TextOnDanger"] = BestForeground(target["Danger"]);
    }

    private static string BestForeground(string background) => Contrast("#000000", background) >= Contrast("#FFFFFF", background) ? "#000000" : "#FFFFFF";
    private static string EnsureContrast(string foreground, string background, double minimum) => Contrast(foreground, background) >= minimum ? foreground : BestForeground(background);
    private static string Blend(string foreground, string background, double alpha)
    {
        var fg = Parse(foreground); var bg = Parse(background);
        return $"#{(byte)Math.Round(fg.R * alpha + bg.R * (1 - alpha)):X2}{(byte)Math.Round(fg.G * alpha + bg.G * (1 - alpha)):X2}{(byte)Math.Round(fg.B * alpha + bg.B * (1 - alpha)):X2}";
    }
    private static string Adjust(string color, double amount)
    {
        var c = Parse(color); byte Shift(byte v) => (byte)Math.Clamp(Math.Round(v + (amount < 0 ? v : 255 - v) * amount), 0, 255);
        return $"#{Shift(c.R):X2}{Shift(c.G):X2}{Shift(c.B):X2}";
    }
    private static (byte R, byte G, byte B) Parse(string color) =>
        ((byte)Convert.ToInt32(color[^6..^4], 16), (byte)Convert.ToInt32(color[^4..^2], 16), (byte)Convert.ToInt32(color[^2..], 16));
    private static double Contrast(string first, string second)
    {
        static double Luminance(string color)
        {
            var (r, g, b) = Parse(color);
            static double Channel(byte value) { var c = value / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Channel(r) + .7152 * Channel(g) + .0722 * Channel(b);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}

/// <summary>Accessibility validation for fully-resolved palettes.</summary>
public static class ThemePaletteValidator
{
    public static bool TryValidate(IReadOnlyDictionary<string, string> colors, out string? failingToken)
    {
        failingToken = null;
        if (!ThemePaletteDefaults.IsComplete(colors)) { failingToken = "required-token"; return false; }
        foreach (var (foreground, background, minimum) in new[]
        {
            ("TextPrimary", "Surface", 4.5), ("TextSecondary", "Surface", 4.5), ("TextOnAccent", "Accent", 4.5),
            ("TextOnDanger", "Danger", 4.5), ("SelectionForeground", "SelectionBackground", 4.5), ("FocusRing", "Surface", 3.0),
        })
        {
            if (colors[foreground].Length != 7 || colors[background].Length != 7) { failingToken = foreground; return false; }
            if (Contrast(colors[foreground], colors[background]) < minimum) { failingToken = foreground; return false; }
        }
        return true;
    }

    private static double Contrast(string first, string second)
    {
        static double Luminance(string color)
        {
            static double Channel(string hex) { var c = Convert.ToInt32(hex, 16) / 255d; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
            return .2126 * Channel(color[^6..^4]) + .7152 * Channel(color[^4..^2]) + .0722 * Channel(color[^2..]);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
