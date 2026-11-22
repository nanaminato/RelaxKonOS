using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Data;
using RelaxKonOS.UI;
using RelaxKonOS.UI.ServerInstallation;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.AppSDK;

public interface IServerCenterPackageWorkspace
{
    void Open(Func<IServerCenterWorkspacePresentation, ManagedWindow> showWindow,
        Func<ServerRollbackReview, ModalDialog<bool>, Func<string, Binding>, Control> createRollbackConfirmation,
        ServerCenterManagementViewFactory createManagementView,
        Func<string, string, Action, Control> createHostAddressesView);
}

public interface IServerCenterPackageContext
{
    IServerCenterPackageWorkspace? ServerCenterWorkspace { get; }
}

public interface IServerCenterWorkspacePresentation : INotifyPropertyChanged
{
    string Title { get; }
    string Subtitle { get; }
    string HostsPageTitle { get; }
    string DeploymentPageTitle { get; }
    string HistoryPageTitle { get; }
    IServerCenterHistoryPresentation History { get; }
    IServerCenterHostsPresentation Hosts { get; }
    IServerCenterDeploymentPresentation Deployment { get; }
    string NavigationMenuTitle { get; }
    bool IsDeploymentProgressIndeterminate { get; }
    double DeploymentTransferProgress { get; }
    bool ShowWorkspaceProgress { get; }
    string StatusMessage { get; }
    string ErrorMessage { get; }
    bool HasError { get; }
}

public interface IServerCenterHostsPresentation : INotifyPropertyChanged
{
    string HostsPageTitle { get; }
    string HostsLabel { get; }
    IEnumerable<ServerHostTarget> Hosts { get; }
    ServerHostTarget? SelectedHost { get; set; }
    string EmptyHostsText { get; }
    bool HasHosts { get; }
    string VerifiedStateText { get; }
    bool HasVerifiedState { get; }
    bool NeedsHostKeyConfirmation { get; }
    string HostKeyReviewTitle { get; }
    string HostKeyReviewText { get; }
    bool HostKeyReplacesPinnedKey { get; }
    string PinnedFingerprintLabel { get; }
    string PreviousHostKeyFingerprint { get; }
    string PreviousHostKeyConfirmedText { get; }
    string ObservedFingerprintLabel { get; }
    string HostKeyFingerprint { get; }
    ICommand ConfirmHostKeyCommand { get; }
    string ConfirmHostKeyText { get; }
    ICommand RemoveHostCommand { get; }
    string RemoveText { get; }
    string AddHostLabel { get; }
    string Host { get; set; }
    string HostLabel { get; }
    string Port { get; set; }
    string PortLabel { get; }
    string UserName { get; set; }
    string UserLabel { get; }
    string DisplayName { get; set; }
    string DisplayNameLabel { get; }
    string NewHostPassword { get; set; }
    string NewHostPasswordLabel { get; }
    bool SaveNewHostPassword { get; set; }
    string SaveNewHostPasswordText { get; }
    ICommand AddHostCommand { get; }
    string AddText { get; }
}

public interface IServerCenterHistoryPresentation : INotifyPropertyChanged
{
    string HistoryPageTitle { get; }
    string SelectHostHint { get; }
    ServerHostTarget? SelectedHost { get; }
    string SshPassword { get; set; }
    string DeploymentPasswordHint { get; }
    IEnumerable Platforms { get; }
    object? SelectedPlatform { get; set; }
    string PlatformLabel { get; }
    ICommand LoadOperationHistoryCommand { get; }
    ICommand RefreshOperationCommand { get; }
    ICommand ClearOperationHistoryCommand { get; }
    string LoadOperationHistoryText { get; }
    string RefreshOperationText { get; }
    string ClearOperationHistoryText { get; }
    IEnumerable Operations { get; }
    object? SelectedOperation { get; set; }
    string OperationDetailsText { get; }
    string HistoryTimeHeader { get; }
    string HistoryKindHeader { get; }
    string HistoryStateHeader { get; }
    string HistoryMessageHeader { get; }
}


public interface IServerCenterDeploymentPresentation : INotifyPropertyChanged
{
    string DeploymentPageTitle { get; }
    string SelectedTargetLabel { get; }
    bool IsBusy { get; }
    IEnumerable<ServerHostTarget> Hosts { get; }
    ServerHostTarget? SelectedHost { get; set; }
    string EmptyHostsText { get; }
    bool HasHosts { get; }
    bool HasSelectedHost { get; }
    IServerStatusBadgePresentation HostStatus { get; }
    string VerifiedStateText { get; }
    bool HasVerifiedState { get; }
    string CachedStateText { get; }
    string SshPassword { get; set; }
    string DeploymentPasswordHint { get; }
    string MaintenanceSudoPassword { get; set; }
    bool ShowMaintenanceSudoPassword { get; }
    string MaintenanceSudoPasswordText { get; }
    string PlatformLabel { get; }
    string SelectedPlatformText { get; }
    string LastProbeText { get; }
    bool HasLastProbe { get; }
    string IncompleteInstallationText { get; }
    bool HasIncompleteInstallation { get; }
    ICommand RecoverCommand { get; }
    string RecoverText { get; }
    bool HasManagedInstallation { get; }
    string ComponentsText { get; }
    string SettingsText { get; }
    ICommand ProbeHostCommand { get; }
    string ProbeText { get; }
    string RefreshActionDescription { get; }
    ICommand OpenInstallationWizardCommand { get; }
    string DeployText { get; }
    string InstallActionDescription { get; }
    ICommand UpdateCommand { get; }
    string UpdateText { get; }
    string InstallationInfoTitle { get; }
    string RepairText { get; }
    string RepairActionDescription { get; }
    ICommand RollbackCommand { get; }
    string RollbackText { get; }
    bool HasPreviousVersion { get; }
    string RollbackActionDescription { get; }
    string UninstallText { get; }
    string UninstallActionDescription { get; }

    Task EnsureSelectedHostPlatformAsync(CancellationToken cancellationToken = default);
    Task ShowManagementSectionAsync(string section);
}

public sealed record ServerRollbackReview(string Host, string CurrentVersion, string PreviousVersion);


public delegate Control ServerCenterManagementViewFactory(IServerCenterManagementPresentation presentation,
    string section, Action close, Func<Task> browseRepairPackage);

public interface IServerCenterManagementPresentation : INotifyPropertyChanged
{
    string ApplyConfigurationText { get; }
    string AutoStartText { get; }
    string BrowseRepairPackageText { get; }
    bool CanConfigureAutoStart { get; }
    bool CanConfigureFileAccess { get; }
    bool CanConfigureGuardian { get; }
    bool CanManageCertificate { get; }
    bool CanManageFirewall { get; }
    string CloseText { get; }
    string ComponentSelectionText { get; }
    string ComponentsText { get; }
    bool ConfigurationAutoStart { get; set; }
    FileAccessOption? ConfigurationFileAccess { get; set; }
    IReadOnlyList<FileAccessOption> ConfigurationFileAccesses { get; }
    string ConfigurationFileRoots { get; set; }
    bool ConfigurationGuardian { get; set; }
    bool ConfigurationLan { get; set; }
    string ConfigurationLanText { get; }
    string ConfigurationNote { get; }
    string ConfigurationPort { get; set; }
    string ConfigurationPortText { get; }
    ICommand ConfigureCommand { get; }
    IEnumerable<IServerStatusBadgePresentation> CurrentConfigurationDetails { get; }
    string CurrentConfigurationTitle { get; }
    bool DeleteServerData { get; set; }
    string DeleteServerDataText { get; }
    ICommand EnsureFirewallCommand { get; }
    string EnsureFirewallText { get; }
    string ErrorMessage { get; }
    string FileAccessDescription { get; }
    string FileAccessText { get; }
    string FileRootsText { get; }
    string FirewallChoiceText { get; }
    string FirewallHelpText { get; }
    string GuardianChoiceText { get; }
    string GuardianDescription { get; }
    bool HasError { get; }
    bool HasManagementStatus { get; }
    IServerStatusBadgePresentation HostStatus { get; }
    IEnumerable<IServerStatusBadgePresentation> InstallationDetails { get; }
    string InstallationInfoNote { get; }
    string InstallationInfoTitle { get; }
    bool IsBusy { get; }
    bool MaintenanceAddFirewallRule { get; set; }
    string NetworkSettingsTitle { get; }
    ICommand RemoveFirewallCommand { get; }
    string RemoveFirewallText { get; }
    bool RemoveFrp { get; set; }
    string RemoveFrpText { get; }
    bool RemoveMihomo { get; set; }
    string RemoveMihomoText { get; }
    bool RemoveNginx { get; set; }
    string RemoveNginxText { get; }
    bool RemoveSmb { get; set; }
    string RemoveSmbText { get; }
    string RepairActionDescription { get; }
    string RepairCertificateIdentities { get; set; }
    string RepairCertificateNote { get; }
    string RepairCertificateText { get; }
    ICommand RepairCommand { get; }
    bool RepairLanCertificate { get; set; }
    string RepairPackagePath { get; set; }
    string RepairPackageText { get; }
    string RepairPlanText { get; }
    string RepairText { get; }
    ServerHostTarget? SelectedHost { get; }
    bool ShowConfigurationFileRoots { get; }
    string StatusMessage { get; }
    string UninstallActionDescription { get; }
    ICommand UninstallCommand { get; }
    string UninstallText { get; }
    string VerifiedStateText { get; }
}
