namespace RelaxKonOS.Protocol.Workspace;

/// <summary>
/// Stable, semantic colour keys shared by palette persistence, validation and rendering.
/// These are deliberately presentation roles rather than named colours.
/// </summary>
public static class ThemePaletteContract
{
    public static IReadOnlySet<string> ColorTokens { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AppBackground", "ShellBackground", "Surface", "SurfaceRaised", "SurfaceSunken", "SurfaceHover", "SurfacePressed",
        "TextPrimary", "TextSecondary", "TextTertiary", "TextDisabled", "TextOnAccent", "TextOnDanger",
        "BorderSubtle", "BorderDefault", "BorderStrong", "FocusBorder", "FocusRing",
        "Accent", "AccentHover", "AccentPressed", "AccentMuted", "SelectionBackground", "SelectionForeground",
        "Success", "SuccessMuted", "Warning", "WarningMuted", "Danger", "DangerHover", "DangerPressed", "Info",
        "TaskbarBackground", "TaskbarForeground", "StartMenuBackground", "WindowFrameBackground", "WindowTitleBarBackground",
        "WindowTitleForeground", "WindowInactiveTitleForeground", "OverlayScrim", "Shadow", "DesktopIconHover", "DesktopIconSelected",
        "CardShadow", "FlyoutShadow", "DialogScrim", "ChartGridLine", "ChartSeries1", "ChartSeries2", "ChartSeries3", "ChartSeries4",
        "ChartSeries5", "ChartSeries6", "ChartSeries7", "ChartSeries8",
    };

    public static IReadOnlyList<string> RequiredColorTokens { get; } = ColorTokens
        .Where(key => key is not ("CardShadow" or "FlyoutShadow" or "DialogScrim" or "ChartGridLine" or
            "ChartSeries1" or "ChartSeries2" or "ChartSeries3" or "ChartSeries4" or "ChartSeries5" or "ChartSeries6" or "ChartSeries7" or "ChartSeries8"))
        .ToArray();
}
