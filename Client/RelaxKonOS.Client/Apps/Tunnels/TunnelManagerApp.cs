using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.Installations;
using Avalonia.Input.Platform;
using RelaxKonOS.Client.Apps.Tunnels.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Tunnels;

/// <summary>FRP administration workspace with separate editors and independently refreshable log windows.</summary>
public sealed class TunnelManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.tunnels"), "Tunnel Manager", "1.1.0", "↔", "Manage FRP tunnel desired state and runtime status", [AppPermissions.ServerTunnelsRead, AppPermissions.ServerTunnelsManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);
    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteTunnelClient)) as IRemoteTunnelClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("tunnels.title"), new TunnelLoginRequiredView(), new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false); return;
        }
        var canManage = context.Permissions.IsGranted(AppPermissions.ServerTunnelsManage);
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var vm = new TunnelManagerViewModel(client, canManage);
        vm.Installation = InstallationPanel.Create(context, InstallationServiceId.Frp, "relaxkonos.tunnels", vm.RefreshAfterChildAsync); var window = context.ShowWindow(LocalizedText.Get("tunnels.title"), InstallationPanel.Wrap(new TunnelManagerView { DataContext = vm }, vm.Installation), new Rect(90, 65, 1040, 680), Manifest.IconGlyph);
        async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window, title, dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); }, confirmLabel),
            });
            return confirmed;
        }
        vm.RequestConfirmationAsync = (title, message) => ConfirmAsync(title, message, title);
        vm.RequestServerRuntimePackageAsync = async () =>
        {
            if (files is null) return null;
            return await context.ShowDialogAsync<string?>(window, LocalizedText.Get("tunnels.runtime.select_server_package"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [new ExplorerFileFilter(LocalizedText.Get("tunnels.runtime.package_filter"), ["*.zip", "*.tar.gz", "*.tgz"])]),
                    paths => dialog.Close(paths.FirstOrDefault()))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, new Size(720, 520));
        };
        vm.ShowRuntimeDownloadUrlAsync = url => ShowDownloadUrlAsync(LocalizedText.Get("tunnels.runtime_download_title"), url);
        vm.ShowManagedFrpsConfigurationAsync = async () =>
        {
            await vm.LoadManagedFrpsForEditingAsync();
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get("tunnels.frps.configuration"), dialog =>
                new TunnelManagedFrpsConfigurationView { DataContext = vm, CloseAction = dialog.Cancel }, new Size(720, 690));
        };
        vm.ShowManagedFrpsDiagnosticsAsync = async () =>
        {
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get("tunnels.frps.diagnostics"), dialog =>
                new TunnelManagedFrpsDiagnosticsView { DataContext = vm, CloseAction = dialog.Cancel }, new Size(720, 600));
        };
        vm.OpenProfileEditorAsync = async profile =>
        {
            var editableProfile = profile is null ? null : await client.GetProfileAsync(profile.Id);
            if (profile is not null && editableProfile is null) return;
            var editor = new TunnelProfileEditorViewModel(client, editableProfile, null)
            {
                SavedAsync = vm.RefreshAfterChildAsync,
                RequestDeletionConfirmationAsync = () => ConfirmAsync(LocalizedText.Get("common.delete"), LocalizedText.Get("tunnels.server.delete_confirmation"), LocalizedText.Get("common.delete")),
            };
            await context.ShowDialogAsync<bool?>(window,
                LocalizedText.Get(profile is null ? "tunnels.server.new_title" : "tunnels.server.edit_title"), dialog =>
                {
                    editor.CloseAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return new TunnelProfileEditorView { DataContext = editor };
                }, new Size(620, 540));
        };
        vm.OpenTunnelEditorAsync = async tunnel =>
        {
            var editor = new TunnelDefinitionEditorViewModel(client, vm.Profiles, tunnel)
            {
                SavedAsync = vm.RefreshAfterChildAsync,
                RequestDeletionConfirmationAsync = () => ConfirmAsync(LocalizedText.Get("common.delete"), LocalizedText.Get("tunnels.tunnel.delete_confirmation"), LocalizedText.Get("common.delete")),
            };
            await context.ShowDialogAsync<bool?>(window,
                LocalizedText.Get(tunnel is null ? "tunnels.tunnel.new_title" : "tunnels.tunnel.edit_title"), dialog =>
                {
                    editor.CloseAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    return new TunnelDefinitionEditorView { DataContext = editor };
                }, new Size(650, 520));
        };
        vm.OpenLogsWindowAsync = profile =>
        {
            var logViewModel = new TunnelLogViewModel(client, profile);
            var logWindow = context.ShowWindow(LocalizedText.Format("tunnels.logs.title_format", profile.Name),
                new TunnelLogWindowView { DataContext = logViewModel }, new Rect(270, 175, 720, 510), Manifest.IconGlyph);
            EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? logClosed = null;
            logClosed = (_, item) =>
            {
                if (!ReferenceEquals(item, logWindow)) return;
                context.WindowManager.WindowClosed -= logClosed;
                logViewModel.Dispose();
            };
            context.WindowManager.WindowClosed += logClosed;
            _ = logViewModel.StartAsync();
            return Task.CompletedTask;
        };
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, item) => { if (!ReferenceEquals(item, window)) return; context.WindowManager.WindowClosed -= closed; vm.Dispose(); };
        context.WindowManager.WindowClosed += closed; _ = vm.StartAsync();

        Task ShowDownloadUrlAsync(string title, string url) => context.ShowDialogAsync<bool?>(window, title, dialog => new DownloadUrlDialogView
        {
            DataContext = new DownloadUrlDialogViewModel(url, CopyToClipboardAsync, () => dialog.Close(true)),
        }, new Size(660, 210));

        async Task CopyToClipboardAsync(string value)
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            if (topLevel?.Clipboard is not null) await topLevel.Clipboard.SetTextAsync(value);
        }
    }
}
