using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.Dialogs;

/// <summary>
/// Applies the shared discard-confirmation policy to a top-level application window. A modal
/// editor is guarded by <see cref="DraftDialogGuard"/> through its dialog handle; a regular
/// application window (Notepad, Code Editor, Terminal, …) has no such handle, so the guard is
/// attached to the managed window itself. Title bar, Alt+F4, taskbar and window overview all
/// funnel through <see cref="IWindowManager.Close"/>, so every user close gesture asks before
/// discarding work. Teardown paths call <see cref="IWindowManager.ForceClose"/> and bypass it.
/// </summary>
public static class DraftWindowCloseGuard
{
    /// <summary>Guards a window whose unsaved state is changes to be discarded.</summary>
    public static void Attach(IWindowManager manager, ManagedWindow window, Func<bool> isBusy, Func<bool> hasChanges)
        => Attach(manager, window, isBusy, hasChanges,
            LocalizedText.Get("common.discard_title"),
            LocalizedText.Get("common.discard_message"),
            LocalizedText.Get("common.discard"));

    /// <summary>
    /// Guards a window where closing loses state that is not "changes to discard" — for example a
    /// terminal session whose server process is killed when the window closes.
    /// </summary>
    public static void Attach(
        IWindowManager manager,
        ManagedWindow window,
        Func<bool> isBusy,
        Func<bool> hasChanges,
        string title,
        string message,
        string confirmLabel)
    {
        window.CanCloseAsync = async () =>
        {
            if (isBusy()) return false;
            if (!hasChanges()) return true;
            var confirmed = await manager.ShowDialogAsync<bool>(window, title,
                confirmation => new ConfirmDialogView
                {
                    DataContext = new ConfirmDialogViewModel(message, result => confirmation.Close(result), confirmLabel)
                });
            return confirmed && !isBusy();
        };
    }
}
