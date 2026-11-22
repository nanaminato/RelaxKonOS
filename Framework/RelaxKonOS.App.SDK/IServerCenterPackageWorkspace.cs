using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.AppSDK;

public interface IServerCenterPackageWorkspace
{
    void Open(Func<IServerCenterWorkspacePresentation, Func<Control>, ManagedWindow> showWindow);
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
