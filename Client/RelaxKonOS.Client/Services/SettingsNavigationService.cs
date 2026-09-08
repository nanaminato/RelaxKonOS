using Avalonia.Threading;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services;

/// <summary>Host-owned route into Settings for built-in and package applications.</summary>
public sealed class SettingsNavigationService : ISettingsNavigation
{
    private readonly IAppActivationService _activations;

    public SettingsNavigationService(IAppActivationService activations) => _activations = activations;

    public Task OpenApplicationsAsync() => Dispatcher.UIThread.InvokeAsync(() =>
        _activations.Activate(new AppActivationRequest(RemoteOsActivationUris.SettingsApplications))).GetTask();
}
