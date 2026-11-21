using RelaxKonOS.App.Docker;
using RelaxKonOS.App.Docker.Views;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using Microsoft.Extensions.DependencyInjection;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RelaxKonOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage, AppPermissions.ServerComponentsRead], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Docker]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var sdk = context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        var text = new DockerText(LocalizedText.Get);
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        if (session?.State != AuthSessionState.Authenticated)
        {
            sdk.Windows.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"),
                new DockerLoginRequiredView(text, sdk.SystemLanguage),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var dependency = ComponentCenter.ComponentDependencySettingsViewModel.Create(context, "relaxkonos.docker");
        var vm = new DockerManagerViewModel(sdk.DockerResources, text);
        var proxyViewModel = new DockerProxyStatusViewModel(sdk.DockerResources, text);
        var imageMirrorsViewModel = new DockerImageMirrorsViewModel(sdk.DockerImageMirrors, text);
        IExternalAppWindowHandle? window = null;
        var view = new DockerManagerWorkspace(vm, proxyViewModel, imageMirrorsViewModel,
            () => DockerManagerDialogs.ShowCreateContainerAsync(window!, vm),
            () => DockerManagerDialogs.ShowDeployStackAsync(window!, vm),
            () => DockerManagerDialogs.ShowPullImageAsync(window!, vm),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(window!, vm),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(window!, vm),
            () =>
            {
                _ = dependency.RefreshAsync();
                return new ComponentCenter.ComponentDependencySettingsView { DataContext = dependency };
            });
        window = sdk.Windows.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window.Window);
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args)
        {
            vm.RefreshLocalization();
            proxyViewModel.RefreshLocalization();
            imageMirrorsViewModel.RefreshLocalization();
        }
        sdk.SystemLanguage.LanguageChanged += LanguageChanged;
        EventHandler<ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window.Window)) return;
            context.WindowManager.WindowClosed -= closed;
            sdk.SystemLanguage.LanguageChanged -= LanguageChanged;
            dependency.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        // Stopping or restarting the engine terminates every running container on the host, so it
        // gets its own confirmation wording instead of the deletion dialog's.
        vm.RequestEngineConfirmationAsync = message =>
            window.ConfirmAsync(text.Get("docker.engine.control"), message, text.Get("docker.engine.confirm_continue"));
        vm.ShowDockerUnavailableAsync = () => DockerManagerDialogs.ShowDockerUnavailableAsync(window, vm);
        vm.ShowEditContainerAsync = () => DockerManagerDialogs.ShowEditContainerAsync(window, vm);
        vm.ShowEditStackAsync = () => DockerManagerDialogs.ShowEditStackAsync(window, vm);
        vm.ShowContainerDetailsAsync = () => DockerManagerDialogs.ShowContainerDetailsAsync(window, vm);
        vm.ShowResourceDetailsAsync = () => DockerManagerDialogs.ShowResourceDetailsAsync(window, vm);
        vm.ShowErrorDialogAsync = message => DockerManagerDialogs.ShowErrorDialogAsync(window, text, message);
        vm.RequestDeletionConfirmationAsync = message =>
            window.ConfirmAsync(text.Get("common.delete"), message, text.Get("common.delete"));
        vm.OpenFileBrowserAtPathAsync = path =>
        {
            var activation = context.Activations.Activate(RelaxKonOSActivationUris.ExplorerPath(path));
            if (!activation.IsAccepted)
                vm.StatusText = LocalizedText.Get("docker.stack.explorer_unavailable");
            return Task.CompletedTask;
        };
        _ = vm.StartAsync();
    }
}
