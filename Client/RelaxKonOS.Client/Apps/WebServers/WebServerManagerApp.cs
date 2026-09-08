using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.WebServers.Views;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.WebServers;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.WebServers;

/// <summary>Built-in web server manager. Host-global Nginx discovery, config test, integrate, reload.</summary>
public sealed class WebServerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.webservers"), "Web Server Manager", "0.1.0", "🌐", "Manage web servers on the RelaxKonOS Server",
        [AppPermissions.ServerWebServersRead, AppPermissions.ServerWebServersManage],
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteWebServerClient)) as IRemoteWebServerClient;
        var certificates = context.Services.GetService(typeof(IRemoteCertificateClient)) as IRemoteCertificateClient;
        var explorer = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        if (session is null || client is null || certificates is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.webservers.display_name"),
                new WebServerLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }
        var viewModel = new WebServerManagerViewModel(client, certificates, session, context.Permissions);
        var view = WebServerManagerWorkspace.Create(viewModel);
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.webservers.display_name"),
            view, new Rect(70, 55, 1080, 680), Manifest.IconGlyph);
        viewModel.ShowPrivilegedHelperUnavailableAsync = problemCode => PrivilegedHelperUnavailableDialog.ShowAsync(context, window, problemCode);
        viewModel.RequestIntegrationConfirmationAsync = async () =>
        {
            return await ConfirmAsync("webservers.integration.confirmation.title", "webservers.integration.confirmation.message", "webservers.integration.confirmation.confirm");
        };
        viewModel.RequestManagedInstallConfirmationAsync = () => ConfirmAsync("webservers.managed.install.title", "webservers.managed.install.message", "webservers.managed.install.confirm");
        viewModel.RequestExistingManagedInstallActionAsync = () => context.ShowDialogAsync<ManagedInstallExistingDirectoryAction?>(window,
            LocalizedText.Get("webservers.managed.existing.title"), dialog => new ExistingNginxInstallationDialogView
            {
                DataContext = new ExistingNginxInstallationDialogViewModel(action => dialog.Close(action)),
            }, new Size(560, 260));
        viewModel.RequestManagedUninstallConfirmationAsync = () => ConfirmAsync("webservers.managed.uninstall.title", "webservers.managed.uninstall.message", "webservers.managed.uninstall.confirm");
        viewModel.ShowManagedDownloadUrlAsync = url => ShowDownloadUrlAsync(LocalizedText.Get("webservers.managed.download_title"), url);
        viewModel.OpenFileBrowserAtPathAsync = path =>
        {
            var activation = context.Activations.Activate(RelaxKonOSActivationUris.ExplorerPath(path));
            if (!activation.Succeeded && !activation.IsPendingUserChoice)
                viewModel.SiteStatusText = LocalizedText.Get("webservers.site.file_browser_failed");
            return Task.CompletedTask;
        };
        viewModel.ShowSiteEditorAsync = async isEdit =>
        {
            try
            {
                await context.ShowDialogAsync<bool>(window, LocalizedText.Get(isEdit ? "webservers.site.edit" : "webservers.site.new"), dialog =>
                {
                    viewModel.CloseSiteEditorAsync = () =>
                    {
                        dialog.Close(true);
                        return Task.CompletedTask;
                    };
                    viewModel.ShowSiteSaveErrorAsync = message => dialog.ShowDialogAsync<bool>(LocalizedText.Get("webservers.site.save_error.title"),
                        errorDialog => new WebServerSiteSaveErrorDialogView(message, errorDialog));
                    return new WebServerSiteDialogView(viewModel, dialog);
                }, new Size(640, 670));
            }
            finally
            {
                viewModel.CloseSiteEditorAsync = null;
                viewModel.ShowSiteSaveErrorAsync = null;
            }
        };
        viewModel.RequestLocalNginxPackageAsync = async () =>
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return null;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("webservers.managed.select_package"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("webservers.managed.package_file_type")) { Patterns = ["*.zip"] }],
            });
            return selected.FirstOrDefault()?.TryGetLocalPath();
        };
        viewModel.RequestServerCertificateFileAsync = isPrivateKey => explorer is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window,
                LocalizedText.Get(isPrivateKey ? "webservers.site.dialog.choose_private_key" : "webservers.site.dialog.choose_certificate"), dialog =>
                {
                    var filter = isPrivateKey
                        ? new ExplorerFileFilter(LocalizedText.Get("webservers.site.dialog.private_key_filter"), ["*.pem", "*.key"])
                        : new ExplorerFileFilter(LocalizedText.Get("webservers.site.dialog.certificate_filter"), ["*.pem", "*.crt", "*.cer"]);
                    var picker = new ExplorerViewModel(explorer,
                        new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [filter]), paths => dialog.Close(paths[0]))
                    {
                        CancelAction = dialog.Cancel,
                    };
                    _ = picker.LoadRootAsync();
                    return new ExplorerMainView { DataContext = picker };
                }, new Size(860, 580));

        async Task<bool> ConfirmAsync(string titleKey, string messageKey, string confirmKey)
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool?>(window, LocalizedText.Get(titleKey), dialog =>
            {
                var dialogViewModel = new NginxIntegrationConfirmationDialogViewModel(result =>
                {
                    confirmed = result;
                    dialog.Close(result);
                }, messageKey, confirmKey);
                return new NginxIntegrationConfirmationDialogView { DataContext = dialogViewModel };
            }, new Size(500, 220));
            return confirmed;
        }
        _ = viewModel.StartAsync();

        Task ShowDownloadUrlAsync(string title, string url) => context.ShowDialogAsync<bool?>(window, title, dialog => new DownloadUrlDialogView
        {
            DataContext = new DownloadUrlDialogViewModel(url, CopyToClipboardAsync, () => dialog.Close(true)),
        }, new Size(660, 210));

        async Task CopyToClipboardAsync(string value)
        {
            var topLevel = GetTopLevel();
            if (topLevel?.Clipboard is not null) await topLevel.Clipboard.SetTextAsync(value);
        }
    }

    private static TopLevel? GetTopLevel() => Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow
        : null;
}
