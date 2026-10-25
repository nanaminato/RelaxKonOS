using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using RelaxKonOS.Shell;
using RelaxKonOS.WindowManager;

internal static class DesktopShellSwitchChecks
{
    public static async Task RunAsync(ShellSettings settings, LocalizationService localization, IServiceProvider services)
    {
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        var vm = new DesktopShellViewModel(windows, new ApplicationManager(windows, services), settings,
            localization, DispatchProxy.Create<IAuthSession, UnusedPreviewServices>(),
            new SshDesktopSession(null!, new ShellSettings(null!)), () => { },
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null);
        var state = new ShellStateStore();
        state.Publish(vm);
        windows.Attach(new Canvas { Width = 1000, Height = 700 });
        var first = windows.Create(new WindowCreateOptions(new AppId("shell.switch.image"), "Image application",
            new Border(), IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/explorer.png"));
        if (!first.HasIconImage) throw new Exception("Switch regression requires a real image icon.");
        var second = windows.Create(new WindowCreateOptions(new AppId("shell.switch.glyph"), "Glyph application",
            new Border(), IconGlyph: "G"));
        var host = new Window { Width = 1000, Height = 700 };
        host.Show();
        LauncherDesktopShellBase? previous = null;
        try
        {
            for (var round = 0; round < 2; round++)
            foreach (var next in new LauncherDesktopShellBase[] { new WindowsLikeDesktopShell(), new MacosLikeDesktopShell(), new UbuntuLikeDesktopShell() })
            {
                var registry = new PreviewSurfaceRegistry(windows);
                await next.InitializeAsync(new ShellPresentationContext(state, null!, null!, registry, null!), CancellationToken.None);
                if (previous is not null) await previous.DeactivateAsync(CancellationToken.None);
                windows.Detach();
                host.Content = next.View;
                windows.Attach(registry.Surfaces!.WindowHost);
                windows.AttachFullScreenHost(registry.Surfaces.FullScreenWindowHost);
                await next.ActivateAsync(CancellationToken.None);
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (previous is not null) await previous.DisposeAsync();
                previous = next;
                foreach (var group in vm.TaskbarGroups)
                {
                    var button = next.View.GetVisualDescendants().OfType<Button>()
                        .First(control => ReferenceEquals(control.Command, vm.ToggleTaskbarGroupCommand) && ReferenceEquals(control.CommandParameter, group));
                    button.Command!.Execute(button.CommandParameter);
                    host.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                if (windows.Windows.Count != 2) throw new Exception("Shell switching lost a running window.");
                Console.WriteLine($"PASS: {next.Descriptor.Id}: switch round {round + 1} preserves image/glyph application windows and renders running-app buttons.");
            }
            // Opening another application while Ubuntu is already attached exercises live template creation.
            var added = windows.Create(new WindowCreateOptions(new AppId("shell.switch.added"), "Added application",
                new Border(), IconPath: "avares://RelaxKonOS.Client/Assets/AppIcons/terminal.png"));
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            windows.Close(added);
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Console.WriteLine("PASS: Ubuntu Dock adds and removes running image applications while attached.");
        }
        finally
        {
            foreach (var window in windows.Windows.ToArray()) windows.ForceClose(window);
            windows.Detach();
            host.Content = null;
            if (previous is not null) await previous.DisposeAsync();
            host.Close();
        }
    }
}
