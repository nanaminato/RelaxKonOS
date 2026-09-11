using System.Collections.ObjectModel;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Installation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Client.Apps.Docker;

/// <summary>State and safe, typed operations for the server-local Docker Manager.</summary>
public sealed partial class DockerManagerViewModel(IRemoteDockerClient client) : ObservableObject
{
    public InstallationTaskViewModel Installation { get; set; } = null!;
    public ObservableCollection<DockerContainerDto> Containers { get; } = [];
    public ObservableCollection<DockerImageDto> Images { get; } = [];
    public ObservableCollection<DockerNetworkDto> Networks { get; } = [];
    public ObservableCollection<DockerVolumeDto> Volumes { get; } = [];
    public ObservableCollection<DockerStackDto> Stacks { get; } = [];
    public ObservableCollection<DockerStackServiceDto> StackServices { get; } = [];
    public ObservableCollection<string> AvailableNetworks { get; } = ["bridge"];

    // Docker's built-in drivers that can create a user-defined network. Host and none are
    // built-in special networks, rather than choices for `docker network create`.
    public IReadOnlyList<string> NetworkDrivers { get; } = ["bridge", "ipvlan", "macvlan", "overlay"];
    public IReadOnlyList<string> VolumeDrivers { get; } = ["local"];
    public IReadOnlyList<string> RestartPolicies { get; } = ["no", "always", "unless-stopped", "on-failure"];

    [ObservableProperty] private string _statusText = LocalizedText.Get("docker.status.loading");
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty] private string _operationTitle = string.Empty;
    [ObservableProperty] private string _operationLog = string.Empty;
    [ObservableProperty] private bool _isDockerAvailable;
    [ObservableProperty] private bool _isDockerInstallRequired;
    [ObservableProperty] private string _engineVersion = "—";
    [ObservableProperty] private string _enginePlatform = "—";
    [ObservableProperty] private DockerContainerDto? _selectedContainer;
    [ObservableProperty] private DockerContainerDetailsDto? _containerDetails;
    [ObservableProperty] private string _containerDetailsText = string.Empty;
    [ObservableProperty] private string _containerPortsText = string.Empty;
    [ObservableProperty] private string _containerMountsText = string.Empty;
    [ObservableProperty] private string _containerNetworksText = string.Empty;
    [ObservableProperty] private string _containerEnvironmentText = string.Empty;
    [ObservableProperty] private string _containerLabelsText = string.Empty;
    [ObservableProperty] private string _containerLogs = string.Empty;
    [ObservableProperty] private string _containerStats = string.Empty;
    [ObservableProperty] private DockerStackDto? _selectedStack;
    [ObservableProperty] private string _stackName = string.Empty;
    [ObservableProperty] private string _composeYaml = string.Empty;
    [ObservableProperty] private string _imageReference = string.Empty;
    [ObservableProperty] private string _containerName = string.Empty;
    [ObservableProperty] private string _containerImage = string.Empty;
    [ObservableProperty] private string _containerArguments = string.Empty;
    [ObservableProperty] private string _containerPorts = string.Empty;
    [ObservableProperty] private string _containerEnvironment = string.Empty;
    [ObservableProperty] private string _containerMounts = string.Empty;
    [ObservableProperty] private string _containerNetwork = "bridge";
    [ObservableProperty] private string _containerRestartPolicy = "unless-stopped";
    [ObservableProperty] private DockerImageDto? _selectedImage;
    [ObservableProperty] private string _networkName = string.Empty;
    [ObservableProperty] private string _selectedNetworkDriver = "bridge";
    [ObservableProperty] private DockerNetworkDto? _selectedNetwork;
    [ObservableProperty] private string _volumeName = string.Empty;
    [ObservableProperty] private string _selectedVolumeDriver = "local";
    [ObservableProperty] private DockerVolumeDto? _selectedVolume;
    [ObservableProperty] private string _resourceDetailsText = string.Empty;
    [ObservableProperty] private string _resourceDetailsTitle = string.Empty;

    /// <summary>Assigned by the app shell so operations can surface an unavailable Engine immediately.</summary>
    public Func<Task>? ShowDockerUnavailableAsync { get; set; }
    /// <summary>Assigned by the app shell to open the localized Docker installation guide.</summary>
    public Func<Task>? OpenDockerInstallGuideAsync { get; set; }
    /// <summary>Assigned by the app shell to display edit dialogs without coupling the VM to views.</summary>
    public Func<Task>? ShowEditContainerAsync { get; set; }
    public Func<Task>? ShowEditStackAsync { get; set; }
    public Func<Task>? ShowContainerDetailsAsync { get; set; }
    /// <summary>Assigned by the app shell to display read-only network and volume inspection data.</summary>
    public Func<Task>? ShowResourceDetailsAsync { get; set; }
    /// <summary>Assigned by the app shell to report an error without using the operation log panel.</summary>
    public Func<string, Task>? ShowErrorDialogAsync { get; set; }
    /// <summary>Assigned by the app shell to confirm destructive operations before they reach Docker.</summary>
    public Func<string, Task<bool>>? RequestDeletionConfirmationAsync { get; set; }
    /// <summary>Routes a known server-side Compose directory into RemoteExplorer.</summary>
    public Func<string, Task>? OpenFileBrowserAtPathAsync { get; set; }
    private bool _isUnavailableDialogShowing;

    public int RunningContainerCount => Containers.Count(container => container.State.Equals("running", StringComparison.OrdinalIgnoreCase));
    public bool HasOperationActivity => !string.IsNullOrWhiteSpace(OperationTitle);

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var statusTask = client.GetStatusAsync();
            var containersTask = client.ListContainersAsync();
            var imagesTask = client.ListImagesAsync();
            var networksTask = client.ListNetworksAsync();
            var volumesTask = client.ListVolumesAsync();
            var stacksTask = client.ListStacksAsync();
            await Task.WhenAll(statusTask, containersTask, imagesTask, networksTask, volumesTask, stacksTask);
            var status = await statusTask;
            IsDockerAvailable = status.IsAvailable;
            IsDockerInstallRequired = IsInstallRequired(status.IsAvailable, status.ProblemCode);
            EngineVersion = status.ServerVersion ?? "—";
            EnginePlatform = string.Join(" / ", new[] { status.OperatingSystem, status.Architecture }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(EnginePlatform)) EnginePlatform = "—";
            StatusText = status.IsAvailable
                ? LocalizedText.Format("docker.status.available", status.ServerVersion ?? "", status.OperatingSystem ?? "")
                : LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
            Replace(Containers, await containersTask); Replace(Images, await imagesTask);
            var networks = await networksTask;
            Replace(Networks, networks); Replace(Volumes, await volumesTask);
            Replace(Stacks, await stacksTask);
            Replace(AvailableNetworks, networks.Select(network => network.Name).Prepend("bridge").Distinct(StringComparer.Ordinal));
            if (!AvailableNetworks.Contains(ContainerNetwork, StringComparer.Ordinal)) ContainerNetwork = "bridge";
            OnPropertyChanged(nameof(RunningContainerCount));
        }
        catch (Exception exception)
        {
            IsDockerInstallRequired = false;
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear(); foreach (var value in values) target.Add(value);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task StartContainerAsync() => ApplyContainerActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task StopContainerAsync() => ApplyContainerActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task RestartContainerAsync() => ApplyContainerActionAsync("restart");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task PauseContainerAsync() => ApplyContainerActionAsync("pause");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task UnpauseContainerAsync() => ApplyContainerActionAsync("unpause");
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private Task LoadContainerDetailsAsync() => LoadSelectedContainerDetailsAsync();
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))]
    private async Task EditContainerAsync()
    {
        if (SelectedContainer is null || ShowEditContainerAsync is null) return;
        ContainerName = SelectedContainer.Names;
        await ShowEditContainerAsync();
    }
    [RelayCommand(CanExecute = nameof(CanDeleteContainer))]
    private async Task DeleteContainerAsync()
    {
        var container = SelectedContainer;
        if (container is null || !await ConfirmDeletionAsync(LocalizedText.Format("docker.container.delete_confirmation", container.Names))) return;
        await ApplyContainerActionAsync("delete", true);
    }
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private async Task LoadContainerLogsAsync()
    {
        var container = SelectedContainer; if (container is null) return;
        await RunQuietReadAsync(LocalizedText.Get("docker.container.logs"), async () =>
        {
            var logs = await client.GetContainerLogsAsync(container.Id);
            if (logs is null) return false;
            ContainerLogs = string.Join(Environment.NewLine, logs.Lines);
            StatusText = LocalizedText.Format("docker.action.succeeded", OperationText("logs"), container.Names);
            return true;
        });
    }
    [RelayCommand(CanExecute = nameof(HasSelectedContainer))] private async Task LoadContainerStatsAsync()
    {
        var container = SelectedContainer; if (container is null) return;
        await RunQuietReadAsync(LocalizedText.Get("docker.container.stats"), async () =>
        {
            var stats = await client.GetContainerStatsAsync(container.Id);
            if (stats is null) return false;
            ContainerStats = LocalizedText.Format("docker.stats.summary", stats.CpuPercent, stats.MemoryUsage, stats.NetworkIo, stats.BlockIo);
            StatusText = LocalizedText.Format("docker.action.succeeded", OperationText("stats"), container.Names);
            return true;
        });
    }

    private bool HasSelectedContainer => SelectedContainer is not null && !IsLoading;
    private bool CanDeleteContainer => HasSelectedContainer;
    partial void OnSelectedContainerChanged(DockerContainerDto? value)
    {
        ContainerLogs = ContainerStats = string.Empty;
        ContainerDetails = null;
        ContainerDetailsText = string.Empty;
        ContainerPortsText = ContainerMountsText = ContainerNetworksText = ContainerEnvironmentText = ContainerLabelsText = string.Empty;
        NotifyContainerCommands();
    }
    partial void OnIsLoadingChanged(bool value)
    {
        NotifyContainerCommands();
        NotifyStackCommands();
        DeleteImageCommand.NotifyCanExecuteChanged(); DeleteNetworkCommand.NotifyCanExecuteChanged(); DeleteVolumeCommand.NotifyCanExecuteChanged();
        LoadNetworkDetailsCommand.NotifyCanExecuteChanged(); LoadVolumeDetailsCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsDockerInstallRequiredChanged(bool value) => OpenInstallGuideCommand.NotifyCanExecuteChanged();

    private bool CanOpenInstallGuide => IsDockerInstallRequired && OpenDockerInstallGuideAsync is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInstallGuide))]
    private async Task OpenInstallGuideAsync()
    {
        if (OpenDockerInstallGuideAsync is not null)
            await OpenDockerInstallGuideAsync();
    }
    private void NotifyContainerCommands()
    {
        StartContainerCommand.NotifyCanExecuteChanged(); StopContainerCommand.NotifyCanExecuteChanged(); RestartContainerCommand.NotifyCanExecuteChanged();
        PauseContainerCommand.NotifyCanExecuteChanged(); UnpauseContainerCommand.NotifyCanExecuteChanged(); DeleteContainerCommand.NotifyCanExecuteChanged();
        EditContainerCommand.NotifyCanExecuteChanged(); LoadContainerDetailsCommand.NotifyCanExecuteChanged(); LoadContainerLogsCommand.NotifyCanExecuteChanged(); LoadContainerStatsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Loads the selected container's inspectable runtime details and opens the details window.</summary>
    public async Task LoadSelectedContainerDetailsAsync()
    {
        var container = SelectedContainer;
        if (container is null) return;
        await RunQuietReadAsync(LocalizedText.Get("docker.resource.details"), async () =>
        {
            var details = await client.GetContainerAsync(container.Id);
            ContainerDetails = details;
            if (details is null) return false;
            ContainerDetailsText = FormatContainerDetails(details);
            ContainerPortsText = string.Join(Environment.NewLine, details.Ports);
            ContainerMountsText = string.Join(Environment.NewLine, details.Mounts);
            ContainerNetworksText = string.Join(Environment.NewLine, details.Networks);
            ContainerEnvironmentText = string.Join(Environment.NewLine, details.Environment);
            ContainerLabelsText = string.Join(Environment.NewLine, details.Labels.Select(label => $"{label.Key}={label.Value}"));
            StatusText = LocalizedText.Format("docker.container.details_loaded", container.Names);
            return true;
        });
        if (ContainerDetails is not null && ShowContainerDetailsAsync is not null)
            await ShowContainerDetailsAsync();
    }
    private async Task ApplyContainerActionAsync(string action, bool confirmed = false)
    {
        var container = SelectedContainer; if (container is null) return;
        Func<DockerOperationResult, string> status = result => result.Success
            ? LocalizedText.Format("docker.action.succeeded", OperationText(action), container.Names)
            : LocalizedText.Format("docker.action.failed", OperationText(action), ProblemText(result.ProblemCode));
        if (action is "start" or "stop" or "restart" or "pause" or "unpause")
        {
            await RunQuietOperationAsync(
                () => client.ApplyContainerActionAsync(container.Id, action, new DockerContainerActionRequest(Confirmed: confirmed)),
                result => result.Success,
                status);
            return;
        }
        await RunOperationAsync(
            () => client.ApplyContainerActionAsync(container.Id, action, new DockerContainerActionRequest(Confirmed: confirmed)),
            status,
            operationName: LocalizedText.Format("docker.operation.container_action", OperationText(action), container.Names));
    }

    /// <summary>Queues the non-destructive in-place container edit and reports whether its dialog can close.</summary>
    public async Task<bool> TryUpdateContainerAsync()
    {
        var container = SelectedContainer;
        if (IsLoading || container is null || string.IsNullOrWhiteSpace(ContainerName)) return false;
        var name = ContainerName.Trim();
        if (name.Equals(container.Names, StringComparison.Ordinal)) return true;
        return await RunOperationAsync(
            () => client.UpdateContainerAsync(container.Id, new DockerContainerUpdateRequest(name)),
            result => result.Success ? LocalizedText.Format("docker.container.updated", name) : LocalizedText.Format("docker.container.update_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.update_container", container.Names));
    }

    [RelayCommand] private Task ValidateStackAsync() => ApplyStackAsync("validate");
    [RelayCommand] private Task DeployStackAsync() => TryDeployStackAsync();

    /// <summary>Queues a Compose deployment and reports whether its dialog can close immediately.</summary>
    public Task<bool> TryDeployStackAsync() => ApplyStackAsync("deploy");

    private async Task<bool> ApplyStackAsync(string operation)
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Get("docker.stack.required"); return false; }
        return await RunStackOperationAsync(operation);
    }
    private async Task<bool> RunStackOperationAsync(string operation)
    {
        return await RunOperationAsync(
            () => client.ApplyStackOperationAsync(operation, new DockerStackDefinitionDto(StackName.Trim(), ComposeYaml)),
            result =>
            {
                var detail = result.Messages.FirstOrDefault() ?? result.ProblemCode;
                return result.Success ? LocalizedText.Format("docker.stack.succeeded", OperationText(operation), StackName) : LocalizedText.Format("docker.stack.failed", OperationText(operation), detail);
            }, LocalizedText.Format("docker.operation.stack", OperationText(operation), StackName));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedStack))]
    private async Task LoadStackServicesAsync()
    {
        var stack = SelectedStack;
        if (stack is null) return;
        await RunQuietReadAsync(LocalizedText.Get("docker.stack.services"), async () =>
        {
            Replace(StackServices, await client.ListStackServicesAsync(stack.Name));
            StatusText = LocalizedText.Format("docker.stack.services_loaded", stack.Name, StackServices.Count);
            return true;
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedStack))] private Task StartStackAsync() => ApplySelectedStackActionAsync("start");
    [RelayCommand(CanExecute = nameof(HasSelectedStack))] private Task StopStackAsync() => ApplySelectedStackActionAsync("stop");
    [RelayCommand(CanExecute = nameof(HasSelectedStack))] private Task RestartStackAsync() => ApplySelectedStackActionAsync("restart");
    [RelayCommand(CanExecute = nameof(CanDeleteStack))]
    private async Task DeleteStackAsync()
    {
        var stack = SelectedStack;
        if (stack is null || !await ConfirmDeletionAsync(LocalizedText.Format("docker.stack.delete_confirmation", stack.Name))) return;
        await ApplySelectedStackActionAsync("delete", confirmed: true);
    }
    [RelayCommand(CanExecute = nameof(HasSelectedStack))]
    private async Task EditStackAsync()
    {
        var stack = SelectedStack;
        if (stack is null || ShowEditStackAsync is null) return;
        DockerStackDefinitionDto? definition = null;
        await RunQuietReadAsync(LocalizedText.Get("docker.stack.edit"), async () =>
        {
            definition = await client.GetStackDefinitionAsync(stack.Name);
            if (definition is null) return false;
            StatusText = LocalizedText.Format("docker.stack.source_loaded", stack.Name);
            return true;
        }, LocalizedText.Get("docker.stack.source_unavailable"));
        if (definition is null) return;
        StackName = definition.Name;
        ComposeYaml = definition.ComposeYaml;
        await ShowEditStackAsync();
    }
    private bool CanOpenStackSource => HasSelectedStack && !string.IsNullOrWhiteSpace(SelectedStack?.ConfigDirectory) && OpenFileBrowserAtPathAsync is not null;
    [RelayCommand(CanExecute = nameof(CanOpenStackSource))]
    private async Task OpenSelectedStackSourceAsync()
    {
        if (SelectedStack?.ConfigDirectory is { Length: > 0 } path && OpenFileBrowserAtPathAsync is not null)
            await OpenFileBrowserAtPathAsync(path);
    }

    private bool HasSelectedStack => SelectedStack is not null && !IsLoading;
    private bool CanDeleteStack => HasSelectedStack;
    partial void OnSelectedStackChanged(DockerStackDto? value)
    {
        Replace(StackServices, []);
        NotifyStackCommands();
        if (value is not null) _ = LoadStackServicesAsync();
    }
    private void NotifyStackCommands()
    {
        LoadStackServicesCommand.NotifyCanExecuteChanged();
        StartStackCommand.NotifyCanExecuteChanged(); StopStackCommand.NotifyCanExecuteChanged(); RestartStackCommand.NotifyCanExecuteChanged();
        EditStackCommand.NotifyCanExecuteChanged(); OpenSelectedStackSourceCommand.NotifyCanExecuteChanged();
        DeleteStackCommand.NotifyCanExecuteChanged();
    }
    private async Task ApplySelectedStackActionAsync(string action, bool confirmed = false)
    {
        var stack = SelectedStack;
        if (stack is null) return;
        Func<DockerStackOperationResult, string> status = result => result.Success
            ? LocalizedText.Format("docker.stack.succeeded", OperationText(action), stack.Name)
            : LocalizedText.Format("docker.stack.failed", OperationText(action), ProblemText(result.ProblemCode));
        if (action is "start" or "stop" or "restart")
        {
            await RunQuietOperationAsync(
                () => client.ApplyStackActionAsync(stack.Name, action, new DockerStackActionRequest(confirmed)),
                result => result.Success,
                status);
            return;
        }
        await RunOperationAsync(
            () => client.ApplyStackActionAsync(stack.Name, action, new DockerStackActionRequest(confirmed)),
            status,
            LocalizedText.Format("docker.operation.stack", OperationText(action), stack.Name));
    }

    [RelayCommand] private Task PullImageAsync() => TryPullImageAsync();

    /// <summary>Queues an image pull and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryPullImageAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ImageReference)) { StatusText = LocalizedText.Get("docker.image.required"); return false; }
        var imageReference = ImageReference.Trim();
        return await RunOperationAsync(
            () => client.PullImageAsync(new DockerImageOperationRequest(imageReference)),
            result => result.Success ? LocalizedText.Format("docker.image.pull_succeeded", imageReference) : LocalizedText.Format("docker.image.pull_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => ImageReference = string.Empty,
            operationName: LocalizedText.Format("docker.operation.pull", imageReference));
    }

    [RelayCommand] private Task CreateContainerAsync() => TryCreateContainerAsync();

    /// <summary>Queues container creation and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryCreateContainerAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ContainerImage)) { StatusText = LocalizedText.Get("docker.container.required"); return false; }
        var name = ContainerName.Trim();
        return await RunOperationAsync(
            () => client.CreateContainerAsync(new DockerContainerCreateRequest(
                name, ContainerImage.Trim(), Lines(ContainerArguments), Lines(ContainerPorts), Lines(ContainerEnvironment), Lines(ContainerMounts), ContainerNetwork, ContainerRestartPolicy)),
            result => result.Success ? LocalizedText.Format("docker.container.created", name) : LocalizedText.Format("docker.container.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () =>
            {
                ContainerName = ContainerImage = ContainerArguments = ContainerPorts = ContainerEnvironment = ContainerMounts = string.Empty;
                ContainerNetwork = "bridge"; ContainerRestartPolicy = "unless-stopped";
            },
            operationName: LocalizedText.Format("docker.operation.create_container", name));
    }

    [RelayCommand(CanExecute = nameof(CanDeleteImage))] private async Task DeleteImageAsync()
    {
        var image = SelectedImage; if (image is null) return;
        if (!await ConfirmDeletionAsync(LocalizedText.Format("docker.image.delete_confirmation", image.Repository))) return;
        await RunOperationAsync(
            () => client.DeleteImageAsync(image.Id, new DockerImageOperationRequest(image.Id, true)),
            result => result.Success ? LocalizedText.Format("docker.image.deleted", image.Repository) : LocalizedText.Format("docker.image.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_image", image.Repository));
    }
    private bool CanDeleteImage => SelectedImage is not null && !IsLoading;
    partial void OnSelectedImageChanged(DockerImageDto? value) => DeleteImageCommand.NotifyCanExecuteChanged();

    [RelayCommand] private Task CreateNetworkAsync() => TryCreateNetworkAsync();

    /// <summary>Queues network creation and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryCreateNetworkAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(NetworkName)) { StatusText = LocalizedText.Get("docker.network.required"); return false; }
        var name = NetworkName.Trim();
        return await RunOperationAsync(
            () => client.CreateNetworkAsync(new DockerNetworkCreateRequest(name, SelectedNetworkDriver)),
            result => result.Success ? LocalizedText.Format("docker.network.created", name) : LocalizedText.Format("docker.network.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => NetworkName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_network", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteNetwork))] private async Task DeleteNetworkAsync()
    {
        var network = SelectedNetwork; if (network is null) return;
        if (!await ConfirmDeletionAsync(LocalizedText.Format("docker.network.delete_confirmation", network.Name))) return;
        await RunOperationAsync(
            () => client.DeleteNetworkAsync(network.Id, true),
            result => result.Success ? LocalizedText.Format("docker.network.deleted", network.Name) : LocalizedText.Format("docker.network.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_network", network.Name));
    }
    private bool CanDeleteNetwork => SelectedNetwork is not null && !IsLoading;
    [RelayCommand(CanExecute = nameof(CanLoadNetworkDetails))]
    private async Task LoadNetworkDetailsAsync()
    {
        var network = SelectedNetwork;
        if (network is null) return;
        ResourceDetailsText = string.Empty;
        await RunQuietReadAsync(LocalizedText.Get("docker.resource.details"), async () =>
        {
            var details = await client.GetNetworkAsync(network.Id);
            if (details is null) return false;

            ResourceDetailsTitle = LocalizedText.Format("docker.resource.network_details", network.Name);
            ResourceDetailsText = FormatNetworkDetails(details);
            StatusText = LocalizedText.Format("docker.resource.details_loaded", network.Name);
            return true;
        }, LocalizedText.Format("docker.resource.details_unavailable", network.Name));
        if (!string.IsNullOrWhiteSpace(ResourceDetailsText) && ShowResourceDetailsAsync is not null)
            await ShowResourceDetailsAsync();
    }
    private bool CanLoadNetworkDetails => SelectedNetwork is not null && !IsLoading;
    partial void OnSelectedNetworkChanged(DockerNetworkDto? value)
    {
        ResourceDetailsText = string.Empty;
        DeleteNetworkCommand.NotifyCanExecuteChanged();
        LoadNetworkDetailsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private Task CreateVolumeAsync() => TryCreateVolumeAsync();

    /// <summary>Queues volume creation and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryCreateVolumeAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(VolumeName)) { StatusText = LocalizedText.Get("docker.volume.required"); return false; }
        var name = VolumeName.Trim();
        return await RunOperationAsync(
            () => client.CreateVolumeAsync(new DockerVolumeCreateRequest(name, SelectedVolumeDriver)),
            result => result.Success ? LocalizedText.Format("docker.volume.created", name) : LocalizedText.Format("docker.volume.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => VolumeName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_volume", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteVolume))] private async Task DeleteVolumeAsync()
    {
        var volume = SelectedVolume; if (volume is null) return;
        if (!await ConfirmDeletionAsync(LocalizedText.Format("docker.volume.delete_confirmation", volume.Name))) return;
        await RunOperationAsync(
            () => client.DeleteVolumeAsync(volume.Name, true),
            result => result.Success ? LocalizedText.Format("docker.volume.deleted", volume.Name) : LocalizedText.Format("docker.volume.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_volume", volume.Name));
    }
    private bool CanDeleteVolume => SelectedVolume is not null && !IsLoading;
    [RelayCommand(CanExecute = nameof(CanLoadVolumeDetails))]
    private async Task LoadVolumeDetailsAsync()
    {
        var volume = SelectedVolume;
        if (volume is null) return;
        ResourceDetailsText = string.Empty;
        await RunQuietReadAsync(LocalizedText.Get("docker.resource.details"), async () =>
        {
            var details = await client.GetVolumeAsync(volume.Name);
            if (details is null) return false;

            ResourceDetailsTitle = LocalizedText.Format("docker.resource.volume_details", volume.Name);
            ResourceDetailsText = FormatVolumeDetails(details);
            StatusText = LocalizedText.Format("docker.resource.details_loaded", volume.Name);
            return true;
        }, LocalizedText.Format("docker.resource.details_unavailable", volume.Name));
        if (!string.IsNullOrWhiteSpace(ResourceDetailsText) && ShowResourceDetailsAsync is not null)
            await ShowResourceDetailsAsync();
    }
    private bool CanLoadVolumeDetails => SelectedVolume is not null && !IsLoading;
    partial void OnSelectedVolumeChanged(DockerVolumeDto? value)
    {
        ResourceDetailsText = string.Empty;
        DeleteVolumeCommand.NotifyCanExecuteChanged();
        LoadVolumeDetailsCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> ConfirmDeletionAsync(string message) =>
        RequestDeletionConfirmationAsync is not null && await RequestDeletionConfirmationAsync(message);

    private async Task<bool> RunOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, string> status, Action? onSuccess = null, string? operationName = null)
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return false;
        IsLoading = true;
        BeginOperation(operationName);
        _ = CompleteOperationAsync(operation, status, onSuccess);
        return true;
    }
    private async Task CompleteOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, string> status, Action? onSuccess)
    {
        try
        {
            var result = await operation();
            StatusText = status(result);
            AppendOperationLog(result.LogLines);
            CompleteOperation(StatusText);
            if (result.Success) onSuccess?.Invoke();
            else await ShowUnavailableForProblemAsync(result.ProblemCode);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
        await RefreshAsync();
    }
    private async Task<bool> RunOperationAsync(Func<Task<DockerStackOperationResult>> operation, Func<DockerStackOperationResult, string> status, string? operationName = null)
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return false;
        IsLoading = true;
        BeginOperation(operationName);
        _ = CompleteStackOperationAsync(operation, status);
        return true;
    }
    private async Task CompleteStackOperationAsync(Func<Task<DockerStackOperationResult>> operation, Func<DockerStackOperationResult, string> status)
    {
        try
        {
            var result = await operation(); StatusText = status(result);
            AppendOperationLog(result.Messages);
            CompleteOperation(StatusText);
            if (!result.Success) await ShowUnavailableForProblemAsync(result.ProblemCode);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
        await RefreshAsync();
    }
    private async Task RunQuietOperationAsync<TResult>(Func<Task<TResult>> operation, Func<TResult, bool> isSuccess, Func<TResult, string> status)
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return;
        IsLoading = true;
        try
        {
            var result = await operation();
            StatusText = status(result);
            if (!isSuccess(result)) await ShowErrorAsync(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.status.failed", exception.Message);
            await ShowErrorAsync(StatusText);
        }
        finally { IsLoading = false; }
        await RefreshAsync();
    }
    private async Task RunQuietReadAsync(string operationName, Func<Task<bool>> operation, string? unavailableDetail = null)
    {
        if (!await EnsureDockerAvailableAsync()) return;
        IsLoading = true;
        try
        {
            if (await operation()) return;
            StatusText = LocalizedText.Format("docker.action.failed", operationName, unavailableDetail ?? LocalizedText.Get("docker.read_error.not_found"));
            await ShowErrorAsync(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Format("docker.action.failed", operationName, exception.Message);
            await ShowErrorAsync(StatusText);
        }
        finally { IsLoading = false; }
    }
    private Task ShowErrorAsync(string message) => ShowErrorDialogAsync?.Invoke(message) ?? Task.CompletedTask;
    private async Task<bool> EnsureDockerAvailableAsync()
    {
        if (IsDockerAvailable) return true;
        StatusText = LocalizedText.Get("docker.status.unavailable_operation");
        await ShowDockerUnavailableDialogAsync();
        return false;
    }
    private async Task ShowUnavailableForProblemAsync(string problemCode)
    {
        if (problemCode is not ("docker.unavailable" or "docker.not_installed" or "docker.api_incompatible")) return;
        IsDockerAvailable = false;
        IsDockerInstallRequired = IsInstallRequired(false, problemCode);
        StatusText = LocalizedText.Format("docker.status.unavailable", problemCode);
        await ShowDockerUnavailableDialogAsync();
    }
    private async Task ShowUnavailableForExceptionAsync()
    {
        try
        {
            var status = await client.GetStatusAsync();
            if (status.IsAvailable) return;
            IsDockerAvailable = false;
            IsDockerInstallRequired = IsInstallRequired(false, status.ProblemCode);
            StatusText = LocalizedText.Format("docker.status.unavailable", status.ProblemCode);
        }
        catch
        {
            IsDockerAvailable = false;
            IsDockerInstallRequired = false;
            StatusText = LocalizedText.Get("docker.status.unavailable_operation");
        }
        await ShowDockerUnavailableDialogAsync();
    }
    private async Task ShowDockerUnavailableDialogAsync()
    {
        if (_isUnavailableDialogShowing || ShowDockerUnavailableAsync is null) return;
        _isUnavailableDialogShowing = true;
        try { await ShowDockerUnavailableAsync(); }
        finally { _isUnavailableDialogShowing = false; }
    }
    private static bool IsInstallRequired(bool isAvailable, string? problemCode) =>
        !isAvailable && string.Equals(problemCode, "docker.not_installed", StringComparison.OrdinalIgnoreCase);
    private static string OperationText(string operation) => operation switch
    {
        "validate" => LocalizedText.Get("docker.stack.validate"),
        "deploy" => LocalizedText.Get("docker.stack.deploy"),
        "logs" => LocalizedText.Get("docker.container.logs"),
        "stats" => LocalizedText.Get("docker.container.stats"),
        "delete" => LocalizedText.Get("common.delete"),
        _ => LocalizedText.Get($"docker.action.{operation}"),
    };
    private void BeginOperation(string? operationName)
    {
        OperationTitle = string.IsNullOrWhiteSpace(operationName) ? LocalizedText.Get("docker.operation.running") : operationName;
        OperationLog = LocalizedText.Format("docker.operation.started", OperationTitle);
        IsOperationRunning = true;
        OnPropertyChanged(nameof(HasOperationActivity));
        StatusText = LocalizedText.Format("docker.operation.running", OperationTitle);
    }
    [RelayCommand]
    private void CloseOperationActivity()
    {
        // Closing the panel only dismisses its local diagnostic output. The Docker request,
        // including an operation still in progress, continues independently.
        OperationTitle = string.Empty;
        OperationLog = string.Empty;
        OnPropertyChanged(nameof(HasOperationActivity));
    }
    private void AppendOperationLog(IEnumerable<string>? lines)
    {
        if (lines is null) return;
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (values.Length == 0) return;
        OperationLog = string.Join(Environment.NewLine, new[] { OperationLog }.Concat(values));
    }
    private void CompleteOperation(string outcome) =>
        OperationLog = string.Join(Environment.NewLine, new[] { OperationLog, LocalizedText.Format("docker.operation.finished", outcome) });
    private static string ProblemText(string problemCode) => problemCode switch
    {
        "docker.operation_timeout" => LocalizedText.Get("docker.problem.timeout"),
        "docker.operation_failed" => LocalizedText.Get("docker.problem.failed"),
        "docker.stack_no_services" => LocalizedText.Get("docker.problem.stack_no_services"),
        "docker.stack_source_unavailable" => LocalizedText.Get("docker.stack.source_unavailable"),
        _ => problemCode
    };
    private static IReadOnlyList<string> Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string FormatContainerDetails(DockerContainerDetailsDto details) => string.Join(Environment.NewLine, new[]
    {
        $"{LocalizedText.Get("docker.container.name")}: {details.Name}",
        $"{LocalizedText.Get("docker.container.id")}: {details.Id}",
        $"{LocalizedText.Get("docker.container.image")}: {details.Image}",
        $"{LocalizedText.Get("docker.table.state")}: {details.State}",
        $"{LocalizedText.Get("docker.table.status")}: {details.Status}",
        $"{LocalizedText.Get("docker.container.created_at")}: {details.Created}",
        $"{LocalizedText.Get("docker.container.restart")}: {details.RestartPolicy}",
        $"{LocalizedText.Get("docker.container.working_directory")}: {details.WorkingDirectory}",
        $"{LocalizedText.Get("docker.container.command")}: {details.Command}",
        FormatSection(LocalizedText.Get("docker.container.ports"), details.Ports),
        FormatSection(LocalizedText.Get("docker.container.mounts"), details.Mounts),
        FormatSection(LocalizedText.Get("docker.container.networks"), details.Networks),
        FormatSection(LocalizedText.Get("docker.container.environment"), details.Environment),
        FormatSection(LocalizedText.Get("docker.container.labels"), details.Labels.Select(label => $"{label.Key}={label.Value}"))
    });
    private static string FormatNetworkDetails(DockerNetworkDetailsDto details) => string.Join(Environment.NewLine, new[]
    {
        $"{LocalizedText.Get("docker.table.name")}: {details.Name}",
        $"{LocalizedText.Get("docker.container.id")}: {details.Id}",
        $"{LocalizedText.Get("docker.network.driver")}: {details.Driver}",
        $"{LocalizedText.Get("docker.table.scope")}: {details.Scope}",
        FormatSection(LocalizedText.Get("docker.resource.attached_containers"), details.Containers)
    });
    private static string FormatVolumeDetails(DockerVolumeDetailsDto details) => string.Join(Environment.NewLine, new[]
    {
        $"{LocalizedText.Get("docker.table.name")}: {details.Name}",
        $"{LocalizedText.Get("docker.volume.driver")}: {details.Driver}",
        $"{LocalizedText.Get("docker.table.mount_point")}: {details.Mountpoint}",
        FormatSection(LocalizedText.Get("docker.container.labels"), details.Labels.Select(label => $"{label.Key}={label.Value}"))
    });
    private static string FormatSection(string heading, IEnumerable<string> values) => $"{heading}:{Environment.NewLine}{string.Join(Environment.NewLine, values)}";
}
