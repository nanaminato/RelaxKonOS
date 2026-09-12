using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.FileServices.Views;

/// <summary>Opens the AXAML dialogs with their owner, title and size.</summary>
internal static class FileServicesDialogs
{
    public static Task<string?> RequestPasswordAsync(AppContext context, ManagedWindow owner, string title, string? message = null) =>
        context.ShowDialogAsync<string?>(owner, title, dialog => new FileServicesPasswordDialogView(dialog, message), new Size(460, message is null ? 160 : 230));

    public static async Task ShowShareEditorAsync(AppContext context, ManagedWindow owner, FileServicesViewModel vm, bool editing)
    {
        var bounds = owner.Info.Bounds;
        var size = new Size(Math.Min(720, Math.Max(480, bounds.Width - 48)), Math.Min(700, Math.Max(420, bounds.Height - 56)));
        try
        {
            await context.ShowDialogAsync<bool>(owner, LocalizedText.Get(editing ? "file_services.share_edit_title" : "file_services.share_new_title"),
                dialog =>
                {
                    vm.ConfirmSharePathAsync = path => dialog.ShowDialogAsync<bool>(LocalizedText.Get("file_services.path_warning_title"),
                        warning => new FileServicesPathWarningDialogView(path, warning));
                    return new FileServicesShareDialogView(vm, dialog, editing);
                }, size);
        }
        finally { vm.ConfirmSharePathAsync = null; }
    }

    public static Task<bool> ConfirmDeleteAsync(AppContext context, ManagedWindow owner, string name) =>
        context.ShowDialogAsync<bool>(owner, LocalizedText.Get("file_services.delete"), dialog => new FileServicesDeleteDialogView(name, dialog), new Size(440, 220));
}
