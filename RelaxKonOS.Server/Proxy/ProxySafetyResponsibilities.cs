using System.Security.Cryptography;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.ProxyServer;

namespace RelaxKonOS.Server.Proxy;

/// <summary>Inspects safety ownership and confirms a pending inbound publication from the original Helper receipt.</summary>
internal static class ProxySafetyResponsibilities
{
    internal static async Task<IReadOnlyList<string>> InspectAsync(IServiceProvider services, IProxyPlatformPaths paths, bool confirmPublication, CancellationToken ct)
    {
        var facts = new List<string>();
        try
        {
            if (await ProxyTunSafetyService.HasUnknownRecoveryAsync(paths, ct)) facts.Add("TUN recovery marker is unknown; operator recovery is required.");
        }
        catch (Exception error) when (Unreadable(error)) { facts.Add("TUN recovery responsibility cannot be verified."); }
        try { await new ProxySettingsReader(paths).GetAsync(ct); }
        catch (Exception error) when (Unreadable(error)) { facts.Add("System proxy intent cannot be verified; guard responsibility is unknown."); }
        try
        {
            var file = Path.Combine(paths.GetStateDirectory(), "proxy-server.protected"); RuntimeSourceStore.RequireNoLinks(file);
            if (Directory.Exists(file)) throw new InvalidDataException();
            var store = services.GetService<ProxyServerStore>() ?? new ProxyServerStore(paths,
                services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
            await store.Gate.WaitAsync(ct);
            try
            {
                if ((await store.ReadAsync(ct)).PendingPublication is not null)
                {
                    if (confirmPublication)
                    {
                        var publication = new ProxyServerPublicationService(store,
                            services.GetRequiredService<RelaxKonOS.Server.Privileged.IPrivilegedOperationTransport>(),
                            services.GetRequiredService<RelaxKonOS.Server.HostMode.IServerModeResolver>());
                        await publication.ReadStateAsync(ct);
                    }
                    else facts.Add(PendingPublication);
                }
            }
            catch (ProxyServerException)
            {
                facts.Add(PendingPublication);
            }
            finally { store.Gate.Release(); }
        }
        catch (Exception error) when (Unreadable(error)) { facts.Add("Inbound published-state responsibility cannot be verified."); }
        try
        {
            var store = services.GetService<SquidConfigurationStore>() ?? new SquidConfigurationStore(paths);
            await store.Gate.WaitAsync(ct);
            try
            {
                var state = await store.ReadAsync(ct);
                if (state.LastProblemCode is "squid.publication.unconfirmed" or "squid.helper.unavailable" or "squid.recovery.required" or "squid.io_or_busy")
                    facts.Add("Squid publication or native compensation requires reconciliation; retained intent must not be discarded.");
            }
            finally { store.Gate.Release(); }
        }
        catch (Exception error) when (Unreadable(error)) { facts.Add("Squid publication responsibility cannot be verified."); }
        return facts;
    }
    private const string PendingPublication = "Inbound publication remains unconfirmed; expiry/recovery ownership is retained by Core.";

    private static bool Unreadable(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Text.Json.JsonException or CryptographicException or RuntimePolicyException or FormatException;
}
