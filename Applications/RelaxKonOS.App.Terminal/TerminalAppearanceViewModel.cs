using Avalonia.Utilities;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.App.Terminal;

/// <summary>Edits the visible terminal palette and font independently of the settings store.</summary>
public abstract partial class TerminalAppearanceViewModel : ObservableObject
{
    private bool _loadingAppearance;
    private readonly ISystemLanguage? _language;

    protected TerminalAppearanceViewModel(ISystemLanguage? language, string vtEngine)
    {
        _vtEngine = vtEngine;
        _language = language;
        if (language is not null)
            WeakEventHandlerManager.Subscribe<ISystemLanguage, SystemLanguageChangedEventArgs, TerminalAppearanceViewModel>(
                language, nameof(ISystemLanguage.LanguageChanged), OnLanguageChanged);
    }

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => OnPropertyChanged(string.Empty);

    public string SettingsTitle => Label("terminal.settings");
    public string FontFamilyLabel => Label("terminal.font_family");
    public string FontSizeLabel => Label("terminal.font_size");
    public string ColorSchemeLabel => Label("terminal.color_scheme");
    public string VtEngineLabel => Label("terminal.vt_engine");
    public string VtEngineDescription => Label("terminal.vt_engine.description");
    public string DoneLabel => Label("terminal.done");

    protected string Label(string key) => TerminalLocalization.Resolve(_language, key);

    public IReadOnlyList<string> VtEngines { get; } = ["Auto", "Managed", "Native"];
    [ObservableProperty] private string _vtEngine = "Auto";

    partial void OnVtEngineChanged(string value)
    {
        if (VtEngines.Contains(value)) OnVtEngineEdited(value);
    }

    protected abstract void OnVtEngineEdited(string value);
    public Action? CloseSettingsAction { get; set; }

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

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
