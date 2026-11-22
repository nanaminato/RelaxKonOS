using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings;

public readonly record struct AccessibilityOptions(int InterfaceScale, bool ReducedMotion, bool HighContrast);

public interface IAccessibilityPreferences : INotifyPropertyChanged
{
    IReadOnlyList<int> ScaleChoices { get; }
    AccessibilityOptions Accessibility { get; set; }
    bool SaveFailed { get; }
    void Save();
}

public sealed partial class AccessibilityPageContent : ObservableObject, IDisposable
{
    private readonly IAccessibilityPreferences _preferences;
    private readonly Func<string, string, string> _localize;

    public AccessibilityPageContent(IAccessibilityPreferences preferences, Func<string, string, string> localize)
    {
        _preferences = preferences;
        _localize = localize;
        _preferences.PropertyChanged += OnPreferencesChanged;
    }

    public IReadOnlyList<int> ScaleChoices => _preferences.ScaleChoices;
    public int InterfaceScale
    {
        get => _preferences.Accessibility.InterfaceScale;
        set => _preferences.Accessibility = _preferences.Accessibility with { InterfaceScale = value };
    }

    public bool ReducedMotion
    {
        get => _preferences.Accessibility.ReducedMotion;
        set => _preferences.Accessibility = _preferences.Accessibility with { ReducedMotion = value };
    }

    public bool HighContrast
    {
        get => _preferences.Accessibility.HighContrast;
        set => _preferences.Accessibility = _preferences.Accessibility with { HighContrast = value };
    }

    public bool SaveFailed => _preferences.SaveFailed;
    public string SaveStatus => _localize(SaveFailed ? "settings.device.save_failed" : "settings.device.saved", "Saved on this device");

    public Func<string, string> Labels => Text;

    [RelayCommand] private void ResetAccessibility() => _preferences.Accessibility = new(100, false, false);
    [RelayCommand] private void RetrySave() => _preferences.Save();

    public void RefreshLanguage() => OnPropertyChanged(string.Empty);
    public void Dispose() => _preferences.PropertyChanged -= OnPreferencesChanged;

    private string Text(string key) => _localize(key, key);
    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
