using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.WebServer;

internal sealed record NginxWebRootStep(string SiteId, Guid ReloadId, string PreviousHash, string CandidateHash,
    bool Dispatched = false, Guid? CompensationId = null);

internal sealed record NginxWebRootCheckpoint(WebServerDto Instance, string[] Sites, int Applied,
    NginxWebRootStep? Current, Guid[] Receipts, string? Problem = null);

internal sealed partial class WebServerOperationStore
{
    internal async Task<bool> HasPendingWebRootAsync(string instanceId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return _operations.Values.Any(item => item.InstanceId == instanceId && item.WebRoot is not null
                && item.State == WebServerOperationState.Running);
        }
        finally { _gate.Release(); }
    }

    private async Task UpdateWebRootAsync(Guid id, Func<NginxWebRootCheckpoint?, NginxWebRootCheckpoint> update, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var original = _operations[id];
            _operations[id] = original with { WebRoot = update(original.WebRoot) };
            try { await SaveAsync(CancellationToken.None); }
            catch (Exception error) { throw new ResidentWorkCompletionException(error); }
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> ReconcileWebRootAsync(PersistedOperation original, CancellationToken ct)
    {
        if (_nativeOperations is null || original.WebRoot is not { } plan) return false;
        try
        {
            while (!await NginxWebServerManager.CleanWebRootMarkerStageAsync(_nativeOperations, plan.Instance, ct))
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            if (plan.Current is { } step)
            {
                while (!await NginxWebServerManager.ReconcileWebRootStepAsync(_nativeOperations, plan.Instance, step, ct))
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                plan = EndWebRootStep(plan, step, applied: step.Dispatched && step.CompensationId is null);
                await UpdateWebRootAsync(original.OperationId, _ => plan, ct);
            }
            var allApplied = plan.Applied == plan.Sites.Length;
            return await CompleteAsync(original.OperationId, allApplied ? WebServerOperationState.Succeeded : WebServerOperationState.Failed,
                allApplied ? "" : plan.Problem ?? "webserver.operation_interrupted", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }

    private static NginxWebRootCheckpoint EndWebRootStep(NginxWebRootCheckpoint plan, NginxWebRootStep step, bool applied, string? problem = null)
    {
        if (plan.Current != step) throw new InvalidOperationException("The original WebRoot site checkpoint changed.");
        Guid[] receipts = step.CompensationId is { } compensation ? [step.ReloadId, compensation]
            : step.Dispatched ? new[] { step.ReloadId } : [];
        return plan with { Applied = applied ? plan.Applied + 1 : plan.Applied, Current = null, Receipts = [.. plan.Receipts, .. receipts],
            Problem = step.CompensationId is not null ? "webserver.site_reload_failed" : problem };
    }

    internal sealed partial class OperationProgress
    {
        internal Task PlanWebRootAsync(WebServerDto instance, string[] sites, CancellationToken ct) =>
            owner.UpdateWebRootAsync(operationId, old => old is null && sites.Length != 0
                ? new(instance, sites, 0, null, []) : throw new InvalidOperationException("WebRoot was already planned."), ct);

        internal Task BeginWebRootStepAsync(NginxWebRootStep step, CancellationToken ct) =>
            owner.UpdateWebRootAsync(operationId, plan => plan is { Current: null } && plan.Sites[plan.Applied] == step.SiteId
                ? plan with { Current = step } : throw new InvalidOperationException("The WebRoot site is not next in the original plan."), ct);

        internal Task AdvanceWebRootStepAsync(NginxWebRootStep previous, NginxWebRootStep next, CancellationToken ct) =>
            owner.UpdateWebRootAsync(operationId, plan => plan?.Current == previous
                ? plan with { Current = next } : throw new InvalidOperationException("The original WebRoot site checkpoint changed."), ct);

        internal Task EndWebRootStepAsync(NginxWebRootStep step, bool applied, string? problem = null) =>
            owner.UpdateWebRootAsync(operationId, plan => EndWebRootStep(plan!, step, applied, problem), CancellationToken.None);
    }
}
