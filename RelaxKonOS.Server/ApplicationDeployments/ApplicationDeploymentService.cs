using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Proxy.Mihomo;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>A typed deployment request. It is what a fingerprint is computed over and what the
/// worker executes — never the raw client JSON.</summary>
internal sealed record DeploymentRequest(
    Guid ApplicationId,
    DeploymentOperationKind Kind,
    DeploymentSourceInputDto? Source = null,
    Guid? RevisionId = null,
    bool Force = false,
    bool DeleteVolumes = false);

/// <summary>
/// Executes one deployment operation against the local Docker Engine. It owns the stage sequence,
/// records every external side effect around the ledger, and performs a best-effort rollback whose
/// outcome is reported separately from the original failure.
/// </summary>
internal sealed class ApplicationDeploymentService(
    ApplicationDeploymentCatalogStore catalog,
    ApplicationDeploymentSecretStore secrets,
    ApplicationDeploymentStagingStore staging,
    ApplicationDeploymentRuntime runtime,
    IApplicationDeploymentProxyIntegration proxy,
    ApplicationDeploymentOptions options,
    IHostEnvironment environment,
    ILogger<ApplicationDeploymentService> logger)
{
    /// <summary>Read-only secret delivery path inside the workload container.</summary>
    public const string SecretsMountPath = "/run/relaxkonos/secrets";
    private const int PreservedBuildContexts = 20;

    public static IReadOnlyList<string> Resources(Guid applicationId) => [$"application-deployment:{applicationId:D}"];

    /// <summary>
    /// A canonical fingerprint of the typed request. Two requests sharing an idempotency key must
    /// produce the same fingerprint; any difference must conflict instead of reusing silently.
    /// </summary>
    public static string Fingerprint(DeploymentRequest request)
    {
        var parts = new List<string>
        {
            request.ApplicationId.ToString("D"),
            request.Kind.ToString(),
            request.RevisionId?.ToString("D") ?? "-",
            request.Force ? "force" : "-",
            request.DeleteVolumes ? "purge" : "-",
        };
        if (request.Source is { } source) parts.Add(Canonical(source));
        return ApplicationDeploymentValidation.Reference(string.Join('\u001e', parts));
    }

    /// <summary>Cancellation is offered only before the activation or recovery critical section.</summary>
    public bool Cancel(DeploymentOperationDto operation) => operation.Cancellable;

    public async Task StartAsync(Guid operationId, DeploymentRequest request, string actor, IApplicationDeploymentProgress progress, CancellationToken cancellationToken)
    {
        var application = catalog.Find(request.ApplicationId)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ApplicationNotFound, 404);
        switch (request.Kind)
        {
            case DeploymentOperationKind.Deploy:
                await DeployAsync(application, operationId, request, actor, progress, cancellationToken);
                return;
            case DeploymentOperationKind.Rollback:
                await RollbackAsync(application, operationId, request, progress, cancellationToken);
                return;
            case DeploymentOperationKind.Start or DeploymentOperationKind.Stop or DeploymentOperationKind.Restart:
                await ApplyLifecycleAsync(application, request, progress, cancellationToken);
                return;
            case DeploymentOperationKind.Delete:
                await DeleteAsync(application, operationId, progress, request.DeleteVolumes, cancellationToken);
                return;
            default:
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NotSupported, 400);
        }
    }

    /// <summary>
    /// Startup reconciliation. An operation that was running when the process stopped is never
    /// replayed: residue is removed, the previous instance is restored when possible, and the outcome
    /// is reported as a recovery result rather than overwriting the interrupted state.
    /// </summary>
    public async Task<ApplicationDeploymentRecovery> RecoverAsync(DeploymentOperationDto operation, CancellationToken cancellationToken)
    {
        var application = catalog.Find(operation.ApplicationId);
        if (application is null)
            return new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.ApplicationNotFound, ApplicationDeploymentProblemCodes.RecoveryUnknown);

        try
        {
            var candidate = await runtime.FindOwnedContainerAsync(application.Id,
                ApplicationDeploymentValidation.CandidateContainerName(application.Id), cancellationToken);
            if (candidate is not null)
            {
                await runtime.StopAsync(candidate.Id, force: true, cancellationToken);
                await runtime.RemoveAsync(candidate.Id, cancellationToken);
            }

            if (application.CurrentRevisionId is { } interruptedRevision)
                secrets.ReleaseMaterialized(application.Id, interruptedRevision);

            var canonical = await runtime.FindOwnedContainerAsync(application.Id,
                ApplicationDeploymentValidation.ContainerName(application.Id), cancellationToken);
            if (canonical is null)
            {
                var previous = await runtime.FindOwnedContainerAsync(application.Id,
                    ApplicationDeploymentValidation.PreviousContainerName(application.Id), cancellationToken);
                if (previous is not null)
                {
                    var restoredName = await runtime.RenameAsync(previous.Id, ApplicationDeploymentValidation.ContainerName(application.Id), cancellationToken);
                    if (!restoredName.Success)
                        return new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.RecoveryFailed);
                    if (application.DesiredState == ApplicationDesiredState.Running)
                    {
                        var started = await runtime.StartAsync(previous.Id, cancellationToken);
                        if (!started.Success)
                            return new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.RecoveryFailed);
                    }
                    catalog.BindRuntime(application.Id, ApplicationDeploymentValidation.ContainerName(application.Id), previous.Id, null);
                    return new(DeploymentOperationState.Interrupted, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.PreviousInstanceRestored);
                }
                catalog.BindRuntime(application.Id, null, null, ApplicationDesiredState.Stopped);
                return new(DeploymentOperationState.Interrupted, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.OrphanedResources);
            }

            if (application.DesiredState == ApplicationDesiredState.Running && !canonical.State.Equals("running", StringComparison.OrdinalIgnoreCase))
                await runtime.StartAsync(canonical.Id, cancellationToken);

            catalog.BindRuntime(application.Id, canonical.Names, canonical.Id, null);
            return new(DeploymentOperationState.Interrupted, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.PreviousInstanceRestored);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            logger.LogWarning(exception, "Recovery of an interrupted deployment operation did not complete. OperationId={OperationId}", operation.OperationId);
            return new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Interrupted, ApplicationDeploymentProblemCodes.RecoveryUnknown);
        }
    }

    private async Task DeployAsync(ApplicationRecord application, Guid operationId, DeploymentRequest request, string actor,
        IApplicationDeploymentProgress progress, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(new(DeploymentStage.Preflight, null, true), cancellationToken);
        var platform = await runtime.PreflightEngineAsync(cancellationToken);
        EnsureExecutableDefinition(application);

        if (request.Source is not { } source)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
        if (runtime.ProbeBindAddress(application.BindAddress, application.HostPort) is { } conflict) throw conflict;
        CleanupExpiredBuildContexts();

        var inputReference = ApplicationDeploymentValidation.Reference(Canonical(source));
        var template = ApplicationTemplateCatalog.Require(application.SourceKind);
        var plan = template.Validate(source, application, options, inputReference);

        await progress.ReportAsync(new(DeploymentStage.Preparing, null, true), cancellationToken);
        var contextDirectory = Path.Combine(BuildRoot, inputReference[..16]);
        var entryPoint = "image default";
        var arguments = plan.Arguments;
        if (template is not ImageTemplate)
        {
            TryDeleteDirectory(contextDirectory);
            Directory.CreateDirectory(contextDirectory);
            using (var archive = staging.Open(plan.ArchiveReferenceId!, actor))
                await ApplicationArchiveSafety.ExtractAsync(archive.Stream, contextDirectory, options, cancellationToken);
            (entryPoint, arguments) = await template.PrepareBuildContextAsync(plan, contextDirectory, application, options, cancellationToken);
        }

        await progress.ReportAsync(new(template is ImageTemplate ? DeploymentStage.Pulling : DeploymentStage.Building, null, true), cancellationToken);
        await ProduceImageAsync(template, plan, contextDirectory, cancellationToken);

        var identity = await runtime.ResolveImageIdentityAsync(plan.ImageReference, cancellationToken);
        if (identity.ImageId is null)
            throw new ApplicationDeploymentException(
                template is ImageTemplate ? ApplicationDeploymentProblemCodes.ImageNotFound : ApplicationDeploymentProblemCodes.BuildFailed, 409);

        var revision = catalog.AddRevision(new RevisionRecord(
            Guid.NewGuid(), application.Id, 0, application.SourceKind, template.TemplateVersion,
            inputReference, identity.Reference ?? plan.ImageReference, identity.ImageId, platform,
            plan.BaseImage, application.WorkloadKind, application.ReadinessLevel, application.HealthCheckPath,
            entryPoint, arguments, application.ContainerPort, application.HostPort, application.BindAddress,
            application.Limits, application.Volumes, application.Configuration,
            application.SiteId,
            ApplicationDeploymentValidation.Reference(actor), DateTimeOffset.UtcNow), out _);

        await ActivateRevisionAsync(application, operationId, revision, progress, cancellationToken, rollback: false);
    }

    private async Task RollbackAsync(ApplicationRecord application, Guid operationId, DeploymentRequest request,
        IApplicationDeploymentProgress progress, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(new(DeploymentStage.Preflight, null, true), cancellationToken);
        await runtime.PreflightEngineAsync(cancellationToken);
        if (request.RevisionId is not { } revisionId)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RevisionNotFound, 404);
        var revision = catalog.ReadRevisions(application.Id).FirstOrDefault(x => x.Id == revisionId)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.RevisionNotFound, 404);
        if (revision.Id == application.CurrentRevisionId)
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.AlreadyActive, 409);
        var definition = EffectiveDefinition(application, revision);
        EnsureExecutableDefinition(definition);

        // The old image, its start definition, and its secret versions must all still exist.
        if (revision.ImageId is { Length: > 0 } imageId && !await runtime.ImageExistsAsync(imageId, cancellationToken))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ImageNotFound, 409);
        foreach (var entry in revision.Configuration.Where(entry => entry.IsSecret))
            if (!secrets.Has(application.Id, entry.Name, entry.SecretVersion))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.SecretVersionMissing, 409);
        if (runtime.ProbeBindAddress(definition.BindAddress, definition.HostPort) is { } conflict) throw conflict;

        await progress.ReportAsync(new(DeploymentStage.Preparing, null, true), cancellationToken);
        await ActivateRevisionAsync(application, operationId, revision, progress, cancellationToken, rollback: true);
    }

    /// <summary>
    /// The shared stop-and-replace flow and the only place that touches the running instance. Every
    /// side effect is recorded around the ledger, and every failure path attempts a restore.
    /// </summary>
    private async Task ActivateRevisionAsync(ApplicationRecord application, Guid operationId, RevisionRecord revision,
        IApplicationDeploymentProgress progress, CancellationToken cancellationToken, bool rollback)
    {
        var definition = EffectiveDefinition(application, revision);
        var previous = await runtime.FindOwnedContainerAsync(application.Id,
            ApplicationDeploymentValidation.ContainerName(application.Id), cancellationToken);
        var previousWasRunning = previous is not null && previous.State.Equals("running", StringComparison.OrdinalIgnoreCase);
        string? candidateId = null;
        var previousRenamed = false;
        ApplicationDeploymentProxyResult? proxyApplied = null;

        try
        {
            foreach (var volume in revision.Volumes)
                await runtime.EnsureVolumeAsync(definition, revision, operationId, volume.Name, cancellationToken);

            // Candidate creation, start, and readiness all have a safe cleanup path. Cancellation is
            // withheld only once activation begins to alter the canonical instance and proxy route.
            await progress.ReportAsync(new(DeploymentStage.Creating, null, true), cancellationToken);
            var secretsDirectory = secrets.Materialize(application.Id, revision.Id, revision.Configuration);
            if (previousWasRunning) await StopOrThrowAsync(previous!.Id, cancellationToken);

            var created = await runtime.CreateContainerAsync(definition, revision,
                ApplicationDeploymentValidation.CandidateContainerName(application.Id),
                BuildEnvironment(definition, revision), operationId, ApplicationDeploymentValidation.RoleCandidate,
                secretsDirectory, cancellationToken);
            if (!created.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerCreateFailed, 409);

            var candidate = await runtime.FindOwnedContainerAsync(application.Id,
                ApplicationDeploymentValidation.CandidateContainerName(application.Id), cancellationToken)
                ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerCreateFailed, 409);
            candidateId = candidate.Id;
            catalog.BindRuntime(application.Id, candidate.Names, candidate.Id, null);

            var started = await runtime.StartAsync(candidate.Id, cancellationToken);
            if (!started.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerStartFailed, 409);

            await progress.ReportAsync(new(DeploymentStage.HealthChecking, null, true), cancellationToken);
            if (!await WaitForReadyAsync(definition, candidate.Id, cancellationToken))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.HealthCheckTimeout, 409);

            // Activation is the critical section: cancellation is no longer offered from here on.
            await progress.ReportAsync(new(DeploymentStage.Activating, null, false), cancellationToken);
            if (definition.SiteId is { Length: > 0 } && definition.HostPort is { } hostPort)
            {
                proxyApplied = await proxy.ApplyAsync(definition, hostPort, cancellationToken);
                if (!proxyApplied.Success)
                {
                    var reverted = await proxy.RevertAsync(definition, proxyApplied, cancellationToken);
                    throw new ProxyFailure(proxyApplied.ProblemCode ?? ApplicationDeploymentProblemCodes.ActivationFailed, reverted.ProblemCode);
                }
            }

            var canonicalName = ApplicationDeploymentValidation.ContainerName(application.Id);
            // Keep the stopped previous instance recoverable until the candidate is live through the
            // proxy and has acquired the canonical name. Deleting it first makes recovery impossible.
            if (previous is not null)
            {
                var renamedPrevious = await runtime.RenameAsync(previous.Id,
                    ApplicationDeploymentValidation.PreviousContainerName(application.Id), cancellationToken);
                if (!renamedPrevious.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ActivationFailed, 409);
                previousRenamed = true;
            }
            var renamed = await runtime.RenameAsync(candidate.Id, canonicalName, cancellationToken);
            if (!renamed.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerCreateFailed, 409);

            if (previous is not null)
            {
                var removed = await runtime.RemoveAsync(previous.Id, cancellationToken);
                if (!removed.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerRemoveFailed, 409);
            }

            if (proxyApplied is { Success: true })
                catalog.BindSite(application.Id, proxyApplied.SiteInstanceId, proxyApplied.SiteId, proxyApplied.Domain);

            var retired = application.CurrentRevisionId;
            catalog.Activate(application.Id, revision.Id, DateTimeOffset.UtcNow);
            catalog.BindRuntime(application.Id, canonicalName, candidate.Id, ApplicationDesiredState.Running);
            if (retired is { } retiredRevision && retiredRevision != revision.Id)
                secrets.ReleaseMaterialized(application.Id, retiredRevision);

            runtime.LogOperation(rollback ? "rollback" : "deploy", operationId,
                ("Application", application.Name), ("Revision", revision.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await progress.ReportAsync(new(DeploymentStage.Completed, null, false), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller requested cancellation before activation's critical section. Cleanup must
            // not inherit that cancelled token or it would leave the candidate and a stopped old
            // instance behind.
            await RestorePreviousAsync(application, definition, previous, previousWasRunning, candidateId, previousRenamed,
                proxyApplied, revision, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            var problem = exception switch
            {
                ProxyFailure proxyFailure => proxyFailure.ProblemCode,
                ApplicationDeploymentException domainFailure => domainFailure.ProblemCode,
                _ => ApplicationDeploymentProblemCodes.Failed,
            };
            var recovery = await RestorePreviousAsync(application, definition, previous, previousWasRunning, candidateId, previousRenamed,
                proxyApplied, revision, cancellationToken);
            logger.LogWarning("Deployment activation failed. ApplicationId={ApplicationId}, ProblemCode={ProblemCode}, Recovery={Recovery}",
                application.Id, problem, recovery.ProblemCode ?? "<none>");
            throw recovery.ProblemCode is { Length: > 0 }
                ? new DeploymentFailure(problem, recovery.ProblemCode)
                : new ApplicationDeploymentException(problem, 409);
        }
    }

    /// <summary>
    /// Best-effort restore of the previous instance. A failed recovery is reported on its own, so the
    /// original error is never replaced and a partial success is never presented as a success.
    /// </summary>
    private async Task<ApplicationDeploymentProxyResult> RestorePreviousAsync(
        ApplicationRecord application, ApplicationRecord definition, DockerContainerDto? previous, bool previousWasRunning, string? candidateId,
        bool previousRenamed, ApplicationDeploymentProxyResult? proxyApplied, RevisionRecord candidateRevision, CancellationToken cancellationToken)
    {
        try
        {
            if (candidateId is { Length: > 0 })
            {
                await runtime.StopAsync(candidateId, force: true, cancellationToken);
                await runtime.RemoveAsync(candidateId, cancellationToken);
            }
            secrets.ReleaseMaterialized(application.Id, candidateRevision.Id);

            if (proxyApplied is { PreviousDefinition: not null })
            {
                var reverted = await proxy.RevertAsync(definition, proxyApplied, cancellationToken);
                if (!reverted.Success) return new(false, ApplicationDeploymentProblemCodes.RecoveryFailed, null, null, null, null);
            }

            if (previous is null)
            {
                catalog.BindRuntime(application.Id, null, null, ApplicationDesiredState.Stopped);
                return new(true, ApplicationDeploymentProblemCodes.PreviousInstanceRestored, null, null, null, null);
            }

            if (previousRenamed)
            {
                var restoredName = await runtime.RenameAsync(previous.Id, ApplicationDeploymentValidation.ContainerName(application.Id), cancellationToken);
                if (!restoredName.Success) return new(false, ApplicationDeploymentProblemCodes.RecoveryFailed, null, null, null, null);
            }

            if (previousWasRunning)
            {
                var started = await runtime.StartAsync(previous.Id, cancellationToken);
                if (!started.Success) return new(false, ApplicationDeploymentProblemCodes.RecoveryFailed, null, null, null, null);
            }
            catalog.BindRuntime(application.Id, previous.Names, previous.Id, null);
            return new(true, ApplicationDeploymentProblemCodes.PreviousInstanceRestored, null, null, null, null);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            logger.LogWarning(exception, "Restoring the previous instance failed. ApplicationId={ApplicationId}", application.Id);
            return new(false, ApplicationDeploymentProblemCodes.RecoveryFailed, null, null, null, null);
        }
    }

    private async Task ApplyLifecycleAsync(ApplicationRecord application, DeploymentRequest request,
        IApplicationDeploymentProgress progress, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(new(DeploymentStage.Preflight, null, true), cancellationToken);
        await runtime.PreflightEngineAsync(cancellationToken);
        var container = await runtime.FindOwnedContainerAsync(application.Id,
            ApplicationDeploymentValidation.ContainerName(application.Id), cancellationToken)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.DriftContainerMissing, 409);
        var current = application.CurrentRevisionId is { } currentRevisionId ? catalog.FindRevision(currentRevisionId) : null;
        var definition = current is null ? application : EffectiveDefinition(application, current);

        if (request.Kind == DeploymentOperationKind.Stop)
        {
            await progress.ReportAsync(new(DeploymentStage.Deactivating, null, false), cancellationToken);
            var stopped = await runtime.StopAsync(container.Id, request.Force, cancellationToken);
            if (!stopped.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerStopFailed, 409);
            catalog.BindRuntime(application.Id, container.Names, container.Id, ApplicationDesiredState.Stopped);
            await progress.ReportAsync(new(DeploymentStage.Completed, null, false), cancellationToken);
            return;
        }

        await progress.ReportAsync(new(DeploymentStage.Activating, null, false), cancellationToken);
        if (request.Kind == DeploymentOperationKind.Restart)
        {
            var stopped = await runtime.StopAsync(container.Id, request.Force, cancellationToken);
            if (!stopped.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerStopFailed, 409);
        }
        var started = await runtime.StartAsync(container.Id, cancellationToken);
        if (!started.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerStartFailed, 409);

        await progress.ReportAsync(new(DeploymentStage.HealthChecking, null, false), cancellationToken);
        if (!await WaitForReadyAsync(definition, container.Id, cancellationToken))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.HealthCheckTimeout, 409);

        catalog.BindRuntime(application.Id, container.Names, container.Id, ApplicationDesiredState.Running);
        await progress.ReportAsync(new(DeploymentStage.Completed, null, false), cancellationToken);
    }

    private async Task DeleteAsync(ApplicationRecord application, Guid operationId, IApplicationDeploymentProgress progress,
        bool deleteVolumes, CancellationToken cancellationToken)
    {
        await progress.ReportAsync(new(DeploymentStage.Preflight, null, true), cancellationToken);
        // Removing managed resources requires a reachable engine, otherwise the record would be
        // dropped while its container kept running as an untracked orphan.
        await runtime.PreflightEngineAsync(cancellationToken);

        await progress.ReportAsync(new(DeploymentStage.Deactivating, null, false), cancellationToken);
        var containers = await runtime.FindApplicationContainersAsync(application.Id, cancellationToken);
        foreach (var container in containers)
        {
            await runtime.StopAsync(container.Id, force: true, cancellationToken);
            var removed = await runtime.RemoveAsync(container.Id, cancellationToken);
            if (!removed.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerRemoveFailed, 409);
        }

        await progress.ReportAsync(new(DeploymentStage.CleaningUp, null, false), cancellationToken);
        if (deleteVolumes)
        {
            var volumeNames = catalog.ReadRevisions(application.Id)
                .SelectMany(revision => revision.Volumes)
                .Select(volume => volume.Name)
                .Concat(application.Volumes.Select(volume => volume.Name))
                .Distinct(StringComparer.Ordinal);
            foreach (var volumeName in volumeNames)
            {
                if (!await runtime.VolumeExistsAsync(application.Id, volumeName, cancellationToken)) continue;
                var removed = await runtime.RemoveVolumeAsync(application.Id, volumeName, cancellationToken);
                if (!removed.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.VolumeRemoveFailed, 409);
            }
        }

        if (application.SiteId is { Length: > 0 } && application.HostPort is { } hostPort)
        {
            var detached = await proxy.RemoveAsync(application, hostPort, cancellationToken);
            if (!detached.Success) runtime.LogOperation("proxy-detach-failed", operationId, ("Application", application.Name), ("ProblemCode", detached.ProblemCode));
        }

        secrets.RemoveApplication(application.Id);
        foreach (var revision in catalog.ReadRevisions(application.Id))
            TryDeleteDirectory(Path.Combine(BuildRoot, revision.InputReference[..16]));
        catalog.Delete(application.Id);
        runtime.LogOperation("delete", operationId, ("Application", application.Name), ("Containers", containers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        await progress.ReportAsync(new(DeploymentStage.Completed, null, false), cancellationToken);
    }

    private async Task ProduceImageAsync(IApplicationTemplate template, DeploymentPlan plan, string contextDirectory, CancellationToken cancellationToken)
    {
        if (template is ImageTemplate)
        {
            var pull = await runtime.PullAsync(plan.ImageReference, null, cancellationToken);
            if (pull.Success) return;
            throw new ApplicationDeploymentException(pull.ProblemCode switch
            {
                "docker.not_installed" => ApplicationDeploymentProblemCodes.EngineNotInstalled,
                "docker.unavailable" => ApplicationDeploymentProblemCodes.EngineUnavailable,
                "docker.operation_timeout" => ApplicationDeploymentProblemCodes.RegistryUnreachable,
                "docker.permission_denied" => ApplicationDeploymentProblemCodes.RegistryAuthenticationFailed,
                _ => ApplicationDeploymentProblemCodes.ImageNotFound,
            }, 409);
        }

        var build = await runtime.BuildAsync(contextDirectory, plan.ImageReference, cancellationToken);
        if (!build.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.BuildFailed, 409);
    }

    /// <summary>
    /// Non-secret configuration becomes container environment; a secret becomes a read-only file
    /// under <see cref="SecretsMountPath"/> plus a <c>{NAME}_FILE</c> pointer, so its body never
    /// appears in the create request, the process list, or an inspect result.
    /// </summary>
    private static IReadOnlyList<string> BuildEnvironment(ApplicationRecord application, RevisionRecord revision)
    {
        var environment = new List<string>
        {
            $"RELAXKONOS_APPLICATION={application.Name}",
            $"RELAXKONOS_REVISION={revision.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"RELAXKONOS_READINESS={application.ReadinessLevel.ToString().ToLowerInvariant()}",
        };
        foreach (var entry in revision.Configuration)
        {
            if (entry.IsSecret)
            {
                environment.Add($"{entry.Name}_FILE={SecretsMountPath}/{entry.Name}");
                continue;
            }
            environment.Add($"{entry.Name}={entry.Value}");
        }
        return environment;
    }

    private async Task<bool> WaitForReadyAsync(ApplicationRecord application, string containerId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(options.HealthCheckTimeoutSeconds, 5, 3600));
        var interval = TimeSpan.FromSeconds(Math.Clamp(options.HealthCheckIntervalSeconds, 1, 30));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await runtime.IsReadyAsync(application, containerId, cancellationToken)) return true;

            // A container that already exited cannot become ready; fail fast instead of waiting.
            var details = await runtime.InspectAsync(containerId, cancellationToken);
            if (details is not null && details.State is "exited" or "dead") return false;
            await Task.Delay(interval, cancellationToken);
        }
        return false;
    }

    /// <summary>A definition that cannot be executed is rejected before any host side effect.</summary>
    private static void EnsureExecutableDefinition(ApplicationRecord application)
    {
        if (application.ReadinessLevel == ApplicationReadinessLevel.Http
            && (application.HostPort is null || string.IsNullOrWhiteSpace(application.HealthCheckPath)))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.InvalidRequest, 400);
    }

    private async Task StopOrThrowAsync(string containerId, CancellationToken cancellationToken)
    {
        var stopped = await runtime.StopAsync(containerId, force: false, cancellationToken);
        if (!stopped.Success) throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ContainerStopFailed, 409);
    }

    private static bool IsExpected(Exception exception) => exception
        is ApplicationDeploymentException or ProxyFailure or DeploymentFailure or IOException or InvalidOperationException or UnauthorizedAccessException;

    /// <summary>Execution uses the immutable revision snapshot. The mutable application record remains
    /// operator intent for the next release and must not leak into a rollback of an older revision.</summary>
    private static ApplicationRecord EffectiveDefinition(ApplicationRecord application, RevisionRecord revision) => application with
    {
        WorkloadKind = revision.WorkloadKind,
        ReadinessLevel = revision.ReadinessLevel,
        HealthCheckPath = revision.HealthCheckPath,
        ContainerPort = revision.ContainerPort,
        HostPort = revision.HostPort,
        BindAddress = revision.BindAddress,
        Limits = revision.Limits,
        Volumes = revision.Volumes,
        Configuration = revision.Configuration,
        SiteId = revision.SiteId,
    };

    private string DeploymentRoot => Path.Combine(environment.ContentRootPath, options.RootDirectory);
    private string BuildRoot => Path.Combine(DeploymentRoot, "build");

    /// <summary>Orders the request into a stable string so the fingerprint is reproducible.</summary>
    private static string Canonical(DeploymentSourceInputDto source) => string.Join('\u001e',
        source.ImageReference ?? "-", source.BaseImage ?? "-", source.ArchiveReferenceId ?? "-",
        source.RuntimeVersion ?? "-", source.ProgramEntry ?? "-",
        source.SelfContained ? "self-contained" : "framework-dependent",
        string.Join('\u001f', source.Arguments ?? []));

    /// <summary>Keeps build contexts bounded without ever reaching outside the deployment root.</summary>
    private void CleanupExpiredBuildContexts()
    {
        try
        {
            if (!Directory.Exists(BuildRoot)) return;
            var contexts = Directory.GetDirectories(BuildRoot).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(PreservedBuildContexts);
            foreach (var context in contexts) TryDeleteDirectory(context);
        }
        catch (Exception exception) when (IsExpected(exception)) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>An activation failure that also carries the outcome of reverting the reverse-proxy change.</summary>
internal sealed class ProxyFailure(string problemCode, string? revertedProblemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public string? RevertedProblemCode { get; } = revertedProblemCode;
}

/// <summary>A failure whose recovery outcome must be reported separately from the original error.</summary>
internal sealed class DeploymentFailure(string problemCode, string recoveryProblemCode) : Exception(problemCode)
{
    public string ProblemCode { get; } = problemCode;
    public string RecoveryProblemCode { get; } = recoveryProblemCode;
}

/// <summary>Log sanitization shared with the proxy domain so no credential ever reaches the client.</summary>
internal static class ApplicationDeploymentLogSanitizer
{
    public const int MaximumLineLength = 512;
    public static string Sanitize(string? line) => ProxyLogSanitizer.Sanitize(line, MaximumLineLength);
}
