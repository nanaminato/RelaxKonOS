using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.WindowManager;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class ServerCenterDialogChecks
{
    public static void Run()
    {
        foreach (var section in new[] { "details", "repair", "uninstall" })
        {
            var vm = new ServerCenterViewModel(null!, null!, null!, null!, null!, null!, null!,
                new LoginLocalizationService(new LocalLanguageStore()));
            var manager = new WindowManagerService();
            manager.Attach(new Canvas { Width = 1000, Height = 700 });
            var owner = manager.Create(new WindowCreateOptions(new("test.server-center"), "Owner", new TextBlock(),
                new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
            ModalDialog<bool>? handle = null;
            var result = manager.ShowDialogAsync<bool>(owner, section, dialog =>
            {
                handle = dialog;
                return new ServerCenterManagementDialog(vm, section, dialog);
            });
            vm.IsBusy = true;
            manager.Close(handle!.Window!);
            Check(!result.IsCompleted && manager.Windows.Count == 2, section + ": window close is blocked while busy.");
            vm.IsBusy = false;
            if (section == "details")
            {
                handle.Cancel();
                PumpUntil(() => manager.Windows.Count == 1);
                Check(result.IsCompleted, "Details close without a draft prompt.");
            }
            else
            {
                if (section == "repair") vm.RepairCertificateIdentities = "changed.example";
                else vm.DeleteServerData = !vm.DeleteServerData;
                handle.Cancel();
                Check(manager.Windows.Count == 3 && !result.IsCompleted, section + ": edited options require discard confirmation.");
                Confirmation(manager).NoCommand.Execute(null);
                PumpUntil(() => manager.Windows.Count == 2);
                Check(!result.IsCompleted, section + ": keeping changes returns to the management editor.");
                handle.Cancel();
                Confirmation(manager).YesCommand.Execute(null);
                PumpUntil(() => manager.Windows.Count == 1);
                Check(result.IsCompleted, section + ": discard closes both modal layers.");
            }
            manager.Close(owner);
        }
        Console.WriteLine("Server center dialog checks passed.");
    }
    private static ConfirmDialogViewModel Confirmation(WindowManagerService manager)
        => (ConfirmDialogViewModel)((Control)manager.Windows.Last().View.Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Modal cleanup did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
