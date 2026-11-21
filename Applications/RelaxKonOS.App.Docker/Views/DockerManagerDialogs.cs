using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.App.Docker.Views;

/// <summary>Opens the Docker AXAML dialog views at their intended sizes.</summary>
public static class DockerManagerDialogs
{
    public static Task ShowCreateContainerAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.container.create"), dialog => new DockerContainerDialogView(vm, dialog), new Size(720, 690));

    public static Task ShowEditContainerAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.container.edit"), dialog => new DockerContainerEditDialogView(vm, dialog), new Size(520, 250));

    public static Task ShowContainerDetailsAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.container.details"), dialog => new DockerContainerDetailsDialogView(vm, dialog), new Size(720, 620));

    public static Task ShowResourceDetailsAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.ResourceDetailsTitle, dialog => new DockerResourceDetailsDialogView(vm, dialog), new Size(620, 460));

    public static Task ShowErrorDialogAsync(IExternalAppWindowHandle owner, DockerText text, string message) =>
        owner.ShowDialogAsync<bool>(text.Get("docker.read_error.title"), dialog => new DockerErrorDialogView(message, dialog, text), new Size(460, 220));

    // The Compose editor fills the dialog, so the dialog itself decides how much YAML is visible at once.
    public static Task ShowDeployStackAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.stack.deploy"), dialog => new DockerStackDialogView(vm, dialog, owner), new Size(760, 620));

    public static Task ShowEditStackAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.stack.edit"), dialog => new DockerStackDialogView(vm, dialog, owner), new Size(760, 620));

    public static Task ShowPullImageAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.image.pull"), dialog => new DockerPullImageDialogView(vm, dialog), new Size(470, 230));

    public static Task ShowCreateNetworkAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("common.create"), dialog => new DockerNetworkDialogView(vm, dialog), new Size(470, 280));

    public static Task ShowCreateVolumeAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("common.create"), dialog => new DockerVolumeDialogView(vm, dialog), new Size(470, 280));

    public static Task ShowDockerUnavailableAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.unavailable_dialog.title"), dialog => new DockerUnavailableDialogView(vm, dialog), new Size(460, 220));

    public static Task ShowWindowsSetupGuideAsync(IExternalAppWindowHandle owner, DockerManagerViewModel vm) =>
        owner.ShowDialogAsync<bool>(vm.Labels.Get("docker.setup_guide.title"), dialog => new DockerWindowsSetupGuideDialogView(vm, dialog), new Size(640, 590));
}
