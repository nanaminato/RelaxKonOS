using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RelaxKonOS.Client.Services.AppPackages;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.Services.Modules;
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
    private Guid? _invalidJointPlan;

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
    public string ActionLabel => LocalizedText.Get(CurrentPackage switch
    {
        { RequiresDeploymentReview: true } => "app_installer.deployment.review",
        { IsRecoveredDeployment: true } => "app_installer.deployment.resume",
        { IsRepair: true } => "app_installer.action.repair",
        { IsUpdate: true } => "common.update",
        _ => "common.install",
    });
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

    internal async Task OpenAsync()
    {
        await RefreshOfficialCommand.ExecuteAsync(null);
        if (_disposed) return;
        if (JointDeployment is { } deployment)
        {
            IsBusy = true;
            try
            {
                var recovered = await deployment.RecoverArchivesAsync(_officialReleases, _storeLifetime.Token);
                if (_disposed)
                {
                    foreach (var candidate in recovered)
                        _installer.Discard(candidate);
                    return;
                }
                foreach (var candidate in recovered)
                    _pending.Enqueue(candidate);
                ShowNext();
            }
            catch (OperationCanceledException) when (_disposed) { return; }
            catch (Exception error) when (AppPackageInstallerService.IsStoreFailure(error))
            {
                Status = LocalizedText.Ref("app_installer.status.install_failed", DeploymentProblem(error));
            }
            finally { IsBusy = false; }
        }
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
            if (candidate.Manifest.Dependencies is not null || candidate.IsRecoveredDeployment)
            {
                var joint = JointDeployment ?? throw new InvalidOperationException("deployment-coordinator-unavailable");
                if (_jointReview is null)
                {
                    var pending = await joint.FindPendingAsync(candidate, _storeLifetime.Token);
                    if (pending is not null && pending.Plan.Id != _invalidJointPlan)
                    {
                        if (ConfirmDeploymentAsync is null || !await ConfirmDeploymentAsync(pending.Plan)) return;
                        _jointReview = pending;
                    }
                }
                if (_jointReview is null)
                {
                    var optional = candidate.Manifest.Dependencies?.Optional ?? [];
                    var selected = optional.Count == 0 ? optional
                        : SelectOptionalAsync is { } select ? await select(optional) : null;
                    if (selected is null || ConfirmDeploymentAsync is null) return;
                    var review = await joint.PrepareAsync(candidate, selected, _storeLifetime.Token);
                    if (!await ConfirmDeploymentAsync(review.Plan)) return;
                    _jointReview = review;
                }
            }
            var reviewedCandidate = _jointReview?.Candidate ?? candidate;
            var installed = _jointReview is { } confirmed && JointDeployment is { } deployment
                ? await deployment.ExecuteAsync(confirmed, _storeLifetime.Token)
                : await _installer.InstallAsync(candidate);
            Status = LocalizedText.Ref(reviewedCandidate.IsRepair ? "app_installer.status.repaired"
                : reviewedCandidate.IsUpdate ? "app_installer.status.updated" : "app_installer.status.installed", installed.DisplayName, installed.Version);
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
            if (exception is DeploymentPlanReviewRequiredException
                || exception.Message is "deployment-plan-expired" or "deployment-snapshot-changed"
                or "deployment-module-plan-expired" or "deployment-server-changed" or "module-server-mismatch"
                or "deployment-client-dependency-changed")
            {
                _invalidJointPlan = _jointReview?.Plan.Id;
                _jointReview = null;
            }
            else if (exception.Message is "deployment-account-changed" or "module-session-changed")
                _jointReview = null;
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
        ModuleDeploymentRecoveryException { OperationId: { } operation } recovery =>
            LocalizedText.Format("app_installer.deployment.module_recovery_operation", recovery.ModuleId, operation),
        ModuleDeploymentRecoveryException recovery =>
            LocalizedText.Format("app_installer.deployment.module_recovery", recovery.ModuleId),
        ModulePackageDeploymentFailedException failure =>
            LocalizedText.Format("app_installer.deployment.module_package_failed", failure.ModuleId, failure.OperationId),
        { Message: "module-desktop-dependency-unavailable" } => LocalizedText.Get("app_installer.deployment.module_protection_unavailable"),
        { Message: "deployment-module-not-effective" } => LocalizedText.Get("app_installer.deployment.activation_pending"),
        { Message: "deployment-module-unhealthy" } => LocalizedText.Get("app_installer.deployment.health_pending"),
        { Message: "module-http-403" } => LocalizedText.Get("app_installer.deployment.no_permission"),
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } =>
            LocalizedText.Get("app_installer.deployment.server_unreachable"),
        { Message: "deployment-plan-in-progress" } => LocalizedText.Get("app_installer.deployment.plan_in_progress"),
        { Message: "deployment-account-changed" or "module-session-changed" } =>
            LocalizedText.Get("app_installer.deployment.session_changed"),
        { Message: "deployment-server-changed" or "module-server-mismatch" } =>
            LocalizedText.Get("app_installer.deployment.server_changed"),
        DeploymentPlanReviewRequiredException or
        { Message: "deployment-plan-expired" or "deployment-module-plan-expired" or "deployment-snapshot-changed"
            or "deployment-client-dependency-changed" } =>
            LocalizedText.Get("app_installer.deployment.review_again"),
        _ => error.Message,
    };

    partial void OnCurrentPackageChanged(AppPackageCandidate? value)
    {
        _jointReview = null;
        _invalidJointPlan = null;
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
        Status = CurrentPackage.RequiresDeploymentReview ? LocalizedText.Ref("app_installer.deployment.review_again")
            : CurrentPackage.IsRecoveredDeployment ? LocalizedText.Ref("app_installer.deployment.resume_pending")
            : CurrentPackage.IsRepair ? LocalizedText.Ref("app_installer.status.confirm_repair")
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
