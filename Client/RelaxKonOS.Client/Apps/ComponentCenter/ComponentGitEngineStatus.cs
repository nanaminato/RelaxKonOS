using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

internal sealed partial class ComponentGitEngineStatus(IServerGit client) : ObservableObject, IComponentGitEngineStatus
{
    [ObservableProperty] private bool _isGitAvailable = true;
    [ObservableProperty] private bool _isGitInstallRequired;
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _enginePath = "—";

    ICommand IComponentGitEngineStatus.RefreshEngineStatusCommand => RefreshEngineStatusCommand;

    [RelayCommand]
    private async Task RefreshEngineStatusAsync()
    {
        try
        {
            var status = await client.GetEngineStatusAsync();
            IsGitAvailable = status.IsAvailable;
            IsGitInstallRequired = !status.IsAvailable
                && string.Equals(status.ProblemCode, "not_installed", StringComparison.OrdinalIgnoreCase);
            EngineVersion = string.IsNullOrWhiteSpace(status.Version) ? "—" : status.Version;
            EnginePath = string.IsNullOrWhiteSpace(status.ExecutablePath) ? "—" : status.ExecutablePath;
        }
        catch (Exception)
        {
            IsGitAvailable = false;
            IsGitInstallRequired = false;
            EngineVersion = "—";
            EnginePath = "—";
        }
    }
}
