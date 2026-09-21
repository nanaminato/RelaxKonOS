using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Foundation.DependencyInjection;
using RelaxKonOS.Client.Mobile.ViewModels;
using RelaxKonOS.Client.Mobile.Views;

namespace RelaxKonOS.Client.Mobile;

/// <summary>Platform-neutral Mobile Shell. Android and future iOS hosts only supply lifecycle and native services.</summary>
public sealed class MobileApp : Application
{
    public IServiceProvider Services { get; private set; } = default!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddRelaxKonOSFoundation();
        services.AddSingleton<MobileShellViewModel>();
        Services = services.BuildServiceProvider();

        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new MobileShellView { DataContext = Services.GetRequiredService<MobileShellViewModel>() };

        base.OnFrameworkInitializationCompleted();
    }
}
