using System.Collections.Concurrent;
using System.Text.Json;
using RelaxKonOS.Protocol.Installations;

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

    public InstallationOperationDto Start(InstallationServiceId service, InstallationOperationKind kind, JsonElement options, string actor, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(c => c < 33 || c > 126))
            throw new InstallationException(InstallationProblemCodes.IdempotencyRequired, 400);
        if (!registry.TryGetValue(service, out var domain)) throw new InstallationException(InstallationProblemCodes.NotSupported, 400);
        object request;
        try { request = domain.Validate(kind, options); }
        catch (JsonException) { throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400); }
        // Hash the canonical typed request, not property ordering or arbitrary client JSON.
        var fingerprint = InstallationOperationStore.Reference(JsonSerializer.Serialize(request, request.GetType()));
        lock (gate)
        {
            if (!ready || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new InstallationException(InstallationProblemCodes.StoreUnavailable, 503);
            var entry = operationStore.Create(service, kind, actor, key, fingerprint, domain.Resources, out var created);
            if (created)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
                cancellations[entry.Operation.OperationId] = cancellation;
                running[entry.Operation.OperationId] = Task.Run(() => RunAsync(entry.Operation, domain, request, actor, cancellation));
            }
            return entry.Operation;
        }
    }

    public InstallationEntry? Get(Guid id) => operationStore.Read().FirstOrDefault(x => x.Operation.OperationId == id);
    public InstallationEntry? GetActive(InstallationServiceId service, string actor, bool administrator) => operationStore.Read()
        .Where(x => x.Operation.Service == service && InstallationOperationStore.Active(x.Operation)
            && (administrator || x.ActorReference == InstallationOperationStore.Reference(actor)))
        .OrderByDescending(x => x.Operation.CreatedAt).FirstOrDefault();

    public InstallationOperationDto Cancel(Guid id)
    {
        lock (gate)
        {
            var operation = Get(id)?.Operation ?? throw new InstallationException("installation.not_found", 404);
            if (!InstallationOperationStore.Active(operation)) return operation;
            if (!operation.Cancellable || !registry[operation.Service].Cancel(operation) || !cancellations.TryGetValue(id, out var source))
                throw new InstallationException(InstallationProblemCodes.NotCancellable);
            // Only the worker may acknowledge cancellation and release resources.
            var result = operationStore.Update(id, x => x with { Cancellable = false }, "cancel-requested");
            source.Cancel();
            return result;
        }
    }

    private async Task RunAsync(InstallationOperationDto operation, IInstallationService domain, object request, string actor, CancellationTokenSource cancellation)
    {
        var id = operation.OperationId;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            operationStore.Update(id, x => x with { State = InstallationOperationState.Running, Stage = InstallationStage.Preflight,
                StartedAt = DateTimeOffset.UtcNow }, "started");
            InstallationExecutionContext.Progress.Value = new Reporter(this, id, cancellation.Token);
            await domain.StartAsync(operation.Kind, request, actor, InstallationExecutionContext.Progress.Value, cancellation.Token);
            Complete(id, InstallationOperationState.Succeeded, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Complete(id, lifetime.ApplicationStopping.IsCancellationRequested ? InstallationOperationState.Interrupted : InstallationOperationState.Cancelled,
                lifetime.ApplicationStopping.IsCancellationRequested ? InstallationProblemCodes.Interrupted : InstallationProblemCodes.Cancelled);
        }
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
            if (entry.Operation.State == InstallationOperationState.Running)
            {
                try
                {
                    recovery = registry.TryGetValue(entry.Operation.Service, out var domain)
                        ? await domain.RecoverAsync(entry.Operation, cancellationToken)
                        : new(InstallationOperationState.Failed, InstallationProblemCodes.RecoveryUnknown);
                }
                catch { recovery = new(InstallationOperationState.Failed, InstallationProblemCodes.RecoveryUnknown); }
            }
            if (recovery.State is not (InstallationOperationState.Succeeded or InstallationOperationState.Interrupted or InstallationOperationState.Failed))
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
        public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default)
        {
            lock (owner.gate)
            {
                workerToken.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                owner.operationStore.Update(id, x => x with { Stage = progress.Stage, Progress = progress.Progress, Cancellable = progress.Cancellable }, "stage");
            }
            return Task.CompletedTask;
        }
    }
}
