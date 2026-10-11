using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.Dialogs;

/// <summary>Uses the same cancellation policy for buttons and managed window gestures.</summary>
public static class DraftDialogGuard
{
    public static void Attach<TResult>(ModalDialog<TResult> dialog, Func<bool> isBusy, Func<bool> hasChanges)
    {
        dialog.CanCancelAsync = async () =>
        {
            if (isBusy()) return false;
            if (!hasChanges()) return true;
            var discard = await dialog.ShowDialogAsync<bool>(LocalizedText.Get("common.discard_title"), confirmation =>
                new ConfirmDialogView
                {
                    DataContext = new ConfirmDialogViewModel(LocalizedText.Get("common.discard_message"),
                        result => confirmation.Close(result), LocalizedText.Get("common.discard"))
                });
            return discard && !isBusy();
        };
    }
}
