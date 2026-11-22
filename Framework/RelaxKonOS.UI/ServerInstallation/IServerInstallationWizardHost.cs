using System.ComponentModel;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.UI.ServerInstallation;

public interface IServerInstallationWizardHost : INotifyPropertyChanged
{
    ServerHostTarget? SelectedHost { get; }
    HostPlatformKind? SelectedPlatformKind { get; }
    bool IsBusy { get; }
    string ErrorMessage { get; }
    string StatusMessage { get; }
    string DeployText { get; }
    double DeploymentTransferProgress { get; }
    bool IsDeploymentProgressIndeterminate { get; }

    string Text(string key, string fallback);
    Task<bool> DeployAsync(ServerInstallationOptions installation, CancellationToken cancellationToken = default);
}
