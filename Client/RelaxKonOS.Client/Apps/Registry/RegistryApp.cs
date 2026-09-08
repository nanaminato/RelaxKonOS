using Avalonia;
using Avalonia.Controls;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.Registry;

/// <summary>Built-in registry editor for the current user's RelaxKonOS configuration hive.</summary>
public sealed class RegistryApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("remoteos.registry"), "Registry", "1.0.0", "⚙", "Browse supported RelaxKonOS configuration values", [AppPermissions.ServerRegistryRead, AppPermissions.ServerRegistryWrite], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRegistryClient)) as IRegistryClient;
        if (session?.State != AuthSessionState.Authenticated || client is null)
        {
            context.ShowWindow(LocalizedText.Get("application.remoteos.registry.display_name", "Registry"), new TextBlock { Text = LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."), Margin = new Thickness(24) }, new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new RegistryViewModel(client);
        var window = context.ShowWindow(LocalizedText.Get("application.remoteos.registry.display_name", "Registry"), new RegistryView { DataContext = viewModel }, new Rect(80, 60, 1120, 700), Manifest.IconGlyph);
        viewModel.ShowEditDialogAsync = async row =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("registry.dialog.edit", "Edit Registry Value"), dialog => new RegistryValueDialogView
            {
                DataContext = new RegistryValueDialogViewModel(row, client, dialog.Close, viewModel.ApplySaved),
            }, new Size(720, 520));
        };
        viewModel.ShowNewValueDialogAsync = async (scope, path) =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("registry.dialog.new", "New Registry Value"), dialog => new RegistryValueDialogView
            {
                DataContext = new RegistryValueDialogViewModel(scope, path, client, dialog.Close, viewModel.ApplySaved),
            }, new Size(720, 520));
        };
        viewModel.ShowNewKeyDialogAsync = async (scope, parentPath) =>
        {
            await context.ShowDialogAsync<bool>(window, "New Registry Key", dialog => new RegistryKeyDialogView
            {
                DataContext = new RegistryKeyDialogViewModel(scope, parentPath, client, dialog.Close, viewModel.ApplyCreatedKey),
            });
        };
        _ = viewModel.RefreshAsync();
    }
}
