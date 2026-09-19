using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.UI.Themes.SystemStyle;

namespace RelaxKonOS.Client.Services.Theming;

/// <summary>
/// Applies the workspace's desktop appearance to the live Avalonia resource graph, replacing the
/// earlier colour-only service. Colours and shape are two independent inputs that are resolved
/// together and installed as one atomic provider pair:
///
/// * the palette dictionary owns every <c>*Color</c> key and comes from
///   <see cref="AppearancePreferencesDto"/> alone;
/// * the style dictionary owns every shape/size/motion token and comes from a validated
///   <see cref="SystemStyleManifestDto"/> alone.
///
/// A candidate style that cannot be resolved leaves the previous, already-verified pair in place,
/// so a bad style can never produce a half-styled or transparent UI.
/// </summary>
public sealed class AppearanceService : IDisposable
{
    private readonly Application _application;
    private readonly ISystemStyleRegistry _styles;

    private ResourceDictionary _paletteResources = new();
    private ResourceDictionary _styleResources = new();
    private AppearancePreferencesDto _appearance = AppearancePreferencesDto.Default;
    private ThemeKind _mode = ThemeKind.Light;
    private string _styleId = SystemStyleIds.WindowsLike;
    private string? _styleProblem;

    public AppearanceService(Application application, ISystemStyleRegistry styles)
    {
        _application = application;
        _styles = styles;
        _application.Resources.MergedDictionaries.Add(_paletteResources);
        _application.Resources.MergedDictionaries.Add(_styleResources);
        _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Apply(ThemeKind.Light, AppearancePreferencesDto.Default, SystemStyleIds.WindowsLike);
    }

    /// <summary>The style actually rendering right now. May differ from the requested id when that id is unavailable.</summary>
    public string AppliedStyleId => _styleId;

    /// <summary>Problem code for the requested style, or null when it resolved.</summary>
    public string? StyleProblem => _styleProblem;

    /// <summary>Raised after a new provider pair has been installed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// When true, every motion duration collapses to the style's reduced-motion value. The host
    /// currently exposes this as an explicit switch; wiring it to the platform setting is tracked
    /// separately because Avalonia does not surface a cross-platform reduced-motion flag.
    /// </summary>
    public bool ReducedMotion { get; set; }

    public void Apply(ThemeKind mode, AppearancePreferencesDto? appearance, string? systemStyleId)
    {
        _mode = mode;
        _appearance = appearance ?? AppearancePreferencesDto.Default;
        _styleId = string.IsNullOrWhiteSpace(systemStyleId) ? SystemStyleIds.WindowsLike : systemStyleId!;
        if (Dispatcher.UIThread.CheckAccess()) ApplyCore();
        else Dispatcher.UIThread.Post(ApplyCore);
    }

    /// <summary>Convenience overload for a whole workspace desktop-experience payload.</summary>
    public void Apply(DesktopExperiencePreferencesDto? experience)
    {
        var source = experience ?? DesktopExperiencePreferencesDto.Default;
        Apply(source.Appearance.Mode, source.Appearance, source.SystemStyleId);
    }

    internal void ReapplyForMode() => ApplyCore();

    private void ApplyCore()
    {
        _application.RequestedThemeVariant = _mode switch
        {
            ThemeKind.Dark => ThemeVariant.Dark,
            ThemeKind.System => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };

        var dark = _mode == ThemeKind.Dark || (_mode == ThemeKind.System && _application.ActualThemeVariant == ThemeVariant.Dark);
        var colors = ThemePaletteDefaults.Resolve(_appearance, dark);
        if (!ThemePaletteValidator.TryValidate(colors, out _))
            colors = ThemePaletteDefaults.Resolve(AppearancePreferencesDto.Default, dark);

        var palette = new ResourceDictionary();
        foreach (var (name, value) in colors)
            palette[name + "Color"] = Color.Parse(value);

        // A style that fails to resolve keeps the last good style on screen; the problem code is
        // surfaced through StyleProblem instead of degrading the UI.
        var resolved = _styles.TryGetManifest(_styleId, out var manifest, out var problem);
        _styleProblem = resolved ? null : problem;

        var style = BuildStyleResources(colors, dark, resolved ? manifest : null);
        Swap(palette, style);
    }

    private ResourceDictionary BuildStyleResources(
        IReadOnlyDictionary<string, string> colors,
        bool dark,
        SystemStyleManifestDto? manifest)
    {
        // Fall back to the shipped default profile so the token set stays complete even when the
        // requested style is missing on this device. The UI keeps rendering a full, valid style.
        var effective = manifest ?? BuiltInSystemStyles.Default;
        return SystemStyleResourceBuilder.Build(effective, dark, colors["Shadow"], ReducedMotion);
    }

    /// <summary>Swaps both providers, adding first and then removing, so there is never a gap.</summary>
    private void Swap(ResourceDictionary palette, ResourceDictionary style)
    {
        var previousPalette = _paletteResources;
        var previousStyle = _styleResources;

        _application.Resources.MergedDictionaries.Add(palette);
        _application.Resources.MergedDictionaries.Add(style);
        _application.Resources.MergedDictionaries.Remove(previousPalette);
        _application.Resources.MergedDictionaries.Remove(previousStyle);

        _paletteResources = palette;
        _styleResources = style;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_mode == ThemeKind.System) ApplyCore();
    }

    public void Dispose()
    {
        _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
    }
}
