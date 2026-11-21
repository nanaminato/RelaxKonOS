using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Deployment;

namespace RelaxKonOS.Client.Apps.AppInstaller.ViewModels;

/// <summary>Queues packages and requires an explicit install/update action for every package.</summary>
public sealed partial class AppInstallerViewModel : ObservableObject, IDisposable
{
    private readonly AppPackageInstallerService _installer;
    private readonly LocalizationService _localization;
    private readonly Queue<AppPackageCandidate> _pending = new();
    private JointAppDeploymentService.Review? _jointReview;
    private Guid? _expiredJointPlan;

    public AppInstallerViewModel(AppPackageInstallerService installer)
    {
        _installer = installer;
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public Func<Task<IReadOnlyList<string>>>? RequestLocalPackagesAsync { get; set; }
    public bool AllowPackageSelection { get; init; } = true;
    public Func<Task<IReadOnlyList<string>>>? RequestServerPackagesAsync { get; set; }
    public Func<string, string, Task>? ShowMessageAsync { get; set; }
    internal JointAppDeploymentService? JointDeployment { get; set; }
    internal Func<IReadOnlyList<DeploymentRequirement>, Task<IReadOnlyList<DeploymentRequirement>?>>? SelectOptionalAsync { get; set; }
    internal Func<DeploymentPlan, Task<bool>>? ConfirmDeploymentAsync { get; set; }

    [ObservableProperty] private AppPackageCandidate? _currentPackage;
    [ObservableProperty] private LocalizedStatus _status;
    [ObservableProperty] private bool _isBusy;

    public IImage? PackageIcon { get; private set; }
    public bool HasPackageIcon => PackageIcon is not null;
    public string PackageIconGlyph => string.IsNullOrWhiteSpace(CurrentPackage?.Manifest.IconGlyph) ? "📦" : CurrentPackage.Manifest.IconGlyph;
    public bool HasPackage => CurrentPackage is not null;
    public string ActionLabel => CurrentPackage?.IsRepair == true ? LocalizedText.Get("app_installer.action.repair")
        : CurrentPackage?.IsUpdate == true ? LocalizedText.Get("common.update") : LocalizedText.Get("common.install");
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

    public void QueueCatalogPlan(RelaxKonOS.Client.Apps.ComponentCenter.RoappComponentInstallPlan plan)
    {
        _pending.Enqueue(plan.Package);
        ShowNext(); // Always wait for the existing explicit Install/Update command.
    }

    public async Task QueueServerPackagesAsync(IEnumerable<string> paths)
    {
        IsBusy = true;
        Status = LocalizedText.Ref("app_installer.status.downloading");
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
            var candidate = CurrentPackage;
            if (candidate.Manifest.Dependencies is { } dependencies)
            {
                var joint = JointDeployment ?? throw new InvalidOperationException("deployment-coordinator-unavailable");
                if (_jointReview is null)
                {
                    var pending = await joint.FindPendingAsync(candidate, _storeLifetime.Token);
                    if (pending is not null && pending.Plan.Id != _expiredJointPlan)
                    {
                        if (ConfirmDeploymentAsync is null || !await ConfirmDeploymentAsync(pending.Plan)) return;
                        _jointReview = pending;
                    }
                }
                if (_jointReview is null)
                {
                    var optional = dependencies.Optional ?? [];
                    var selected = optional.Count == 0 ? optional
                        : SelectOptionalAsync is { } select ? await select(optional) : null;
                    if (selected is null || ConfirmDeploymentAsync is null) return;
                    var review = await joint.PrepareAsync(candidate, selected, _storeLifetime.Token);
                    if (!await ConfirmDeploymentAsync(review.Plan)) return;
                    _jointReview = review;
                }
            }
            var installed = _jointReview is { } confirmed && JointDeployment is { } deployment
                ? await deployment.ExecuteAsync(confirmed, _storeLifetime.Token)
                : await _installer.InstallAsync(candidate);
            Status = LocalizedText.Ref(CurrentPackage.IsRepair ? "app_installer.status.repaired"
                : CurrentPackage.IsUpdate ? "app_installer.status.updated" : "app_installer.status.installed", installed.DisplayName, installed.Version);
            CurrentPackage = null;
            ShowNext();
            RefreshOfficialRows();
        }
        catch (PackageStorageUnavailableException exception)
        {
            // The drive keeps a single fixed package folder, so this failure is environmental rather
            // than a broken package: name the folder and what to do about it.
            Status = LocalizedText.Ref("app_installer.status.storage_unavailable", exception.Folder);
            await ReportAsync(LocalizedText.Get("app_installer.error.install_title"), Status);
        }
        catch (Exception exception)
        {
            if (exception.Message is "deployment-plan-expired" or "deployment-snapshot-changed"
                or "deployment-module-plan-expired")
            {
                _expiredJointPlan = _jointReview?.Plan.Id;
                _jointReview = null;
            }
            Status = LocalizedText.Ref("app_installer.status.install_failed", DeploymentProblem(exception));
            await ReportAsync(LocalizedText.Get("app_installer.error.install_title"), Status);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSkip))]
    private void Skip()
    {
        if (CurrentPackage is not null) _installer.Discard(CurrentPackage);
        CurrentPackage = null;
        Status = LocalizedText.Ref("app_installer.status.skipped");
        ShowNext();
    }

    private bool CanInstall() => CurrentPackage is not null && !IsBusy;
    private bool CanSkip() => CurrentPackage is not null && !IsBusy;

    private static string DeploymentProblem(Exception error) => error switch
    {
        DeploymentResolutionException { Message: "deployment-dependency-conflict", Package: { } package } =>
            LocalizedText.Format(package.Kind == DeploymentPackageKind.ServerModule
                ? "app_installer.deployment.module_unavailable" : "app_installer.deployment.app_unavailable", package.Id),
        { Message: "deployment-module-AwaitingRestart" } => LocalizedText.Get("app_installer.deployment.restart_pending"),
        { Message: "deployment-module-not-effective" } => LocalizedText.Get("app_installer.deployment.activation_pending"),
        { Message: "module-http-403" } => LocalizedText.Get("app_installer.deployment.no_permission"),
        _ => error.Message,
    };

    partial void OnCurrentPackageChanged(AppPackageCandidate? value)
    {
        _jointReview = null;
        _expiredJointPlan = null;
        (PackageIcon as IDisposable)?.Dispose();
        PackageIcon = null;
        if (value?.IconData is { Length: > 0 } data)
        {
            try
            {
                using var stream = new MemoryStream(data);
                PackageIcon = Bitmap.DecodeToWidth(stream, 72);
            }
            catch (Exception) { /* An invalid optional icon must not hide the installation review. */ }
        }
        OnPropertyChanged(nameof(PackageIcon));
        OnPropertyChanged(nameof(HasPackageIcon));
        OnPropertyChanged(nameof(PackageIconGlyph));
        OnPropertyChanged(nameof(HasPackage));
        OnPropertyChanged(nameof(ShowOfficialStore));
        OnPropertyChanged(nameof(ShowPackageSecurityWarning));
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(CurrentVersionText));
        OnPropertyChanged(nameof(RequestedPermissions));
        OnPropertyChanged(nameof(HasRequestedPermissions));
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        NotifyOfficialActions();
    }

    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        NotifyOfficialActions();
    }

    private void ShowNext()
    {
        if (CurrentPackage is not null || _pending.Count == 0) return;
        CurrentPackage = _pending.Dequeue();
        Status = CurrentPackage.IsRepair ? LocalizedText.Ref("app_installer.status.confirm_repair")
            : CurrentPackage.IsUpdate ? LocalizedText.Ref("app_installer.status.confirm_update") : LocalizedText.Ref("app_installer.status.confirm_install");
        OnPropertyChanged(nameof(PendingCount));
    }

    private Task ReportAsync(string title, string message) => ShowMessageAsync?.Invoke(title, message) ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storeLifetime.Cancel();
        _storeLifetime.Dispose();
        _localization.LanguageChanged -= OnLanguageChanged;
        (PackageIcon as IDisposable)?.Dispose();
        PackageIcon = null;
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
        RefreshOfficialRows();
        NotifyOfficialActions();
    }
}

public sealed record LocalizedPermissionInfo(string DisplayName, string Description);
