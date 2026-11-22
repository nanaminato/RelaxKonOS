using RelaxKonOS.App.Settings;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Local navigation only: opening Home never waits for a remote provider.</summary>
public sealed class SettingsHomePageViewModel : SettingsPageViewModel
{
    private readonly DesktopDevicePreferences _preferences;

    public SettingsHomePageViewModel(ShellSettings settings, IReadOnlyList<SettingsPageViewModel> pages, DesktopDevicePreferences preferences)
        : base(settings, null)
    {
        _preferences = preferences;
        Content = new(pages, () => preferences.Value.PinnedSettings, key => T(key, key));
        preferences.Changed += OnPinnedChanged;
    }

    public override string Route => "home";
    public override string DisplayNameKey => "settings.page.home";
    public override string DisplayName => "Home";
    public SettingsHomePageContent Content { get; }

    private void OnPinnedChanged(object? sender, EventArgs e) => Content.RefreshPinnedRoutes();

    protected override void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        Content.RefreshLanguage();
        base.OnLanguageChanged(sender, e);
    }

    protected override void DisposeCore() => _preferences.Changed -= OnPinnedChanged;
}
