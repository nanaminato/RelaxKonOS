using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.SystemMonitor;

/// <summary>A non-loopback IPv4 or IPv6 address assigned to the RelaxKonOS server.</summary>
public sealed record NetworkAddressDto(
    [property: JsonPropertyName("interfaceName")] string InterfaceName,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("family")] string Family);
