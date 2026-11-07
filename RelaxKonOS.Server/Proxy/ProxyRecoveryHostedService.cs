namespace RelaxKonOS.Server.Proxy;

/// <summary>
/// Scans the durable TUN marker for work left by an earlier host or an unfinished transition.
/// The safety service owns current-session identity and serializes recovery with live transitions.
/// </summary>
public sealed class ProxyRecoveryHostedService(IProxyTunSafetyService tunSafety, ILogger<ProxyRecoveryHostedService> logger) : BackgroundService
{
    // Stop future scans, then wait for any accepted TUN restoration and its durable marker update.
    public override Task StopAsync(CancellationToken cancellationToken) => base.StopAsync(CancellationToken.None);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProxyTunRecoveryReference? reference = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    reference ??= await tunSafety.CaptureRecoveryAsync(includeCurrentSession: false, stoppingToken);
                    if (reference is not null)
                    {
                        var problem = await tunSafety.RecoverAsync(reference, stoppingToken);
                        if (string.IsNullOrEmpty(problem)) reference = null;
                        else logger.LogWarning("Proxy TUN recovery evaluation requires operator attention: {ProblemCode}", problem);
                    }
                }
                catch (Exception error) when (IsStateFailure(error))
                { logger.LogWarning("Proxy TUN recovery state could not be verified; recovery remains pending."); }
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static bool IsStateFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or RelaxKonOS.ManagedRuntimes.RuntimePolicyException;
}
