namespace RelaxKonOS.Server.Certificate;

/// <summary>Restores the last healthy Kestrel deployment after process restart without scanning
/// arbitrary certificate directories or selecting a newer version whose previous deployment failed.</summary>
internal sealed class KestrelCertificateStartupService(ICertificateStore certificates, CertificateDeploymentRepository deployments,
    KestrelCertificateRegistry kestrel, ILogger<KestrelCertificateStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var deployment in await deployments.ListKestrelAsync(cancellationToken))
        {
            var metadata = await certificates.GetAsync(deployment.CertificateId, cancellationToken);
            if (metadata is null) continue;
            var certificate = await certificates.LoadVersionAsync(deployment.CertificateId, deployment.CurrentVersion, cancellationToken);
            if (certificate is null || !kestrel.Activate(deployment.CertificateId, certificate, metadata.Domains))
            {
                certificate?.Dispose();
                logger.LogError("Unable to restore Kestrel certificate deployment. CertificateId={CertificateId}", deployment.CertificateId);
                continue;
            }
            logger.LogInformation("Restored Kestrel certificate deployment. CertificateId={CertificateId}", deployment.CertificateId);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
