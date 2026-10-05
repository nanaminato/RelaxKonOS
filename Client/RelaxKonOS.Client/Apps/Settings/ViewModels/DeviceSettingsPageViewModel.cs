using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public string SaveStatus => T(Preferences.SaveFailed ? "settings.device.save_failed" : "settings.device.saved", "Saved on this device");
    public bool SaveFailed => Preferences.SaveFailed;
    [RelayCommand] private void RetrySave() => Preferences.Save();
    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
    protected override void DisposeCore() => Preferences.PropertyChanged -= OnDeviceChanged;
}

public sealed partial class AccessibilityPageViewModel : DeviceSettingsPageViewModel
{
    public AccessibilityPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences) : base(settings, preferences) { }
    public override string Route => "accessibility";
    public override string DisplayNameKey => "settings.page.accessibility";
    public override string DisplayName => "Accessibility";
    public IReadOnlyList<int> ScaleChoices => DesktopDevicePreferences.ScaleChoices;
    public int InterfaceScale { get => Preferences.Value.InterfaceScale; set => Preferences.Update(p => p with { InterfaceScale = value }); }
    public bool ReducedMotion { get => Preferences.Value.ReducedMotion; set => Preferences.Update(p => p with { ReducedMotion = value }); }
    public bool HighContrast { get => Preferences.Value.HighContrast; set => Preferences.Update(p => p with { HighContrast = value }); }
    [RelayCommand] private void ResetAccessibility() => Preferences.Update(p => p with { InterfaceScale = 100, ReducedMotion = false, HighContrast = false });
}

public sealed partial class DailySettingsPageViewModel : DeviceSettingsPageViewModel
{
    public DailySettingsPageViewModel(ShellSettings settings, DesktopDevicePreferences preferences) : base(settings, preferences) { }
    public override string Route => "system/preferences";
    public override string DisplayNameKey => "settings.page.daily";
    public override string DisplayName => "Notifications and startup";
    public bool NotificationsEnabled { get => Preferences.Value.NotificationsEnabled; set => Preferences.Update(p => p with { NotificationsEnabled = value }); }
    public bool DoNotDisturb { get => Preferences.Value.DoNotDisturb; set => Preferences.Update(p => p with { DoNotDisturb = value }); }
    public bool RestoreTerminals { get => Preferences.Value.RestoreTerminals; set => Preferences.Update(p => p with { RestoreTerminals = value }); }
    public bool KeepConnectionBarVisible { get => Preferences.Value.KeepConnectionBarVisible; set => Preferences.Update(p => p with { KeepConnectionBarVisible = value }); }
    [RelayCommand] private void ResetDailySettings() => Preferences.Update(p => p with { NotificationsEnabled = true, DoNotDisturb = false, RestoreTerminals = true, KeepConnectionBarVisible = false });
}
