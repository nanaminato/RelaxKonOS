using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface IPersonalizationColorsPage
{
    IPersonalizationColors Editor { get; }
}

public interface IPersonalizationColors : IPersonalizationOverview
{
    bool IsLightTheme { get; set; }
    bool IsDarkTheme { get; set; }
    bool IsSystemTheme { get; set; }
    IReadOnlyList<ThemePaletteChoice> PaletteChoices { get; }
    string PaletteId { get; set; }
    string AccentInput { get; set; }
    string? AccentError { get; }
    bool HasAccentError { get; }
    bool HasAccentOverride { get; }
    bool HasSelectedCustomPalette { get; }
    ICommand DeleteThemeCommand { get; }
    ICommand ExportThemeCommand { get; }
    ICommand ImportThemeCommand { get; }
    ICommand ResetAccentCommand { get; }
    ICommand ResetColorsCommand { get; }
}

public sealed record ThemePaletteChoice(string Id, string Name, bool IsCustom);
