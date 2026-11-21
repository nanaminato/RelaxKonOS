using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.App.Terminal;

/// <summary>Edits the visible terminal palette and font independently of the settings store.</summary>
public abstract partial class TerminalAppearanceViewModel : ObservableObject
{
    private bool _loadingAppearance;

    [ObservableProperty] private TerminalSettingsDto _appearance = TerminalSettingsDto.Default;
    [ObservableProperty] private string _fontFamily = TerminalSettingsDto.Default.FontFamily;
    [ObservableProperty] private double _fontSize = TerminalSettingsDto.Default.FontSize;
    [ObservableProperty] private string _colorScheme = TerminalSettingsDto.Default.ColorScheme;

    public IReadOnlyList<string> FontFamilies => TerminalAppearance.FontFamilies;
    public IReadOnlyList<double> FontSizes => TerminalAppearance.FontSizes;
    public IReadOnlyList<string> ColorSchemes => TerminalAppearance.ColorSchemes;

    protected void ApplyAppearance(TerminalSettingsDto settings)
    {
        _loadingAppearance = true;
        Appearance = settings;
        FontFamily = settings.FontFamily;
        FontSize = settings.FontSize;
        ColorScheme = settings.ColorScheme;
        _loadingAppearance = false;
    }

    partial void OnFontFamilyChanged(string value) => UpdateAppearance();
    partial void OnFontSizeChanged(double value) => UpdateAppearance();
    partial void OnColorSchemeChanged(string value) => UpdateAppearance(applyScheme: true);

    private void UpdateAppearance(bool applyScheme = false)
    {
        if (_loadingAppearance)
            return;

        // The stored engine is always the built-in one, so a workspace written by an older build
        // (which could still hold the legacy `xterm` token) migrates on its next appearance write.
        var updated = Appearance with
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            ColorScheme = ColorScheme,
            Backend = TerminalBackends.BuiltIn,
        };
        Appearance = applyScheme ? TerminalAppearance.ApplyScheme(updated, ColorScheme) : updated;
        OnAppearanceEdited(Appearance);
    }

    protected abstract void OnAppearanceEdited(TerminalSettingsDto settings);
}
