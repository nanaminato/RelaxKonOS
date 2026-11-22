using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.ServerModule.Tailscale;

public sealed class TailscaleExecution : ITailscaleExecution
{
    public ITailscaleControl CreateControl(IHostEnvironment environment, IDataProtectionProvider protection,
        ITailscaleControlHost host, ITailscaleControlAuthorization grants) =>
        new TailscaleOperationCoordinator(new(environment, protection), host, grants);

    public ITailscaleLogin CreateLogin(IHostEnvironment environment, IDataProtectionProvider protection,
        ITailscaleLoginHost host, ITailscaleControlAuthorization grants, Func<Guid> readHostId) =>
        new TailscaleLoginCoordinator(new(environment, protection), host, grants, readHostId);
}
