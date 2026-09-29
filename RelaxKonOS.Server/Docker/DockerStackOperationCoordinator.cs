using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.EventAlerts;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// Owns the lifecycle of every durable Compose stack operation. It is the only caller of
/// <see cref="IDockerComposeService" />, it is what makes an operation survive the HTTP request and the
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
    IDockerComposeService compose,
    IOptions<DockerComposeOptions> options,
    IHostApplicationLifetime lifetime,
    IOperationalEventPublisher eventPublisher,
    ILogger<DockerStackOperationCoordinator> logger) : IHostedService
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private bool ready;

    /// <summary>Queues a deployment (or an update, which is the same Compose call), or returns the
    /// operation already bound to this idempotency key.</summary>
    public DockerStackOperationDto Deploy(DockerStackDeployRequest request, string actor, string key)
    {
        var definition = request.Definition;
        Preflight(definition);
        // The approval the operator gave is bound to one exact document. A submission whose source no
        // longer matches the preview it carries is refused instead of being applied silently.
        if (!string.Equals(request.DefinitionVersion, DockerStackValidation.DefinitionVersion(definition.Name, definition.ComposeYaml), StringComparison.Ordinal))
            throw new DockerStackException(DockerStackProblem.DefinitionChanged, 409);

        return Start(DockerStackOperationKind.Deploy, definition.Name, definition, actor, key,
            DockerStackValidation.Reference($"{DockerStackOperationKind.Deploy}|{definition.Name}|{request.DefinitionVersion}"));
    }

    /// <summary>Queues a whole-project lifecycle action.</summary>
    public DockerStackOperationDto ApplyAction(string name, DockerStackOperationKind kind, bool confirmed, string actor, string key)
    {
        if (!DockerStackValidation.IsValidProjectName(name))
            throw new DockerStackException(DockerStackProblem.InvalidName, 400);
        if (kind == DockerStackOperationKind.Delete && !confirmed)
            throw new DockerStackException(DockerStackProblem.ConfirmationRequired, 400);
        // The confirmation flag is part of the request identity: a key that was first used for an
        // unconfirmed attempt can never be replayed as if the operator had approved a delete.
        return Start(kind, name, null, actor, key, DockerStackValidation.Reference($"{kind}|{name}|{confirmed}"));
    }

    public DockerStackOperationDto? Get(Guid operationId) => operations.Get(operationId)?.Operation;

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

            var requested = operations.Update(operationId, operation => operation with { Cancellable = false }, "cancel-requested");
            source.Cancel();
            return requested;
        }
    }

    private DockerStackOperationDto Start(DockerStackOperationKind kind, string project, DockerStackDefinitionDto? definition,
        string actor, string key, string requestReference)
    {
        ValidateKey(key);

        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new DockerStackException(DockerStackProblem.StoreUnavailable, 503);
            var replay = operations.FindIdempotent(kind, actor, key, requestReference);
            if (replay is not null) return replay.Operation;
            // The ceiling is decided before the record is written, so a rejected request never leaves a
            // queued operation behind that nothing will ever run.
            if (running.Count >= Math.Max(1, options.Value.MaximumConcurrentOperations))
                throw new DockerStackException(DockerStackProblem.OperationConflict);

            var entry = operations.Create(project, kind, actor, key, requestReference, out var created);
            if (created)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                cancellations[entry.Operation.OperationId] = cancellation;
                running[entry.Operation.OperationId] = Task.Run(
                    () => RunAsync(entry.Operation, kind, project, definition, cancellation), CancellationToken.None);
            }
            return entry.Operation;
        }
    }

    private async Task RunAsync(DockerStackOperationDto operation, DockerStackOperationKind kind, string project,
        DockerStackDefinitionDto? definition, CancellationTokenSource cancellation)
    {
        var id = operation.OperationId;
        var messages = new List<string>();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            operations.Update(id, current => current with
            {
                State = DockerStackOperationState.Running,
                Stage = DockerStackOperationStage.Preflight,
                StartedAt = DateTimeOffset.UtcNow,
            }, "started");

            IReadOnlyList<string> desired = [];
            if (kind == DockerStackOperationKind.Deploy)
            {
                operations.Update(id, current => current with { Stage = DockerStackOperationStage.Parsing }, "stage");
                var preview = await compose.PreviewAsync(definition!, cancellation.Token);
                desired = [.. preview.Services.Select(service => service.Service)];
                messages.Add($"parsed {preview.Services.Count} service(s) from the definition");
            }

            operations.Update(id, current => current with { Stage = DockerStackOperationStage.Applying }, "stage");
            var result = kind == DockerStackOperationKind.Deploy
                ? await compose.DeployAsync(definition!, cancellation.Token)
                : await compose.ApplyActionAsync(project, kind, confirmed: true, cancellation.Token);
            messages.AddRange(result.Messages);

            cancellation.Token.ThrowIfCancellationRequested();
            operations.Update(id, current => current with { Stage = DockerStackOperationStage.Observing }, "stage");
            var observed = await compose.ListServicesAsync(project, cancellation.Token);
            Observe(id, observed);

            var (state, problem, recovery) = Classify(kind, result, desired, observed);
            if (state == DockerStackOperationState.PartialFailed) messages.Add("some services are not in their desired state; the project was left as observed");
            Complete(id, state, problem, recovery, messages);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A process that is stopping has not failed: it leaves the operation active so the next
            // startup reconciles it against the Engine instead of reporting a phantom outcome.
            var stopping = lifetime.ApplicationStopping.IsCancellationRequested;
            if (stopping) return;
            var observed = await SafeObserveAsync(project);
            Complete(id, DockerStackOperationState.Cancelled, DockerStackProblem.Cancelled, null, messages, observed);
        }
        catch (DockerStackException failure)
        {
            var observed = await SafeObserveAsync(project);
            var state = observed.Count > 0 ? DockerStackOperationState.PartialFailed : DockerStackOperationState.Failed;
            Complete(id, state, failure.ProblemCode, state == DockerStackOperationState.PartialFailed ? DockerStackProblem.PartialFailure : null, messages, observed);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Stack operation {OperationId} for project {Project} failed before an outcome could be classified. {ExceptionType}",
                id, project, exception.GetType().Name);
            var observed = await SafeObserveAsync(project);
            var state = observed.Count > 0 ? DockerStackOperationState.PartialFailed : DockerStackOperationState.Failed;
            Complete(id, state, DockerStackProblem.ComposeFailed, state == DockerStackOperationState.PartialFailed ? DockerStackProblem.PartialFailure : null, messages, observed);
        }
        finally
        {
            lock (gate) { cancellations.TryRemove(id, out _); cancellation.Dispose(); }
            running.TryRemove(id, out _);
        }
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

    private async Task<IReadOnlyList<DockerStackServiceDto>> SafeObserveAsync(string project)
    {
        try { return await compose.ListServicesAsync(project, CancellationToken.None); }
        catch { return []; }
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
        catch (Exception exception) when (exception is DockerStackException or InvalidOperationException)
        {
            // The ledger has failed closed. The durable Running record is what the next startup
            // reconciles, so the operation is not lost by failing to write its outcome now.
        }
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
    /// Startup reconciliation. An operation that was queued or running when the process stopped is never
    /// replayed — a half-applied project must not be resumed blindly — so the project is observed, the
    /// observed services are recorded, and the outcome is reported as unverified. Losing contact with the
    /// Engine is never upgraded into a success.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StackOperationEntry[] entries;
        try { entries = operations.Read(); }
        catch (DockerStackException) { return; }

        foreach (var entry in entries.Where(x => DockerStackOperationStore.Active(x.Operation)))
        {
            var observed = entry.Operation.State == DockerStackOperationState.Running
                ? await SafeObserveAsync(entry.Operation.ProjectName)
                : [];
            var recovery = RecoveryCode(entry.Operation.Kind, observed);
            Complete(entry.Operation.OperationId, DockerStackOperationState.Interrupted, DockerStackProblem.Interrupted, recovery,
                ["the server restarted while this operation was active; the project was observed, not replayed"], observed);
        }
        lock (gate) ready = true;
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
        lock (gate) ready = false;
        try { await Task.WhenAll(running.Values).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }
}
