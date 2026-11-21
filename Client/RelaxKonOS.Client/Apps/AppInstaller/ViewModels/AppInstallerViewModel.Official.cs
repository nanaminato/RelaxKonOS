using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;

namespace RelaxKonOS.Client.Apps.AppInstaller.ViewModels;

public sealed partial class AppInstallerViewModel
{
    private readonly CancellationTokenSource _storeLifetime = new();
    private IReadOnlyList<OfficialDesktopRelease> _officialReleases = [];
    private bool _disposed;

    public Func<OfficialAppListing, Task<bool>>? ConfirmUninstallAsync { get; set; }
    public bool ShowOfficialStore => AllowPackageSelection && !HasPackage;
    internal bool CanCloseWindow => !IsBusy || RefreshOfficialCommand.IsRunning || DownloadOfficialCommand.IsRunning;
    public bool ShowPackageSecurityWarning => CurrentPackage is { } package
        && !OfficialDesktopPackages.IsExternalOfficialId(package.Manifest.Id);
    public string OfficialActionLabel => LocalizedText.Get(SelectedOfficialApp?.Installed is null
        ? "app_installer.store.download" : "app_installer.store.download_update");

    [ObservableProperty] private IReadOnlyList<OfficialAppListing> _officialApps = [];
    [ObservableProperty] private OfficialAppListing? _selectedOfficialApp;
    [ObservableProperty] private LocalizedStatus _storeStatus = LocalizedText.Ref("app_installer.store.loading");

    private bool CanRefreshOfficial() => ShowOfficialStore && !IsBusy && !_disposed;
    private bool CanDownloadOfficial() => CanRefreshOfficial() && SelectedOfficialApp is { } row
        && row.Release is { } release && AppPackageInstallerService.IsNewer(release, row.Installed);
    private bool CanUninstallOfficial() => CanRefreshOfficial() && SelectedOfficialApp?.Installed is not null
        && ConfirmUninstallAsync is not null;

    [RelayCommand(CanExecute = nameof(CanRefreshOfficial))]
    private async Task RefreshOfficialAsync()
    {
        IsBusy = true;
        StoreStatus = LocalizedText.Ref("app_installer.store.loading");
        RefreshOfficialRows();
        try
        {
            var catalog = await _installer.BrowseOfficialAsync(_storeLifetime.Token);
            _officialReleases = catalog.Releases;
            RefreshOfficialRows();
            StoreStatus = catalog.OnlineUnavailable ? LocalizedText.Ref("app_installer.store.unavailable")
                : OfficialApps.Count == 0 ? LocalizedText.Ref("app_installer.store.empty") : default;
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDownloadOfficial), IncludeCancelCommand = true)]
    private async Task DownloadOfficialAsync(CancellationToken cancellationToken)
    {
        if (SelectedOfficialApp?.Release is not { } release) return;
        IsBusy = true;
        Status = LocalizedText.Ref("app_installer.status.downloading");
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_storeLifetime.Token, cancellationToken);
        try
        {
            var candidate = await _installer.StageOfficialAsync(release, stopping.Token);
            if (_disposed) _installer.Discard(candidate);
            else
            {
                _pending.Enqueue(candidate);
                ShowNext();
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = LocalizedText.Ref("app_installer.store.download_cancelled");
        }
        catch (Exception error) when (AppPackageInstallerService.IsStoreFailure(error))
        {
            Status = LocalizedText.Ref("app_installer.store.download_failed");
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUninstallOfficial))]
    private async Task UninstallOfficialAsync()
    {
        if (SelectedOfficialApp is not { Installed: { } installed } row || ConfirmUninstallAsync is null) return;
        IsBusy = true;
        try
        {
            if (!await ConfirmUninstallAsync(row) || _disposed) return;
            if (await _installer.UninstallOfficialAsync(installed))
                Status = LocalizedText.Ref("app_installer.store.uninstalled", row.DisplayName);
        }
        catch (Exception error) when (AppPackageInstallerService.IsStoreFailure(error))
        {
            Status = LocalizedText.Ref("app_installer.store.uninstall_failed", error.Message);
        }
        finally
        {
            RefreshOfficialRows();
            IsBusy = false;
        }
    }

    private void RefreshOfficialRows()
    {
        if (!AllowPackageSelection || _disposed) return;
        var installed = _installer.InstalledOfficial.ToDictionary(app => app.Id, StringComparer.Ordinal);
        var latest = _officialReleases.GroupBy(release => release.AppId).ToDictionary(group => group.Key,
            group => group.OrderByDescending(item => Version.Parse(item.Version)).First(), StringComparer.Ordinal);
        var current = SelectedOfficialApp?.Id;
        OfficialApps = latest.Keys.Union(installed.Keys, StringComparer.Ordinal)
            .Select(id => new OfficialAppListing(id, latest.GetValueOrDefault(id), installed.GetValueOrDefault(id)))
            .OrderBy(item => item.DisplayName).ToArray();
        SelectedOfficialApp = OfficialApps.FirstOrDefault(item => item.Id == current) ?? OfficialApps.FirstOrDefault();
    }

    partial void OnSelectedOfficialAppChanged(OfficialAppListing? value) => NotifyOfficialActions();

    private void NotifyOfficialActions()
    {
        OnPropertyChanged(nameof(OfficialActionLabel));
        RefreshOfficialCommand.NotifyCanExecuteChanged();
        DownloadOfficialCommand.NotifyCanExecuteChanged();
        UninstallOfficialCommand.NotifyCanExecuteChanged();
    }
}

public sealed class OfficialAppListing
{
    internal OfficialAppListing(string id, OfficialDesktopRelease? release, DeveloperAppInfo? installed)
    {
        Id = id;
        Release = release;
        Installed = installed;
    }

    internal OfficialDesktopRelease? Release { get; }
    internal DeveloperAppInfo? Installed { get; }
    public string Id { get; }
    public string DisplayName => Release?.DisplayName ?? Installed?.DisplayName ?? Id;
    public string AvailableVersion => Release?.Version ?? "";
    public string Description => Release?.Description ?? "";
    public string InstalledText => Installed is null ? LocalizedText.Get("app_installer.not_installed")
        : LocalizedText.Format("app_installer.current_version", Installed.Version);
}
