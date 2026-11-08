using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.WebServer;

public sealed class NginxReloadExecutor(INginxReloadPorts ports) : INginxReloadExecutor
{
    public async Task<string> ReloadAsync(CancellationToken cancellationToken)
    {
        await ports.ReportDispatchAsync(cancellationToken);
        if (!ports.UsesSystemPackage)
        {
            try
            {
                if (!await ports.RunDirectAsync(cancellationToken)) return NginxReloadOutcome.RecoveryRequired;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return NginxReloadOutcome.RecoveryRequired;
            }
            await ports.ReportResponseAsync(confirmed: true);
            return "";
        }

        var helper = await ports.RunTrackedAsync(ports.OperationId, cancellationToken);
        var receipt = helper.NginxReloadReceipt;
        if (helper.ProblemCode == PrivilegedProblemCode.RecoveryRequired
            || helper.Success && receipt is null
            || receipt is not null && (receipt.OperationId != ports.OperationId || receipt.Succeeded != helper.Success))
            return NginxReloadOutcome.RecoveryRequired;

        await ports.ReportResponseAsync(receipt is not null);
        return helper.Success ? "" : ports.ToReloadProblem(helper.ProblemCode);
    }
}
