using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services.Theming;

/// <summary>Applies a fully validated workspace palette to the live Avalonia resource graph.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private ResourceDictionary _paletteResources = new();
    private ThemeKind _mode = ThemeKind.Light;
    private ThemePreferencesDto _preferences = ThemePreferencesDto.Default;

    public ThemeService(Application application)
    {
        _application = application;
        _application.Resources.MergedDictionaries.Add(_paletteResources);
        _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Apply(ThemeKind.Light, ThemePreferencesDto.Default);
    }

    public void Apply(ThemeKind mode, ThemePreferencesDto? preferences)
    {
        _mode = mode;
        _preferences = preferences ?? ThemePreferencesDto.Default;
        if (Dispatcher.UIThread.CheckAccess()) ApplyCore();
        else Dispatcher.UIThread.Post(ApplyCore);
    }

    private void ApplyCore()
    {
        _application.RequestedThemeVariant = _mode switch
        {
            ThemeKind.Dark => ThemeVariant.Dark,
            ThemeKind.System => ThemeVariant.Default,
            _ => ThemeVariant.Light,
        };

        var dark = _mode == ThemeKind.Dark || (_mode == ThemeKind.System && _application.ActualThemeVariant == ThemeVariant.Dark);
        var colors = ThemePaletteDefaults.Resolve(_preferences, dark);
        if (!ThemePaletteValidator.TryValidate(colors, out _))
            colors = ThemePaletteDefaults.Resolve(ThemePreferencesDto.Default, dark);

        var next = new ResourceDictionary();
        foreach (var (name, value) in colors)
            next[name + "Color"] = Color.Parse(value);

        // Swap a complete provider instead of clearing then repopulating the active one.
        _application.Resources.MergedDictionaries.Add(next);
        _application.Resources.MergedDictionaries.Remove(_paletteResources);
        _paletteResources = next;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_mode == ThemeKind.System) ApplyCore();
    }

    public void Dispose() => _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
}
