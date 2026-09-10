using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

/// <summary>Opens the AXAML dialogs with their owner, title and size.</summary>
internal static class FileServicesDialogs
{
    public static Task<string?> RequestPasswordAsync(AppContext context, ManagedWindow owner, string title) =>
        context.ShowDialogAsync<string?>(owner, title, dialog => new FileServicesPasswordDialogView(dialog), new Size(420, 160));

    public static Task ShowShareEditorAsync(AppContext context, ManagedWindow owner, FileServicesViewModel vm, bool editing) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get(editing ? "file_services.share_edit_title" : "file_services.share_new_title"),
            dialog => new FileServicesShareDialogView(vm, dialog, editing), new Size(620, 650));

    public static Task<bool> ConfirmDeleteAsync(AppContext context, ManagedWindow owner, string name) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("file_services.delete"), dialog => new FileServicesDeleteDialogView(name, dialog), new Size(440, 220));
}
