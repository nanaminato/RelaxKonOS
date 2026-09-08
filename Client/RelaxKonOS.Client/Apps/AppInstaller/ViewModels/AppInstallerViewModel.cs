using System.Collections.ObjectModel;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.AppInstaller.ViewModels;

/// <summary>Queues packages and requires an explicit install/update action for every package.</summary>
public sealed partial class AppInstallerViewModel : ObservableObject, IDisposable
{
    private readonly AppPackageInstallerService _installer;
    private readonly LocalizationService _localization;
    private readonly Queue<AppPackageCandidate> _pending = new();

    public AppInstallerViewModel(AppPackageInstallerService installer)
    {
        _installer = installer;
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public Func<Task<IReadOnlyList<string>>>? RequestLocalPackagesAsync { get; set; }
    public Func<Task<IReadOnlyList<string>>>? RequestServerPackagesAsync { get; set; }
    public Func<string, string, Task>? ShowMessageAsync { get; set; }

    [ObservableProperty] private AppPackageCandidate? _currentPackage;
    [ObservableProperty] private string _status = LocalizedText.Get("app_installer.status.choose_package");
    [ObservableProperty] private bool _isBusy;

    public bool HasPackage => CurrentPackage is not null;
    public string ActionLabel => CurrentPackage?.IsUpdate == true ? LocalizedText.Get("common.update") : LocalizedText.Get("common.install");
    public string CurrentVersionText => CurrentPackage?.InstalledVersion is null ? LocalizedText.Get("app_installer.not_installed") : LocalizedText.Format("app_installer.current_version", CurrentPackage.InstalledVersion);
    public int PendingCount => _pending.Count;
    public IReadOnlyList<LocalizedPermissionInfo> RequestedPermissions => CurrentPackage?.Manifest.RequestedPermissions
        ?.Select(AppPermissions.Find).OfType<AppPermissionDefinition>()
        .Select(permission => new LocalizedPermissionInfo(PermissionText.DisplayName(permission), PermissionText.Description(permission)))
        .ToArray() ?? [];
    public bool HasRequestedPermissions => RequestedPermissions.Count > 0;

    [RelayCommand]
    private async Task ChooseLocalAsync()
    {
        if (RequestLocalPackagesAsync is null) return;
        await QueueLocalPackagesAsync(await RequestLocalPackagesAsync());
    }

    [RelayCommand]
    private async Task ChooseServerAsync()
    {
        if (RequestServerPackagesAsync is null) return;
        await QueueServerPackagesAsync(await RequestServerPackagesAsync());
    }

    public async Task QueueLocalPackagesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try { _pending.Enqueue(await _installer.InspectLocalAsync(path)); }
            catch (Exception exception) { await ReportAsync(LocalizedText.Get("app_installer.error.read_title"), LocalizedText.Format("app_installer.error.package", Path.GetFileName(path), exception.Message)); }
        }
        ShowNext();
    }

    public async Task QueueServerPackagesAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        Status = LocalizedText.Get("app_installer.status.downloading");
        try
        {
            foreach (var path in paths)
            {
                try { _pending.Enqueue(await _installer.StageServerPackageAsync(path)); }
                catch (Exception exception) { await ReportAsync(LocalizedText.Get("app_installer.error.download_title"), LocalizedText.Format("app_installer.error.package", Path.GetFileName(path), exception.Message)); }
            }
        }
        finally { IsBusy = false; }
        ShowNext();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (CurrentPackage is null) return;
        IsBusy = true;
        try
        {
            var installed = await _installer.InstallAsync(CurrentPackage);
            Status = LocalizedText.Format(CurrentPackage.IsUpdate ? "app_installer.status.updated" : "app_installer.status.installed", installed.DisplayName, installed.Version);
            CurrentPackage = null;
            ShowNext();
        }
        catch (Exception exception)
        {
            Status = LocalizedText.Format("app_installer.status.install_failed", exception.Message);
            await ReportAsync(LocalizedText.Get("app_installer.error.install_title"), Status);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        if (CurrentPackage is not null) _installer.Discard(CurrentPackage);
        CurrentPackage = null;
        Status = LocalizedText.Get("app_installer.status.skipped");
        ShowNext();
    }

    private bool CanInstall() => CurrentPackage is not null && !IsBusy;
    private bool CanSkip() => CurrentPackage is not null && !IsBusy;

    partial void OnCurrentPackageChanged(AppPackageCandidate? value)
    {
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(RequestedPermissions));
        OnPropertyChanged(nameof(HasRequestedPermissions));
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    private void ShowNext()
    {
        if (CurrentPackage is not null || _pending.Count == 0) return;
        CurrentPackage = _pending.Dequeue();
        Status = CurrentPackage.IsUpdate ? LocalizedText.Get("app_installer.status.confirm_update") : LocalizedText.Get("app_installer.status.confirm_install");
        OnPropertyChanged(nameof(PendingCount));
    }

    private Task ReportAsync(string title, string message) => ShowMessageAsync?.Invoke(title, message) ?? Task.CompletedTask;

    public void Dispose()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        if (CurrentPackage is not null)
            _installer.Discard(CurrentPackage);
        while (_pending.TryDequeue(out var candidate))
            _installer.Discard(candidate);
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(RequestedPermissions));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CurrentVersionText));
    }
}

public sealed record LocalizedPermissionInfo(string DisplayName, string Description);
