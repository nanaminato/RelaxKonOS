using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Owns the operator-facing definition of a deployment target: the templates that can be chosen, the
/// application record itself, its published revisions, its recent operations, and its logs. It never
/// starts a workload — that belongs to <see cref="ApplicationDeploymentService"/> behind the
/// operation coordinator — and it never returns a secret value.
/// </summary>
internal sealed class ApplicationDeploymentManager(
    ApplicationDeploymentCatalogStore catalog,
    ApplicationDeploymentSecretStore secrets,
    ApplicationDeploymentOperationStore operations,
    ApplicationDeploymentRuntime runtime,
    ApplicationDeploymentOptions options)
{
    public ApplicationDeploymentTemplateDto[] Templates() => ApplicationTemplateCatalog.DescribeAll(options);

    /// <summary>
    /// One engine listing serves the whole page, so a list of applications costs one Docker call
    /// instead of one per application. When the engine cannot be reached the observed state is
    /// reported as unknown rather than invented: an unreachable engine is not a stopped workload.
    /// </summary>
    public async Task<ApplicationDto[]> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainerDto>? containers;
        try { containers = await runtime.ListContainersAsync(cancellationToken); }
        catch (ApplicationDeploymentException) { containers = null; }

        var revisions = catalog.ReadRevisionsAll().ToDictionary(revision => revision.Id);
        return [.. catalog.ReadApplications().Select(application => ApplicationDeploymentMapper.Describe(
            application,
            application.CurrentRevisionId is { } currentId && revisions.TryGetValue(currentId, out var current) ? current : null,
            containers is null ? null : Match(containers, application.Id),
            containers is not null,
            // The list matches by the deterministic name only. The detail view inspects the container
            // and verifies the ownership labels, which is where a name collision surfaces.
            ownedByUs: true))];
    }

    public async Task<ApplicationDeploymentSnapshotDto> SnapshotAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var application = Require(applicationId);
        var view = await ObserveAsync(application, cancellationToken);
        var revisions = catalog.ReadRevisions(applicationId);
        var current = revisions.FirstOrDefault(revision => revision.Id == application.CurrentRevisionId);
        var active = operations.GetActive(applicationId);
        return new ApplicationDeploymentSnapshotDto(
            ApplicationDeploymentMapper.Describe(application, current, view.Container, view.EngineAvailable, view.Owned),
            [.. revisions.Select(revision => ApplicationDeploymentMapper.Revision(revision, application.CurrentRevisionId))],
            Operations(applicationId, 50),
            active?.Operation);
    }

    public ApplicationRevisionDto[] Revisions(Guid applicationId)
    {
        var application = Require(applicationId);
        return [.. catalog.ReadRevisions(applicationId)
            .Select(revision => ApplicationDeploymentMapper.Revision(revision, application.CurrentRevisionId))];
    }

    public DeploymentOperationDto[] Operations(Guid applicationId, int maximum)
    {
        Require(applicationId);
        return [.. operations.Read()
            .Where(entry => entry.Operation.ApplicationId == applicationId)
            .OrderByDescending(entry => entry.Operation.CreatedAt)
            .Take(Math.Clamp(maximum, 1, 200))
            .Select(entry => entry.Operation)];
    }

    /// <summary>Container output with the same bounded, sanitized treatment the proxy domain uses.</summary>
    public async Task<DeploymentLogDto> LogsAsync(Guid applicationId, int tail, CancellationToken cancellationToken)
    {
        var application = Require(applicationId);
        var view = await ObserveAsync(application, cancellationToken);
        if (view.Container is null)
            return new(applicationId, null, application.CurrentRevisionId, [], false);

        var logs = await runtime.LogsAsync(view.Container.Id, tail, cancellationToken);
        return logs is null
            ? new(applicationId, view.Container.Id, application.CurrentRevisionId, [], false)
            : new(applicationId, view.Container.Id, application.CurrentRevisionId,
                [.. logs.Lines.Select(ApplicationDeploymentLogSanitizer.Sanitize)], logs.Truncated);
    }

    /// <summary>
    /// Bounded output of the step that produced an operation's outcome. An unknown operation and an
    /// operation that recorded no output both answer null, so the route reports them identically and
    /// a client never has to tell "nothing to show" apart from "does not exist".
    /// </summary>
    public DeploymentOperationDiagnosticsDto? OperationDiagnostics(Guid operationId)
    {
        var entry = operations.Get(operationId);
        if (entry?.Diagnostics is not { Length: > 0 } lines) return null;
        var operation = entry.Operation;
        return new(operation.OperationId, operation.ApplicationId, operation.Kind, operation.Stage,
            operation.ProblemCode, lines, entry.DiagnosticsTruncated);
    }

    /// <summary>
    /// Creates the definition. The source archive or image is deliberately not part of it: a published
    /// revision binds the source, so the definition can be edited without implying a rebuild.
    /// </summary>
    public async Task<ApplicationDto> CreateAsync(CreateApplicationRequest request, string actor, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(request.SourceKind))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);

        var identity = Guid.NewGuid();
        var volumes = Volumes(request.Volumes);
        var configuration = Configuration(identity, request.Configuration, allowExistingVersions: false);
        var definition = Validate(identity, request.Name, request.WorkloadKind, request.ReadinessLevel, request.HealthCheckPath,
            request.ContainerPort, request.HostPort, request.BindAddress, request.Limits, volumes, configuration, request.SiteId);

        // The name conflict is decided before a secret version is written, and the secret store is
        // rolled back if the catalog write still fails, so a rejected request leaves no residue.
        if (catalog.FindByName(definition.Name) is not null)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NameConflict);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var application = new ApplicationRecord(identity, definition.Name,
                ApplicationDeploymentValidation.Reference(actor), request.SourceKind, definition.WorkloadKind,
                ApplicationDesiredState.Stopped, definition.ReadinessLevel, definition.HealthCheckPath,
                definition.ContainerPort, definition.HostPort, definition.BindAddress, definition.Limits,
                volumes, configuration, null, null, null, null, definition.SiteId, null, now, now, null);
            catalog.Create(application);
            return await DescribeOneAsync(application, cancellationToken);
        }
        catch
        {
            secrets.RemoveApplication(identity);
            throw;
        }
    }

    /// <summary>
    /// Replaces the stored definition. Runtime-bound fields such as ports, limits, and mounts only take
    /// effect on the next deployment, because applying them means replacing the container instance.
    /// </summary>
    public async Task<ApplicationDto> UpdateAsync(Guid applicationId, UpdateApplicationRequest request, CancellationToken cancellationToken)
    {
        Require(applicationId);
        // Definition fields become a revision snapshot during deployment. Letting an update race the
        // worker would make the operator's stored intent and the candidate's snapshot ambiguous.
        if (operations.GetActive(applicationId) is not null)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ResourceConflict);
        var volumes = Volumes(request.Volumes);
        var configuration = Configuration(applicationId, request.Configuration, allowExistingVersions: true);
        var definition = Validate(applicationId, request.Name, request.WorkloadKind, request.ReadinessLevel, request.HealthCheckPath,
            request.ContainerPort, request.HostPort, request.BindAddress, request.Limits, volumes, configuration, request.SiteId);

        var updated = catalog.Update(applicationId, application => application with
        {
            Name = definition.Name,
            WorkloadKind = definition.WorkloadKind,
            ReadinessLevel = definition.ReadinessLevel,
            HealthCheckPath = definition.HealthCheckPath,
            ContainerPort = definition.ContainerPort,
            HostPort = definition.HostPort,
            BindAddress = definition.BindAddress,
            Limits = definition.Limits,
            Volumes = volumes,
            Configuration = configuration,
            SiteId = definition.SiteId,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return await DescribeOneAsync(updated, cancellationToken);
    }

    public ApplicationRecord Require(Guid applicationId) => catalog.Find(applicationId)
        ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ApplicationNotFound, 404);

    private async Task<ApplicationDto> DescribeOneAsync(ApplicationRecord application, CancellationToken cancellationToken)
    {
        var view = await ObserveAsync(application, cancellationToken);
        var current = application.CurrentRevisionId is { } id ? catalog.FindRevision(id) : null;
        return ApplicationDeploymentMapper.Describe(application, current, view.Container, view.EngineAvailable, view.Owned);
    }

    /// <summary>The observed canonical container plus whether the observation itself succeeded.</summary>
    private async Task<RuntimeView> ObserveAsync(ApplicationRecord application, CancellationToken cancellationToken)
    {
        IReadOnlyList<DockerContainerDto> containers;
        try { containers = await runtime.ListContainersAsync(cancellationToken); }
        catch (ApplicationDeploymentException) { return new(null, false, true); }

        var container = Match(containers, application.Id);
        if (container is null) return new(null, true, true);
        try
        {
            var details = await runtime.InspectAsync(container.Id, cancellationToken);
            return new(container, true, details is not null && ApplicationDeploymentValidation.IsOwnedBy(details.Labels, application.Id));
        }
        catch (ApplicationDeploymentException) { return new(container, true, true); }
    }

    private static DockerContainerDto? Match(IReadOnlyList<DockerContainerDto> containers, Guid applicationId)
    {
        var name = ApplicationDeploymentValidation.ContainerName(applicationId);
        return containers.FirstOrDefault(container => container.Names
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(candidate => string.Equals(candidate, name, StringComparison.Ordinal)));
    }

    private static ApplicationVolumeRecord[] Volumes(IReadOnlyList<ApplicationVolumeDto>? volumes) => volumes is null
        ? []
        : [.. volumes.Select(volume => new ApplicationVolumeRecord(
            volume.Name?.Trim() ?? string.Empty, volume.ContainerPath?.Trim() ?? string.Empty, volume.ReadOnly))];

    /// <summary>
    /// Resolves the configuration list into records. A secret is stored through the protected store and
    /// only its version is kept in the record, so the value can never reach a revision snapshot or a
    /// log. On update an already-stored version may be referenced instead of re-sending the value.
    /// </summary>
    private ApplicationConfigRecord[] Configuration(Guid applicationId, IReadOnlyList<ApplicationConfigEntryDto>? entries,
        bool allowExistingVersions)
    {
        if (entries is null) return [];
        var records = new ApplicationConfigRecord[entries.Count];
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var name = entry.Name?.Trim() ?? string.Empty;
            if (!ApplicationDeploymentValidation.IsValidEnvironmentName(name))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);

            if (!entry.IsSecret)
            {
                // A control character here would let a value break out of the container environment
                // list, so it is rejected instead of being escaped.
                if (entry.Value is null || entry.Value.Length > 4096 || entry.Value.Any(char.IsControl))
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
                records[index] = new(name, false, 0, entry.Value);
                continue;
            }

            if (!string.IsNullOrEmpty(entry.Value))
            {
                if (entry.Value.Length > 4096)
                    throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
                records[index] = new(name, true, secrets.Set(applicationId, name, entry.Value), null);
                continue;
            }

            if (allowExistingVersions && entry.SecretVersion is { } version && secrets.Has(applicationId, name, version))
            {
                records[index] = new(name, true, version, null);
                continue;
            }

            throw new ApplicationDeploymentException(allowExistingVersions
                ? ApplicationDeploymentProblemCodes.SecretVersionMissing
                : ApplicationDeploymentProblemCodes.InvalidRequest, allowExistingVersions ? 409 : 400);
        }
        return records;
    }

    /// <summary>
    /// Validates the definition as a whole, including the cross-field rules that a per-field check
    /// cannot express: an HTTP-readiness workload needs a published port and a path to probe, a worker
    /// cannot be HTTP-ready, a proxy association needs a port to reach, and two applications may not
    /// publish the same host port.
    /// </summary>
    private Definition Validate(Guid applicationId, string? name, ApplicationWorkloadKind workloadKind,
        ApplicationReadinessLevel readinessLevel, string? healthCheckPath, int containerPort, int? hostPort,
        string? bindAddress, ApplicationResourceLimitsDto? limits, ApplicationVolumeRecord[] volumes,
        ApplicationConfigRecord[] configuration, string? siteId)
    {
        if (!ApplicationDeploymentValidation.IsValidName(name)
            || !Enum.IsDefined(workloadKind) || !Enum.IsDefined(readinessLevel)
            || !ApplicationDeploymentValidation.IsValidPort(containerPort)
            || (hostPort is { } port && !ApplicationDeploymentValidation.IsValidPort(port))
            || !ApplicationDeploymentValidation.IsValidBindAddress(bindAddress)
            || !ApplicationDeploymentValidation.IsValidLimits(limits)
            || !ApplicationDeploymentValidation.IsValidVolumes(volumes)
            || !ApplicationDeploymentValidation.IsValidConfiguration(configuration))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);

        // Process readiness has no probe, so a path left over from a previous definition is cleared
        // rather than rejected: it has no effect and rejecting it would only surprise the operator.
        var path = readinessLevel == ApplicationReadinessLevel.Http ? healthCheckPath?.Trim() : null;
        if (!ApplicationDeploymentValidation.IsValidHealthPath(path))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (readinessLevel == ApplicationReadinessLevel.Http && (hostPort is null || string.IsNullOrWhiteSpace(path)))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (readinessLevel == ApplicationReadinessLevel.Http && workloadKind != ApplicationWorkloadKind.Web)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (siteId is not null && (siteId.Length > 128 || !siteId.All(char.IsAsciiLetterOrDigit) || hostPort is null))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (hostPort is { } published && catalog.ReadApplications()
            .Any(application => application.Id != applicationId && application.HostPort == published))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.PortConflict);

        return new(name!, workloadKind, readinessLevel, path, containerPort, hostPort, bindAddress!,
            limits ?? new ApplicationResourceLimitsDto(), siteId);
    }

    private readonly record struct RuntimeView(DockerContainerDto? Container, bool EngineAvailable, bool Owned);

    private sealed record Definition(
        string Name,
        ApplicationWorkloadKind WorkloadKind,
        ApplicationReadinessLevel ReadinessLevel,
        string? HealthCheckPath,
        int ContainerPort,
        int? HostPort,
        string BindAddress,
        ApplicationResourceLimitsDto Limits,
        string? SiteId);
}
