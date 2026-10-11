using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class ModalCancellationChecks
{
    public static void Run()
    {
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(
            new RelaxKonOS.Core.Applications.AppId("test.modal"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 520)));
        ModalDialog<bool>? dialog = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Draft", handle =>
        {
            dialog = handle;
            return new TextBox { Text = "unsaved" };
        });
        dialog!.CanCancelAsync = () => Task.FromResult(false);
        manager.Close(dialog.Window!);
        Check(!result.IsCompleted && manager.Windows.Count == 2, "Denied chrome close retains the draft window.");
        dialog.Cancel();
        Check(!result.IsCompleted, "Denied cancel button retains the draft.");

        var confirmation = new TaskCompletionSource<bool>();
        var calls = 0;
        dialog.CanCancelAsync = () => { calls++; return confirmation.Task; };
        var cancel = dialog.CancelAsync();
        dialog.Cancel();
        manager.Close(dialog.Window!);
        Check(calls == 1, "Repeated gestures share one pending cancellation check.");
        confirmation.SetResult(true);
        Dispatcher.UIThread.RunJobs();
        Check(cancel.IsCompletedSuccessfully && result.IsCompleted, "Confirmed cancellation completes the dialog.");
        // Modal cleanup is posted from a continuation on the thread pool.
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 1; }, 2000);
        Check(manager.Windows.Count == 1, "Confirmed cancellation removes the window and blocker.");

        var draft = manager.ShowDialogAsync<bool>(owner, "Dirty", handle =>
        {
            dialog = handle;
            RelaxKonOS.Client.Services.Dialogs.DraftDialogGuard.Attach(handle, () => false, () => true);
            return new TextBox { Text = "keep me" };
        });
        dialog!.Cancel();
        Check(manager.Windows.Count == 3 && !draft.IsCompleted, "Dirty cancellation opens an owned confirmation.");
        var confirmationVm = (RelaxKonOS.Client.Apps.Explorer.Dialogs.ConfirmDialogViewModel)
            ((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirmationVm.NoCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 2; }, 2000);
        Check(!draft.IsCompleted && manager.Windows.Count == 2, "Keeping changes returns to the editor.");
        dialog.Cancel();
        confirmationVm = (RelaxKonOS.Client.Apps.Explorer.Dialogs.ConfirmDialogViewModel)
            ((Control)manager.Windows.Last().View.Content!).DataContext!;
        confirmationVm.YesCommand.Execute(null);
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 1; }, 2000);
        Check(draft.IsCompleted && manager.Windows.Count == 1, "Discarding changes closes the editor and both modal layers.");

        manager.ShowDialogAsync<bool>(owner, "Busy", handle =>
        {
            dialog = handle;
            handle.CanCancelAsync = () => Task.FromResult(false);
            return new TextBlock();
        });
        manager.Close(owner);
        Check(dialog!.Result.IsCompleted, "Owner teardown cancels even a busy dialog without prompting.");
        SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return manager.Windows.Count == 0; }, 2000);
        Check(manager.Windows.Count == 0, "Owner teardown releases the modal chain.");
        Console.WriteLine("Modal cancellation checks passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
