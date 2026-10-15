using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media.Imaging;
using System.Reflection;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;
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
        foreach (var section in new[] { "components", "settings", "details", "repair", "uninstall" })
        {
            var vm = new ServerCenterViewModel(null!, null!, null!, null!, null!, null!, null!,
                new LoginLocalizationService(new LocalLanguageStore()));
            var target = ServerHostTargetRules.Create("192.168.1.7", 22, "admin", null, DateTimeOffset.UtcNow);
            vm.SelectedHost = target;
            var snapshot = new ServerHostSnapshotDto("rki-26691ddcd2e74b6080ee10052cddbe11", true, ServerInstallMode.LinuxSystem,
                "0.1.0-dev20261014-05", "0.1.0-dev20261012-03", "/opt/relaxkonos", "/var/lib/relaxkonos", "http://127.0.0.1:5201", true, null, [], DateTimeOffset.UtcNow, GuardianEnabled: false);
            var snapshots = (Dictionary<string, ServerHostSnapshotDto>)typeof(ServerCenterViewModel).GetField("_installationSnapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            snapshots[target.HostId] = snapshot;
            Check(vm.PrepareConfiguration(), "Management preview has verified configuration facts.");
            Check(vm.CurrentConfigurationDetails.All(row => row.Value is not ("True" or "False")), "Configuration status uses translated values instead of raw booleans.");
            var manager = new WindowManagerService();
            var canvas = new Canvas { Width = 1000, Height = 740 };
            var preview = new Window { Width = 1000, Height = 740, Content = canvas };
            preview.Show();
            manager.Attach(canvas);
            var owner = manager.Create(new WindowCreateOptions(new("test.server-center"), "Owner", new TextBlock(),
                new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
            ModalDialog<bool>? handle = null;
            var result = manager.ShowDialogAsync<bool>(owner, section, dialog =>
            {
                handle = dialog;
                return new ServerCenterManagementDialog(vm, section, dialog);
            }, new RelaxKonOS.Core.Primitives.Size(640, 600));
            Dispatcher.UIThread.RunJobs();
            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "server-center");
            Directory.CreateDirectory(output);
            var page = (Control)handle!.Window!.View.Content!;
            using (var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(page.Bounds.Width), (int)Math.Ceiling(page.Bounds.Height))))
            {
                bitmap.Render(page);
                bitmap.Save(Path.Combine(output, section + ".png"), PngBitmapEncoderOptions.Default);
            }
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
                if (section is "components" or "settings") vm.ConfigurationGuardian = !vm.ConfigurationGuardian;
                else if (section == "repair") vm.RepairCertificateIdentities = "changed.example";
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
            preview.Close();
        }
        foreach (var approve in new[] { false, true })
        {
            var manager = new WindowManagerService();
            var canvas = new Canvas { Width = 1000, Height = 740 };
            var preview = new Window { Width = 1000, Height = 740, Content = canvas };
            preview.Show();
            manager.Attach(canvas);
            var owner = manager.Create(new WindowCreateOptions(new("test.rollback"), "Owner", new TextBlock(),
                new RelaxKonOS.Core.Primitives.Rect(0, 0, 800, 600)));
            ServerRollbackConfirmationView? view = null;
            var result = manager.ShowDialogAsync<bool>(owner, "Restore previous version", dialog =>
                view = new ServerRollbackConfirmationView(new("192.168.1.7:22", "0.1.0-dev20261014-05", "0.1.0-dev20261012-03"), dialog),
                new RelaxKonOS.Core.Primitives.Size(560, 420));
            Dispatcher.UIThread.RunJobs();
            var values = view!.GetVisualDescendants().OfType<SelectableTextBlock>().Select(text => text.Text).ToArray();
            Check(values.Contains("0.1.0-dev20261014-05") && values.Contains("0.1.0-dev20261012-03"), "Rollback dialog visibly renders both exact versions.");
            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "server-center");
            using (var bitmap = new RenderTargetBitmap(new PixelSize(560, 420)))
            {
                bitmap.Render(view!);
                bitmap.Save(Path.Combine(output, "rollback.png"), PngBitmapEncoderOptions.Default);
            }
            var buttons = view!.GetVisualDescendants().OfType<Button>().ToArray();
            buttons[approve ? 1 : 0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => result.IsCompleted);
            Check(result.Result == approve, "Rollback dialog returns only the explicit user's choice.");
            manager.Close(owner);
            preview.Close();
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
