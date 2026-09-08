using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>One deliberately small, one-permission approval prompt bound to an application window.</summary>
public sealed partial class AppPermissionRequestDialogViewModel : ObservableObject, IDisposable
{
    private readonly AppPermissionDefinition _permission;
    private readonly LocalizationService _localization;
    private readonly Action<AppPermissionStatus?> _complete;

    public AppPermissionRequestDialogViewModel(
        ApplicationInfo app,
        AppPermissionDefinition permission,
        LocalizationService localization,
        Action<AppPermissionStatus?> complete)
    {
        AppName = app.DisplayName;
        _permission = permission;
        _localization = localization;
        _complete = complete;
        RefreshLocalizedText();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string AppName { get; }
    [ObservableProperty] private string _prompt = string.Empty;
    [ObservableProperty] private string _permissionName = string.Empty;
    [ObservableProperty] private string _permissionDescription = string.Empty;
    [ObservableProperty] private string _deferredHint = string.Empty;
    [ObservableProperty] private string _allowText = string.Empty;
    [ObservableProperty] private string _denyText = string.Empty;
    [ObservableProperty] private string _laterText = string.Empty;

    [RelayCommand] private void Allow() => _complete(AppPermissionStatus.Granted);
    [RelayCommand] private void Deny() => _complete(AppPermissionStatus.Denied);
    [RelayCommand] private void Later() => _complete(null);

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        Prompt = LocalizedText.Get("permission.request.prompt", "Would you like to allow this application to use the following permission?");
        PermissionName = PermissionText.DisplayName(_permission);
        PermissionDescription = PermissionText.Description(_permission);
        DeferredHint = LocalizedText.Get("permission.request.later_hint", "You can decide later in this application's Settings permissions page.");
        AllowText = LocalizedText.Get("permission.request.allow", "Allow");
        DenyText = LocalizedText.Get("permission.request.deny", "Deny");
        LaterText = LocalizedText.Get("permission.request.later", "Later");
    }
}
