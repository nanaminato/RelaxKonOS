using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Deployment;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public sealed class ApplicationAvailabilityUi(IWindowManager windows, ApplicationManager applications, LocalizationService localization)
    : IApplicationAvailabilityUi
{
    public async Task<bool> ShowAsync(ApplicationAvailability availability)
    {
        // Only the states a person can act on in Component Center offer the jump; an unreadable or
        // unpermitted state is reported without pretending that opening the center would fix it.
        var canManage = availability.State is ComponentAvailabilityKind.Missing or ComponentAvailabilityKind.External
            or ComponentAvailabilityKind.Recovery or ComponentAvailabilityKind.Busy;
        var accepted = false;
        var application = applications.Registered.FirstOrDefault(app => app.Id == availability.ApplicationId);
        var title = localization.Get("application." + availability.ApplicationId.Value + ".display_name",
            application?.GetLocalizedMetadata(localization.CurrentLanguage).DisplayName ?? availability.ApplicationId.Value);
        var message = canManage && availability.PackageKind is { } kind
            ? LocalizedText.Format(kind == DeploymentPackageKind.Application
                ? "component_center.availability.package_repair" : "component_center.availability.module_repair",
                title, availability.ComponentId)
            : LocalizedText.Format("component_center.availability." + availability.State, title,
                ComponentCatalog.Find(availability.ComponentId)?.DisplayName ?? availability.ComponentId);
        await windows.ShowShellDialogAsync<bool>(title, dialog => new ConfirmDialogView
        {
            DataContext = new ConfirmDialogViewModel(message, value => { accepted = value; dialog.Close(value); },
                canManage ? LocalizedText.Get(availability.PackageKind == DeploymentPackageKind.Application
                    ? "app_installer.title" : "component_center.open") : LocalizedText.Get("common.ok"))
        }, new Size(560, 260));
        return canManage && accepted;
    }
}
