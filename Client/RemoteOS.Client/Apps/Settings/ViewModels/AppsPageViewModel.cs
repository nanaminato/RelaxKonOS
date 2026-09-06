using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using Client.Apps.Browser;
using Client.Services;
using Client.Services.AppPermissions;
using Client.Services.Developer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using RemoteOS.Protocol.Browser;
using RemoteOS.WindowManager;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Installed applications and the detail view for one selected application.</summary>
public sealed partial class AppsPageViewModel : SettingsPageViewModel, IDisposable
{
    private readonly ApplicationManager _apps;
    private readonly DeveloperPackageManager _packages;
    private readonly LocalizationService _localization;
    private readonly IBrowserClient _browserClient;
    private BrowserSettingsDto? _browserSettings;

    public AppsPageViewModel(
        ShellSettings settings,
        ApplicationManager apps,
        DeveloperPackageManager packages,
        LocalizationService localization,
        IBrowserClient browserClient)
        : base(settings, save: null)
    {
        _apps = apps;
        _packages = packages;
        _localization = localization;
        _browserClient = browserClient;
        _apps.RegistryChanged += OnRegistryChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshApplications();
    }

    public override string Glyph => "📱";
    public override string DisplayNameKey => "settings.page.applications";
    public override string DisplayName => "Applications";
    public ObservableCollection<SettingsAppEntry> RegisteredApps { get; } = new();

    /// <summary>Provided by Settings to open the selected application's permission page.</summary>
    public Func<ApplicationInfo, Task>? RequestPermissionEditorAsync { get; set; }
    /// <summary>Provided by Settings so an uninstall always has an explicit confirmation step.</summary>
    public Func<ApplicationInfo, Task<bool>>? RequestUninstallConfirmationAsync { get; set; }
    /// <summary>Provided by Settings to choose and clear one application's data categories.</summary>
    public Func<ApplicationInfo, Task<AppDataClearResult?>>? RequestClearDataAsync { get; set; }

    [ObservableProperty] private AppsSubpage _subpage = AppsSubpage.InstalledApps;
    [ObservableProperty] private ApplicationInfo? _selectedApp;
    [ObservableProperty] private string _actionStatus = string.Empty;
    [ObservableProperty] private bool _isUninstalling;
    [ObservableProperty] private bool _isClearingData;
    [ObservableProperty] private bool _isBrowserSettingsLoading;
    [ObservableProperty] private bool _isSavingBrowserSettings;
    [ObservableProperty] private string _browserSettingsStatus = string.Empty;
    [ObservableProperty] private BrowserLinkOpenTarget _browserLinkOpenTarget = BrowserLinkOpenTarget.BuiltInBrowser;

    public bool IsInstalledApps => Subpage == AppsSubpage.InstalledApps;
    public bool IsAppDetails => Subpage == AppsSubpage.AppDetails;
    public bool HasSelectedAppPermissions => SelectedApp?.Permissions.Count > 0;
    [ObservableProperty] private IImage? _selectedAppIconImage;
    public bool HasSelectedAppIconImage => SelectedAppIconImage is not null;
    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatus);
    public bool CanUninstallSelectedApp => !IsUninstalling && SelectedApp is not null
        && _packages.FindInstalled(SelectedApp.Id.Value) is not null;
    public bool IsBrowserSettingsVisible => SelectedApp?.Id.Value == "remoteos.browser";
    public bool OpenBrowserLinksInBuiltInBrowser
    {
        get => BrowserLinkOpenTarget == BrowserLinkOpenTarget.BuiltInBrowser;
        set { if (value) BrowserLinkOpenTarget = BrowserLinkOpenTarget.BuiltInBrowser; }
    }
    public bool OpenBrowserLinksOnHost
    {
        get => BrowserLinkOpenTarget == BrowserLinkOpenTarget.HostBrowser;
        set { if (value) BrowserLinkOpenTarget = BrowserLinkOpenTarget.HostBrowser; }
    }
    public bool HasBrowserSettingsStatus => !string.IsNullOrWhiteSpace(BrowserSettingsStatus);
    public string SelectedAppPermissionSummary => SelectedApp is null || SelectedApp.Permissions.Count == 0
        ? T("settings.apps.no_permissions", "This application does not request RemoteOS permissions.")
        : string.Format(CultureInfo.CurrentCulture,
            T("settings.apps.permissions_requested", "This application requests {0} RemoteOS permissions."),
            SelectedApp.Permissions.Count);
    public string UninstallAvailabilityText => CanUninstallSelectedApp
        ? T("settings.apps.uninstall_available", "This third-party application can be uninstalled from this device.")
        : T("settings.apps.built_in", "Built-in applications are managed by RemoteOS and cannot be uninstalled here.");

    public void Dispose()
    {
        _apps.RegistryChanged -= OnRegistryChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnRegistryChanged(object? sender, EventArgs eventArgs) => Dispatcher.UIThread.Post(RefreshApplications);
    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs eventArgs) => Dispatcher.UIThread.Post(RefreshApplications);

    private void RefreshApplications()
    {
        var apps = _apps.Registered.Select(Localize).Select(app => new SettingsAppEntry(app)).ToArray();
        Replace(RegisteredApps, apps);
        if (SelectedApp is not null)
            SelectedApp = apps.FirstOrDefault(app => app.Id == SelectedApp.Id)?.App;
    }

    [RelayCommand]
    private void ShowInstalledApps() => Subpage = AppsSubpage.InstalledApps;

    [RelayCommand]
    private void ShowAppDetails(ApplicationInfo app)
    {
        SelectedApp = app;
        ActionStatus = string.Empty;
        Subpage = AppsSubpage.AppDetails;
        if (IsBrowserSettingsVisible)
            _ = LoadBrowserSettingsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelectedApp))]
    private void OpenSelectedApp()
    {
        if (SelectedApp is null) return;
        ActionStatus = _apps.Launch(SelectedApp.Id)
            ? string.Format(CultureInfo.CurrentCulture, T("settings.apps.opened", "Opened {0}."), SelectedApp.DisplayName)
            : T("settings.apps.not_launchable", "This application is no longer available to launch.");
    }

    [RelayCommand]
    private async Task EditSelectedPermissionsAsync()
    {
        if (SelectedApp is not null && RequestPermissionEditorAsync is not null)
            await RequestPermissionEditorAsync(SelectedApp);
        RefreshApplications();
    }

    /// <summary>Used only by the host activation route after it has selected this page.</summary>
    public async Task OpenPermissionsAsync(string appId)
    {
        var app = RegisteredApps.FirstOrDefault(candidate =>
            candidate.Id.Value.Equals(appId, StringComparison.OrdinalIgnoreCase));
        if (app is null) return;
        ShowAppDetails(app.App);
        await EditSelectedPermissionsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanUninstallSelectedApp))]
    private async Task UninstallSelectedAppAsync()
    {
        if (SelectedApp is null || IsUninstalling || !CanUninstallSelectedApp) return;
        if (RequestUninstallConfirmationAsync is not null && !await RequestUninstallConfirmationAsync(SelectedApp)) return;

        IsUninstalling = true;
        try
        {
            var displayName = SelectedApp.DisplayName;
            if (await _packages.UninstallAsync(SelectedApp.Id.Value))
            {
                SelectedApp = null;
                Subpage = AppsSubpage.InstalledApps;
                ActionStatus = string.Format(CultureInfo.CurrentCulture, T("settings.apps.uninstalled", "Uninstalled {0}."), displayName);
            }
            else
                ActionStatus = T("settings.apps.already_uninstalled", "This application has already been uninstalled or does not support uninstallation.");
        }
        catch (Exception exception)
        {
            ActionStatus = string.Format(CultureInfo.CurrentCulture, T("settings.apps.uninstall_failed", "Uninstall failed: {0}"), exception.Message);
        }
        finally { IsUninstalling = false; }
    }

    private bool CanOpenSelectedApp() => SelectedApp is not null;

    [RelayCommand]
    private async Task ClearSelectedAppDataAsync()
    {
        if (SelectedApp is null || IsClearingData || RequestClearDataAsync is null)
            return;

        IsClearingData = true;
        try
        {
            var result = await RequestClearDataAsync(SelectedApp);
            if (result is null)
                return;
            ActionStatus = result.ServerDataCleared
                ? T("settings.apps.clear_data.complete_server", "Application data and the selected optional data were cleared.")
                : result.PermissionDecisionsCleared
                    ? T("settings.apps.clear_data.complete_permissions", "Application data and selected permission decisions were cleared.")
                    : T("settings.apps.clear_data.complete_local", "Local application data was cleared.");
        }
        catch (Exception exception)
        {
            ActionStatus = string.Format(CultureInfo.CurrentCulture,
                T("settings.apps.clear_data.failed", "Could not clear application data: {0}"), exception.Message);
        }
        finally { IsClearingData = false; }
    }

    [RelayCommand]
    private async Task SaveBrowserLinkOpenTargetAsync()
    {
        if (!IsBrowserSettingsVisible || IsSavingBrowserSettings)
            return;

        IsSavingBrowserSettings = true;
        BrowserSettingsStatus = string.Empty;
        try
        {
            var settings = _browserSettings ?? await _browserClient.GetSettingsAsync();
            var saved = await _browserClient.SaveSettingsAsync(settings with { LinkOpenTarget = BrowserLinkOpenTarget });
            _browserSettings = saved;
            BrowserLinkOpenTarget = saved.LinkOpenTarget;
            BrowserSettingsStatus = T("settings.apps.browser.link_open_target_saved", "Link opening preference saved.");
        }
        catch (Exception exception)
        {
            BrowserSettingsStatus = string.Format(
                CultureInfo.CurrentCulture,
                T("settings.apps.browser.link_open_target_save_failed", "Could not save the link opening preference: {0}"),
                exception.Message);
        }
        finally { IsSavingBrowserSettings = false; }
    }

    private async Task LoadBrowserSettingsAsync()
    {
        IsBrowserSettingsLoading = true;
        BrowserSettingsStatus = string.Empty;
        try
        {
            var settings = await _browserClient.GetSettingsAsync();
            if (!IsBrowserSettingsVisible)
                return;
            _browserSettings = settings;
            BrowserLinkOpenTarget = settings.LinkOpenTarget;
        }
        catch (Exception exception)
        {
            if (IsBrowserSettingsVisible)
                BrowserSettingsStatus = string.Format(
                    CultureInfo.CurrentCulture,
                    T("settings.apps.browser.link_open_target_load_failed", "Could not load the link opening preference: {0}"),
                    exception.Message);
        }
        finally { IsBrowserSettingsLoading = false; }
    }

    partial void OnSubpageChanged(AppsSubpage value)
    {
        OnPropertyChanged(nameof(IsInstalledApps));
        OnPropertyChanged(nameof(IsAppDetails));
    }

    partial void OnSelectedAppChanged(ApplicationInfo? value)
    {
        SelectedAppIconImage = AppIconImageLoader.Load(value?.IconPath);
        OnPropertyChanged(nameof(HasSelectedAppPermissions));
        OnPropertyChanged(nameof(CanUninstallSelectedApp));
        OnPropertyChanged(nameof(SelectedAppPermissionSummary));
        OnPropertyChanged(nameof(UninstallAvailabilityText));
        OnPropertyChanged(nameof(IsBrowserSettingsVisible));
        OpenSelectedAppCommand.NotifyCanExecuteChanged();
        UninstallSelectedAppCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsUninstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUninstallSelectedApp));
        OnPropertyChanged(nameof(UninstallAvailabilityText));
        UninstallSelectedAppCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsClearingDataChanged(bool value) => OnPropertyChanged(nameof(CanUninstallSelectedApp));

    partial void OnActionStatusChanged(string value) => OnPropertyChanged(nameof(HasActionStatus));
    partial void OnSelectedAppIconImageChanged(IImage? value) => OnPropertyChanged(nameof(HasSelectedAppIconImage));
    partial void OnBrowserLinkOpenTargetChanged(BrowserLinkOpenTarget value)
    {
        OnPropertyChanged(nameof(OpenBrowserLinksInBuiltInBrowser));
        OnPropertyChanged(nameof(OpenBrowserLinksOnHost));
    }
    partial void OnBrowserSettingsStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserSettingsStatus));

    private ApplicationInfo Localize(ApplicationInfo app)
    {
        var metadata = app.GetLocalizedMetadata(_localization.CurrentLanguage);
        return app with
        {
            DisplayName = T($"application.{app.Id.Value}.display_name", metadata.DisplayName),
            Description = metadata.Description is null
                ? null
                : T($"application.{app.Id.Value}.description", metadata.Description),
        };
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item);
    }
}

public enum AppsSubpage
{
    InstalledApps,
    AppDetails,
}

/// <summary>Presentation data for an installed app; image loading matches desktop and start-menu entries.</summary>
public sealed class SettingsAppEntry
{
    public SettingsAppEntry(ApplicationInfo app)
    {
        App = app;
        IconImage = AppIconImageLoader.Load(app.IconPath);
    }

    public ApplicationInfo App { get; }
    public AppId Id => App.Id;
    public string DisplayName => App.DisplayName;
    public string? IconGlyph => App.IconGlyph;
    public string? Description => App.Description;
    public string Version => App.Version;
    public IImage? IconImage { get; }
    public bool HasIconImage => IconImage is not null;
}
