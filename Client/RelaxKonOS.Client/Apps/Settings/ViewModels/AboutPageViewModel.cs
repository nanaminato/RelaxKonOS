using RelaxKonOS.App.Settings;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class AboutPageViewModel(ShellSettings settings, LocalizationService localization)
    : SettingsPageViewModel(settings, save: null)
{
    public override string Route => "about";
    public override string DisplayNameKey => "settings.page.about";
    public override string DisplayName => "About RelaxKonOS";

    public AboutPageContent Content { get; } = new(key => localization.Get(key, key));
    public override object Presentation => Content;

    protected override void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        Content.RefreshLanguage();
        base.OnLanguageChanged(sender, e);
    }
}
