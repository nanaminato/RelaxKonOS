using System.ComponentModel;
using Avalonia.Controls;
using RelaxKonOS.WindowManager;

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
    History,
}
