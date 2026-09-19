using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Projects ledger records onto protocol DTOs. It is the single place that decides what a client may
/// see, so a secret entry can never leak a value: only its protected-store version is reported.
/// </summary>
internal static class ApplicationDeploymentMapper
{
    public static ApplicationDto Describe(
        ApplicationRecord application,
        RevisionRecord? currentRevision,
        DockerContainerDto? container,
        bool engineAvailable,
        bool ownedByUs)
        => new(
            application.Id,
            application.Name,
            application.OwnerReference,
            application.SourceKind,
            application.WorkloadKind,
            application.DesiredState,
            ActualState(application, container, engineAvailable),
            application.ReadinessLevel,
            application.HealthCheckPath,
            application.ContainerPort,
            application.HostPort,
            application.BindAddress,
            // A worker workload publishes no endpoint, so an endpoint object would be a lie.
            application.HostPort is { } hostPort && application.WorkloadKind == ApplicationWorkloadKind.Web
                ? new ApplicationEndpointDto("http", application.ContainerPort, hostPort, application.BindAddress)
                : null,
            application.Limits,
            [.. application.Volumes.Select(Volume)],
            [.. application.Configuration.Select(Config)],
            application.CurrentRevisionId,
            currentRevision?.Number,
            application.ContainerName,
            application.ContainerId,
            application.SiteId,
            application.Domain,
            application.CreatedAt,
            application.UpdatedAt,
            application.LastDeployedAt,
            ApplicationDeploymentRuntime.DescribeDrift(application, container, engineAvailable, ownedByUs));

    public static ApplicationRevisionDto Revision(RevisionRecord revision, Guid? currentRevisionId) => new(
        revision.Id,
        revision.ApplicationId,
        revision.Number,
        revision.SourceKind,
        revision.TemplateVersion,
        revision.InputReference,
        revision.ImageReference,
        revision.ImageId,
        revision.Platform,
        revision.BaseImage,
        revision.EntryPoint,
        revision.Arguments,
        revision.ContainerPort,
        revision.HostPort,
        revision.BindAddress,
        revision.Limits,
        [.. revision.Volumes.Select(Volume)],
        [.. revision.Configuration.Select(Config)],
        revision.Id == currentRevisionId,
        revision.CreatedByReference,
        revision.CreatedAt);

    public static ApplicationVolumeDto Volume(ApplicationVolumeRecord volume) =>
        new(volume.Name, volume.ContainerPath, volume.ReadOnly);

    /// <summary>A secret entry reports the version a revision binds, and never the value.</summary>
    public static ApplicationConfigEntryDto Config(ApplicationConfigRecord entry) =>
        new(entry.Name, entry.IsSecret ? null : entry.Value, entry.IsSecret, entry.IsSecret ? entry.SecretVersion : null);

    /// <summary>
    /// The observed state is derived from real containers, never from the ledger alone, so a failed or
    /// externally removed instance is never presented as running.
    /// </summary>
    private static ApplicationActualState ActualState(ApplicationRecord application, DockerContainerDto? container, bool engineAvailable)
    {
        if (!engineAvailable) return ApplicationActualState.Unknown;
        if (container is null)
            return application.CurrentRevisionId is null
                ? ApplicationActualState.Unknown
                : application.DesiredState == ApplicationDesiredState.Running
                    ? ApplicationActualState.Missing
                    : ApplicationActualState.Stopped;

        return container.State.ToLowerInvariant() switch
        {
            "running" => ApplicationActualState.Running,
            "restarting" => ApplicationActualState.Starting,
            "created" or "paused" => ApplicationActualState.Stopped,
            "exited" => application.DesiredState == ApplicationDesiredState.Running
                ? ApplicationActualState.Failed
                : ApplicationActualState.Stopped,
            "dead" or "removing" => ApplicationActualState.Failed,
            _ => ApplicationActualState.Unknown,
        };
    }
}
