using RelaxKonOS.Client.Apps.Docker.Views;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RelaxKonOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage], InstancePolicy: ApplicationInstancePolicy.SingleWindow);

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
        ManagedWindow? window = null;
        var view = DockerManagerWorkspace.Create(vm,
            () => DockerManagerDialogs.ShowCreateContainerAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowDeployStackAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowPullImageAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(context, window!, vm));
        window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
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
        vm.OpenDockerInstallGuideAsync = () =>
        {
            var language = (context.Services.GetService(typeof(ISystemLanguage)) as ISystemLanguage)?.CurrentLanguage ?? "en-US";
            var uri = new Uri($"help://guide/docker/install?lang={Uri.EscapeDataString(language)}");
            (context.Services.GetService(typeof(IAppActivationDiagnostics)) as IAppActivationDiagnostics)
                ?.Record($"Docker Manager requested installation guide: uri={uri.Scheme}://{uri.Host}{uri.AbsolutePath}, language={language}.");
            var activation = context.Activations.Activate(uri);
            (context.Services.GetService(typeof(IAppActivationDiagnostics)) as IAppActivationDiagnostics)
                ?.Record($"Docker Manager installation guide activation result: status={activation.Status}, target={activation.TargetAppId?.Value ?? "<none>"}.");
            if (!activation.Succeeded && !activation.IsPendingUserChoice)
                vm.StatusText = LocalizedText.Get("docker.status.install_guide_unavailable");
            return Task.CompletedTask;
        };
        _ = vm.StartAsync();
    }
}
