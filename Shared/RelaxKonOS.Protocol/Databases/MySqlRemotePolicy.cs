using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Databases;

/// <summary>First remote-access slice: one exact IPv4 source and one non-administrative MySQL identity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlRemotePolicy(Guid InstanceId, string ListenAddress, string SourceAddress,
    string Database, string User, MySqlGrant Grant, string ServerName, Guid CertificateId, string CertificateFingerprint)
{
    [JsonIgnore]
    public bool Valid => InstanceId != Guid.Empty && IsIPv4(ListenAddress) && IsIPv4(SourceAddress)
        && ListenAddress != SourceAddress && MySqlAccountCommand.Name(Database) && MySqlAccountCommand.Name(User)
        && Enum.IsDefined(Grant) && ServerName is { Length: > 0 and <= 253 }
        && Uri.CheckHostName(ServerName) == UriHostNameType.Dns && CertificateId != Guid.Empty
        && CertificateFingerprint is { Length: 64 } && CertificateFingerprint.All(Uri.IsHexDigit);

    public static bool IsIPv4(string? value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork
        && address.ToString() == value && !IPAddress.IsLoopback(address)
        && address.GetAddressBytes()[0] is > 0 and < 224
        && (address.GetAddressBytes()[0] != 169 || address.GetAddressBytes()[1] != 254);
}


[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MySqlRemoteChange(Guid InstanceId, MySqlRemotePolicy? Policy = null)
{
    [JsonIgnore]
    public bool Valid => InstanceId != Guid.Empty && (Policy is null || Policy.Valid && Policy.InstanceId == InstanceId);
}
