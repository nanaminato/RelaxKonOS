using RelaxKonOS.Client.Apps.Docker.Views;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RelaxKonOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Docker]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var client = context.Services.GetService(typeof(IRemoteDockerClient)) as IRemoteDockerClient;
        if (session is null || client is null || session.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"),
                new DockerLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var vm = new DockerManagerViewModel(client);
        var proxyViewModel = new DockerProxyViewModel(client);
        var imageMirrorsViewModel = new DockerImageMirrorsViewModel(context.Services.GetRequiredService<IDockerImageMirrorClient>());
        vm.Installation = InstallationPanel.Create(context, InstallationServiceId.Docker, "relaxkonos.docker", () => vm.RefreshCommand.ExecuteAsync(null));
        ManagedWindow? window = null;
        var view = DockerManagerWorkspace.Create(vm, proxyViewModel, imageMirrorsViewModel,
            () => DockerManagerDialogs.ShowCreateContainerAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowDeployStackAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowPullImageAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(context, window!, vm));
        window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"), InstallationPanel.Wrap(view, vm.Installation), new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        // A daemon-layer change restarts Docker and interrupts running containers, so it is always
        // confirmed interactively rather than being an effect of pressing Save.
        proxyViewModel.RequestConfirmationAsync = async message =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window!, LocalizedText.Get("docker.proxy.title"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); }, LocalizedText.Get("docker.proxy.confirm_continue")),
            });
            return confirmed;
        };
        // Stopping or restarting the engine terminates every running container on the host, so it
        // gets its own confirmation wording instead of the deletion dialog's.
        vm.RequestEngineConfirmationAsync = async message =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window!, LocalizedText.Get("docker.engine.control"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); }, LocalizedText.Get("docker.engine.confirm_continue")),
            });
            return confirmed;
        };
        vm.ShowDockerUnavailableAsync = () => DockerManagerDialogs.ShowDockerUnavailableAsync(context, window, vm);
        vm.ShowEditContainerAsync = () => DockerManagerDialogs.ShowEditContainerAsync(context, window!, vm);
        vm.ShowEditStackAsync = () => DockerManagerDialogs.ShowEditStackAsync(context, window!, vm);
        vm.ShowContainerDetailsAsync = () => DockerManagerDialogs.ShowContainerDetailsAsync(context, window!, vm);
        vm.ShowResourceDetailsAsync = () => DockerManagerDialogs.ShowResourceDetailsAsync(context, window!, vm);
        vm.ShowErrorDialogAsync = message => DockerManagerDialogs.ShowErrorDialogAsync(context, window!, message);
        vm.RequestDeletionConfirmationAsync = async message =>
        {
            var confirmed = false;
            await context.ShowDialogAsync<bool>(window!, LocalizedText.Get("common.delete"), dialog => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, result => { confirmed = result; dialog.Close(result); }, LocalizedText.Get("common.delete")),
            });
            return confirmed;
        };
        vm.OpenFileBrowserAtPathAsync = path =>
        {
            var activation = context.Activations.Activate(RelaxKonOSActivationUris.ExplorerPath(path));
            if (!activation.Succeeded && !activation.IsPendingUserChoice)
                vm.StatusText = LocalizedText.Get("docker.stack.explorer_unavailable");
            return Task.CompletedTask;
        };
        vm.OpenDockerInstallGuideAsync = () => DockerManagerDialogs.ShowWindowsSetupGuideAsync(context, window!, vm);
        _ = vm.StartAsync();
    }
}
