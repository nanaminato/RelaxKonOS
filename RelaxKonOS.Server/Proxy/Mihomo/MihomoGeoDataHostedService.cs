namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>
/// Stages the Server-packaged GEO artifacts before Proxy Manager accepts any profile or
/// subscription operation.  Mihomo is always launched with this same directory as its <c>-d</c>
/// HomeDir, so GEOIP and GEOSITE rules have local data without a first-import download.
/// </summary>
public sealed class MihomoGeoDataHostedService(IProxyGeoDataService geoData) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // EnsureBundledAsync records an actionable diagnostic on failure. Do not prevent the
        // Server itself from starting: Proxy Manager can surface the stable problem code and an
        // administrator may repair a protected host directory without taking other apps offline.
        _ = await geoData.EnsureBundledAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
