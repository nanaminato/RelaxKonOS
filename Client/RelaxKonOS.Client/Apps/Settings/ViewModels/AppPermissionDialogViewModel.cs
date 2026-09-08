using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Edits one application's declared permissions as a draft until the user saves.</summary>
public sealed partial class AppPermissionDialogViewModel : ObservableObject, IDisposable
{
    private readonly AppId _appId;
    private readonly IAppPermissionManager _permissionManager;
    private readonly Action<bool> _complete;
    private readonly LocalizationService _localization;

    public AppPermissionDialogViewModel(
        ApplicationInfo app,
        IAppPermissionManager permissionManager,
        LocalizationService localization,
        Action<bool> complete)
    {
        _appId = app.Id;
        _permissionManager = permissionManager;
        _complete = complete;
        _localization = localization;
        AppName = app.DisplayName;
        AppId = app.Id.Value;

        PermissionGroups = app.Permissions
            .Select(AppPermissions.Find)
            .OfType<AppPermissionDefinition>()
            .GroupBy(AppPermissions.GetCategory, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new AppPermissionCategoryViewModel(
                group.Key,
                group.OrderBy(permission => PermissionText.DisplayName(permission), StringComparer.Ordinal)
                    .Select(permission => new AppPermissionChoiceViewModel(
                        permission,
                        permissionManager.GetStatus(app.Id, permission.Id)))
                    .ToArray()))
            .ToArray();

        _localization.LanguageChanged += OnLanguageChanged;
    }

    public string AppName { get; }
    public string AppId { get; }
    public IReadOnlyList<AppPermissionCategoryViewModel> PermissionGroups { get; }

    [RelayCommand]
    private void Save()
    {
        foreach (var permission in PermissionGroups.SelectMany(group => group.Permissions))
            if (permission.HasChanged)
                _permissionManager.SetStatus(_appId, permission.PermissionId,
                    permission.IsGranted ? AppPermissionStatus.Granted : AppPermissionStatus.Denied);
        _complete(true);
    }

    [RelayCommand]
    private void Cancel() => _complete(false);

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        foreach (var group in PermissionGroups)
            group.RefreshLocalizedText();
    }
}

public sealed partial class AppPermissionCategoryViewModel : ObservableObject
{
    private readonly string _category;

    public AppPermissionCategoryViewModel(string category, IReadOnlyList<AppPermissionChoiceViewModel> permissions)
    {
        _category = category;
        Permissions = permissions;
        RefreshLocalizedText();
    }

    [ObservableProperty] private string _name = string.Empty;
    public IReadOnlyList<AppPermissionChoiceViewModel> Permissions { get; }

    public void RefreshLocalizedText()
    {
        Name = PermissionText.Category(_category);
        foreach (var permission in Permissions)
            permission.RefreshLocalizedText();
    }
}

public sealed partial class AppPermissionChoiceViewModel : ObservableObject
{
    private readonly AppPermissionDefinition _definition;

    public AppPermissionChoiceViewModel(AppPermissionDefinition definition, AppPermissionStatus status)
    {
        _definition = definition;
        PermissionId = definition.Id;
        InitialStatus = status;
        _isGranted = status == AppPermissionStatus.Granted;
        RefreshLocalizedText();
    }

    public string PermissionId { get; }
    public AppPermissionStatus InitialStatus { get; }
    public bool HasChanged => IsGranted != (InitialStatus == AppPermissionStatus.Granted);
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private bool _isGranted;

    public void RefreshLocalizedText()
    {
        DisplayName = PermissionText.DisplayName(_definition);
        Description = PermissionText.Description(_definition);
    }
}
