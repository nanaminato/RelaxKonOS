using System.Collections;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.AppSDK;

public interface IServerCenterPackageWorkspace
{
    void Open(Func<IServerCenterWorkspacePresentation, Func<ServerCenterWorkspacePage, Control>, ManagedWindow> showWindow);
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
    string NavigationMenuTitle { get; }
    bool IsDeploymentProgressIndeterminate { get; }
    double DeploymentTransferProgress { get; }
    bool ShowWorkspaceProgress { get; }
    string StatusMessage { get; }
    string ErrorMessage { get; }
    bool HasError { get; }
}

public enum ServerCenterWorkspacePage
{
    Hosts,
    Deployment,
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
