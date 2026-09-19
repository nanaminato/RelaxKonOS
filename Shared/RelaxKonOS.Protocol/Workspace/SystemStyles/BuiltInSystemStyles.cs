namespace RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>
/// The styles RelaxKonOS ships itself, as plain data. They are the reference profiles every
/// external manifest is measured against: same schema, same validator, same token vocabulary.
/// A style never carries colours — the palette stays an independent user choice.
/// </summary>
public static class BuiltInSystemStyles
{
    public const string Source = SystemStyleSources.BuiltIn;

    public static SystemStyleManifestDto WindowsLike { get; } = new()
    {
        Id = SystemStyleIds.WindowsLike,
        DisplayName = "Windows-like",
        SupportedRecipes = new SystemStyleRecipeSelectionDto
        {
            WindowChrome = WindowChromeRecipes.CaptionButtonsRight,
            ContextMenu = ContextMenuRecipes.CompactCommandMenu,
            TaskSwitcher = TaskSwitcherRecipes.WindowsGrid,
            ShellChrome = ShellChromeRecipes.BottomTaskbar,
        },
        Tokens = Tokens(
            ("ControlHeight", 32), ("ControlCornerRadius", 4), ("ControlBorderThickness", 1), ("OverlayCornerRadius", 8),
            ("FocusRingThickness", 2), ("MinimumHitTarget", 40), ("TransitionFast", 120), ("ReducedMotionDuration", 0),
            ("WindowTitleBarHeight", 42), ("WindowFrameThickness", 1), ("WindowCornerRadius", 8),
            ("WindowControlWidth", 52), ("WindowInactiveOpacity", 0.55),
            ("WindowShadowDepth", 28), ("WindowShadowOpacity", 0.40),
            ("MenuCornerRadius", 6), ("MenuBorderThickness", 1), ("MenuItemHeight", 30), ("MenuPadding", 6),
            ("MenuSubmenuDelay", 250),
            ("FlyoutCornerRadius", 8), ("FlyoutElevation", 12), ("DialogCornerRadius", 8), ("DialogMotionDuration", 160),
            ("TaskbarHeight", 48), ("TopBarHeight", 30), ("LauncherCornerRadius", 12),
            ("TaskbarIconSize", 40), ("DockMagnification", 1.2),
            ("OverviewCardRadius", 10), ("OverviewCardBorderThickness", 2), ("OverviewThumbnailScale", 1),
            ("OverviewEnterDuration", 180), ("OverviewSelectionRingThickness", 3)),
        Accessibility = new SystemStyleAccessibilityDto { MinimumHitTarget = 40, SupportsReducedMotion = true },
        Source = Source,
    };

    public static SystemStyleManifestDto MacOsLike { get; } = new()
    {
        Id = SystemStyleIds.MacOsLike,
        DisplayName = "macOS-like",
        SupportedRecipes = new SystemStyleRecipeSelectionDto
        {
            WindowChrome = WindowChromeRecipes.TrafficLightsLeft,
            ContextMenu = ContextMenuRecipes.RoundedCommandMenu,
            TaskSwitcher = TaskSwitcherRecipes.MacOsStrip,
            ShellChrome = ShellChromeRecipes.TopMenuPlusDock,
        },
        Tokens = Tokens(
            ("ControlHeight", 30), ("ControlCornerRadius", 6), ("ControlBorderThickness", 0), ("OverlayCornerRadius", 10),
            ("FocusRingThickness", 3), ("MinimumHitTarget", 40), ("TransitionFast", 200), ("ReducedMotionDuration", 0),
            ("WindowTitleBarHeight", 38), ("WindowFrameThickness", 1), ("WindowCornerRadius", 12),
            ("WindowControlWidth", 28), ("WindowInactiveOpacity", 0.6),
            ("WindowShadowDepth", 34), ("WindowShadowOpacity", 0.32),
            ("MenuCornerRadius", 10), ("MenuBorderThickness", 1), ("MenuItemHeight", 28), ("MenuPadding", 8),
            ("MenuSubmenuDelay", 150),
            ("FlyoutCornerRadius", 12), ("FlyoutElevation", 18), ("DialogCornerRadius", 14), ("DialogMotionDuration", 200),
            ("TaskbarHeight", 64), ("TopBarHeight", 28), ("LauncherCornerRadius", 20),
            ("TaskbarIconSize", 48), ("DockMagnification", 1.35),
            ("OverviewCardRadius", 14), ("OverviewCardBorderThickness", 2), ("OverviewThumbnailScale", 1.05),
            ("OverviewEnterDuration", 220), ("OverviewSelectionRingThickness", 3)),
        Accessibility = new SystemStyleAccessibilityDto { MinimumHitTarget = 40, SupportsReducedMotion = true },
        Source = Source,
    };

    public static SystemStyleManifestDto UbuntuLike { get; } = new()
    {
        Id = SystemStyleIds.UbuntuLike,
        DisplayName = "Ubuntu-like",
        SupportedRecipes = new SystemStyleRecipeSelectionDto
        {
            WindowChrome = WindowChromeRecipes.HeaderbarRight,
            ContextMenu = ContextMenuRecipes.GnomePopoverMenu,
            TaskSwitcher = TaskSwitcherRecipes.GnomeOverview,
            ShellChrome = ShellChromeRecipes.TopBarPlusLeftDock,
        },
        Tokens = Tokens(
            ("ControlHeight", 34), ("ControlCornerRadius", 6), ("ControlBorderThickness", 1), ("OverlayCornerRadius", 12),
            ("FocusRingThickness", 2), ("MinimumHitTarget", 44), ("TransitionFast", 150), ("ReducedMotionDuration", 0),
            ("WindowTitleBarHeight", 46), ("WindowFrameThickness", 1), ("WindowCornerRadius", 10),
            ("WindowControlWidth", 44), ("WindowInactiveOpacity", 0.5),
            ("WindowShadowDepth", 24), ("WindowShadowOpacity", 0.30),
            ("MenuCornerRadius", 12), ("MenuBorderThickness", 0), ("MenuItemHeight", 34), ("MenuPadding", 7),
            ("MenuSubmenuDelay", 200),
            ("FlyoutCornerRadius", 12), ("FlyoutElevation", 14), ("DialogCornerRadius", 12), ("DialogMotionDuration", 180),
            ("TaskbarHeight", 72), ("TopBarHeight", 32), ("LauncherCornerRadius", 18),
            ("TaskbarIconSize", 44), ("DockMagnification", 1.2),
            ("OverviewCardRadius", 12), ("OverviewCardBorderThickness", 3), ("OverviewThumbnailScale", 0.95),
            ("OverviewEnterDuration", 200), ("OverviewSelectionRingThickness", 3)),
        Accessibility = new SystemStyleAccessibilityDto { MinimumHitTarget = 44, SupportsReducedMotion = true },
        Source = Source,
    };

    public static IReadOnlyList<SystemStyleManifestDto> All { get; } = [WindowsLike, MacOsLike, UbuntuLike];

    public static SystemStyleManifestDto Default => WindowsLike;

    public static bool TryGet(string? id, out SystemStyleManifestDto manifest)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                manifest = candidate;
                return true;
            }
        }
        manifest = Default;
        return false;
    }

    private static Dictionary<string, double> Tokens(params (string Key, double Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
}
