namespace RelaxKonOS.Protocol.Workspace;

/// <summary>Device-rendering contrast defaults. Never serialize these over Workspace appearance.</summary>
public static class AccessibilityPaletteDefaults
{
    public static Dictionary<string, string> Create(IReadOnlyDictionary<string, string> source)
    {
        var colors = source.ToDictionary(item => item.Key, item => item.Value);
        foreach (var key in new[] { "AppBackground", "ShellBackground", "Surface", "SurfaceRaised", "SurfaceSunken",
            "TaskbarBackground", "StartMenuBackground", "WindowFrameBackground", "WindowTitleBarBackground" })
            colors[key] = "#000000";
        foreach (var key in new[] { "TextPrimary", "TextSecondary", "TextTertiary", "TaskbarForeground", "WindowTitleForeground",
            "WindowInactiveTitleForeground", "BorderSubtle", "BorderDefault", "BorderStrong", "ChartGridLine" })
            colors[key] = "#FFFFFF";
        foreach (var key in new[] { "Accent", "AccentHover", "AccentPressed", "FocusBorder", "FocusRing" })
            colors[key] = "#FFFF00";
        foreach (var key in new[] { "SurfaceHover", "SurfacePressed", "AccentMuted", "SelectionBackground", "SuccessMuted", "WarningMuted" })
            colors[key] = "#333333";
        colors["SelectionForeground"] = "#FFFFFF";
        colors["TextOnAccent"] = "#000000";
        colors["TextDisabled"] = "#BFBFBF";
        colors["Success"] = "#7FFF7F";
        colors["Warning"] = "#FFFF00";
        colors["Danger"] = "#FF8080";
        colors["DangerHover"] = "#FF9999";
        colors["DangerPressed"] = "#FF6666";
        colors["TextOnDanger"] = "#000000";
        colors["Info"] = "#80DFFF";
        return colors;
    }

}
