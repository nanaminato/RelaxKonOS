namespace RelaxKonOS.Server.Proxy;

/// <summary>
/// Evaluates a durable TUN recovery marker after the HTTP host is available. The marker itself
/// blocks new TUN activation, so recovery need not delay ordinary Server sign-in or listening.
/// </summary>
public sealed class ProxyRecoveryHostedService(IProxyTunSafetyService tunSafety, ILogger<ProxyRecoveryHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var problem = await tunSafety.EvaluateRecoveryAsync(stoppingToken);
        if (!string.IsNullOrEmpty(problem)) logger.LogWarning("Proxy TUN recovery evaluation requires operator attention: {ProblemCode}", problem);
    }
}
