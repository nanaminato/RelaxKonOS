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
    /// <summary>Recent operations of the selected project, newest first. The durable record is the
    /// authority, so this list is re-read from the server instead of being kept from live events.</summary>
    public ObservableCollection<StackOperationRow> StackOperations { get; } = [];
    public ObservableCollection<string> AvailableNetworks { get; } = ["bridge"];

    // Docker's built-in drivers that can create a user-defined network. Host and none are
    // built-in special networks, rather than choices for `docker network create`.
    public IReadOnlyList<string> NetworkDrivers { get; } = ["bridge", "ipvlan", "macvlan", "overlay"];
    public IReadOnlyList<string> VolumeDrivers { get; } = ["local"];
    public IReadOnlyList<string> RestartPolicies { get; } = ["no", "always", "unless-stopped", "on-failure"];

    [ObservableProperty] private LocalizedStatus _statusText;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOperationRunning;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationActivity))]
    private string _operationTitle = string.Empty;
    [ObservableProperty] private string _operationLog = string.Empty;
    [ObservableProperty] private LocalizedStatus _operationStatus;
    [ObservableProperty] private bool _isOperationLogExpanded;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopEngineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestartEngineCommand))]
    private bool _isDockerAvailable;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContainerLogs))]
    private string _containerLogs = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContainerStats))]
    private string _containerStats = string.Empty;
    public bool HasContainerLogs => !string.IsNullOrWhiteSpace(ContainerLogs);
    public bool HasContainerStats => !string.IsNullOrWhiteSpace(ContainerStats);
    [ObservableProperty] private DockerStackDto? _selectedStack;
    [ObservableProperty] private string _stackName = string.Empty;
    [ObservableProperty] private string _composeYaml = string.Empty;
    /// <summary>What the server's Compose parser resolved for the current draft. Empty until a preview runs.</summary>
    [ObservableProperty] private string _stackPreviewText = string.Empty;
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
    /// <summary>Assigned by the app shell to open the server Docker installation flow.</summary>
    public Func<Task>? OpenDockerInstallationAsync { get; set; }
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
                ? LocalizedText.Ref("docker.status.available", status.ServerVersion ?? "", status.OperatingSystem ?? "")
                : LocalizedText.Ref("docker.status.unavailable", status.ProblemCode);
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
            StatusText = LocalizedText.Ref("docker.status.failed", exception.Message);
        }
        finally { IsLoading = false; }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear(); foreach (var value in values) target.Add(value);
    }

    /// <summary>
    /// Asked before a stop or restart, which terminates every running container on the host. The
    /// manager has no window of its own, so the shell supplies the dialog.
    /// </summary>
    public Func<string, Task<bool>>? RequestEngineConfirmationAsync { get; set; }

    [ObservableProperty] private bool _isEngineActionRunning;
    [ObservableProperty] private LocalizedStatus _engineActionText;

    /// <summary>A stopped engine can be started; only a reachable one can be stopped or restarted.</summary>
    private bool CanStartEngine => !IsEngineActionRunning;
    private bool CanStopEngine => !IsEngineActionRunning && IsDockerAvailable;

    [RelayCommand(CanExecute = nameof(CanStartEngine))] private Task StartEngineAsync() => ApplyEngineActionAsync(DockerEngineAction.Start);
    [RelayCommand(CanExecute = nameof(CanStopEngine))] private Task StopEngineAsync() => ApplyEngineActionAsync(DockerEngineAction.Stop);
    [RelayCommand(CanExecute = nameof(CanStopEngine))] private Task RestartEngineAsync() => ApplyEngineActionAsync(DockerEngineAction.Restart);

    /// <summary>
    /// Engine control is host-wide and interrupts every running container, so stop and restart are
    /// confirmed first and the whole manager is re-read afterwards: the point of the action is the
    /// engine's resulting state rather than the command's exit code.
    /// </summary>
    private async Task ApplyEngineActionAsync(DockerEngineAction action)
    {
        if (IsEngineActionRunning) return;
        var segment = DockerEngineActionRoutes.Segment(action);
        if (action != DockerEngineAction.Start && RequestEngineConfirmationAsync is not null
            && !await RequestEngineConfirmationAsync(LocalizedText.Get($"docker.engine.confirm.{segment}")))
        {
            EngineActionText = LocalizedText.Ref("docker.engine.status.cancelled");
            return;
        }

        IsEngineActionRunning = true;
        EngineActionText = LocalizedText.Ref($"docker.engine.status.{segment}");
        try
        {
            var result = await client.ApplyEngineActionAsync(action, confirmed: true);
            EngineActionText = result.Success
                ? LocalizedText.Ref("docker.engine.status.completed")
                : LocalizedText.Ref(result.ProblemCode);
        }
        catch (Exception exception)
        {
            EngineActionText = LocalizedText.Ref("docker.engine.status.failed", exception.Message);
        }
        finally
        {
            IsEngineActionRunning = false;
            await RefreshAsync();
        }
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
            StatusText = LocalizedText.Ref("docker.action.succeeded", OperationText("logs"), container.Names);
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
            StatusText = LocalizedText.Ref("docker.action.succeeded", OperationText("stats"), container.Names);
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
    partial void OnIsDockerInstallRequiredChanged(bool value) => OpenInstallationCommand.NotifyCanExecuteChanged();

    private bool CanOpenInstallation => IsDockerInstallRequired && OpenDockerInstallationAsync is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInstallation))]
    private async Task OpenInstallationAsync()
    {
        if (OpenDockerInstallationAsync is not null)
            await OpenDockerInstallationAsync();
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
            StatusText = LocalizedText.Ref("docker.container.details_loaded", container.Names);
            return true;
        });
        if (ContainerDetails is not null && ShowContainerDetailsAsync is not null)
            await ShowContainerDetailsAsync();
    }
    private async Task ApplyContainerActionAsync(string action, bool confirmed = false)
    {
        var container = SelectedContainer; if (container is null) return;
        Func<DockerOperationResult, LocalizedStatus> status = result => result.Success
            ? LocalizedText.Ref("docker.action.succeeded", OperationText(action), container.Names)
            : LocalizedText.Ref("docker.action.failed", OperationText(action), ProblemText(result.ProblemCode));
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
            result => result.Success ? LocalizedText.Ref("docker.container.updated", name) : LocalizedText.Ref("docker.container.update_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.update_container", container.Names));
    }

    [RelayCommand] private Task ValidateStackAsync() => PreviewStackAsync();

    /// <summary>
    /// Parses the definition with the server's Compose parser and reports what it would run. Nothing is
    /// applied, so the operator reviews the same document that a deployment will later carry.
    /// </summary>
    private async Task PreviewStackAsync()
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return;
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Ref("docker.stack.required"); return; }
        var name = StackName.Trim();
        await RunQuietReadAsync(LocalizedText.Get("docker.stack.validate"), async () =>
        {
            var preview = await client.PreviewStackAsync(new DockerStackDefinitionDto(name, ComposeYaml));
            StackPreviewText = FormatPreview(preview);
            StatusText = LocalizedText.Ref("docker.stack.preview_succeeded", name, preview.Services.Count, preview.Volumes.Count, preview.Networks.Count);
            return true;
        }, LocalizedText.Get("docker.stack.preview_unavailable"));
    }

    /// <summary>Queues a Compose deployment and reports whether its dialog can close immediately.</summary>
    public async Task<bool> TryDeployStackAsync()
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return false;
        if (string.IsNullOrWhiteSpace(StackName) || string.IsNullOrWhiteSpace(ComposeYaml)) { StatusText = LocalizedText.Ref("docker.stack.required"); return false; }
        var name = StackName.Trim();
        var title = LocalizedText.Format("docker.operation.stack", OperationText("deploy"), name);
        IsLoading = true;
        BeginOperation(title);
        _ = DeployStackCoreAsync(name);
        return true;
    }

    private async Task DeployStackCoreAsync(string name)
    {
        try
        {
            // The deployment carries the identity of the exact document the operator approved, so an
            // approval given for a different file is refused instead of applied. The parse is repeated
            // here rather than reusing an earlier click's answer.
            var definition = new DockerStackDefinitionDto(name, ComposeYaml);
            var preview = await client.PreviewStackAsync(definition);
            AppendOperationLog(Lines(FormatPreview(preview)));
            var operation = await client.DeployStackAsync(
                new DockerStackDeployRequest(definition, preview.DefinitionVersion),
                StackKey(DockerStackOperationKind.Deploy, name, preview.DefinitionVersion));
            await TrackStackOperationAsync(operation);
        }
        catch (Exception exception) { await FailStackOperationAsync(exception); }
        finally
        {
            CompleteOperation(StatusText);
            IsOperationRunning = false;
            IsLoading = false;
            await RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedStack))]
    private async Task LoadStackServicesAsync()
    {
        var stack = SelectedStack;
        if (stack is null) return;
        await RunQuietReadAsync(LocalizedText.Get("docker.stack.services"), async () =>
        {
            Replace(StackServices, await client.ListStackServicesAsync(stack.Name));
            Replace(StackOperations, (await client.ListStackOperationsAsync(stack.Name)).Select(operation => new StackOperationRow(operation)));
            StatusText = LocalizedText.Ref("docker.stack.services_loaded", stack.Name, StackServices.Count);
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
            StatusText = LocalizedText.Ref("docker.stack.source_loaded", stack.Name);
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
        if (stack is null || !DockerStackActionRoutes.TryParseAction(action, out var kind)) return;
        if (IsLoading || !await EnsureDockerAvailableAsync()) return;
        var title = LocalizedText.Format("docker.operation.stack", OperationText(action), stack.Name);
        IsLoading = true;
        BeginOperation(title);
        _ = ApplyStackActionCoreAsync(stack.Name, kind, confirmed);
    }

    private async Task ApplyStackActionCoreAsync(string name, DockerStackOperationKind kind, bool confirmed)
    {
        try
        {
            var operation = await client.ApplyStackActionAsync(name, kind, new DockerStackActionRequest(confirmed),
                StackKey(kind, name, confirmed.ToString()));
            await TrackStackOperationAsync(operation);
        }
        catch (Exception exception) { await FailStackOperationAsync(exception); }
        finally
        {
            CompleteOperation(StatusText);
            IsOperationRunning = false;
            IsLoading = false;
            await RefreshAsync();
        }
    }

    [RelayCommand] private Task PullImageAsync() => TryPullImageAsync();

    /// <summary>Queues an image pull and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryPullImageAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ImageReference)) { StatusText = LocalizedText.Ref("docker.image.required"); return false; }
        var imageReference = ImageReference.Trim();
        return await RunOperationAsync(
            () => client.PullImageAsync(new DockerImageOperationRequest(imageReference)),
            result => result.Success ? LocalizedText.Ref("docker.image.pull_succeeded", imageReference) : LocalizedText.Ref("docker.image.pull_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => ImageReference = string.Empty,
            operationName: LocalizedText.Format("docker.operation.pull", imageReference));
    }

    [RelayCommand] private Task CreateContainerAsync() => TryCreateContainerAsync();

    /// <summary>Queues container creation and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryCreateContainerAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(ContainerName) || string.IsNullOrWhiteSpace(ContainerImage)) { StatusText = LocalizedText.Ref("docker.container.required"); return false; }
        var name = ContainerName.Trim();
        return await RunOperationAsync(
            () => client.CreateContainerAsync(new DockerContainerCreateRequest(
                name, ContainerImage.Trim(), Lines(ContainerArguments), Lines(ContainerPorts), Lines(ContainerEnvironment), Lines(ContainerMounts), ContainerNetwork, ContainerRestartPolicy)),
            result => result.Success ? LocalizedText.Ref("docker.container.created", name) : LocalizedText.Ref("docker.container.create_failed", ProblemText(result.ProblemCode)),
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
            result => result.Success ? LocalizedText.Ref("docker.image.deleted", image.Repository) : LocalizedText.Ref("docker.image.delete_failed", ProblemText(result.ProblemCode)),
            operationName: LocalizedText.Format("docker.operation.delete_image", image.Repository));
    }
    private bool CanDeleteImage => SelectedImage is not null && !IsLoading;
    partial void OnSelectedImageChanged(DockerImageDto? value) => DeleteImageCommand.NotifyCanExecuteChanged();

    [RelayCommand] private Task CreateNetworkAsync() => TryCreateNetworkAsync();

    /// <summary>Queues network creation and reports whether the dialog can close immediately.</summary>
    public async Task<bool> TryCreateNetworkAsync()
    {
        if (IsLoading) return false;
        if (string.IsNullOrWhiteSpace(NetworkName)) { StatusText = LocalizedText.Ref("docker.network.required"); return false; }
        var name = NetworkName.Trim();
        return await RunOperationAsync(
            () => client.CreateNetworkAsync(new DockerNetworkCreateRequest(name, SelectedNetworkDriver)),
            result => result.Success ? LocalizedText.Ref("docker.network.created", name) : LocalizedText.Ref("docker.network.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => NetworkName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_network", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteNetwork))] private async Task DeleteNetworkAsync()
    {
        var network = SelectedNetwork; if (network is null) return;
        if (!await ConfirmDeletionAsync(LocalizedText.Format("docker.network.delete_confirmation", network.Name))) return;
        await RunOperationAsync(
            () => client.DeleteNetworkAsync(network.Id, true),
            result => result.Success ? LocalizedText.Ref("docker.network.deleted", network.Name) : LocalizedText.Ref("docker.network.delete_failed", ProblemText(result.ProblemCode)),
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
            StatusText = LocalizedText.Ref("docker.resource.details_loaded", network.Name);
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
        if (string.IsNullOrWhiteSpace(VolumeName)) { StatusText = LocalizedText.Ref("docker.volume.required"); return false; }
        var name = VolumeName.Trim();
        return await RunOperationAsync(
            () => client.CreateVolumeAsync(new DockerVolumeCreateRequest(name, SelectedVolumeDriver)),
            result => result.Success ? LocalizedText.Ref("docker.volume.created", name) : LocalizedText.Ref("docker.volume.create_failed", ProblemText(result.ProblemCode)),
            onSuccess: () => VolumeName = string.Empty,
            operationName: LocalizedText.Format("docker.operation.create_volume", name));
    }
    [RelayCommand(CanExecute = nameof(CanDeleteVolume))] private async Task DeleteVolumeAsync()
    {
        var volume = SelectedVolume; if (volume is null) return;
        if (!await ConfirmDeletionAsync(LocalizedText.Format("docker.volume.delete_confirmation", volume.Name))) return;
        await RunOperationAsync(
            () => client.DeleteVolumeAsync(volume.Name, true),
            result => result.Success ? LocalizedText.Ref("docker.volume.deleted", volume.Name) : LocalizedText.Ref("docker.volume.delete_failed", ProblemText(result.ProblemCode)),
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
            StatusText = LocalizedText.Ref("docker.resource.details_loaded", volume.Name);
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

    private async Task<bool> RunOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, LocalizedStatus> status, Action? onSuccess = null, string? operationName = null)
    {
        if (IsLoading || !await EnsureDockerAvailableAsync()) return false;
        IsLoading = true;
        BeginOperation(operationName);
        _ = CompleteOperationAsync(operation, status, onSuccess);
        return true;
    }
    private async Task CompleteOperationAsync(Func<Task<DockerOperationResult>> operation, Func<DockerOperationResult, LocalizedStatus> status, Action? onSuccess)
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
            StatusText = LocalizedText.Ref("docker.status.failed", exception.Message);
            AppendOperationLog([exception.Message]);
            CompleteOperation(StatusText);
            await ShowUnavailableForExceptionAsync();
        }
        finally { IsOperationRunning = false; IsLoading = false; }
        await RefreshAsync();
    }
    /// <summary>
    /// Watches a durable operation until it reaches a terminal state. The ledger, not this loop, is the
    /// authority: keep showing progress until a terminal state is observed; an operation
    /// that outlives the window is still readable from its own record.
    /// </summary>
    private async Task TrackStackOperationAsync(DockerStackOperationDto operation)
    {
        var current = operation;
        var action = OperationText(DockerStackActionRoutes.Segment(current.Kind));
        AppendOperationLog([LocalizedText.Format("docker.stack.operation_queued", current.OperationId.ToString("D"))]);
        var lastStage = string.Empty;
        var operationPrefix = OperationLog;
        var diagnosticSnapshot = string.Empty;
        while (current.State is DockerStackOperationState.Queued or DockerStackOperationState.Running)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            current = await client.GetStackOperationAsync(current.OperationId) ?? current;
            StatusText = LocalizedText.Ref("docker.stack.operation_running", action, current.ProjectName, current.Stage.ToString());
            OperationStatus = StatusText;
            if (lastStage != current.Stage.ToString())
            {
                lastStage = current.Stage.ToString();
                operationPrefix += Environment.NewLine + $"[{DateTime.Now:HH:mm:ss}] {StatusText}";
            }
            try
            {
                var live = await client.GetStackOperationDiagnosticsAsync(current.OperationId);
                if (live is not null) diagnosticSnapshot = string.Join(Environment.NewLine, live.Lines);
            }
            catch (Exception) { /* A diagnostic read must not interrupt tracking the operation. */ }
            OperationLog = string.Join(Environment.NewLine, new[] { operationPrefix, diagnosticSnapshot }.Where(value => !string.IsNullOrEmpty(value)));
        }

        try
        {
            var diagnostics = await client.GetStackOperationDiagnosticsAsync(current.OperationId);
            if (diagnostics is not null)
                OperationLog = string.Join(Environment.NewLine, new[] { operationPrefix }.Concat(diagnostics.Lines));
        }
        catch (Exception exception) { AppendOperationLog([exception.Message]); }

        // The observed services are the outcome. A partial failure is exactly the case where the
        // operator has to choose between retrying, stopping, or removing the project.
        AppendOperationLog(ServiceOutcomeLines(current));
        StatusText = LocalizedText.Ref(OutcomeKey(current.State), action, current.ProjectName);
        if (current.State is DockerStackOperationState.Failed) await ShowUnavailableForProblemAsync(current.ProblemCode ?? string.Empty);
    }

    private async Task FailStackOperationAsync(Exception exception)
    {
        StatusText = LocalizedText.Ref("docker.status.failed", exception.Message);
        AppendOperationLog([exception.Message]);
        if (exception is DockerStackRequestException refusal) await ShowUnavailableForProblemAsync(refusal.ProblemCode);
        else await ShowUnavailableForExceptionAsync();
    }

    private static string OutcomeKey(DockerStackOperationState state) => state switch
    {
        DockerStackOperationState.Succeeded => "docker.stack.succeeded",
        DockerStackOperationState.PartialFailed => "docker.stack.partial_failed",
        DockerStackOperationState.Interrupted => "docker.stack.interrupted",
        DockerStackOperationState.Cancelled => "docker.stack.cancelled",
        _ => "docker.stack.failed",
    };

    private static IEnumerable<string> ServiceOutcomeLines(DockerStackOperationDto operation) =>
        operation.Services.Select(service => $"{service.Service} · {service.State} · {service.Status}")
            .Prepend($"{LocalizedText.Get("docker.stack.observed_services")}:");

    private static string FormatPreview(DockerStackPreviewDto preview) => string.Join(Environment.NewLine, new[]
    {
        $"{LocalizedText.Get("docker.stack.preview_version")}: {preview.DefinitionVersion[..12]}",
        $"{LocalizedText.Get("docker.stack.services")}: {string.Join(", ", preview.Services.Select(service => service.Image.Length > 0 ? $"{service.Service} ({service.Image})" : service.Service))}",
        $"{LocalizedText.Get("docker.stack.preview_volumes")}: {string.Join(", ", preview.Volumes)}",
        $"{LocalizedText.Get("docker.stack.preview_networks")}: {string.Join(", ", preview.Networks)}",
    });

    /// <summary>
    /// The idempotency key is reused only for the immediately preceding identical request. Reusing it for
    /// a changed request would be rejected as a conflict, and reusing it later would be meaningless.
    /// </summary>
    private string StackKey(DockerStackOperationKind kind, string name, string reference)
    {
        var request = $"{kind}|{name}|{reference}";
        if (!string.Equals(request, _lastStackRequest, StringComparison.Ordinal))
        {
            _lastStackRequest = request;
            _lastStackKey = Guid.NewGuid().ToString("N");
        }
        return _lastStackKey!;
    }
    private string? _lastStackRequest;
    private string? _lastStackKey;

    private async Task RunQuietOperationAsync<TResult>(Func<Task<TResult>> operation, Func<TResult, bool> isSuccess, Func<TResult, LocalizedStatus> status)
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
            StatusText = LocalizedText.Ref("docker.status.failed", exception.Message);
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
            StatusText = LocalizedText.Ref("docker.action.failed", operationName, unavailableDetail ?? LocalizedText.Get("docker.read_error.not_found"));
            await ShowErrorAsync(StatusText);
        }
        catch (Exception exception)
        {
            StatusText = LocalizedText.Ref("docker.action.failed", operationName, exception.Message);
            await ShowErrorAsync(StatusText);
        }
        finally { IsLoading = false; }
    }
    private Task ShowErrorAsync(string message) => ShowErrorDialogAsync?.Invoke(message) ?? Task.CompletedTask;
    private async Task<bool> EnsureDockerAvailableAsync()
    {
        if (IsDockerAvailable) return true;
        StatusText = LocalizedText.Ref("docker.status.unavailable_operation");
        await ShowDockerUnavailableDialogAsync();
        return false;
    }
    private async Task ShowUnavailableForProblemAsync(string problemCode)
    {
        if (problemCode is not ("docker.unavailable" or "docker.not_installed" or "docker.api_incompatible")) return;
        IsDockerAvailable = false;
        IsDockerInstallRequired = IsInstallRequired(false, problemCode);
        StatusText = LocalizedText.Ref("docker.status.unavailable", problemCode);
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
            StatusText = LocalizedText.Ref("docker.status.unavailable", status.ProblemCode);
        }
        catch
        {
            IsDockerAvailable = false;
            IsDockerInstallRequired = false;
            StatusText = LocalizedText.Ref("docker.status.unavailable_operation");
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

    /// <summary>Display text of a durable operation's kind, shared by the history grid and the messages.</summary>
    internal static string OperationTextFor(DockerStackOperationKind kind) => kind == DockerStackOperationKind.Deploy
        ? LocalizedText.Get("docker.stack.deploy")
        : kind == DockerStackOperationKind.Delete
            ? LocalizedText.Get("common.delete")
            : OperationText(DockerStackActionRoutes.Segment(kind));

    /// <summary>Display text of a durable operation's state.</summary>
    internal static string StateText(DockerStackOperationState state) => LocalizedText.Get(state switch
    {
        DockerStackOperationState.Queued => "docker.stack.state.queued",
        DockerStackOperationState.Running => "docker.stack.state.running",
        DockerStackOperationState.Succeeded => "docker.stack.state.succeeded",
        DockerStackOperationState.PartialFailed => "docker.stack.state.partial_failed",
        DockerStackOperationState.Failed => "docker.stack.state.failed",
        DockerStackOperationState.Cancelled => "docker.stack.state.cancelled",
        _ => "docker.stack.state.interrupted",
    });
    private void BeginOperation(string? operationName)
    {
        OperationTitle = string.IsNullOrWhiteSpace(operationName) ? LocalizedText.Get("docker.operation.reading") : operationName;
        OperationLog = LocalizedText.Format("docker.operation.started", OperationTitle);
        OperationStatus = LocalizedText.Ref("docker.operation.running_label");
        IsOperationRunning = true;
        StatusText = LocalizedText.Ref("docker.operation.running", OperationTitle);
    }
    [RelayCommand]
    private void CloseOperationActivity()
    {
        if (IsOperationRunning) return;
        OperationTitle = string.Empty;
        OperationLog = string.Empty;
    }
    private void AppendOperationLog(IEnumerable<string>? lines)
    {
        if (lines is null) return;
        var values = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (values.Length == 0) return;
        OperationLog = string.Join(Environment.NewLine, new[] { OperationLog }.Concat(values).SelectMany(value => value.Split(Environment.NewLine)).TakeLast(500));
    }
    private void CompleteOperation(string outcome)
    {
        OperationStatus = outcome;
        AppendOperationLog([LocalizedText.Format("docker.operation.finished", outcome)]);
    }
    /// <summary>
    /// The sentence for a Docker problem code. A refusal from the Compose domain is something an
    /// operator has to be able to act on, so every code this client can provoke is named here rather
    /// than being printed as the raw protocol identifier.
    /// </summary>
    private static string ProblemText(string problemCode) => problemCode switch
    {
        "docker.operation_timeout" => LocalizedText.Get("docker.problem.timeout"),
        "docker.operation_failed" or "docker.compose_failed" => LocalizedText.Get("docker.problem.failed"),
        "docker.stack_no_services" => LocalizedText.Get("docker.problem.stack_no_services"),
        "docker.stack_source_unavailable" => LocalizedText.Get("docker.stack.source_unavailable"),
        "docker.volume_in_use" => LocalizedText.Get("docker.volume.in_use"),
        "docker.stack_invalid_name" => LocalizedText.Get("docker.problem.stack_invalid_name"),
        "docker.stack_invalid_compose" => LocalizedText.Get("docker.problem.stack_invalid_compose"),
        "docker.compose_feature_unsupported" => LocalizedText.Get("docker.problem.compose_feature_unsupported"),
        "docker.compose_variable_unresolved" => LocalizedText.Get("docker.problem.compose_variable_unresolved"),
        "docker.stack_definition_changed" => LocalizedText.Get("docker.problem.stack_definition_changed"),
        "docker.confirmation_required" => LocalizedText.Get("docker.problem.confirmation_required"),
        "docker.stack_not_found" => LocalizedText.Get("docker.problem.stack_not_found"),
        "docker.stack_operation_conflict" => LocalizedText.Get("docker.problem.stack_operation_conflict"),
        "docker.stack_idempotency_required" => LocalizedText.Get("docker.problem.stack_idempotency_required"),
        "docker.stack_idempotency_conflict" => LocalizedText.Get("docker.problem.stack_idempotency_conflict"),
        "docker.stack_not_cancellable" => LocalizedText.Get("docker.problem.stack_not_cancellable"),
        "docker.stack_store_unavailable" => LocalizedText.Get("docker.problem.stack_store_unavailable"),
        "docker.unavailable" => LocalizedText.Get("docker.problem.engine_unavailable"),
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
        FormatSection(LocalizedText.Get("docker.resource.attached_containers"), details.Containers),
        FormatSection(LocalizedText.Get("docker.container.labels"), details.Labels.Select(label => $"{label.Key}={label.Value}"))
    });
    private static string FormatVolumeDetails(DockerVolumeDetailsDto details)
    {
        var lines = new List<string>
        {
            $"{LocalizedText.Get("docker.table.name")}: {details.Name}",
            $"{LocalizedText.Get("docker.volume.driver")}: {details.Driver}",
            $"{LocalizedText.Get("docker.table.mount_point")}: {details.Mountpoint}",
        };
        // Deleting an in-use volume is refused, so this list is the impact the operator has to see
        // before deciding to release the data. A container keeps the volume reserved even when stopped.
        if (details.UsedBy.Count > 0)
            lines.Add(FormatSection(LocalizedText.Get("docker.resource.attached_containers"), details.UsedBy));
        if (details.Labels.Count > 0)
            lines.Add(FormatSection(LocalizedText.Get("docker.container.labels"), details.Labels.Select(label => $"{label.Key}={label.Value}")));
        return string.Join(Environment.NewLine, lines);
    }
    private static string FormatSection(string heading, IEnumerable<string> values) => $"{heading}:{Environment.NewLine}{string.Join(Environment.NewLine, values)}";
}

/// <summary>
/// Read-only projection of one durable stack operation. The history grid binds to display text, so the
/// kind and the state are localized in one place instead of leaking enum names onto the screen.
/// </summary>
public sealed class StackOperationRow(DockerStackOperationDto operation)
{
    public Guid OperationId { get; } = operation.OperationId;
    public string Operation { get; } = DockerManagerViewModel.OperationTextFor(operation.Kind);
    public string Result { get; } = DockerManagerViewModel.StateText(operation.State);
    /// <summary>Empty for a success; the stable code is shown verbatim because it is what a support
    /// request or a bug report has to name.</summary>
    public string Problem { get; } = operation.ProblemCode ?? string.Empty;
    public string Created { get; } = operation.CreatedAt.ToLocalTime().ToString("g");
}
