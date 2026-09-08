using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppPermissions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Explicit selection of the data categories that Settings may clear for one app.</summary>
public sealed partial class AppDataClearDialogViewModel : ObservableObject
{
    private readonly Action<AppDataClearOptions?> _complete;

    public AppDataClearDialogViewModel(ApplicationInfo app, Action<AppDataClearOptions?> complete)
    {
        _complete = complete;
        Title = LocalizedText.Format("settings.apps.clear_data_title", app.DisplayName);
        Description = LocalizedText.Get("settings.apps.clear_data_description",
            "Choose the optional data to remove. Local application data is always removed.");
        LocalDataLabel = LocalizedText.Get("settings.apps.clear_data.local", "Local application data");
        LocalDataHint = LocalizedText.Get("settings.apps.clear_data.local_hint",
            "Always removed from this device. Application installation files are not removed.");
        PermissionsLabel = LocalizedText.Get("settings.apps.clear_data.permissions", "Permission decisions");
        PermissionsHint = LocalizedText.Get("settings.apps.clear_data.permissions_hint",
            "Remove this device's allowed and denied decisions; the app will ask again when opened.");
        ServerDataLabel = LocalizedText.Get("settings.apps.clear_data.server", "Server application data");
        ServerDataHint = LocalizedText.Get("settings.apps.clear_data.server_hint",
            "Remove all private settings for this application in your current RelaxKonOS account.");
        ClearLabel = LocalizedText.Get("settings.apps.clear_data.action", "Clear data");
    }

    public string Title { get; }
    public string Description { get; }
    public string LocalDataLabel { get; }
    public string LocalDataHint { get; }
    public string PermissionsLabel { get; }
    public string PermissionsHint { get; }
    public string ServerDataLabel { get; }
    public string ServerDataHint { get; }
    public string ClearLabel { get; }
    [ObservableProperty] private bool _clearPermissionDecisions;
    [ObservableProperty] private bool _clearServerData;

    [RelayCommand]
    private void Clear() => _complete(new AppDataClearOptions(ClearPermissionDecisions, ClearServerData));

    [RelayCommand]
    private void Cancel() => _complete(null);
}
