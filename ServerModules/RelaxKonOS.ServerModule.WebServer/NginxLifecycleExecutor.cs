namespace RelaxKonOS.Server.WebServer;

public sealed class NginxLifecycleExecutor(INginxLifecyclePorts ports) : INginxLifecycleExecutor
{
    public async Task<bool?> RunAsync(CancellationToken cancellationToken)
    {
        await ports.ReportDispatchAsync(cancellationToken);
        var result = await ports.RunTrackedAsync();
        if (result.NginxReloadReceipt is not { } receipt
            || receipt.OperationId != ports.OperationId || receipt.Succeeded != result.Success)
            return null;

        await ports.ReportConfirmedAsync();
        return receipt.Succeeded;
    }
}
