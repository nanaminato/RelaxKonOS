using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Local navigation only: opening Home never waits for a remote provider.</summary>
public sealed class SettingsHomePageViewModel : SettingsPageViewModel
{
    private readonly DesktopDevicePreferences _preferences;
    private readonly IReadOnlyList<SettingsPageViewModel> _pages;
    public SettingsHomePageViewModel(ShellSettings settings, IReadOnlyList<SettingsPageViewModel> pages, DesktopDevicePreferences preferences)
        : base(settings, null)
    {
        _preferences = preferences;
        _pages = pages;
        preferences.Changed += OnPinnedChanged;

    }
    public override string Route => "home";
    public override string DisplayNameKey => "settings.page.home";
    public override string DisplayName => "Home";
    public IReadOnlyList<SettingsPageViewModel> Entries => _pages.Where(page =>
        page.Route is "personalization" or "network" or "default-apps" or "time-language" or "account-security" or "system")
        .Where(page => !_preferences.Value.PinnedSettings.Contains(page.Route)).ToArray();
    public bool HasEntries => Entries.Count > 0;
    public IReadOnlyList<SettingsPageViewModel> PinnedEntries => _preferences.Value.PinnedSettings
        .Select(route => _pages.FirstOrDefault(page => page.Route == route)).OfType<SettingsPageViewModel>().ToArray();
    public bool HasPinnedEntries => PinnedEntries.Count > 0;
    private void OnPinnedChanged(object? sender, EventArgs e) { OnPropertyChanged(nameof(PinnedEntries)); OnPropertyChanged(nameof(HasPinnedEntries)); OnPropertyChanged(nameof(Entries)); OnPropertyChanged(nameof(HasEntries)); }
    protected override void DisposeCore() => _preferences.Changed -= OnPinnedChanged;
}
