using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.App.Settings.Views;

public interface IApplicationDetailPage
{
    IAppsPage Editor { get; }
}

public interface IAppsPage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<ISettingsAppEntry> RegisteredApps { get; }
    ApplicationInfo? SelectedApp { get; }
    IImage? SelectedAppIconImage { get; }
    object? ActionStatus { get; }
    string SelectedAppPermissionSummary { get; }
    string UninstallAvailabilityText { get; }
    bool OpenBrowserLinksInBuiltInBrowser { get; set; }
    bool OpenBrowserLinksOnHost { get; set; }
    bool CanUninstallSelectedApp { get; }
    bool HasActionStatus { get; }
    bool HasSelectedAppIconImage { get; }
    bool HasSelectedAppPermissions { get; }
    bool IsAppDetails { get; }
    bool IsBrowserSettingsLoading { get; }
    bool IsBrowserSettingsVisible { get; }
    bool IsClearingData { get; }
    bool IsInstalledApps { get; }
    ICommand ClearSelectedAppDataCommand { get; }
    ICommand EditSelectedPermissionsCommand { get; }
    ICommand OpenSelectedAppCommand { get; }
    ICommand ShowAppDetailsCommand { get; }
    ICommand UninstallSelectedAppCommand { get; }
}

public interface ISettingsAppEntry
{
    ApplicationInfo App { get; }
    string DisplayName { get; }
    string? IconGlyph { get; }
    string? Description { get; }
    string Version { get; }
    IImage? IconImage { get; }
    bool HasIconImage { get; }
}
