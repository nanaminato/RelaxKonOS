using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RelaxKonOS.Protocol.SystemMonitor;

namespace RelaxKonOS.Server.Endpoints;

/// <summary>
/// Host network addresses (non-loopback IPv4/IPv6). This is a Core question rather than a
/// performance one: connection setup and the proxy application ask for it without reading any
/// metric, so it must survive <c>Modules:SystemPerformance:Enabled=false</c>.
/// </summary>
public static class HostNetworkEndpoints
{
    public static IEndpointRouteBuilder MapHostNetworkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(SystemMonitorApiRoutes.NetworkAddresses, () => Results.Ok(GetNetworkAddresses()))
           .RequireAuthorization()
           .WithTags("System");
        return app;
    }

    private static IReadOnlyList<NetworkAddressDto> GetNetworkAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                                  network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses
                    .Select(unicast => (network.Name, Address: unicast.Address)))
                .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(item => !IPAddress.IsLoopback(item.Address) && !item.Address.IsIPv6LinkLocal)
                .Select(item => new NetworkAddressDto(
                    item.Name,
                    item.Address.ToString(),
                    item.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6"))
                .DistinctBy(item => (item.Family, item.Address))
                .OrderBy(item => item.Family)
                .ThenBy(item => item.Address, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<NetworkAddressDto>();
        }
    }
}
