using Avalonia.Controls;
using Avalonia.Layout;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.FileServices;

/// <summary>Built-in SMB administration entry point. Unsupported protocols deliberately have no card or feature flag.</summary>
public sealed class FileServicesApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.file-services"), "File Services", "1.0.0", "🗄", "Manage the host SMB control plane",
        [AppPermissions.ServerFileServicesRead, AppPermissions.ServerFileServicesManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteFileServicesClient)) as IRemoteFileServicesClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated) { context.ShowWindow("File Services", new TextBlock { Text = "Sign in to manage SMB file services.", Margin = new Avalonia.Thickness(20) }, new Rect(180, 160, 440, 160), Manifest.IconGlyph, false, false, false); return; }
        var vm = new FileServicesViewModel(client, context.Permissions);
        var root = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; status.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.StatusText))); root.Children.Add(status);
        var connection = new TextBlock(); connection.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(vm.ConnectionText))); root.Children.Add(connection);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(new Button { Content = "Refresh", Command = vm.RefreshCommand }); actions.Children.Add(new Button { Content = "Install", Command = vm.InstallCommand }); actions.Children.Add(new Button { Content = "Start", Command = vm.StartServiceCommand }); actions.Children.Add(new Button { Content = "Stop", Command = vm.StopCommand }); actions.Children.Add(new Button { Content = "Restart", Command = vm.RestartCommand }); root.Children.Add(actions);
        root.Children.Add(new TextBlock { Text = "Managed SMB shares" }); root.Children.Add(new DataGrid { ItemsSource = vm.Shares, AutoGenerateColumns = true, IsReadOnly = true, Height = 300 });
        context.ShowWindow("File Services", root, new Rect(90, 80, 860, 540), Manifest.IconGlyph); _ = vm.StartAsync();
    }
}
