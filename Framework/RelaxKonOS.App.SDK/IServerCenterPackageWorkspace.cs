using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using RelaxKonOS.UI;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.AppSDK;

public interface IServerCenterPackageWorkspace
{
    void Open(Func<IServerCenterWorkspacePresentation, ManagedWindow> showWindow);
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
