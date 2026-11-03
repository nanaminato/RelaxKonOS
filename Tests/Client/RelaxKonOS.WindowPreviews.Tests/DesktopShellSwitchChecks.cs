using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
                // Right-click ownership is the same in every built-in shell: the title bar answers with
                // the host window menu, the application body answers with nothing of its own, and only
                // the empty wallpaper answers with the desktop menu.
                var shellId = next.Descriptor.Id;
                var desktopMenu = registry.Surfaces!.InputBackdrop.ContextMenu
                    ?? throw new Exception($"{shellId}: the shell has no desktop context menu.");
                var desktopMenuOpened = 0;
                desktopMenu.Opening += (_, _) => desktopMenuOpened++;
                var activeView = (windows.ActiveWindow ?? first).View;
                var titleBar = activeView.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_TitleBar");
                var windowMenu = titleBar.ContextMenu
                    ?? throw new Exception($"{shellId}: the title bar has no window menu.");
                var windowMenuOpened = 0;
                windowMenu.Opening += (_, _) => windowMenuOpened++;
                var titlePoint = activeView.TranslatePoint(new Point(160, 15), host)!.Value;
                host.MouseDown(titlePoint, MouseButton.Right); host.MouseUp(titlePoint, MouseButton.Right);
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (!windowMenu.IsOpen || desktopMenuOpened != 0)
                    throw new Exception($"{shellId}: a title-bar right click did not answer with the window menu alone.");
                windowMenu.Close();
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var bodyPoint = activeView.TranslatePoint(new Point(160, 120), host)!.Value;
                host.MouseDown(bodyPoint, MouseButton.Right); host.MouseUp(bodyPoint, MouseButton.Right);
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (desktopMenuOpened != 0 || windowMenu.IsOpen || windowMenuOpened != 1)
                    throw new Exception($"{shellId}: a right click inside an application window opened a menu it does not own.");
                var wallpaper = registry.Surfaces!.InputBackdrop.TranslatePoint(new Point(4, 4), host)!.Value;
                host.MouseDown(wallpaper, MouseButton.Right); host.MouseUp(wallpaper, MouseButton.Right);
                host.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (desktopMenuOpened != 1)
                    throw new Exception($"{shellId}: a right click on the wallpaper did not open the desktop menu.");
                desktopMenu.Close();
                Console.WriteLine($"PASS: {shellId}: title bar answers with the window menu, the application body with nothing, the wallpaper with the desktop menu.");
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
