using RelaxKonOS.App.Settings;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class AccessibilityPageViewModel : SettingsPageViewModel
{
    public AccessibilityPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences) : base(settings, null)
    {
        Content = new(preferences, T);
    }

    public override string Route => "accessibility";
    public override string DisplayNameKey => "settings.page.accessibility";
    public override string DisplayName => "Accessibility";
    public AccessibilityPageContent Content { get; }

    protected override void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        Content.RefreshLanguage();
        base.OnLanguageChanged(sender, e);
    }

    protected override void DisposeCore() => Content.Dispose();
}

public sealed class DailySettingsPageViewModel : SettingsPageViewModel
{
    public DailySettingsPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences,
        DesktopDevicePreferences? sshPreferences = null) : base(settings, null)
    {
        Content = new(preferences, sshPreferences, T);
    }

    public override string Route => "system/preferences";
    public override string DisplayNameKey => "settings.page.daily";
    public override string DisplayName => "Notifications and startup";
    public DailySettingsPageContent Content { get; }

    protected override void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        Content.RefreshLanguage();
        base.OnLanguageChanged(sender, e);
    }

    protected override void DisposeCore() => Content.Dispose();
}
