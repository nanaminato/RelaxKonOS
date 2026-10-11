using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Views.Login;

internal static class LocalInstallationCloseChecks
{
    public static void Run()
    {
        var localization = new LoginLocalizationService(new LocalLanguageStore());
        var center = new ServerCenterViewModel(null!, null!, null!, null!, null!, null!, null!, localization);
        // Any accidental installation would fail instead of invoking a real launcher.
        var installer = new LocalWindowsServerInstaller(null!, null!, null!);
        using var provider = new ServiceCollection().AddSingleton(localization).AddSingleton(center).AddSingleton(installer).BuildServiceProvider();
        var property = typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!;
        var previous = property.GetValue(null);
        property.SetValue(null, provider);
        try
        {
            var window = new LocalServerInstallationWindow();
            window.Show();
            OpenWizard(window, localization);
            var wizard = (ServerInstallationWizardViewModel)((Control)window.Content!).DataContext!;
            Check(!wizard.HasDraftChanges, "Local mode selection establishes the initial draft baseline.");
            wizard.CertificatePassword = "temporary";
            var cancel = wizard.CancelCommand.ExecuteAsync(null);
            Confirmation(window).NoCommand.Execute(null);
            PumpUntil(() => cancel.IsCompleted);
            Check(window.Content is ServerInstallationWizardView && wizard.CertificatePassword == "temporary",
                "Local cancel confirmation preserves the original wizard and password.");
            cancel = wizard.CancelCommand.ExecuteAsync(null);
            Confirmation(window).YesCommand.Execute(null);
            PumpUntil(() => cancel.IsCompleted);
            Check(window.Content is not ServerInstallationWizardView && wizard.CertificatePassword.Length == 0,
                "Confirmed local discard returns to management and clears the password.");
            OpenWizard(window, localization);
            var fresh = (ServerInstallationWizardViewModel)((Control)window.Content!).DataContext!;
            Check(!ReferenceEquals(fresh, wizard) && !fresh.HasDraftChanges && fresh.CertificatePassword.Length == 0,
                "Reopening local installation creates a fresh draft.");
            fresh.ServerPortText = "invalid";
            fresh.IsBusy = true;
            window.Close();
            Check(window.IsVisible && !window.OwnedWindows.Any(), "Busy local installation blocks native title-bar close.");
            fresh.IsBusy = false;
            window.Close();
            Confirmation(window).NoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Check(window.IsVisible && fresh.ServerPortText == "invalid", "Canceling native close retains invalid input.");
            fresh.CertificatePassword = "temporary";
            var veto = true;
            var closeAttempts = 0;
            window.Closing += (_, args) => { closeAttempts++; if (veto) args.Cancel = true; };
            window.Close();
            Confirmation(window).YesCommand.Execute(null);
            PumpUntil(() => closeAttempts >= 2);
            Check(window.IsVisible && fresh.CertificatePassword == "temporary",
                "Another close veto retains secrets when the native window remains open.");
            veto = false;
            window.Close();
            Check(window.OwnedWindows.Count == 1, "A previous approved close cannot bypass later draft protection.");
            Confirmation(window).YesCommand.Execute(null);
            PumpUntil(() => !window.IsVisible);
            Check(fresh.CertificatePassword.Length == 0, "Confirmed native close clears secrets and closes the owner.");
        }
        finally { property.SetValue(null, previous); }
        Console.WriteLine("Local installation close checks passed.");
    }
    private static void OpenWizard(LocalServerInstallationWindow window, LoginLocalizationService localization)
    {
        Dispatcher.UIThread.RunJobs();
        var label = localization.Get("login.local_manage_install", "Install / update");
        window.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == label)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
    private static ConfirmDialogViewModel Confirmation(Window owner)
        => (ConfirmDialogViewModel)((Control)owner.OwnedWindows.Single().Content!).DataContext!;
    private static void PumpUntil(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, 2000))
            throw new Exception("Native draft transition did not complete.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
