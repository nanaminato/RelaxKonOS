namespace RelaxKonOS.Server.Proxy;

/// <summary>Runs recovery evaluation before Proxy API/UI exists; failures leave the durable marker intact.</summary>
public sealed class ProxyRecoveryHostedService(IProxyTunSafetyService tunSafety, ILogger<ProxyRecoveryHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var problem = await tunSafety.EvaluateRecoveryAsync(cancellationToken);
        if (!string.IsNullOrEmpty(problem)) logger.LogWarning("Proxy TUN recovery evaluation requires operator attention: {ProblemCode}", problem);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
