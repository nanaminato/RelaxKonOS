using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.AppSDK;

public sealed record ComponentCenterPresentation(
    IComponentCenterLayout Model,
    Control ModulesView,
    Control NginxView,
    Control RuntimeView,
    Action OpenApplication);

public interface IComponentCenterNavigation
{
    bool ShowComponentBrowser { get; }
    bool HasResults { get; }
    string Search { get; set; }
    IEnumerable<IComponentCenterPageItem> BrowsePages { get; }
    IComponentCenterPageItem? SelectedNavigation { get; set; }
    IEnumerable<IComponentCenterTreeItem> VisibleRoots { get; }
    IComponentCenterTreeItem? SelectedNode { get; set; }
}

public interface IComponentCenterPageItem
{
    string Label { get; }
}

public interface IComponentCenterTreeItem
{
    string Label { get; }
    IReadOnlyList<IComponentCenterTreeItem> VisibleChildren { get; }
    bool ShowStatus { get; }
    bool StatusIdle { get; }
    bool StatusOk { get; }
    bool StatusWarning { get; }
    bool StatusDanger { get; }
    string Status { get; }
    bool ShowVersion { get; }
    string Version { get; }
    bool HasUpdate { get; }
    bool ShowRequirement { get; }
    string Requirement { get; }
}

public interface IComponentGitInstallation : IComponentInstallationProgress
{
    IComponentGitEngineStatus? GitEngine { get; }
}

public interface IComponentGitEngineStatus : INotifyPropertyChanged
{
    string EngineVersion { get; }
    string EnginePath { get; }
    ICommand RefreshEngineStatusCommand { get; }
}

public interface IComponentInstallationProgress
{
    InstallationTaskViewModel? PackageInstallation { get; }
    ICommand ReviewServiceStopActionCommand { get; }
    bool CanReviewServiceStop { get; }
}

public interface IComponentCenterLayout
{
    string HostIdentity { get; }
    string ConnectionText { get; }
    string HostTargetText { get; }
    bool HasTargetText { get; }
    string HeaderOutcomeText { get; }
    bool HasHeaderOutcome { get; }

    bool ShowServerModules { get; }
    bool ShowCatalogSources { get; }
    ICatalogSourcesViewModel? CatalogSources { get; }
    bool ShowComponentBrowser { get; }
    bool HasComponentDetails { get; }
    bool ShowDesktopPackage { get; }
    bool ShowNginxSelection { get; }
    bool ShowGitInstallation { get; }
    bool ShowNotIntegrated { get; }
    bool ShowHostObservation { get; }
    bool ShowOfficialDownloads { get; }
    bool ShowPackageFreeze { get; }
    bool HasRuntimeDetails { get; }
    bool HasResults { get; }
}

public interface IComponentPackageCandidate : IComponentInstallationProgress
{
    string PackageMethodTitle { get; }
    string PackageMethodHint { get; }
    ICommand ResolvePackageCommand { get; }
    bool CanResolvePackage { get; }
    string PlatformMethodText { get; }
    string PackageStateText { get; }
    bool HasPackageStateProblem { get; }
    bool IsSystemPackage { get; }
    string SelectedPackageManagerText { get; }
    bool HasPackageManagerChoice { get; }
    IReadOnlyList<string> PackageManagers { get; }
    string? SelectedPackageManager { get; set; }
    bool IsResolvingPackage { get; }
    string PackageSourceActionText { get; }
    ICommand RefreshPackageSourcesCommand { get; }
    bool CanRefreshPackageSources { get; }
    bool HasPinnedArchiveCandidates { get; }
    IReadOnlyList<ComponentPinnedArchive> PinnedArchiveCandidates { get; }
    ComponentPinnedArchive? SelectedPinnedArchive { get; set; }
    bool ShowInstallOnlyNote { get; }
    string PackageInstalledText { get; }
    string PackageCandidateText { get; }
    bool HasPackageFreeze { get; }
    string PackageRepositoryText { get; }
    string PackageAlsoInstallsText { get; }
    string PackageDigestText { get; }
    string PackageVerdictText { get; }
    ICommand InstallPackageCommand { get; }
    bool CanInstallPackage { get; }
    bool ShowPackageInstall { get; }
    bool ShowPackageChangeActions { get; }
    ICommand UpdatePackageCommand { get; }
    bool CanChangeInstalledPackage { get; }
    bool ShowRegisterPackage { get; }
    string RegisterPackageText { get; }
    ICommand RegisterPackageCommand { get; }
    bool CanRegisterPackage { get; }
    ICommand UninstallPackageCommand { get; }
    bool CanRecoverPostgreSql { get; }
    ICommand RecoverPostgreSqlCommand { get; }
    bool CanRecoverPackage { get; }
    string PackageRecoveryText { get; }
    ICommand StopPackageRecoveryCommand { get; }
    ICommand ClosePackageRecoveryCommand { get; }
    string ExternalPackageChangeText { get; }
    bool HasExternalPackageChange { get; }
    string PackageFreezeNotice { get; }
}

public interface IComponentCenterHeaderActions
{
    ICommand OpenTasksCommand { get; }
    ICommand RefreshCommand { get; }
    IComponentCenterRefreshState Runtime { get; }
}

public interface IComponentCenterRefreshState
{
    bool CanRefreshSources { get; }
}

public interface IComponentHostObservation
{
    ICommand ObserveHostCommand { get; }
    bool CanObserveHost { get; }
    bool IsObservingHost { get; }
    bool HasHostObservation { get; }
    string HostObservationPlatformText { get; }
    string HostObservationManagementText { get; }
    string HostObservationVersionText { get; }
    string HostObservationStateText { get; }
    string HostObservationNoteText { get; }
    string HostObservationNotice { get; }
}

public interface IComponentCenterEmptyState
{
    string EmptyMessage { get; }
}

public interface IComponentIdentity : IComponentApplicationLink
{
    string SelectedComponentTitle { get; }
    string SelectedStatusText { get; }
    bool SelectedToneOk { get; }
    bool SelectedToneWarning { get; }
    bool SelectedToneDanger { get; }
    bool SelectedToneIdle { get; }
    bool SelectedHasUpdate { get; }
    bool HasSelectedVersion { get; }
    string SelectedVersionText { get; }
    string SelectedComponentDescription { get; }
    string SelectedCatalogProvenance { get; }
    bool ShowHeaderOpenApplication { get; }
    string SelectedCategoryText { get; }
    string SelectedIntegrationText { get; }
    string SelectedApplicationText { get; }
    bool HasHostPlatformSources { get; }
    bool ShowOtherPlatformSources { get; }
    IReadOnlyList<IComponentSourceChip> SelectedHostSources { get; }
    IReadOnlyList<IComponentSourceChip> SelectedOtherSources { get; }
}

public interface IComponentSourceChip
{
    string Text { get; }
    string Platform { get; }
    bool Ready { get; }
}

public interface IComponentApplicationNotice : IComponentApplicationLink
{
    string NotIntegratedMessage { get; }
    bool CanOpenSelectedApplication { get; }
}

public interface IComponentApplicationLink
{
    string SelectedApplicationOpenText { get; }
}

public interface IComponentDesktopPackage
{
    string DesktopCompatibilityText { get; }
    ICommand ReviewDesktopCatalogPackageCommand { get; }
}

public interface IComponentOfficialDownloads : IComponentInstallationProgress
{
    ICommand RefreshOfficialReleasesCommand { get; }
    bool CanRefreshOfficial { get; }
    bool IsLoadingOfficialReleases { get; }
    IReadOnlyList<IComponentDownloadMethodChoice> DownloadMethods { get; }
    IComponentDownloadMethodChoice SelectedDownloadMethod { get; set; }
    bool IsInstallingOfficial { get; }
    bool ShowOnlineVersions { get; }
    IReadOnlyList<ComponentDownloadRelease> OfficialReleases { get; }
    ComponentDownloadRelease? SelectedOfficialRelease { get; set; }
    bool ShowManualVersion { get; }
    string OfficialVersion { get; set; }
    bool IsFileSource { get; }
    string SelectedPackageFileText { get; }
    ICommand SelectPackageFileCommand { get; }
    bool IsSelectingPackageFile { get; }
    string OfficialDownloadNotice { get; }
    string OfficialDownloadUrl { get; }
    ICommand OpenOfficialDownloadCommand { get; }
    ICommand InstallOfficialCommand { get; }
    bool CanInstallOfficial { get; }
}

public interface IComponentDownloadMethodChoice
{
    string Text { get; }
}

public interface ICatalogSourcesViewModel
{
    IEnumerable<ICatalogSourceRow> Sources { get; }
    bool IsBusy { get; }
    bool IsReviewing { get; }
    bool CanEdit { get; }
    string SourceId { get; set; }
    string DisplayName { get; set; }
    string PublisherId { get; set; }
    string BaseUri { get; set; }
    string CatalogPath { get; set; }
    string KeyId { get; set; }
    string PublicKeySpki { get; set; }
    string MaximumValiditySeconds { get; set; }
    bool AllowRoapp { get; set; }
    bool AllowApt { get; set; }
    bool AllowArchive { get; set; }
    bool AllowDownload { get; set; }
    string PrivateNetwork { get; set; }
    string PrivatePort { get; set; }
    string ReviewIdentity { get; }
    string ReviewTrust { get; }
    string ReviewOrigin { get; }
    string ReviewKeys { get; }
    string ReviewProviders { get; }
    string ReviewNetworks { get; }
    string ReviewValidity { get; }
    string ReviewFingerprint { get; }
    string Notice { get; }
    ICommand RefreshCommand { get; }
    ICommand EditCommand { get; }
    ICommand DisableCommand { get; }
    ICommand RemoveCommand { get; }
    ICommand ReviewCommand { get; }
    ICommand ConfirmCommand { get; }
    ICommand CancelReviewCommand { get; }
}

public interface ICatalogSourceRow
{
    string Name { get; }
    string Trust { get; }
    string Status { get; }
    string Sequence { get; }
    string Id { get; }
    string Publisher { get; }
    string Origin { get; }
    string Fingerprints { get; }
    string Providers { get; }
    string Problem { get; }
}
