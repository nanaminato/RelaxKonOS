using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Protocol.Workspace.SystemStyles;

namespace RelaxKonOS.UI.Themes.SystemStyle;

/// <summary>Resource keys the host exposes for the active recipe selection.</summary>
public static class SystemStyleRecipeKeys
{
    public const string WindowChrome = "SystemStyle.WindowChrome";
    public const string ContextMenu = "SystemStyle.ContextMenu";
    public const string TaskSwitcher = "SystemStyle.TaskSwitcher";
    public const string ShellChrome = "SystemStyle.ShellChrome";

    /// <summary>The four slots a style must fill, paired with their resource key.</summary>
    public static IReadOnlyList<(string Kind, string Key)> All { get; } =
    [
        (SystemStyleRecipeKinds.WindowChrome, WindowChrome),
        (SystemStyleRecipeKinds.ContextMenu, ContextMenu),
        (SystemStyleRecipeKinds.TaskSwitcher, TaskSwitcher),
        (SystemStyleRecipeKinds.ShellChrome, ShellChrome),
    ];
}

/// <summary>
/// Resource keys the host derives from a primary token so XAML can consume a ready-made
/// composite. They are pure functions of the token set, but Avalonia resources cannot express
/// "three corners of this radius", so the host materialises them here instead of letting a
/// view hardcode the missing dimension.
/// </summary>
public static class SystemStyleDerivedKeys
{
    /// <summary><c>CornerRadius(r, r, 0, 0)</c> from <c>OverlayCornerRadius</c>.</summary>
    public const string OverlayTopCornerRadius = "OverlayTopCornerRadius";

    /// <summary><c>CornerRadius(0, 0, r, r)</c> from <c>OverlayCornerRadius</c>.</summary>
    public const string OverlayBottomCornerRadius = "OverlayBottomCornerRadius";

    /// <summary>Soft shadow for cards, menus, flyouts and dialogs, from <c>FlyoutElevation</c>.</summary>
    public const string ElevationShadow = "ElevationShadow";

    /// <summary>
    /// Fully-rounded caption-button radius for the left-side (traffic-light) chrome variant: half
    /// of the smaller of the control width and the title-bar height, so the button stays a
    /// well-formed pill at every profile's proportions.
    /// </summary>
    public const string WindowCaptionCornerRadius = "WindowCaptionCornerRadius";

    /// <summary>
    /// Top inset that reserves room for the host window's own title bar. The host shell uses the
    /// same <c>WindowTitleBarHeight</c> token as a managed window, so the outer frame and the
    /// managed windows it contains never disagree about how tall a title bar is.
    /// </summary>
    public const string HostTitleBarMargin = "HostTitleBarMargin";

    public static IReadOnlyList<string> All { get; } =
        [OverlayTopCornerRadius, OverlayBottomCornerRadius, ElevationShadow, WindowCaptionCornerRadius, HostTitleBarMargin];
}

/// <summary>
/// Fluent theme keys whose value is a <see cref="Thickness"/> or a plain number.
/// A brush key can carry <c>Color="{DynamicResource ...}"</c> from AXAML, but a thickness cannot,
/// so the host materialises these from the style tokens plus the style's <c>contextMenu</c> recipe.
/// They are the only place a Fluent-named key is written outside the bridge dictionary.
/// </summary>
public static class SystemStyleCommandSurfaceKeys
{
    public const string MenuPresenterBorderThickness = "MenuFlyoutPresenterBorderThemeThickness";
    public const string MenuPresenterPadding = "MenuFlyoutPresenterThemePadding";
    public const string MenuScrollerMargin = "MenuFlyoutScrollerMargin";
    public const string MenuItemPadding = "MenuFlyoutItemThemePaddingNarrow";
    public const string MenuSeparatorHeight = "MenuFlyoutSeparatorThemeHeight";
    public const string MenuSeparatorPadding = "MenuFlyoutSeparatorThemePadding";
    public const string ToolTipBorderThickness = "ToolTipBorderThemeThickness";
    public const string ToolTipPadding = "ToolTipBorderThemePadding";
    public const string FlyoutBorderThickness = "FlyoutBorderThemeThickness";
    public const string FlyoutBorderPadding = "FlyoutBorderThemePadding";
    public const string FlyoutContentPadding = "FlyoutContentThemePadding";

    public static IReadOnlyList<string> All { get; } =
    [
        MenuPresenterBorderThickness, MenuPresenterPadding, MenuScrollerMargin, MenuItemPadding,
        MenuSeparatorHeight, MenuSeparatorPadding,
        ToolTipBorderThickness, ToolTipPadding,
        FlyoutBorderThickness, FlyoutBorderPadding, FlyoutContentPadding,
    ];
}

/// <summary>
/// Turns a validated, data-only style manifest into a complete Avalonia resource provider.
/// The provider is always built in full before it is installed, so a style switch can never leave
/// the UI with a half-applied token set. Colour values are never read from the manifest: the window
/// shadow is composed from the *current palette's* shadow colour plus the style's numeric tokens.
/// </summary>
public static class SystemStyleResourceBuilder
{
    /// <summary>Builds every token for the requested colour mode.</summary>
    /// <param name="manifest">A manifest that has already passed <see cref="SystemStyleManifestValidator"/>.</param>
    /// <param name="dark">Whether the resolved colour mode is dark.</param>
    /// <param name="paletteShadow">The palette's <c>Shadow</c> colour, used for the window shadow.</param>
    /// <param name="reducedMotion">When true, every motion duration collapses to the style's reduced-motion value.</param>
    public static ResourceDictionary Build(
        SystemStyleManifestDto manifest,
        bool dark,
        string paletteShadow,
        bool reducedMotion)
    {
        var tokens = manifest.ResolveTokens(dark);
        var dictionary = new ResourceDictionary();

        foreach (var definition in SystemStyleTokenContract.All)
        {
            var value = tokens.TryGetValue(definition.Key, out var declared) ? declared : definition.Default;
            if (reducedMotion && definition.Kind == SystemStyleTokenKind.Duration)
                value = tokens.TryGetValue("ReducedMotionDuration", out var reduced) ? reduced : 0;
            dictionary[definition.Key] = Materialize(definition, value);
        }

        dictionary["WindowShadow"] = BuildWindowShadow(tokens, paletteShadow);

        // Derived composites: XAML can reference a token directly, but it cannot build
        // "only the bottom two corners are rounded" or "a shadow from depth + palette" by itself.
        var overlayRadius = tokens.TryGetValue("OverlayCornerRadius", out var declaredOverlay) ? declaredOverlay : 8;
        dictionary[SystemStyleDerivedKeys.OverlayTopCornerRadius] = new CornerRadius(overlayRadius, overlayRadius, 0, 0);
        dictionary[SystemStyleDerivedKeys.OverlayBottomCornerRadius] = new CornerRadius(0, 0, overlayRadius, overlayRadius);
        dictionary[SystemStyleDerivedKeys.ElevationShadow] = BuildElevationShadow(tokens, paletteShadow);

        var controlWidth = tokens.TryGetValue("WindowControlWidth", out var declaredControlWidth) ? declaredControlWidth : 52;
        var titleBarHeight = tokens.TryGetValue("WindowTitleBarHeight", out var declaredTitleBarHeight) ? declaredTitleBarHeight : 42;
        dictionary[SystemStyleDerivedKeys.WindowCaptionCornerRadius] =
            new CornerRadius(Math.Min(controlWidth, titleBarHeight) / 2);
        dictionary[SystemStyleDerivedKeys.HostTitleBarMargin] = new Thickness(0, titleBarHeight, 0, 0);

        var recipes = manifest.SupportedRecipes;
        BuildCommandSurfaceThicknessKeys(dictionary, tokens, recipes.ContextMenu);

        dictionary[SystemStyleRecipeKeys.WindowChrome] = recipes.WindowChrome;
        dictionary[SystemStyleRecipeKeys.ContextMenu] = recipes.ContextMenu;
        dictionary[SystemStyleRecipeKeys.TaskSwitcher] = recipes.TaskSwitcher;
        dictionary[SystemStyleRecipeKeys.ShellChrome] = recipes.ShellChrome;

        return dictionary;
    }

    private static object Materialize(SystemStyleTokenDefinition definition, double value) => definition.Kind switch
    {
        SystemStyleTokenKind.CornerRadius => new CornerRadius(value),
        SystemStyleTokenKind.Thickness => new Thickness(value),
        SystemStyleTokenKind.Duration => TimeSpan.FromMilliseconds(value),
        _ => value,
    };

    /// <summary>
    /// A single soft drop shadow whose depth comes from the style and whose colour comes from the
    /// palette, so a style can change how heavy a window feels without touching the colour contract.
    /// </summary>
    private static BoxShadows BuildWindowShadow(IReadOnlyDictionary<string, double> tokens, string paletteShadow)
    {
        var depth = tokens.TryGetValue("WindowShadowDepth", out var declaredDepth) ? declaredDepth : 28;
        var opacity = tokens.TryGetValue("WindowShadowOpacity", out var declaredOpacity) ? declaredOpacity : 0.4;
        var color = Color.Parse(paletteShadow);
        // The declared opacity is relative to the reference 0.4: a style may lighten or darken the
        // palette's shadow colour, but it can never introduce a colour of its own.
        var alpha = (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(opacity / 0.4, 0, 1)), 0, 255);
        return SoftShadow(depth, color, alpha);
    }

    /// <summary>Card / menu / flyout / dialog shadow: elevation from the style, colour from the palette.</summary>
    private static BoxShadows BuildElevationShadow(IReadOnlyDictionary<string, double> tokens, string paletteShadow)
    {
        var elevation = tokens.TryGetValue("FlyoutElevation", out var declared) ? declared : 12;
        var color = Color.Parse(paletteShadow);
        // Elevation is a purely visual weight, so it does not scale the palette alpha.
        return SoftShadow(elevation, color, color.A);
    }

    private static BoxShadows SoftShadow(double depth, Color color, byte alpha) =>
        new(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = Math.Round(depth / 2.3),
            Blur = depth,
            Spread = 0,
            Color = Color.FromArgb(alpha, color.R, color.G, color.B),
        });

    /// <summary>
    /// Re-points the command-surface thickness keys at the style's menu tokens, so one style
    /// switch changes the density of every context menu, submenu, tooltip and flyout without any
    /// application participating. See <see cref="SystemStyleCommandSurfaceKeys"/>.
    /// </summary>
    /// <remarks>
    /// The <c>contextMenu</c> recipe then scales that result, which is how the three recipes become
    /// three visibly different menus while every application keeps creating a plain
    /// <c>ContextMenu</c>. What a recipe may tune here is density, inset and border weight only:
    /// corner radius comes from the style's own <c>MenuCornerRadius</c>/<c>FlyoutCornerRadius</c>
    /// tokens, and it cannot be overridden per recipe because the Fluent presenter takes its radius
    /// from one shared overlay key. Replacing that template to gain a per-recipe radius is
    /// deliberately out of bounds - it would cost submenu, keyboard and light-dismiss behaviour.
    /// </remarks>
    private static void BuildCommandSurfaceThicknessKeys(
        ResourceDictionary dictionary,
        IReadOnlyDictionary<string, double> tokens,
        string contextMenuRecipe)
    {
        double Token(string key, double fallback) => tokens.TryGetValue(key, out var value) ? value : fallback;

        // borderScale, paddingScale, extra item inset, separator gap scale.
        var (borderScale, paddingScale, itemExtra, separatorScale) = contextMenuRecipe switch
        {
            ContextMenuRecipes.CompactCommandMenu => (1d, 1d, 0d, 1d),
            ContextMenuRecipes.RoundedCommandMenu => (1d, 1.35d, 4d, 1.5d),
            ContextMenuRecipes.GnomePopoverMenu => (0d, 1.6d, 8d, 2d),
            // A validated manifest can only carry a closed-set value; the default keeps a
            // hand-constructed manifest on the same path instead of throwing.
            _ => (1d, 1d, 0d, 1d),
        };

        var border = Token("MenuBorderThickness", 1) * borderScale;
        var padding = Math.Round(Token("MenuPadding", 6) * paddingScale);
        var flyoutRadius = Token("FlyoutCornerRadius", 8);

        // Horizontal item inset grows with the menu's own padding so the two never disagree.
        var itemInset = 6 + padding + itemExtra;
        // A separator is a hairline separated from both neighbours by half the menu padding,
        // but never closer than two pixels so it does not read as a border.
        var separatorGap = Math.Max(2, Math.Round(padding / 2 * separatorScale));
        // Flyout content inset follows the flyout corner radius: a rounder surface needs more room
        // before its content starts, or the radius visually clips the first line of text.
        var flyoutInset = Math.Max(10, flyoutRadius);

        dictionary[SystemStyleCommandSurfaceKeys.MenuPresenterBorderThickness] = new Thickness(border);
        dictionary[SystemStyleCommandSurfaceKeys.MenuPresenterPadding] = new Thickness(padding);
        // The presenter owns the outer inset; the scroller must not add a second one.
        dictionary[SystemStyleCommandSurfaceKeys.MenuScrollerMargin] = new Thickness(0);
        dictionary[SystemStyleCommandSurfaceKeys.MenuItemPadding] = new Thickness(itemInset, 0);
        dictionary[SystemStyleCommandSurfaceKeys.MenuSeparatorHeight] = 1d;
        dictionary[SystemStyleCommandSurfaceKeys.MenuSeparatorPadding] = new Thickness(0, separatorGap, 0, separatorGap);
        dictionary[SystemStyleCommandSurfaceKeys.ToolTipBorderThickness] = new Thickness(border);
        dictionary[SystemStyleCommandSurfaceKeys.ToolTipPadding] = new Thickness(itemInset, padding);
        dictionary[SystemStyleCommandSurfaceKeys.FlyoutBorderThickness] = new Thickness(border);
        dictionary[SystemStyleCommandSurfaceKeys.FlyoutBorderPadding] = new Thickness(flyoutInset);
        dictionary[SystemStyleCommandSurfaceKeys.FlyoutContentPadding] = new Thickness(flyoutInset);
    }
}
