using System;
using Avalonia;
using RelaxKonOS.Client.Services.ServerCenter;

namespace RelaxKonOS.Client.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (LocalWindowsDeploymentBroker.IsBrokerInvocation(args))
        {
            Environment.ExitCode = OperatingSystem.IsWindows()
                ? LocalWindowsDeploymentBroker.RunAsync(args).GetAwaiter().GetResult() : 2;
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
