using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class InstallationWizardDialogChecks
{
    public static void Run()
    {
        var center = new ServerCenterViewModel(null!, null!, null!, null!, null!, null!, null!,
            new LoginLocalizationService(new LocalLanguageStore()));
        var manager = new WindowManagerService();
        manager.Attach(new Canvas { Width = 1000, Height = 700 });
        var owner = manager.Create(new WindowCreateOptions(new("test.installation"), "Owner", new TextBlock(),
            new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
        ModalDialog<bool>? handle = null;
        ServerInstallationWizardViewModel? wizard = null;
        var result = manager.ShowDialogAsync<bool>(owner, "Install", dialog =>
        {
            handle = dialog;
            wizard = new(center, () => dialog.Close(true),
                async () => { await dialog.CancelAsync(); return dialog.Result.IsCompleted; },
                () => Task.FromResult<string?>(null), () => Task.CompletedTask, _ => Task.FromResult(true));
            return new ServerInstallationWizardView(wizard, dialog);
        });
        Check(!wizard!.HasDraftChanges, "Default installation options are not an edited draft.");
        wizard.ServerPortText = "invalid";
        Check(wizard.HasDraftChanges, "Invalid input still participates in the draft comparison.");
        wizard.SudoPassword = "temporary";
        wizard.CertificatePassword = "certificate";
        var cancel = wizard.CancelCommand.ExecuteAsync(null);
        Confirmation(manager).NoCommand.Execute(null);
        PumpUntil(() => cancel.IsCompleted && manager.Windows.Count == 2);
        Check(!result.IsCompleted && wizard.SudoPassword == "temporary" && wizard.CertificatePassword == "certificate",
            "Canceling discard retains the installation draft and passwords.");
        wizard.IsBusy = true;
        manager.Close(handle!.Window!);
        wizard.CancelCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(!result.IsCompleted && manager.Windows.Count == 2, "Busy installation rejects both page cancellation and window close.");
        wizard.IsBusy = false;
        cancel = wizard.CancelCommand.ExecuteAsync(null);
        Confirmation(manager).YesCommand.Execute(null);
        PumpUntil(() => cancel.IsCompleted && manager.Windows.Count == 1 && wizard.CertificatePassword.Length == 0);
        Check(result.IsCompleted && !result.Result && wizard.SudoPassword.Length == 0,
            "Confirmed discard returns cancellation and clears passwords after closing.");
        manager.Close(owner);
        Console.WriteLine("Installation wizard dialog checks passed.");
    }
    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Wizard modal transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
