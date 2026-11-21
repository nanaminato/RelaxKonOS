using RelaxKonOS.App.Docker;
using RelaxKonOS.Client.Apps.Docker.Views;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Client.Services.AppPermissions;
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
using Microsoft.Extensions.DependencyInjection;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>Built-in client for the server-local Docker Engine.</summary>
public sealed class DockerManagerApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.docker"), "Docker Manager", "0.2.0", "🐳", "Manage the local Docker Engine on the RelaxKonOS Server", [AppPermissions.ServerDockerRead, AppPermissions.ServerDockerManage, AppPermissions.ServerComponentsRead], ServerRequirements: new ApplicationServerRequirements(Capabilities: [ServerCapabilities.Docker]), InstancePolicy: ApplicationInstancePolicy.SingleWindow);

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        if (session?.State != AuthSessionState.Authenticated)
        {
            context.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"),
                new DockerLoginRequiredView(),
                new Rect(180, 160, 470, 180), Manifest.IconGlyph, false, false, false);
            return;
        }

        var dependency = ComponentCenter.ComponentDependencySettingsViewModel.Create(context, "relaxkonos.docker");
        var sdk = context.Services.GetRequiredService<ExternalAppContextFactory>().Create(Manifest);
        var text = new DockerText(LocalizedText.Get);
        var vm = new DockerManagerViewModel(sdk.DockerResources, text);
        var proxyViewModel = new DockerProxyViewModel(sdk.DockerResources);
        var imageMirrorsViewModel = new DockerImageMirrorsViewModel(sdk.DockerImageMirrors, text);
        ManagedWindow? window = null;
        var view = DockerManagerWorkspace.Create(vm, proxyViewModel, imageMirrorsViewModel,
            () => DockerManagerDialogs.ShowCreateContainerAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowDeployStackAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowPullImageAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateNetworkAsync(context, window!, vm),
            () => DockerManagerDialogs.ShowCreateVolumeAsync(context, window!, vm), dependency);
        window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.docker.display_name"), view, new Rect(70, 55, 1180, 760), Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => imageMirrorsViewModel.RefreshLocalization();
        sdk.SystemLanguage.LanguageChanged += LanguageChanged;
        EventHandler<ManagedWindow>? closed = null;
        closed = (_, current) =>
        {
            if (!ReferenceEquals(current, window)) return;
            context.WindowManager.WindowClosed -= closed;
            sdk.SystemLanguage.LanguageChanged -= LanguageChanged;
            dependency.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
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
            if (!activation.IsAccepted)
                vm.StatusText = LocalizedText.Get("docker.stack.explorer_unavailable");
            return Task.CompletedTask;
        };
        _ = vm.StartAsync();
    }
}
