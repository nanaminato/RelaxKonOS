using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Owns the lifecycle of every durable Compose stack operation. It lets an operation survive the HTTP request and the
/// phone that started it, and it is the single authority for idempotency and for serializing changes to
/// one project while other projects keep working.
///
/// The outcome is always derived from the Engine, never from the Compose source: after every command the
/// coordinator observes the project's containers and reports what is actually there. A deployment that
/// returns non-zero but leaves services running is a partial failure, not a failure, and a whole-project
/// atomic rollback is never claimed.
/// </summary>
internal sealed class DockerStackOperationCoordinator(
    DockerStackOperationStore operations,
    IServiceScopeFactory scopes,
    IOptions<DockerComposeOptions> options,
    IHostApplicationLifetime lifetime,
    IOperationalEventPublisher eventPublisher,
    ILogger<DockerStackOperationCoordinator> logger, IResidentModuleWorkLeaseSource leases) : IHostedService, IDisposable
{
    private readonly ResidentBackgroundWorkOwner workOwner = new(leases, ServerModules.DockerId);
    internal bool HasResponsibility => workOwner.HasResponsibility;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private static readonly TimeSpan ObservationRetryDelay = TimeSpan.FromSeconds(5);
    private bool ready;
    private bool stopping;

    /// <summary>Queues a deployment (or an update, which is the same Compose call), or returns the
    /// operation already bound to this idempotency key.</summary>
    public async Task<DockerStackOperationDto> DeployAsync(DockerStackDeployRequest request, string actor, string key, CancellationToken ct = default)
    {
        var definition = request.Definition;
        Preflight(definition);
        // The approval the operator gave is bound to one exact document. A submission whose source no
        // longer matches the preview it carries is refused instead of being applied silently.
        if (!string.Equals(request.DefinitionVersion, DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml), StringComparison.Ordinal))
            throw new DockerStackException(DockerStackProblem.DefinitionChanged, 409);

        return await StartOperationAsync(DockerStackOperationKind.Deploy, definition.Name, definition, actor, key,
            DockerStackValidation.Reference($"{DockerStackOperationKind.Deploy}|{definition.Name}|{request.DefinitionVersion}"), ct);
    }

    /// <summary>Queues a whole-project lifecycle action.</summary>
    public async Task<DockerStackOperationDto> ApplyActionAsync(string name, DockerStackOperationKind kind, bool confirmed, string actor, string key, CancellationToken ct = default)
    {
        if (!DockerStackValidation.IsValidProjectName(name))
            throw new DockerStackException(DockerStackProblem.InvalidName, 400);
        if (kind == DockerStackOperationKind.Delete && !confirmed)
            throw new DockerStackException(DockerStackProblem.ConfirmationRequired, 400);
        // The confirmation flag is part of the request identity: a key that was first used for an
        // unconfirmed attempt can never be replayed as if the operator had approved a delete.
        return await StartOperationAsync(kind, name, null, actor, key, DockerStackValidation.Reference($"{kind}|{name}|{confirmed}"), ct);
    }

    public DockerStackOperationDto? Get(Guid operationId) => operations.Get(operationId)?.Operation;

    public bool HoldsCleanupResponsibility(Guid operationId) => operations.Get(operationId) is { CleanupPending: true };

    public DockerStackOperationDto? GetByRequest(string actor, string key)
    {
        ValidateKey(key);
        return operations.FindRequest(actor, key)?.Operation;
    }

    public DockerStackOperationDto? GetActive(string project) => operations.GetActive(project)?.Operation;

    public IReadOnlyList<DockerStackOperationDto> History(string project, int limit)
    {
        if (!DockerStackValidation.IsValidProjectName(project))
            throw new DockerStackException(DockerStackProblem.InvalidName, 400);
        return [.. operations.History(project, limit).Select(entry => entry.Operation)];
    }

    public DockerStackOperationDiagnosticsDto Diagnostics(Guid operationId) =>
        new(operations.Diagnostics(operationId), operations.DiagnosticsTruncated(operationId));

    /// <summary>Requests cancellation. Only the worker acknowledges it and releases its resources.</summary>
    public DockerStackOperationDto Cancel(Guid operationId, string key)
    {
        ValidateKey(key);
        lock (gate)
        {
            var entry = operations.Get(operationId) ?? throw new DockerStackException(DockerStackProblem.OperationNotFound, 404);
            if (!DockerStackOperationStore.Active(entry.Operation)) return entry.Operation;
            if (!entry.Operation.Cancellable || !cancellations.TryGetValue(operationId, out var source))
                throw new DockerStackException(DockerStackProblem.NotCancellable);

            var requested = operations.Update(operationId, operation => operation with { Cancellable = false }, "cancel-requested", cleanupPending: true);
            source.Cancel();
            return requested;
        }
    }

    private async Task<DockerStackOperationDto> StartOperationAsync(DockerStackOperationKind kind, string project, DockerStackDefinitionDto? definition,
        string actor, string key, string requestReference, CancellationToken ct)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        if (operations.FindIdempotent(kind, actor, key, requestReference) is { } previous) return previous.Operation;
        using var work = await workOwner.AdmitAsync(ct);

        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new DockerStackException(DockerStackProblem.StoreUnavailable, 503);
            work.Token.ThrowIfCancellationRequested();
            var replay = operations.FindIdempotent(kind, actor, key, requestReference);
            if (replay is not null) return replay.Operation;
            // The ceiling is decided before the record is written, so a rejected request never leaves a
            // queued operation behind that nothing will ever run.
            if (running.Count >= Math.Max(1, options.Value.MaximumConcurrentOperations))
                throw new DockerStackException(DockerStackProblem.OperationConflict);

            StackOperationEntry entry;
            bool created;
            try { entry = operations.Create(project, kind, actor, key, requestReference, out created); }
            catch (Exception error) when (error is not DockerStackException rejected || rejected.ProblemCode == DockerStackProblem.StoreUnavailable)
            { work.Fail(error); throw; }
            if (created)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping, work.Token);
                cancellations[entry.Operation.OperationId] = cancellation;
                running[entry.Operation.OperationId] = work.Completion;
                work.Run(stopping => RunAsync(entry.Operation, kind, project, definition, cancellation, stopping));
            }
            return entry.Operation;
        }
    }

    private async Task RunAsync(DockerStackOperationDto operation, DockerStackOperationKind kind, string project,
        DockerStackDefinitionDto? definition, CancellationTokenSource cancellation, CancellationToken workStopping)
    {
        var id = operation.OperationId;
        try
        {
            Outcome? outcome;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var compose = scope.ServiceProvider.GetRequiredService<IDockerComposeService>();
                outcome = await ExecuteAsync(compose, id, kind, project, definition, cancellation, workStopping);
            }
            if (cancellation.IsCancellationRequested)
                operations.Update(id, current => current.Stage == DockerStackOperationStage.Applying
                    ? current with { Stage = DockerStackOperationStage.Observing }
                    : current, "cleanup-drained", cleanupPending: false, executionPending: false);
            else if (outcome is { AwaitingObservation: true })
                operations.Update(id, current => current, "execution-drained", executionPending: false);
            if (outcome is { AwaitingObservation: true })
            {
                using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(workStopping, lifetime.ApplicationStopping);
                IReadOnlyList<DockerStackServiceDto>? observed;
                do
                {
                    await Task.Delay(ObservationRetryDelay, shutdown.Token);
                    observed = await TryObserveProjectAsync(id, project, shutdown.Token);
                } while (observed is null);
                shutdown.Token.ThrowIfCancellationRequested();
                outcome = outcome with { Observed = observed, Recovery = RecoveryCode(kind, observed) };
            }
            if (outcome is not null)
                Complete(id, outcome.State, outcome.Problem, outcome.Recovery, outcome.Messages, outcome.Observed);
        }
        catch (DockerComposeCleanupException)
        {
            RecordUnverifiedCleanup(id);
            throw;
        }
        catch (OperationCanceledException) when (workStopping.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // The durable active receipt remains available to startup reconciliation.
        }
        finally
        {
            lock (gate) { cancellations.TryRemove(id, out _); cancellation.Dispose(); }
            running.TryRemove(id, out _);
        }
    }

    private void RecordUnverifiedCleanup(Guid operationId) =>
        operations.Update(operationId, current => current with { Cancellable = false }, "cleanup-unverified",
            ["Compose process or output cleanup could not be verified"], cleanupPending: true);

    private sealed record Outcome(DockerStackOperationState State, string? Problem, string? Recovery,
        IReadOnlyList<string> Messages, IReadOnlyList<DockerStackServiceDto>? Observed = null, bool AwaitingObservation = false);

    private async Task<Outcome?> ExecuteAsync(IDockerComposeService compose, Guid id, DockerStackOperationKind kind, string project,
        DockerStackDefinitionDto? definition, CancellationTokenSource cancellation, CancellationToken workStopping)
    {
        var messages = new List<string>();
        var mutationMayHaveStarted = false;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                operations.Update(id, current => current with
                {
                    State = DockerStackOperationState.Running,
                    Stage = DockerStackOperationStage.Preflight,
                    StartedAt = DateTimeOffset.UtcNow,
                }, "started", executionPending: true);
            }
            catch (Exception error) { throw new ResidentWorkCompletionException(error); }

            IReadOnlyList<string> desired = [];
            if (kind == DockerStackOperationKind.Deploy)
            {
                operations.Update(id, current => current with { Stage = DockerStackOperationStage.Parsing }, "stage");
                var preview = await compose.PreviewAsync(definition!, cancellation.Token);
                desired = [.. preview.Services.Select(service => service.Service)];
                messages.Add($"parsed {preview.Services.Count} service(s) from the definition");
            }

            operations.Update(id, current => current with { Stage = DockerStackOperationStage.Applying }, "stage");
            cancellation.Token.ThrowIfCancellationRequested();
            mutationMayHaveStarted = true;
            var result = kind == DockerStackOperationKind.Deploy
                ? await compose.DeployAsync(definition!, cancellation.Token)
                : await compose.ApplyActionAsync(project, kind, confirmed: true, cancellation.Token);
            messages.AddRange(result.Messages);

            cancellation.Token.ThrowIfCancellationRequested();
            operations.Update(id, current => current with { Stage = DockerStackOperationStage.Observing }, "stage");
            var observed = await TryObserveAsync(compose, project, cancellation.Token);
            if (observed is null)
                return AwaitObservation(id, messages);
            Observe(id, observed);

            var (state, problem, recovery) = Classify(kind, result, desired, observed);
            if (state == DockerStackOperationState.PartialFailed) messages.Add("some services are not in their desired state; the project was left as observed");
            return new(state, problem, recovery, messages);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A process that is stopping has not failed: it leaves the operation active so the next
            // startup reconciles it against the Engine instead of reporting a phantom outcome.
            var stopping = lifetime.ApplicationStopping.IsCancellationRequested || workStopping.IsCancellationRequested;
            if (stopping) return null;
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(workStopping, lifetime.ApplicationStopping);
            var observed = mutationMayHaveStarted
                ? await TryObserveAsync(compose, project, shutdown.Token)
                : await SafeObserveAsync(compose, project);
            if (observed is null) return AwaitObservation(id, messages);
            return new(DockerStackOperationState.Cancelled, DockerStackProblem.Cancelled, null, messages, observed);
        }
        catch (DockerStackException failure)
        {
            var observed = await SafeObserveAsync(compose, project);
            var state = observed.Count > 0 ? DockerStackOperationState.PartialFailed : DockerStackOperationState.Failed;
            return new(state, failure.ProblemCode, state == DockerStackOperationState.PartialFailed ? DockerStackProblem.PartialFailure : null, messages, observed);
        }
        catch (Exception exception) when (exception is not (ResidentWorkCompletionException or DockerComposeCleanupException))
        {
            logger.LogWarning("Stack operation {OperationId} for project {Project} failed before an outcome could be classified. {ExceptionType}",
                id, project, exception.GetType().Name);
            var observed = await SafeObserveAsync(compose, project);
            var state = observed.Count > 0 ? DockerStackOperationState.PartialFailed : DockerStackOperationState.Failed;
            return new(state, DockerStackProblem.ComposeFailed, state == DockerStackOperationState.PartialFailed ? DockerStackProblem.PartialFailure : null, messages, observed);
        }
    }

    private Outcome AwaitObservation(Guid id, List<string> messages)
    {
        messages.Add("Engine observation was unavailable after dispatch; recovery only observes the project");
        lock (gate) operations.Update(id, current => current with
        {
            Stage = DockerStackOperationStage.Observing,
            Cancellable = false,
        }, "observation-unavailable", messages);
        return new(DockerStackOperationState.Interrupted, DockerStackProblem.Interrupted, null, messages, AwaitingObservation: true);
    }

    /// <summary>
    /// Turns "the command ran" plus "here is what the Engine actually runs" into a terminal outcome.
    /// The desired set comes from the Compose parser for a deployment and from the observed set itself
    /// for a lifecycle action, so a service that never appeared still counts as missing.
    /// </summary>
    private static (DockerStackOperationState State, string? Problem, string? Recovery) Classify(
        DockerStackOperationKind kind, DockerStackMutationResult result, IReadOnlyList<string> desired, IReadOnlyList<DockerStackServiceDto> observed)
    {
        if (kind == DockerStackOperationKind.Delete)
        {
            if (observed.Count > 0) return (DockerStackOperationState.PartialFailed, DockerStackProblem.PartialFailure, null);
            return result.Success
                ? (DockerStackOperationState.Succeeded, null, null)
                : (DockerStackOperationState.Failed, result.ProblemCode, null);
        }

        if (!result.Success)
        {
            return observed.Count == 0
                ? (DockerStackOperationState.Failed, result.ProblemCode, null)
                : (DockerStackOperationState.PartialFailed, result.ProblemCode, DockerStackProblem.PartialFailure);
        }

        var services = desired.Count > 0 ? desired : [.. observed.Select(service => service.Service)];
        var unsatisfied = kind == DockerStackOperationKind.Stop
            ? services.Where(service => observed.Any(item => item.Service == service && IsRunning(item.State))).ToArray()
            : services.Where(service => !observed.Any(item => item.Service == service && IsRunning(item.State))).ToArray();
        // The command succeeded, so the partial failure is the whole verdict and there is no second
        // code to add: a recovery code that merely repeats the primary one tells a caller nothing.
        return unsatisfied.Length == 0
            ? (DockerStackOperationState.Succeeded, null, null)
            : (DockerStackOperationState.PartialFailed, DockerStackProblem.PartialFailure, null);
    }

    private static bool IsRunning(string state) => state.Equals("running", StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<DockerStackServiceDto>> SafeObserveAsync(IDockerComposeService compose, string project)
    {
        try { return await compose.ListServicesAsync(project, CancellationToken.None); }
        catch (Exception error) when (error is not DockerComposeCleanupException) { return []; }
    }

    private void Observe(Guid id, IReadOnlyList<DockerStackServiceDto> observed) =>
        operations.Update(id, current => current with { Services = observed }, "observed");

    private void Complete(Guid id, DockerStackOperationState state, string? problem, string? recovery,
        IReadOnlyList<string> messages, IReadOnlyList<DockerStackServiceDto>? observed = null)
    {
        try
        {
            var stage = state switch
            {
                DockerStackOperationState.Succeeded => DockerStackOperationStage.Completed,
                DockerStackOperationState.Cancelled => DockerStackOperationStage.Cancelled,
                DockerStackOperationState.Interrupted => DockerStackOperationStage.Interrupted,
                _ => DockerStackOperationStage.Failed,
            };
            var terminal = operations.Update(id, operation => operation with
            {
                State = state,
                Stage = stage,
                ProblemCode = problem,
                RecoveryProblemCode = recovery,
                CompletedAt = DateTimeOffset.UtcNow,
                Cancellable = false,
                Services = observed ?? operation.Services,
            }, "completed", messages);
            PublishTerminalSignal(terminal);
        }
        catch (Exception exception) { throw new ResidentWorkCompletionException(exception); }
    }

    private void PublishTerminalSignal(DockerStackOperationDto terminal)
    {
        if (terminal.State is not (DockerStackOperationState.Failed or DockerStackOperationState.PartialFailed
            or DockerStackOperationState.Succeeded)) return;
        try
        {
            var projectHash = SHA256.HashData(Encoding.UTF8.GetBytes("docker-stack:" + terminal.ProjectName));
            var projectId = new Guid(projectHash.AsSpan(0, 16));
            eventPublisher.PublishAsync(new OperationalEventSignal(
                $"docker-stack-terminal:{terminal.OperationId:D}:{terminal.State}",
                "docker.operation_failed", projectId, Guid.NewGuid(),
                terminal.ProblemCode ?? terminal.RecoveryProblemCode ?? "docker.recovered",
                terminal.OperationId, IsRecovery: terminal.State == DockerStackOperationState.Succeeded)).GetAwaiter().GetResult();
        }
        catch
        {
            logger.LogWarning("Event Alert Center did not record terminal Compose operation {OperationId}.", terminal.OperationId);
        }
    }

    private static void Preflight(DockerStackDefinitionDto definition)
    {
        if (!DockerStackValidation.IsValidProjectName(definition.Name))
            throw new DockerStackException(DockerStackProblem.InvalidName, 400);
        if (string.IsNullOrWhiteSpace(definition.ComposeYaml))
            throw new DockerStackException(DockerStackProblem.InvalidCompose, 400);
        if (!DockerComposeSubsetValidation.IsSupported(definition.ComposeYaml, out var problem))
            throw new DockerStackException(problem, 400);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(character => character < 33 || character > 126))
            throw new DockerStackException(DockerStackProblem.IdempotencyRequired, 400);
    }

    /// <summary>
    /// Startup never replays active operations. Unconfirmed command and observation scopes stay protected.
    /// Operations whose prior scope drained can be reconciled against the Engine.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var recoveryWork = await workOwner.AdmitAsync(cancellationToken);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, recoveryWork.Token);
        List<StackOperationEntry> awaitingEngine;
        bool awaitingCleanup;
        try
        {
            var active = operations.Read().Where(entry => DockerStackOperationStore.Active(entry.Operation)).ToArray();
            awaitingCleanup = active.Any(RequiresCleanupEvidence);
            awaitingEngine = await ReconcileAsync(active.Where(entry => !RequiresCleanupEvidence(entry)), startup.Token);
        }
        catch (OperationCanceledException) when (startup.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            recoveryWork.Fail(error);
            logger.LogError(error, "Compose startup recovery did not complete; its work remains quarantined.");
            return;
        }
        lock (gate)
        {
            if (stopping) return;
            startup.Token.ThrowIfCancellationRequested();
            if (awaitingEngine.Count != 0 || awaitingCleanup) recoveryWork.Run(ct => RetryRecoveryAsync(awaitingEngine, awaitingCleanup, ct));
            ready = true;
        }
    }

    private static bool RequiresCleanupEvidence(StackOperationEntry entry) =>
        entry.Operation.State == DockerStackOperationState.Running
        && (entry.ExecutionPending is not false || entry.CleanupPending || entry.Operation.Stage == DockerStackOperationStage.Applying);

    private async Task RetryRecoveryAsync(List<StackOperationEntry> awaitingEngine, bool awaitingCleanup, CancellationToken ct)
    {
        try
        {
            while (awaitingEngine.Count != 0)
            {
                await Task.Delay(ObservationRetryDelay, ct);
                awaitingEngine = await ReconcileAsync(awaitingEngine, ct);
            }
            if (awaitingCleanup) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Compose recovery could not continue; its work remains quarantined.");
            throw;
        }
    }

    private async Task<List<StackOperationEntry>> ReconcileAsync(IEnumerable<StackOperationEntry> entries, CancellationToken ct)
    {
        List<StackOperationEntry> awaitingEngine = [];
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var operation = entry.Operation;
            IReadOnlyList<DockerStackServiceDto> observed = [];
            if (operation.State == DockerStackOperationState.Running)
            {
                IReadOnlyList<DockerStackServiceDto>? snapshot;
                try { snapshot = await TryObserveProjectAsync(operation.OperationId, operation.ProjectName, ct); }
                catch (DockerComposeCleanupException)
                {
                    RecordUnverifiedCleanup(operation.OperationId);
                    throw;
                }
                if (snapshot is null)
                {
                    awaitingEngine.Add(entry);
                    continue;
                }
                observed = snapshot;
            }
            ct.ThrowIfCancellationRequested();
            var message = operation.State == DockerStackOperationState.Running
                ? "the server restarted while this operation was active; the project was observed, not replayed"
                : "the server restarted before this operation started; it was not replayed";
            Complete(operation.OperationId, DockerStackOperationState.Interrupted, DockerStackProblem.Interrupted,
                RecoveryCode(operation.Kind, observed), [message], observed);
        }
        return awaitingEngine;
    }

    private async Task<IReadOnlyList<DockerStackServiceDto>?> TryObserveProjectAsync(Guid id, string project, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        operations.Update(id, current => current, "observation-started", executionPending: true);
        IReadOnlyList<DockerStackServiceDto>? observed = null;
        await using (var scope = scopes.CreateAsyncScope())
        {
            try { observed = await TryObserveAsync(scope.ServiceProvider.GetRequiredService<IDockerComposeService>(), project, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        operations.Update(id, current => current, "observation-drained", executionPending: false);
        ct.ThrowIfCancellationRequested();
        return observed;
    }

    private static async Task<IReadOnlyList<DockerStackServiceDto>?> TryObserveAsync(IDockerComposeService compose, string project, CancellationToken ct)
    {
        try { return await compose.ListServicesAsync(project, ct); }
        catch (DockerStackException error) when (error.ProblemCode == DockerStackProblem.EngineUnavailable) { return null; }
    }

    /// <summary>Which follow-up the operator most likely needs after an unverified operation. A project
    /// left holding containers after a delete, or a deployment that left nothing running, are the two
    /// cases where doing nothing is not an option.</summary>
    private static string? RecoveryCode(DockerStackOperationKind kind, IReadOnlyList<DockerStackServiceDto> observed)
    {
        if (observed.Count == 0) return kind is DockerStackOperationKind.Deploy or DockerStackOperationKind.Start or DockerStackOperationKind.Restart
            ? DockerStackProblem.PartialFailure
            : null;
        return kind == DockerStackOperationKind.Delete ? DockerStackProblem.PartialFailure : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            stopping = true;
            ready = false;
        }
        await workOwner.StopAsync(cancellationToken);
    }
    public void Dispose() => workOwner.Dispose();
}
