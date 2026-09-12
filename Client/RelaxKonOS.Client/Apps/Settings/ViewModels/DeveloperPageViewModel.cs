using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Developer features are a top-level Settings category, separate from Applications.</summary>
public sealed class DeveloperPageViewModel : SettingsPageViewModel
{
    public DeveloperPageViewModel(ShellSettings settings, DeveloperModeService developerMode,
        NetworkInspectorWindowService networkInspector, LocalizationService localization, Action? save)
        : base(settings, save)
    {
        DeveloperMode = new DeveloperModeViewModel(developerMode);
        NetworkInspector = new NetworkInspectorLauncherViewModel(developerMode, networkInspector, localization);
    }

    public override string Route => "developer";
    public override string DisplayNameKey => "settings.page.developer";
    public override string DisplayName => "Developer";
    public DeveloperModeViewModel DeveloperMode { get; }
    public NetworkInspectorLauncherViewModel NetworkInspector { get; }
}

public sealed partial class NetworkInspectorLauncherViewModel : ObservableObject
{
    private readonly DeveloperModeService _developerMode;
    private readonly NetworkInspectorWindowService _networkInspector;

    public NetworkInspectorLauncherViewModel(DeveloperModeService developerMode, NetworkInspectorWindowService networkInspector,
        LocalizationService localization)
    {
        _developerMode = developerMode;
        _networkInspector = networkInspector;
        _developerMode.Changed += (_, _) => Refresh();
        localization.LanguageChanged += (_, _) => Refresh();
    }

    public bool CanOpen => _networkInspector.CanOpen;
    public string Status => !_developerMode.IsEnabled ? LocalizedText.Get("settings.network_inspector.requires_developer_mode")
        : LocalizedText.Get("settings.network_inspector.ready");

    [RelayCommand]
    private void Open() => _networkInspector.Open();

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Status));
        OpenCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class DeveloperModeViewModel : ObservableObject
{
    private readonly DeveloperModeService _developerMode;

    public DeveloperModeViewModel(DeveloperModeService developerMode)
    {
        _developerMode = developerMode;
        _isEnabled = developerMode.IsEnabled;
    }

    [ObservableProperty] private bool _isEnabled;
    public string Endpoint => _developerMode.Endpoint;
    public string PairingToken => _developerMode.PairingToken;

    partial void OnIsEnabledChanged(bool value) => _developerMode.SetEnabled(value);

    [RelayCommand]
    private void RegeneratePairingToken()
    {
        _developerMode.RegeneratePairingToken();
        OnPropertyChanged(nameof(PairingToken));
    }
}
