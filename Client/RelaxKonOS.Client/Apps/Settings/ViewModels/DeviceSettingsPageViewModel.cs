using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.App.Settings;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public abstract partial class DeviceSettingsPageViewModel : SettingsPageViewModel
{
    protected readonly DesktopDevicePreferences Preferences;
    protected DeviceSettingsPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences) : base(settings, null)
    {
        Preferences = preferences;
        preferences.PropertyChanged += OnDeviceChanged;
    }
    public string SaveStatus => T(SaveFailed ? "settings.device.save_failed" : "settings.device.saved", "Saved on this device");
    public virtual bool SaveFailed => Preferences.SaveFailed;
    [RelayCommand] private void RetrySave() => SavePreferences();
    protected virtual void SavePreferences() => Preferences.Save();
    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
    protected override void DisposeCore() => Preferences.PropertyChanged -= OnDeviceChanged;
}

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

public sealed partial class DailySettingsPageViewModel : DeviceSettingsPageViewModel
{
    private readonly DesktopDevicePreferences? _sshPreferences;
    public DailySettingsPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences, DesktopDevicePreferences? sshPreferences = null) : base(settings, preferences)
    {
        _sshPreferences = sshPreferences;
        if (_sshPreferences is not null) _sshPreferences.PropertyChanged += OnSshPreferencesChanged;
    }
    public bool IsSshDesktop => _sshPreferences is not null;
    public bool OpenTerminalOnSshLogin
    {
        get => _sshPreferences?.Value.OpenTerminalOnSshLogin ?? true;
        set => _sshPreferences?.Update(p => p with { OpenTerminalOnSshLogin = value });
    }
    public override bool SaveFailed => base.SaveFailed || _sshPreferences?.SaveFailed == true;
    protected override void SavePreferences() { base.SavePreferences(); _sshPreferences?.Save(); }
    private void OnSshPreferencesChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
    protected override void DisposeCore()
    {
        if (_sshPreferences is not null) _sshPreferences.PropertyChanged -= OnSshPreferencesChanged;
        base.DisposeCore();
    }
    public override string Route => "system/preferences";
    public override string DisplayNameKey => "settings.page.daily";
    public override string DisplayName => "Notifications and startup";
    public bool NotificationsEnabled { get => Preferences.Value.NotificationsEnabled; set => Preferences.Update(p => p with { NotificationsEnabled = value }); }
    public bool DoNotDisturb { get => Preferences.Value.DoNotDisturb; set => Preferences.Update(p => p with { DoNotDisturb = value }); }
    public bool RestoreTerminals { get => Preferences.Value.RestoreTerminals; set => Preferences.Update(p => p with { RestoreTerminals = value }); }
    public bool KeepConnectionBarVisible { get => Preferences.Value.KeepConnectionBarVisible; set => Preferences.Update(p => p with { KeepConnectionBarVisible = value }); }
    [RelayCommand] private void ResetDailySettings()
    {
        Preferences.Update(p => p with { NotificationsEnabled = true, DoNotDisturb = false, RestoreTerminals = true, KeepConnectionBarVisible = false });
        if (IsSshDesktop) OpenTerminalOnSshLogin = true;
    }
}
