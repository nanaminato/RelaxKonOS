using System.Net;
using System.Net.Sockets;
using System.Globalization;
using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// The single place that turns a deployment plan into real Docker resources. It composes the
/// existing <see cref="IDockerEngineService"/> boundary, always passes structured arguments, and
/// labels every resource it creates with its application, revision, operation, and owner.
/// </summary>
internal sealed class ApplicationDeploymentRuntime(
    IDockerEngineService engine,
    ApplicationDeploymentOptions options,
    IHttpClientFactory httpClients,
    ILogger<ApplicationDeploymentRuntime> logger)
{
    public const string HealthClientName = "application-deployment-health";

    /// <summary>Verifies the engine is present, reachable, and able to run Linux containers.</summary>
    public async Task<string> PreflightEngineAsync(CancellationToken cancellationToken)
    {
        var status = await engine.GetStatusAsync(cancellationToken);
        if (status.IsAvailable)
        {
            var platform = ContainerImageFacts.Describe(status.OperatingSystem, status.Architecture);
            if (!ContainerImageFacts.IsSupportedPlatform(status.OperatingSystem, status.Architecture))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.PlatformUnsupported, 409);
            return platform!;
        }

        throw new ApplicationDeploymentException(status.ProblemCode switch
        {
            "docker.not_installed" => ApplicationDeploymentProblemCodes.EngineNotInstalled,
            _ => ApplicationDeploymentProblemCodes.EngineUnavailable,
        }, 409);
    }

    /// <summary>
    /// A bounded bind probe. Docker reports a conflicting publish as a create failure, but failing
    /// before the old instance is stopped keeps a recoverable deployment from becoming an outage.
    /// </summary>
    public ApplicationDeploymentException? ProbeBindAddress(string bindAddress, int? hostPort)
    {
        if (hostPort is not { } port) return null;
        if (bindAddress is not ("127.0.0.1" or "0.0.0.0")) return null;
        var address = bindAddress == "127.0.0.1" ? IPAddress.Loopback : IPAddress.Any;
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            listener.Start();
            return null;
        }
        catch (SocketException)
        {
            return new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.PortUnavailable, 409);
        }
        finally { try { listener?.Stop(); } catch (SocketException) { } }
    }

    public async Task<IReadOnlyList<DockerContainerDto>> ListContainersAsync(CancellationToken cancellationToken)
    {
        try { return await engine.ListContainersAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.EngineUnavailable, 409);
        }
    }

    /// <summary>Finds a container by its deterministic name and proves it belongs to this application
    /// before handing it to a mutating workflow. A matching name alone is never ownership.</summary>
    public async Task<DockerContainerDto?> FindOwnedContainerAsync(Guid applicationId, string name, CancellationToken cancellationToken)
    {
        var containers = await ListContainersAsync(cancellationToken);
        var candidate = containers.FirstOrDefault(container => ContainerMatches(container, name));
        if (candidate is null) return null;
        var details = await engine.GetContainerAsync(candidate.Id, cancellationToken);
        return details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, applicationId) ? candidate : null;
    }

    public async Task<DockerContainerDetailsDto?> InspectAsync(string containerId, CancellationToken cancellationToken)
        => await engine.GetContainerAsync(containerId, cancellationToken);

    /// <summary>Lists only containers whose ownership labels identify this application, including an
    /// interrupted candidate. Name-prefix matches are merely candidates for inspection.</summary>
    public async Task<IReadOnlyList<DockerContainerDto>> FindApplicationContainersAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var stem = ApplicationDeploymentValidation.ContainerName(applicationId);
        var containers = await ListContainersAsync(cancellationToken);
        var matches = containers.Where(container => container.Names.Split(',')
            .Any(name => name.Trim().Equals(stem, StringComparison.Ordinal)
                || name.Trim().StartsWith(stem + "-", StringComparison.Ordinal)));
        var owned = new List<DockerContainerDto>();
        foreach (var container in matches)
        {
            var details = await engine.GetContainerAsync(container.Id, cancellationToken);
            if (details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, applicationId)) owned.Add(container);
        }
        return owned;
    }

    public async Task<DockerOperationResult> PullAsync(string imageReference, string? resolvedImageReference, CancellationToken cancellationToken, Action<string> onOutput)
        => await engine.PullImageAsync(new DockerImageOperationRequest(imageReference), resolvedImageReference, cancellationToken, onOutput);

    /// <summary>
    /// Builds the server-generated context for a revision. The build output is requested because a
    /// failed build has no other channel: without it the operator is left with a problem code and no
    /// way to see why the image did not build.
    /// </summary>
    public async Task<DockerOperationResult> BuildAsync(string contextDirectory, string imageReference, CancellationToken cancellationToken, Action<string> onOutput)
        => await engine.BuildImageAsync(new DockerBuildRequest(contextDirectory, imageReference), includeBuildOutput: true, cancellationToken, onOutput);

    /// <summary>Resolves the observed identity of an image. Tags are display only; this is the binding.</summary>
    public async Task<(string? ImageId, string? Reference)> ResolveImageIdentityAsync(string imageReference, CancellationToken cancellationToken)
    {
        var images = await engine.ListImagesAsync(cancellationToken);
        var expected = SplitReference(imageReference);
        var match = images.FirstOrDefault(image => string.Equals(image.Repository, expected.Repository, StringComparison.Ordinal)
            && string.Equals(image.Tag, expected.Tag, StringComparison.Ordinal));
        return match is null ? (null, null) : (match.Id, $"{match.Repository}:{match.Tag}");
    }

    public async Task<bool> ImageExistsAsync(string imageId, CancellationToken cancellationToken)
    {
        try
        {
            var images = await engine.ListImagesAsync(cancellationToken);
            return images.Any(image => string.Equals(image.Id, imageId, StringComparison.Ordinal)
                || image.Id.StartsWith(imageId, StringComparison.Ordinal) || imageId.StartsWith(image.Id, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { return false; }
    }

    public async Task EnsureVolumeAsync(ApplicationRecord application, RevisionRecord revision, Guid operationId, string volumeName, CancellationToken cancellationToken)
    {
        var name = ApplicationDeploymentValidation.VolumeName(application.Id, volumeName);
        var existing = await engine.ListVolumesAsync(cancellationToken);
        if (existing.Any(volume => string.Equals(volume.Name, name, StringComparison.Ordinal)))
        {
            var details = await engine.GetVolumeAsync(name, cancellationToken);
            if (details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, application.Id)) return;
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.DriftUnownedResource, 409);
        }
        var labels = ApplicationDeploymentValidation.Labels(application.Id, application.Name, revision.Id, revision.Number,
            operationId, ApplicationDeploymentValidation.RoleVolume);
        var result = await engine.CreateVolumeAsync(new DockerVolumeCreateRequest(name, Labels: labels), cancellationToken);
        if (!result.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.VolumeCreateFailed, 409);
    }

    public async Task<bool> VolumeExistsAsync(Guid applicationId, string volumeName, CancellationToken cancellationToken)
    {
        var name = ApplicationDeploymentValidation.VolumeName(applicationId, volumeName);
        var details = await engine.GetVolumeAsync(name, cancellationToken);
        return details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, applicationId);
    }

    /// <summary>Creates the container for one published revision. It is always created stopped, then
    /// started explicitly, so the health check observes a deliberate transition.</summary>
    /// <param name="secretsDirectory">Host directory holding the materialized secret files of this
    /// revision. It is mounted read-only at <see cref="ApplicationDeploymentService.SecretsMountPath"/>;
    /// the mount is only added when the revision actually declares a secret entry.</param>
    public async Task<DockerOperationResult> CreateContainerAsync(
        ApplicationRecord application,
        RevisionRecord revision,
        string containerName,
        IReadOnlyList<string> environment,
        Guid operationId,
        string role,
        string? secretsDirectory,
        CancellationToken cancellationToken)
    {
        var labels = ApplicationDeploymentValidation.Labels(application.Id, application.Name, revision.Id, revision.Number, operationId, role);
        var ports = new List<string>();
        if (application.HostPort is { } hostPort)
            ports.Add($"{application.BindAddress}:{hostPort}:{application.ContainerPort}");
        var mounts = revision.Volumes
            .Select(volume => $"{ApplicationDeploymentValidation.VolumeName(application.Id, volume.Name)}:{volume.ContainerPath}{(volume.ReadOnly ? ":ro" : string.Empty)}")
            .ToList();
        // Secret bodies are delivered as files, never as environment values, so they stay out of the
        // create request, the process list, and an inspect result. One directory per revision keeps a
        // rollback target readable until its revision is retired.
        if (!string.IsNullOrWhiteSpace(secretsDirectory) && revision.Configuration.Any(entry => entry.IsSecret))
            mounts.Add($"{secretsDirectory}:{ApplicationDeploymentService.SecretsMountPath}:ro");

        var request = new DockerContainerCreateRequest(
            containerName,
            revision.ImageReference,
            revision.Arguments,
            ports,
            environment,
            mounts,
            Network: null,
            RestartPolicy: "unless-stopped",
            Labels: labels,
            Resources: new DockerContainerResourceOptions(
                CpuCores: revision.Limits.CpuCores ?? options.DefaultCpuCores,
                MemoryBytes: revision.Limits.MemoryBytes ?? options.DefaultMemoryBytes,
                PidsLimit: revision.Limits.PidsLimit ?? options.DefaultPidsLimit,
                LogDriver: options.DefaultLogDriver,
                LogOptions: [$"max-size={options.DefaultLogMaxSize}", $"max-file={options.DefaultLogMaxFiles}"]));

        return await engine.CreateContainerAsync(request, cancellationToken);
    }

    public async Task<DockerOperationResult> StartAsync(string containerId, CancellationToken cancellationToken)
        => await engine.ApplyContainerActionAsync(containerId, "start", new DockerContainerActionRequest(), cancellationToken);

    public async Task<DockerOperationResult> StopAsync(string containerId, bool force, CancellationToken cancellationToken)
        => await engine.ApplyContainerActionAsync(containerId, "stop", new DockerContainerActionRequest(Force: force, Confirmed: true), cancellationToken);

    /// <summary>
    /// Removes a container this application owns. Confirmation is already established by the
    /// protocol request that authorized the operation; the server does not ask itself again.
    /// </summary>
    public async Task<DockerOperationResult> RemoveAsync(string containerId, CancellationToken cancellationToken)
        => await engine.ApplyContainerActionAsync(containerId, "delete", new DockerContainerActionRequest(Force: true, Confirmed: true), cancellationToken);

    public async Task<DockerOperationResult> RenameAsync(string containerId, string newName, CancellationToken cancellationToken)
        => await engine.UpdateContainerAsync(containerId, new DockerContainerUpdateRequest(newName), cancellationToken);

    public async Task<DockerOperationResult> RemoveVolumeAsync(Guid applicationId, string volumeName, CancellationToken cancellationToken)
        => await engine.DeleteVolumeAsync(ApplicationDeploymentValidation.VolumeName(applicationId, volumeName), confirmed: true, cancellationToken);

    public async Task<DockerContainerLogsDto?> LogsAsync(string containerId, int tail, CancellationToken cancellationToken)
    {
        try { return await engine.GetContainerLogsAsync(containerId, Math.Clamp(tail, 1, 1000), cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.EngineUnavailable, 409);
        }
    }

    /// <summary>
    /// Works out whether the started container is ready. A process-level check is explicitly weaker
    /// and is what a worker workload must select; an HTTP check probes the loopback publish.
    /// </summary>
    public async Task<bool> IsReadyAsync(ApplicationRecord application, string containerId, CancellationToken cancellationToken)
    {
        var details = await engine.GetContainerAsync(containerId, cancellationToken);
        if (details is null) return false;
        if (!details.State.Equals("running", StringComparison.OrdinalIgnoreCase)) return false;
        if (application.ReadinessLevel != ApplicationReadinessLevel.Http) return true;
        if (application.HostPort is not { } hostPort) return false;

        var path = string.IsNullOrWhiteSpace(application.HealthCheckPath) ? "/" : application.HealthCheckPath;
        var client = httpClients.CreateClient(HealthClientName);
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{hostPort}{path}", cancellationToken);
            return (int)response.StatusCode is >= 200 and < 400;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Reports the observed state of a managed container as a stable drift problem code, or null when
    /// the observation agrees with the recorded intent. A container stopped because the operator asked
    /// for it to stop is not drift, so the desired state is part of the comparison.
    /// </summary>
    /// <param name="engineAvailable">When the engine could not be reached the observation is unknown,
    /// which is not the same as drift and must not be reported as one.</param>
    /// <param name="ownedByUs">Whether the matched container actually carries this application's
    /// ownership labels. Name matching alone cannot rule out a foreign container reusing the name.</param>
    public static string? DescribeDrift(ApplicationRecord application, DockerContainerDto? container, bool engineAvailable, bool ownedByUs)
    {
        if (!engineAvailable) return null;
        if (container is null)
            return application.CurrentRevisionId is null ? null : ApplicationDeploymentProblemCodes.DriftContainerMissing;
        if (!ownedByUs) return ApplicationDeploymentProblemCodes.DriftUnownedResource;

        return container.State.ToLowerInvariant() switch
        {
            "running" => null,
            "created" or "paused" when application.DesiredState == ApplicationDesiredState.Stopped => null,
            "exited" when application.DesiredState == ApplicationDesiredState.Stopped => null,
            "created" or "exited" or "paused" or "dead" or "removing" => ApplicationDeploymentProblemCodes.DriftContainerExternallyModified,
            _ => null,
        };
    }

    private static bool ContainerMatches(DockerContainerDto container, string name) => container.Names
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(candidate => string.Equals(candidate, name, StringComparison.Ordinal));

    /// <summary>Splits <c>repository:tag</c>, defaulting the tag the way Docker does.</summary>
    private static (string Repository, string Tag) SplitReference(string imageReference)
    {
        var separator = imageReference.LastIndexOf(':');
        var slash = imageReference.LastIndexOf('/');
        if (separator <= slash) return (Normalize(imageReference), "latest");
        return (Normalize(imageReference[..separator]), imageReference[(separator + 1)..]);
    }

    /// <summary>Docker reports an official image repository as <c>library/name</c>.</summary>
    private static string Normalize(string repository)
    {
        var name = repository.Contains('/') ? repository : $"library/{repository}";
        return name.Contains('.') || name.Contains(':') || name.StartsWith("localhost/", StringComparison.Ordinal) ? repository : name;
    }

    /// <summary>Appends a bounded, sanitized diagnostic line for the server log without echoing secrets.</summary>
    public void LogOperation(string operation, Guid operationId, params (string Key, string? Value)[] fields)
    {
        var context = string.Join(", ", fields.Where(field => field.Value is { Length: > 0 })
            .Select(field => $"{field.Key}={field.Value}"));
        logger.LogInformation("Application deployment {Operation} for operation {OperationId}. {Context}",
            operation, operationId.ToString("D", CultureInfo.InvariantCulture), context);
    }
}
