using System.Collections.Concurrent;
using System.Text.Json;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.TaskOrchestration;

namespace RelaxKonOS.Server.Installations;

public sealed class InstallationCoordinator(InstallationOperationStore store, IEnumerable<IInstallationService> services,
    IHostApplicationLifetime lifetime) : IHostedService
{
    private readonly InstallationOperationStore operationStore = store;
    private readonly Dictionary<InstallationServiceId, IInstallationService> registry = services.ToDictionary(x => x.Id);
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    private readonly ConcurrentDictionary<Guid, Task> running = new();
    private bool ready;

    public InstallationOperationDto Start(InstallationServiceId service, InstallationOperationKind kind, JsonElement options, string actor, string key) =>
        StartCore(service, kind, options, actor, key, null);

    public InstallationOperationDto StartTask(InstallationOperationKind kind, JsonElement options, string actor, string key, TaskInstallationExecutionBinding binding) =>
        StartCore(InstallationServiceId.SingBox, kind, options, actor, key, binding ?? throw new ArgumentNullException(nameof(binding)));

    private InstallationOperationDto StartCore(InstallationServiceId service, InstallationOperationKind kind, JsonElement options,
        string actor, string key, TaskInstallationExecutionBinding? binding)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new InstallationException(InstallationProblemCodes.IdempotencyRequired, 400);
        if (!registry.TryGetValue(service, out var domain)) throw new InstallationException(InstallationProblemCodes.NotSupported, 400);
        object request;
        try { request = domain.Validate(kind, options, key); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        }
        // Hash the canonical typed request, not property ordering or arbitrary client JSON.
        var fingerprint = InstallationOperationStore.Reference(JsonSerializer.Serialize(request, request.GetType()));
        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new InstallationException(InstallationProblemCodes.StoreUnavailable, 503);
            var existing = FindValidated(service, kind, actor, key, fingerprint);
            if (existing is not null)
            {
                if (existing.TaskOwner != binding?.Owner) throw new InstallationException(InstallationProblemCodes.IdempotencyConflict);
                return existing.Operation;
            }
            binding?.ValidateAcceptance();
            var entry = operationStore.Create(service, kind, actor, key, fingerprint, domain.Resources, out var created, binding?.Owner);
            if (created)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                cancellations[entry.Operation.OperationId] = cancellation;
                running[entry.Operation.OperationId] = Task.Run(() => RunAsync(entry.Operation, domain, request, actor, cancellation, binding));
            }
            return entry.Operation;
        }
    }

    public InstallationEntry? Get(Guid id) => operationStore.Read().FirstOrDefault(x => x.Operation.OperationId == id);
    /// <summary>Serializes no-acceptance closure with domain admission. The callback only writes the task journal.</summary>
    internal bool TryCloseUnacceptedTask(InstallationTaskOwner owner, InstallationOperationKind kind, JsonElement options,
        string actor, string key, Action closeJournal)
    {
        lock (gate)
        {
            var existing = FindByKey(InstallationServiceId.SingBox, kind, options, actor, key);
            if (existing is not null)
            {
                if (existing.TaskOwner != owner) throw new InstallationException(InstallationProblemCodes.IdempotencyConflict);
                return false;
            }
            closeJournal();
            return true;
        }
    }
    /// <summary>Read-only lookup for recovery after acceptance but before an orchestrator saved the operation ID.</summary>
    public InstallationEntry? FindByKey(InstallationServiceId service, InstallationOperationKind kind, JsonElement options, string actor, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new InstallationException(InstallationProblemCodes.IdempotencyRequired, 400);
        if (!registry.TryGetValue(service, out var domain)) throw new InstallationException(InstallationProblemCodes.NotSupported, 400);
        object request;
        try { request = domain.Validate(kind, options, key); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        { throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400); }
        var fingerprint = InstallationOperationStore.Reference(JsonSerializer.Serialize(request, request.GetType()));
        return FindValidated(service, kind, actor, key, fingerprint);
    }
    private InstallationEntry? FindValidated(InstallationServiceId service, InstallationOperationKind kind, string actor, string key, string fingerprint)
    {
        var actorReference = InstallationOperationStore.Reference(actor);
        var keyReference = InstallationOperationStore.Reference(actorReference + "\n" + key);
        var entry = operationStore.Read().SingleOrDefault(item => item.IdempotencyReference == keyReference);
        if (entry is not null && (entry.ActorReference != actorReference || entry.Operation.Service != service
            || entry.Operation.Kind != kind || entry.RequestReference != fingerprint))
            throw new InstallationException(InstallationProblemCodes.IdempotencyConflict);
        return entry;
    }
    public InstallationEntry? GetActive(InstallationServiceId service, string actor, bool administrator) => operationStore.Read()
        .Where(x => x.Operation.Service == service && InstallationOperationStore.Active(x.Operation)
            && (administrator || x.ActorReference == InstallationOperationStore.Reference(actor)))
        .OrderByDescending(x => x.Operation.CreatedAt).FirstOrDefault();

    /// <summary>
    /// Accepts a cancellation request. Aborting services lose their worker token here; a cooperative service
    /// only gets the request recorded, because its root transaction owns the outcome and is still running.
    /// </summary>
    public async Task<InstallationOperationDto> CancelAsync(Guid id, CancellationToken ct)
    {
        InstallationOperationDto operation;
        CancellationTokenSource source;
        IInstallationService domain;
        lock (gate)
        {
            operation = Get(id)?.Operation ?? throw new InstallationException("installation.not_found", 404);
            if (!InstallationOperationStore.Active(operation)) return operation;
            domain = registry[operation.Service];
            if (!operation.Cancellable || !domain.Cancel(operation) || !cancellations.TryGetValue(id, out var active))
                throw new InstallationException(InstallationProblemCodes.NotCancellable);
            source = active;
            // Withdraw the offer first so a second request cannot race the delivery below.
            operation = operationStore.Update(id, x => x with { Cancellable = false }, "cancel-requested");
        }
        if (!domain.CooperativeCancel) { source.Cancel(); return operation; }
        try { await domain.RequestCancelAsync(operation, ct); }
        catch (InstallationException)
        {
            // The request never reached the transaction, so the offer stays open instead of silently vanishing.
            lock (gate) { operationStore.Update(id, x => x with { Cancellable = true }, "cancel-undelivered"); }
            throw;
        }
        return operation;
    }

    private async Task RunAsync(InstallationOperationDto operation, IInstallationService domain, object request, string actor,
        CancellationTokenSource cancellation, TaskInstallationExecutionBinding? binding)
    {
        var id = operation.OperationId;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            operationStore.Update(id, x => x with { State = InstallationOperationState.Running, Stage = InstallationStage.Preflight,
                StartedAt = DateTimeOffset.UtcNow }, "started");
            IInstallationProgress progress = new Reporter(this, id, cancellation.Token);
            if (binding is not null) progress = binding.Attach(progress);
            InstallationExecutionContext.Progress.Value = progress;
            await domain.StartAsync(operation.Kind, request, actor, InstallationExecutionContext.Progress.Value, cancellation.Token);
            Complete(id, InstallationOperationState.Succeeded, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Complete(id, lifetime.ApplicationStopping.IsCancellationRequested ? InstallationOperationState.Interrupted : InstallationOperationState.Cancelled,
                lifetime.ApplicationStopping.IsCancellationRequested ? InstallationProblemCodes.Interrupted : InstallationProblemCodes.Cancelled);
        }
        catch (InstallationInterruptedException error) { Complete(id, InstallationOperationState.Interrupted, error.ProblemCode); }
        catch (InstallationCancelledException error) { Complete(id, InstallationOperationState.Cancelled, error.ProblemCode); }
        catch (InstallationException error) { Complete(id, InstallationOperationState.Failed, error.ProblemCode); }
        catch { Complete(id, InstallationOperationState.Failed, InstallationProblemCodes.Failed); }
        finally
        {
            InstallationExecutionContext.Progress.Value = null;
            lock (gate) { cancellations.TryRemove(id, out _); cancellation.Dispose(); }
            running.TryRemove(id, out _);
        }
    }

    private void Complete(Guid id, InstallationOperationState state, string? problem, string eventName = "completed")
    {
        try
        {
            operationStore.Update(id, x => x with { State = state, Stage = state switch
            {
                InstallationOperationState.Succeeded => InstallationStage.Completed,
                InstallationOperationState.Cancelled => InstallationStage.Cancelled,
                InstallationOperationState.Interrupted => InstallationStage.Interrupted,
                _ => InstallationStage.Failed
            }, Progress = null, ProblemCode = problem, CompletedAt = DateTimeOffset.UtcNow, Cancellable = false }, eventName);
        }
        catch (InstallationException) { /* Store has failed closed; durable Running is handled on next startup. */ }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        InstallationEntry[] entries;
        try { entries = operationStore.Read(); }
        catch (InstallationException) { return; }
        foreach (var entry in entries.Where(x => InstallationOperationStore.Active(x.Operation)))
        {
            var recovery = new InstallationRecovery(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
            // Task-owned recovery must wait for task-specific authorization; never fall through to ordinary recovery.
            if (entry.Operation.State == InstallationOperationState.Running && entry.TaskOwner is null)
            {
                try
                {
                    recovery = registry.TryGetValue(entry.Operation.Service, out var domain)
                        ? await domain.RecoverAsync(entry.Operation, cancellationToken)
                        : new(InstallationOperationState.Failed, InstallationProblemCodes.RecoveryUnknown);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { recovery = new(InstallationOperationState.Failed, InstallationProblemCodes.RecoveryUnknown); }
            }
            if (recovery.State is not (InstallationOperationState.Succeeded or InstallationOperationState.Interrupted or InstallationOperationState.Failed or InstallationOperationState.Cancelled))
                recovery = new(InstallationOperationState.Failed, InstallationProblemCodes.RecoveryUnknown);
            Complete(entry.Operation.OperationId, recovery.State, recovery.ProblemCode, "recovered");
        }
        lock (gate) ready = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (gate) ready = false;
        try { await Task.WhenAll(running.Values).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private sealed class Reporter(InstallationCoordinator owner, Guid id, CancellationToken workerToken) : IInstallationProgress
    {
        public Guid OperationId => id;
        public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default)
        {
            lock (owner.gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.operationStore.Update(id, x => x with { Stage = progress.Stage, Progress = progress.Progress,
                    Cancellable = progress.Cancellable && !workerToken.IsCancellationRequested }, "stage");
            }
            return Task.CompletedTask;
        }
    }
}
