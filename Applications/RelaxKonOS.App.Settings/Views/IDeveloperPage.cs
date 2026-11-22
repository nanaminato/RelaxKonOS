using System.ComponentModel;
using System.Windows.Input;

namespace RelaxKonOS.App.Settings.Views;

public interface IDeveloperPage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IDeveloperModePresentation DeveloperMode { get; }
    INetworkInspectorLauncher NetworkInspector { get; }
}

public interface IDeveloperModePresentation : INotifyPropertyChanged
{
    bool IsEnabled { get; set; }
    string Endpoint { get; }
    string PairingToken { get; }
    ICommand RegeneratePairingTokenCommand { get; }
}

public interface INetworkInspectorLauncher : INotifyPropertyChanged
{
    string Status { get; }
    bool CanOpen { get; }
    ICommand OpenCommand { get; }
}
