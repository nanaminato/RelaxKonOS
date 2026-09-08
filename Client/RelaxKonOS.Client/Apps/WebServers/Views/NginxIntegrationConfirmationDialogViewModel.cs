using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

/// <summary>State for the Nginx integration confirmation dialog.</summary>
internal sealed partial class NginxIntegrationConfirmationDialogViewModel(
    Action<bool> complete,
    string messageKey = "webservers.integration.confirmation.message",
    string confirmKey = "webservers.integration.confirmation.confirm") : ObservableObject
{
    public string Message => LocalizedText.Get(messageKey);
    public string ConfirmLabel => LocalizedText.Get(confirmKey);

    [RelayCommand]
    private void Confirm() => complete(true);

    [RelayCommand]
    private void Cancel() => complete(false);
}
