using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>Server inventory available inside an SSH desktop.</summary>
public sealed class ServerCenterApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.server-center"),
        DisplayName: "Install or manage server",
        Version: "1.0.0",
        IconGlyph: "🖧",
        Description: "SSH hosts and RelaxKonOS installation",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        if (!context.Services.GetRequiredService<SshDesktopSession>().IsConnected) return;
        var viewModel = context.Services.GetRequiredService<ServerCenterViewModel>();
        context.ShowWindow(viewModel.Title, new ServerCenterWorkspace { DataContext = viewModel },
            new Rect(70, 50, 1120, 760), "🖧");
        _ = viewModel.LoadAsync();
    }
}
