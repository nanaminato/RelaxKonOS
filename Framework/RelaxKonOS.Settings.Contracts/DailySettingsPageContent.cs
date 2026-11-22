using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings;

public readonly record struct DailySettingsOptions(bool NotificationsEnabled, bool DoNotDisturb,
    bool RestoreTerminals, bool KeepConnectionBarVisible);

public interface IDailySettingsPreferences : INotifyPropertyChanged
{
    DailySettingsOptions DailySettings { get; set; }
    bool OpenTerminalOnSshLogin { get; set; }
    bool SaveFailed { get; }
    void Save();
}

public sealed partial class DailySettingsPageContent : ObservableObject, IDisposable
{
    private readonly IDailySettingsPreferences _preferences;
    private readonly IDailySettingsPreferences? _sshPreferences;
    private readonly Func<string, string, string> _localize;

    public DailySettingsPageContent(IDailySettingsPreferences preferences, IDailySettingsPreferences? sshPreferences,
        Func<string, string, string> localize)
    {
        _preferences = preferences;
        _sshPreferences = sshPreferences;
        _localize = localize;
        _preferences.PropertyChanged += OnPreferencesChanged;
        if (_sshPreferences is not null) _sshPreferences.PropertyChanged += OnPreferencesChanged;
    }

    public bool NotificationsEnabled
    {
        get => _preferences.DailySettings.NotificationsEnabled;
        set => _preferences.DailySettings = _preferences.DailySettings with { NotificationsEnabled = value };
    }

    public bool DoNotDisturb
    {
        get => _preferences.DailySettings.DoNotDisturb;
        set => _preferences.DailySettings = _preferences.DailySettings with { DoNotDisturb = value };
    }

    public bool RestoreTerminals
    {
        get => _preferences.DailySettings.RestoreTerminals;
        set => _preferences.DailySettings = _preferences.DailySettings with { RestoreTerminals = value };
    }

    public bool KeepConnectionBarVisible
    {
        get => _preferences.DailySettings.KeepConnectionBarVisible;
        set => _preferences.DailySettings = _preferences.DailySettings with { KeepConnectionBarVisible = value };
    }

    public bool IsSshDesktop => _sshPreferences is not null;
    public bool OpenTerminalOnSshLogin
    {
        get => _sshPreferences?.OpenTerminalOnSshLogin ?? true;
        set { if (_sshPreferences is not null) _sshPreferences.OpenTerminalOnSshLogin = value; }
    }

    public bool SaveFailed => _preferences.SaveFailed || _sshPreferences?.SaveFailed == true;
    public string SaveStatus => _localize(SaveFailed ? "settings.device.save_failed" : "settings.device.saved", "Saved on this device");

    public Func<string, string> Labels => Text;

    [RelayCommand]
    private void ResetDailySettings()
    {
        _preferences.DailySettings = new(true, false, true, false);
        if (IsSshDesktop) OpenTerminalOnSshLogin = true;
    }

    [RelayCommand]
    private void RetrySave()
    {
        _preferences.Save();
        _sshPreferences?.Save();
    }

    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
    public void Dispose()
    {
        _preferences.PropertyChanged -= OnPreferencesChanged;
        if (_sshPreferences is not null) _sshPreferences.PropertyChanged -= OnPreferencesChanged;
    }

    private string Text(string key) => _localize(key, key);
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
