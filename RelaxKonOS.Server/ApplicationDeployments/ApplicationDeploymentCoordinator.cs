using System.Collections.Concurrent;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Owns the lifecycle of every deployment operation. It is the only caller of
/// <see cref="ApplicationDeploymentService"/>, it is what makes an operation durable across a process
/// restart, and it is the single authority for idempotency and for serializing changes to one
/// application while other applications keep working.
/// </summary>
internal sealed class ApplicationDeploymentCoordinator(
    ApplicationDeploymentOperationStore operations,
    ApplicationDeploymentCatalogStore catalog,
    ApplicationDeploymentService service,
    ApplicationDeploymentOptions options,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private bool ready;

    /// <summary>
    /// Queues one operation, or returns the operation already bound to this idempotency key. The same
    /// key with a different request conflicts instead of silently reusing an unrelated result.
    /// </summary>
    public DeploymentOperationDto Start(DeploymentRequest request, string actor, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(character => character < 33 || character > 126))
            throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.IdempotencyRequired, 400);

        var application = catalog.Find(request.ApplicationId)
            ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ApplicationNotFound, 404);
        var fingerprint = ApplicationDeploymentService.Fingerprint(request);

        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
            // The concurrency ceiling is decided before the record is written, so a rejected request
            // never leaves a queued operation behind that nothing will ever run.
            if (running.Count >= Math.Max(1, options.MaximumConcurrentOperations))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.ResourceConflict);

            var entry = operations.Create(application.Id, application.Name, request.Kind, actor, key, fingerprint,
                ApplicationDeploymentService.Resources(application.Id), out var created);
            if (created)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                cancellations[entry.Operation.OperationId] = cancellation;
                running[entry.Operation.OperationId] = Task.Run(
                    () => RunAsync(entry.Operation, request, actor, cancellation), CancellationToken.None);
            }
            return entry.Operation;
        }
    }

    public DeploymentOperationDto? Get(Guid operationId) => operations.Get(operationId)?.Operation;

    public DeploymentOperationDto? GetActive(Guid applicationId) => operations.GetActive(applicationId)?.Operation;

    /// <summary>Requests cancellation. Only the worker acknowledges it and releases its resources.</summary>
    public DeploymentOperationDto Cancel(Guid operationId)
    {
        lock (gate)
        {
            var entry = operations.Get(operationId)
                ?? throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.OperationNotFound, 404);
            if (!ApplicationDeploymentOperationStore.Active(entry.Operation)) return entry.Operation;
            if (!entry.Operation.Cancellable || !cancellations.TryGetValue(operationId, out var source))
                throw new ApplicationDeploymentException(ApplicationDeploymentProblemCodes.NotCancellable);

            var requested = operations.Update(operationId, operation => operation with { Cancellable = false }, "cancel-requested");
            source.Cancel();
            return requested;
        }
    }

    private async Task RunAsync(DeploymentOperationDto operation, DeploymentRequest request, string actor, CancellationTokenSource cancellation)
    {
        var id = operation.OperationId;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            operations.Update(id, current => current with
            {
                State = DeploymentOperationState.Running,
                Stage = DeploymentStage.Preflight,
                StartedAt = DateTimeOffset.UtcNow,
            }, "started");

            await service.StartAsync(id, request, actor, new Reporter(operations, gate, id, cancellation.Token), cancellation.Token);
            Complete(id, DeploymentOperationState.Succeeded, null, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A process that is stopping has not failed: it leaves the operation interrupted so the
            // next startup reconciles it instead of reporting a phantom failure.
            var stopping = lifetime.ApplicationStopping.IsCancellationRequested;
            Complete(id, stopping ? DeploymentOperationState.Interrupted : DeploymentOperationState.Cancelled, null,
                stopping ? ApplicationDeploymentProblemCodes.Interrupted : ApplicationDeploymentProblemCodes.Cancelled);
        }
        catch (DeploymentFailure failure)
        {
            Complete(id, DeploymentOperationState.Failed, failure.ProblemCode, failure.RecoveryProblemCode);
        }
        catch (ApplicationDeploymentException exception)
        {
            Complete(id, DeploymentOperationState.Failed, exception.ProblemCode, null);
        }
        catch
        {
            Complete(id, DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Failed, null);
        }
        finally
        {
            lock (gate) { cancellations.TryRemove(id, out _); cancellation.Dispose(); }
            running.TryRemove(id, out _);
        }
    }

    private void Complete(Guid id, DeploymentOperationState state, string? problem, string? recovery)
    {
        try
        {
            operations.Update(id, operation => operation with
            {
                State = state,
                Stage = state switch
                {
                    DeploymentOperationState.Succeeded => DeploymentStage.Completed,
                    DeploymentOperationState.Cancelled => DeploymentStage.Cancelled,
                    DeploymentOperationState.Interrupted => DeploymentStage.Interrupted,
                    _ => DeploymentStage.Failed,
                },
                Progress = null,
                ProblemCode = problem,
                RecoveryProblemCode = recovery,
                CompletedAt = DateTimeOffset.UtcNow,
                Cancellable = false,
            }, "completed");
        }
        catch (Exception exception) when (exception is ApplicationDeploymentException or InvalidOperationException)
        {
            // The store has failed closed. The durable Running record is what the next startup
            // reconciles, so the operation is not lost by failing to write its outcome now.
        }
    }

    /// <summary>
    /// Startup reconciliation. An operation that was queued or running when the process stopped is
    /// never replayed — a half-applied deployment must not be resumed blindly — so residue is removed,
    /// the previous instance is restored where possible, and the outcome is recorded as interrupted.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        DeploymentEntry[] entries;
        try { entries = operations.Read(); }
        catch (ApplicationDeploymentException) { return; }

        foreach (var entry in entries.Where(x => ApplicationDeploymentOperationStore.Active(x.Operation)))
        {
            // A queued record means the process stopped before its worker was ever launched, so no
            // side effect exists and there is nothing to recover.
            var recovery = new ApplicationDeploymentRecovery(DeploymentOperationState.Interrupted,
                ApplicationDeploymentProblemCodes.Interrupted, null);
            if (entry.Operation.State == DeploymentOperationState.Running)
            {
                try { recovery = await service.RecoverAsync(entry.Operation, cancellationToken); }
                catch
                {
                    recovery = new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Interrupted,
                        ApplicationDeploymentProblemCodes.RecoveryUnknown);
                }
            }
            if (recovery.State is not (DeploymentOperationState.Succeeded or DeploymentOperationState.Failed
                or DeploymentOperationState.Interrupted))
                recovery = new(DeploymentOperationState.Failed, ApplicationDeploymentProblemCodes.Interrupted,
                    ApplicationDeploymentProblemCodes.RecoveryUnknown);

            Complete(entry.Operation.OperationId, recovery.State, recovery.ProblemCode, recovery.RecoveryProblemCode);
        }
        lock (gate) ready = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) ready = false;
        try { await Task.WhenAll(running.Values).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Persists in-stage progress. It writes nothing once cancellation has been requested, so a
    /// cancelled operation cannot be given a stage after its cancel request was recorded.
    ///
    /// The ledger and the gate arrive as constructor arguments rather than being reached through the
    /// owner, because a nested type cannot read its enclosing type's primary-constructor parameters.
    /// </summary>
    private sealed class Reporter(
        ApplicationDeploymentOperationStore operations,
        object gate,
        Guid id,
        CancellationToken workerToken) : IApplicationDeploymentProgress
    {
        public Task ReportAsync(ApplicationDeploymentProgress progress, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                workerToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                operations.Update(id, operation => operation with
                {
                    Stage = progress.Stage,
                    Progress = progress.Progress,
                    Cancellable = progress.Cancellable,
                }, "stage");
            }
            return Task.CompletedTask;
        }
    }
}
