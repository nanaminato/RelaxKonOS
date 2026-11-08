using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Server.Residency;

namespace RelaxKonOS.Server.WebServer;

internal sealed partial class WebServerOperationStore
{
    private const string IntegrationCompensated = "webserver.integration_compensated";

    private sealed record IntegrationCompensation(Guid Id, string? Actor);

    internal async Task<WebServerOperationDto?> CompensateIntegrationAsync(Guid id, string? actor, CancellationToken ct)
    {
        var original = await ReadOperationAsync(id, ct);
        if (original.Compensation is not null) return original.ToDto();
        if (original is not { Kind: "integrate", State: WebServerOperationState.Running,
                ProblemCode: NativeRecoveryRequired, Integration: { } integration }
            || integration.Instance.Id != original.InstanceId
            || _nativeOperations is null || _metadata is null
            || !await NginxWebServerManager.VerifyIntegrationCompensationAsync(_nativeOperations, _metadata, original.OperationId, integration, ct))
            return null;

        using var work = await _workOwner.AdmitAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            RequireReady();
            var current = _operations[id];
            if (current.Compensation is not null) return current.ToDto();
            if (current != original) return null;
            work.Token.ThrowIfCancellationRequested();
            var accepted = current with { Compensation = new(Guid.NewGuid(), actor), Stage = "integration-compensation-dispatch" };
            _operations[id] = accepted;
            try { await SaveAsync(CancellationToken.None); }
            catch (Exception error) { work.Fail(error); throw; }
            work.Run(token => RunIntegrationCompensationAsync(accepted, token));
            return accepted.ToDto();
        }
        finally { _gate.Release(); }
    }

    private async Task RunIntegrationCompensationAsync(PersistedOperation operation, CancellationToken ct)
    {
        if (_nativeOperations is null || _metadata is null || operation.Integration is not { } integration
            || operation.Compensation is not { } compensation)
            return;
        // Admission creates the only dispatch authority. Startup only observes the same ID.
        try
        {
            await NginxWebServerManager.ExecuteIntegrationCompensationAsync(_nativeOperations, _metadata, operation.OperationId,
                compensation.Id, integration, ct);
            await ReconcileIntegrationCompensationAsync(operation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ReconcileIntegrationCompensationAsync(PersistedOperation operation, CancellationToken ct)
    {
        if (_nativeOperations is null || _metadata is null || operation.Integration is not { } integration
            || operation.Compensation is not { } compensation)
            return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var evidence = await _nativeOperations.QueryReloadReceiptAsync(compensation.Id, ct);
                if (evidence is { Success: true, NginxReloadReceipt: { Succeeded: true } receipt } && receipt.OperationId == compensation.Id
                    && await NginxWebServerManager.ConfirmIntegrationCompensationAsync(_metadata, operation.OperationId, integration, ct))
                {
                    if (await CompleteAsync(operation.OperationId, WebServerOperationState.Failed, IntegrationCompensated, null, nativeEvidence: true))
                        await RetireNativeReceiptsAsync([operation.OperationId], ct);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
