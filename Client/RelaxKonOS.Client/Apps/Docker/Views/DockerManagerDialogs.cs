using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Docker.Views;

/// <summary>Opens the Docker AXAML dialog views at their intended sizes.</summary>
internal static class DockerManagerDialogs
{
    public static Task ShowCreateContainerAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.container.create"), dialog => new DockerContainerDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(720, 690));

    public static Task ShowEditContainerAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.container.edit"), dialog => new DockerContainerEditDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(520, 250));

    public static Task ShowContainerDetailsAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.container.details"), dialog => new DockerContainerDetailsDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(720, 620));

    public static Task ShowResourceDetailsAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, vm.ResourceDetailsTitle, dialog => new DockerResourceDetailsDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(620, 460));

    public static Task ShowErrorDialogAsync(AppContext context, ManagedWindow owner, string message) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.read_error.title"), dialog => new DockerErrorDialogView(message, dialog), new RelaxKonOS.Core.Primitives.Size(460, 220));

    public static Task ShowDeployStackAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.stack.deploy"), dialog => new DockerStackDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(760, 550));

    public static Task ShowEditStackAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.stack.edit"), dialog => new DockerStackDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(760, 550));

    public static Task ShowPullImageAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.image.pull"), dialog => new DockerPullImageDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(470, 230));

    public static Task ShowCreateNetworkAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("common.create"), dialog => new DockerNetworkDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(470, 280));

    public static Task ShowCreateVolumeAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("common.create"), dialog => new DockerVolumeDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(470, 280));

    public static Task ShowDockerUnavailableAsync(AppContext context, ManagedWindow owner, DockerManagerViewModel vm) =>
        context.ShowDialogAsync<bool>(owner, RelaxKonOS.Client.Localization.LocalizedText.Get("docker.unavailable_dialog.title"), dialog => new DockerUnavailableDialogView(vm, dialog), new RelaxKonOS.Core.Primitives.Size(460, 220));
}
