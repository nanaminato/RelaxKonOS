using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.App.Docker.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.App.Docker;

public sealed class DockerPackageApp : IExternalRemoteApplication
{
    private const string TitleKey = "application.relaxkonos.docker.display_name";

    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.docker"), "Docker Manager",
        "0.2.0", "🐳", "Manage the local Docker Engine on the RelaxKonOS Server",
        [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage, AppPermissions.ServerComponentsRead],
        ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Docker]),
        InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        var text = DockerLocalization.ForLanguage(language);
        if (!context.HasAuthenticatedServer)
        {
            var signInWindow = context.Windows.ShowWindow(text.Get(TitleKey), new DockerLoginRequiredView(text, language),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            ObserveLanguage(signInWindow, language, text, () => { });
            return Task.CompletedTask;
        }

        var model = new DockerManagerViewModel(context.DockerResources, text);
        var proxy = new DockerProxyStatusViewModel(context.DockerResources, text);
        var mirrors = new DockerImageMirrorsViewModel(context.DockerImageMirrors, text);
        IExternalAppWindowHandle? owner = null;
        Control? settings = null;
        var dependencies = context.ComponentDependencies;
        var workspace = new DockerManagerWorkspace(model, proxy, mirrors,
            () => DockerManagerDialogs.ShowCreateContainerAsync(owner!, model),
            () => DockerManagerDialogs.ShowDeployStackAsync(owner!, model),
            () => DockerManagerDialogs.ShowPullImageAsync(owner!, model),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(owner!, model),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(owner!, model),
            dependencies is null ? null : () => settings ??= dependencies.CreateSettingsView(owner!));
        owner = context.Windows.ShowWindow(text.Get(TitleKey), workspace, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        workspace.AttachWindowHeader(owner.Window);
        ObserveLanguage(owner, language, text, () =>
        {
            model.RefreshLocalization();
            proxy.RefreshLocalization();
            mirrors.RefreshLocalization();
        });

        model.RequestEngineConfirmationAsync = message =>
            owner.ConfirmAsync(text.Get("docker.engine.control"), message, text.Get("docker.engine.confirm_continue"));
        model.ShowDockerUnavailableAsync = () => DockerManagerDialogs.ShowDockerUnavailableAsync(owner, model);
        model.ShowEditContainerAsync = () => DockerManagerDialogs.ShowEditContainerAsync(owner, model);
        model.ShowEditStackAsync = () => DockerManagerDialogs.ShowEditStackAsync(owner, model);
        model.ShowContainerDetailsAsync = () => DockerManagerDialogs.ShowContainerDetailsAsync(owner, model);
        model.ShowResourceDetailsAsync = () => DockerManagerDialogs.ShowResourceDetailsAsync(owner, model);
        model.ShowErrorDialogAsync = message => DockerManagerDialogs.ShowErrorDialogAsync(owner, text, message);
        model.RequestDeletionConfirmationAsync = message =>
            owner.ConfirmAsync(text.Get("common.delete"), message, text.Get("common.delete"));
        model.OpenFileBrowserAtPathAsync = path =>
        {
            if (!context.Activations.Activate(RelaxKonOSActivationUris.ExplorerPath(path)).IsAccepted)
                model.StatusText = text.Get("docker.stack.explorer_unavailable");
            return Task.CompletedTask;
        };
        _ = model.StartAsync();
        return Task.CompletedTask;
    }

    private static void ObserveLanguage(IExternalAppWindowHandle owner, ISystemLanguage language, DockerText text, Action refresh)
    {
        var closed = owner.Closed;
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (closed.IsCancellationRequested) return;
                owner.Window.Title = text.Get(TitleKey);
                refresh();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        language.LanguageChanged += LanguageChanged;
        closed.Register(() => language.LanguageChanged -= LanguageChanged);
    }
}
