using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Apps.ApplicationDeployments.ViewModels;
using RelaxKonOS.Client.Apps.ApplicationDeployments.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ApplicationDeployments;

/// <summary>
/// Built-in client for containerized application deployment. It is a separate application from the
/// Docker Manager on purpose: Docker Manager operates on engine resources, while this one operates on
/// owned applications whose definitions, revisions, and operations the server records durably.
/// </summary>
public sealed class ApplicationDeploymentsApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.application-deployments"), "Application Deployments", "0.1.0", "📦",
        "Deploy and manage containerized applications on the RelaxKonOS Server",
        [AppPermissions.ServerApplicationDeploymentsRead, AppPermissions.ServerApplicationDeploymentsManage],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.ApplicationDeployments]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteApplicationDeploymentClient)) as IRemoteApplicationDeploymentClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.application-deployments.display_name"),
                new ApplicationDeploymentsLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        // The server-side file picker is optional: without a file-services client the wizard still
        // offers the local upload path, which is the one that never requires a server browse.
        var explorer = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var viewModel = new ApplicationDeploymentsViewModel(client, context.Permissions);
        ManagedWindow? window = null;
        var view = ApplicationDeploymentsWorkspace.Create(viewModel);
        window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.application-deployments.display_name"),
            view, new Rect(60, 50, 1220, 780), Manifest.IconGlyph);

        viewModel.ConfirmAsync = async message =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool?>(window!, LocalizedText.Get("common.confirm"), dialog =>
                new ConfirmDialogView
                {
                    DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); },
                        LocalizedText.Get("common.confirm")),
                }, new Size(460, 200));
            return confirmed;
        };

        viewModel.ShowWizardAsync = wizard => context.ShowDialogAsync<bool>(window!,
            LocalizedText.Get("application_deployments.wizard.title"),
            dialog => new DeploymentWizardView(wizard, dialog), new Size(780, 740));

        // The access address is the one field an operator routinely needs outside this window, so the
        // shell supplies both ways out of it: the system clipboard, and the default browser.
        // The type is spelled out because this class has its own TopLevel() helper.
        viewModel.CopyToClipboardAsync = async address =>
        {
            var clipboard = Avalonia.Controls.TopLevel.GetTopLevel(view)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(address);
        };

        // The launch is deliberately not swallowed here: a host with no registered browser handler
        // throws, and the view model reports that instead of the click doing nothing.
        viewModel.OpenInBrowserAsync = address =>
        {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            return Task.CompletedTask;
        };

        viewModel.PickLocalArchiveAsync = async () =>
        {
            var topLevel = TopLevel();
            if (topLevel is null) return null;
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizedText.Get("application_deployments.wizard.choose_local_archive"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(LocalizedText.Get("application_deployments.wizard.archive_file_type")) { Patterns = ["*.zip", "*.jar", "*.tar", "*.gz", "*.tgz"] }],
            });
            return selected.FirstOrDefault()?.TryGetLocalPath();
        };

        viewModel.PickServerArchiveAsync = () => explorer is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string?>(window!,
                LocalizedText.Get("application_deployments.wizard.choose_server_archive"), dialog =>
                {
                    var picker = new ExplorerViewModel(explorer,
                        new ExplorerPickerOptions(ExplorerPickerMode.OpenFile), paths => dialog.Close(paths[0]))
                    {
                        CancelAction = dialog.Cancel,
                    };
                    _ = picker.LoadRootAsync();
                    return new ExplorerMainView { DataContext = picker };
                }, new Size(860, 580));

        _ = viewModel.StartAsync();
    }

    /// <summary>The host window, which is the root that owns the OS file picker.</summary>
    private static TopLevel? TopLevel() =>
        Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
