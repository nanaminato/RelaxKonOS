using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.App.WebServers.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.App.WebServers;

public sealed class WebServerPackageApp : IExternalRemoteApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.webservers"),
        "Web Server Manager", "1.0.0", "🌐", "Manage web servers on the RelaxKonOS Server",
        [AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage, AppPermissions.ServerComponentsRead,
            AppPermissions.ServerCertificatesRead, AppPermissions.ServerFilesRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.WebServer]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = Text("webservers.title");
        if (!context.HasAuthenticatedServer)
        {
            context.Windows.ShowWindow(title, new TextBlock
            {
                Text = Text("webservers.login_required"),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(24),
            }, new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var connection = context.ServerConnection;
        var model = new WebServerManagerViewModel(context.WebServers, context.Certificates, connection,
            connection.Platform, context.Permissions, new CapabilityAuthorization(), context.SystemLanguage);
        IExternalAppWindowHandle owner = null!;
        var workspace = WebServerManagerWorkspace.Create(model,
            () => context.ComponentDependencies?.CreateSettingsView(owner) ?? new ContentControl());
        owner = context.Windows.ShowWindow(title, workspace, new Rect(70, 55, 1080, 680), Manifest.IconGlyph);
        workspace.AttachWindowHeader(owner.Window);
        model.RequestIntegrationConfirmationAsync = () => owner.ConfirmAsync(Text("webservers.integration.confirmation.title"),
            Text("webservers.integration.confirmation.message"), Text("webservers.integration.confirmation.confirm"));
        model.OpenFileBrowserAtPathAsync = path =>
        {
            if (!context.Activations.Activate(RelaxKonOSActivationUris.ExplorerPath(path)).IsAccepted)
                model.SiteStatusText = Text("webservers.site.file_browser_failed");
            return Task.CompletedTask;
        };
        model.RequestSiteRootDirectoryAsync = () => PickAsync(new(Text("webservers.site.dialog.choose_static_root"),
            ServerFilePickerMode.SelectFolder));
        model.RequestServerCertificateFileAsync = privateKey => PickAsync(new(
            Text(privateKey ? "webservers.site.dialog.choose_private_key" : "webservers.site.dialog.choose_certificate"),
            ServerFilePickerMode.OpenFile,
            [privateKey
                ? new(Text("webservers.site.dialog.private_key_filter"), ["*.pem", "*.key"])
                : new(Text("webservers.site.dialog.certificate_filter"), ["*.pem", "*.crt", "*.cer"])]));
        model.ShowSiteEditorAsync = ShowEditorAsync;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        var refreshing = false;
        timer.Tick += async (_, _) =>
        {
            if (refreshing || !connection.IsCurrent) return;
            refreshing = true;
            try { await model.SynchronizeCurrentNginxAsync(); }
            finally { refreshing = false; }
        };
        owner.Closed.Register(() =>
        {
            timer.Stop();
            model.Dispose();
        });
        timer.Start();
        await model.StartAsync();

        string Text(string key) => WebServerLocalization.Resolve(context.SystemLanguage, key);

        async Task<string?> PickAsync(ServerFilePickerOptions options)
        {
            if (!context.Permissions.IsGranted(AppPermissions.ServerFilesRead)
                && await context.Permissions.RequestAsync(AppPermissions.ServerFilesRead, owner.Closed) != AppPermissionStatus.Granted)
                return null;
            var result = await context.ServerFiles.PickAsync(owner, options);
            return result.Status == AppCapabilityResult.Succeeded ? result.Path : null;
        }

        async Task ShowEditorAsync(bool edit)
        {
            try
            {
                await owner.ShowDialogAsync<bool>(Text(edit ? "webservers.site.edit" : "webservers.site.new"), dialog =>
                {
                    model.CloseSiteEditorAsync = () => { dialog.Close(true); return Task.CompletedTask; };
                    model.ShowSiteSaveErrorAsync = message => dialog.ShowDialogAsync<bool>(
                        Text("webservers.site.save_error.title"), error => CreateErrorContent(message, error));
                    return new WebServerSiteDialogView(model, dialog, AttachDraftGuard);
                }, new Size(640, 670));
            }
            finally
            {
                model.EndSiteEditing();
                model.CloseSiteEditorAsync = null;
                model.ShowSiteSaveErrorAsync = null;
            }
        }

        void AttachDraftGuard(ModalDialog<bool> dialog, Func<bool> busy, Func<bool> changed)
        {
            dialog.CanCancelAsync = async () =>
            {
                if (busy()) return false;
                if (!changed()) return true;
                var discard = await owner.ConfirmNestedAsync(dialog, Text("webservers.discard_title"),
                    Text("webservers.discard_message"), Text("webservers.discard"));
                return discard && !busy();
            };
        }

        Control CreateErrorContent(string message, ModalDialog<bool> dialog)
        {
            var close = new Button { Content = Text("webservers.site.save_error.acknowledge"), Classes = { "primary" } };
            close.Click += (_, _) => dialog.Close(true);
            return new StackPanel
            {
                Margin = new Avalonia.Thickness(20), Spacing = 12,
                Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, close },
            };
        }
    }

    // The scoped SDK client performs the host elevation at dispatch time.
    private sealed class CapabilityAuthorization : IWebServerAuthorization
    {
        public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation();
        }
    }
}
