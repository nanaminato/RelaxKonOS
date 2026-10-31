using System.Threading.Channels;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.Startup;

/// <summary>Committed runtime intentions outlive the request. Interrupted intentions are never replayed.</summary>
internal sealed class ModuleManagementExecutor(ModuleManagementStore store, IServiceProvider services,
    ILogger<ModuleManagementExecutor> logger) : BackgroundService
{
    private sealed record Work(ServerModuleOperationDto Operation, ServerModulePlanDto Plan);
    private readonly Channel<Work> _work = Channel.CreateBounded<Work>(new BoundedChannelOptions(32)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    internal ServerModuleOperationDto Apply(string actor, Guid planId, IReadOnlyList<ServerModuleDto> inventory)
    {
        var plan = store.PlanStatus(actor, planId).Plan;
        if (!ModuleManagementStore.IsRuntimeAction(plan.Target.Action)) return store.Apply(actor, planId, inventory);
        var result = store.BeginRuntime(actor, planId, inventory);
        if (result.Created && !_work.Writer.TryWrite(new(result.Operation, result.Plan)))
        {
            store.UpdateRuntime(result.Operation.OperationId, ServerModuleOperationState.Failed, "module-wake-overloaded");
            return store.Operation(actor, result.Operation.OperationId);
        }
        return result.Operation;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken))
            {
                List<ModuleLease> leases = [];
                List<ServerModuleActionResult> results = [];
                string? currentModule = null;
                try
                {
                    store.UpdateRuntime(work.Operation.OperationId, ServerModuleOperationState.Applying);
                    var residency = services.GetRequiredService<ModuleResidencyCoordinator>();
                    foreach (var id in work.Plan.Target.ModuleIds)
                    {
                        currentModule = id;
                        stoppingToken.ThrowIfCancellationRequested();
                        if (work.Plan.Target.Action == ServerModuleAction.Wake)
                            leases.Add(await residency.AcquireAsync(id, stoppingToken).ConfigureAwait(false));
                        else if (await residency.TrySleepAsync(id, stoppingToken).ConfigureAwait(false) is { } problem)
                            throw new ModuleManagementException(problem);
                        results.Add(new(id, true));
                        store.UpdateRuntime(work.Operation.OperationId, ServerModuleOperationState.Applying, results: results);
                        currentModule = null;
                    }
                    store.UpdateRuntime(work.Operation.OperationId, ServerModuleOperationState.Succeeded, results: results);
                }
                catch (Exception exception)
                {
                    var code = exception switch
                    {
                        OperationCanceledException => "module-host-stopping",
                        ModuleUnavailableException unavailable => unavailable.Code,
                        ModuleManagementException management => management.Code,
                        _ => "module-runtime-operation-failed"
                    };
                    if (currentModule is not null && !results.Any(x => x.ModuleId == currentModule)) results.Add(new(currentModule, false, code));
                    try { store.UpdateRuntime(work.Operation.OperationId, ServerModuleOperationState.Failed, code, results); }
                    catch (Exception persistence)
                    { logger.LogError(persistence, "Module operation {OperationId} requires recovery after receipt persistence failed", work.Operation.OperationId); }
                    logger.LogWarning("Module operation {OperationId} ended with {ProblemCode}", work.Operation.OperationId, code);
                }
                finally
                {
                    foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _work.Writer.TryComplete();
            while (_work.Reader.TryRead(out var pending))
            {
                try { store.UpdateRuntime(pending.Operation.OperationId, ServerModuleOperationState.Failed, "module-host-stopping"); }
                catch (Exception exception) { logger.LogError(exception, "Module operation {OperationId} remains interrupted during host shutdown", pending.Operation.OperationId); }
            }
        }
    }
}
