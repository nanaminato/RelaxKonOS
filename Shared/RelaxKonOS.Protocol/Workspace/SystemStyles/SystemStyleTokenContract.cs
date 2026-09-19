namespace RelaxKonOS.Protocol.Workspace.SystemStyles;

/// <summary>How the host materialises a numeric system-style token into an Avalonia resource.</summary>
public enum SystemStyleTokenKind
{
    /// <summary>Plain <c>double</c> (<c>x:Double</c>).</summary>
    Number,

    /// <summary>Degree used as a corner radius; emitted as <c>CornerRadius</c>.</summary>
    CornerRadius,

    /// <summary>Degree used as a uniform border/edge thickness; emitted as <c>Thickness</c>.</summary>
    Thickness,

    /// <summary>Milliseconds; emitted as a duration resource.</summary>
    Duration,

    /// <summary>0..1 alpha multiplier.</summary>
    Opacity,
}

/// <summary>One admissible system-style token: its stable key, kind, accepted range and fallback.</summary>
public sealed record SystemStyleTokenDefinition(
    string Key,
    SystemStyleTokenKind Kind,
    double Minimum,
    double Maximum,
    double Default);

/// <summary>
/// The closed vocabulary of system-style shape tokens. Colour roles stay in
/// <see cref="ThemePaletteContract"/>: a system style decides how parts are arranged, sized and
/// animated, never which colours are used. Everything a style can change is declared here, with an
/// explicit range so a manifest cannot smuggle in a layout-breaking value.
/// </summary>
public static class SystemStyleTokenContract
{
    private static readonly SystemStyleTokenDefinition[] Definitions =
    [
        // ── Shared density and feedback ──
        new("ControlHeight", SystemStyleTokenKind.Number, 20, 64, 32),
        new("ControlCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 24, 4),
        new("ControlBorderThickness", SystemStyleTokenKind.Thickness, 0, 4, 1),
        new("OverlayCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 8),
        new("FocusRingThickness", SystemStyleTokenKind.Thickness, 0, 6, 2),
        new("MinimumHitTarget", SystemStyleTokenKind.Number, 28, 64, 40),
        new("TransitionFast", SystemStyleTokenKind.Duration, 0, 1000, 120),
        new("ReducedMotionDuration", SystemStyleTokenKind.Duration, 0, 1000, 0),

        // ── Managed window chrome ──
        new("WindowTitleBarHeight", SystemStyleTokenKind.Number, 24, 96, 42),
        new("WindowFrameThickness", SystemStyleTokenKind.Thickness, 0, 8, 1),
        new("WindowCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 8),
        new("WindowControlWidth", SystemStyleTokenKind.Number, 28, 96, 52),
        new("WindowInactiveOpacity", SystemStyleTokenKind.Opacity, 0.2, 1, 0.55),
        new("WindowShadowDepth", SystemStyleTokenKind.Number, 0, 64, 28),
        new("WindowShadowOpacity", SystemStyleTokenKind.Opacity, 0, 1, 0.4),

        // ── Command menus (desktop and in-app) ──
        new("MenuCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 6),
        new("MenuBorderThickness", SystemStyleTokenKind.Thickness, 0, 4, 1),
        new("MenuItemHeight", SystemStyleTokenKind.Number, 20, 64, 30),
        new("MenuPadding", SystemStyleTokenKind.Thickness, 0, 24, 6),
        new("MenuSubmenuDelay", SystemStyleTokenKind.Duration, 0, 1000, 250),

        // ── Flyouts, dialogs and modality ──
        new("FlyoutCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 8),
        new("FlyoutElevation", SystemStyleTokenKind.Number, 0, 64, 12),
        new("DialogCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 8),
        new("DialogMotionDuration", SystemStyleTokenKind.Duration, 0, 1000, 160),

        // ── Desktop chrome owned by a shell ──
        new("TaskbarHeight", SystemStyleTokenKind.Number, 32, 96, 48),
        new("TopBarHeight", SystemStyleTokenKind.Number, 20, 64, 30),
        new("LauncherCornerRadius", SystemStyleTokenKind.CornerRadius, 0, 40, 12),
        new("DockMagnification", SystemStyleTokenKind.Number, 1, 2, 1.25),
        new("TaskbarIconSize", SystemStyleTokenKind.Number, 24, 64, 40),

        // ── Window overview / task switcher ──
        new("OverviewCardRadius", SystemStyleTokenKind.CornerRadius, 0, 32, 10),
        new("OverviewCardBorderThickness", SystemStyleTokenKind.Thickness, 0, 6, 2),
        new("OverviewThumbnailScale", SystemStyleTokenKind.Number, 0.4, 1.5, 1),
        new("OverviewEnterDuration", SystemStyleTokenKind.Duration, 0, 1000, 180),
        new("OverviewSelectionRingThickness", SystemStyleTokenKind.Thickness, 0, 8, 3),
    ];

    private static readonly Dictionary<string, SystemStyleTokenDefinition> ByKey =
        Definitions.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    public static IReadOnlyList<SystemStyleTokenDefinition> All { get; } = Definitions;

    /// <summary>Tokens every style must supply. Optional tokens fall back to their default.</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        "ControlHeight", "ControlCornerRadius", "ControlBorderThickness", "OverlayCornerRadius", "FocusRingThickness", "MinimumHitTarget",
        "TransitionFast", "ReducedMotionDuration",
        "WindowTitleBarHeight", "WindowFrameThickness", "WindowCornerRadius", "WindowControlWidth", "WindowInactiveOpacity",
        "WindowShadowDepth", "WindowShadowOpacity",
        "MenuCornerRadius", "MenuBorderThickness", "MenuItemHeight", "MenuPadding",
        "FlyoutCornerRadius", "FlyoutElevation", "DialogCornerRadius", "DialogMotionDuration",
        "TaskbarHeight", "TopBarHeight", "LauncherCornerRadius",
        "OverviewCardRadius", "OverviewCardBorderThickness", "OverviewEnterDuration", "OverviewSelectionRingThickness",
    ];

    /// <summary>The lowest hit target the host accepts for any interactive system component.</summary>
    public const double AbsoluteMinimumHitTarget = 40;

    public static bool TryGet(string? key, out SystemStyleTokenDefinition definition)
    {
        definition = null!;
        return !string.IsNullOrEmpty(key) && ByKey.TryGetValue(key, out definition!);
    }

    public static bool IsKnown(string? key) => !string.IsNullOrEmpty(key) && ByKey.ContainsKey(key);

    public static double DefaultOf(string key) => ByKey.TryGetValue(key, out var definition) ? definition.Default : 0;

    public static bool IsInRange(string key, double value) =>
        ByKey.TryGetValue(key, out var definition) && value >= definition.Minimum && value <= definition.Maximum;
}
