using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed class ApplicationAvailabilityUi(IWindowManager windows) : IApplicationAvailabilityUi
{
    public async Task<bool> ShowAsync(ApplicationAvailability availability)
    {
        var canManage = availability.State is ComponentAvailabilityKind.Missing or ComponentAvailabilityKind.External
            or ComponentAvailabilityKind.Recovery or ComponentAvailabilityKind.Busy;
        var accepted = false;
        var title = LocalizedText.Get("application." + availability.ApplicationId.Value + ".display_name");
        var message = LocalizedText.Get("component_center.availability." + availability.State);
        await windows.ShowShellDialogAsync<bool>(title, dialog => new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); },
                canManage ? LocalizedText.Get("component_center.open") : LocalizedText.Get("common.ok"))
        }, new Size(560, 260));
        return canManage && accepted;
    }
}
