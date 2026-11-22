using System.Windows.Input;
using Avalonia.Controls;
using RelaxKonOS.Protocol.ManagedRuntimes;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentPackageCandidateView : UserControl
{
    public ComponentPackageCandidateView() => InitializeComponent();
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
