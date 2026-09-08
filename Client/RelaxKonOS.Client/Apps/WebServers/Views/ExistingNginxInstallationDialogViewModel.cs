using RelaxKonOS.Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.WebServers;

namespace RelaxKonOS.Client.Apps.WebServers.Views;

/// <summary>Explicit, user-selected handling for an unmarked Nginx directory at the managed path.</summary>
internal sealed partial class ExistingNginxInstallationDialogViewModel(Action<ManagedInstallExistingDirectoryAction?> complete) : ObservableObject
{
    public string Message => LocalizedText.Get("webservers.managed.existing.message");
    public string ReuseLabel => LocalizedText.Get("webservers.managed.existing.reuse");
    public string ReplaceLabel => LocalizedText.Get("webservers.managed.existing.replace");

    [RelayCommand]
    private void Reuse() => complete(ManagedInstallExistingDirectoryAction.Reuse);

    [RelayCommand]
    private void Replace() => complete(ManagedInstallExistingDirectoryAction.Replace);

    [RelayCommand]
    private void Cancel() => complete(null);
}
