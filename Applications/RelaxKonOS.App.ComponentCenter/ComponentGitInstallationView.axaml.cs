using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentGitInstallationView : UserControl
{
    public ComponentGitInstallationView() => InitializeComponent();
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
