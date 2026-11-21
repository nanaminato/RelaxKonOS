using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RelaxKonOS.App.ApplicationDeployments.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Common;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.ApplicationDeployments;

public sealed class ApplicationDeploymentsPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.application-deployments.display_name";
    private static readonly string[] ArchivePatterns = ["*.zip", "*.jar", "*.tar", "*.gz", "*.tgz"];

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.application-deployments"),
        "Application Deployments", "1.0.0", "📦", "Deploy and manage containerized applications on the RelaxKonOS Server",
        [AppPermissions.ServerApplicationDeploymentsRead, AppPermissions.ServerApplicationDeploymentsManage, AppPermissions.ServerFilesRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.ApplicationDeployments]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public async Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => ApplicationDeploymentsLocalization.Resolve(language, key, key);
        var connection = context.ServerConnection;
        if (!context.HasAuthenticatedServer || !connection.IsCurrent)
        {
            context.Windows.ShowWindow(Label(TitleKey), new ApplicationDeploymentsLoginRequiredView(language),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var permissions = context.Permissions;
        if (permissions.GetStatus(AppPermissions.ServerApplicationDeploymentsRead) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerApplicationDeploymentsRead, cancellationToken);
        if (permissions.IsGranted(AppPermissions.ServerApplicationDeploymentsRead)
            && permissions.GetStatus(AppPermissions.ServerApplicationDeploymentsManage) == AppPermissionStatus.Undecided)
            await permissions.RequestAsync(AppPermissions.ServerApplicationDeploymentsManage, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.IsCurrent) return;

        var client = context.ApplicationDeployments;
        var model = new ApplicationDeploymentsViewModel(client, permissions, Label);
        var view = ApplicationDeploymentsWorkspace.Create(model);
        var window = context.Windows.ShowWindow(Label(TitleKey), view, new Rect(60, 50, 1220, 780), Manifest.IconGlyph);
        view.AttachWindowHeader(window.Window);

        model.ConfirmAsync = message => window.ConfirmAsync(Label("common.confirm"), message.Resolve(), Label("common.confirm"));
        model.ShowWizardAsync = async wizard =>
        {
            void Update(object? sender, SystemLanguageChangedEventArgs args) =>
                Dispatch(window.Closed, () => wizard.OnLanguageChanged(sender, args));
            language.LanguageChanged += Update;
            try
            {
                await window.ShowDialogAsync<bool>(Label("application_deployments.wizard.title"),
                    dialog => new DeploymentWizardView(wizard, dialog), new Size(780, 740));
            }
            finally
            {
                language.LanguageChanged -= Update;
                wizard.StopObserving();
            }
        };
        model.ShowCatalogAsync = releases => window.ShowDialogAsync<CatalogApplicationInstallDto?>(
            Label("application_deployments.catalog.title"), dialog =>
            {
                var descriptor = connection.Platform is { } platform ? new ServerDescriptorDto(platform, connection.Capabilities) : null;
                return new ApplicationCatalogView(client, releases, descriptor, dialog, Label, language);
            }, new Size(600, 680));
        model.ShowCatalogUpgradeAsync = (application, releases) => window.ShowDialogAsync<DeploymentOperationDto?>(
            Label("application_deployments.catalog.upgrade"),
            dialog => new ApplicationCatalogUpgradeView(client, application, releases, dialog, Label), new Size(680, 520));

        model.CopyToClipboardAsync = async address =>
        {
            if (TopLevel.GetTopLevel(view)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(address);
        };
        model.OpenInBrowserAsync = async address =>
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp
                || TopLevel.GetTopLevel(view)?.Launcher is not { } launcher || !await launcher.LaunchUriAsync(uri))
                throw new System.ComponentModel.Win32Exception();
        };

        model.PickLocalArchiveAsync = async () =>
        {
            if (TopLevel.GetTopLevel(view)?.StorageProvider is not { } storage) return null;
            var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Label("application_deployments.wizard.choose_local_archive"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType(Label("application_deployments.wizard.archive_file_type")) { Patterns = ArchivePatterns }],
            });
            var file = selected.FirstOrDefault();
            return file is null || window.Closed.IsCancellationRequested ? null : new(file.Name, file.OpenReadAsync);
        };

        model.PickServerArchiveAsync = async () =>
        {
            if (permissions.GetStatus(AppPermissions.ServerFilesRead) == AppPermissionStatus.Undecided)
                await permissions.RequestAsync(AppPermissions.ServerFilesRead, window.Closed);
            if (!connection.IsCurrent || !permissions.IsGranted(AppPermissions.ServerFilesRead)) return null;
            var picked = await context.ServerFiles.PickAsync(window,
                new(Label("application_deployments.wizard.choose_server_archive"), ServerFilePickerMode.OpenFile));
            return picked.Status == AppCapabilityResult.Succeeded ? picked.Path : null;
        };

        void Refresh(object? sender, SystemLanguageChangedEventArgs args) => Dispatch(window.Closed, () =>
        {
            window.Window.Title = Label(TitleKey);
            model.OnLanguageChanged(sender, args);
        });

        language.LanguageChanged += Refresh;
        var observation = connection.ObserveInvalidation(() => Dispatch(window.Closed, model.StopObserving));
        window.Closed.Register(() =>
        {
            language.LanguageChanged -= Refresh;
            observation.Dispose();
            if (Dispatcher.UIThread.CheckAccess()) model.StopObserving();
            else Dispatcher.UIThread.Post(model.StopObserving);
        });
        await model.StartAsync();
    }

    private static void Dispatch(CancellationToken closed, Action action)
    {
        void Apply() { if (!closed.IsCancellationRequested) action(); }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}
