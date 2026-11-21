using RelaxKonOS.App.Registry;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client.Services.AppPermissions;
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
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.registry"), "Registry", "1.0.0", "⚙", "Browse supported RelaxKonOS configuration values", [AppPermissions.ServerRegistryRead, AppPermissions.ServerRegistryWrite], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var sdk = context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        if (session?.State != AuthSessionState.Authenticated || !sdk.HasAuthenticatedServer)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.registry.display_name", "Registry"), new TextBlock { Text = LocalizedText.Get("registry.error.sign_in", "Sign in to browse the configuration registry."), Margin = new Thickness(24) }, new Rect(200, 160, 460, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var client = sdk.Registry;
        var view = new RegistryView();
        var window = sdk.Windows.ShowWindow(LocalizedText.Get("application.relaxkonos.registry.display_name", "Registry"), view, new Rect(80, 60, 1120, 700), Manifest.IconGlyph);
        var viewModel = new RegistryViewModel(client, sdk.RegistryAppearance,
            sdk.Permissions, window.Closed, sdk.SystemLanguage);
        view.DataContext = viewModel;
        viewModel.ShowEditDialogAsync = async row =>
        {
            if (!await viewModel.EnsurePermissionAsync(write: true) || !sdk.ServerConnection.IsCurrent) return;
            if (row.Source.Scope == RelaxKonOS.Protocol.Registry.RegistryScope.Device && row.Source.Address.ScopeId != session.CurrentDevice?.Id)
                throw new InvalidOperationException("The client device changed. Reopen the registry editor.");
            await window.ShowDialogAsync<bool>(LocalizedText.Get("registry.dialog.edit", "Edit Registry Value"), dialog => new RegistryValueDialogView
            {
                AppearancePreferences = sdk.RegistryAppearance,
                DataContext = new RegistryValueDialogViewModel(row, row.Source.Scope == RelaxKonOS.Protocol.Registry.RegistryScope.Server ? client.ForServer(row.Source.Address.ScopeId) : client.ForUser(row.Source.Address.OwnerUserId!.Value), dialog.Close, entry => viewModel.ApplySaved(entry)),
            }, row.Source.ValueType == RelaxKonOS.Protocol.Registry.RegistryValueType.Number ? new Size(600, 320) : new Size(720, 520));
        };
        viewModel.ShowRenameDialogAsync = async row =>
        {
            if (!await viewModel.EnsurePermissionAsync(write: true) || !sdk.ServerConnection.IsCurrent) return;
            await window.ShowDialogAsync<bool>(LocalizedText.Get("registry.dialog.rename", "Rename Registry Value"), dialog => new RegistryRenameDialogView
            {
                DataContext = new RegistryRenameDialogViewModel(row, row.Source.Scope == RelaxKonOS.Protocol.Registry.RegistryScope.Server ? client.ForServer(row.Source.Address.ScopeId) : client.ForUser(row.Source.Address.OwnerUserId!.Value), dialog.Close, viewModel.ApplyRenamed),
            }, new Size(560, 320));
        };
        viewModel.ShowNewValueDialogAsync = async (scope, path) =>
        {
            if (viewModel.SelectedKey?.TargetUserId is not { } owner
                || !await viewModel.EnsurePermissionAsync(write: true) || !sdk.ServerConnection.IsCurrent) return;
            var ownedClient = client.ForUser(owner);
            await window.ShowDialogAsync<bool>(LocalizedText.Get("registry.dialog.new", "New Registry Value"), dialog => new RegistryValueDialogView
            {
                AppearancePreferences = sdk.RegistryAppearance,
                DataContext = new RegistryValueDialogViewModel(scope, path, ownedClient, dialog.Close, entry => viewModel.ApplySaved(entry), sdk.SystemLanguage),
            }, new Size(720, 520));
        };
        viewModel.ShowNewKeyDialogAsync = async (scope, parentPath) =>
        {
            if (viewModel.SelectedKey?.TargetUserId is not { } owner
                || !await viewModel.EnsurePermissionAsync(write: true) || !sdk.ServerConnection.IsCurrent) return;
            var ownedClient = client.ForUser(owner);
            await window.ShowDialogAsync<bool>(LocalizedText.Get("registry.menu.new_key"), dialog => new RegistryKeyDialogView
            {
                DataContext = new RegistryKeyDialogViewModel(scope, parentPath, ownedClient, dialog.Close, viewModel.ApplyCreatedKey, sdk.SystemLanguage),
            }, new Size(460, 300));
        };
        _ = viewModel.RefreshAsync();
    }
}
