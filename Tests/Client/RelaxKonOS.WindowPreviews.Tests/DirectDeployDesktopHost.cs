using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client;
using RelaxKonOS.Client.Services.Developer;
using RelaxKonOS.Client.ViewModels.Shell;
using RelaxKonOS.Client.Views;
using RelaxKonOS.WindowManager;

/// <summary>Runs the actual desktop shell with no remote session for local package lifecycle acceptance.</summary>
internal static class DirectDeployDesktopHost
{
    public static void Run(string[] args)
    {
        if (!DeveloperProfile.IsDebug) throw new InvalidOperationException("Use the Debug build of the desktop acceptance host.");
        using var profile = DeveloperProfile.Acquire();
        AppBuilder.Configure<App>().UsePlatformDetect()
            .AfterSetup(_ => Dispatcher.UIThread.Post(OpenDesktopAsync))
            .StartWithClassicDesktopLifetime(args);
    }

    private static async void OpenDesktopAsync()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            while (App.Services is null || lifetime.MainWindow is null) await Task.Delay(50, deadline.Token);
            var login = lifetime.MainWindow;
            var services = App.Services;
            var shell = new MainWindow { DataContext = services.GetRequiredService<DesktopShellViewModel>() };
            lifetime.MainWindow = shell;
            shell.Closed += (_, _) => lifetime.Shutdown();
            shell.Show();
            login.Hide();
            var manager = services.GetRequiredService<IWindowManager>();
            while (manager.HostBounds.Width <= 0 || manager.HostBounds.Height <= 0) await Task.Delay(50, deadline.Token);
            Console.WriteLine("Direct Deploy desktop ready.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            lifetime.Shutdown(1);
        }
    }
}
