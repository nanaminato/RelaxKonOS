using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Client.Views.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>Opens the same server inventory from either desktop session.</summary>
public sealed class ServerCenterApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.server-center"),
        DisplayName: "Server centre",
        Version: "1.0.0",
        IconGlyph: "🖧",
        Description: "SSH hosts and RelaxKonOS installation");

    public override void Activate(AppContext context) => _ = OpenAsync(context);

    private static async Task OpenAsync(AppContext context)
    {
        var viewModel = context.Services.GetRequiredService<ServerCenterViewModel>();
        await viewModel.LoadAsync();
        new ServerCenterWindow { DataContext = viewModel }.Show();
    }
}
