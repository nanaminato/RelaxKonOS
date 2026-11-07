namespace RelaxKonOS.Server.Proxy;

/// <summary>
/// Evaluates a durable TUN recovery marker after the HTTP host is available. The marker itself
/// blocks new TUN activation, so recovery need not delay ordinary Server sign-in or listening.
/// </summary>
public sealed class ProxyRecoveryHostedService(IProxyTunSafetyService tunSafety, ILogger<ProxyRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ProxyTunRecoveryReference? reference;
        try { reference = await tunSafety.CaptureRecoveryAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
        {
            logger.LogWarning("Proxy TUN startup responsibility could not be captured; operator recovery is required.");
            return;
        }
        if (reference is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var problem = await tunSafety.RecoverAsync(reference, stoppingToken);
                if (string.IsNullOrEmpty(problem)) return;
                logger.LogWarning("Proxy TUN recovery evaluation requires operator attention: {ProblemCode}", problem);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
                or RelaxKonOS.ManagedRuntimes.RuntimePolicyException)
            { logger.LogWarning("Proxy TUN recovery state could not be verified; recovery remains pending."); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
