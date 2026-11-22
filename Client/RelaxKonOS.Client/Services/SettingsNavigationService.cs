using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Services;

/// <summary>Host-owned route into Settings for built-in and package applications.</summary>
public sealed class SettingsNavigationService : ISettingsNavigation
{
    private readonly IAppActivationService _activations;
    private static readonly Uri Recovery = RelaxKonOSActivationUris.LaunchApplication(new AppId("relaxkonos.appinstaller"));

    public SettingsNavigationService(IAppActivationService activations) => _activations = activations;

    public Task OpenApplicationsAsync() => OpenAsync(RelaxKonOSActivationUris.SettingsApplications);

    public Task OpenEnvironmentAsync() => OpenAsync(RelaxKonOSActivationUris.SettingsEnvironment);

    private Task OpenAsync(Uri uri) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var result = _activations.Activate(new AppActivationRequest(uri));
        if (result.Status == AppActivationStatus.RouteNotFound)
            _activations.Activate(new AppActivationRequest(Recovery));
    }).GetTask();
}
