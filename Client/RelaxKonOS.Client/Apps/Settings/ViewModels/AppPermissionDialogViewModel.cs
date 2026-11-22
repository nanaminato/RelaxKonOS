using RelaxKonOS.App.Settings.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class AppPermissionDialogViewModel : AppPermissionEditor, IDisposable
{
    private readonly LocalizationService _localization;

    public AppPermissionDialogViewModel(
        ApplicationInfo app,
        IAppPermissionManager permissionManager,
        LocalizationService localization,
        Action<bool> complete)
        : base(app.Id.Value,
            () => localization.Get($"application.{app.Id.Value}.display_name",
                app.GetLocalizedMetadata(localization.CurrentLanguage).DisplayName),
            key => localization.Get(key, key),
            BuildGroups(app, permissionManager),
            (permission, granted) => permissionManager.SetStatus(app.Id, permission,
                granted ? AppPermissionStatus.Granted : AppPermissionStatus.Denied),
            complete)
    {
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    private static IReadOnlyList<AppPermissionCategoryViewModel> BuildGroups(
        ApplicationInfo app, IAppPermissionManager permissionManager) => app.Permissions
        .Select(AppPermissions.Find)
        .OfType<AppPermissionDefinition>()
        .GroupBy(AppPermissions.GetCategory, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new AppPermissionCategoryViewModel(
            () => PermissionText.Category(group.Key),
            group.OrderBy(permission => PermissionText.DisplayName(permission), StringComparer.Ordinal)
                .Select(permission => new AppPermissionChoiceViewModel(
                    permission.Id,
                    permissionManager.GetStatus(app.Id, permission.Id) == AppPermissionStatus.Granted,
                    () => PermissionText.DisplayName(permission),
                    () => PermissionText.Description(permission)))
                .ToArray()))
        .ToArray();

    public void Dispose() => _localization.LanguageChanged -= OnLanguageChanged;

    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshLanguage();
}
