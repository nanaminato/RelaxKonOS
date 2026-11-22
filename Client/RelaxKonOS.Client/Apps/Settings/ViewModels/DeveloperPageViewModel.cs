using System.Windows.Input;
using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Developer features are a top-level Settings category, separate from Applications.</summary>
public sealed class DeveloperPageViewModel : SettingsPageViewModel, IDeveloperPage
{
    public DeveloperPageViewModel(ShellSettings settings, DeveloperModeService developerMode,
        NetworkInspectorWindowService networkInspector, LocalizationService localization, Action? save)
        : base(settings, save)
    {
        Labels = key => localization.Get(key, key);
        DeveloperMode = new DeveloperModeViewModel(developerMode);
        NetworkInspector = new NetworkInspectorLauncherViewModel(developerMode, networkInspector, localization);
    }

    protected override void DisposeCore() => NetworkInspector.Dispose();

    public override string Route => "developer";
    public override string DisplayNameKey => "settings.page.developer";
    public override string DisplayName => "Developer";
    public Func<string, string> Labels { get; }
    IDeveloperModePresentation IDeveloperPage.DeveloperMode => DeveloperMode;
    INetworkInspectorLauncher IDeveloperPage.NetworkInspector => NetworkInspector;

    protected override void OnLanguageChanged(object? sender, RelaxKonOS.AppSDK.SystemLanguageChangedEventArgs e)
    {
        base.OnLanguageChanged(sender, e);
        OnPropertyChanged(nameof(Labels));
    }

    public DeveloperModeViewModel DeveloperMode { get; }
    public NetworkInspectorLauncherViewModel NetworkInspector { get; }
}

public sealed partial class NetworkInspectorLauncherViewModel : ObservableObject, IDisposable, INetworkInspectorLauncher
{
    private readonly DeveloperModeService _developerMode;
    private readonly NetworkInspectorWindowService _networkInspector;
    private readonly LocalizationService _localization;

    public NetworkInspectorLauncherViewModel(DeveloperModeService developerMode, NetworkInspectorWindowService networkInspector,
        LocalizationService localization)
    {
        _developerMode = developerMode;
        _networkInspector = networkInspector;
        _localization = localization;
        _developerMode.Changed += OnDeveloperModeChanged;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    ICommand INetworkInspectorLauncher.OpenCommand => OpenCommand;

    public bool CanOpen => _networkInspector.CanOpen;
    public string Status => !_developerMode.IsEnabled ? LocalizedText.Get("settings.network_inspector.requires_developer_mode")
        : LocalizedText.Get("settings.network_inspector.ready");

    [RelayCommand]
    private void Open() => _networkInspector.Open();

    public void Dispose()
    {
        _developerMode.Changed -= OnDeveloperModeChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnDeveloperModeChanged(object? sender, EventArgs e) => Refresh();
    private void OnLanguageChanged(object? sender, RelaxKonOS.AppSDK.SystemLanguageChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanOpen));
        OnPropertyChanged(nameof(Status));
        OpenCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class DeveloperModeViewModel : ObservableObject, IDeveloperModePresentation
{
    private readonly DeveloperModeService _developerMode;

    public DeveloperModeViewModel(DeveloperModeService developerMode)
    {
        _developerMode = developerMode;
        _isEnabled = developerMode.IsEnabled;
    }

    ICommand IDeveloperModePresentation.RegeneratePairingTokenCommand => RegeneratePairingTokenCommand;

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
